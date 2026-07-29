using System;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Formats.Sf2 {

    /// <summary>
    /// Resolves a MIDI (key, velocity) pair through the SF2 two-level zone model to a
    /// <see cref="SampleRegion"/>, implementing the v1 generator subset plus preset-level +
    /// instrument-level InitialAttenuation(48) accumulation.  This is the single home for all SF2
    /// interpretation; later generator families (offset generators, modulation) attach here.
    /// </summary>
    /// <remarks>
    /// Resolution is deterministic, rate-independent, and defensive: structurally-valid-but-
    /// musically-imperfect SF2 content degrades to no-match rather than throwing on the note path.
    /// </remarks>
    public sealed class Sf2RegionResolver {

        const int DefaultEnvelopeTimecents = -12000;
        const float MaxEnvelopeSeconds = 20f;
        const int MaxSustainAttenuationCentibels = 1440;
        const int DefaultFilterCutoffCents = 13500;
        const int MinFilterCutoffCents = 1500;
        const int MaxFilterResonanceCentibels = 960;
        const double FilterCutoffReferenceHz = 8.176;
        const int MaxLfoPitchDepthCents = 1200;
        const float MinLfoFrequencyHz = 0.1f;
        const float MaxLfoFrequencyHz = 20f;
        const int MaxLfoVolumeDepthCentibels = 960;
        const int MaxLfoFilterDepthCents = 12000;
        const int MaxPanUnits = 500;
        const float PanUnitsDivisor = 500f;
        const int MaxReverbSendUnits = 1000;
        const float ReverbSendUnitsDivisor = 1000f;
        const int MaxChorusSendUnits = 1000;
        const float ChorusSendUnitsDivisor = 1000f;

        readonly Sf2PresetHeader preset;
        readonly Sf2Instrument[] instruments;
        readonly Sf2SampleHeader[] sampleHeaders;
        readonly float[] floatPool;

        /// <summary>
        /// Creates an <see cref="Sf2RegionResolver"/> for one preset.
        /// </summary>
        /// <param name="preset">the preset header whose zones are searched during resolution</param>
        /// <param name="instruments">all instruments in the bank, indexed by Instrument(41) generator amount</param>
        /// <param name="sampleHeaders">all sample headers in the bank, indexed by SampleID(53) generator amount</param>
        /// <param name="floatPool">shared normalized float pool from the file's <see cref="Sf2SampleData"/></param>
        public Sf2RegionResolver(
            Sf2PresetHeader preset,
            Sf2Instrument[] instruments,
            Sf2SampleHeader[] sampleHeaders,
            float[] floatPool) {
            this.preset = preset ?? throw new ArgumentNullException(nameof(preset));
            this.instruments = instruments ?? throw new ArgumentNullException(nameof(instruments));
            this.sampleHeaders = sampleHeaders ?? throw new ArgumentNullException(nameof(sampleHeaders));
            this.floatPool = floatPool ?? throw new ArgumentNullException(nameof(floatPool));
        }

        /// <summary>
        /// Attempts to resolve <paramref name="key"/> and <paramref name="velocity"/> to a
        /// <see cref="SampleRegion"/> by walking preset zones → instrument → instrument zones.
        /// </summary>
        /// <param name="key">MIDI key number (0–127)</param>
        /// <param name="velocity">MIDI velocity (0–127)</param>
        /// <param name="region">the resolved region on match; undefined on no-match</param>
        /// <param name="cacheKey">
        /// opaque long encoding the matched (instrumentIndex, zoneIndex) pair for the caller's cache;
        /// undefined on no-match
        /// </param>
        /// <returns>true if a matching zone was found; false on no-match</returns>
        public bool TryResolve(int key, int velocity, out SampleRegion? region, out long cacheKey) {
            region = null;
            cacheKey = 0;

            int instrIndex = FindInstrumentIndex(key, velocity, out int presetZoneIndex);
            if (instrIndex < 0 || instrIndex >= instruments.Length)
                return false;

            Sf2Zone[] presetZones = preset.Zones;
            Sf2Zone presetZone = presetZones[presetZoneIndex];
            int presetGlobalZoneIndex = FindPresetGlobalZoneIndex(presetZones);
            Sf2Zone? presetGlobalZone = presetGlobalZoneIndex >= 0 ? presetZones[presetGlobalZoneIndex] : null;

            Sf2Instrument instrument = instruments[instrIndex];
            Sf2Zone[] zones = instrument.Zones;

            int globalZoneIndex = FindInstrumentGlobalZoneIndex(zones);
            Sf2Zone? globalZone = globalZoneIndex >= 0 ? zones[globalZoneIndex] : null;

            for (int zi = 0; zi < zones.Length; zi++) {
                if (zi == globalZoneIndex)
                    continue;

                Sf2Zone zone = zones[zi];

                if (!TryFindGenerator(zone.Generators, Sf2GeneratorType.SampleID, out int sampleIdRaw))
                    continue;

                if (!ZoneCoversNote(zone, globalZone, key, velocity))
                    continue;

                int sampleId = sampleIdRaw;
                if (sampleId < 0 || sampleId >= sampleHeaders.Length)
                    continue;

                SampleRegion? built = BuildRegion(zone, globalZone, presetZone, presetGlobalZone, sampleId);
                if (built is null)
                    continue;

                region = built;
                cacheKey = ((long)instrIndex << 32) | (uint)zi;
                return true;
            }

            return false;
        }

        int FindInstrumentIndex(int key, int velocity, out int presetZoneIndex) {
            Sf2Zone[] presetZones = preset.Zones;
            for (int i = 0; i < presetZones.Length; i++) {
                Sf2Zone zone = presetZones[i];
                if (!TryFindGenerator(zone.Generators, Sf2GeneratorType.Instrument, out int instrRaw))
                    continue;

                if (TryFindGenerator(zone.Generators, Sf2GeneratorType.KeyRange, out int keyRangeRaw)) {
                    int lo = keyRangeRaw & 0xFF;
                    int hi = (keyRangeRaw >> 8) & 0xFF;
                    if (key < lo || key > hi)
                        continue;
                }

                if (TryFindGenerator(zone.Generators, Sf2GeneratorType.VelocityRange, out int velRangeRaw)) {
                    int lo = velRangeRaw & 0xFF;
                    int hi = (velRangeRaw >> 8) & 0xFF;
                    if (velocity < lo || velocity > hi)
                        continue;
                }

                presetZoneIndex = i;
                return instrRaw;
            }
            presetZoneIndex = -1;
            return -1;
        }

        static int FindInstrumentGlobalZoneIndex(Sf2Zone[] zones) {
            if (zones.Length == 0)
                return -1;
            Sf2Zone first = zones[0];
            foreach (Sf2Generator gen in first.Generators) {
                if (gen.Type == Sf2GeneratorType.SampleID)
                    return -1;
            }
            return 0;
        }

        /// <summary>
        /// Locates the preset's global zone: SF2 §7.2/§8.2 identifies a preset zone as global when it
        /// carries no <see cref="Sf2GeneratorType.Instrument"/> generator (the terminal/linking generator
        /// for preset zones, mirroring how <see cref="FindInstrumentGlobalZoneIndex"/> uses the absence of
        /// SampleID to identify an instrument zone as global). A global preset zone, if present, is always
        /// zone 0.
        /// </summary>
        static int FindPresetGlobalZoneIndex(Sf2Zone[] zones) {
            if (zones.Length == 0)
                return -1;
            Sf2Zone first = zones[0];
            foreach (Sf2Generator gen in first.Generators) {
                if (gen.Type == Sf2GeneratorType.Instrument)
                    return -1;
            }
            return 0;
        }

        static bool ZoneCoversNote(Sf2Zone zone, Sf2Zone? globalZone, int key, int velocity) {
            int keyRaw = GetEffectiveRaw(zone, globalZone, Sf2GeneratorType.KeyRange, defaultValue: -1);
            if (keyRaw >= 0) {
                int lo = keyRaw & 0xFF;
                int hi = (keyRaw >> 8) & 0xFF;
                if (key < lo || key > hi)
                    return false;
            }

            int velRaw = GetEffectiveRaw(zone, globalZone, Sf2GeneratorType.VelocityRange, defaultValue: -1);
            if (velRaw >= 0) {
                int lo = velRaw & 0xFF;
                int hi = (velRaw >> 8) & 0xFF;
                if (velocity < lo || velocity > hi)
                    return false;
            }

            return true;
        }

        SampleRegion? BuildRegion(Sf2Zone zone, Sf2Zone? globalZone, Sf2Zone presetZone, Sf2Zone? presetGlobalZone, int sampleId) {
            Sf2SampleHeader header = sampleHeaders[sampleId];

            int start = (int)header.Start;
            int end = (int)header.End;
            int loopStart = (int)header.StartLoop;
            int loopEnd = (int)header.EndLoop;
            int sourceSampleRate = (int)header.SampleRate;

            if (sourceSampleRate <= 0)
                return null;
            if (end <= start)
                return null;
            if (end > floatPool.Length || start < 0)
                return null;

            int sampleModesRaw = GetEffectiveRaw(zone, globalZone, Sf2GeneratorType.SampleModes, defaultValue: 0);
            LoopMode loopMode = MapSampleModes(sampleModesRaw & 0xFFFF);

            if (loopMode == LoopMode.Continuous) {
                bool loopValid = loopStart >= start && loopStart < end
                              && loopEnd > loopStart && loopEnd <= end;
                if (!loopValid)
                    loopMode = LoopMode.NoLoop;
            }

            int rootKey = ResolveRootKey(zone, globalZone, header);

            int pitchCorrectionCents = header.PitchCorrection
                + GetEffectiveInt16(zone, globalZone, Sf2GeneratorType.FineTune, defaultValue: 0)
                + GetEffectiveInt16(zone, globalZone, Sf2GeneratorType.CoarseTune, defaultValue: 0) * 100;

            EnvelopeParameters envelope = BuildEnvelopeParameters(zone, globalZone);
            FilterParameters filter = BuildFilterParameters(zone, globalZone);
            LfoParameters lfo = BuildLfoParameters(zone, globalZone);
            float pan = BuildPan(zone, globalZone);
            float reverbSend = BuildReverbSend(zone, globalZone);
            float chorusSend = BuildChorusSend(zone, globalZone);
            int exclusiveClass = GetEffectiveRaw(zone, globalZone, Sf2GeneratorType.ExclusiveClass, defaultValue: 0);
            float initialAttenuationGain = BuildInitialAttenuationGain(zone, globalZone, presetZone, presetGlobalZone);

            return new SampleRegion(
                floatPool,
                start,
                end,
                loopStart,
                loopEnd,
                loopMode,
                sourceSampleRate,
                rootKey,
                pitchCorrectionCents,
                envelope,
                filter,
                lfo,
                pan,
                reverbSend,
                chorusSend,
                exclusiveClass,
                initialAttenuationGain);
        }

        /// <summary>
        /// Reads generator 48 (InitialAttenuation, centibels) at both the instrument-zone level and the
        /// preset-zone level and sums them — SF2 spec §8.1.2 defines InitialAttenuation as additive across
        /// the preset and instrument generator levels, unlike a plain override — then converts the total
        /// to a linear gain via <see cref="CentibelsToLinear"/>, the same centibel-to-linear conversion and
        /// [0, 1440] cB clamp already used for the volume envelope's sustain level. Absent gen-48 at both
        /// levels sums to 0 cB, which maps to a gain of 1.0, so the region's amplitude is unchanged from
        /// before this generator was read.
        /// </summary>
        static float BuildInitialAttenuationGain(Sf2Zone zone, Sf2Zone? globalZone, Sf2Zone presetZone, Sf2Zone? presetGlobalZone) {
            int instrumentCentibels = GetEffectiveInt16(zone, globalZone, Sf2GeneratorType.InitialAttenuation, defaultValue: 0);
            int presetCentibels = GetEffectiveInt16(presetZone, presetGlobalZone, Sf2GeneratorType.InitialAttenuation, defaultValue: 0);
            return CentibelsToLinear(instrumentCentibels + presetCentibels);
        }

        /// <summary>
        /// Reads generator 17 (Pan) and normalises its SF2-spec ±500 raw range to [-1,1]; absent or
        /// out-of-range raw values default to/clamp toward centre (0).
        /// </summary>
        static float BuildPan(Sf2Zone zone, Sf2Zone? globalZone) {
            int raw = GetEffectiveInt16(zone, globalZone, Sf2GeneratorType.Pan, defaultValue: 0);
            if (raw > MaxPanUnits)
                raw = MaxPanUnits;
            if (raw < -MaxPanUnits)
                raw = -MaxPanUnits;
            return raw / PanUnitsDivisor;
        }

        /// <summary>
        /// Reads generator 16 (reverbEffectsSend) in 0.1%-units (0..1000 → 0..1); absent defaults to the
        /// SF2 spec's literal generator default of 0 — an absent gen-16 contributes no additive bias, so
        /// the channel's CC91 send still drives the voice on its own (combination is additive/clamped,
        /// design §9.3 revised).
        /// </summary>
        static float BuildReverbSend(Sf2Zone zone, Sf2Zone? globalZone) {
            int raw = GetEffectiveInt16(zone, globalZone, Sf2GeneratorType.ReverbEffectsSend, defaultValue: 0);
            if (raw > MaxReverbSendUnits)
                raw = MaxReverbSendUnits;
            if (raw < 0)
                raw = 0;
            return raw / ReverbSendUnitsDivisor;
        }

        /// <summary>
        /// Reads generator 15 (chorusEffectsSend) in 0.1%-units (0..1000 → 0..1); absent defaults to the
        /// SF2 spec's literal generator default of 0 — an absent gen-15 contributes no additive bias, so
        /// the channel's CC93 send still drives the voice on its own (additive/clamped combination,
        /// design #7190 §8).
        /// </summary>
        static float BuildChorusSend(Sf2Zone zone, Sf2Zone? globalZone) {
            int raw = GetEffectiveInt16(zone, globalZone, Sf2GeneratorType.ChorusEffectsSend, defaultValue: 0);
            if (raw > MaxChorusSendUnits)
                raw = MaxChorusSendUnits;
            if (raw < 0)
                raw = 0;
            return raw / ChorusSendUnitsDivisor;
        }

        static FilterParameters BuildFilterParameters(Sf2Zone zone, Sf2Zone? globalZone) {
            int cutoffCents = GetEffectiveInt16(
                zone, globalZone, Sf2GeneratorType.InitialFilterCutoffFrequency, DefaultFilterCutoffCents);
            int resonanceCentibels = GetEffectiveInt16(
                zone, globalZone, Sf2GeneratorType.InitialFilterQ, defaultValue: 0);

            float cutoffHz = FilterCutoffCentsToHz(cutoffCents);
            float resonance = FilterCentibelsToResonance(resonanceCentibels);
            return new FilterParameters(cutoffHz, resonance);
        }

        static float FilterCutoffCentsToHz(int cents) {
            if (cents >= DefaultFilterCutoffCents)
                return FilterParameters.Sf2OpenCutoffHz;
            if (cents < MinFilterCutoffCents)
                cents = MinFilterCutoffCents;
            double hz = FilterCutoffReferenceHz * Math.Pow(2.0, cents / 1200.0);
            return (float)hz;
        }

        static float FilterCentibelsToResonance(int centibels) {
            if (centibels <= 0)
                return FilterParameters.ButterworthResonance;
            if (centibels > MaxFilterResonanceCentibels)
                centibels = MaxFilterResonanceCentibels;
            double resonanceDb = centibels / 10.0;
            double q = Math.Pow(10.0, resonanceDb / 20.0) * FilterParameters.ButterworthResonance;
            return (float)q;
        }

        static LfoParameters BuildLfoParameters(Sf2Zone zone, Sf2Zone? globalZone) {
            float delay = TimecentsToSeconds(
                GetEffectiveInt16(zone, globalZone, Sf2GeneratorType.DelayModulationLFO, DefaultEnvelopeTimecents));
            float frequencyHz = LfoFrequencyCentsToHz(
                GetEffectiveInt16(zone, globalZone, Sf2GeneratorType.FrequencyModulationLFO, defaultValue: 0));
            int pitchDepthCents = GetEffectiveInt16(
                zone, globalZone, Sf2GeneratorType.ModulationLFOToPitch, defaultValue: 0);
            if (pitchDepthCents > MaxLfoPitchDepthCents)
                pitchDepthCents = MaxLfoPitchDepthCents;
            if (pitchDepthCents < -MaxLfoPitchDepthCents)
                pitchDepthCents = -MaxLfoPitchDepthCents;

            int volumeDepthCentibels = GetEffectiveInt16(
                zone, globalZone, Sf2GeneratorType.ModulationLFOToVolume, defaultValue: 0);
            if (volumeDepthCentibels > MaxLfoVolumeDepthCentibels)
                volumeDepthCentibels = MaxLfoVolumeDepthCentibels;
            if (volumeDepthCentibels < -MaxLfoVolumeDepthCentibels)
                volumeDepthCentibels = -MaxLfoVolumeDepthCentibels;

            int filterDepthCents = GetEffectiveInt16(
                zone, globalZone, Sf2GeneratorType.ModulationLFOToFilterCutoffFrequency, defaultValue: 0);
            if (filterDepthCents > MaxLfoFilterDepthCents)
                filterDepthCents = MaxLfoFilterDepthCents;
            if (filterDepthCents < -MaxLfoFilterDepthCents)
                filterDepthCents = -MaxLfoFilterDepthCents;

            return new LfoParameters(delay, frequencyHz, pitchDepthCents, volumeDepthCentibels, filterDepthCents);
        }

        static float LfoFrequencyCentsToHz(int cents) {
            double hz = FilterCutoffReferenceHz * Math.Pow(2.0, cents / 1200.0);
            if (hz < MinLfoFrequencyHz)
                hz = MinLfoFrequencyHz;
            if (hz > MaxLfoFrequencyHz)
                hz = MaxLfoFrequencyHz;
            return (float)hz;
        }

        static EnvelopeParameters BuildEnvelopeParameters(Sf2Zone zone, Sf2Zone? globalZone) {
            float delay = TimecentsToSeconds(
                GetEffectiveInt16(zone, globalZone, Sf2GeneratorType.DelayVolumeEnvelope, DefaultEnvelopeTimecents));
            float attack = TimecentsToSeconds(
                GetEffectiveInt16(zone, globalZone, Sf2GeneratorType.AttackVolumeEnvelope, DefaultEnvelopeTimecents));
            float hold = TimecentsToSeconds(
                GetEffectiveInt16(zone, globalZone, Sf2GeneratorType.HoldVolumeEnvelope, DefaultEnvelopeTimecents));
            float decay = TimecentsToSeconds(
                GetEffectiveInt16(zone, globalZone, Sf2GeneratorType.DecayVolumeEnvelope, DefaultEnvelopeTimecents));
            float sustain = CentibelsToLinear(
                GetEffectiveInt16(zone, globalZone, Sf2GeneratorType.SustainVolumeEnvelope, defaultValue: 0));
            float release = TimecentsToSeconds(
                GetEffectiveInt16(zone, globalZone, Sf2GeneratorType.ReleaseVolumeEnvelope, DefaultEnvelopeTimecents));
            return new EnvelopeParameters(delay, attack, hold, decay, sustain, release);
        }

        static float TimecentsToSeconds(int timecents) {
            double seconds = Math.Pow(2.0, timecents / 1200.0);
            if (seconds > MaxEnvelopeSeconds)
                return MaxEnvelopeSeconds;
            if (seconds < 0d)
                return 0f;
            return (float)seconds;
        }

        /// <summary>
        /// Converts a centibel attenuation amount to linear gain (<c>10^(-cB/200)</c>), clamped to the SF2
        /// valid attenuation range [0, <see cref="MaxSustainAttenuationCentibels"/>] cB — a negative amount
        /// is clamped up to 0 cB (full gain, 1.0) and an amount at or beyond the max is clamped down to
        /// silence (0.0). Shared by the volume envelope's sustain level and <see cref="BuildInitialAttenuationGain"/>'s
        /// region-level InitialAttenuation(48) gain, since both are SF2 centibel-attenuation quantities
        /// under the same [0, 1440] cB bound.
        /// </summary>
        static float CentibelsToLinear(int centibels) {
            if (centibels <= 0)
                return 1f;
            if (centibels >= MaxSustainAttenuationCentibels)
                return 0f;
            return (float)Math.Pow(10.0, -centibels / 200.0);
        }

        static int ResolveRootKey(Sf2Zone zone, Sf2Zone? globalZone, Sf2SampleHeader header) {
            int overrideRaw = GetEffectiveRaw(zone, globalZone, Sf2GeneratorType.OverridingRootKey, defaultValue: -1);
            if (overrideRaw >= 0 && overrideRaw <= 127)
                return overrideRaw;
            if (header.RootKey <= 127)
                return header.RootKey;
            return 60;
        }

        static LoopMode MapSampleModes(int modes) {
            switch (modes) {
                case 1: return LoopMode.Continuous;
                case 3: return LoopMode.Continuous;
                default: return LoopMode.NoLoop;
            }
        }

        static int GetEffectiveRaw(Sf2Zone zone, Sf2Zone? globalZone, Sf2GeneratorType type, int defaultValue) {
            if (TryFindGenerator(zone.Generators, type, out int local))
                return local;
            if (globalZone != null && TryFindGenerator(globalZone.Generators, type, out int global))
                return global;
            return defaultValue;
        }

        static int GetEffectiveInt16(Sf2Zone zone, Sf2Zone? globalZone, Sf2GeneratorType type, int defaultValue) {
            if (TryFindGenerator(zone.Generators, type, out int local))
                return (short)local;
            if (globalZone != null && TryFindGenerator(globalZone.Generators, type, out int global))
                return (short)global;
            return defaultValue;
        }

        static bool TryFindGenerator(Sf2Generator[] generators, Sf2GeneratorType type, out int rawAmount) {
            foreach (Sf2Generator gen in generators) {
                if (gen.Type == type) {
                    rawAmount = gen.RawAmount;
                    return true;
                }
            }
            rawAmount = 0;
            return false;
        }
    }
}
