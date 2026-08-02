using System;
using System.Collections.Generic;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// Concrete (bank, program) → <see cref="IPatch"/> lookup with a never-null, never-throws
    /// fallback chain for a non-empty bank (DiVoid #7117 §8). Format- and MIDI-neutral.
    /// </summary>
    public sealed class SoundBank {

        const int MelodicBank = 0;
        const int PercussionBank = 128;
        const int DefaultProgram = 0;

        readonly IPatch[] patches;
        readonly Dictionary<int, SortedDictionary<int, (string Name, IPatch Patch)>> byBank;
        readonly PatchInfo[] availablePatches;

        /// <summary>
        /// Builds a <see cref="SoundBank"/> from an ordered collection of (bank, program, name, patch) entries.
        /// </summary>
        public SoundBank(IEnumerable<(int Bank, int Program, string Name, IPatch Patch)> entries) {
            if (entries is null) throw new ArgumentNullException(nameof(entries));

            List<IPatch> all = new List<IPatch>();
            byBank = new Dictionary<int, SortedDictionary<int, (string Name, IPatch Patch)>>();
            foreach ((int bank, int program, string name, IPatch patch) in entries) {
                if (patch is null)
                    throw new ArgumentException("Entry patch must not be null.", nameof(entries));
                if (name is null)
                    throw new ArgumentException("Entry name must not be null.", nameof(entries));

                all.Add(patch);
                if (!byBank.TryGetValue(bank, out SortedDictionary<int, (string Name, IPatch Patch)>? programs)) {
                    programs = new SortedDictionary<int, (string Name, IPatch Patch)>();
                    byBank[bank] = programs;
                }
                programs[program] = (name, patch);
            }
            patches = all.ToArray();
            availablePatches = BuildAvailablePatches(byBank);
        }

        static PatchInfo[] BuildAvailablePatches(Dictionary<int, SortedDictionary<int, (string Name, IPatch Patch)>> byBank) {
            List<PatchInfo> result = new List<PatchInfo>();
            foreach (int bank in new SortedSet<int>(byBank.Keys))
                foreach (KeyValuePair<int, (string Name, IPatch Patch)> entry in byBank[bank])
                    result.Add(new PatchInfo(bank, entry.Key, entry.Value.Name));
            return result.ToArray();
        }

        /// <summary>
        /// Every patch loaded into this bank, in load order.
        /// </summary>
        public IReadOnlyList<IPatch> Patches => patches;

        /// <summary>
        /// Number of patches loaded into this bank.
        /// </summary>
        public int Count => patches.Length;

        /// <summary>
        /// Loadable patches as (bank, program, name), ordered bank then program ascending; exactly the
        /// set <see cref="GetPatch"/> resolves by exact match.
        /// </summary>
        public IReadOnlyList<PatchInfo> AvailablePatches => availablePatches;

        /// <summary>
        /// Resolves (bank, program) via the fallback chain: exact match, bank-0 same program (variation
        /// banks only — MIDI Bank Select regression guard, design #7251 §8.2), same-bank lowest present
        /// program, melodic default (bank 0/program 0), any percussion (bank 128) preset, then the first
        /// loaded patch. Never null; throws only when the bank holds no patches at all.
        /// </summary>
        public IPatch GetPatch(int bank, int program) {
            if (patches.Length == 0)
                throw new InvalidOperationException(
                    "SoundBank contains no patches; GetPatch requires at least one loaded preset.");

            if (TryExactMatch(bank, program, out IPatch? exact))
                return exact!;
            if (bank != MelodicBank && bank != PercussionBank && TryExactMatch(MelodicBank, program, out IPatch? bankZeroSameProgram))
                return bankZeroSameProgram!;
            if (TryLowestInBank(bank, out IPatch? lowestInBank))
                return lowestInBank!;
            if (bank != PercussionBank && TryExactMatch(MelodicBank, DefaultProgram, out IPatch? melodicDefault))
                return melodicDefault!;
            if (TryLowestInBank(PercussionBank, out IPatch? anyPercussion))
                return anyPercussion!;
            return patches[0];
        }

        bool TryExactMatch(int bank, int program, out IPatch? patch) {
            if (byBank.TryGetValue(bank, out SortedDictionary<int, (string Name, IPatch Patch)>? programs)
                && programs.TryGetValue(program, out (string Name, IPatch Patch) entry)) {
                patch = entry.Patch;
                return true;
            }
            patch = null;
            return false;
        }

        bool TryLowestInBank(int bank, out IPatch? patch) {
            if (byBank.TryGetValue(bank, out SortedDictionary<int, (string Name, IPatch Patch)>? programs)) {
                foreach (KeyValuePair<int, (string Name, IPatch Patch)> entry in programs) {
                    patch = entry.Value.Patch;
                    return true;
                }
            }
            patch = null;
            return false;
        }
    }
}
