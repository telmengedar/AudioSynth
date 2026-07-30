using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Full-path tests for gen-11 (ModEnvToFilterCutoff) inside <see cref="SamplePlaybackVoice"/> and the
    /// per-note velocity-to-filter-cutoff offset inside <see cref="SamplePatch.StartVoice"/>: the audible
    /// cutoff sweep driven by the modulation envelope, and soft notes darkening beyond the concave gain curve.
    /// </summary>
    [TestFixture]
    public class SamplePlaybackVoiceModEnvFilterTests {

        const int SampleRate = 44100;

        // StartVoice re-resolves hold/decay from the region's raw timecents fields, not modEnv.HoldSeconds/DecaySeconds.
        static float SecondsToTimecents(float seconds) => (float)(1200.0 * Math.Log(seconds, 2.0));

        static SampleRegion BuildRegion(
            float frequency, FilterParameters filter, ModulationEnvelopeParameters modEnv, int bufferLength) {
            float[] buffer = new float[bufferLength];
            for (int i = 0; i < bufferLength; i++)
                buffer[i] = (float)Math.Sin(2.0 * Math.PI * frequency * i / SampleRate);
            return new SampleRegion(buffer, 0, buffer.Length, 0, buffer.Length, LoopMode.Continuous,
                SampleRate, 60, 0, EnvelopeParameters.Default, filter, LfoParameters.Default, 0f,
                modEnv: modEnv,
                modEnvHoldTimecents: SecondsToTimecents(Math.Max(modEnv.HoldSeconds, 0.0001f)),
                modEnvDecayTimecents: SecondsToTimecents(Math.Max(modEnv.DecaySeconds, 0.0001f)));
        }

        static float[] Render(SampleRegion region, int velocity, int frames) {
            SynthesizerOptions opts = new SynthesizerOptions(SampleRate, 1, 64, 16);
            SamplePatch patch = new SamplePatch(region, opts.SampleRate);
            Synthesizer synth = new Synthesizer(opts, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);
            synth.NoteOn(0, 60, velocity);
            OfflineRenderer.Render(synth, sink, frames);
            return sink.ToArray();
        }

        static float WindowRms(float[] samples, int center, int halfWindow) {
            double sum = 0.0;
            int count = 0;
            for (int i = center - halfWindow; i < center + halfWindow; i++) {
                sum += (double)samples[i] * samples[i];
                count++;
            }
            return (float)Math.Sqrt(sum / count);
        }

        [Test]
        [Description("Deliverable proof (design §13): on a mod-env-to-cutoff preset with an open base cutoff " +
            "and Ocarina's own gen-11 sign (-2630 cents, mirroring the real preset), high-frequency energy is " +
            "LOW right after the attack (mod envelope at its peak, cutoff pulled down the furthest) and RISES " +
            "once the envelope decays to a brighter partial sustain — an audible sweep the base-cutoff-alone " +
            "open-filter check could never produce, since the base cutoff (13500 cents) never itself moves.")]
        public void ModEnvToCutoffSweep_HfEnergyRisesAsEnvelopeDecaysToPartialSustain() {
            const float toneHz = 6000f;
            FilterParameters filter = new FilterParameters(FilterParameters.Sf2OpenCutoffHz, FilterParameters.ButterworthResonance, modEnvToCutoffCents: -2630f);
            ModulationEnvelopeParameters modEnv = new ModulationEnvelopeParameters(
                delaySeconds: 0f, attackSeconds: 0.001f, holdSeconds: 0f, decaySeconds: 0.05f, sustainLevel: 0.3f, releaseSeconds: 0.01f);
            SampleRegion region = BuildRegion(toneHz, filter, modEnv, SampleRate * 2);

            float[] output = Render(region, 127, SampleRate);

            int justAfterAttack = (int)(0.02f * SampleRate);
            int afterDecaySettles = (int)(0.3f * SampleRate);
            int halfWindow = 300;

            float rmsNearPeak = WindowRms(output, justAfterAttack, halfWindow);
            float rmsAfterDecay = WindowRms(output, afterDecaySettles, halfWindow);

            Assert.That(rmsAfterDecay, Is.GreaterThan(rmsNearPeak * 1.1f),
                $"HF energy once the envelope has decayed to its brighter partial sustain (rms={rmsAfterDecay}) " +
                $"must exceed the level near the envelope's peak, where the cutoff is pulled down the furthest " +
                $"(rms={rmsNearPeak}), by a wide margin.");
        }

        [Test]
        [Description("Bypass fidelity (design §8.2 invariant): zero gen-11 depth renders identically regardless " +
            "of the modulation envelope's shape, since the envelope's value is multiplied by a zero depth.")]
        public void ZeroModEnvDepth_IsUnaffectedByEnvelopeShape() {
            const float toneHz = 6000f;
            FilterParameters filter = new FilterParameters(FilterParameters.Sf2OpenCutoffHz, FilterParameters.ButterworthResonance, modEnvToCutoffCents: 0f);
            ModulationEnvelopeParameters activeShape = new ModulationEnvelopeParameters(0f, 0.001f, 0f, 0.05f, 0f, 0.01f);
            ModulationEnvelopeParameters inertShape = ModulationEnvelopeParameters.Default;

            float[] withActiveShape = Render(BuildRegion(toneHz, filter, activeShape, SampleRate), 127, SampleRate / 2);
            float[] withInertShape = Render(BuildRegion(toneHz, filter, inertShape, SampleRate), 127, SampleRate / 2);

            for (int i = 0; i < withActiveShape.Length; i++)
                Assert.That(withActiveShape[i], Is.EqualTo(withInertShape[i]),
                    $"sample {i} diverged even though gen-11 depth is zero; the envelope's shape must not leak through.");
        }

        [Test]
        [Description("Velocity darkening (design §8.4, SF2 §8.4.2): a soft note's high-frequency content, " +
            "normalised by its own concave gain, is measurably darker than a loud note's — proving the " +
            "velocity-to-filter-cutoff offset stacks on top of (not instead of) the concave velocity-to-gain curve.")]
        public void SoftVelocity_DarkensBeyondConcaveGainCurve() {
            const float toneHz = 8000f;
            // Soft note's cutoff offset engages the filter here; loud (zero offset) stays in the passband.
            FilterParameters filter = new FilterParameters(FilterParameters.CentsToHz(10000f), FilterParameters.ButterworthResonance);
            SampleRegion region = BuildRegion(toneHz, filter, ModulationEnvelopeParameters.Default, SampleRate);

            float[] loud = Render(region, 127, SampleRate / 2);
            float[] soft = Render(region, 20, SampleRate / 2);

            float loudRms = WindowRms(loud, loud.Length / 2, 2000);
            float softRms = WindowRms(soft, soft.Length / 2, 2000);

            float loudNormalised = loudRms / SamplePatch.VelocityToGain(127);
            float softNormalised = softRms / SamplePatch.VelocityToGain(20);

            Assert.That(softNormalised, Is.LessThan(loudNormalised * 0.8f),
                $"gain-normalised soft-note level ({softNormalised}) must be measurably darker than the loud note's " +
                $"({loudNormalised}); a flat ratio would mean velocity only affected gain, not filter cutoff.");
        }
    }
}
