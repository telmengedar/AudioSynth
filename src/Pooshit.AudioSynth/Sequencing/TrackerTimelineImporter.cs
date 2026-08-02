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
            TimelineCellSink sink = new TimelineCellSink(timeline, channelCount);
            TrackerCellApplier applier = new TrackerCellApplier(channelCount, sink);
            for (int channel = 0; channel < channelCount; channel++) {
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

                    sink.Offset = (long)Math.Round(cursor);
                    for (int channel = 0; channel < channelCount; channel++)
                        applier.Apply(pattern.Cells[rowBase + channel], channel, song);

                    cursor += currentSpeed * (double)sampleRate * TickSecondsScale / currentTempo;
                }
            }

            return timeline;
        }
    }
}
