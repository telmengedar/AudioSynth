using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Pooshit.AudioSynth.Synthesis;
using Pooshit.AudioSynth.Tests.Helpers;

namespace Pooshit.AudioSynth.Tests {

    /// <summary>
    /// <see cref="SoundBank.GetPatch"/> fallback-chain tests (DiVoid #7117 §8, extended by MIDI Bank
    /// Select design #7251 §8.2): exact match, bank-0 same program (the bank-select regression guard),
    /// same-bank lowest-present program, melodic default, percussion default, and the absolute fallback.
    /// </summary>
    [TestFixture]
    public class SoundBankTests {

        [Test]
        [Description("An exact (bank, program) match returns that patch.")]
        public void GetPatch_ExactMatch_ReturnsThatPatch() {
            StubPatch piano = new StubPatch("piano");
            StubPatch bass = new StubPatch("bass");
            SoundBank bank = new SoundBank(new[] {
                (0, 0, "piano", (IPatch)piano),
                (0, 33, "bass", (IPatch)bass),
            });

            Assert.That(bank.GetPatch(0, 33), Is.SameAs(bass));
        }

        [Test]
        [Description("A missing program in a present bank falls back to that bank's lowest-numbered present program.")]
        public void GetPatch_MissingProgramInPresentBank_ReturnsLowestPresentProgram() {
            StubPatch low = new StubPatch("low");
            StubPatch high = new StubPatch("high");
            SoundBank bank = new SoundBank(new[] {
                (0, 10, "high", (IPatch)high),
                (0, 3, "low", (IPatch)low),
            });

            Assert.That(bank.GetPatch(0, 5), Is.SameAs(low),
                "Program 5 is absent; the lowest present program (3) in the same bank must be chosen over program 10.");
        }

        [Test]
        [Description("A variation bank absent entirely, with the requested program also absent from bank 0, " +
                     "falls back to bank 0 / program 0 (GM piano). Renamed per OQ-2 (design #7251 §13): now " +
                     "that rung 2 (bank-0 same program) exists, this test's name must reflect that it exercises " +
                     "the case where rung 2 ALSO misses, not merely 'bank absent'.")]
        public void GetPatch_BankAbsentAndGmProgramAbsent_FallsBackToBankZeroProgramZero() {
            StubPatch piano = new StubPatch("piano");
            SoundBank bank = new SoundBank(new[] {
                (0, 0, "piano", (IPatch)piano),
            });

            Assert.That(bank.GetPatch(5, 12), Is.SameAs(piano),
                "Bank 5 does not exist and bank 0/program 12 does not exist either; the melodic default " +
                "(bank 0/program 0) must be used.");
        }

        [Test]
        [Description("Rung 2 (regression guard, design #7251 §8.2): a variation bank absent entirely, but " +
                     "the requested program present in bank 0, returns the bank-0 patch for that program " +
                     "rather than degrading all the way to the melodic default (0,0).")]
        public void GetPatch_VariationBankAbsentButGmProgramPresent_ReturnsBankZeroSameProgram() {
            StubPatch piano = new StubPatch("piano");
            StubPatch viola = new StubPatch("viola");
            SoundBank bank = new SoundBank(new[] {
                (0, 0, "piano", (IPatch)piano),
                (0, 40, "viola", (IPatch)viola),
            });

            Assert.That(bank.GetPatch(8, 40), Is.SameAs(viola),
                "Bank 8 does not exist; rung 2 must return the same program (40) from bank 0 (viola), " +
                "not the melodic default (piano) — this is the GM-only-font regression guard.");
        }

        [Test]
        [Description("Rung 2 beats rung 3 (design #7251 §8.2): when the variation bank IS present but lacks " +
                     "the requested program, and bank 0 has that program, rung 2 (bank-0 same program) must " +
                     "win over rung 3 (same-bank lowest present program) — the same instrument beats an " +
                     "unrelated substitute from the requested bank.")]
        public void GetPatch_VariationBankPresentButProgramAbsent_Rung2BeatsSameBankLowest() {
            StubPatch bankEightLow = new StubPatch("bank8-lowest");
            StubPatch viola = new StubPatch("viola");
            SoundBank bank = new SoundBank(new[] {
                (8, 3, "bank8-lowest", (IPatch)bankEightLow),
                (0, 40, "viola", (IPatch)viola),
            });

            Assert.That(bank.GetPatch(8, 40), Is.SameAs(viola),
                "Bank 8 exists but lacks program 40; rung 2 (bank 0, program 40 = viola) must be preferred " +
                "over rung 3 (bank 8's lowest present program).");
        }

