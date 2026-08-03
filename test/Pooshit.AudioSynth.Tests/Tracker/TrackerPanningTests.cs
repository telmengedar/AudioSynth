using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Formats.Tracker;
using Pooshit.AudioSynth.Sequencing;
using Pooshit.AudioSynth.Sequencing.Timeline;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;
using Pooshit.AudioSynth.Tests.Helpers;

namespace Pooshit.AudioSynth.Tests.Tracker {

    /// <summary>
    /// Channel panning (design DiVoid #7561): <see cref="TrackerPan"/> math, initial-pan seeding and the
    /// <see cref="TrackerEffectCommand.SetPan"/> effect on both the live and offline playback paths.
    /// </summary>
    [TestFixture, Parallelizable]
    public class TrackerPanningTests {

        const int SampleRate = 44100;
        const int SamplesPerRow = 5292; // 6 * 44100 * 2.5 / 125, exact.

        static SoundBank OnePatchBank() =>
            new SoundBank(new[] { (0, 0, "a", (IPatch)new StubPatch("a")) });

        static CallLoggingSynthesizer Logging() => new CallLoggingSynthesizer(new AudioFormat(SampleRate, 1));

        static Cell Note(int key, int instrument = 1) =>
            new Cell { Note = TrackerNotes.FromKey(key), Instrument = (byte)instrument };

        static Song OneChannelSong(int rows, params (int row, Cell cell)[] cells) {
            Cell[] grid = new Cell[rows];
            foreach ((int row, Cell cell) in cells)
                grid[row] = cell;
            return new Song {
                DefaultBpm = 125, DefaultSpeed = 6, DefaultRows = rows, ChannelCount = 1,
                Instruments = new[] { new Instrument { Bank = 0, Program = 0, Name = "a" } },
                Patterns = new[] { new Pattern { Rows = rows, Cells = grid } },
                Order = new[] { 0 }
            };
        }

        static Song TwoChannelSong(int rows, params (int row, int channel, Cell cell)[] cells) {
            Cell[] grid = new Cell[rows * 2];
            foreach ((int row, int channel, Cell cell) in cells)
                grid[row * 2 + channel] = cell;
            return new Song {
                DefaultBpm = 125, DefaultSpeed = 6, DefaultRows = rows, ChannelCount = 2,
                Instruments = new[] { new Instrument { Bank = 0, Program = 0, Name = "a" } },
                Patterns = new[] { new Pattern { Rows = rows, Cells = grid } },
                Order = new[] { 0 }
            };
        }

        static List<TimelineEntry> Entries(Song song) => TrackerTimelineImporter.Import(song, SampleRate).Compile().Entries.ToList();

        static void Pull(TrackerSequencer seq, int frames) {
            float[] block = new float[frames];
            seq.Read(block);
        }

        static float ParsePanValue(string call) {
            int comma = call.IndexOf(',') + 1;
            int close = call.IndexOf(')');
            return float.Parse(call.Substring(comma, close - comma), CultureInfo.InvariantCulture);
        }

        // ---- TrackerPan: pure math ----

        [TestCase((byte)0, -1f)]
        [TestCase((byte)64, 0f)]
        [TestCase((byte)128, 1f)]
        public void ToSignedPan_MapsEndpointsExactly(byte value, float expected) {
            Assert.That(TrackerPan.ToSignedPan(value), Is.EqualTo(expected));
        }

        [TestCase((byte)200)]
        [TestCase((byte)255)]
        public void ToSignedPan_ClampsAboveRight(byte value) {
            Assert.That(TrackerPan.ToSignedPan(value), Is.EqualTo(1f));
        }

        [Test, Parallelizable]
        public void DefaultByte_SingleChannel_IsCentre() {
            Assert.That(TrackerPan.DefaultByte(1, 0), Is.EqualTo(TrackerPan.Center));
        }

        [TestCase(0, (byte)32)]
        [TestCase(1, (byte)96)]
        [TestCase(2, (byte)32)]
        [TestCase(3, (byte)96)]
        public void DefaultByte_MultiChannel_Alternates(int channelIndex, byte expected) {
            Assert.That(TrackerPan.DefaultByte(4, channelIndex), Is.EqualTo(expected));
        }

        [Test, Parallelizable]
        public void InitialSigned_EmptyChannelPan_UsesDefaultLayout() {
            Song song = new Song { ChannelCount = 2, ChannelPan = Array.Empty<byte>() };

            Assert.That(TrackerPan.InitialSigned(song, 0), Is.EqualTo(-0.5f));
            Assert.That(TrackerPan.InitialSigned(song, 1), Is.EqualTo(0.5f));
        }

