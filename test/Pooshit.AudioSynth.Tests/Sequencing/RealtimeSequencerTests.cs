using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Formats.Midi;
using Pooshit.AudioSynth.Sequencing;
using Pooshit.AudioSynth.Sequencing.Timeline;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;
using Pooshit.AudioSynth.Tests.Helpers;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// <see cref="RealtimeSequencer"/> tests: sub-block dispatch precision, loop cursor-jump semantics,
    /// and the central bit-parity oracle vs offline <see cref="MidiSequencer.Render"/>.
    /// </summary>
    [TestFixture]
    public class RealtimeSequencerTests {

        static readonly AudioFormat MonoFormat = new AudioFormat(44100, 1);

        static SoundBank SinglePatchBank(IPatch patch) => new SoundBank(new[] { (0, 0, patch), (128, 0, patch) });

        static SampleRegion BuildSustainedDcRegion(float value, int length) {
            float[] buffer = new float[length];
            for (int i = 0; i < length; i++)
                buffer[i] = value;
            return new SampleRegion(buffer, 0, length, 0, length, LoopMode.Continuous, MonoFormat.SampleRate, 60, 0,
                EnvelopeParameters.Default, FilterParameters.Default, LfoParameters.Default, 0f);
        }

        [Test]
        [Description("A mid-block event must dispatch at its exact sample offset, splitting the requested " +
                     "block into a gap-render up to the offset and a tail-render after it -- never quantized " +
                     "to the block's own edge.")]
        public void Read_EventMidBlock_DispatchesAtExactOffsetNotBlockEdge() {
            Timeline timeline = new Timeline();
            timeline.Add(100, NeutralEvent.NoteOn(0, 60, 100));
            CompiledSchedule schedule = timeline.Compile();

            CallLoggingSynthesizer synth = new CallLoggingSynthesizer(MonoFormat);
            StubPatch patch = new StubPatch("patch");
            RealtimeSequencer driver = new RealtimeSequencer(schedule, synth, SinglePatchBank(patch), releaseTailFrames: 50);

            float[] buffer = new float[200];
            int produced = driver.Read(buffer);

            Assert.That(produced, Is.EqualTo(150), "150 = the event's offset (100) plus the 50-frame release tail; " +
                "the driver must stop there, signaling end-of-stream, rather than filling the full 200-frame request.");
            Assert.That(synth.CallLog, Is.EqualTo(new[] { "Read(100)", "NoteOn(0,60,100)", "Read(50)" }),
                "the gap before the event must be exactly 100 frames (the event's own offset), not quantized " +
                "to any block boundary; the tail after it must be exactly the release tail.");
        }

        [Test]
        [Description("Reaching loopEnd must jump the cursor to loopStart and re-fire the loop-region events, " +
                     "with no SilenceChannel/reset call at the seam (locked decision: a loop is a pure cursor jump).")]
        public void Read_CursorReachesLoopEnd_JumpsToLoopStartAndRefiresEvents() {
            Timeline timeline = new Timeline();
            timeline.Add(0, NeutralEvent.NoteOn(0, 60, 100));
            timeline.Add(10, NeutralEvent.NoteOff(0, 60));
            CompiledSchedule schedule = timeline.Compile();

            CallLoggingSynthesizer synth = new CallLoggingSynthesizer(MonoFormat);
            StubPatch patch = new StubPatch("patch");
            RealtimeSequencer driver = new RealtimeSequencer(schedule, synth, SinglePatchBank(patch),
                releaseTailFrames: 0, loopStart: 0, loopEnd: 20);

            float[] buffer = new float[45];
            int produced = driver.Read(buffer);

            Assert.That(produced, Is.EqualTo(45), "looped playback never signals end-of-stream.");
            Assert.That(synth.CallLog, Is.EqualTo(new[] {
                "NoteOn(0,60,100)", "Read(10)", "NoteOff(0,60)", "Read(10)",
                "NoteOn(0,60,100)", "Read(10)", "NoteOff(0,60)", "Read(10)",
                "NoteOn(0,60,100)", "Read(5)"
            }), "each 20-frame loop iteration must re-fire NoteOn/NoteOff at their original in-region offsets, " +
                "and no SilenceChannel/reset call may appear anywhere in the log.");
            Assert.That(synth.CallLog, Has.None.Contains("Silence"));
        }

        [TestCase(64)]
        [TestCase(512)]
        [TestCase(1000)]
        [TestCase(4096)]
        [Description("Central parity oracle: pumping the real-time driver dry at any block size must " +
                     "produce audio bit-identical to offline MidiSequencer.Render, proving no " +
                     "block-quantization drift and that Synthesizer.Read is block-size-invariant.")]
        public void Read_PumpedDryAtAnyBlockSize_IsBitIdenticalToOfflineRender(int blockSizeFrames) {
            TimedMessageSequence sequence = BuildSong();
            SoundBank bank = SinglePatchBank(new SamplePatch(BuildSustainedDcRegion(0.4f, 8192), MonoFormat.SampleRate));

            SynthesizerOptions options = new SynthesizerOptions(MonoFormat.SampleRate, MonoFormat.Channels, 64, 8);
            Synthesizer referenceSynth = new Synthesizer(options, bank.GetPatch(0, 0));
            InMemoryAudioSink referenceSink = new InMemoryAudioSink(MonoFormat);
            MidiSequencer.Render(sequence, referenceSynth, referenceSink, bank);
            float[] reference = referenceSink.ToArray();

            Timeline timeline = MidiTimelineImporter.Import(sequence, MonoFormat.SampleRate);
            long releaseTailFrames = (long)(MidiSequencer.ReleaseTailSeconds * MonoFormat.SampleRate);
            Synthesizer probeSynth = new Synthesizer(options, bank.GetPatch(0, 0));
            RealtimeSequencer driver = new RealtimeSequencer(timeline.Compile(), probeSynth, bank, releaseTailFrames);

            List<float> actual = new List<float>();
            float[] block = new float[blockSizeFrames * MonoFormat.Channels];
            int produced;
            do {
                produced = driver.Read(block);
                for (int i = 0; i < produced; i++)
                    actual.Add(block[i]);
            } while (produced == block.Length);

            Assert.That(actual, Is.EqualTo(reference),
                $"block size {blockSizeFrames} must reproduce the offline render bit-for-bit.");
        }

        static TimedMessageSequence BuildSong() {
            byte[] track = new MidiTrackEventBuilder()
                .Tempo(0, 500000)
                .Controller(0, 0, 10, 20)
                .NoteOn(0, 0, 60, 100)
                .Controller(200, 0, 91, 10)
                .PitchWheel(150, 0, 9000)
                .NoteOff(300, 0, 60)
                .EndOfTrack()
                .BuildChunk();
            MidiFile file = MidiFile.Read(new MemoryStream(MidiTestBuilder.BuildFile(480, track)));
            return new TimedMessageSequence(file);
        }
    }
}
