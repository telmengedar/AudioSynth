using System.IO;
using NUnit.Framework;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Formats.Midi;
using Pooshit.AudioSynth.Formats.Sf2;
using Pooshit.AudioSynth.Sequencing;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Tests.Helpers;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// Deliverable-proof tests for MIDI Bank Select (design #7251, task #7252): a real GS soundfont
    /// (Omega GM/GS2) reaches a non-bank-0 variation preset when a channel sends CC0 then ProgramChange,
    /// and a real GM-only soundfont (Florestan) is byte-identical to pre-bank-select behavior when it
    /// cannot honor the requested bank. Both use <see cref="RecordingSynthesizer"/> (no audio synthesis)
    /// to assert patch identity directly, per design §14 item 8. Skip gracefully when the large external
    /// demo soundfonts or dev-tree assets are absent (they are not committed to the repo).
    /// </summary>
    [TestFixture]
    public class MidiSequencerBankSelectRenderProofTests {

        /// <summary>The demo GS soundfont referenced by design #7251 (not part of the repo).</summary>
        const string OmegaGsSoundfontPath = @"C:\dev\claude\OmegaGMGS2.sf2";

        static readonly AudioFormat Format = new AudioFormat(44100, 2);

        static string? FindDevTreeAsset(string subfolder, string fileName) {
            string? dir = Path.GetDirectoryName(typeof(MidiSequencerBankSelectRenderProofTests).Assembly.Location);
            while (dir != null) {
                string candidate = Path.Combine(dir, "Source", "AudioSynthesis.Tests", subfolder, fileName);
                if (File.Exists(candidate))
                    return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        static SoundBank LoadSoundBank(string soundfontPath) {
            using FileStream stream = File.OpenRead(soundfontPath);
            return new Sf2SoundBankLoader(Format.SampleRate).Load(stream);
        }

        static TimedMessageSequence BuildBankSelectThenProgramChange(byte channel, byte bankMsb, byte program) {
            byte[] chunk = new MidiTrackEventBuilder()
                .Controller(0, channel, 0, bankMsb)
                .ProgramChange(0, channel, program)
                .EndOfTrack()
                .BuildChunk();
            MidiFile file = MidiFile.Read(new MemoryStream(MidiTestBuilder.BuildFile(480, new[] { chunk })));
            return new TimedMessageSequence(file);
        }

        [Test]
        [Description("Success criterion 1 (design #7251): against the real Omega GS soundfont, a channel " +
                     "sending CC0=8 then ProgramChange must resolve to a bank-8 preset, not the bank-0 GM " +
                     "preset for the same program — proving bank-select reaches genuinely different " +
                     "instruments end-to-end through MidiSequencer.Render. Skips if the (uncommitted, " +
                     "~278MB) Omega GS soundfont is not present on this machine.")]
        public void Render_BankSelect8ThroughOmegaGs_ResolvesBankEightPresetNotBankZero() {
            if (!File.Exists(OmegaGsSoundfontPath)) {
                Assert.Ignore($"Omega GS soundfont not found at '{OmegaGsSoundfontPath}'; skipping GS bank-select render proof.");
                return;
            }

            SoundBank bank = LoadSoundBank(OmegaGsSoundfontPath);
            Assert.That(bank.Count, Is.GreaterThan(0), "Omega GS must contain at least one preset.");

            const byte channel = 0;
            const byte program = 25; // Steel-string guitar family; present with GS bank-8 variations in Omega GS.
            IPatch bankZeroExpected = bank.GetPatch(0, program);

            TimedMessageSequence sequence = BuildBankSelectThenProgramChange(channel, bankMsb: 8, program: program);
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), bank);

            (int Channel, IPatch Patch) programChangeCall = synth.ChannelPatchCalls[16];
            Assert.That(programChangeCall.Channel, Is.EqualTo(channel));

            Sf2Patch resolved = (Sf2Patch)programChangeCall.Patch;
            TestContext.WriteLine($"CC0=8, ProgramChange({program}) resolved to preset " +
                                  $"'{resolved.Preset.Name}' (bank {resolved.Preset.BankNumber}, program {resolved.Preset.PatchNumber}).");

            Assert.That(programChangeCall.Patch, Is.Not.SameAs(bankZeroExpected),
                "CC0=8 must select a different preset than bank 0's program 25 — proving bank-select " +
                "actually changed which instrument was chosen.");
            Assert.That(resolved.Preset.BankNumber, Is.EqualTo(8),
                "Omega GS is expected to expose a GS variation bank at wBank 8 for this program; if this " +
                "fails, GetPatch fell back past the exact match (check the fallback ladder or the program " +
                "number's availability in bank 8 of the loaded font).");
        }

        [Test]
        [Description("Regression guarantee (design #7251 §10.1): rendering the existing Florestan GM " +
                     "soundfont with a bank-select it cannot honor must resolve identically to " +
                     "GetPatch(0, program) — the pre-bank-select behavior — never a different instrument. " +
                     "Note: Florestan carries a partial GS layer (banks 0-9/16/24/32/128 per inspection), " +
                     "so this deliberately selects a bank number (100) confirmed absent from the loaded " +
                     "font, to genuinely exercise the 'can't honor this bank at all' fallback path rather " +
                     "than accidentally landing on a bank Florestan actually implements. Skips gracefully " +
                     "when the dev-tree Florestan asset is absent.")]
        public void Render_BankSelectUnhonorableThroughFlorestan_IsByteIdenticalToPreChangeBehavior() {
            string? soundfontPath = FindDevTreeAsset("Soundfonts", "__Florestan_Basic_GM_GS.sf2");
            if (soundfontPath is null) {
                Assert.Ignore("Florestan GM SoundFont not found in the dev tree; skipping the GM-only no-regression render proof.");
                return;
            }

            SoundBank bank = LoadSoundBank(soundfontPath);
            Assert.That(bank.Count, Is.GreaterThan(0), "Florestan must contain at least one preset.");

            const byte channel = 0;
            const byte unhonorableBank = 100; // Confirmed absent from Florestan's loaded bank set.
            const byte program = 40; // Violin in GM; present at bank 0 in Florestan.
            IPatch expected = bank.GetPatch(0, program);

            TimedMessageSequence sequence = BuildBankSelectThenProgramChange(channel, bankMsb: unhonorableBank, program: program);
            RecordingSynthesizer synth = new RecordingSynthesizer(Format);

            MidiSequencer.Render(sequence, synth, new InMemoryAudioSink(Format), bank);

            (int Channel, IPatch Patch) programChangeCall = synth.ChannelPatchCalls[16];
            Assert.That(programChangeCall.Patch, Is.SameAs(expected),
                "Florestan has no bank 100; CC0=100 followed by ProgramChange(40) must still resolve to " +
                "exactly GetPatch(0, 40) via the SoundBank rung-2 regression guard — byte-identical to how " +
                "this song rendered before bank-select existed.");
        }
    }
}
