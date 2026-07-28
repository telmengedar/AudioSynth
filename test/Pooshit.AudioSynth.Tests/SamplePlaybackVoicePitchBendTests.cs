using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;
using Pooshit.AudioSynth.Synthesis.Voices;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// <see cref="SamplePlaybackVoice.SetPitchBend"/> and <see cref="Synthesizer.SetChannelPitchBend"/>
    /// tests (DiVoid #7140), mirroring <see cref="SamplePlaybackVoiceVibratoTests"/>: a bend shifts the
    /// measured read-increment by the expected ratio, a centered bend leaves it bit-for-bit unchanged,
    /// and a note started during an active channel bend inherits it (scoop-into-note).
    /// </summary>
    [TestFixture]
    public class SamplePlaybackVoicePitchBendTests {

        const int SampleRate = 44100;

        /// <summary>The voice's control-rate tick period, mirroring <see cref="SamplePlaybackVoiceVibratoTests"/>.</summary>
        const int ControlRateFrames = 64;

        /// <summary>
        /// Mirrors <c>Synthesizer.MasterHeadroomTrim</c> (DiVoid BUG #7212, design #7213): tests that render
        /// through the full <see cref="Synthesizer"/> path divide the measured increment by this factor to
        /// compensate for the master-bus headroom attenuation before comparing against the untrimmed ratio.
        /// </summary>
        const float MasterHeadroomTrim = 0.5f;

        static readonly EnvelopeParameters InstantSustainEnvelope = new EnvelopeParameters(0f, 0f, 0f, 0f, 1f, 0f);

        static SampleRegion BuildRampRegion(float scale, int length) {
            float[] buf = new float[length];
            for (int i = 0; i < length; i++)
                buf[i] = i * scale;
            return new SampleRegion(buf, 0, length, 0, length, LoopMode.NoLoop, SampleRate, 60, 0,
                InstantSustainEnvelope, FilterParameters.Default, LfoParameters.Default, 0f);
        }

        static float MeasuredIncrementAtTick(float[] output, int tick, float scale) {
            int frame = tick * ControlRateFrames + ControlRateFrames / 2;
            return (output[frame + 1] - output[frame]) / scale;
        }

        [Test]
        [Description("A voice bent by +7 semitones via SetPitchBend shifts its measured read-increment by 2^(7/12), at every steady-state control tick.")]
        public void SetPitchBend_ShiftsMeasuredIncrementByExpectedRatio() {
            const float scale = 0.001f;
            const float pitchIncrement = 1f;
            const float semitones = 7f;
            const int ticksToRender = 12;
            const int firstCheckedTick = 5;
            float expectedFactor = (float)Math.Pow(2.0, semitones / 12.0);

            SampleRegion region = BuildRampRegion(scale, 2000);
            SamplePlaybackVoice voice = new SamplePlaybackVoice(region, pitchIncrement, 1f, SampleRate);
            voice.SetPitchBend(expectedFactor);

            float[] output = new float[ControlRateFrames * ticksToRender];
            voice.RenderBlock(output.AsSpan());

            for (int tick = firstCheckedTick; tick < ticksToRender; tick++) {
                float measured = MeasuredIncrementAtTick(output, tick, scale);
                Assert.That(measured, Is.EqualTo(expectedFactor).Within(0.01f),
                    $"tick {tick}: measured increment {measured} did not match the expected bend factor {expectedFactor} for +{semitones} semitones.");
            }
        }

        [Test]
        [Description("An explicit centered bend (SetPitchBend(1.0)) reproduces the base pitch increment bit-for-bit — no shift.")]
        public void SetPitchBend_Centered_ProducesNoShift() {
            const float scale = 0.001f;
            const int ticksToRender = 12;
            const int firstCheckedTick = 5;

            SampleRegion region = BuildRampRegion(scale, 2000);
            SamplePlaybackVoice voice = new SamplePlaybackVoice(region, 1f, 1f, SampleRate);
            voice.SetPitchBend(1f);

            float[] output = new float[ControlRateFrames * ticksToRender];
            voice.RenderBlock(output.AsSpan());

            for (int tick = firstCheckedTick; tick < ticksToRender; tick++) {
                float measured = MeasuredIncrementAtTick(output, tick, scale);
                Assert.That(measured, Is.EqualTo(1f).Within(1e-4f),
                    $"tick {tick}: measured increment {measured} deviated from the base pitch increment under a centered bend.");
            }
        }

        [Test]
        [Description("No-regression: never calling SetPitchBend (the PitchWheel-free case) reproduces the pre-bend constant-increment read advance bit-for-bit — the key gate for centered/no-bend songs.")]
        public void DefaultBend_ReproducesBaseIncrementBitForBit() {
            const float scale = 0.001f;
            const int framesToRender = 800;
            const int convergedFrame = 300;

            SampleRegion region = BuildRampRegion(scale, 1200);
            SamplePlaybackVoice voice = new SamplePlaybackVoice(region, 1f, 1f, SampleRate);

            float[] output = new float[framesToRender];
            voice.RenderBlock(output.AsSpan());

            for (int i = convergedFrame; i < framesToRender - 1; i++) {
                float measured = (output[i + 1] - output[i]) / scale;
                Assert.That(measured, Is.EqualTo(1f).Within(1e-4f),
                    $"frame {i}: measured increment {measured} deviates from the base pitch increment with no bend applied.");
            }
        }

        [Test]
        [Description("A note started while a channel's bend is active inherits it: SetChannelPitchBend called before NoteOn still bends the resulting voice (scoop-into-note).")]
        public void NoteOn_DuringActiveChannelBend_InheritsBend() {
            const float scale = 0.001f;
            const float semitones = -5f;
            const int ticksToRender = 12;
            const int firstCheckedTick = 5;
            float expectedFactor = (float)Math.Pow(2.0, semitones / 12.0);

            SynthesizerOptions options = new SynthesizerOptions(SampleRate, 1, ControlRateFrames, 16);
            SampleRegion region = BuildRampRegion(scale, 2000);
            SamplePatch patch = new SamplePatch(region, options.SampleRate);
            Synthesizer synth = new Synthesizer(options, patch);
            InMemoryAudioSink sink = new InMemoryAudioSink(synth.Format);

            synth.SetChannelPitchBend(0, semitones);
            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, sink, ControlRateFrames * ticksToRender);

            float[] output = sink.ToArray();
            for (int tick = firstCheckedTick; tick < ticksToRender; tick++) {
                float measured = MeasuredIncrementAtTick(output, tick, scale) / MasterHeadroomTrim;
                Assert.That(measured, Is.EqualTo(expectedFactor).Within(0.01f),
                    $"tick {tick}: a note started during an active {semitones}-semitone bend measured increment {measured}, expected {expectedFactor}.");
            }
        }

        [Test]
        [Description("SetChannelPitchBend fans out to a currently-sounding voice on that channel, shifting its measured increment mid-note.")]
        public void SetChannelPitchBend_FansOutToSoundingVoiceOnChannel() {
            const float scale = 0.0002f;
            const float semitones = 3f;
            const int settleTicks = 6;
            const int postBendTicks = 12;
            const int firstCheckedTick = 5;
            float expectedFactor = (float)Math.Pow(2.0, semitones / 12.0);

            SynthesizerOptions options = new SynthesizerOptions(SampleRate, 1, ControlRateFrames, 16);
            SampleRegion region = BuildRampRegion(scale, 4000);
            SamplePatch patch = new SamplePatch(region, options.SampleRate);
            Synthesizer synth = new Synthesizer(options, patch);
            InMemoryAudioSink settleSink = new InMemoryAudioSink(synth.Format);

            synth.NoteOn(0, 60, 127);
            OfflineRenderer.Render(synth, settleSink, ControlRateFrames * settleTicks);

            synth.SetChannelPitchBend(0, semitones);
            InMemoryAudioSink postBendSink = new InMemoryAudioSink(synth.Format);
            OfflineRenderer.Render(synth, postBendSink, ControlRateFrames * postBendTicks);

            float[] output = postBendSink.ToArray();
            for (int tick = firstCheckedTick; tick < postBendTicks; tick++) {
                float measured = MeasuredIncrementAtTick(output, tick, scale) / MasterHeadroomTrim;
                Assert.That(measured, Is.EqualTo(expectedFactor).Within(0.01f),
                    $"tick {tick}: a mid-note SetChannelPitchBend of {semitones} semitones measured increment {measured}, expected {expectedFactor}.");
            }
        }
    }
}
