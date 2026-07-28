using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Deliverable-proof tests for the sustain pedal seam (DiVoid #7155, design #7179): asset-free,
    /// synth-level proof — mirroring <see cref="ReverbSendRoutingTests"/> — that a channel's sustain
    /// state (<see cref="Synthesizer.SetChannelSustain"/>) defers <see cref="Synthesizer.NoteOff"/>
    /// while held, releases every deferred voice on pedal-up, leaves a physically-held note (no
    /// NoteOff) untouched by a pedal cycle, reproduces the unchanged immediate-release behaviour when
    /// sustain is never engaged, and correctly resets a reused pool slot's deferred-release marker.
    /// </summary>
    [TestFixture]
    public class SynthesizerSustainTests {

        const int SampleRate = 44100;

        /// <summary>Frames rendered before any NoteOff so the DAHDSR envelope fully settles to its sustain level.</summary>
        const int SettleFrames = 500;

        /// <summary>
        /// Frames rendered after a NoteOff/pedal-up to observe the outcome; well beyond the SF2-default
        /// release tail (≈43 samples at 44.1kHz), so a released voice has fully decayed by the end of it.
        /// </summary>
        const int PostEventFrames = 400;

        /// <summary>Trailing window, in frames, averaged to measure "is this voice still sounding".</summary>
        const int MeasureWindowFrames = 50;

        /// <summary>
        /// Mirrors <c>Synthesizer.MasterHeadroomTrim</c> (DiVoid BUG #7212, design #7213): every render goes
        /// through the master bus, so "still sounding" assertions divide the measured level by this factor
        /// to recover the pre-trim level before comparing against the raw settled DC value.
        /// </summary>
        const float MasterHeadroomTrim = 0.5f;

        static SynthesizerOptions MonoOptions(int maxVoices = 4) => new SynthesizerOptions(SampleRate, 1, 64, maxVoices);

        static SampleRegion BuildSustainedDcRegion(float value, int length) {
            float[] buf = new float[length];
            for (int i = 0; i < length; i++)
                buf[i] = value;
            return new SampleRegion(buf, 0, length, 0, length, LoopMode.Continuous, SampleRate, 60, 0,
                EnvelopeParameters.Default, FilterParameters.Default, LfoParameters.Default, 0f);
        }

        static SampleRegion BuildOneShotDcRegion(float value, int length) {
            float[] buf = new float[length];
            for (int i = 0; i < length; i++)
                buf[i] = value;
            return new SampleRegion(buf, 0, length, 0, length, LoopMode.NoLoop, SampleRate, 60, 0,
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
        [Description("Deliverable core (design §6/§14): while a channel's sustain pedal is held, a NoteOff " +
                     "defers the voice's release instead of releasing it — the voice keeps sounding at its " +
                     "settled level well past where an immediate release would have decayed it to silence.")]
        public void NoteOff_WhileSustainHeld_DefersReleaseAndVoiceKeepsSounding() {
            const float value = 0.3f;
            SynthesizerOptions options = MonoOptions();
            Synthesizer synth = new Synthesizer(options, new SamplePatch(BuildSustainedDcRegion(value, 1024), SampleRate));
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.SetChannelSustain(0, true);
            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, SettleFrames);

            synth.NoteOff(0, 60);
            OfflineRenderer.Render(synth, sink, PostEventFrames);

            float level = TrailingMeanAbs(sink.ToArray(), MeasureWindowFrames) / MasterHeadroomTrim;
            TestContext.WriteLine($"Trailing level after deferred NoteOff: {level:F6} (settled value {value}).");
            Assert.That(level, Is.GreaterThan(value * 0.9f),
                "a NoteOff received while the pedal is held must defer release; the voice must still be " +
                "sounding at essentially its settled level.");
        }

        [Test]
        [Description("Deliverable core (design §6/§14): disengaging the sustain pedal releases every voice " +
                     "deferred since it went down, decaying it to silence through its normal release tail.")]
        public void SetChannelSustain_PedalUp_ReleasesDeferredVoice() {
            const float value = 0.3f;
            SynthesizerOptions options = MonoOptions();
            Synthesizer synth = new Synthesizer(options, new SamplePatch(BuildSustainedDcRegion(value, 1024), SampleRate));
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.SetChannelSustain(0, true);
            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, SettleFrames);

            synth.NoteOff(0, 60);
            OfflineRenderer.Render(synth, sink, 10);

            synth.SetChannelSustain(0, false);
            OfflineRenderer.Render(synth, sink, PostEventFrames);

            float level = TrailingMeanAbs(sink.ToArray(), MeasureWindowFrames);
            TestContext.WriteLine($"Trailing level after pedal-up: {level:F6}.");
            Assert.That(level, Is.LessThan(value * 0.05f),
                "lifting the pedal must release every voice deferred since pedal-down, decaying it to " +
                "near-silence through its release tail.");
        }

        [Test]
        [Description("Control (bit-for-bit precedent, design §9): a channel that never engages sustain takes " +
                     "the unchanged immediate-release path — NoteOff decays the voice to silence directly.")]
        public void NoteOff_SustainNeverEngaged_ReleasesImmediately() {
            const float value = 0.3f;
            SynthesizerOptions options = MonoOptions();
            Synthesizer synth = new Synthesizer(options, new SamplePatch(BuildSustainedDcRegion(value, 1024), SampleRate));
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, SettleFrames);

            synth.NoteOff(0, 60);
            OfflineRenderer.Render(synth, sink, PostEventFrames);

            float level = TrailingMeanAbs(sink.ToArray(), MeasureWindowFrames);
            TestContext.WriteLine($"Trailing level with sustain never engaged: {level:F6}.");
            Assert.That(level, Is.LessThan(value * 0.05f),
                "with sustain never engaged, NoteOff must release the voice immediately, exactly as before " +
                "this feature.");
        }

        [Test]
        [Description("Bit-identical regression (design §9): a channel that never receives a CC64 event — so " +
                     "SetChannelSustain is never called at all — must render exactly like one where the " +
                     "neutral disengaged state (held: false) was explicitly set, proving the added sustain " +
                     "machinery is a true no-op when unused, matching the pre-feature unconditional-Release " +
                     "path bit-for-bit.")]
        public void NoCc64_NeverCallingSetChannelSustain_RendersBitIdenticalToExplicitDisengage() {
            const float value = 0.3f;

            SynthesizerOptions untouchedOptions = MonoOptions();
            Synthesizer untouched = new Synthesizer(untouchedOptions, new SamplePatch(BuildSustainedDcRegion(value, 1024), SampleRate));
            InMemoryAudioSink untouchedSink = new InMemoryAudioSink(untouched.Format);
            untouched.NoteOn(0, 60, 127);
            OfflineRenderer.Render(untouched, untouchedSink, SettleFrames);
            untouched.NoteOff(0, 60);
            OfflineRenderer.Render(untouched, untouchedSink, PostEventFrames);

            SynthesizerOptions explicitOptions = MonoOptions();
            Synthesizer explicitlyDisengaged = new Synthesizer(explicitOptions, new SamplePatch(BuildSustainedDcRegion(value, 1024), SampleRate));
            InMemoryAudioSink explicitSink = new InMemoryAudioSink(explicitlyDisengaged.Format);
            explicitlyDisengaged.SetChannelSustain(0, false);
            explicitlyDisengaged.NoteOn(0, 60, 127);
            OfflineRenderer.Render(explicitlyDisengaged, explicitSink, SettleFrames);
            explicitlyDisengaged.NoteOff(0, 60);
            OfflineRenderer.Render(explicitlyDisengaged, explicitSink, PostEventFrames);

            Assert.That(explicitSink.ToArray(), Is.EqualTo(untouchedSink.ToArray()),
                "never touching SetChannelSustain must render bit-identically to explicitly disengaging it, " +
                "since the ctor default (false) is already neutral.");
        }

        [Test]
        [Description("Risk mitigation (design §11): a note that never receives a NoteOff is unaffected by a " +
                     "full pedal-down/pedal-up cycle — it keeps sounding at its settled level.")]
        public void PhysicallyHeldNote_PedalCycleWithNoNoteOff_IsNotReleased() {
            const float value = 0.3f;
            SynthesizerOptions options = MonoOptions();
            Synthesizer synth = new Synthesizer(options, new SamplePatch(BuildSustainedDcRegion(value, 1024), SampleRate));
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, SettleFrames);

            synth.SetChannelSustain(0, true);
            OfflineRenderer.Render(synth, sink, 10);
            synth.SetChannelSustain(0, false);
            OfflineRenderer.Render(synth, sink, PostEventFrames);

            float level = TrailingMeanAbs(sink.ToArray(), MeasureWindowFrames) / MasterHeadroomTrim;
            TestContext.WriteLine($"Trailing level for a physically-held note after a pedal cycle: {level:F6}.");
            Assert.That(level, Is.GreaterThan(value * 0.9f),
                "a note that never received a NoteOff must keep sounding through an unrelated pedal cycle.");
        }

        [Test]
        [Description("Risk mitigation / slot-reuse regression (design §11/§14): a deferred-release marker must " +
                     "not survive its slot being reclaimed and reallocated to a new note. With a one-voice pool, " +
                     "a first note gets a deferred NoteOff (sustain held) and then exhausts its one-shot buffer " +
                     "naturally, without Release() ever being called, freeing its slot with a stale " +
                     "PendingRelease=true; a second note reuses that exact slot, which NoteOn must reset — so " +
                     "pedal-up must not incorrectly release the freshly-started voice.")]
        public void SetChannelSustain_PedalUp_DoesNotReleaseUnrelatedVoiceInReusedSlot() {
            const float heldValue = 0.4f;
            SynthesizerOptions options = MonoOptions(maxVoices: 1);

            SampleRegion oneShot = BuildOneShotDcRegion(0.2f, 200);
            Synthesizer synth = new Synthesizer(options, new SamplePatch(oneShot, SampleRate));
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.SetChannelSustain(0, true);
            synth.NoteOn(0, 60, 127);
            synth.NoteOff(0, 60);
            OfflineRenderer.Render(synth, sink, 400);

            synth.SetChannelPatch(0, new SamplePatch(BuildSustainedDcRegion(heldValue, 1024), SampleRate));
            synth.NoteOn(0, 61, 127);
            OfflineRenderer.Render(synth, sink, SettleFrames);

            synth.SetChannelSustain(0, false);
            OfflineRenderer.Render(synth, sink, PostEventFrames);

            float level = TrailingMeanAbs(sink.ToArray(), MeasureWindowFrames) / MasterHeadroomTrim;
            TestContext.WriteLine($"Trailing level for the reused-slot voice after pedal-up: {level:F6}.");
            Assert.That(level, Is.GreaterThan(heldValue * 0.9f),
                "pedal-up must not release a new voice that reused a slot whose stale PendingRelease marker " +
                "was not reset at NoteOn.");
        }

        [TestCase(-1)]
        [TestCase(16)]
        [Description("SetChannelSustain rejects a channel outside [0,15], mirroring SetChannelPan/SetChannelReverbSend.")]
        public void SetChannelSustain_ChannelOutOfRange_Throws(int channel) {
            SynthesizerOptions options = MonoOptions();
            Synthesizer synth = new Synthesizer(options, new SamplePatch(BuildSustainedDcRegion(0.1f, 64), SampleRate));

            Assert.Throws<ArgumentOutOfRangeException>(() => synth.SetChannelSustain(channel, true));
        }
    }
}
