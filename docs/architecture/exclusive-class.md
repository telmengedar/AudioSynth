> **Repo copy (authoritative wording):** `docs/architecture/exclusive-class.md` on branch `feature/exclusive-class` in `C:\dev\claude\AudioSynth\Pooshit.AudioSynth`. A DiVoid `documentation` node mirrors this.
> **Source bug:** DiVoid #7226 · **Project:** #6128 · **Map root:** #6708
> **Load-bearing inputs:** Design Contracts #1136 (§1 KISS/DRY/YAGNI, §5 Pre-Design Checklist), Code Contracts #114 (§0/§1/§5.5), voice-stealing design #7200 (the `FastFadeForSteal` declick mechanism reused here), synth engine architecture #6401 §8 (INV-1 declick-on-steal, INV-2 finalize choke point).
> **Reuses verbatim:** `IVoice.FastFadeForSteal()` and the `Read` finished-voice cleanup, both merged via voice-stealing PR #23.

# Architectural Document: SF2 Exclusive Class (generator 57) — hi-hat choke

## 1. Problem Statement

SF2 generator 57 (`exclusiveClass`) is defined in the engine's generator enum but is **never read** by `Sf2RegionResolver` and **never applied** by `Synthesizer`. In General MIDI, percussion voices that must not overlap — a kit's hi-hats (closed 42 / pedal 44 / open 46) — share a single non-zero exclusive class so that a new hit **chokes** (cuts to silence) any still-sounding voice of the same class on the same channel: a closed hat cuts a ringing open hat; rapid closed hats do not pile up.

Because the engine ignores the generator, hi-hat hits **layer** instead of choking. Overlapping decays **sum**. When hit density rises during a fill (measured at Force Your Way 21.0–21.5s: ~16 hits/s vs ~10/s steady), the summed hi-hat level **swells for ~2s, then normalises** — an audible volume pump the user hears at 20–22s and again at 2:09. Windows Media Player, which implements exclusive class, renders the same passage flat.

**Goal:** implement exclusive-class choking so that starting a voice with a non-zero exclusive class silences every already-sounding voice of the same class on the same channel, click-free, restoring a constant hi-hat level across density spikes.

**Success criteria:**
- Re-rendering `1-10-Force_Your_Way.mid` through Florestan produces a hi-hat level that is **stable across the 20–22s and 2:09 density spikes** (windowed level-stability metric flat), matching WMP's character.
- A deterministic unit test proves: a second voice with the same non-zero exclusive class **chokes** the first (its gain fast-fades to 0); a different class or zero class does **not** choke; a different channel does **not** choke.
- Content with **no** exclusive classes renders **bit-identical** to today.

## 2. Scope & Non-Scope

**In scope**
- `Sf2RegionResolver` reads generator 57 into a new `SampleRegion.ExclusiveClass` (int, 0 = none), reusing the existing `GetEffectiveRaw` zone-lookup path.
- `IVoice` surfaces the voice's exclusive class (read-only) so the engine can match it.
- `Synthesizer` chokes same-class, same-channel voices when a voice with a non-zero exclusive class starts, reusing `FastFadeForSteal()` for the click-free cut.

