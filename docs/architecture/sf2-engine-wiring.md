# Architectural Document: SF2 → Synthesis-Engine Wiring (First Audio)

**Author:** Sarah (software architect) · **Date:** 2026-07-16 · **Source task:** DiVoid #6667 · **Project:** #6128
**Builds on:** engine design #6503, rewrite design #6401. **Regression source:** defect catalog #6272.
**Load-bearing contracts:** Code Contracts #114 §1 (naming), Design Contracts #1136.
**DiVoid copy:** documentation node linked to #6667 + #6128 (this repo file is the authoritative wording).

> This is the PR 4 milestone from the roadmap in #6401 §14 / #6503 (roadmap item i): connect the SF2
> loader (PR #2) to the voice engine (PR #3) by implementing `Sf2Patch.StartVoice`, shipped bundled
> with a **first increment** that renders a real SoundFont note to audio. It is also the **reference
> for the later generator PRs** (envelope / filter / LFO / attenuation): it documents the SF2 two-level
> zone/generator resolution model and names the seam where those generators plug in.

---

## 1. Problem Statement

`Sf2Patch.StartVoice(int key, int velocity)` currently throws `NotImplementedException`. The SF2 loader
produces a faithful in-memory SF2 model (presets → zones, instruments → zones, sample headers, a shared
raw sample-data pool) and the voice engine can turn an `IVoice` into mixed, bounded PCM — but nothing
bridges the two. The bridge is the act of **resolving**, for a played (key, velocity), the SF2
preset → instrument → zone → sample chain plus the tuning/loop generators into an engine-native
`SampleRegion`, and starting a `SamplePlaybackVoice` from it.

**Success criteria:** a real SoundFont loaded, a note started, and a block rendered through a
`Synthesizer` produces **non-silent, amplitude-bounded** output — "first audio" — with the resolution
logic deterministically unit-tested against synthetic SF2 fixtures.

---

## 2. Scope & Non-Scope

**In scope (this PR / increment):**
- A shared, cached, normalized `float[]` "sample pool" derived once from `Sf2SampleData`.
- **Instrument-level** zone resolution (v1): preset → instrument navigation (to *find* the instrument),
  then instrument-zone key/velocity matching with instrument-global-zone defaults, producing a
  `SampleRegion`.
- Interpretation of the v1 generator subset: `KeyRange`(43), `VelocityRange`(44), `SampleID`(53),
  `OverridingRootKey`(58), `CoarseTune`(51), `FineTune`(52), `SampleModes`(54); plus sample-header
  `RootKey`/`PitchCorrection`/`Start`/`End`/`StartLoop`/`EndLoop`/`SampleRate`.
- `Sf2Patch.StartVoice` returning a live `SamplePlaybackVoice`, reusing the proven `SamplePatch`
  pitch/gain math.
- Per-instrument-zone caching of the resolved region.
- Tests: an always-green synthetic "first audio" proof + deterministic resolution unit tests, and a
  real-`__Florestan_Basic_GM_GS.sf2` integration test.

**Explicitly out of scope (each a NAMED follow-up PR — see §12):**
- **PR 4a — Preset-level generator addition & velocity layers:** summing preset-zone/preset-global
  generator offsets onto the instrument-level result; multiple layered instrument zones per note.
- **PR 4b — Sample-offset generators:** `StartAddressOffset`(0), `EndAddressOffset`(1),
  `StartLoopAddressOffset`(2), `EndLoopAddressOffset`(3), `StartAddressCoarseOffset`(4),
  `StartLoopAddressCoarseOffset`(45), `EndLoopAddressCoarseOffset`(50).
- **PR 4c — Attenuation & pan:** `InitialAttenuation`(48), `Pan`(17).
- **PR 4d+ — Modulation (all of it):** volume/modulation envelopes (ADSR), filter (`InitialFilterCutoff`
  /`Q`), LFOs, and the `Sf2Modulator` real-time routing.
- Preset/bank *selection* policy (choosing which preset answers a MIDI program/bank) — the engine still
  drives a single `defaultPatch`; multi-patch banks are a separate engine concern.

---

## 3. Assumptions & Constraints

| # | Assumption / Constraint | Confidence |
|---|---|---|
| C1 | Multi-target `netstandard2.0;net8.0`; hot render path (`Read`/`RenderBlock`) stays alloc-free. `StartVoice`/`NoteOn` is an event-time path where a bounded allocation (voice, first-time region) is acceptable — it already allocates a voice. | High |
| C2 | #114 §1 naming: private fields plain camelCase, **no** underscore prefix, **no** explicit `private` modifier; constructors disambiguate with `this.field = field`. One type per file. | High (hard contract) |
| C3 | Tests are NUnit 4.x, net8.0 only; `[Test]` methods are exempt from the XML-summary rule; body-comment grep must be 0. | High (verified) |
| C4 | The whole-file sample pool is indexed by **absolute frame index**; SF2 sample-header `Start/End/StartLoop/EndLoop` are absolute indices into that pool (they are, per SF2 §7.10 and the loader). | High |
| C5 | The `Synthesizer` plays a **single** `defaultPatch` for all notes (verified in `Synthesizer.cs`). The integration/first-audio proof picks one preset and builds one `Synthesizer` from it. | High |
| C6 | A patch is **bound to the output sample rate at construction** — the established codebase convention (`new SamplePatch(region, opts.SampleRate)`), and it is the caller's responsibility to construct the patch at the same rate as its `Synthesizer`. `Sf2Patch` must follow the same convention. | High (verified pattern) |
| C7 | The reference `__Florestan_Basic_GM_GS.sf2` lives **outside** the git repo (`../../Source/AudioSynthesis.Tests/Soundfonts/…` relative to the repo root) and is a 3.27 MB third-party binary; no git-lfs is configured. | High (verified) |

---

## 4. Architectural Overview

The bridge is one small, well-bounded pipeline invoked from `Sf2Patch.StartVoice`:

```
 NoteOn(key,vel)
      │
      ▼
 Sf2Patch.StartVoice(key, vel)                         [IPatch seam; bound to outputSampleRate]
      │  1. resolve
      ▼
 Sf2RegionResolver.Resolve(key, vel)  ───────────────► SampleRegion?   (null = no zone matches → no voice)
      │   • preset → instrument navigation (find instrument; v1 uses NO preset generators)
      │   • instrument-zone key/vel match + instrument-global-zone defaults
      │   • effective generators → rootKey, cents, loop mode, buffer indices
      │   • buffer = Sf2SampleData shared normalized float pool (built once)
      │  2. cache region per instrument-zone → wrap in a SamplePatch (reuses pitch/gain math)
      ▼
 SamplePatch.StartVoice(key, vel)  ──────────────────► SamplePlaybackVoice   [IVoice]
      │
      ▼
 Synthesizer mixes + finalizes (INV-1/INV-2 already in place)  ─────────► bounded float PCM
```

Two additive units and one seam change:
- **`Sf2SampleData` float pool (additive helper)** — a cached normalized `float[]` for the whole file.
- **`Sf2RegionResolver` (new type)** — the SF2 two-level interpreter; the single home for all future
  generator work.
- **`Sf2Patch.StartVoice` (seam change)** — resolve → cache → delegate to `SamplePatch`.

Nothing in the engine (`Synthesizer`, `SamplePlaybackVoice`, `GainRamp`, `SampleRegion`) changes; the
invariants INV-1 (gain glide) and INV-2 (finalize choke point) from #6503 already protect the output.

---

## 5. Components & Responsibilities

### 5.1 `Sf2SampleData` — shared normalized sample pool (additive)
- **Owns:** the raw sample words (unchanged) **plus** a lazily-built, cached normalized `float[]`.
- **New responsibility:** expose the whole pool as `float[]` where each frame = `GetSample(i) /
  2^(BitsPerSample-1)` (÷32768 for 16-bit, ÷8388608 for 24-bit). Built **once** on first request and
  reused by every region/patch from the file (all patches share one `Sf2SampleData`).
- **Does NOT own:** region boundaries, tuning, loop semantics, or per-note state. It is a pure data pool.
- **Concurrency note:** first-build races are benign (idempotent, produces identical arrays) but should be
  made deterministic — build under a simple guard, or accept a benign double-build. Prefer a guarded
  one-time build; do not introduce locking on the read path.

### 5.2 `Sf2RegionResolver` — SF2 two-level zone/generator interpreter (NEW)
- **Owns:** the entire SF2 spec interpretation for v1 — preset→instrument navigation, instrument-zone
  key/velocity matching, global-zone default merging, effective-generator resolution, and construction of
  an engine-native `SampleRegion`. **This is the documented plug-in point for every deferred generator
  family (§12).**
- **Does NOT own:** pitch-increment/velocity-gain math (that is `SamplePatch`), output sample rate (it is
  rate-independent), voice lifecycle, mixing, or caching policy of the owning patch.
- **Determinism:** for a given (key, velocity) it resolves to at most one instrument zone (v1: the first
  match in file order) and a `SampleRegion`; returns "no match" (a nullable/`false` result) when no zone
  covers the note, in which case no voice is started.
- Constructed from the parsed SF2 data a patch already holds: preset, instruments, sample headers, and the
  shared float pool.

### 5.3 `Sf2Patch` — the IPatch seam, rate-bound (seam change)
- **Owns:** the `IPatch` contract for an SF2 preset; **binding to the output sample rate** (C6); a
  per-instrument-zone cache of resolved regions/`SamplePatch`es; delegation of voice construction.
- **New responsibility:** `StartVoice(key, vel)` = resolve (via `Sf2RegionResolver`) → look up/populate
  the per-zone cache → delegate to a `SamplePatch` (reusing the proven math) → return the voice. Returns a
  silent/no-op result when resolution finds no matching zone (see §8 for the exact contract).
- **Does NOT own:** SF2 interpretation (delegated to the resolver) or pitch math (delegated to
  `SamplePatch`). It stays a thin coordinator.

### 5.4 `Sf2SoundBankLoader` — rate binding at load (seam change)
- **New responsibility:** thread an **output sample rate** into each `Sf2Patch` it constructs, so the
  produced patches are playable. See decision D1 (§10) for why this lives on the loader and not the
  `ISoundBankLoader.Load(Stream)` untrusted-input seam.

### 5.5 Reused unchanged
`SampleRegion` (immutable descriptor + validation), `SamplePatch` (pitch increment + velocity gain →
`SamplePlaybackVoice`), `SamplePlaybackVoice` (interpolated read + `GainRamp`), `Synthesizer`,
`GainRamp`. No edits.

---

## 6. Interactions & Data Flow

**Load (once):** `Sf2SoundBankLoader.Load` parses the file (unchanged) and constructs one `Sf2Patch` per
preset, **stamping each with the output sample rate**. The shared `Sf2SampleData` is handed to every
patch; its float pool is built lazily on first note.

**Note-on (event-time):**
1. `Synthesizer.NoteOn` → `defaultPatch.StartVoice(key, velocity)`.
2. `Sf2Patch` asks `Sf2RegionResolver` which instrument zone matches (key, velocity).
   - **Preset level (navigation only, v1):** iterate `preset.Zones`; skip the preset-global zone (the
     zone with no `Instrument`(41) generator); pick the first preset zone whose key/velocity range (if
     present) covers the note and that carries an `Instrument`(41) generator; that generator's amount is
     the index into `Instruments[]`. **v1 does not sum any preset generators into the region** — the
     preset zone is used solely to select the instrument.
   - **Instrument level (the real resolution, v1):** within the chosen instrument, identify the
     instrument-global zone (the zone with no `SampleID`(53) generator) as the default source; pick the
     first instrument zone whose effective `KeyRange`(43)/`VelocityRange`(44) covers the note and that
     carries a `SampleID`(53). Effective generator value = local zone's generator, else global zone's
     generator, else the SF2 spec default.
3. If a zone matches: `Sf2Patch` returns the cached `SamplePatch` for that zone (building the region on
   first encounter), and delegates `StartVoice`. If **no** zone matches: no voice (see §8).
4. The returned `SamplePlaybackVoice` renders mono blocks; the `Synthesizer` mixes + finalizes.

**Effective-generator → `SampleRegion` mapping (v1):**

| SampleRegion field | Derived from |
|---|---|
| `buffer` | shared normalized float pool from `Sf2SampleData` |
| `start` / `end` | header `Start` / `End` (absolute frame indices; no offset gens in v1) |
| `loopStart` / `loopEnd` | header `StartLoop` / `EndLoop` |
| `loopMode` | `SampleModes`(54): 0→NoLoop, 1→Continuous, 2 (reserved)→NoLoop, 3 (loop-until-release)→Continuous (v1) |
| `sourceSampleRate` | header `SampleRate` |
| `rootKey` | `OverridingRootKey`(58) if 0–127, else header `RootKey` if 0–127, else default 60 (see D3) |
| `pitchCorrectionCents` | header `PitchCorrection` + `FineTune`(52) + `CoarseTune`(51) × 100 |

All tuning folds into the single `pitchCorrectionCents` field because `SamplePatch`'s formula is
`2^((key − rootKey + cents/100)/12) × sourceRate/outputRate` — coarse semitones become ×100 cents, fine
cents pass through, header correction adds in.

---

## 7. Data Model (Conceptual)

No new persistent entities. Conceptual relationships already modelled by the parsed types:

```
Sf2Patch(1) ── preset(1) ── presetZones(*) ──[Instrument gen]──► Sf2Instrument(1 of many, shared)
                                                                       │
                                                                 instrumentZones(*)
                                                                       │ [SampleID gen]
                                                                       ▼
                                                                 Sf2SampleHeader(1) ──► region-in-pool
Sf2Patch(*) ────────────────────────── share ─────────────────► Sf2SampleData(1) ──► float pool(1)
```

- One `Sf2SampleData` (and thus one float pool) is shared by all patches from a file.
- `Instruments[]` and `SampleHeaders[]` are shared across all patches.
- A resolved `SampleRegion` is a lightweight immutable *view* over the shared pool (indices + metadata),
  cached per instrument zone.

**Global-zone rule (precise):** within a preset or instrument, a zone is **global** iff it lacks its
terminal generator — `Instrument`(41) for preset zones, `SampleID`(53) for instrument zones — and it is
the first zone. Its generators are defaults for every sibling zone; a sibling's own generator overrides.

---

## 8. Contracts & Interfaces (Abstract)

**`Sf2RegionResolver.Resolve(key, velocity)`**
- **Input:** MIDI key (0–127), velocity (0–127).
- **Output:** a resolved instrument zone identity + a `SampleRegion`, or an explicit **no-match** result.
- **Semantics:** deterministic; first-match in file order; instrument-global defaults applied; only the v1
  generator subset interpreted; rate-independent (no output-rate dependency).
- **Invariants:** never throws on a *structurally valid but musically-imperfect* SF2 (defensive fallbacks,
  §9); the returned region always satisfies `SampleRegion`'s own validation (or resolution reports
  no-match rather than handing the region ctor an out-of-range input).

