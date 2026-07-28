using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Deliverable-proof tests for voice-stealing (DiVoid #7183, design #7200): when the voice pool is
    /// full, <see cref="Synthesizer.NoteOn"/> must reclaim ("steal") the best-candidate slot instead of
    /// dropping the note, the reclaim must be click-free, the victim must be chosen by the documented
    /// <c>(releasedTier, currentGain, age)</c> policy, and a render that never exceeds <c>MaxVoices</c>
    /// must be unaffected by any of this new bookkeeping.
    /// </summary>
    [TestFixture]
    public class VoiceStealingTests {

        const int SampleRate = 44100;

        /// <summary>Frames rendered before any steal-triggering event so the DAHDSR envelope fully settles.</summary>
        const int SettleFrames = 500;

        /// <summary>
        /// Frames rendered after a steal-triggering NoteOn: long enough for the victim's ~5 ms
        /// <see cref="GainRamp"/> fade-out, up to one render block of latency, and the incoming note's own
        /// DAHDSR attack to fully settle.
        /// </summary>
        const int StealSettleFrames = 800;

        /// <summary>Trailing window, in frames, averaged to measure "what is sounding right now".</summary>
        const int MeasureWindowFrames = 50;

        static SynthesizerOptions MonoOptions(int maxVoices) => new SynthesizerOptions(SampleRate, 1, 64, maxVoices);

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
        [Description("Steal-not-drop (design §14): with the pool full, one more NoteOn reclaims a slot and " +
                     "the new note sounds, instead of being silently dropped as FindFreeSlot()<0 used to.")]
        public void NoteOn_PoolFull_StealsVictimInsteadOfDropping() {
            const int maxVoices = 4;
            const float heldValue = 0.1f;
            const float stolenNoteValue = 0.5f;

            SynthesizerOptions options = MonoOptions(maxVoices);
            Synthesizer synth = new Synthesizer(options, new SamplePatch(BuildSustainedDcRegion(heldValue, 1024), SampleRate));
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            for (int channel = 0; channel < maxVoices; channel++) {
                synth.SetChannelPatch(channel, new SamplePatch(BuildSustainedDcRegion(heldValue, 1024), SampleRate));
                synth.NoteOn(channel, 60, 127);
            }
            OfflineRenderer.Render(synth, sink, SettleFrames);

            synth.SetChannelPatch(maxVoices, new SamplePatch(BuildSustainedDcRegion(stolenNoteValue, 1024), SampleRate));
            synth.NoteOn(maxVoices, 60, 127);
            OfflineRenderer.Render(synth, sink, StealSettleFrames);

            float level = TrailingMeanAbs(sink.ToArray(), MeasureWindowFrames);
            float noStealLevel = maxVoices * heldValue;
            TestContext.WriteLine($"Trailing level after the 5th NoteOn past a full pool: {level:F6} " +
                                   $"(no-steal baseline would stay at {noStealLevel:F6}).");
            Assert.That(level, Is.GreaterThan(noStealLevel + stolenNoteValue * 0.5f),
                "with the pool full, the extra NoteOn must steal a slot and sound rather than being dropped.");
        }

        [Test]
        [Description("Victim policy (design §8.3): a released voice is reclaimed before any still-held, " +
                     "sounding voice — releasedTier outranks currentGain in the victim comparator.")]
        public void NoteOn_PoolFull_OneVoiceReleased_ReclaimsReleasedVoiceNotHeldOne() {
            const int maxVoices = 4;
            const float heldValue = 0.15f;
            const float newValue = 0.3f;

            SynthesizerOptions options = MonoOptions(maxVoices);
            Synthesizer synth = new Synthesizer(options, new SamplePatch(BuildSustainedDcRegion(heldValue, 1024), SampleRate));
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            for (int channel = 0; channel < maxVoices; channel++) {
                synth.SetChannelPatch(channel, new SamplePatch(BuildSustainedDcRegion(heldValue, 1024), SampleRate));
                synth.NoteOn(channel, 60, 127);
            }
            OfflineRenderer.Render(synth, sink, SettleFrames);

            synth.NoteOff(0, 60);

            synth.SetChannelPatch(maxVoices, new SamplePatch(BuildSustainedDcRegion(newValue, 1024), SampleRate));
            synth.NoteOn(maxVoices, 60, 127);
            OfflineRenderer.Render(synth, sink, StealSettleFrames);

            float level = TrailingMeanAbs(sink.ToArray(), MeasureWindowFrames);
            float correctVictimLevel = (maxVoices - 1) * heldValue + newValue;
            float wrongVictimLevel = (maxVoices - 2) * heldValue + newValue;
            TestContext.WriteLine($"Trailing level: {level:F6} (released-victim expectation {correctVictimLevel:F6}, " +
                                   $"wrong-victim expectation {wrongVictimLevel:F6}).");
            Assert.That(level, Is.GreaterThan((correctVictimLevel + wrongVictimLevel) / 2f),
                "the released voice must be the one reclaimed, leaving all three still-held loud voices " +
                "sounding alongside the new note.");
        }

        [Test]
        [Description("Victim policy (design §8.3): with none of the pool released, the quietest sounding " +
                     "voice is reclaimed before louder ones.")]
        public void NoteOn_PoolFull_NoneReleased_ReclaimsQuietestVoiceNotLoudOnes() {
            const int maxVoices = 4;
            const float quietValue = 0.05f;
            const float loudValue = 0.2f;
            const float newValue = 0.15f;

            SynthesizerOptions options = MonoOptions(maxVoices);
            Synthesizer synth = new Synthesizer(options, new SamplePatch(BuildSustainedDcRegion(loudValue, 1024), SampleRate));
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.SetChannelPatch(0, new SamplePatch(BuildSustainedDcRegion(quietValue, 1024), SampleRate));
            synth.NoteOn(0, 60, 127);
            for (int channel = 1; channel < maxVoices; channel++) {
                synth.SetChannelPatch(channel, new SamplePatch(BuildSustainedDcRegion(loudValue, 1024), SampleRate));
                synth.NoteOn(channel, 60, 127);
            }
            OfflineRenderer.Render(synth, sink, SettleFrames);

            synth.SetChannelPatch(maxVoices, new SamplePatch(BuildSustainedDcRegion(newValue, 1024), SampleRate));
            synth.NoteOn(maxVoices, 60, 127);
            OfflineRenderer.Render(synth, sink, StealSettleFrames);

            float level = TrailingMeanAbs(sink.ToArray(), MeasureWindowFrames);
            float correctVictimLevel = (maxVoices - 1) * loudValue + newValue;
            float wrongVictimLevel = quietValue + (maxVoices - 2) * loudValue + newValue;
            TestContext.WriteLine($"Trailing level: {level:F6} (quietest-victim expectation {correctVictimLevel:F6}, " +
                                   $"wrong-victim expectation {wrongVictimLevel:F6}).");
            Assert.That(level, Is.GreaterThan((correctVictimLevel + wrongVictimLevel) / 2f),
                "the quietest voice must be the one reclaimed, leaving the three loud voices sounding " +
                "alongside the new note.");
        }

        [Test]
        [Description("Victim policy (design §8.3): among equally-loud, equally-unreleased voices, the " +
                     "oldest is reclaimed — the age tiebreak. All four held voices are started at the same " +
                     "velocity so their settled currentGain is bit-identical, isolating the age comparator.")]
        public void NoteOn_PoolFull_EqualGainNoneReleased_ReclaimsOldestVoice() {
            const int maxVoices = 4;
            float[] heldValues = { 0.05f, 0.08f, 0.11f, 0.14f };
            const float newValue = 0.20f;

            SynthesizerOptions options = MonoOptions(maxVoices);
            Synthesizer synth = new Synthesizer(options, new SamplePatch(BuildSustainedDcRegion(heldValues[0], 1024), SampleRate));
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            for (int channel = 0; channel < maxVoices; channel++) {
                synth.SetChannelPatch(channel, new SamplePatch(BuildSustainedDcRegion(heldValues[channel], 1024), SampleRate));
                synth.NoteOn(channel, 60, 127);
            }
            OfflineRenderer.Render(synth, sink, SettleFrames);

            synth.SetChannelPatch(maxVoices, new SamplePatch(BuildSustainedDcRegion(newValue, 1024), SampleRate));
            synth.NoteOn(maxVoices, 60, 127);
            OfflineRenderer.Render(synth, sink, StealSettleFrames);

            float level = TrailingMeanAbs(sink.ToArray(), MeasureWindowFrames);
            float oldestReclaimedLevel = heldValues[1] + heldValues[2] + heldValues[3] + newValue;
            float newestReclaimedLevel = heldValues[0] + heldValues[1] + heldValues[2] + newValue;
            TestContext.WriteLine($"Trailing level: {level:F6} (oldest-reclaimed expectation {oldestReclaimedLevel:F6}, " +
                                   $"newest-reclaimed expectation {newestReclaimedLevel:F6}).");
            Assert.That(level, Is.GreaterThan((oldestReclaimedLevel + newestReclaimedLevel) / 2f),
                "with every candidate's currentGain tied, the oldest voice (the first NoteOn) must be the " +
                "one reclaimed, not the newest.");
        }

        [Test]
        [Description("No click (design §9, INV-1): forcing a steal across a full loud pool never introduces " +
                     "a large sample-to-sample discontinuity — both the outgoing fade and the incoming " +
                     "note's fresh onset stay ramp-limited.")]
        public void NoteOn_PoolFull_ForcedSteal_HasNoLargeSampleToSampleDiscontinuity() {
            const int maxVoices = 4;
            const float loudValue = 0.6f;
            const float newValue = 0.6f;

            // A real click (an instantaneous slot swap) would jump by close to a full voice's DC amplitude
            // in a single sample; this threshold is comfortably above the bounded per-frame ramp step
            // (~1/220 of the ramp's own gain range, times a single voice's amplitude) and comfortably below
            // that jump, so it discriminates a click without being sensitive to floating-point noise.
            const float maxAllowedDelta = 0.05f;

            SynthesizerOptions options = MonoOptions(maxVoices);
            Synthesizer synth = new Synthesizer(options, new SamplePatch(BuildSustainedDcRegion(loudValue, 4096), SampleRate));
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            for (int channel = 0; channel < maxVoices; channel++) {
                synth.SetChannelPatch(channel, new SamplePatch(BuildSustainedDcRegion(loudValue, 4096), SampleRate));
                synth.NoteOn(channel, 60, 127);
            }
            OfflineRenderer.Render(synth, sink, SettleFrames);

            synth.SetChannelPatch(maxVoices, new SamplePatch(BuildSustainedDcRegion(newValue, 4096), SampleRate));
            synth.NoteOn(maxVoices, 60, 127);
            OfflineRenderer.Render(synth, sink, StealSettleFrames);

            float[] samples = sink.ToArray();
            float maxDelta = 0f;
            for (int i = 1; i < samples.Length; i++)
                maxDelta = Math.Max(maxDelta, Math.Abs(samples[i] - samples[i - 1]));

            TestContext.WriteLine($"Max sample-to-sample |delta| across the forced steal: {maxDelta:F6}.");
            Assert.That(maxDelta, Is.LessThan(maxAllowedDelta),
                "a click would show as a large sample-to-sample jump at the steal boundary; the victim's " +
                "fade-out and the new note's fade-in must both stay ramp-limited.");
        }

        [Test]
        [Description("Bit-identical regression (design §10/§14, success criterion 4): a render that never " +
                     "exceeds MaxVoices never enters the steal path, so the same note sequence rendered " +
                     "under a spacious pool and under a pool sized exactly to the concurrent voice count " +
                     "(still never full) must be bit-for-bit identical — the new age/released/pending-note " +
                     "bookkeeping is inert dead state outside the full-pool path.")]
        public void NoteOn_SubCapacityRender_IsBitIdenticalRegardlessOfPoolSize() {
            SynthesizerOptions spaciousOptions = MonoOptions(maxVoices: 32);
            Synthesizer spacious = new Synthesizer(spaciousOptions, new SamplePatch(BuildSustainedDcRegion(0.3f, 1024), SampleRate));
            InMemoryAudioSink spaciousSink = new InMemoryAudioSink(spacious.Format);
            RenderThreeConcurrentNotesWithOneRelease(spacious, spaciousSink);

            SynthesizerOptions exactOptions = MonoOptions(maxVoices: 3);
            Synthesizer exact = new Synthesizer(exactOptions, new SamplePatch(BuildSustainedDcRegion(0.3f, 1024), SampleRate));
            InMemoryAudioSink exactSink = new InMemoryAudioSink(exact.Format);
            RenderThreeConcurrentNotesWithOneRelease(exact, exactSink);

            Assert.That(exactSink.ToArray(), Is.EqualTo(spaciousSink.ToArray()),
                "a render that never exceeds the pool must be bit-identical regardless of how much slack " +
                "the pool has, proving the voice-stealing bookkeeping never perturbs the non-full render path.");
        }

        static void RenderThreeConcurrentNotesWithOneRelease(Synthesizer synth, InMemoryAudioSink sink) {
            for (int channel = 0; channel < 3; channel++) {
                synth.SetChannelPatch(channel, new SamplePatch(BuildSustainedDcRegion(0.3f, 1024), SampleRate));
                synth.NoteOn(channel, 60 + channel, 127);
            }
            OfflineRenderer.Render(synth, sink, SettleFrames);

            synth.NoteOff(0, 60);
            OfflineRenderer.Render(synth, sink, StealSettleFrames);
        }
    }
}
