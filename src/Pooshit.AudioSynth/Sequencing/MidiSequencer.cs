using System;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Formats.Midi;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Sequencing {

    /// <summary>
    /// Drives an <see cref="ISynthesizer"/> from a <see cref="TimedMessageSequence"/>, owning all GM/MIDI
    /// routing semantics: per-channel <c>ProgramChange</c> and channel 9 = percussion (DiVoid #7117).
    /// </summary>
    public static class MidiSequencer {

        /// <summary>
        /// Fixed release tail rendered after the last scheduled event so envelope releases finish
        /// audibly; not a configurable knob (DiVoid #7098 §8).
        /// </summary>
        public const float ReleaseTailSeconds = 3.0f;

        const int ChannelCount = 16;
        const int PercussionChannel = 9;
        const int MelodicBank = 0;
        const int PercussionBank = 128;
        const int DefaultProgram = 0;

        /// <summary>GM-reset default for CC7 (Channel Volume).</summary>
        const int DefaultChannelVolume = 100;

        /// <summary>GM-reset default for CC11 (Expression).</summary>
        const int DefaultExpression = 127;

        /// <summary>Full-scale value for a 7-bit MIDI controller.</summary>
        const int ControllerFullScale = 127;

        /// <summary>The centered (no-pan) 7-bit CC10 (Pan) value.</summary>
        const int PanControllerCenter = 64;

        /// <summary>GM1 default for CC91 (Effects 1 Depth / reverb send).</summary>
        const int DefaultReverbSend = 40;

        /// <summary>GM default for CC93 (Effects 3 Depth / chorus send): off unless the song raises it.</summary>
        const int DefaultChorusSend = 0;

        /// <summary>
        /// CC64 (Hold Pedal 1 / sustain) threshold: a raw value at or above this is "pedal down"
        /// (MIDI convention), below it is "pedal up".
        /// </summary>
        const int SustainPedalThreshold = 64;

        /// <summary>GM default pitch-bend range, in semitones, applied symmetrically around center.</summary>
        const float PitchBendSemitoneRange = 2f;

        /// <summary>The centered (no-bend) 14-bit PitchWheel value.</summary>
        const int PitchWheelCenter = 8192;

        /// <summary>The 14-bit PitchWheel value span from center to either extreme.</summary>
        const int PitchWheelSpan = 8192;

        /// <summary>
        /// The 14-bit RPN-null selector (CC101=127, CC100=127): the standard MIDI value meaning
        /// "no RPN armed", so a stray Data Entry (CC6) is ignored (DiVoid #7210 §6).
        /// </summary>
        const int RpnNull = 16383;

        /// <summary>
        /// Builds the ordered sample-offset schedule for <paramref name="sequence"/>; pure, no audio
        /// touched. Folds a <c>NoteOn</c> with velocity 0 into its <c>NoteOff</c> equivalent.
        /// </summary>
        /// <param name="sequence">the timed message sequence to schedule</param>
        /// <param name="sampleRate">the target render sample rate, in frames per second</param>
        /// <returns>events ordered non-decreasing by <see cref="ScheduledMidiEvent.SampleOffset"/></returns>
        public static ScheduledMidiEvent[] BuildSchedule(TimedMessageSequence sequence, int sampleRate) {
            if (sequence is null)
                throw new ArgumentNullException(nameof(sequence));
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive.");

            TimedMidiMessage[] timed = sequence.Messages;
            ScheduledMidiEvent[] schedule = new ScheduledMidiEvent[timed.Length];
            for (int i = 0; i < timed.Length; i++) {
                long sampleOffset = (long)Math.Round(timed[i].Time * sampleRate, MidpointRounding.AwayFromZero);
                schedule[i] = new ScheduledMidiEvent(sampleOffset, FoldNoteOnVelocityZero(timed[i].Message));
            }
            return schedule;
        }

        /// <summary>
        /// Renders <paramref name="sequence"/> through <paramref name="synthesizer"/> into
        /// <paramref name="sink"/>, GM-resetting all 16 channels from <paramref name="soundBank"/>
        /// first, then renders the fixed release tail.
        /// </summary>
        /// <param name="sequence">the timed message sequence to render</param>
        /// <param name="synthesizer">the engine driving per-channel patches</param>
        /// <param name="sink">the destination for rendered audio</param>
        /// <param name="soundBank">the (bank, program) patch lookup used for GM routing</param>
        /// <returns>the total number of frames written, including the release tail</returns>
        public static long Render(TimedMessageSequence sequence, ISynthesizer synthesizer, IAudioSink sink, SoundBank soundBank) {
            if (synthesizer is null)
                throw new ArgumentNullException(nameof(synthesizer));
            if (sink is null)
                throw new ArgumentNullException(nameof(sink));
            if (soundBank is null)
                throw new ArgumentNullException(nameof(soundBank));

            int[] cc7 = new int[ChannelCount];
            int[] cc11 = new int[ChannelCount];
            int[] selectedRpn = new int[ChannelCount];
            float[] bendRange = new float[ChannelCount];
            for (int channel = 0; channel < ChannelCount; channel++) {
                synthesizer.SetChannelPatch(channel, ResolveProgramPatch(soundBank, channel, DefaultProgram));
                cc7[channel] = DefaultChannelVolume;
                cc11[channel] = DefaultExpression;
                synthesizer.SetChannelGain(channel, ChannelGain(cc7[channel], cc11[channel]));
                synthesizer.SetChannelPan(channel, 0f);
                synthesizer.SetChannelReverbSend(channel, DefaultReverbSend / (float)ControllerFullScale);
                synthesizer.SetChannelChorusSend(channel, DefaultChorusSend / (float)ControllerFullScale);
                selectedRpn[channel] = RpnNull;
                bendRange[channel] = PitchBendSemitoneRange;
            }

            ScheduledMidiEvent[] schedule = BuildSchedule(sequence, synthesizer.Format.SampleRate);
            long cursor = 0;

            foreach (ScheduledMidiEvent scheduled in schedule) {
                long gap = scheduled.SampleOffset - cursor;
                if (gap > 0)
                    cursor += OfflineRenderer.Render(synthesizer, sink, gap);
                ApplyMessage(scheduled.Message, synthesizer, soundBank, cc7, cc11, selectedRpn, bendRange);
            }

            long tailFrames = (long)Math.Round(ReleaseTailSeconds * synthesizer.Format.SampleRate, MidpointRounding.AwayFromZero);
            cursor += OfflineRenderer.Render(synthesizer, sink, tailFrames);
            return cursor;
        }

        static IMidiMessage FoldNoteOnVelocityZero(IMidiMessage message) {
            if (message is ChannelMessage channel && channel.Command == ChannelCommandType.NoteOn && channel.Data2 == 0)
                return new ChannelMessage(ChannelCommandType.NoteOff, channel.MidiChannel, channel.Data1, 0);
            return message;
        }

        /// <summary>
        /// Applies one scheduled MIDI message. CC101/CC100 arm a channel's 14-bit RPN selector;
        /// while that selector is 0 (RPN 0, Pitch Bend Sensitivity), CC6 sets <paramref name="bendRange"/>
        /// for the channel, otherwise CC6 is ignored (DiVoid #7210). CC120/CC121/CC123 route to the
        /// Tier-1 GM housekeeping channel-mode controllers (design #7245): CC120 hard-silences the
        /// channel via <see cref="ISynthesizer.SilenceChannel"/>, CC123 releases every held note via
        /// <see cref="ISynthesizer.ReleaseAllNotes"/>, and CC121 resets a defined controller subset via
        /// <see cref="ResetAllControllers"/>.
        /// </summary>
        static void ApplyMessage(IMidiMessage message, ISynthesizer synthesizer, SoundBank soundBank, int[] cc7, int[] cc11, int[] selectedRpn, float[] bendRange) {
            if (!(message is ChannelMessage channel))
                return;

            switch (channel.Command) {
                case ChannelCommandType.NoteOn:
                    synthesizer.NoteOn(channel.MidiChannel, channel.Data1, channel.Data2);
                    break;
                case ChannelCommandType.NoteOff:
                    synthesizer.NoteOff(channel.MidiChannel, channel.Data1);
                    break;
                case ChannelCommandType.ProgramChange:
                    synthesizer.SetChannelPatch(channel.MidiChannel, ResolveProgramPatch(soundBank, channel.MidiChannel, channel.Data1));
                    break;
                case ChannelCommandType.Controller:
                    if (channel.Data1 == (byte)ControllerType.Pan) {
                        synthesizer.SetChannelPan(channel.MidiChannel, ControllerToPan(channel.Data2));
                        break;
                    }
                    if (channel.Data1 == (byte)ControllerType.EffectsLevel) {
                        synthesizer.SetChannelReverbSend(channel.MidiChannel, channel.Data2 / (float)ControllerFullScale);
                        break;
                    }
                    if (channel.Data1 == (byte)ControllerType.ChorusLevel) {
                        synthesizer.SetChannelChorusSend(channel.MidiChannel, channel.Data2 / (float)ControllerFullScale);
                        break;
                    }
                    if (channel.Data1 == (byte)ControllerType.HoldPedal1) {
                        synthesizer.SetChannelSustain(channel.MidiChannel, channel.Data2 >= SustainPedalThreshold);
                        break;
                    }
                    if (channel.Data1 == (byte)ControllerType.ModulationWheel) {
                        synthesizer.SetChannelModulation(channel.MidiChannel, channel.Data2 / (float)ControllerFullScale);
                        break;
                    }
                    if (channel.Data1 == (byte)ControllerType.RegisteredParameterCoarse) {
                        selectedRpn[channel.MidiChannel] = (channel.Data2 << 7) | (selectedRpn[channel.MidiChannel] & 0x7F);
                        break;
                    }
                    if (channel.Data1 == (byte)ControllerType.RegisteredParameterFine) {
                        selectedRpn[channel.MidiChannel] = (selectedRpn[channel.MidiChannel] & 0x3F80) | channel.Data2;
                        break;
                    }
                    if (channel.Data1 == (byte)ControllerType.DataEntrySlider) {
                        if (selectedRpn[channel.MidiChannel] == 0)
                            bendRange[channel.MidiChannel] = channel.Data2;
                        break;
                    }
                    if (channel.Data1 == (byte)ControllerType.AllSoundOff) {
                        synthesizer.SilenceChannel(channel.MidiChannel);
                        break;
                    }
                    if (channel.Data1 == (byte)ControllerType.AllNotesOff) {
                        synthesizer.ReleaseAllNotes(channel.MidiChannel);
                        break;
                    }
                    if (channel.Data1 == (byte)ControllerType.AllControllersOff) {
                        ResetAllControllers(channel.MidiChannel, synthesizer, cc7, cc11, selectedRpn);
                        break;
                    }
                    if (channel.Data1 == (byte)ControllerType.Volume)
                        cc7[channel.MidiChannel] = channel.Data2;
                    else if (channel.Data1 == (byte)ControllerType.Expression)
                        cc11[channel.MidiChannel] = channel.Data2;
                    else
                        break;
                    synthesizer.SetChannelGain(channel.MidiChannel, ChannelGain(cc7[channel.MidiChannel], cc11[channel.MidiChannel]));
                    break;
                case ChannelCommandType.PitchWheel:
                    int value14 = (channel.Data2 << 7) | channel.Data1;
                    float semitones = (value14 - PitchWheelCenter) / (float)PitchWheelSpan * bendRange[channel.MidiChannel];
                    synthesizer.SetChannelPitchBend(channel.MidiChannel, semitones);
                    break;
            }
        }

        /// <summary>
        /// CC121 (Reset All Controllers, GM channel-mode): resets a channel's <em>controller</em> state
        /// to GM defaults via existing seams — modulation (CC1) to 0, expression (CC11) to
        /// <see cref="DefaultExpression"/> (recomputing gain with <paramref name="cc7"/> preserved),
        /// sustain (CC64) off (which itself sweeps and releases any pedal-deferred voice), pitch-bend to
        /// center, and the RPN selector to <see cref="RpnNull"/>. Deliberately a strict subset of the
        /// full GM-reset loop in <see cref="Render"/>: pan, program/bank, reverb send (CC91), chorus send
        /// (CC93), <paramref name="cc7"/> (volume) itself, and the stored <c>bendRange</c> value are all
        /// untouched (design #7245 §4/§11) — do not refactor this to call the startup reset loop, which
        /// resets strictly more than GM RAC specifies.
        /// </summary>
        static void ResetAllControllers(int channel, ISynthesizer synthesizer, int[] cc7, int[] cc11, int[] selectedRpn) {
            synthesizer.SetChannelModulation(channel, 0f);

            cc11[channel] = DefaultExpression;
            synthesizer.SetChannelGain(channel, ChannelGain(cc7[channel], cc11[channel]));

            synthesizer.SetChannelSustain(channel, false);

            synthesizer.SetChannelPitchBend(channel, 0f);

            selectedRpn[channel] = RpnNull;
        }

        static IPatch ResolveProgramPatch(SoundBank soundBank, int channel, int program) {
            int bank = channel == PercussionChannel ? PercussionBank : MelodicBank;
            return soundBank.GetPatch(bank, program);
        }

        /// <summary>
        /// Combined linear mix gain for CC7 (Volume) and CC11 (Expression):
        /// <c>(cc7/127)² · (cc11/127)²</c> (DiVoid #7126 §9).
        /// </summary>
        /// <param name="cc7">raw CC7 (Channel Volume) value, 0-127</param>
        /// <param name="cc11">raw CC11 (Expression) value, 0-127</param>
        /// <returns>linear gain in [0,1]; 1.0 when both controllers are at full scale</returns>
        static float ChannelGain(int cc7, int cc11) {
            float volume = cc7 / (float)ControllerFullScale;
            float expression = cc11 / (float)ControllerFullScale;
            return volume * volume * expression * expression;
        }

        /// <summary>
        /// Maps a raw CC10 (Pan) value (0-127) to a signed engine pan ∈ [-1,1]: 0 → -1 (full left),
        /// 64 → 0 (centre), 127 → +0.984 (≈ full right; the symmetric divisor leaves the top rail
        /// slightly short, inaudible under equal-power placement).
        /// </summary>
        /// <param name="value">raw CC10 value, 0-127</param>
        /// <returns>signed pan in [-1,1]</returns>
        static float ControllerToPan(int value) {
            float pan = (value - PanControllerCenter) / (float)PanControllerCenter;
            if (pan < -1f)
                pan = -1f;
            if (pan > 1f)
                pan = 1f;
            return pan;
        }
    }
}
