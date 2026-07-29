using System;
using System.IO;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Formats.Midi;
using Pooshit.AudioSynth.Sequencing;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;
using Pooshit.AudioSynth.Tests.Helpers;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// <see cref="MidiSequencer.Render"/> routing tests for the Tier-1 GM housekeeping channel-mode
    /// controllers (DiVoid task #7243, design #7245): CC120 (All Sound Off) routes to
    /// <see cref="ISynthesizer.SilenceChannel"/>, CC123 (All Notes Off) routes to
    /// <see cref="ISynthesizer.ReleaseAllNotes"/>, and CC121 (Reset All Controllers) composes existing
    /// seams via a strict GM-RAC subset — modulation, expression (gain, with volume preserved), sustain,
    /// pitch-bend and the RPN selector — while leaving pan, program/bank, reverb send and chorus send
    /// untouched.
    /// </summary>
    [TestFixture]
    public class MidiSequencerHousekeepingControllersTests {

        static readonly AudioFormat Format = new AudioFormat(44100, 2);

        static TimedMessageSequence BuildSequence(params MidiTrackEventBuilder[] builders) {
            byte[][] chunks = new byte[builders.Length][];
            for (int i = 0; i < builders.Length; i++)
                chunks[i] = builders[i].EndOfTrack().BuildChunk();
            MidiFile file = MidiFile.Read(new MemoryStream(MidiTestBuilder.BuildFile(480, chunks)));
            return new TimedMessageSequence(file);
        }

        static SoundBank SinglePresetBank() {
            StubPatch piano = new StubPatch("piano");
            return new SoundBank(new[] {
                (0, 0, (IPatch)piano),
                (128, 0, (IPatch)piano),
            });
        }

        [Test]
        [Description("CC120 (All Sound Off) must route to exactly one SilenceChannel call for the message's " +
                     "channel, and must not touch ReleaseAllNotes.")]
        public void Render_Cc120_RoutesToSilenceChannel() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().Controller(0, 3, 120, 0));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.SilenceChannelCalls, Is.EqualTo(new[] { 3 }),
                "CC120 must fire exactly one SilenceChannel(3) call.");
            Assert.That(synth.ReleaseAllNotesCalls, Is.Empty,
                "CC120 must never call ReleaseAllNotes.");
        }

        [Test]
        [Description("CC123 (All Notes Off) must route to exactly one ReleaseAllNotes call for the message's " +
                     "channel, and must not touch SilenceChannel.")]
        public void Render_Cc123_RoutesToReleaseAllNotes() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().Controller(0, 5, 123, 0));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.ReleaseAllNotesCalls, Is.EqualTo(new[] { 5 }),
                "CC123 must fire exactly one ReleaseAllNotes(5) call.");
            Assert.That(synth.SilenceChannelCalls, Is.Empty,
                "CC123 must never call SilenceChannel.");
        }

        [Test]
        [Description("CC120 and CC123 on the same channel must dispatch to their own distinct engine seam " +
                      "each — a hard fast-fade (SilenceChannel) is not interchangeable with a sustain-respecting " +
                      "release (ReleaseAllNotes).")]
        public void Render_Cc120AndCc123_DispatchToDistinctSeams() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder()
                    .Controller(0, 2, 120, 0)
                    .Controller(0, 2, 123, 0));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.SilenceChannelCalls, Is.EqualTo(new[] { 2 }));
            Assert.That(synth.ReleaseAllNotesCalls, Is.EqualTo(new[] { 2 }));
        }

        [Test]
        [Description("CC121 (Reset All Controllers) must reset modulation to 0, sustain to off, and pitch-bend " +
                     "to center, and must reset expression (recomputing gain) while preserving the channel's " +
                     "current CC7 volume rather than snapping it to the GM default.")]
        public void Render_Cc121_ResetsModExpressionSustainAndBend_PreservingVolume() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder()
                    .Controller(0, 4, 7, 80)   // CC7 volume -> 80 (non-default)
                    .Controller(0, 4, 11, 40)  // CC11 expression -> 40 (non-default)
                    .Controller(0, 4, 1, 100)  // CC1 modulation -> non-zero
                    .Controller(0, 4, 64, 127) // CC64 sustain -> down
                    .PitchWheel(0, 4, 12000)   // pitch-bend away from center
                    .Controller(0, 4, 121, 0)); // CC121 Reset All Controllers

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.ChannelModulationCalls[^1], Is.EqualTo((4, 0f)),
                "CC121 must reset modulation (CC1) to 0.");
            Assert.That(synth.ChannelSustainCalls[^1], Is.EqualTo((4, false)),
                "CC121 must reset sustain (CC64) to off.");
            Assert.That(synth.ChannelPitchBendCalls[^1].Channel, Is.EqualTo(4));
            Assert.That(synth.ChannelPitchBendCalls[^1].Semitones, Is.EqualTo(0f).Within(1e-6f),
                "CC121 must reset pitch-bend to center.");

            float expectedGain = (80f / 127f) * (80f / 127f) * (127f / 127f) * (127f / 127f);
            (int channel, float gain) lastGain = synth.ChannelGainCalls[^1];
            Assert.That(lastGain.channel, Is.EqualTo(4));
            Assert.That(lastGain.gain, Is.EqualTo(expectedGain).Within(1e-5f),
                "CC121 must reset expression (CC11) to 127 while preserving the previously-set CC7=80 volume.");
        }

        [Test]
        [Description("CC121 must not touch pan, reverb send, chorus send, or program/bank — only the GM-reset " +
                     "startup calls (one per channel) may appear for those seams; CC121 adds none.")]
        public void Render_Cc121_DoesNotTouchPanReverbChorusOrProgram() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().Controller(0, 6, 121, 0));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            Assert.That(synth.ChannelPanCalls, Has.Count.EqualTo(16),
                "CC121 must not add a SetChannelPan call beyond the 16 GM-reset calls.");
            Assert.That(synth.ChannelReverbSendCalls, Has.Count.EqualTo(16),
                "CC121 must not add a SetChannelReverbSend call beyond the 16 GM-reset calls.");
            Assert.That(synth.ChannelChorusSendCalls, Has.Count.EqualTo(16),
                "CC121 must not add a SetChannelChorusSend call beyond the 16 GM-reset calls.");
            Assert.That(synth.ChannelPatchCalls, Has.Count.EqualTo(16),
                "CC121 must not add a SetChannelPatch call beyond the 16 GM-reset calls.");
        }

        [Test]
        [Description("CC121 must reset the RPN selector to null: an RPN 0 armed just before CC121 must not " +
                     "let a subsequent Data Entry (CC6) change the channel's pitch-bend range — proven by a " +
                     "following PitchWheel message still resolving against the GM-default 2-semitone range.")]
        public void Render_Cc121_ResetsRpnSelector_SoSubsequentDataEntryIsIgnored() {
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder()
                    .Controller(0, 7, 101, 0)   // RPN coarse = 0
                    .Controller(0, 7, 100, 0)   // RPN fine = 0 -> RPN 0 (pitch-bend range) armed
                    .Controller(0, 7, 121, 0)   // CC121 -> nulls the RPN selector
                    .Controller(0, 7, 6, 24)    // Data Entry: would set bend range to 24 semitones if honored
                    .PitchWheel(0, 7, 16383));  // full-scale bend: value14=16383 -> +1 * bendRange semitones

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), SinglePresetBank());

            (int channel, float semitones) lastBend = synth.ChannelPitchBendCalls[^1];
            Assert.That(lastBend.channel, Is.EqualTo(7));
            Assert.That(lastBend.semitones, Is.EqualTo(2f).Within(1e-3f),
                "with the RPN selector nulled by CC121, the post-CC121 Data Entry must be ignored, so the " +
                "final PitchWheel must still resolve against the GM-default 2-semitone bend range, not 24.");
        }

        /// <summary>
        /// MIDI division (ticks per quarter note) paired with <see cref="RealEngineTempoMicroseconds"/> so
        /// that, at <see cref="RealEngineSampleRate"/>, exactly one tick equals exactly one rendered sample
        /// frame (1,000,000 / 10,000 / 441 = 1 / 44100) -- letting delta-tick counts below be read directly
        /// as frame offsets, mirroring the frame-based conventions of <c>SynthesizerReleaseAllNotesTests</c>.
        /// </summary>
        const short RealEngineDivision = 441;

        const int RealEngineTempoMicroseconds = 10000;

        const int RealEngineSampleRate = 44100;

        static TimedMessageSequence BuildRealEngineSequence(params MidiTrackEventBuilder[] builders) {
            byte[][] chunks = new byte[builders.Length][];
            for (int i = 0; i < builders.Length; i++)
                chunks[i] = builders[i].EndOfTrack().BuildChunk();
            MidiFile file = MidiFile.Read(new MemoryStream(MidiTestBuilder.BuildFile(RealEngineDivision, chunks)));
            return new TimedMessageSequence(file);
        }

        static SampleRegion BuildSustainedDcRegion(float value, int length) {
            float[] buf = new float[length];
            for (int i = 0; i < length; i++)
                buf[i] = value;
            return new SampleRegion(buf, 0, length, 0, length, LoopMode.Continuous, RealEngineSampleRate, 60, 0,
                EnvelopeParameters.Default, FilterParameters.Default, LfoParameters.Default, 0f);
        }

        static float MeanAbs(float[] samples, int start, int windowFrames) {
            double sum = 0.0;
            for (int i = start; i < start + windowFrames; i++)
                sum += Math.Abs(samples[i]);
            return (float)(sum / windowFrames);
        }

        [Test]
        [Description("Task #7249 W2, design §8/§15 end-to-end proof: CC121 must not merely call " +
                     "SetChannelSustain(false) (already proven by the routing tests above against a spy) -- " +
                     "against a REAL Synthesizer through the sequencer, a voice deferred by a held sustain " +
                     "pedal (NoteOff arrived, pedal still down) must actually be released by CC121's " +
                     "sustain-off step, not merely left flagged.")]
        public void Render_Cc121_ThroughRealSynthesizer_ActuallyReleasesSustainDeferredVoice() {
            const int channel = 3;
            const int key = 60;
            const float value = 0.4f;
            const int settleFrames = 500;
            const int holdFrames = 400;
            const int postReleaseFrames = 400;
            const int measureWindowFrames = 50;

            TimedMessageSequence sequence = BuildRealEngineSequence(
                new MidiTrackEventBuilder()
                    .Tempo(0, RealEngineTempoMicroseconds)
                    .Controller(0, channel, 64, 127)       // sustain pedal down
                    .NoteOn(0, channel, key, 127)
                    .NoteOff(settleFrames, channel, key)   // deferred by the held pedal, not released
                    .Controller(holdFrames, channel, 121, 0)); // CC121 Reset All Controllers

            AudioFormat format = new AudioFormat(RealEngineSampleRate, 1);
            SoundBank bank = new SoundBank(new[] {
                (0, 0, (IPatch)new SamplePatch(BuildSustainedDcRegion(value, 4096), RealEngineSampleRate)),
                (128, 0, (IPatch)new SamplePatch(BuildSustainedDcRegion(value, 4096), RealEngineSampleRate)),
            });
            SynthesizerOptions options = new SynthesizerOptions(RealEngineSampleRate, 1, 64, 4);
            Synthesizer synthesizer = new Synthesizer(options, bank.GetPatch(0, 0));
            InMemoryAudioSink sink = new InMemoryAudioSink(format);

            MidiSequencer.Render(sequence, synthesizer, sink, bank);
            float[] samples = sink.ToArray();

            // Sample-accurate offsets: with RealEngineDivision/RealEngineTempoMicroseconds chosen so one
            // tick == one frame, these indices land exactly where each event fires.
            int noteOffOffset = settleFrames;
            int cc121Offset = settleFrames + holdFrames;

            float baseline = MeanAbs(samples, noteOffOffset - measureWindowFrames, measureWindowFrames);
            float heldAfterNoteOff = MeanAbs(samples, cc121Offset - measureWindowFrames, measureWindowFrames);
            float afterCc121 = MeanAbs(samples, cc121Offset + postReleaseFrames - measureWindowFrames, measureWindowFrames);

            TestContext.WriteLine($"Baseline (pre-NoteOff) level: {baseline:F6}; held (post-NoteOff, pre-CC121) " +
                                   $"level: {heldAfterNoteOff:F6}; post-CC121 level: {afterCc121:F6}.");

            Assert.That(heldAfterNoteOff, Is.GreaterThan(baseline * 0.9f),
                "with the pedal held, NoteOff must defer the release -- immediately before CC121 the voice " +
                "must still be sounding at essentially its settled level.");
            Assert.That(afterCc121, Is.LessThan(baseline * 0.05f),
                "CC121's sustain-off step must actually run the pedal-up release sweep, silencing the " +
                "deferred voice -- not merely record that SetChannelSustain(false) was called.");
        }
    }
}
