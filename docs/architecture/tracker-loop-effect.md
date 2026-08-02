# Design: Tracker Loop-to-Order Position Effect

> Repo mirror (source of truth): `docs/architecture/tracker-loop-effect.md` on branch `feature/tracker-loop-effect`.
> DiVoid task #7453 · design of the format #7452 · project #6128 · foundation Timeline #7361, tracker format #7451 (PR #41).
> Design + first implementation increment ship in ONE PR (#1165). Governing comment contract: **#2051** (>5 XML content-lines = Critical; 3-5 = Warning; target ≤2).
> Load-bearing: Code Contracts #114 §0, Design Contracts #1136 §1 (KISS/DRY/YAGNI).

## Problem

BGM needs to loop: play an intro once, then repeat a main section forever (classic MOD/S3M `Bxx` "position jump"). Toni, verbatim (2026-08-02):

> "i think looping will be a custom effect - loop to order x - this handles all cases i can think of (intro, then the looping part)"

The insight (Toni's framing, verified against the code): a backward "loop to order x" maps directly onto machinery that already exists. `RealtimeSequencer`'s constructor already takes an optional loop region `(loopStart, loopEnd)` in samples (`RealtimeSequencer.cs:38-53`, `112-114`) and repeats `[loopStart, loopEnd)` forever. So looping needs **no synth/driver change** — the importer detects a loop-to-order effect during its order-walk, computes the two sample offsets, and surfaces them to the caller, who constructs the sequencer with that region.

The real design problem this exposes: `TrackerTimelineImporter.Import(Song, sampleRate)` returns only a `Timeline` (`TrackerTimelineImporter.cs:25`) — there is no channel to convey the computed loop points to the caller. This design resolves that, which also closes design #7452's deferred OQ-3 (loop-length / loop-point seam).

## Pre-Design Checklist (#1136 §5)

**1. What exactly is being built?** One new effect command `JumpToOrder` (enum append) + loop-region computation in the importer + a return-type change on `Import` so the caller receives the computed `(loopStart, loopEnd)`. No driver/synth/Timeline change.

**2. Does it already exist / can it be reused?** Yes — the entire loop machinery already exists in `RealtimeSequencer`. This design adds only the importer-side detection and the surfacing seam. The finite-timeline walk is reused unchanged; loop computation piggybacks on the existing per-row pre-pass (`TrackerTimelineImporter.cs:66-72`) that already scans cells for `SetSpeed`/`SetTempo`.

**3. Smallest cohesive increment?** The whole feature is small and cohesive: enum value + return type + computation + tests. It ships in one PR. Per-tick DSP effects (arpeggio/slides/…) and `PatternBreak` are explicitly out (see Scope).

**4. Can any new element be deleted / merged / inlined?** The one new type (`TrackerImport`) survives the check — it is the return carrier; see §KISS below. The one new enum value is the effect itself. No new helpers, layers, or fields beyond these.

**5. What does NOT go in?** See "Scope — what does NOT go in".

## Effect encoding

Append one value to `TrackerEffectCommand` (byte-backed enum — append is format-compatible, existing songs unaffected):

| Value | Name | Param semantics |
|-------|------|-----------------|
| 3 | `JumpToOrder` | target **order-list position** (index into `Song.Order`), classic `Bxx` |

The parameter is an **order-list position** (index into `Song.Order`), not a pattern index — matching classic `Bxx` "position jump" and Toni's words "loop to order x". `EffectParam` is a `byte` (0..255); an order-list position beyond `Order.Length` is treated as out-of-range (see Jump semantics).

## Jump semantics — precise rules

The importer keeps doing its full linear walk of the whole `Order` list (it must, to know which jump is *last*), building the finite timeline exactly as today. It additionally records, for every order-list **position** `p`, the sample offset at which that position begins (`orderStart[p] = round(cursor)` captured at the top of each position, before the existing invalid-pattern-index skip so every position has an offset). When it encounters a `JumpToOrder` cell it evaluates one rule:

- **A jump is loop-valid iff its target position `t` satisfies `0 ≤ t ≤ p`** (the current order-list position) — i.e. it is a **backward or self jump** into a region already walked. Such a jump forms loop region `[loopStart, loopEnd)` where `loopStart = orderStart[t]` and `loopEnd = the cursor value immediately after the jump row's advance` (the offset at which the row following the jump would start).
- **Forward and out-of-range targets (`t > p`, which includes `t ≥ Order.Length`) are ignored** — no loop region contributed. `RealtimeSequencer`'s loop region is a *backward repeat window* only; a forward "skip ahead" cannot be expressed as one, and the finite timeline already plays all orders in sequence. Ignoring (not throwing, not clamping) is consistent with the importer's existing tolerance of out-of-range `Order` entries (`TrackerTimelineImporter.cs:57-58`, silently skipped) and of unknown effect commands (passed through uninterpreted). This collapses "forward" and "out-of-range" into one ignored case: any `t > p` is out of the already-walked region.
- **Last valid jump wins.** Scan order is `(order position, row, channel)` lexicographic — left-to-right within a row, top-to-bottom across rows, in play order across the order list. Each loop-valid jump overwrites the pending candidate; the final candidate at end of walk defines the region. This matches classic tracker "last executed position-jump governs". Because a valid target satisfies `t ≤ p`, `orderStart[t]` is always already recorded — resolution is immediate, no second pass.

**Why `loopEnd = post-row cursor` (exclusive):** the jump takes effect after its row plays. `RealtimeSequencer` treats `loopEnd` as exclusive (`cursor >= loopEnd` triggers the jump-back; an event exactly at `loopEnd` is never dispatched — `ComputeLimit` caps at `loopEnd`). So the row *at* `loopEnd` (the first row that would follow the jump) belongs to the next iteration, which restarts at `loopStart`. Correct by construction.

**Intro-then-loop falls out for free:** target position `t > 0` ⇒ `loopStart = orderStart[t] > 0`, so everything before it is a play-once intro. Toni's exact use case, with no special code.

**Inert tail:** timeline events past `loopEnd` (from the linear walk continuing) are never dispatched once a loop region is set (the driver never advances past `loopEnd`). They are harmless and left in place — truncating the walk is unnecessary and would complicate last-wins. KISS.

## Coexistence with today's finite behavior

- **No `JumpToOrder` (or only ignored ones):** `Import` returns `LoopStart = LoopEnd = null`. The caller passes both nulls to `RealtimeSequencer` (which already accepts both-null = no loop) — **today's finite render + release-tail behavior is unchanged**, bit for bit.
- **A valid loop present:** `Import` returns the computed `(LoopStart, LoopEnd)`; the caller passes them; the driver loops the region forever. The two modes are mutually exclusive and selected purely by whether a valid backward jump exists.

`TrackerImport` guarantees both-or-neither by construction, which exactly matches `RealtimeSequencer`'s invariant (`loopStart.HasValue == loopEnd.HasValue`, else it throws — `RealtimeSequencer.cs:47-48`).

## Return-type seam (the API change)

`Import` changes from returning `Timeline` to returning a new POD carrier:

**`TrackerImport`** — a `readonly struct` (namespace `Pooshit.AudioSynth.Sequencing`) with:

| Member | Type | Meaning |
|--------|------|---------|
| `Timeline` | `Timeline` | the lowered, uncompiled timeline (as before) |
| `LoopStart` | `long?` | inclusive loop-region start in samples; `null` = finite playback |
| `LoopEnd` | `long?` | exclusive loop-region end in samples; `null` = finite playback |

Invariant: `LoopStart.HasValue == LoopEnd.HasValue`. Caller usage becomes:

```
TrackerImport import = TrackerTimelineImporter.Import(song, sampleRate);
var driver = new RealtimeSequencer(import.Timeline.Compile(), synth, bank, releaseTail, import.LoopStart, import.LoopEnd);
```

This is a **breaking change** on `Import` (preview package `0.1.0-preview.1`). It must be called out in the PR body. Impacted call sites in-repo (both must be updated in this PR):
- `test/Pooshit.AudioSynth.Tests/Tracker/TrackerTimelineImporterTests.cs:37` — the `Entries` helper: `.Import(song, SampleRate).Compile()` → `.Import(song, SampleRate).Timeline.Compile()`.
- `test/Pooshit.AudioSynth.Tests/Tracker/TrackerSongRenderTests.cs:52` — `Timeline timeline = TrackerTimelineImporter.Import(...)` → capture `TrackerImport` and use `.Timeline`.
- Any usage snippet in `docs/usage.md` / `docs/architecture/tracker-format.md` that shows `Import(...).Compile()` — update the snippet (docs only, no behavior).

This return-type seam **resolves design #7452 OQ-3** (whether `Import` should report loop info): yes, via `TrackerImport.LoopStart/LoopEnd`. Total-length reporting is *not* added — YAGNI until a concrete need (the loop use case needs the region, not the length).

## Components & responsibilities

| Component | Change | Owns | Does NOT own |
|-----------|--------|------|--------------|
| `TrackerEffectCommand` | append `JumpToOrder = 3` | the effect vocabulary | any behavior |
| `TrackerImport` (new) | new `readonly struct` | carrying `Timeline` + optional loop region out of `Import` | any computation; it is pure data |
| `TrackerTimelineImporter.Import` | returns `TrackerImport`; records `orderStart` per position; detects `JumpToOrder`; computes last-valid-backward region | loop-region computation + the finite walk (unchanged) | constructing the sequencer; touching the driver |
| `RealtimeSequencer` | **unchanged** | looping the region | — |
| caller (game engine / tests) | reads `import.LoopStart/LoopEnd`, passes to sequencer | wiring | loop computation |

## Data flow

```
Song ──Import──► TrackerImport { Timeline, LoopStart?, LoopEnd? }
                      │                    │
                 Timeline.Compile()   (both null → finite; both set → loop)
                      │                    │
                      └──────► new RealtimeSequencer(schedule, synth, bank, tail, LoopStart, LoopEnd)
                                            │
                                   finite render + tail  OR  repeats [LoopStart,LoopEnd) forever
```

## KISS / DRY / YAGNI (principles math, #1267 + #1333)

- **KISS — new type `TrackerImport`.** Can-it-be-deleted/merged/inlined check: it cannot be inlined (a method returns a single value; the feature needs three: timeline + two offsets). The alternatives are `out` params or a positional tuple `(Timeline, long?, long?)`. Chosen a named `readonly struct` over both: `out` params make the common call site clumsy and are the classic anti-pattern for a multi-value return; a tuple's fields are positional and undocumented, whereas the struct gives documented names (`LoopStart`/`LoopEnd`) at the call site and a home for the resolved OQ-3 seam. **Must stay.**
- **KISS — no new helpers.** Loop detection extends the existing per-row pre-pass scan (`TrackerTimelineImporter.cs:66-72`); `orderStart` is one array; the candidate is two locals. No new method, class, or layer beyond the return struct.
- **DRY.** No repeated block introduced. The jump-detection is a single site folded into the existing scan; `block_size × site_count` is not applicable (nothing duplicated). No extraction decision to justify.
- **YAGNI.** `LoopStart`/`LoopEnd` are exactly the asked-for feature, not speculation. `PatternBreak`, per-tick DSP effects, forward-jump-as-skip semantics, and total-length reporting are all deferred/dropped — no "for future flexibility" element persists. The enum append leaves room for future effects at zero present cost (it is the effect being asked for, not a speculative slot).

No principle is overridden in favour of a different shape; nothing to bounce.

## Scope — what does NOT go in

Explicitly OUT (named as follow-ups where relevant, one-feature-per-PR):
- **`PatternBreak` (`Cxx`)** — end pattern early / jump to a row of the next order. Not trivial to combine with the linear walk + loop computation, and not needed for the BGM loop use case. **Deferred** as a named follow-up task.
- **Per-tick DSP effect engine** (arpeggio, volume/pitch slides, vibrato, retrigger, note-delay, tick-cut) — the larger separate Phase-2 increment named in #7453. Not built here.
- **Forward-jump-as-skip semantics** — a `JumpToOrder` with `t > p` is defined as *ignored*, not as a forward skip. Forward skips are not a BGM requirement and cannot be expressed on the driver's backward-only loop region.
- **Any synth/driver/`Timeline`/`CompiledSchedule`/`NeutralEvent` change** — hard constraint; the loop region already exists on `RealtimeSequencer`.
- **Total-length / additional import metadata** beyond the loop region (OQ-3 answered minimally).
- **`.mod`/`.s3m`/`.it` file parsing, editor, >16 channels** — unchanged from #7452's scope.

## First implementation increment (shipped in this PR)

1. `Formats/Tracker/TrackerEffectCommand.cs` — append `JumpToOrder = 3` with a ≤2-line `<summary>`.
2. `Sequencing/TrackerImport.cs` — new `readonly struct` (one type per file), ≤2-line summaries on type + members.
3. `Sequencing/TrackerTimelineImporter.cs` — change `Import` return type to `TrackerImport`; add `orderStart` recording, `JumpToOrder` detection, last-valid-backward-jump resolution; return finite (`null,null`) or looping region.
4. Update the two in-repo call sites (`.Timeline` accessor) + any doc snippet.
5. Tests (keep `TrackerTimelineImporter` at **100% line coverage**):
   - backward jump → correct `LoopStart`/`LoopEnd` sample offsets;
   - intro-then-loop (`target > 0` ⇒ `LoopStart > 0`);
   - last-jump-wins (two valid jumps → the later governs);
   - forward target ignored → `null` region (finite);
   - out-of-range target (`param ≥ Order.Length`) ignored → `null` region;
   - self-jump (`t == p`) → loops the current pattern from its start;
   - no-jump song → `null` region (today's behavior preserved);
   - end-to-end: a looping `Song` builds a `RealtimeSequencer` with the region and, rendered past `LoopEnd`, keeps producing bounded non-silent audio (the region actually repeats — the render never ends on its own).

## Open questions for Toni

None blocking. One confirm-only: within a single row, the **highest-index channel** with a `JumpToOrder` wins (last in left-to-right execution). Multiple jumps in one row is a malformed edge; the rule is defined and tested. If Toni prefers first-channel-wins, it is a one-line change — flagged, not blocking.

## Follow-up (named, not built here)

- `PatternBreak (Cxx)` position effect — separate task/PR.
- Per-tick DSP effect engine — the larger Phase-2 increment (#7453 §2).
