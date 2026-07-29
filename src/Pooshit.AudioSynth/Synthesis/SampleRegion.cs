using System;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Immutable descriptor of a playable mono sample region: buffer reference, loop boundaries,
    /// source sample rate, root key, and pitch correction; runtime playback state lives in the voice.
    /// </summary>
    public sealed class SampleRegion {

        /// <summary>
        /// Creates a <see cref="SampleRegion"/>.
        /// </summary>
        /// <param name="buffer">mono float PCM samples shared across all voices that play this region</param>
        /// <param name="start">inclusive start index into <paramref name="buffer"/></param>
        /// <param name="end">exclusive end index into <paramref name="buffer"/></param>
        /// <param name="loopStart">inclusive loop-start index; ignored when <see cref="LoopMode"/> is <see cref="LoopMode.NoLoop"/></param>
        /// <param name="loopEnd">exclusive loop-end index; ignored when <see cref="LoopMode"/> is <see cref="LoopMode.NoLoop"/></param>
        /// <param name="loopMode">loop behaviour for this region</param>
        /// <param name="sourceSampleRate">sample rate of the source recording in frames per second</param>
        /// <param name="rootKey">MIDI key number at which the sample plays at its original pitch (0–127)</param>
        /// <param name="pitchCorrectionCents">fine-tuning offset in cents applied on top of the key transposition</param>
        /// <param name="envelope">volume-envelope parameters shaping this region's amplitude over the note's life</param>
        /// <param name="filter">low-pass filter parameters shaping this region's timbre before the amplifier</param>
        /// <param name="lfo">modulation-LFO parameters driving this region's vibrato over the note's life</param>
        /// <param name="pan">static per-voice stereo position in [-1,1] (-1 = full left, 0 = centre, +1 = full right), sourced from SF2 generator 17</param>
        /// <param name="reverbSend">
        /// static per-voice reverb-send weight in [0,1], sourced from SF2 generator 16 (reverbEffectsSend);
        /// defaults to <c>1.0</c> (neutral pass-through — a region without an explicit gen-16 lets the
        /// channel's <see cref="ISynthesizer.SetChannelReverbSend"/> weight drive fully) rather than SF2's
        /// literal gen-16 default of 0, so CC91 is never impotent for regions that omit the generator.
        /// </param>
        /// <param name="chorusSend">
        /// static per-voice chorus-send weight in [0,1], sourced from SF2 generator 15 (chorusEffectsSend);
        /// defaults to <c>0f</c>, matching both the SF2 spec's literal gen-15 default and the additive
        /// combination's neutral element, so a region without an explicit gen-15 contributes no bias and
        /// the channel's <see cref="ISynthesizer.SetChannelChorusSend"/> weight alone still drives the
        /// voice (no impotence special-case, unlike <paramref name="reverbSend"/>'s inherited 1f).
        /// </param>
        /// <param name="exclusiveClass">
        /// SF2 generator 57 (exclusiveClass) value; defaults to <c>0</c>, meaning the region belongs to
        /// no choke group. A non-zero value names a choke group: starting a voice for this region silences
        /// every other sounding voice sharing the same class on the same channel (SF2 spec; e.g. GM
        /// hi-hats), a click-free choke the engine implements by reusing <see cref="IVoice.FastFadeForSteal"/>.
        /// </param>
        public SampleRegion(
            float[] buffer,
            int start,
            int end,
            int loopStart,
            int loopEnd,
            LoopMode loopMode,
            int sourceSampleRate,
            int rootKey,
            int pitchCorrectionCents,
            EnvelopeParameters envelope,
            FilterParameters filter,
            LfoParameters lfo,
            float pan,
            float reverbSend = 1f,
            float chorusSend = 0f,
            int exclusiveClass = 0) {
            if (buffer is null)
                throw new ArgumentNullException(nameof(buffer));
            if (start < 0 || start >= buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(start));
            if (end <= start || end > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(end));
            if (loopMode == LoopMode.Continuous) {
                if (loopStart < start || loopStart >= end)
                    throw new ArgumentOutOfRangeException(nameof(loopStart));
                if (loopEnd <= loopStart || loopEnd > end)
                    throw new ArgumentOutOfRangeException(nameof(loopEnd));
            }
            if (sourceSampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sourceSampleRate));
            if (rootKey < 0 || rootKey > 127)
                throw new ArgumentOutOfRangeException(nameof(rootKey));
            Buffer = buffer;
            Start = start;
            End = end;
            LoopStart = loopStart;
            LoopEnd = loopEnd;
            LoopMode = loopMode;
            SourceSampleRate = sourceSampleRate;
            RootKey = rootKey;
            PitchCorrectionCents = pitchCorrectionCents;
            Envelope = envelope;
            Filter = filter;
            Lfo = lfo;
            Pan = pan;
            ReverbSend = reverbSend;
            ChorusSend = chorusSend;
            ExclusiveClass = exclusiveClass;
        }

        /// <summary>
        /// Mono PCM sample data shared across all voices that play this region.
        /// </summary>
        public float[] Buffer { get; }

        /// <summary>
        /// Inclusive start index into <see cref="Buffer"/>.
        /// </summary>
        public int Start { get; }

        /// <summary>
        /// Exclusive end index into <see cref="Buffer"/>.
        /// </summary>
        public int End { get; }

        /// <summary>
        /// Inclusive loop-start index; relevant only when <see cref="LoopMode"/> is <see cref="LoopMode.Continuous"/>.
        /// </summary>
        public int LoopStart { get; }

        /// <summary>
        /// Exclusive loop-end index; relevant only when <see cref="LoopMode"/> is <see cref="LoopMode.Continuous"/>.
        /// </summary>
        public int LoopEnd { get; }

        /// <summary>
        /// Loop behaviour: no-loop one-shot or continuous looping between loop points.
        /// </summary>
        public LoopMode LoopMode { get; }

        /// <summary>
        /// Sample rate of the source recording in frames per second.
        /// </summary>
        public int SourceSampleRate { get; }

        /// <summary>
        /// MIDI key number at which the sample plays at its original pitch (0–127).
        /// </summary>
        public int RootKey { get; }

        /// <summary>
        /// Fine-tuning offset in cents added to the key transposition.
        /// </summary>
        public int PitchCorrectionCents { get; }

        /// <summary>
        /// Volume-envelope parameters shaping this region's amplitude over the note's life.
        /// </summary>
        public EnvelopeParameters Envelope { get; }

        /// <summary>
        /// Low-pass filter parameters shaping this region's timbre before the amplifier stage.
        /// </summary>
        public FilterParameters Filter { get; }

        /// <summary>
        /// Modulation-LFO parameters driving this region's vibrato (pitch modulation) over the note's life.
        /// </summary>
        public LfoParameters Lfo { get; }

        /// <summary>
        /// Static per-voice stereo position in [-1,1] (-1 = full left, 0 = centre, +1 = full right),
        /// sourced from SF2 generator 17 and combined with the channel's dynamic pan at mix time.
        /// </summary>
        public float Pan { get; }

        /// <summary>
        /// Static per-voice reverb-send weight in [0,1], sourced from SF2 generator 16 and combined
        /// additively with the channel's dynamic reverb-send weight at mix time (clamped to [0,1]).
        /// </summary>
        public float ReverbSend { get; }

        /// <summary>
        /// Static per-voice chorus-send weight in [0,1], sourced from SF2 generator 15 and combined
        /// additively with the channel's dynamic chorus-send weight at mix time.
        /// </summary>
        public float ChorusSend { get; }

        /// <summary>
        /// SF2 generator 57 (exclusiveClass) value; <c>0</c> means the region belongs to no choke group.
        /// A non-zero value names a choke group matched, at note-onset, within the same MIDI channel.
        /// </summary>
        public int ExclusiveClass { get; }
    }
}
