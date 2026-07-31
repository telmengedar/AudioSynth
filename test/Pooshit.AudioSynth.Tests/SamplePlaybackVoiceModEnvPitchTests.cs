using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Full-path tests for gen-7 (ModEnvToPitch) inside <see cref="SamplePlaybackVoice"/>: the modulation
    /// envelope folding into the effective pitch increment each control tick, alongside the existing
    /// LFO/pitch-bend/mod-wheel contributions, and bit-for-bit bypass when the depth is zero.
    /// </summary>
    [TestFixture]
    public class SamplePlaybackVoiceModEnvPitchTests {

        const int SampleRate = 44100;

        // StartVoice re-resolves hold/decay from the region's raw timecents fields, not modEnv.HoldSeconds/DecaySeconds.
        static float SecondsToTimecents(float seconds) => (float)(1200.0 * Math.Log(seconds, 2.0));

        static SampleRegion BuildRegion(float frequency, float modEnvToPitchCents, ModulationEnvelopeParameters modEnv, int bufferLength) {
            float[] buffer = new float[bufferLength];
            for (int i = 0; i < bufferLength; i++)
                buffer[i] = (float)Math.Sin(2.0 * Math.PI * frequency * i / SampleRate);
            return new SampleRegion(buffer, 0, buffer.Length, 0, buffer.Length, LoopMode.Continuous,
                SampleRate, 60, 0, EnvelopeParameters.Default, FilterParameters.Default, LfoParameters.Default, 0f,
                modEnv: modEnv,
                modEnvHoldTimecents: SecondsToTimecents(Math.Max(modEnv.HoldSeconds, 0.0001f)),
                modEnvDecayTimecents: SecondsToTimecents(Math.Max(modEnv.DecaySeconds, 0.0001f)),
                modEnvToPitchCents: modEnvToPitchCents);
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

        static int CountZeroCrossings(float[] samples, int start, int length) {
            int count = 0;
            for (int i = start + 1; i < start + length; i++) {
                if (samples[i - 1] <= 0f && samples[i] > 0f)
                    count++;
            }
            return count;
        }

        [Test]
        [Description("Deliverable proof (design D1 fast-follow): with a substantial gen-7 depth and a decaying " +
            "mod envelope, the measured pitch (zero-crossing rate) near the envelope's peak is measurably " +
            "HIGHER than once it has decayed toward a lower sustain -- proving the mod-envelope value folds " +
            "into the effective pitch increment each control tick, alongside the existing LFO/pitch-bend/" +
            "mod-wheel contributions that already recompute there.")]
        public void ModEnvToPitchDepth_PitchTracksEnvelopeDecay() {
            const float toneHz = 440f;
            ModulationEnvelopeParameters modEnv = new ModulationEnvelopeParameters(
                delaySeconds: 0f, attackSeconds: 0.001f, holdSeconds: 0f, decaySeconds: 0.05f, sustainLevel: 0f, releaseSeconds: 0.01f);
            SampleRegion region = BuildRegion(toneHz, modEnvToPitchCents: 1200f, modEnv, SampleRate);

            float[] output = Render(region, SampleRate / 2);

            int window = SampleRate / 20;
            int nearPeak = CountZeroCrossings(output, (int)(0.01f * SampleRate), window);
            int afterDecay = CountZeroCrossings(output, (int)(0.3f * SampleRate), window);

            Assert.That(nearPeak, Is.GreaterThan((int)(afterDecay * 1.2f)),
                $"zero-crossing rate near the envelope's peak ({nearPeak}) must exceed the rate once it has " +
                $"decayed toward a lower sustain ({afterDecay}) by a wide margin; a +1200-cent gen-7 depth " +
                $"doubles pitch when the mod envelope is at its peak.");
        }

        [Test]
        [Description("Bypass fidelity (mirrors the gen-11 design §8.2 invariant): zero gen-7 depth renders " +
            "identically regardless of the modulation envelope's shape, since the envelope's value is " +
            "multiplied by a zero depth.")]
        public void ZeroModEnvToPitchDepth_IsUnaffectedByEnvelopeShape() {
            const float toneHz = 440f;
            ModulationEnvelopeParameters activeShape = new ModulationEnvelopeParameters(0f, 0.001f, 0f, 0.05f, 0f, 0.01f);
            ModulationEnvelopeParameters inertShape = ModulationEnvelopeParameters.Default;

            float[] withActiveShape = Render(BuildRegion(toneHz, 0f, activeShape, SampleRate), SampleRate / 2);
            float[] withInertShape = Render(BuildRegion(toneHz, 0f, inertShape, SampleRate), SampleRate / 2);

            for (int i = 0; i < withActiveShape.Length; i++)
                Assert.That(withActiveShape[i], Is.EqualTo(withInertShape[i]),
                    $"sample {i} diverged even though gen-7 depth is zero; the envelope's shape must not leak through.");
        }
    }
}
