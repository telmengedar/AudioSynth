# Design — Per-Voice Resonant Low-Pass Biquad Filter

> **Repo path of record:** `docs/architecture/biquad-filter.md` (branch `feature/biquad-filter`, from `origin/main`).
> **DiVoid:** design node filed under project #6128 · **Task:** #7066 · **Map root:** #6708.
> **Precedent (mirror this):** amplitude-envelope design #7063, map node #7065 (`AmplitudeEnvelope`), task #7062, merged in PR 6 (`0945940`).
> **Load-bearing contracts:** Code Contracts #114 §0/§4/§5.5 (implementer-side); Design Contracts #1136 §1–§4 + the §5 Pre-Design Checklist (applied at the foot of this document).

---

## 1. Problem Statement

Through PR 6 every synth voice plays its interpolated sample stream shaped only by pitch and the DAHDSR **amplitude** envelope. There is **no tone shaping** — every note is exactly as bright as its source recording. SF2 patches specify a per-voice **initial low-pass cutoff** (generator 8) and **resonance** (generator 9); these are the classic subtractive-synthesis timbre controls that make "warm", "pad", "bass" and filtered presets sound as intended. Because the engine ignores them, those patches render uniformly bright and wrong.

**Goal:** give every voice a **static resonant low-pass biquad filter** whose coefficients are derived once at note start from the SF2 initial-cutoff and Q generators, applied to each interpolated sample **before** the amplitude/gain multiply (matching the SF2 signal chain oscillator → filter → amplifier).

**Success criteria:**
- A region with a low initial cutoff measurably attenuates high-frequency energy while passing low-frequency energy.
- A region with the SF2 **default/open** cutoff renders **bit-identical** to pre-PR output (zero regression on every existing voice/synth test and every "open" patch).
- The filter never *produces* NaN/Inf; coefficient computation is guarded and Q/cutoff are clamped to a stable range. INV-1 (zipper-free) and INV-2 (NaN/Inf choke point) are preserved.

---

## 2. Scope & Non-Scope

**In scope (this ONE PR):**
- A per-voice **static** resonant low-pass biquad DSP unit (RBJ / Audio-EQ-Cookbook low-pass), coefficients computed **once at note start**.
- A rate-independent **filter-parameter descriptor** riding on `SampleRegion` (one new field, exactly as `Envelope` rides today).
- SF2 mapping of generator 8 (initial filter cutoff, absolute cents) and generator 9 (initial filter Q, centibels) → cutoff Hz + resonance, with SF2-spec defaults, in `Sf2RegionResolver`.
- Application of the filter **pre-amplifier** inside `SamplePlaybackVoice.RenderBlock`.
- Spec-correct **open-filter ⇒ exact passthrough (bypass)** so untouched patches are bit-identical.
- Stability / NaN guards (cutoff clamped below Nyquist, Q clamped to a stable band, coefficients finite by construction).
- Regression + proof tests (see §11 and §14).

**Explicitly out of scope (separate later PRs — do NOT bundle):**
- **Filter-envelope** and **LFO** modulation of cutoff (dynamic per-frame cutoff sweeps) — those arrive with the modulation sources (SF2 generators 10, 11, and the modulation-envelope family).
- The generator-loop DRY refactor.
- Other filter types (high-pass, band-pass, shelving).
- Preset-level (as opposed to instrument-level) generator addition for the filter generators.
- A MathF/SIMD per-sample fast path — **not needed**: the per-sample hot path is multiply-add only; the only transcendental math (cents→Hz, coefficient trig) runs once at note start via `System.Math`, which is netstandard2.0-safe. Pulling in the polyfill here would be pure YAGNI (§9 confirms the deferral holds).
- Denormal flush-to-zero. A resonant biquad fed silence can drift into denormal state (a CPU-speed concern, never a correctness or NaN concern). No audible or numeric defect results, so it is deferred; noted in §11.

---

## 3. Assumptions & Constraints

