using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;
using Pooshit.AudioSynth.Synthesis.Voices;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Full-path tests for LFO-to-volume (tremolo) inside <see cref="SamplePlaybackVoice"/>: the
    /// per-sample glide tracking proof and the #6272 §B zipper regression extended to this routing.
    /// </summary>
    [TestFixture]
    public class SamplePlaybackVoiceTremoloTests {

        const int SampleRate = 44100;
        const int ControlRateFrames = 64;

        static readonly EnvelopeParameters InstantSustainEnvelope = new EnvelopeParameters(0f, 0f, 0f, 0f, 1f, 0f);

        const float DcValue = 0.25f;
        const float ToneAmplitude = 0.1f;

        static SampleRegion BuildDcRegion(LfoParameters lfo, int length) {
            float[] buf = new float[length];
            for (int i = 0; i < length; i++)
                buf[i] = DcValue;
            return new SampleRegion(buf, 0, length, 0, length, LoopMode.Continuous, SampleRate, 60, 0,
                InstantSustainEnvelope, FilterParameters.Default, lfo, 0f);
        }

        static SampleRegion BuildToneRegion(float frequency, LfoParameters lfo, int bufferLength) {
            float[] buffer = new float[bufferLength];
            for (int i = 0; i < bufferLength; i++)
                buffer[i] = ToneAmplitude * (float)Math.Sin(2.0 * Math.PI * frequency * i / SampleRate);
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
        [Description("Deliverable proof: the per-sample tremolo multiplier tracks a linear glide toward " +
            "10^(lfoValue*VolumeDepthCentibels/200) at each control tick, replicated by an independent simulation.")]
        public void Tremolo_TracksLfoValue_AtControlTicks() {
            const float rateHz = 5f;
            const float depthCentibels = 100f;
            const int ticksToRender = 12;
            const int firstCheckedFrame = 320;

            LfoParameters lfoParameters = new LfoParameters(0f, rateHz, 0f, depthCentibels, 0f);
            SampleRegion region = BuildDcRegion(lfoParameters, 4000);
            int framesToRender = ControlRateFrames * ticksToRender;
            float[] output = Render(region, framesToRender);

            ModulationLfo predictor = new ModulationLfo(lfoParameters, SampleRate);
            float tremoloCurrent = 1f;
            float tremoloStep = 0f;

            for (int frame = 0; frame < framesToRender; frame++) {
                if (frame % ControlRateFrames == 0) {
                    float lfoValue = predictor.Advance(ControlRateFrames);
                    float target = (float)Math.Pow(10.0, lfoValue * depthCentibels / 200.0);
                    tremoloStep = (target - tremoloCurrent) / ControlRateFrames;
                }
                tremoloCurrent += tremoloStep;

                if (frame < firstCheckedFrame)
                    continue;

                float measuredMultiplier = output[frame] / DcValue;
                Assert.That(measuredMultiplier, Is.EqualTo(tremoloCurrent).Within(0.01f),
                    $"frame {frame}: measured tremolo gain {measuredMultiplier} did not track the predicted glide {tremoloCurrent}.");
            }
        }

        [Test]
        [Description("Regression for defect catalog #6272 §B (clicks/zipper class), extended to tremolo: a " +
            "control-rate tick introduces no amplitude discontinuity, even under strong tremolo depth.")]
        public void ControlTick_IntroducesNoAmplitudeDiscontinuity() {
            LfoParameters lfo = new LfoParameters(0f, 7f, 0f, 100f, 0f);
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
