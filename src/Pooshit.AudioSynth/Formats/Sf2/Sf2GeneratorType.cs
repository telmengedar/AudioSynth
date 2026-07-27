namespace Pooshit.AudioSynth.Formats.Sf2 {

    /// <summary>
    /// SF2 section 8.1.2 — generator operation codes, each identified by a 16-bit index stored in
    /// <see cref="Sf2Generator.Type"/>.
    /// </summary>
    public enum Sf2GeneratorType : ushort {

        /// <summary>Sample start point fine offset (in sample data points).</summary>
        StartAddressOffset = 0,

        /// <summary>Sample end point fine offset (in sample data points).</summary>
        EndAddressOffset = 1,

        /// <summary>Loop start point fine offset (in sample data points).</summary>
        StartLoopAddressOffset = 2,

        /// <summary>Loop end point fine offset (in sample data points).</summary>
        EndLoopAddressOffset = 3,

        /// <summary>Sample start point coarse offset (×32768 sample data points).</summary>
        StartAddressCoarseOffset = 4,

        /// <summary>Modulation LFO to pitch (cents).</summary>
        ModulationLFOToPitch = 5,

        /// <summary>Vibrato LFO to pitch (centibels).</summary>
        VibratoLFOToPitch = 6,

        /// <summary>Modulation envelope to pitch (centibels).</summary>
        ModulationEnvelopeToPitch = 7,

        /// <summary>Initial filter cutoff frequency (absolute cents).</summary>
        InitialFilterCutoffFrequency = 8,

        /// <summary>Initial filter resonance (centibels).</summary>
        InitialFilterQ = 9,

        /// <summary>Modulation LFO to filter cutoff frequency (cents).</summary>
        ModulationLFOToFilterCutoffFrequency = 10,

        /// <summary>Modulation envelope to filter cutoff frequency (centibels).</summary>
        ModulationEnvelopeToFilterCutoffFrequency = 11,

        /// <summary>Sample end point coarse offset (×32768 sample data points).</summary>
        EndAddressCoarseOffset = 12,

        /// <summary>Modulation LFO to volume (centibels).</summary>
        ModulationLFOToVolume = 13,

        /// <summary>Unused 1.</summary>
        Unused1 = 14,

        /// <summary>Chorus effects send (0.1% units).</summary>
        ChorusEffectsSend = 15,

        /// <summary>Reverb effects send (0.1% units).</summary>
        ReverbEffectsSend = 16,

        /// <summary>Panorama position (0.1% units, -50=left, +50=right).</summary>
        Pan = 17,

        /// <summary>Unused 2.</summary>
        Unused2 = 18,

        /// <summary>Unused 3.</summary>
        Unused3 = 19,

        /// <summary>Unused 4.</summary>
        Unused4 = 20,

        /// <summary>Delay of modulation LFO (timecents).</summary>
        DelayModulationLFO = 21,

        /// <summary>Frequency of modulation LFO (absolute cents).</summary>
        FrequencyModulationLFO = 22,

        /// <summary>Delay of vibrato LFO (timecents).</summary>
        DelayVibratoLFO = 23,

        /// <summary>Frequency of vibrato LFO (absolute cents).</summary>
        FrequencyVibratoLFO = 24,

        /// <summary>Delay of modulation envelope (timecents).</summary>
        DelayModulationEnvelope = 25,

        /// <summary>Attack of modulation envelope (timecents).</summary>
        AttackModulationEnvelope = 26,

        /// <summary>Hold of modulation envelope (timecents).</summary>
        HoldModulationEnvelope = 27,

        /// <summary>Decay of modulation envelope (timecents).</summary>
        DecayModulationEnvelope = 28,

        /// <summary>Sustain of modulation envelope (centibels).</summary>
        SustainModulationEnvelope = 29,

        /// <summary>Release of modulation envelope (timecents).</summary>
        ReleaseModulationEnvelope = 30,

        /// <summary>MIDI key number to modulation envelope hold (centibels/key).</summary>
        KeyNumberToModulationEnvelopeHold = 31,

        /// <summary>MIDI key number to modulation envelope decay (centibels/key).</summary>
        KeyNumberToModulationEnvelopeDecay = 32,

        /// <summary>Delay of volume envelope (timecents).</summary>
        DelayVolumeEnvelope = 33,

        /// <summary>Attack of volume envelope (timecents).</summary>
        AttackVolumeEnvelope = 34,

        /// <summary>Hold of volume envelope (timecents).</summary>
        HoldVolumeEnvelope = 35,

        /// <summary>Decay of volume envelope (timecents).</summary>
        DecayVolumeEnvelope = 36,

        /// <summary>Sustain of volume envelope (centibels).</summary>
        SustainVolumeEnvelope = 37,

        /// <summary>Release of volume envelope (timecents).</summary>
        ReleaseVolumeEnvelope = 38,

        /// <summary>MIDI key number to volume envelope hold (centibels/key).</summary>
        KeyNumberToVolumeEnvelopeHold = 39,

        /// <summary>MIDI key number to volume envelope decay (centibels/key).</summary>
        KeyNumberToVolumeEnvelopeDecay = 40,

        /// <summary>Index into instrument list (used in preset zones only).</summary>
        Instrument = 41,

        /// <summary>Reserved 1 — not used.</summary>
        Reserved1 = 42,

        /// <summary>Key range (byte pair: lo/hi MIDI key).</summary>
        KeyRange = 43,

        /// <summary>Velocity range (byte pair: lo/hi velocity).</summary>
        VelocityRange = 44,

        /// <summary>Loop start coarse offset (×32768 sample data points).</summary>
        StartLoopAddressCoarseOffset = 45,

        /// <summary>Forces the MIDI key number to override the key played.</summary>
        KeyNumber = 46,

        /// <summary>Forces the velocity to a specified value.</summary>
        Velocity = 47,

        /// <summary>Initial attenuation (centibels).</summary>
        InitialAttenuation = 48,

        /// <summary>Reserved 2 — not used.</summary>
        Reserved2 = 49,

        /// <summary>Loop end coarse offset (×32768 sample data points).</summary>
        EndLoopAddressCoarseOffset = 50,

        /// <summary>Coarse tuning (semitones).</summary>
        CoarseTune = 51,

        /// <summary>Fine tuning (cents).</summary>
        FineTune = 52,

        /// <summary>Index into sample header list (used in instrument zones only).</summary>
        SampleID = 53,

        /// <summary>Sample looping modes bitfield.</summary>
        SampleModes = 54,

        /// <summary>Reserved 3 — not used.</summary>
        Reserved3 = 55,

        /// <summary>Scale tuning (cents/MIDI key; 100 = normal chromatic scale).</summary>
        ScaleTuning = 56,

        /// <summary>Exclusive class — voices sharing a non-zero value mute each other.</summary>
        ExclusiveClass = 57,

        /// <summary>Overrides the sample's root key.</summary>
        OverridingRootKey = 58,

        /// <summary>Unused 5.</summary>
        Unused5 = 59,

        /// <summary>Unused end marker.</summary>
        UnusedEnd = 60
    }
}
