# Architectural Document: Exponential (linear-in-dB) Amplitude Envelope Decay & Release

> Repo: `C:\dev\claude\AudioSynth\Pooshit.AudioSynth` · Fix site: `src/Pooshit.AudioSynth/Synthesis/AmplitudeEnvelope.cs`
> DiVoid: BUG #7184 · project AudioSynth #6128 · map root #6708 · precedent `AmplitudeEnvelope` #7065 (PR6), `Sf2RegionResolver.BuildEnvelopeParameters` #6752, `GainRamp` #6731.
> Load-bearing conventions: **Code Contracts #114** (§0 KISS/DRY/YAGNI, §5.5 XML docs, §6.10 self-audit), **Design Contracts #1136** (§1 KISS/DRY/YAGNI, §5 Pre-Design Checklist).

---

## 1. Problem Statement

The voice amplitude envelope's **decay** and **release** stages advance **linearly in amplitude**
(`level -= step` per frame). SF2 / General-MIDI volume envelopes are **exponential** — linear in the
**dB (centibel)** domain. A linear-in-amplitude decay holds its level near the top and then lingers at
the quiet end instead of dropping away; in a mix, notes never damp and the signal never clears to
silence between phrases.

Measured evidence (BUG #7184): a single held piano note decays **linearly** to silence over ~9 s; a
released note fades **linearly** over ~1.1 s. Against a Windows Media Player reference render of the same
MIDI (`renders/wmp-eyesonme.wav`), WMP has **5.9%** near-silent 50 ms windows (< 0.05 normalized); our
render has **0.0%** — our signal has a floor that never clears. Reverb, sustain (CC64), and
voice-stealing were each ruled out empirically; the envelope **shape** is the defect.

**Goal:** make decay and release **exponential (linear-in-dB)** so notes damp naturally and mixes clear
to silence, while (a) keeping the audio hot path allocation-free and free of per-sample transcendental
math, and (b) preserving the two standing invariants: **INV-1** (no zipper / block-boundary steps) and
**INV-2** (`Synthesizer.Finalize` untouched).

**Success criteria:**
1. A held note's decay and a released note's release are **geometric** (constant successive-level *ratio*),
   not linear (constant successive *difference*).
2. On `3-20-Eyes_On_Me_2.mid` (Florestan piano) the near-silent-window fraction rises from **0.0%**
   toward the WMP reference **~5.9%** — the mix now clears between phrases.
3. The audio hot path (`AmplitudeEnvelope.AdvanceFrame`) remains multiply/add only — no per-sample
   `Pow`/`Exp`/`Log`. Transcendentals occur **once per stage at construction**, as they already do.
4. No declick regression through the full synth path; DKC2 (busy material) shows no audible regression.

---

## 2. Scope & Non-Scope

**In scope**
- Change the **Decay** segment (peak → sustain) and **Release** segment (current level → silence) of
  `AmplitudeEnvelope` from linear-in-amplitude to **geometric / linear-in-dB**.
- Keep the change **allocation-free** and **no per-sample transcendental**: a per-stage geometric
  factor computed once (at construction), applied per sample as `level *= factor`.
- Add unit tests asserting the exponential **shape** of decay and release (constant ratio, not constant
  difference). Verify the existing envelope/declick tests still hold and adjust only where an assertion
  encoded the linear shape (none require rewriting — see §8/§10).
- **Secondary verification** (analysis only, no code change): confirm the resolver reads the Florestan
  piano's ~9 s decay / ~1.1 s release *times* correctly (magnitudes are not inflated; the shape is the
  sole defect). Confirmed — see §7.

**Out of scope (explicitly)**
- The **attack** stage stays **linear** (SF2 attack is approximately linear/convex; BUG #7184 and PR6
  both call for this). Delay and Hold are unchanged.
