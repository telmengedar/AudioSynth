using System;
using Pooshit.AudioSynth.Formats.Tracker;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Sequencing {

    /// <summary>
    /// Per-channel per-tick tracker effect state machine (design DiVoid #7511): drives <see cref="ISynthesizer"/>
    /// per tick, delegating base cell decoding to an owned <see cref="TrackerCellApplier"/>.
    /// </summary>
    public sealed class TrackerEffectEngine {

        const int FullVolume = 64;
        const int DefaultVelocity = 127;

        /// <summary>Divisor turning a portamento/tone-portamento param byte into a semitone-per-tick step.</summary>
        const float PortaStepScale = 16f;

        /// <summary>Multiplier turning a vibrato rate nibble into radians of phase advance per tick.</summary>
        const float VibratoRateScale = 0.1f;

        /// <summary>Divisor turning a vibrato depth nibble into a semitone excursion.</summary>
        const float VibratoDepthScale = 8f;

        static readonly int ParamMemorySlotCount = (int)TrackerEffectCommand.NoteDelay + 1;

        readonly ISynthesizer synth;
        readonly TrackerCellApplier applier;
        readonly int channelCount;
        readonly ChannelEffectState[] channels;
        Song song = null!;

        /// <summary>Creates an engine for a fixed channel count, driving <paramref name="synth"/> both directly and through its own applier.</summary>
        /// <param name="channelCount">number of channels whose effect state is tracked</param>
        /// <param name="synth">the engine receiving both cell-decoded and per-tick control calls</param>
        /// <param name="soundBank">bank resolving each cell's symbolic instrument for the owned applier</param>
        public TrackerEffectEngine(int channelCount, ISynthesizer synth, SoundBank soundBank) {
            this.synth = synth ?? throw new ArgumentNullException(nameof(synth));
            this.channelCount = channelCount;
            applier = new TrackerCellApplier(channelCount, new SynthCellSink(synth, soundBank));
            channels = new ChannelEffectState[channelCount];
            for (int channel = 0; channel < channelCount; channel++)
                channels[channel] = CreateInitialState();
        }

        /// <summary>Tick-0 step: decodes each channel's cell, applies its base controls/note, and arms the row's effect.</summary>
        /// <param name="pattern">the pattern holding the row</param>
        /// <param name="row">the row index within <paramref name="pattern"/></param>
        /// <param name="song">the song supplying the instrument table for cell application</param>
        public void EnterRow(Pattern pattern, int row, Song song) {
            this.song = song;
            int rowBase = row * channelCount;
            for (int channel = 0; channel < channelCount; channel++)
                EnterRowForChannel(pattern.Cells[rowBase + channel], channel);
        }

        /// <summary>Tick 1..speed-1 step: advances every channel's armed effect and pushes the resulting control call.</summary>
        /// <param name="tickIndex">the tick index within the current row, in 1..speed-1</param>
        public void Tick(int tickIndex) {
            for (int channel = 0; channel < channelCount; channel++)
                TickChannel(channel, tickIndex);
        }

        /// <summary>Clears every channel's running effect state and param memory, and resets the owned applier.</summary>
        public void Reset() {
            applier.Reset();
            for (int channel = 0; channel < channelCount; channel++)
                channels[channel] = CreateInitialState();
        }

        static ChannelEffectState CreateInitialState() => new ChannelEffectState {
            VolumeLevel = FullVolume,
            ParamMemory = new byte[ParamMemorySlotCount]
        };

        void EnterRowForChannel(in Cell cell, int channel) {
            TrackerEffectCommand effect = cell.Effect;
            byte param = ResolveParam(channel, effect, cell.EffectParam);
            ref ChannelEffectState state = ref channels[channel];
            TrackerEffectCommand priorEffect = state.ActiveEffect;
            state.ActiveEffect = IsPerTickCommand(effect) ? effect : TrackerEffectCommand.None;
            bool pitchSettled = false;

            switch (effect) {
                case TrackerEffectCommand.NoteDelay:
                    if (param == 0) {
                        pitchSettled = ApplyFreshCell(cell, channel, ref state);
                        state.ActiveEffect = TrackerEffectCommand.None;
                    }
                    else {
                        state.HeldCell = cell;
                        state.DelayTick = param;
                    }
                    break;
                case TrackerEffectCommand.TonePortamento:
                    applier.ApplyControls(cell, channel, song);
                    UpdateVolumeLevel(cell, ref state);
                    ArmTonePortamento(cell, channel, param, ref state);
                    break;
                case TrackerEffectCommand.SetPan:
                    pitchSettled = ApplyFreshCell(cell, channel, ref state);
                    synth.SetChannelPan(channel, TrackerPan.ToSignedPan(param));
                    break;
                default:
                    pitchSettled = ApplyFreshCell(cell, channel, ref state);
                    ArmAdditiveEffect(effect, param, channel, ref state);
                    break;
            }

            if (!pitchSettled && IsOscillating(priorEffect) && !IsOscillating(state.ActiveEffect))
                synth.SetChannelPitchBend(channel, state.PitchOffset);
        }

        bool ApplyFreshCell(in Cell cell, int channel, ref ChannelEffectState state) {
            applier.Apply(cell, channel, song);
            UpdateVolumeLevel(cell, ref state);
            bool freshNote = TrackerNotes.IsPlayable(cell.Note);
            if (freshNote) {
                state.PitchOffset = 0f;
                state.VibratoPhase = 0f;
                synth.SetChannelPitchBend(channel, 0f);
            }
            return freshNote;
        }

        void ArmTonePortamento(in Cell cell, int channel, byte param, ref ChannelEffectState state) {
            if (TrackerNotes.IsPlayable(cell.Note)) {
                int activeKey = applier.ActiveKey(channel);
                if (activeKey != -1)
                    state.PortaTarget = TrackerNotes.KeyOf(cell.Note) - activeKey;
            }
            state.PortaStep = param / PortaStepScale;
        }

        void ArmAdditiveEffect(TrackerEffectCommand effect, byte param, int channel, ref ChannelEffectState state) {
            switch (effect) {
                case TrackerEffectCommand.VolumeSlide:
                    int up = param >> 4, down = param & 0xF;
                    state.VolumeSlideDelta = up > 0 ? up : -down;
                    break;
                case TrackerEffectCommand.PortamentoUp:
                case TrackerEffectCommand.PortamentoDown:
                    state.PortaStep = param / PortaStepScale;
                    break;
                case TrackerEffectCommand.Arpeggio:
                    state.ArpHi = param >> 4;
                    state.ArpLo = param & 0xF;
                    EmitArpeggioTick(channel, ref state, 0);
                    break;
                case TrackerEffectCommand.Vibrato:
                    state.VibratoRate = (param >> 4) * VibratoRateScale;
                    state.VibratoDepth = (param & 0xF) / VibratoDepthScale;
                    EmitVibratoTick(channel, ref state);
                    break;
                case TrackerEffectCommand.Retrigger:
                    state.RetriggerInterval = param;
                    break;
                case TrackerEffectCommand.NoteCut:
                    state.CutTick = param;
                    break;
            }
        }

        void TickChannel(int channel, int tickIndex) {
            ref ChannelEffectState state = ref channels[channel];
            switch (state.ActiveEffect) {
                case TrackerEffectCommand.VolumeSlide:
                    state.VolumeLevel = Clamp(state.VolumeLevel + state.VolumeSlideDelta, 0, FullVolume);
                    synth.SetChannelGain(channel, state.VolumeLevel / (float)FullVolume);
                    break;
                case TrackerEffectCommand.PortamentoUp:
                    state.PitchOffset += state.PortaStep;
                    synth.SetChannelPitchBend(channel, state.PitchOffset);
                    break;
                case TrackerEffectCommand.PortamentoDown:
                    state.PitchOffset -= state.PortaStep;
                    synth.SetChannelPitchBend(channel, state.PitchOffset);
                    break;
                case TrackerEffectCommand.TonePortamento:
                    AdvanceTonePortamento(channel, ref state);
                    break;
                case TrackerEffectCommand.Arpeggio:
                    EmitArpeggioTick(channel, ref state, tickIndex);
                    break;
                case TrackerEffectCommand.Vibrato:
                    state.VibratoPhase += state.VibratoRate;
                    EmitVibratoTick(channel, ref state);
                    break;
                case TrackerEffectCommand.Retrigger:
                    if (state.RetriggerInterval > 0 && tickIndex % state.RetriggerInterval == 0) {
                        int key = applier.ActiveKey(channel);
                        if (key != -1)
                            synth.NoteOn(channel, key, DefaultVelocity);
                    }
                    break;
                case TrackerEffectCommand.NoteCut:
                    if (tickIndex == state.CutTick)
                        applier.Cut(channel);
                    break;
                case TrackerEffectCommand.NoteDelay:
                    if (tickIndex == state.DelayTick) {
                        applier.Apply(state.HeldCell, channel, song);
                        UpdateVolumeLevel(state.HeldCell, ref state);
                    }
                    break;
            }
        }

        void AdvanceTonePortamento(int channel, ref ChannelEffectState state) {
            if (state.PortaTarget is null || applier.ActiveKey(channel) == -1)
                return;
            float target = state.PortaTarget.Value;
            if (state.PitchOffset < target)
                state.PitchOffset = Math.Min(state.PitchOffset + state.PortaStep, target);
            else if (state.PitchOffset > target)
                state.PitchOffset = Math.Max(state.PitchOffset - state.PortaStep, target);
            synth.SetChannelPitchBend(channel, state.PitchOffset);
        }

        void EmitArpeggioTick(int channel, ref ChannelEffectState state, int tickIndex) {
            int step = tickIndex % 3;
            float offset = step == 0 ? 0f : step == 1 ? state.ArpHi : state.ArpLo;
            synth.SetChannelPitchBend(channel, state.PitchOffset + offset);
        }

        void EmitVibratoTick(int channel, ref ChannelEffectState state) {
            float offset = (float)Math.Sin(state.VibratoPhase) * state.VibratoDepth;
            synth.SetChannelPitchBend(channel, state.PitchOffset + offset);
        }

        byte ResolveParam(int channel, TrackerEffectCommand effect, byte param) {
            if (!IsPerTickCommand(effect))
                return param;
            ref ChannelEffectState state = ref channels[channel];
            int slot = (int)effect;
            if (param != 0) {
                state.ParamMemory[slot] = param;
                return param;
            }
            return state.ParamMemory[slot];
        }

        static void UpdateVolumeLevel(in Cell cell, ref ChannelEffectState state) {
            if (cell.Volume != 0)
                state.VolumeLevel = cell.Volume > FullVolume ? FullVolume : cell.Volume;
        }

        static bool IsPerTickCommand(TrackerEffectCommand effect) =>
            effect >= TrackerEffectCommand.VolumeSlide && effect <= TrackerEffectCommand.NoteDelay;

        static bool IsOscillating(TrackerEffectCommand effect) =>
            effect == TrackerEffectCommand.Vibrato || effect == TrackerEffectCommand.Arpeggio;

        static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
    }
}
