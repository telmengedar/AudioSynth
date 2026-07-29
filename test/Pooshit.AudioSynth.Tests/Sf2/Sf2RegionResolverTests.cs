using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Formats.Sf2;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Unit tests for <see cref="Sf2RegionResolver"/> covering the v1 generator subset:
    /// zone match hit/miss, global-zone defaults, local-overrides-global, rootKey resolution,
    /// tune-to-cents mapping, SampleModes-to-LoopMode, and the Sf2SampleData float-pool normalization.
    /// All tests construct SF2 model objects directly without going through the binary loader.
    /// </summary>
    [TestFixture]
    public class Sf2RegionResolverTests {

        static Sf2Generator Gen(Sf2GeneratorType type, ushort amount) =>
            new Sf2Generator(type, amount);

        static Sf2Zone Zone(params Sf2Generator[] gens) =>
            new Sf2Zone(gens, Array.Empty<Sf2Modulator>());

        static Sf2SampleHeader Header(uint start, uint end, uint loopStart, uint loopEnd,
            uint sampleRate = 44100, byte rootKey = 60, sbyte pitchCorrection = 0) =>
            new Sf2SampleHeader("S", start, end, loopStart, loopEnd, sampleRate,
                rootKey, pitchCorrection, 0, Sf2SampleLink.MonoSample);

        static (Sf2RegionResolver resolver, Sf2SampleData sampleData) BuildResolver(
            Sf2Zone[] presetZones,
            Sf2Zone[] instrumentZones,
            Sf2SampleHeader? header = null,
            short[]? smpl = null) {
            short[] pool = smpl ?? BuildFullScalePool(8);
            Sf2SampleData data = new Sf2SampleData(pool);
            Sf2SampleHeader hdr = header ?? Header(0, (uint)pool.Length, 0, (uint)pool.Length);
            Sf2Instrument instrument = new Sf2Instrument("Inst", instrumentZones);
            Sf2PresetHeader preset = new Sf2PresetHeader("P", 0, 0, presetZones);
            Sf2RegionResolver resolver = new Sf2RegionResolver(preset, new[] { instrument }, new[] { hdr }, data.FloatPool);
            return (resolver, data);
        }

        static Sf2Zone[] PresetZoneWithInstrument(int instrIndex = 0) =>
            new[] { Zone(Gen(Sf2GeneratorType.Instrument, (ushort)instrIndex)) };

        static Sf2Zone[] PresetZoneWithInstrumentAndAttenuation(int instrIndex, short attenuationCentibels) =>
            new[] { Zone(Gen(Sf2GeneratorType.Instrument, (ushort)instrIndex),
                         Gen(Sf2GeneratorType.InitialAttenuation, (ushort)attenuationCentibels)) };

        static Sf2Zone InstrumentZone(int keyLo, int keyHi, params Sf2Generator[] extras) {
            ushort keyRangeAmount = (ushort)(keyLo | (keyHi << 8));
            Sf2Generator keyRange = Gen(Sf2GeneratorType.KeyRange, keyRangeAmount);
            Sf2Generator sampleId = Gen(Sf2GeneratorType.SampleID, 0);
            Sf2Generator[] all = new Sf2Generator[2 + extras.Length];
            all[0] = keyRange;
            all[1] = sampleId;
            for (int i = 0; i < extras.Length; i++)
                all[2 + i] = extras[i];
            return new Sf2Zone(all, Array.Empty<Sf2Modulator>());
        }

        static short[] BuildFullScalePool(int frames) {
            short[] pool = new short[frames];
            for (int i = 0; i < frames; i++)
                pool[i] = 32767;
            return pool;
        }

        [Test]
        [Description("TryResolve returns true when key falls within the instrument zone's key range.")]
        public void TryResolve_KeyInRange_ReturnsTrue() {
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { InstrumentZone(50, 70) });

            bool found = resolver.TryResolve(60, 100, out _, out _);

            Assert.That(found, Is.True, "Key 60 in [50,70] must resolve.");
        }

        [Test]
        [Description("TryResolve returns false when key falls outside the instrument zone's key range.")]
        public void TryResolve_KeyOutOfRange_ReturnsFalse() {
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { InstrumentZone(50, 59) });

            bool found = resolver.TryResolve(60, 100, out _, out _);

            Assert.That(found, Is.False, "Key 60 outside [50,59] must not resolve.");
        }

        [Test]
        [Description("TryResolve returns false when no preset zone carries an Instrument(41) generator.")]
        public void TryResolve_NoInstrumentGen_ReturnsFalse() {
            Sf2Zone[] zones = new[] { Zone() };
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(zones, new[] { InstrumentZone(0, 127) });

            bool found = resolver.TryResolve(60, 100, out _, out _);

            Assert.That(found, Is.False, "Preset zone without Instrument gen must not resolve.");
        }

        [Test]
        [Description("Global instrument zone provides FineTune default when the local zone has none.")]
        public void TryResolve_GlobalZoneFineTune_AppliedWhenLocalHasNone() {
            Sf2Zone globalZone = Zone(Gen(Sf2GeneratorType.FineTune, (ushort)(short)30));
            Sf2Zone localZone = InstrumentZone(0, 127);
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { globalZone, localZone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.PitchCorrectionCents, Is.EqualTo(30),
                "FineTune=30 from global zone must appear in pitchCorrectionCents.");
        }

        [Test]
        [Description("Local zone FineTune overrides the global zone's FineTune.")]
        public void TryResolve_LocalFineTuneOverridesGlobal() {
            Sf2Zone globalZone = Zone(Gen(Sf2GeneratorType.FineTune, (ushort)(short)10));
            Sf2Zone localZone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.FineTune, (ushort)(short)25));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { globalZone, localZone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.PitchCorrectionCents, Is.EqualTo(25),
                "Local FineTune=25 must override global FineTune=10.");
        }

        [Test]
        [Description("OverridingRootKey(58) in 0–127 takes precedence over the sample-header root key.")]
        public void TryResolve_OverridingRootKey_TakesPrecedence() {
            Sf2SampleHeader header = Header(0, 8, 0, 8, 44100, rootKey: 48);
            Sf2Zone localZone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.OverridingRootKey, 72));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { localZone },
                header: header);

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.RootKey, Is.EqualTo(72),
                "OverridingRootKey(58)=72 must override header RootKey=48.");
        }

        [Test]
        [Description("Header RootKey is used when no OverridingRootKey generator is present.")]
        public void TryResolve_NoOverrideRootKey_UsesHeaderRootKey() {
            Sf2SampleHeader header = Header(0, 8, 0, 8, 44100, rootKey: 48);
            Sf2Zone localZone = InstrumentZone(0, 127);
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { localZone },
                header: header);

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.RootKey, Is.EqualTo(48),
                "When no OverridingRootKey gen, header RootKey=48 must be used.");
        }

        [Test]
        [Description("Header RootKey=255 (unpitched) with no OverridingRootKey defaults to key 60 (D3).")]
        public void TryResolve_HeaderRootKey255_DefaultsTo60() {
            Sf2SampleHeader header = Header(0, 8, 0, 8, 44100, rootKey: 255);
            Sf2Zone localZone = InstrumentZone(0, 127);
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { localZone },
                header: header);

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.RootKey, Is.EqualTo(60),
                "Unpitched header RootKey=255 with no override must default to 60.");
        }

        [Test]
        [Description("CoarseTune(51) is multiplied by 100 and added to pitchCorrectionCents.")]
        public void TryResolve_CoarseTune_MultipliedBy100() {
            Sf2Zone localZone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.CoarseTune, (ushort)(short)7));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { localZone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.PitchCorrectionCents, Is.EqualTo(700),
                "CoarseTune=7 semitones must contribute 700 cents.");
        }

        [Test]
        [Description("FineTune(52) plus header PitchCorrection fold into pitchCorrectionCents.")]
        public void TryResolve_FineTuneAndHeaderCorrection_SumInCents() {
            Sf2SampleHeader header = Header(0, 8, 0, 8, 44100, rootKey: 60, pitchCorrection: 10);
            Sf2Zone localZone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.FineTune, (ushort)(short)15));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { localZone },
                header: header);

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.PitchCorrectionCents, Is.EqualTo(25),
                "pitchCorrectionCents must be header(10) + FineTune(15) = 25.");
        }

        [Test]
        [Description("SampleModes(54)=0 maps to LoopMode.NoLoop.")]
        public void TryResolve_SampleModes0_MapsToNoLoop() {
            Sf2Zone localZone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.SampleModes, 0));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { localZone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.LoopMode, Is.EqualTo(LoopMode.NoLoop));
        }

        [Test]
        [Description("SampleModes(54)=1 maps to LoopMode.Continuous when loop points are valid.")]
        public void TryResolve_SampleModes1_MapsToContinuous() {
            Sf2SampleHeader header = Header(0, 8, 0, 8, 44100, rootKey: 60);
            Sf2Zone localZone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.SampleModes, 1));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { localZone },
                header: header);

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.LoopMode, Is.EqualTo(LoopMode.Continuous));
        }

        [Test]
        [Description("SampleModes(54)=3 (loop-until-release) maps to LoopMode.Continuous in v1.")]
        public void TryResolve_SampleModes3_MapsToContiniuousV1() {
            Sf2SampleHeader header = Header(0, 8, 0, 8, 44100, rootKey: 60);
            Sf2Zone localZone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.SampleModes, 3));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { localZone },
                header: header);

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.LoopMode, Is.EqualTo(LoopMode.Continuous),
                "SampleModes=3 (loop-until-release) is Continuous in v1.");
        }

        [Test]
        [Description("SampleModes=1 with invalid loop points falls back to NoLoop (defensive §9).")]
        public void TryResolve_SampleModes1_InvalidLoopPoints_FallsBackToNoLoop() {
            Sf2SampleHeader header = Header(0, 8, 8, 8, 44100, rootKey: 60);
            Sf2Zone localZone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.SampleModes, 1));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { localZone },
                header: header);

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.LoopMode, Is.EqualTo(LoopMode.NoLoop),
                "loopStart=8 >= loopEnd=8 is invalid; resolver must fall back to NoLoop.");
        }

        [Test]
        [Description("Sf2SampleData.FloatPool: full-scale positive 16-bit word normalises to ~+1.0.")]
        public void FloatPool_FullScalePositive_NormalisesNearPlusOne() {
            Sf2SampleData data = new Sf2SampleData(new short[] { 32767 });

            float value = data.FloatPool[0];

            Assert.That(value, Is.InRange(0.9999f, 1.0001f),
                "32767 / 32768 must normalise to approximately +1.0.");
        }

        [Test]
        [Description("Sf2SampleData.FloatPool: full-scale negative 16-bit word normalises to -1.0.")]
        public void FloatPool_FullScaleNegative_NormalisesNegativeOne() {
            Sf2SampleData data = new Sf2SampleData(new short[] { unchecked((short)-32768) });

            float value = data.FloatPool[0];

            Assert.That(value, Is.EqualTo(-1.0f),
                "-32768 / 32768 must normalise to exactly -1.0.");
        }

        [Test]
        [Description("Sf2SampleData.FloatPool length equals FrameCount.")]
        public void FloatPool_Length_EqualsFrameCount() {
            short[] smpl = new short[42];
            Sf2SampleData data = new Sf2SampleData(smpl);

            Assert.That(data.FloatPool.Length, Is.EqualTo(data.FrameCount));
        }

        [Test]
        [Description("Sf2SampleData.FloatPool returns the same array instance on every call.")]
        public void FloatPool_SameInstanceOnEveryCall() {
            Sf2SampleData data = new Sf2SampleData(new short[] { 100, 200 });

            float[] first = data.FloatPool;
            float[] second = data.FloatPool;

            Assert.That(ReferenceEquals(first, second), Is.True,
                "FloatPool must return the same cached array every time.");
        }

        [Test]
        [Description("Absent volume-envelope generators fall back to the SF2 default attack time (≈0.977 ms).")]
        public void TryResolve_NoVolEnvGens_UsesSf2DefaultTimes() {
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { InstrumentZone(0, 127) });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.Envelope.AttackSeconds, Is.EqualTo(EnvelopeParameters.Sf2DefaultTimeSeconds).Within(1e-5f),
                "absent AttackVolumeEnvelope must map to the SF2 default time.");
            Assert.That(region.Envelope.SustainLevel, Is.EqualTo(1f),
                "absent SustainVolumeEnvelope (0 cB) must map to full level.");
        }

        [Test]
        [Description("AttackVolumeEnvelope(34)=0 timecents maps to a 1-second attack (2^0).")]
        public void TryResolve_AttackVolEnvZeroTimecents_MapsToOneSecond() {
            Sf2Zone localZone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.AttackVolumeEnvelope, 0));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { localZone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.Envelope.AttackSeconds, Is.EqualTo(1f).Within(1e-4f),
                "0 timecents must map to 2^0 = 1 second.");
        }

        [Test]
        [Description("SustainVolumeEnvelope(37)=100 cB attenuation maps to a linear level of ~0.316.")]
        public void TryResolve_SustainVolEnv100Centibels_MapsToLinearLevel() {
            Sf2Zone localZone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.SustainVolumeEnvelope, 100));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { localZone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.Envelope.SustainLevel, Is.EqualTo(0.316f).Within(0.005f),
                "100 cB (10 dB) of attenuation must map to 10^(-100/200) ≈ 0.316.");
        }

        [Test]
        [Description("Local zone volume-envelope generator overrides the global instrument zone's value.")]
        public void TryResolve_LocalVolEnvOverridesGlobal() {
            Sf2Zone globalZone = Zone(Gen(Sf2GeneratorType.AttackVolumeEnvelope, 0));
            Sf2Zone localZone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.AttackVolumeEnvelope, (ushort)(short)1200));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { globalZone, localZone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.Envelope.AttackSeconds, Is.EqualTo(2f).Within(1e-4f),
                "local AttackVolumeEnvelope=1200 tc (2 s) must override global 0 tc (1 s).");
        }

        [Test]
        [Description("Resolved cacheKey is consistent across identical note lookups.")]
        public void TryResolve_SameNote_SameCacheKey() {
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { InstrumentZone(0, 127) });

            resolver.TryResolve(60, 100, out _, out long key1);
            resolver.TryResolve(60, 100, out _, out long key2);

            Assert.That(key1, Is.EqualTo(key2),
                "The same note must produce the same cacheKey for idempotent caching.");
        }

        [Test]
        [Description("Absent initial-filter generators yield the open default cutoff (SF2 13500-cent sentinel).")]
        public void TryResolve_NoFilterGenerators_YieldsOpenCutoff() {
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { InstrumentZone(0, 127) });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.Filter.CutoffHz, Is.EqualTo(FilterParameters.Sf2OpenCutoffHz).Within(1e-3f),
                "Absent InitialFilterCutoffFrequency must map to the open default cutoff.");
            Assert.That(region.Filter.Resonance, Is.EqualTo(FilterParameters.ButterworthResonance).Within(1e-4f),
                "Absent InitialFilterQ must map to a flat Butterworth resonance.");
        }

        [Test]
        [Description("A low InitialFilterCutoffFrequency generator maps to its absolute-cents frequency in hertz.")]
        public void TryResolve_LowCutoffGenerator_MapsToHertz() {
            Sf2Zone zone = InstrumentZone(0, 127,
                Gen(Sf2GeneratorType.InitialFilterCutoffFrequency, 4500));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { zone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            float expectedHz = (float)(8.176 * System.Math.Pow(2.0, 4500.0 / 1200.0));
            Assert.That(found, Is.True);
            Assert.That(region!.Filter.CutoffHz, Is.EqualTo(expectedHz).Within(expectedHz * 0.001f),
                "4500 cents must map to 8.176 * 2^(4500/1200) Hz.");
            Assert.That(region.Filter.CutoffHz, Is.LessThan(FilterParameters.Sf2OpenCutoffHz),
                "A 4500-cent cutoff is well below the open sentinel and must not be treated as open.");
        }

        [Test]
        [Description("A positive InitialFilterQ generator raises resonance above the flat Butterworth value.")]
        public void TryResolve_ResonanceGenerator_RaisesResonance() {
            Sf2Zone zone = InstrumentZone(0, 127,
                Gen(Sf2GeneratorType.InitialFilterCutoffFrequency, 4500),
                Gen(Sf2GeneratorType.InitialFilterQ, 120));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { zone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.Filter.Resonance, Is.GreaterThan(FilterParameters.ButterworthResonance),
                "120 cB of resonance must raise Q above the flat Butterworth value.");
        }

        [Test]
        [Description("Absent mod-LFO generators yield the SF2 default frequency (8.176 Hz) and zero pitch depth (inert).")]
        public void TryResolve_NoLfoGenerators_YieldsSf2DefaultsAndZeroDepth() {
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { InstrumentZone(0, 127) });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.Lfo.FrequencyHz, Is.EqualTo(8.176f).Within(0.01f),
                "Absent FrequencyModulationLFO must map to the 0-cent default (8.176 Hz).");
            Assert.That(region.Lfo.PitchDepthCents, Is.EqualTo(0f),
                "Absent ModulationLFOToPitch must map to zero depth (inert).");
        }

        [Test]
        [Description("FrequencyModulationLFO(22) generator maps absolute cents to hertz via 8.176 * 2^(cents/1200).")]
        public void TryResolve_LfoFrequencyGenerator_MapsToHertz() {
            Sf2Zone zone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.FrequencyModulationLFO, 1200));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { zone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            float expectedHz = (float)(8.176 * System.Math.Pow(2.0, 1200.0 / 1200.0));
            Assert.That(found, Is.True);
            Assert.That(region!.Lfo.FrequencyHz, Is.EqualTo(expectedHz).Within(expectedHz * 0.01f),
                "1200 cents must map to 8.176 * 2^1 Hz.");
        }

        [Test]
        [Description("ModulationLFOToPitch(5) generator maps directly to PitchDepthCents.")]
        public void TryResolve_LfoPitchDepthGenerator_MapsToPitchDepthCents() {
            Sf2Zone zone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.ModulationLFOToPitch, unchecked((ushort)(short)150)));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { zone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.Lfo.PitchDepthCents, Is.EqualTo(150f),
                "ModulationLFOToPitch=150 cents must map directly to PitchDepthCents.");
        }

        [Test]
        [Description("A positive ModulationLFOToPitch generator beyond the ±1200-cent stability cap is clamped to +1200.")]
        public void TryResolve_LfoPitchDepthBeyondPositiveCap_IsClamped() {
            Sf2Zone zone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.ModulationLFOToPitch, 4000));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { zone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.Lfo.PitchDepthCents, Is.EqualTo(1200f),
                "4000-cent depth must clamp to the +1200-cent stability cap.");
        }

        [Test]
        [Description("A negative ModulationLFOToPitch generator beyond the ±1200-cent stability cap is clamped to -1200.")]
        public void TryResolve_LfoPitchDepthBeyondNegativeCap_IsClamped() {
            Sf2Zone zone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.ModulationLFOToPitch, unchecked((ushort)(short)-4000)));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { zone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.Lfo.PitchDepthCents, Is.EqualTo(-1200f),
                "-4000-cent depth must clamp to the -1200-cent stability cap.");
        }

        [Test]
        [Description("A very low FrequencyModulationLFO generator clamps to the minimum stable LFO frequency (0.1 Hz).")]
        public void TryResolve_LfoFrequencyBelowMin_IsClampedToMinimum() {
            Sf2Zone zone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.FrequencyModulationLFO, unchecked((ushort)(short)-8000)));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { zone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.Lfo.FrequencyHz, Is.EqualTo(0.1f).Within(1e-4f),
                "-8000 cents must clamp to the 0.1 Hz stability floor.");
        }

        [Test]
        [Description("A very high FrequencyModulationLFO generator clamps to the maximum stable LFO frequency (20 Hz).")]
        public void TryResolve_LfoFrequencyAboveMax_IsClampedToMaximum() {
            Sf2Zone zone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.FrequencyModulationLFO, 2400));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { zone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.Lfo.FrequencyHz, Is.EqualTo(20f).Within(1e-3f),
                "2400 cents must clamp to the 20 Hz stability ceiling.");
        }

        [Test]
        [Description("Local zone mod-LFO generators override the global instrument zone's values.")]
        public void TryResolve_LocalLfoGeneratorsOverrideGlobal() {
            Sf2Zone globalZone = Zone(Gen(Sf2GeneratorType.ModulationLFOToPitch, unchecked((ushort)(short)50)));
            Sf2Zone localZone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.ModulationLFOToPitch, unchecked((ushort)(short)300)));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { globalZone, localZone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.Lfo.PitchDepthCents, Is.EqualTo(300f),
                "local ModulationLFOToPitch=300 must override global ModulationLFOToPitch=50.");
        }

        [Test]
        [Description("Absent ModulationLFOToVolume(13) and ModulationLFOToFilterCutoffFrequency(10) generators yield zero depth (inert).")]
        public void TryResolve_NoTremoloOrSweepGenerators_YieldsZeroDepth() {
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { InstrumentZone(0, 127) });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.Lfo.VolumeDepthCentibels, Is.EqualTo(0f),
                "Absent ModulationLFOToVolume must map to zero depth (inert).");
            Assert.That(region.Lfo.FilterDepthCents, Is.EqualTo(0f),
                "Absent ModulationLFOToFilterCutoffFrequency must map to zero depth (inert).");
        }

        [Test]
        [Description("ModulationLFOToVolume(13) generator maps directly to VolumeDepthCentibels (SF2 units: centibels).")]
        public void TryResolve_LfoVolumeDepthGenerator_MapsToVolumeDepthCentibels() {
            Sf2Zone zone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.ModulationLFOToVolume, 120));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { zone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.Lfo.VolumeDepthCentibels, Is.EqualTo(120f),
                "ModulationLFOToVolume=120 centibels must map directly to VolumeDepthCentibels.");
        }

        [Test]
        [Description("A positive ModulationLFOToVolume generator beyond the ±960-centibel stability cap is clamped to +960.")]
        public void TryResolve_LfoVolumeDepthBeyondPositiveCap_IsClamped() {
            Sf2Zone zone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.ModulationLFOToVolume, 2000));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { zone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.Lfo.VolumeDepthCentibels, Is.EqualTo(960f),
                "2000-centibel depth must clamp to the +960-centibel stability cap.");
        }

        [Test]
        [Description("A negative ModulationLFOToVolume generator beyond the ±960-centibel stability cap is clamped to -960.")]
        public void TryResolve_LfoVolumeDepthBeyondNegativeCap_IsClamped() {
            Sf2Zone zone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.ModulationLFOToVolume, unchecked((ushort)(short)-2000)));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { zone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.Lfo.VolumeDepthCentibels, Is.EqualTo(-960f),
                "-2000-centibel depth must clamp to the -960-centibel stability cap.");
        }

        [Test]
        [Description("ModulationLFOToFilterCutoffFrequency(10) generator maps directly to FilterDepthCents (SF2 units: cents).")]
        public void TryResolve_LfoFilterDepthGenerator_MapsToFilterDepthCents() {
            Sf2Zone zone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.ModulationLFOToFilterCutoffFrequency, unchecked((ushort)(short)3000)));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { zone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.Lfo.FilterDepthCents, Is.EqualTo(3000f),
                "ModulationLFOToFilterCutoffFrequency=3000 cents must map directly to FilterDepthCents.");
        }

        [Test]
        [Description("A positive ModulationLFOToFilterCutoffFrequency generator beyond the ±12000-cent stability cap is clamped to +12000.")]
        public void TryResolve_LfoFilterDepthBeyondPositiveCap_IsClamped() {
            Sf2Zone zone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.ModulationLFOToFilterCutoffFrequency, 20000));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { zone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.Lfo.FilterDepthCents, Is.EqualTo(12000f),
                "20000-cent depth must clamp to the +12000-cent stability cap.");
        }

        [Test]
        [Description("A negative ModulationLFOToFilterCutoffFrequency generator beyond the ±12000-cent stability cap is clamped to -12000.")]
        public void TryResolve_LfoFilterDepthBeyondNegativeCap_IsClamped() {
            Sf2Zone zone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.ModulationLFOToFilterCutoffFrequency, unchecked((ushort)(short)-20000)));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { zone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.Lfo.FilterDepthCents, Is.EqualTo(-12000f),
                "-20000-cent depth must clamp to the -12000-cent stability cap.");
        }

        [Test]
        [Description("Absent Pan(17) generator defaults to centre (0).")]
        public void TryResolve_NoPanGenerator_DefaultsToCentre() {
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { InstrumentZone(0, 127) });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.Pan, Is.EqualTo(0f),
                "Absent Pan(17) generator must default to centre (0).");
        }

        [Test]
        [Description("Pan(17)=-500 (SF2-spec full left) normalises to -1.")]
        public void TryResolve_PanMinus500_NormalisesToFullLeft() {
            Sf2Zone zone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.Pan, unchecked((ushort)(short)-500)));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { zone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.Pan, Is.EqualTo(-1f),
                "Pan raw=-500 (SF2-spec full left) must normalise to -1.");
        }

        [Test]
        [Description("Pan(17)=+250 (SF2-spec half right) normalises to +0.5.")]
        public void TryResolve_PanPlus250_NormalisesToHalfRight() {
            Sf2Zone zone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.Pan, 250));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { zone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.Pan, Is.EqualTo(0.5f).Within(1e-6f),
                "Pan raw=+250 must normalise to +0.5 (raw/500).");
        }

        [Test]
        [Description("A Pan(17) raw amount beyond the SF2-spec ±500 range is clamped before normalisation.")]
        public void TryResolve_PanBeyondSpecRange_IsClamped() {
            Sf2Zone zone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.Pan, unchecked((ushort)(short)-32768)));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { zone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.Pan, Is.EqualTo(-1f),
                "A raw amount far below -500 must clamp to -500 before normalising, yielding -1.");
        }

        [Test]
        [Description("Local zone Pan(17) generator overrides the global instrument zone's value.")]
        public void TryResolve_LocalPanOverridesGlobal() {
            Sf2Zone globalZone = Zone(Gen(Sf2GeneratorType.Pan, unchecked((ushort)(short)-500)));
            Sf2Zone localZone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.Pan, 500));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { globalZone, localZone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.Pan, Is.EqualTo(1f),
                "local Pan=+500 must override global Pan=-500.");
        }

        [Test]
        [Description("Absent ReverbEffectsSend(16) generator defaults to the SF2 spec's literal 0 (DiVoid #7170 " +
                     "§9.3 revised): with the additive/clamped combination, an absent gen-16 contributes no " +
                     "bias, so the channel's CC91 send alone still drives the voice.")]
        public void TryResolve_NoReverbSendGenerator_DefaultsToZero() {
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { InstrumentZone(0, 127) });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.ReverbSend, Is.EqualTo(0f),
                "Absent ReverbEffectsSend(16) generator must default to the SF2 spec's literal 0.");
        }

        [Test]
        [Description("ReverbEffectsSend(16)=500 (0.1%-units, half send) normalises to 0.5.")]
        public void TryResolve_ReverbSend500_NormalisesToHalf() {
            Sf2Zone zone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.ReverbEffectsSend, 500));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { zone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.ReverbSend, Is.EqualTo(0.5f).Within(1e-6f),
                "ReverbEffectsSend raw=500 must normalise to 0.5 (raw/1000).");
        }

        [Test]
        [Description("ReverbEffectsSend(16)=0 (explicit dry) normalises to 0.0.")]
        public void TryResolve_ReverbSendZero_NormalisesToZero() {
            Sf2Zone zone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.ReverbEffectsSend, 0));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { zone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.ReverbSend, Is.EqualTo(0f),
                "An explicit ReverbEffectsSend=0 must normalise to 0.0 (fully dry region), unlike the absent case.");
        }

        [Test]
        [Description("A ReverbEffectsSend(16) raw amount beyond the SF2 0..1000 range is clamped before normalisation.")]
        public void TryResolve_ReverbSendBeyondRange_IsClamped() {
            Sf2Zone zone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.ReverbEffectsSend, 2000));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { zone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.ReverbSend, Is.EqualTo(1f),
                "A raw amount above 1000 must clamp to 1000 before normalising, yielding 1.0.");
        }

        [Test]
        [Description("Local zone ReverbEffectsSend(16) generator overrides the global instrument zone's value.")]
        public void TryResolve_LocalReverbSendOverridesGlobal() {
            Sf2Zone globalZone = Zone(Gen(Sf2GeneratorType.ReverbEffectsSend, 1000));
            Sf2Zone localZone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.ReverbEffectsSend, 250));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { globalZone, localZone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.ReverbSend, Is.EqualTo(0.25f).Within(1e-6f),
                "local ReverbEffectsSend=250 must override global ReverbEffectsSend=1000.");
        }

        [Test]
        [Description("Absent ChorusEffectsSend(15) generator defaults to the SF2 spec's literal 0 (design #7190 " +
                     "§8), agreeing with the additive combination's neutral element: an absent gen-15 " +
                     "contributes no bias, so the channel's CC93 send alone still drives the voice.")]
        public void TryResolve_NoChorusSendGenerator_DefaultsToZero() {
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { InstrumentZone(0, 127) });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.ChorusSend, Is.EqualTo(0f),
                "Absent ChorusEffectsSend(15) generator must default to the SF2 spec's literal 0.");
        }

        [Test]
        [Description("ChorusEffectsSend(15)=500 (0.1%-units, half send) normalises to 0.5.")]
        public void TryResolve_ChorusSend500_NormalisesToHalf() {
            Sf2Zone zone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.ChorusEffectsSend, 500));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { zone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.ChorusSend, Is.EqualTo(0.5f).Within(1e-6f),
                "ChorusEffectsSend raw=500 must normalise to 0.5 (raw/1000).");
        }

        [Test]
        [Description("ChorusEffectsSend(15)=0 (explicit dry) normalises to 0.0.")]
        public void TryResolve_ChorusSendZero_NormalisesToZero() {
            Sf2Zone zone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.ChorusEffectsSend, 0));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { zone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.ChorusSend, Is.EqualTo(0f),
                "An explicit ChorusEffectsSend=0 must normalise to 0.0, same as the absent case.");
        }

        [Test]
        [Description("A ChorusEffectsSend(15) raw amount beyond the SF2 0..1000 range is clamped before normalisation.")]
        public void TryResolve_ChorusSendBeyondRange_IsClamped() {
            Sf2Zone zone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.ChorusEffectsSend, 2000));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { zone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.ChorusSend, Is.EqualTo(1f),
                "A raw amount above 1000 must clamp to 1000 before normalising, yielding 1.0.");
        }

        [Test]
        [Description("Local zone ChorusEffectsSend(15) generator overrides the global instrument zone's value.")]
        public void TryResolve_LocalChorusSendOverridesGlobal() {
            Sf2Zone globalZone = Zone(Gen(Sf2GeneratorType.ChorusEffectsSend, 1000));
            Sf2Zone localZone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.ChorusEffectsSend, 250));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { globalZone, localZone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.ChorusSend, Is.EqualTo(0.25f).Within(1e-6f),
                "local ChorusEffectsSend=250 must override global ChorusEffectsSend=1000.");
        }

        [Test]
        [Description("Absent ExclusiveClass(57) generator defaults to 0 (no choke group), DiVoid #7226/#7227.")]
        public void TryResolve_NoExclusiveClassGenerator_DefaultsToZero() {
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { InstrumentZone(0, 127) });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.ExclusiveClass, Is.EqualTo(0),
                "Absent ExclusiveClass(57) generator must default to 0.");
        }

        [Test]
        [Description("ExclusiveClass(57)=1 (the GM hi-hat/percussion choke group) reads through as the " +
                     "unsigned raw amount, unclamped (DiVoid #7226/#7227).")]
        public void TryResolve_ExclusiveClassOne_ReadsThroughUnclamped() {
            Sf2Zone zone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.ExclusiveClass, 1));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { zone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.ExclusiveClass, Is.EqualTo(1),
                "ExclusiveClass=1 must read through directly as the choke-group id.");
        }

        [Test]
        [Description("Local zone ExclusiveClass(57) generator overrides the global instrument zone's value.")]
        public void TryResolve_LocalExclusiveClassOverridesGlobal() {
            Sf2Zone globalZone = Zone(Gen(Sf2GeneratorType.ExclusiveClass, 3));
            Sf2Zone localZone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.ExclusiveClass, 5));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { globalZone, localZone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.ExclusiveClass, Is.EqualTo(5),
                "local ExclusiveClass=5 must override global ExclusiveClass=3.");
        }

        [Test]
        [Description("Absent InitialAttenuation(48) at both preset and instrument level leaves the region " +
                     "gain unchanged at 1.0 (DiVoid #7269: no spurious attenuation).")]
        public void TryResolve_NoInitialAttenuationGenerator_GainIsUnchangedAtOne() {
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { InstrumentZone(0, 127) });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.InitialAttenuationGain, Is.EqualTo(1f),
                "Absent InitialAttenuation(48) at both levels must leave region gain at 1.0.");
        }

        [Test]
        [Description("InitialAttenuation(48)=60 cB at the instrument level maps to a linear gain of ~0.759 " +
                     "under the EMU 0.4 scaling (10^(-0.4*60/200) = 10^(-60/500) ≈ 0.759; DiVoid #7273 — " +
                     "NOT the spec-literal 10^(-60/200)=0.5, which over-attenuates against the EMU8k/10k " +
                     "convention every soundfont, including OmegaGMGS2, is authored against).")]
        public void TryResolve_InstrumentAttenuation60Centibels_MapsToEmuScaledGain() {
            Sf2Zone zone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.InitialAttenuation, 60));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { zone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.InitialAttenuationGain, Is.EqualTo(0.759f).Within(0.005f),
                "60 cB of attenuation must map to 10^(-0.4*60/200) ≈ 0.759 under the EMU scaling.");
        }

        [Test]
        [Description("InitialAttenuation(48)=100 cB at the instrument level maps to a linear gain of ~0.631 " +
                     "under the EMU 0.4 scaling (10^(-0.4*100/200) = 10^(-100/500) ≈ 0.631; DiVoid #7273).")]
        public void TryResolve_InstrumentAttenuation100Centibels_MapsToExpectedGain() {
            Sf2Zone zone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.InitialAttenuation, 100));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { zone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.InitialAttenuationGain, Is.EqualTo(0.631f).Within(0.005f),
                "100 cB (10 dB specified) of attenuation must map to 10^(-0.4*100/200) ≈ 0.631 under the EMU scaling.");
        }

        [Test]
        [Description("Preset-zone InitialAttenuation(48) and instrument-zone InitialAttenuation(48) accumulate " +
                     "additively (SF2 §8.1.2), not by override: preset=40 cB + instrument=60 cB sums to 100 cB, " +
                     "then the EMU 0.4 scale is applied to the total (summing-then-scaling is exact: " +
                     "0.4*(40+60) = 0.4*40 + 0.4*60).")]
        public void TryResolve_PresetAndInstrumentAttenuation_AccumulateAdditively() {
            Sf2Zone[] presetZones = PresetZoneWithInstrumentAndAttenuation(0, attenuationCentibels: 40);
            Sf2Zone instrumentZone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.InitialAttenuation, 60));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(presetZones, new[] { instrumentZone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.InitialAttenuationGain, Is.EqualTo(0.631f).Within(0.005f),
                "preset(40 cB) + instrument(60 cB) = 100 cB must map to 10^(-0.4*100/200) ≈ 0.631, " +
                "proving additive (not override) accumulation across the two zone levels under the EMU scaling.");
        }

        [Test]
        [Description("A total InitialAttenuation beyond the SF2 max of 1440 cB clamps to silence (gain 0).")]
        public void TryResolve_TotalAttenuationBeyondMax_ClampsToSilence() {
            Sf2Zone[] presetZones = PresetZoneWithInstrumentAndAttenuation(0, attenuationCentibels: 1000);
            Sf2Zone instrumentZone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.InitialAttenuation, 1000));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(presetZones, new[] { instrumentZone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.InitialAttenuationGain, Is.EqualTo(0f),
                "preset(1000 cB) + instrument(1000 cB) = 2000 cB exceeds the 1440 cB max and must clamp to silence.");
        }

        [Test]
        [Description("A negative InitialAttenuation total (e.g. a malformed negative gen-48 amount) clamps up " +
                     "to 0 cB, yielding full gain rather than amplification beyond unity.")]
        public void TryResolve_NegativeAttenuationTotal_ClampsToFullGain() {
            Sf2Zone zone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.InitialAttenuation, unchecked((ushort)(short)-50)));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(PresetZoneWithInstrument(), new[] { zone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.InitialAttenuationGain, Is.EqualTo(1f),
                "A negative attenuation total must clamp to 0 cB (gain 1.0), never amplify beyond unity.");
        }

        [Test]
        [Description("Local instrument-zone InitialAttenuation(48) overrides the global instrument zone's " +
                     "value (local-over-global precedence, same as every other generator); resulting 60 cB " +
                     "maps through the EMU 0.4 scaling to ~0.759 (DiVoid #7273).")]
        public void TryResolve_LocalInstrumentAttenuationOverridesGlobal() {
            Sf2Zone globalZone = Zone(Gen(Sf2GeneratorType.InitialAttenuation, 200));
            Sf2Zone localZone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.InitialAttenuation, 60));
            (Sf2RegionResolver resolver, Sf2SampleData _) = BuildResolver(
                PresetZoneWithInstrument(),
                new[] { globalZone, localZone });

            bool found = resolver.TryResolve(60, 100, out SampleRegion? region, out _);

            Assert.That(found, Is.True);
            Assert.That(region!.InitialAttenuationGain, Is.EqualTo(0.759f).Within(0.005f),
                "local InitialAttenuation=60 cB must override the global instrument zone's 200 cB, and map " +
                "through the EMU 0.4 scaling to 10^(-0.4*60/200) ≈ 0.759.");
        }
    }
}