        [Test]
        [Description("Rung 2 is guarded off for bank 0 requests: a missing program in bank 0 itself must " +
                     "still fall to the same-bank lowest present program (rung 3), not loop back into rung 2.")]
        public void GetPatch_BankZeroRequestMissingProgram_Rung2GuardedOff_UsesSameBankLowest() {
            StubPatch low = new StubPatch("low");
            StubPatch high = new StubPatch("high");
            SoundBank bank = new SoundBank(new[] {
                (0, 10, "high", (IPatch)high),
                (0, 3, "low", (IPatch)low),
            });

            Assert.That(bank.GetPatch(0, 99), Is.SameAs(low),
                "Bank-0 requests must be unaffected by rung 2 (it is redundant with rung 1 there); " +
                "the existing same-bank-lowest fallback (rung 3) must still apply.");
        }

        [Test]
        [Description("Rung 2 is guarded off for percussion (bank 128) requests: a missing program in bank " +
                     "128, even when bank 0 holds that same program number, must never degrade to a melodic " +
                     "instrument — it must resolve within bank 128 or the deeper percussion/absolute fallback.")]
        public void GetPatch_PercussionRequestMissingProgram_Rung2GuardedOff_NeverReturnsMelodicPatch() {
            StubPatch piano = new StubPatch("piano");
            StubPatch kit = new StubPatch("kit");
            SoundBank bank = new SoundBank(new[] {
                (0, 57, "piano", (IPatch)piano),
                (128, 0, "kit", (IPatch)kit),
            });

            Assert.That(bank.GetPatch(128, 57), Is.SameAs(kit),
                "Bank 128 lacks program 57, but bank 0/program 57 (piano) exists; rung 2 must be guarded " +
                "off for percussion requests, so the result must stay within bank 128 (the kit), never piano.");
        }

        [Test]
        [Description("Percussion bank 128 absent entirely, with no melodic default present, falls back to the first loaded patch.")]
        public void GetPatch_PercussionBankAbsentAndNoMelodicDefault_FallsBackToFirstPatch() {
            StubPatch first = new StubPatch("first");
            StubPatch second = new StubPatch("second");
            SoundBank bank = new SoundBank(new[] {
                (3, 7, "first", (IPatch)first),
                (3, 9, "second", (IPatch)second),
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
                (0, 0, "piano", (IPatch)piano),
                (128, 0, "kit", (IPatch)kit),
            });

            Assert.That(bank.GetPatch(128, 57), Is.SameAs(kit),
                "A percussion-channel request must resolve within bank 128, not degrade to the melodic default.");
        }

        [Test]
        [Description("GetPatch throws a clear exception when the bank holds no patches at all.")]
        public void GetPatch_EmptyBank_Throws() {
            SoundBank bank = new SoundBank(Array.Empty<(int, int, string, IPatch)>());

            Assert.Throws<InvalidOperationException>(() => bank.GetPatch(0, 0));
        }

        [Test]
        [Description("Patches and Count reflect every loaded entry.")]
        public void PatchesAndCount_ReflectLoadedEntries() {
            StubPatch a = new StubPatch("a");
            StubPatch b = new StubPatch("b");
            SoundBank bank = new SoundBank(new[] {
                (0, 0, "a", (IPatch)a),
                (0, 1, "b", (IPatch)b),
            });

            Assert.That(bank.Count, Is.EqualTo(2));
            Assert.That(bank.Patches, Has.Count.EqualTo(2));
        }

        [Test]
        [Description("Constructing a SoundBank from a null entry collection throws ArgumentNullException.")]
        public void Constructor_NullEntries_Throws() {
            Assert.Throws<ArgumentNullException>(() => new SoundBank(null!));
        }

