using System;
using System.Collections.Generic;
using Pooshit.AudioSynth.Formats.Midi;
using Pooshit.AudioSynth.Sequencing.Timeline;

namespace Pooshit.AudioSynth.Sequencing {

    /// <summary>
    /// MIDI importer: runs the full GM decode state machine that used to be fused into the offline
    /// renderer's message dispatch and emits a MIDI-neutral <see cref="Timeline.Timeline"/>. Bank-select
    /// and RPN are decode-only state, never timeline events; they are folded into resolved
    /// <see cref="NeutralEvent.SetPatch"/>/<see cref="NeutralEvent.SetPitchBend"/> events.
    /// </summary>
    public static class MidiTimelineImporter {

        const int ChannelCount = 16;
        const int PercussionChannel = 9;
        const int PercussionBank = 128;
        const int DefaultProgram = 0;
        const int DefaultBankValue = 0;
        const int DefaultChannelVolume = 100;
        const int DefaultExpression = 127;
        const int ControllerFullScale = 127;
        const int PanControllerCenter = 64;
        const int DefaultReverbSend = 40;
        const int DefaultChorusSend = 0;
        const int SustainPedalThreshold = 64;
        const float PitchBendSemitoneRange = 2f;
        const int PitchWheelCenter = 8192;
        const int PitchWheelSpan = 8192;
        const int RpnNull = 16383;

        /// <summary>
        /// Imports <paramref name="sequence"/> into a fresh <see cref="Timeline.Timeline"/>: GM-resets all
        /// 16 channels as neutral events at offset 0, then walks <see cref="MidiSequencer.BuildSchedule"/>
        /// running the GM decode state machine verbatim, reproducing the pre-refactor synth-call sequence.
        /// </summary>
        public static Timeline.Timeline Import(TimedMessageSequence sequence, int sampleRate) {
            Timeline.Timeline timeline = new Timeline.Timeline();

            int[] cc7 = new int[ChannelCount];
            int[] cc11 = new int[ChannelCount];
            int[] selectedRpn = new int[ChannelCount];
            float[] bendRange = new float[ChannelCount];
            int[] bankMsb = new int[ChannelCount];
            int[] bankLsb = new int[ChannelCount];
            for (int channel = 0; channel < ChannelCount; channel++) {
                bankMsb[channel] = DefaultBankValue;
                bankLsb[channel] = DefaultBankValue;
                timeline.Add(0, NeutralEvent.SetPatch(channel, ResolveBank(channel, bankMsb[channel], bankLsb[channel]), DefaultProgram));
                cc7[channel] = DefaultChannelVolume;
                cc11[channel] = DefaultExpression;
                timeline.Add(0, NeutralEvent.SetGain(channel, ChannelGain(cc7[channel], cc11[channel])));
                timeline.Add(0, NeutralEvent.SetPan(channel, 0f));
                timeline.Add(0, NeutralEvent.SetReverbSend(channel, DefaultReverbSend / (float)ControllerFullScale));
                timeline.Add(0, NeutralEvent.SetChorusSend(channel, DefaultChorusSend / (float)ControllerFullScale));
                selectedRpn[channel] = RpnNull;
                bendRange[channel] = PitchBendSemitoneRange;
            }

            Dictionary<(int Channel, int Key), Stack<long>> openNotes = new Dictionary<(int, int), Stack<long>>();
            ScheduledMidiEvent[] schedule = MidiSequencer.BuildSchedule(sequence, sampleRate);
            foreach (ScheduledMidiEvent scheduled in schedule)
                ApplyToTimeline(scheduled, timeline, cc7, cc11, selectedRpn, bendRange, bankMsb, bankLsb, openNotes);

            return timeline;
        }

        static void ApplyToTimeline(ScheduledMidiEvent scheduled, Timeline.Timeline timeline, int[] cc7, int[] cc11,
            int[] selectedRpn, float[] bendRange, int[] bankMsb, int[] bankLsb,
            Dictionary<(int Channel, int Key), Stack<long>> openNotes) {
            if (!(scheduled.Message is ChannelMessage channel))
                return;

            long offset = scheduled.SampleOffset;
            switch (channel.Command) {
                case ChannelCommandType.NoteOn: {
                    long id = timeline.Add(offset, NeutralEvent.NoteOn(channel.MidiChannel, channel.Data1, channel.Data2));
                    (int, int) key = (channel.MidiChannel, channel.Data1);
                    if (!openNotes.TryGetValue(key, out Stack<long>? stack)) {
                        stack = new Stack<long>();
                        openNotes[key] = stack;
                    }
                    stack.Push(id);
                    break;
                }
                case ChannelCommandType.NoteOff: {
                    long id = timeline.Add(offset, NeutralEvent.NoteOff(channel.MidiChannel, channel.Data1));
                    (int, int) key = (channel.MidiChannel, channel.Data1);
                    if (openNotes.TryGetValue(key, out Stack<long>? stack) && stack.Count > 0)
                        timeline.LinkNote(stack.Pop(), id);
                    break;
                }
                case ChannelCommandType.ProgramChange:
                    timeline.Add(offset, NeutralEvent.SetPatch(channel.MidiChannel,
                        ResolveBank(channel.MidiChannel, bankMsb[channel.MidiChannel], bankLsb[channel.MidiChannel]), channel.Data1));
                    break;
                case ChannelCommandType.Controller:
                    ApplyController(channel, offset, timeline, cc7, cc11, selectedRpn, bendRange, bankMsb, bankLsb);
                    break;
                case ChannelCommandType.PitchWheel:
                    int value14 = (channel.Data2 << 7) | channel.Data1;
                    float semitones = (value14 - PitchWheelCenter) / (float)PitchWheelSpan * bendRange[channel.MidiChannel];
                    timeline.Add(offset, NeutralEvent.SetPitchBend(channel.MidiChannel, semitones));
                    break;
            }
        }

