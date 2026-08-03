# Design: Per-Tick Tracker Effect Engine

> Repo mirror (source of truth): `docs/architecture/tracker-tick-effects.md` on branch `feature/tracker-tick-effects`.
> DiVoid task #7453 · sequencer design #7503 (PR #43, merged) · tracker format design #7452 (PR #41, merged) · project #6128.
> Load-bearing: Code Contracts #114 §0, Design Contracts #1136 (§1 KISS/DRY/YAGNI). Governing comment/XML-doc contract: #2051.
> Scope: the **per-tick DSP effect engine** only. Position effects (JumpToOrder) already shipped in PR #43 as a cursor jump.

---

## 1. Problem Statement

The merged `TrackerSequencer` (PR #43, design #7503) walks a live `Song` with a direct cursor over `(order, row, sampleWithinRow)` and applies each row's cells **once, at the row boundary**, through the shared `TrackerCellApplier`. It interprets only three effect commands (SetSpeed, SetTempo, JumpToOrder). Everything a tracker musician expresses *between* row boundaries — the sub-row modulation that gives tracker music its character — is absent.

Task #7453 §2 (verbatim):

> **Per-tick DSP effects (larger, second):** arpeggio, volume/pitch slides, vibrato, retrigger, note-delay, tick-cut — require sub-row (per-tick) event emission on the speed(ticks/row) grid the v1 timing model already established. Extend the importer to emit intra-row events at tick offsets; add the corresponding `TrackerEffectCommand` values (enum appends, format-compatible).

The timing model already reserves the room: `speed` = **ticks per row**, `samplesPerRow = speed × sampleRate × 2.5 / tempo` (tracker-format design #7452). A row is `speed` ticks; today the cursor renders a row as one chunk and applies cells only at its start. This design **subdivides the row into its `speed` ticks** and runs a per-tick effect step at each tick boundary, translating running effect state into the **existing** `ISynthesizer` per-channel control calls.

**Success criteria:** a song carrying the v1 effect commands hears volume/pitch slides, arpeggio, vibrato, retrigger and note delay/cut when played live through `TrackerSequencer`, produced entirely through the current synth control surface — **no `Synthesizer` DSP changes**.

## 2. Scope & Non-Scope

**In scope**
- Subdividing `TrackerSequencer.Read` from row-sized chunks into `speed` tick-sized chunks (no-drift sample accounting preserved).
- A new `TrackerEffectEngine`: per-channel effect state machine + effect-parameter memory, driven by the cursor per tick, expressed through existing `ISynthesizer` calls.
- A behaviour-preserving refactor of `TrackerCellApplier` to let the engine apply a cell's controls and note **separately** (for note-delay and tone-portamento) and read a channel's active key (for retrigger).
- A focused v1 effect set with `TrackerEffectCommand` enum appends (format-compatible) and `EffectParam` nibble layouts (see §8, surfaced to Toni).

**Out of scope (explicit)**
- Any `Synthesizer` / voice / DSP change. Effects are expressed **only** through existing per-channel control calls.
- Offline per-tick lowering in `TrackerTimelineImporter` — resolved **live-only for v1** (§7). The offline path is unchanged; unknown/new effect commands continue to pass through it uninterpreted (graceful degradation).
- Position effects (JumpToOrder shipped in PR #43). **PatternBreak (Cxx)** is *not* a per-tick effect; recommendation in §12.
- The long tail of tracker effects (fine slides, tremolo, panning/pan-slide, vibrato-waveform select, sample-offset, pattern-loop SBx, global volume, etc.) — named as follow-ups in §12.
- No new sink interface, no `ITrackerCellSink` growth (justified in §5).
- `.mod`/`.s3m`/`.it` file parsing (the format is the in-engine POD model).

## 3. Assumptions & Constraints

| # | Assumption / Constraint | Source | Confidence |
|---|---|---|---|
| C1 | Effects use **only** existing `ISynthesizer` calls (`SetChannelPitchBend`, `SetChannelGain`, `NoteOn`, `NoteOff`, `SilenceChannel`). No synth changes. | Brief §3, task #7453 | Hard constraint |
| C2 | `speed` = ticks per row; `samplesPerTick = samplesPerRow / speed`. | Design #7452 | Verified in code |
| C3 | One effect column per cell → at most one effect per channel per row. No effect-mixing on a channel. | Format design #7452 | Verified |
| C4 | Single-threaded: `Read` and all mutations share one thread (same discipline as today). | Design #7503 §3 | Verified |
| C5 | `SetChannelPitchBend` and `SetChannelGain` are **glided, not stepped** (ISynthesizer XML docs). This is the key risk for *fast* pitch effects (arpeggio). | ISynthesizer.cs:28,35 | Verified — see §11 R1 |
| A1 | The `2.5` tick-seconds scale and `double`-cursor no-drift accumulation are correct and stay. | Design #7452 | High |
| A2 | Pitch effects work in **semitone** space (the synth's pitch-bend unit), not classic MOD "period" units. This reinterprets the muscle-memory of S3M/IT authors → needs Toni (§8, §13). | Architect call | Needs Toni |

## 4. Architectural Overview

```
 TrackerSequencer  (transport + timing + validation + row/tick clock)
   Read(dest):
     per row boundary (tick 0 of the row):
        EnterRow(): validate order/pattern/row
                    ScanTimingAndJump()   -> SetSpeed/SetTempo/JumpToOrder (row-level, unchanged)
                    compute rowSpr, tick schedule (round(rowStart + t*sprPerTick))
                    engine.EnterRow(pattern,row,song)   <-- tick-0 cell application + effect arm
     for tick t = 0 .. speed-1:
        if t>0: engine.Tick(t)                          <-- per-tick effect advance
        pull tickSamples[t] frames from synth into dest
     row end: advance cursor; jump or row++

 TrackerEffectEngine  (per-channel effect state machine, LIVE-only)
   owns TrackerCellApplier (base cell decoding, shared with offline)
   EnterRow: for each channel  -> decode effect+param, update per-channel param memory,
                                  decide trigger discipline, apply base cell via applier,
                                  arm running effect state (tick-0 portion)
   Tick(t):  for each channel  -> advance active effect, push control calls to ISynthesizer,
                                  fire scheduled retrigger / cut / delayed-trigger
       drives ISynthesizer directly: SetChannelPitchBend / SetChannelGain / NoteOn / SilenceChannel
       calls applier for delayed-trigger and tone-porta control application

 TrackerCellApplier  (UNCHANGED responsibility: effect-agnostic cell decoding)
   Apply = ApplyControls (+ ApplyNote)   [refactor: split into two composable public methods]
   ActiveKey(channel) : int              [new read-only accessor, for retrigger/porta source]

 ISynthesizer  (UNCHANGED)
 TrackerTimelineImporter (offline)  (UNCHANGED — new effects pass through ignored)
```

The design adds **one new component** (`TrackerEffectEngine`), makes **one behaviour-preserving refactor** (`TrackerCellApplier` method split + one accessor), and **restructures one method's clock** (`TrackerSequencer.Read`). The applier stays effect-agnostic — all effect *interpretation* lives in the engine; the cursor owns *timing*, the engine owns *effect state*.

## 5. Components & Responsibilities

### 5.1 `TrackerEffectEngine` (new)

**Owns:** per-channel running effect state and effect-parameter memory; the translation of that state into synth control calls each tick.

**Per-channel state (conceptual fields, one per channel):**

| State | Meaning | Lifecycle |
|---|---|---|
| `pitchOffset` (semitones) | Persistent pitch bend accumulated by portamento up/down and tone-portamento. | Persists across rows; **reset to 0 on a fresh NoteOn** (except tone-porta). Classic tracker: slides stick, a new note re-centres. |
| `portaTarget` (semitones, nullable) | Tone-portamento destination relative to base. | Set when a Gxx cell carries a note; cleared when reached. |
| `volumeLevel` (0..64) | Current tracker volume for volume-slide arithmetic. | Initialised from the cell's Volume column / carried; slid by Dxy. |
| `vibratoPhase`, `vibratoRate`, `vibratoDepth` | Vibrato oscillator. | Phase advances per tick while active; phase resets on a fresh note (v1: always reset — waveform-retrigger flag is deferred). |
| `arpOffsets` (2 semitone steps) | Arpeggio's 2nd/3rd semitone offsets. | Valid for the arpeggio row only; pitch returns to `pitchOffset` when arpeggio stops. |
| `retriggerInterval`, `retriggerCounter` | Retrigger scheduling in ticks. | Valid for the row; counts ticks. |
| `cutTick` / `delayTick` + held cell | Scheduled note-cut / note-delay tick and (for delay) the withheld cell. | One row. |
| `paramMemory[effect]` | Last **non-zero** `EffectParam` seen per effect command on this channel. | Persists across rows (classic "00 = reuse last param"). |

**Two entry points (both iterate all channels):**
- `EnterRow(pattern, row, song)` — the **tick-0** step. For each channel: decode `cell.Effect`/`cell.EffectParam`, apply the `00 = reuse` memory rule, choose the trigger discipline, apply the base cell through the applier accordingly, and arm the effect's running state (including any tick-0 pitch/volume it dictates).
- `Tick(tickIndex)` — the **tick 1..speed-1** step. For each channel: advance the active effect and push the resulting control call; fire a scheduled retrigger (`NoteOn`), cut (`SilenceChannel`) or delayed trigger (applier note apply) when its tick arrives.

**Drives:** `ISynthesizer` **directly** for the continuous/live-only controls (pitch bend, gain, note on/off, silence), and the applier for base-cell decoding.

**Does NOT own:** timing (rowSpr, tick schedule, cursor), transport, order/row validation, or the SetSpeed/SetTempo/JumpToOrder scan — those stay in `TrackerSequencer`.

### 5.2 `TrackerCellApplier` (behaviour-preserving refactor)

Today `Apply` does, in order: (1) latch instrument, (2) Volume→`SetGain`, (3) note handling (patch, release-prior, `NoteOn`/`NoteOff`/`Silence`). Refactor into two composable public methods, **with no behaviour change and no effect awareness added**:

| Method | Content | Callers |
|---|---|---|
| `ApplyControls(cell, channel, song)` | steps (1)+(2): instrument latch + Volume→gain. | tone-porta (controls only, no retrigger) |
| `ApplyNote(cell, channel, song)` | step (3): patch + note verbs. | delayed-note firing |
| `Apply(cell, channel, song)` | = `ApplyControls` then `ApplyNote` (current signature, unchanged). | **offline importer, and normal live cells — both unchanged** |

Add one read-only accessor: **`ActiveKey(channel) : int`** (returns the sounding key, `-1` if none) — the engine reads it for retrigger (`NoteOn` the same key) and for tone-portamento's slide source.

The applier still **never reads `cell.Effect`** — it remains the effect-agnostic, offline/live-shared primitive. All effect logic is in the engine and the cursor's orchestration. This split introduces **no duplication** (the two new methods are called by `Apply` via composition; DRY math is N/A — no block is inlined at multiple sites).

### 5.3 `TrackerSequencer` (clock restructure)

Restructure `Read` to iterate ticks within a row instead of rendering a whole row as one chunk:
- `EnterRow` keeps validation + `ScanTimingAndJump` (SetSpeed/SetTempo/JumpToOrder — **unchanged**), computes `currentRowSpr`, and derives the tick schedule.
- Tick boundaries: `tickStart[t] = round(rowStartCursor + t × sprPerTick)`, `sprPerTick = currentRowSpr / speed`; `tickSamples[t] = tickStart[t+1] − tickStart[t]`. Sum over `t` = `rowSamples` exactly (no drift; same rounding discipline as the existing per-row cursor).
- At tick 0: `engine.EnterRow(...)`. At tick t>0: `engine.Tick(t)`. Then pull `tickSamples[t]` frames from the synth into the destination.
- Row end: advance `rowStartCursor += currentRowSpr`, then jump or `row++` (unchanged).

**Owns:** transport, order/row validation, the timing scan and cursor, tick subdivision, audio pull. Delegates all cell + effect application to the engine.

## 6. Interactions & Data Flow

Per-row conceptual sequence (one channel shown; the engine iterates all channels at each step):

```
tick 0  cursor.EnterRow -> scan speed/tempo/jump; compute tick schedule
        engine.EnterRow:
           decode effect; param 00 -> reuse paramMemory[effect]
           trigger discipline:
             Normal (no effect / additive effect) : applier.Apply(cell)         ; reset pitchOffset on NoteOn
             TonePortamento (Gxx w/ note)         : applier.ApplyControls(cell) ; portaTarget = note ; NO retrigger
             NoteDelay (SDx)                      : hold cell ; delayTick = x   ; no apply yet
           arm running effect state (vibrato/arp/slide deltas, retrigger/cut counters, volumeLevel)
        cursor: pull tickSamples[0] frames
tick t  engine.Tick(t):
           VolumeSlide  : volumeLevel += delta ; SetChannelGain(volumeLevel/64)
           PortamentoUp/Down / TonePorta : pitchOffset += step (toward target for porta) ; SetChannelPitchBend
           Arpeggio     : SetChannelPitchBend(pitchOffset + arp[ t % 3 ])
           Vibrato      : vibratoPhase += rate ; SetChannelPitchBend(pitchOffset + sin(phase)*depth)
           Retrigger    : if (t % interval == 0) NoteOn(channel, ActiveKey, velocity)
           NoteCut      : if (t == cutTick) SilenceChannel(channel)
           NoteDelay    : if (t == delayTick) applier.ApplyNote(heldCell)
        cursor: pull tickSamples[t] frames
row end  advance cursor; jump or row++
```

All calls are synchronous, single-threaded, in-process. No queues, no events, no async — the cursor pulls audio and drives control calls in the same call stack as `Read`.

## 7. Interaction with existing directives and the offline path

**SetSpeed / SetTempo / JumpToOrder (row-level, unchanged).** These stay in `TrackerSequencer.ScanTimingAndJump`, run once per row at tick 0, and set `speed`/`tempo`/`pendingJump`. `speed` *is* the tick count, so a SetSpeed changes how many tick subdivisions subsequent rows have. Tick-indexed effects (note-delay tick x, note-cut tick x, retrigger interval) operate within the current row's `speed`; a tick index ≥ `speed` simply never fires (classic behaviour: a delay past the last tick silences the note). JumpToOrder still fires at row end (last-valid-wins), after the row's ticks have played.

**Offline `TrackerTimelineImporter` — per-tick lowering is LIVE-ONLY for v1. RESOLVED.**

The importer is **unchanged**. Its per-row pre-pass interprets only SetSpeed/SetTempo; the new effect commands fall through and are ignored, exactly as unknown commands already are (format design #7452: "unknown/unnamed values are legal and pass through uninterpreted"). A song with per-tick effects renders its **base notes** offline (no sub-row modulation) and its **full effects** live. This is graceful degradation, not breakage.

Justification (KISS/YAGNI, Design Contracts #1136 §1, §4):
1. **No present consumer.** The named, driving use case for #7453 is *live* in-engine BGM. Offline WAV export (design #7503 §3) is a retained secondary path; nobody has asked for effect-accurate offline renders.
2. **The Timeline is a discrete-event model, not a per-tick DSP model.** Lowering per-tick effects offline means emitting an absolute-value control event at *every tick* of *every* effect-bearing note (a vibrato = one pitch-bend event per tick for the note's whole duration) — massive timeline bloat — **and** it still requires the same per-tick state machine to compute those values. So no logic is saved; it is merely relocated, at the cost of a second, parallel effect processor. That is a §4 "compromise shape" with no consumer.
3. **Consistency is preserved for the common case.** Effect-free songs render identically live and offline (both go through the same `TrackerCellApplier`), which is the property #7503 §3 guarantees. Only effect-bearing songs differ, and only in the sub-row modulation.

Named follow-up (§12): *offline per-tick lowering* — to be built **only if** an effect-accurate WAV-export use case becomes concrete, at which point the engine's per-tick state machine can feed a `TimelineCellSink`-style adapter with the actual shape in hand.

## 8. Contracts & Interfaces (Abstract) — the v1 effect set

`TrackerEffectCommand` is a `byte`-backed open enum; append-only keeps the format compatible. Current values: `None=0, SetSpeed=1, SetTempo=2, JumpToOrder=3`. Proposed appends:

| Value | Command | Param (`EffectParam` byte) | Semantics (per tick unless noted) | Ticks active | S3M/IT analogue |
|---|---|---|---|---|---|
| 4 | **VolumeSlide** | hi nibble = up/tick, lo nibble = down/tick (hi>0 ⇒ up, else down) | `volumeLevel += ±nibble`, clamp [0,64]; `SetChannelGain(volumeLevel/64)` | 1..speed-1 | Dxy |
| 5 | **PortamentoUp** | pitch step per tick | `pitchOffset += step`; `SetChannelPitchBend` | 1..speed-1 | Fxx (E/F) |
| 6 | **PortamentoDown** | pitch step per tick | `pitchOffset -= step`; `SetChannelPitchBend` | 1..speed-1 | Exx |
| 7 | **TonePortamento** | slide speed toward target | cell note = target; slide `pitchOffset` toward target, **no retrigger** | 1..speed-1 | Gxx |
| 8 | **Arpeggio** | hi nibble = 2nd offset, lo nibble = 3rd offset (semitones) | `SetChannelPitchBend(pitchOffset + [0,hi,lo][t%3])` | **0..speed-1** | 0xy (Jxy) |
| 9 | **Vibrato** | hi nibble = rate, lo nibble = depth | `SetChannelPitchBend(pitchOffset + sin(phase)*depth)`; phase += rate | **0..speed-1** | Hxy |
| 10 | **Retrigger** | tick interval | every `interval` ticks: `NoteOn(channel, ActiveKey, velocity)` | 1..speed-1 | Qxy (Rxy) |
| 11 | **NoteCut** | tick to cut | at `t == param`: `SilenceChannel(channel)` | the cut tick | SCx |
| 12 | **NoteDelay** | tick to trigger | withhold the cell's note; at `t == param`: apply note | the delay tick | SDx |

**Effect-parameter memory:** for every command, `param == 0` reuses the last non-zero param seen for that command on that channel (`paramMemory[effect]`), per classic tracker semantics.

**Units that need Toni's tracker-domain input (see §13):**
- **Pitch-effect unit (rows 5–7, 9-depth).** Classic MOD/S3M portamento and vibrato are expressed in **period units** (non-linear in pitch). This synth's `SetChannelPitchBend` is in **semitones** (linear). v1 proposes interpreting the param in **semitone fractions** — e.g. portamento step = `param/16` semitone per tick (`0x10` = 1 semitone/tick), vibrato depth = `lo/8` semitone. This is musically reasonable but *feels different* from period-based values an S3M/IT author has in muscle memory. **This is the primary decision to confirm with Toni.**
- **Retrigger nibble layout (row 10).** IT's `Qxy` uses `x` = volume-change variant, `y` = interval. v1 proposes `param` = interval only (`x` reserved) for simplicity. Confirm whether the volume-change variant is wanted in v1.

## 9. Cross-Cutting Concerns

- **No synth changes (C1):** every effect resolves to `SetChannelPitchBend`, `SetChannelGain`, `NoteOn`, `NoteOff`, `SilenceChannel` — all existing. Verified against `ISynthesizer.cs`.
- **No-drift timing:** tick boundaries are `round()` of the fractional cursor; per-tick sample counts sum to the row's exactly. Identical discipline to the existing per-row cursor — bounded < 1 sample error at every boundary, deterministic.
- **Thread-safety:** unchanged single-thread contract (C4). No locks, no snapshots (YAGNI, per #7503 §3).
- **Audio-thread never throws:** the engine reads only already-validated indices (the cursor validates order/pattern/row before calling `EnterRow`); effect params are bytes (no range faults). A tick index ≥ `speed` is a silent no-op, not an error.
- **Live-edit contract (unchanged):** the cursor still reads `Song`/`Pattern`/`Cell` fresh at each row boundary; a mid-row edit is observed at the next row entry; a sounding note (and its running effect) is unaffected until re-entry.
- **State reset on Seek/Stop:** `TrackerSequencer.SeekTo` already resets the applier; it must **also reset the effect engine** (clear per-channel running state; param memory reset is the safe choice on a hard seek). `Stop` silences channels; running effect state is cleared on the next `Play`+`EnterRow` (fresh arm).

## 10. Quality Attributes & Trade-offs

| Attribute | How addressed |
|---|---|
| **Simplicity (KISS)** | One new component, one behaviour-preserving refactor, one clock restructure. No new sink, no new abstraction with a single implementation, no offline duplication. |
| **DRY** | Base cell decoding stays single-sourced in the applier (shared offline/live). The engine drives the same synth the live sink does — no parallel note logic. No block inlined at >2 sites → no extraction math needed. |
| **Extensibility** | New effects = append an enum value + a case in `EnterRow`/`Tick`. The open byte enum + effect-agnostic applier absorb growth with zero format churn. |
| **Performance** | Per tick: a handful of arithmetic ops + control calls per channel. Ticks are ~ms-scale; negligible vs. the synth's per-sample DSP. |

**Trade-off — the effect engine drives `ISynthesizer` directly instead of via a sink (§5.1).**
The applier's `ITrackerCellSink` seam exists to route the *same cell decisions* to offline (`TimelineCellSink`) or live (`SynthCellSink`). Per-tick effects have **no offline counterpart in v1** (§7) and need `SetChannelPitchBend`, which is *not* a cell verb. Putting pitch-bend on `ITrackerCellSink` would force `TimelineCellSink` to grow a method the offline path never calls, muddying the sink's single responsibility with a continuous-control concern — a §4/§6 YAGNI wart. **Call:** the engine talks to `ISynthesizer` directly. The live sink (`SynthCellSink`) is itself a thin passthrough to the same synth, so there is no behavioural divergence — the effect calls and the base-cell calls land on the same object. If offline effect lowering ever becomes concrete, the engine's state machine is the reusable piece; the sink question is revisited *then*, with the consumer's shape known.

**Trade-off — the applier is split (`ApplyControls`/`ApplyNote`) rather than left whole.**
Tone-portamento needs the cell's instrument/volume applied but its note *not* retriggered; note-delay needs the note applied on a later tick. Both need the note step separable from the controls step. The alternative (post-correcting a wrongly-triggered note with a NoteOff) is uglier and audibly wrong. The split is behaviour-preserving (`Apply` still composes both) and adds no effect knowledge to the applier. **Call:** split.

**Rejected alternative — make the applier effect-aware.** Rejected: it would push tracker-effect interpretation into the offline/live-shared primitive, coupling the offline path to per-tick concepts it never runs. The engine isolates all effect logic instead.

## 11. Risks & Mitigations

| # | Risk | Mitigation |
|---|---|---|
| **R1** | **`SetChannelPitchBend` is glided, not stepped (C5).** Fast per-tick pitch effects — **arpeggio** above all (pitch changes every single tick, ~ms apart) — may be smeared/softened by the glide. Vibrato and portamento are gradual and tolerate gliding; arpeggio is the sharp case. | v1 accepts the glide and **evaluates audibly** (John renders an arpeggio proof; Toni judges). If the smear is unacceptable, arpeggio is **deferred** to a follow-up — a stepped pitch-set would be a synth addition, which C1 forbids in this PR. **Flagged for Toni (§13).** John should confirm the glide time-constant vs. tick duration during implementation. |
| R2 | Semitone reinterpretation of period-based params (A2) feels wrong to S3M/IT authors. | §8 proposes concrete units; Toni confirms/adjusts before John hard-codes the scale (§13). Isolated to one constant per effect — cheap to retune. |
| R3 | Effect + JumpToOrder in the same row: running effect state leaks past the jump. | Effect state is armed per row in `EnterRow`; the jump only changes which row is entered next, which re-arms. No leak. |
| R4 | Retrigger/tone-porta read a stale `ActiveKey` after a note-off row. | `ActiveKey` returns `-1` when nothing sounds; the engine no-ops retrigger/porta when key is `-1`. |
| R5 | Scope creep into the effect long tail. | v1 set is fixed in §8; everything else is an explicit §12 follow-up. |

## 12. Migration / Rollout & Follow-ups

This is additive: existing songs (no per-tick effects) play **bit-identically** — the tick subdivision of a row renders the same total samples, and with no effect armed the only tick-0 action is the unchanged base-cell application. No migration of existing data.

**PatternBreak (Cxx) — recommendation.** PatternBreak is a *position* effect (end pattern early, advance to next order at a row offset), **not** a per-tick DSP effect. It is trivial (an enum value + a few lines in `ScanTimingAndJump`/`AdvanceRow`, reusing the pending-jump machinery). To keep this PR cleanly = "the per-tick engine" (one-feature-one-PR), the recommendation is a **separate tiny follow-up PR** alongside the position-effect family. It is ready-to-bundle if the operator prefers; flagged as an operator decision, not folded silently.

**Named follow-ups (not built here):**
- Offline per-tick lowering in `TrackerTimelineImporter` (only if effect-accurate WAV export becomes concrete — §7).
- PatternBreak (Cxx) position effect (above).
- Effect long tail: fine slides (DFx/DxF, EEx/FEx), tremolo, panning + pan-slide, vibrato-waveform select & retrigger flag, sample-offset (Oxx), pattern-loop (SBx), global volume/volume-slide, tremor.
- Retrigger volume-change variant (IT Qxy `x` nibble), if §8 defers it.
- Arpeggio, **iff** R1's glide smear proves unacceptable and it is pulled from v1.

## 13. Open Questions (need Toni's tracker-domain input, not an architect call)

1. **Pitch-effect unit (primary).** Confirm the semitone-fraction interpretation of portamento step / vibrato depth (§8, A2), or specify the exact param→pitch mapping you want. Period-based authors will have strong intuition here.
2. **Arpeggio under a glided pitch-bend (R1).** Accept the glide for v1 and evaluate audibly, or pull arpeggio to a follow-up until a stepped pitch path exists? (A stepped path = a synth change, out of this PR's scope by C1.)
3. **Retrigger nibble layout (§8 row 10).** Interval-only (`x` reserved), or include IT's volume-change `x` variant in v1?
4. **Note-delay granularity.** v1 delays the **whole cell** (instrument+volume+note) to the delay tick. IT's SDx applies instrument/volume at tick 0 and delays only the note. Which do you want? (Trivial to switch — `Apply` vs `ApplyControls`@0 + `ApplyNote`@x.)
5. **PatternBreak bundling (§12).** Separate tiny PR (recommended) or fold into this one?

## 14. Implementation Guidance for the Next Agent (John)

Build order (each step compiles + is testable before the next):

1. **Enum appends.** Add values 4–12 to `TrackerEffectCommand` with ≤2-line XML summaries each (contract #2051). Format-compatible; no other change.
2. **Applier refactor (behaviour-preserving).** Split `Apply` → `ApplyControls` + `ApplyNote` (compose in `Apply`); add read-only `ActiveKey(channel)`. Existing applier tests must stay green **unchanged** — this is a pure refactor. Offline importer caller is untouched.
3. **`TrackerEffectEngine` skeleton.** Per-channel state (§5.1 table) + param memory + the `00 = reuse` rule. `EnterRow` initially does only the trigger-discipline routing (Normal path = `applier.Apply`), `Tick` a no-op. Verify a no-effect song is bit-identical to today.
4. **Cursor tick subdivision.** Restructure `TrackerSequencer.Read` to the tick clock (§5.3), calling `engine.EnterRow`/`engine.Tick`. Wire `SeekTo` to reset the engine (§9). Verify no-effect songs unchanged; verify per-tick sample counts sum to the row (no drift).
5. **Additive effects (lowest risk first):** VolumeSlide → PortamentoUp/Down → Retrigger → NoteCut → NoteDelay. Each: one `EnterRow` arm + one `Tick` case + a targeted test (slide direction/clamp, retrigger count, cut/delay tick).
6. **Tone-portamento** (uses `ApplyControls` + `portaTarget`, no retrigger; source = `ActiveKey`).
7. **Vibrato**, then **Arpeggio last** (R1 — render an audible proof; hold for Toni's judgement on the glide before committing arpeggio to v1).
8. **End-to-end proof:** a short song exercising each v1 effect renders non-silent, bounded audio through `TrackerSequencer`; the pitch/gain call sequence matches the §8 semantics (assert via a recording synth/spy).

Respect: no `Synthesizer`/voice/DSP edits; no new sink; offline importer untouched; comment contract #2051 (summaries ≤2 content lines); one type per file.

---

## Pre-Design Checklist (#1136 §5) — answered

- **No mirror type / parallel enum:** the effect set extends the existing `TrackerEffectCommand` (append-only); no parallel enum. ✓
- **No abstraction with one impl and no second:** the engine drives `ISynthesizer` directly rather than inventing a single-impl control interface (§10). No new sink. ✓
- **No "might need later" element:** offline per-tick lowering is explicitly deferred with a named trigger (§7, §12); arpeggio is conditional on an audible check, not speculation. ✓
- **No deprecation window / shim:** additive; existing songs bit-identical. ✓
- **DRY math:** no multi-line block inlined at >2 sites; the applier split is composition, not duplication. `block_size × site_count` N/A. ✓
- **Existing systems first:** reuses the applier (shared decoding), the existing synth control surface, the existing timing model and no-drift cursor; the *only* new persisted concept is per-channel effect state, which has no existing home. ✓
- **Configurability:** the pitch-unit scale and tick-seconds scale are `const` (named, in code), not config knobs — no operator/env difference justifies a knob (#1136 §3). ✓
- **Less is better:** each element passed delete/merge/inline — the engine can't be deleted (no per-tick home exists), can't merge into the applier without coupling offline to effects, can't inline into the cursor without bloating transport with effect state. ✓
- **Scope in/out named:** §2. **Out-of-scope listed explicitly:** §2, §12. ✓
- **Trade-offs explicit:** §10 (direct-synth vs sink; applier split; rejected effect-aware applier). ✓
- **Load-bearing contracts cited:** #114, #1136, #2051 (header). ✓
- **No superseded predecessor left live:** this design *extends* #7503/#7452; neither is superseded (both still describe shipped, current behaviour). ✓
