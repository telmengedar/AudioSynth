using Pooshit.AudioSynth.Formats.Tracker;

namespace Pooshit.AudioSynth.Sequencing {

    /// <summary>
    /// Shared cell-to-events decision logic for the importer (offline) and <see cref="TrackerSequencer"/> (live):
    /// resolves a cell's sub-columns into gain, patch and note verbs on a bound <see cref="ITrackerCellSink"/>.
    /// </summary>
    public sealed class TrackerCellApplier {

        const int DefaultVelocity = 127;
        const int FullVolume = 64;

        readonly ITrackerCellSink sink;
        readonly int[] currentInstrument;
        readonly int[] activeKey;
        readonly int[] appliedBank;
        readonly int[] appliedProgram;
        readonly bool[] patchApplied;

        /// <summary>Creates an applier for a fixed channel count, emitting to <paramref name="sink"/>.</summary>
        /// <param name="channelCount">number of channels whose state is tracked</param>
        /// <param name="sink">emission target for the resolved verbs</param>
        public TrackerCellApplier(int channelCount, ITrackerCellSink sink) {
            this.sink = sink;
            currentInstrument = new int[channelCount];
            activeKey = new int[channelCount];
            appliedBank = new int[channelCount];
            appliedProgram = new int[channelCount];
            patchApplied = new bool[channelCount];
            for (int channel = 0; channel < channelCount; channel++)
                activeKey[channel] = -1;
        }

        /// <summary>Resolves one cell's instrument latch and volume, emitting the gain verb it implies.</summary>
        /// <param name="cell">the cell to interpret</param>
        /// <param name="channel">the channel the cell belongs to</param>
        /// <param name="song">the song supplying the instrument table; unused here, kept for signature symmetry with <see cref="ApplyNote"/>.</param>
        public void ApplyControls(in Cell cell, int channel, Song song) {
            if (cell.Instrument != 0)
                currentInstrument[channel] = cell.Instrument;

            if (cell.Volume != 0) {
                int level = cell.Volume > FullVolume ? FullVolume : cell.Volume;
                sink.SetGain(channel, level / (float)FullVolume);
            }
        }

        /// <summary>Resolves one cell's note sub-column, emitting the patch and note verbs it implies.</summary>
        /// <param name="cell">the cell to interpret</param>
        /// <param name="channel">the channel the cell belongs to</param>
        /// <param name="song">the song supplying the instrument table</param>
        public void ApplyNote(in Cell cell, int channel, Song song) {
            if (TrackerNotes.IsPlayable(cell.Note)) {
                ApplyPatch(channel, song);
                if (activeKey[channel] != -1)
                    ReleaseActive(channel);
                int key = TrackerNotes.KeyOf(cell.Note);
                sink.NoteOn(channel, key, DefaultVelocity);
                activeKey[channel] = key;
            }
            else if (cell.Note == TrackerNotes.Off) {
                if (activeKey[channel] != -1)
                    ReleaseActive(channel);
            }
            else if (cell.Note == TrackerNotes.Cut) {
                Cut(channel);
            }
        }

        /// <summary>Resolves one cell for a channel: <see cref="ApplyControls"/> then <see cref="ApplyNote"/>.</summary>
        /// <param name="cell">the cell to interpret</param>
        /// <param name="channel">the channel the cell belongs to</param>
        /// <param name="song">the song supplying the instrument table</param>
        public void Apply(in Cell cell, int channel, Song song) {
            ApplyControls(cell, channel, song);
            ApplyNote(cell, channel, song);
        }

        /// <summary>The channel's currently sounding MIDI key, or -1 if none is sounding.</summary>
        /// <param name="channel">the channel to query</param>
        public int ActiveKey(int channel) => activeKey[channel];

        /// <summary>Silences a channel and clears its active key, so a later retrigger cannot revive the cut note.</summary>
        /// <param name="channel">the channel to cut</param>
        public void Cut(int channel) {
            if (activeKey[channel] == -1)
                return;
            sink.Silence(channel);
            activeKey[channel] = -1;
        }

        /// <summary>Clears all per-channel state, e.g. after a transport seek.</summary>
        public void Reset() {
            for (int channel = 0; channel < activeKey.Length; channel++) {
                currentInstrument[channel] = 0;
                activeKey[channel] = -1;
                appliedBank[channel] = 0;
                appliedProgram[channel] = 0;
                patchApplied[channel] = false;
            }
        }

        void ApplyPatch(int channel, Song song) {
            int slot = currentInstrument[channel];
            if (slot < 1 || slot > song.Instruments.Length)
                return;
            Instrument instrument = song.Instruments[slot - 1];
            if (patchApplied[channel] && appliedBank[channel] == instrument.Bank && appliedProgram[channel] == instrument.Program)
                return;
            sink.SelectPatch(channel, instrument.Bank, instrument.Program);
            appliedBank[channel] = instrument.Bank;
            appliedProgram[channel] = instrument.Program;
            patchApplied[channel] = true;
        }

        void ReleaseActive(int channel) {
            sink.NoteOff(channel, activeKey[channel]);
            activeKey[channel] = -1;
        }
    }
}
