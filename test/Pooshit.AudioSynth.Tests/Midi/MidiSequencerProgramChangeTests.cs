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
    /// <see cref="MidiSequencer.Render"/> GM-routing tests (DiVoid #7117 §14.4): the GM reset seeds
    /// all 16 channels, <c>ProgramChange</c> updates a channel's patch, and channel 9 always resolves
    /// within the percussion bank (128).
    /// </summary>
    [TestFixture]
    public class MidiSequencerProgramChangeTests {

        const int PercussionChannel = 9;
        static readonly AudioFormat Format = new AudioFormat(44100, 2);

        static TimedMessageSequence BuildSequence(params MidiTrackEventBuilder[] builders) {
            byte[][] chunks = new byte[builders.Length][];
            for (int i = 0; i < builders.Length; i++)
                chunks[i] = builders[i].EndOfTrack().BuildChunk();
            MidiFile file = MidiFile.Read(new MemoryStream(MidiTestBuilder.BuildFile(480, chunks)));
            return new TimedMessageSequence(file);
        }

        [Test]
        [Description("Success criteria 2/3: before any event is applied, Render GM-resets every channel, "
                   + "routing channel 9 to bank 128 and every other channel to bank 0.")]
        public void Render_Always_GmResetsAllChannelsWithChannelNineOnPercussion() {
            StubPatch piano = new StubPatch("piano");
            StubPatch kit = new StubPatch("kit");
            SoundBank bank = new SoundBank(new[] {
                (0, 0, (IPatch)piano),
                (128, 0, (IPatch)kit),
            });
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(new MidiTrackEventBuilder());

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), bank);

            Assert.That(synth.ChannelPatchCalls, Has.Count.GreaterThanOrEqualTo(16));
            for (int channel = 0; channel < 16; channel++) {
                (int Channel, IPatch Patch) call = synth.ChannelPatchCalls[channel];
                Assert.That(call.Channel, Is.EqualTo(channel));
                Assert.That(call.Patch, Is.SameAs(channel == PercussionChannel ? kit : piano),
                    $"channel {channel} must GM-reset to {(channel == PercussionChannel ? "the percussion kit" : "GM piano")}.");
            }
        }

        [Test]
        [Description("Success criterion 1: a ProgramChange on a melodic channel causes the next "
                   + "SetChannelPatch on that channel to carry the program-resolved patch.")]
        public void Render_ProgramChangeOnMelodicChannel_ResolvesBankZeroProgram() {
            StubPatch piano = new StubPatch("piano");
            StubPatch epiano = new StubPatch("epiano");
            StubPatch kit = new StubPatch("kit");
            SoundBank bank = new SoundBank(new[] {
                (0, 0, (IPatch)piano),
                (0, 5, (IPatch)epiano),
                (128, 0, (IPatch)kit),
            });
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().ProgramChange(0, 0, 5));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), bank);

            (int Channel, IPatch Patch) programChangeCall = synth.ChannelPatchCalls[16];
            Assert.That(programChangeCall.Channel, Is.EqualTo(0));
            Assert.That(programChangeCall.Patch, Is.SameAs(epiano),
                "ProgramChange(channel 0, program 5) must resolve bank 0/program 5, not the GM-reset piano.");
        }

        [Test]
        [Description("Success criterion 2: channel 9 always resolves within bank 128, even for a "
                   + "program number that only exists as a melodic preset.")]
        public void Render_ProgramChangeOnChannelNine_ResolvesPercussionBankRegardlessOfProgram() {
            StubPatch piano = new StubPatch("piano");
            StubPatch kit = new StubPatch("kit");
            SoundBank bank = new SoundBank(new[] {
                (0, 0, (IPatch)piano),
                (128, 0, (IPatch)kit),
            });
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);
            TimedMessageSequence sequence = BuildSequence(
                new MidiTrackEventBuilder().ProgramChange(0, PercussionChannel, 57));

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), bank);

            (int Channel, IPatch Patch) programChangeCall = synth.ChannelPatchCalls[16];
            Assert.That(programChangeCall.Channel, Is.EqualTo(PercussionChannel));
            Assert.That(programChangeCall.Patch, Is.SameAs(kit),
                "Channel 9 must resolve within bank 128 (the only loaded percussion preset), never a melodic bank.");
        }
    }
}
