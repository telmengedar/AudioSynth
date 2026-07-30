using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Render-level regression tests for SF2 generator 48 (InitialAttenuation), DiVoid #7269:
    /// <see cref="SampleRegion.InitialAttenuationGain"/> must fold into <see cref="SamplePatch.StartVoice"/>'s
    /// velocity-derived target gain as a static multiplier, alongside (not replacing) velocity gain, so a
    /// region carrying attenuation renders proportionally quieter at steady state than an identical region
    /// with no attenuation.
    /// </summary>
    public class InitialAttenuationGainTests {

        const int SampleRate = 44100;
        const int InternalBlockFrames = 64;
        const float DcValue = 0.8f;

        // 500 ms: comfortably past the 5 ms gain-ramp glide and the ~1 ms SF2-default envelope settle,
        // so the tail of the render reflects only the steady-state target gain.
        const int SteadyStateFrames = 22050;

        static SampleRegion BuildDcRegion(int length, float initialAttenuationGain) {
            float[] buf = new float[length];
            for (int i = 0; i < length; i++)
                buf[i] = DcValue;
            return new SampleRegion(buf, 0, length, 0, length, LoopMode.Continuous, SampleRate, 60, 0,
                EnvelopeParameters.Default, FilterParameters.Default, LfoParameters.Default, 0f,
                reverbSend: 1f, chorusSend: 0f, exclusiveClass: 0, initialAttenuationGain: initialAttenuationGain);
        }

        static float RenderSteadyStateAmplitude(float initialAttenuationGain) {
            SynthesizerOptions opts = new SynthesizerOptions(SampleRate, 1, InternalBlockFrames, 16);
            SampleRegion region = BuildDcRegion(SteadyStateFrames * 2, initialAttenuationGain);
            SamplePatch patch = new SamplePatch(region, SampleRate);
            Synthesizer synth = new Synthesizer(opts, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, SteadyStateFrames);

            float[] samples = sink.ToArray();
            float tail = 0f;
            for (int i = samples.Length - 100; i < samples.Length; i++)
                tail = Math.Max(tail, Math.Abs(samples[i]));
            return tail;
        }

        [Test]
        [Description("A region with InitialAttenuationGain=1 (absent gen-48) renders the same steady-state " +
                     "amplitude as before this generator was read: full DC level at velocity 127.")]
        public void InitialAttenuationGainOne_RendersFullLevel() {
            float amplitude = RenderSteadyStateAmplitude(1f);

            Assert.That(amplitude, Is.EqualTo(DcValue).Within(0.01f),
                "gain=1 (no attenuation) must reproduce the unattenuated DC level.");
        }

        [Test]
        [Description("A region with InitialAttenuationGain=0.5 (e.g. 60 cB of gen-48) renders at half the " +
                     "steady-state amplitude of an otherwise-identical unattenuated region, at the same velocity.")]
        public void InitialAttenuationGainHalf_HalvesSteadyStateAmplitude() {
            float full = RenderSteadyStateAmplitude(1f);
            float attenuated = RenderSteadyStateAmplitude(0.5f);

            Assert.That(attenuated, Is.EqualTo(full * 0.5f).Within(0.01f),
                $"a region gain of 0.5 must halve the rendered amplitude (full={full}, attenuated={attenuated}).");
        }
    }
}
