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

    [TestFixture, Parallelizable]
    public class TrackerSequencerTests {

        const int SampleRate = 44100;

        static SoundBank OnePatchBank() =>
            new SoundBank(new[] { (0, 0, "a", (IPatch)new StubPatch("a")) });

        static SoundBank TwoPatchBank() =>
            new SoundBank(new[] { (0, 0, "a", (IPatch)new StubPatch("a")), (1, 5, "b", (IPatch)new StubPatch("b")) });

        static CallLoggingSynthesizer Logging(int sampleRate = SampleRate) =>
            new CallLoggingSynthesizer(new AudioFormat(sampleRate, 1));

        static Song OneChannel(Cell[] grid, int rows, int[]? order = null, Instrument[]? instruments = null, Pattern[]? patterns = null) =>
            new Song {
                DefaultBpm = 125,
                DefaultSpeed = 6,
                DefaultRows = rows,
                ChannelCount = 1,
                Instruments = instruments ?? new[] { new Instrument { Bank = 0, Program = 0, Name = "a" } },
                Patterns = patterns ?? new[] { new Pattern { Rows = rows, Cells = grid } },
                Order = order ?? new[] { 0 }
            };

        static Cell Note(int key, int instrument = 1, int volume = 0) =>
            new Cell { Note = TrackerNotes.FromKey(key), Instrument = (byte)instrument, Volume = (byte)volume };

        static List<string> Controls(CallLoggingSynthesizer synth) =>
            synth.CallLog.Where(c => !c.StartsWith("Read", StringComparison.Ordinal)).ToList();

        static int Pull(TrackerSequencer seq, int frames) {
            float[] block = new float[frames];
            return seq.Read(block);
        }

        [Test, Parallelizable]
        public void Constructor_Rejects_InvalidArguments() {
            SoundBank bank = OnePatchBank();
            CallLoggingSynthesizer synth = Logging();
            Song valid = OneChannel(new Cell[4], 4);

            Assert.Throws<ArgumentNullException>(() => new TrackerSequencer(null!, synth, bank));
            Assert.Throws<ArgumentNullException>(() => new TrackerSequencer(valid, null!, bank));
            Assert.Throws<ArgumentNullException>(() => new TrackerSequencer(valid, synth, null!));
            Assert.Throws<ArgumentException>(() => new TrackerSequencer(OneChannelWithChannels(0), synth, bank));
            Assert.Throws<ArgumentException>(() => new TrackerSequencer(OneChannelWithChannels(17), synth, bank));
            Assert.Throws<ArgumentException>(() => new TrackerSequencer(WithBpm(0), synth, bank));
            Assert.Throws<ArgumentException>(() => new TrackerSequencer(WithSpeed(0), synth, bank));
        }

        static Song OneChannelWithChannels(int channels) {
            Song song = OneChannel(new Cell[4], 4);
            song.ChannelCount = channels;
            return song;
        }

        static Song WithBpm(int bpm) {
            Song song = OneChannel(new Cell[4], 4);
            song.DefaultBpm = bpm;
            return song;
        }

        static Song WithSpeed(int speed) {
            Song song = OneChannel(new Cell[4], 4);
            song.DefaultSpeed = speed;
            return song;
        }

        [Test, Parallelizable]
        public void Format_ReflectsBoundSynth() {
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(new Cell[4], 4), synth, OnePatchBank());
            Assert.That(seq.Format, Is.EqualTo(synth.Format));
        }

        [Test, Parallelizable]
        public void TrivialSong_AppliesCellsInOrder_AndStopsAtEnd() {
            Cell[] grid = new Cell[4];
            grid[0] = Note(60, instrument: 1, volume: 64);
            grid[2] = Note(64, instrument: 1);
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 4), synth, OnePatchBank());

            seq.Play();
            Pull(seq, 25000);

            Assert.That(Controls(synth), Is.EqualTo(new[] {
                "SetChannelGain(0,1)", "SetChannelPatch(0)", "NoteOn(0,60,127)",
                "NoteOff(0,60)", "NoteOn(0,64,127)"
            }));
            Assert.That(seq.IsPlaying, Is.False, "a non-looping song stops when the order list ends.");
            Assert.That(seq.OrderIndex, Is.EqualTo(0));
            Assert.That(seq.Row, Is.EqualTo(3), "the last applied row is the final row of the pattern.");
        }

        [Test, Parallelizable]
        public void Volume_ScalesGain_AndClampsAboveFull() {
            Cell[] grid = new Cell[4];
            grid[0] = Note(60, instrument: 1, volume: 32);
            grid[1] = new Cell { Volume = 100 };
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 4), synth, OnePatchBank());

            seq.Play();
            Pull(seq, 25000);

            List<string> controls = Controls(synth);
            Assert.That(controls, Does.Contain("SetChannelGain(0,0.5)"), "volume 32/64 = 0.5.");
            Assert.That(controls, Does.Contain("SetChannelGain(0,1)"), "volume 100 clamps to full (1.0).");
        }

        [Test, Parallelizable]
        public void NoteOff_ReleasesSoundingNote() {
            Cell[] grid = new Cell[4];
            grid[0] = Note(60);
            grid[1] = new Cell { Note = TrackerNotes.Off };
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 4), synth, OnePatchBank());

            seq.Play();
            Pull(seq, 25000);

            Assert.That(Controls(synth), Does.Contain("NoteOff(0,60)"));
        }

        [Test, Parallelizable]
        public void NoteCut_SilencesSoundingNote() {
            Cell[] grid = new Cell[4];
            grid[0] = Note(60);
            grid[1] = new Cell { Note = TrackerNotes.Cut };
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 4), synth, OnePatchBank());

            seq.Play();
            Pull(seq, 25000);

            Assert.That(Controls(synth), Does.Contain("SilenceChannel(0)"));
        }

        [Test, Parallelizable]
        public void InstrumentSlotOutOfRange_SelectsNoPatch() {
            Cell[] grid = new Cell[4];
            grid[0] = Note(60, instrument: 5);
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 4), synth, OnePatchBank());

            seq.Play();
            Pull(seq, 25000);

            List<string> controls = Controls(synth);
            Assert.That(controls, Does.Contain("NoteOn(0,60,127)"));
            Assert.That(controls, Has.None.EqualTo("SetChannelPatch(0)"), "an out-of-range instrument slot selects no patch.");
        }

        [Test, Parallelizable]
        public void InstrumentChange_ReselectsPatch_AndReleasesPrior() {
            Cell[] grid = new Cell[4];
            grid[0] = Note(60, instrument: 1);
            grid[1] = Note(62, instrument: 2);
            Instrument[] instruments = { new Instrument { Bank = 0, Program = 0 }, new Instrument { Bank = 1, Program = 5 } };
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 4, instruments: instruments), synth, TwoPatchBank());

            seq.Play();
            Pull(seq, 25000);

            Assert.That(Controls(synth), Is.EqualTo(new[] {
                "SetChannelPatch(0)", "NoteOn(0,60,127)",
                "SetChannelPatch(0)", "NoteOff(0,60)", "NoteOn(0,62,127)"
            }));
        }

        [Test, Parallelizable]
        public void Position_AdvancesByRow_AfterExactRowSamples() {
            Cell[] grid = new Cell[4];
            grid[0] = Note(60);
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 4), synth, OnePatchBank());

            int samplesPerRow = 6 * SampleRate * 5 / 2 / 125;
            seq.Play();
            Pull(seq, samplesPerRow);
            Assert.That(seq.Row, Is.EqualTo(0), "still in row 0 until its samples are consumed.");
            Pull(seq, 1);
            Assert.That(seq.Row, Is.EqualTo(1), "crossing the row boundary advances to row 1.");
        }

        [Test, Parallelizable]
        public void NotPlaying_FillsFullBlockWithSilence() {
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(new Cell[4], 4), synth, OnePatchBank());

            Assert.That(seq.IsPlaying, Is.False, "a fresh sequencer starts stopped.");
            int produced = Pull(seq, 64);
            Assert.That(produced, Is.EqualTo(64), "a stopped sequencer still fills the whole block.");
        }

        [Test, Parallelizable]
        public void Stop_SilencesEveryChannel() {
            CallLoggingSynthesizer synth = Logging();
            Song song = OneChannel(new Cell[4], 4);
            song.ChannelCount = 3;
            song.Patterns = new[] { new Pattern { Rows = 4, Cells = new Cell[12] } };
            TrackerSequencer seq = new TrackerSequencer(song, synth, OnePatchBank());

            seq.Play();
            Pull(seq, 100);
            seq.Stop();

            Assert.That(seq.IsPlaying, Is.False);
            Assert.That(Controls(synth), Does.Contain("SilenceChannel(0)")
                .And.Contain("SilenceChannel(1)").And.Contain("SilenceChannel(2)"));
        }

        [Test, Parallelizable]
        public void SeekTo_AppliesFromTargetRow() {
            Cell[] grid = new Cell[4];
            grid[0] = Note(60);
            grid[2] = Note(64);
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 4), synth, OnePatchBank());

            seq.SeekTo(0, 2);
            seq.Play();
            Pull(seq, 25000);

            List<string> controls = Controls(synth);
            Assert.That(controls, Does.Contain("NoteOn(0,64,127)"), "seek to row 2 plays that row's note.");
            Assert.That(controls, Has.None.EqualTo("NoteOn(0,60,127)"), "rows before the seek target are skipped.");
        }

        [Test, Parallelizable]
        public void Looping_WrapsToStartAtEndOfOrder() {
            Cell[] grid = new Cell[4];
            grid[0] = Note(60);
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 4), synth, OnePatchBank()) { Looping = true };

            seq.Play();
            Pull(seq, 60000);

            Assert.That(seq.IsPlaying, Is.True, "a looping sequencer never self-ends.");
            Assert.That(Controls(synth).Count(c => c == "NoteOn(0,60,127)"), Is.GreaterThan(1),
                "the first row re-triggers after wrapping.");
        }

        [Test, Parallelizable]
        public void InvalidAndNegativeOrderEntries_AreSkipped() {
            Cell[] grid = new Cell[4];
            grid[0] = Note(60);
            CallLoggingSynthesizer synth = Logging();
            Song song = OneChannel(grid, 4, order: new[] { -1, 5, 0 });
            TrackerSequencer seq = new TrackerSequencer(song, synth, OnePatchBank());

            seq.Play();
            Pull(seq, 25000);

            Assert.That(Controls(synth), Does.Contain("NoteOn(0,60,127)"), "the walk skips invalid orders and reaches the valid one.");
            Assert.That(seq.OrderIndex, Is.EqualTo(2));
        }

        [Test, Parallelizable]
        public void MalformedPattern_IsSkipped() {
            Cell[] good = new Cell[4];
            good[0] = Note(60);
            Pattern[] patterns = {
                new Pattern { Rows = 4, Cells = new Cell[1] },
                new Pattern { Rows = 4, Cells = good }
            };
            CallLoggingSynthesizer synth = Logging();
            Song song = OneChannel(good, 4, order: new[] { 0, 1 }, patterns: patterns);
            TrackerSequencer seq = new TrackerSequencer(song, synth, OnePatchBank());

            seq.Play();
            Pull(seq, 25000);

            Assert.That(Controls(synth), Does.Contain("NoteOn(0,60,127)"), "a pattern with too few cells is skipped, not thrown on.");
        }

        [Test, Parallelizable]
        public void ZeroRowPattern_IsSkipped() {
            Cell[] good = new Cell[4];
            good[0] = Note(60);
            Pattern[] patterns = {
                new Pattern { Rows = 0, Cells = Array.Empty<Cell>() },
                new Pattern { Rows = 4, Cells = good }
            };
            CallLoggingSynthesizer synth = Logging();
            Song song = OneChannel(good, 4, order: new[] { 0, 1 }, patterns: patterns);
            TrackerSequencer seq = new TrackerSequencer(song, synth, OnePatchBank());

            seq.Play();
            Pull(seq, 25000);

            Assert.That(Controls(synth), Does.Contain("NoteOn(0,60,127)"));
        }

        [Test, Parallelizable]
        public void AllInvalidOrders_StopPlayback() {
            Cell[] grid = new Cell[4];
            grid[0] = Note(60);
            CallLoggingSynthesizer synth = Logging();
            Song song = OneChannel(grid, 4, order: new[] { 5, 9 });
            TrackerSequencer seq = new TrackerSequencer(song, synth, OnePatchBank()) { Looping = true };

            seq.Play();
            Pull(seq, 25000);

            Assert.That(seq.IsPlaying, Is.False, "an all-invalid order list trips the scan guard and stops instead of spinning.");
            Assert.That(Controls(synth).Any(c => c.StartsWith("NoteOn", StringComparison.Ordinal)), Is.False);
        }

        [Test, Parallelizable]
        public void JumpToOrder_BackwardSelf_LoopsCursor() {
            Cell[] grid = new Cell[4];
            grid[0] = Note(60);
            grid[3] = new Cell { Effect = TrackerEffectCommand.JumpToOrder, EffectParam = 0 };
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 4), synth, OnePatchBank());

            seq.Play();
            Pull(seq, 60000);

            Assert.That(seq.IsPlaying, Is.True, "a self-jump loops forever.");
            Assert.That(Controls(synth).Count(c => c == "NoteOn(0,60,127)"), Is.GreaterThan(1));
        }

        [Test, Parallelizable]
        public void JumpToOrder_Forward_IsIgnored() {
            Cell[] first = new Cell[4];
            first[0] = new Cell { Effect = TrackerEffectCommand.JumpToOrder, EffectParam = 1 };
            first[2] = Note(64);
            Cell[] second = new Cell[4];
            second[0] = Note(67);
            Pattern[] patterns = { new Pattern { Rows = 4, Cells = first }, new Pattern { Rows = 4, Cells = second } };
            CallLoggingSynthesizer synth = Logging();
            Song song = OneChannel(first, 4, order: new[] { 0, 1 }, patterns: patterns);
            TrackerSequencer seq = new TrackerSequencer(song, synth, OnePatchBank());

            seq.Play();
            Pull(seq, 50000);

            List<string> controls = Controls(synth);
            Assert.That(controls, Does.Contain("NoteOn(0,64,127)"), "a forward jump is ignored, so order 0 plays in full.");
            Assert.That(controls, Does.Contain("NoteOn(0,67,127)"), "order 1 then plays linearly.");
        }

        [Test, Parallelizable]
        public void JumpToOrder_MultiChannel_LastValidWins() {
            Cell[] grid = new Cell[8];
            grid[0] = Note(60);
            grid[6] = new Cell { Effect = TrackerEffectCommand.JumpToOrder, EffectParam = 0 };
            grid[7] = new Cell { Effect = TrackerEffectCommand.JumpToOrder, EffectParam = 0 };
            Song song = new Song {
                DefaultBpm = 125, DefaultSpeed = 6, DefaultRows = 4, ChannelCount = 2,
                Instruments = new[] { new Instrument { Bank = 0, Program = 0 } },
                Patterns = new[] { new Pattern { Rows = 4, Cells = grid } },
                Order = new[] { 0 }
            };
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(song, synth, OnePatchBank());

            seq.Play();
            Pull(seq, 60000);

            Assert.That(seq.IsPlaying, Is.True, "two same-row jumps resolve to a valid target and loop.");
        }

        [Test, Parallelizable]
        public void SpeedAndTempoEffects_AreConsumed_AndShortenRowLength() {
            Cell[] grid = new Cell[4];
            grid[0] = new Cell { Effect = TrackerEffectCommand.SetSpeed, EffectParam = 3 };
            grid[1] = new Cell { Effect = TrackerEffectCommand.SetTempo, EffectParam = 250 };
            grid[2] = Note(60);
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 4), synth, OnePatchBank());

            int fastRowSamples = 3 * SampleRate * 5 / 2 / 250;
            seq.Play();
            Pull(seq, 6 * SampleRate * 5 / 2 / 125);
            Assert.That(seq.Row, Is.GreaterThan(0), "a halved-tempo, halved-speed row is shorter, so the cursor is past row 0.");
            Pull(seq, fastRowSamples * 3);
            Assert.That(Controls(synth), Does.Contain("NoteOn(0,60,127)"), "the speed/tempo effects are consumed and later rows still play.");
        }

        [Test, Parallelizable]
        public void StarvedSynth_ShortReadStopsTheLoop() {
            HalfFillSynthesizer synth = new HalfFillSynthesizer(new AudioFormat(SampleRate, 1));
            Cell[] grid = new Cell[4];
            grid[0] = Note(60);
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 4), synth, OnePatchBank());

            seq.Play();
            int produced = Pull(seq, 64);

            Assert.That(produced, Is.LessThan(64), "an underfilling synth short-reads rather than spinning.");
        }

        [Test, Parallelizable]
        public void DegenerateRowLength_StillAdvances() {
            Cell[] grid = new Cell[4];
            grid[0] = Note(60);
            Song song = OneChannel(grid, 4);
            song.DefaultSpeed = 1;
            song.DefaultBpm = 255;
            CallLoggingSynthesizer synth = Logging(sampleRate: 10);
            TrackerSequencer seq = new TrackerSequencer(song, synth, OnePatchBank()) { Looping = true };

            seq.Play();
            int produced = Pull(seq, 100);

            Assert.That(produced, Is.EqualTo(100), "sub-sample rows floor to one sample and never stall the loop.");
        }

        [Test, Parallelizable]
        public void Read_RejectsLengthNotMultipleOfChannels() {
            CallLoggingSynthesizer synth = new CallLoggingSynthesizer(new AudioFormat(SampleRate, 2));
            Song song = OneChannel(new Cell[8], 4);
            song.ChannelCount = 2;
            song.Patterns = new[] { new Pattern { Rows = 4, Cells = new Cell[8] } };
            TrackerSequencer seq = new TrackerSequencer(song, synth, OnePatchBank());

            Assert.Throws<ArgumentException>(() => {
                float[] block = new float[5];
                seq.Read(block);
            });
        }

        [Test, Parallelizable]
        [Description("End-to-end: a live Song played through the real Synthesizer renders non-silent, bounded audio.")]
        public void LiveSong_ThroughRealSynthesizer_RendersNonSilentBoundedAudio() {
            float[] dc = new float[8192];
            for (int i = 0; i < dc.Length; i++)
                dc[i] = 0.4f;
            SampleRegion region = new SampleRegion(dc, 0, dc.Length, 0, dc.Length, LoopMode.Continuous,
                SampleRate, 60, 0, EnvelopeParameters.Default, FilterParameters.Default, LfoParameters.Default, 0f);
            SoundBank bank = new SoundBank(new[] { (0, 0, "", (IPatch)new SamplePatch(region, SampleRate)) });

            Cell[] grid = new Cell[4];
            grid[0] = Note(60, instrument: 1, volume: 64);
            grid[2] = Note(64, instrument: 1);
            SynthesizerOptions options = new SynthesizerOptions(SampleRate, 1, 64, 8);
            Synthesizer synth = new Synthesizer(options, bank.GetPatch(0, 0));
            TrackerSequencer seq = new TrackerSequencer(OneChannel(grid, 4), synth, bank);

            seq.Play();
            float[] block = new float[64];
            float peak = 0f;
            bool bounded = true;
            int totalFrames = 0;
            while (totalFrames < 25000) {
                int produced = seq.Read(block);
                for (int i = 0; i < produced; i++) {
                    peak = Math.Max(peak, Math.Abs(block[i]));
                    if (Math.Abs(block[i]) > 1f)
                        bounded = false;
                }
                totalFrames += produced;
            }

            Assert.That(bounded, Is.True, "all rendered samples must be within [-1, 1].");
            Assert.That(peak, Is.GreaterThan(0.01f), "a live tracker song must not render silent.");
        }
    }
}
