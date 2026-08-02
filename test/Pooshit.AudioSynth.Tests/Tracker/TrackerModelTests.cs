using NUnit.Framework;
using Pooshit.AudioSynth.Formats.Tracker;

namespace Pooshit.AudioSynth.Tests.Tracker {

    /// <summary>
    /// POD model invariants: the all-zero cell is empty, the note sub-column encoding round-trips, and the
    /// flat row-major grid indexes as documented.
    /// </summary>
    [TestFixture, Parallelizable]
    public class TrackerModelTests {

        [Test, Parallelizable]
        public void DefaultCell_IsFullyEmpty() {
            Cell cell = default;

            Assert.That(cell.Note, Is.EqualTo(0));
            Assert.That(cell.Instrument, Is.EqualTo(0));
            Assert.That(cell.Volume, Is.EqualTo(0));
            Assert.That(cell.Effect, Is.EqualTo(TrackerEffectCommand.None));
            Assert.That(cell.EffectParam, Is.EqualTo(0));
            Assert.That(TrackerNotes.IsPlayable(cell.Note), Is.False);
        }

        [TestCase((byte)0, false)]
        [TestCase((byte)1, true)]
        [TestCase((byte)120, true)]
        [TestCase((byte)254, false)]
        [TestCase((byte)255, false)]
        public void IsPlayable_ClassifiesNoteBytes(byte note, bool expected) {
            Assert.That(TrackerNotes.IsPlayable(note), Is.EqualTo(expected));
        }

        [Test, Parallelizable]
        public void KeyOf_And_FromKey_RoundTrip() {
            Assert.That(TrackerNotes.KeyOf(1), Is.EqualTo(0));
            Assert.That(TrackerNotes.KeyOf(60), Is.EqualTo(59));
            Assert.That(TrackerNotes.FromKey(59), Is.EqualTo(60));
            Assert.That(TrackerNotes.FromKey(TrackerNotes.KeyOf(45)), Is.EqualTo(45));
        }

        [Test, Parallelizable]
        [Description("The flat row-major grid indexes as row * channelCount + channel, the stride the importer relies on.")]
        public void FlatGrid_IndexesRowMajor() {
            int channelCount = 3;
            Pattern pattern = new Pattern { Rows = 2, Cells = new Cell[2 * channelCount] };
            pattern.Cells[1 * channelCount + 2] = new Cell { Note = TrackerNotes.FromKey(60) };

            Cell target = pattern.Cells[1 * channelCount + 2];
            Assert.That(TrackerNotes.KeyOf(target.Note), Is.EqualTo(60));
            Assert.That(pattern.Cells[0].Note, Is.EqualTo(0));
        }
    }
}
