using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Deliverable-proof tests for per-channel chorus send routing (DiVoid #7188, design #7190 §8): asset-free,
    /// synth-level proof that the channel send (CC93) and the region send (gen-15) combine additively and
    /// clamp at 1.0 — neither alone being zero silences the other's contribution, only both together do —
    /// that an all-zero send bus reproduces the chorus-absent render bit-for-bit, and that
    /// <see cref="SynthesizerOptions.GlobalChorus"/> reproduces the pre-send-bus uniform master-insert
    /// bit-for-bit. Mirrors <see cref="ReverbSendRoutingTests"/>.
    /// </summary>
    [TestFixture]
    public class ChorusSendRoutingTests {

        const int SampleRate = 44100;

        /// <summary>
        /// Long enough that the SF2-default volume envelope and both gain ramps (channel and voice, 5 ms
        /// each) fully settle to an audible sustain level before the one-shot sample runs out.
        /// </summary>
        const int NoteFrames = 4410;

        const int TailFrames = 8000;

        static ChorusSettings BuildStrongChorusSettings() => new ChorusSettings(wet: 1f);

        static SampleRegion BuildOneShotDcRegion(float value, int length, float chorusSend = 0f) {
            float[] buf = new float[length];
            for (int i = 0; i < length; i++)
                buf[i] = value;
            return new SampleRegion(buf, 0, length, 0, length, LoopMode.NoLoop, SampleRate, 60, 0,
                EnvelopeParameters.Default, FilterParameters.Default, LfoParameters.Default, 0f, reverbSend: 0f, chorusSend: chorusSend);
        }

        static SampleRegion BuildSustainedDcRegion(float value, int length, float chorusSend = 0f) {
            float[] buf = new float[length];
            for (int i = 0; i < length; i++)
                buf[i] = value;
            return new SampleRegion(buf, 0, length, 0, length, LoopMode.Continuous, SampleRate, 60, 0,
                EnvelopeParameters.Default, FilterParameters.Default, LfoParameters.Default, 0f, reverbSend: 0f, chorusSend: chorusSend);
        }

        static float[] RenderOneShotWithChannelSend(float channelSend) {
            SynthesizerOptions options = new SynthesizerOptions(
                SampleRate, 2, 64, 4, reverb: null, globalReverb: false, chorus: BuildStrongChorusSettings());
            SampleRegion region = BuildOneShotDcRegion(0.5f, NoteFrames);
            SamplePatch patch = new SamplePatch(region, options.SampleRate);
            Synthesizer synth = new Synthesizer(options, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.SetChannelChorusSend(0, channelSend);
            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, NoteFrames + TailFrames);
            return sink.ToArray();
        }

        static float[] RenderOneShotWithSends(float channelSend, float regionSend) {
            SynthesizerOptions options = new SynthesizerOptions(
                SampleRate, 2, 64, 4, reverb: null, globalReverb: false, chorus: BuildStrongChorusSettings());
            SampleRegion region = BuildOneShotDcRegion(0.5f, NoteFrames, regionSend);
            SamplePatch patch = new SamplePatch(region, options.SampleRate);
            Synthesizer synth = new Synthesizer(options, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.SetChannelChorusSend(0, channelSend);
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
        [Description("A channel with chorus send=1.0 carries audible energy after its one-shot note ends " +
                     "(the chorus delay line still holds recently-sent samples), while the identical note " +
                     "on a channel with send=0.0 decays to exact silence — proving the per-channel send " +
                     "weight independently gates chorus routing.")]
        public void ChannelChorusSend_GatesWhetherChannelReachesChorus() {
            float[] sendOne = RenderOneShotWithChannelSend(1.0f);
            float[] sendZero = RenderOneShotWithChannelSend(0.0f);

            float tailRmsSendOne = TailRms(sendOne, channels: 2, TailFrames);
            float tailRmsSendZero = TailRms(sendZero, channels: 2, TailFrames);

            TestContext.WriteLine($"Tail RMS (send=1.0): {tailRmsSendOne:F6}; Tail RMS (send=0.0): {tailRmsSendZero:F6}.");

            Assert.That(tailRmsSendZero, Is.EqualTo(0f),
                "a channel with chorus send=0 must decay to exact silence once its dry note ends " +
                "(it never contributed to the send bus).");
            Assert.That(tailRmsSendOne, Is.GreaterThan(0f),
                "a channel with chorus send=1.0 must carry audible chorus energy after its dry note ends.");
        }

        [Test]
        [Description("Additive-combination core (design #7190 RULE 1) — mirrors the reverb regression: a " +
                     "channel with CC93>0 and a region with gen-15=0 must still reach the chorus, because " +
                     "the channel's send is not multiplied away by the region's absent bias.")]
        public void AdditiveCombination_ChannelSendWithZeroRegionSend_StillReachesChorus() {
            float[] samples = RenderOneShotWithSends(channelSend: 0.6f, regionSend: 0f);
            float tailRms = TailRms(samples, channels: 2, TailFrames);

            TestContext.WriteLine($"Tail RMS (CC93=0.6, gen-15=0): {tailRms:F6}.");
            Assert.That(tailRms, Is.GreaterThan(0f),
                "CC93>0 with gen-15=0 must still carry audible chorus energy.");
        }

        [Test]
        [Description("A channel with CC93=0 and a region with gen-15>0 must still reach the chorus — the " +
                     "channel's zero send must not zero out the region's own additive per-instrument bias.")]
        public void AdditiveCombination_RegionSendWithZeroChannelSend_StillReachesChorus() {
            float[] samples = RenderOneShotWithSends(channelSend: 0f, regionSend: 0.6f);
            float tailRms = TailRms(samples, channels: 2, TailFrames);

            TestContext.WriteLine($"Tail RMS (CC93=0, gen-15=0.6): {tailRms:F6}.");
            Assert.That(tailRms, Is.GreaterThan(0f),
                "gen-15>0 with CC93=0 must still carry audible chorus energy.");
        }

        [Test]
        [Description("Only when BOTH the channel send (CC93) and the region send (gen-15) are exactly 0 does " +
                     "the additive combination clamp01(0+0)=0 leave the voice fully dry.")]
        public void AdditiveCombination_BothSendsZero_IsDry() {
            float[] samples = RenderOneShotWithSends(channelSend: 0f, regionSend: 0f);
            float tailRms = TailRms(samples, channels: 2, TailFrames);

            Assert.That(tailRms, Is.EqualTo(0f),
                "both channel send and region send at 0 must decay to exact silence.");
        }

        [Test]
        [Description("CC93 near-full plus a non-zero gen-15 must clamp the combined send at 1.0 rather than " +
                     "overdriving past it: a (0.9, 0.5) pair (raw sum 1.4) must render bit-identically to an " +
                     "already-saturated (1.0, 0.0) pair, proving the clamp actually bounds the sum.")]
        public void AdditiveCombination_SendsSummingAboveOne_ClampAtOne() {
            float[] overOne = RenderOneShotWithSends(channelSend: 0.9f, regionSend: 0.5f);
            float[] saturated = RenderOneShotWithSends(channelSend: 1f, regionSend: 0f);

            Assert.That(overOne, Is.EqualTo(saturated),
                "a combined send summing above 1.0 must clamp identically to an already-saturated full send.");
        }

        [Test]
        [Description("Every channel send (CC93) AND every region send (gen-15) truly at 0, per-channel mode " +
                     "(the default), must render bit-identically to chorus being entirely absent: the additive " +
                     "combination clamp01(0+0)=0, so the send bus contributes nothing and the master path is " +
                     "untouched (regression, design #7190).")]
        public void AllChannelAndRegionSendsZero_PerChannelMode_RendersBitIdenticalToChorusAbsent() {
            SynthesizerOptions dryOptions = new SynthesizerOptions(SampleRate, 2, 64, 8, chorus: null);
            SynthesizerOptions wetOptions = new SynthesizerOptions(SampleRate, 2, 64, 8, chorus: ChorusSettings.Default);

            SampleRegion dryRegion = BuildSustainedDcRegion(0.3f, 4096, chorusSend: 0f);
            Synthesizer dry = new Synthesizer(dryOptions, new SamplePatch(dryRegion, dryOptions.SampleRate));
            InMemoryAudioSink drySink = new InMemoryAudioSink(dry.Format);
            dry.NoteOn(0, 60, 100);
            OfflineRenderer.Render(dry, drySink, 4096);

            SampleRegion wetRegion = BuildSustainedDcRegion(0.3f, 4096, chorusSend: 0f);
            Synthesizer wet = new Synthesizer(wetOptions, new SamplePatch(wetRegion, wetOptions.SampleRate));
            InMemoryAudioSink wetSink = new InMemoryAudioSink(wet.Format);
            wet.SetChannelChorusSend(0, 0f);
            wet.NoteOn(0, 60, 100);
            OfflineRenderer.Render(wet, wetSink, 4096);

            Assert.That(wetSink.ToArray(), Is.EqualTo(drySink.ToArray()),
                "an all-zero channel send AND all-zero region send must reproduce the chorus-absent render bit-for-bit.");
        }

        [Test]
        [Description("GlobalChorus=true must render bit-identically to the per-channel mode with every channel " +
                     "send forced to 1.0 (region gen-15=0), proving the master insert is exactly the special " +
                     "case where every voice sends fully (design #7190, mirrors GlobalReverb).")]
        public void GlobalChorusTrue_ReproducesPerChannelAllSendsOne_BitIdentically() {
            SynthesizerOptions perChannelOptions = new SynthesizerOptions(
                SampleRate, 2, 64, 8, reverb: null, globalReverb: false, chorus: ChorusSettings.Default, globalChorus: false);
            SynthesizerOptions globalOptions = new SynthesizerOptions(
                SampleRate, 2, 64, 8, reverb: null, globalReverb: false, chorus: ChorusSettings.Default, globalChorus: true);

            SampleRegion perChannelRegion = BuildSustainedDcRegion(0.3f, 4096, chorusSend: 0f);
            Synthesizer perChannel = new Synthesizer(perChannelOptions, new SamplePatch(perChannelRegion, perChannelOptions.SampleRate));
            InMemoryAudioSink perChannelSink = new InMemoryAudioSink(perChannel.Format);
            perChannel.SetChannelChorusSend(0, 1f);
            perChannel.NoteOn(0, 60, 100);
            OfflineRenderer.Render(perChannel, perChannelSink, 4096);

            SampleRegion globalRegion = BuildSustainedDcRegion(0.3f, 4096, chorusSend: 0f);
            Synthesizer global = new Synthesizer(globalOptions, new SamplePatch(globalRegion, globalOptions.SampleRate));
            InMemoryAudioSink globalSink = new InMemoryAudioSink(global.Format);
            global.NoteOn(0, 60, 100);
            OfflineRenderer.Render(global, globalSink, 4096);

            float[] perChannelSamples = perChannelSink.ToArray();
            float[] globalSamples = globalSink.ToArray();

            Assert.That(globalSamples, Is.EqualTo(perChannelSamples),
                "GlobalChorus=true must reproduce the per-channel-all-sends-1.0 render bit-for-bit.");

            float dryPeak = 0f;
            foreach (float s in globalSamples)
                dryPeak = Math.Max(dryPeak, Math.Abs(s));
            Assert.That(dryPeak, Is.GreaterThan(0f), "the global-chorus render must produce non-silent audio.");
        }

        [TestCase(-1)]
        [TestCase(16)]
        [Description("SetChannelChorusSend rejects a channel outside [0,15], mirroring SetChannelReverbSend.")]
        public void SetChannelChorusSend_ChannelOutOfRange_Throws(int channel) {
            SynthesizerOptions options = new SynthesizerOptions(SampleRate, 2, 64, 8);
            Synthesizer synth = new Synthesizer(options, new SamplePatch(BuildSustainedDcRegion(0.1f, 64), SampleRate));

            Assert.Throws<ArgumentOutOfRangeException>(() => synth.SetChannelChorusSend(channel, 1f));
        }
    }
}
