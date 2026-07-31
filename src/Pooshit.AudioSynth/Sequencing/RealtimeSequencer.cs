using System;
using System.Collections.Generic;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Sequencing.Timeline;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Sequencing {

    /// <summary>
    /// Real-time pull driver over a <see cref="CompiledSchedule"/>: a sample cursor that, per
    /// <see cref="Read"/>, dispatches every due event at its exact sub-block sample offset and pulls the
    /// inter-event gaps straight from the bound <see cref="ISynthesizer"/> — no MIDI, no
    /// block-quantization. Single-thread contract: <see cref="Read"/> and every mutation/transport call
    /// must come from the same thread.
    /// </summary>
    public sealed class RealtimeSequencer : IAudioSource {

        readonly CompiledSchedule schedule;
        readonly ISynthesizer synthesizer;
        readonly long releaseTailFrames;
        readonly bool loopEnabled;
        readonly long loopStart;
        readonly long loopEnd;
        readonly long endOfStreamFrame;
        readonly HashSet<int> pendingTriggers = new HashSet<int>();
        SoundBank soundBank;
        long cursor;
        int scheduleIndex;
        bool ended;

        /// <summary>Creates a <see cref="RealtimeSequencer"/> over an already-compiled schedule.</summary>
        /// <param name="schedule">the compiled, offset-sorted event stream to dispatch</param>
        /// <param name="synthesizer">the engine pulled for inter-event audio</param>
        /// <param name="soundBank">bound bank resolving each <see cref="NeutralEventKind.SetPatch"/>'s symbolic (bank, program)</param>
        /// <param name="releaseTailFrames">non-loop tail rendered past the last event before end-of-stream</param>
        /// <param name="loopStart">inclusive loop-region start, in samples; <c>null</c> disables looping</param>
        /// <param name="loopEnd">exclusive loop-region end, in samples; required iff <paramref name="loopStart"/> is set</param>
        public RealtimeSequencer(CompiledSchedule schedule, ISynthesizer synthesizer, SoundBank soundBank,
            long releaseTailFrames, long? loopStart = null, long? loopEnd = null) {
            this.schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
            this.synthesizer = synthesizer ?? throw new ArgumentNullException(nameof(synthesizer));
            this.soundBank = soundBank ?? throw new ArgumentNullException(nameof(soundBank));
            if (releaseTailFrames < 0)
                throw new ArgumentOutOfRangeException(nameof(releaseTailFrames), releaseTailFrames, "Release tail must be non-negative.");
            this.releaseTailFrames = releaseTailFrames;

            if (loopStart.HasValue != loopEnd.HasValue)
                throw new ArgumentException("loopStart and loopEnd must both be set, or both be null.");
            loopEnabled = loopStart.HasValue;
            this.loopStart = loopStart ?? 0;
            this.loopEnd = loopEnd ?? 0;
            if (loopEnabled && this.loopEnd <= this.loopStart)
                throw new ArgumentOutOfRangeException(nameof(loopEnd), this.loopEnd, "loopEnd must be greater than loopStart.");

            long lastEventOffset = schedule.Count > 0 ? schedule.Entries[schedule.Count - 1].SampleOffset : 0;
            endOfStreamFrame = lastEventOffset + releaseTailFrames;
        }

        /// <inheritdoc/>
        public AudioFormat Format => synthesizer.Format;

        /// <summary>Rebinds the sound bank used to resolve symbolic patches, e.g. to swap a soundfont without rebuilding the timeline.</summary>
        public void Bind(SoundBank newSoundBank) {
            soundBank = newSoundBank ?? throw new ArgumentNullException(nameof(newSoundBank));
        }

        /// <summary>
        /// Records a rhythm-game trigger for <paramref name="gateId"/> (Phase 3 seam); not yet consulted
        /// by any policy, since Phase 1 BGM playback never constructs a gated entry.
        /// </summary>
        public void Trigger(int gateId) {
            pendingTriggers.Add(gateId);
        }

        /// <inheritdoc/>
        public int Read(Span<float> destination) {
            int channels = Format.Channels;
            if (destination.Length % channels != 0)
                throw new ArgumentException(
                    $"destination length ({destination.Length}) must be a multiple of the channel count ({channels}).",
                    nameof(destination));

            int totalFrames = destination.Length / channels;
            int producedFrames = 0;

            while (producedFrames < totalFrames) {
                if (!loopEnabled && ended)
                    break;

                long limit = ComputeLimit(cursor, totalFrames - producedFrames);

                while (scheduleIndex < schedule.Count && schedule.Entries[scheduleIndex].SampleOffset < cursor)
                    scheduleIndex++;

                if (scheduleIndex < schedule.Count && schedule.Entries[scheduleIndex].SampleOffset < limit) {
                    long dueGap = schedule.Entries[scheduleIndex].SampleOffset - cursor;
                    int rendered = RenderGap(destination, producedFrames, dueGap);
                    producedFrames += rendered;
                    if (rendered < dueGap)
                        break; // underlying source underfilled; stop rather than spin on a stalled cursor
                    while (scheduleIndex < schedule.Count && schedule.Entries[scheduleIndex].SampleOffset == cursor)
                        Dispatch(schedule.Entries[scheduleIndex++].Event);
                    continue;
                }

                long tailGap = limit - cursor;
                int tailRendered = RenderGap(destination, producedFrames, tailGap);
                producedFrames += tailRendered;
                if (tailRendered < tailGap)
                    break;

                if (loopEnabled && cursor >= loopEnd) {
                    cursor = loopStart;
                    scheduleIndex = schedule.FindFirstAtOrAfter(loopStart);
                } else if (!loopEnabled && cursor >= endOfStreamFrame) {
                    ended = true;
                    break;
                }
            }

            return producedFrames * channels;
        }

        long ComputeLimit(long from, long requestedFrames) {
            long limit = from + requestedFrames;
            if (loopEnabled && from < loopEnd && loopEnd < limit)
                limit = loopEnd;
            if (!loopEnabled && endOfStreamFrame < limit)
                limit = endOfStreamFrame;
            return limit;
        }

        int RenderGap(Span<float> destination, int producedFrames, long gapFrames) {
            if (gapFrames <= 0)
                return 0;
            int channels = Format.Channels;
            Span<float> slice = destination.Slice(producedFrames * channels, (int)gapFrames * channels);
            int read = synthesizer.Read(slice);
            cursor += read / channels;
            return read / channels;
        }

        void Dispatch(NeutralEvent @event) {
            switch (@event.Kind) {
                case NeutralEventKind.NoteOn:
                    synthesizer.NoteOn(@event.Channel, @event.Key, @event.Velocity);
                    break;
                case NeutralEventKind.NoteOff:
                    synthesizer.NoteOff(@event.Channel, @event.Key);
                    break;
                case NeutralEventKind.SetPatch:
                    synthesizer.SetChannelPatch(@event.Channel, soundBank.GetPatch(@event.Bank, @event.Program));
                    break;
                case NeutralEventKind.SetGain:
                    synthesizer.SetChannelGain(@event.Channel, @event.Value);
                    break;
                case NeutralEventKind.SetPan:
                    synthesizer.SetChannelPan(@event.Channel, @event.Value);
                    break;
                case NeutralEventKind.SetPitchBend:
                    synthesizer.SetChannelPitchBend(@event.Channel, @event.Value);
                    break;
                case NeutralEventKind.SetModulation:
                    synthesizer.SetChannelModulation(@event.Channel, @event.Value);
                    break;
                case NeutralEventKind.SetReverbSend:
                    synthesizer.SetChannelReverbSend(@event.Channel, @event.Value);
                    break;
                case NeutralEventKind.SetChorusSend:
                    synthesizer.SetChannelChorusSend(@event.Channel, @event.Value);
                    break;
                case NeutralEventKind.SetSustain:
                    synthesizer.SetChannelSustain(@event.Channel, @event.Held);
                    break;
                case NeutralEventKind.SilenceChannel:
                    synthesizer.SilenceChannel(@event.Channel);
                    break;
                case NeutralEventKind.ReleaseAllNotes:
                    synthesizer.ReleaseAllNotes(@event.Channel);
                    break;
            }
        }
    }
}
