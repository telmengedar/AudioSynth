using System;
using System.Collections.Generic;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Formats.Sf2;
using Pooshit.AudioSynth.Sequencing;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;
using Pooshit.AudioSynth.Tests.Helpers;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Unit tests for adaptive master gain-staging (DiVoid #7254/#7257): the load-time
    /// <see cref="Sf2SampleData.LoudnessEstimate"/> measurement, the <see cref="SoundBank.LoudnessEstimate"/>
    /// pass-through, the <see cref="MidiSequencer.DeriveCalibrationGain"/> attenuate-only derivation, and the
    /// <see cref="Synthesizer.SetMasterCalibrationGain"/> engine seam. All synthetic here — no real SF2 asset
    /// required; the dev-tree Florestan/Omega acceptance proofs live under <c>Midi/MasterGainCalibrationAcceptanceTests.cs</c>.
    /// </summary>
    [TestFixture]
    public class MasterGainCalibrationTests {

        const int SampleRate = 44100;

        static short[] ConstantShorts(short value, int count) {
            short[] result = new short[count];
            for (int i = 0; i < result.Length; i++)
                result[i] = value;
            return result;
        }

        [Test]
        [Description("LoudnessEstimate is a pure, deterministic function of the pool: computing it twice on " +
                     "the same Sf2SampleData yields the identical value (load-bearing for the Florestan " +
                     "byte-identity anchor, which depends on this being reproducible bit-for-bit).")]
        public void LoudnessEstimate_ComputedTwice_YieldsIdenticalValue() {
            Sf2SampleData data = new Sf2SampleData(ConstantShorts(10000, 5000));

            float first = data.LoudnessEstimate;
            float second = data.LoudnessEstimate;

            Assert.That(second, Is.EqualTo(first), "LoudnessEstimate must be deterministic across repeated reads.");
        }

        [Test]
        [Description("A pool that is entirely silence (every sample exactly 0) must not divide by zero or " +
                     "produce NaN/Inf/negative: the degenerate case falls back to 0f.")]
        public void LoudnessEstimate_AllSilentPool_FallsBackToZero() {
            Sf2SampleData data = new Sf2SampleData(ConstantShorts(0, 4096));

            float estimate = data.LoudnessEstimate;

            Assert.That(estimate, Is.EqualTo(0f), "An all-silence pool must yield the 0f sentinel, not NaN/Inf/negative.");
        }

        [Test]
        [Description("A pool that is mostly near-silence with a modest-level signal confined to a small portion " +
                     "must not be deflated toward ~0 by the silent majority: the silence gate excludes the quiet " +
                     "blocks so the estimate reflects the signal's own level.")]
        public void LoudnessEstimate_MostlySilenceWithModestSignal_ReflectsSignalNotSilence() {
            // 40 blocks of near-digital-silence (well below the -50 dBFS gate), then 4 blocks at a modest,
            // clearly-above-gate level (~0.2 full scale). Block size in the implementation is 2048 frames.
            const int blockSize = 2048;
            const short silentValue = 1;      // ~0.00003 full scale: far below the ~0.00316 silence gate
            const short signalValue = 6554;    // 6554/32768 ~= 0.2

            List<short> samples = new List<short>();
            for (int b = 0; b < 40; b++)
                samples.AddRange(ConstantShorts(silentValue, blockSize));
            for (int b = 0; b < 4; b++)
                samples.AddRange(ConstantShorts(signalValue, blockSize));

            Sf2SampleData data = new Sf2SampleData(samples.ToArray());

            float estimate = data.LoudnessEstimate;

            Assert.That(estimate, Is.EqualTo(0.2f).Within(0.01f),
                $"Expected the estimate to track the signal level (~0.2), not be dragged toward the silent " +
                $"majority; got {estimate}.");
        }

        [Test]
        [Description("A single very loud outlier block amid many moderate-level blocks must not dominate the " +
                     "median-of-block-RMS aggregate: the estimate tracks the bulk moderate level, not the spike.")]
        public void LoudnessEstimate_SingleLoudOutlierBlock_DoesNotDominateEstimate() {
            const int blockSize = 2048;
            const short moderateValue = 3277;  // ~0.1 full scale
            const short outlierValue = 32000;  // ~0.977 full scale: a single very loud one-shot block

            List<short> samples = new List<short>();
            for (int b = 0; b < 29; b++)
                samples.AddRange(ConstantShorts(moderateValue, blockSize));
            samples.AddRange(ConstantShorts(outlierValue, blockSize));

            Sf2SampleData data = new Sf2SampleData(samples.ToArray());

            float estimate = data.LoudnessEstimate;

            Assert.That(estimate, Is.EqualTo(0.1f).Within(0.01f),
                $"Expected the median-of-block-RMS estimate to track the bulk moderate level (~0.1) despite " +
                $"one very loud outlier block; got {estimate} (a mean would have been pulled toward the outlier).");
        }

        [Test]
        [Description("SoundBank built via the plain (entries-only) constructor defaults LoudnessEstimate to the " +
                     "0f sentinel; the overload carries an explicit value through unchanged.")]
        public void SoundBankLoudnessEstimate_DefaultsToZeroSentinel_OrCarriesExplicitValue() {
            StubPatch patch = new StubPatch("patch");

            SoundBank defaultBank = new SoundBank(new[] { (0, 0, (IPatch)patch) });
            SoundBank explicitBank = new SoundBank(new[] { (0, 0, (IPatch)patch) }, 0.42f);

            Assert.That(defaultBank.LoudnessEstimate, Is.EqualTo(0f),
                "A SoundBank built without the loudness argument must default to the 0f 'unmeasured' sentinel.");
            Assert.That(explicitBank.LoudnessEstimate, Is.EqualTo(0.42f),
                "A SoundBank built with an explicit loudness estimate must carry it through unchanged.");
        }

        [Test]
        [Description("A bank whose measured LoudnessEstimate is the 0f sentinel (unmeasured) resolves to the " +
                     "neutral gain 1f, never a boost, regardless of how low/degenerate the estimate is.")]
        public void DeriveCalibrationGain_ZeroSentinelEstimate_ResolvesToNeutralGain() {
            StubPatch patch = new StubPatch("patch");
            SoundBank bank = new SoundBank(new[] { (0, 0, (IPatch)patch) }, 0f);

            float gain = MidiSequencer.DeriveCalibrationGain(bank);

            Assert.That(gain, Is.EqualTo(1f), "A 0f (unmeasured) estimate must never imply a boost; it must resolve to 1f.");
        }

        [Test]
        [Description("Attenuate-only clamp (locked decision #2): a bank measured quieter than the reference " +
                     "must resolve to exactly 1f, never a gain greater than 1f (a boost).")]
        public void DeriveCalibrationGain_QuieterThanReference_NeverBoosts() {
            StubPatch patch = new StubPatch("quiet-patch");
            // An estimate far below any plausible ReferenceLoudness anchor.
            SoundBank quietBank = new SoundBank(new[] { (0, 0, (IPatch)patch) }, 0.0001f);

            float gain = MidiSequencer.DeriveCalibrationGain(quietBank);

            Assert.That(gain, Is.EqualTo(1f), "A font quieter than the reference must never be boosted above 1f.");
            Assert.That(gain, Is.LessThanOrEqualTo(1f), "DeriveCalibrationGain must never exceed 1f.");
        }

        static SynthesizerOptions Options(int channels) => new SynthesizerOptions(SampleRate, channels, 64, 16);

        static SampleRegion BuildDcRegion(float value, int length) {
            float[] buf = new float[length];
            for (int i = 0; i < length; i++)
                buf[i] = value;
            return new SampleRegion(buf, 0, length, 0, length, LoopMode.Continuous, SampleRate, 60, 0,
                EnvelopeParameters.Default, FilterParameters.Default, LfoParameters.Default, 0f);
        }

        [Test]
        [Description("SetMasterCalibrationGain(0.5f) halves a DC voice that stays below the soft-clip knee, " +
                     "proving the calibration multiply is applied at the head of ApplyMasterBus.")]
        public void SetMasterCalibrationGain_Half_AttenuatesBelowKneeVoiceByHalf() {
            SynthesizerOptions opts = Options(1);
            SampleRegion region = BuildDcRegion(0.6f, 1024);
            SamplePatch patch = new SamplePatch(region, opts.SampleRate);
            Synthesizer synth = new Synthesizer(opts, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.SetMasterCalibrationGain(0.5f);
            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, 500 + 500);
            float[] samples = sink.ToArray();

            float peak = 0f;
            for (int i = 500; i < samples.Length; i++)
                peak = Math.Max(peak, Math.Abs(samples[i]));

            Assert.That(peak, Is.EqualTo(0.3f).Within(1e-4f),
                $"A 0.6 DC voice below the knee, calibrated at 0.5f, must attenuate to 0.3; peak was {peak}.");
        }

        [Test]
        [Description("Never calling SetMasterCalibrationGain (default 1.0f, no-op) leaves the master bus " +
                     "bit-identical to before this feature: a quiet DC voice below the knee passes through unchanged.")]
        public void SetMasterCalibrationGain_NeverCalled_LeavesOutputUnchanged() {
            SynthesizerOptions opts = Options(1);
            SampleRegion region = BuildDcRegion(0.3f, 1024);
            SamplePatch patch = new SamplePatch(region, opts.SampleRate);
            Synthesizer synth = new Synthesizer(opts, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, 500 + 500);
            float[] samples = sink.ToArray();

            float peak = 0f;
            for (int i = 500; i < samples.Length; i++)
                peak = Math.Max(peak, Math.Abs(samples[i]));

            Assert.That(peak, Is.EqualTo(0.3f).Within(1e-4f),
                $"Default calibration gain (1.0, never set) must leave a quiet DC voice unchanged; peak was {peak}.");
        }

        [Test]
        [Description("INV-2 remains intact with a non-1.0 calibration gain active: NaN/Inf injected by a voice " +
                     "are still zeroed by Finalize, not turned into some other escaping value by the calibration multiply.")]
        public void SetMasterCalibrationGain_NonUnity_DoesNotBreakNanInfSafety() {
            SynthesizerOptions opts = new SynthesizerOptions(44100, 2, 64, 16);
            NanEmittingPatch patch = new NanEmittingPatch();
            Synthesizer synth = new Synthesizer(opts, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.SetMasterCalibrationGain(0.5f);
            synth.NoteOn(0, 60, 100);
            OfflineRenderer.Render(synth, sink, 512);
            float[] samples = sink.ToArray();

            foreach (float s in samples) {
                Assert.That(float.IsNaN(s), Is.False, "NaN reached the output with a non-1.0 calibration gain active.");
                Assert.That(float.IsInfinity(s), Is.False, "Inf reached the output with a non-1.0 calibration gain active.");
                Assert.That(Math.Abs(s), Is.LessThanOrEqualTo(1f), $"sample out of [-1,1] with calibration active: {s}");
            }
        }
    }
}
