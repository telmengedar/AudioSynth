using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;
using Pooshit.AudioSynth.Tests.Helpers;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// <see cref="Synthesizer.SetChannelPan"/> and per-voice <see cref="SampleRegion.Pan"/> tests
    /// (DiVoid #7127): equal-power L/R placement at the rails and centre, the combined equal-power
    /// ratio at a mid position, channel+region pan composition, and the channel-range guard mirroring
    /// <see cref="SynthesizerChannelGainTests"/>.
    /// </summary>
    [TestFixture]
    public class SynthesizerChannelPanTests {

        const int SampleRate = 44100;
        const int SettleFrames = 500;

        static SynthesizerOptions StereoOptions() => new SynthesizerOptions(SampleRate, 2, 64, 16);

        static SampleRegion BuildDcRegion(float value, int length, float pan) {
            float[] buf = new float[length];
            for (int i = 0; i < length; i++)
                buf[i] = value;
            return new SampleRegion(buf, 0, length, 0, length, LoopMode.Continuous, SampleRate, 60, 0,
                EnvelopeParameters.Default, FilterParameters.Default, LfoParameters.Default, pan);
        }

        static (float Left, float Right) RenderSettledLR(float channelPan, float regionPan = 0f) {
            SynthesizerOptions opts = StereoOptions();
            SampleRegion region = BuildDcRegion(0.2f, 1024, regionPan);
            SamplePatch patch = new SamplePatch(region, opts.SampleRate);
            Synthesizer synth = new Synthesizer(opts, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.SetChannelPan(0, channelPan);
            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, SettleFrames);

            float[] samples = sink.ToArray();
            int lastFrameBase = (SettleFrames - 1) * 2;
            return (samples[lastFrameBase], samples[lastFrameBase + 1]);
        }

        [Test]
        [Description("A channel panned hard-left (-1) places the full signal in L and none in R.")]
        public void SetChannelPan_HardLeft_PlacesAllInLeftChannel() {
            (float left, float right) = RenderSettledLR(-1f);

            Assert.That(Math.Abs(right), Is.LessThan(0.001f), $"expected R≈0 at hard-left pan; measured R={right}.");
            Assert.That(left, Is.GreaterThan(0.1f), $"expected L to carry the full signal at hard-left pan; measured L={left}.");
        }

        [Test]
        [Description("A channel panned hard-right (+1) places the full signal in R and none in L.")]
        public void SetChannelPan_HardRight_PlacesAllInRightChannel() {
            (float left, float right) = RenderSettledLR(1f);

            Assert.That(Math.Abs(left), Is.LessThan(0.001f), $"expected L≈0 at hard-right pan; measured L={left}.");
            Assert.That(right, Is.GreaterThan(0.1f), $"expected R to carry the full signal at hard-right pan; measured R={right}.");
        }

        [Test]
        [Description("A centred channel pan (0) places equal signal in L and R.")]
        public void SetChannelPan_Centre_PlacesEqualSignalInBothChannels() {
            (float left, float right) = RenderSettledLR(0f);

            Assert.That(left, Is.EqualTo(right).Within(1e-5f), $"expected L==R at centre pan; measured L={left}, R={right}.");
        }

        [Test]
        [Description("A mid-position pan produces the equal-power ratio left/right = cot(theta) the design's pan law predicts.")]
        public void SetChannelPan_MidPosition_ProducesEqualPowerRatio() {
            const float pan = 0.5f;
            (float left, float right) = RenderSettledLR(pan);

            double theta = (pan + 1.0) * (Math.PI / 4.0);
            float expectedRatio = (float)(Math.Cos(theta) / Math.Sin(theta));
            float measuredRatio = left / right;

            Assert.That(measuredRatio, Is.EqualTo(expectedRatio).Within(0.001f),
                $"expected left/right = cot(theta) = {expectedRatio}; measured {measuredRatio}.");
        }

        [Test]
        [Description("Channel pan and per-voice region pan sum before clamping, placing the voice at the combined (clamped) position.")]
        public void SetChannelPan_CombinesWithRegionPan_ClampsAtRail() {
            (float left, float right) = RenderSettledLR(channelPan: 0.7f, regionPan: 0.7f);

            Assert.That(Math.Abs(left), Is.LessThan(0.001f),
                $"channel pan 0.7 + region pan 0.7 must clamp to +1 (hard right); measured L={left}.");
            Assert.That(right, Is.GreaterThan(0.1f),
                $"channel pan 0.7 + region pan 0.7 must clamp to +1 (hard right); measured R={right}.");
        }

        [TestCase(-1)]
        [TestCase(16)]
        [Description("SetChannelPan rejects a channel outside [0,15].")]
        public void SetChannelPan_ChannelOutOfRange_Throws(int channel) {
            Synthesizer synth = new Synthesizer(StereoOptions(), new RecordingPatch());

            Assert.Throws<ArgumentOutOfRangeException>(() => synth.SetChannelPan(channel, 0f));
        }
    }
}