| # | Assumption / Constraint | Confidence |
|---|---|---|
| A1 | The biquad runs at the **output** sample rate, on the post-interpolation sample stream (the values written to the render block). Cutoff-vs-Nyquist is judged against output `fs`. | High — confirmed by the render loop. |
| A2 | SF2 generator 8 is **absolute cents** referenced to 8.176 Hz: `Hz = 8.176 · 2^(cents/1200)`. Default 13500 cents ≈ 19912 Hz. Spec-useful range is 1500–13500 cents. | High — SF2 2.x spec §8.1.2. |
| A3 | SF2 generator 9 is **centibels** of resonance (peak gain above DC). Default 0 cB ⇒ no resonant peak (Butterworth Q ≈ 0.707). | High — SF2 2.x spec §8.1.3. |
| A4 | 13500 cents is the SF2 sentinel for "filter effectively open/disabled"; treating cutoff ≥ that value as a pure bypass is both spec-faithful and the only way to guarantee bit-identical passthrough at every sample rate. | High — standard SF2-engine behaviour (FluidSynth / TinySoundFont). |
| A5 | The filter is **static** for a note's lifetime — coefficients computed once at note start, never recomputed per frame. Dynamic cutoff is explicitly a later PR. | High — brief. |
| A6 | The mutable-struct-advanced-in-place idiom (`GainRamp`, `AmplitudeEnvelope`) is the house pattern for per-voice DSP state and is the right shape for the filter. | High — confirmed in code. |
| A7 | Filter generators are read from the **instrument** zone (local-then-global), matching how every other generator (including the whole volume envelope) is resolved in v1. | High — matches `Sf2RegionResolver`. |

---

## 4. Architectural Overview

Three cohesive additions, each a direct analogue of an existing envelope-side component. Nothing else in the resolver → patch → voice signature chain changes (Design Contracts §4 — ride params on the region, do not thread a new sibling).

```
  SF2 generators 8 (Fc cents) + 9 (Q cB)
              │  (Sf2RegionResolver.BuildFilterParameters — unit conversion, spec defaults, clamps)
              ▼
     FilterParameters  ── rides on ──►  SampleRegion  (immutable, shared, rate-agnostic)
     (CutoffHz, Resonance)                    │  new field, sibling of Envelope
                                              ▼
                              SamplePlaybackVoice ctor (has outputSampleRate)
                                              │  builds coefficients once, at note start
                                              ▼
                              BiquadLowPassFilter  (mutable struct, per-voice state)
                                              │
   ── per frame, inside RenderBlock ──────────┤
                                              ▼
  interpolated sample ─► filter.Process(x) ─► × (envelope × gainRamp) ─► block[i]
       (oscillator)         (FILTER)                 (amplifier)
```

**Signal-flow placement (brief-mandated, load-bearing):** the biquad processes the interpolated sample **before** the `gain = envelope × gainRamp` multiply. In the current loop that is precisely between `ReadInterpolated()`/the exhausted-substitution and the `block[i] = sample * gain` line. This realises the SF2 chain **oscillator → low-pass filter → amplifier(volume envelope)**. The order is not cosmetic: filtering after the gain multiply would let the amplitude contour (and any future CC gain) modulate the filter's input level and change its resonant behaviour — wrong per spec.

**Rate-independent descriptor, rate-dependent state — the envelope pattern, restated.** `EnvelopeParameters` carry *seconds* (rate-agnostic) and `AmplitudeEnvelope` converts them to *frame counts* using the voice's `outputSampleRate` at construction. Identically: `FilterParameters` carry *cutoff Hz + resonance* (rate-agnostic) and `BiquadLowPassFilter` converts them to *biquad coefficients* using `outputSampleRate` at construction. The region stays sample-rate-agnostic and shareable across voices; the per-voice object owns the rate-dependent, mutable state. This is why coefficients are **not** precomputed in the resolver (the resolver has no output sample rate, and coefficients are rate-dependent) — the descriptor-on-region / coefficients-in-voice split is the DRY-consistent choice, not an arbitrary one.

---

## 5. Components & Responsibilities

