using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Formats.Tracker;
using Pooshit.AudioSynth.Sequencing;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;
using Pooshit.AudioSynth.Tests.Helpers;

namespace Pooshit.AudioSynth.Tests.Tracker {

    /// <summary>
    /// Per-tick tracker effect engine behavior (design DiVoid #7511): the right synth calls at the
    /// armed tick, the "00 = reuse last param" memory rule, and no-drift per-tick sample accounting.
    /// </summary>
    [TestFixture, Parallelizable]
    public class TrackerEffectEngineTests {

        const int SampleRate = 44100;
        const int Speed = 6;
        const int Tempo = 125;
        const int SamplesPerTick = 882; // 6 * 44100 * 2.5 / 125 / 6, exact.

        static SoundBank OnePatchBank() =>
            new SoundBank(new[] { (0, 0, "a", (IPatch)new StubPatch("a")) });

        static CallLoggingSynthesizer Logging() => new CallLoggingSynthesizer(new AudioFormat(SampleRate, 1));

        static Song OneChannel(Cell[] grid, int rows) =>
            new Song {
                DefaultBpm = Tempo,
                DefaultSpeed = Speed,
                DefaultRows = rows,
                ChannelCount = 1,
                Instruments = new[] { new Instrument { Bank = 0, Program = 0, Name = "a" } },
                Patterns = new[] { new Pattern { Rows = rows, Cells = grid } },
                Order = new[] { 0 }
            };

        static Cell Note(int key, int instrument = 1) =>
            new Cell { Note = TrackerNotes.FromKey(key), Instrument = (byte)instrument };

        static List<string> Controls(CallLoggingSynthesizer synth) =>
            synth.CallLog.Where(c => !c.StartsWith("Read", StringComparison.Ordinal)).ToList();

        static void Pull(TrackerSequencer seq, int frames) {
            float[] block = new float[frames];
            seq.Read(block);
        }

        static string Gain(float value) =>
            FormattableString.Invariant($"SetChannelGain(0,{value})");

        [Test, Parallelizable]
        public void VolumeSlide_RampsGainEachTick_AndClampsAtFull() {
            Cell[] grid = new Cell[1];
            grid[0] = new Cell {
                Note = TrackerNotes.FromKey(60), Instrument = 1, Volume = 40,
                Effect = TrackerEffectCommand.VolumeSlide, EffectParam = 0xA0
            };
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 1), synth, OnePatchBank());

            seq.Play();
            Pull(seq, SamplesPerTick * Speed);

