# Architectural Document: Stereo Reverb — the Engine's First Effect

**Author:** Sarah (software architect) · **Date:** 2026-07-27
**Source task:** DiVoid #7162 (PR 16) · **Project:** #6128 · **Map root:** #6708
**DiVoid copy of this doc:** documentation node (see task #7162 links).
**Load-bearing contracts:** Design Contracts #1136 (§1 KISS/DRY/YAGNI, §3 configurability, §4 less-is-better, §5 Pre-Design Checklist) + Code Contracts #114 (§0 principles, §6.10 self-audit). PR-shape #1165 (design + first increment ship in one PR).
**Precedent:** master mix-bus stage #7126 (`docs/architecture/mix-bus.md`), stereo-pan #7127 (`docs/architecture/stereo-pan.md`), rewrite roadmap #6401 §10/§14.

---

## 1. Problem Statement

The synth engine has no effects: every voice reaches the master bus dry, so renders sound like an anechoic close-mic — flat, disjoint, "not a real recording." Real recordings and GM soundfonts (which *assume* a reverb send exists) carry room/hall ambience that gives spatial depth and glues instruments — now stereo-panned (PR 15) — into a shared acoustic space.

**Goal:** add a **stereo algorithmic reverb** to the engine so a rendered song has audible room/hall ambience, including a decaying tail after notes stop.

**Success criteria:**
- Rendering `07dkc2bram.mid` through Florestan produces an audibly reverberant result with an ambient tail (energy continues after the last note-off / at song end).
- The reverb is **inherently stable**: bounded output for bounded input, feedback strictly `< 1`, never producing NaN/Inf from finite input, never relying on the master soft-clip or `Finalize` clamp to stay bounded.
- **Dry-passthrough regression:** with the reverb configured but its wet mix at 0, the render is bit-identical (float-exact) to the dry PR-15 baseline. With no reverb configured at all, every existing test render is unchanged.
- `Synthesizer.Read` stays allocation-free in steady state (INV: reverb delay buffers allocated at construction).
- `Finalize` remains the sole NaN/Inf guard (INV-2, untouched).

## 2. Scope & Non-Scope

**In scope (PR 16, one feature, one PR):**
- A stereo algorithmic reverb DSP (Schroeder/Freeverb topology) as a new concrete type.
- Its integration as a **single global master insert** on the summed stereo bus, between voice mixing and the existing `ApplyMasterBus` soft-clip.
- A small immutable reverb-parameter type (room size, damping, wet, width) with named-const defaults, threaded through `SynthesizerOptions` as an optional setting (absent by default).
- Wiring the render tool (`MidiRender/Program.cs`) to opt into a reverb preset so the deliverable render has a tail.
- Unit tests: stability (bounded, decaying tail from a bounded impulse), tail-presence (wet > 0), and dry-passthrough regression (wet = 0 ⇒ float-identical to dry; reverb absent ⇒ unchanged).

**Out of scope (explicitly — later work / YAGNI boundary):**
- **Option B** — per-channel / per-voice reverb *send* (MIDI CC91 reverb depth + SF2 gen 16 `reverbEffectsSend`). Filed as an immediate follow-up task; see §12. Not built here.
- Any other effect: chorus (CC93), delay/echo, EQ.
- Any `IAudioEffect` / effects-graph abstraction. One effect ships; the interface is deferred until a second concrete effect exists (§10, per #6401 §10 "≥2 impls" rule).
- A runtime seam on `ISynthesizer` to change reverb parameters mid-render (no MIDI driver exists for a *global* reverb in Option A — CC91 is per-channel and belongs to Option B).
- Non-stereo reverb. Reverb is a stereo master effect; mono/other channel counts are unaffected (§5).

## 3. Assumptions & Constraints

| # | Assumption / Constraint | Source | Confidence |
|---|---|---|---|
| C1 | Internal format is 32-bit float, interleaved; master bus is stereo `[L,R,L,R,…]`. | Synthesizer.cs, #6401 §3 | Certain |
| C2 | Reverb sits at the master bus (post voice-sum), the first *global stateful* stage. | task #7162, #7126 | Certain |
| C3 | `Read` must stay allocation-free in steady state; all reverb buffers allocated at construction (they depend on sample rate). | Synthesizer.cs L15, task #7162 | Certain |
| C4 | `Finalize` stays the sole NaN/Inf guard (INV-2); reverb must be *inherently* stable, not rely on the clamp. | Synthesizer.cs L277, task #7162 | Certain |
| C5 | Engine is sample-rate-agnostic; Freeverb delay-line lengths are defined at 44.1 kHz and scale by `sampleRate / 44100`. | #6401 §3, standard Freeverb | Certain |
| C6 | Deliverable render uses 44.1 kHz stereo Florestan (`__Florestan_Basic_GM_GS.sf2`, #7153). | task #7162 §deliverable | Certain |
| C7 | Existing DSP primitives (`GainRamp`, `BiquadLowPassFilter`, `ModulationLfo`) are `public` and unit-tested directly from the test assembly; the reverb follows that precedent. | test/ layout | Certain |
| A1 | A single, well-chosen global room character + a wet control satisfies the user's ask ("room/hall ambience … improve the sound quite a lot"). Option B is the refinement if the global reverb reads too uniform. | task #7162 recommendation | High — validate by A/B (§ deliverable) |

## 4. Architectural Overview

The reverb is a **single stateful insert** on the existing master bus. Nothing about the pull pipeline, the voice engine, the sequencer, or the MIDI/SF2 layers changes. The only edit to `Synthesizer` is: after the per-block voice mix and before `ApplyMasterBus`, pass the interleaved stereo master block through the reverb in place.

```
 per-block pipeline in Synthesizer.Read (stereo path):

   voices ──► mix into masterSlice (dry stereo)        [unchanged]
                     │
                     ▼
             ┌───────────────┐
             │  Reverb.Process│  in-place: masterSlice ← dry + wet   [NEW, only when configured + stereo]
             └───────────────┘
                     │
                     ▼
             ApplyMasterBus (soft-clip)                 [unchanged — now limits dry+wet]
                     │
                     ▼
             Finalize (sole NaN/Inf + hard-clip guard)  [unchanged, INV-2]
                     │
                     ▼
             copy to destination
```

The reverb itself is a classic **stereo Freeverb**: a mono send (scaled sum of L+R) feeds two parallel banks of **comb filters** (each with a damping low-pass in its feedback path), whose outputs pass through a series of **allpass filters**; the two banks use slightly different delay lengths (a fixed "stereo spread") so left and right decorrelate, and a width control cross-mixes them. The wet result is added on top of the untouched dry signal.

```
 Reverb.Process, per stereo frame:

   in = (L + R) * inputGain          (mono send, inputGain ≈ 0.015)

   ┌── comb bank L (8 combs, summed) ──┐        ┌── comb bank R (8 combs, summed) ──┐
   │  each: damped-feedback delay line │        │  lengths = L lengths + spread(23) │
   └───────────────┬───────────────────┘        └───────────────┬───────────────────┘
                   ▼                                             ▼
         allpass chain L (4 series)                     allpass chain R (4 series)
                   ▼   wetL                                      ▼   wetR
                   └──────────────┬──────────────────────────────┘
                                  ▼
     outL = L*dryGain + wetL*wet1 + wetR*wet2        (dryGain ≡ 1.0, fixed)
     outR = R*dryGain + wetR*wet1 + wetL*wet2
     wet1 = wet*(width/2 + 0.5),  wet2 = wet*((1-width)/2)
```

## 5. Components & Responsibilities

Four new types, each in its own file (one-type-per-file, Code Contracts §6.10). New types live in `Synthesis/`, consistent with the existing flat placement of `GainRamp`, `BiquadLowPassFilter`, `ModulationLfo`. No `Audio/Effects/` folder is introduced — that is premature structure for a single effect (KISS/YAGNI).

| Type | File | Access | Owns | Does NOT own |
|---|---|---|---|---|
| `ReverbSettings` | `Synthesis/ReverbSettings.cs` | `public sealed` (immutable value) | The four reverb parameters (RoomSize, Damping, Wet, Width) + named-const defaults; range-clamping in its ctor so feedback can never be driven `≥ 1`. | Any DSP state, buffers, or sample-rate knowledge. |
| `Reverb` | `Synthesis/Reverb.cs` | `public sealed` | The stereo Freeverb processor: ctor-allocated comb/allpass buffers sized to the sample rate; in-place `Process` of an interleaved stereo block; the dry+wet mix; inherent stability. | Voice mixing, master soft-clip, NaN guard (those stay in `Synthesizer`). It never re-clips or re-guards — it only adds bounded wet to dry. |
| `CombFilter` | `Synthesis/CombFilter.cs` | `internal sealed` | One comb filter: a single delay line with a one-pole damping low-pass in its feedback path; `feedback < 1`. | The bank topology, the mix, stereo layout. |
| `AllpassFilter` | `Synthesis/AllpassFilter.cs` | `internal sealed` | One allpass filter: a single delay line with fixed feedback (0.5). | Everything above it. |

**Changed types:**
- `SynthesizerOptions` — gains one optional `ReverbSettings? Reverb` (default `null` = no reverb). Immutable, validated as today.
- `Synthesizer` — gains one nullable `Reverb` field, constructed only when `options.Reverb` is present **and** output is stereo; one guarded call in `Read`. No other change.
- `MidiRender/Program.cs` — constructs `SynthesizerOptions` with a reverb preset so the deliverable render is reverberant (one changed line).

**Why `CombFilter` / `AllpassFilter` are their own types (DRY check):** Freeverb is 8 combs + 4 allpasses **per channel** = 24 identical-logic filter states. A comb's damped-feedback update is ~5 lines reused 16×; the allpass update ~4 lines reused 8×. `block_size × site_count` is far above the ~15-20 threshold (#1267), and each helper names in one word — the extraction plainly earns its keep versus 24 sets of parallel inline arrays. They are `internal` (pure implementation detail of `Reverb`; no external consumer).

## 6. Interactions & Data Flow

1. **Construction (offline, allocating):** the host builds `SynthesizerOptions` with an optional `ReverbSettings`. `Synthesizer`'s ctor, when `options.Reverb != null` and `options.Channels == 2`, constructs a `Reverb` for the configured sample rate — allocating every comb and allpass delay line up front (lengths = the Freeverb 44.1 kHz tunings scaled by `sampleRate/44100`, each `≥ 1`). When reverb is absent or output is non-stereo, the field stays `null` and nothing is allocated.
2. **Per block (hot path, allocation-free):** `Read` mixes voices into `masterSlice` exactly as today. Then, `if (reverb != null) reverb.Process(masterSlice)` — the reverb walks the interleaved block frame by frame, advancing its delay lines (state carried across blocks), replacing each `[L,R]` pair with `dry + wet`. Then `ApplyMasterBus` and `Finalize` run unchanged. Block size is irrelevant to correctness — state lives in the member buffers, so partial final blocks are handled naturally.
3. **No cross-component messaging.** The reverb is a leaf: it reads/writes only the master block passed to it and its own buffers. It is invisible to voices, channels, the sequencer, and the SF2/MIDI layers.

**Pull/stability invariant:** for any finite input, comb feedback ≤ `0.98 < 1` and the in-feedback damping low-pass strictly removes energy each pass, so the delay-line energy decays; the allpass sections are unconditionally stable (feedback 0.5). Output is therefore bounded and decaying for a bounded impulse — proven by test, not assumed.

## 7. Data Model (Conceptual)

No persisted data, no entities. The only new state is DSP state, all transient and owned by `Reverb`:

- **ReverbSettings** (value): `RoomSize`, `Damping`, `Wet`, `Width` — each a normalized scalar in `[0,1]`, clamped in the ctor. Named-const defaults form a "sensible hall" preset.
- **Reverb** (runtime): two comb banks (8 each) + two allpass chains (4 each); derived mix coefficients (`wet1`, `wet2`, `inputGain`, per-comb `feedback`/`damping`) computed once from the settings + sample rate at construction.
- **CombFilter / AllpassFilter** (runtime): one `float[]` delay buffer + an integer write index (+ a one-pole damping store for the comb).

## 8. Contracts & Interfaces (Abstract)

**`ReverbSettings` (construction contract).**
- Inputs: four normalized scalars, each clamped to `[0,1]` at construction. `RoomSize` maps to comb feedback in `[0.7, 0.98]` (feedback = `RoomSize·0.28 + 0.7`), guaranteeing the `< 1` stability bound regardless of caller input. `Wet` is the dry/wet balance; `Wet = 0` ⇒ silent reverb. `Damping` sets the in-feedback low-pass amount; `Width` the stereo cross-mix.
- Invariant: immutable; no method can produce a feedback ≥ 1.
- Defaults: exposed as named `const`s; a `Default` preset ("hall") is the recommended value for the render tool.

**`Reverb` (processing contract).**
- Input: an interleaved stereo block `Span<float>` of length `frames·2` (the master `masterSlice`). Requires stereo layout.
- Output semantics: in place — each `[L,R]` pair becomes `[L + wetL, R + wetR]` where `dryGain ≡ 1.0` is fixed (the dry signal passes through unchanged) and the wet term is scaled by `Wet`/`Width`. Therefore **`Wet = 0` ⇒ output is bit-identical to input** (dry·1.0 + wet·0.0 = dry, for finite samples).
- Invariants: (a) allocation-free; (b) bounded, decaying, NaN/Inf-free output for finite input; (c) never relies on downstream clamping; (d) state persists across calls (the tail spans blocks).
- Non-goal: it does not clamp, soft-clip, or NaN-guard — those remain `ApplyMasterBus` / `Finalize`.

**`Synthesizer` integration contract.** Unchanged public surface. Internally: reverb is applied only on the stereo path, only when configured, exactly once per block, strictly between voice mixing and `ApplyMasterBus`. `Finalize` remains the single NaN/Inf choke point (INV-2). A centered/dry configuration reproduces prior output bit-for-bit (regression).

## 9. Cross-Cutting Concerns

- **Stability (the load-bearing one):** feedback bounded `< 1` by the `RoomSize → [0.7,0.98]` map; damping low-pass dissipates energy per pass; allpass feedback fixed at 0.5; mono-send `inputGain ≈ 0.015` keeps wet amplitude in range. Reverb is BIBO-stable by construction; the master soft-clip and `Finalize` remain pure safety nets that a correct reverb never exercises for amplitude.
- **Allocation / real-time safety:** every buffer allocated in the `Reverb` ctor; `Process` allocates nothing. Preserves the alloc-free `Read` steady state.
- **NaN/Inf:** the reverb performs only finite arithmetic on finite inputs and adds bounded wet; it introduces no new NaN source. `Finalize` stays the sole guard (INV-2 untouched).
- **Determinism / regression:** dry-passthrough is float-exact by design (dryGain ≡ 1.0, wet·0 = 0). This makes the "wet=0 reproduces PR-15" guarantee structural, not approximate.
- **Concurrency:** unchanged — the reverb is per-`Synthesizer` state, mutated only inside `Read`, same single-threaded model as the rest of the engine.
- **Non-stereo degradation:** consistent with the pan feature, reverb is stereo-only; non-stereo output silently runs dry (no reverb allocated). Documented, not thrown.

## 10. Quality Attributes & Trade-offs

**Decision 1 — Placement: master insert (Option A), not per-channel send (Option B). CONFIRMED.**
Option A is one reverb on the summed stereo master with a global wet. It is the smallest change (touches only the master-bus stage), immediately and audibly transformative, and carries no plumbing risk. Option B (CC91 + SF2 gen 16 per-voice send bus) is GM-accurate — it respects "this instrument dry, that one wet" — but requires a send seam on `ISynthesizer`, sequencer CC91 handling, SF2 gen-16 resolution, and a mixed-back send bus: materially more surface for a first effect. **Trade-off:** Option A applies one uniform room to everything, which *can* read flat if the mix wants some instruments drier than others. Probability this matters for the target song: moderate; cost if it does: build Option B next. Present cost of doing B now: a multi-seam change across four files for uncertain benefit. **Call: ship A; file B as the immediate follow-up** (§12), to be pulled forward only if the A/B listen shows the global reverb is too uniform. This matches the task #7162 recommendation.

**Decision 2 — No `IAudioEffect` abstraction. CONFIRMED.**
Per #6401 §10, an interface is committed only with ≥2 concrete implementations. Exactly one effect ships here. A `public sealed Reverb` in its own file is the right shape; the family interface is born with the second effect (chorus/delay), not speculatively now. **Trade-off:** when the second effect lands, a small refactor introduces `IAudioEffect` and an effect list on the master bus. That refactor is cheap and, crucially, will be *shaped by the real second effect* rather than guessed today (§4 less-is-better: pre-built extensibility extends in the wrong direction). Rejected alternative: ship `IAudioEffect` + an effects chain now — pure YAGNI indirection with one impl.

**Decision 3 — Reverb parameters are construction-time config on `SynthesizerOptions`, defaulting to absent. CONFIRMED.**
The reverb is opt-in: `SynthesizerOptions.Reverb` defaults to `null`, so every existing test and render is bit-identical with zero changes, and the "reverb absent ⇒ unchanged" guarantee is the default. The render tool opts in explicitly. **§3 configurability check:** `RoomSize/Damping/Wet/Width` are *not* speculative future-tuning knobs — they are the intrinsic parameter surface of the effect itself (the task specifies them), cohesive in **one** `ReverbSettings` type (not four loose options fields — no sprawl), carry **no** test-matrix cost (pure scalar coefficients; buffer sizes are fixed by sample rate, not by these params), and mirror how the engine already exposes creative controls (pan/gain/bend seams). They are construction-time only — **no runtime `ISynthesizer` seam** — because Option A has no MIDI driver for a *global* reverb (CC91 is per-channel = Option B). Rejected alternative: expose only `Wet` and hardcode room character as `const`s — rejected because it would force a recompile to adjust the room, and the marginal cost of the other three fields is a single immutable struct. Rejected alternative: a runtime `SetReverb` seam — YAGNI, no caller.

**Decision 4 — Insert *before* `ApplyMasterBus`, not after. CONFIRMED.**
Placing the reverb before the soft-clip lets the soft-clip limit the *whole* bus (dry + tail) musically; placing it after would let the wet tail bypass the soft-clip and hit `Finalize`'s hard clip (harsh). Dry-passthrough regression holds either way (wet=0 ⇒ reverb output = dry ⇒ identical soft-clip input).

| Attribute | How the design serves it |
|---|---|
| Performance | One extra in-place pass over the stereo block; alloc-free; `null` field ⇒ zero cost when reverb is off. |
| Maintainability | Four small, single-responsibility types; no framework; no change to the pull pipeline or MIDI/SF2 layers. |
| Correctness | Dry-passthrough is structural (float-exact), not tuned; stability is proven by test. |
| Extensibility | The second effect naturally introduces `IAudioEffect` + a bus effect list, shaped by a real second impl. |

## 11. Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Runaway feedback / instability | `RoomSize → [0.7, 0.98]` clamp guarantees feedback `< 1`; damping low-pass dissipates energy; allpass fixed at 0.5. Stability unit test (bounded impulse ⇒ bounded, decaying, NaN-free). |
| Dry regression drift (wet=0 ≠ PR-15) | `dryGain ≡ 1.0`, wet·0 = 0 ⇒ float-exact passthrough. Explicit regression test asserts bit-identity to the reverb-absent render. |
| Hidden allocation in `Read` | All buffers ctor-allocated; `Process` uses only member state + the passed span. Review + the existing alloc-discipline convention. |
| Global reverb reads too uniform | A/B vs `renders/song-dkc2-PAN.wav`; Option B (per-channel send) is filed and ready to pull forward. |
| Sample-rate scaling wrong at non-44.1k | Lengths scale by `sampleRate/44100`, floored to `≥ 1`; deliverable runs at 44.1 kHz (exact Freeverb tunings). |
| Non-stereo misconfiguration | Reverb only constructed when `Channels == 2`; non-stereo runs dry (documented), matching pan degradation. |

## 12. Migration / Rollout Strategy

Additive, no migration. Reverb is off by default (`SynthesizerOptions.Reverb == null`), so the change is invisible to every existing consumer and test until explicitly opted into. The render tool opts in to produce the deliverable. **Immediate follow-up (filed as a DiVoid task):** Option B — per-channel/per-voice reverb send (MIDI CC91 reverb depth in `MidiSequencer`, SF2 gen 16 `reverbEffectsSend` in `Sf2RegionResolver` → `SampleRegion`, a `SetChannelReverbSend` seam on `ISynthesizer`, and a send bus mixed into the same master reverb) — to be pulled forward only if the global reverb proves too uniform on the A/B listen.

## 13. Open Questions

1. **Default preset character:** the recommended default is a mid-size "hall" (RoomSize ≈ 0.7, Damping ≈ 0.5, Wet ≈ 0.25, Width ≈ 1.0). Final numeric defaults are an ear-tuning call the implementer/user makes against the A/B render — none of them affect the architecture. (Non-blocking.)
2. **Option B trigger:** does the global reverb read acceptably on `07dkc2bram`, or should the CC91/gen-16 send be pulled forward? Answer comes from the A/B listen; does not block PR 16. (For the orchestrator/user.)

## 14. Implementation Guidance for the Next Agent (john-backend-dev)

Ordered build phases. One feature, one PR — this design doc ships in the same PR (#1165). Cite Code Contracts #114 (§0 principles, §6.10 self-audit) throughout.

1. **`AllpassFilter`** (`Synthesis/AllpassFilter.cs`, `internal sealed`): one delay-line allpass, fixed feedback 0.5, ctor-allocated buffer, alloc-free per-sample process. XML `///` docs only (no body comments).
2. **`CombFilter`** (`Synthesis/CombFilter.cs`, `internal sealed`): one delay-line comb with a one-pole damping low-pass in the feedback path; ctor takes buffer length, feedback (`< 1`), damping; alloc-free process.
3. **`ReverbSettings`** (`Synthesis/ReverbSettings.cs`, `public sealed`, immutable): `RoomSize`, `Damping`, `Wet`, `Width`; clamp each to `[0,1]` in the ctor; named-const defaults + a `Default` hall preset. Map `RoomSize → feedback = RoomSize·0.28 + 0.7` so feedback is always `< 1`.
4. **`Reverb`** (`Synthesis/Reverb.cs`, `public sealed`): ctor takes `ReverbSettings` + sample rate; build two comb banks (8) and two allpass chains (4) using the standard Freeverb tunings scaled by `sampleRate/44100` (right bank = left + stereo-spread 23); precompute `wet1/wet2/inputGain`. `Process(Span<float>)` walks the interleaved stereo block, mono-sends `(L+R)·inputGain` into both banks, sums combs → series allpasses → `dry·1.0 + wet` cross-mix, in place. Alloc-free. Dry gain fixed at 1.0.
5. **`SynthesizerOptions`**: add an optional `ReverbSettings? Reverb` (default `null`); keep the type immutable and validated.
6. **`Synthesizer`**: add a nullable `Reverb` field; in the ctor, construct it only when `options.Reverb != null` **and** `options.Channels == StereoChannelCount`. In `Read`, call `reverb?.Process(masterSlice)` exactly once, **after** the voice-mix loop and **before** `ApplyMasterBus`. Do not touch `ApplyMasterBus` or `Finalize`.
7. **`MidiRender/Program.cs`**: construct `SynthesizerOptions` with `ReverbSettings.Default` so the deliverable render is reverberant (one line).
8. **Tests** (`test/…`, follow the existing render-proof style, Florestan):
   - *Stability:* feed a bounded impulse through a `Reverb`, render several blocks, assert output is bounded, NaN/Inf-free, and the tail RMS decays across successive windows.
   - *Tail presence:* with `Wet > 0`, assert non-zero RMS in a window after the input goes silent.
   - *Dry-passthrough regression:* render a short sequence twice — once with `ReverbSettings` at `Wet = 0`, once with `Reverb = null` — assert the two outputs are float-identical (and, ideally, that `Wet=0` reproduces a captured dry PR-15 slice).
9. **Deliverable proof:** render `07dkc2bram.mid` via `tools/Pooshit.AudioSynth.MidiRender` with the reverb preset; confirm an audible ambient tail (energy continues after notes stop / at song end), measure tail RMS decay vs the dry render, confirm `Wet=0` reproduces the dry render, and A/B vs `renders/song-dkc2-PAN.wav`.
10. **Gate:** both TFMs build 0-warning; `dotnet test` green; §6.10 self-audit (body-comment grep = 0 on all new/changed files; XML-summary size gate on all accessibilities; one type per file).
