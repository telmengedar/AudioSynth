> **Repo path of record:** `docs/architecture/mod-lfo.md` (branch `feature/mod-lfo`, from `origin/main` @ `d6eebc2`).
> **DiVoid mirror:** documentation node (graph-discoverable copy). **Task:** #7071 · **Project:** #6128 · **Map root:** #6708.
> **Precedents (same pattern):** envelope design #7063 / map #7065 / task #7062 (PR 6, merged `0945940`); filter design #7067 / map #7070 / task #7066 (PR 7, merged `d6eebc2`).
> **Load-bearing contracts:** Code Contracts #114 §0/§4/§5.5; Design Contracts #1136 §1–§4 + §5 checklist.

# Design — Per-Voice Modulation LFO: Vibrato (with Tremolo / Filter-Sweep deferred)

## Problem

Through PR 7 every voice is shaped by pitch, a static DAHDSR volume envelope (PR 6), and a static resonant low-pass filter (PR 7) — but all of it is **frozen for the note's duration**. There is no *movement*. SF2 presets carry a **modulation LFO** (a low-frequency oscillator) that, routed to pitch, produces **vibrato**; routed to volume, **tremolo**; routed to filter cutoff, a **sweep**. Presets that specify a mod-LFO render flat and wrong without it. This milestone adds the per-voice mod-LFO oscillator and routes it to **pitch (vibrato)** — the audible headline, present on any note — while deferring the two routings that require new anti-zipper machinery to an immediate follow-up.

## Scope decision (the architect's cut) — and why

**This PR ships: the mod-LFO oscillator (triangle, delay + frequency) + pitch routing (vibrato).**
**Immediate follow-up PR ships: volume routing (tremolo) + filter-cutoff routing (sweep).**

The cut is not "vibrato because it's smallest." It falls on a **principled fault line**:

- **Vibrato routes the LFO to a parameter the voice already varies continuously** — the read-position advance (pitch increment). Stepping the increment at a control rate changes the *slope* of a continuous read position; it introduces **no amplitude discontinuity** and therefore **no zipper** (INV-1 holds by construction). Vibrato needs **zero new smoothing machinery**.
- **Tremolo and filter-sweep route the LFO to parameters that are currently static-per-note** — the gain scalar (a stepped gain multiplier *is* an amplitude discontinuity → zipper) and the biquad coefficients (a stepped coefficient set → filter transient). Each needs a *make-a-static-parameter-smoothly-time-varying* mechanism: per-sample gain smoothing for tremolo, and a coefficient-recompute path on `BiquadLowPassFilter` (today its coefficients are `readonly`, computed once in the ctor) for the sweep. Those two share one problem and belong in one follow-up PR.

Bundling all three would (a) put the just-merged `BiquadLowPassFilter` under surgery in the same PR that introduces the LFO, and (b) mix the zero-risk routing with the two INV-1-risk routings. Splitting on the smoothing boundary yields two clean, independently-reviewable, independently-valuable PRs. This is exactly the brief's named fallback, chosen on merit. **This PR touches no other DSP type** — it is purely additive in the region, the resolver, and the voice.

## Key decisions (mirror the envelope #7063 and filter #7067 precedents)

1. **`LfoParameters` (new immutable `readonly struct`, mirrors `EnvelopeParameters` / `FilterParameters`)** — rate-independent, SF2-unit-free: `DelaySeconds`, `FrequencyHz`, `PitchDepthCents` (peak pitch deviation at full-scale LFO excursion). `Default` = inert (pitch depth `0` ⇒ the LFO contributes nothing ⇒ exact passthrough, mirroring `FilterParameters.Default`). Rides on `SampleRegion` as **one new field**, sibling of `Envelope` and `Filter` (Design Contracts §4 — do **not** thread a new sibling through `TryResolve` / `SamplePatch` / voice signatures). Only the routing this PR consumes is carried; `modLfoToFilterFc` / `modLfoToVolume` depths are **not** fields yet (YAGNI — they arrive as field additions in the follow-up, exactly as `Envelope` arrived in PR 6 and `Filter` in PR 7).

