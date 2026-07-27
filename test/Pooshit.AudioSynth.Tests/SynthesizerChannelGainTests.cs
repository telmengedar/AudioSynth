using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;
using Pooshit.AudioSynth.Tests.Helpers;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// <see cref="Synthesizer.SetChannelGain"/> tests (DiVoid #7126): scales channel contribution,
    /// glides mid-note (INV-1), and guards its channel range like <see cref="Synthesizer.SetChannelPatch"/>.
    /// </summary>
    [TestFixture]
    public class SynthesizerChannelGainTests {

        const int SampleRate = 44100;
        const int SettleFrames = 500;

        static SynthesizerOptions Options() => new SynthesizerOptions(SampleRate, 1, 64, 16);

        static SampleRegion BuildDcRegion(float value, int length) {
            float[] buf = new float[length];
            for (int i = 0; i < length; i++)
                buf[i] = value;
            return new SampleRegion(buf, 0, length, 0, length, LoopMode.Continuous, SampleRate, 60, 0,
                EnvelopeParameters.Default, FilterParameters.Default, LfoParameters.Default);
        }

        static float RenderSettledPeak(float channelGain) {
            SynthesizerOptions opts = Options();
            SampleRegion region = BuildDcRegion(0.2f, 1024);
            SamplePatch patch = new SamplePatch(region, opts.SampleRate);
            Synthesizer synth = new Synthesizer(opts, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.SetChannelGain(0, channelGain);
            OfflineRenderer.Render(synth, sink, SettleFrames); // let the channel-gain ramp settle before the note.
            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, SettleFrames); // let the note's own velocity ramp settle too.

            float[] samples = sink.ToArray();
            float peak = 0f;
            for (int i = SettleFrames; i < samples.Length; i++)
                peak = Math.Max(peak, Math.Abs(samples[i]));
            return peak;
        }

        [Test]
        [Description("Two otherwise-identical renders with channel gains 1.0 and 0.5 contribute in a 2:1 " +
                     "ratio once both the channel-gain and velocity ramps have settled.")]
        public void SetChannelGain_ScalesChannelContributionProportionally() {
            float fullGainPeak = RenderSettledPeak(1.0f);
            float halfGainPeak = RenderSettledPeak(0.5f);

            Assert.That(fullGainPeak, Is.GreaterThan(0.05f), $"full-gain render was unexpectedly quiet; peak={fullGainPeak}.");
            Assert.That(halfGainPeak / fullGainPeak, Is.EqualTo(0.5f).Within(0.02f),
                $"expected channel-gain 0.5 to halve the contribution; full={fullGainPeak}, half={halfGainPeak}.");
        }

        [Test]
        [Description("A channel-gain change issued mid-note glides across frames rather than stepping (INV-1).")]
        public void SetChannelGain_MidNoteChange_GlidesNotSteps() {
            SynthesizerOptions opts = Options();
            SampleRegion region = BuildDcRegion(0.3f, 1024);
            SamplePatch patch = new SamplePatch(region, opts.SampleRate);
            Synthesizer synth = new Synthesizer(opts, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, SettleFrames); // let the note settle at unity channel gain.
            synth.SetChannelGain(0, 0.2f);
            OfflineRenderer.Render(synth, sink, 300);

            float[] samples = sink.ToArray();
            const float epsilon = 0.01f;
            float maxDelta = 0f;
            for (int i = SettleFrames; i < samples.Length - 1; i++)
                maxDelta = Math.Max(maxDelta, Math.Abs(samples[i + 1] - samples[i]));

            Assert.That(maxDelta, Is.LessThan(epsilon),
                $"a mid-note channel-gain change produced a delta of {maxDelta}; expected a glide under {epsilon} " +
                "(a step would produce a delta near 0.3 * (1.0 - 0.2) = 0.24).");

            bool driftedFromPreChangeValue = false;
            for (int i = SettleFrames; i < samples.Length; i++) {
                if (Math.Abs(samples[i] - 0.3f) > 1e-4f) {
                    driftedFromPreChangeValue = true;
                    break;
                }
            }
            Assert.That(driftedFromPreChangeValue, Is.True, "expected the channel gain to eventually move toward its new target.");
        }

        [TestCase(-1)]
        [TestCase(16)]
        [Description("SetChannelGain rejects a channel outside [0,15].")]
        public void SetChannelGain_ChannelOutOfRange_Throws(int channel) {
            Synthesizer synth = new Synthesizer(Options(), new RecordingPatch());

            Assert.Throws<ArgumentOutOfRangeException>(() => synth.SetChannelGain(channel, 1f));
        }
    }
}