**`Sf2Patch.StartVoice(key, velocity)` → `IVoice`**
- **On match:** a live `SamplePlaybackVoice` at the correct pitch increment and velocity gain.
- **On no match / unplayable sample:** must honor the `IPatch` contract, which is non-nullable `IVoice`.
  Return an **inactive no-op voice** (a voice whose `IsActive` is already false and whose `RenderBlock`
  emits silence) rather than `null` or throwing — the `Synthesizer` will reclaim it on the next block.
  This keeps `NoteOn` total and the hot path branch-free of null checks. (See D4.)
- **Idempotent-ish caching:** repeated notes on the same resolved zone reuse the cached region/`SamplePatch`.

**`Sf2SampleData` float pool**
- **Input:** none (reads its own words). **Output:** `float[]` of length `FrameCount`, values in
  [−1, 1], with a full-scale positive 16-bit word (32767) → ~0.99997 and −32768 → −1.0.
- **Invariant:** built once; identical array returned on every call for the life of the object.

**`Sf2SoundBankLoader`**
- Gains an output-sample-rate input (constructor, default 44100). `Load(Stream)` signature and the
  `ISoundBankLoader` interface are **unchanged** (D1).

---

## 9. Cross-Cutting Concerns

- **Untrusted-input robustness (continuity with #6272 D-class):** resolution must not crash on a
  structurally valid file with odd content. Guard: `SampleID` out of range → no-match; `end ≤ start` or
  zero-length region → no-match; `SampleRate == 0` → no-match; `Continuous` with invalid loop points →
  fall back to `NoLoop` rather than let `SampleRegion` throw. The parse boundary already rejects hostile
  files; the resolver adds a *musical* validity layer that degrades to silence, never to an exception on
  the note path.
- **Determinism / testability:** all resolution is pure and rate-independent, so unit tests assert exact
  region fields from synthetic fixtures with no floating-point ambiguity.
- **Allocation:** the float pool is one allocation per file (amortized, off the hot path). Region
  construction is one allocation per distinct instrument zone, cached thereafter. The steady-state
  `Read`/`RenderBlock` path allocates nothing (unchanged).
- **Concurrency:** single-threaded note application (the engine's current contract, #6503 decision 4).
  The only shared lazily-initialized state is the float pool; keep its initialization benign (§5.1).
- **Observability / errors:** the core takes no logging dependency (reach floor). No-match is a silent,
  expected outcome, not an error; genuinely invalid *files* still surface as `InvalidSoundFontException`
  from the loader.

---

## 10. Quality Attributes & Trade-offs

**D1 — Output sample rate is bound on the loader, not on `ISoundBankLoader.Load(Stream)`.**
`Sf2Patch` needs the output rate to compute the pitch increment, but the `Load(Stream)` seam is the
*untrusted-input* boundary and must stay free of playback config. Chosen: give `Sf2SoundBankLoader` the
rate as construction config (default 44100), stamping each `Sf2Patch`. This mirrors the existing
`SamplePatch(region, outputSampleRate)` convention (C6), changes **no public interface**, and keeps
file-parsing conceptually decoupled from the rate.
*Rejected — add rate to `IPatch.StartVoice(key, vel)`:* ripples to `SamplePatch` and the engine call site
for a value known at construction; larger seam churn than binding once.
*Rejected — late-binding rebind step (engine binds patches to its rate):* the architecturally "purest"
(rate-agnostic loaded banks) but introduces a binding type/phase for no v1 benefit; recorded as a future
refinement (Q1).

**D2 — A dedicated `Sf2RegionResolver` type rather than inlining resolution in `Sf2Patch`.**
Resolution (SF2 spec interpretation) and the `IPatch` seam (lifecycle + rate binding + caching) are two
responsibilities. Splitting them honors the rewrite's core lesson — the legacy `VoiceParameters`
god-object (#6272) mixed state + mixing + buffer-writing and rotted. The resolver is also the **named home
every deferred generator PR attaches to**, so it earns its file now.
*Trade-off:* one extra type in v1; justified by SRP and the explicit downstream roadmap.

**D3 — Unpitched samples (header `RootKey == 255`) with no `OverridingRootKey` default to key 60.**
`SampleRegion` requires `rootKey` in 0–127. True SF2 percussion is a preset-level/drum concern out of v1
scope; defaulting to 60 keeps such a note *playable and bounded* rather than throwing. Reversible when
drum handling lands.

**D4 — No-match returns an inactive no-op voice, not null/exception.**
Keeps `IPatch.StartVoice` total and the engine's `NoteOn` branch-free; the engine reclaims the inactive
voice on the next block. Alternative (nullable return) would ripple a null check into the hot-adjacent
`NoteOn` and change the `IPatch` contract for every archetype.

**D5 — Reuse `SamplePatch` for pitch/gain rather than re-deriving the formula in `Sf2Patch`.**
Avoids duplicating the increment math (the legacy 4-copy DRY smell, #6272). The resolved region is wrapped
in a cached `SamplePatch` per zone; `Sf2Patch.StartVoice` delegates.

**Scalability/perf:** O(zones) resolution per *first* note of a zone, O(1) cached thereafter; one pool
allocation per file. **Maintainability:** the two-level model lives in exactly one type. **Correctness:**
deterministic, unit-tested region fields; real-file integration proof.

---

## 11. Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Real Florestan preset resolves to a *silent* sample at the chosen key → false "first audio" pass | Pick a known melodic preset (first bank-0 preset) and a mid key (60); assert peak above a real threshold; if the first pick is silent, the deterministic synthetic proof still guarantees the wiring. |
| Reference SF2 absent on a clean checkout/CI (it lives outside the repo) → test failure | Integration test resolves the path robustly and `Assert.Ignore`s with a clear message when absent; the **synthetic full-scale-sample test is the always-green first-audio proof** (D6, §14). |
| Global-zone / terminal-generator detection wrong → wrong or no sample | Unit tests with explicit global + local instrument zones; assert local overrides global and global supplies defaults. |
| `SampleRegion` ctor throws on imperfect real samples (e.g. loop points, zero-length) | Defensive resolver fallbacks (§9): degrade to `NoLoop`/no-match instead of throwing on the note path. |
| Onset ramp makes a too-short render look silent | `GainRamp` reaches target in ~5 ms (~220 frames @44.1k); render several thousand frames before asserting non-silence. |
| Bit-depth normalization wrong (24-bit) | Unit test: full-scale 16-bit word → ~±1.0; the loader's 24-bit sign-extension is already correct (#6272 A fixed) and reused via `GetSample`. |

---

## 12. Roadmap — where the deferred generators plug in

All attach to **`Sf2RegionResolver`** (or, for modulation, to a per-voice state object the resolver will
populate). Each is its own PR (one feature per PR):

1. **PR 4 (this):** instrument-level region resolution + first audio.
2. **PR 4a — preset-level generator addition & velocity layers:** resolver sums preset-zone/preset-global
   generator *offsets* onto the instrument result; supports multiple matching instrument zones per note.
3. **PR 4b — sample-offset generators:** resolver applies gens 0–4/45/50 to the region's start/end/loop
   indices.
4. **PR 4c — attenuation & pan:** `InitialAttenuation`(48) folds into voice gain; `Pan`(17) into the
   engine's per-voice pan (needs an engine seam for per-voice pan).
5. **PR 4d — volume envelope (ADSR):** resolver reads the volume-envelope gens (33–40) into a per-voice
   envelope that modulates the `GainRamp` target; the engine's gain path (INV-1) is the attach point.
6. **PR 4e — filter + LFO:** `InitialFilterCutoff`(8)/`Q`(9) and the LFO gens (21–24) drive a per-voice
   biquad + LFO stage inside the voice's render.
7. **PR 4f — modulators:** `Sf2Modulator` real-time controller routing.

---

## 13. Open Questions

- **Q1 (D1 follow-up):** Is rate-at-load acceptable for v1, or do you want rate-agnostic loaded banks with
  a late-binding rebind step? Recommended: rate-at-load now (matches `SamplePatch`); revisit only if a
  use-case needs one loaded bank across multiple output rates. *Non-blocking.*
- **Q2 (test asset):** Confirm the reference-with-graceful-skip approach for `__Florestan…sf2` (D6) vs.
  committing a small purpose-built real SF2 fixture. Recommended: skip-if-absent + synthetic always-green
  proof; avoids a 3.27 MB third-party blob in git history. *Non-blocking.*
- **Q3 (drums):** D3 defaults unpitched samples to key 60. Confirm that is fine until drum/percussion
  handling is designed. *Non-blocking.*

None block the increment.

---

## 14. Implementation Guidance for the Next Agent

Build order (each step buildable/testable; **no code in this doc — this is the work breakdown**):

**M1 — Shared float pool (additive, `Sf2SampleData`).**
Add a cached, lazily-built normalized `float[]` accessor (÷ 2^(BitsPerSample-1)) reusing `GetSample`.
Built once; benign init (§5.1). *Unit test:* a full-scale 16-bit word → ~±1.0; length == `FrameCount`.

**M2 — `Sf2RegionResolver` (new file, one type).**
Pure, rate-independent resolution per §6/§8: preset→instrument navigation (v1 uses no preset generators),
instrument-zone key/vel match with global-zone defaults, effective-generator → `SampleRegion` mapping
(the §6 table), defensive fallbacks (§9), explicit no-match. *Unit tests* against synthetic fixtures:
zone match hit/miss; local-overrides-global; rootKey override vs header vs 255-default; coarse/fine/header
tune → `pitchCorrectionCents`; `SampleModes` → `LoopMode`.

**M3 — `Sf2Patch.StartVoice` (seam change) + rate binding.**
Add the `outputSampleRate` field/ctor param (C6, D1). Implement `StartVoice`: resolve → per-zone cache →
delegate to a cached `SamplePatch` (D5); no-match → inactive no-op voice (D4). Update
`Sf2SoundBankLoader` to supply the rate (constructor default 44100) to each `Sf2Patch`; keep
`ISoundBankLoader.Load(Stream)` and `FormatId` unchanged.

**M4 — Extend `Sf2TestBuilder` + `Options`.**
The current `BuildWithOnePreset` fixture emits **no** `Instrument`(41) or `SampleID`(53) generators, so it
cannot resolve. Extend the builder to emit, with correct bag/gen indices: a preset zone carrying
`Instrument`=0; an instrument zone carrying `KeyRange`, `SampleID`=0, and optional
`OverridingRootKey`/`CoarseTune`/`FineTune`/`SampleModes`; a non-silent sample (e.g. full-scale words).
Add `Options` knobs for these. Keep sizes computed from content (existing discipline).

**M5 — Tests: first audio.**
- *Synthetic (always-green first-audio proof, D6):* build an SF2 with a full-scale non-silent sample via
  M4, load, build a `Synthesizer` from the resolved patch, `NoteOn`, render several thousand frames,
  assert **non-silent** (peak above a real threshold) and **bounded** (all |s| ≤ 1).
- *Real Florestan (integration):* resolve `__Florestan_Basic_GM_GS.sf2` via a repo-relative path walking
  up to the `AudioSynth` root; `Assert.Ignore` with a clear message if absent. Load, pick the first
  melodic preset, `NoteOn(0, 60, 100)`, render, assert non-silent + bounded. This is the real-file "first
  audio" proof that runs wherever the asset exists (the dev environment).
- *Resolution units:* the M2 tests above.

**M6 — Self-audit before hand-off (per brief §7).**
Underscore-field grep = 0; body-comment grep (`^\+\s*//[^/]`) = 0; one-type-per-file; both TFMs build with
**0 warnings** (a warning signals a missed `this.` disambiguation); all tests green.

> **D6 (test-asset decision, §11/Q2):** reference the external Florestan file with graceful skip **and**
> ship a synthetic full-scale-sample test as the always-green first-audio proof. Rationale: the 3.27 MB
> third-party binary is not ours to commit and would bloat every future clone; the synthetic proof
> guarantees the wiring deterministically while the real-file test exercises a full GM SoundFont wherever
> the asset is present.

**Handoff note:** implementation (M1–M6), branch `feature/sf2-engine-wiring`, and the PR are for the
backend implementer (`john-backend-dev`), not the architect. This document is the blueprint; it contains
no code by design.
