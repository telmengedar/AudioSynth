using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Regression tests for the master-bus soft-clip stage (DiVoid #7126): loud sums round off instead
    /// of hard-clamping; a quiet voice below the knee is unaffected.
    /// </summary>
    public class MasterBusSoftClipTests {

        const int SampleRate = 44100;
        const int SettleFrames = 500;
        const int MeasureFrames = 500;

        static SynthesizerOptions Options(int channels) => new SynthesizerOptions(SampleRate, channels, 64, 16);

        static SampleRegion BuildDcRegion(float value, int length) {
            float[] buf = new float[length];
            for (int i = 0; i < length; i++)
                buf[i] = value;
            return new SampleRegion(buf, 0, length, 0, length, LoopMode.Continuous, SampleRate, 60, 0,
                EnvelopeParameters.Default, FilterParameters.Default, LfoParameters.Default, 0f);
        }

        [Test]
        [Description("Two simultaneous full-velocity voices, which previously summed past 1.0 and hard-clamped, " +
                     "now round off under the ceiling: no sample reaches |s| >= 1.0, and the master bus is " +
                     "measurably above the knee (the soft clip engaged, not just unity pass-through).")]
        public void SeveralLoudSimultaneousVoices_NoLongerClip() {
            const int LoudVoiceCount = 2;
            SynthesizerOptions opts = Options(2);
            SampleRegion region = BuildDcRegion(1f, 1024);
            SamplePatch patch = new SamplePatch(region, opts.SampleRate);
            Synthesizer synth = new Synthesizer(opts, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            for (int channel = 0; channel < LoudVoiceCount; channel++)
                synth.NoteOn(channel, 60, 127);
            OfflineRenderer.Render(synth, sink, SettleFrames + MeasureFrames);

            float[] samples = sink.ToArray();
            int measureStartSample = SettleFrames * opts.Channels;

            float peak = 0f;
            int clipped = 0;
            for (int i = measureStartSample; i < samples.Length; i++) {
                float magnitude = Math.Abs(samples[i]);
                peak = Math.Max(peak, magnitude);
                if (magnitude >= 1f)
                    clipped++;
            }

            Assert.That(clipped, Is.Zero,
                $"expected no sample pinned at the ceiling (two full-velocity stereo voices sum to ~1.41 " +
                $"pre-clip; the old hard clamp would have pinned every one of these samples to exactly 1.0 " +
                $"for the whole duration); found {clipped}.");
            Assert.That(peak, Is.GreaterThan(0.9f), $"expected the soft-clip knee to have engaged; peak was {peak}.");
            Assert.That(peak, Is.LessThanOrEqualTo(1f), $"sample exceeded [-1,1]: {peak}.");
        }

        [Test]
        [Description("A single quiet voice well below the knee is unaffected by the master bus stage " +
                     "(low-level dynamics pass through unchanged).")]
        public void QuietSingleVoice_UnaffectedByMasterBus() {
            SynthesizerOptions opts = Options(1);
            SampleRegion region = BuildDcRegion(0.3f, 1024);
            SamplePatch patch = new SamplePatch(region, opts.SampleRate);
            Synthesizer synth = new Synthesizer(opts, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, SettleFrames + MeasureFrames);
            float[] samples = sink.ToArray();

            float peak = 0f;
            for (int i = SettleFrames; i < samples.Length; i++)
                peak = Math.Max(peak, Math.Abs(samples[i]));

            Assert.That(peak, Is.EqualTo(0.3f).Within(1e-4f),
                $"a quiet DC voice below the knee should pass through the master bus unchanged; peak was {peak}.");
        }
    }
}
