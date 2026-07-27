using System;
using System.Collections.Generic;
using NUnit.Framework;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Tests.Helpers;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// <see cref="SoundBank.GetPatch"/> fallback-chain tests (DiVoid #7117 §8): exact match, same-bank
    /// lowest-present program, melodic default, percussion default, and the absolute fallback.
    /// </summary>
    [TestFixture]
    public class SoundBankTests {

        [Test]
        [Description("An exact (bank, program) match returns that patch.")]
        public void GetPatch_ExactMatch_ReturnsThatPatch() {
            StubPatch piano = new StubPatch("piano");
            StubPatch bass = new StubPatch("bass");
            SoundBank bank = new SoundBank(new[] {
                (0, 0, (IPatch)piano),
                (0, 33, (IPatch)bass),
            });

            Assert.That(bank.GetPatch(0, 33), Is.SameAs(bass));
        }

        [Test]
        [Description("A missing program in a present bank falls back to that bank's lowest-numbered present program.")]
        public void GetPatch_MissingProgramInPresentBank_ReturnsLowestPresentProgram() {
            StubPatch low = new StubPatch("low");
            StubPatch high = new StubPatch("high");
            SoundBank bank = new SoundBank(new[] {
                (0, 10, (IPatch)high),
                (0, 3, (IPatch)low),
            });

            Assert.That(bank.GetPatch(0, 5), Is.SameAs(low),
                "Program 5 is absent; the lowest present program (3) in the same bank must be chosen over program 10.");
        }

        [Test]
        [Description("A melodic bank absent entirely falls back to bank 0 / program 0 (GM piano).")]
        public void GetPatch_MelodicBankAbsent_FallsBackToBankZeroProgramZero() {
            StubPatch piano = new StubPatch("piano");
            SoundBank bank = new SoundBank(new[] {
                (0, 0, (IPatch)piano),
            });

            Assert.That(bank.GetPatch(5, 12), Is.SameAs(piano),
                "Bank 5 does not exist; the melodic default (bank 0/program 0) must be used.");
        }

        [Test]
        [Description("Percussion bank 128 absent entirely, with no melodic default present, falls back to the first loaded patch.")]
        public void GetPatch_PercussionBankAbsentAndNoMelodicDefault_FallsBackToFirstPatch() {
            StubPatch first = new StubPatch("first");
            StubPatch second = new StubPatch("second");
            SoundBank bank = new SoundBank(new[] {
                (3, 7, (IPatch)first),
                (3, 9, (IPatch)second),
            });

            Assert.That(bank.GetPatch(128, 0), Is.SameAs(first),
                "No bank 128 and no bank 0/program 0 exist; the absolute fallback (first loaded patch) must be used.");
        }

        [Test]
        [Description("A percussion request (bank 128) with a mismatched program returns any bank-128 preset rather than a melodic default.")]
        public void GetPatch_PercussionProgramMissing_ReturnsAnyPercussionPreset() {
            StubPatch piano = new StubPatch("piano");
            StubPatch kit = new StubPatch("kit");
            SoundBank bank = new SoundBank(new[] {
                (0, 0, (IPatch)piano),
                (128, 0, (IPatch)kit),
            });

            Assert.That(bank.GetPatch(128, 57), Is.SameAs(kit),
                "A percussion-channel request must resolve within bank 128, not degrade to the melodic default.");
        }

        [Test]
        [Description("GetPatch throws a clear exception when the bank holds no patches at all.")]
        public void GetPatch_EmptyBank_Throws() {
            SoundBank bank = new SoundBank(Array.Empty<(int, int, IPatch)>());

            Assert.Throws<InvalidOperationException>(() => bank.GetPatch(0, 0));
        }

        [Test]
        [Description("Patches and Count reflect every loaded entry.")]
        public void PatchesAndCount_ReflectLoadedEntries() {
            StubPatch a = new StubPatch("a");
            StubPatch b = new StubPatch("b");
            SoundBank bank = new SoundBank(new[] {
                (0, 0, (IPatch)a),
                (0, 1, (IPatch)b),
            });

            Assert.That(bank.Count, Is.EqualTo(2));
            Assert.That(bank.Patches, Has.Count.EqualTo(2));
        }

        [Test]
        [Description("Constructing a SoundBank from a null entry collection throws ArgumentNullException.")]
        public void Constructor_NullEntries_Throws() {
            Assert.Throws<ArgumentNullException>(() => new SoundBank(null!));
        }
    }
}
