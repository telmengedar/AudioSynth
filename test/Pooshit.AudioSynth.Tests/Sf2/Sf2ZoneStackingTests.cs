using System;
using System.Collections.Generic;
using NUnit.Framework;
using Pooshit.AudioSynth.Formats.Sf2;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// <see cref="Sf2Patch"/>-level tests for SF2 zone/layer stacking (DiVoid #7282): <see cref="Sf2Patch.StartVoices"/>
    /// must materialise every layer <see cref="Sf2RegionResolver.ResolveAll"/> resolves into a live voice,
    /// respecting the <c>MaxLayersPerNote</c> cap (locked at 4, DiVoid #7283 decision 1), while
    /// <see cref="Sf2Patch.StartVoice"/> keeps returning exactly the single first-match voice for callers
    /// that only want one (backward compatibility with every pre-stacking call site). All tests construct
    /// SF2 model objects directly without going through the binary loader, mirroring
    /// <see cref="Sf2RegionResolverTests"/>'s style.
    /// </summary>
    [TestFixture]
    public class Sf2ZoneStackingTests {

        static Sf2Generator Gen(Sf2GeneratorType type, ushort amount) =>
            new Sf2Generator(type, amount);

        static Sf2Zone Zone(params Sf2Generator[] gens) =>
            new Sf2Zone(gens, Array.Empty<Sf2Modulator>());

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

        static Sf2Zone[] PresetZoneWithInstrument(int instrIndex = 0) =>
            new[] { Zone(Gen(Sf2GeneratorType.Instrument, (ushort)instrIndex)) };

        static Sf2SampleHeader Header(uint end) =>
            new Sf2SampleHeader("S", 0, end, 0, end, 44100, 60, 0, 0, Sf2SampleLink.MonoSample);

        static short[] BuildFullScalePool(int frames) {
            short[] pool = new short[frames];
            for (int i = 0; i < frames; i++)
                pool[i] = 32767;
            return pool;
        }

        static Sf2Patch BuildPatch(Sf2Zone[] presetZones, Sf2Zone[] instrumentZones) {
            short[] pool = BuildFullScalePool(8);
            Sf2SampleData data = new Sf2SampleData(pool);
            Sf2SampleHeader hdr = Header((uint)pool.Length);
            Sf2Instrument instrument = new Sf2Instrument("Inst", instrumentZones);
            Sf2PresetHeader preset = new Sf2PresetHeader("P", 0, 0, presetZones);
            return new Sf2Patch(preset, new[] { instrument }, new[] { hdr }, data);
        }

        [Test]
        [Description("Two overlapping instrument zones (a tonal zone plus a near-0-cB companion, the " +
                     "Rock-Organ-style authoring pattern per DiVoid #7281) must start 2 voices from one " +
                     "StartVoices call, not just the first match.")]
        public void StartVoices_TwoOverlappingZones_StartsTwoVoices() {
            Sf2Zone tonalZone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.InitialAttenuation, 177));
            Sf2Zone companionZone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.InitialAttenuation, 0));
            Sf2Patch patch = BuildPatch(PresetZoneWithInstrument(), new[] { tonalZone, companionZone });

            List<IVoice> voices = new List<IVoice>();
            patch.StartVoices(60, 100, voices);

            Assert.That(voices, Has.Count.EqualTo(2), "both overlapping zones must each start a voice.");
            Assert.That(voices[0].IsActive, Is.True);
            Assert.That(voices[1].IsActive, Is.True);
        }

        [Test]
        [Description("A single covering zone must still start exactly 1 voice via StartVoices (no " +
                     "regression for the overwhelmingly common non-stacking case).")]
        public void StartVoices_SingleCoveringZone_StartsOneVoice() {
            Sf2Patch patch = BuildPatch(PresetZoneWithInstrument(), new[] { InstrumentZone(0, 127) });

            List<IVoice> voices = new List<IVoice>();
            patch.StartVoices(60, 100, voices);

            Assert.That(voices, Has.Count.EqualTo(1));
        }

        [Test]
        [Description("No covering zone must append zero voices from StartVoices (mirrors StartVoice's " +
                     "InactiveVoice no-match case, but without occupying an engine slot for it).")]
        public void StartVoices_NoCoveringZone_AppendsNothing() {
            Sf2Patch patch = BuildPatch(PresetZoneWithInstrument(), new[] { InstrumentZone(80, 90) });

            List<IVoice> voices = new List<IVoice>();
            patch.StartVoices(60, 100, voices);

            Assert.That(voices, Is.Empty);
        }

        [Test]
        [Description("MaxLayersPerNote (locked at 4, DiVoid #7283 decision 1) caps the number of voices " +
                     "started even when more zones cover the note: 6 overlapping instrument zones must " +
                     "start only 4 voices.")]
        public void StartVoices_MoreThanCapCoveringZones_CapsAtMaxLayersPerNote() {
            const int overlappingZoneCount = 6;
            const int expectedCap = 4;

            Sf2Zone[] zones = new Sf2Zone[overlappingZoneCount];
            for (int i = 0; i < overlappingZoneCount; i++)
                zones[i] = InstrumentZone(0, 127, Gen(Sf2GeneratorType.InitialAttenuation, (ushort)(i * 10)));

            Sf2Patch patch = BuildPatch(PresetZoneWithInstrument(), zones);

            List<IVoice> voices = new List<IVoice>();
            patch.StartVoices(60, 100, voices);

            Assert.That(voices, Has.Count.EqualTo(expectedCap),
                $"{overlappingZoneCount} covering zones must be capped at MaxLayersPerNote={expectedCap}.");
        }

        [Test]
        [Description("StartVoice (the pre-stacking single-voice API) must keep returning exactly one active " +
                     "voice for a preset with overlapping zones, unaffected by ResolveAll's existence -- " +
                     "backward compatibility for every caller that only wants one voice.")]
        public void StartVoice_StillSingleMatch_WithOverlappingZonesPresent() {
            Sf2Zone tonalZone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.InitialAttenuation, 177));
            Sf2Zone companionZone = InstrumentZone(0, 127, Gen(Sf2GeneratorType.InitialAttenuation, 0));
            Sf2Patch patch = BuildPatch(PresetZoneWithInstrument(), new[] { tonalZone, companionZone });

            IVoice voice = patch.StartVoice(60, 100);

            Assert.That(voice.IsActive, Is.True);
        }
    }
}
