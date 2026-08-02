using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Pooshit.AudioSynth.Formats.Tracker;
using Pooshit.AudioSynth.Sequencing;
using Pooshit.AudioSynth.Sequencing.Timeline;

namespace Pooshit.AudioSynth.Tests.Tracker {

    /// <summary>
    /// <see cref="TrackerTimelineImporter"/> lowering: timing, event emission, and validation, asserted on
    /// the compiled schedule.
    /// </summary>
    [TestFixture, Parallelizable]
    public class TrackerTimelineImporterTests {

        const int SampleRate = 44100;

        static Song OneChannelSong(int rows, params (int row, Cell cell)[] cells) {
            Cell[] grid = new Cell[rows];
            foreach ((int row, Cell cell) in cells)
                grid[row] = cell;
            return new Song {
                DefaultBpm = 125,
                DefaultSpeed = 6,
                DefaultRows = rows,
                ChannelCount = 1,
                Instruments = new[] { new Instrument { Bank = 0, Program = 0, Name = "lead" } },
                Patterns = new[] { new Pattern { Rows = rows, Cells = grid } },
                Order = new[] { 0 }
            };
        }

        static Cell Note(int key, int instrument = 0) => new Cell { Note = TrackerNotes.FromKey(key), Instrument = (byte)instrument };

        static List<TimelineEntry> Entries(Song song) => TrackerTimelineImporter.Import(song, SampleRate).Compile().Entries.ToList();

        static List<TimelineEntry> OfKind(Song song, NeutralEventKind kind) =>
            Entries(song).Where(e => e.Event.Kind == kind).ToList();

        [Test, Parallelizable]
        [Description("Each channel is seeded with gain and pan at offset 0, mirroring the MIDI importer's channel reset.")]
        public void Import_SeedsChannelGainAndPanAtOffsetZero() {
            Song song = OneChannelSong(1);

            List<TimelineEntry> entries = Entries(song);

            Assert.That(entries.Any(e => e.Event.Kind == NeutralEventKind.SetGain && e.SampleOffset == 0), Is.True);
            Assert.That(entries.Any(e => e.Event.Kind == NeutralEventKind.SetPan && e.SampleOffset == 0), Is.True);
        }

        [Test, Parallelizable]
        [Description("At speed 6 / tempo 125 / 44100 Hz a row is 5292 samples; successive note rows land at exact multiples.")]
        public void Import_RowOffsets_FollowSpeedTempoClock() {
            Song song = OneChannelSong(3, (0, Note(60, 1)), (1, Note(60, 1)), (2, Note(60, 1)));

            long[] noteOnOffsets = OfKind(song, NeutralEventKind.NoteOn).Select(e => e.SampleOffset).ToArray();

            Assert.That(noteOnOffsets, Is.EqualTo(new long[] { 0, 5292, 10584 }));
        }

        [Test, Parallelizable]
        [Description("A SetTempo effect on a row governs that row's own duration onward; offsets accumulate from the change.")]
        public void Import_MidSongTempoChange_ShiftsSubsequentOffsets() {
            Cell tempoAndNote = new Cell { Note = TrackerNotes.FromKey(60), Instrument = 1, Effect = TrackerEffectCommand.SetTempo, EffectParam = 250 };
            Song song = OneChannelSong(2, (0, tempoAndNote), (1, Note(60, 1)));

            long[] noteOnOffsets = OfKind(song, NeutralEventKind.NoteOn).Select(e => e.SampleOffset).ToArray();

            Assert.That(noteOnOffsets, Is.EqualTo(new long[] { 0, 2646 }));
        }

