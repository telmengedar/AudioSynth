using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Dry-passthrough regression guards for the reverb master insert (DiVoid #7162, design §14.8): with
    /// no reverb configured, or with a reverb configured but its wet mix at 0, the master path must be
    /// bit-for-bit identical to the pre-reverb (PR 15) render — proving the reverb never touches the
    /// signal it is not asked to affect.
    /// </summary>
    [TestFixture]
    public class ReverbDryPassthroughRegressionTests {

        const int SampleRate = 44100;
        const int RenderFrames = 4096;

        static SampleRegion BuildDcRegion(float value, int length) {
            float[] buf = new float[length];
            for (int i = 0; i < length; i++)
                buf[i] = value;
            return new SampleRegion(buf, 0, length, 0, length, LoopMode.Continuous, SampleRate, 60, 0,
                EnvelopeParameters.Default, FilterParameters.Default, LfoParameters.Default, 0f);
        }

        static float[] Render(SynthesizerOptions options) {
            SampleRegion region = BuildDcRegion(0.3f, RenderFrames);
            SamplePatch patch = new SamplePatch(region, options.SampleRate);
            Synthesizer synth = new Synthesizer(options, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 100);
            OfflineRenderer.Render(synth, sink, RenderFrames);
            return sink.ToArray();
        }

        [Test]
        [Description("Reverb absent (options.Reverb == null, the default) and reverb present with Wet = 0 " +
                     "must render bit-for-bit identically: Wet=0 is a structural passthrough, not a tuned one.")]
        public void ReverbAbsent_AndWetZero_RenderBitIdentically() {
            SynthesizerOptions dryOptions = new SynthesizerOptions(SampleRate, 2, 64, 8, reverb: null);
            SynthesizerOptions wetZeroOptions = new SynthesizerOptions(SampleRate, 2, 64, 8, reverb: new ReverbSettings(wet: 0f));

            float[] dry = Render(dryOptions);
            float[] wetZero = Render(wetZeroOptions);

            Assert.That(wetZero, Is.EqualTo(dry),
                "a reverb configured with Wet=0 must reproduce the reverb-absent render bit-for-bit.");
        }

        [Test]
        [Description("A configured, audible reverb (Wet > 0) must diverge from the dry render — otherwise the " +
                     "master insert is not doing anything and the deliverable render proof would be meaningless.")]
        public void AudibleReverb_DivergesFromDryRender() {
            SynthesizerOptions dryOptions = new SynthesizerOptions(SampleRate, 2, 64, 8, reverb: null);
            SynthesizerOptions wetOptions = new SynthesizerOptions(SampleRate, 2, 64, 8, reverb: ReverbSettings.Default);

            float[] dry = Render(dryOptions);
            float[] wet = Render(wetOptions);

            Assert.That(wet, Is.Not.EqualTo(dry), "an audible reverb (Wet > 0) must change the master output.");
        }

        [Test]
        [Description("Non-stereo output is unaffected by a configured reverb (design §5/§14.6): a Reverb is " +
                     "constructed only when Channels == 2, so a mono render is bit-identical regardless of " +
                     "whether reverb settings are supplied.")]
        public void NonStereoOutput_IsBitIdentical_RegardlessOfReverbConfiguration() {
            SynthesizerOptions monoDry = new SynthesizerOptions(SampleRate, 1, 64, 8, reverb: null);
            SynthesizerOptions monoWet = new SynthesizerOptions(SampleRate, 1, 64, 8, reverb: ReverbSettings.Default);

            float[] dry = Render(monoDry);
            float[] wet = Render(monoWet);

            Assert.That(wet, Is.EqualTo(dry), "mono output must be unaffected by reverb configuration.");
        }
    }
}
