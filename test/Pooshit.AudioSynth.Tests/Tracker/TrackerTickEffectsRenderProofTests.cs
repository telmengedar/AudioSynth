using System;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Formats.Tracker;
using Pooshit.AudioSynth.Sequencing;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;

namespace Pooshit.AudioSynth.Tests.Tracker {

    /// <summary>
    /// Deliverable proof (design DiVoid #7511 §14 build step 8): a song exercising every v1 per-tick
    /// effect renders non-silent, bounded audio live through the real <see cref="Synthesizer"/>.
    /// </summary>
    [TestFixture, Parallelizable]
    public class TrackerTickEffectsRenderProofTests {

        const int SampleRate = 44100;

        static SampleRegion SineRegion(float freqHz, int length) {
            float[] buffer = new float[length];
            for (int i = 0; i < length; i++)
                buffer[i] = (float)Math.Sin(2 * Math.PI * freqHz * i / SampleRate) * 0.5f;
            return new SampleRegion(buffer, 0, length, 0, length, LoopMode.Continuous, SampleRate, 60, 0,
                EnvelopeParameters.Default, FilterParameters.Default, LfoParameters.Default, 0f);
        }

        static Song TickEffectSong(Cell[] grid) => new Song {
            Title = "tick-effects",
            DefaultBpm = 125,
            DefaultSpeed = 6,
            DefaultRows = grid.Length,
            ChannelCount = 1,
            Instruments = new[] { new Instrument { Bank = 0, Program = 0, Name = "sine" } },
            Patterns = new[] { new Pattern { Rows = grid.Length, Cells = grid } },
            Order = new[] { 0 }
        };

        [Test, Parallelizable]
        [Description("A song exercising VolumeSlide, PortamentoUp/Down, TonePortamento, Arpeggio, Vibrato, " +
                     "Retrigger, NoteCut and NoteDelay in one pattern renders non-silent, bounded audio through " +
                     "the real Synthesizer -> TrackerSequencer pipeline, with no Synthesizer/DSP changes involved.")]
        public void TickEffectSong_ThroughRealSynthesizer_RendersNonSilentBoundedAudio() {
            SoundBank bank = new SoundBank(new[] { (0, 0, "", (IPatch)new SamplePatch(SineRegion(220f, 8192), SampleRate)) });

            Cell[] grid = new Cell[9];
            grid[0] = new Cell { Note = TrackerNotes.FromKey(60), Instrument = 1, Volume = 40, Effect = TrackerEffectCommand.VolumeSlide, EffectParam = 0x20 };
            grid[1] = new Cell { Note = TrackerNotes.FromKey(60), Instrument = 1, Effect = TrackerEffectCommand.PortamentoUp, EffectParam = 0x10 };
            grid[2] = new Cell { Note = TrackerNotes.FromKey(60), Instrument = 1, Effect = TrackerEffectCommand.PortamentoDown, EffectParam = 0x10 };
            grid[3] = new Cell { Note = TrackerNotes.FromKey(64), Instrument = 1, Effect = TrackerEffectCommand.TonePortamento, EffectParam = 0x10 };
            grid[4] = new Cell { Note = TrackerNotes.FromKey(60), Instrument = 1, Effect = TrackerEffectCommand.Arpeggio, EffectParam = 0x47 };
            grid[5] = new Cell { Note = TrackerNotes.FromKey(60), Instrument = 1, Effect = TrackerEffectCommand.Vibrato, EffectParam = 0x38 };
            grid[6] = new Cell { Note = TrackerNotes.FromKey(60), Instrument = 1, Effect = TrackerEffectCommand.Retrigger, EffectParam = 2 };
            grid[7] = new Cell { Note = TrackerNotes.FromKey(60), Instrument = 1, Effect = TrackerEffectCommand.NoteCut, EffectParam = 3 };
            grid[8] = new Cell { Note = TrackerNotes.FromKey(60), Instrument = 1, Volume = 50, Effect = TrackerEffectCommand.NoteDelay, EffectParam = 3 };

            SynthesizerOptions options = new SynthesizerOptions(SampleRate, 1, 64, 8);
            Synthesizer synth = new Synthesizer(options, bank.GetPatch(0, 0));
            TrackerSequencer seq = new TrackerSequencer(TickEffectSong(grid), synth, bank);

            seq.Play();
            float[] block = new float[64];
            float peak = 0f;
            bool bounded = true;
            int totalFrames = 0;
            int produced;
            do {
                produced = seq.Read(block);
                for (int i = 0; i < produced; i++) {
                    peak = Math.Max(peak, Math.Abs(block[i]));
                    if (Math.Abs(block[i]) > 1f)
                        bounded = false;
                }
                totalFrames += produced;
            } while (produced == block.Length && totalFrames < 80000);

            Assert.That(bounded, Is.True, "all rendered samples must be within [-1, 1].");
            Assert.That(peak, Is.GreaterThan(0.01f), "the tick-effects song must not render silent.");
        }
    }
}
