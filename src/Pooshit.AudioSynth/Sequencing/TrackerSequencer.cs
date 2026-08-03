using System;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Formats.Tracker;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Sequencing {

    /// <summary>
    /// Live cursor playback of a mutable <see cref="Song"/>: applies each row's cells to a bound
    /// <see cref="ISynthesizer"/> and pulls its audio (no Timeline). Single-thread: <see cref="Read"/> and every mutation share one thread.
    /// </summary>
    public sealed class TrackerSequencer : IAudioSource {

        const int MaxChannels = 16;
        const double TickSecondsScale = 2.5;

        readonly Song song;
        readonly ISynthesizer synth;
        readonly int channelCount;
        readonly int sampleRate;
        readonly TrackerEffectEngine engine;

        int orderIndex;
        int row;
        int currentOrder;
        int currentRow;
        int currentSpeed;
        int currentTempo;
        double rowStartCursor;
        double currentRowSpr;
        double sprPerTick;
        int tickIndex;
        long currentTickSamples;
        long sampleWithinTick;
        bool rowApplied;
        bool playing;
        bool pendingJump;
        int pendingJumpTarget;
        bool channelsSeeded;

        /// <summary>Creates a sequencer over a live song, driving <paramref name="synth"/> via <paramref name="soundBank"/>.</summary>
        /// <param name="song">the live composition the cursor walks</param>
        /// <param name="synth">the engine the cursor drives and pulls audio from</param>
        /// <param name="soundBank">bank resolving each instrument's symbolic (bank, program)</param>
        public TrackerSequencer(Song song, ISynthesizer synth, SoundBank soundBank) {
            this.song = song ?? throw new ArgumentNullException(nameof(song));
            this.synth = synth ?? throw new ArgumentNullException(nameof(synth));
            if (soundBank is null)
                throw new ArgumentNullException(nameof(soundBank));
            if (song.ChannelCount < 1 || song.ChannelCount > MaxChannels)
                throw new ArgumentException($"Song.ChannelCount must be in [1,{MaxChannels}]; the synth is a {MaxChannels}-channel engine.", nameof(song));
            if (song.DefaultBpm <= 0)
                throw new ArgumentException("Song.DefaultBpm must be positive.", nameof(song));
            if (song.DefaultSpeed <= 0)
                throw new ArgumentException("Song.DefaultSpeed must be positive.", nameof(song));
            if (song.ChannelPan.Length != 0 && song.ChannelPan.Length != song.ChannelCount)
                throw new ArgumentException($"Song.ChannelPan must be empty or have length equal to ChannelCount ({song.ChannelCount}).", nameof(song));

            channelCount = song.ChannelCount;
            sampleRate = synth.Format.SampleRate;
            engine = new TrackerEffectEngine(channelCount, synth, soundBank);
            currentSpeed = song.DefaultSpeed;
            currentTempo = song.DefaultBpm;
        }

        /// <inheritdoc/>
        public AudioFormat Format => synth.Format;

        /// <summary>Whether end-of-order-list wraps back to the first order (true) or stops (false).</summary>
        public bool Looping { get; set; }

        /// <summary>Whether the transport is currently advancing the cursor.</summary>
        public bool IsPlaying => playing;

        /// <summary>Order-list position of the currently sounding row (the playhead).</summary>
        public int OrderIndex => currentOrder;

        /// <summary>Row of the currently sounding row within its pattern (the playhead).</summary>
        public int Row => currentRow;

        /// <summary>Begins or resumes playback, re-applying the current row on the next <see cref="Read"/>.</summary>
        public void Play() {
            playing = true;
            rowApplied = false;
        }

        /// <summary>Stops advancing and silences every channel; the cursor and running timing are kept.</summary>
        public void Stop() {
            playing = false;
            SilenceAll();
        }

        /// <summary>Moves the cursor to a position, resetting timing to song defaults and clearing sounding state.</summary>
        /// <param name="order">target order-list position</param>
        /// <param name="targetRow">target row within the pattern at that position</param>
        public void SeekTo(int order, int targetRow) {
            SilenceAll();
            engine.Reset();
            currentSpeed = song.DefaultSpeed;
            currentTempo = song.DefaultBpm;
            orderIndex = order < 0 ? 0 : order;
            row = targetRow < 0 ? 0 : targetRow;
            currentOrder = orderIndex;
            currentRow = row;
            rowStartCursor = 0.0;
            tickIndex = 0;
            sampleWithinTick = 0;
            rowApplied = false;
            pendingJump = false;
            channelsSeeded = false;
        }

        /// <inheritdoc/>
        public int Read(Span<float> destination) {
            int channels = Format.Channels;
            if (destination.Length % channels != 0)
                throw new ArgumentException(
                    $"destination length ({destination.Length}) must be a multiple of the channel count ({channels}).",
                    nameof(destination));

            int totalFrames = destination.Length / channels;
            int produced = 0;

            while (produced < totalFrames) {
                if (playing && !rowApplied)
                    EnterRow();

                int frames = playing
                    ? (int)Math.Min(totalFrames - produced, currentTickSamples - sampleWithinTick)
                    : totalFrames - produced;
                int got = synth.Read(destination.Slice(produced * channels, frames * channels)) / channels;
                produced += got;

                if (playing) {
                    sampleWithinTick += got;
                    if (got == frames && sampleWithinTick >= currentTickSamples)
                        AdvanceTick();
                }

                if (got < frames)
                    break;
            }

            return produced * channels;
        }

        void AdvanceTick() {
            int nextTick = tickIndex + 1;
            if (nextTick >= currentSpeed) {
                rowStartCursor += currentRowSpr;
                AdvanceRow();
                rowApplied = false;
            }
            else {
                engine.Tick(nextTick);
                EnterTick(nextTick);
            }
        }

        void EnterTick(int t) {
            tickIndex = t;
            long start = (long)Math.Round(rowStartCursor + t * sprPerTick);
            long end = (long)Math.Round(rowStartCursor + (t + 1) * sprPerTick);
            currentTickSamples = end - start;
            if (currentTickSamples < 1)
                currentTickSamples = 1;
            sampleWithinTick = 0;
        }

        void EnterRow() {
            int guard = 0;
            while (true) {
                if (orderIndex >= song.Order.Length) {
                    if (Looping && song.Order.Length > 0)
                        orderIndex = 0;
                    else {
                        playing = false;
                        return;
                    }
                }

                int patternIndex = song.Order[orderIndex];
                Pattern? pattern = patternIndex >= 0 && patternIndex < song.Patterns.Length
                    ? song.Patterns[patternIndex]
                    : null;
                int effectiveRows = pattern is null ? 0 : pattern.Rows ?? song.DefaultRows;
                bool playable = pattern != null
                    && effectiveRows > 0
                    && pattern.Cells.Length >= effectiveRows * channelCount
                    && row < effectiveRows;
                if (!playable) {
                    row = 0;
                    if (!AdvanceToNextOrder(ref guard))
                        return;
                    continue;
                }

                ScanTimingAndJump(pattern!);
                ApplyRow(pattern!);
                return;
            }
        }

        bool AdvanceToNextOrder(ref int guard) {
            orderIndex++;
            guard++;
            if (guard > song.Order.Length) {
                playing = false;
                return false;
            }
            return true;
        }

        void ScanTimingAndJump(Pattern pattern) {
            pendingJump = false;
            int rowBase = row * channelCount;
            for (int channel = 0; channel < channelCount; channel++) {
                Cell cell = pattern.Cells[rowBase + channel];
                if (cell.Effect == TrackerEffectCommand.SetSpeed && cell.EffectParam > 0)
                    currentSpeed = cell.EffectParam;
                else if (cell.Effect == TrackerEffectCommand.SetTempo && cell.EffectParam > 0)
                    currentTempo = cell.EffectParam;
                else if (cell.Effect == TrackerEffectCommand.JumpToOrder && cell.EffectParam <= orderIndex) {
                    pendingJump = true;
                    pendingJumpTarget = cell.EffectParam;
                }
            }
        }

        void ApplyRow(Pattern pattern) {
            currentRowSpr = currentSpeed * (double)sampleRate * TickSecondsScale / currentTempo;
            sprPerTick = currentRowSpr / currentSpeed;

            if (!channelsSeeded) {
                for (int channel = 0; channel < channelCount; channel++)
                    synth.SetChannelPan(channel, TrackerPan.InitialSigned(song, channel));
                channelsSeeded = true;
            }

            engine.EnterRow(pattern, row, song);

            currentOrder = orderIndex;
            currentRow = row;
            rowApplied = true;
            EnterTick(0);
        }

        void AdvanceRow() {
            if (pendingJump) {
                orderIndex = pendingJumpTarget;
                row = 0;
                pendingJump = false;
                return;
            }
            row++;
        }

        void SilenceAll() {
            for (int channel = 0; channel < channelCount; channel++)
                synth.SilenceChannel(channel);
        }
    }
}