        [Test, Parallelizable]
        [Description("The double accumulator holds exact fractional position, so a fractional row length does not drift over many rows.")]
        public void Import_FractionalRowLength_DoesNotDrift() {
            (int, Cell)[] notes = Enumerable.Range(0, 64).Select(r => (r, Note(60, 1))).ToArray();
            Song song = OneChannelSong(64, notes);
            song.DefaultBpm = 150;

            double samplesPerRow = 6 * (double)SampleRate * 2.5 / 150;
            long[] offsets = OfKind(song, NeutralEventKind.NoteOn).Select(e => e.SampleOffset).ToArray();

            Assert.That(offsets[63], Is.EqualTo((long)Math.Round(samplesPerRow * 63)));
            Assert.That(Math.Abs(offsets[63] - samplesPerRow * 63), Is.LessThan(1.0));
        }

        [Test, Parallelizable]
        public void Import_NoteWithInstrument_EmitsPatchThenNoteOn() {
            Song song = OneChannelSong(1, (0, Note(60, 1)));

            List<TimelineEntry> atZero = Entries(song).Where(e => e.SampleOffset == 0).ToList();
            int patchIndex = atZero.FindIndex(e => e.Event.Kind == NeutralEventKind.SetPatch);
            int noteIndex = atZero.FindIndex(e => e.Event.Kind == NeutralEventKind.NoteOn);

            Assert.That(patchIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(noteIndex, Is.GreaterThan(patchIndex), "the patch must be selected before the note that uses it.");
            Assert.That(OfKind(song, NeutralEventKind.NoteOn).Single().Event.Key, Is.EqualTo(60));
        }

        [Test, Parallelizable]
        [Description("Repeating the same instrument on consecutive notes selects the patch only once.")]
        public void Import_SameInstrumentTwice_EmitsPatchOnce() {
            Song song = OneChannelSong(2, (0, Note(60, 1)), (1, Note(62, 1)));

            Assert.That(OfKind(song, NeutralEventKind.SetPatch).Count, Is.EqualTo(1));
        }

        [Test, Parallelizable]
        [Description("A new note on a channel that is already sounding releases the prior note before the retrigger.")]
        public void Import_Retrigger_ReleasesPriorNoteFirst() {
            Song song = OneChannelSong(2, (0, Note(60, 1)), (1, Note(64, 1)));

            List<NeutralEventKind> noteKinds = Entries(song)
                .Where(e => e.Event.Kind == NeutralEventKind.NoteOn || e.Event.Kind == NeutralEventKind.NoteOff)
                .Select(e => e.Event.Kind).ToList();

            Assert.That(noteKinds, Is.EqualTo(new[] {
                NeutralEventKind.NoteOn, NeutralEventKind.NoteOff, NeutralEventKind.NoteOn
            }));
        }

        [Test, Parallelizable]
        public void Import_VolumeColumn_EmitsChannelGain() {
            Cell noteWithVolume = new Cell { Note = TrackerNotes.FromKey(60), Instrument = 1, Volume = 32 };
            Song song = OneChannelSong(1, (0, noteWithVolume));

            TimelineEntry gain = OfKind(song, NeutralEventKind.SetGain).Last(e => Math.Abs(e.Event.Value - 0.5f) < 1e-6);
            Assert.That(gain.Event.Value, Is.EqualTo(0.5f).Within(1e-6f), "volume 32 of 64 maps to gain 0.5.");
        }

        [Test, Parallelizable]
        public void Import_NoteOff_EmitsNoteOff() {
            Song song = OneChannelSong(2, (0, Note(60, 1)), (1, new Cell { Note = TrackerNotes.Off }));

            Assert.That(OfKind(song, NeutralEventKind.NoteOff).Count, Is.EqualTo(1));
        }

        [Test, Parallelizable]
        [Description("Note-cut silences the channel immediately (declick, no envelope release), not a NoteOff.")]
        public void Import_NoteCut_EmitsSilenceChannel() {
            Song song = OneChannelSong(2, (0, Note(60, 1)), (1, new Cell { Note = TrackerNotes.Cut }));

            Assert.That(OfKind(song, NeutralEventKind.SilenceChannel).Count, Is.EqualTo(1));
            Assert.That(OfKind(song, NeutralEventKind.NoteOff), Is.Empty);
        }

        [Test, Parallelizable]
        [Description("An effect command the importer does not interpret produces no event and no error.")]
        public void Import_UnknownEffect_IsIgnored() {
            Cell noteWithUnknownEffect = new Cell { Note = TrackerNotes.FromKey(60), Instrument = 1, Effect = (TrackerEffectCommand)200, EffectParam = 5 };
            Song song = OneChannelSong(1, (0, noteWithUnknownEffect));

            Assert.That(OfKind(song, NeutralEventKind.NoteOn).Count, Is.EqualTo(1));
        }

        [Test, Parallelizable]
        public void Import_ChannelCountAboveSixteen_Throws() {
            Song song = OneChannelSong(1);
            song.ChannelCount = 17;

            Assert.That(() => TrackerTimelineImporter.Import(song, SampleRate), Throws.ArgumentException);
        }

        [Test, Parallelizable]
        [Description("A SetSpeed effect on a row governs that row's own duration onward; halving the speed halves the row cadence.")]
        public void Import_MidSongSpeedChange_ShiftsSubsequentOffsets() {
            Cell speedAndNote = new Cell { Note = TrackerNotes.FromKey(60), Instrument = 1, Effect = TrackerEffectCommand.SetSpeed, EffectParam = 3 };
            Song song = OneChannelSong(2, (0, speedAndNote), (1, Note(60, 1)));

            long[] noteOnOffsets = OfKind(song, NeutralEventKind.NoteOn).Select(e => e.SampleOffset).ToArray();

            Assert.That(noteOnOffsets, Is.EqualTo(new long[] { 0, 2646 }));
        }

        [Test, Parallelizable]
        public void Import_NonPositiveSampleRate_Throws() {
            Song song = OneChannelSong(1);

            Assert.That(() => TrackerTimelineImporter.Import(song, 0), Throws.InstanceOf<ArgumentOutOfRangeException>());
        }

        [Test, Parallelizable]
        public void Import_NonPositiveDefaultBpm_Throws() {
            Song song = OneChannelSong(1);
            song.DefaultBpm = 0;

            Assert.That(() => TrackerTimelineImporter.Import(song, SampleRate), Throws.ArgumentException);
        }

        [Test, Parallelizable]
        public void Import_NonPositiveDefaultSpeed_Throws() {
            Song song = OneChannelSong(1);
            song.DefaultSpeed = 0;

            Assert.That(() => TrackerTimelineImporter.Import(song, SampleRate), Throws.ArgumentException);
        }

        [Test, Parallelizable]
        [Description("A pattern whose flat grid is shorter than Rows × ChannelCount is rejected at import.")]
        public void Import_PatternGridTooSmall_Throws() {
            Song song = OneChannelSong(1);
            song.Patterns[0].Rows = 4;

            Assert.That(() => TrackerTimelineImporter.Import(song, SampleRate), Throws.ArgumentException);
        }

        [Test, Parallelizable]
        [Description("A note on a channel with no valid instrument selects no patch but still triggers.")]
        public void Import_NoteWithoutValidInstrument_SkipsPatchButTriggers() {
            Song song = OneChannelSong(1, (0, Note(60)));

            Assert.That(OfKind(song, NeutralEventKind.SetPatch), Is.Empty);
            Assert.That(OfKind(song, NeutralEventKind.NoteOn).Count, Is.EqualTo(1));
        }

        [Test, Parallelizable]
        [Description("An out-of-range order index is tolerated and skipped, not thrown; the valid entries still play.")]
        public void Import_OrderIndexOutOfRange_IsSkipped() {
            Song song = OneChannelSong(1, (0, Note(60, 1)));
            song.Order = new[] { 99, 0 };

            Assert.That(OfKind(song, NeutralEventKind.NoteOn).Count, Is.EqualTo(1));
        }

        [Test, Parallelizable]
        public void Import_NullSong_Throws() {
            Assert.That(() => TrackerTimelineImporter.Import(null!, SampleRate), Throws.InstanceOf<ArgumentNullException>());
        }
    }
}