        static void ApplyController(ChannelMessage channel, long offset, Timeline.Timeline timeline, int[] cc7, int[] cc11,
            int[] selectedRpn, float[] bendRange, int[] bankMsb, int[] bankLsb) {
            if (channel.Data1 == (byte)ControllerType.BankSelect) {
                bankMsb[channel.MidiChannel] = channel.Data2;
                return;
            }
            if (channel.Data1 == (byte)ControllerType.BankSelectFine) {
                bankLsb[channel.MidiChannel] = channel.Data2;
                return;
            }
            if (channel.Data1 == (byte)ControllerType.Pan) {
                timeline.Add(offset, NeutralEvent.SetPan(channel.MidiChannel, ControllerToPan(channel.Data2)));
                return;
            }
            if (channel.Data1 == (byte)ControllerType.EffectsLevel) {
                timeline.Add(offset, NeutralEvent.SetReverbSend(channel.MidiChannel, channel.Data2 / (float)ControllerFullScale));
                return;
            }
            if (channel.Data1 == (byte)ControllerType.ChorusLevel) {
                timeline.Add(offset, NeutralEvent.SetChorusSend(channel.MidiChannel, channel.Data2 / (float)ControllerFullScale));
                return;
            }
            if (channel.Data1 == (byte)ControllerType.HoldPedal1) {
                timeline.Add(offset, NeutralEvent.SetSustain(channel.MidiChannel, channel.Data2 >= SustainPedalThreshold));
                return;
            }
            if (channel.Data1 == (byte)ControllerType.ModulationWheel) {
                timeline.Add(offset, NeutralEvent.SetModulation(channel.MidiChannel, channel.Data2 / (float)ControllerFullScale));
                return;
            }
            if (channel.Data1 == (byte)ControllerType.RegisteredParameterCoarse) {
                selectedRpn[channel.MidiChannel] = (channel.Data2 << 7) | (selectedRpn[channel.MidiChannel] & 0x7F);
                return;
            }
            if (channel.Data1 == (byte)ControllerType.RegisteredParameterFine) {
                selectedRpn[channel.MidiChannel] = (selectedRpn[channel.MidiChannel] & 0x3F80) | channel.Data2;
                return;
            }
            if (channel.Data1 == (byte)ControllerType.DataEntrySlider) {
                if (selectedRpn[channel.MidiChannel] == 0)
                    bendRange[channel.MidiChannel] = channel.Data2;
                return;
            }
            if (channel.Data1 == (byte)ControllerType.AllSoundOff) {
                timeline.Add(offset, NeutralEvent.SilenceChannel(channel.MidiChannel));
                return;
            }
            if (channel.Data1 == (byte)ControllerType.AllNotesOff) {
                timeline.Add(offset, NeutralEvent.ReleaseAllNotes(channel.MidiChannel));
                return;
            }
            if (channel.Data1 == (byte)ControllerType.AllControllersOff) {
                ResetAllControllers(channel.MidiChannel, offset, timeline, cc7, cc11, selectedRpn);
                return;
            }
            if (channel.Data1 == (byte)ControllerType.Volume)
                cc7[channel.MidiChannel] = channel.Data2;
            else if (channel.Data1 == (byte)ControllerType.Expression)
                cc11[channel.MidiChannel] = channel.Data2;
            else
                return;
            timeline.Add(offset, NeutralEvent.SetGain(channel.MidiChannel, ChannelGain(cc7[channel.MidiChannel], cc11[channel.MidiChannel])));
        }

        static void ResetAllControllers(int channel, long offset, Timeline.Timeline timeline, int[] cc7, int[] cc11, int[] selectedRpn) {
            timeline.Add(offset, NeutralEvent.SetModulation(channel, 0f));

            cc11[channel] = DefaultExpression;
            timeline.Add(offset, NeutralEvent.SetGain(channel, ChannelGain(cc7[channel], cc11[channel])));

            timeline.Add(offset, NeutralEvent.SetSustain(channel, false));

            timeline.Add(offset, NeutralEvent.SetPitchBend(channel, 0f));

            selectedRpn[channel] = RpnNull;
        }

        static int ResolveBank(int channel, int msb, int lsb) {
            return channel == PercussionChannel ? PercussionBank : msb;
        }

        static float ChannelGain(int cc7, int cc11) {
            float volume = cc7 / (float)ControllerFullScale;
            float expression = cc11 / (float)ControllerFullScale;
            return volume * volume * expression * expression;
        }

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
