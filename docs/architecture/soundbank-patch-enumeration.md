# Architectural Document: SoundBank Patch Enumeration (`(bank, program, name)`)

> Repo path: `docs/architecture/soundbank-patch-enumeration.md`
> DiVoid task: **#7508** — project **#6128** — supersedes the sketch in `tracker-sequencer.md` §11 (which recommended a `(bank, program)`-only `EnumeratePatches()` / `PatchReference` shape; the operator has since decided to include names, resolving sequencer design #7503 OQ-5).
> Load-bearing contracts: **Design Contracts #1136** (KISS/DRY/YAGNI), **Code Contracts #114 §0**, **real structures under real names #6836**.

---

## Direction (operator decision — verbatim)

> "Expose the **real SF2 preset names alongside `(bank, program)`** — each available patch as `(bank, program, name)` — so the editor shows real instrument labels, not just numbers."

This document designs to those words and validates them against the current code.

---

## 1. Problem Statement

The in-engine tracker editor (a separate session, consuming the published `Pooshit.AudioSynth` package) needs to populate an **instrument picker**. To do that it must ask a loaded `SoundBank` "which patches are available, and what are they called?" — and get back, per selectable instrument, the `(bank, program)` address it will pass to `SoundBank.GetPatch(bank, program)` plus a human-readable **name** ("Acoustic Grand", "Strings") for display.

Success criteria:

- A read-only enumeration on `SoundBank` returns one descriptor `(bank, program, name)` per **loadable** patch.
- The enumeration reflects **exactly** the set resolvable by exact match through `GetPatch(bank, program)` — no fallback-only phantom entries, no shadowed duplicates.
- Real SF2 preset names appear, not just numbers.
- No change to synthesis or loading behaviour beyond retaining the preset name the SF2 parser **already reads**.

## 2. Key Finding (the code reality)

**Preset names are already parsed and retained — but they stop at the loader boundary and never reach `SoundBank`.**

Tracing the load path:

| Stage | File | Name present? |
|-------|------|---------------|
| Raw phdr record | `Formats/Sf2/RawPreset.cs` — `Name` | ✔ parsed from `achPresetName` (20-byte ASCII, NUL-trimmed by `ReadFixedAscii`) |
| Parsed preset header | `Formats/Sf2/Sf2PresetHeader.cs` — `Name`, `PatchNumber`, `BankNumber` | ✔ retained |
| Patch object | `Formats/Sf2/Sf2Patch.cs` — `Preset` property | ✔ retained (holds the `Sf2PresetHeader`) |
| **Bank assembly** | `Sf2SoundBankLoader.BuildPatches` (line 483–489) | ✖ **name dropped** — builds `(int Bank, int Program, IPatch Patch)` tuples; the name is discarded here |
| `SoundBank` storage | `Synthesis/SoundBank.cs` | ✖ stores only `byBank: bank → (program → IPatch)` and a flat `IPatch[]`; **no name anywhere** |
| Synthesis seam | `Synthesis/IPatch.cs` | ✖ `IPatch` has only `StartVoice` — **no name, and correctly so** (see §7) |

**Conclusion:** names are **not** disproportionate to retain. They are already in memory on `Sf2PresetHeader.Name`; the only thing missing is one field on the `SoundBank` constructor tuple to carry the name from the loader into the bank. This is a **small, load-path-neutral change** — the SF2 parser is untouched; only the tuple it feeds into `SoundBank` gains a `Name` element. We therefore implement the operator's decided `(bank, program, name)` shape (not the `(bank, program)`-only fallback).

## 3. Scope & Non-Scope

**In scope**
- A public descriptor type `PatchInfo` = `(int Bank, int Program, string Name)`.
- Threading the preset name from the SF2 loader into `SoundBank` (one new tuple element).
- A read-only enumeration on `SoundBank` returning the deduped, resolvable set as `IReadOnlyList<PatchInfo>`.
- Mechanically updating every in-repo `new SoundBank(...)` call site to the new tuple arity (inventory in §11).

