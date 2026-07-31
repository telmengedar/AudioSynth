using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// <see cref="SynthesizerOptions.MasterGain"/> / <see cref="Synthesizer.SetMasterGain"/> tests: unity
    /// reproduces the pre-existing render exactly, a non-unity gain scales the pre-soft-clip master bus,
    /// a live <see cref="Synthesizer.SetMasterGain"/> call steps rather than glides, and invalid values
    /// are rejected at both construction and runtime.
    /// </summary>
    [TestFixture]
    public class SynthesizerMasterGainTests {

        const int SampleRate = 44100;
        const int SettleFrames = 500;
        const int MeasureFrames = 500;

        static SynthesizerOptions Options(float masterGain = SynthesizerOptions.DefaultMasterGain) =>
            new SynthesizerOptions(SampleRate, 1, 64, 16, masterGain: masterGain);

        static SampleRegion BuildDcRegion(float value, int length) {
            float[] buf = new float[length];
            for (int i = 0; i < length; i++)
                buf[i] = value;
            return new SampleRegion(buf, 0, length, 0, length, LoopMode.Continuous, SampleRate, 60, 0,
                EnvelopeParameters.Default, FilterParameters.Default, LfoParameters.Default, 0f);
        }

        static float[] RenderSettled(SynthesizerOptions opts) {
            SampleRegion region = BuildDcRegion(0.3f, 1024);
            SamplePatch patch = new SamplePatch(region, opts.SampleRate);
            Synthesizer synth = new Synthesizer(opts, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, SettleFrames + MeasureFrames);
            return sink.ToArray();
        }

        [Test]
        [Description("An explicit unity MasterGain renders sample-for-sample identical to a Synthesizer built " +
                     "from options that never mention MasterGain at all (both resolve to the same 1.0 default), " +
                     "proving unity gain reproduces the pre-existing output exactly.")]
        public void MasterGain_Unity_BitIdenticalToDefaultOptions() {
            float[] withoutExplicitGain = RenderSettled(new SynthesizerOptions(SampleRate, 1, 64, 16));
            float[] withExplicitUnityGain = RenderSettled(Options(1.0f));

            Assert.That(withExplicitUnityGain, Is.EqualTo(withoutExplicitGain),
                "an explicit MasterGain of 1.0 must be bit-identical to the implicit default.");
        }

        [Test]
        [Description("A quiet DC voice well below the soft-clip knee, scaled by MasterGain 0.5, is exactly half " +
                     "its unity-gain peak (a clean x0.5 read at the pre-soft-clip point).")]
        public void MasterGain_Half_ScalesPreClipSignalByHalf() {
            float[] unityGain = RenderSettled(Options(1.0f));
            float[] halfGain = RenderSettled(Options(0.5f));

            float unityPeak = PeakFrom(unityGain, SettleFrames);
            float halfPeak = PeakFrom(halfGain, SettleFrames);

            Assert.That(unityPeak, Is.EqualTo(0.3f).Within(1e-4f), $"unity-gain peak should be the raw DC value; was {unityPeak}.");
            Assert.That(halfPeak, Is.EqualTo(0.15f).Within(1e-4f), $"half-gain peak should be exactly half the raw DC value; was {halfPeak}.");
        }

        [Test]
        [Description("SetMasterGain issued mid-stream applies to the next rendered block immediately (a step), " +
                     "not a gradual glide like the per-channel GainRamp.")]
        public void SetMasterGain_MidStream_StepsImmediately() {
            SynthesizerOptions opts = Options(1.0f);
            SampleRegion region = BuildDcRegion(0.3f, 1024);
            SamplePatch patch = new SamplePatch(region, opts.SampleRate);
            Synthesizer synth = new Synthesizer(opts, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, SettleFrames);
            synth.SetMasterGain(0.5f);
            OfflineRenderer.Render(synth, sink, MeasureFrames);

            float[] samples = sink.ToArray();
            for (int i = SettleFrames; i < samples.Length; i++) {
                Assert.That(samples[i], Is.EqualTo(0.15f).Within(1e-4f),
                    $"sample {i} should already be at the new gain level immediately after SetMasterGain " +
                    $"(no partial/glided value); was {samples[i]}.");
            }
        }

        [Test]
        [Description("SetMasterGain rejects negative and NaN values.")]
        public void SetMasterGain_InvalidValue_Throws() {
            Synthesizer synth = new Synthesizer(Options(), new SamplePatch(BuildDcRegion(0f, 16), SampleRate));

            Assert.Throws<ArgumentOutOfRangeException>(() => synth.SetMasterGain(-0.1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => synth.SetMasterGain(float.NaN));
        }

        [Test]
        [Description("SynthesizerOptions rejects a negative or NaN MasterGain at construction, mirroring its " +
                     "other validated fields.")]
        public void SynthesizerOptions_InvalidMasterGain_Throws() {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SynthesizerOptions(masterGain: -1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SynthesizerOptions(masterGain: float.NaN));
        }

        static float PeakFrom(float[] samples, int startIndex) {
            float peak = 0f;
            for (int i = startIndex; i < samples.Length; i++)
                peak = Math.Max(peak, Math.Abs(samples[i]));
            return peak;
        }
    }
}
