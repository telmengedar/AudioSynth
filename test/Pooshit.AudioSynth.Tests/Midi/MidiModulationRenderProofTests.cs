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
    /// Deliverable-proof tests (DiVoid #7181, design #7180): a count-regression guard on the real
    /// <c>1-01-Liberi_Fatali.mid</c> deliverable song's diagnosed 471 CC1 events on channels 2/3/5, and a
    /// real render through the Florestan GM SF2 confirming non-silent audio with mod-wheel vibrato
    /// applied. Both real-asset tests skip gracefully when the dev-tree assets are absent, mirroring
    /// <see cref="MidiSustainRenderProofTests"/> and <see cref="MidiPitchBendRenderProofTests"/>.
    /// </summary>
    [TestFixture]
    public class MidiModulationRenderProofTests {

        static readonly AudioFormat Format = new AudioFormat(SynthesizerOptions.DefaultSampleRate, 1);

        static string? FindDevTreeAsset(string subfolder, string fileName) {
            string? dir = Path.GetDirectoryName(typeof(MidiModulationRenderProofTests).Assembly.Location);
            while (dir != null) {
                string candidate = Path.Combine(dir, "Source", "AudioSynthesis.Tests", subfolder, fileName);
                if (File.Exists(candidate))
                    return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        [Test]
        [Description("Real-song regression guard: 1-01-Liberi_Fatali.mid's diagnosed 471 CC1 events on " +
                     "channels 2/3/5 (design §1/§13 O3) must all reach MidiSequencer.ApplyMessage and fan " +
                     "out as SetChannelModulation calls. Skips gracefully when the dev-tree asset is absent.")]
        public void RealSong_LiberiFatali_ModulationEventsReachSequencer() {
            string? songPath = FindDevTreeAsset("Midi", "1-01-Liberi_Fatali.mid");
            if (songPath is null) {
                Assert.Ignore("1-01-Liberi_Fatali.mid dev-tree asset not found; skipping the modulation count regression guard.");
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

            Assert.That(synth.ChannelModulationCalls, Has.Count.EqualTo(471),
                "the diagnosed CC1 event count for 1-01-Liberi_Fatali.mid (design §1/§13 O3) must all reach SetChannelModulation.");

            int leadChannelCalls = 0;
            foreach ((int channel, float _) in synth.ChannelModulationCalls) {
                if (channel == 2 || channel == 3 || channel == 5)
                    leadChannelCalls++;
            }
            Assert.That(leadChannelCalls, Is.EqualTo(471),
                "the diagnosed modulation activity for this song is entirely on channels 2/3/5; " +
                $"only {leadChannelCalls} of {synth.ChannelModulationCalls.Count} calls targeted them.");
        }

        [Test]
        [Description("Deliverable render: 1-01-Liberi_Fatali.mid through the Florestan GM SF2 renders " +
                     "non-silent, in-range audio with mod-wheel vibrato applied. Skips gracefully when the " +
                     "dev-tree assets are absent.")]
        public void RealSong_LiberiFatali_ThroughFlorestan_RendersNonSilent() {
            string? soundfontPath = FindDevTreeAsset("Soundfonts", "__Florestan_Basic_GM_GS.sf2");
            string? songPath = FindDevTreeAsset("Midi", "1-01-Liberi_Fatali.mid");
            if (soundfontPath is null || songPath is null) {
                Assert.Ignore("MIDI/SoundFont dev-tree assets not found; skipping the modulation deliverable render.");
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
            Assert.That(peak, Is.GreaterThan(0.01f), "rendering the modulation deliverable song must not be silent.");
            Assert.That(peak, Is.LessThanOrEqualTo(1f), "every sample must remain within [-1, 1].");
        }
    }
}
