using System;

namespace Pooshit.AudioSynth.Formats.Tracker {

    /// <summary>
    /// A whole tracker composition as plain, serializable data: playback defaults, the channel count, the
    /// instrument table, the pattern bank, and the order list. A pure tree — no behavior, no engine
    /// references, no cycles — so a game engine can serialize it to JSON itself. Lowered to playable
    /// audio by <c>TrackerTimelineImporter</c>.
    /// </summary>
    public class Song {

        /// <summary>Optional song title for editors.</summary>
        public string? Title { get; set; }

        /// <summary>Default tempo in BPM, used unless a <see cref="TrackerEffectCommand.SetTempo"/> overrides it.</summary>
        public int DefaultBpm { get; set; }

        /// <summary>Default speed in ticks per row, used unless a <see cref="TrackerEffectCommand.SetSpeed"/> overrides it.</summary>
        public int DefaultSpeed { get; set; }

        /// <summary>Default rows-per-pattern an editor uses when creating a new pattern (e.g. 64 or 128).</summary>
        public int DefaultRows { get; set; }

        /// <summary>Number of channels (grid columns); must be in 1..16 to lower onto the synth.</summary>
        public int ChannelCount { get; set; }

        /// <summary>Instrument table; a cell's 1-based instrument slot <c>n</c> selects <c>Instruments[n − 1]</c>.</summary>
        public Instrument[] Instruments { get; set; } = Array.Empty<Instrument>();

        /// <summary>Pattern bank; entries are referenced by index from <see cref="Order"/>.</summary>
        public Pattern[] Patterns { get; set; } = Array.Empty<Pattern>();

        /// <summary>Order list: indices into <see cref="Patterns"/>, in play order.</summary>
        public int[] Order { get; set; } = Array.Empty<int>();
    }
}