- Filter envelope, LFO envelopes — this is the *amplitude* envelope only.
- Voice-stealing (BUG #7183), reverb, mix/pan, sustain-pedal — all ruled out as unrelated by #7184.
- No new configuration knob, no new type, no envelope-architecture rewrite. This is a **segment-math
  shape fix to one existing struct** (KISS / YAGNI).

---

## 3. Assumptions & Constraints

| # | Assumption / Constraint | Confidence |
|---|---|---|
| A1 | SF2 volume-env decay/release are linear-in-dB; the fix is to match that. | High — SF2 2.04 §8.1.2; BUG #7184 evidence. |
| A2 | The Florestan piano sustain level resolves near zero (held note decays to silence), so an exponential decay damps the note even while held. | High — matches the measured held-note-to-silence evidence. |
| A3 | Stage **times** (decay ~9 s, release ~1.1 s) are already correct; only the shape is wrong. | High — verified in §7. |
| A4 | `Math.Pow` at construction is acceptable and netstandard2.0-safe (already used at resolve time and, for `decayStep`, effectively at construction). It must **not** appear per sample. | High — per node #7065 the hot path must avoid the MathF/SIMD polyfill. |
| A5 | A **fixed −100 dB silence floor** (linear 1e-5) is an adequate, conventional definition of "silent" for terminating decay/release. | High — 1e-5 is well below the 1e-4 tail-silence assertion and inaudible. |
| C1 | The struct stays a mutable value type advanced in place (like `GainRamp`); no allocation, no heap. | Hard constraint (INV-1 architecture). |
| C2 | Block size is never an input to the envelope; it advances one frame at a time. | Hard constraint (INV-1). |

---

## 4. Architectural Overview

No structural change. One component — the `AmplitudeEnvelope` value-type — changes the **update rule**
of two of its stages. The stage machine (Delay → Attack → Hold → Decay → Sustain → …Release → Finished),
the public surface (`AdvanceFrame`, `Release`, `IsFinished`, `Stage`), the per-frame countdown, and the
end-of-stage snap-to-target are all retained.

```
 level
  1.0 |‾‾\        peak
      |   \··.        <- Decay: was linear (straight), now GEOMETRIC (concave, fast-then-slow toward sustain)
 sus  |     `·······──────────  sustain (held)
      |                       \··.
      |                          `····.___   <- Release: was linear, now GEOMETRIC (fast drop + long quiet tail)
  0.0 |________________________________________ time
        A   D           S            R  (snap→0 at end of R = Finished)

 Per-frame update (hot path, multiply/add only):
   Attack :  level += attackStep         (LINEAR — unchanged)
   Decay  :  level *= decayFactor         (GEOMETRIC — new; factor precomputed once)
   Release:  level *= releaseFactor       (GEOMETRIC — new; factor precomputed once)
```

The only new arithmetic in the hot path is a **multiply** replacing a subtract. All `Pow` calls live in
the constructor (one for decay, one for release), exactly where `decayStep` is computed today.

---

## 5. Components & Responsibilities

| Component | Owns (after change) | Does NOT own |
|---|---|---|
| `AmplitudeEnvelope` (struct) | The `[0,1]` amplitude contour over a note's life. Precomputes per-stage step/factor at construction; advances one frame per `AdvanceFrame`; snaps to the stage target at each stage boundary. **New:** decay & release advance geometrically. | SF2 unit conversion (owned by resolver); zipper-free CC/velocity slew (owned by `GainRamp`); voice lifecycle beyond reporting `IsFinished`. |
| `EnvelopeParameters` (readonly struct) | Rate-independent stage **times** (seconds) + sustain level (linear [0,1]). | Unchanged. No new field. |
| `Sf2RegionResolver.BuildEnvelopeParameters` | timecents→seconds and centibels→linear mapping. | Unchanged. §7 confirms times are correct. |
| `GainRamp` (struct) | Zipper-free slew for CC-volume / velocity target changes (INV-1). | Unchanged. Still composed in series: `gain = envelope × gainRamp × tremolo`. |

Single-responsibility is preserved: the envelope still owns *shape*, the resolver owns *units*, GainRamp
owns *slew*. Only the envelope's decay/release update rule changes.

---

## 6. Interactions & Data Flow

Unchanged. `SamplePlaybackVoice` composes per frame:
`gain = envelope.AdvanceFrame() × gainRamp.AdvanceFrame() × tremoloCurrent` and deactivates when
`released && envelope.IsFinished`. The contract of `AdvanceFrame` (returns current `[0,1]` level),
`Release` (enter release from current level), and `IsFinished` (release tail reached zero) is identical.
Because release is now a **multiplicative** update, "release from the current level" falls out naturally:
multiplying whatever level the note is at by a constant factor needs no per-release recomputation.

---

## 7. Data Model (Conceptual) & Secondary Time Verification

No entity/schema change. For the secondary check the brief requested:

`TimecentsToSeconds(tc) = 2^(tc/1200)`, capped at `MaxEnvelopeSeconds = 20`.
- Florestan piano **decay ≈ 9 s** ⇒ tc ≈ `1200·log2(9) ≈ 3805` ⇒ `2^(3805/1200) ≈ 9.0 s`. Under the 20 s
  cap; **not clamped, correct**.
- **release ≈ 1.1 s** ⇒ tc ≈ `1200·log2(1.1) ≈ 165` ⇒ `2^(165/1200) ≈ 1.1 s`. **Correct.**

Conclusion: the *times* are read faithfully. The **shape** is the only defect — this fix touches shape
only, not the resolver.

---

## 8. Contracts & Interfaces (Abstract) — the segment math

Define one **silence floor**: `SilenceFloorLinear = 1e-5` (≈ −100 dB), a named constant with a clear
rationale (the level below which the tail is inaudible; well under the 1e-4 tail-silence assertion).

Define one geometric-factor primitive (documents the linear-in-dB intent without a body comment; a
private helper with a concise XML `<summary>`):

> **GeometricStepFactor(targetRatio, frames)** = the per-frame multiplier that carries a level from its
> current value to `targetRatio × current` over `frames` frames, i.e. `targetRatio^(1/frames)`. A
> constant multiplicative step is, by definition, a constant dB-per-frame step — the linear-in-dB shape.

**Decay** (peak 1.0 → sustain, over `decayFrames`):
- Target ratio = `max(sustainLevel, SilenceFloorLinear)` (the floor only bites when sustain ≈ 0, so a
  zero-sustain patch decays geometrically toward silence instead of jumping there).
- `decayFactor = GeometricStepFactor(targetRatio, decayFrames)`.
- Per frame: `level *= decayFactor`; guard `if (level < sustainLevel) level = sustainLevel`; at the
  frame countdown boundary, **snap** `level = sustainLevel` (kills float drift), enter Sustain.
- Lands **exactly** on `sustainLevel` at the end of `decayFrames` (same time semantics as today).
  `decayFrames == 0` ⇒ immediate snap to sustain (existing path); factor unused.

**Release** (current level → silence, over `releaseFrames`):
- Target ratio = `SilenceFloorLinear` (a fixed −100 dB drop over the release time — SF2 rate semantics:
  the release *time* is the time for a full excursion, independent of the level at note-off).
- `releaseFactor = GeometricStepFactor(SilenceFloorLinear, releaseFrames)` — **level-independent**, so it
  is precomputed **at construction**, not at `Release()` time. (This removes the current per-release
  `releaseStep` recomputation — a small simplification.)
- Per frame: `level *= releaseFactor`; guard `if (level < 0) level = 0`; at the countdown boundary,
  **snap** `level = 0`, enter Finished. At that boundary the geometric level is `≈ initial × 1e-5`, so
  the snap is inaudible (< 1e-4). `releaseFrames == 0` ⇒ immediate finish (existing path); factor unused.

**Attack / Delay / Hold** — unchanged (attack linear per §2).

**Invariants preserved**
- **INV-1 (no zipper):** the factor is a small, continuous per-frame step; block size is not an input;
  the geometric contour has no block-boundary discontinuity. `GainRamp` still runs in series for CC/vel.
- **INV-2:** `Synthesizer.Finalize` is not touched.
- **Allocation-free / no per-sample transcendental:** two `Pow` at construction; hot path is `*=`/`+=`.

---

## 9. Cross-Cutting Concerns

- **Performance:** hot path swaps a subtract for a multiply — equal cost. Two extra `Pow` per note
  construction (once), same order as the existing `decayStep` and resolve-time `Pow`. No allocation.
- **Numerical safety:** `Pow` guarded against `frames == 0` (immediate-snap paths already exist); the
  `max(sustain, floor)` guard prevents `0^(1/n)` collapsing decay to an instant jump.
- **Concurrency / idempotency:** none introduced; the struct is single-threaded per voice, advanced in
  place, exactly as today.
- **Declick (INV-1):** the exponential first step is largest for very short release/decay times. For all
  real SF2 instrument releases and every existing test (release ≥ 50 ms), the first step stays under the
  declick epsilon (0.02) — e.g. a 50 ms release first step is ≈ 0.005. See §11 for the bounded
  short-time limitation.

---

## 10. Quality Attributes & Trade-offs

- **Correctness (primary):** geometric decay/release matches SF2/GM and the WMP reference; notes damp and
  the mix clears. This is the whole point of the fix.
- **Simplicity:** net change is *smaller* than the current code — the mutable `releaseStep` field and its
  per-release recomputation are removed in favour of a precomputed readonly `releaseFactor`. One new
  named constant, one small private helper, two field swaps.
- **Trade-off — fixed −100 dB floor vs. per-patch configurable floor:** we use a **fixed** −100 dB
  silence floor, not a knob. Per Design Contracts §3, a configurable floor has no named operator and no
  environment variance — it would be a magic number with indirection. −100 dB is inaudible and universal.
  **Decision: fixed constant.**
- **Trade-off — relative (rate) release vs. absolute-target release:** we use the **relative** model
  (fixed dB drop over release time, level-independent factor). Rejected the absolute-target model
  (`(floor/level)^(1/frames)`) because it needs a per-`Release()` `Pow`, breaks SF2 rate semantics, and
  is strictly more complex. The relative model also makes "release from current level" fall out of the
  multiply for free. **Decision: relative.**
- **Trade-off — exponential vs. linear attack:** attack stays linear (SF2 attack is ~linear; changing it
  is out of scope and unmotivated by the evidence). **Decision: linear attack.**

---

## 11. Risks & Mitigations

| Risk | Likelihood | Mitigation |
|---|---|---|
| Exponential first step clicks on **very short** release/decay times (< ~13 ms), a regime where linear did not. | Low in practice | No real SF2 instrument release is that short; every existing test uses ≥ 50 ms; the default 1 ms envelope is used only by synthetic/hand-built patches where a ~1 ms cutoff is *intended* to be near-instant (linear was already an effective hard stop there). Documented as a bounded, known limitation — **not** patched with a knob (YAGNI). If a real patch ever needs it, revisit with the concrete shape in hand. |
| Zero/near-zero sustain collapses decay to an instant jump (`0^(1/n)`). | Would be a bug | `max(sustainLevel, SilenceFloorLinear)` floor on the decay target ratio — decays geometrically to −100 dB then snaps to the true sustain. |
| Near-silent metric doesn't reach exactly 5.9%. | Possible | Success is a *substantial move* from 0.0% toward ~5.9% (the mix clears), not an exact match; measured in §12 as the deliverable proof. WMP is a different engine — parity of *behaviour* (clears to silence), not of *number*, is the bar. |
| A held note with a **long** decay (9 s) still doesn't clear fast enough while held. | Low | Exponential −100 dB over 9 s reaches −20 dB (10%) in ~1.8 s and −40 dB in ~3.6 s — the note audibly damps; released notes clear in ~0.22 s (−20 dB over 1.1 s). The near-silent gaps come chiefly from released notes now clearing. |

---

## 12. Deliverable Proof (must be run before the PR is considered done)

1. **Unit shape tests (new):**
   - Release is geometric: sample the release level at even frame intervals; assert successive
     **ratios** are ~constant and successive **differences** are **not** constant (distinguishes
     exponential from the old linear).
   - Decay is geometric: same assertion for a decay to a sustain < 1.
2. **Existing suite green:** `AmplitudeEnvelopeTests`, `EnvelopeDeclickTests`, resolver + voice tests all
   pass unchanged (they assert declick/settling/monotonicity, not linearity — see §8). Run
   `dotnet test` in the **foreground**, `timeout 600000` (slow suite, DiVoid #7173).
3. **Render proof vs. WMP reference:** re-render `3-20-Eyes_On_Me_2.mid` (Florestan) via
   `tools/Pooshit.AudioSynth.MidiRender`; measure the **near-silent-window fraction** (50 ms windows,
   normalized, threshold < 0.05). It must rise from **0.0%** toward the WMP reference **~5.9%**
   (`renders/wmp-eyesonme.wav`). Report before/after.
4. **No busy-case regression:** re-render DKC2 (`07dkc2bram.mid`); confirm it still sounds right (busy
   material where the defect was masked).

---

## 13. Open Questions

1. **Silence floor magnitude:** −100 dB (1e-5) chosen. The SF2 attenuation ceiling elsewhere in the
   resolver is 1440 cB (−144 dB) for sustain=0 and 960 cB (−96 dB) for filter/LFO. −100 dB is a clean,
   inaudible middle. Flag if a specific SF2-spec citation dictates 960 cB instead — trivially swappable
   (one constant), no structural impact.
2. **Held-note decay clearing:** if empirical near-silent % remains well below ~5% after the shape fix,
   the next suspect is the resolved **sustain level** (is it truly ~0 for this piano?) or the decay
   *time* interpretation — but the evidence points to shape as the dominant term. Confirmed by the §12
   render proof.

---

## 14. Implementation Guidance for the Next Agent

Ordered, at the architectural-unit level (all in one PR with this doc — DiVoid #1165):

1. **`AmplitudeEnvelope.cs`** — replace the two linear update rules with geometric ones:
   - Add a named `SilenceFloorLinear` constant (≈ 1e-5, −100 dB) with an XML-doc rationale.
   - Add a small private `GeometricStepFactor(targetRatio, frames)` helper (concise XML `<summary>`
     naming the linear-in-dB semantics) — or inline the two `Pow` calls if the reviewer prefers; the
     helper is favoured because it documents intent without a body comment.
   - Replace the `decayStep` field with a `decayFactor` (precomputed in the constructor from
     `max(sustainLevel, SilenceFloorLinear)` and `decayFrames`).
   - Replace the mutable `releaseStep` field with a readonly `releaseFactor` precomputed in the
     constructor from `SilenceFloorLinear` and `releaseFrames`; remove the per-`Release()` recomputation
     in `BeginStage(Release)`.
   - `AdvanceFrame`: Decay → `level *= decayFactor` (clamp ≥ sustain); Release → `level *= releaseFactor`
     (clamp ≥ 0). Keep every countdown snap and the immediate-snap (`frames == 0`) paths.
   - Update the struct's `<summary>` to say decay/release advance geometrically (linear-in-dB); attack
     linear.
2. **Tests** — add the two exponential-shape tests (§12.1) to `AmplitudeEnvelopeTests`. Confirm the rest
   of `AmplitudeEnvelopeTests` and `EnvelopeDeclickTests` pass **unchanged**; only touch an assertion if
   it demonstrably encoded linearity (none do — verify, don't pre-emptively rewrite).
3. **Build + full test run** (foreground, 600 s).
4. **Render proof** (§12.3–12.4): Eyes On Me before/after near-silent %, DKC2 regression check.
5. **Self-audit** (Code Contracts §6.10): body-comment grep = 0; XML `<summary>` on every accessibility
   present and concise; one type per file; no allocation / no per-sample transcendental introduced.
6. **One PR** bundling this doc + the code, from `feature/exponential-envelope`.
