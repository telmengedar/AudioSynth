# Design: Direct-Cursor TrackerSequencer (live playback)

> Repo mirror (source of truth): `docs/architecture/tracker-sequencer.md` on branch `feature/tracker-sequencer`.
> DiVoid task **#7498** · tracker format design **#7452** (PR #41, merged) · loop effect design **#7494** / PR #42 (recommended CLOSE, see §10) · project **#6128**.
> Design + first implementation increment ship in ONE PR (Design Contracts #1136 §7 / #1165).
> Load-bearing: Code Contracts #114 §0, Design Contracts #1136 (§1–§5). Governing comment/XML-doc contract: **#2051** (>5 summary content-lines = Critical, 3–5 = Warning, target ≤2), per repo convention #7455 — NOT mamgo #114 §5.5.

## 1. Problem Statement

Toni, verbatim (#7498):

> "for the live edit i generally don't know why there is not just a cursor over the existing data which then feeds the audio engine - why do we need to transform that data?"

The current tracker playback path is `Song → TrackerTimelineImporter → Timeline → Compile → CompiledSchedule → RealtimeSequencer`. That transform burns pattern structure into a flat, sample-offset event stream. For an in-engine **editor** this is the wrong shape: editing a cell means re-import + re-compile + losing the playhead; the playhead position is not recoverable from the flat stream; "play from row N" and "loop to order X" have no natural home.

A **direct cursor over the live `Song`** — the classic tracker-replayer model — makes four editor prerequisites fall out of one mechanism:

| Prerequisite | How the cursor delivers it |
|---|---|
| Live edit | The cursor reads `Song`/`Pattern`/`Cell` data fresh at each row boundary. Mutate a cell; the cursor observes it the next time it enters that row. No transform, no swap. |
| Position feedback | The cursor *is* `(orderIndex, row)`. The editor reads it each frame for the playhead. |
| Play-from-position (seek) | Set the cursor. |
| Looping | `JumpToOrder` is a cursor jump. No computed sample-offset loop region. |

**Goal / success criteria.** A `TrackerSequencer : IAudioSource` that plays a live `Song` end-to-end by walking an `(orderIndex, row, sampleWithinRow)` cursor and driving the unchanged `ISynthesizer`; exposes an always-valid `(orderIndex, row)` playhead and a transport (play / stop / seek / loop); honours `JumpToOrder` as a cursor jump; and shares its cell→synth interpretation with the existing importer (no duplicated decision logic). Timing matches the importer sample-for-sample so live and offline agree.

## 2. Scope & Non-Scope

**In scope (this PR — design + first increment):**

- `TrackerSequencer : IAudioSource` — the cursor driver: order-walk, row boundaries, cell application, `JumpToOrder` cursor loop, position + transport read API.
- A **shared cell-application seam** (`ITrackerCellSink` + `TrackerCellApplier`) so the importer and the cursor share one cell→synth decision implementation (DRY, §7). Two thin sinks: `TimelineCellSink` (offline, symbolic) and `SynthCellSink` (live, resolves patches via `SoundBank`).
- Refactor `TrackerTimelineImporter` to route its cell interpretation through `TrackerCellApplier` + `TimelineCellSink` (behaviour-preserving; guarded by the importer's existing 100 %-line tests).
- Append `TrackerEffectCommand.JumpToOrder = 3` (the enum value the task requires we keep) — re-introduced here since it lives only on PR #42 today.
- The live-edit thread-safety contract (single-thread, documented), position feedback, and transport (play / stop / seek-to-(order,row) / loop on-off).
- Tests + an end-to-end audible-render proof.

**Explicitly NOT in scope (out):**

- The per-tick DSP effect engine (arpeggio / slides / vibrato / retrigger / note-delay / tick-cut). Explicitly deferred by #7452 and #7453 §2.
- **Patch enumeration** (task item 7). Designed in §11; ships as its **own PR** (one-feature-one-PR).
- Any `Synthesizer` / DSP change. Hard constraint.
- Any change to the POD tracker format (merged + published) beyond the `JumpToOrder` enum append (which is format-compatible: byte-enum append).
- Deleting the `Timeline` / `CompiledSchedule` / `RealtimeSequencer` / `TrackerTimelineImporter` path. It is retained (§6).
- Tracker-file (.mod/.s3m/.it) parsing, >16 channels, JSON.
- Seek-with-state-reconstruction (see OQ-1), forward-jump-as-skip (defined-as-ignored, §5).
- A lock-free snapshot / double-buffer for live edits. The contract is single-thread (§4), matching `RealtimeSequencer`.

## 3. Assumptions & Constraints

- The synth is a fixed 16-channel engine; `Song.ChannelCount ∈ [1,16]` (same guard as the importer). `ChannelCount` is fixed for a `TrackerSequencer`'s lifetime — changing it requires recreating the sequencer (§4).
- `ISynthesizer.Read` fills the whole span it is given (the `Synthesizer` mixer always does; this is the contract `RealtimeSequencer` already relies on). The cursor still carries a stall guard for a genuinely starved source (§5), mirroring `RealtimeSequencer.cs:99`.
- Timing model reused verbatim from #7452: `samplesPerRow = speed × sampleRate × 2.5 / tempo`, accumulated as a `double` cursor, rounded to `long` per row (bounded < 1 sample, no drift).
- Single-thread contract: `Read` and every mutation (transport, seek, and edits to the bound `Song`) come from the same thread (or are externally serialised). Same discipline as `RealtimeSequencer.cs:13`.
- The editor is built by a separate session; this library is the prerequisite it consumes via NuGet.

## 4. Architectural Overview

```
                         bound live reference (read fresh each row boundary)
      ┌───────────────────────────────────────────────────────────┐
      │                          Song                              │
      │   Order[] ── Patterns[] ── Cells[]      Instruments[]      │
      └───────────────────────────────────────────────────────────┘
                                  ▲  reads cells at each (order,row) boundary
                                  │
   ┌──────────────────────────────────────────────────────────────┐
   │  TrackerSequencer : IAudioSource   (LIVE playback driver)     │
   │                                                               │
   │  cursor (orderIndex, row, sampleWithinRow)  +  timing state   │
   │  transport: Play / Stop / SeekTo / Looping                    │
   │  playhead:  OrderIndex / Row  (always-valid, sounding row)    │
   │                                                               │
   │  per row boundary:  timing pre-pass (Speed/Tempo/JumpToOrder) │
   │                     applier.Apply(cell, ch, song)  ───────┐   │
   │  between boundaries: synth.Read(slice) ──► destination    │   │
   └───────────────────────────────────────────────────────────┼──┘
                                                                │
        ┌───────────────────────── shared ─────────────────────┼──┐
        │  TrackerCellApplier  (ONE cell→events decision impl)  │  │
        │  owns per-channel state: currentInstrument, activeKey,│  │
        │  patch-dedup. Emits verbs to an ITrackerCellSink:     │  │
        │  SetGain · SelectPatch · NoteOn · NoteOff · Silence    ◄─┘  │
        └───────────┬───────────────────────────────┬──────────────┘
                    │ live                           │ offline
          ┌─────────▼──────────┐          ┌──────────▼───────────────┐
          │ SynthCellSink      │          │ TimelineCellSink         │
          │ resolves patch via │          │ appends NeutralEvents at │
          │ SoundBank, calls   │          │ Offset; LinkNote pairs   │
          │ ISynthesizer live  │          │ on/off (editor parity)   │
          └────────────────────┘          └──────────┬───────────────┘
                                                      │
                              TrackerTimelineImporter (RETAINED: offline WAV
                              export, MIDI interop, Phase-3 rhythm gates)
                                          │
                              Timeline → Compile → RealtimeSequencer
```

Two playback paths, one cell-interpretation implementation. **Live tracker playback** is the cursor. **Offline / interop** stays on the Timeline path. Both interpret cells through the same `TrackerCellApplier`, so they agree note-for-note.

## 5. Components & Responsibilities

### 5.1 `TrackerSequencer : IAudioSource` (new)

Owns: the cursor `(orderIndex, row, sampleWithinRow)`, the running timing state (`currentSpeed`, `currentTempo`, fractional `rowStartCursor`), transport state (`playing`, `Looping`), and the exposed playhead (`currentOrder`, `currentRow`).

Does NOT own: cell→synth decision logic (delegated to `TrackerCellApplier`), patch resolution (delegated to `SynthCellSink`/`SoundBank`), or any DSP.

Responsibilities:

- **`Read(Span<float>)`** — the pull driver. While frames are requested: if a new row was entered, run the row's timing pre-pass + cell application; then pull inter-boundary audio straight from the synth, bounded by the samples remaining in the current row; on a row boundary, advance the cursor. It is a **real-time source**: it always fills the requested span (synth audio while playing, rendered tails → silence while stopped) and does **not** signal end-of-stream by short read except as a stall guard for a starved synth. (For finite offline render, use the Timeline path — §6.)
- **Timing pre-pass** per row: scan the row's cells for `SetSpeed` / `SetTempo` (mutating running timing) and `JumpToOrder` (recording a pending jump, last-valid-wins). Divergent from the importer's pre-pass (which does not scan jumps), and only ~5 lines, so it is NOT shared (below the DRY threshold — §7).
- **Row-boundary sample math** identical to the importer: `rowSamples = round(rowStartCursor + spr) − round(rowStartCursor)`, then `rowStartCursor += spr`. Guarantees live/offline timing parity.
- **Transport:** `Play()`, `Stop()`, `SeekTo(order,row)`, `Looping { get; set; }`.
- **Playhead:** `OrderIndex`, `Row`, `IsPlaying` — plain reads of the last-applied (sounding) row.

### 5.2 `ITrackerCellSink` (new) — the emission seam

Five verbs describing what a cell does, independent of where it lands: `SetGain(channel, gain)`, `SelectPatch(channel, bank, program)`, `NoteOn(channel, key, velocity)`, `NoteOff(channel, key)`, `Silence(channel)`. Owns nothing; two implementations.

### 5.3 `TrackerCellApplier` (new) — the shared decision logic

Owns the per-channel interpretation state (`currentInstrument`, `activeKey`, patch-dedup `appliedBank`/`appliedProgram`/`patchApplied`) and the cell decision tree (instrument select → volume→gain → playable note = patch-select-if-changed + release-prior + note-on / note-off / note-cut). Emits verbs to its bound `ITrackerCellSink`. Exposes `Apply(in Cell, channel, Song)` and `Reset()` (for seek). This is the single copy of logic that previously lived in the importer's `EmitCell` / `ApplyPatch` / `ReleaseActive`.

### 5.4 `TimelineCellSink : ITrackerCellSink` (new) — offline emission

Wraps a `Timeline` + a mutable `Offset` (set per row by the importer) + per-channel `openNoteId`. Appends `NeutralEvent`s at `Offset`; `NoteOff` links to the prior `NoteOn`'s id (`LinkNote`, editor parity) — the linking concern is fully encapsulated here, keeping the applier target-agnostic. `SelectPatch` stays symbolic (emits `SetPatch(bank, program)`; patch resolved later by `RealtimeSequencer`).

### 5.5 `SynthCellSink : ITrackerCellSink` (new) — live emission

Wraps `ISynthesizer` + `SoundBank`. Resolves `SelectPatch` immediately via `SoundBank.GetPatch(bank, program)` → `SetChannelPatch`; the other verbs map 1:1 to synth calls. No note ids, no linking (live playback has no timeline).

### 5.6 `TrackerTimelineImporter` (refactored, retained)

Same public surface (`Import(Song, sampleRate) → Timeline`), same behaviour, same 16-channel guard and timing. Internals now build a `TimelineCellSink` + `TrackerCellApplier` and drive them; the seed gain=1/pan=0 init events and the `SetSpeed`/`SetTempo` timing pre-pass stay in the importer. `JumpToOrder` is ignored by the importer (finite linear render — §6).

## 6. Interactions & Data Flow — the coexistence verdict

**Tracker LIVE playback (editor preview + runtime BGM) → `TrackerSequencer`.** This is the path that yields live-edit, position, seek and loop as natural cursor properties.

**`TrackerTimelineImporter` + `Timeline` + `CompiledSchedule` + `RealtimeSequencer` → RETAINED, not deleted**, for:

- **(a) Offline WAV export** of a tracker `Song` — a finite, deterministic linear render. `JumpToOrder` is **ignored** offline: a backward loop cannot be rendered into a finite file, so offline plays every order once in sequence (exactly today's importer behaviour). Both the cursor and the Timeline path interpret cells through the same `TrackerCellApplier`, so the offline render matches live playback note-for-note (minus the loop, by definition).
- **(b) MIDI playback** (`MidiTimelineImporter → Timeline → RealtimeSequencer`) — untouched.
- **(c) Phase-3 rhythm-gate seams** — `GatePolicy` / `GateGroup` live in the `Timeline.Compile` step; the cursor has no gate machinery. Rhythm-game conditional playback stays on the Timeline path.

**Why the cursor does not also serve offline export:** it *could* (it is an `IAudioSource`; `OfflineRenderer` could drive it with `Looping=false`), but the cursor is a real-time source with no end-of-stream signal, whereas `RealtimeSequencer` already implements finite render + release tail + the gate seam. Retaining the Timeline path for offline keeps the finite-render/gate semantics in one proven place and avoids duplicating end-of-stream logic. The two paths share the *cell* semantics (the part that would otherwise drift); they legitimately differ in *scheduling* (live cursor vs. compiled schedule).

**Key flow — a live row boundary (playing):**

1. `Read` is called with a destination span.
2. If the current row is not yet applied: resolve `(orderIndex, row)` against the *live* song (advancing past invalid orders / rolling over full patterns / looping or stopping at the end); run the timing pre-pass; compute `rowSamples`; call `applier.Apply(cell, ch, song)` for each channel; publish the playhead.
3. Pull `min(framesRequested, rowSamples − sampleWithinRow)` frames from `synth.Read` into the destination.
4. When the row's samples are exhausted, advance `rowStartCursor`, apply any pending `JumpToOrder`, else `row++`; mark row-not-applied.

## 7. Contracts & Interfaces (Abstract)

| Interface | Input | Output / effect | Invariant |
|---|---|---|---|
| `ITrackerCellSink.SetGain` | channel, gain∈[0,1] | channel mix gain set | idempotent |
| `ITrackerCellSink.SelectPatch` | channel, bank, program | future notes on channel use that patch | symbolic (timeline) or resolved (synth) |
| `ITrackerCellSink.NoteOn` | channel, key, velocity | note starts | one sounding note per channel (monophonic-channel model) |
| `ITrackerCellSink.NoteOff` | channel, key | sounding note released to tail | timeline sink links to matching on-id |
| `ITrackerCellSink.Silence` | channel | channel silenced immediately | no envelope release |
| `TrackerCellApplier.Apply` | cell, channel, song | drives the sink per the cell | tracks activeKey to release-prior before a new note |
| `TrackerSequencer.OrderIndex/Row` | — | current sounding `(order,row)` | always a valid position while playing; eventually-consistent for a cross-thread reader (§9) |

### Live-edit semantics (the crux)

- **When a mutation takes effect:** the cursor reads `Song`/`Pattern`/`Cell` data *at the moment it enters a row*. A mutation to a cell is observed the next time the cursor enters that cell's row. A mutation to `Song.Order`, a `Pattern.Rows`, or an added/removed pattern is observed at the next boundary the walk crosses.
- **The currently-sounding row:** editing the cell of a row that is *currently sounding* does not retroactively change the sound — the note was already applied to the synth. The edit is heard on the next pass. This is the natural, simple tracker semantic.
- **Structural safety:** every boundary re-validates `(orderIndex, row)` against the live song. If an edit shrank the `Order` list or a pattern, the walk clamps at the next boundary (past end → loop-or-stop; row past a shrunk pattern → advance order; invalid/malformed pattern → skip that order). The audio thread never throws (unlike the importer, which validates once and may throw offline). **`ChannelCount` is the one structural field that is fixed** for the sequencer's lifetime; changing it requires recreating the sequencer (the applier's per-channel arrays are sized at construction).
- **Thread-safety rule (the contract the editor must follow):** `Read` and every mutation (transport, seek, and edits to the bound `Song`) must be serialised — same thread, or an external lock the host owns. No internal lock, no snapshot. Identical discipline to `RealtimeSequencer`. This is KISS and sufficient for a single-audio-thread editor; a lock-free scheme is YAGNI until a concurrent-edit requirement is concrete.

### Transport semantics

- **`Play()`** — begin/resume; re-applies the current row on the next `Read` (so the current row's notes retrigger cleanly after a stop).
- **`Stop()`** — stop advancing and silence every channel; the cursor and running timing are preserved. While stopped, `Read` still pulls the synth (rendering the declick tail, then silence) so stopping does not click, and never signals end-of-stream.
- **`SeekTo(order, row)`** — silence all, reset the applier's per-channel state, re-seed running timing to `Song` defaults, set the cursor, reset the fractional row accumulator. Playback (once playing) applies from the target row.
  - **Known simplification:** a seek does NOT reconstruct timing/instrument state accumulated *earlier* in the song (a mid-song `SetTempo` before the target, or an instrument column set on an earlier row and inherited by a note-without-instrument at the target). It starts from song defaults with no inherited instrument. See OQ-1.
- **`Looping { get; set; }`** — governs behaviour when the walk runs off the *end of the order list* with no jump: `true` wraps to order 0; `false` stops. Independent of `JumpToOrder`, which is song-authored data and always honoured (giving intro-then-loop for free).

### `JumpToOrder` cursor-jump semantics (consistent with format design #7494)

- On entering a row, the timing pre-pass scans for `JumpToOrder`; **last valid wins** (lexicographic within the row, highest channel index last) — matching #7494 and classic last-executed position-jump.
- **Valid target = backward or self:** `0 ≤ target ≤ orderIndex`. The row plays for its full duration, then the cursor jumps to `(target, 0)`.
- **Forward (`target > orderIndex`) and out-of-range (`target ≥ Order.Length`) → ignored** — consistent with #7494 and with the importer already ignoring out-of-range effects. (The cursor *could* express a forward skip, unlike the importer's backward-only sample loop region; keeping it ignored keeps the live and offline jump *classification* identical. Forward-jump-as-skip is a named, deferred extension, OQ-2.)

## 8. Cross-Cutting Concerns

- **Real-time safety:** the audio-thread path (`Read` → `Apply` → sink → synth) allocates nothing steady-state (per-channel state arrays are ctor-sized, like `Synthesizer` and `RealtimeSequencer`), and never throws (structural defects are skipped, not thrown).
- **Click-free stop/seek:** `Silence` uses the synth's existing declick fade; while stopped the cursor keeps pulling the synth so the fade renders.
- **Determinism:** row boundaries use the same rounded-double cursor as the importer, so a given `Song` yields identical row offsets live and offline.
- **Idempotency / dedup:** patch selection is de-duplicated per channel in the applier (a re-entered row does not re-select an unchanged patch), shared by both sinks.
- **Error handling:** constructor validates like the importer (non-null, `ChannelCount ∈ [1,16]`, positive tempo/speed). Post-construction structural defects are tolerated at runtime (skip/clamp), never thrown.

## 9. Quality Attributes & Trade-offs

- **Maintainability / DRY:** one cell-interpretation implementation feeds two schedulers. The alternative (duplicate the decision tree in the cursor) is rejected on the #1267 math below.
- **Performance:** the cursor is O(channels) work per row boundary + a straight `synth.Read` between boundaries — lighter than compiling a schedule, and allocation-free steady-state.
- **Playhead consistency (trade-off):** `OrderIndex`/`Row` are exposed as plain `int`s reflecting the last-applied sounding row. A cross-thread UI reader (Godot `_Process` vs. the audio thread) may observe the pair up to one audio block stale, and — at an order boundary — could in principle read a straddled `(newOrder, oldRow)` pair for one frame. For a playhead this is a sub-frame cosmetic effect. **Rejected alternative:** packing the pair into one atomic `long` (or `Interlocked`). It removes the straddle but adds encode/decode and 64-bit-atomicity caveats on `netstandard2.0`; not worth it for a playhead. If glitch-free cross-thread readout is ever required, revisit (OQ-3).
- **Seek fidelity (trade-off):** simple re-seed vs. scan-from-start state reconstruction — the simple form ships (§7 / OQ-1); reconstruction is deferred until the editor demonstrates a concrete need.

## 10. Risks & Mitigations · PR #42 recommendation

| Risk | Mitigation |
|---|---|
| Importer refactor regresses offline playback | The importer's existing tests are 100 %-line and behaviour-locked; the refactor is behaviour-preserving and re-run green. |
| Audio thread throws on a mid-edit malformed song | Every boundary tolerates structural defects (skip/clamp); the cursor never throws. Covered by malformed-song tests. |
| Live/offline timing divergence | Identical rounded-double cursor math; covered by a parity test asserting the cursor's row boundaries equal the importer's offsets. |
| Enum-value collision with PR #42 | Resolved by the PR #42 recommendation below. |

**PR #42 recommendation — CLOSE.** PR #42 (design #7494) adds `JumpToOrder=3` + a `TrackerImport{LoopStart,LoopEnd}` return type + an importer-side sample-offset loop region. Its entire payload is *looping for the RealtimeSequencer path*. With live playback on the cursor, looping is a cursor jump; the importer-side loop region has **no live consumer**, and offline export renders **finite** (a file cannot loop forever) so it has **no offline consumer either**. Keeping #42 open also guarantees a merge conflict on the enum. Therefore:

- **Close PR #42.** This PR re-introduces `JumpToOrder=3` (identical value, format-compatible) and implements it as a cursor jump — satisfying the task's "keep the enum value."
- Design #7494 stays on record. If **loop-point export metadata** (so a game can loop an exported WAV at engine level) ever becomes a concrete need, revive the `TrackerImport` seam then, as its own PR (OQ-4). Deferring it now is YAGNI-clean.

## 11. Patch enumeration (task item 7) — its OWN PR, designed here

The editor's instrument picker needs to list available patches. Today `SoundBank` holds `byBank: bank → (program → IPatch)` and a flat `Patches` list, but exposes no `(bank, program)`-keyed enumeration, and `IPatch` carries **no name** (`IPatch.cs` has only `StartVoice`). Design:

- Add a read-only enumeration to `SoundBank` yielding `(int Bank, int Program)` pairs in bank/program order (trivially derived from the existing `byBank` map). Shape: a small POD (`PatchReference` struct or equivalent) + an `IEnumerable<PatchReference> EnumeratePatches()`.
- **Name source (open):** `IPatch` has no name. Options: (i) enumerate `(bank, program)` only and let the editor label via the tracker `Instrument.Name` it already stores; (ii) capture preset names at load time (SF2 presets have names) and carry them into the bank. Recommend (i) for the first cut (zero new load-path coupling); revisit if the picker needs synth-side names.
- **Ships as a separate PR** (`feature/soundbank-patch-enumeration`), per one-feature-one-PR. It is independent of the sequencer.

## 12. Pre-Design Checklist (#1136 §5)

**KISS / DRY / YAGNI**
- No new type mirroring an existing type. `ITrackerCellSink` is a genuine 2-implementation seam (live + offline), not a mirror.
- No abstraction with one implementation: `ITrackerCellSink` has two concrete sinks, both built in this PR.
- No element justified by "might need later": seek reconstruction, forward-jump-skip, atomic playhead packing, and loop-point export are all *deferred with named OQs*, not built.
- No deprecation window / feature flag / shim.
- DRY math (below): stated for the extract decision.

**Existing systems first**
- The cursor is a genuinely different scheduler (live, positional, editable) that the compiled-schedule `RealtimeSequencer` cannot be, without becoming the transform we are removing. The Timeline path is retained, not duplicated (§6).
- The shared applier is the *anti*-duplication move: it removes copy of the importer's decision tree rather than adding a parallel layer.

**Configurability** — no new config knobs. Velocity (127) and full-volume (64) stay `const` in the applier, exactly as in the importer today.

**Less is better**
- `TrackerSequencer` — can't be deleted (it is the feature); can't be merged into `RealtimeSequencer` (opposite scheduling model); can't be inlined.
- `ITrackerCellSink` / `TrackerCellApplier` — can't be deleted (removing → 84-line duplication, math below); can't be merged (decision vs. emission are distinct responsibilities, and the two sinks target different systems).
- Exposed playhead pair (`currentOrder`/`currentRow`) distinct from the walk pointer — can't be merged with the raw walk pointer without leaking transient mid-walk rollover values to the UI; kept as 2 ints.

**Document discipline** — cites #114 and #1136 as load-bearing; out-of-scope listed (§2); supersession of PR #42 stated (§10); no multi-paragraph "why X stays" filler.

### DRY math (#1267) — the extract decision

The importer's cell-interpretation block is `EmitCell` (~24 content lines) + `ApplyPatch` (~13) + `ReleaseActive` (~5) ≈ **42 lines**. The cursor needs the *same* decision tree at a second site. Inlining both = `42 × 2 = 84` lines, far above the ~15–20 threshold. **Extract.** Named-helper test: "cell applier" / "cell sink" — nameable in 1–3 words, clear single responsibility. The extracted `TrackerCellApplier.Apply` (~30 lines) + two sinks (~8 lines each) replace the duplication with one decision implementation and two thin target adapters. Decision: **extract** (this is DRY, not premature abstraction — the second consumer exists now).

### KISS math — per new element

- `ITrackerCellSink`: delete → 84-line dup; merge → conflates decision with emission; **must stay** (2 impls, 2 consumers, both present).
- `TrackerCellApplier`: delete → dup; inline → dup; **must stay**.
- `SynthCellSink` / `TimelineCellSink`: each is the minimal target-specific emission; can't merge (different systems); **must stay**.
- `TrackerSequencer`: the feature; **must stay**.

### YAGNI — deferred, not built

Per-tick DSP engine · seek state-reconstruction · forward-jump-skip · atomic playhead packing · loop-point export metadata · patch-name capture. Each is a named OQ or follow-up, none built here.

## 13. Open Questions

- **OQ-1 (seek fidelity):** is re-seed-to-defaults acceptable for "play from row N", or does the editor need accurate mid-song timing/instrument reconstruction at the seek target? Simple form ships now.
- **OQ-2 (forward jump):** keep forward `JumpToOrder` ignored (current design, consistent with offline), or honour it as a live forward skip? Ignored ships now.
- **OQ-3 (playhead atomicity):** is an eventually-consistent, possibly-one-frame-straddled `(order,row)` playhead acceptable, or is glitch-free cross-thread readout required? Plain-int ships now.
- **OQ-4 (loop-point export):** will offline WAV export need loop-point metadata (revive #7494's `TrackerImport` seam)? Deferred as YAGNI.
- **OQ-5 (patch names):** for the picker (§11), enumerate `(bank,program)` only (editor labels via `Instrument.Name`), or capture synth-side preset names at load? Recommend the former first.

## 14. Implementation Guidance / Build Phases

1. **Shared seam.** `ITrackerCellSink` (5 verbs) → `TrackerCellApplier` (decision tree + state + `Reset`) → `TimelineCellSink` → `SynthCellSink`. One type per file.
2. **Refactor the importer** onto `TrackerCellApplier` + `TimelineCellSink`; keep the seed init + timing pre-pass in the importer; re-run its existing tests green (behaviour lock).
3. **Enum append** `TrackerEffectCommand.JumpToOrder = 3` (≤2-line summary).
4. **`TrackerSequencer`** — cursor fields, `Read` pull loop, `EnterRow` (validate/skip/loop/stop + pre-pass + apply), `AdvanceRow` (jump/row++), transport (`Play`/`Stop`/`SeekTo`/`Looping`), playhead (`OrderIndex`/`Row`/`IsPlaying`).
5. **Tests** (new-line 100 %, tool-measured): timing parity vs. importer; cell application via a recording fake synth (instrument/volume/patch-dedup/release-prior/off/cut/slot-out-of-range); order rollover; invalid + malformed order skip; end-of-order loop vs. stop; `JumpToOrder` backward loop / self / forward-ignored / last-wins; transport play/stop/seek; live-edit (mutate a cell → next pass reflects it); stall-guard (underfilling fake synth); end-to-end audible render with the real `Synthesizer` (non-silent, bounded).

**Follow-up PRs (named):** patch enumeration (§11); optionally seek reconstruction / forward-skip / loop-point export if the OQs resolve toward building them.
