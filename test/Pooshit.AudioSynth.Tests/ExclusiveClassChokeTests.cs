using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;
using Pooshit.AudioSynth.Tests.Helpers;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Deterministic + render-level tests for SF2 exclusive-class choke (DiVoid bug #7226, design #7227):
    /// starting a voice whose region carries a non-zero exclusive class must fast-fade every other
    /// occupied, non-draining, same-channel voice sharing that class; a different class, a different
    /// channel, or class 0 on both sides must never choke; content with no exclusive classes at all
    /// (the overwhelming majority of existing SF2 content, and every non-SF2 patch) must render exactly
    /// as it did before this feature existed.
    /// </summary>
    [TestFixture]
    public class ExclusiveClassChokeTests {

        const int SampleRate = 44100;
        const int SettleFrames = 500;
        const int MeasureWindowFrames = 50;

        static SynthesizerOptions MonoOptions(int maxVoices) => new SynthesizerOptions(SampleRate, 1, 64, maxVoices);

        [Test]
        [Description("Design §9/§14 step 4: a second voice with the same non-zero exclusive class on the " +
                     "same channel chokes the first — its FastFadeForSteal is called.")]
        public void NoteOn_SameNonZeroClass_SameChannel_ChokesFirstVoice() {
            const int channel = 0;
            const int exclusiveClass = 1;

            ExclusiveClassPatch firstPatch = new ExclusiveClassPatch(exclusiveClass);
            Synthesizer synth = new Synthesizer(MonoOptions(maxVoices: 4), firstPatch);
            synth.SetChannelPatch(channel, firstPatch);
            synth.NoteOn(channel, 46, 100);
            RecordingExclusiveVoice firstVoice = firstPatch.LastVoice!;

            ExclusiveClassPatch secondPatch = new ExclusiveClassPatch(exclusiveClass);
            synth.SetChannelPatch(channel, secondPatch);
            synth.NoteOn(channel, 42, 100);

            Assert.That(firstVoice.FastFadeForStealCalled, Is.True,
                "a second same-class same-channel note-on must choke the first voice.");
        }

        [Test]
        [Description("A second voice with a different non-zero exclusive class on the same channel must " +
                     "not choke the first.")]
        public void NoteOn_DifferentClass_SameChannel_DoesNotChoke() {
            const int channel = 0;

            ExclusiveClassPatch firstPatch = new ExclusiveClassPatch(exclusiveClass: 1);
            Synthesizer synth = new Synthesizer(MonoOptions(maxVoices: 4), firstPatch);
            synth.SetChannelPatch(channel, firstPatch);
            synth.NoteOn(channel, 46, 100);
            RecordingExclusiveVoice firstVoice = firstPatch.LastVoice!;

            ExclusiveClassPatch secondPatch = new ExclusiveClassPatch(exclusiveClass: 2);
            synth.SetChannelPatch(channel, secondPatch);
            synth.NoteOn(channel, 42, 100);

            Assert.That(firstVoice.FastFadeForStealCalled, Is.False,
                "a different exclusive class must not choke the first voice.");
        }

        [Test]
        [Description("Two class-0 voices on the same channel must never choke each other — class 0 means " +
                     "'no choke group' and is the structural fast path (design §7/§10).")]
        public void NoteOn_BothClassZero_SameChannel_DoesNotChoke() {
            const int channel = 0;

            ExclusiveClassPatch firstPatch = new ExclusiveClassPatch(exclusiveClass: 0);
            Synthesizer synth = new Synthesizer(MonoOptions(maxVoices: 4), firstPatch);
            synth.SetChannelPatch(channel, firstPatch);
            synth.NoteOn(channel, 46, 100);
            RecordingExclusiveVoice firstVoice = firstPatch.LastVoice!;

            ExclusiveClassPatch secondPatch = new ExclusiveClassPatch(exclusiveClass: 0);
            synth.SetChannelPatch(channel, secondPatch);
            synth.NoteOn(channel, 42, 100);

            Assert.That(firstVoice.FastFadeForStealCalled, Is.False,
                "class 0 on both voices must never trigger a choke.");
        }

        [Test]
        [Description("Same non-zero exclusive class but a different MIDI channel must not choke — the SF2 " +
                     "spec matches exclusive class within a channel/preset only.")]
        public void NoteOn_SameClass_DifferentChannel_DoesNotChoke() {
            const int exclusiveClass = 1;

            ExclusiveClassPatch firstPatch = new ExclusiveClassPatch(exclusiveClass);
            Synthesizer synth = new Synthesizer(MonoOptions(maxVoices: 4), firstPatch);
            synth.SetChannelPatch(0, firstPatch);
            synth.NoteOn(0, 46, 100);
            RecordingExclusiveVoice firstVoice = firstPatch.LastVoice!;

            ExclusiveClassPatch secondPatch = new ExclusiveClassPatch(exclusiveClass);
            synth.SetChannelPatch(1, secondPatch);
            synth.NoteOn(1, 42, 100);

            Assert.That(firstVoice.FastFadeForStealCalled, Is.False,
                "the same exclusive class on a different channel must not choke across channels.");
        }

        [Test]
        [Description("Bit-identical regression (design §10/§14 step 6): two overlapping notes on the same " +
                     "channel through real SamplePlaybackVoice regions with no exclusive class (default 0) " +
                     "must both keep sounding together — the overwhelmingly common legacy shape (a channel " +
                     "playing more than one note at once) is structurally untouched by the choke scan.")]
        public void NoteOn_TwoOverlappingNotesNoExclusiveClass_BothKeepSounding() {
            const int channel = 0;
            const float firstValue = 0.2f;
            const float secondValue = 0.3f;

            Synthesizer synth = new Synthesizer(MonoOptions(maxVoices: 4), new SamplePatch(BuildSustainedDcRegion(firstValue, 1024), SampleRate));
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(channel, 60, 127);
            OfflineRenderer.Render(synth, sink, SettleFrames);

            synth.SetChannelPatch(channel, new SamplePatch(BuildSustainedDcRegion(secondValue, 1024), SampleRate));
            synth.NoteOn(channel, 61, 127);
            OfflineRenderer.Render(synth, sink, SettleFrames);

            float level = TrailingMeanAbs(sink.ToArray(), MeasureWindowFrames);
            float expectedBothSounding = firstValue + secondValue;
            TestContext.WriteLine($"Trailing level: {level:F6} (both-sounding expectation {expectedBothSounding:F6}).");
            Assert.That(level, Is.EqualTo(expectedBothSounding).Within(0.01f),
                "with no exclusive class on either region, both same-channel notes must keep sounding " +
                "together, exactly as before the choke feature existed.");
        }

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
    }
}