### 5.1 `FilterParameters` (new — mirrors `EnvelopeParameters`)
- **Is:** an immutable, rate-independent, SF2-unit-free `readonly struct` describing a low-pass filter: a **cutoff frequency in Hz** and a **resonance** (linear Q). Carries no SoundFont-specific knowledge — cents and centibels are converted away before an instance exists.
- **Owns:** the two descriptive values and a well-known **`Default`** (= open filter: cutoff at the SF2 open value ≈ 19912 Hz, resonance 0.707). `Default` is what hand-built patches and every non-SF2 construction site use.
- **Does NOT own:** coefficients, sample rate, per-sample state, or any notion of frames. Does NOT decide bypass (that is the filter's job, since bypass depends on the runtime sample rate relationship only through the open sentinel it carries).

### 5.2 `BiquadLowPassFilter` (new — mirrors `AmplitudeEnvelope`)
- **Is:** a mutable `struct` advanced in place (like `GainRamp`/`AmplitudeEnvelope`), constructed from `(FilterParameters, sampleRate)`. Copying by value loses in-flight state — same documented caveat as the envelope.
- **Owns:**
  - **One-time coefficient computation** at construction: the standard RBJ Audio-EQ-Cookbook **low-pass** coefficient set derived from `(cutoffHz, resonanceQ, sampleRate)`, normalised to **unity DC gain**. The trig/`Math.Pow` runs exactly once here (netstandard2.0-safe).
  - The **bypass decision**, computed once at construction: `bypass = requestedCutoffHz ≥ Sf2OpenCutoffHz` (a named const ≈ 19912 Hz). When bypassed the section is a pure passthrough.
  - **Coefficient guards:** effective cutoff clamped to `(minHz, 0.45·fs)` for the non-bypass case; resonance clamped to a stable band (floor ≈ 0.5, ceiling ≈ 25) so `a0 ≠ 0` and all coefficients are finite by construction.
  - The **two per-sample state variables** of the section and the `Process(sample)` step that returns the filtered sample and advances the state by exactly one sample.
- **Does NOT own:** the amplitude gain, the read position, block iteration, the region, or any block-size awareness. `Process` is a single-sample operation — block size is never an input (INV-1 preserved by construction).

### 5.3 `Sf2RegionResolver` (existing — one new private builder, mirrors `BuildEnvelopeParameters`)
- **New responsibility:** a `BuildFilterParameters(zone, globalZone)` helper that reads generator 8 and generator 9 via the existing local-then-global-zone accessor, applies SF2-spec defaults where absent, converts units to Hz + linear Q, applies spec-range clamps, and returns a `FilterParameters`. Its result is passed to the (one) `new SampleRegion(...)` construction alongside the existing envelope.
- **Does NOT own:** coefficient math, sample rate, or the running filter. Stays the single home for SF2 *interpretation* only.

### 5.4 `SampleRegion` (existing — one new field, sibling of `Envelope`)
- **New responsibility:** carry an immutable `Filter` (`FilterParameters`) field, populated at construction, exposed read-only. One new required ctor parameter, exactly as `Envelope` was added in PR 6.
- **Does NOT own:** running filter state (that is per-voice, like the read position — it must NOT live on the shared region).

### 5.5 `SamplePlaybackVoice` (existing — construct + apply)
- **New responsibility:** build a `BiquadLowPassFilter` from `region.Filter` + `outputSampleRate` in its constructor (beside the existing `AmplitudeEnvelope` construction), and call `filter.Process(sample)` on the interpolated sample **before** the gain multiply in `RenderBlock`.
- **Does NOT own:** coefficient math or SF2 interpretation.

---

## 6. Interactions & Data Flow

**Note-start (once per voice):**
1. `Sf2RegionResolver.BuildRegion` calls `BuildFilterParameters` → `FilterParameters(cutoffHz, resonanceQ)` → stored on the new `SampleRegion.Filter`.
2. `SamplePatch.StartVoice` constructs `SamplePlaybackVoice` (signature unchanged — the filter descriptor is already on the region).
3. `SamplePlaybackVoice` ctor builds `new BiquadLowPassFilter(region.Filter, outputSampleRate)` → coefficients + bypass flag computed once.

**Per frame (inside `RenderBlock`, per sample):**
1. Compute the interpolated sample (or the `0f` substituted when the one-shot is exhausted), advance read position — **unchanged**.
2. `sample = filter.Process(sample);` — the filter advances its two state variables by one sample. When bypassed it returns the input unchanged (exact passthrough). The filter processes **every** frame, including the substituted `0f` after exhaustion, so its resonant tail decays naturally instead of freezing.
3. `block[i] = sample * (envelope.AdvanceFrame() × gainRamp.AdvanceFrame());` — gain multiply **after** the filter.
4. Existing release/exhaustion lifecycle checks — unchanged.

All frames still pass through `Synthesizer.Finalize` (the single INV-2 NaN/Inf + clamp choke point) — untouched.

---

## 7. Data Model (Conceptual)

| Entity | New? | Key attributes | Ownership | Lifetime |
|---|---|---|---|---|
| `FilterParameters` | new | CutoffHz, Resonance (linear Q); `Default` = open | immutable value on `SampleRegion` | shared, region-lived |
| `SampleRegion.Filter` | new field | a `FilterParameters` | region | shared across voices |
| `BiquadLowPassFilter` | new | coefficients (const after ctor), bypass flag, 2 state vars | one per **voice** | note-lived, mutable |

The state/param split mirrors the envelope exactly: **descriptor is immutable + shared** (`FilterParameters` on the region), **running state is mutable + per-voice** (`BiquadLowPassFilter` in the voice), just as `EnvelopeParameters` (region) vs `AmplitudeEnvelope` (voice), and read position (voice, not region).

---

## 8. Contracts & Interfaces (Abstract)

**`FilterParameters`**
- Constructed from a cutoff in Hz and a linear resonance Q. Immutable. Carries no sample rate and no coefficients.
- `Default` = the SF2 open filter (cutoff ≥ the open sentinel ⇒ downstream bypass; resonance 0.707). Invariant: constructing a `BiquadLowPassFilter` from `Default` yields an **exact passthrough** at any sample rate.

**`BiquadLowPassFilter`**
- Constructed from `(FilterParameters, sampleRate)`; `sampleRate ≤ 0` is rejected (mirrors envelope/ramp).
- Coefficients + bypass computed once at construction; **no allocation, no transcendental math after construction**.
- `Process(sample)` → returns the filtered sample and advances the two state variables by exactly one sample. Semantics:
  - **Bypass mode** (open filter): returns the input unchanged, bit-for-bit.
  - **Active mode:** applies the normalised RBJ low-pass section (unity DC gain).
  - Invariants: block size is never an input; given finite input and the ctor guards, output is finite (never NaN/Inf); a low-frequency input passes with ≈ unity magnitude, a high-frequency input above cutoff is attenuated.

**Voice composition invariant (extended):** per frame,
`block[i] = filter.Process(interpolatedSample) × envelope.AdvanceFrame() × gainRamp.AdvanceFrame()`,
with the filter strictly **upstream** of the gain product and no block-boundary discontinuity introduced by the filter.

---

## 9. Cross-Cutting Concerns

- **INV-1 (no zipper):** preserved by construction — `Process` is per-sample with no block-size term, identical in spirit to the envelope/ramp. Existing `GainGlideTests` remain green (default = open = passthrough, so they are untouched byte-for-byte).
- **INV-2 (NaN/Inf-safe):** unchanged as the system safety net — `Finalize` stays the single choke point. The filter's *additional* obligation is not to *manufacture* NaN/Inf: guaranteed by clamping cutoff strictly below Nyquist and resonance to a positive stable band at construction, so `a0 = 1 + alpha ≠ 0` and every coefficient is finite. A stable low-pass with finite coefficients and finite input produces finite output; resonance may transiently boost magnitude above input near cutoff, which `Finalize` clamps to `[-1, 1]` exactly as it already does for summed voices.
- **No new transcendental math on the hot path (netstandard2.0 + MathF/SIMD deferral holds):** `Math.Pow`/`sin`/`cos` run **once** per note at coefficient build; `Process` is multiply-add only. The polyfill stays deferred — a YAGNI deferral, not an oversight.
- **Concurrency:** none introduced. `FilterParameters` is immutable and shareable; `BiquadLowPassFilter` state is per-voice and touched only by that voice's render, exactly like the read position and envelope.
- **Error handling:** structurally-valid-but-imperfect SF2 filter generators degrade to clamped-but-sane values (never throw on the note path), matching the resolver's existing defensive posture. Out-of-range cutoff/Q are clamped, not rejected.
- **Denormals:** a resonant section fed silence can enter denormal state (slow, not wrong). No correctness impact; flush-to-zero is deferred (§2 OUT). If profiling later shows it, it is a self-contained follow-up.

---

## 10. Quality Attributes & Trade-offs

| Decision | Chosen | Alternative rejected | Reasoning |
|---|---|---|---|
| Descriptor location | `FilterParameters` on `SampleRegion` (sibling of `Envelope`) | New sibling threaded through `TryResolve` / `SamplePatch` / voice ctor | Design Contracts §4 + envelope precedent (decision #3 of #7063). One region field + N ctor-site updates beats 3 signature changes and breaking many call-sites. |
| Coefficients: where computed | Once, in `BiquadLowPassFilter` ctor at note start (voice has `outputSampleRate`) | Precompute in the resolver and store coefficients on the region | Coefficients are **rate-dependent**; the region is shared and rate-agnostic; the resolver has no output rate. Mirrors seconds-on-region / frames-in-envelope exactly. |
| Filter form | Transposed Direct Form II biquad (2 state vars) | Direct Form I (4 state vars) | Fewer state variables, better float behaviour, textbook default. Either is spec-correct; DF2T is the simpler, well-conditioned choice. (Form is an implementation detail — the contract is "a normalised RBJ low-pass section"; the implementer may choose DF1 if a regression test dictates.) |
| Open-filter handling | Explicit **bypass** keyed to the SF2 open sentinel (Fc ≥ 13500 cents) → exact passthrough | Always run the biquad; accept "within tolerance" | Only a keyed bypass gives **bit-identical** no-regression at **every** sample rate (a near-Nyquist biquad at 48 kHz would still roll off). Also saves CPU on the (common) open case and sidesteps near-Nyquist conditioning. Simpler to reason about than a tolerance argument. |
| Bypass placement | Inside the filter (`Process` short-circuits on a bool) | Nullable filter + per-block branch in the voice | Keeps the voice loop uniform (`always filter.Process`), one well-predicted bool per sample — negligible, and DRY with the always-call-`AdvanceFrame` envelope shape. |
| Static filter only | Coefficients fixed per note | Per-frame recompute for modulation | Brief scope; modulation sources are a separate PR. YAGNI — no dynamic-cutoff consumer exists yet. |

**KISS/DRY/YAGNI (load-bearing).** User ask (verbatim, orchestrator): *"merged - sounded like a smooth note release, so continue i guess"* → the next DSP feature, the filter. The design adds the **minimum** cohesive unit: two new small types (each a 1:1 mirror of an existing envelope type — no novel abstraction), one region field, one resolver helper, one voice apply-line. No config knobs (cutoff/Q come from SF2 data, not operator tuning — §3-of-#1136 satisfied: no named operator, no environment variance ⇒ they stay data, not config). No new service/layer. No speculative extensibility hooks for the future LFO/filter-envelope (those get their own PR with their real shape). DRY math for any inline-vs-extract call: the only multi-line block is the coefficient computation, which lives **once** in the filter ctor (not inlined anywhere) — no duplication to weigh.

---

## 11. Risks & Mitigations

| Risk | Failure mode | Mitigation |
|---|---|---|
| Wrong transfer function (legacy defect #6164 — stale coeff from member vs local) | Filter attenuates the wrong band | Regression test: impulse/two-tone asserting measured HF attenuation vs LF pass matches the expected low-pass response within tolerance. Coefficient computation isolated in one place, unit-tested directly. |
| Filter silently disabled (legacy defect #6243 — `Disable()` hard-wired) | All patches stay bright | Regression test: a region with a **low** cutoff must attenuate HF through the full voice path (proves the filter is wired and *applied*, not stubbed). |
| Regression on open patches | Existing renders / tests drift | Bypass on the open sentinel ⇒ **bit-identical**; assert `Process(x) == x` exactly for `FilterParameters.Default`; existing `GainGlideTests`/`EnvelopeDeclickTests` stay green unchanged. |
| Coefficient blow-up (extreme Q or cutoff ≥ Nyquist) | NaN/Inf or self-oscillation | Cutoff clamped `< 0.45·fs`, resonance clamped to `[~0.5, ~25]` at construction ⇒ finite coefficients, `a0 ≠ 0`. `Finalize` remains the final net. Unit test: extreme params → finite output. |
| Filter frozen during release/exhaustion | Resonant tail clicks or holds a DC value | Filter processes **every** frame including the post-exhaustion `0f`, so state decays naturally. |
| Denormal slowdown on long silent tails | CPU cost (not audibility/correctness) | Accepted and deferred (§2 OUT); revisit only if profiling flags it. |

---

## 12. Migration / Rollout Strategy

Not applicable in the deployment sense (library, atomic build). The only migration is the **required new ctor parameter** on `SampleRegion`: every `new SampleRegion(...)` site gains a `FilterParameters` argument. Sites (from a repo scan): one production (`Sf2RegionResolver.BuildRegion`) plus the test construction sites (`EnvelopeDeclickTests`, `GainGlideTests`, `SynthesizerFlowTests`). Production passes the resolver-built value; all hand-built/test sites pass `FilterParameters.Default` (open ⇒ passthrough ⇒ those tests stay bit-identical). This mirrors exactly how PR 6 added the `Envelope` parameter.

---

## 13. Open Questions

1. **Audible A/B demo mechanism — RESOLVED (no demo change).** The `FilterVoiceFlowTests` prove HF attenuation through the full synth path (measured >4× reduction of a 6 kHz source at a 400 Hz cutoff), and the existing green voice/synth tests prove the open-filter no-regression. Imposing a cutoff from `RenderDemo` would require exposing/reconstructing the SF2-resolved region's filter through the library surface — demo-only plumbing that fails KISS/YAGNI. **Decision:** no demo change; the audible A/B is produced by rendering the same preset/note on `main` (filter absent) vs this branch (filter active), and the authoritative transfer-function proof is the automated tests.
2. **Exact SF2 Q→linear-Q constant.** The conceptual mapping is fixed (centibels → dB via ÷10, dB → linear Q via the standard cookbook relation, 0 cB ⇒ Q ≈ 0.707). Whether to apply the common −3.01 dB DC-gain adjustment used by some engines (FluidSynth/TSF) is an implementer choice to pin against the regression test's tolerance; default to the plain cookbook relation unless the two-tone test says otherwise.

---

## 14. Implementation Guidance for the Next Agent

Build order (each step compiles + tests green before the next; no code decisions beyond what §5/§8 fix):

1. **`FilterParameters`** (`Synthesis/`) — immutable `readonly struct`: cutoff Hz, resonance (linear Q), `Default` = open (cutoff = the SF2 open Hz ≈ 19912, resonance 0.707). Mirror `EnvelopeParameters` doc-comment style. Encode the open-cutoff and default-Q constants as named `const`/`static readonly` (they are spec constants, not config).
2. **`BiquadLowPassFilter`** (`Synthesis/`) — mutable `struct`, ctor `(FilterParameters, sampleRate)`: compute the normalised RBJ low-pass coefficients once (guarded: clamp cutoff `< 0.45·fs`, resonance `[~0.5, ~25]`); compute the bypass flag (`cutoff ≥ Sf2OpenCutoffHz`); two state vars; `Process(sample)` single-sample step (passthrough when bypassed). Mirror `AmplitudeEnvelope`/`GainRamp` structure and the "copying loses state" caveat.
   - **Unit tests first (regression-encoded):** (a) impulse/two-tone → HF attenuated, LF ≈ passed (defect #6164); (b) `Default` → `Process(x) == x` exactly (bit-identical passthrough); (c) extreme Q/cutoff → finite output (INV-2 support); (d) a resonant cutoff shows the expected peak/rolloff shape within tolerance.
3. **`SampleRegion`** — add the required `Filter` (`FilterParameters`) ctor param + read-only property, sibling of `Envelope`. Update every `new SampleRegion(...)` site; hand-built/test sites pass `FilterParameters.Default`.
4. **`Sf2RegionResolver`** — add `BuildFilterParameters(zone, globalZone)`: read gen 8 (Fc cents, default 13500) and gen 9 (Q cB, default 0) via the existing local-then-global accessor; convert `Hz = 8.176 · 2^(cents/1200)` and centibels → linear Q; clamp Fc to the spec range and Q to the stable band; pass the result into the `new SampleRegion(...)` call. Resolver unit tests: absent generators → `Default` (open); a low-Fc generator → the expected cutoff Hz; a resonance generator → the expected Q.
5. **`SamplePlaybackVoice`** — build `BiquadLowPassFilter` in the ctor (beside the envelope) from `region.Filter` + `outputSampleRate`; insert `sample = filter.Process(sample);` **before** the `block[i] = sample * gain;` line, after read/exhaustion substitution. Update the class doc-comment to state the filter sits pre-amplifier. Full-path test: a region with a low cutoff attenuates HF energy through the synth; an open region renders bit-identical to a pre-filter baseline; existing `GainGlideTests`/`EnvelopeDeclickTests` unchanged and green.
6. **`RenderDemo`** — unchanged (per Open Question 1's resolution). The audible A/B is a `main`-vs-branch render of the same preset/note; the automated tests are the authoritative proof.
7. **Verify:** `dotnet build` both TFMs (netstandard2.0 + net8.0), 0 warnings/0 errors; `dotnet test` green (all prior + new). Run the body-comment grep and XML-doc check per Code Contracts §5.5 before declaring PR-ready.

---

## Pre-Design Checklist (Design Contracts #1136 §5)

**KISS / DRY / YAGNI** — ✓ No mirror type with an overlapping value-space (the two new types mirror the *envelope* pattern structurally but describe a disjoint concern — filtering, not amplitude). ✓ No abstraction with a single implementation and no second planned (concrete types, no interface). ✓ No "might need later" element — LFO/filter-envelope hooks are explicitly out. ✓ No deprecation/flag/shim (atomic build). ✓ Inline-vs-extract: the only multi-line block (coefficients) lives once in the filter ctor — no `block_size × site_count` duplication to weigh.

**Existing systems first** — ✓ Audited: no filter/biquad exists in the new `src` tree (grep clean); this is genuinely new behaviour, not a parallel layer. ✓ Rides on the existing `SampleRegion` and `Sf2RegionResolver` rather than a new layer; the one new region field's concrete justification (per-note running state must not be shared; descriptor must be) is named. ✓ No new persisted data (library, no store).

**Configurability** — ✓ No config knobs: cutoff/Q are SF2 *data*, not operator-tunable settings (no named operator, no environment variance — §3-of-#1136). Spec constants (open cutoff, default Q, clamp bounds) stay named `const`/`static readonly` in code.

**Less is better** — ✓ Every element passes can-it-be-deleted (removing any breaks the feature) / merged (filter descriptor merged onto the region, not a new sub-object) / inlined (coefficients are the one justified extraction). ✓ Trade-offs named explicitly in §10 (bypass vs tolerance; descriptor-on-region vs threaded sibling; coefficients-in-voice vs in-resolver).

**Document discipline** — ✓ Cites Code Contracts #114 and Design Contracts #1136 as load-bearing. ✓ Scope inventories explicit; out-of-scope listed. ✓ Migration enumerates every `SampleRegion` construction site (production + tests). ✓ No multi-paragraph "why keep X" filler. ✓ Does not supersede a prior design (additive to the envelope work).

---

## Regression Basis (defect catalog #6272)

- **§C #6243** — legacy `Sf2Patch.Start()` unconditionally disabled the filter (the notable "all SF2 too bright" regression). Encoded as: a low-cutoff region **must** attenuate HF through the full voice path — proving the filter is applied, not stubbed.
- **§C #6164** — legacy `GenerateFilterCoeff` used a stale member field for a low-pass coefficient → wrong transfer function. Encoded as: impulse/two-tone transfer-function assertion (HF attenuated, LF passed, resonant shape within tolerance) driven directly against the coefficient computation.
