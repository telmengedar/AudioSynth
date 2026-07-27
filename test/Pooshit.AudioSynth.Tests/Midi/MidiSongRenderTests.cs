using System;
using System.Collections.Generic;
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
    /// Deliverable-proof test (DiVoid #7095/#7098): a real song through the Florestan GM SoundFont
    /// renders non-silent, bounded audio. Skips gracefully when the dev-tree assets are absent.
    /// </summary>
    [TestFixture]
    public class MidiSongRenderTests {

        const int MaxVoices = 128;

        static string? FindDevTreeAsset(string subfolder, string fileName) {
            string? dir = Path.GetDirectoryName(typeof(MidiSongRenderTests).Assembly.Location);
            while (dir != null) {
                string candidate = Path.Combine(dir, "Source", "AudioSynthesis.Tests", subfolder, fileName);
                if (File.Exists(candidate))
                    return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        static float PeakAmplitude(float[] samples) {
            float peak = 0f;
            foreach (float s in samples)
                peak = Math.Max(peak, Math.Abs(s));
            return peak;
        }

        static bool AllBounded(float[] samples) {
            foreach (float s in samples) {
                if (Math.Abs(s) > 1f)
                    return false;
            }
            return true;
        }

        [Test]
        [Description("Real-song integration: 07dkc2bram.mid through the Florestan GM SoundFont renders "
                   + "non-silent, bounded audio whose duration matches the parsed song. Skipped gracefully "
                   + "when the dev-tree assets are absent; the deterministic schedule tests above are "
                   + "always-green.")]
        public void RealSong_ThroughFlorestan_RendersNonSilentBoundedAudio() {
            string? soundfontPath = FindDevTreeAsset("Soundfonts", "__Florestan_Basic_GM_GS.sf2");
            string? songPath = FindDevTreeAsset("Midi", "07dkc2bram.mid");
            if (soundfontPath is null || songPath is null) {
                Assert.Ignore("MIDI/SoundFont dev-tree assets not found; skipping real-song integration test. " +
                               "The deterministic MidiSequencerScheduleTests above are always-green.");
                return;
            }

            AudioFormat format = new AudioFormat(SynthesizerOptions.DefaultSampleRate, SynthesizerOptions.DefaultChannels);

            IReadOnlyList<IPatch> patches;
            using (FileStream sf2Stream = File.OpenRead(soundfontPath))
                patches = new Sf2SoundBankLoader(format.SampleRate).Load(sf2Stream);
            Assert.That(patches.Count, Is.GreaterThan(0), "Florestan must contain at least one preset.");

            MidiFile midiFile;
            using (FileStream songStream = File.OpenRead(songPath))
                midiFile = MidiFile.Read(songStream);
            TimedMessageSequence sequence = new TimedMessageSequence(midiFile);
            Assert.That(sequence.Messages, Is.Not.Empty, "The real song must parse into at least one timed message.");

            float expectedDurationSeconds = sequence.Messages[sequence.Messages.Length - 1].Time;
            Assert.That(expectedDurationSeconds, Is.GreaterThan(0), "The real song must have non-zero duration.");

            SynthesizerOptions options = new SynthesizerOptions(format.SampleRate, format.Channels, SynthesizerOptions.DefaultBlockFrames, MaxVoices);
            Synthesizer synthesizer = new Synthesizer(options, patches[0]);
            InMemoryAudioSink sink = new InMemoryAudioSink(format);

            long frames = MidiSequencer.Render(sequence, synthesizer, sink);

            Assert.That(frames, Is.GreaterThan(0), "The render must produce at least one frame.");
            long expectedMinimumFrames = (long)(expectedDurationSeconds * format.SampleRate);
            Assert.That(frames, Is.GreaterThanOrEqualTo(expectedMinimumFrames),
                "Rendered frames must cover at least the song's own duration (before the release tail).");

            float[] rendered = sink.ToArray();
            Assert.That(AllBounded(rendered), Is.True, "All rendered samples must be within [-1, 1].");
            Assert.That(PeakAmplitude(rendered), Is.GreaterThan(0.01f),
                "Rendering a real song through a real SoundFont must not be silent.");
        }
    }
}
