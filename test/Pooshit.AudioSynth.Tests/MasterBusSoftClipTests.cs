using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Regression tests for the master-bus stage (DiVoid #7126, #7212): loud sums round off instead
    /// of hard-clamping; a quiet voice below the knee is uniformly attenuated by the headroom trim.
    /// </summary>
    public class MasterBusSoftClipTests {

        const int SampleRate = 44100;
        const int SettleFrames = 500;
        const int MeasureFrames = 500;

        /// <summary>Mirrors <c>Synthesizer.MasterHeadroomTrim</c> (DiVoid BUG #7212, design #7213).</summary>
        const float MasterHeadroomTrim = 0.5f;

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
                     "still never reach |s| >= 1.0. With the headroom trim (DiVoid BUG #7212) folded in, their " +
                     "~1.41 pre-trim sum is now trimmed to ~0.71 — at or below the knee — demonstrating the " +
                     "soft-clip no longer engages continuously on this normal-level material.")]
        public void SeveralLoudSimultaneousVoices_NoLongerClip() {
            const int LoudVoiceCount = 2;
            const float KneeThreshold = 0.9f;
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
            Assert.That(peak, Is.LessThanOrEqualTo(KneeThreshold),
                $"expected the headroom trim to keep this normal-level sum at or below the soft-clip knee " +
                $"({KneeThreshold}), demonstrating the limiter no longer engages continuously (DiVoid BUG #7212); " +
                $"peak was {peak}.");
            Assert.That(peak, Is.LessThanOrEqualTo(1f), $"sample exceeded [-1,1]: {peak}.");
        }

        [Test]
        [Description("A single quiet voice below the knee is uniformly attenuated by the master headroom trim " +
                     "(DiVoid BUG #7212): it is no longer passed through at unity — every finite sample, above " +
                     "or below the knee, is scaled by MasterHeadroomTrim before the soft-clip stage.")]
        public void QuietSingleVoice_AttenuatedByHeadroomTrim() {
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

            Assert.That(peak, Is.EqualTo(0.3f * MasterHeadroomTrim).Within(1e-4f),
                $"a quiet DC voice below the knee should now read back trim x its level (headroom trim folded " +
                $"into the master bus stage, DiVoid BUG #7212); peak was {peak}.");
        }
    }
}
