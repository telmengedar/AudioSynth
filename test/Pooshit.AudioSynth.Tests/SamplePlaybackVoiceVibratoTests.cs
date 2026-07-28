using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Full-path tests that the per-voice mod-LFO modulates pitch inside
    /// <see cref="Pooshit.AudioSynth.Synthesis.Voices.SamplePlaybackVoice"/> (vibrato): the deliverable
    /// tracking proof, the inert-LFO no-regression guarantee, and the #6272 §B clicks/zipper regression.
    /// </summary>
    [TestFixture]
    public class SamplePlaybackVoiceVibratoTests {

        const int SampleRate = 44100;

        /// <summary>
        /// The voice's control-rate tick period; pinned per the mod-LFO design's open question 2
        /// (docs/architecture/mod-lfo.md).
        /// </summary>
        const int ControlRateFrames = 64;

        /// <summary>
        /// Mirrors <c>Synthesizer.MasterHeadroomTrim</c> (DiVoid BUG #7212, design #7213): <see cref="Render"/>
        /// goes through the full <see cref="Synthesizer"/> path, so measured increments are divided by this
        /// factor to compensate for the master-bus headroom attenuation before comparing against the
        /// untrimmed ratio.
        /// </summary>
        const float MasterHeadroomTrim = 0.5f;

        static readonly EnvelopeParameters InstantSustainEnvelope = new EnvelopeParameters(0f, 0f, 0f, 0f, 1f, 0f);

        static SampleRegion BuildRampRegion(LfoParameters lfo, float scale, int length) {
            float[] buf = new float[length];
            for (int i = 0; i < length; i++)
                buf[i] = i * scale;
            return new SampleRegion(buf, 0, length, 0, length, LoopMode.NoLoop, SampleRate, 60, 0,
                InstantSustainEnvelope, FilterParameters.Default, lfo, 0f);
        }

        static SampleRegion BuildToneRegion(float frequency, LfoParameters lfo, int bufferLength) {
            float[] buffer = new float[bufferLength];
            for (int i = 0; i < bufferLength; i++)
                buffer[i] = (float)Math.Sin(2.0 * Math.PI * frequency * i / SampleRate);
            return new SampleRegion(buffer, 0, buffer.Length, 0, buffer.Length, LoopMode.Continuous,
                SampleRate, 60, 0, EnvelopeParameters.Default, FilterParameters.Default, lfo, 0f);
        }

        static float[] Render(SampleRegion region, int frames) {
            SynthesizerOptions opts = new SynthesizerOptions(SampleRate, 1, 64, 16);
            SamplePatch patch = new SamplePatch(region, opts.SampleRate);
            Synthesizer synth = new Synthesizer(opts, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);
            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, frames);
            return sink.ToArray();
        }

        [Test]
        [Description("Deliverable proof: the effective pitch increment at each control tick matches an independently-advanced ModulationLfo at the configured depth and rate.")]
        public void EffectiveIncrement_TracksLfoValue_AtControlTicks() {
            const float scale = 0.001f;
            const float depthCents = 200f;
            const int ticksToRender = 12;
            const int firstCheckedTick = 5;

            LfoParameters lfoParameters = new LfoParameters(0f, 5f, depthCents, 0f, 0f);
            SampleRegion region = BuildRampRegion(lfoParameters, scale, 1200);
            float[] output = Render(region, ControlRateFrames * ticksToRender);

            ModulationLfo predictor = new ModulationLfo(lfoParameters, SampleRate);

            for (int tick = 0; tick < ticksToRender; tick++) {
                float predictedLfoValue = predictor.Advance(ControlRateFrames);
                if (tick < firstCheckedTick)
                    continue;

                float expectedIncrement = (float)Math.Pow(2.0, predictedLfoValue * depthCents / 1200.0);
                int frame = tick * ControlRateFrames + ControlRateFrames / 2;
                float measuredIncrement = (output[frame + 1] - output[frame]) / scale / MasterHeadroomTrim;

                Assert.That(measuredIncrement, Is.EqualTo(expectedIncrement).Within(0.01f),
                    $"tick {tick}: measured increment {measuredIncrement} did not track the LFO-predicted increment {expectedIncrement}.");
            }
        }

        [Test]
        [Description("No-regression: LfoParameters.Default reproduces the pre-LFO constant-increment read advance bit-for-bit (mirrors the filter's open-bypass guarantee).")]
        public void DefaultLfo_ReadPositionAdvancesByExactBasePitchIncrement() {
            const float scale = 0.001f;
            const int framesToRender = 800;
            const int convergedFrame = 300;

            SampleRegion region = BuildRampRegion(LfoParameters.Default, scale, 1200);
            float[] output = Render(region, framesToRender);

            for (int i = convergedFrame; i < framesToRender - 1; i++) {
                float measuredIncrement = (output[i + 1] - output[i]) / scale / MasterHeadroomTrim;
                Assert.That(measuredIncrement, Is.EqualTo(1f).Within(1e-4f),
                    $"frame {i}: measured increment {measuredIncrement} deviates from the base pitch increment.");
            }
        }

        [Test]
        [Description("Regression for defect catalog #6272 §B (clicks/zipper class): a control-rate tick introduces no amplitude discontinuity, even under strong vibrato.")]
        public void ControlTick_IntroducesNoAmplitudeDiscontinuity() {
            LfoParameters lfo = new LfoParameters(0f, 7f, 1200f, 0f, 0f);
            SampleRegion region = BuildToneRegion(200f, lfo, 8 * SampleRate);
            float[] samples = Render(region, SampleRate);

            float maxDelta = 0f;
            for (int i = 1; i < samples.Length; i++)
                maxDelta = Math.Max(maxDelta, Math.Abs(samples[i] - samples[i - 1]));

            Assert.That(maxDelta, Is.LessThan(0.15f),
                $"max consecutive-sample delta {maxDelta} indicates a discontinuity at a control-rate tick boundary.");
        }
    }
}