**Out of scope (YAGNI)**
- No new choke DSP. The fade-to-silence primitive already exists (`FastFadeForSteal`) and is reused verbatim.
- No cross-channel exclusive classes. The SF2 spec matches within the channel/preset; that is the whole requirement.
- No exclusive-class awareness in voice-stealing victim selection (the pool is not full for drums — see §11 O2).
- No pending-note handoff for the choke. Choked voices fade in their own slots and free via the existing `Read` cleanup branch.
- No configuration knobs, no thresholds, no magic numbers (the 5 ms fade is the existing `GainRamp` smoothing, inherited).
- The master-headroom question (bug #7212 / PR #25) is a **separate** decision, explicitly not addressed here (see §12 note).

## 3. Assumptions & Constraints

| # | Assumption / Constraint | Confidence |
|---|---|---|
| A1 | Florestan's percussion instrument sets gen 57 on its hi-hat regions. | **Verified** — see §4. |
| A2 | One MIDI channel maps to exactly one patch/preset (`channelPatch[channel]`), so "same channel" == "same preset" and satisfies the SF2 within-preset matching rule. | High — confirmed in `Synthesizer` ctor and `SetChannelPatch`. |
| A3 | The voice-stealing `FastFadeForSteal()` + `Read` finished-voice cleanup are merged into `main`. | **Verified** — present in `IVoice`, `SamplePlaybackVoice`, `Synthesizer.Read`. |
| A4 | Exclusive-class amounts are small non-negative integers (0–127 per spec); 0 is the only semantically special value ("none"). | High — SF2 spec; Florestan uses 1–7 (§4). |
| A5 | `Sf2Generator.RawAmount` is a `ushort`; reading gen 57 as the unsigned raw amount yields the class id directly with no sign or range hazard. | **Verified** — `Sf2Generator.RawAmount` is `ushort`. |

**Constraints inherited from the engine:**
- **INV-1 (declick):** every level transition must be click-free. The choke satisfies this by reusing the 5 ms `GainRamp` fade, exactly as voice-stealing does.
- **INV-2 (allocation-free steady state):** `Read` and the note path allocate nothing. The choke is a single linear pool scan with no allocation.
- **Bit-identical regression:** any path not exercised by exclusive-class content must be byte-for-byte unchanged.

## 4. Verification of A1 — Florestan actually sets gen 57 (load-bearing)

The entire fix is inert if the SoundFont never emits generator 57. Parsing `__Florestan_Basic_GM_GS.sf2` directly:

| Chunk | Total generator records | gen-57 (`exclusiveClass`) records | Distinct class values → zone count |
|---|---|---|---|
| `igen` (instrument level) | 19 284 | **120** | `1→24, 2→16, 3→16, 4→16, 5→16, 6→16, 7→16` |
| `pgen` (preset level) | 238 | 0 | — |

**Findings:**
- Gen 57 is present **120 times**, exclusively at the **instrument** level (`igen`), not the preset level (`pgen`). This matches where the resolver already reads pan/reverb/chorus — the instrument-zone generators walked by `BuildRegion` via `GetEffectiveRaw`. Reading gen 57 in `BuildRegion` on that same path is therefore correct and sufficient; no preset-level generator handling is needed.
- Exclusive class **1** covers **24 zones** — the GM percussion group (hi-hats and other choke-group drums across kits). Classes 2–7 cover 16 zones each (other choke groups: e.g. GM defines mutually-exclusive triangle and guiro pairs).
- Conclusion: **the fix is real.** Florestan supplies the data; the engine discards it. The re-render will change audibly once gen 57 is honoured.

## 5. Architectural Overview

The exclusive class is a single integer that must travel from the SF2 file to the moment a voice starts, so the engine can match it against sounding voices. It rides the **existing region → voice seam** — the same path pan, reverb-send, and chorus-send already travel:

```
 SF2 file                     Sf2RegionResolver.BuildRegion
   gen 57  ──read (GetEffectiveRaw, default 0)──►  SampleRegion.ExclusiveClass (int)
                                                        │
                                              SamplePlaybackVoice(region)
                                                        │  exposes
                                                        ▼
                                              IVoice.ExclusiveClass  (read-only int, 0 = none)
                                                        │  read by
                                                        ▼
   Synthesizer.StartVoiceInSlot(slot, ch, key, vel)
        1. start the voice in the slot (unchanged)
        2. newClass = slot.Voice.ExclusiveClass
        3. if newClass == 0 → return           ◄── the bit-identical fast path
        4. else scan pool: for each occupied, non-draining slot i ≠ this slot
             with Voice.ExclusiveClass == newClass AND Channel == ch:
                 slot[i].Voice.FastFadeForSteal()   ◄── reused 5 ms declick fade
```

The choked voices keep rendering their own fading tails through the **unchanged** per-block mix loop and are freed by the **existing** "voice finished → free slot" branch in `Read`. No new type, no new file, no new service, no config, no new magic number, no mix-loop change.

**Why the choke lives inside `StartVoiceInSlot` (not inline in `NoteOn`):** `StartVoiceInSlot` is the *single* point where any voice begins sounding — it is called both from the free-slot path in `NoteOn` and from the deferred pending-note start in `Read` (the voice-stealing handoff). Placing the choke there makes it fire correctly on **both** onset paths with one implementation (DRY), and the just-started slot is naturally excluded by an index check.

## 6. Components & Responsibilities

| Component | Change | Owns | Does NOT own |
|---|---|---|---|
| `Sf2RegionResolver.BuildRegion` | Read gen 57 via `GetEffectiveRaw(zone, globalZone, ExclusiveClass, 0)`; pass to the `SampleRegion` ctor. | Interpreting the SF2 generator into a region field. | Any choke logic; any voice/engine state. |
| `SampleRegion` | +1 read-only `ExclusiveClass` property; +1 defaulted ctor parameter (`int exclusiveClass = 0`, appended last). | Carrying the immutable class id for the region. | Runtime matching. |
| `SamplePlaybackVoice` | Expose `ExclusiveClass => region.ExclusiveClass`. | Reporting its region's class. | Matching / choking. |
| `IVoice` | +1 read-only member `int ExclusiveClass { get; }`. | The abstract contract that a voice reports its class (0 = none). | — |
| `InactiveVoice`, `StubVoice`, `NanEmittingVoice` | Return `0`. | Reporting "no class" for non-sample voices. | — |
| `Synthesizer.StartVoiceInSlot` | After starting the voice, if its class ≠ 0, scan the pool and `FastFadeForSteal()` same-class same-channel occupied non-draining voices (excluding this slot). | The choke decision and the matching scan. | The fade mechanism (delegated to the voice). |

## 7. Interactions & Data Flow

**Key flow — closed hat chokes a ringing open hat (channel 9, class 1):**

1. The open hat (key 46) is sounding in some slot; its voice reports `ExclusiveClass == 1`.
2. `NoteOn(9, 42, vel)` arrives. `FindFreeSlot()` returns a free slot (drums do not fill the pool).
3. `StartVoiceInSlot(freeSlot, 9, 42, vel)` starts the closed-hat voice; it reports `ExclusiveClass == 1`.
4. The choke scan runs: it finds the open-hat slot (occupied, not draining, channel 9, class 1, different slot) and calls `FastFadeForSteal()` on its voice.
5. The open-hat voice re-targets its `GainRamp` to 0 and deactivates once silent (over the existing ~5 ms window); `Read`'s finished-voice branch frees its slot on a later block.
6. The closed-hat voice ramps up from its own envelope attack. Result: click-free cut, constant hi-hat level.

**Deferred-onset flow (pathological full pool):** if the pool were full when the exclusive-class note arrived, `NoteOn` parks it as a pending note behind a steal victim (existing voice-stealing behaviour, unchanged). When the victim reaches silence, `Read` calls `StartVoiceInSlot` for the pending note — and the choke scan runs **there**, so the choke still happens, just deferred by up to the victim's fade. This path is not reached by drum material (§11 O2).

**No-class flow (the regression guarantee):** a voice whose `ExclusiveClass == 0` (every non-SF2 voice, and every SF2 region without gen 57) causes `StartVoiceInSlot` to `return` before the scan. The note path is byte-for-byte today's path.

## 8. Data Model (Conceptual)

One new immutable scalar on the region entity:

| Entity | Field | Type | Default | Meaning |
|---|---|---|---|---|
| `SampleRegion` | `ExclusiveClass` | int | `0` | SF2 gen-57 value; `0` = the voice belongs to no choke group; any non-zero value names a choke group matched within a channel. |

No new entity, no relationship change. The class id is a plain identifier compared for equality; it carries no ordering or range semantics beyond "0 is none".

## 9. Contracts & Interfaces (Abstract)

**`IVoice.ExclusiveClass` (new, read-only)**
- **Input:** none (property read).
- **Output:** the voice's exclusive class as an int; `0` means "no choke group".
- **Invariant:** immutable for the voice's lifetime; reading never mutates the voice; every implementer returns a stable value. Non-sample voices (inactive/stub/NaN) return `0`.
- **Consumer:** `Synthesizer` only.

**`SampleRegion.ExclusiveClass` (new, read-only)**
- **Semantics:** the value of SF2 generator 57 for the resolved instrument zone, or `0` when the generator is absent. Passed through unmodified (no clamp): every non-zero value is a valid group id, and a clamp would be a guard for a scenario that cannot change behaviour (Design Contracts §6 "defensive code for impossible scenarios").

**`Sf2RegionResolver` gen-57 read (new)**
- Reuses `GetEffectiveRaw(zone, globalZone, Sf2GeneratorType.ExclusiveClass, defaultValue: 0)` — the identical local-then-global zone fallback used for `SampleModes`/`OverridingRootKey`. Read the **unsigned raw** amount (not the int16 cast used for signed generators like pan), because exclusive class is an unsigned identifier.

**`Synthesizer` choke (new, internal to `StartVoiceInSlot`)**
- **Precondition:** a voice has just been placed in `slotIndex` for `channel`.
- **Behaviour:** let `c = pool[slotIndex].Voice.ExclusiveClass`. If `c == 0`, do nothing. Otherwise, for every slot `i`: skip if `i == slotIndex`, if not occupied, if already draining (`PendingChannel != NoPendingNote`), if `Channel != channel`, or if `Voice.ExclusiveClass != c`; for the rest, call `Voice.FastFadeForSteal()`.
- **Postcondition:** every other sounding, non-draining voice of class `c` on `channel` is fading to silence; the new voice is untouched.
- **Complexity:** O(pool), single pass, no allocation.

## 10. Cross-Cutting Concerns

- **Declick (INV-1):** the choke's only level change is `FastFadeForSteal()`, which re-targets the existing `GainRamp` to 0 — the same click-free 5 ms fade voice-stealing already ships. No hard cut, so no click.
- **Allocation / real-time (INV-2):** the scan is a linear pass over the fixed pool with no allocation; `StartVoiceInSlot` remains allocation-free apart from the voice the patch already creates.
- **Idempotency:** `FastFadeForSteal()` is idempotent (no-op if already stealing or inactive), so choking a voice that is already fading — or a slot revisited across the deferred path — is safe.
- **Draining-slot safety:** the scan skips slots with a pending note (`PendingChannel != NoPendingNote`) so an in-flight steal handoff is never disturbed; the parked note, once it starts, runs its own choke scan (convergent).
- **Consistency model:** the choke is synchronous within the `NoteOn`/`Read` call that starts the triggering voice — no deferred queue, no cross-block state beyond the voices' own fades.
- **Observability / error handling:** no new failure modes. The resolver's existing defensive-degradation philosophy is preserved: an absent or malformed gen 57 degrades to class 0 (no choke), never throws on the note path.

## 11. Quality Attributes & Trade-offs

| Attribute | How the design addresses it |
|---|---|
| **Correctness** | Matches the SF2 within-channel/preset rule (A2); chokes on both onset paths via the single `StartVoiceInSlot` seam. |
| **Performance** | One O(pool) scan per note that carries a non-zero class; the common no-class note early-returns. No allocation, no DSP added. |
| **Maintainability** | +1 IVoice member, +1 region field, +1 resolver read, +1 scan. No new type/file/service. The scan mirrors the existing `SetChannelPitchBend`/`SetChannelModulation`/`FindStealVictim` pool-scan idiom already in the class. |
| **Regression safety** | `ExclusiveClass == 0` → early return → bit-identical for all non-choke content (structural guarantee, not a test-only claim). |

**Trade-offs made explicit:**

- **T1 — Choke placed in `StartVoiceInSlot`, not in `NoteOn`.** Alternative: read the class in `NoteOn` and scan there. Rejected: it would miss the deferred pending-note onset (voice-stealing handoff), forcing a second copy of the scan in `Read` — a DRY violation. The chosen seam fires on both onsets with one implementation. Downside: the choke reads `slot.Voice.ExclusiveClass` after placement rather than before; sonically identical because the choked voices and the new voice occupy different slots and the new voice is excluded by index.
- **T2 — Read the class off `IVoice`, no `VoiceSlot.ExclusiveClass` field.** Alternative: cache the class on the slot at note-start (mirroring the voice-stealing `Age`/`Released` fields). Rejected: the class originates in the region and is only reachable through the voice, so the synth must widen `IVoice` regardless; a slot field would be a **redundant** copy of `IVoice.ExclusiveClass` (Design Contracts §4 can-it-be-deleted). Reading `slot.Voice.ExclusiveClass` directly in the scan is leaner. The voice is guaranteed non-null for occupied slots (engine invariant). Net surface: `IVoice` +1, `VoiceSlot` +0.
- **T3 — No exclusive-class term in voice-stealing victim selection.** Adding one would let a full pool prefer evicting a same-class voice. Rejected as YAGNI: the drum pool is never full (the bug's whole premise is layering *within* an under-full pool), and the deferred path already applies the choke correctly. Cost of adding it (a fourth tuple term, more tests) buys nothing for the actual defect.

**Alternatives rejected wholesale:**
- A dedicated choke DSP / crossfade — rejected, `FastFadeForSteal` already declicks (would duplicate DSP).
- Widening `IPatch.StartVoice` to also return the class — rejected, more surface (4 implementers) and still needs the class on the voice for later matching.
- A configurable fade time for the choke — rejected (§3, no operator, no environment difference; the 5 ms is inherited).

## 12. Risks & Mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Gen 57 absent in Florestan | Re-render unchanged; fix inert | **Retired** — verified present, 120 records, class 1 on 24 zones (§4). |
| Choking the just-started voice (self-choke) | New note silenced instantly | Scan excludes `i == slotIndex`; enforced by an explicit index guard. |
| Disturbing an in-flight steal handoff | Parked note lost / double-started | Scan skips draining slots (`PendingChannel != NoPendingNote`); the parked note runs its own scan on start (convergent). |
| Non-drum content that legitimately uses exclusive classes (some melodic SF2 presets set them) | Unexpected choking | Correct by spec — such presets *intend* the choke; matched within channel only, so cross-instrument bleed is impossible. |
| Signed misread of the generator amount | Wrong / negative class | Read the **unsigned** `RawAmount` (A5), not the int16 cast; 0 stays the sole special value. |

**Note on PR #25 (master headroom):** this design supersedes the master-headroom misdiagnosis for the hi-hat pump. It does not touch the master bus. Whether the ~6 dB headroom trim in PR #25 is still wanted (and at what value) is a **separate** call to be made after this lands, not bundled here.

## 13. Open Questions

- **O1 — Force-Your-Way proof isolation.** The cleanest automated hi-hat-stability metric renders the percussion channel (9) in isolation and measures windowed RMS variation across 18–24s (flat after the fix, swelling before). If the MIDI render harness cannot easily solo a channel, the fallback is a full-mix windowed level-stability metric (coefficient of variation of sliding-window RMS in the 18–24s span, expected to drop). **Recommendation:** attempt channel-solo; fall back to full-mix CoV. This is a QA/implementation choice, not an architectural one — flagged for John/Jenny. Gate the test on the dev-tree asset with `Assert.Ignore` when absent, matching `MidiSongRenderTests`.
- **O2 — Full-pool drum edge.** Accepted as a documented limitation: when the pool is full, the choke is applied at the deferred pending-note start rather than at `NoteOn`. Not reachable by drum material. **Recommendation:** accept (KISS); do not add exclusive-class awareness to victim selection.

## 14. Implementation Guidance for the Next Agent

Ordered build phases (still no code — architectural units). Single PR bundling this doc + the implementation, per DiVoid #1165.

1. **Region field.** Add `ExclusiveClass` (read-only int) to `SampleRegion` with a defaulted `int exclusiveClass = 0` constructor parameter **appended last** (after `chorusSend`), so every existing positional caller — including the test builders that pass through `chorusSend` — stays valid. XML summary on the property.
2. **Resolver read.** In `Sf2RegionResolver.BuildRegion`, read `GetEffectiveRaw(zone, globalZone, Sf2GeneratorType.ExclusiveClass, defaultValue: 0)` (unsigned raw, no int16 cast, no clamp) and pass it to the `SampleRegion` ctor. Inline the single call — do **not** add a `BuildExclusiveClass` helper (it would be a pure-restatement wrapper over `GetEffectiveRaw`; the Build* helpers exist for unit conversion/clamping, of which this has none).
3. **Voice contract.** Add `int ExclusiveClass { get; }` to `IVoice` with an XML summary (0 = none, immutable, engine-only consumer). Implement: `SamplePlaybackVoice` → `region.ExclusiveClass`; `InactiveVoice`, `StubVoice`, `NanEmittingVoice` → `0`.
4. **Engine choke.** In `Synthesizer.StartVoiceInSlot`, after the slot is populated, read the new voice's class; if `0`, return; otherwise run the O(pool) scan described in §9 (`FastFadeForSteal` on occupied, non-draining, same-channel, same-class slots, excluding `slotIndex`). Prefer a small private helper (e.g. one named method) invoked at the end of `StartVoiceInSlot` so the method stays readable; keep it allocation-free. Update the `Synthesizer` class XML summary to note the exclusive-class choke, mirroring how the voice-stealing paragraph was added.
5. **Deterministic unit tests** (new fixture, e.g. `ExclusiveClassChokeTests`). Use a small active test double that reports a settable `ExclusiveClass` and records whether `FastFadeForSteal` was called (the existing `StubVoice` is inactive and unsuitable as a choke target; extend the helpers). Assert:
   - same non-zero class + same channel → first voice's `FastFadeForSteal` **called**;
   - different class, same channel → **not** called;
   - class 0 on both → **not** called;
   - same non-zero class, **different** channel → **not** called.
   A render-level variant (following `VoiceStealingTests`: sustained-DC regions, trailing-mean-abs level) is acceptable for the same-class and cross-channel cases; the different-class case is cleanest with the direct-observation double.
6. **Bit-identical regression test.** Render a short sequence of class-0 voices and assert the output matches a baseline (the class-0 early-return makes this a structural guarantee; the test pins it).
7. **Force-Your-Way deliverable proof.** Add a render-proof test (home: `test/Pooshit.AudioSynth.Tests/Midi/`, gated with `Assert.Ignore` on the dev-tree asset) implementing the O1 metric: hi-hat/percussion level is stable across the 20–22s and 2:09 density spikes. Record the observed before/after level-stability figures in the PR body alongside the §4 gen-57 confirmation.
8. **Audit gates.** Both TFMs 0-warning; `dotnet test` green (foreground, per #7173). Body-comment grep = 0; XML-summary size gate; one type per file. Cite Code Contracts #114 and Design Contracts #1136 in the PR body.

## Pre-Design Checklist (Design Contracts #1136 §5)

**KISS / DRY / YAGNI**
- [x] No new type whose value-space mirrors an existing type — one int field on an existing entity.
- [x] No new abstraction with a single implementation — no new interface/type at all.
- [x] No element justified by "might need later" — the full-pool victim-selection term (T3) and a config knob were explicitly rejected as YAGNI.
- [x] No deprecation period / feature flag / compatibility shim — atomic change.
- [x] No "inline N sites" duplication decision — the choke is one scan in one method (DRY seam chosen precisely to avoid a second copy in `Read`).

**Existing systems first**
- [x] Audited the existing surface: the region→voice seam already carries pan/reverb/chorus; the exclusive class rides it identically. No new layer.
- [x] No new persisted store; one field on an existing in-memory entity.
- [x] `IVoice.ExclusiveClass` has a named consumer (`Synthesizer`) that acts on it (the choke) — not transitive-dead.

**Configurability**
- [x] No new config knob. The 5 ms fade is the inherited `GainRamp.DefaultSmoothingSeconds`; no new magic number.

**Less is better**
- [x] Can-it-be-deleted / merged / inlined applied: rejected a redundant `VoiceSlot.ExclusiveClass` field (T2) and a pointless `BuildExclusiveClass` helper (step 2).
- [x] Trade-offs T1–T3 named explicitly with the simpler-vs-complex comparison.
- [x] No compromise shape — the class is read directly off the voice, the radical-lean choice.
- [x] Reader inventory: `IVoice.ExclusiveClass` implementers enumerated (4) and `SampleRegion` ctor callers preserved by the append-last parameter.

**Document discipline**
- [x] Cites Code Contracts #114 and Design Contracts #1136 as load-bearing.
- [x] Out-of-scope listed explicitly (§2).
- [x] Scope inventories explicit (§6 component table, §14 phases).
- [x] Not a supersession of a live design (bug #7226 supersedes the #7212 headroom misdiagnosis; no predecessor design doc to banner).

**Chain:** sarah-software-architect (this design) → john-backend-dev (implement, one PR bundling doc + code) → jenny-qa-reviewer.
