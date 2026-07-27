using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Unit tests for <see cref="ModulationLfo"/>: the delayed onset, the bipolar periodic triangle
    /// signal, the inert-when-zero-depth bypass, and the bounded-phase regression basis for defect
    /// catalog #6272 §B (#6214, unbounded phase accumulation).
    /// </summary>
    [TestFixture]
    public class ModulationLfoTests {

        const int SampleRate = 44100;

        [Test]
        [Description("An inert LFO (zero pitch depth) always yields a constant zero value.")]
        public void Inert_AlwaysYieldsZero() {
            ModulationLfo lfo = new ModulationLfo(LfoParameters.Default, SampleRate);

            for (int i = 0; i < 10; i++)
                Assert.That(lfo.Advance(64), Is.EqualTo(0f));
        }

        [Test]
        [Description("The LFO value stays zero for every frame spent inside the delay.")]
        public void Delay_HoldsZeroBeforeOscillationBegins() {
            LfoParameters parameters = new LfoParameters(0.01f, 5f, 100f, 0f, 0f);
            ModulationLfo lfo = new ModulationLfo(parameters, SampleRate);

            int delayFrames = (int)(0.01f * SampleRate);
            float value = lfo.Advance(delayFrames - 1);

            Assert.That(value, Is.EqualTo(0f), "value must stay at zero while still inside the delay.");
        }

        [Test]
        [Description("Once the delay elapses the LFO produces a non-zero, bounded bipolar value.")]
        public void AfterDelay_ProducesBoundedBipolarValue() {
            LfoParameters parameters = new LfoParameters(0f, 5f, 100f, 0f, 0f);
            ModulationLfo lfo = new ModulationLfo(parameters, SampleRate);

            float value = lfo.Advance(SampleRate / 20);

            Assert.That(value, Is.GreaterThanOrEqualTo(-1f).And.LessThanOrEqualTo(1f));
            Assert.That(value, Is.Not.EqualTo(0f));
        }

        [Test]
        [Description("The waveform starts at zero and rises, giving a click-free vibrato onset.")]
        public void Oscillation_StartsAtZeroRising() {
            LfoParameters parameters = new LfoParameters(0f, 5f, 100f, 0f, 0f);
            ModulationLfo lfo = new ModulationLfo(parameters, SampleRate);

            float early = lfo.Advance(1);
            float later = lfo.Advance(10);

            Assert.That(early, Is.EqualTo(0f).Within(0.01f), "first advanced frame must start near zero.");
            Assert.That(later, Is.GreaterThan(early), "waveform must rise immediately after zero.");
        }

        [Test]
        [Description("The signal is periodic at the configured frequency: one full period returns to the same value.")]
        public void Oscillation_IsPeriodicAtConfiguredFrequency() {
            const float frequencyHz = 5f;
            LfoParameters parameters = new LfoParameters(0f, frequencyHz, 100f, 0f, 0f);
            ModulationLfo lfo = new ModulationLfo(parameters, SampleRate);

            int periodFrames = (int)(SampleRate / frequencyHz);
            float beforePeriod = lfo.Advance(1);
            float afterOnePeriod = lfo.Advance(periodFrames - 1);

            Assert.That(afterOnePeriod, Is.EqualTo(beforePeriod).Within(0.02f),
                "value one full period later must match the value one frame in (periodicity).");
        }

        [Test]
        [Description("Reaches the +1 peak a quarter period after onset and the -1 trough three-quarters in.")]
        public void Oscillation_ReachesPeakAndTrough() {
            const float frequencyHz = 10f;
            LfoParameters parameters = new LfoParameters(0f, frequencyHz, 100f, 0f, 0f);
            ModulationLfo lfo = new ModulationLfo(parameters, SampleRate);

            int periodFrames = (int)(SampleRate / frequencyHz);
            float peak = lfo.Advance(periodFrames / 4);
            float trough = lfo.Advance(periodFrames / 2);

            Assert.That(peak, Is.EqualTo(1f).Within(0.02f), "quarter period in, the triangle must peak near +1.");
            Assert.That(trough, Is.EqualTo(-1f).Within(0.02f), "three-quarters period in, the triangle must trough near -1.");
        }

        [Test]
        [Description("Regression for defect catalog #6272 §B (#6214): phase stays bounded over a long note, keeping the vibrato period stable with no drift.")]
        public void LongRunningNote_PhaseStaysBounded_PeriodRemainsStable() {
            const float frequencyHz = 5f;
            LfoParameters parameters = new LfoParameters(0f, frequencyHz, 100f, 0f, 0f);
            ModulationLfo lfo = new ModulationLfo(parameters, SampleRate);

            int periodFrames = (int)(SampleRate / frequencyHz);
            long totalFrames = (long)SampleRate * 30;
            long framesAdvanced = 0;
            float valueAtStartOfLastPeriod = 0f;

            while (framesAdvanced + periodFrames <= totalFrames) {
                valueAtStartOfLastPeriod = lfo.Advance(1);
                lfo.Advance(periodFrames - 1);
                framesAdvanced += periodFrames;
            }

            Assert.That(valueAtStartOfLastPeriod, Is.EqualTo(0f).Within(0.02f),
                "after 30s of continuous advance the phase must still land near zero at every period boundary; " +
                "a drifted phase indicates unbounded accumulation (#6214).");
        }

        [Test]
        [Description("A non-positive sample rate is rejected.")]
        public void Constructor_NonPositiveSampleRate_Throws() {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ModulationLfo(LfoParameters.Default, 0));
        }

        [Test]
        [Description("Load-bearing correctness fix (design lfo-tremolo-sweep §5.2): a tremolo-only preset " +
            "(zero pitch depth, nonzero volume depth) must not be treated as inert; the bypass condition " +
            "widens to all-three-depths-zero, so this LFO still produces nonzero values.")]
        public void VolumeOnlyDepth_IsNotBypassed_ProducesNonzeroValues() {
            LfoParameters parameters = new LfoParameters(0f, 5f, 0f, 200f, 0f);
            ModulationLfo lfo = new ModulationLfo(parameters, SampleRate);

            float value = lfo.Advance(SampleRate / 20);

            Assert.That(value, Is.Not.EqualTo(0f),
                "a volume-only depth LFO must not be bypassed; pitch-only bypass would silently mute tremolo.");
        }

        [Test]
        [Description("A filter-only preset (zero pitch and volume depth, nonzero filter depth) must also not be bypassed.")]
        public void FilterOnlyDepth_IsNotBypassed_ProducesNonzeroValues() {
            LfoParameters parameters = new LfoParameters(0f, 5f, 0f, 0f, 1200f);
            ModulationLfo lfo = new ModulationLfo(parameters, SampleRate);

            float value = lfo.Advance(SampleRate / 20);

            Assert.That(value, Is.Not.EqualTo(0f),
                "a filter-only depth LFO must not be bypassed; pitch-only bypass would silently mute the sweep.");
        }

        [Test]
        [Description("All three depths zero is the only inert case; LfoParameters.Default must still bypass.")]
        public void AllDepthsZero_DefaultParameters_IsBypassed() {
            ModulationLfo lfo = new ModulationLfo(LfoParameters.Default, SampleRate);

            Assert.That(lfo.Advance(SampleRate / 20), Is.EqualTo(0f));
        }
    }
}
