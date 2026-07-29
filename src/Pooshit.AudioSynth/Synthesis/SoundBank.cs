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
        readonly Dictionary<int, SortedDictionary<int, IPatch>> byBank;

        /// <summary>
        /// Builds a <see cref="SoundBank"/> from an ordered collection of (bank, program, patch) entries.
        /// <paramref name="loudnessEstimate"/> is the bank's measured inherent loudness (DiVoid #7254/#7257),
        /// a plain <c>float</c> so this format-neutral <c>Synthesis</c> type stays free of any dependency on
        /// a concrete format loader (e.g. <c>Formats.Sf2</c>); the sentinel <c>0f</c> means "unmeasured /
        /// no-op" — every bank built via callers that don't pass this argument (including every existing
        /// caller and test) defaults to it, and a wiring-layer gain-derivation helper must resolve that
        /// sentinel to a neutral (non-boosting) gain.
        /// </summary>
        public SoundBank(IEnumerable<(int Bank, int Program, IPatch Patch)> entries, float loudnessEstimate = 0f) {
            if (entries is null) throw new ArgumentNullException(nameof(entries));

            List<IPatch> all = new List<IPatch>();
            byBank = new Dictionary<int, SortedDictionary<int, IPatch>>();
            foreach ((int bank, int program, IPatch patch) in entries) {
                if (patch is null)
                    throw new ArgumentException("Entry patch must not be null.", nameof(entries));

                all.Add(patch);
                if (!byBank.TryGetValue(bank, out SortedDictionary<int, IPatch>? programs)) {
                    programs = new SortedDictionary<int, IPatch>();
                    byBank[bank] = programs;
                }
                programs[program] = patch;
            }
            patches = all.ToArray();
            LoudnessEstimate = loudnessEstimate;
        }

        /// <summary>
        /// This bank's measured inherent loudness (DiVoid #7254/#7257), or the <c>0f</c> sentinel for
        /// "unmeasured / no-op" when built via a caller that doesn't supply one. Format-neutral: a plain
        /// scalar handed in by the loader, not derived here.
        /// </summary>
        public float LoudnessEstimate { get; }

        /// <summary>
        /// Every patch loaded into this bank, in load order.
        /// </summary>
        public IReadOnlyList<IPatch> Patches => patches;

        /// <summary>
        /// Number of patches loaded into this bank.
        /// </summary>
        public int Count => patches.Length;

        /// <summary>
        /// Resolves (bank, program) via the fallback chain: exact match, same-bank lowest present
        /// program, melodic default (bank 0/program 0), any percussion (bank 128) preset, then the
        /// first loaded patch. Never null; throws only when the bank holds no patches at all.
        /// </summary>
        public IPatch GetPatch(int bank, int program) {
            if (patches.Length == 0)
                throw new InvalidOperationException(
                    "SoundBank contains no patches; GetPatch requires at least one loaded preset.");

            if (TryExactMatch(bank, program, out IPatch? exact))
                return exact!;
            if (TryLowestInBank(bank, out IPatch? lowestInBank))
                return lowestInBank!;
            if (bank != PercussionBank && TryExactMatch(MelodicBank, DefaultProgram, out IPatch? melodicDefault))
                return melodicDefault!;
            if (TryLowestInBank(PercussionBank, out IPatch? anyPercussion))
                return anyPercussion!;
            return patches[0];
        }

        bool TryExactMatch(int bank, int program, out IPatch? patch) {
            if (byBank.TryGetValue(bank, out SortedDictionary<int, IPatch>? programs)
                && programs.TryGetValue(program, out patch))
                return true;
            patch = null;
            return false;
        }

        bool TryLowestInBank(int bank, out IPatch? patch) {
            if (byBank.TryGetValue(bank, out SortedDictionary<int, IPatch>? programs)) {
                foreach (KeyValuePair<int, IPatch> entry in programs) {
                    patch = entry.Value;
                    return true;
                }
            }
            patch = null;
            return false;
        }
    }
}
