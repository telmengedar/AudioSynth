using Pooshit.AudioSynth.Sequencing.Timeline;

namespace Pooshit.AudioSynth.Sequencing {

    /// <summary>
    /// Offline <see cref="ITrackerCellSink"/>: appends symbolic <see cref="NeutralEvent"/>s to a timeline at
    /// the current row <see cref="Offset"/>, pairing each note-off with its note-on via <c>LinkNote</c>.
    /// </summary>
    public sealed class TimelineCellSink : ITrackerCellSink {

        readonly Timeline.Timeline timeline;
        readonly long[] openNoteId;

        /// <summary>Creates a sink writing to <paramref name="timeline"/> for a fixed channel count.</summary>
        /// <param name="timeline">the timeline receiving the emitted events</param>
        /// <param name="channelCount">number of channels whose open notes are tracked for linking</param>
        public TimelineCellSink(Timeline.Timeline timeline, int channelCount) {
            this.timeline = timeline;
            openNoteId = new long[channelCount];
        }

        /// <summary>Sample offset at which the next emitted events are placed; set per row by the importer.</summary>
        public long Offset { get; set; }

        /// <inheritdoc/>
        public void SetGain(int channel, float gain) =>
            timeline.Add(Offset, NeutralEvent.SetGain(channel, gain));

        /// <inheritdoc/>
        public void SelectPatch(int channel, int bank, int program) =>
            timeline.Add(Offset, NeutralEvent.SetPatch(channel, bank, program));

        /// <inheritdoc/>
        public void NoteOn(int channel, int key, int velocity) =>
            openNoteId[channel] = timeline.Add(Offset, NeutralEvent.NoteOn(channel, key, velocity));

        /// <inheritdoc/>
        public void NoteOff(int channel, int key) {
            long offId = timeline.Add(Offset, NeutralEvent.NoteOff(channel, key));
            timeline.LinkNote(openNoteId[channel], offId);
        }

        /// <inheritdoc/>
        public void Silence(int channel) =>
            timeline.Add(Offset, NeutralEvent.SilenceChannel(channel));
    }
}
