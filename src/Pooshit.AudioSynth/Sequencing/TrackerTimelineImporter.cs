using System;
using Pooshit.AudioSynth.Formats.Tracker;
using Pooshit.AudioSynth.Sequencing.Timeline;

namespace Pooshit.AudioSynth.Sequencing {

    /// <summary>
    /// Lowers a POD <see cref="Song"/> onto a MIDI-neutral <see cref="Timeline.Timeline"/> playable by
    /// <see cref="RealtimeSequencer"/> — the tracker twin of <see cref="MidiTimelineImporter"/>.
    /// </summary>
    public static class TrackerTimelineImporter {

        const int MaxChannels = 16;
        const int DefaultVelocity = 127;
        const int FullVolume = 64;
        const double TickSecondsScale = 2.5;

        /// <summary>
        /// Lowers <paramref name="song"/> into a fresh timeline, emitting each cell's neutral events at its
        /// accumulated row offset. Symbolic throughout — never touches a SoundBank or patch.
        /// </summary>
        /// <param name="song">the composition to lower</param>
        /// <param name="sampleRate">target sample rate, used to convert rows to sample offsets</param>
        /// <returns>a populated, uncompiled timeline</returns>
        public static Timeline.Timeline Import(Song song, int sampleRate) {
            if (song is null)
                throw new ArgumentNullException(nameof(song));
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive.");
            if (song.ChannelCount < 1 || song.ChannelCount > MaxChannels)
                throw new ArgumentException($"Song.ChannelCount must be in [1,{MaxChannels}]; the synth is a {MaxChannels}-channel engine.", nameof(song));
            if (song.DefaultBpm <= 0)
                throw new ArgumentException("Song.DefaultBpm must be positive.", nameof(song));
            if (song.DefaultSpeed <= 0)
                throw new ArgumentException("Song.DefaultSpeed must be positive.", nameof(song));

            int channelCount = song.ChannelCount;
            Timeline.Timeline timeline = new Timeline.Timeline();

            int[] currentInstrument = new int[channelCount];
            int[] activeKey = new int[channelCount];
            long[] openNoteId = new long[channelCount];
            int[] appliedBank = new int[channelCount];
            int[] appliedProgram = new int[channelCount];
            bool[] patchApplied = new bool[channelCount];
            for (int channel = 0; channel < channelCount; channel++) {
                activeKey[channel] = -1;
                timeline.Add(0, NeutralEvent.SetGain(channel, 1f));
                timeline.Add(0, NeutralEvent.SetPan(channel, 0f));
            }

            int currentSpeed = song.DefaultSpeed;
            int currentTempo = song.DefaultBpm;
            double cursor = 0.0;

            foreach (int orderIndex in song.Order) {
                if (orderIndex < 0 || orderIndex >= song.Patterns.Length)
                    continue;
                Pattern pattern = song.Patterns[orderIndex];
                int rows = pattern.Rows ?? song.DefaultRows;
                if (pattern.Cells.Length < rows * channelCount)
                    throw new ArgumentException($"Pattern at order index {orderIndex} has {pattern.Cells.Length} cells; expected at least {rows * channelCount} (effective Rows × ChannelCount).", nameof(song));

                for (int row = 0; row < rows; row++) {
                    int rowBase = row * channelCount;
                    for (int channel = 0; channel < channelCount; channel++) {
                        Cell cell = pattern.Cells[rowBase + channel];
                        if (cell.Effect == TrackerEffectCommand.SetSpeed && cell.EffectParam > 0)
                            currentSpeed = cell.EffectParam;
                        else if (cell.Effect == TrackerEffectCommand.SetTempo && cell.EffectParam > 0)
                            currentTempo = cell.EffectParam;
                    }

                    long offset = (long)Math.Round(cursor);
                    for (int channel = 0; channel < channelCount; channel++)
                        EmitCell(timeline, offset, channel, pattern.Cells[rowBase + channel], song,
                            currentInstrument, activeKey, openNoteId, appliedBank, appliedProgram, patchApplied);

                    cursor += currentSpeed * (double)sampleRate * TickSecondsScale / currentTempo;
                }
            }

            return timeline;
        }

        static void EmitCell(Timeline.Timeline timeline, long offset, int channel, Cell cell, Song song,
            int[] currentInstrument, int[] activeKey, long[] openNoteId,
            int[] appliedBank, int[] appliedProgram, bool[] patchApplied) {
            if (cell.Instrument != 0)
                currentInstrument[channel] = cell.Instrument;

            if (cell.Volume != 0) {
                int level = cell.Volume > FullVolume ? FullVolume : cell.Volume;
                timeline.Add(offset, NeutralEvent.SetGain(channel, level / (float)FullVolume));
            }

            if (TrackerNotes.IsPlayable(cell.Note)) {
                ApplyPatch(timeline, offset, channel, song, currentInstrument, appliedBank, appliedProgram, patchApplied);
                if (activeKey[channel] != -1)
                    ReleaseActive(timeline, offset, channel, activeKey, openNoteId);
                int key = TrackerNotes.KeyOf(cell.Note);
                openNoteId[channel] = timeline.Add(offset, NeutralEvent.NoteOn(channel, key, DefaultVelocity));
                activeKey[channel] = key;
            }
            else if (cell.Note == TrackerNotes.Off) {
                if (activeKey[channel] != -1)
                    ReleaseActive(timeline, offset, channel, activeKey, openNoteId);
            }
            else if (cell.Note == TrackerNotes.Cut) {
                if (activeKey[channel] != -1) {
                    timeline.Add(offset, NeutralEvent.SilenceChannel(channel));
                    activeKey[channel] = -1;
                }
            }
        }

        static void ApplyPatch(Timeline.Timeline timeline, long offset, int channel, Song song,
            int[] currentInstrument, int[] appliedBank, int[] appliedProgram, bool[] patchApplied) {
            int slot = currentInstrument[channel];
            if (slot < 1 || slot > song.Instruments.Length)
                return;
            Instrument instrument = song.Instruments[slot - 1];
            if (patchApplied[channel] && appliedBank[channel] == instrument.Bank && appliedProgram[channel] == instrument.Program)
                return;
            timeline.Add(offset, NeutralEvent.SetPatch(channel, instrument.Bank, instrument.Program));
            appliedBank[channel] = instrument.Bank;
            appliedProgram[channel] = instrument.Program;
            patchApplied[channel] = true;
        }

        static void ReleaseActive(Timeline.Timeline timeline, long offset, int channel, int[] activeKey, long[] openNoteId) {
            long offId = timeline.Add(offset, NeutralEvent.NoteOff(channel, activeKey[channel]));
            timeline.LinkNote(openNoteId[channel], offId);
            activeKey[channel] = -1;
        }
    }
}
