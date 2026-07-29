using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Deliverable-proof tests for <see cref="Synthesizer.SilenceChannel"/> (CC120 All Sound Off engine
    /// seam, DiVoid task #7243, design #7245): every sounding voice on the channel must fast-fade to
    /// silence over the ~5ms declick window (<see cref="GainRamp.DefaultSmoothingSeconds"/>) — distinctly
    /// faster than a long natural release tail would decay — regardless of the channel's sustain-pedal
    /// state, must cancel a parked pending steal-note on the channel, and must leave other channels
    /// untouched.
    /// </summary>
    [TestFixture]
    public class SynthesizerSilenceChannelTests {

        const int SampleRate = 44100;

        /// <summary>Frames rendered before SilenceChannel so the DAHDSR envelope fully settles.</summary>
        const int SettleFrames = 500;

        /// <summary>
        /// Frames rendered after SilenceChannel: comfortably past the ~5ms (≈220-frame) fast-fade window,
        /// but a small fraction of the long 0.5s release tail used to distinguish a fade from a release.
        /// </summary>
        const int PostSilenceFrames = 400;

        const int MeasureWindowFrames = 50;

        static SynthesizerOptions MonoOptions(int maxVoices = 4) => new SynthesizerOptions(SampleRate, 1, 64, maxVoices);

        /// <summary>
        /// A sustained DC region whose release is deliberately long (0.5s, far longer than the fast-fade's
        /// ~5ms) so a test can tell "faded via FastFadeForSteal" (silent well within PostSilenceFrames)
        /// apart from "released via Release()" (still near-full-level after the same window).
        /// </summary>
        static SampleRegion BuildLongReleaseDcRegion(float value, int length) {
            float[] buf = new float[length];
            for (int i = 0; i < length; i++)
                buf[i] = value;
            EnvelopeParameters longRelease = new EnvelopeParameters(
                EnvelopeParameters.Sf2DefaultTimeSeconds, EnvelopeParameters.Sf2DefaultTimeSeconds,
                EnvelopeParameters.Sf2DefaultTimeSeconds, EnvelopeParameters.Sf2DefaultTimeSeconds,
                1f, 0.5f);
            return new SampleRegion(buf, 0, length, 0, length, LoopMode.Continuous, SampleRate, 60, 0,
                longRelease, FilterParameters.Default, LfoParameters.Default, 0f);
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
        [Description("Deliverable core (design §6): SilenceChannel must silence a sounding voice within the " +
                     "~5ms fast-fade window, not the full (here, deliberately long) release tail — proving it " +
                     "goes through FastFadeForSteal and not Release().")]
        public void SilenceChannel_SoundingVoice_SilencesWithinFastFadeWindow_NotFullReleaseTail() {
            const float value = 0.4f;
            SynthesizerOptions options = MonoOptions();
            Synthesizer synth = new Synthesizer(options, new SamplePatch(BuildLongReleaseDcRegion(value, 4096), SampleRate));
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, SettleFrames);

            synth.SilenceChannel(0);
            OfflineRenderer.Render(synth, sink, PostSilenceFrames);

            float level = TrailingMeanAbs(sink.ToArray(), MeasureWindowFrames);
            TestContext.WriteLine($"Trailing level after SilenceChannel (long-release patch): {level:F6} (settled value {value}).");
            Assert.That(level, Is.LessThan(value * 0.05f),
                "with a 0.5s release tail, a voice released via Release() would still be near its settled " +
                "level after only ~9ms; SilenceChannel must instead fast-fade it to silence in that window.");
        }

        [Test]
        [Description("Deliverable core (design §6/§12): SilenceChannel ignores the channel's sustain-pedal " +
                     "state entirely — a pedal-down, physically-held note is still hard-silenced.")]
        public void SilenceChannel_SustainPedalDown_StillSilencesVoice() {
            const float value = 0.4f;
            SynthesizerOptions options = MonoOptions();
            Synthesizer synth = new Synthesizer(options, new SamplePatch(BuildLongReleaseDcRegion(value, 4096), SampleRate));
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.SetChannelSustain(0, true);
            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, SettleFrames);

            synth.SilenceChannel(0);
            OfflineRenderer.Render(synth, sink, PostSilenceFrames);

            float level = TrailingMeanAbs(sink.ToArray(), MeasureWindowFrames);
            TestContext.WriteLine($"Trailing level after SilenceChannel with pedal down: {level:F6}.");
            Assert.That(level, Is.LessThan(value * 0.05f),
                "SilenceChannel must silence a voice even while the channel's sustain pedal is held.");
        }

        [Test]
        [Description("Design §6/§12: SilenceChannel must leave voices on other channels untouched.")]
        public void SilenceChannel_OtherChannel_IsUntouched() {
            const float silencedValue = 0.4f;
            const float otherValue = 0.25f;
            SynthesizerOptions options = MonoOptions();
            Synthesizer synth = new Synthesizer(options, new SamplePatch(BuildLongReleaseDcRegion(silencedValue, 4096), SampleRate));
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 127);
            synth.SetChannelPatch(1, new SamplePatch(BuildLongReleaseDcRegion(otherValue, 4096), SampleRate));
            synth.NoteOn(1, 60, 127);
            OfflineRenderer.Render(synth, sink, SettleFrames);

            synth.SilenceChannel(0);
            OfflineRenderer.Render(synth, sink, PostSilenceFrames);

            float level = TrailingMeanAbs(sink.ToArray(), MeasureWindowFrames);
            TestContext.WriteLine($"Trailing level after silencing channel 0 only: {level:F6} (channel-1-only expectation {otherValue:F6}).");
            Assert.That(level, Is.EqualTo(otherValue).Within(0.02f),
                "silencing channel 0 must not affect channel 1's still-sounding voice.");
        }

        [Test]
        [Description("Design §6/§12: a channel with no occupied voices is a no-op — no exception, no effect " +
                     "on other channels.")]
        public void SilenceChannel_EmptyChannel_IsNoOp() {
            SynthesizerOptions options = MonoOptions();
            Synthesizer synth = new Synthesizer(options, new SamplePatch(BuildLongReleaseDcRegion(0.3f, 4096), SampleRate));

            Assert.DoesNotThrow(() => synth.SilenceChannel(5));
        }

        [TestCase(-1)]
        [TestCase(16)]
        [Description("SilenceChannel rejects a channel outside [0,15], mirroring the other channel-scoped seams.")]
        public void SilenceChannel_ChannelOutOfRange_Throws(int channel) {
            SynthesizerOptions options = MonoOptions();
            Synthesizer synth = new Synthesizer(options, new SamplePatch(BuildLongReleaseDcRegion(0.3f, 4096), SampleRate));

            Assert.Throws<ArgumentOutOfRangeException>(() => synth.SilenceChannel(channel));
        }

        [Test]
        [Description("Design §6/§12: a note parked behind another voice's steal fade on the silenced channel " +
                     "must be cancelled by SilenceChannel — once the outgoing fade completes, the pool slot " +
                     "must not spring the cancelled note to life.")]
        public void SilenceChannel_ParkedPendingSteal_IsCancelled() {
            const float value = 0.5f;
            SynthesizerOptions options = MonoOptions(maxVoices: 1);
            Synthesizer synth = new Synthesizer(options, new SamplePatch(BuildLongReleaseDcRegion(value, 4096), SampleRate));
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, SettleFrames);

            // Pool is full (maxVoices=1); this NoteOn forces a steal, parking a pending note behind the
            // first voice's fade-out instead of starting immediately.
            synth.NoteOn(0, 61, 127);

            synth.SilenceChannel(0);
            OfflineRenderer.Render(synth, sink, PostSilenceFrames);

            float level = TrailingMeanAbs(sink.ToArray(), MeasureWindowFrames);
            TestContext.WriteLine($"Trailing level after cancelling a parked steal-note: {level:F6}.");
            Assert.That(level, Is.LessThan(value * 0.05f),
                "the parked pending note must be cancelled by SilenceChannel, not started once the victim's " +
                "fade completes.");
        }

        /// <summary>
        /// Frames rendered after a steal-triggering NoteOn so a NOT-cancelled parked note has time to
        /// start (once the victim's ~5ms fade completes) and its own DAHDSR attack to fully settle,
        /// mirroring <c>VoiceStealingTests.StealSettleFrames</c>.
        /// </summary>
        const int StealSettleFrames = 800;

        [Test]
        [Description("Task #7249 W1/W3 (over-cancel): during a cross-channel steal, a slot's CURRENT voice " +
                     "can belong to a different channel than the note PARKED behind its fade. Silencing the " +
                     "OUTGOING voice's channel must fast-fade that voice but must NOT cancel the parked note " +
                     "for the OTHER channel -- it must still start and sound once the fade completes.")]
        public void SilenceChannel_ParkedPendingSteal_ForDifferentChannel_IsNotDropped() {
            const float victimValue = 0.5f;
            const float parkedValue = 0.6f;
            SynthesizerOptions options = MonoOptions(maxVoices: 1);
            Synthesizer synth = new Synthesizer(options, new SamplePatch(BuildLongReleaseDcRegion(victimValue, 4096), SampleRate));
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            // Channel 0 occupies the only slot (it will be the outgoing/victim voice).
            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, SettleFrames);

            // Channel 1's NoteOn forces a steal: the slot's Channel stays 0 (the fading victim) while
            // PendingChannel becomes 1 (the parked incoming note) -- exactly the asymmetric case W1 covers.
            synth.SetChannelPatch(1, new SamplePatch(BuildLongReleaseDcRegion(parkedValue, 4096), SampleRate));
            synth.NoteOn(1, 61, 127);

            // Silencing channel 0 -- the slot's CURRENT (victim) channel -- must fast-fade that voice but
            // must not touch channel 1's parked note (over-cancel would key this off slot.Channel alone).
            synth.SilenceChannel(0);
            OfflineRenderer.Render(synth, sink, StealSettleFrames);

            float level = TrailingMeanAbs(sink.ToArray(), MeasureWindowFrames);
            TestContext.WriteLine($"Trailing level after SilenceChannel(0) with channel 1's note parked behind " +
                                   $"its fade: {level:F6} (expected ~{parkedValue:F6}, the parked note settled).");
            Assert.That(level, Is.EqualTo(parkedValue).Within(0.05f),
                "SilenceChannel(0) must not cancel channel 1's parked note merely because it is parked " +
                "behind channel 0's fading voice; it must start and settle once the fade completes.");
        }

        [Test]
        [Description("Task #7249 W1/W3 (under-cancel / resurrection): a note parked behind a DIFFERENT " +
                     "channel's fading victim, but itself destined for the channel being silenced, must be " +
                     "cancelled -- otherwise it resurrects after All Sound Off, which SilenceChannel exists " +
                     "to prevent (design §12).")]
        public void SilenceChannel_ParkedPendingSteal_TargetingSilencedChannel_IsCancelled_ViaOtherVictimSlot() {
            const float victimValue = 0.5f;
            const float parkedValue = 0.6f;
            SynthesizerOptions options = MonoOptions(maxVoices: 1);
            Synthesizer synth = new Synthesizer(options, new SamplePatch(BuildLongReleaseDcRegion(victimValue, 4096), SampleRate));
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            // Channel 0 occupies the only slot (the victim voice).
            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, SettleFrames);

            // Channel 1's NoteOn forces a steal: slot.Channel stays 0 (the fading victim), slot.PendingChannel
            // becomes 1 -- the parked note is destined for channel 1, not the slot's current channel.
            synth.SetChannelPatch(1, new SamplePatch(BuildLongReleaseDcRegion(parkedValue, 4096), SampleRate));
            synth.NoteOn(1, 61, 127);

            // Silencing channel 1 -- the PARKED note's target channel, NOT the slot's current victim
            // channel (0) -- must still cancel the parked note (under-cancel would skip this slot entirely
            // because slot.Channel == 0 != 1, letting the note resurrect once the victim's fade completes).
            synth.SilenceChannel(1);
            OfflineRenderer.Render(synth, sink, PostSilenceFrames);

            float level = TrailingMeanAbs(sink.ToArray(), MeasureWindowFrames);
            TestContext.WriteLine($"Trailing level after SilenceChannel(1) cancels a parked note behind " +
                                   $"channel 0's fading victim: {level:F6}.");
            Assert.That(level, Is.LessThan(0.05f),
                "SilenceChannel(1) must cancel channel 1's parked note even though it sits behind channel " +
                "0's fading voice; the note must not resurrect once that fade completes.");
        }
    }
}
