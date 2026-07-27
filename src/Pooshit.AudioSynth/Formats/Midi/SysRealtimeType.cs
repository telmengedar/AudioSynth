namespace Pooshit.AudioSynth.Formats.Midi {

    /// <summary>
    /// Closed vocabulary of system realtime status bytes (0xF8, 0xFA-0xFC, 0xFE-0xFF).
    /// </summary>
    public enum SysRealtimeType {

        /// <summary>Timing clock.</summary>
        Clock = 0xF8,

        /// <summary>Tick (undefined in the MIDI spec but reserved).</summary>
        Tick,

        /// <summary>Start.</summary>
        Start,

        /// <summary>Continue.</summary>
        Continue,

        /// <summary>Stop.</summary>
        Stop,

        /// <summary>Active sensing.</summary>
        ActiveSense = 0xFE,

        /// <summary>System reset.</summary>
        Reset
    }
}
