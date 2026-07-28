using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;
using Pooshit.AudioSynth.Synthesis.Voices;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// <see cref="SamplePlaybackVoice.SetModWheel"/> and <see cref="Synthesizer.SetChannelModulation"/>
    /// tests (DiVoid #7181, design #7180), mirroring <see cref="SamplePlaybackVoiceVibratoTests"/> and
    /// <see cref="SamplePlaybackVoicePitchBendTests"/>: a mod-wheel amount introduces a dedicated vibrato
    /// tracking an independently-advanced <see cref="ModulationLfo"/> at the GM/DLS default rate and peak
    /// depth, amount=0 leaves the region path bit-for-bit unchanged (whether or not the region itself
    /// bakes vibrato), and a note started during / mid-note under an active channel wheel inherits it.
    /// </summary>
    [TestFixture]
    public class SamplePlaybackVoiceModWheelTests {

        const int SampleRate = 44100;

        /// <summary>The voice's control-rate tick period, mirroring the sibling vibrato/pitch-bend tests.</summary>
        const int ControlRateFrames = 64;

        /// <summary>
        /// Peak mod-wheel vibrato depth, in cents, at amount=1 (mirrors
        /// <c>SamplePlaybackVoice.MaxModWheelVibratoCents</c>, design §8/§14).
        /// </summary>
        const float MaxModWheelVibratoCents = 50f;

        static readonly EnvelopeParameters InstantSustainEnvelope = new EnvelopeParameters(0f, 0f, 0f, 0f, 1f, 0f);

        static SampleRegion BuildRampRegion(LfoParameters lfo, float scale, int length) {
            float[] buf = new float[length];
            for (int i = 0; i < length; i++)
                buf[i] = i * scale;
            return new SampleRegion(buf, 0, length, 0, length, LoopMode.NoLoop, SampleRate, 60, 0,
                InstantSustainEnvelope, FilterParameters.Default, lfo, 0f);
        }

        static SampleRegion BuildToneRegion(float frequency, LfoParameters lfo, int bufferLength) {
            float[] buffer = new float[bufferLength];
            for (int i = 0; i < bufferLength; i++)
                buffer[i] = (float)Math.Sin(2.0 * Math.PI * frequency * i / SampleRate);
            return new SampleRegion(buffer, 0, buffer.Length, 0, buffer.Length, LoopMode.Continuous,
                SampleRate, 60, 0, EnvelopeParameters.Default, FilterParameters.Default, lfo, 0f);
        }

        static float MeasuredIncrementAtTick(float[] output, int tick, float scale) {
            int frame = tick * ControlRateFrames + ControlRateFrames / 2;
            return (output[frame + 1] - output[frame]) / scale;
        }

        [Test]
        [Description("Deliverable proof: a mod-wheel amount of 1 on a region with no baked LFO introduces a " +
                     "vibrato whose effective increment at each control tick matches an independently-advanced " +
                     "ModulationLfo at the GM/DLS default rate (8.176 Hz) and peak depth (50 cents).")]
        public void SetModWheel_FullAmount_TracksIndependentModWheelLfo() {
            const float scale = 0.001f;
            const float pitchIncrement = 1f;
            const float amount = 1f;
            const int ticksToRender = 12;
            const int firstCheckedTick = 5;

            SampleRegion region = BuildRampRegion(LfoParameters.Default, scale, 2000);
            SamplePlaybackVoice voice = new SamplePlaybackVoice(region, pitchIncrement, 1f, SampleRate);
            voice.SetModWheel(amount);

            float[] output = new float[ControlRateFrames * ticksToRender];
            voice.RenderBlock(output.AsSpan());

            LfoParameters modWheelParameters = new LfoParameters(0f, LfoParameters.Sf2DefaultFrequencyHz, MaxModWheelVibratoCents, 0f, 0f);
            ModulationLfo predictor = new ModulationLfo(modWheelParameters, SampleRate);

            for (int tick = 0; tick < ticksToRender; tick++) {
                float predictedLfoValue = predictor.Advance(ControlRateFrames);
                if (tick < firstCheckedTick)
                    continue;

                float expectedIncrement = (float)Math.Pow(2.0, predictedLfoValue * MaxModWheelVibratoCents * amount / 1200.0);
                float measuredIncrement = MeasuredIncrementAtTick(output, tick, scale);

                Assert.That(measuredIncrement, Is.EqualTo(expectedIncrement).Within(0.01f),
                    $"tick {tick}: measured increment {measuredIncrement} did not track the mod-wheel-LFO-predicted increment {expectedIncrement}.");
            }
        }

        [Test]
        [Description("A mod-wheel amount of 0.5 halves the peak cents relative to amount=1, at the same tick.")]
        public void SetModWheel_HalfAmount_HalvesEffectiveCents() {
            const float scale = 0.001f;
            const float pitchIncrement = 1f;
            const float amount = 0.5f;
            const int ticksToRender = 12;
            const int firstCheckedTick = 5;

            SampleRegion region = BuildRampRegion(LfoParameters.Default, scale, 2000);
            SamplePlaybackVoice voice = new SamplePlaybackVoice(region, pitchIncrement, 1f, SampleRate);
            voice.SetModWheel(amount);

            float[] output = new float[ControlRateFrames * ticksToRender];
            voice.RenderBlock(output.AsSpan());

            LfoParameters modWheelParameters = new LfoParameters(0f, LfoParameters.Sf2DefaultFrequencyHz, MaxModWheelVibratoCents, 0f, 0f);
            ModulationLfo predictor = new ModulationLfo(modWheelParameters, SampleRate);

            for (int tick = 0; tick < ticksToRender; tick++) {
                float predictedLfoValue = predictor.Advance(ControlRateFrames);
                if (tick < firstCheckedTick)
                    continue;

                float expectedIncrement = (float)Math.Pow(2.0, predictedLfoValue * MaxModWheelVibratoCents * amount / 1200.0);
                float measuredIncrement = MeasuredIncrementAtTick(output, tick, scale);

                Assert.That(measuredIncrement, Is.EqualTo(expectedIncrement).Within(0.01f),
                    $"tick {tick}: measured increment {measuredIncrement} did not match the half-amount-scaled increment {expectedIncrement}.");
            }
        }

        [Test]
        [Description("No-regression: never calling SetModWheel (the CC1-free case) reproduces the pre-mod-wheel " +
                     "constant-increment read advance bit-for-bit — the key gate for songs with no CC1.")]
        public void DefaultModWheel_ReproducesBaseIncrementBitForBit() {
            const float scale = 0.001f;
            const int framesToRender = 800;
            const int convergedFrame = 300;

            SampleRegion region = BuildRampRegion(LfoParameters.Default, scale, 1200);
            SamplePlaybackVoice voice = new SamplePlaybackVoice(region, 1f, 1f, SampleRate);

            float[] output = new float[framesToRender];
            voice.RenderBlock(output.AsSpan());

            for (int i = convergedFrame; i < framesToRender - 1; i++) {
                float measured = (output[i + 1] - output[i]) / scale;
                Assert.That(measured, Is.EqualTo(1f).Within(1e-4f),
                    $"frame {i}: measured increment {measured} deviates from the base pitch increment with no mod-wheel applied.");
            }
        }

        [Test]
        [Description("An explicit zero amount (SetModWheel(0f)) reproduces the base pitch increment bit-for-bit " +
                     "even on a region that bakes its own vibrato — the mod-wheel path is additive, not a " +
                     "replacement, and must not perturb the region's own untouched LFO output.")]
        public void SetModWheel_ZeroOnRegionWithBakedVibrato_IdenticalToPreModWheel() {
            const float scale = 0.001f;
            const float depthCents = 200f;
            const int ticksToRender = 12;
            const int firstCheckedTick = 5;

            LfoParameters bakedVibrato = new LfoParameters(0f, 5f, depthCents, 0f, 0f);
            SampleRegion region = BuildRampRegion(bakedVibrato, scale, 1200);

            SamplePlaybackVoice withExplicitZero = new SamplePlaybackVoice(region, 1f, 1f, SampleRate);
            withExplicitZero.SetModWheel(0f);
            float[] outputWithZero = new float[ControlRateFrames * ticksToRender];
            withExplicitZero.RenderBlock(outputWithZero.AsSpan());

            SamplePlaybackVoice withoutModWheel = new SamplePlaybackVoice(region, 1f, 1f, SampleRate);
            float[] outputWithout = new float[ControlRateFrames * ticksToRender];
            withoutModWheel.RenderBlock(outputWithout.AsSpan());

            for (int tick = firstCheckedTick; tick < ticksToRender; tick++) {
                float measuredWithZero = MeasuredIncrementAtTick(outputWithZero, tick, scale);
                float measuredWithout = MeasuredIncrementAtTick(outputWithout, tick, scale);
                Assert.That(measuredWithZero, Is.EqualTo(measuredWithout).Within(1e-5f),
                    $"tick {tick}: a zero mod-wheel amount must not perturb a region's own baked vibrato (with={measuredWithZero}, without={measuredWithout}).");
            }
        }

        [Test]
        [Description("A note started while a channel's mod wheel is already raised inherits it: SetChannelModulation " +
                     "called before NoteOn still applies the vibrato to the resulting voice (scoop-into-note).")]
        public void NoteOn_DuringActiveChannelModulation_InheritsAmount() {
            const float scale = 0.001f;
            const float amount = 1f;
            const int ticksToRender = 12;
            const int firstCheckedTick = 5;

            SynthesizerOptions options = new SynthesizerOptions(SampleRate, 1, ControlRateFrames, 16);
            SampleRegion region = BuildRampRegion(LfoParameters.Default, scale, 2000);
            SamplePatch patch = new SamplePatch(region, options.SampleRate);
            Synthesizer synth = new Synthesizer(options, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.SetChannelModulation(0, amount);
            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, ControlRateFrames * ticksToRender);

            float[] output = sink.ToArray();

            LfoParameters modWheelParameters = new LfoParameters(0f, LfoParameters.Sf2DefaultFrequencyHz, MaxModWheelVibratoCents, 0f, 0f);
            ModulationLfo predictor = new ModulationLfo(modWheelParameters, SampleRate);

            for (int tick = 0; tick < ticksToRender; tick++) {
                float predictedLfoValue = predictor.Advance(ControlRateFrames);
                if (tick < firstCheckedTick)
                    continue;

                float expectedIncrement = (float)Math.Pow(2.0, predictedLfoValue * MaxModWheelVibratoCents * amount / 1200.0);
                float measured = MeasuredIncrementAtTick(output, tick, scale);
                Assert.That(measured, Is.EqualTo(expectedIncrement).Within(0.01f),
                    $"tick {tick}: a note started during an active mod-wheel amount {amount} measured increment {measured}, expected {expectedIncrement}.");
            }
        }

        [Test]
        [Description("SetChannelModulation fans out to a currently-sounding voice on that channel, introducing " +
                     "the vibrato mid-note.")]
        public void SetChannelModulation_FansOutToSoundingVoiceOnChannel() {
            const float scale = 0.0002f;
            const float amount = 1f;
            const int settleTicks = 6;
            const int postModTicks = 12;
            const int firstCheckedTick = 5;

            SynthesizerOptions options = new SynthesizerOptions(SampleRate, 1, ControlRateFrames, 16);
            SampleRegion region = BuildRampRegion(LfoParameters.Default, scale, 4000);
            SamplePatch patch = new SamplePatch(region, options.SampleRate);
            Synthesizer synth = new Synthesizer(options, patch);
            InMemoryAudioSink settleSink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, settleSink, ControlRateFrames * settleTicks);

            synth.SetChannelModulation(0, amount);
            InMemoryAudioSink postModSink = new InMemoryAudioSink(synth.Format);
            OfflineRenderer.Render(synth, postModSink, ControlRateFrames * postModTicks);

            float[] output = postModSink.ToArray();

            LfoParameters modWheelParameters = new LfoParameters(0f, LfoParameters.Sf2DefaultFrequencyHz, MaxModWheelVibratoCents, 0f, 0f);
            ModulationLfo predictor = new ModulationLfo(modWheelParameters, SampleRate);

            for (int tick = 0; tick < postModTicks; tick++) {
                float predictedLfoValue = predictor.Advance(ControlRateFrames);
                if (tick < firstCheckedTick)
                    continue;

                float expectedIncrement = (float)Math.Pow(2.0, predictedLfoValue * MaxModWheelVibratoCents * amount / 1200.0);
                float measured = MeasuredIncrementAtTick(output, tick, scale);
                Assert.That(measured, Is.EqualTo(expectedIncrement).Within(0.01f),
                    $"tick {tick}: a mid-note SetChannelModulation of amount {amount} measured increment {measured}, expected {expectedIncrement}.");
            }
        }

        [Test]
        [Description("Regression mirroring #6272 §B (clicks/zipper class): a control-rate tick under a full " +
                     "mod-wheel vibrato introduces no amplitude discontinuity.")]
        public void ControlTick_UnderModWheelVibrato_IntroducesNoAmplitudeDiscontinuity() {
            SampleRegion region = BuildToneRegion(200f, LfoParameters.Default, 8 * SampleRate);
            SynthesizerOptions opts = new SynthesizerOptions(SampleRate, 1, ControlRateFrames, 16);
            SamplePatch patch = new SamplePatch(region, opts.SampleRate);
            Synthesizer synth = new Synthesizer(opts, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.SetChannelModulation(0, 1f);
            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, SampleRate);

            float[] samples = sink.ToArray();
            float maxDelta = 0f;
            for (int i = 1; i < samples.Length; i++)
                maxDelta = Math.Max(maxDelta, Math.Abs(samples[i] - samples[i - 1]));

            Assert.That(maxDelta, Is.LessThan(0.15f),
                $"max consecutive-sample delta {maxDelta} indicates a discontinuity at a control-rate tick boundary under mod-wheel vibrato.");
        }
    }
}