**Out of scope (explicitly)**
- Any change to `GetPatch`'s fallback chain, `Patches`, or `Count`.
- Any change to the SF2 parser, `Sf2PresetHeader`, `Sf2Patch`, `Sf2RegionResolver`, or sample handling.
- Adding a name to `IPatch` (§7 explains why the name must **not** live there).
- Filtering, searching, grouping, or sort-mode options on the enumeration — the editor sorts/filters its own view (YAGNI).
- Carrying the `IPatch` reference inside `PatchInfo` — the picker addresses patches by `(bank, program)` through `GetPatch`, it does not need the object (YAGNI).
- Synthesising a display label when a preset name is blank — that is an editor display concern (YAGNI; see §9).
- SFZ / native `.bank` loaders — not present; the seam (`ISoundBankLoader`) already accommodates them later with no change here.
- Async / caching / a new service — the enumeration is pre-built in-memory data (KISS).

## 4. Assumptions & Constraints

- `SoundBank` is the public type the editor calls (task: "a read API **on `SoundBank`**"). The enumeration therefore lives on `SoundBank`, not a companion service.
- The published package's real production builder of `SoundBank` is the internal `Sf2SoundBankLoader`; external consumers obtain a `SoundBank` from `ISoundBankLoader.Load`, not by constructing it directly. Direct construction in this repo is a **test-only** pattern (verified: the only non-test `new SoundBank(...)` is `Sf2SoundBankLoader.cs:488`).
- SF2 `achPresetName` is up to 20 ASCII chars; `ReadFixedAscii` already trims at the first NUL. Names may occasionally be empty/whitespace in malformed fonts — we store what is parsed, verbatim.
- `.NET` target and language conventions per `Pooshit.AudioSynth.csproj` and Code Contracts #114 §0 (explicit types, no `var`, XML doc on public members — matching the existing `SoundBank`/`IPatch` style).

## 5. Architectural Overview

```
 Sf2SoundBankLoader.BuildPatches
   for each Sf2PresetHeader preset:
     entries.Add( (preset.BankNumber, preset.PatchNumber, preset.Name, sf2Patch) )   ← +Name
                              │
                              ▼
 new SoundBank( IEnumerable<(int Bank, int Program, string Name, IPatch Patch)> )
   ├─ byBank : bank → (program → { Name, Patch })   ← name co-located with the resolvable slot
   ├─ patches[]        (unchanged: IPatch load order, backs Patches/Count)
   └─ AvailablePatches : IReadOnlyList<PatchInfo>    ← built once, deduped, (bank,program) ascending
                              │
                              ▼
        Tracker editor instrument picker
          reads AvailablePatches → shows "{Name}" → on select calls GetPatch(Bank, Program)
```

The single non-obvious design choice: **the name is stored co-located with the patch in the same `byBank` slot that `GetPatch` resolves against**, so the enumerated set and the resolvable set are the *same* data structure and can never drift apart.

## 6. Components & Responsibilities

