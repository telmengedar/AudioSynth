using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Deliverable-proof tests for <see cref="Synthesizer.ReleaseAllNotes"/> (CC123 All Notes Off engine
    /// seam, DiVoid task #7243, design #7245): every sounding voice on the channel must be released
    /// exactly as a real <see cref="Synthesizer.NoteOff"/> would — deferred while the channel's sustain
    /// pedal is held, released into the normal envelope tail once it is not — leaving other channels
    /// untouched.
    /// </summary>
    [TestFixture]
    public class SynthesizerReleaseAllNotesTests {

        const int SampleRate = 44100;
        const int SettleFrames = 500;
        const int PostEventFrames = 400;
        const int MeasureWindowFrames = 50;

        static SynthesizerOptions MonoOptions(int maxVoices = 4) => new SynthesizerOptions(SampleRate, 1, 64, maxVoices);

        static SampleRegion BuildSustainedDcRegion(float value, int length) {
            float[] buf = new float[length];
            for (int i = 0; i < length; i++)
                buf[i] = value;
            return new SampleRegion(buf, 0, length, 0, length, LoopMode.Continuous, SampleRate, 60, 0,
                EnvelopeParameters.Default, FilterParameters.Default, LfoParameters.Default, 0f);
        }

        static float TrailingMeanAbs(float[] samples, int windowFrames) {
            int window = Math.Min(samples.Length, windowFrames);
            int start = samples.Length - window;
            double sum = 0.0;
            for (int i = start; i < samples.Length; i++)
                sum += Math.Abs(samples[i]);
            return (float)(sum / window);
        }

        [Test]
        [Description("Deliverable core (design §6): with sustain never engaged, ReleaseAllNotes releases " +
                     "every held key on the channel into the normal envelope tail, exactly as a real NoteOff " +
                     "per key would.")]
        public void ReleaseAllNotes_SustainNotHeld_ReleasesAllKeysOnChannel() {
            const float value = 0.3f;
            SynthesizerOptions options = MonoOptions();
            Synthesizer synth = new Synthesizer(options, new SamplePatch(BuildSustainedDcRegion(value, 1024), SampleRate));
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 127);
            synth.NoteOn(0, 64, 127);
            OfflineRenderer.Render(synth, sink, SettleFrames);

            synth.ReleaseAllNotes(0);
            OfflineRenderer.Render(synth, sink, PostEventFrames);

            float level = TrailingMeanAbs(sink.ToArray(), MeasureWindowFrames);
            TestContext.WriteLine($"Trailing level after ReleaseAllNotes (two keys, sustain up): {level:F6}.");
            Assert.That(level, Is.LessThan(value * 0.05f),
                "both keys on the channel must release to silence through their normal envelope tail.");
        }

        [Test]
        [Description("Deliverable core (design §6/§12): with the sustain pedal held, ReleaseAllNotes must " +
                     "defer every voice on the channel instead of releasing it — they keep ringing until " +
                     "the pedal lifts, exactly like a real NoteOff received under a held pedal.")]
        public void ReleaseAllNotes_SustainHeld_DefersRelease_UntilPedalUp() {
            const float value = 0.3f;
            SynthesizerOptions options = MonoOptions();
            Synthesizer synth = new Synthesizer(options, new SamplePatch(BuildSustainedDcRegion(value, 1024), SampleRate));
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.SetChannelSustain(0, true);
            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, SettleFrames);

            synth.ReleaseAllNotes(0);
            OfflineRenderer.Render(synth, sink, PostEventFrames);

            float heldLevel = TrailingMeanAbs(sink.ToArray(), MeasureWindowFrames);
            TestContext.WriteLine($"Trailing level after ReleaseAllNotes with pedal down: {heldLevel:F6} (settled value {value}).");
            Assert.That(heldLevel, Is.GreaterThan(value * 0.9f),
                "with the pedal down, ReleaseAllNotes must defer the release — the voice must still be " +
                "sounding at essentially its settled level.");

            synth.SetChannelSustain(0, false);
            OfflineRenderer.Render(synth, sink, PostEventFrames);

            float releasedLevel = TrailingMeanAbs(sink.ToArray(), MeasureWindowFrames);
            TestContext.WriteLine($"Trailing level after pedal-up: {releasedLevel:F6}.");
            Assert.That(releasedLevel, Is.LessThan(value * 0.05f),
                "lifting the pedal must release the voice deferred by ReleaseAllNotes.");
        }

        [Test]
        [Description("Design §6/§12: ReleaseAllNotes must leave a voice on another channel untouched.")]
        public void ReleaseAllNotes_OtherChannel_IsUntouched() {
            const float releasedValue = 0.3f;
            const float otherValue = 0.2f;
            SynthesizerOptions options = MonoOptions();
            Synthesizer synth = new Synthesizer(options, new SamplePatch(BuildSustainedDcRegion(releasedValue, 1024), SampleRate));
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 127);
            synth.SetChannelPatch(1, new SamplePatch(BuildSustainedDcRegion(otherValue, 1024), SampleRate));
            synth.NoteOn(1, 60, 127);
            OfflineRenderer.Render(synth, sink, SettleFrames);

            synth.ReleaseAllNotes(0);
            OfflineRenderer.Render(synth, sink, PostEventFrames);

            float level = TrailingMeanAbs(sink.ToArray(), MeasureWindowFrames);
            TestContext.WriteLine($"Trailing level after releasing channel 0 only: {level:F6} (channel-1-only expectation {otherValue:F6}).");
            Assert.That(level, Is.EqualTo(otherValue).Within(0.02f),
                "releasing all notes on channel 0 must not affect channel 1's still-sounding voice.");
        }

        [Test]
        [Description("Design §6/§12: a channel with no occupied voices is a no-op.")]
        public void ReleaseAllNotes_EmptyChannel_IsNoOp() {
            SynthesizerOptions options = MonoOptions();
            Synthesizer synth = new Synthesizer(options, new SamplePatch(BuildSustainedDcRegion(0.3f, 1024), SampleRate));

            Assert.DoesNotThrow(() => synth.ReleaseAllNotes(5));
        }

        [TestCase(-1)]
        [TestCase(16)]
        [Description("ReleaseAllNotes rejects a channel outside [0,15], mirroring the other channel-scoped seams.")]
        public void ReleaseAllNotes_ChannelOutOfRange_Throws(int channel) {
            SynthesizerOptions options = MonoOptions();
            Synthesizer synth = new Synthesizer(options, new SamplePatch(BuildSustainedDcRegion(0.3f, 1024), SampleRate));

            Assert.Throws<ArgumentOutOfRangeException>(() => synth.ReleaseAllNotes(channel));
        }
    }
}
