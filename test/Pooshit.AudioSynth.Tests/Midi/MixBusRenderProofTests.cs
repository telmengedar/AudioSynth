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
    /// Deliverable-proof test (DiVoid #7126): re-rendering the diagnosed songs must show the clipped
    /// fraction drop sharply below the ~7.19% baseline. Skips gracefully when dev-tree assets are absent.
    /// </summary>
    [TestFixture]
    public class MixBusRenderProofTests {

        const int MaxVoices = 128;

        /// <summary>Diagnosis #7124's measured baseline for 07dkc2bram.mid through Florestan, before this fix.</summary>
        const float BaselineClippedFraction = 0.0719f;

        static string? FindDevTreeAsset(string subfolder, string fileName) {
            string? dir = Path.GetDirectoryName(typeof(MixBusRenderProofTests).Assembly.Location);
            while (dir != null) {
                string candidate = Path.Combine(dir, "Source", "AudioSynthesis.Tests", subfolder, fileName);
                if (File.Exists(candidate))
                    return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        static float[] RenderSong(string songPath, string soundfontPath, out int channels) {
            AudioFormat format = new AudioFormat(SynthesizerOptions.DefaultSampleRate, SynthesizerOptions.DefaultChannels);
            channels = format.Channels;

            SoundBank bank;
            using (FileStream soundfontStream = File.OpenRead(soundfontPath))
                bank = new Sf2SoundBankLoader(format.SampleRate).Load(soundfontStream);

            MidiFile midiFile;
            using (FileStream songStream = File.OpenRead(songPath))
                midiFile = MidiFile.Read(songStream);
            TimedMessageSequence sequence = new TimedMessageSequence(midiFile);

            SynthesizerOptions options = new SynthesizerOptions(format.SampleRate, format.Channels, SynthesizerOptions.DefaultBlockFrames, MaxVoices);
            Synthesizer synthesizer = new Synthesizer(options, bank.GetPatch(0, 0));
            InMemoryAudioSink sink = new InMemoryAudioSink(format);

            MidiSequencer.Render(sequence, synthesizer, sink, bank);
            return sink.ToArray();
        }

        static float ClippedFraction(float[] samples, int start, int length) {
            int clipped = 0;
            int end = start + length;
            for (int i = start; i < end; i++) {
                if (Math.Abs(samples[i]) >= 1f)
                    clipped++;
            }
            return (float)clipped / length;
        }

        [TestCase("07dkc2bram.mid", Description = "The song diagnosis #7124 measured at ~7.19% clipped, opening marimba/vibraphone buzzed.")]
        [TestCase("1-02-Balamb_Garden.mid", Description = "FF8 track (O3): a second real-song A/B point.")]
        public void RealSong_ThroughFlorestan_ClippedFractionDropsSharplyBelowBaseline(string songFileName) {
            string? soundfontPath = FindDevTreeAsset("Soundfonts", "__Florestan_Basic_GM_GS.sf2");
            string? songPath = FindDevTreeAsset("Midi", songFileName);
            if (soundfontPath is null || songPath is null) {
                Assert.Ignore("MIDI/SoundFont dev-tree assets not found; skipping the mix-bus deliverable-proof render.");
                return;
            }

            float[] samples = RenderSong(songPath, soundfontPath, out int channels);

            Assert.That(samples, Is.Not.Empty, "the render must produce audio.");

            float peak = 0f;
            foreach (float s in samples)
                peak = Math.Max(peak, Math.Abs(s));
            Assert.That(peak, Is.GreaterThan(0.01f), "rendering a real song must not be silent.");
            Assert.That(peak, Is.LessThanOrEqualTo(1f), "every sample must remain within [-1, 1].");

            float clippedFraction = ClippedFraction(samples, 0, samples.Length);
            Assert.That(clippedFraction, Is.LessThan(BaselineClippedFraction / 10f),
                $"clipped fraction must drop sharply below the diagnosed baseline ({BaselineClippedFraction:P2}); " +
                $"measured {clippedFraction:P4}.");

            int openingSamples = Math.Min(samples.Length, SynthesizerOptions.DefaultSampleRate * channels);
            float openingClippedFraction = ClippedFraction(samples, 0, openingSamples);
            Assert.That(openingClippedFraction, Is.LessThan(0.01f),
                "the diagnosed buzz was most audible at the very opening, so it must now render clean; " +
                $"measured clipped fraction {openingClippedFraction:P4}.");
        }
    }
}
