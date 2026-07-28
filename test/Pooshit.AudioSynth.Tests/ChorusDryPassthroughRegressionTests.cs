using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Dry-passthrough regression guards for the chorus master insert (DiVoid #7188, design #7190 §14.9): with
    /// no chorus configured, or with a chorus configured but its wet mix at 0, the master path must be
    /// bit-for-bit identical to the pre-chorus render — proving the chorus never touches the signal it is
    /// not asked to affect. Mirrors <see cref="ReverbDryPassthroughRegressionTests"/>.
    /// </summary>
    [TestFixture]
    public class ChorusDryPassthroughRegressionTests {

        const int SampleRate = 44100;
        const int RenderFrames = 4096;

        static SampleRegion BuildDcRegion(float value, int length) {
            float[] buf = new float[length];
            for (int i = 0; i < length; i++)
                buf[i] = value;
            return new SampleRegion(buf, 0, length, 0, length, LoopMode.Continuous, SampleRate, 60, 0,
                EnvelopeParameters.Default, FilterParameters.Default, LfoParameters.Default, 0f);
        }

        static float[] Render(SynthesizerOptions options, float channelChorusSend = 0f) {
            SampleRegion region = BuildDcRegion(0.3f, RenderFrames);
            SamplePatch patch = new SamplePatch(region, options.SampleRate);
            Synthesizer synth = new Synthesizer(options, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.SetChannelChorusSend(0, channelChorusSend);
            synth.NoteOn(0, 60, 100);
            OfflineRenderer.Render(synth, sink, RenderFrames);
            return sink.ToArray();
        }

        [Test]
        [Description("Chorus absent (options.Chorus == null, the default) and chorus present with Wet = 0 " +
                     "must render bit-for-bit identically even with a fully-open channel send: Wet=0 is a " +
                     "structural passthrough inside Process, not merely an artifact of an empty send bus.")]
        public void ChorusAbsent_AndWetZero_RenderBitIdentically() {
            SynthesizerOptions dryOptions = new SynthesizerOptions(SampleRate, 2, 64, 8, chorus: null);
            SynthesizerOptions wetZeroOptions = new SynthesizerOptions(SampleRate, 2, 64, 8, chorus: new ChorusSettings(wet: 0f));

            float[] dry = Render(dryOptions, channelChorusSend: 1f);
            float[] wetZero = Render(wetZeroOptions, channelChorusSend: 1f);

            Assert.That(wetZero, Is.EqualTo(dry),
                "a chorus configured with Wet=0 must reproduce the chorus-absent render bit-for-bit.");
        }

        [Test]
        [Description("A configured, audible chorus (Wet > 0) fed by a real channel send must diverge from the " +
                     "dry render — otherwise the master insert is not doing anything and the deliverable " +
                     "render proof would be meaningless.")]
        public void AudibleChorus_DivergesFromDryRender() {
            SynthesizerOptions dryOptions = new SynthesizerOptions(SampleRate, 2, 64, 8, chorus: null);
            SynthesizerOptions wetOptions = new SynthesizerOptions(SampleRate, 2, 64, 8, chorus: ChorusSettings.Default);

            float[] dry = Render(dryOptions, channelChorusSend: 1f);
            float[] wet = Render(wetOptions, channelChorusSend: 1f);

            Assert.That(wet, Is.Not.EqualTo(dry), "an audible chorus (Wet > 0) fed by a real send must change the master output.");
        }

        [Test]
        [Description("Non-stereo output is unaffected by a configured chorus (mirrors the reverb §5/§14.6 " +
                     "guarantee): a Chorus is constructed only when Channels == 2, so a mono render is " +
                     "bit-identical regardless of whether chorus settings are supplied.")]
        public void NonStereoOutput_IsBitIdentical_RegardlessOfChorusConfiguration() {
            SynthesizerOptions monoDry = new SynthesizerOptions(SampleRate, 1, 64, 8, chorus: null);
            SynthesizerOptions monoWet = new SynthesizerOptions(SampleRate, 1, 64, 8, chorus: ChorusSettings.Default);

            float[] dry = Render(monoDry);
            float[] wet = Render(monoWet);

            Assert.That(wet, Is.EqualTo(dry), "mono output must be unaffected by chorus configuration.");
        }
    }
}
