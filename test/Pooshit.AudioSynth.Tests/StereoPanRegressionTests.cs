using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Regression guards for the stereo-pan render-loop change (DiVoid #7127, design §10.2/§14.8):
    /// a fully-centred stereo render must reproduce the pre-pan centre mix within float tolerance,
    /// and a mono (non-stereo) render must be unaffected by channel/region pan values, since the
    /// non-stereo branch is kept verbatim.
    /// </summary>
    [TestFixture]
    public class StereoPanRegressionTests {

        const int SampleRate = 44100;
        const int SettleFrames = 500;

        /// <summary>
        /// Mirrors <c>Synthesizer.MasterHeadroomTrim</c> (DiVoid BUG #7212, design #7213): the render goes
        /// through the master bus, so the pre-pan centre-gain expectation is scaled by this factor.
        /// </summary>
        const float MasterHeadroomTrim = 0.5f;

        static SampleRegion BuildDcRegion(float value, int length, float pan) {
            float[] buf = new float[length];
            for (int i = 0; i < length; i++)
                buf[i] = value;
            return new SampleRegion(buf, 0, length, 0, length, LoopMode.Continuous, SampleRate, 60, 0,
                EnvelopeParameters.Default, FilterParameters.Default, LfoParameters.Default, pan);
        }

        [Test]
        [Description("Centre regression (design §10.2): a fully-centred stereo render (no channel pan, no region " +
                     "pan) places the old panGain (1/sqrt(2)) in both L and R, within float tolerance — cos(pi/4) " +
                     "and sin(pi/4) equal the pre-pan stereo centre gain.")]
        public void CentrePan_StereoRender_ReproducesOldPanGainWithinTolerance() {
            SynthesizerOptions opts = new SynthesizerOptions(SampleRate, 2, 64, 16);
            SampleRegion region = BuildDcRegion(0.2f, 1024, pan: 0f);
            SamplePatch patch = new SamplePatch(region, opts.SampleRate);
            Synthesizer synth = new Synthesizer(opts, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, SettleFrames);

            float[] samples = sink.ToArray();
            int lastFrameBase = (SettleFrames - 1) * 2;
            float left = samples[lastFrameBase];
            float right = samples[lastFrameBase + 1];

            float oldPanGain = (float)(1.0 / Math.Sqrt(2));
            float expected = 0.2f * oldPanGain * MasterHeadroomTrim;

            Assert.That(left, Is.EqualTo(expected).Within(1e-4f),
                $"centred L must reproduce the old panGain-scaled mix; measured L={left}, expected={expected}.");
            Assert.That(right, Is.EqualTo(expected).Within(1e-4f),
                $"centred R must reproduce the old panGain-scaled mix; measured R={right}, expected={expected}.");
        }

        [Test]
        [Description("Mono regression (design §11 R5): with channels==1 the non-stereo mono-to-all path is kept " +
                     "verbatim, so the render is bit-identical regardless of channel or region pan.")]
        public void MonoOutput_IsBitIdentical_RegardlessOfPanValues() {
            SynthesizerOptions opts = new SynthesizerOptions(SampleRate, 1, 64, 16);

            float[] RenderWithPan(float channelPan, float regionPan) {
                SampleRegion region = BuildDcRegion(0.2f, 1024, regionPan);
                SamplePatch patch = new SamplePatch(region, opts.SampleRate);
                Synthesizer synth = new Synthesizer(opts, patch);
                InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);
                synth.SetChannelPan(0, channelPan);
                synth.NoteOn(0, 60, 127);
                OfflineRenderer.Render(synth, sink, SettleFrames);
                return sink.ToArray();
            }

            float[] centreSamples = RenderWithPan(0f, 0f);
            float[] hardPanSamples = RenderWithPan(1f, 1f);

            Assert.That(hardPanSamples, Is.EqualTo(centreSamples),
                "mono output must be bit-identical regardless of channel/region pan; the non-stereo path must not read pan.");
        }
    }
}
