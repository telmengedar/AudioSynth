namespace Pooshit.AudioSynth.Formats.Midi {

    /// <summary>
    /// Standard MIDI CC numbers carried as <c>Data1</c> on a <see cref="ChannelCommandType.Controller"/>
    /// message. Not interpreted in increment 1; retained for follow-up PRs (mixing, expression).
    /// </summary>
    public enum ControllerType {

        /// <summary>Bank select, coarse.</summary>
        BankSelect,

        /// <summary>Modulation wheel, coarse.</summary>
        ModulationWheel,

        /// <summary>Breath control, coarse.</summary>
        BreathControl,

        /// <summary>Foot pedal, coarse.</summary>
        FootPedal = 4,

        /// <summary>Portamento time, coarse.</summary>
        PortamentoTime,

        /// <summary>Data entry slider, coarse.</summary>
        DataEntrySlider,

        /// <summary>Channel volume, coarse.</summary>
        Volume,

        /// <summary>Balance, coarse.</summary>
        Balance,

        /// <summary>Pan position, coarse.</summary>
        Pan = 10,

        /// <summary>Expression, coarse.</summary>
        Expression,

        /// <summary>Effect control 1, coarse.</summary>
        EffectControl1,

        /// <summary>Effect control 2, coarse.</summary>
        EffectControl2,

        /// <summary>General purpose slider 1.</summary>
        GeneralPurposeSlider1 = 16,

        /// <summary>General purpose slider 2.</summary>
        GeneralPurposeSlider2,

        /// <summary>General purpose slider 3.</summary>
        GeneralPurposeSlider3,

        /// <summary>General purpose slider 4.</summary>
        GeneralPurposeSlider4,

        /// <summary>Bank select, fine.</summary>
        BankSelectFine = 32,

        /// <summary>Modulation wheel, fine.</summary>
        ModulationWheelFine,

        /// <summary>Breath control, fine.</summary>
        BreathControlFine,

        /// <summary>Foot pedal, fine.</summary>
        FootPedalFine = 36,

        /// <summary>Portamento time, fine.</summary>
        PortamentoTimeFine,

        /// <summary>Data entry slider, fine.</summary>
        DataEntrySliderFine,

        /// <summary>Channel volume, fine.</summary>
        VolumeFine,

        /// <summary>Balance, fine.</summary>
        BalanceFine,

        /// <summary>Pan position, fine.</summary>
        PanFine = 42,

        /// <summary>Expression, fine.</summary>
        ExpressionFine,

        /// <summary>Effect control 1, fine.</summary>
        EffectControl1Fine,

        /// <summary>Effect control 2, fine.</summary>
        EffectControl2Fine,

        /// <summary>Hold pedal 1 (sustain).</summary>
        HoldPedal1 = 64,

        /// <summary>Portamento on/off.</summary>
        Portamento,

        /// <summary>Sostenuto pedal.</summary>
        SustenutoPedal,

        /// <summary>Soft pedal.</summary>
        SoftPedal,

        /// <summary>Legato pedal.</summary>
        LegatoPedal,

        /// <summary>Hold pedal 2 (freeze).</summary>
        HoldPedal2,

        /// <summary>Sound variation.</summary>
        SoundVariation,

        /// <summary>Sound timbre.</summary>
        SoundTimbre,

        /// <summary>Sound release time.</summary>
        SoundReleaseTime,

        /// <summary>Sound attack time.</summary>
        SoundAttackTime,

        /// <summary>Sound brightness.</summary>
        SoundBrightness,

        /// <summary>Sound control 6.</summary>
        SoundControl6,

        /// <summary>Sound control 7.</summary>
        SoundControl7,

        /// <summary>Sound control 8.</summary>
        SoundControl8,

        /// <summary>Sound control 9.</summary>
        SoundControl9,

        /// <summary>Sound control 10.</summary>
        SoundControl10,

        /// <summary>General purpose button 1.</summary>
        GeneralPurposeButton1,

        /// <summary>General purpose button 2.</summary>
        GeneralPurposeButton2,

        /// <summary>General purpose button 3.</summary>
        GeneralPurposeButton3,

        /// <summary>General purpose button 4.</summary>
        GeneralPurposeButton4,

        /// <summary>Effects level.</summary>
        EffectsLevel = 91,

        /// <summary>Tremolo level.</summary>
        TremeloLevel,

        /// <summary>Chorus level.</summary>
        ChorusLevel,

        /// <summary>Celeste (detune) level.</summary>
        CelesteLevel,

        /// <summary>Phaser level.</summary>
        PhaserLevel,

        /// <summary>Data button increment.</summary>
        DataButtonIncrement,

        /// <summary>Data button decrement.</summary>
        DataButtonDecrement,

        /// <summary>Non-registered parameter number, fine.</summary>
        NonRegisteredParameterFine,

        /// <summary>Non-registered parameter number, coarse.</summary>
        NonRegisteredParameterCoarse,

        /// <summary>Registered parameter number, fine.</summary>
        RegisteredParameterFine,

        /// <summary>Registered parameter number, coarse.</summary>
        RegisteredParameterCoarse,

        /// <summary>All sound off.</summary>
        AllSoundOff = 120,

        /// <summary>Reset all controllers.</summary>
        AllControllersOff,

        /// <summary>Local keyboard on/off.</summary>
        LocalKeyboard,

        /// <summary>All notes off.</summary>
        AllNotesOff,

        /// <summary>Omni mode off.</summary>
        OmniModeOff,

        /// <summary>Omni mode on.</summary>
        OmniModeOn,

        /// <summary>Mono operation.</summary>
        MonoOperation,

        /// <summary>Poly operation.</summary>
        PolyOperation
    }
}
