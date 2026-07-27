> **Repo path of record:** `docs/architecture/lfo-tremolo-sweep.md` (branch `feature/lfo-tremolo-sweep`, from `origin/main` @ `9aa2557`). The repo copy is the canonical, reviewable deliverable; the DiVoid documentation node is the graph-discoverable mirror.
> **Task:** DiVoid #7073 (PR 9) · **Project:** #6128 · **Map root:** #6708.
> **Directly extends:** mod-LFO design #7072 (`docs/architecture/mod-lfo.md`, PR 8, merged `9aa2557`) — this is the deferred second half of that feature; the anti-zipper fault line drawn there is the reason these two routings were split out together.
> **Precedents (same additive pattern):** envelope #7063 (PR 6, `0945940`); filter #7067 (PR 7, `d6eebc2`); mod-LFO/vibrato #7072 (PR 8, `9aa2557`).
> **Load-bearing contracts:** Code Contracts #114 §0/§4/§5.5; Design Contracts #1136 §1–§4 + §5 checklist. This design applies EVERY rule in those documents, not only the ones cited inline.

# Architectural Document: LFO Tremolo (volume) + Filter-Cutoff Sweep

## 1. Problem Statement

Through PR 8, every voice carries a per-voice modulation LFO (`ModulationLfo`) that produces a delayed, bipolar `[-1, 1]` triangle at a control rate of `ControlRateFrames = 64`, and that LFO is routed to **pitch (vibrato)**. The two other SF2 mod-LFO destinations are still dark:

- **Tremolo** — the LFO modulating **volume** (SF2 generator 13, `modLfoToVolume`, centibels). Presets that specify it render with no amplitude movement.
- **Filter-sweep** — the LFO modulating the **biquad cutoff** (SF2 generator 10, `modLfoToFilterFc`, cents). Presets that specify it render with a static timbre.

The business/technical goal is to complete the mod-LFO feature by wiring these two routings, sourced from their SF2 generators with spec defaults, so mod-LFO presets render as the SoundFont author intended.

**Success criteria:**
1. A note with a nonzero `modLfoToVolume` renders a periodic amplitude wobble measurable in the WAV envelope, at the LFO rate.
2. A note with a nonzero `modLfoToFilterFc` on a filtered preset renders a periodic high-frequency-energy variation, at the LFO rate.
3. Both routings are click-free — no zipper (INV-1) at control-tick boundaries.
4. Both are NaN/Inf-safe (INV-2): finite inputs never manufacture non-finite samples.
5. **Zero-mod-amount ⇒ bit-identical.** A patch whose volume and filter mod depths are both zero renders bit-for-bit identical to the PR-8 baseline.

## 2. Scope & Non-Scope

