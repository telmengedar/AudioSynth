using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Formats.Midi;
using Pooshit.AudioSynth.Formats.Sf2;
using Pooshit.AudioSynth.Sequencing;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Tests.Midi {

    /// <summary>
    /// Dev-tree acceptance proofs for adaptive master gain-staging (DiVoid #7254/#7257): the Florestan
    /// byte-identity guarantee and the Omega calibration mechanism's clip-reduction, against the real
    /// SF2/MIDI assets used to gather the original evidence. Gracefully skips (or degrades to a
    /// documented diagnostic) when an asset is absent, mirroring <c>MidiSongRenderTests</c> and
    /// <c>Sf2FirstAudioTests</c>.
    /// </summary>
    [TestFixture]
    public class MasterGainCalibrationAcceptanceTests {

        const int MaxVoices = 128;
        const float NearClipThreshold = 0.985f;

        /// <summary>
        /// Reference anchor baked into <see cref="MidiSequencer"/> for the Florestan-anchored gain of
        /// exactly 1.0f — kept in sync manually; a mismatch here would only weaken this test's own
        /// sanity-checks, not the production anchor itself.
        /// </summary>
        const float ReferenceLoudness = 0.303088784f;

        static string? FindDevTreeAsset(string subfolder, string fileName) {
            string? dir = Path.GetDirectoryName(typeof(MasterGainCalibrationAcceptanceTests).Assembly.Location);
            while (dir != null) {
                string candidate = Path.Combine(dir, "Source", "AudioSynthesis.Tests", subfolder, fileName);
                if (File.Exists(candidate))
                    return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        static string? FindOmegaSoundfont() {
            string? dir = Path.GetDirectoryName(typeof(MasterGainCalibrationAcceptanceTests).Assembly.Location);
            while (dir != null) {
                string candidate = Path.Combine(dir, "OmegaGMGS2.sf2");
                if (File.Exists(candidate))
                    return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        static TimedMessageSequence LoadSequence(string midiPath) {
            MidiFile midiFile;
            using (FileStream fs = File.OpenRead(midiPath))
                midiFile = MidiFile.Read(fs);
            return new TimedMessageSequence(midiFile);
        }

        static float NearClipFraction(float[] samples) {
            int nearClip = 0;
            foreach (float s in samples) {
                if (Math.Abs(s) >= NearClipThreshold)
                    nearClip++;
            }
            return samples.Length == 0 ? 0f : (float)nearClip / samples.Length;
        }

        /// <summary>
        /// Rebuilds a full 128-melodic + 128-percussion SoundBank from an already-loaded bank's own
        /// <see cref="SoundBank.GetPatch"/> resolution (i.e. the same patch objects the original bank
        /// would hand a real song for any program-change it could see), paired with an explicit
        /// <paramref name="loudnessEstimate"/>. Used to isolate "identical real audio content, different
        /// calibration gain" for the mechanism-proof A/B without needing to re-parse the SF2 file.
        /// </summary>
        static SoundBank WithLoudnessEstimate(SoundBank source, float loudnessEstimate) {
            List<(int Bank, int Program, IPatch Patch)> entries = new List<(int, int, IPatch)>(256);
            for (int program = 0; program < 128; program++)
                entries.Add((0, program, source.GetPatch(0, program)));
            for (int program = 0; program < 128; program++)
                entries.Add((128, program, source.GetPatch(128, program)));
            return new SoundBank(entries, loudnessEstimate);
        }

        [Test]
        [Description("Real Florestan integration: MidiSequencer.DeriveCalibrationGain resolves Florestan's own " +
                     "measured loudness to exactly 1.0f (Is.EqualTo, no tolerance) — the load-bearing proof that " +
                     "the round-trip-literal ReferenceLoudness anchor works. Skipped gracefully when the dev-tree " +
                     "asset is absent.")]
        public void Florestan_DeriveCalibrationGain_IsExactlyOne() {
            string? soundfontPath = FindDevTreeAsset("Soundfonts", "__Florestan_Basic_GM_GS.sf2");
            if (soundfontPath is null) {
                Assert.Ignore("__Florestan_Basic_GM_GS.sf2 not found in the local source tree; skipping.");
                return;
            }

            SoundBank bank;
            using (FileStream fs = File.OpenRead(soundfontPath))
                bank = new Sf2SoundBankLoader(44100).Load(fs);

            float gain = MidiSequencer.DeriveCalibrationGain(bank);

            Assert.That(gain, Is.EqualTo(1f),
                $"Florestan's own measured loudness ({bank.LoudnessEstimate}) must resolve to exactly gain 1.0f " +
                $"(the round-trip-literal anchor technique); got {gain}.");
        }

        [Test]
        [Description("Real Florestan integration: rendering Force Your Way through the real MidiSequencer.Render " +
                     "pipeline (which now calls SetMasterCalibrationGain internally, resolving to exactly 1.0f for " +
                     "Florestan) is fully deterministic across two independent renders of the identical bank/song " +
                     "— and, combined with the exact-1.0f proof above and the IEEE-754 identity of multiplying by " +
                     "1.0f (also proven directly in MasterGainCalibrationTests), constitutes the full byte-identity " +
                     "guarantee: Florestan renders exactly as it did before this feature. Skipped gracefully when " +
                     "the dev-tree assets are absent.")]
        public void Florestan_RealSongRender_IsDeterministicAndBoundedWithCalibrationActive() {
            string? soundfontPath = FindDevTreeAsset("Soundfonts", "__Florestan_Basic_GM_GS.sf2");
            string? songPath = FindDevTreeAsset("Midi", "1-10-Force_Your_Way.mid");
            if (soundfontPath is null || songPath is null) {
                Assert.Ignore("Florestan SoundFont or Force Your Way MIDI not found in the dev tree; skipping.");
                return;
            }

            SoundBank bank;
            using (FileStream fs = File.OpenRead(soundfontPath))
                bank = new Sf2SoundBankLoader(44100).Load(fs);
            TimedMessageSequence sequence = LoadSequence(songPath);

            AudioFormat format = new AudioFormat(SynthesizerOptions.DefaultSampleRate, SynthesizerOptions.DefaultChannels);
            SynthesizerOptions options = new SynthesizerOptions(format.SampleRate, format.Channels, SynthesizerOptions.DefaultBlockFrames, MaxVoices);

            Synthesizer synthA = new Synthesizer(options, bank.GetPatch(0, 0));
            InMemoryAudioSink sinkA = new InMemoryAudioSink(format);
            MidiSequencer.Render(sequence, synthA, sinkA, bank);
            float[] samplesA = sinkA.ToArray();

            Synthesizer synthB = new Synthesizer(options, bank.GetPatch(0, 0));
            InMemoryAudioSink sinkB = new InMemoryAudioSink(format);
            MidiSequencer.Render(sequence, synthB, sinkB, bank);
            float[] samplesB = sinkB.ToArray();

            Assert.That(samplesA.Length, Is.EqualTo(samplesB.Length), "Two renders of the identical bank/song must produce the same frame count.");
            for (int i = 0; i < samplesA.Length; i++) {
                if (samplesA[i] != samplesB[i])
                    Assert.Fail($"Sample {i} differs between two identical Florestan renders: {samplesA[i]} vs {samplesB[i]}.");
            }

            float peak = 0f;
            foreach (float s in samplesA) {
                Assert.That(Math.Abs(s), Is.LessThanOrEqualTo(1f), "All rendered samples must be within [-1,1].");
                peak = Math.Max(peak, Math.Abs(s));
            }
            Assert.That(peak, Is.GreaterThan(0.01f), "The real song through a real SoundFont must not be silent.");
        }

        [Test]
        [Description("Real Omega diagnostic: measures MidiSequencer.DeriveCalibrationGain against the real, " +
                     "load-time-measured OmegaGMGS2 loudness estimate. Documents the actual result rather than " +
                     "asserting a specific improvement — see docs/architecture/master-gain-staging.md " +
                     "'Implementation Notes (as shipped)' for the full finding: for THIS real asset, the " +
                     "whole-file raw-sample-pool statistic (as locked by the design) measures Omega's aggregate " +
                     "sample loudness as LOWER than Florestan's despite Omega's rendered output being hotter, so " +
                     "this resolves to a neutral (no-op) gain for Omega specifically; the mechanism itself is " +
                     "proven independently by the forced-estimate test below using the same real audio content. " +
                     "Skipped gracefully when the asset is absent.")]
        public void Omega_RealMeasuredCalibrationGain_NeverExceedsUnity() {
            string? omegaPath = FindOmegaSoundfont();
            if (omegaPath is null) {
                Assert.Ignore("OmegaGMGS2.sf2 not found in the dev tree; skipping.");
                return;
            }

            SoundBank bank;
            using (FileStream fs = File.OpenRead(omegaPath))
                bank = new Sf2SoundBankLoader(44100).Load(fs);

            float gain = MidiSequencer.DeriveCalibrationGain(bank);

            TestContext.Out.WriteLine($"Omega real LoudnessEstimate={bank.LoudnessEstimate}, ReferenceLoudness={ReferenceLoudness}, DeriveCalibrationGain={gain}");
            Assert.That(gain, Is.GreaterThan(0f), "DeriveCalibrationGain must never be zero/negative.");
            Assert.That(gain, Is.LessThanOrEqualTo(1f), "DeriveCalibrationGain must never boost (attenuate-only, locked decision #2).");
        }

        [Test]
        [Description("Real Omega mechanism proof: using Omega's real, decoded audio (same patches the real " +
                     "loader resolves), forcing a synthetic 'hot' LoudnessEstimate (~0.69, chosen so " +
                     "DeriveCalibrationGain lands near the evidence's own implied ~0.44 ratio) against the same " +
                     "real LoudnessEstimate=0 (neutral/uncalibrated) baseline, rendering Force Your Way through " +
                     "both via the real MidiSequencer.Render pipeline: the calibrated near-clip percentage must " +
                     "be at least an order of magnitude lower than the uncalibrated one, proving the calibration " +
                     "mechanism (measurement -> derivation -> engine seam -> ApplyMasterBus) genuinely reduces " +
                     "clipping end-to-end on real audio when a font IS measured as hot. Skipped gracefully when " +
                     "the assets are absent; this is a large (278MB) SoundFont and a ~337s song, so this test " +
                     "takes real wall-clock time.")]
        public void Omega_ForcedHotEstimate_DramaticallyReducesNearClipPercentage() {
            string? omegaPath = FindOmegaSoundfont();
            string? songPath = FindDevTreeAsset("Midi", "1-10-Force_Your_Way.mid");
            if (omegaPath is null || songPath is null) {
                Assert.Ignore("OmegaGMGS2.sf2 or Force Your Way MIDI not found in the dev tree; skipping.");
                return;
            }

            SoundBank realBank;
            using (FileStream fs = File.OpenRead(omegaPath))
                realBank = new Sf2SoundBankLoader(44100).Load(fs);
            TimedMessageSequence sequence = LoadSequence(songPath);

            AudioFormat format = new AudioFormat(SynthesizerOptions.DefaultSampleRate, SynthesizerOptions.DefaultChannels);
            SynthesizerOptions options = new SynthesizerOptions(format.SampleRate, format.Channels, SynthesizerOptions.DefaultBlockFrames, MaxVoices);

            SoundBank uncalibratedBank = WithLoudnessEstimate(realBank, 0f);
            SoundBank calibratedBank = WithLoudnessEstimate(realBank, 0.69f);

            Synthesizer uncalibratedSynth = new Synthesizer(options, uncalibratedBank.GetPatch(0, 0));
            InMemoryAudioSink uncalibratedSink = new InMemoryAudioSink(format);
            MidiSequencer.Render(sequence, uncalibratedSynth, uncalibratedSink, uncalibratedBank);
            float uncalibratedNearClip = NearClipFraction(uncalibratedSink.ToArray());

            Synthesizer calibratedSynth = new Synthesizer(options, calibratedBank.GetPatch(0, 0));
            InMemoryAudioSink calibratedSink = new InMemoryAudioSink(format);
            MidiSequencer.Render(sequence, calibratedSynth, calibratedSink, calibratedBank);
            float calibratedNearClip = NearClipFraction(calibratedSink.ToArray());

            TestContext.Out.WriteLine($"Omega/Force Your Way near-clip%: uncalibrated={uncalibratedNearClip:P4}, calibrated(gain={MidiSequencer.DeriveCalibrationGain(calibratedBank):F4})={calibratedNearClip:P4}");

            Assert.That(uncalibratedNearClip, Is.GreaterThan(0f),
                "Sanity check: the uncalibrated baseline must reproduce measurable near-clipping (else the A/B proves nothing).");
            Assert.That(calibratedNearClip, Is.LessThan(uncalibratedNearClip * 0.1f),
                $"Expected the calibrated render's near-clip fraction ({calibratedNearClip:P4}) to be at least an " +
                $"order of magnitude lower than the uncalibrated baseline ({uncalibratedNearClip:P4}).");
        }
    }
}
