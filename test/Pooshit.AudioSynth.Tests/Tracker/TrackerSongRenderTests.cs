using System;
using System.Collections.Generic;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Formats.Tracker;
using Pooshit.AudioSynth.Sequencing;
using Pooshit.AudioSynth.Sequencing.Timeline;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Synthesis.Patches;

namespace Pooshit.AudioSynth.Tests.Tracker {

    /// <summary>
    /// End-to-end: a trivial <see cref="Song"/> imports, compiles, and renders non-silent bounded audio
    /// through <see cref="RealtimeSequencer"/>.
    /// </summary>
    [TestFixture, Parallelizable]
    public class TrackerSongRenderTests {

        static readonly AudioFormat MonoFormat = new AudioFormat(44100, 1);

        static SampleRegion SustainedDcRegion(float value, int length) {
            float[] buffer = new float[length];
            for (int i = 0; i < length; i++)
                buffer[i] = value;
            return new SampleRegion(buffer, 0, length, 0, length, LoopMode.Continuous, MonoFormat.SampleRate, 60, 0,
                EnvelopeParameters.Default, FilterParameters.Default, LfoParameters.Default, 0f);
        }

        static Song TrivialSong() {
            Cell[] grid = new Cell[4];
            grid[0] = new Cell { Note = TrackerNotes.FromKey(60), Instrument = 1, Volume = 64 };
            grid[2] = new Cell { Note = TrackerNotes.FromKey(64), Instrument = 1 };
            return new Song {
                Title = "trivial",
                DefaultBpm = 125,
                DefaultSpeed = 6,
                DefaultRows = 4,
                ChannelCount = 1,
                Instruments = new[] { new Instrument { Bank = 0, Program = 0, Name = "dc" } },
                Patterns = new[] { new Pattern { Rows = 4, Cells = grid } },
                Order = new[] { 0 }
            };
        }

        [Test, Parallelizable]
        public void TrivialSong_ThroughRealtimeSequencer_RendersNonSilentBoundedAudio() {
            SoundBank bank = new SoundBank(new[] {
                (0, 0, (IPatch)new SamplePatch(SustainedDcRegion(0.4f, 8192), MonoFormat.SampleRate))
            });

            Timeline timeline = TrackerTimelineImporter.Import(TrivialSong(), MonoFormat.SampleRate);
            SynthesizerOptions options = new SynthesizerOptions(MonoFormat.SampleRate, MonoFormat.Channels, 64, 8);
            Synthesizer synth = new Synthesizer(options, bank.GetPatch(0, 0));
            RealtimeSequencer driver = new RealtimeSequencer(timeline.Compile(), synth, bank, releaseTailFrames: 4410);

            List<float> rendered = new List<float>();
            float[] block = new float[64];
            int produced;
            do {
                produced = driver.Read(block);
                for (int i = 0; i < produced; i++)
                    rendered.Add(block[i]);
            } while (produced == block.Length);

            Assert.That(rendered.Count, Is.GreaterThan(0), "the render must produce frames.");
            float peak = 0f;
            bool bounded = true;
            foreach (float s in rendered) {
                peak = Math.Max(peak, Math.Abs(s));
                if (Math.Abs(s) > 1f)
                    bounded = false;
            }

            Assert.That(bounded, Is.True, "all rendered samples must be within [-1, 1].");
            Assert.That(peak, Is.GreaterThan(0.01f), "a trivial tracker song must not render silent.");
        }
    }
}
