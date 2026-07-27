using System;
using System.IO;
using NUnit.Framework;
using Pooshit.AudioSynth.Formats.Sf2;
using Pooshit.AudioSynth.Synthesis;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// First-audio milestone tests for PR 4: <see cref="Sf2Patch.StartVoice"/> produces audible,
    /// amplitude-bounded output through a <see cref="Synthesizer"/>.
    /// Contains a synthetic always-green proof (D6) and an optional real-Florestan integration test
    /// that gracefully skips when the reference file is absent.
    /// </summary>
    [TestFixture]
    public class Sf2FirstAudioTests {

        static string? FindFlorestanPath() {
            string? dir = Path.GetDirectoryName(typeof(Sf2FirstAudioTests).Assembly.Location);
            while (dir != null) {
                string candidate = Path.Combine(dir,
                    "Source", "AudioSynthesis.Tests", "Soundfonts",
                    "__Florestan_Basic_GM_GS.sf2");
                if (File.Exists(candidate))
                    return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        static float PeakAmplitude(float[] samples) {
            float peak = 0f;
            foreach (float s in samples)
                peak = Math.Max(peak, Math.Abs(s));
            return peak;
        }

        static bool AllBounded(float[] samples) {
            foreach (float s in samples) {
                if (Math.Abs(s) > 1f)
                    return false;
            }
            return true;
        }

        [Test]
        [Description("D6 synthetic first-audio proof (always green): a full-scale looping sample " +
                     "resolves through Sf2Patch.StartVoice and produces non-silent bounded output.")]
        public void SyntheticFirstAudio_FullScaleSample_ProducesNonSilentBoundedOutput() {
            byte[] sf2 = Sf2TestBuilder.BuildWithResolvablePreset(sampleModes: 1);
            SoundBank bank = new Sf2SoundBankLoader(44100).Load(new MemoryStream(sf2));

            Assert.That(bank.Count, Is.GreaterThan(0), "Resolvable SF2 must produce at least one patch.");

            SynthesizerOptions opts = new SynthesizerOptions(44100, 2, 64, 16);
            Synthesizer synth = new Synthesizer(opts, bank.Patches[0]);

            synth.NoteOn(0, 60, 100);

            int renderSamples = 4096 * 2;
            float[] output = new float[renderSamples];
            synth.Read(output.AsSpan());

            float peak = PeakAmplitude(output);
            Assert.That(peak, Is.GreaterThan(0.05f),
                $"Rendered output was silent (peak={peak}); Sf2Patch.StartVoice is not wired correctly.");
            Assert.That(AllBounded(output), Is.True,
                "All rendered samples must be within [-1, 1].");
        }

        [Test]
        [Description("Sf2Patch.StartVoice returns a live voice (IsActive=true) for a resolvable zone.")]
        public void SyntheticFirstAudio_StartVoice_ReturnsActiveVoice() {
            byte[] sf2 = Sf2TestBuilder.BuildWithResolvablePreset(sampleModes: 1);
            SoundBank bank = new Sf2SoundBankLoader(44100).Load(new MemoryStream(sf2));

            IVoice voice = bank.Patches[0].StartVoice(60, 100);

            Assert.That(voice.IsActive, Is.True,
                "StartVoice for a fully resolvable zone must return an active voice.");
        }

        [Test]
        [Description("Repeated NoteOn on the same key reuses the cached zone (no exception, still active).")]
        public void SyntheticFirstAudio_RepeatedNoteOn_UsesCachedZone() {
            byte[] sf2 = Sf2TestBuilder.BuildWithResolvablePreset(sampleModes: 1);
            SoundBank bank = new Sf2SoundBankLoader(44100).Load(new MemoryStream(sf2));
            IPatch patch = bank.Patches[0];

            IVoice v1 = patch.StartVoice(60, 100);
            IVoice v2 = patch.StartVoice(60, 80);

            Assert.That(v1.IsActive, Is.True);
            Assert.That(v2.IsActive, Is.True,
                "Second StartVoice call via cached zone must also return an active voice.");
        }

        [Test]
        [Description("Real Florestan GM integration: NoteOn(0,60,100) through a real SF2 is non-silent and bounded. " +
                     "Skipped gracefully when __Florestan_Basic_GM_GS.sf2 is absent from the dev tree.")]
        public void FlorestanIntegration_MiddleC_IsNonSilentAndBounded() {
            string? path = FindFlorestanPath();
            if (path is null)
                Assert.Ignore("__Florestan_Basic_GM_GS.sf2 not found in the local source tree; " +
                              "skipping real-file integration test. The synthetic proof above is always-green.");

            SoundBank bank;
            using (FileStream fs = File.OpenRead(path!)) {
                bank = new Sf2SoundBankLoader(44100).Load(fs);
            }

            Assert.That(bank.Count, Is.GreaterThan(0), "Florestan must contain at least one preset.");

            SynthesizerOptions opts = new SynthesizerOptions(44100, 2, 64, 16);
            Synthesizer synth = new Synthesizer(opts, bank.Patches[0]);

            synth.NoteOn(0, 60, 100);

            int renderSamples = 5000 * 2;
            float[] output = new float[renderSamples];
            synth.Read(output.AsSpan());

            float peak = PeakAmplitude(output);
            Assert.That(peak, Is.GreaterThan(0.01f),
                $"Florestan NoteOn(0,60,100) rendered silence (peak={peak}); wiring broken for real SF2.");
            Assert.That(AllBounded(output), Is.True,
                "All Florestan rendered samples must be within [-1, 1].");
        }
    }
}