        [Test, Parallelizable]
        public void InitialSigned_ProvidedChannelPan_UsesExplicitValues() {
            Song song = new Song { ChannelCount = 2, ChannelPan = new byte[] { 0, 128 } };

            Assert.That(TrackerPan.InitialSigned(song, 0), Is.EqualTo(-1f));
            Assert.That(TrackerPan.InitialSigned(song, 1), Is.EqualTo(1f));
        }

        // ---- Model / format compatibility ----

        [Test, Parallelizable]
        public void Song_DefaultChannelPan_IsEmpty() {
            Assert.That(new Song().ChannelPan, Is.Empty);
        }

        [Test, Parallelizable]
        [Description("Format-compatible append: SetPan takes the next open value after the tick-effects design's NoteDelay.")]
        public void SetPan_IsAppendedAtThirteen() {
            Assert.That((byte)TrackerEffectCommand.SetPan, Is.EqualTo(13));
        }

        // ---- Live path (TrackerSequencer / TrackerEffectEngine) ----

        [Test, Parallelizable]
        public void Constructor_Rejects_ChannelPanLengthMismatch() {
            Song song = OneChannelSong(4, (0, Note(60)));
            song.ChannelPan = new byte[2];

            Assert.Throws<ArgumentException>(() => new TrackerSequencer(song, Logging(), OnePatchBank()));
        }

        [Test, Parallelizable]
        public void InitialPan_SeededOnce_UsingDefaultLayout() {
            Song song = TwoChannelSong(4, (0, 0, Note(60)), (0, 1, Note(64)));
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(song, synth, OnePatchBank());

            seq.Play();
            Pull(seq, SamplesPerRow * 4);

            List<string> pans = synth.CallLog.Where(c => c.StartsWith("SetChannelPan", StringComparison.Ordinal)).ToList();
            Assert.That(pans, Is.EqualTo(new[] { "SetChannelPan(0,-0.5)", "SetChannelPan(1,0.5)" }),
                "the default alternating layout seeds once at start, before either channel's row controls.");
        }

        [Test, Parallelizable]
        public void InitialPan_ProvidedArray_IsHonored() {
            Song song = TwoChannelSong(4, (0, 0, Note(60)), (0, 1, Note(64)));
            song.ChannelPan = new byte[] { 0, 128 };
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(song, synth, OnePatchBank());

            seq.Play();
            Pull(seq, SamplesPerRow * 4);

            List<string> pans = synth.CallLog.Where(c => c.StartsWith("SetChannelPan", StringComparison.Ordinal)).ToList();
            Assert.That(pans, Is.EqualTo(new[] { "SetChannelPan(0,-1)", "SetChannelPan(1,1)" }));
        }

        [Test, Parallelizable]
        [Description("Regression guard: a mono song's unset ChannelPan resolves to dead-centre, matching pre-panning behavior.")]
        public void InitialPan_MonoSong_DefaultsToCentre() {
            Song song = OneChannelSong(4, (0, Note(60)));
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(song, synth, OnePatchBank());

            seq.Play();
            Pull(seq, SamplesPerRow * 4);

            List<string> pans = synth.CallLog.Where(c => c.StartsWith("SetChannelPan", StringComparison.Ordinal)).ToList();
            Assert.That(pans, Is.EqualTo(new[] { "SetChannelPan(0,0)" }));
        }

        [Test, Parallelizable]
        public void SeekTo_ReseedsInitialPan() {
            Song song = TwoChannelSong(4, (0, 0, Note(60)), (0, 1, Note(64)));
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(song, synth, OnePatchBank());

            seq.Play();
            Pull(seq, 100);
            seq.SeekTo(0, 0);
            seq.Play();
            Pull(seq, 100);

            Assert.That(synth.CallLog.Count(c => c == "SetChannelPan(0,-0.5)"), Is.EqualTo(2),
                "seeking bypasses any SetPan effects already applied, so the initial pan is re-established.");
        }

        [Test, Parallelizable]
        public void Resume_DoesNotReseedInitialPan() {
            Song song = TwoChannelSong(4, (0, 0, Note(60)), (0, 1, Note(64)));
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(song, synth, OnePatchBank());

            seq.Play();
            Pull(seq, 100);
            seq.Stop();
            seq.Play();
            Pull(seq, 100);

            Assert.That(synth.CallLog.Count(c => c == "SetChannelPan(0,-0.5)"), Is.EqualTo(1),
                "Play() alone (no SeekTo) resumes without re-seeding, preserving any pan a SetPan effect already moved.");
        }

