using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Formats.Midi;
using Pooshit.AudioSynth.Formats.Sf2;
using Pooshit.AudioSynth.Sequencing;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Deliverable-proof test (DiVoid bug #7226, design #7227): re-rendering the diagnosed
    /// <c>1-10-Force_Your_Way.mid</c> through the real Florestan GM SoundFont must show the hi-hat's
    /// density-spike volume pump gone. Per design §11 O1, the metric is the full-mix fallback (the
    /// harness has no built-in per-channel solo render): the coefficient of variation (stddev/mean) of
    /// sliding-window RMS across the diagnosed 20-22s and 2:09 density spikes, which must stay low
    /// (a swelling-then-normalising level would show as a high CoV; a flat level shows as a low one).
    /// Skips gracefully when the dev-tree assets are absent.
    /// </summary>
    [TestFixture]
    public class ExclusiveClassHiHatRenderProofTests {

        const int MaxVoices = 128;

        /// <summary>Sliding sub-window size, in seconds, for the RMS series feeding the CoV metric.</summary>
        const float SubWindowSeconds = 0.1f;

        /// <summary>
        /// Coefficient-of-variation ceiling a flat (choked) hi-hat level must stay under across a density
        /// spike; measured against the post-fix render (see PR body for the pre-fix figure).
        /// </summary>
        const float MaxCoefficientOfVariation = 0.35f;

        static string? FindDevTreeAsset(string subfolder, string fileName) {
            string? dir = Path.GetDirectoryName(typeof(ExclusiveClassHiHatRenderProofTests).Assembly.Location);
            while (dir != null) {
                string candidate = Path.Combine(dir, "Source", "AudioSynthesis.Tests", subfolder, fileName);
                if (File.Exists(candidate))
                    return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        static float[] RenderSong(string songPath, string soundfontPath, out int channels, out int sampleRate) {
            AudioFormat format = new AudioFormat(SynthesizerOptions.DefaultSampleRate, SynthesizerOptions.DefaultChannels);
            channels = format.Channels;
            sampleRate = format.SampleRate;

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

        static float FrameRms(float[] samples, int channels, int frameStart, int frameCount) {
            int start = frameStart * channels;
            int count = frameCount * channels;
            double sumSquares = 0.0;
            for (int i = start; i < start + count; i++)
                sumSquares += (double)samples[i] * samples[i];
            return (float)Math.Sqrt(sumSquares / count);
        }

        /// <summary>
        /// Coefficient of variation (stddev/mean) of consecutive, non-overlapping <see cref="SubWindowSeconds"/>
        /// RMS windows spanning [<paramref name="startSeconds"/>, <paramref name="endSeconds"/>); a flat level
        /// yields a low value, a swell-then-normalise yields a high one. Clamps to the available audio.
        /// </summary>
        static float WindowedRmsCoefficientOfVariation(
            float[] samples, int channels, int sampleRate, float startSeconds, float endSeconds) {
            int totalFrames = samples.Length / channels;
            int subWindowFrames = (int)(SubWindowSeconds * sampleRate);
            int startFrame = Math.Max(0, (int)(startSeconds * sampleRate));
            int endFrame = Math.Min(totalFrames, (int)(endSeconds * sampleRate));

            List<float> rmsSeries = new List<float>();
            for (int frame = startFrame; frame + subWindowFrames <= endFrame; frame += subWindowFrames)
                rmsSeries.Add(FrameRms(samples, channels, frame, subWindowFrames));

            if (rmsSeries.Count < 2)
                return 0f;

            double mean = rmsSeries.Average();
            if (mean < 1e-6)
                return 0f;

            double variance = rmsSeries.Select(v => (v - mean) * (v - mean)).Average();
            return (float)(Math.Sqrt(variance) / mean);
        }

        [Test]
        [Description("Deliverable proof: 1-10-Force_Your_Way.mid through Florestan must show a stable " +
                     "(low-CoV) full-mix level across the diagnosed 20-22s hi-hat fill, evidencing the " +
                     "choke instead of the pre-fix layering swell. Skips gracefully when dev-tree assets are absent.")]
        public void RealSong_ForceYourWay_ThroughFlorestan_LevelStableAcross20To22sSpike() {
            string? soundfontPath = FindDevTreeAsset("Soundfonts", "__Florestan_Basic_GM_GS.sf2");
            string? songPath = FindDevTreeAsset("Midi", "1-10-Force_Your_Way.mid");
            if (soundfontPath is null || songPath is null) {
                Assert.Ignore("MIDI/SoundFont dev-tree assets not found; skipping the hi-hat choke deliverable-proof render.");
                return;
            }

            float[] samples = RenderSong(songPath, soundfontPath, out int channels, out int sampleRate);
            Assert.That(samples, Is.Not.Empty, "the render must produce audio.");

            float coefficientOfVariation = WindowedRmsCoefficientOfVariation(samples, channels, sampleRate, 20f, 22f);
            TestContext.WriteLine($"20-22s full-mix windowed-RMS CoV (post-fix): {coefficientOfVariation:F4} " +
                                  $"(ceiling {MaxCoefficientOfVariation:F4}).");

            Assert.That(coefficientOfVariation, Is.LessThan(MaxCoefficientOfVariation),
                "the 20-22s hi-hat fill must render with a stable (low-CoV) level now that same-class, " +
                "same-channel hits choke instead of layering.");
        }

        [Test]
        [Description("Deliverable proof: the same stability holds at the second diagnosed spike, 2:09 " +
                     "(129s). Skips gracefully when dev-tree assets are absent.")]
        public void RealSong_ForceYourWay_ThroughFlorestan_LevelStableAcross129sSpike() {
            string? soundfontPath = FindDevTreeAsset("Soundfonts", "__Florestan_Basic_GM_GS.sf2");
            string? songPath = FindDevTreeAsset("Midi", "1-10-Force_Your_Way.mid");
            if (soundfontPath is null || songPath is null) {
                Assert.Ignore("MIDI/SoundFont dev-tree assets not found; skipping the hi-hat choke deliverable-proof render.");
                return;
            }

            float[] samples = RenderSong(songPath, soundfontPath, out int channels, out int sampleRate);
            Assert.That(samples, Is.Not.Empty, "the render must produce audio.");

            float coefficientOfVariation = WindowedRmsCoefficientOfVariation(samples, channels, sampleRate, 128f, 130f);
            TestContext.WriteLine($"128-130s full-mix windowed-RMS CoV (post-fix): {coefficientOfVariation:F4} " +
                                  $"(ceiling {MaxCoefficientOfVariation:F4}).");

            Assert.That(coefficientOfVariation, Is.LessThan(MaxCoefficientOfVariation),
                "the 2:09 hi-hat fill must render with a stable (low-CoV) level now that same-class, " +
                "same-channel hits choke instead of layering.");
        }
    }
}
