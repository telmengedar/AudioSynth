using System;
using System.IO;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Formats.Midi;
using Pooshit.AudioSynth.Formats.Sf2;
using Pooshit.AudioSynth.Sequencing;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Tests.Helpers;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Deliverable-proof tests (DiVoid #7127): a real render through the Florestan GM SF2 must show the
    /// L and R channels diverge (they were bit-identical under the old mono-summed centre mix), mirroring
    /// <see cref="MixBusRenderProofTests"/>; a full-stack measurement note (#7141) records whether the
    /// diagnosed song moves the percussion channel off-centre. Both skip gracefully when dev-tree assets
    /// are absent.
    /// </summary>
    [TestFixture]
    public class StereoPanRenderProofTests {

        const int MaxVoices = 128;
        const int PercussionChannel = 9;

        /// <summary>Minimum L-R mean-squared difference proving the channels are no longer identical.</summary>
        const double MinLeftRightDivergence = 1e-6;

        static readonly AudioFormat Format = new AudioFormat(44100, 2);

        static string? FindDevTreeAsset(string subfolder, string fileName) {
            string? dir = Path.GetDirectoryName(typeof(StereoPanRenderProofTests).Assembly.Location);
            while (dir != null) {
                string candidate = Path.Combine(dir, "Source", "AudioSynthesis.Tests", subfolder, fileName);
                if (File.Exists(candidate))
                    return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        static float[] RenderSong(string songPath, string soundfontPath) {
            AudioFormat format = new AudioFormat(SynthesizerOptions.DefaultSampleRate, SynthesizerOptions.DefaultChannels);

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

        static double MeanSquaredLeftRightDifference(float[] samples) {
            int frameCount = samples.Length / 2;
            double sumSquaredDiff = 0;
            for (int i = 0; i < frameCount; i++) {
                double diff = samples[i * 2] - samples[i * 2 + 1];
                sumSquaredDiff += diff * diff;
            }
            return sumSquaredDiff / frameCount;
        }

        [TestCase("07dkc2bram.mid", Description = "The deliverable song: L and R were bit-identical (mono-summed centre) before this PR.")]
        [TestCase("1-02-Balamb_Garden.mid", Description = "FF8 track: a second real-song A/B point.")]
        public void RealSong_ThroughFlorestan_LeftAndRightChannelsDiverge(string songFileName) {
            string? soundfontPath = FindDevTreeAsset("Soundfonts", "__Florestan_Basic_GM_GS.sf2");
            string? songPath = FindDevTreeAsset("Midi", songFileName);
            if (soundfontPath is null || songPath is null) {
                Assert.Ignore("MIDI/SoundFont dev-tree assets not found; skipping the stereo-pan deliverable-proof render.");
                return;
            }

            float[] samples = RenderSong(songPath, soundfontPath);

            Assert.That(samples, Is.Not.Empty, "the render must produce audio.");

            float peak = 0f;
            foreach (float s in samples)
                peak = Math.Max(peak, Math.Abs(s));
            Assert.That(peak, Is.GreaterThan(0.01f), "rendering a real song must not be silent.");
            Assert.That(peak, Is.LessThanOrEqualTo(1f), "every sample must remain within [-1, 1].");

            double divergence = MeanSquaredLeftRightDifference(samples);
            Assert.That(divergence, Is.GreaterThan(MinLeftRightDivergence),
                $"L and R must now diverge (they were bit-identical, divergence 0.0, before this PR); " +
                $"measured mean-squared L-R difference {divergence:E4}.");
        }

        [Test]
        [Description("Measurement note (#7141, not a design target): records whether 07dkc2bram.mid sends a " +
                     "non-centre CC10 to channel 9 (percussion) — pan only moves percussion off-centre if the " +
                     "song/soundfont specifies it. Skips gracefully when the dev-tree asset is absent.")]
        public void RealSong_07dkc2bram_PercussionPanMeasurementNote() {
            string? songPath = FindDevTreeAsset("Midi", "07dkc2bram.mid");
            if (songPath is null) {
                Assert.Ignore("07dkc2bram.mid dev-tree asset not found; skipping the percussion-pan measurement note.");
                return;
            }

            MidiFile midiFile;
            using (FileStream songStream = File.OpenRead(songPath))
                midiFile = MidiFile.Read(songStream);
            TimedMessageSequence sequence = new TimedMessageSequence(midiFile);

            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            StubPatch piano = new StubPatch("piano");
            SoundBank bank = new SoundBank(new[] {
                (0, 0, "piano", (IPatch)piano),
                (128, 0, "piano", (IPatch)piano),
            });

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), bank);

            bool percussionPanned = false;
            foreach ((int channel, float pan) in synth.ChannelPanCalls) {
                if (channel == PercussionChannel && Math.Abs(pan) > 1e-6f) {
                    percussionPanned = true;
                    break;
                }
            }

            TestContext.WriteLine(percussionPanned
                ? "07dkc2bram.mid sends a non-centre CC10 to channel 9 (percussion): pan DOES move percussion off-centre (re #7141)."
                : "07dkc2bram.mid never sends a non-centre CC10 to channel 9 (percussion): pan does not move percussion off-centre for this song (re #7141).");
        }
    }
}
