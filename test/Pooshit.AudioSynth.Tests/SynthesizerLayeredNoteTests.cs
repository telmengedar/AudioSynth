using NUnit.Framework;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Tests.Helpers;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Engine-level tests for SF2 zone/layer stacking (DiVoid #7282): when a channel's patch is an
    /// <see cref="IMultiVoicePatch"/>, <see cref="Synthesizer.NoteOn"/> must start every layer the patch
    /// resolves, give each its own pool slot sharing <c>(channel, key)</c>, release every layer together
    /// through <see cref="Synthesizer.NoteOff"/>/<see cref="Synthesizer.ReleaseAllNotes"/>/
    /// <see cref="Synthesizer.SilenceChannel"/>, and never let stacked layers of the same note choke each
    /// other via the gen-57 exclusive-class mechanism (DiVoid #7226/#7227).
    /// </summary>
    [TestFixture]
    public class SynthesizerLayeredNoteTests {

        const int SampleRate = 44100;

        static SynthesizerOptions MonoOptions(int maxVoices) => new SynthesizerOptions(SampleRate, 1, 64, maxVoices);

        [Test]
        [Description("A note-on through an IMultiVoicePatch that resolves 3 layers must start all 3 voices " +
                     "(DiVoid #7282 §6.1).")]
        public void NoteOn_MultiVoicePatch_StartsEveryLayer() {
            MultiVoicePatch patch = new MultiVoicePatch(voiceCount: 3);
            Synthesizer synth = new Synthesizer(MonoOptions(maxVoices: 8), patch);

            synth.NoteOn(0, 60, 100);

            Assert.That(patch.StartedVoices, Has.Count.EqualTo(3),
                "all 3 layers the patch resolves must be started for one note-on.");
        }

        [Test]
        [Description("A note-on with zero resolved layers (empty IMultiVoicePatch result) must not throw " +
                     "and must occupy no slot (verified indirectly: no voice recorded).")]
        public void NoteOn_MultiVoicePatch_ZeroLayers_StartsNothing() {
            MultiVoicePatch patch = new MultiVoicePatch(voiceCount: 0);
            Synthesizer synth = new Synthesizer(MonoOptions(maxVoices: 8), patch);

            Assert.DoesNotThrow(() => synth.NoteOn(0, 60, 100));
            Assert.That(patch.StartedVoices, Is.Empty);
        }

        [Test]
        [Description("NoteOff on the (channel, key) of a layered note must release every layer together " +
                     "(DiVoid #7282 §6.3): all layers share the slot-matching key, exactly like a single-voice note.")]
        public void NoteOff_ReleasesEveryLayerOfTheNote() {
            MultiVoicePatch patch = new MultiVoicePatch(voiceCount: 3);
            Synthesizer synth = new Synthesizer(MonoOptions(maxVoices: 8), patch);

            synth.NoteOn(0, 60, 100);
            synth.NoteOff(0, 60);

            foreach (RecordingExclusiveVoice voice in patch.StartedVoices) {
                Assert.That(voice.ReleaseCalled, Is.True,
                    "every layer of the note must receive Release() from a single NoteOff on its (channel, key).");
            }
        }

        [Test]
        [Description("ReleaseAllNotes (All Notes Off) on a layered note's channel must release every layer.")]
        public void ReleaseAllNotes_ReleasesEveryLayer() {
            MultiVoicePatch patch = new MultiVoicePatch(voiceCount: 3);
            Synthesizer synth = new Synthesizer(MonoOptions(maxVoices: 8), patch);

            synth.NoteOn(0, 60, 100);
            synth.ReleaseAllNotes(0);

            foreach (RecordingExclusiveVoice voice in patch.StartedVoices) {
                Assert.That(voice.ReleaseCalled, Is.True,
                    "All Notes Off must release every layer of a stacked note on the channel.");
            }
        }

        [Test]
        [Description("SilenceChannel (All Sound Off) on a layered note's channel must fast-fade every layer.")]
        public void SilenceChannel_FastFadesEveryLayer() {
            MultiVoicePatch patch = new MultiVoicePatch(voiceCount: 3);
            Synthesizer synth = new Synthesizer(MonoOptions(maxVoices: 8), patch);

            synth.NoteOn(0, 60, 100);
            synth.SilenceChannel(0);

            foreach (RecordingExclusiveVoice voice in patch.StartedVoices) {
                Assert.That(voice.FastFadeForStealCalled, Is.True,
                    "All Sound Off must fast-fade every layer of a stacked note on the channel.");
            }
        }

        [Test]
        [Description("Design §9.2 / task #7283: two layers of the SAME note sharing a non-zero exclusive " +
                     "class must NOT choke each other -- the sibling-aware choke excludes the note's own " +
                     "just-placed layers from the victim scan.")]
        public void NoteOn_StackedLayersSameExclusiveClass_DoNotChokeEachOther() {
            const int exclusiveClass = 7;
            MultiVoicePatch patch = new MultiVoicePatch(voiceCount: 2, exclusiveClass: exclusiveClass);
            Synthesizer synth = new Synthesizer(MonoOptions(maxVoices: 8), patch);

            synth.NoteOn(0, 60, 100);

            Assert.That(patch.StartedVoices, Has.Count.EqualTo(2));
            foreach (RecordingExclusiveVoice voice in patch.StartedVoices) {
                Assert.That(voice.FastFadeForStealCalled, Is.False,
                    "stacked layers of one note-on must never choke each other, even sharing an exclusive class.");
            }
        }

        [Test]
        [Description("An external, previously-sounding same-class voice on the same channel must still be " +
                     "choked by a new layered note's exclusive class -- sibling exclusion must not become a " +
                     "blanket suppression of the choke feature.")]
        public void NoteOn_StackedLayers_StillChokesExternalSameClassVoice() {
            const int channel = 0;
            const int exclusiveClass = 7;

            ExclusiveClassPatch priorPatch = new ExclusiveClassPatch(exclusiveClass);
            Synthesizer synth = new Synthesizer(MonoOptions(maxVoices: 8), priorPatch);
            synth.SetChannelPatch(channel, priorPatch);
            synth.NoteOn(channel, 42, 100);
            RecordingExclusiveVoice priorVoice = priorPatch.LastVoice!;

            MultiVoicePatch layeredPatch = new MultiVoicePatch(voiceCount: 2, exclusiveClass: exclusiveClass);
            synth.SetChannelPatch(channel, layeredPatch);
            synth.NoteOn(channel, 60, 100);

            Assert.That(priorVoice.FastFadeForStealCalled, Is.True,
                "a prior external same-class voice on the channel must still be choked by the new layered note.");
        }
    }
}
