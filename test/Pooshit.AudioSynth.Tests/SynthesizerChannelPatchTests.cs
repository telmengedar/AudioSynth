using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Tests.Helpers;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Per-channel patch state tests (DiVoid #7117 §14.2): <see cref="Synthesizer.NoteOn"/> starts
    /// voices from the channel's current patch, and <see cref="Synthesizer.SetChannelPatch"/> guards
    /// its channel range.
    /// </summary>
    [TestFixture]
    public class SynthesizerChannelPatchTests {

        static SynthesizerOptions Options() => new SynthesizerOptions(44100, 2, 64, 16);

        [Test]
        [Description("NoteOn on distinct channels starts voices from each channel's own current patch.")]
        public void NoteOn_DistinctChannels_StartVoiceOnDistinctPatches() {
            RecordingPatch ctorPatch = new RecordingPatch();
            RecordingPatch channelOnePatch = new RecordingPatch();
            Synthesizer synth = new Synthesizer(Options(), ctorPatch);
            synth.SetChannelPatch(1, channelOnePatch);

            synth.NoteOn(0, 60, 100);
            synth.NoteOn(1, 64, 90);

            Assert.That(ctorPatch.StartVoiceCalls, Has.Count.EqualTo(1).And.Contains((60, 100)));
            Assert.That(channelOnePatch.StartVoiceCalls, Has.Count.EqualTo(1).And.Contains((64, 90)));
        }

        [Test]
        [Description("A channel that never receives SetChannelPatch still uses the constructor's patch.")]
        public void NoteOn_UnsetChannel_UsesConstructorPatch() {
            RecordingPatch ctorPatch = new RecordingPatch();
            Synthesizer synth = new Synthesizer(Options(), ctorPatch);

            synth.NoteOn(5, 72, 110);

            Assert.That(ctorPatch.StartVoiceCalls, Has.Count.EqualTo(1).And.Contains((72, 110)));
        }

        [Test]
        [Description("A later SetChannelPatch call changes only future NoteOn calls on that channel.")]
        public void SetChannelPatch_ChangesOnlyFutureNoteOns() {
            RecordingPatch first = new RecordingPatch();
            RecordingPatch second = new RecordingPatch();
            Synthesizer synth = new Synthesizer(Options(), first);

            synth.NoteOn(2, 60, 100);
            synth.SetChannelPatch(2, second);
            synth.NoteOn(2, 61, 101);

            Assert.That(first.StartVoiceCalls, Has.Count.EqualTo(1).And.Contains((60, 100)));
            Assert.That(second.StartVoiceCalls, Has.Count.EqualTo(1).And.Contains((61, 101)));
        }

        [TestCase(-1)]
        [TestCase(16)]
        [Description("SetChannelPatch rejects a channel outside [0,15].")]
        public void SetChannelPatch_ChannelOutOfRange_Throws(int channel) {
            Synthesizer synth = new Synthesizer(Options(), new RecordingPatch());

            Assert.Throws<ArgumentOutOfRangeException>(() => synth.SetChannelPatch(channel, new RecordingPatch()));
        }

        [TestCase(-1)]
        [TestCase(16)]
        [Description("NoteOn rejects a channel outside [0,15].")]
        public void NoteOn_ChannelOutOfRange_Throws(int channel) {
            Synthesizer synth = new Synthesizer(Options(), new RecordingPatch());

            Assert.Throws<ArgumentOutOfRangeException>(() => synth.NoteOn(channel, 60, 100));
        }

        [Test]
        [Description("SetChannelPatch rejects a null patch.")]
        public void SetChannelPatch_NullPatch_Throws() {
            Synthesizer synth = new Synthesizer(Options(), new RecordingPatch());

            Assert.Throws<ArgumentNullException>(() => synth.SetChannelPatch(0, null!));
        }
    }
}
