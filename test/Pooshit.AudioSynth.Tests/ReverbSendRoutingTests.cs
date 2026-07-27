using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Deliverable-proof tests for per-channel reverb send routing (DiVoid #7165, design #7170 §9.3/§14.10
    /// revised): asset-free, synth-level proof that the channel send (CC91) and the region send (gen-16)
    /// combine additively and clamp at 1.0 — neither alone being zero silences the other's contribution,
    /// only both together do — that an all-zero send bus reproduces the reverb-absent render bit-for-bit,
    /// and that <see cref="SynthesizerOptions.GlobalReverb"/> reproduces the pre-send-bus uniform
    /// master-insert bit-for-bit.
    /// </summary>
    [TestFixture]
    public class ReverbSendRoutingTests {

        const int SampleRate = 44100;

        /// <summary>
        /// Long enough that the SF2-default volume envelope (delay+attack+hold+decay, each
        /// <see cref="EnvelopeParameters.Sf2DefaultTimeSeconds"/> ≈ 1 ms) and both gain ramps (channel
        /// and voice, 5 ms each) fully settle to an audible sustain level before the one-shot sample runs
        /// out — a too-short note would exhaust while still silently ramping up, making the test measure
        /// nothing regardless of routing.
        /// </summary>
        const int NoteFrames = 4410;

        const int TailFrames = 8000;

        static SampleRegion BuildOneShotDcRegion(float value, int length, float reverbSend = 0f) {
            float[] buf = new float[length];
            for (int i = 0; i < length; i++)
                buf[i] = value;
            return new SampleRegion(buf, 0, length, 0, length, LoopMode.NoLoop, SampleRate, 60, 0,
                EnvelopeParameters.Default, FilterParameters.Default, LfoParameters.Default, 0f, reverbSend);
        }

        static SampleRegion BuildSustainedDcRegion(float value, int length, float reverbSend = 0f) {
            float[] buf = new float[length];
            for (int i = 0; i < length; i++)
                buf[i] = value;
            return new SampleRegion(buf, 0, length, 0, length, LoopMode.Continuous, SampleRate, 60, 0,
                EnvelopeParameters.Default, FilterParameters.Default, LfoParameters.Default, 0f, reverbSend);
        }

        /// <summary>
        /// Renders a one-shot note with gen-16 (region <see cref="SampleRegion.ReverbSend"/>) held at 0, so
        /// the reverb tail is driven by <paramref name="channelSend"/> (CC91) alone — isolating the channel
        /// send's contribution to the additive/clamped combination (design #7170 §9.3 revised).
        /// </summary>
        static float[] RenderOneShotWithChannelSend(float channelSend) {
            SynthesizerOptions options = new SynthesizerOptions(
                SampleRate, 2, 64, 4, new ReverbSettings(roomSize: 0.9f, damping: 0.3f, wet: 1f, width: 1f));
            SampleRegion region = BuildOneShotDcRegion(0.5f, NoteFrames);
            SamplePatch patch = new SamplePatch(region, options.SampleRate);
            Synthesizer synth = new Synthesizer(options, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.SetChannelReverbSend(0, channelSend);
            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, NoteFrames + TailFrames);
            return sink.ToArray();
        }

        /// <summary>
        /// Renders a one-shot note with independently-chosen channel send (CC91) and region send (gen-16),
        /// so the additive/clamped combination <c>clamp01(channelSend + regionSend)</c> can be probed
        /// directly (design #7170 §9.3 revised).
        /// </summary>
        static float[] RenderOneShotWithSends(float channelSend, float regionSend) {
            SynthesizerOptions options = new SynthesizerOptions(
                SampleRate, 2, 64, 4, new ReverbSettings(roomSize: 0.9f, damping: 0.3f, wet: 1f, width: 1f));
            SampleRegion region = BuildOneShotDcRegion(0.5f, NoteFrames, regionSend);
            SamplePatch patch = new SamplePatch(region, options.SampleRate);
            Synthesizer synth = new Synthesizer(options, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.SetChannelReverbSend(0, channelSend);
            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, NoteFrames + TailFrames);
            return sink.ToArray();
        }

        static float TailRms(float[] samples, int channels, int tailFrames) {
            int windowSamples = Math.Min(samples.Length, tailFrames * channels);
            int start = samples.Length - windowSamples;
            double sum = 0.0;
            for (int i = start; i < samples.Length; i++)
                sum += (double)samples[i] * samples[i];
            return (float)Math.Sqrt(sum / windowSamples);
        }

        [Test]
        [Description("Deliverable core (design §14.10): a channel with reverb send=1.0 carries an audible tail " +
                     "after its one-shot note ends, while the identical note on a channel with send=0.0 decays " +
                     "to exact silence — proving the per-channel send weight independently gates reverb routing.")]
        public void ChannelReverbSend_GatesWhetherChannelReachesReverb() {
            float[] sendOne = RenderOneShotWithChannelSend(1.0f);
            float[] sendZero = RenderOneShotWithChannelSend(0.0f);

            float tailRmsSendOne = TailRms(sendOne, channels: 2, TailFrames);
            float tailRmsSendZero = TailRms(sendZero, channels: 2, TailFrames);

            TestContext.WriteLine($"Tail RMS (send=1.0): {tailRmsSendOne:F6}; Tail RMS (send=0.0): {tailRmsSendZero:F6}.");

            Assert.That(tailRmsSendZero, Is.EqualTo(0f),
                "a channel with reverb send=0 must decay to exact silence once its dry note ends " +
                "(it never contributed to the send bus).");
            Assert.That(tailRmsSendOne, Is.GreaterThan(0f),
                "a channel with reverb send=1.0 must carry an audible reverb tail after its dry note ends.");
        }

        [Test]
        [Description("Additive-combination core (design §9.3/§14.10 revised) — this is exactly the regression " +
                     "the multiplicative first cut failed: a channel with CC91>0 and a region with gen-16=0 " +
                     "(the Florestan case — every probed region sets an explicit 0) must still reach the " +
                     "reverb, because the channel's send is not multiplied away by the region's absent bias.")]
        public void AdditiveCombination_ChannelSendWithZeroRegionSend_StillReachesReverb() {
            float[] samples = RenderOneShotWithSends(channelSend: 0.6f, regionSend: 0f);
            float tailRms = TailRms(samples, channels: 2, TailFrames);

            TestContext.WriteLine($"Tail RMS (CC91=0.6, gen-16=0): {tailRms:F6}.");
            Assert.That(tailRms, Is.GreaterThan(0f),
                "CC91>0 with gen-16=0 (the Florestan case) must still carry an audible reverb tail.");
        }

        [Test]
        [Description("A channel with CC91=0 and a region with gen-16>0 must still reach the reverb — the " +
                     "channel's zero send must not zero out the region's own additive per-instrument bias.")]
        public void AdditiveCombination_RegionSendWithZeroChannelSend_StillReachesReverb() {
            float[] samples = RenderOneShotWithSends(channelSend: 0f, regionSend: 0.6f);
            float tailRms = TailRms(samples, channels: 2, TailFrames);

            TestContext.WriteLine($"Tail RMS (CC91=0, gen-16=0.6): {tailRms:F6}.");
            Assert.That(tailRms, Is.GreaterThan(0f),
                "gen-16>0 with CC91=0 must still carry an audible reverb tail.");
        }

        [Test]
        [Description("Only when BOTH the channel send (CC91) and the region send (gen-16) are exactly 0 does " +
                     "the additive combination clamp01(0+0)=0 leave the voice fully dry.")]
        public void AdditiveCombination_BothSendsZero_IsDry() {
            float[] samples = RenderOneShotWithSends(channelSend: 0f, regionSend: 0f);
            float tailRms = TailRms(samples, channels: 2, TailFrames);

            Assert.That(tailRms, Is.EqualTo(0f),
                "both channel send and region send at 0 must decay to exact silence.");
        }

        [Test]
        [Description("CC91 near-full plus a non-zero gen-16 must clamp the combined send at 1.0 rather than " +
                     "overdriving past it: a (0.9, 0.5) pair (raw sum 1.4) must render bit-identically to an " +
                     "already-saturated (1.0, 0.0) pair, proving the clamp actually bounds the sum.")]
        public void AdditiveCombination_SendsSummingAboveOne_ClampAtOne() {
            float[] overOne = RenderOneShotWithSends(channelSend: 0.9f, regionSend: 0.5f);
            float[] saturated = RenderOneShotWithSends(channelSend: 1f, regionSend: 0f);

            Assert.That(overOne, Is.EqualTo(saturated),
                "a combined send summing above 1.0 must clamp identically to an already-saturated full send.");
        }

        [Test]
        [Description("Every channel send (CC91) AND every region send (gen-16) truly at 0, per-channel mode " +
                     "(the default), must render bit-identically to reverb being entirely absent: the additive " +
                     "combination clamp01(0+0)=0, so the send bus contributes nothing and the master path is " +
                     "untouched (regression, design §14.10 revised).")]
        public void AllChannelAndRegionSendsZero_PerChannelMode_RendersBitIdenticalToReverbAbsent() {
            SynthesizerOptions dryOptions = new SynthesizerOptions(SampleRate, 2, 64, 8, reverb: null);
            SynthesizerOptions wetOptions = new SynthesizerOptions(SampleRate, 2, 64, 8, reverb: ReverbSettings.Default);

            SampleRegion dryRegion = BuildSustainedDcRegion(0.3f, 4096, reverbSend: 0f);
            Synthesizer dry = new Synthesizer(dryOptions, new SamplePatch(dryRegion, dryOptions.SampleRate));
            InMemoryAudioSink drySink = new InMemoryAudioSink(dry.Format);
            dry.NoteOn(0, 60, 100);
            OfflineRenderer.Render(dry, drySink, 4096);

            SampleRegion wetRegion = BuildSustainedDcRegion(0.3f, 4096, reverbSend: 0f);
            Synthesizer wet = new Synthesizer(wetOptions, new SamplePatch(wetRegion, wetOptions.SampleRate));
            InMemoryAudioSink wetSink = new InMemoryAudioSink(wet.Format);
            wet.SetChannelReverbSend(0, 0f);
            wet.NoteOn(0, 60, 100);
            OfflineRenderer.Render(wet, wetSink, 4096);

            Assert.That(wetSink.ToArray(), Is.EqualTo(drySink.ToArray()),
                "an all-zero channel send AND all-zero region send must reproduce the reverb-absent render bit-for-bit.");
        }

        [Test]
        [Description("GlobalReverb=true must render bit-identically to the per-channel mode with every channel " +
                     "send forced to 1.0 (region gen-16=0), proving the master insert is exactly the special " +
                     "case where every voice sends fully (design §4/§14.10 revised).")]
        public void GlobalReverbTrue_ReproducesPerChannelAllSendsOne_BitIdentically() {
            SynthesizerOptions perChannelOptions = new SynthesizerOptions(
                SampleRate, 2, 64, 8, reverb: ReverbSettings.Default, globalReverb: false);
            SynthesizerOptions globalOptions = new SynthesizerOptions(
                SampleRate, 2, 64, 8, reverb: ReverbSettings.Default, globalReverb: true);

            SampleRegion perChannelRegion = BuildSustainedDcRegion(0.3f, 4096, reverbSend: 0f);
            Synthesizer perChannel = new Synthesizer(perChannelOptions, new SamplePatch(perChannelRegion, perChannelOptions.SampleRate));
            InMemoryAudioSink perChannelSink = new InMemoryAudioSink(perChannel.Format);
            perChannel.SetChannelReverbSend(0, 1f);
            perChannel.NoteOn(0, 60, 100);
            OfflineRenderer.Render(perChannel, perChannelSink, 4096);

            SampleRegion globalRegion = BuildSustainedDcRegion(0.3f, 4096, reverbSend: 0f);
            Synthesizer global = new Synthesizer(globalOptions, new SamplePatch(globalRegion, globalOptions.SampleRate));
            InMemoryAudioSink globalSink = new InMemoryAudioSink(global.Format);
            global.NoteOn(0, 60, 100);
            OfflineRenderer.Render(global, globalSink, 4096);

            float[] perChannelSamples = perChannelSink.ToArray();
            float[] globalSamples = globalSink.ToArray();

            Assert.That(globalSamples, Is.EqualTo(perChannelSamples),
                "GlobalReverb=true must reproduce the per-channel-all-sends-1.0 render bit-for-bit.");

            float dryPeak = 0f;
            foreach (float s in globalSamples)
                dryPeak = Math.Max(dryPeak, Math.Abs(s));
            Assert.That(dryPeak, Is.GreaterThan(0f), "the global-reverb render must produce non-silent audio.");
        }

        [TestCase(-1)]
        [TestCase(16)]
        [Description("SetChannelReverbSend rejects a channel outside [0,15], mirroring SetChannelPan.")]
        public void SetChannelReverbSend_ChannelOutOfRange_Throws(int channel) {
            SynthesizerOptions options = new SynthesizerOptions(SampleRate, 2, 64, 8);
            Synthesizer synth = new Synthesizer(options, new SamplePatch(BuildSustainedDcRegion(0.1f, 64), SampleRate));

            Assert.Throws<ArgumentOutOfRangeException>(() => synth.SetChannelReverbSend(channel, 1f));
        }
    }
}
