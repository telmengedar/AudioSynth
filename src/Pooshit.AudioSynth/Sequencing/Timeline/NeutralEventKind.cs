namespace Pooshit.AudioSynth.Sequencing.Timeline {

    /// <summary>
    /// Discriminator for <see cref="NeutralEvent"/>; maps 1:1 onto an <c>ISynthesizer</c> control method.
    /// </summary>
    public enum NeutralEventKind {

        /// <summary>Starts a note (channel, key, velocity).</summary>
        NoteOn,

        /// <summary>Releases a note (channel, key).</summary>
        NoteOff,

        /// <summary>Resolved (bank, program) patch select for a channel.</summary>
        SetPatch,

        /// <summary>Channel mix gain.</summary>
        SetGain,

        /// <summary>Channel stereo pan.</summary>
        SetPan,

        /// <summary>Channel pitch bend, in semitones.</summary>
        SetPitchBend,

        /// <summary>Channel mod-wheel amount.</summary>
        SetModulation,

        /// <summary>Channel reverb-send level.</summary>
        SetReverbSend,

        /// <summary>Channel chorus-send level.</summary>
        SetChorusSend,

        /// <summary>Channel sustain-pedal state.</summary>
        SetSustain,

        /// <summary>Hard-silences every voice on a channel.</summary>
        SilenceChannel,

        /// <summary>Releases every voice on a channel.</summary>
        ReleaseAllNotes
    }
}