### 6.1 `PatchInfo` (new public value type, `Pooshit.AudioSynth.Synthesis`)
- **Owns:** the immutable descriptor of one available patch as the editor sees it: `Bank`, `Program`, `Name`.
- **Does NOT own:** the `IPatch` object, any synthesis behaviour, or any mutable state.
- Immutable `readonly struct` (small POD-ish descriptor, per the task's "POD-ish descriptor" wording). `ToString()` may mirror `Sf2PresetHeader.ToString()` (`"{Bank}-{Program} {Name}"`) for diagnostics.

### 6.2 `SoundBank` (modified, `Synthesis/SoundBank.cs`)
- **New responsibility:** report the loadable patch set as `IReadOnlyList<PatchInfo> AvailablePatches`.
- **Changed responsibility:** its constructor now accepts a **name per entry**; it stores that name alongside each resolvable patch.
- **Unchanged responsibilities:** `GetPatch` fallback resolution, `Patches`, `Count` — semantics identical.

### 6.3 `Sf2SoundBankLoader` (one-line change, `Formats/Sf2/Sf2SoundBankLoader.cs`)
- **Changed:** `BuildPatches` supplies `preset.Name` as the new tuple element. Nothing else in the loader changes.

### 6.4 `IPatch`, `Sf2Patch`, `Sf2PresetHeader` — **untouched.**

## 7. Interactions & Data Flow — and why the name is NOT on `IPatch`

`IPatch` is the **synthesis seam**: "starts a runtime voice for a played note" (`StartVoice`). A preset's display name is **catalog metadata**, not synthesis behaviour — it plays no part in producing audio, and format-neutral archetypes (basic, FM, multi) have no natural name. Putting `Name` on `IPatch` would (a) pollute the synthesis contract with UI metadata, and (b) force every non-SF2 patch archetype to invent a name it doesn't have. The name therefore rides on the **bank entry** (where `(bank, program)` identity already lives), threaded from the loader — exactly where the address it pairs with lives. This keeps `SoundBank` format-neutral: it stores whatever name the loader hands it, without reaching into `IPatch`.

**Picker flow (conceptual):**
1. Editor loads a bank via `ISoundBankLoader.Load` → `SoundBank`.
2. Editor reads `SoundBank.AvailablePatches` → list of `(Bank, Program, Name)`.
3. Editor renders each `Name` in the instrument list.
4. On selection, editor calls `GetPatch(Bank, Program)` → resolves by **exact match** (rung 1), because every enumerated entry is a loaded slot.

## 8. Contracts & Interfaces (Abstract)

### `PatchInfo`
| Field | Meaning | Notes |
|-------|---------|-------|
| `Bank` | MIDI bank number (0–127; 128 = percussion) | as loaded |
| `Program` | MIDI program/patch number | as loaded |
| `Name` | preset name as parsed from the SF2 `achPresetName` | verbatim; may be empty for a malformed/blank font entry |

### `SoundBank.AvailablePatches : IReadOnlyList<PatchInfo>`
- **Returns:** one `PatchInfo` per **distinct loadable `(bank, program)` slot**, i.e. exactly the set `GetPatch` resolves by exact match. Deduped: if two loaded entries share a `(bank, program)`, the enumeration lists it **once**, carrying the **same winning name+patch** that `GetPatch` would return (last-write-wins, consistent with the existing `byBank` overwrite semantics).
- **Order:** `Bank` ascending, then `Program` ascending — deterministic and picker-friendly. (Programs are already ordered by the existing `SortedDictionary`; only the bank keys need sorting.)
- **Materialisation:** built once during construction and stored; the property is O(1) to access and returns a stable, immutable snapshot.
- **Empty bank:** returns an empty list (it does not throw — only `GetPatch` throws on an empty bank).

### Invariant (state and test this)
> For every `PatchInfo p` in `AvailablePatches`, `GetPatch(p.Bank, p.Program)` returns — via exact match, never fallback — the patch whose stored name is `p.Name`. The enumeration never lists a `(bank, program)` that only resolves through the fallback chain.

### `SoundBank` constructor (changed signature)
- **From:** `SoundBank(IEnumerable<(int Bank, int Program, IPatch Patch)>)`
- **To:** `SoundBank(IEnumerable<(int Bank, int Program, string Name, IPatch Patch)>)`
- Null-entry and null-collection guards unchanged. A null `Name` should be guarded the same way a null patch is (reject), OR normalised to empty string — pick reject to match the existing strict-entry stance (`patch is null` throws). **Decision: reject null `Name`** (consistent with the existing null-patch `ArgumentException`); callers with no meaningful name pass `""`, not null.

## 9. Cross-Cutting Concerns

- **Error handling:** unchanged strictness — invalid entries (null patch, now null name) throw `ArgumentException`; null collection throws `ArgumentNullException`.
- **Blank names:** stored verbatim (possibly `""`). `SoundBank` does not invent a label — reporting what's loaded is its job. The editor may render a `"{Bank}-{Program}"` placeholder for empty names (its concern, not ours).
- **Immutability / thread-safety:** `AvailablePatches` is built at construction and never mutated; `SoundBank` remains effectively immutable, so concurrent reads by the editor are safe with no locking.
- **Allocation:** one additional `PatchInfo[]` sized to the distinct-slot count, built once at load. Negligible; no per-call allocation (the property returns the stored array as `IReadOnlyList<PatchInfo>`).
- **Observability:** none required; this is a pure in-memory read.

## 10. Quality Attributes & Trade-offs (KISS / DRY / YAGNI audit)

**KISS — can each element be deleted / merged / inlined?**
- `PatchInfo` type vs. returning `IReadOnlyList<(int,int,string)>` tuples: the descriptor **crosses a published-package boundary** (a separate editor session consumes it). A named `PatchInfo` with named fields is materially clearer than positional tuple elements at that boundary and satisfies #6836 (real structures under real names). **Named struct kept**; the value-tuple alternative is rejected because unnamed positional fields across a package seam are a documented readability cost.
- `AvailablePatches` as a **property** (not a method): it is pre-built stored data, so a property matches the sibling `Patches`/`Count` and signals "data, not computation." Kept.
- Storing the name **in the `byBank` slot** rather than a parallel `bank→program→name` dictionary: a parallel structure would duplicate the `(bank, program)` keying and risk divergence from the resolvable set. Co-location is the single-source-of-truth choice and cannot drift. Kept.

**DRY** — no multi-line block is duplicated across ≥2 sites in this design; the `GetPatch`/`TryExactMatch`/`TryLowestInBank` edits are single-token `.Patch` dereferences, not extractable blocks. Block-DRY math is **N/A** (no `block_size × site_count` above threshold). The one repeated *call-site* edit (tuple arity) is a mechanical carrier-swap, enumerated in §11 so no site is missed (avoids the "representative cases only" anti-pattern).

**YAGNI** — dropped speculative shapes, each named: no filter/search/sort-mode API, no `IPatch` reference in the descriptor, no `IPatch.Name`, no blank-name label synthesis, no async/caching. Each is added only if a concrete consumer later demands it.

**Constructor change vs. a back-compat overload (the one real trade-off).**
Adding `Name` to the entry tuple is a breaking change to `SoundBank`'s public constructor. The alternative — keeping the 3-tuple constructor and adding a 4-tuple overload — was **rejected**:
- Design Contracts #6 explicitly rejects "compatibility shim for an internal interface where you can grep all callers — just change the callers." Every `new SoundBank(...)` caller is in this repo (§11), so they are all greppable and updatable.
- Design Contracts #4 "can it be merged" — two constructors doing the same job is the merge smell.
- The name is **intrinsic** to a bank entry (every SF2 preset has one); a nameless entry is a test convenience, not a real shape. Making the name part of the canonical tuple keeps one honest way to build a `SoundBank`.
- **Cost named concretely:** ~25 in-repo call sites (all but one are tests) gain one tuple element — a mechanical, one-time edit in the same PR. This cost is bounded and enumerated; the permanent second-constructor surface is not. Single constructor wins.

## 11. Call-Site Inventory (carrier-swap — every affected site, not representative)

Every `new SoundBank(...)` call site must change from `(bank, program, patch)` to `(bank, program, name, patch)`. Production passes the real name; tests pass a descriptive stub name (many already have a `StubPatch("piano")` — pass `"piano"`) or `""` where the name is irrelevant.

**Production (1 site — passes the real name):**
- `src/Pooshit.AudioSynth/Formats/Sf2/Sf2SoundBankLoader.cs:488` → add `preset.Name` as the 3rd tuple element.

**Tests (update every `new SoundBank(...)`; grep pattern `new SoundBank(` finds them all):**
- `test/.../SoundBankTests.cs` (lines 22, 35, 51, 67, 85, 101, 118, 133, 147, 159, 169, 181) — note line 159 `Array.Empty<(int, int, IPatch)>()` becomes `Array.Empty<(int, int, string, IPatch)>()`; add `AvailablePatches` coverage here (§13).
- `test/.../Tracker/TrackerSequencerTests.cs` (20, 23, 451)
- `test/.../Tracker/TrackerSongRenderTests.cs` (48)
- `test/.../Sequencing/RealtimeSequencerTests.cs` (24)
- `test/.../Midi/MidiSequencerBankSelectTests.cs` (39, 61, 87, 113, 138)
- `test/.../Midi/MidiSequencerProgramChangeTests.cs` (37, 62, 85)
- `test/.../Midi/MidiPitchBendRenderProofTests.cs` (56, 123, 180)
- `test/.../Midi/MidiSequencerHousekeepingControllersTests.cs` (38, 227)
- `test/.../Midi/MidiSequencerChannelGainTests.cs` (31), `MidiSequencerPitchBendTests.cs` (31), `MidiSequencerModulationTests.cs` (34), `MidiSequencerChannelChorusSendTests.cs` (33), `MidiSequencerChannelReverbSendTests.cs` (33), `MidiSequencerChannelPanTests.cs` (32), `MidiSequencerSustainTests.cs` (34), `MidiSustainRenderProofTests.cs` (55), `MidiModulationRenderProofTests.cs` (55), `StereoPanRenderProofTests.cs` (117), `ChorusRenderProofTests.cs` (46)

The authoritative discovery command for the implementer: grep `new SoundBank(` across the solution and update **every** hit — the list above is the current snapshot, the grep is the source of truth.

## 12. Risks & Mitigations

| Risk | Mitigation |
|------|-----------|
| A `new SoundBank(...)` site is missed → compile error | Compile error is loud and immediate; the grep command in §11 finds all sites. Low risk. |
| Enumeration drifts from the resolvable set (lists a fallback-only slot, or a shadowed duplicate) | Structural mitigation: name is stored in the **same** `byBank` slot `GetPatch` resolves; `AvailablePatches` is built by walking `byBank`, so it is the resolvable set by construction. Invariant test in §13. |
| Duplicate `(bank, program)` in a font | Existing last-write-wins in `byBank` applies to name+patch together (they arrive as one tuple); enumeration lists the slot once with the winning name. |
| Blank preset name confuses the picker | Out of scope here; editor renders a placeholder. `SoundBank` reports parsed names verbatim. |

## 13. Migration / Rollout & Test Guidance

- Single PR, branch `feature/soundbank-patch-enumeration` (independent of the editor; the editor session consumes the published package).
- Update `tracker-sequencer.md` §11 in this PR: replace its `(bank, program)`-only sketch with a one-line pointer to this doc (its `PatchReference`/`EnumeratePatches()` recommendation is superseded by the operator's names decision).
- **Tests to add** (in `SoundBankTests.cs`):
  1. `AvailablePatches` returns one `PatchInfo` per loaded slot with correct `Bank`/`Program`/`Name`.
  2. Order is `(bank, program)` ascending across multiple banks.
  3. Duplicate `(bank, program)` appears once, carrying the last-written name (matching `GetPatch`).
  4. Invariant: for each `PatchInfo p`, `GetPatch(p.Bank, p.Program)` returns the patch whose name is `p.Name` (exact match).
  5. Empty bank → `AvailablePatches` is empty (does not throw).
- **SF2 end-to-end** (extend `Sf2/Sf2LoaderTests.cs`): loading a real SF2 yields `AvailablePatches` whose names equal the corresponding `Sf2PresetHeader.Name` values.

## 14. Open Questions

None that block implementation. One note for the editor session (not blocking this library PR): the editor decides how to display blank names and whether to group by bank — both are editor concerns; `SoundBank` provides the canonical `(bank, program)`-ascending list with verbatim names.

## 15. Implementation Guidance for the Next Agent (John)

1. Add `PatchInfo` readonly struct (`Bank`, `Program`, `Name`) in `Synthesis`, with XML docs matching the existing style and a `ToString()` mirroring `Sf2PresetHeader.ToString()`.
2. Change the `SoundBank` constructor tuple to `(int Bank, int Program, string Name, IPatch Patch)`; reject null `Name` like null patch.
3. Co-locate the name with the patch in `byBank` (e.g. inner map value becomes a tiny `(string Name, IPatch Patch)` slot); update `GetPatch`/`TryExactMatch`/`TryLowestInBank` to dereference `.Patch`. Semantics unchanged.
4. Build `AvailablePatches` once in the constructor: iterate bank keys ascending, programs ascending (already sorted), emit one `PatchInfo` per slot; expose as `IReadOnlyList<PatchInfo>`.
5. Change `Sf2SoundBankLoader.BuildPatches` to pass `preset.Name`.
6. Update every `new SoundBank(...)` call site per §11 (grep `new SoundBank(`).
7. Add the tests in §13.
8. Update `tracker-sequencer.md` §11 to point here.
9. Leave `IPatch`, `Sf2Patch`, `Sf2PresetHeader`, and the SF2 parser untouched.