2. **`ModulationLfo` (new mutable `struct` advanced in place, mirrors `AmplitudeEnvelope` / `GainRamp` / `BiquadLowPassFilter`)** — single responsibility: **produce a delayed, periodic, bipolar `[-1, 1]` control signal at the configured rate.** Ctor `(LfoParameters, sampleRate)` precomputes the per-frame phase increment (rate-dependent), the delay frame count, and a **bypass** flag (set when `PitchDepthCents == 0` — the only routing in scope). It does **not** know about pitch, gain, or filtering — routing is the caller's concern. This keeps the required test ("LFO is a periodic bipolar signal at the configured rate") targeted at the LFO itself, independent of any routing.
   - **Waveform: triangle** (bipolar, starts at `0` rising). Chosen over sine because it is the SF2 LFO waveform, it needs **no transcendental to evaluate** (only adds/compares — consistent with the envelope's linear-step philosophy), and starting at `0` gives a **smooth vibrato onset** (zero initial deviation). Decisive: triangle.
   - **Bounded phase (regression against defect catalog #6272 §B "unbounded phase accumulation" #6214):** the phase is a `double` accumulator **wrapped to `[0, 1)` at every advance**, never an unbounded running sum. No precision loss / detune on long high notes.

3. **Control-rate modulation — the transcendental stays out of the per-sample hot path.** Both prior designs explicitly preserved "no per-sample transcendental" so the MathF/SIMD polyfill defers. Vibrato's pitch factor is `2^(lfo · PitchDepthCents / 1200)` — a `Math.Pow` per evaluation. To keep it out of the sample loop, the voice re-evaluates the LFO and recomputes the effective pitch increment **once every `ControlRateFrames` frames** (a `const`, e.g. 64 → ~1.45 ms / ~690 Hz control rate at 44.1 kHz, far above any musical LFO rate — no aliasing of the LFO). Between control ticks the effective increment is **held constant**. Because a held-then-stepped pitch increment is exactly what the voice does today (it uses one constant increment for the whole note), stepping it every 64 frames is **benign — the read position stays continuous, only its slope steps in tiny increments**. Per-sample hot path remains multiply-add only ⇒ **INV-1 preserved by construction; MathF/SIMD polyfill still defers (explicit YAGNI).**
   - `ControlRateFrames` is a **magic number that stays magic** (`const`, Design Contracts §3): no operator tunes it, it does not vary by environment. The same tick will drive the follow-up's filter-coefficient recompute — so it is defined once here and reused, not re-introduced.

4. **Rate-independent descriptor / rate-dependent state** (same split as envelope seconds-on-region / frames-in-voice and filter Hz-on-region / coeffs-in-voice). `LfoParameters` carries seconds + Hz + cents and is **shareable and rate-agnostic**; the voice builds the `ModulationLfo` with `outputSampleRate`, which fixes the per-frame phase increment and the delay frame count. Phase increment and delay are **not** precomputed in the resolver (it has no output rate).

5. **Voice integration (`SamplePlaybackVoice`).** Add a `ModulationLfo` field (built in the ctor from `region.Lfo`, like `envelope` / `filter`), keep the existing `readonly` base pitch increment, and add a mutable **effective** pitch increment plus a control-tick countdown. In `RenderBlock`, at each control tick: advance the LFO by the elapsed frames (wrapped phase), read its bipolar value, and set `effectiveIncrement = basePitchIncrement · 2^(lfoValue · PitchDepthCents / 1200)`. `AdvanceReadPosition` uses `effectiveIncrement` instead of the constant. The flat per-frame loop is preserved (one countdown decrement per frame; recompute only on tick). An **initial tick at frame 0** establishes the increment before the first sample (with a non-zero delay the LFO value is `0` there ⇒ base increment ⇒ smooth onset).

6. **SF2 mapping in `Sf2RegionResolver`** (the single home for SF2 interpretation). New `BuildLfoParameters(zone, globalZone)` reads, local-then-global zone with spec defaults:
   - **gen 21 `DelayModulationLFO`** — timecents → seconds via the existing `TimecentsToSeconds` (`2^(tc/1200)`, already clamped to `MaxEnvelopeSeconds`); default **−12000** (≈0.977 ms, negligible).
   - **gen 22 `FrequencyModulationLFO`** — **absolute cents** → Hz as `8.176 · 2^(cents/1200)`; default **0** ⇒ 8.176 Hz. This reuses the resolver's existing `FilterCutoffReferenceHz = 8.176` constant. Clamp to a sane `[min, max]` Hz band for stability.
   - **gen 5 `ModulationLFOToPitch`** — **cents** (peak deviation at full LFO scale); default **0** ⇒ inert. Clamp magnitude to a stable cap (e.g. ±1 octave = ±1200 cents).
   - Instrument-zone sourced (preset-level generator addition is not v1, matching every other generator).
   - **Verified against SF2 2.04 §8.1.2/§8.1.3.** Note: the `Sf2GeneratorType` XML doc for **gen 5 says "(centibels)" — this is wrong; `modLfoToPitch` is in cents.** Correct that one doc comment when consuming the generator (in-scope; it is the generator this PR reads). Gens 10/13 doc comments (also "centibels" for gen 10, which is cents) are left for the follow-up that consumes them.

7. **DRY check on the abs-cents→Hz conversion (Design Contracts §1, math required).** The pure kernel `8.176 · 2^(cents/1200)` now has two call sites: filter cutoff (`FilterCutoffCentsToHz`, gen 8) and LFO frequency (gen 22). They are **not identical** — `FilterCutoffCentsToHz` embeds the open-cutoff sentinel + a filter-specific min clamp; the LFO frequency needs its own min/max-Hz clamp and no open sentinel. The shared kernel is a **single expression**: `block_size × site_count = 1 × 2 = 2`, far below the ~15–20 extraction threshold ⇒ **inline at both sites is correct; no helper extraction.** (The `8.176` reference constant is already shared.)

8. **INV-2 support (not replacement).** LFO value ∈ `[-1, 1]` (bounded triangle); `PitchDepthCents` clamped at build; `FrequencyHz` clamped ⇒ `2^(bounded)` is always finite and positive ⇒ effective increment finite. The LFO/vibrato path never *manufactures* NaN/Inf; `Synthesizer.Finalize` remains the **single INV-2 choke point (untouched)**.

## Scope

**In:** the `ModulationLfo` (triangle, delay + frequency, bounded phase, bypass-when-inert) + `LfoParameters` on the region + SF2 mapping of gens 21/22/5 + control-rate pitch modulation in the voice + regression/proof tests, **ONE PR**.
**Out (immediate follow-up PR — needs the smoothing machinery):** volume routing (tremolo, needs per-sample gain smoothing) + filter-cutoff routing (sweep, needs a coefficient-recompute path on `BiquadLowPassFilter` + gens 10/13 mapping). **Out (later / separate):** the distinct SF2 *vibrato* LFO (`vibLfoToPitch`, gen 6) and its delay/freq (gens 23/24); the generator-loop DRY refactor; sine waveform option; effects; real-time NAudio adapter; MathF/SIMD fast path (not needed).

## Contracts (abstract)

| Unit | Contract |
|---|---|
| `LfoParameters(delaySeconds, frequencyHz, pitchDepthCents)` | Immutable, rate-independent, SF2-unit-free. `Default` = inert (pitch depth `0`) ⇒ building an LFO from it yields exact passthrough at any rate. |
| `ModulationLfo(LfoParameters, sampleRate)` | Produces a bipolar `[-1, 1]` triangle at `FrequencyHz`, preceded by `DelaySeconds` of `0` output. Phase is bounded (wrapped `[0,1)`), advanced by a caller-supplied frame count; value read after advance. Inert params ⇒ bypass ⇒ constant `0` value. Never emits NaN/Inf for finite construction. Block size never an input. |
| Voice invariant | `effectiveIncrement` is recomputed only at control ticks (`= basePitchIncrement · 2^(lfoValue · pitchDepthCents / 1200)`) and held constant between them; `readPos += effectiveIncrement` per frame. Pitch-depth `0` ⇒ `effectiveIncrement == basePitchIncrement` every frame ⇒ **bit-identical to the pre-LFO render**. No amplitude discontinuity at a control tick (INV-1). |

## Regression basis (defect catalog #6272)

- **§B unbounded phase accumulation (#6214):** test that a long-note render keeps the LFO phase bounded and the vibrato period stable end-to-end (no drift/detune) — encoded by asserting periodicity over a multi-second render.
- **§B clicks/zipper class:** test that a control tick introduces **no amplitude discontinuity** — bounded consecutive-sample deltas across tick boundaries through the full voice path (vibrato must not click).
- **No-regression (mirrors the filter's open-bypass guarantee):** a region with `LfoParameters.Default` (or any zero pitch depth) renders **bit-identical** to the PR-7 baseline (Florestan note 60).

## Verification target

Build both TFMs (netstandard2.0 + net8.0), **0 warnings**; `dotnet test` green (all prior + new). New tests:
- **LFO unit:** `ModulationLfo` yields a periodic bipolar signal at the configured rate; is `0` throughout the delay; is constant `0` when inert.
- **Vibrato tracking:** effective pitch increment tracks the LFO value at the configured depth/rate.
- **No-regression:** `LfoParameters.Default` ⇒ `Process`/render bit-identical to the PR-7 baseline; `EnvelopeDeclickTests` / `GainGlideTests` / `FilterVoiceFlowTests` unchanged green.
- **Full-path proof (brief deliverable):** render note 60 through `RenderDemo` with a mod-LFO→pitch mapping active and show a **measurable periodic pitch / zero-crossing wobble** (vibrato) in the WAV.
- **Migration:** `SampleRegion` gains one ctor param (`LfoParameters`), like PR 6's `Envelope` and PR 7's `Filter`. Sites: 1 production (`Sf2RegionResolver.BuildRegion`) + test/construction sites pass `LfoParameters.Default` (inert ⇒ bit-identical).

## Open questions (non-blocking)

1. **Audible A/B mechanism.** Recommend a **demo-only vibrato CLI arg** on `RenderDemo` (rate Hz + depth cents — a few lines, tool not library, mirroring the filter PR's demo-only cutoff arg) so the A/B works regardless of Florestan preset[0] content. The unit + full-path tests are the authoritative proof; the WAV is the audible confirmation.
2. **`ControlRateFrames` value.** 64 frames proposed (~690 Hz control rate at 44.1 kHz). Pin against the tick-boundary declick test; drop lower only if a tick step proves audible (it should not, given the tiny per-tick pitch delta).
3. **Pitch-depth clamp cap.** ±1200 cents proposed as the stability cap; confirm no in-corpus preset exceeds it meaningfully.

## Pre-Design Checklist (#1136 §5): PASS

**KISS / DRY / YAGNI**
- No new type whose value-space mirrors an existing type — `LfoParameters` (delay/freq/depth) and `ModulationLfo` (a control oscillator) are disjoint from the envelope (amplitude contour) and filter (timbre) concerns.
- No new abstraction with one implementation and no second — the LFO is a concrete DSP unit, not an interface; no routing-strategy abstraction is introduced for the single routing (would be speculative — the follow-up adds routings directly and extracts a helper only if the DRY math crosses threshold then).
- No element justified by "we might need X later" — filter/volume depths are **not** carried until the follow-up routes them.
- No deprecation period / feature flag / compatibility shim (private repo, atomic deploy).
- DRY math quoted: abs-cents→Hz kernel `1 × 2 = 2` sites, below threshold ⇒ inline (decision 7).

**Existing systems first**
- Audited: the LFO rides the existing `SampleRegion` + `Sf2RegionResolver` + `SamplePlaybackVoice`; no new service/table/layer. The control tick reuses (and defines-once-for) the follow-up's recompute cadence.
- New `SampleRegion` field justified concretely: rate-independent per-region descriptor with a distinct lifecycle from envelope/filter; merged onto the region rather than threaded (§4).
- No new persisted data (this is an in-memory DSP descriptor).

**Configurability**
- `ControlRateFrames`, the Hz/cents clamps, and `8.176` stay `const`/`static readonly` — SF2-spec or engine constants, not operator-tunable. No config knobs, no audit columns.

**Less is better**
- Every element passes delete/merge/inline: the LFO can't be deleted (it *is* the feature); depth math inlines at the voice tick; the abs-cents→Hz kernel inlines.
- Trade-offs named explicitly: triangle-vs-sine (decision 2), control-rate-vs-per-frame transcendental (decision 3), inline-vs-extract cents→Hz (decision 7), vibrato-solo-vs-all-three (scope decision).
- Radical-clean no-regression: inert LFO is a **bypass** (bit-identical), not a near-passthrough tolerance argument.

**Document discipline**
- Cites Code Contracts #114 and Design Contracts #1136 as load-bearing; out-of-scope items listed explicitly; SF2 units verified against the 2.04 spec with the gen-5 doc-comment correction called out; no superseded predecessor (this is a new component, precedents remain live as siblings).