        [Test]
        [Description("Constructing a SoundBank with a null entry name throws ArgumentException, matching the null-patch guard.")]
        public void Constructor_NullName_Throws() {
            StubPatch piano = new StubPatch("piano");

            Assert.Throws<ArgumentException>(() => new SoundBank(new[] {
                (0, 0, (string)null!, (IPatch)piano),
            }));
        }

        [Test]
        [Description("AvailablePatches returns one PatchInfo per loaded slot with the correct bank, program and name.")]
        public void AvailablePatches_ReturnsOnePatchInfoPerLoadedSlot() {
            StubPatch piano = new StubPatch("piano");
            StubPatch bass = new StubPatch("bass");
            SoundBank bank = new SoundBank(new[] {
                (0, 0, "piano", (IPatch)piano),
                (0, 33, "bass", (IPatch)bass),
            });

            Assert.That(bank.AvailablePatches, Has.Count.EqualTo(2));
            Assert.That(bank.AvailablePatches[0].Bank, Is.EqualTo(0));
            Assert.That(bank.AvailablePatches[0].Program, Is.EqualTo(0));
            Assert.That(bank.AvailablePatches[0].Name, Is.EqualTo("piano"));
            Assert.That(bank.AvailablePatches[1].Program, Is.EqualTo(33));
            Assert.That(bank.AvailablePatches[1].Name, Is.EqualTo("bass"));
        }

        [Test]
        [Description("AvailablePatches is ordered (bank, program) ascending across multiple banks, independent of load order.")]
        public void AvailablePatches_OrderedByBankThenProgramAscending() {
            StubPatch a = new StubPatch("a");
            StubPatch b = new StubPatch("b");
            StubPatch c = new StubPatch("c");
            SoundBank bank = new SoundBank(new[] {
                (1, 5, "c", (IPatch)c),
                (0, 10, "b", (IPatch)b),
                (0, 2, "a", (IPatch)a),
            });

            Assert.That(bank.AvailablePatches.Select(p => (p.Bank, p.Program)),
                Is.EqualTo(new[] { (0, 2), (0, 10), (1, 5) }));
        }

        [Test]
        [Description("A duplicate (bank, program) across two entries appears once in AvailablePatches, carrying the last-written name, matching GetPatch's last-write-wins resolution.")]
        public void AvailablePatches_DuplicateBankProgram_ListedOnceWithLastWrittenName() {
            StubPatch first = new StubPatch("first");
            StubPatch second = new StubPatch("second");
            SoundBank bank = new SoundBank(new[] {
                (0, 0, "first", (IPatch)first),
                (0, 0, "second", (IPatch)second),
            });

            Assert.That(bank.AvailablePatches, Has.Count.EqualTo(1));
            Assert.That(bank.AvailablePatches[0].Name, Is.EqualTo("second"));
            Assert.That(bank.GetPatch(0, 0), Is.SameAs(second));
        }

        [Test]
        [Description("Invariant: for every PatchInfo in AvailablePatches, GetPatch resolves it by exact match to the patch whose name matches (the no-drift invariant).")]
        public void AvailablePatches_EveryEntry_ResolvesByExactMatchToMatchingName() {
            StubPatch piano = new StubPatch("piano");
            StubPatch kit = new StubPatch("kit");
            StubPatch viola = new StubPatch("viola");
            SoundBank bank = new SoundBank(new[] {
                (0, 0, "piano", (IPatch)piano),
                (128, 0, "kit", (IPatch)kit),
                (8, 40, "viola", (IPatch)viola),
            });

            foreach (PatchInfo info in bank.AvailablePatches) {
                IPatch resolved = bank.GetPatch(info.Bank, info.Program);
                Assert.That(resolved, Is.SameAs(info.Bank switch {
                    0 => piano,
                    128 => kit,
                    _ => viola,
                }), $"PatchInfo {info} must resolve by exact match to the patch it was built from.");
            }
        }

        [Test]
        [Description("AvailablePatches on an empty bank returns an empty list without throwing.")]
        public void AvailablePatches_EmptyBank_ReturnsEmptyList() {
            SoundBank bank = new SoundBank(Array.Empty<(int, int, string, IPatch)>());

            Assert.That(bank.AvailablePatches, Is.Empty);
        }
    }
}
