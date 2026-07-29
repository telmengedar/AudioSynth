using System.IO;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Formats.Midi;
using Pooshit.AudioSynth.Sequencing;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Tests.Helpers;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// <see cref="MidiSequencer.Render"/> MIDI Bank Select (CC0/CC32) routing tests (design #7251):
    /// per-channel bank-select is a pure state write, latched only by the next <c>ProgramChange</c>
    /// (running-status behavior); the percussion channel (index 9) always resolves within bank 128
    /// regardless of bank-select; and a GM-only bank exhibits the rung-2 regression-guard parity.
    /// </summary>
    [TestFixture]
    public class MidiSequencerBankSelectTests {

        const int PercussionChannel = 9;
        const byte BankSelectMsb = 0;
        const byte BankSelectLsb = 32;
        static readonly AudioFormat Format = new AudioFormat(44100, 2);

        static TimedMessageSequence BuildSequence(params MidiTrackEventBuilder[] builders) {
            byte[][] chunks = new byte[builders.Length][];
            for (int i = 0; i < builders.Length; i++)
                chunks[i] = builders[i].EndOfTrack().BuildChunk();
            MidiFile file = MidiFile.Read(new MemoryStream(MidiTestBuilder.BuildFile(480, chunks)));
            return new TimedMessageSequence(file);
        }

        [Test]
        [Description("Latch semantics (design #7251 §9): CC0 and CC32 alone are pure state writes and " +
                     "must never trigger an additional SetChannelPatch call beyond the 16 GM-reset calls.")]
        public void Render_BankSelectAlone_ProducesNoAdditionalSetChannelPatchCall() {
            StubPatch piano = new StubPatch("piano");
            SoundBank bank = new SoundBank(new[] {
                (0, 0, (IPatch)piano),
            });
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder()
                    .Controller(0, 0, BankSelectMsb, 8)
                    .Controller(0, 0, BankSelectLsb, 3));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), bank);

            Assert.That(synth.ChannelPatchCalls, Has.Count.EqualTo(16),
                "Only the 16 GM-reset SetChannelPatch calls must occur; CC0/CC32 alone must not add a 17th.");
        }

        [Test]
        [Description("Success criterion 1 (design #7251): CC0=8 (bank MSB) then a ProgramChange on a melodic " +
                     "channel latches the (8, program) patch, not the GM-reset (0, 0) patch.")]
        public void Render_BankSelectThenProgramChangeOnMelodicChannel_ResolvesSelectedBank() {
            StubPatch piano = new StubPatch("piano");
            StubPatch variation = new StubPatch("bank8-variation");
            StubPatch kit = new StubPatch("kit");
            SoundBank bank = new SoundBank(new[] {
                (0, 0, (IPatch)piano),
                (8, 5, (IPatch)variation),
                (128, 0, (IPatch)kit),
            });
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder()
                    .Controller(0, 0, BankSelectMsb, 8)
                    .Controller(0, 0, BankSelectLsb, 0)
                    .ProgramChange(0, 0, 5));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), bank);

            (int Channel, IPatch Patch) programChangeCall = synth.ChannelPatchCalls[16];
            Assert.That(programChangeCall.Channel, Is.EqualTo(0));
            Assert.That(programChangeCall.Patch, Is.SameAs(variation),
                "CC0=8, CC32=0, ProgramChange(5) on channel 0 must resolve bank 8/program 5, not GM piano.");
        }

        [Test]
        [Description("Design #7251 §8.1: bank-select on channel 9 (percussion) is ignored — the channel " +
                     "always resolves within bank 128 regardless of the latched MSB/LSB.")]
        public void Render_BankSelectThenProgramChangeOnPercussionChannel_IgnoresBankSelectStaysOnBank128() {
            StubPatch piano = new StubPatch("piano");
            StubPatch kit = new StubPatch("kit");
            SoundBank bank = new SoundBank(new[] {
                (0, 0, (IPatch)piano),
                (128, 0, (IPatch)kit),
            });
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder()
                    .Controller(0, PercussionChannel, BankSelectMsb, 8)
                    .ProgramChange(0, PercussionChannel, 57));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), bank);

            (int Channel, IPatch Patch) programChangeCall = synth.ChannelPatchCalls[16];
            Assert.That(programChangeCall.Channel, Is.EqualTo(PercussionChannel));
            Assert.That(programChangeCall.Patch, Is.SameAs(kit),
                "Channel 9 must resolve within bank 128 (the only loaded percussion preset) even though " +
                "CC0=8 was sent on that channel; bank-select is ignored on the percussion channel.");
        }

        [Test]
        [Description("GM-only-font no-regression (rung-2 parity, design #7251 §10.1): a GM-only bank " +
                     "hearing CC0=8 then ProgramChange(40) must still resolve to (0, 40) — the same GM " +
                     "instrument it would have played before bank-select existed — not the melodic default.")]
        public void Render_BankSelectUnhonorableByGmOnlyFont_ResolvesBankZeroSameProgram() {
            StubPatch piano = new StubPatch("piano");
            StubPatch viola = new StubPatch("viola");
            SoundBank gmOnlyBank = new SoundBank(new[] {
                (0, 0, (IPatch)piano),
                (0, 40, (IPatch)viola),
            });
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder()
                    .Controller(0, 0, BankSelectMsb, 8)
                    .ProgramChange(0, 0, 40));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), gmOnlyBank);

            (int Channel, IPatch Patch) programChangeCall = synth.ChannelPatchCalls[16];
            Assert.That(programChangeCall.Patch, Is.SameAs(viola),
                "A GM-only font cannot honor bank 8; SoundBank rung 2 must fall back to (0, 40) = viola, " +
                "identical to pre-bank-select behavior, never the melodic default (0, 0) = piano.");
        }

        [Test]
        [Description("Design #7251 §9: bank state does not leak across separate Render invocations — a " +
                     "fresh render against the same SoundBank instance GM-resets to bank 0, even though a " +
                     "prior render on the same bank latched a non-zero bank on that channel.")]
        public void Render_AfterPriorRenderLatchedNonZeroBank_FreshRenderStillGmResetsToBankZero() {
            StubPatch piano = new StubPatch("piano");
            StubPatch variation = new StubPatch("bank8-variation");
            SoundBank bank = new SoundBank(new[] {
                (0, 0, (IPatch)piano),
                (8, 5, (IPatch)variation),
            });

            RecordingSynthesizer firstSynth = new RecordingSynthesizer(Format);
            TimedMessageSequence firstSequence = BuildSequence(
                new MidiTrackEventBuilder()
                    .Controller(0, 0, BankSelectMsb, 8)
                    .ProgramChange(0, 0, 5));
            MidiSequencer.Render(firstSequence, firstSynth, new InMemoryAudioSink(Format), bank);
            Assert.That(firstSynth.ChannelPatchCalls[16].Patch, Is.SameAs(variation),
                "Sanity check: the first render did latch bank 8 on channel 0.");

            RecordingSynthesizer secondSynth = new RecordingSynthesizer(Format);
            TimedMessageSequence secondSequence = BuildSequence(new MidiTrackEventBuilder());
            MidiSequencer.Render(secondSequence, secondSynth, new InMemoryAudioSink(Format), bank);

            Assert.That(secondSynth.ChannelPatchCalls[0].Patch, Is.SameAs(piano),
                "A fresh Render call must not inherit bank state from a prior render; channel 0 must " +
                "GM-reset to bank 0 (piano), even though the same SoundBank previously resolved bank 8.");
        }
    }
}
