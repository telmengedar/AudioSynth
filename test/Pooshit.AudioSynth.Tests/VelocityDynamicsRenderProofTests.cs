using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Deliverable-proof render for the concave velocity curve (DiVoid #7139): the same note rendered
    /// end-to-end through the real voice/synth path at a soft velocity versus a loud velocity must show a
    /// dynamic spread matching the concave <c>(velocity/127)²</c> ratio — noticeably wider than the linear
    /// map would produce. A constant-value (DC) region isolates the gain so the steady-state peak reflects
    /// only the velocity-derived gain.
    /// </summary>
    [TestFixture]
    public class VelocityDynamicsRenderProofTests {

        const int SampleRate = 44100;
        const int InternalBlockFrames = 64;
        const float DcValue = 0.8f;

        static SampleRegion BuildDcRegion(int length) {
            float[] buffer = new float[length];
            for (int i = 0; i < length; i++)
                buffer[i] = DcValue;
            return new SampleRegion(buffer, 0, length, 0, length, LoopMode.Continuous, SampleRate, 60, 0,
                EnvelopeParameters.Default, FilterParameters.Default, LfoParameters.Default);
        }

        static float SteadyStatePeak(int velocity) {
            const int framesToRender = 4096;
            SynthesizerOptions options = new SynthesizerOptions(SampleRate, 1, InternalBlockFrames, 16);
            SampleRegion region = BuildDcRegion(framesToRender * 4);
            SamplePatch patch = new SamplePatch(region, SampleRate);
            Synthesizer synth = new Synthesizer(options, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, velocity);
            OfflineRenderer.Render(synth, sink, framesToRender);

            float[] samples = sink.ToArray();
            float peak = 0f;
            for (int i = samples.Length / 2; i < samples.Length; i++)
                peak = Math.Max(peak, Math.Abs(samples[i]));
            return peak;
        }

        [Test]
        [Description("A soft (velocity 64) note renders at the concave ratio (~0.254) of a loud (velocity 127) " +
                     "note — well below the linear 0.5 — proving the widened note-to-note dynamics end-to-end.")]
        public void SoftVersusLoudNote_DynamicSpread_MatchesConcaveRatio() {
            float loudPeak = SteadyStatePeak(127);
            float softPeak = SteadyStatePeak(64);

            Assert.That(loudPeak, Is.GreaterThan(0.01f), "the loud note must render audible level.");

            float ratio = softPeak / loudPeak;
            Assert.That(ratio, Is.LessThan(0.5f),
                $"soft/loud ratio {ratio:F3} must fall below the linear 0.5; a concave curve widens dynamics.");
            Assert.That(ratio, Is.EqualTo(0.254f).Within(0.02f),
                $"soft/loud ratio {ratio:F3} must match the concave (64/127)² ≈ 0.254 characteristic.");
        }
    }
}
