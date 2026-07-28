> **Repo path (source of truth):** `docs/architecture/master-headroom.md` on branch `feature/master-headroom` of `telmengedar/AudioSynth` (branched from `origin/main` @ merge `1707299`). The DiVoid node is the graph-discoverable copy; this repo file ships in the PR.
>
> **Load-bearing contracts:** Code Contracts (DiVoid #114 §0/§1/§5.5), Design Contracts (DiVoid #1136 §1 KISS/DRY/YAGNI, §3 magic-numbers-stay-magic, §5 Pre-Design Checklist), PR-shape #1165. **Source bug:** #7212. **Predecessor design:** mix-bus #7126 (this fix cashes in its deferred open question O2/O3 — the "master-trim const").

# Architectural Document: Master Headroom Trim (gain-staging the master bus)

## 1. Problem Statement

The offline renderer's master bus soft-clip limiter (`Synthesizer.ApplyMasterBus`, knee 0.9, `tanh` saturation) is engaged **continuously** on loud/dense songs, not just on transients. BUG #7212 measured the master pinned at peak **1.000 for 5+ seconds straight** (126.5–131.5s of *Force Your Way*), RMS wobbling 0.36–0.50, with the summed mix (7+ active channels × ~0.62 default CC7/CC11 gain × equal-power pan) constantly exceeding the 0.9 knee.

Because the soft-clip is a **single shared non-linearity**, a quiet steady element (a riding hihat, ch9, velocity constant 127) has its apparent level **inversely modulated by the density of the loud elements around it**: at a melodic lull the compression eases and the hihat swells; when the mix thickens again it is squashed. That intermodulation is the audible "sliding volume bump" the user heard at 2:09 — classic limiter pumping/breathing, most obvious on an exposed steady voice.

**Goal:** give the master **headroom** so normal-playing peaks land *below* the soft-clip knee, and the soft-clip only catches genuine transients. **Success criteria** (BUG #7212 acceptance gate, measured on re-render):

1. The master peak is **no longer pinned at 1.0** across 126–131s of *Force Your Way* — peaks vary (real headroom restored).
2. The exposed-hihat level is **stable across the ~128s lull** (pumping gone or materially reduced).
3. **No hard clipping** — `Finalize` clamp count ≈ 0.
4. A sparse ballad (*Eyes On Me*) is **not now too quiet**; DKC2 retains character (no regression).

## 2. Scope & Non-Scope

**In scope**
- A single master **headroom trim** — a constant attenuation applied to the fully-summed master (post voice-mix, post reverb/chorus) *before* the soft-clip.
- One named `const` for the trim factor.
- Verification (automated unit test + the empirical re-render gate) that the limiter no longer pins constantly and no hard clipping occurs.

**Out of scope** (explicitly — do NOT build)
- Per-channel or per-voice dynamics / limiting.
- A look-ahead / level-tracking / adaptive limiter with attack/release state.
- Makeup-gain automation, loudness normalization, or an auto-normalizer.
- Any new user-facing knob or configuration surface beyond the single `const`.
- Any change to `Finalize` (INV-2 must hold), to the allocation-free `Read` contract, or to the CC7/CC11 gain curve.
- Pan / SF2 pan (already handled), SoundBank (exonerated in #7124).

## 3. Assumptions & Constraints

| # | Assumption / Constraint | Confidence |
|---|---|---|
| A1 | Offline render only — no realtime latency budget; a single extra multiply per sample is free. | High |
| A2 | The summed master (post-effects) on the densest measured song has a raw pre-trim peak **well above** the 0.9 knee (the soft-clip output pinned at 1.0 means the `tanh` input excess was large, i.e. raw magnitude ≳ 1.2–1.6). Exact value is unknown until measured — see O1. | Medium |
| A3 | The user accepts a lower absolute output level in exchange for clean dynamics ("the engine getting a bit quieter" is fine). This licenses a fixed trim over any makeup gain. | High (stated in brief) |
| A4 | `INV-2` (Finalize is the sole NaN/Inf choke + final guard) and the allocation-free steady-state `Read` are hard invariants inherited from mix-bus #7126. | High |
| A5 | Both TFMs (`netstandard2.0`, `net8.0`) must build 0-warning; full `dotnet test` green. | High |

## 4. Architectural Overview

The fix is a **gain-staging attenuation** on the master bus: multiply the fully-summed master signal by a fixed headroom factor `< 1.0` **before** the existing soft-clip, so the whole mix is lowered uniformly and its normal peaks sit under the 0.9 knee.

```
                       voices (per-channel gain × equal-power pan)
                                    │  Σ
                                    ▼
                              [ master buffer ]
                                    │
                       chorus send-return  ──┐  (added into master)
                       reverb send-return  ──┘
                                    │
                                    ▼
        ┌─────────────────────  MASTER BUS  ──────────────────────┐
        │  ① headroom trim:  x ← x · MasterHeadroomTrim   (NEW)    │   ← the fix
        │  ② soft-clip:      if |x| > knee → tanh-saturate         │   (unchanged law)
        └──────────────────────────┬──────────────────────────────┘
                                    ▼
                              Finalize  (NaN/Inf → 0; hard clamp guard)   ← INV-2, untouched
                                    ▼
                              destination
```

Step ① is the entire change. It is a **uniform linear scale of the complete mix**, which is the defining property that kills the pumping:

- The soft-clip disengages on normal peaks (they now fall below the knee), so the shared non-linearity stops inversely modulating the steady hihat.
- Because the scale is a single constant applied to *every* sample equally, the **relative** balance between voices is preserved exactly — the hihat's level relative to the mix is unchanged; only the absolute level drops. No intermodulation, no character change beyond level.

**Why post-effects (on the summed master), not at voice-mix time.** The reverb and chorus send-returns are added into the master *after* the voice loop. Trimming the voice sum before the effects would let the wet returns re-inflate the signal past the knee. The trim must apply to the *final* summed master — a true master fader — which is exactly the point immediately before the soft-clip.

## 5. Components & Responsibilities

Only one component changes. No new type, no new file, no new method.

| Component | Responsibility (after change) | Does NOT own |
|---|---|---|
| `Synthesizer.MasterHeadroomTrim` (new `const float`) | Names the fixed master attenuation factor. Sits beside `MasterBusKneeThreshold`. | Any runtime/config mutability — it is a fixed engine characteristic (§3 Design Contracts: magic numbers stay magic). |
| `Synthesizer.ApplyMasterBus` (existing static method, modified) | The **master bus stage**: apply headroom trim to every finite sample, then soft-clip anything still above the knee. One pass, stateless. | NaN/Inf zeroing and the final hard-clamp guard — those stay in `Finalize` (INV-2). |
| `Finalize` | Unchanged. Sole NaN/Inf choke + last-resort clamp. | The headroom/limiting behaviour. |

### 5.1 Form decision — fold the trim INTO `ApplyMasterBus` (single stage)

Two forms were considered:

- **Form A — separate stage:** a new `ApplyMasterHeadroom` method (or inline loop) multiplying every master sample by the trim, called before `ApplyMasterBus`. Keeps `ApplyMasterBus` bit-for-bit. Cost: a second full O(N) pass over the buffer and a second method.
- **Form B — fold into `ApplyMasterBus` (CHOSEN):** the trim becomes the first per-sample operation inside the existing loop; the soft-clip then acts on the trimmed value. One pass, no new method.

**Chosen: Form B.** Rationale (Design Contracts §4 "can it be merged"): headroom and soft-clip are two facets of *one* master-bus gain-staging operation; folding them is one stage, one pass, no new surface. Form A's separation buys nothing here — "apply headroom then limit" is not two independently-meaningful concerns, it is the master bus. The method's contract changes from "unity below the knee" to "trim the whole signal, then soft-clip above the (post-trim) knee" — which is precisely the behaviour the bug demands (unity-below-knee is exactly the no-headroom defect being removed).

**Structural note for the implementer:** today the below-knee branch does an early `continue` with no write (unity = no change). After folding, *every* finite sample is scaled and therefore must be written back — the early-`continue` for below-knee samples is replaced by a single write of the trimmed value at the end of the finite path. NaN/Inf samples still `continue` untouched (not trimmed) so `Finalize` remains their sole choke (INV-2 preserved: `NaN·trim = NaN`, `Inf·trim = Inf`, both still caught downstream).

## 6. Interactions & Data Flow

No interface changes. `ApplyMasterBus` is a private static call inside `Read`, invoked once per block on `masterSlice` immediately before `Finalize`. The call site (`Synthesizer.Read`, ~line 434) is unchanged. Blast radius is confined to the body of one static method plus one new `const` — no change to `ISynthesizer`, `MidiSequencer`, `OfflineRenderer`, the CLI, or any test-only implementer.

Per-sample logic after the change (conceptual, prose — not code):

1. Read the sample. If NaN or Infinity → leave it and skip to the next (Finalize will zero it).
2. Multiply the sample by `MasterHeadroomTrim`.
3. If the trimmed magnitude is at or below the knee → keep the trimmed value.
4. Otherwise → apply the existing sign-preserving `tanh` soft-clip to the trimmed value.
5. Write the resulting value back.

## 7. Data Model (Conceptual)

None. No entities, no persistence. The only new datum is a compile-time constant.

## 8. Contracts & Interfaces (Abstract)

| Element | Contract |
|---|---|
| `MasterHeadroomTrim` | A `const float` in `(0,1]`. The uniform attenuation applied to the summed master before soft-clip. XML `<summary>` states its purpose and cites BUG #7212 + this design. Recommended value below (O1). |
| `ApplyMasterBus(block)` | **Post:** every finite input sample `x` becomes `clip(x · trim)` where `clip` is unity for `|x·trim| ≤ knee` and the sign-preserving `tanh` saturation otherwise; NaN/Inf pass through unchanged. Stateless, allocation-free, one pass. The result is bounded to `(−1, 1)` for finite input, so `Finalize`'s hard clamp is a no-op for finite samples (INV-2 unchanged). |

**Invariants preserved:** INV-2 (Finalize is the sole NaN/Inf choke + final guard); allocation-free `Read`; the CC7/CC11 gain curve and pan/effects paths are untouched.

## 9. Cross-Cutting Concerns

- **Determinism:** the trim is a pure constant multiply — fully deterministic, identical across both TFMs. No platform-dependent math (unlike the existing `tanh`/`cos`/`sin`, which are unchanged).
- **Numerical safety:** trimming reduces magnitudes, so it cannot *introduce* clipping or overflow; it strictly reduces the load on the soft-clip and the Finalize guard. NaN/Inf handling is unchanged.
- **Performance:** one extra multiply per sample inside an existing loop; no new allocation, no new pass (Form B). Negligible and irrelevant for offline render.
- **Observability:** none needed at runtime. Validation is offline (the acceptance renders + the unit test).

## 10. Quality Attributes & Trade-offs

- **Simplicity (primary):** one const + a fold into one existing method. No new type, file, method, knob, or state. This is the minimal change that solves the measured problem — matches Design Contracts §1/§4 and the brief's "one attenuation, one const" mandate.
- **Trade-off — fixed trim vs. level-tracking limiter.** A level-tracking (attack/release) limiter would give per-song optimal loudness with smoother behaviour. It was **rejected** (already rejected in mix-bus #7126): it adds per-channel/global state, attack/release magic constants, and pumping-of-its-own risk — over-engineering for an offline render. Concrete downside of the fixed trim, named per §4: a single constant cannot be optimal for both the densest song and the sparsest — the sparse ballad is attenuated more than it strictly needs. Probability this matters: low (the user accepts a quieter engine, A3); cost if it does: re-tune one number, or (future, only if a real need surfaces) revisit adaptivity with the actual requirement in hand. The fixed trim wins now.
- **Trade-off — makeup gain: deliberately NOT added.** Makeup gain would restore level after the trim, but restoring level is precisely what re-engages the limiter and reintroduces the pumping. Gain-staging *accepts* the lower level (A3). Adding makeup would defeat the fix. No makeup.
- **Maintainability:** the master-bus stage remains a single stateless static method; the new behaviour is one line + one constant, self-documented via XML summary citing the bug.

## 11. Risks & Mitigations

| Risk | Likelihood | Mitigation |
|---|---|---|
| Trim too small → limiter still pins on the densest song (peaks still ≳ knee). | Medium (value is a first guess) | The acceptance gate is empirical: re-render *Force Your Way* and confirm peaks vary across 126–131s. If still pinned, lower the trim and re-render. O1 records the tuning band + procedure. |
| Trim too large → *Eyes On Me* becomes too quiet / mix feels weak. | Low–Medium | Re-render *Eyes On Me* (§4 criterion 4). If too quiet, raise the trim toward 0.6–0.7. The two acceptance renders bracket the value. |
| Below-knee branch no longer writes the trimmed value (missed write in the fold). | Low | Covered by an updated unit test: a quiet DC voice below the knee must now read back **trim × its level**, not its raw level (see §14). |
| INV-2 accidentally broken by trimming NaN/Inf. | Low | Keep the NaN/Inf `continue` *before* the multiply; NaN/Inf are never trimmed and remain Finalize's job. Unit-level DC/loud tests plus the existing soft-clip tests guard the finite path. |

## 12. Migration / Rollout Strategy

Not applicable — a private-monorepo, atomic single-PR behavioural change to an offline engine. No feature flag, no deprecation window (Design Contracts §6). Ships in one PR bundling this design + the implementation (PR-shape #1165).

## 13. Open Questions

- **O1 — the trim value (needs user sign-off; the one number to tune by ear).** Recommended **starting value: `0.5` (−6 dB)** — a standard, principled master-headroom target. Rationale: the densest measured song's soft-clip is pinned at 1.0, implying a raw summed peak well above the 0.9 knee (≈1.4–1.6, A2); halving it lands typical peaks in the ~0.7–0.8 range, comfortably below the knee, so the soft-clip disengages on normal material and only catches true transients. If *Eyes On Me* proves too quiet at 0.5, raise toward **0.6 (−4.4 dB)** or **0.7 (−3.1 dB)** — 0.6 puts a ~1.5 raw peak exactly at the knee (marginal), 0.7 leaves the densest song still lightly limited but louder. The two acceptance renders arbitrate; the value is the single tunable const. **Refinement procedure for the implementer:** measure the raw pre-soft-clip master peak on *Force Your Way* (e.g. temporarily read `masterSlice` peak before the trim, or set trim = 1 and read the pre-clip peak), then pick `trim ≈ 0.85 × knee / rawPeak` as a headroom-with-margin target and confirm against the four §4 criteria. **User: do you want to sign off on 0.5 as the starting point, or state a preferred value/band before it's tuned by ear?**
- **O2 — is the raw pre-trim peak worth logging once during tuning?** Suggest yes as a throwaway during implementation (to ground O1's value), but NOT shipped as runtime observability (YAGNI). Confirm no persistent telemetry is wanted.

## 14. Implementation Guidance for the Next Agent

Ordered, at the architectural-unit level (no code here — this is the work breakdown). **Recommended executor: `john-backend-dev`** — I (architect) commit this design to the branch; John implements on the same branch and opens ONE PR bundling design + fix (PR-shape #1165, mirrors the mix-bus #7126 → PR precedent). This is a contained change (one const + fold into one method + tests) but it is code, so it goes to John, not bundled by the architect.

1. **Add the constant.** Introduce `MasterHeadroomTrim` as a `const float` beside `MasterBusKneeThreshold` in `Synthesizer.cs`, with an XML `<summary>` stating it is the master headroom attenuation applied before the soft-clip, citing BUG #7212 and this design. Initial value **0.5** (pending O1 sign-off / ear-tuning).
2. **Fold the trim into `ApplyMasterBus`.** Apply the trim as the first per-sample operation on every finite sample, then run the existing soft-clip on the trimmed value; ensure the below-knee path now *writes back* the trimmed value (the early no-write `continue` is replaced by a single write of the finite result). Keep the NaN/Inf `continue` ahead of the multiply. Update the method's `<summary>` to reflect "headroom trim, then soft-clip." No new method.
3. **Update the master-bus unit tests** (`test/Pooshit.AudioSynth.Tests/MasterBusSoftClipTests.cs`):
   - `QuietSingleVoice_UnaffectedByMasterBus` currently asserts a 0.3 DC voice reads back exactly 0.3. After the trim it must read back **0.3 × trim**. Rename/adjust the assertion and its `[Description]` to state the new contract (a below-knee voice is now uniformly attenuated by the headroom trim, not passed through at unity). This test is the guard for the "below-knee branch must write the trimmed value" risk.
   - `SeveralLoudSimultaneousVoices_NoLongerClip` should still pass (no hard clip); add/extend an assertion that the measured peak is now **below the knee** for the trimmed sum where applicable, demonstrating the soft-clip is no longer continuously engaged. Keep it asset-free and deterministic.
4. **Empirical acceptance (the gate — DiVoid #7212).** Re-render the dev-tree assets through Florestan and report before/after:
   - *Force Your Way* (`Source/AudioSynthesis.Tests/Midi/1-10-Force_Your_Way.mid`): master peak is **no longer pinned at 1.0** across 126–131s (report the peak-per-window before/after showing it varies); the exposed-hihat level is **stable across the ~128s lull** (report a level-stability metric — e.g. windowed RMS/peak of the exposed section before vs after); `Finalize` hard-clamp count ≈ 0.
   - *Eyes On Me* (sparse ballad): confirm not too quiet (report peak/RMS).
   - DKC2 (`07dkc2bram.mid`): confirm no regression in character.
   These renders use dev-tree assets located by walking up from the test assembly (see `ReverbRenderProofTests.FindDevTreeAsset`); the render-proof pattern already skips gracefully when assets are absent, so any *automated* proof test must degrade the same way. The full before/after numbers go in the PR body.
5. **Build + test gates.** Both TFMs (`netstandard2.0;net8.0`) 0-warning; `dotnet test` FOREGROUND with `timeout: 600000` (slow suite #7173), green.
6. **Self-audit on committed code** (Code Contracts §6.10 / §0 / §1): body-comment grep = **0**; one type per file (unchanged — no new type file); XML-summary present on the new const and the modified method; explicit types, no `var`; no `private` modifier noise. Confirm the diff is exactly: one new const + the `ApplyMasterBus` body fold + the two test updates + this doc. Nothing else.

---

*Author: sarah-software-architect · 2026-07-28 · supersedes mix-bus #7126's deferred open question O2/O3 (the master-trim const), which is now provably needed and specified here.*
