using System;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Stereo modulated-delay chorus: the interleaved stereo send is averaged to a mono feed and pushed
    /// into a single ctor-allocated circular delay buffer; <see cref="ChorusSettings.VoiceCount"/> chorus
    /// voices each read that buffer at a fractional, LFO-modulated delay
    /// <c>baseDelay + depth·sin(2π·phase)</c> (linearly interpolated between adjacent samples), voice
    /// <c>k</c> running at LFO phase <c>k/VoiceCount</c>; the left channel reads at the voice's own phase
    /// and the right channel at that phase plus a quarter turn (90°), decorrelating L/R for stereo width
    /// from the one mono buffer. Voice reads are summed, normalized by voice count, scaled by
    /// <see cref="ChorusSettings.Wet"/> and added to <see cref="Process"/>'s <c>master</c> — never the dry
    /// signal already carried there. There is no feedback path, so the effect is a bounded sum of bounded
    /// delay-line reads and is BIBO-stable by construction, independent of the master soft-clip or
    /// <see cref="Synthesizer"/>'s NaN/Inf guard (INV-2). <see cref="ChorusSettings.Wet"/> = 0 means
    /// nothing is added, a structural dry passthrough. The delay buffer is sized from the sample rate and
    /// <see cref="ChorusSettings.BaseDelayMs"/>/<see cref="ChorusSettings.DepthMs"/> at construction, so
    /// <see cref="Process"/> is allocation-free and safe to call from <see cref="Synthesizer.Read"/>'s
    /// steady state. <see cref="Process"/> is a send-return: <c>send</c> may alias <c>master</c> (the
    /// master-insert special case, mirroring <see cref="Reverb"/>) — each
    /// frame's send values are read into locals, and the buffer read for that frame happens before the
    /// buffer write, so the aliased case never observes a partially-updated frame.
    /// </summary>
    public sealed class Chorus : IAudioEffect {

        /// <summary>Gain applied to <c>(L + R)</c> to average the stereo send down to a mono feed.</summary>
        const float InputGain = 0.5f;

        /// <summary>Quarter turn (in LFO cycles) separating the right-channel read phase from the left's.</summary>
        const double RightPhaseOffsetCycles = 0.25;

        /// <summary>Extra samples appended to the delay buffer beyond the maximum possible delay, guarding the interpolation lookahead.</summary>
        const int InterpolationGuardSamples = 4;

        const double MillisecondsPerSecond = 1000.0;

        readonly float[] buffer;
        readonly int bufferLength;
        readonly float baseDelaySamples;
        readonly float depthSamples;
        readonly double phaseIncrementPerFrame;
        readonly float voiceGain;
        readonly float wet;
        readonly int voiceCount;

        int writeIndex;
        double basePhase;

        /// <summary>
        /// Creates a <see cref="Chorus"/> for <paramref name="sampleRate"/>, allocating the circular delay
        /// buffer up front so <see cref="Process"/> allocates nothing.
        /// </summary>
        /// <param name="settings">rate, depth, base delay, wet and voice count; already stability-clamped</param>
        /// <param name="sampleRate">output sample rate in frames per second; must be positive</param>
        public Chorus(ChorusSettings settings, int sampleRate) {
            if (settings is null)
                throw new ArgumentNullException(nameof(settings));
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive.");

            baseDelaySamples = (float)(settings.BaseDelayMs / MillisecondsPerSecond * sampleRate);
            depthSamples = (float)(settings.DepthMs / MillisecondsPerSecond * sampleRate);
            phaseIncrementPerFrame = settings.RateHz / sampleRate;
            voiceCount = settings.VoiceCount;
            voiceGain = 1f / voiceCount;
            wet = settings.Wet;

            int maxDelaySamples = (int)Math.Ceiling(baseDelaySamples + depthSamples);
            bufferLength = maxDelaySamples + InterpolationGuardSamples;
            buffer = new float[bufferLength];
            writeIndex = 0;
            basePhase = 0.0;
        }

        /// <summary>
        /// Computes wet from <paramref name="send"/> and adds it to <paramref name="master"/> in place;
        /// a no-op when <see cref="ChorusSettings.Wet"/> is 0.
        /// </summary>
        /// <param name="send">interleaved stereo send samples that feed the chorus; length must equal <paramref name="master"/>'s and be a multiple of 2</param>
        /// <param name="master">interleaved stereo master samples that the wet signal is added into</param>
        public void Process(ReadOnlySpan<float> send, Span<float> master) {
            if (master.Length % 2 != 0)
                throw new ArgumentException($"master length ({master.Length}) must be a multiple of 2 (interleaved stereo).", nameof(master));
            if (send.Length != master.Length)
                throw new ArgumentException($"send length ({send.Length}) must equal master length ({master.Length}).", nameof(send));

            if (wet == 0f)
                return;

            for (int i = 0; i < master.Length; i += 2) {
                float sendL = send[i];
                float sendR = send[i + 1];
                float mono = (sendL + sendR) * InputGain;

                float wetL = 0f;
                float wetR = 0f;
                for (int v = 0; v < voiceCount; v++) {
                    double voicePhase = basePhase + (double)v / voiceCount;
                    double leftCycles = voicePhase - Math.Floor(voicePhase);
                    double rightCycles = voicePhase + RightPhaseOffsetCycles;
                    rightCycles -= Math.Floor(rightCycles);

                    float lfoLeft = (float)Math.Sin(leftCycles * 2.0 * Math.PI);
                    float lfoRight = (float)Math.Sin(rightCycles * 2.0 * Math.PI);

                    float delayLeft = baseDelaySamples + depthSamples * lfoLeft;
                    float delayRight = baseDelaySamples + depthSamples * lfoRight;

                    wetL += ReadInterpolated(delayLeft);
                    wetR += ReadInterpolated(delayRight);
                }

                master[i] += wetL * voiceGain * wet;
                master[i + 1] += wetR * voiceGain * wet;

                buffer[writeIndex] = mono;
                writeIndex++;
                if (writeIndex >= bufferLength)
                    writeIndex = 0;

                basePhase += phaseIncrementPerFrame;
                basePhase -= Math.Floor(basePhase);
            }
        }

        float ReadInterpolated(float delaySamples) {
            double readPos = writeIndex - (double)delaySamples;
            double floorPos = Math.Floor(readPos);
            float frac = (float)(readPos - floorPos);

            int index0 = ((int)floorPos % bufferLength + bufferLength) % bufferLength;
            int index1 = index0 + 1;
            if (index1 >= bufferLength)
                index1 = 0;

            float sample0 = buffer[index0];
            float sample1 = buffer[index1];
            return sample0 + frac * (sample1 - sample0);
        }
    }
}