**In scope (ONE feature, ONE PR):**
- Route the existing mod-LFO to **volume (tremolo)**, applied per-sample with a control-block linear glide so it does not zipper.
- Route the existing mod-LFO to the **biquad cutoff (filter-sweep)**, via a new coefficient-recompute path on `BiquadLowPassFilter` driven at the control rate.
- Carry the two new mod depths as fields on `LfoParameters` (deferred from PR 8 by design #7072 decision 1, to be "added in the follow-up as field additions").
- Map SF2 generators **13 (`modLfoToVolume`, centibels)** and **10 (`modLfoToFilterFc`, cents)** in `Sf2RegionResolver`, with spec defaults and sane clamps.
- Correct the `Sf2GeneratorType` gen-10 XML doc comment (it wrongly says "(centibels)"; `modLfoToFilterFc` is **cents**).
- Regression + proof tests (see §11).

**Out of scope (do NOT bundle — explicit):**
- The separate SF2 **vibrato LFO** (`vibLfoToPitch`, gens 6/23/24). Its gen-6 XML doc comment is also wrong ("centibels" → should be "cents") but is left untouched here since the generator is not consumed.
- Generator-loop DRY refactor.
- Any new filter *type*; effects; real-time NAudio adapter; sine waveform; `MathF`/SIMD hot-path polyfill.

## 3. Assumptions & Constraints

| # | Assumption / Constraint | Confidence |
|---|---|---|
| A1 | The mod-LFO produces one bipolar value per control tick; **all three routings read the same value from the same tick** (one `lfo.Advance(ControlRateFrames)` call per tick). | High |
| A2 | `BiquadLowPassFilter` uses **Transposed Direct Form II (TDF2)** — verified against `Process` (`output = b0·x + state1; state1 = b1·x − a1·y + state2; state2 = b2·x − a2·y`). TDF2 is the structure recommended for time-varying biquads; its state variables stay well-behaved when coefficients change, which is what makes control-rate coefficient stepping click-free without per-sample coefficient interpolation. | High |
| A3 | SF2 2.04 §8.1.2 units: gen 5 `modLfoToPitch` = cents (already corrected PR 8); gen 10 `modLfoToFilterFc` = **cents**; gen 13 `modLfoToVolume` = **centibels**. | High |
| A4 | The control rate (690 Hz at 44.1 kHz) is ≫ the maximum LFO rate (clamped to 20 Hz), so the per-tick change in cutoff and in the tremolo multiplier is a small fraction of full excursion. This margin is what keeps the filter's control-rate coefficient step sub-audible. | High |
| A5 | `Synthesizer.Finalize` remains the sole NaN/Inf choke point (untouched); the new routings are self-clamping and never need it, but it is the backstop. | High |
| C1 | `ControlRateFrames = 64` stays a `const` in the voice, shared by all three routings (fixed by PR 8; not re-litigated). | — |
| C2 | Two target framework moniker builds, **0 warnings**; `dotnet test` green; both required before any PR. | — |

## 4. Architectural Overview

The feature is **purely additive**, riding the same three surfaces PR 8 touched (region descriptor → resolver → voice), plus one contained new capability on `BiquadLowPassFilter` (the coefficient-recompute path). No new type, no new layer.

```
 SF2 zone generators                LfoParameters (immutable descriptor, on SampleRegion)
 ┌───────────────────────┐          ┌───────────────────────────────────────────────┐
 │ 21 DelayModLFO         │          │ DelaySeconds                                   │
 │ 22 FreqModLFO          │  build   │ FrequencyHz                                    │
 │  5 ModLFO→Pitch (cent) │ ───────► │ PitchDepthCents      (PR 8)                    │
 │ 13 ModLFO→Vol  (cB)    │          │ VolumeDepthCentibels (NEW — tremolo)          │
 │ 10 ModLFO→FiltFc(cent) │          │ FilterDepthCents     (NEW — filter-sweep)     │
 └───────────────────────┘          └───────────────────────────────────────────────┘
        Sf2RegionResolver.BuildLfoParameters                     │ rides on SampleRegion
                                                                 ▼
 SamplePlaybackVoice — control tick every ControlRateFrames (64):
                                                                 
   lfoValue = lfo.Advance(64)                    ◄── ONE value, THREE consumers
        │
        ├─ vibrato  : effectiveIncrement = base · 2^(lfoValue·PitchDepthCents/1200)   [PR 8, hold-then-step]
        ├─ tremolo  : tremoloTarget      = 10^(lfoValue·VolumeDepthCentibels/200)     [NEW, per-sample GLIDE]
        │             tremoloStep = (tremoloTarget − tremoloCurrent) / 64
        └─ sweep    : effectiveCutoff    = baseCutoff · 2^(lfoValue·FilterDepthCents/1200)  [NEW, control-rate step]
                      filter.SetCutoff(effectiveCutoff)      ◄── NEW recompute path on the biquad

   per sample:  tremoloCurrent += tremoloStep
                gain   = envelope.AdvanceFrame() · gainRamp.AdvanceFrame() · tremoloCurrent
                sample = filter.Process(sample)          (coefficients held between control ticks)
                out    = sample · gain
```

**The central design idea — a three-way anti-zipper spectrum.** The mod-LFO value only changes at the control rate (every 64 frames). How a routing may consume that stepped value depends entirely on *what the value modulates*:

| Routing | Modulates | Effect of a control-rate step | Consumption strategy |
|---|---|---|---|
| **Vibrato** (PR 8) | the read-position **increment** (a slope) | Read position stays continuous; only its derivative steps → **zero discontinuity**, INV-1 by construction. | **Hold-then-step** — no smoothing. |
| **Filter-sweep** (this PR) | the biquad **coefficients** (filter state parameters) | A small transient only; TDF2 + control-rate margin (A4) keep it **sub-audible**. | **Hold-then-step at the control rate** — recompute coefficients each tick, hold between. No per-sample coefficient interpolation. |
| **Tremolo** (this PR) | the **gain scalar** (multiplies the sample directly) | A stepped multiplier **is** an amplitude discontinuity → **directly audible click**. | **Per-sample linear glide** — interpolate the multiplier across the 64-frame block. |

This spectrum is exactly why #7072 grouped tremolo and filter-sweep as "the anti-zipper follow-up" and split them from vibrato: vibrato needs no machinery; these two each need a make-a-value-move-without-clicking mechanism, and they are different mechanisms for principled reasons.

## 5. Components & Responsibilities

### 5.1 `LfoParameters` (existing immutable `readonly struct` — gains two fields)
- **Owns:** the rate-independent, SF2-unit-free description of the mod-LFO, now including all three routed depths.
- **Change:** add `VolumeDepthCentibels` (float) and `FilterDepthCents` (float), siblings of `PitchDepthCents`. The constructor grows from three to five parameters. `Default` becomes all-depths-zero (still inert ⇒ exact passthrough).
- **Does NOT own:** routing, rate conversion, or the LFO's running state.

### 5.2 `ModulationLfo` (existing mutable `struct` — bypass condition widens)
- **Owns:** producing the delayed bipolar triangle; the **bypass optimization**.
- **Change (load-bearing bug-avoidance):** the bypass condition becomes **inert iff ALL three depths are zero** — `PitchDepthCents == 0 && VolumeDepthCentibels == 0 && FilterDepthCents == 0`. Today it is pitch-only; leaving it pitch-only would make a **tremolo-only or sweep-only preset** (pitch depth 0, another depth nonzero) return a constant 0 → the routing silently does nothing. This is the single most important correctness change in the feature.
- **Does NOT own:** any knowledge of *which* destinations are routed beyond the aggregate inert check; producing more than one signal.

### 5.3 `BiquadLowPassFilter` (existing mutable `struct` — gains a recompute path) — **the meatiest change**
- **Owns:** the RBJ low-pass biquad section and its running state; now also the ability to **recompute its coefficients in place for a new cutoff, preserving filter state**.
- **Change:** introduce a `SetCutoff(float cutoffHz)` method that re-runs the existing RBJ coefficient kernel for the new cutoff and overwrites `b0…a2`, **leaving `state1`/`state2` untouched** (resetting state would itself click). To support this the struct must:
  - Retain the data needed to recompute: store `sampleRate` and the clamped resonance `q` as `readonly` fields (resonance does not sweep — only cutoff does).
  - Drop `readonly` from `b0…b2, a1, a2` and from `bypass` (they now change over the note's life). `state1`/`state2` are already mutable.
  - Reuse the **existing** `MinCutoffHz` / `MaxCutoffFractionOfSampleRate` clamps and the `Clamp` helper — the swept cutoff is clamped exactly as the constructor clamps, so coefficients stay finite (INV-2 preserved with zero new guard code).
  - `SetCutoff` sets `bypass = false`: an actively-swept filter is by definition engaged (see §9 for the base-open edge case).
- **Refactor discipline:** the RBJ kernel currently lives inline in the constructor. It must be shared between the constructor and `SetCutoff` (a private `Recompute(float cutoffHz)` that both call). Block-level DRY math: the RBJ block is ~12 lines × 2 sites = ~24, **above** the ~15-20 threshold ⇒ **extraction is correct** (Design Contracts §1 DRY, #1267). The helper names cleanly (`Recompute`), so the extraction earns its keep.
- **Does NOT own:** the LFO, the effective-cutoff math (`baseCutoff · 2^(…)` is the voice's job), or any per-sample coefficient interpolation (explicitly rejected — see §10).

### 5.4 `SamplePlaybackVoice` (existing — control tick gains two routings + per-sample tremolo glide)
- **Owns:** the per-voice control tick and the gain path. At each control tick it now derives three things from the one `lfoValue`: the effective increment (vibrato, unchanged), the tremolo target multiplier + its per-sample step, and — when filter depth is nonzero — the effective cutoff, pushed via `filter.SetCutoff`.
- **New state:** `tremoloCurrent` (init `1.0f`), `tremoloStep` (init `0f`), and the base cutoff to sweep around (read from `region.Filter.CutoffHz`).
- **Gain path change:** `gain = envelope.AdvanceFrame() · gainRamp.AdvanceFrame() · tremoloCurrent`, with `tremoloCurrent += tremoloStep` advanced once per sample (the glide).
- **Does NOT own:** coefficient math (delegated to the biquad), unit conversion (done in the resolver).

### 5.5 `Sf2RegionResolver` (existing — `BuildLfoParameters` gains two generators)
- **Owns:** SF2 interpretation. `BuildLfoParameters` gains gen 13 → `VolumeDepthCentibels` and gen 10 → `FilterDepthCents`, each with default 0 and a magnitude clamp.
- **Does NOT own:** anything beyond mapping raw generator amounts to descriptor fields.

## 6. Interactions & Data Flow

**Build path (per resolved note, rate-independent):** `Sf2RegionResolver.BuildLfoParameters` reads gens 21/22/5/13/10 from the instrument zone (local-then-global), converts units away, clamps, and constructs the five-field `LfoParameters`, which rides on the `SampleRegion`.

**Voice construction (rate-dependent):** the voice builds `ModulationLfo` from `region.Lfo` and the output sample rate (unchanged call site); reads `region.Filter.CutoffHz` as the sweep base; initializes `tremoloCurrent = 1f`, `tremoloStep = 0f`.

**Render — control tick (every 64 frames):**
1. `lfoValue = lfo.Advance(ControlRateFrames)` — one value.
2. Vibrato: `effectiveIncrement = pitchIncrement · 2^(lfoValue · PitchDepthCents / 1200)` (unchanged).
3. Tremolo: `target = 10^(lfoValue · VolumeDepthCentibels / 200)`; `tremoloStep = (target − tremoloCurrent) / ControlRateFrames`. (Skip when `VolumeDepthCentibels == 0`: leave `tremoloStep = 0`, `tremoloCurrent = 1f`.)
4. Filter-sweep (only when `FilterDepthCents != 0`): `effectiveCutoff = baseCutoff · 2^(lfoValue · FilterDepthCents / 1200)`; `filter.SetCutoff(effectiveCutoff)`.

**Render — per sample:** `tremoloCurrent += tremoloStep`; `gain = envelope · gainRamp · tremoloCurrent`; `sample = filter.Process(sample)` (coefficients constant between ticks); `out = sample · gain`.

**Onset:** at the first tick, `lfoValue == 0` (inside the LFO delay), so `target = 10^0 = 1` and `effectiveCutoff = baseCutoff · 2^0 = baseCutoff`. With `tremoloCurrent` initialized to `1f`, the first `tremoloStep` is 0 — the tremolo starts from unity with no jump. The sweep's first `SetCutoff(baseCutoff)` reproduces the base timbre. Both onsets are click-free by construction.

## 7. Data Model (Conceptual)

No new entities. `LfoParameters` gains two scalar fields (both `float`, both in their native rate-independent unit: centibels for volume depth, cents for filter depth). `SampleRegion` is **unchanged** — the `Lfo` field already carries `LfoParameters`; only that struct's internal shape grows. This mirrors how PR 8 deferred these exact two depths as "field additions."

## 8. Contracts & Interfaces (Abstract)

| Interface | Input | Output / Effect | Invariants |
|---|---|---|---|
| `LfoParameters(delay, freq, pitchDepth, volumeDepth, filterDepth)` | five rate-independent scalars | immutable descriptor | `Default` = all depths 0 ⇒ inert ⇒ passthrough at any rate. |
| `ModulationLfo` bypass | `LfoParameters` | inert (constant 0) iff all three depths are 0 | Widened from pitch-only. Inert ⇒ every routing passes through unchanged ⇒ bit-identical. |
| `BiquadLowPassFilter.SetCutoff(cutoffHz)` | a new cutoff in Hz | recomputes `b0…a2` in place from the retained `sampleRate` + `q`; **preserves `state1`/`state2`**; sets `bypass = false` | Cutoff clamped by the existing `MinCutoffHz`/Nyquist-fraction clamps ⇒ coefficients finite (INV-2). State preserved ⇒ no reset click. Idempotent for a repeated cutoff. |
| Voice tremolo glide | `lfoValue`, `VolumeDepthCentibels` | per-sample `tremoloCurrent` linearly interpolated toward `10^(lfoValue·cB/200)` across the control block | `tremoloCurrent` starts at 1; bounded exponent ⇒ finite positive multiplier (INV-2). No intra-block step ⇒ INV-1. |
| Voice no-regression | all depths 0 | pitch increment = base; `tremoloCurrent ≡ 1`; `SetCutoff` never called | Render is **bit-identical** to the PR-8 baseline. |

## 9. Cross-Cutting Concerns

- **INV-1 (no zipper):** the three-way spectrum (§4). Vibrato: mathematically clean. Filter: control-rate step on a TDF2 structure with a ≫20× rate margin (A4) — sub-audible, verified by the existing consecutive-sample-delta regression pattern extended to a swept filter. Tremolo: per-sample glide removes the step entirely.
- **INV-2 (NaN/Inf-safe):** tremolo multiplier is `10^(bounded)` ⇒ finite positive. Swept cutoff is clamped by the biquad's existing clamps ⇒ finite coefficients. `Synthesizer.Finalize` remains the untouched backstop.
- **Bit-identical no-regression:** guaranteed structurally — all-depths-zero makes the LFO inert (constant 0), the tremolo multiplier a constant `1.0f` (IEEE-754 `x·1.0f == x`), and `SetCutoff` is never called so the biquad is byte-for-byte its PR-8 self.
- **Concurrency / performance:** per-sample hot path stays multiply-add — the two transcendentals (`10^…` for tremolo, `2^…` for sweep) run **once per control tick**, alongside the existing vibrato `2^…`. No new per-sample transcendental ⇒ the `MathF`/SIMD deferral from PR 8 still holds (YAGNI).
- **Base-open filter + sweep edge case:** if the base cutoff is the SF2 open sentinel (default, ≈19913 Hz → `bypass=true` at construction) *and* `FilterDepthCents != 0`, the first `SetCutoff` clears bypass and builds an active near-passthrough filter clamped just below Nyquist. A large negative depth sweeps it audibly downward (bottoming at `MinCutoffHz = 20`); a small depth is inaudible. This is spec-consistent and requires no special-casing — the clamp does the work.

## 10. Quality Attributes & Trade-offs

- **Filter-sweep: control-rate coefficient step vs. per-sample coefficient interpolation.** *Chosen: control-rate step (recompute each tick, hold between).* Alternative (interpolate the 5 coefficients per sample) rejected on merit: it adds 5 per-sample multiply-adds to the hot path **and** carries a real stability hazard — a linear interpolation between two individually-stable coefficient sets can pass through an *unstable* set (interpolated poles can leave the unit circle), risking a blow-up that would then lean on the INV-2 backstop. TDF2 (A2) plus the ≫20× control-rate margin (A4) already keep the stepped transient sub-audible, so the interpolation buys a benefit TDF2 provides for free at a cost of complexity + risk. This matches the vibrato precedent (control-rate stepping) and keeps KISS.
- **Tremolo: per-sample glide vs. reuse `GainRamp`.** *Chosen: a 2-field inline glide in the voice.* Alternative (retarget the existing `GainRamp` each tick) rejected: `GainRamp` is a **slew-rate limiter** with a fixed 5 ms time constant, independent of the control rate and the LFO rate. Feeding it the tremolo target would round the triangle peaks and attenuate tremolo depth unpredictably at higher LFO rates — it distorts the modulation shape. The inline linear glide across the control block reconstructs the tremolo envelope exactly (piecewise-linear at 690 Hz, far above the ≤20 Hz LFO) with no depth loss. It is two floats and one per-sample add — below the bar for a new type (Design Contracts §4 less-is-better: cannot be usefully merged or extracted; only tremolo needs it).
- **Why not a shared "control-block interpolator" abstraction for both new routings?** Only tremolo interpolates; the filter steps. A shared abstraction would have exactly one user ⇒ indirection, not abstraction (§4). Inline.
- **Maintainability / reusability:** `SetCutoff` is a genuinely reusable capability on the biquad (any future cutoff modulator — envelope-to-filter, key-tracking — uses it), so extracting the RBJ kernel is a real structural win, not speculation. It is justified *now* by the sweep, not "might need later."

## 11. Risks & Mitigations

| Risk | Likelihood | Mitigation |
|---|---|---|
| Tremolo-only / sweep-only preset silently silent (LFO bypass still pitch-only). | High if missed | §5.2 widens bypass to all-depths-zero. Explicit unit test: a `PitchDepthCents==0, VolumeDepthCentibels!=0` LFO yields nonzero values. |
| Filter coefficient step audibly clicks despite TDF2. | Low | Extend the #6272 §B consecutive-sample-delta regression to a swept filter; if it trips, the fallback is cutoff-parameter smoothing (glide the effective cutoff across the block before recompute) — NOT coefficient interpolation. Documented as the escalation, not built pre-emptively (YAGNI). |
| `SetCutoff` resets filter state → click. | Low | Contract §8 mandates state preservation; unit test asserts `state1`/`state2` continuity across a `SetCutoff` call (feed a constant, assert no output jump). |
| Non-bit-identical no-regression from an unguarded `·tremoloCurrent`. | Low | `tremoloCurrent ≡ 1f` when volume depth is 0, and `x·1.0f == x` in IEEE-754; the bit-identical render test (all depths 0) is the gate. |
| Swept exponent overflow. | Very low | Cents/centibels magnitude clamps in the resolver bound the exponent; cutoff further clamped by the biquad. |
| Wrong SF2 units (gen 10 vs 13). | Low | A3 pins them; gen-10 doc-comment correction is in scope; the resolver test asserts a known gen-10 amount maps to the expected Hz swing and a known gen-13 amount to the expected dB swing. |

## 12. Migration / Rollout Strategy

Single atomic PR (Design Contracts / #1165 — no deprecation window, private repo). The only signature migration is `LfoParameters`' constructor growing by two parameters; every call site (`LfoParameters.Default`, `Sf2RegionResolver.BuildLfoParameters`, `VibratoOverridePatch`, and any test constructing `LfoParameters`) passes the two new depths — `0f, 0f` at inert sites, which keeps them bit-identical. No schema, no data, no config.

## 13. Open Questions (non-blocking)

1. **Depth clamp caps.** Suggest `MaxLfoVolumeDepthCentibels` (e.g. ±960 cB, mirroring `MaxFilterResonanceCentibels`) and `MaxLfoFilterDepthCents` (e.g. ±12000 cents). Both are `const` (Design Contracts §3 — magic numbers stay magic; no knobs). Exact values are a musical judgment; the biquad clamp is the real INV-2 safety for the filter, and `Synthesizer.Finalize` for volume. Confirm no in-corpus preset exceeds the chosen caps (mirrors PR 8 open question 3).
2. **Demo A/B.** Extend `RenderDemo` with tremolo-depth and filter-sweep-depth CLI args, and either extend `VibratoOverridePatch` to override all three depths or add a sibling override patch (demo-only, mirrors PR 8 open question 1). Recommendation: generalize `VibratoOverridePatch` into a single mod-LFO override carrying all three depths, since it already reconstructs the region.
3. **Filter-sweep proof metric.** "Periodic HF-energy variation" is best measured as a periodic change in zero-crossing rate or short-window RMS of a high-passed copy on a filtered preset; pin the exact metric in the test (mirrors PR 8's zero-crossing wobble approach).

## 14. Implementation Guidance for the Next Agent

Build in this order; each step compiles and keeps existing tests green.

1. **`LfoParameters`** — add `VolumeDepthCentibels`, `FilterDepthCents`; grow the constructor; update `Default` to pass `0f, 0f`. Fix all call sites (bit-identical at inert sites).
2. **`ModulationLfo`** — widen the bypass condition to all-three-depths-zero. Add the unit test that a volume-only LFO produces nonzero values.
3. **`BiquadLowPassFilter`** — extract the RBJ kernel into a private `Recompute(cutoffHz)`; store `sampleRate` + clamped `q` as `readonly`; drop `readonly` from `b0…a2`/`bypass`; add public `SetCutoff(cutoffHz)` that clamps, calls `Recompute`, and sets `bypass=false`, **preserving state**. Add the state-continuity unit test.
4. **`Sf2RegionResolver.BuildLfoParameters`** — map gen 13 → volume depth (centibels, clamp), gen 10 → filter depth (cents, clamp); correct the gen-10 XML doc comment ("centibels" → "cents"). Add resolver unit tests for both mappings + units.
5. **`SamplePlaybackVoice`** — add `tremoloCurrent`/`tremoloStep` and the base cutoff; extend the control tick (tremolo target + step; conditional `SetCutoff`); multiply `tremoloCurrent` into the gain; advance the glide per sample.
6. **Tests** — tremolo tracking (WAV envelope wobbles at the LFO rate), filter-sweep tracking (periodic HF-energy variation on a filtered preset), zipper regression extended to both routings, and the **bit-identical no-regression** (all depths 0 vs PR-8 baseline).
7. **Demo + proof** — extend `RenderDemo`; render the two proof WAVs; verify both TFMs 0 warnings + `dotnet test` green.

## Pre-Design Checklist (Design Contracts #1136 §5): PASS

**KISS/DRY/YAGNI** — No new type (two field additions + one method on an existing struct + inline voice state). No single-implementation abstraction (rejected the shared "control-block interpolator" — one user). No "might need later" (depths were the concrete deferral from PR 8, not speculation; `SetCutoff` is justified by the sweep *now*). No deprecation/flag/shim. **DRY math stated in numbers:** RBJ kernel `~12 lines × 2 sites ≈ 24 > threshold ⇒ extract` (`Recompute`); the `2^(lfoValue·cents/1200)` sub-expression `1 line × 2 sites (vibrato + sweep) = 2 < threshold ⇒ inline`; tremolo's `10^(lfoValue·cB/200)` is distinct from the resolver's one-directional clamped `CentibelsToLinear` (different location, form, and direction) ⇒ not a shared kernel.

**Existing systems first** — Rides region + resolver + voice; the one new capability (`SetCutoff`) lives on the existing `BiquadLowPassFilter`, not a new layer. No new persisted data. Field additions justified (the two deferred rate-independent depths, merged onto the existing descriptor, not threaded through signatures — Design Contracts §4).

**Configurability** — `ControlRateFrames`, the two depth-clamp caps, and all reference constants stay `const`/`static readonly` (§3). No knobs, no audit columns, no telemetry-then-tune compound.

**Less is better** — Every element passes delete/merge/inline: the tremolo glide can't merge into `GainRamp` (distorts the shape — trade-off named §10) and can't extract (one user); `SetCutoff` earns its keep (reusable + justified now). Every trade-off named with its rejected alternative and reason (control-rate-step vs coefficient-interp; glide vs GainRamp; inline vs shared interpolator). Inert LFO is a bypass (bit-identical), not a tolerance.

**Document discipline** — Cites Code Contracts #114 + Design Contracts #1136 as load-bearing. Scope and out-of-scope explicit. SF2 units verified vs 2.04; gen-10 doc-comment correction in scope, gen-6 explicitly deferred. New capability on an existing component (no superseded predecessor to banner — this design *extends* #7072, which stays live as the vibrato record).
