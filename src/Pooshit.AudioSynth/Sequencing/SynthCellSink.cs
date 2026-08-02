using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Sequencing {

    /// <summary>
    /// Live <see cref="ITrackerCellSink"/>: applies cell verbs directly to a bound <see cref="ISynthesizer"/>,
    /// resolving each symbolic patch through a <see cref="SoundBank"/> at the moment of selection.
    /// </summary>
    public sealed class SynthCellSink : ITrackerCellSink {

        readonly ISynthesizer synth;
        readonly SoundBank soundBank;

        /// <summary>Creates a sink driving <paramref name="synth"/>, resolving patches via <paramref name="soundBank"/>.</summary>
        /// <param name="synth">the engine receiving the live calls</param>
        /// <param name="soundBank">bank resolving symbolic (bank, program) to a patch</param>
        public SynthCellSink(ISynthesizer synth, SoundBank soundBank) {
            this.synth = synth;
            this.soundBank = soundBank;
        }

        /// <inheritdoc/>
        public void SetGain(int channel, float gain) => synth.SetChannelGain(channel, gain);

        /// <inheritdoc/>
        public void SelectPatch(int channel, int bank, int program) =>
            synth.SetChannelPatch(channel, soundBank.GetPatch(bank, program));

        /// <inheritdoc/>
        public void NoteOn(int channel, int key, int velocity) => synth.NoteOn(channel, key, velocity);

        /// <inheritdoc/>
        public void NoteOff(int channel, int key) => synth.NoteOff(channel, key);

        /// <inheritdoc/>
        public void Silence(int channel) => synth.SilenceChannel(channel);
    }
}