        [Test, Parallelizable]
        public void SetPanEffect_MovesPan_AndNoteStillSounds() {
            Cell panCell = new Cell { Note = TrackerNotes.FromKey(64), Instrument = 1, Effect = TrackerEffectCommand.SetPan, EffectParam = 100 };
            Song song = OneChannelSong(2, (0, Note(60)), (1, panCell));
            CallLoggingSynthesizer synth = Logging();
            TrackerSequencer seq = new TrackerSequencer(song, synth, OnePatchBank());

            seq.Play();
            Pull(seq, SamplesPerRow * 2);

            List<string> controls = synth.CallLog.Where(c => !c.StartsWith("Read", StringComparison.Ordinal)).ToList();
            Assert.That(controls, Does.Contain("NoteOn(0,64,127)"), "the cell's note still plays alongside the pan effect.");
            string expectedPan = FormattableString.Invariant($"SetChannelPan(0,{TrackerPan.ToSignedPan(100)})");
            Assert.That(controls, Does.Contain(expectedPan), "the SetPan effect moves the channel's pan.");
            Assert.That(controls.Count(c => c.StartsWith("SetChannelPan(0,", StringComparison.Ordinal)), Is.EqualTo(2),
                "pan is a discrete row-enter control: the initial seed plus exactly one SetPan call, no per-tick repeats.");
        }

        // ---- Offline path (TrackerTimelineImporter) ----

        [Test, Parallelizable]
        public void Import_Rejects_ChannelPanLengthMismatch() {
            Song song = TwoChannelSong(1, (0, 0, Note(60)));
            song.ChannelPan = new byte[1];

            Assert.That(() => TrackerTimelineImporter.Import(song, SampleRate), Throws.ArgumentException);
        }

        [Test, Parallelizable]
        public void Import_UnsetChannelPan_SeedsDefaultLayout_AtOffsetZero() {
            Song song = TwoChannelSong(1, (0, 0, Note(60)), (0, 1, Note(64)));

            List<TimelineEntry> panAtZero = Entries(song).Where(e => e.Event.Kind == NeutralEventKind.SetPan && e.SampleOffset == 0).ToList();

            Assert.That(panAtZero.Single(e => e.Event.Channel == 0).Event.Value, Is.EqualTo(-0.5f).Within(1e-6f));
            Assert.That(panAtZero.Single(e => e.Event.Channel == 1).Event.Value, Is.EqualTo(0.5f).Within(1e-6f));
        }

        [Test, Parallelizable]
        public void Import_ProvidedChannelPan_SeedsExactValues_AtOffsetZero() {
            Song song = TwoChannelSong(1, (0, 0, Note(60)), (0, 1, Note(64)));
            song.ChannelPan = new byte[] { 0, 128 };

            List<TimelineEntry> panAtZero = Entries(song).Where(e => e.Event.Kind == NeutralEventKind.SetPan && e.SampleOffset == 0).ToList();

            Assert.That(panAtZero.Single(e => e.Event.Channel == 0).Event.Value, Is.EqualTo(-1f));
            Assert.That(panAtZero.Single(e => e.Event.Channel == 1).Event.Value, Is.EqualTo(1f));
        }

        [Test, Parallelizable]
        [Description("Regression guard: a mono song's unset ChannelPan seeds dead-centre, matching the pre-panning hardcoded 0f seed.")]
        public void Import_MonoSong_UnsetChannelPan_SeedsCentre() {
            Song song = OneChannelSong(1, (0, Note(60)));

            TimelineEntry pan = Entries(song).Single(e => e.Event.Kind == NeutralEventKind.SetPan && e.SampleOffset == 0);

            Assert.That(pan.Event.Value, Is.EqualTo(0f));
        }

        [Test, Parallelizable]
        [Description("A SetPan cell emits a NeutralEvent.SetPan at its row's sample offset, decoded via TrackerPan.")]
        public void Import_SetPanEffect_EmitsNeutralEventAtRowOffset() {
            Cell panCell = new Cell { Effect = TrackerEffectCommand.SetPan, EffectParam = 100 };
            Song song = OneChannelSong(2, (0, Note(60)), (1, panCell));

            List<TimelineEntry> panEvents = Entries(song).Where(e => e.Event.Kind == NeutralEventKind.SetPan).ToList();
            TimelineEntry rowOnePan = panEvents.Single(e => e.SampleOffset > 0);

            Assert.That(rowOnePan.SampleOffset, Is.EqualTo(SamplesPerRow));
            Assert.That(rowOnePan.Event.Value, Is.EqualTo(TrackerPan.ToSignedPan(100)).Within(1e-6f));
        }

