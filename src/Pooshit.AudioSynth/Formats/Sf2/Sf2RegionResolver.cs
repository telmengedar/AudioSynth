using System;
using System.Collections.Generic;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Formats.Sf2 {

    /// <summary>
    /// Resolves a MIDI (key, velocity) pair through the SF2 two-level zone model to a
    /// <see cref="SampleRegion"/> (<see cref="TryResolve"/>) or every covering zone at once
    /// (<see cref="ResolveAll"/>, for zone/layer stacking). Builds filter, LFO, and modulation-envelope
    /// descriptors via the general instrument-effective + preset-additive <see cref="EffectiveValue"/> combiner.
    /// Never throws on the note path; an invalid zone degrades to no-match.
    /// </summary>
    public sealed class Sf2RegionResolver {

        const int DefaultEnvelopeTimecents = -12000;
        const float MaxEnvelopeSeconds = 20f;
        const int MaxSustainAttenuationCentibels = 1440;

        // EMU-10K1-derived scale for InitialAttenuation(48) only, matching FluidSynth's measured effective
        // scale (~0.412) rather than the SF2 spec's literal conversion. Do not revert to literal without
        // re-verifying against a real FluidSynth render — a prior revision did, and was wrong.
        const double EmuAttenuationScale = 0.4;

        const int DefaultFilterCutoffCents = 13500;
        const int MinFilterCutoffCents = 1500;
        const int MaxFilterResonanceCentibels = 960;
        const double FilterCutoffReferenceHz = 8.176;
        const int MaxLfoPitchDepthCents = 1200;
        const float MinLfoFrequencyHz = 0.1f;
        const float MaxLfoFrequencyHz = 20f;
        const int MaxLfoVolumeDepthCentibels = 960;
        const int MaxLfoFilterDepthCents = 12000;
        const int MaxModEnvFilterDepthCents = 12000;
        const int MaxModEnvPitchDepthCents = 12000;
        const int MaxModEnvSustainUnits = 1000;
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
        /// opaque long encoding the matched (presetZoneIndex, instrumentIndex, zoneIndex) triple for the
        /// caller's cache, packed the same way as <see cref="ResolveAll"/>'s <see cref="Sf2ResolvedLayer.CacheKey"/>
        /// so both share one cache safely (see <see cref="PackCacheKey"/>);
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
                cacheKey = PackCacheKey(presetZoneIndex, instrIndex, zi);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Resolves <paramref name="key"/> and <paramref name="velocity"/> to <b>every</b> covering
        /// (preset zone, instrument zone) pair, not just the first match, so overlapping zones stack into
        /// simultaneous layers while non-overlapping velocity splits still resolve to exactly one. Global
        /// zones are never emitted as layers themselves; they only supply generator defaults. Ordering is
        /// deterministic (preset-zone then instrument-zone source order). An invalid zone is skipped, not
        /// thrown on.
        /// </summary>
        /// <param name="key">MIDI key number (0–127)</param>
        /// <param name="velocity">MIDI velocity (0–127)</param>
        /// <param name="results">buffer to append to; cleared by the caller, not this method</param>
        /// <returns>the number of layers appended to <paramref name="results"/></returns>
        public int ResolveAll(int key, int velocity, List<Sf2ResolvedLayer> results) {
            Sf2Zone[] presetZones = preset.Zones;
            int presetGlobalZoneIndex = FindPresetGlobalZoneIndex(presetZones);
            Sf2Zone? presetGlobalZone = presetGlobalZoneIndex >= 0 ? presetZones[presetGlobalZoneIndex] : null;

            int before = results.Count;

            for (int pz = 0; pz < presetZones.Length; pz++) {
                if (pz == presetGlobalZoneIndex)
                    continue;

                Sf2Zone presetZone = presetZones[pz];
                if (!TryFindGenerator(presetZone.Generators, Sf2GeneratorType.Instrument, out int instrRaw))
                    continue;

                if (!ZoneCoversNote(presetZone, presetGlobalZone, key, velocity))
                    continue;

                int instrIndex = instrRaw;
                if (instrIndex < 0 || instrIndex >= instruments.Length)
                    continue;

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

                    long cacheKey = PackCacheKey(pz, instrIndex, zi);
                    results.Add(new Sf2ResolvedLayer(built, cacheKey));
                }
            }

            return results.Count - before;
        }

        /// <summary>
        /// Packs (presetZoneIndex, instrumentIndex, instrumentZoneIndex) into a single opaque cache key,
        /// 20 bits per field. presetZoneIndex must be included: the same instrument zone reached through
        /// two different preset zones can accumulate a different InitialAttenuation, so it needs its own
        /// cache entry rather than colliding onto one.
        /// </summary>
        static long PackCacheKey(int presetZoneIndex, int instrumentIndex, int instrumentZoneIndex) =>
            ((long)(presetZoneIndex & 0xFFFFF) << 40)
            | ((long)(instrumentIndex & 0xFFFFF) << 20)
            | (uint)(instrumentZoneIndex & 0xFFFFF);

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
                + EffectiveValue(zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.FineTune, instrumentDefault: 0)
                + EffectiveValue(zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.CoarseTune, instrumentDefault: 0) * 100;

            EnvelopeParameters envelope = BuildEnvelopeParameters(zone, globalZone, presetZone, presetGlobalZone);
            FilterParameters filter = BuildFilterParameters(zone, globalZone, presetZone, presetGlobalZone);
            LfoParameters lfo = BuildLfoParameters(zone, globalZone, presetZone, presetGlobalZone);
            float pan = BuildPan(zone, globalZone, presetZone, presetGlobalZone);
            float reverbSend = BuildReverbSend(zone, globalZone, presetZone, presetGlobalZone);
            float chorusSend = BuildChorusSend(zone, globalZone, presetZone, presetGlobalZone);
            int exclusiveClass = GetEffectiveRaw(zone, globalZone, Sf2GeneratorType.ExclusiveClass, defaultValue: 0);
            float initialAttenuationGain = BuildInitialAttenuationGain(zone, globalZone, presetZone, presetGlobalZone);
            (ModulationEnvelopeParameters modEnv, float modEnvHoldTc, float modEnvDecayTc, float modEnvHoldKeyC, float modEnvDecayKeyC) =
                BuildModEnvParameters(zone, globalZone, presetZone, presetGlobalZone);
            float modEnvToPitchCents = BuildModEnvToPitchCents(zone, globalZone, presetZone, presetGlobalZone);

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
                initialAttenuationGain,
                modEnv,
                modEnvHoldTc,
                modEnvDecayTc,
                modEnvHoldKeyC,
                modEnvDecayKeyC,
                modEnvToPitchCents);
        }

        /// <summary>
        /// Reads generator 7 (ModulationEnvelopeToPitch, cents) via <see cref="EffectiveValue"/>, clamped to
        /// ±<see cref="MaxModEnvPitchDepthCents"/>. Key/velocity-independent, so it bakes straight into the
        /// region — unlike hold/decay, gen-7 has no keynum coefficient pair.
        /// </summary>
        static float BuildModEnvToPitchCents(Sf2Zone zone, Sf2Zone? globalZone, Sf2Zone presetZone, Sf2Zone? presetGlobalZone) {
            int cents = EffectiveValue(
                zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.ModulationEnvelopeToPitch, instrumentDefault: 0);
            if (cents > MaxModEnvPitchDepthCents)
                cents = MaxModEnvPitchDepthCents;
            if (cents < -MaxModEnvPitchDepthCents)
                cents = -MaxModEnvPitchDepthCents;
            return cents;
        }

        /// <summary>
        /// Reads generator 48 (InitialAttenuation, centibels) via <see cref="EffectiveValue"/> — additive
        /// across preset and instrument levels per the SF2 spec — and converts the total to linear gain
        /// via <see cref="AttenuationCentibelsToLinear"/>. Absent at both levels sums to 0 cB (gain 1.0,
        /// unchanged). Uses its own conversion rather than the shared <see cref="CentibelsToLinear"/>
        /// (gen-37's), since gen-48 carries the <see cref="EmuAttenuationScale"/> pre-scale and gen-37
        /// does not.
        /// </summary>
        static float BuildInitialAttenuationGain(Sf2Zone zone, Sf2Zone? globalZone, Sf2Zone presetZone, Sf2Zone? presetGlobalZone) {
            int totalCentibels = EffectiveValue(
                zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.InitialAttenuation, instrumentDefault: 0);
            return AttenuationCentibelsToLinear(totalCentibels);
        }

        /// <summary>
        /// General preset-generator routing combiner: the effective value of a "value" generator is
        /// <c>instrumentEffective + presetAdditive</c>, where <c>instrumentEffective</c> is the ordinary
        /// instrument-level resolution (local zone, else instrument global zone, else
        /// <paramref name="instrumentDefault"/>) and <c>presetAdditive</c> is the preset-level contribution
        /// (preset local zone, else preset global zone, else <b>0</b> — never the generator's musical
        /// default, or an absent preset generator would double-count it). Only "value" generators route
        /// through here; address offsets, KeyNumber/Velocity/SampleModes/ExclusiveClass/OverridingRootKey/
        /// SampleID/Instrument stay instrument-only via <see cref="GetEffectiveInt16"/>/
        /// <see cref="GetEffectiveRaw"/>, and KeyRange/VelocityRange are handled by
        /// <see cref="ZoneCoversNote"/> instead.
        /// </summary>
        static int EffectiveValue(
            Sf2Zone zone, Sf2Zone? globalZone,
            Sf2Zone presetZone, Sf2Zone? presetGlobalZone,
            Sf2GeneratorType type, int instrumentDefault) {
            int instrumentEffective = GetEffectiveInt16(zone, globalZone, type, instrumentDefault);
            int presetAdditive = GetEffectiveInt16(presetZone, presetGlobalZone, type, defaultValue: 0);
            return instrumentEffective + presetAdditive;
        }

        /// <summary>
        /// Converts a centibel InitialAttenuation(48) amount to linear gain using the SF2 2.04 §8.1.3
        /// conversion pre-scaled by <see cref="EmuAttenuationScale"/>, clamped to
        /// [0, <see cref="MaxSustainAttenuationCentibels"/>] cB. Used only for gen-48; the volume
        /// envelope's sustain level (gen-37) uses the unscaled <see cref="CentibelsToLinear"/> instead, and
        /// zone/layer stacking (<see cref="ResolveAll"/>) is orthogonal — both apply together.
        /// </summary>
        static float AttenuationCentibelsToLinear(int centibels) {
            if (centibels <= 0)
                return 1f;
            if (centibels >= MaxSustainAttenuationCentibels)
                return 0f;
            return (float)Math.Pow(10.0, -EmuAttenuationScale * centibels / 200.0);
        }

        /// <summary>
        /// Reads generator 17 (Pan) via the general <see cref="EffectiveValue"/> preset-routing combiner
        /// and normalises its SF2-spec ±500 raw range to [-1,1]; absent or out-of-range raw values
        /// default to/clamp toward centre (0).
        /// </summary>
        static float BuildPan(Sf2Zone zone, Sf2Zone? globalZone, Sf2Zone presetZone, Sf2Zone? presetGlobalZone) {
            int raw = EffectiveValue(zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.Pan, instrumentDefault: 0);
            if (raw > MaxPanUnits)
                raw = MaxPanUnits;
            if (raw < -MaxPanUnits)
                raw = -MaxPanUnits;
            return raw / PanUnitsDivisor;
        }

        /// <summary>
        /// Reads generator 16 (reverbEffectsSend) via the general <see cref="EffectiveValue"/> combiner in
        /// 0.1%-units (0..1000 → 0..1); absent at both levels defaults to the SF2 spec's literal generator
        /// default of 0 — an absent gen-16 contributes no additive bias, so the channel's CC91 send still
        /// drives the voice on its own (combination is additive/clamped, design §9.3 revised).
        /// </summary>
        static float BuildReverbSend(Sf2Zone zone, Sf2Zone? globalZone, Sf2Zone presetZone, Sf2Zone? presetGlobalZone) {
            int raw = EffectiveValue(zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.ReverbEffectsSend, instrumentDefault: 0);
            if (raw > MaxReverbSendUnits)
                raw = MaxReverbSendUnits;
            if (raw < 0)
                raw = 0;
            return raw / ReverbSendUnitsDivisor;
        }

        /// <summary>
        /// Reads generator 15 (chorusEffectsSend) via <see cref="EffectiveValue"/> in 0.1%-units
        /// (0..1000 → 0..1); absent at both levels defaults to 0, so the channel's CC93 send alone drives
        /// the voice.
        /// </summary>
        static float BuildChorusSend(Sf2Zone zone, Sf2Zone? globalZone, Sf2Zone presetZone, Sf2Zone? presetGlobalZone) {
            int raw = EffectiveValue(zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.ChorusEffectsSend, instrumentDefault: 0);
            if (raw > MaxChorusSendUnits)
                raw = MaxChorusSendUnits;
            if (raw < 0)
                raw = 0;
            return raw / ChorusSendUnitsDivisor;
        }

        /// <summary>
        /// Builds the static filter descriptor from gen-8 (cutoff), gen-9 (Q), and gen-11 (mod-envelope-to-
        /// cutoff depth), each combined via <see cref="EffectiveValue"/> so a preset-level filter generator
        /// reaches the region. The base cutoff is never collapsed to an open decision here — the effective
        /// (base + LFO + mod-env) cutoff is what decides open-vs-filtered, per control tick, in
        /// <see cref="Pooshit.AudioSynth.Synthesis.Voices.SamplePlaybackVoice"/>.
        /// </summary>
        static FilterParameters BuildFilterParameters(Sf2Zone zone, Sf2Zone? globalZone, Sf2Zone presetZone, Sf2Zone? presetGlobalZone) {
            int cutoffCents = EffectiveValue(
                zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.InitialFilterCutoffFrequency, DefaultFilterCutoffCents);
            int resonanceCentibels = EffectiveValue(
                zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.InitialFilterQ, instrumentDefault: 0);
            int modEnvToCutoffCents = EffectiveValue(
                zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.ModulationEnvelopeToFilterCutoffFrequency, instrumentDefault: 0);
            if (modEnvToCutoffCents > MaxModEnvFilterDepthCents)
                modEnvToCutoffCents = MaxModEnvFilterDepthCents;
            if (modEnvToCutoffCents < -MaxModEnvFilterDepthCents)
                modEnvToCutoffCents = -MaxModEnvFilterDepthCents;

            float cutoffHz = FilterCutoffCentsToHz(cutoffCents);
            float resonance = FilterCentibelsToResonance(resonanceCentibels);
            return new FilterParameters(cutoffHz, resonance, modEnvToCutoffCents);
        }

        /// <summary>
        /// Builds the modulation-envelope descriptor from gens 25/26/29/30 (delay/attack/sustain/release,
        /// key-independent) plus the raw gen-27/28 hold/decay timecents and gen-31/32 keynum coefficients,
        /// which <see cref="Pooshit.AudioSynth.Synthesis.Patches.SamplePatch.StartVoice"/> re-resolves per played key.
        /// </summary>
        static (ModulationEnvelopeParameters modEnv, float holdTimecents, float decayTimecents, float holdKeynumCents, float decayKeynumCents)
            BuildModEnvParameters(Sf2Zone zone, Sf2Zone? globalZone, Sf2Zone presetZone, Sf2Zone? presetGlobalZone) {
            float delay = TimecentsToSeconds(
                EffectiveValue(zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.DelayModulationEnvelope, DefaultEnvelopeTimecents));
            float attack = TimecentsToSeconds(
                EffectiveValue(zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.AttackModulationEnvelope, DefaultEnvelopeTimecents));
            float release = TimecentsToSeconds(
                EffectiveValue(zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.ReleaseModulationEnvelope, DefaultEnvelopeTimecents));
            int sustainRaw = EffectiveValue(
                zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.SustainModulationEnvelope, instrumentDefault: 0);
            float sustainLevel = ModEnvSustainUnitsToLevel(sustainRaw);
            int holdTimecents = EffectiveValue(
                zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.HoldModulationEnvelope, DefaultEnvelopeTimecents);
            int decayTimecents = EffectiveValue(
                zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.DecayModulationEnvelope, DefaultEnvelopeTimecents);
            int holdKeynumCents = EffectiveValue(
                zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.KeyNumberToModulationEnvelopeHold, instrumentDefault: 0);
            int decayKeynumCents = EffectiveValue(
                zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.KeyNumberToModulationEnvelopeDecay, instrumentDefault: 0);

            ModulationEnvelopeParameters modEnv = new ModulationEnvelopeParameters(
                delay, attack, TimecentsToSeconds(holdTimecents), TimecentsToSeconds(decayTimecents), sustainLevel, release);
            return (modEnv, holdTimecents, decayTimecents, holdKeynumCents, decayKeynumCents);
        }

        /// <summary>
        /// Converts gen-29 (0.1%-units, a decrease from full) to the unipolar sustain level consumed by
        /// <see cref="ModulationEnvelope"/>: <c>1 - units/1000</c>, distinct from the volume envelope's
        /// centibel-attenuation sustain (<see cref="CentibelsToLinear"/>).
        /// </summary>
        static float ModEnvSustainUnitsToLevel(int tenthPercentUnits) {
            if (tenthPercentUnits <= 0)
                return 1f;
            if (tenthPercentUnits >= MaxModEnvSustainUnits)
                return 0f;
            return 1f - tenthPercentUnits / (float)MaxModEnvSustainUnits;
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

        static LfoParameters BuildLfoParameters(Sf2Zone zone, Sf2Zone? globalZone, Sf2Zone presetZone, Sf2Zone? presetGlobalZone) {
            float delay = TimecentsToSeconds(
                EffectiveValue(zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.DelayModulationLFO, DefaultEnvelopeTimecents));
            float frequencyHz = LfoFrequencyCentsToHz(
                EffectiveValue(zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.FrequencyModulationLFO, instrumentDefault: 0));
            int pitchDepthCents = EffectiveValue(
                zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.ModulationLFOToPitch, instrumentDefault: 0);
            if (pitchDepthCents > MaxLfoPitchDepthCents)
                pitchDepthCents = MaxLfoPitchDepthCents;
            if (pitchDepthCents < -MaxLfoPitchDepthCents)
                pitchDepthCents = -MaxLfoPitchDepthCents;

            int volumeDepthCentibels = EffectiveValue(
                zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.ModulationLFOToVolume, instrumentDefault: 0);
            if (volumeDepthCentibels > MaxLfoVolumeDepthCentibels)
                volumeDepthCentibels = MaxLfoVolumeDepthCentibels;
            if (volumeDepthCentibels < -MaxLfoVolumeDepthCentibels)
                volumeDepthCentibels = -MaxLfoVolumeDepthCentibels;

            int filterDepthCents = EffectiveValue(
                zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.ModulationLFOToFilterCutoffFrequency, instrumentDefault: 0);
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

        static EnvelopeParameters BuildEnvelopeParameters(Sf2Zone zone, Sf2Zone? globalZone, Sf2Zone presetZone, Sf2Zone? presetGlobalZone) {
            float delay = TimecentsToSeconds(
                EffectiveValue(zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.DelayVolumeEnvelope, DefaultEnvelopeTimecents));
            float attack = TimecentsToSeconds(
                EffectiveValue(zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.AttackVolumeEnvelope, DefaultEnvelopeTimecents));
            float hold = TimecentsToSeconds(
                EffectiveValue(zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.HoldVolumeEnvelope, DefaultEnvelopeTimecents));
            float decay = TimecentsToSeconds(
                EffectiveValue(zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.DecayVolumeEnvelope, DefaultEnvelopeTimecents));
            float sustain = CentibelsToLinear(
                EffectiveValue(zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.SustainVolumeEnvelope, instrumentDefault: 0));
            float release = TimecentsToSeconds(
                EffectiveValue(zone, globalZone, presetZone, presetGlobalZone, Sf2GeneratorType.ReleaseVolumeEnvelope, DefaultEnvelopeTimecents));
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
        /// Converts a centibel attenuation amount to linear gain using the literal SF2 2.04 §8.1.3
        /// conversion (<c>10^(-cB/200)</c>), clamped to [0, <see cref="MaxSustainAttenuationCentibels"/>] cB.
        /// Used only for the volume envelope's sustain level (gen-37); InitialAttenuation(48) uses its own
        /// <see cref="AttenuationCentibelsToLinear"/> so the two can diverge independently.
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
