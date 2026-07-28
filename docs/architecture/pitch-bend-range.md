> **Repo path (source of truth):** `docs/architecture/pitch-bend-range.md` on branch `feature/pitch-bend-range` of `telmengedar/AudioSynth`. Ships in the same PR as the implementation (DiVoid #1165). This node is the graph-discoverable copy.

# Architectural Document: MIDI RPN 0 — per-channel pitch-bend range

**Author:** Sarah · **Date:** 2026-07-28 · **Source bug:** DiVoid #7209 · **Project:** #6128 · **Map root:** #6708.
**Predecessor design:** pitch-bend #7154 (this design lifts the RPN deferral it made). **Integration node:** MidiSequencer #7114.
**Load-bearing contracts:** Code Contracts #114 (§0 principles, §1 one-type-per-file, §4 comments), Design Contracts #1136 (§1 KISS/DRY/YAGNI, §2 existing-systems-first, §3 configurability, §4 less-is-better, §5 pre-design checklist).

---

## 1. Problem Statement

`MidiSequencer` decodes every channel's `PitchWheel` (0xE0) message into a signed semitone offset using a **single hardcoded ±2 semitone constant** (`PitchBendSemitoneRange = 2f`). MIDI channels may set a **non-default bend range** via **RPN 0** (Registered Parameter Number 0 = "Pitch Bend Sensitivity"): the standard `CC101=0, CC100=0, CC6=<semitones>` sequence. We ignore RPN entirely, so any channel that widens its range renders every bend **too shallow**.

Concrete failure (DiVoid #7209): in `1-10-Force_Your_Way.mid`, channel 6 (GM program 30, Distortion Guitar) sets bend range = **12 semitones** via RPN 0 at t≈68.4 s, then plays sustained low notes with full-scale PitchWheel sweeps — an intended **+1 octave whammy**. We render those sweeps as **+2 semitones (a 6× undershoot)**, so the guitar's pitch lands wrong and sounds detuned/sliding from ~1:08.

**Goal / success criteria:** the sequencer must honor RPN 0 so each channel's PitchWheel decodes against **its own** bend range. Success = the ch-6 guitar bends a full octave after 68.4 s (≈12-semitone sweeps, not ≈2), while any song that sends **no RPN** renders **bit-for-bit identically** to today (range stays 2).

## 2. Scope & Non-Scope

**In scope**
- A per-channel **RPN selector** state machine tracking CC101 (RPN MSB) and CC100 (RPN LSB).
- A per-channel **pitch-bend range** (semitones), written by CC6 (Data Entry MSB) **only while the selected RPN is (0,0)**. Default 2 (GM), matching today.
- **Range-aware PitchWheel decode**: `semitones = (value14 − 8192) / 8192 · range[channel]`.
- **RPN-null (127,127) closure** — handled *implicitly* by the selector (see §6), no special-case branch.

**Out of scope (stay deferred — YAGNI; this song exercises only RPN 0)**
- **RPN 1** (channel fine tuning), **RPN 2** (channel coarse tuning), **RPN 3/4/5**, any **NRPN** (CC98/CC99). No deliverable song sets them.
- **CC38 (Data Entry LSB / cents)** — fractional bend range. Force Your Way sets an integer 12; a cents component would add a second byte to combine for zero deliverable benefit. Deferred (§3, §7-O1).
- **Any engine (`ISynthesizer` / `Synthesizer` / `IVoice`) change.** The `SetChannelPitchBend(channel, semitones)` seam is MIDI-neutral and already carries a signed semitone offset — the range→semitones math is entirely the sequencer's concern (confirmed §5, §8).
- **Config knobs.** The GM default (2) is a fixed named constant, not tunable (§3).

## 3. Assumptions & Constraints

- **Decode altitude is the sequencer, not the engine.** `MidiSequencer.ApplyMessage` is the sole place PitchWheel bytes become semitones; the engine never sees raw MIDI. This is the correct and only seam that changes.
- **Per-render lifetime.** A `Synthesizer` is built fresh per `Render` call; per-channel sequencer state (like the existing `cc7[]`/`cc11[]` arrays) is local to `Render` and threaded into `ApplyMessage`. No cross-render leakage; no GM-reset loop needed for range (its default is the array's initial value).
- **CC6/CC100/CC101 are currently ignored** — they fall through the controller switch's final `else break`. There is no existing consumer to conflict with.
- **Bend range is a whole semitone count** for every deliverable song. Storing it as `float` (initialised to the existing `2f` default const) keeps the decode expression byte-identical to today when the range is unchanged.
- **Constraint — bit-identical no-RPN regression.** The decode multiply must keep the *same operand and order* so IEEE-754 produces identical results: `… / (float)PitchWheelSpan * range[channel]` where `range[channel]` is exactly `2f` for any channel that never selects RPN 0.

## 4. Architectural Overview

Two additions, both inside `MidiSequencer`, both following the **existing `cc7[]`/`cc11[]` threaded-array pattern** — no new type, no new file, no engine surface.

```
  Controller (0xB0) messages                        PitchWheel (0xE0) message
  ┌──────────────────────────────────┐              ┌──────────────────────────────┐
  │ CC101 (RPN MSB)  ─┐               │              │ value14 = (Data2<<7)|Data1   │
  │ CC100 (RPN LSB)  ─┼─► selectedRpn[ch]  (14-bit)  │ semitones =                  │
  │                   │   = which RPN is "armed"     │  (value14 − 8192)/8192        │
  │ CC6 (Data Entry) ─┘   if selectedRpn[ch]==0 →    │     · bendRange[ch]  ◄────────┼── reads range
  │                       bendRange[ch] = CC6 value  │ synth.SetChannelPitchBend(    │
  └──────────────────────────────────┘   writes ─►  └──ch, semitones)  (UNCHANGED)  ┘
                                       bendRange[ch]
   selectedRpn[] init = 16383 (RPN-null 127,127)  ─ no Data Entry captured until RPN 0 is armed
   bendRange[]   init = 2f (GM default)           ─ identical decode to today when untouched
```

- **`selectedRpn[16]`** — the "armed" RPN parameter number per channel (14-bit: `(msb<<7)|lsb`). Updated when CC101 or CC100 arrives. Initialised to **16383** (= RPN-null 127,127) so a stray Data Entry before any RPN select is ignored.
- **`bendRange[16]`** — the per-channel range in semitones. Written by CC6 **only when `selectedRpn[ch] == 0`**. Initialised to **2f** (reuse the existing `PitchBendSemitoneRange` const as the default seed).

## 5. Components & Responsibilities

| Component | Owns (after change) | Does NOT own |
|---|---|---|
| `MidiSequencer.Render` | Allocating + initialising `selectedRpn[]` (→16383) and `bendRange[]` (→2f) alongside the existing `cc7[]`/`cc11[]`; threading both into `ApplyMessage`. | Any RPN decode logic (delegated to `ApplyMessage`). |
| `MidiSequencer.ApplyMessage` | The RPN state machine: routing CC101/CC100 into `selectedRpn[ch]`; routing CC6 into `bendRange[ch]` gated on `selectedRpn[ch]==0`; decoding PitchWheel against `bendRange[ch]`. | Persisting state across renders; any per-voice pitch application. |
| `ISynthesizer` / `Synthesizer` / `IVoice` / voices | **Unchanged.** They already convert a signed semitone offset to a per-voice pitch ratio and fan it out. | Bend-range / RPN knowledge — they never learn RPN exists. |

Single-responsibility framing: the sequencer owns *"MIDI bytes → semitones,"* the engine owns *"semitones → audible pitch."* RPN 0 is purely a refinement of the first responsibility.

## 6. Interactions & Data Flow

All flow is synchronous, in `ApplyMessage`, driven by the existing schedule loop. Two new controller sub-cases join the current CC dispatch (Pan / EffectsLevel / ChorusLevel / HoldPedal1 / ModulationWheel / Volume / Expression):

**Arming an RPN** (`ChannelCommandType.Controller`, `Data1 == RegisteredParameterCoarse` (CC101) or `RegisteredParameterFine` (CC100)):
- CC101 → set the MSB half of `selectedRpn[ch]` from `Data2` (`selectedRpn[ch] = (Data2 << 7) | (selectedRpn[ch] & 0x7F)`).
- CC100 → set the LSB half of `selectedRpn[ch]` from `Data2` (`selectedRpn[ch] = (selectedRpn[ch] & 0x3F80) | Data2`).

**Setting the range** (`Data1 == DataEntrySlider` (CC6)):
- If `selectedRpn[ch] == 0` → `bendRange[ch] = Data2` (semitone count). Otherwise ignore.

**PitchWheel** (`ChannelCommandType.PitchWheel`): decode `value14 = (Data2<<7)|Data1`; `semitones = (value14 − PitchWheelCenter)/(float)PitchWheelSpan * bendRange[ch]`; `synth.SetChannelPitchBend(ch, semitones)`.

**Key flow — Force Your Way ch 6:**
1. `CC101=0` → `selectedRpn[6] = (0<<7)|127 = 127`.
2. `CC100=0` → `selectedRpn[6] = (127 & 0x3F80)|0 = 0`  → RPN 0 armed.
3. `CC6=12` → `selectedRpn[6]==0` → `bendRange[6] = 12`.
4. Later `PitchWheel(16383)` → `(16383−8192)/8192 * 12 ≈ +11.998` semitones → full octave. ✔ (was `* 2 ≈ +2`).

**RPN-null is emergent, not special-cased.** A song that closes the window with `CC101=127, CC100=127` drives `selectedRpn[ch]` back to 16383 ≠ 0, so subsequent Data Entry is ignored automatically. The brief's "RPN-null to close the data-entry window" requirement is satisfied by the selector's design with **zero extra branch** — the simplest correct form (Design Contracts §4: can-it-be-deleted → the explicit branch can). The same mechanism is why `selectedRpn[]` initialises to 16383: an unarmed channel behaves exactly like a null-closed one.

## 7. Data Model (Conceptual)

No persisted data. Two ephemeral per-channel vectors, lifetime = one `Render` call:

| State | Type | Init | Written by | Read by |
|---|---|---|---|---|
| `selectedRpn[ch]` | 14-bit int | 16383 (RPN-null) | CC101, CC100 | CC6 handler (gate) |
| `bendRange[ch]` | float (semitones) | 2f (GM default) | CC6 when `selectedRpn[ch]==0` | PitchWheel decode |

## 8. Contracts & Interfaces (Abstract)

| Interface (all internal to `MidiSequencer`) | Input | Output / effect | Invariants |
|---|---|---|---|
| RPN-arm (CC101/CC100 case) | `ChannelMessage` with `Data1 ∈ {CC101, CC100}` | mutates `selectedRpn[ch]` | idempotent per byte; order-independent (MSB/LSB may arrive in either order) |
| Range-set (CC6 case) | `ChannelMessage`, `Data1 == CC6` | `bendRange[ch] = Data2` **iff** `selectedRpn[ch]==0` | no-op for any other armed RPN or unarmed channel |
| PitchWheel decode | `ChannelMessage`, `Command == PitchWheel` | exactly one `SetChannelPitchBend(ch, semitones)` | `value14==8192 ⇒ semitones==0`; `range==2 ⇒ result byte-identical to pre-change |
| `ISynthesizer.SetChannelPitchBend(channel, semitones)` | **existing, unchanged** | engine bends the channel | MIDI-neutral; no RPN/range awareness |

## 9. Cross-Cutting Concerns

- **Consistency / correctness:** range applies to every PitchWheel *after* the CC6 that set it; bends before it still use the prior range (matches hardware — Force Your Way plays fine at ±2 before 68.4 s and octave after). No retro-active reinterpretation.
- **Error handling:** CC6/CC100/CC101 with out-of-band data cannot occur (7-bit by MIDI framing). `bendRange` of 0 (a channel that sets CC6=0) yields a 0-semitone decode — correct passthrough of the song's intent, not an error to guard. No defensive clamping added (Code Contracts §0 YAGNI; Design Contracts §6 "defensive code for impossible scenarios").
- **Idempotency / re-entrancy:** none needed — single-threaded schedule replay.
- **Observability:** none added; the existing render path is offline/deterministic.
- **Comments:** the bit-manipulation and the `selectedRpn==0` gate are non-obvious MIDI mechanics — a *single* short inline comment on the gate (the "why RPN 0 only") is justified under Code Contracts §4 (workaround/quirk-of-external-spec trigger). No section banners, no next-line restatements.

## 10. Quality Attributes & Trade-offs

- **Maintainability / simplicity (primary):** two arrays + three switch sub-cases in one method that already threads two analogous arrays. No new type, file, interface, or config. This is the smallest change that solves the bug.
- **Performance:** two extra array reads/writes per relevant CC; the PitchWheel hot path gains one array index. Negligible; offline render.
- **Correctness:** the selector makes RPN-null and unarmed-channel behaviour fall out for free (§6), which a "just watch for CC6 after CC101=0/CC100=0" shortcut would get subtly wrong.

**Trade-off — two threaded arrays vs. a per-channel state struct.** Grouping `cc7/cc11/selectedRpn/bendRange` into one `ChannelState` struct would slim `ApplyMessage`'s parameter list but introduces a new type/file for no behavioural gain and diverges from the established `cc7[]`/`cc11[]` precedent this file already uses (Design Contracts §2: don't add a layer for "cleanliness"; §1 DRY: match the existing pattern). **Decision: thread the arrays**, consistent with the file. Downside named: `ApplyMessage`'s signature widens from 5 to 7 parameters — a readability cost, not a correctness one, and strictly local to one internal static method.

**Alternative rejected — store range as `int`.** `float` is required to keep the decode expression (`… * range[ch]`) byte-identical to today when range==2; an `int` would force a cast that, while numerically equal, needlessly diverges the expression the regression proof depends on. `float`, seeded from the existing `2f` const, is the DRY choice.

**Alternative rejected — handle CC38 cents now.** No deliverable song sets it; combining MSB+LSB is speculative range shaping (Design Contracts §3, §4 compromise-shapes). Deferred cleanly.

## 11. Risks & Mitigations

| Risk | Likelihood | Mitigation |
|---|---|---|
| A no-RPN song's render drifts (regression) | Low | `bendRange` seeded to the exact `2f` const; decode expression operand/order unchanged → IEEE-754-identical. Guarded by a bit-identical regression test (§ tests). |
| Data Entry misattributed to bend range after an NRPN/other-RPN select | Very low (no deliverable song mixes them) | Gate is strict `selectedRpn[ch]==0`; any non-zero armed value (incl. NRPN left unhandled) blocks the write. Documented limitation: a song that arms an NRPN via CC98/99 then sends CC6 is not tracked (NRPN out of scope) — no deliverable song does this. |
| Wrong byte order in `selectedRpn` assembly | Low | Contract §8 fixes MSB=CC101, LSB=CC100; unit tests arm RPN 0 and assert the octave result end-to-end. |

## 12. Migration / Rollout Strategy

None. Single private-repo atomic change; no data, no flags, no deprecation window (Design Contracts §5 checklist item). The `PitchBendSemitoneRange` const is retained as the default seed value (not deleted, not renamed) so the diff is additive.

## 13. Open Questions

- **O1 — CC38 (cents).** Recommend **defer** (accepted default): integer-semitone ranges cover every deliverable song. Revisit only if a song surfaces a fractional range.
- **O2 — NRPN isolation.** Should arming an NRPN (CC98/99) clear `selectedRpn` to prevent a later CC6 hitting bend range? Recommend **no** for now — NRPN is out of scope and no deliverable song mixes NRPN Data Entry with RPN 0. Noted as a documented limitation, cheap to add later if a song needs it.
- **O3 — full-up asymmetry.** As with #7154, full-up 16383 yields +11.998 (not exactly +12) via the standard 8192 divisor. Recommend **keep** (conventional, matches the existing ±2 behaviour and tests-with-tolerance pattern).

## 14. Pre-Design Checklist (Design Contracts #1136 §5)

**KISS / DRY / YAGNI**
- [x] No new type mirroring an existing one — two arrays reuse the `cc7[]`/`cc11[]` shape; no new enum/struct/class.
- [x] No new abstraction with one implementation — nothing abstracted; logic sits in the existing method.
- [x] No element justified by "might need later" — RPN 1/2, NRPN, CC38, config knobs all explicitly deferred with the concrete reason (no deliverable song).
- [x] No deprecation window / flag / shim — additive change, atomic deploy.
- [x] DRY block-math: no multi-site duplication introduced; the three CC sub-cases are distinct single statements, not a repeated block.

**Existing systems first**
- [x] Audited the existing surface: the `cc7[]`/`cc11[]` threaded-array pattern already exists for exactly this "per-channel controller state in the sequencer" concern; the new state joins it rather than founding a new layer.
- [x] No new layer proposed — the concrete reason a struct/service is *not* introduced is named (§10 trade-off).
- [x] No new persisted data — state is render-local.
- [x] No "existing reader projects it" chains — new state has a live consumer (PitchWheel decode / CC6 gate).

**Configurability**
- [x] No new config knob. The GM default (2) stays a named `const` seed (Design Contracts §3: no operator, no env-diff → stays magic-number-in-code).
- [x] No telemetry-then-tune compound.

**Less is better**
- [x] Every element passes delete/merge/inline: the RPN-null branch was deleted (emergent from the selector, §6); the state cannot be merged into `cc7/cc11` (different semantics) nor inlined (read at PitchWheel, written at CC6 — genuinely stateful).
- [x] Trade-offs named explicitly (§10: threaded arrays vs struct; float vs int; CC38 deferral).
- [x] Radical-clean shape chosen (no compromise middle structure).
- [x] Reader inventory: the only consumer of `bendRange` is the PitchWheel decode; the only consumer of `selectedRpn` is the CC6 gate — both in-file, no string-literal references.

**Document discipline**
- [x] Cites Code Contracts #114 and Design Contracts #1136 as load-bearing.
- [x] Out-of-scope items listed explicitly (§2).
- [x] No multi-paragraph "why we keep X" filler.
- [x] Predecessor design #7154 is *refined, not superseded* (it correctly deferred RPN as YAGNI for its deliverable; this bug is the concrete X that lifts the deferral) — no supersession banner needed; a one-line cross-reference is added to this doc's header instead.

## 15. Implementation Guidance for the Next Agent

One production file (`MidiSequencer.cs`) + test additions. Ordered milestones:

1. **`Render` — allocate + init state.** Alongside `cc7[]`/`cc11[]`, add `int[] selectedRpn = new int[ChannelCount]` initialised to a named const `RpnNull = (127<<7)|127 = 16383`, and `float[] bendRange = new float[ChannelCount]` initialised to `PitchBendSemitoneRange` (2f) per channel. Thread both into the `ApplyMessage` call.
2. **`ApplyMessage` — widen signature** to accept `selectedRpn` and `bendRange`.
3. **Controller dispatch — add three sub-cases** (before the Volume/Expression tail): `RegisteredParameterCoarse` (CC101) → write MSB half of `selectedRpn[ch]`; `RegisteredParameterFine` (CC100) → write LSB half; `DataEntrySlider` (CC6) → `if (selectedRpn[ch]==0) bendRange[ch] = Data2`. Use the `ControllerType` enum members (do not hardcode 100/101/6). One short inline comment on the CC6 gate explaining "RPN 0 = bend range only".
4. **PitchWheel case — swap the const for the array:** `… / (float)PitchWheelSpan * bendRange[channel.MidiChannel]`. Keep the const `PitchBendSemitoneRange` as the init seed; do not delete it.
5. **Named consts:** add `RpnNull = 16383` (or assemble from `ControllerFullScale`); reuse `PitchWheelCenter`/`PitchWheelSpan`. XML `<summary>` on any new const per §4.
6. **Tests** (mirror `MidiSequencerPitchBendTests`, reuse the existing `.Controller(delta, ch, cc, value)` builder — no new helper needed):
   - **RPN 0 widens range:** `Controller(CC101=0), Controller(CC100=0), Controller(CC6=12)` then `PitchWheel(full-up)` on the same channel ⇒ `ChannelPitchBendCalls` last entry semitones ≈ **+12** (Within ~0.01).
   - **Default/unset unchanged:** full-up PitchWheel with no RPN ⇒ semitones ≈ **+2** (guards the existing behaviour).
   - **RPN-null closes the window:** RPN 0 set to 12, then `CC101=127, CC100=127`, then `CC6=2` ⇒ range stays **12** (CC6 ignored) — full-up still ≈ +12.
   - **Bit-identical no-RPN regression:** a song with PitchWheel but no RPN renders a byte-identical buffer to a pre-change baseline (assert against the current decode, or a golden ratio).
   - **Deliverable proof (graceful-skip, mirror `MidiPitchBendRenderProofTests`):** render `1-10-Force_Your_Way.mid` via `FindDevTreeAsset`; assert ch-6 records a `SetChannelPitchBend` call with |semitones| in the octave band (≈9–12) after the RPN, versus the ±2 band before it — the pitch-track before/after that proves the octave whammy.
7. **Gate:** both TFMs 0-warning; `dotnet test` green (foreground, ~10 min).

**Chain:** sarah (this) → john-backend-dev (implement on `feature/pitch-bend-range`, one PR with this doc) → jenny-qa-reviewer.
