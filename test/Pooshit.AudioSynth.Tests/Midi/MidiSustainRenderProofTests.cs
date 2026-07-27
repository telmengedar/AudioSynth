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
    /// Deliverable-proof tests (DiVoid #7155, design #7179): a count-regression guard on the real
    /// <c>3-20-Eyes_On_Me_2.mid</c> deliverable song's diagnosed 500 CC64 events on channel 0, and a
    /// real render through the Florestan GM SF2 confirming non-silent audio with sustain applied.
    /// Both real-asset tests skip gracefully when the dev-tree assets are absent, mirroring
    /// <see cref="MidiPitchBendRenderProofTests"/>.
    /// </summary>
    [TestFixture]
    public class MidiSustainRenderProofTests {

        static readonly AudioFormat Format = new AudioFormat(SynthesizerOptions.DefaultSampleRate, 1);

        static string? FindDevTreeAsset(string subfolder, string fileName) {
            string? dir = Path.GetDirectoryName(typeof(MidiSustainRenderProofTests).Assembly.Location);
            while (dir != null) {
                string candidate = Path.Combine(dir, "Source", "AudioSynthesis.Tests", subfolder, fileName);
                if (File.Exists(candidate))
                    return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        [Test]
        [Description("Real-song regression guard: 3-20-Eyes_On_Me_2.mid's diagnosed 500 CC64 events on " +
                     "channel 0 (design §1) must all reach MidiSequencer.ApplyMessage and fan out as " +
                     "SetChannelSustain calls. Skips gracefully when the dev-tree asset is absent.")]
        public void RealSong_EyesOnMe_SustainEventsReachSequencer() {
            string? songPath = FindDevTreeAsset("Midi", "3-20-Eyes_On_Me_2.mid");
            if (songPath is null) {
                Assert.Ignore("3-20-Eyes_On_Me_2.mid dev-tree asset not found; skipping the sustain count regression guard.");
                return;
            }

            MidiFile midiFile;
            using (FileStream songStream = File.OpenRead(songPath))
                midiFile = MidiFile.Read(songStream);
            TimedMessageSequence sequence = new TimedMessageSequence(midiFile);

            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            StubPatch piano = new StubPatch("piano");
            SoundBank bank = new SoundBank(new[] {
                (0, 0, (IPatch)piano),
                (128, 0, (IPatch)piano),
            });

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), bank);

            Assert.That(synth.ChannelSustainCalls, Has.Count.EqualTo(500),
                "the diagnosed CC64 event count for 3-20-Eyes_On_Me_2.mid (design §1) must all reach SetChannelSustain.");

            int channel0Calls = 0;
            foreach ((int channel, bool _) in synth.ChannelSustainCalls) {
                if (channel == 0)
                    channel0Calls++;
            }
            Assert.That(channel0Calls, Is.EqualTo(500),
                "the diagnosed sustain activity for this song is entirely on channel 0; " +
                $"only {channel0Calls} of {synth.ChannelSustainCalls.Count} calls targeted it.");
        }

        [Test]
        [Description("Deliverable render: 3-20-Eyes_On_Me_2.mid through the Florestan GM SF2 renders " +
                     "non-silent, in-range audio with the sustain pedal applied. Skips gracefully when the " +
                     "dev-tree assets are absent.")]
        public void RealSong_EyesOnMe_ThroughFlorestan_RendersNonSilent() {
            string? soundfontPath = FindDevTreeAsset("Soundfonts", "__Florestan_Basic_GM_GS.sf2");
            string? songPath = FindDevTreeAsset("Midi", "3-20-Eyes_On_Me_2.mid");
            if (soundfontPath is null || songPath is null) {
                Assert.Ignore("MIDI/SoundFont dev-tree assets not found; skipping the sustain deliverable render.");
                return;
            }

            AudioFormat format = new AudioFormat(SynthesizerOptions.DefaultSampleRate, SynthesizerOptions.DefaultChannels);

            SoundBank bank;
            using (FileStream soundfontStream = File.OpenRead(soundfontPath))
                bank = new Sf2SoundBankLoader(format.SampleRate).Load(soundfontStream);

            MidiFile midiFile;
            using (FileStream songStream = File.OpenRead(songPath))
                midiFile = MidiFile.Read(songStream);
            TimedMessageSequence sequence = new TimedMessageSequence(midiFile);

            SynthesizerOptions options = new SynthesizerOptions(format.SampleRate, format.Channels, SynthesizerOptions.DefaultBlockFrames, 128);
            Synthesizer synthesizer = new Synthesizer(options, bank.GetPatch(0, 0));
            InMemoryAudioSink sink = new InMemoryAudioSink(format);

            MidiSequencer.Render(sequence, synthesizer, sink, bank);
            float[] samples = sink.ToArray();

            Assert.That(samples, Is.Not.Empty, "the render must produce audio.");

            float peak = 0f;
            foreach (float s in samples)
                peak = Math.Max(peak, Math.Abs(s));
            Assert.That(peak, Is.GreaterThan(0.01f), "rendering the sustain-pedal deliverable song must not be silent.");
            Assert.That(peak, Is.LessThanOrEqualTo(1f), "every sample must remain within [-1, 1].");
        }
    }
}