            List<string> gains = synth.CallLog.Where(c => c.StartsWith("SetChannelGain", StringComparison.Ordinal)).ToList();
            Assert.That(gains, Is.EqualTo(new[] {
                Gain(40 / 64f), Gain(50 / 64f), Gain(60 / 64f), Gain(64 / 64f), Gain(64 / 64f), Gain(64 / 64f)
            }), "the base volume, then five ticks of +10/tick, clamping at full (64).");
        }

        [Test, Parallelizable]
        public void VolumeSlide_ContinuesWithoutNote_AndZeroParamReusesMemory() {
            Cell[] grid = new Cell[2];
            grid[0] = new Cell {
                Note = TrackerNotes.FromKey(60), Instrument = 1, Volume = 20,
                Effect = TrackerEffectCommand.VolumeSlide, EffectParam = 0x10
            };
            grid[1] = new Cell { Effect = TrackerEffectCommand.VolumeSlide, EffectParam = 0 };
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 2), synth, OnePatchBank());

            seq.Play();
            Pull(seq, SamplesPerTick * Speed * 2);

            List<string> gains = synth.CallLog.Where(c => c.StartsWith("SetChannelGain", StringComparison.Ordinal)).ToList();
            int[] expectedLevels = { 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30 };
            Assert.That(gains, Is.EqualTo(expectedLevels.Select(l => Gain(l / 64f)).ToArray()),
                "row1 has no note and no explicit param, yet keeps sliding: it carries volumeLevel and reuses row0's memorized delta.");
            Assert.That(synth.CallLog.Count(c => c.StartsWith("NoteOn", StringComparison.Ordinal)), Is.EqualTo(1),
                "row1 carries no note sub-column, so only row0 triggers.");
        }

        [Test, Parallelizable]
        public void PortamentoUp_AccumulatesPitch_AndResetsOnFreshNote() {
            Cell[] grid = new Cell[3];
            grid[0] = new Cell {
                Note = TrackerNotes.FromKey(60), Instrument = 1,
                Effect = TrackerEffectCommand.PortamentoUp, EffectParam = 0x20
            };
            grid[1] = Note(64);
            grid[2] = new Cell {
                Note = TrackerNotes.FromKey(64), Instrument = 1,
                Effect = TrackerEffectCommand.PortamentoUp, EffectParam = 0x10
            };
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 3), synth, OnePatchBank());

            seq.Play();
            Pull(seq, SamplesPerTick * Speed * 3);

            List<string> bends = synth.CallLog.Where(c => c.StartsWith("SetChannelPitchBend", StringComparison.Ordinal)).ToList();
            Assert.That(bends, Is.EqualTo(new[] {
                "SetChannelPitchBend(0,0)",
                "SetChannelPitchBend(0,2)", "SetChannelPitchBend(0,4)", "SetChannelPitchBend(0,6)",
                "SetChannelPitchBend(0,8)", "SetChannelPitchBend(0,10)",
                "SetChannelPitchBend(0,0)",
                "SetChannelPitchBend(0,0)",
                "SetChannelPitchBend(0,1)", "SetChannelPitchBend(0,2)", "SetChannelPitchBend(0,3)",
                "SetChannelPitchBend(0,4)", "SetChannelPitchBend(0,5)"
            }), "each fresh note (row0, row1, row2) re-syncs the synth's bend to 0 before any slide accumulates; " +
                "row2's slide starts from a pitch offset reset by row1's fresh note, not the accumulated 10 from row0.");
        }

        [Test, Parallelizable]
        public void PortamentoDown_DecreasesPitchEachTick() {
            Cell[] grid = new Cell[1];
            grid[0] = new Cell {
                Note = TrackerNotes.FromKey(60), Instrument = 1,
                Effect = TrackerEffectCommand.PortamentoDown, EffectParam = 0x30
            };
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 1), synth, OnePatchBank());

            seq.Play();
            Pull(seq, SamplesPerTick * Speed);

            List<string> bends = synth.CallLog.Where(c => c.StartsWith("SetChannelPitchBend", StringComparison.Ordinal)).ToList();
            Assert.That(bends, Is.EqualTo(new[] {
                "SetChannelPitchBend(0,0)",
                "SetChannelPitchBend(0,-3)", "SetChannelPitchBend(0,-6)", "SetChannelPitchBend(0,-9)",
                "SetChannelPitchBend(0,-12)", "SetChannelPitchBend(0,-15)"
            }), "the fresh note re-syncs the synth's bend to 0 before the slide begins.");
        }

        [Test, Parallelizable]
        public void TonePortamento_SlidesTowardTargetNote_WithoutRetriggering_AndClampsAtTarget() {
            Cell[] grid = new Cell[2];
            grid[0] = Note(60);
            grid[1] = new Cell {
                Note = TrackerNotes.FromKey(64), Instrument = 1,
                Effect = TrackerEffectCommand.TonePortamento, EffectParam = 0x10
            };
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 2), synth, OnePatchBank());

            seq.Play();
            Pull(seq, SamplesPerTick * Speed * 2);

            Assert.That(synth.CallLog.Count(c => c.StartsWith("NoteOn", StringComparison.Ordinal)), Is.EqualTo(1),
                "tone-portamento must not retrigger the note.");
            List<string> bends = synth.CallLog.Where(c => c.StartsWith("SetChannelPitchBend", StringComparison.Ordinal)).ToList();
            Assert.That(bends, Is.EqualTo(new[] {
                "SetChannelPitchBend(0,0)",
                "SetChannelPitchBend(0,1)", "SetChannelPitchBend(0,2)", "SetChannelPitchBend(0,3)",
                "SetChannelPitchBend(0,4)", "SetChannelPitchBend(0,4)"
            }), "row0's fresh note re-syncs the bend to 0; the slide then clamps at the +4 semitone target and holds once reached.");
        }

        [Test, Parallelizable]
        public void TonePortamento_SlidesDownward_WhenTargetNoteIsLower() {
            Cell[] grid = new Cell[2];
            grid[0] = Note(64);
            grid[1] = new Cell {
                Note = TrackerNotes.FromKey(60), Instrument = 1,
                Effect = TrackerEffectCommand.TonePortamento, EffectParam = 0x10
            };
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 2), synth, OnePatchBank());

            seq.Play();
            Pull(seq, SamplesPerTick * Speed * 2);

            List<string> bends = synth.CallLog.Where(c => c.StartsWith("SetChannelPitchBend", StringComparison.Ordinal)).ToList();
            Assert.That(bends, Is.EqualTo(new[] {
                "SetChannelPitchBend(0,0)",
                "SetChannelPitchBend(0,-1)", "SetChannelPitchBend(0,-2)", "SetChannelPitchBend(0,-3)",
                "SetChannelPitchBend(0,-4)", "SetChannelPitchBend(0,-4)"
            }), "row0's fresh note re-syncs the bend to 0; a lower target note then slides the pitch offset downward and clamps there.");
        }

        [Test, Parallelizable]
        public void TonePortamento_NoActiveKey_IsNoOp() {
            Cell[] grid = new Cell[1];
            grid[0] = new Cell {
                Note = TrackerNotes.FromKey(64), Instrument = 1,
                Effect = TrackerEffectCommand.TonePortamento, EffectParam = 0x10
            };
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 1), synth, OnePatchBank());

            seq.Play();
            Pull(seq, SamplesPerTick * Speed);

            Assert.That(synth.CallLog.Any(c => c.StartsWith("SetChannelPitchBend", StringComparison.Ordinal)), Is.False,
                "with no sounding note, tone-portamento has no slide source and must not bend.");
        }

        [Test, Parallelizable]
        public void TonePortamento_ContinuingRowWithoutNote_KeepsPriorTarget() {
            Cell[] grid = new Cell[3];
            grid[0] = Note(60);
            grid[1] = new Cell {
                Note = TrackerNotes.FromKey(64), Instrument = 1,
                Effect = TrackerEffectCommand.TonePortamento, EffectParam = 0x10
            };
            grid[2] = new Cell { Effect = TrackerEffectCommand.TonePortamento, EffectParam = 0x20 };
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 3), synth, OnePatchBank());

            seq.Play();
            Pull(seq, SamplesPerTick * Speed * 3);

            List<string> bends = synth.CallLog.Where(c => c.StartsWith("SetChannelPitchBend", StringComparison.Ordinal)).ToList();
            Assert.That(bends, Is.EqualTo(new[] {
                "SetChannelPitchBend(0,0)",
                "SetChannelPitchBend(0,1)", "SetChannelPitchBend(0,2)", "SetChannelPitchBend(0,3)",
                "SetChannelPitchBend(0,4)", "SetChannelPitchBend(0,4)",
                "SetChannelPitchBend(0,4)", "SetChannelPitchBend(0,4)", "SetChannelPitchBend(0,4)",
                "SetChannelPitchBend(0,4)", "SetChannelPitchBend(0,4)"
            }), "row0's fresh note re-syncs the bend to 0; row2 carries no note, so the +4 target from row1 is kept (already reached, so it just holds).");
        }

        [Test, Parallelizable]
        public void Arpeggio_CyclesThroughOffsets_IncludingTick0() {
            Cell[] grid = new Cell[1];
            grid[0] = new Cell {
                Note = TrackerNotes.FromKey(60), Instrument = 1,
                Effect = TrackerEffectCommand.Arpeggio, EffectParam = 0x47
            };
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 1), synth, OnePatchBank());

            seq.Play();
            Pull(seq, SamplesPerTick * Speed);

            List<string> bends = synth.CallLog.Where(c => c.StartsWith("SetChannelPitchBend", StringComparison.Ordinal)).ToList();
            Assert.That(bends, Is.EqualTo(new[] {
                "SetChannelPitchBend(0,0)",
                "SetChannelPitchBend(0,0)", "SetChannelPitchBend(0,4)", "SetChannelPitchBend(0,7)",
                "SetChannelPitchBend(0,0)", "SetChannelPitchBend(0,4)", "SetChannelPitchBend(0,7)"
            }), "the fresh-note reset fires first (bend 0), then base/hi/lo cycles every 3 ticks, starting at tick 0.");
        }

        [Test, Parallelizable]
        public void Arpeggio_HeldNoteNextRow_SettlesBendToBasePitch() {
            Cell[] grid = new Cell[2];
            grid[0] = new Cell {
                Note = TrackerNotes.FromKey(60), Instrument = 1,
                Effect = TrackerEffectCommand.Arpeggio, EffectParam = 0x47
            };
            grid[1] = new Cell();
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 2), synth, OnePatchBank());

            seq.Play();
            Pull(seq, SamplesPerTick * Speed * 2);

            List<string> bends = synth.CallLog.Where(c => c.StartsWith("SetChannelPitchBend", StringComparison.Ordinal)).ToList();
            Assert.That(bends.Last(), Is.EqualTo("SetChannelPitchBend(0,0)"),
                "row1 holds the note with no arpeggio armed, so the engine settles the synth's bend back to the base " +
                "pitch offset instead of leaving row0's last cycled offset stuck.");
        }

        [Test, Parallelizable]
        public void Vibrato_OscillatesAroundPitchOffset_StartingAtZeroPhase() {
            Cell[] grid = new Cell[1];
            grid[0] = new Cell {
                Note = TrackerNotes.FromKey(60), Instrument = 1,
                Effect = TrackerEffectCommand.Vibrato, EffectParam = 0x38
            };
            RecordingSynthesizer synth = new RecordingSynthesizer(new AudioFormat(SampleRate, 1));
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 1), synth, OnePatchBank());

            seq.Play();
            Pull(seq, SamplesPerTick * Speed);

            List<(int Channel, float Semitones)> bends = synth.ChannelPitchBendCalls;
            Assert.That(bends.Count, Is.EqualTo(Speed + 1), "the fresh-note reset call precedes the vibrato's own speed ticks (0..speed-1).");
            Assert.That(bends[0].Semitones, Is.EqualTo(0f).Within(1e-6f), "the fresh-note reset re-syncs the synth's bend to 0 before vibrato arms.");
            Assert.That(bends[1].Semitones, Is.EqualTo(0f).Within(1e-6f), "tick 0 uses the freshly-reset phase 0.");

            float phase = 0f;
            for (int t = 0; t < Speed; t++) {
                if (t > 0)
                    phase += 0.3f;
                float expected = (float)Math.Sin(phase) * 1f;
                Assert.That(bends[t + 1].Semitones, Is.EqualTo(expected).Within(1e-5f));
            }
        }

        [Test, Parallelizable]
        public void Vibrato_HeldNoteNextRow_SettlesBendToBasePitch() {
            Cell[] grid = new Cell[2];
            grid[0] = new Cell {
                Note = TrackerNotes.FromKey(60), Instrument = 1,
                Effect = TrackerEffectCommand.Vibrato, EffectParam = 0x38
            };
            grid[1] = new Cell();
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 2), synth, OnePatchBank());

            seq.Play();
            Pull(seq, SamplesPerTick * Speed * 2);

            List<string> bends = synth.CallLog.Where(c => c.StartsWith("SetChannelPitchBend", StringComparison.Ordinal)).ToList();
            Assert.That(bends.Last(), Is.EqualTo("SetChannelPitchBend(0,0)"),
                "row1 holds the note with no vibrato armed, so the engine settles the synth's bend back to the base " +
                "pitch offset instead of leaving row0's last oscillation sample stuck.");
        }

        [Test, Parallelizable]
        public void Retrigger_FiresNoteOnAtInterval_UsingActiveKey() {
            Cell[] grid = new Cell[1];
            grid[0] = new Cell {
                Note = TrackerNotes.FromKey(60), Instrument = 1,
                Effect = TrackerEffectCommand.Retrigger, EffectParam = 2
            };
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 1), synth, OnePatchBank());

            seq.Play();
            Pull(seq, SamplesPerTick * Speed);

            List<string> noteOns = synth.CallLog.Where(c => c.StartsWith("NoteOn", StringComparison.Ordinal)).ToList();
            Assert.That(noteOns, Is.EqualTo(new[] { "NoteOn(0,60,127)", "NoteOn(0,60,127)", "NoteOn(0,60,127)" }),
                "the initial trigger plus retriggers at ticks 2 and 4 (interval 2, over ticks 1..5).");
        }

        [Test, Parallelizable]
        public void Retrigger_NoActiveKey_NeverFires() {
            Cell[] grid = new Cell[1];
            grid[0] = new Cell { Effect = TrackerEffectCommand.Retrigger, EffectParam = 1 };
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 1), synth, OnePatchBank());

            seq.Play();
            Pull(seq, SamplesPerTick * Speed);

            Assert.That(synth.CallLog.Any(c => c.StartsWith("NoteOn", StringComparison.Ordinal)), Is.False);
        }

        [Test, Parallelizable]
        public void Retrigger_ZeroInterval_NeverFires() {
            Cell[] grid = new Cell[1];
            grid[0] = new Cell {
                Note = TrackerNotes.FromKey(60), Instrument = 1,
                Effect = TrackerEffectCommand.Retrigger, EffectParam = 0
            };
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 1), synth, OnePatchBank());

            seq.Play();
            Pull(seq, SamplesPerTick * Speed);

            Assert.That(synth.CallLog.Count(c => c.StartsWith("NoteOn", StringComparison.Ordinal)), Is.EqualTo(1),
                "only the initial trigger; an unarmed (zero) interval never retriggers.");
        }

        [Test, Parallelizable]
        public void NoteCut_SilencesAtScheduledTick() {
            Cell[] grid = new Cell[1];
            grid[0] = new Cell {
                Note = TrackerNotes.FromKey(60), Instrument = 1,
                Effect = TrackerEffectCommand.NoteCut, EffectParam = 3
            };
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 1), synth, OnePatchBank());

            seq.Play();
            Pull(seq, SamplesPerTick * Speed);

            Assert.That(synth.CallLog.Count(c => c == "SilenceChannel(0)"), Is.EqualTo(1));
        }

        [Test, Parallelizable]
        public void NoteCut_NoActiveKey_DoesNotSilence() {
            Cell[] grid = new Cell[1];
            grid[0] = new Cell { Effect = TrackerEffectCommand.NoteCut, EffectParam = 2 };
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 1), synth, OnePatchBank());

            seq.Play();
            Pull(seq, SamplesPerTick * Speed);

            Assert.That(synth.CallLog.Any(c => c == "SilenceChannel(0)"), Is.False,
                "with no active key, the cut is a no-op instead of emitting a needless silence call.");
        }

        [Test, Parallelizable]
        public void NoteCut_ThenBareRetrigger_DoesNotRevive() {
            Cell[] grid = new Cell[2];
            grid[0] = new Cell {
                Note = TrackerNotes.FromKey(60), Instrument = 1,
                Effect = TrackerEffectCommand.NoteCut, EffectParam = 2
            };
            grid[1] = new Cell { Effect = TrackerEffectCommand.Retrigger, EffectParam = 1 };
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 2), synth, OnePatchBank());

            seq.Play();
            Pull(seq, SamplesPerTick * Speed * 2);

            Assert.That(synth.CallLog.Count(c => c.StartsWith("NoteOn", StringComparison.Ordinal)), Is.EqualTo(1),
                "row0's cut clears the applier's active key, so row1's bare retrigger has nothing to revive.");
        }

        [Test, Parallelizable]
        public void NoteDelay_WithholdsWholeCellUntilDelayTick() {
            Cell[] grid = new Cell[1];
            grid[0] = new Cell {
                Note = TrackerNotes.FromKey(60), Instrument = 1, Volume = 50,
                Effect = TrackerEffectCommand.NoteDelay, EffectParam = 3
            };
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 1), synth, OnePatchBank());

            seq.Play();
            Pull(seq, SamplesPerTick * 3 - 1);
            Assert.That(Controls(synth), Is.EqualTo(new[] { "SetChannelPan(0,0)" }),
                "only the initial pan seed fires; the whole cell is withheld until its delay tick.");

            Pull(seq, 1);
            Assert.That(Controls(synth), Is.EqualTo(new[] {
                "SetChannelPan(0,0)", Gain(50 / 64f), "SetChannelPatch(0)", "NoteOn(0,60,127)"
            }), "at the delay tick the withheld controls and note both fire, in the applier's usual order.");
        }

        [Test, Parallelizable]
        public void NoteDelay_ZeroParam_AppliesHeldCellImmediatelyAtTick0() {
            Cell[] grid = new Cell[1];
            grid[0] = new Cell {
                Note = TrackerNotes.FromKey(60), Instrument = 1, Volume = 50,
                Effect = TrackerEffectCommand.NoteDelay, EffectParam = 0
            };
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 1), synth, OnePatchBank());

            seq.Play();
            Pull(seq, 1);

            Assert.That(Controls(synth), Is.EqualTo(new[] {
                "SetChannelPan(0,0)", Gain(50 / 64f), "SetChannelPatch(0)", "NoteOn(0,60,127)", "SetChannelPitchBend(0,0)"
            }), "a delay param resolving to 0 has no valid tick 1..speed-1 to fire on, so it must apply the held cell " +
                "immediately at tick 0 instead of silently dropping the note.");
        }

        [Test, Parallelizable]
        public void TickSubdivision_SumsExactlyToRowSamples_WithEffectActive() {
            Cell[] grid = new Cell[2];
            grid[0] = new Cell {
                Note = TrackerNotes.FromKey(60), Instrument = 1,
                Effect = TrackerEffectCommand.VolumeSlide, EffectParam = 0x10
            };
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 2), synth, OnePatchBank());

            seq.Play();
            Pull(seq, SamplesPerTick * Speed);
            Assert.That(seq.Row, Is.EqualTo(0), "still row 0 until the last tick's samples are fully consumed.");

            Pull(seq, 1);
            Assert.That(seq.Row, Is.EqualTo(1), "one more sample crosses into row 1: the six ticks summed exactly.");
        }

        [Test, Parallelizable]
        public void SeekTo_ResetsEffectEngineState_AndParamMemory() {
            Cell[] grid = new Cell[2];
            grid[0] = new Cell {
                Note = TrackerNotes.FromKey(60), Instrument = 1,
                Effect = TrackerEffectCommand.VolumeSlide, EffectParam = 0x30
            };
            grid[1] = new Cell {
                Note = TrackerNotes.FromKey(62), Instrument = 1,
                Effect = TrackerEffectCommand.VolumeSlide, EffectParam = 0
            };
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 2), synth, OnePatchBank());

            seq.Play();
            Pull(seq, SamplesPerTick * Speed);

            seq.SeekTo(0, 1);
            seq.Play();
            Pull(seq, SamplesPerTick * Speed);

            List<string> gainsAfterSeek = synth.CallLog
                .SkipWhile(c => !c.Contains("NoteOn(0,62,127)", StringComparison.Ordinal))
                .Where(c => c.StartsWith("SetChannelGain", StringComparison.Ordinal))
                .ToList();
            Assert.That(gainsAfterSeek, Is.EqualTo(Enumerable.Repeat("SetChannelGain(0,1)", 5).ToList()),
                "SeekTo clears the engine's param memory, so row1's 00 resolves to delta 0, not row0's memorized +3.");
        }
    }
}
