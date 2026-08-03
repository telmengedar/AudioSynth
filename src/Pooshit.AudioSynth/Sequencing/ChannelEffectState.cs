using Pooshit.AudioSynth.Formats.Tracker;

namespace Pooshit.AudioSynth.Sequencing {

    /// <summary>
    /// One channel's running per-tick effect state: pitch/volume accumulators, oscillator phase,
    /// scheduled-tick targets, and the per-effect param memory (design DiVoid #7511 §5.1).
    /// </summary>
    internal struct ChannelEffectState {

        /// <summary>Effect armed for the current row; <see cref="TrackerEffectCommand.None"/> when none.</summary>
        public TrackerEffectCommand ActiveEffect;

        /// <summary>Persistent pitch-bend offset in semitones; reset to 0 on a fresh note (not tone-portamento).</summary>
        public float PitchOffset;

        /// <summary>Tone-portamento destination as a semitone offset from the sounding key; null while inactive.</summary>
        public float? PortaTarget;

        /// <summary>Per-tick semitone step shared by portamento up/down/tone-portamento.</summary>
        public float PortaStep;

        /// <summary>Current tracker volume in 0..64, slid by <see cref="TrackerEffectCommand.VolumeSlide"/>.</summary>
        public int VolumeLevel;

        /// <summary>Per-tick volume delta armed by <see cref="TrackerEffectCommand.VolumeSlide"/>.</summary>
        public int VolumeSlideDelta;

        /// <summary>Vibrato oscillator phase in radians, advanced once per tick while active.</summary>
        public float VibratoPhase;

        /// <summary>Vibrato phase advance per tick, in radians.</summary>
        public float VibratoRate;

        /// <summary>Vibrato peak excursion, in semitones.</summary>
        public float VibratoDepth;

        /// <summary>Arpeggio second-step semitone offset (applied when tick index mod 3 equals 1).</summary>
        public int ArpHi;

        /// <summary>Arpeggio third-step semitone offset (applied when tick index mod 3 equals 2).</summary>
        public int ArpLo;

        /// <summary>Retrigger interval in ticks; 0 means no retrigger is armed this row.</summary>
        public int RetriggerInterval;

        /// <summary>Tick index at which <see cref="TrackerEffectCommand.NoteCut"/> silences the channel.</summary>
        public int CutTick;

        /// <summary>Tick index at which a withheld <see cref="TrackerEffectCommand.NoteDelay"/> cell fires.</summary>
        public int DelayTick;

        /// <summary>The cell withheld by <see cref="TrackerEffectCommand.NoteDelay"/> until <see cref="DelayTick"/>.</summary>
        public Cell HeldCell;

        /// <summary>Last non-zero <see cref="Cell.EffectParam"/> seen per effect command, indexed by command value.</summary>
        public byte[] ParamMemory;
    }
}
