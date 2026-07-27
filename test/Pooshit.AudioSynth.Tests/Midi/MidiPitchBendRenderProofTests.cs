using System;
using System.IO;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Formats.Midi;
using Pooshit.AudioSynth.Formats.Sf2;
using Pooshit.AudioSynth.Sequencing;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;
using Pooshit.AudioSynth.Tests.Helpers;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Deliverable-proof tests (DiVoid #7140): a synthetic full-stack render proves a single
    /// <c>PitchWheel</c> message glides a sounding voice's pitch through the real
    /// <see cref="MidiSequencer.Render"/> → <see cref="Synthesizer"/> → <see cref="Synthesis.Voices.SamplePlaybackVoice"/>
    /// pipeline (mirrors <see cref="MixBusRenderProofTests"/>); a count check confirms the real
    /// <c>07dkc2bram.mid</c> deliverable song's 1196 diagnosed PitchWheel events reach the sequencer
    /// on the diagnosed lead channels; a real render through the Florestan GM SF2 confirms non-silent
    /// audio. Both real-asset tests skip gracefully when the dev-tree assets are absent.
    /// </summary>
    [TestFixture]
    public class MidiPitchBendRenderProofTests {

        const int SampleRate = 44100;
        const int ControlRateFrames = 64;
        const int TicksPerQuarterNote = 480;

        static readonly EnvelopeParameters InstantSustainEnvelope = new EnvelopeParameters(0f, 0f, 0f, 0f, 1f, 0f);
        static readonly AudioFormat Format = new AudioFormat(SampleRate, 1);

        static string? FindDevTreeAsset(string subfolder, string fileName) {
            string? dir = Path.GetDirectoryName(typeof(MidiPitchBendRenderProofTests).Assembly.Location);
            while (dir != null) {
                string candidate = Path.Combine(dir, "Source", "AudioSynthesis.Tests", subfolder, fileName);
                if (File.Exists(candidate))
                    return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        static SampleRegion BuildRampRegion(float scale, int length) {
            float[] buf = new float[length];
            for (int i = 0; i < length; i++)
                buf[i] = i * scale;
            return new SampleRegion(buf, 0, length, 0, length, LoopMode.NoLoop, SampleRate, 60, 0,
                InstantSustainEnvelope, FilterParameters.Default, LfoParameters.Default);
        }

        static SoundBank SingleRampPresetBank(SampleRegion region) {
            SamplePatch patch = new SamplePatch(region, SampleRate);
            return new SoundBank(new[] { (0, 0, (IPatch)patch) });
        }

        [Test]
        [Description("Deliverable proof: through the real MidiSequencer.Render -> Synthesizer -> SamplePlaybackVoice " +
                     "pipeline, a single PitchWheel(+1 semitone) message glides the sounding voice's read-increment " +
                     "from the base 1.0 to 2^(1/12), rather than the note playing unbent throughout.")]
        public void Render_SinglePitchWheelEvent_GlidesSoundingVoiceThroughFullStack() {
            const float scale = 0.001f;
            const float semitones = 1f;
            const int value14 = 12288; // 8192 + 4096*1 => +1 semitone under the GM ±2 default.
            float expectedFactor = (float)Math.Pow(2.0, semitones / 12.0);

            SampleRegion region = BuildRampRegion(scale, 300_000);
            SoundBank bank = SingleRampPresetBank(region);

            byte[] trackBody = new MidiTrackEventBuilder()
                .NoteOn(0, 0, 60, 127)
                .PitchWheel(20, 0, value14)
                .EndOfTrack()
                .BuildChunk();
            MidiFile file = MidiFile.Read(new MemoryStream(MidiTestBuilder.BuildFile(TicksPerQuarterNote, new[] { trackBody })));
            TimedMessageSequence sequence = new TimedMessageSequence(file);

            ScheduledMidiEvent[] schedule = MidiSequencer.BuildSchedule(sequence, SampleRate);
            long pitchWheelSampleOffset = schedule[1].SampleOffset;

            SynthesizerOptions options = new SynthesizerOptions(SampleRate, 1, ControlRateFrames, 16);
            Synthesizer synth = new Synthesizer(options, bank.GetPatch(0, 0));
            InMemoryAudioSink sink = new InMemoryAudioSink(Format);

            MidiSequencer.Render(sequence, synth, sink, bank);
            float[] output = sink.ToArray();

            int beforeFrame = (int)pitchWheelSampleOffset - ControlRateFrames * 3;
            float measuredBefore = (output[beforeFrame + 1] - output[beforeFrame]) / scale;
            Assert.That(Math.Abs(measuredBefore), Is.GreaterThan(0.1f),
                $"before the PitchWheel event the voice must be audibly sounding; measured raw increment {measuredBefore}.");

            int afterFrame = (int)pitchWheelSampleOffset + ControlRateFrames * 5;
            float measuredAfter = (output[afterFrame + 1] - output[afterFrame]) / scale;

            float measuredRatio = measuredAfter / measuredBefore;
            Assert.That(measuredRatio, Is.EqualTo(expectedFactor).Within(0.01f),
                $"the PitchWheel event must glide the voice's read-increment by {expectedFactor}x " +
                $"(channel gain and pan cancel out of the ratio); measured ratio {measuredRatio} " +
                $"(before={measuredBefore}, after={measuredAfter}).");
        }

        [Test]
        [Description("Real-song regression guard: 07dkc2bram.mid's diagnosed 1196 PitchWheel events (DiVoid #7154) " +
                     "must all reach MidiSequencer.ApplyMessage and fan out as SetChannelPitchBend calls, concentrated " +
                     "on the diagnosed lead channels 10-13. Skips gracefully when the dev-tree asset is absent.")]
        public void RealSong_07dkc2bram_PitchWheelEventsReachSequencer() {
            string? songPath = FindDevTreeAsset("Midi", "07dkc2bram.mid");
            if (songPath is null) {
                Assert.Ignore("07dkc2bram.mid dev-tree asset not found; skipping the pitch-bend count regression guard.");
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

            Assert.That(synth.ChannelPitchBendCalls, Has.Count.EqualTo(1196),
                "the diagnosed PitchWheel event count for 07dkc2bram.mid (DiVoid #7154) must all reach SetChannelPitchBend.");

            int leadChannelCalls = 0;
            foreach ((int channel, float _) in synth.ChannelPitchBendCalls) {
                if (channel >= 10 && channel <= 13)
                    leadChannelCalls++;
            }
            Assert.That(leadChannelCalls, Is.GreaterThan(1000),
                "the diagnosed bend activity is concentrated on lead channels 10-13; " +
                $"only {leadChannelCalls} of {synth.ChannelPitchBendCalls.Count} calls targeted them.");
        }

        [Test]
        [Description("Deliverable render: 07dkc2bram.mid through the Florestan GM SF2 renders non-silent, in-range " +
                     "audio with pitch bend applied. Skips gracefully when the dev-tree assets are absent.")]
        public void RealSong_07dkc2bram_ThroughFlorestan_RendersNonSilent() {
            string? soundfontPath = FindDevTreeAsset("Soundfonts", "__Florestan_Basic_GM_GS.sf2");
            string? songPath = FindDevTreeAsset("Midi", "07dkc2bram.mid");
            if (soundfontPath is null || songPath is null) {
                Assert.Ignore("MIDI/SoundFont dev-tree assets not found; skipping the pitch-bend deliverable render.");
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
            Assert.That(peak, Is.GreaterThan(0.01f), "rendering the pitch-bend deliverable song must not be silent.");
            Assert.That(peak, Is.LessThanOrEqualTo(1f), "every sample must remain within [-1, 1].");
        }
    }
}
