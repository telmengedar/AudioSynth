# Architectural Document: Voice Stealing — reclaim a voice when the pool is full

**Author:** Sarah (software architect) · **Date:** 2026-07-28
**Source task:** DiVoid #7183 · **Project:** #6128 · **Map root:** #6708
**Builds on:** rewrite design #6401 (esp. §8 — *declick fade on voice-steal*, INV-1), engine design #6401/#6502
**Load-bearing contracts:** Design Contracts #1136 (§1 KISS/DRY/YAGNI, §5 Pre-Design Checklist) + Code Contracts #114 §0
**Repo copy of this doc:** `docs/architecture/voice-stealing.md` (branch `feature/voice-stealing`)
**DiVoid copy:** documentation node linked to #7183, #6734, #7065, #6731, #6401.

> This is the design for **roadmap gap #7183** — the one remaining robustness hole in the voice
> engine. Today `Synthesizer.FindFreeSlot()` returns `-1` when the pool is full and `NoteOn`
> silently drops the note. This design replaces that drop with a **steal**: pick the best victim,
> declick it, and give its slot to the new note. It ships as **one PR** (design + implementation),
> per DiVoid #1165.

---

## 1. Problem Statement

The synthesizer holds a fixed pool of `MaxVoices` voice slots (default 32; the render demo uses 128).
When every slot is occupied and a `NoteOn` arrives, `FindFreeSlot()` returns `-1` and `NoteOn`
early-returns — the note is **dropped and never heard**. For dense or heavily-sustained material the
pool can saturate (sustain holds notes in their slots longer, raising pressure), and dropped notes
are an audible correctness failure: the music loses events.

