using System;
using System.IO;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Formats.Midi;
using Pooshit.AudioSynth.Formats.Sf2;
using Pooshit.AudioSynth.Sequencing;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Deliverable-proof test for the reverb master insert (DiVoid #7162, design §14.9): rendering
    /// 07dkc2bram.mid through Florestan with the hall preset must show a measurably longer ambient tail at
    /// song end than the dry (Reverb absent) render — energy continuing after the last notes have released
    /// instead of decaying toward silence. Skips gracefully when dev-tree assets are absent.
    /// </summary>
    /// <remarks>
    /// <see cref="RealSong_WithReverb_HasLongerAmbientTailThanDryRender"/> renders with
    /// <c>globalReverb: true</c> (DiVoid #7165/#7170): it exercises the master-insert reverb DSP itself
    /// (the PR-16 behaviour this test was written to prove), not per-channel routing.
    /// <see cref="RealSong_PerChannelDefault_IsNeitherFullyDryNorUniformlyGlobal"/> is the per-channel
    /// deliverable proof (design §9.3/§14.10 revised): the Florestan soundfont's regions carry an explicit
    /// SF2 gen-16 (reverbEffectsSend) of 0 on every probed program, which under a <em>multiplicative</em>
    /// combination (the first cut of this design) would zero every voice's send and render the song
    /// bit-identical to dry regardless of the song's own rich per-channel CC91 automation — the bug this
    /// revision fixes. Under the additive/clamped combination, gen-16=0 contributes no bias but does not
    /// nullify CC91 either, so the per-channel render is audibly wet (driven by CC91 alone) and
    /// non-uniform (tracking the song's varied per-channel CC91 map), unlike the flat
    /// <see cref="SynthesizerOptions.GlobalReverb"/>=<c>true</c> render. The asset-free, deterministic
    /// <see cref="Pooshit.AudioSynth.Tests.ReverbSendRoutingTests"/> remains the routing's asset-independent
    /// backstop.
    /// </remarks>
    [TestFixture]
    public class ReverbRenderProofTests {

        const int MaxVoices = 128;

        /// <summary>Width of the final-window RMS measurement, in frames (the last second of the render).</summary>
        const int TailWindowFrames = SynthesizerOptions.DefaultSampleRate;

        static string? FindDevTreeAsset(string subfolder, string fileName) {
            string? dir = Path.GetDirectoryName(typeof(ReverbRenderProofTests).Assembly.Location);
            while (dir != null) {
                string candidate = Path.Combine(dir, "Source", "AudioSynthesis.Tests", subfolder, fileName);
                if (File.Exists(candidate))
                    return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        static float[] RenderSong(string songPath, string soundfontPath, ReverbSettings? reverb, out int channels, bool globalReverb = false) {
            AudioFormat format = new AudioFormat(SynthesizerOptions.DefaultSampleRate, SynthesizerOptions.DefaultChannels);
            channels = format.Channels;

            SoundBank bank;
            using (FileStream soundfontStream = File.OpenRead(soundfontPath))
                bank = new Sf2SoundBankLoader(format.SampleRate).Load(soundfontStream);

            MidiFile midiFile;
            using (FileStream songStream = File.OpenRead(songPath))
                midiFile = MidiFile.Read(songStream);
            TimedMessageSequence sequence = new TimedMessageSequence(midiFile);

            SynthesizerOptions options = new SynthesizerOptions(
                format.SampleRate, format.Channels, SynthesizerOptions.DefaultBlockFrames, MaxVoices, reverb, globalReverb);
            Synthesizer synthesizer = new Synthesizer(options, bank.GetPatch(0, 0));
            InMemoryAudioSink sink = new InMemoryAudioSink(format);

            MidiSequencer.Render(sequence, synthesizer, sink, bank);
            return sink.ToArray();
        }

        static float TailRms(float[] samples, int channels) {
            int windowSamples = Math.Min(samples.Length, TailWindowFrames * channels);
            int start = samples.Length - windowSamples;
            double sum = 0.0;
            for (int i = start; i < samples.Length; i++)
                sum += (double)samples[i] * samples[i];
            return (float)Math.Sqrt(sum / windowSamples);
        }

        [Test]
        [Description("The wet render's final-second RMS must measurably exceed the dry render's: the reverb " +
                     "tail keeps energy present after the song's last notes have released, where the dry " +
                     "render has already decayed toward silence.")]
        public void RealSong_WithReverb_HasLongerAmbientTailThanDryRender() {
            string? soundfontPath = FindDevTreeAsset("Soundfonts", "__Florestan_Basic_GM_GS.sf2");
            string? songPath = FindDevTreeAsset("Midi", "07dkc2bram.mid");
            if (soundfontPath is null || songPath is null) {
                Assert.Ignore("MIDI/SoundFont dev-tree assets not found; skipping the reverb deliverable-proof render.");
                return;
            }

            float[] dry = RenderSong(songPath, soundfontPath, reverb: null, out int channels, globalReverb: true);
            float[] wet = RenderSong(songPath, soundfontPath, ReverbSettings.Default, out _, globalReverb: true);

            Assert.That(wet, Is.Not.Empty, "the wet render must produce audio.");
            Assert.That(dry.Length, Is.EqualTo(wet.Length), "reverb must not change the rendered frame count.");

            float wetPeak = 0f;
            foreach (float s in wet)
                wetPeak = Math.Max(wetPeak, Math.Abs(s));
            Assert.That(wetPeak, Is.LessThanOrEqualTo(1f),
                "the wet render must remain within [-1, 1] (soft-clip/finalize still bound dry+wet).");

            float dryTailRms = TailRms(dry, channels);
            float wetTailRms = TailRms(wet, channels);

            TestContext.WriteLine(
                $"Dry final-second RMS: {dryTailRms:F6}; Wet final-second RMS: {wetTailRms:F6}; " +
                $"ratio: {(dryTailRms > 0f ? wetTailRms / dryTailRms : float.PositiveInfinity):F2}x.");

            Assert.That(wetTailRms, Is.GreaterThan(dryTailRms),
                $"expected the reverb tail to keep audible energy after the dry render has decayed; " +
                $"dry final-second RMS={dryTailRms}, wet final-second RMS={wetTailRms}.");
        }

        [Test]
        [Description("Deliverable proof for the additive-combination fix (design §9.3/§14.10 revised): " +
                     "07dkc2bram.mid through Florestan (whose regions carry an explicit gen-16=0 on every " +
                     "probed program) must, under the per-channel default, render neither bit-identical to " +
                     "dry (the multiplicative bug this revision fixes — gen-16=0 no longer nullifies the " +
                     "song's own per-channel CC91 automation) nor bit-identical to the flat GlobalReverb=true " +
                     "render (the per-channel send bus tracks the song's varied CC91 map instead of sending " +
                     "every voice fully).")]
        public void RealSong_PerChannelDefault_IsNeitherFullyDryNorUniformlyGlobal() {
            string? soundfontPath = FindDevTreeAsset("Soundfonts", "__Florestan_Basic_GM_GS.sf2");
            string? songPath = FindDevTreeAsset("Midi", "07dkc2bram.mid");
            if (soundfontPath is null || songPath is null) {
                Assert.Ignore("MIDI/SoundFont dev-tree assets not found; skipping the reverb deliverable-proof render.");
                return;
            }

            float[] dry = RenderSong(songPath, soundfontPath, reverb: null, out int channels);
            float[] perChannel = RenderSong(songPath, soundfontPath, ReverbSettings.Default, out _, globalReverb: false);
            float[] global = RenderSong(songPath, soundfontPath, ReverbSettings.Default, out _, globalReverb: true);

            Assert.That(perChannel.Length, Is.EqualTo(dry.Length), "reverb must not change the rendered frame count.");
            Assert.That(perChannel.Length, Is.EqualTo(global.Length), "reverb routing must not change the rendered frame count.");

            float diffFromDryRms = DifferenceRms(perChannel, dry);
            float diffFromGlobalRms = DifferenceRms(perChannel, global);
            float perChannelTailRms = TailRms(perChannel, channels);
            float globalTailRms = TailRms(global, channels);

            TestContext.WriteLine(
                $"Per-channel vs dry difference RMS: {diffFromDryRms:F6}; " +
                $"per-channel vs global difference RMS: {diffFromGlobalRms:F6}; " +
                $"per-channel final-second RMS: {perChannelTailRms:F6}; global final-second RMS: {globalTailRms:F6}.");

            Assert.That(perChannel, Is.Not.EqualTo(dry),
                "the per-channel default must NOT be bit-identical to the dry render: Florestan's gen-16=0 " +
                "must not nullify the song's own per-channel CC91 automation (the multiplicative bug).");
            Assert.That(diffFromDryRms, Is.GreaterThan(0f),
                "the per-channel render must carry measurable reverb energy the dry render lacks.");

            Assert.That(perChannel, Is.Not.EqualTo(global),
                "the per-channel default must NOT be bit-identical to the flat GlobalReverb=true render: " +
                "per-channel routing must track the song's non-uniform CC91 map rather than sending every " +
                "voice fully.");
            Assert.That(diffFromGlobalRms, Is.GreaterThan(0f),
                "the per-channel render must diverge measurably from the uniform global render.");
        }

        static float DifferenceRms(float[] a, float[] b) {
            int length = Math.Min(a.Length, b.Length);
            double sum = 0.0;
            for (int i = 0; i < length; i++) {
                double diff = (double)a[i] - b[i];
                sum += diff * diff;
            }
            return (float)Math.Sqrt(sum / length);
        }

        [Test]
        [Description("Wet = 0 on the same real-song render reproduces the dry render bit-for-bit (deliverable " +
                     "regression, design §14.8/§14.9): the master insert never colours the signal at zero wet.")]
        public void RealSong_WetZero_ReproducesDryRenderBitForBit() {
            string? soundfontPath = FindDevTreeAsset("Soundfonts", "__Florestan_Basic_GM_GS.sf2");
            string? songPath = FindDevTreeAsset("Midi", "07dkc2bram.mid");
            if (soundfontPath is null || songPath is null) {
                Assert.Ignore("MIDI/SoundFont dev-tree assets not found; skipping the reverb deliverable-proof render.");
                return;
            }

            float[] dry = RenderSong(songPath, soundfontPath, reverb: null, out _);
            float[] wetZero = RenderSong(songPath, soundfontPath, new ReverbSettings(wet: 0f), out _);

            Assert.That(wetZero, Is.EqualTo(dry), "Wet=0 must reproduce the reverb-absent render bit-for-bit.");
        }
    }
}
