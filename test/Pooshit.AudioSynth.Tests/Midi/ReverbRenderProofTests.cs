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
    /// (the PR-16 behaviour this test was written to prove), not per-channel routing. The Florestan
    /// soundfont's regions carry an explicit SF2 gen-16 (reverbEffectsSend) of 0 rather than omitting the
    /// generator, so under the new per-channel default every voice's combined send is exactly zero and
    /// this song renders bit-identical to dry regardless of CC91 — confirmed during implementation (design
    /// §14 Q2) and exactly the "real-song render is illustrative, not diagnostic, when the asset doesn't
    /// vary reverb send" contingency the design anticipated. The asset-free, deterministic
    /// <see cref="Pooshit.AudioSynth.Tests.ReverbSendRoutingTests"/> is the routing's proof instead.
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