**Goal.** When the pool is full, *never drop a note while a stealable voice exists*. Instead reclaim
(“steal”) the best-candidate slot for the new note. The reclaim must be **click-free** — cutting a
voice that is currently sounding produces a waveform discontinuity (a click) unless the outgoing
voice is faded out. This declick-on-steal is a pre-existing named requirement in the rewrite
architecture (#6401 §8, invariant INV-1: *no click when a sounding voice is reclaimed*).

**Success criteria.**
1. With the pool full, the *(N+1)*-th `NoteOn` **sounds** (a slot is reclaimed) rather than being dropped.
2. The reclaim introduces **no audible click** — no large sample-to-sample discontinuity at the steal.
3. The victim is chosen by a defined, deterministic policy: a **released** voice is preferred over a
   loud sounding one; among sounding voices the **quietest** is preferred; **oldest** breaks ties.
4. **Zero behavioural change when the pool is not full.** A song that never exceeds `MaxVoices` renders
   **bit-identical** to today — the steal path is simply never entered.

---

## 2. Scope & Non-Scope

**In scope**
- Steal-selection policy inside the voice-allocation path (`NoteOn` / the slot-finding step): when no
  free slot exists, choose a victim instead of returning `-1`.
- Declick-on-steal: the victim fades to silence before the new note occupies the slot (INV-1).
- The **minimal** per-slot / per-voice state the policy needs: a voice “current gain” read (for
  *quietest*), a per-slot age stamp (for *oldest*), a per-slot “released” marker (for *released-first*),
  and the deferred-start bookkeeping the declick requires.

**Explicitly out of scope** (YAGNI — none of these is required by #7183 and none is added speculatively)
- Per-channel voice reserves / priority channels (e.g. protecting channel 9 drums).
- Note-importance / velocity-weighted or key-range steal heuristics beyond released → quietest → oldest.
- Dynamic pool resizing or a configurable pool-growth policy.
- **Any new configuration knob.** Stealing is always-on; the declick fade reuses the existing
  `GainRamp` smoothing time. No new `SynthesizerOptions` field, no new magic number.
- A priority queue / heap for victim selection. The pool is small (32–128); a single linear scan is
  cheaper than maintaining an ordered structure and has no allocation.

---

## 3. Assumptions & Constraints

| # | Assumption / Constraint | Source / Confidence |
|---|---|---|
| A1 | The voice pool is a fixed-size array sized once in the ctor; `Read` allocates nothing in steady state. | `Synthesizer.cs` ctor + class summary — **confirmed**. |
| A2 | A freshly-started voice is inherently click-free on onset: its `GainRamp` starts at 0 and its `AmplitudeEnvelope` starts at level 0, so the new note ramps up from silence. | `SamplePlaybackVoice` ctor + `GainRamp`/`AmplitudeEnvelope` — **confirmed**. The click risk is entirely on the *outgoing* victim. |
| A3 | `GainRamp.DefaultSmoothingSeconds` = 5 ms is the project’s established click-free glide time. | `GainRamp.cs` — **confirmed**. The steal fade reuses it; no new constant. |
| A4 | Note-start already allocates a voice object (`IPatch.StartVoice`) per `NoteOn`. “Allocation-free” means the **steady-state `Read` hot path**; the steal path may allocate a voice exactly as an ordinary `NoteOn` does. | `NoteOn` — **confirmed**. |
| A5 | MIDI note events are sparse relative to audio frames; more than `MaxVoices` note-ons inside a single ~5 ms window on one render call is not a realistic input. | Design judgement — used only to bound one pathological edge (§11 R3). |
| A6 | `VoiceSlot` is an internal mutable struct held by-ref in the pool; growing it with a few value-type fields is free of heap cost and invisible to output. | `VoiceSlot.cs` + `ref VoiceSlot` usage — **confirmed**. |

---

## 4. Architectural Overview

The change lives entirely inside the **voice engine** (`Synthesizer`) and the **voice contract**
(`IVoice` + its implementations) and the **slot record** (`VoiceSlot`). No new component, no new
service, no new file for a new type — the feature is a behavioural upgrade to the existing
allocation path plus two small additions to the voice contract.

```
                         NoteOn(channel, key, velocity)
                                     │
                                     ▼
                        ┌─────────────────────────┐
                        │  find a slot for the note │
                        └─────────────────────────┘
                                     │
                 free slot? ─── yes ─┼──► occupy it, start voice   (UNCHANGED path)
                                     │
                                     no
                                     ▼
                        ┌─────────────────────────┐
                        │  SELECT VICTIM (linear   │   released? → quietest → oldest
                        │  scan, best candidate)   │
                        └─────────────────────────┘
                                     │
                          victim found? ── no ──► drop (only when every slot is
                                     │             already draining — pathological)
                                     yes
                                     ▼
                        ┌─────────────────────────┐
                        │  DECLICK: victim fast-   │   victim keeps rendering its own
                        │  fades to silence (~5 ms)│   real tail in its own slot;
                        │  new note is PENDING     │   new note is queued on the slot
                        └─────────────────────────┘
                                     │
                        (victim reaches silence, goes inactive)
                                     ▼
                        ┌─────────────────────────┐
                        │  slot re-init: start the │   new note now sounds, ramping
                        │  pending note in-place   │   up from 0 (click-free onset)
                        └─────────────────────────┘
```

The core idea that keeps this **simple**: the victim is *not* ripped out of its slot and discarded.
It is told to **fast-fade**, and it keeps rendering — as an ordinary occupant of its own slot, with
its own channel/pan/sends — until it reaches silence. Only then does the slot re-initialise with the
new note. Because the victim renders through the *existing, unchanged* mix loop during its fade, **the
per-block render code needs zero changes**: no second render pass, no frozen gains, no residual
approximation, no crossfade buffer. The new note is held as a tiny “pending note” on the slot until
the fade completes.

---

## 5. Components & Responsibilities

| Component | Change | Owns | Does NOT own |
|---|---|---|---|
| `Synthesizer` | Replace the “full → return -1 → drop” behaviour with victim selection + declick + deferred pending-note start. Assign an age stamp on note-start; mark a slot released on note-off/sustain-lift. | The **steal policy** (victim comparison), the pending-note handoff, age-stamp issuing. | The fade DSP (delegated to the voice); per-frame amplitude (owned by the voice). |
| `IVoice` (contract) | Add a read for **current amplitude** and an action to **fast-fade to silence for a steal**. | The declaration of the two new surface members. | Any policy — the interface stays a pure per-voice contract. |
| `SamplePlaybackVoice` | Implement the two new members: expose the last-frame gain; implement the fast fade by re-targeting its existing `GainRamp` to 0 and deactivating once silent. | Its own fade DSP and its own “current gain”. | Slot allocation / victim choice. |
| `InactiveVoice`, and the test doubles `StubVoice` / `NanEmittingVoice` | Implement the two new members trivially (gain 0; fade is a no-op — they are already silent/inactive). | — | — |
| `VoiceSlot` (record) | Add: a monotonic **age** stamp; a **released** marker; **pending-note** fields (with a sentinel meaning “none”). | Carrying the metadata the policy reads. | Behaviour — it is a passive struct. |

**Single-responsibility framing.** The *engine* decides *which* voice dies and *when* the new note
starts. The *voice* decides *how* it fades (its own DSP). The *slot* only carries state. This mirrors
the existing SRP split the rewrite established (a voice renders its own mono block and never touches
engine buffers, #6401 §5).

---

## 6. Interactions & Data Flow

**Steal sequence (pool full).**
1. `NoteOn(ch, key, vel)` finds no free slot.
2. The engine runs **victim selection** (§8) — a single linear scan over occupied, non-draining slots,
   keeping the best candidate under the comparison order *released-first → quietest → oldest*.
3. If a victim is found, the engine calls the voice’s **fast-fade** action and records the new note as
   the slot’s **pending note** (channel, key, velocity). The slot is now *draining*: its current voice
   is the fading victim; its pending note is the incoming note. The slot’s `Channel`/`Key` still belong
   to the victim (it is still the sounding occupant).
4. Over subsequent frames the victim fades to silence through the **unchanged** mix loop and then
   reports itself inactive.
5. In `Read`, the existing “voice finished → free the slot” branch gains one fork: **if the slot has a
   pending note, re-initialise the slot with that note in place** (allocate its voice via the channel’s
   patch, apply the channel’s live pitch-bend and mod-wheel exactly as `NoteOn` does, stamp a fresh
   age, clear the pending/released markers) **instead of** freeing the slot. The new note now sounds.

**Non-full path (the common case) is untouched:** `FindFreeSlot` returns a free index, the note starts
there immediately, and none of the new state influences rendering. This is what guarantees the
bit-identical regression (§10, success criterion 4).

**Note-off during drain (edge).** `NoteOff` matches slots by `(Channel, Key)`, which during a drain
still identify the *victim*. A note-off for a *pending* note that arrives before the pending note has
started will therefore not match. See §11 R2 for the (rare) consequence and the accepted resolution.

---

## 7. Data Model (Conceptual)

No persisted data; this is all in-memory engine state. The conceptual additions:

- **Voice “current gain.”** The instantaneous amplitude scalar the voice last produced (its envelope ×
  velocity-ramp × tremolo product), in `[0,1]`. Conceptually the voice’s audibility right now. Silent /
  inactive voices report 0.
- **Slot age.** A monotonically increasing stamp issued when a note starts in a slot. Lower = older.
  Used only to break ties in victim selection deterministically toward the oldest note.
- **Slot released marker.** True once the slot’s note has had its key lifted into an actual release
  (immediate note-off, or a sustain-lift that releases a deferred voice). Distinct from the existing
  `PendingRelease` (key up but held by the sustain pedal, still at full level). For steal purposes both
  mean “the player has let this note go,” so both make the slot a *released-tier* victim.
- **Slot pending note.** The incoming note deferred behind a declick fade: channel, key, velocity, plus
  a way to represent “no pending note” (a channel sentinel of `-1` avoids a separate boolean).

---

## 8. Contracts & Interfaces (Abstract)

### 8.1 New `IVoice` surface (two members)

| Member | Kind | Semantics | Invariant |
|---|---|---|---|
| **Current gain** | read-only value in `[0,1]` | The amplitude the voice is currently producing (last rendered frame). The engine reads it to pick the *quietest* sounding victim. | For inactive/silent voices it is 0. Reading it never advances or mutates the voice. |
| **Fast-fade-for-steal** | action (no args, no return) | Instructs the voice to ramp its output to silence over a short, click-free window and then become inactive, regardless of its natural (possibly long) release time. | The fade is **monotone to zero** and completes within a bounded, short time (≈ the `GainRamp` smoothing time, 5 ms). Idempotent — calling it again while already fading is a no-op. On an already-inactive voice it is a no-op. |

**Why these two and no more.** *Current gain* is the only voice-internal fact the *quietest* rule
needs, and *quietest* is the rule that actually fires in the motivating scenario (dense **sustained**
material, where most voices are held — the released tier is often empty). *Fast-fade* is the only
action the voice alone can perform (its own DSP); the engine cannot fade a voice from outside. The
“released” signal is **not** added to `IVoice` — the engine already knows when it releases a voice, so
that lives on the slot (below), keeping the interface minimal (2 members, not 3).

### 8.2 Fast-fade realisation (no new DSP primitive)

The fade **reuses the voice’s existing `GainRamp`**: fast-fade re-targets that ramp to 0. Because the
voice’s output is `envelope × gainRamp × tremolo`, driving the ramp to 0 carries the output smoothly to
silence over the ramp’s 5 ms smoothing time — which is exactly the project’s established click-free
glide (`GainRamp` is *the* declick primitive, #6731). A single new internal flag lets the voice
deactivate once the ramp has reached 0. **No new fade class, no new constant, no crossfade buffer.**

### 8.3 Victim-selection contract (engine-internal)

A pure function over the pool: *given the occupied, non-draining slots, return the index of the best
victim, or “none”.* Candidate ordering is the lexicographic tuple, smallest wins:

```
( releasedTier , currentGain , age )
   releasedTier = 0 if the slot's note has been let go (released marker OR PendingRelease), else 1
   currentGain  = the slot voice's current gain           (prefer quieter)
   age          = the slot's age stamp                    (prefer older / smaller stamp)
```

This single comparator expresses the whole policy: *all released voices rank above all held voices;
within a tier the quietest wins; equal gains break toward the oldest.* Draining slots (those already
holding a pending note) are **excluded** from candidacy — they are already committed and must not lose
their pending note. Deterministic, allocation-free, O(pool).

---

## 9. Cross-Cutting Concerns

- **Declick / INV-1.** The whole feature is built around it. Two transitions exist and both are
  click-free by construction: the **outgoing** victim fades to 0 over 5 ms via its `GainRamp` (no step);
  the **incoming** note ramps up from 0 via its own fresh `GainRamp` + envelope (no step). Because the
  victim keeps rendering its real waveform through the unchanged mix path during the fade, the declick
  is an *actual* fade of the actual tail, not an approximation.
- **Concurrency.** Unchanged. The engine’s single-threaded control/render model (#6401 §8) is preserved;
  no locks, no new shared state. Note events and `Read` are serialised by the host as today.
- **Allocation / real-time safety.** The steady-state `Read` hot path still allocates nothing. Victim
  selection is a scan over value-type slot fields. The only allocation on the steal path is the voice
  object itself — the same allocation an ordinary `NoteOn` already performs, and it happens on the
  (sparse) note event, not per frame.
- **Idempotency / safety.** Fast-fade is idempotent and safe on inactive voices. The pending-note
  sentinel makes “no pending note” unambiguous. Re-entrancy is not a concern (single-threaded).
- **Observability.** The engine surfaces no logging dependency (reach constraint, #6401 §8/§9). Steal
  behaviour is verified structurally by tests (§10), not by runtime logs.

---

## 10. Quality Attributes & Trade-offs

| Attribute | How the design addresses it |
|---|---|
| **Correctness** | Notes are no longer dropped while any stealable voice exists — the headline requirement. |
| **Audio quality** | Click-free by two-sided construction (outgoing fade + incoming ramp). Policy minimises audibility: released/quiet voices die first. |
| **Performance** | O(pool) scan, no allocation, no heap structure. Pool is 32–128 — the scan is a few hundred cheap comparisons on note-on only. |
| **Simplicity / maintainability** | Mix loop untouched; two new `IVoice` members; a handful of value-type slot fields; no new type, file, service, or config. |
| **Regression safety** | Non-full path is byte-for-byte the current path; new state is metadata the mix loop never reads. |

**Trade-offs made explicit.**

1. **Deferred start (~5–7 ms latency on the stolen note) vs. on-time start with a heavier structure.**
   Chosen: **deferred start.** Alternative (rejected): start the new note immediately and render the
   victim’s fade in a separate “retiring-voice” side pool with frozen L/R gains (a second render pass).
   The deferred design reuses the *existing* per-slot render for the victim’s fade — zero mix-loop
   change, no frozen-gain duplication of the pan/gain/send math, no second pass. Cost: the stolen note
   sounds ~5 ms (fade) + up to one block later — **imperceptible** for note-onset timing and standard
   practice in polyphonic synths. This is the KISS-winning shape; the on-time alternative buys nothing
   the ear can detect while adding a whole parallel render path. The brief itself frames the mechanism
   as “fade the victim *before the new voice takes the slot*,” i.e. deferral.

2. **Age stamp (oldest tiebreak) — a genuinely-optional field kept deliberately.** Dropping it would
   fall back to *first-in-scan-order* among equal-gain candidates — deterministic but an artefact of
   array layout. The age stamp costs one value-type field per slot plus a single counter, and makes the
   tiebreak the *musically standard* “steal the oldest note.” The task requests it explicitly. It earns
   its keep; kept. (If a reviewer wants the absolute minimum, this is the one element that could be cut
   without failing any success criterion — noted for transparency, but the recommendation is to keep it.)

3. **“Released” tracked on the slot, not on `IVoice`.** Keeps the core interface at +2 members instead
   of +3. The engine is the authority on when it releases a voice, so the slot is the natural home.

**Alternatives rejected.**
- *Priority queue / heap for victim selection* — unjustified for a 32–128 pool; adds a structure to
  maintain and (likely) allocate. A linear scan is simpler and faster here.
- *“Steal the quietest” via a residual zero-order-hold ramp on the disappearing sample* — would let the
  new note start on time but replaces the victim’s real tail with a DC-ish approximation and pushes a
  frozen-gain add into the mix loop. More moving parts than the deferred design for no audible gain.
- *A configurable steal policy / configurable fade time* — YAGNI (§2, Design Contracts §3). One policy,
  one fade time (the existing 5 ms). Promote to config only if a real tuning need ever surfaces.

---

## 11. Risks & Mitigations

| # | Risk / failure mode | Mitigation |
|---|---|---|
| R1 | Fade too short → residual click; too long → the stolen note lingers audibly. | Reuse the established 5 ms `GainRamp` glide — already proven click-free project-wide (#6731) and short enough to be inaudible as lingering. Verified by the no-click test (§10 / deliverable). |
| R2 | A `NoteOff` for the *pending* note arrives during the victim’s ~5 ms drain and does not match (slot still keyed to the victim), so the pending note starts un-released. | **Accepted as a documented limitation.** Window is ≤ ~5 ms and requires a note shorter than the fade landing exactly during a full-pool steal (A5) — vanishingly rare. Consequence: the note plays out its own envelope (one-shot samples exhaust naturally; a looped/sustained sample would sound until it otherwise ends). If field evidence ever shows it biting, the cheap fix is to also match pending notes in `NoteOff` — deliberately deferred (YAGNI) rather than built speculatively. **Open question O1 for Toni.** |
| R3 | Every slot is *already draining* (all committed to pending notes) when another `NoteOn` arrives → no candidate → the note is dropped. | Requires > `MaxVoices` note-ons inside one ~5 ms window on a single render (A5) — not a realistic MIDI input. Dropping here is acceptable and is strictly better than today (today drops at the *first* over-capacity note). |
| R4 | Widening `IVoice` breaks its four implementers (`SamplePlaybackVoice`, `InactiveVoice`, `StubVoice`, `NanEmittingVoice`). | All four are updated in this PR; the three silent/inactive ones get trivial members (gain 0, fade no-op). Enumerated in the implementation brief so none is missed. |
| R5 | New slot fields or age-stamp issuing accidentally perturb the non-full render. | The new fields are read *only* by victim selection and the pending-note handoff, both reachable only when the pool is full. Locked down by the bit-identical regression test (§10). |

---

## 12. Migration / Rollout Strategy

None required — this is a behavioural upgrade to an existing engine, greenfield project, atomic deploy.
The old behaviour (drop on full) is simply replaced. No feature flag, no transition window (Design
Contracts §4 / §6 — no deprecation shims in an atomic-deploy codebase). The bit-identical regression
test is the safety net.

---

## 13. Open Questions

- **O1 (for Toni).** The note-off-during-drain edge (§11 R2): accept as a documented ~5 ms limitation
  (recommended, KISS), or spend the few extra lines to also match pending notes in `NoteOff`? Default:
  accept.
- **O2.** Age-stamp tiebreak (§10 trade-off 2): keep “oldest” (recommended, task-requested) or fall back
  to scan-order to shave one field? Default: keep.

Neither blocks implementation; both have a decisive default.

---

## 14. Implementation Guidance for the Next Agent (John)

Build order (single branch `feature/voice-stealing`, single PR bundling this doc + the code):

1. **Widen `IVoice`** with the two members (§8.1): current-gain read, fast-fade action. Update all four
   implementers — real DSP in `SamplePlaybackVoice`, trivial in `InactiveVoice` / `StubVoice` /
   `NanEmittingVoice`.
2. **`SamplePlaybackVoice`:** cache the per-frame `gain` into a field and expose it as current-gain;
   implement fast-fade by setting a `stealing` flag and re-targeting the existing `gainRamp` to 0,
   deactivating (`isActive = false`) once the ramp has reached 0. No new constant — the 5 ms comes from
   the existing `GainRamp` smoothing.
3. **`VoiceSlot`:** add the age stamp, the released marker, and the pending-note fields (channel
   sentinel `-1` = none). XML-doc each field.
4. **`Synthesizer`:**
   - Issue a monotonic age stamp when a note starts in a slot (both the ordinary `NoteOn` path and the
     deferred pending-note start).
   - Set the released marker in `NoteOff` (immediate-release branch) and in the sustain-lift sweep.
   - Replace the `FindFreeSlot() < 0 → return` drop with: run victim selection (§8.3); if a victim
     exists, fast-fade it and record the pending note on its slot; only if no candidate exists (all
     draining) do nothing (drop).
   - In `Read`, extend the “voice finished” branch: if the finished slot has a pending note, re-init the
     slot with it (allocate via the channel patch, apply live bend + mod-wheel as `NoteOn` does, stamp a
     fresh age, clear pending + released) instead of freeing.
   - Keep the mix loop otherwise **untouched**.
5. **Tests (deliverable proof).** Use a small `MaxVoices` (e.g. 4):
   - *Steal-not-drop:* fill the pool, then one more `NoteOn` → a slot is reclaimed and the new note
     sounds (non-silent) rather than being dropped.
   - *Victim policy:* release one voice, keep the rest loud/held, over-fill → the **released** voice is
     the one reclaimed (not a loud sounding one).
   - *No click:* fill with loud voices, force a steal, render across the steal boundary, assert no large
     sample-to-sample discontinuity (bounded |Δ|).
   - *Bit-identical regression:* a render that never exceeds the pool matches a pre-change golden buffer
     exactly (steal path never entered).
6. **Self-audit before PR (Code Contracts #114 §6.10):** body-comment grep = **0** (all comments `///`
   XML docs, matching the codebase); XML `<summary>` size gate on new members; one type per file
   (no new type shares a file). Both TFMs 0-warning; `dotnet test` green (run foreground, slow suite).

**Commit this design doc on the branch in the same PR as the code** (DiVoid #1165 — no design-only PR).

---

## 15. Pre-Design Checklist (Design Contracts #1136 §5 — walked verbatim)

**KISS / DRY / YAGNI**
- ✅ No new type whose value-space mirrors an existing type. (No new types at all — behavioural change + 2 interface members + slot fields.)
- ✅ No new abstraction with a single implementation and no second. (No new interface; `IVoice` gains two members, honoured by all four existing implementers.)
- ✅ No element justified by “might need X later.” Every addition is required by a success criterion or the declick invariant. The one optional element (age stamp) is named as such in §10 with a decisive keep-recommendation.
- ✅ No deprecation period / feature flag / compatibility shim / transition window (atomic-deploy greenfield).
- ✅ No “inline N sites” DRY tension — nothing is duplicated; the victim comparator is a single function, the fade reuses the existing `GainRamp`, the pending-note start reuses the `NoteOn` start logic (called from `Read`).

**Existing systems first**
- ✅ Audited: the concern lives on the existing `Synthesizer` allocation path; no new service/table/DTO. A method on the existing engine covers it.
- ✅ No new layer proposed — so no “why can’t it live on the existing surface” to answer beyond “it does.”
- ✅ No new persisted data (in-memory engine only; the 4-week-decision gate is N/A).
- ✅ No field justified by “an existing reader projects it” — the new fields have concrete, named consumers (victim selection, pending-note handoff).

**Configurability**
- ✅ **No new config knob.** Stealing is always-on; fade time reuses the existing 5 ms `GainRamp` constant. No operator/env-difference to justify a knob, so none is added.
- ✅ No telemetry-then-tune compound.
- ✅ The one “magic number” (5 ms) is *not new* — it is the existing `GainRamp.DefaultSmoothingSeconds`, reused, staying a named `const` where it already lives.

**Less is better**
- ✅ Can-it-be-deleted / merged / inlined run on every element: mix loop untouched (deleted the need for a second pass); “released” folded onto the slot rather than a third interface member; the fade reuses `GainRamp` rather than a new primitive; the pending-note “none” state reuses a sentinel rather than a separate boolean.
- ✅ Trade-offs named explicitly (§10): deferred-vs-on-time start, age-stamp keep-vs-cut, released-on-slot-vs-interface.
- ✅ No consumer-less surface kept as a compromise shape.
- ✅ Reader/scope inventory: the four `IVoice` implementers are enumerated (§5, R4) — AST-complete; there are no string-literal references to these members.

**Data deliverables** — N/A (no SQL / migration / schema).

**Document discipline**
- ✅ Cites Code Contracts (#114) and Design Contracts (#1136) as load-bearing (header).
- ✅ Reader/scope inventory explicit (IVoice implementers).
- ✅ Out-of-scope listed explicitly (§2).
- ✅ No multi-paragraph “rationale for keeping X” for things that obviously stay.
- ✅ No predecessor design superseded by this one (it extends #6401 §8, which stays current).
```