        [Test, Parallelizable]
        [Description("An unknown effect command must not be mistaken for SetPan and must not emit a pan event.")]
        public void Import_UnknownEffect_EmitsNoExtraPanEvent() {
            Cell unknown = new Cell { Note = TrackerNotes.FromKey(60), Instrument = 1, Effect = (TrackerEffectCommand)200, EffectParam = 100 };
            Song song = OneChannelSong(1, (0, unknown));

            Assert.That(Entries(song).Count(e => e.Event.Kind == NeutralEventKind.SetPan), Is.EqualTo(1),
                "only the offset-0 seed; the unknown effect is not interpreted as SetPan.");
        }

        // ---- Parity proof (design R3): live == offline ----

        [Test, Parallelizable]
        [Description("A song with an initial default-layout pan plus a mid-song SetPan produces an identical pan " +
                     "value sequence on both playback paths, since both resolve through TrackerPan.")]
        public void InitialAndMidSongPan_ProduceEquivalentValues_OnBothPaths() {
            Song song = TwoChannelSong(2, (0, 0, Note(60)), (0, 1, Note(64)),
                (1, 0, new Cell { Effect = TrackerEffectCommand.SetPan, EffectParam = 20 }));

            CallLoggingSynthesizer liveSynth = Logging();
            TrackerSequencer seq = new TrackerSequencer(song, liveSynth, OnePatchBank());
            seq.Play();
            Pull(seq, SamplesPerRow * 2);
            List<float> livePanChannel0 = liveSynth.CallLog
                .Where(c => c.StartsWith("SetChannelPan(0,", StringComparison.Ordinal))
                .Select(ParsePanValue)
                .ToList();

            List<float> offlinePanChannel0 = Entries(song)
                .Where(e => e.Event.Kind == NeutralEventKind.SetPan && e.Event.Channel == 0)
                .OrderBy(e => e.SampleOffset)
                .Select(e => e.Event.Value)
                .ToList();

            Assert.That(livePanChannel0, Is.EqualTo(offlinePanChannel0),
                "channel 0's pan sequence (initial default layout, then the mid-song SetPan) must match exactly on both paths.");
        }

        [Test, Parallelizable]
        [Description("A panned song renders non-silent, bounded audio through both the live TrackerSequencer and " +
                     "the offline RealtimeSequencer path, with no Synthesizer/DSP change involved.")]
        public void PannedSong_RendersNonSilentBoundedAudio_OnBothPaths() {
            float[] dc = new float[8192];
            for (int i = 0; i < dc.Length; i++)
                dc[i] = 0.4f;
            SampleRegion region = new SampleRegion(dc, 0, dc.Length, 0, dc.Length, LoopMode.Continuous,
                SampleRate, 60, 0, EnvelopeParameters.Default, FilterParameters.Default, LfoParameters.Default, 0f);
            SoundBank bank = new SoundBank(new[] { (0, 0, "", (IPatch)new SamplePatch(region, SampleRate)) });
            Song song = TwoChannelSong(2, (0, 0, Note(60)), (0, 1, Note(64)));

            SynthesizerOptions liveOptions = new SynthesizerOptions(SampleRate, 1, 64, 8);
            Synthesizer liveSynth = new Synthesizer(liveOptions, bank.GetPatch(0, 0));
            TrackerSequencer seq = new TrackerSequencer(song, liveSynth, bank);
            seq.Play();
            AssertRendersNonSilentBoundedAudio(seq);

            Timeline timeline = TrackerTimelineImporter.Import(song, SampleRate);
            SynthesizerOptions offlineOptions = new SynthesizerOptions(SampleRate, 1, 64, 8);
            Synthesizer offlineSynth = new Synthesizer(offlineOptions, bank.GetPatch(0, 0));
            RealtimeSequencer driver = new RealtimeSequencer(timeline.Compile(), offlineSynth, bank, releaseTailFrames: 4410);
            AssertRendersNonSilentBoundedAudio(driver);
        }

        static void AssertRendersNonSilentBoundedAudio(IAudioSource source) {
            float[] block = new float[64];
            float peak = 0f;
            bool bounded = true;
            int totalFrames = 0;
            int produced;
            do {
                produced = source.Read(block);
                for (int i = 0; i < produced; i++) {
                    peak = Math.Max(peak, Math.Abs(block[i]));
                    if (Math.Abs(block[i]) > 1f)
                        bounded = false;
                }
                totalFrames += produced;
            } while (produced == block.Length && totalFrames < 80000);

            Assert.That(bounded, Is.True, "all rendered samples must be within [-1, 1].");
            Assert.That(peak, Is.GreaterThan(0.01f), "a panned tracker song must not render silent.");
        }
    }
}
