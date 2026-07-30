using System;
using System.Collections.Generic;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Formats.Sf2 {

    /// <summary>
    /// Resolves a MIDI (key, velocity) pair through the SF2 two-level zone model to a
    /// <see cref="SampleRegion"/> (<see cref="TryResolve"/>, single first-covering-zone match) or to
    /// every covering zone at once (<see cref="ResolveAll"/>, the full preset-zone × instrument-zone
    /// cartesian — SF2 zone/layer stacking, DiVoid #7282), implementing the v1 generator subset plus
    /// preset-level + instrument-level InitialAttenuation(48) accumulation. This is the single home for
    /// all SF2 interpretation; later generator families (offset generators, modulation) attach here.
    /// </summary>
    /// <remarks>
    /// Resolution is deterministic, rate-independent, and defensive: structurally-valid-but-
    /// musically-imperfect SF2 content degrades to no-match rather than throwing on the note path.
    /// </remarks>
    public sealed class Sf2RegionResolver {

        const int DefaultEnvelopeTimecents = -12000;
        const float MaxEnvelopeSeconds = 20f;
        const int MaxSustainAttenuationCentibels = 1440;

        /// <summary>
        /// EMU-10K1-hardware-derived scale applied to InitialAttenuation(48) only, restoring the
        /// convention many GS-family SF2 fonts (e.g. OmegaGMGS2) are authored against. Empirically
        /// measured against FluidSynth 2.5.7 single-note isolation renders: effective scale = 0.412
        /// (linear-fit slope across gen-48 = 0/80/150 cB single-zone presets), independently confirmed
        /// by the 0.4x-scaled render collapsing those same presets' spread from 8.8 dB to 0.4 dB
        /// (DiVoid #7305). 0.4 sits well within the +/-2 dB target of the 0.412 optimum.
        /// DO NOT remove/revert this constant without re-litigating #7305 (and the reverted history at
        /// #7273/#7281/#7282 it corrects) — a prior revision of this file reverted an identical 0.4
        /// scale on the (empirically wrong) premise that FluidSynth applies gen-48 literally.
        /// </summary>
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
        /// (preset zone, instrument zone) pair — the full cartesian product, not just the first match —
        /// so that overlapping zones stack into multiple simultaneous layers while non-overlapping
        /// velocity splits still resolve to exactly one (SF2 zone/layer stacking, DiVoid #7282 §8.1).
        /// A zone "covers" the note when its KeyRange AND VelocityRange (local-then-global-zone
        /// inheritance, via <see cref="ZoneCoversNote"/>) include it — the identical rule
        /// <see cref="TryResolve"/> uses for a single match, applied exhaustively instead of stopping at
        /// the first hit. Global zones (preset-level and instrument-level) are never themselves emitted
        /// as layers; they only supply generator defaults to the zones that inherit from them. Ordering
        /// is deterministic (preset-zone source order, then instrument-zone source order within each),
        /// so caching on the returned <see cref="Sf2ResolvedLayer.CacheKey"/> and tests are reproducible.
        /// A structurally-invalid zone (bad sample bounds, missing SampleID) is skipped defensively — it
        /// never aborts the rest of the stack, matching <see cref="TryResolve"/>'s no-throw-on-note-path
        /// contract.
        /// </summary>
        /// <param name="key">MIDI key number (0–127)</param>
        /// <param name="velocity">MIDI velocity (0–127)</param>
        /// <param name="results">
        /// cleared by the caller before calling (not by this method, so the caller controls buffer
        /// reuse); every covering layer is appended in deterministic order. Empty on no-match.
        /// </param>
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
        /// Packs (presetZoneIndex, instrumentIndex, instrumentZoneIndex) into a single opaque cache key.
        /// Distinct from <see cref="TryResolve"/>'s 2-field key: the same instrument zone reached through
        /// two different preset zones bakes a different accumulated InitialAttenuation into its region
        /// (SF2 §8.1.2 additive accumulation), so presetZoneIndex must be part of the key or two distinct
        /// regions would collide onto the same cached <c>SamplePatch</c> (DiVoid #7282 §7). Each field gets
        /// 20 bits (up to ~1,048,575), far beyond any realistic SF2 zone/instrument count.
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
        /// preset-zone level — SF2 spec §8.1.2 defines InitialAttenuation as additive across the preset
        /// and instrument generator levels, unlike a plain override — and converts each level's
        /// contribution to linear gain separately before multiplying them (equivalent to summing
        /// centibels then converting, <i>except</i> for the one asymmetric case documented on
        /// <see cref="EmuAttenuationScale"/>: a preset-level contribution inherited through the
        /// <b>preset's own global zone</b> uses the literal, unscaled conversion
        /// (<see cref="LiteralAttenuationCentibelsToLinear"/>) instead of the EMU-scaled one. Absent
        /// gen-48 at both levels multiplies to a gain of 1.0, so the region's amplitude is unchanged from
        /// before this generator was read.
        /// </summary>
        static float BuildInitialAttenuationGain(Sf2Zone zone, Sf2Zone? globalZone, Sf2Zone presetZone, Sf2Zone? presetGlobalZone) {
            int instrumentCentibels = GetEffectiveInt16(zone, globalZone, Sf2GeneratorType.InitialAttenuation, defaultValue: 0);
            int presetCentibels = GetEffectiveInt16(
                presetZone, presetGlobalZone, Sf2GeneratorType.InitialAttenuation, defaultValue: 0,
                out bool presetViaGlobalZoneFallback);

            // The valid-range clamp (DiVoid #7269) is evaluated on the RAW SF2-spec total, exactly as
            // before this method split the two levels' scale treatment apart: SF2's own [0, 1440] cB
            // bound is a property of the combined attenuation regardless of which quirk-scale each
            // component eventually gets, so a combined total at/above the max must still clamp to
            // silence even when each individual component, converted on its own, would not.
            int totalCentibels = instrumentCentibels + presetCentibels;
            if (totalCentibels <= 0)
                return 1f;
            if (totalCentibels >= MaxSustainAttenuationCentibels)
                return 0f;

            float instrumentGain = AttenuationCentibelsToLinear(instrumentCentibels);
            float presetGain = presetViaGlobalZoneFallback
                ? LiteralAttenuationCentibelsToLinear(presetCentibels)
                : AttenuationCentibelsToLinear(presetCentibels);
            return instrumentGain * presetGain;
        }

        /// <summary>
        /// Converts a centibel InitialAttenuation(48) amount to linear gain using the SF2 2.04 §8.1.3
        /// conversion pre-scaled by <see cref="EmuAttenuationScale"/> — <c>10^(-EmuAttenuationScale*cB/200)</c>
        /// — clamped to [0, <see cref="MaxSustainAttenuationCentibels"/>] cB. A prior revision of this fix
        /// (task #7269) shipped this same 0.4 scale, then a later revision (task #7282, PR #31) reverted
        /// it to the literal (unscaled) formula on the premise that FluidSynth applies gen-48 literally
        /// (#7281) and that the zone/layer stacking landing alongside it (#7282) would supply the missing
        /// presence instead. <b>That premise was empirically wrong</b>: the per-voice gain audit in
        /// #7305 renders single sustained notes through FluidSynth 2.5.7 and fits the observed
        /// attenuation-vs-gen-48 slope directly — effective scale = 0.412, not 1.0 — and confirms it
        /// independently by showing a 0.4x-scaled render collapses three single-zone presets (gen-48 =
        /// 0/80/150 cB) from an 8.8 dB spread down to 0.4 dB. So the EMU ~0.4 scaling is not a hack that
        /// stacking should have obsoleted; it is FluidSynth's actual behavior for this generator. This
        /// restores the #7269 constant with #7305's empirical citation. Zone/layer stacking
        /// (<see cref="ResolveAll"/>, #7282) is CORRECT and unrelated — FluidSynth stacks companion
        /// zones too — the two fixes compose (0.4 scale per voice, all covering voices stacked), they do
        /// not substitute for each other. DO NOT revert this constant back to literal without
        /// re-litigating #7305. Used ONLY for InitialAttenuation(48); the volume envelope's sustain level
        /// (gen-37) has always used the unscaled <see cref="CentibelsToLinear"/> and is untouched by any
        /// of this.
        /// </summary>
        /// <remarks>
        /// One documented exception (DiVoid #7312/#7313, #7326): a preset-level InitialAttenuation
        /// contribution inherited through the <b>preset's own global zone</b> (rather than set directly
        /// on the matched preset zone) does NOT get this scale — see
        /// <see cref="LiteralAttenuationCentibelsToLinear"/> and its call site in
        /// <see cref="BuildInitialAttenuationGain"/>.
        /// </remarks>
        static float AttenuationCentibelsToLinear(int centibels) {
            if (centibels <= 0)
                return 1f;
            if (centibels >= MaxSustainAttenuationCentibels)
                return 0f;
            return (float)Math.Pow(10.0, -EmuAttenuationScale * centibels / 200.0);
        }

        /// <summary>
        /// Converts a centibel InitialAttenuation(48) amount to linear gain using the literal, unscaled
        /// SF2 2.04 §8.1.3 conversion (<c>10^(-cB/200)</c>, no <see cref="EmuAttenuationScale"/> pre-scale),
        /// clamped to [0, <see cref="MaxSustainAttenuationCentibels"/>] cB. Used ONLY for the preset-level
        /// InitialAttenuation contribution when <see cref="BuildInitialAttenuationGain"/> determines it
        /// was inherited through the preset's own global zone rather than set directly on the matched
        /// preset zone (DiVoid #7312/#7313, #7326).
        /// </summary>
        /// <remarks>
        /// Root-caused via single-note isolation of the Ocarina preset (program 79, OmegaGMGS2.sf2): its
        /// combined InitialAttenuation (100 cB) reaches the resolver entirely through the preset's global
        /// zone (preset zone[0]) — the four velocity-split zones that actually match a note carry no
        /// local gen-48 override. Hand-tracing <see cref="BuildInitialAttenuationGain"/>'s prior
        /// EMU-scaled-sum behavior confirmed it computed exactly the documented value (100 cB × 0.4 →
        /// gain 0.631, -4.0 dB) — the arithmetic was not wrong — yet back-solving FluidSynth 2.5.7's
        /// measured single-note RMS for the identical note (with our own linear gain model as the
        /// yardstick) implied an effective total attenuation near -10 dB, i.e. the LITERAL (unscaled)
        /// conversion of the same 100 cB, not the EMU-scaled one. A same-file cross-check with
        /// Distortion Guitar (program 30) — the only other of the five tested presets whose matched
        /// zone lacks a local gen-48 override, but at the <b>instrument</b> level (falling back to its
        /// instrument's own global zone, 240 cB) — showed NO excess: its measured offset sits inside the
        /// same baseline band as the presets with no fallback at all. So the EMU quirk demonstrably still
        /// applies to an instrument-level global-zone fallback; it is specifically the preset-level
        /// global-zone fallback that needs the literal formula. DO NOT extend this literal treatment to
        /// the instrument-level lookup in <see cref="BuildInitialAttenuationGain"/> without new evidence
        /// — the Distortion Guitar cross-check would regress.
        /// </remarks>
        static float LiteralAttenuationCentibelsToLinear(int centibels) {
            if (centibels <= 0)
                return 1f;
            if (centibels >= MaxSustainAttenuationCentibels)
                return 0f;
            return (float)Math.Pow(10.0, -centibels / 200.0);
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
        /// Converts a centibel attenuation amount to linear gain using the literal SF2 2.04 §8.1.3
        /// conversion (<c>10^(-cB/200)</c>), clamped to the SF2 valid attenuation range
        /// [0, <see cref="MaxSustainAttenuationCentibels"/>] cB — a negative amount is clamped up to 0 cB
        /// (full gain, 1.0) and an amount at or beyond the max is clamped down to silence (0.0). Used ONLY
        /// for the volume envelope's sustain level (gen-37); InitialAttenuation(48) uses its own named
        /// <see cref="AttenuationCentibelsToLinear"/> instead — numerically identical today, but kept as a
        /// separate method so gen-48 and gen-37 can diverge independently in future without entangling
        /// each other (DiVoid #7282 §8.4; do not collapse them back into one helper).
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

        static int GetEffectiveInt16(Sf2Zone zone, Sf2Zone? globalZone, Sf2GeneratorType type, int defaultValue) =>
            GetEffectiveInt16(zone, globalZone, type, defaultValue, out _);

        /// <summary>
        /// Same lookup as the 4-argument overload, plus <paramref name="viaGlobalZoneFallback"/> reporting
        /// whether the returned value came from <paramref name="zone"/> itself (<c>false</c>) or was
        /// inherited from <paramref name="globalZone"/> because <paramref name="zone"/> has no local
        /// generator of this type (<c>true</c>); <c>false</c> when neither zone carries one and
        /// <paramref name="defaultValue"/> is returned. <see cref="BuildInitialAttenuationGain"/> uses
        /// this to single out the one case (preset-level InitialAttenuation inherited through the
        /// preset's own global zone) that needs <see cref="LiteralAttenuationCentibelsToLinear"/> instead
        /// of the EMU-scaled conversion (DiVoid #7312/#7313, #7326).
        /// </summary>
        static int GetEffectiveInt16(
            Sf2Zone zone, Sf2Zone? globalZone, Sf2GeneratorType type, int defaultValue, out bool viaGlobalZoneFallback) {
            if (TryFindGenerator(zone.Generators, type, out int local)) {
                viaGlobalZoneFallback = false;
                return (short)local;
            }
            if (globalZone != null && TryFindGenerator(globalZone.Generators, type, out int global)) {
                viaGlobalZoneFallback = true;
                return (short)global;
            }
            viaGlobalZoneFallback = false;
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
