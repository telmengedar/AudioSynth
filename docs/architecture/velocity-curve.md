# Architectural Document: Note-Velocity Dynamics — Concave Velocity→Gain Curve

> **Repo path:** `docs/architecture/velocity-curve.md` (branch `feature/velocity-curve`, PR 13).
> **DiVoid:** task #7139 · project #6128 · map root #6708 · roadmap #7098.
> **Load-bearing contracts:** Design Contracts #1136 (§1 KISS/DRY/YAGNI; §5 Pre-Design Checklist), Code Contracts #114 (§0 principles, §1, §5.5), PR-shape #1165.
> **Precedent:** PR 12 / mix-bus (#7125/#7126) established the `(x/127)²` concave curve for CC7/CC11 in `MidiSequencer.ChannelGain`.

---

## 1. Problem Statement

`SamplePatch.StartVoice(key, velocity)` maps MIDI velocity to voice gain **linearly**: `targetGain = velocity / 127f`. Real synthesizers and the SF2/GM standard use a **concave** velocity→amplitude characteristic — a soft note should be *much* quieter than a proportional reading of its velocity, not merely proportionally quieter. Under the linear map, mid-velocity notes (velocity 64 → −6 dB) still read as loud, so note-to-note dynamics are flattened: every note tends to sound "full volume."

Before PR 12 (mix bus) landed, master-bus clipping masked this — the flattening was hidden behind distortion. Now that PR 12 restored headroom and clipping no longer masks dynamics, the linear velocity curve is the audible defect the user reported: *"note volume — currently it sometimes sounds off because all notes are probably played at full volume."*

**Success criteria:**
- Soft notes are measurably and audibly quieter relative to loud notes than under the linear map.
- The velocity→gain mapping is concave (soft end attenuated more than linear; velocity 64 → noticeably below 0.5 gain).
- The mapping is a fixed, pure function (no configuration knob).
- Full musical range preserved: velocity 127 → unity, velocity 0 → silence, monotonic in between.

## 2. Scope & Non-Scope

**In scope (this PR):**
- Replace the linear velocity→gain map in `SamplePatch.StartVoice` with a concave curve.
- A unit test asserting the concave property (velocity 64 → gain well below 0.5).
- A render-proof test + re-render demonstrating widened soft-vs-loud dynamic range on a track with clear dynamics (`1-02-Balamb_Garden.mid`).

**Explicitly out of scope:**
- **Velocity-layer sample selection (`velRange` zone resolution).** Investigated and found **already implemented** — see §5.1. No work required; not deferred, *already done*.
- MIDI expression / modulation / pitch bend / sustain — separate task #7140 (flute-glide observation).
- Pan — separate task #7127 (PR 12b).
- Velocity→filter-cutoff, velocity→envelope-time, and other velocity-driven generators — not requested; YAGNI.
- Any configuration surface for the curve shape — the curve is fixed by design (see §4, §9.3).
- SF2-accurate per-modulator initial-attenuation modelling (the exact 960 cB concave default modulator) — deliberately **not** built; the square law is the chosen approximation (see §9.1 trade-off).

## 3. Assumptions & Constraints

| # | Assumption / Constraint | Confidence |
|---|---|---|
| A1 | The single home for note velocity→gain is `SamplePatch.StartVoice`. `Sf2Patch.StartVoice` delegates to a cached `SamplePatch.StartVoice`, so fixing `SamplePatch` covers **both** the standalone-patch path and the SF2 path in one place. | Verified (read `Sf2Patch.cs:83`). |
| A2 | The codebase uses `(x/127)²` as its established concave amplitude curve (CC7/CC11 in `MidiSequencer.ChannelGain`, documented against DiVoid #7126 §9). Consistency favors reusing that shape for velocity. | Verified (`MidiSequencer.cs:143-146`). |
| A3 | Velocity 0 arriving at the patch is harmless: MIDI Note-On velocity 0 is treated as Note-Off upstream; at the patch a gain of 0 (0² = 0) is correct regardless. | Verified (standard MIDI; patch is downstream of sequencer note handling). |
| A4 | The gain value flows through `GainRamp` (5 ms click-free slew) then is multiplied by the DAHDSR envelope and tremolo in `SamplePlaybackVoice`. The curve is applied **once**, at voice construction, to the ramp target — the correct, single point. | Verified (`SamplePlaybackVoice.cs:54-55,109`). |
| A5 | Both TFMs (`netstandard2.0`, `net8.0`) must compile 0-warning. The change uses only basic float arithmetic — no new dependency, no TFM-specific API. | Verified (`.csproj` TargetFrameworks). |

## 4. Architectural Overview

The change is a **single-point semantic substitution** at the one chokepoint where velocity becomes gain. No new components, no new layers, no new data, no configuration.

```
 NoteOn(channel, key, velocity)
        │
        ▼
 Synthesizer.NoteOn ──► channelPatch[ch].StartVoice(key, velocity)
        │                         │
        │            ┌────────────┴─────────────┐
        │            ▼                          ▼
        │   Sf2Patch.StartVoice        SamplePatch.StartVoice   ◄── THE ONE CHANGE
        │   (resolve region,           targetGain = VelocityToGain(velocity)
        │    delegates ↓)                        = (velocity/127)²      [was: velocity/127]
        │            └────────────┬─────────────┘
        ▼                         ▼
   VoiceSlot            new SamplePlaybackVoice(region, pitchIncrement, targetGain, …)
                                  │
                                  ▼
                        GainRamp.SetTarget(targetGain)  → click-free slew
                                  × AmplitudeEnvelope (DAHDSR)
                                  × tremolo
                                  = per-frame gain
```

The velocity→gain mapping is extracted into a small named pure function `VelocityToGain`, mirroring the existing `MidiSequencer.ChannelGain` pattern so the two concave amplitude curves in the engine share a consistent shape and naming vocabulary.

## 5. Investigation Findings (drives the scope cut)

### 5.1 Velocity-layer sample selection is already implemented

The brief flagged velocity-split zone resolution (`velRange` generator) as a candidate for bundling, on the premise that "the resolver currently resolves by key only." **That premise is outdated.** `Sf2RegionResolver` already resolves by velocity at both zone levels:

| Site | What it does |
|---|---|
| `FindInstrumentIndex(key, velocity)` — `Sf2RegionResolver.cs:122-127` | Filters **preset** zones by `VelocityRange` (generator 44), in addition to `KeyRange`. |
| `ZoneCoversNote(zone, globalZone, key, velocity)` — `Sf2RegionResolver.cs:154-160` | Filters **instrument** zones by `VelocityRange` (with global-zone fallback), in addition to `KeyRange`. |
| `TryResolve` — `Sf2RegionResolver.cs:66,70,89` | Threads `velocity` through both filters. |

A velocity-split instrument (e.g. soft sample on velocity 0–63, hard sample on 64–127) is therefore already resolved to the correct layer for the note's velocity. **No resolver work is in this PR.** This is not a deferral — the capability exists.

**Minor gap noted (not this PR):** there is no explicit unit test exercising a *velocity-split* instrument in `Sf2RegionResolverTests`. That is a test-only hardening opportunity, behavior-neutral, and belongs in a separate small task, not bundled here (see §13 / Open Questions). Bundling a test-only change for an already-working, unrelated mechanism into this feature PR would violate one-feature-per-PR.

### 5.2 The curve is the entire feature

With velocity-layer selection already working, the user's report ("all notes full volume") is fully explained by, and fully addressed by, the linear→concave curve substitution. This keeps the PR to one cohesive, small change.

## 6. Components & Responsibilities

| Component | Change | Owns | Does NOT own |
|---|---|---|---|
| `SamplePatch` (`Synthesis/Patches/SamplePatch.cs`) | Replace `velocity / 127f` with a call to a new `public static float VelocityToGain(int velocity)`. Update XML doc summaries (class + `StartVoice`) to say "concave" rather than "linear." | The velocity→gain characteristic; pitch-increment computation. | Gain smoothing (that is `GainRamp`), envelope, per-channel mix gain (that is `MidiSequencer`/`Synthesizer`). |
| Test project | Add a unit test for `VelocityToGain` (concavity + endpoints). Add a velocity-dynamics render-proof test paralleling `MixBusRenderProofTests`. | Verifying the curve shape and the audible dynamic-range widening. | — |

No other production file changes. `Sf2Patch`, `Sf2RegionResolver`, `SamplePlaybackVoice`, `GainRamp`, `Synthesizer`, `MidiSequencer` are untouched.

## 7. Interactions & Data Flow

Unchanged from today except the value of `targetGain`. The call sequence for a note:

1. `Synthesizer.NoteOn` → `channelPatch[ch].StartVoice(key, velocity)`.
2. For SF2: `Sf2Patch.StartVoice` resolves the region (key **and** velocity, §5.1), then delegates to a cached `SamplePatch.StartVoice(key, velocity)`.
3. `SamplePatch.StartVoice` computes `targetGain = VelocityToGain(velocity)` and constructs the voice.
4. `SamplePlaybackVoice` slews `GainRamp` toward `targetGain` (5 ms) and multiplies it into the per-frame envelope × tremolo product.

The curve applies **once per note**, at construction, to the ramp target. It is orthogonal to and composes cleanly with the per-channel CC7/CC11 mix gain (applied later, in `Synthesizer.Read`).

## 8. Contracts & Interfaces (Abstract)

**`VelocityToGain` — velocity→linear-gain characteristic**

| Property | Contract |
|---|---|
| Input | MIDI velocity, integer 0–127. |
| Output | Linear gain, float in [0, 1]. |
| Shape | Concave: attenuates the soft end more than a linear reading. Specifically `gain = (velocity/127)²`. |
| Invariant — endpoints | `VelocityToGain(0) = 0` (silence); `VelocityToGain(127) = 1` (unity). |
| Invariant — monotonic | Strictly non-decreasing in velocity. |
| Invariant — concavity proof point | `VelocityToGain(64) = (64/127)² ≈ 0.254` — well below the linear 0.504, and below the 0.5 threshold named in the deliverable proof. |
| Purity | Pure, allocation-free, deterministic, no dependency on sample rate, region, or engine state. |

The interface widens `SamplePatch`'s public surface by one pure static utility. This is deliberate — see §9.3 for why a named public method beats both an inlined expression and an `internal` + `InternalsVisibleTo` variant here.

## 9. Quality Attributes & Trade-offs

### 9.1 Curve choice: `(velocity/127)²` vs. SF2-accurate dB attenuation

This is the one real design decision. Two candidates:

**(A) Square law — `gain = (velocity/127)²`  ◄ CHOSEN**

**(B) SF2-accurate — velocity drives `initialAttenuation` via the SF2 2.04 default concave modulator (960 cB, i.e. up to −96 dB across the velocity range), converted to linear gain.**

| Criterion | (A) Square law | (B) SF2-accurate dB |
|---|---|---|
| Concavity / "soft notes genuinely soft" | Yes — velocity 64 → 0.254 (−11.9 dB), 6 dB more attenuation than linear. Fully meets the goal. | Yes — velocity 64 → ~0.25–0.30 depending on the exact concave table. |
| Audible difference from (A) for the stated goal | — | Marginal; both restore note-to-note dynamics indistinguishably for this use. |
| Consistency (DRY) | **Reuses the exact `(x/127)²` shape already shipped for CC7/CC11** (`MidiSequencer.ChannelGain`). One amplitude-curve vocabulary across the engine. | Introduces a second, different amplitude-curve mechanism (centibel attenuation) alongside the square law used for CC. |
| New magic numbers | None. | A 960 cB attenuation constant + a `Math.Pow(10, −att/200)` conversion + a velocity-0 guard. |
| KISS | One multiply. | A transcendental per note + a spec-table decision. |

**Decision: (A).** Per Design Contracts §1 (KISS/DRY/YAGNI) and the brief's explicit steer ("prefer consistency/simplicity unless SF2-accuracy matters audibly"), the square law wins decisively: it is the established codebase shape (DRY with PR 12), needs no new constants or transcendental math (KISS), and fully achieves the audible goal. The SF2-accurate curve is **YAGNI for the stated requirement** — its extra fidelity is inaudible for "make soft notes soft," and it would add a parallel amplitude-curve mechanism.

**Named downside of (A), per §4 discipline:** if a future side-by-side against a reference SF2 synth reveals the square law's soft-end is audibly wrong (probability: low; the square law is the widely-used perceptual approximation of the SF2 concave default), promoting to the true SF2 concave table is a bounded, well-scoped follow-up — the engine already has a centibel→linear helper (`Sf2RegionResolver.CentibelsToLinear`) to build on. We do not build that speculatively now; the present cost (a parallel curve mechanism) is real and the future need is hypothetical.

### 9.2 Where the curve applies

Applying at `SamplePatch.StartVoice` (voice construction), not per-frame, is correct and cheapest: velocity is fixed for a note's lifetime, so the mapping is a once-per-note scalar. It composes with the downstream `GainRamp` (click-free onset) and envelope without interaction. Rejected alternative: applying inside `SamplePlaybackVoice` per frame — pointless recomputation of a constant.

### 9.3 Named public method vs. inlined expression vs. internal+InternalsVisibleTo

The deliverable requires a unit test asserting the curve shape. Options:

- **Inlined private expression** (`targetGain = (velocity/127f)*(velocity/127f)`): simplest production form, but the exact curve is only testable through a brittle behavioral render (settle the gain ramp + envelope, measure steady-state amplitude ratio). Tests more than the curve.
- **`internal static` + `[InternalsVisibleTo]`**: directly testable, but adds an assembly-level attribute — new machinery for one test.
- **`public static VelocityToGain`  ◄ CHOSEN**: directly and exactly unit-testable with **zero** new assembly machinery; names the concept; mirrors the established `MidiSequencer.ChannelGain` named-formula pattern. `SamplePatch` is already a public library class, and `VelocityToGain` is a pure, documented utility — a coherent, minimal surface addition, comparable to the public constants already exposed across the synthesis types (`GainRamp.DefaultSmoothingSeconds`, `FilterParameters.Sf2OpenCutoffHz`).

The one caller-in-production + one caller-in-test shape is acceptable here because the method names a real domain concept and is the least-machinery path to the required test. This is an explicit §4 "does the abstraction earn its keep" call: yes — via testability + concept-naming + pattern-consistency, not "cleanliness."

### 9.4 Other attributes
- **Performance:** one extra multiply per note-on; negligible, and off the per-sample hot path.
- **Maintainability:** the engine now has two named concave amplitude curves (`VelocityToGain`, `ChannelGain`) with the identical `(x/127)²` shape — easy to reason about together.
- **Backward compatibility:** none required (private monorepo, atomic deploy). Existing renders are simply superseded by the new dynamics.

## 10. Risks & Mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Overall mix now perceptibly quieter (every note attenuated more at the soft/mid end), sounding "too soft." | Medium | Low | The concave curve only pulls down soft/mid velocities; velocity 127 still maps to unity. Loud notes are unchanged; the *range* widens. If the whole mix reads too quiet, that is a master-gain concern (PR 12 territory), not a reason to flatten the velocity curve. Deliverable-proof render confirms the intended widening, not a blanket drop. |
| A regression test elsewhere assumed linear velocity gain. | Low | Low | Grep shows only the amplitude-envelope design doc references `velocity/127` prose (historical, correctly deferred this work); no test asserts the linear value. Full `dotnet test` gate catches any hidden assumption. |
| Square law audibly under-shoots a reference synth at the soft end. | Low | Low | Documented bounded follow-up (§9.1); not built speculatively. |

## 11. Migration / Rollout Strategy

None required. Single atomic change; no data, no config, no deprecation window (Design Contracts §5 — no transition window in a private-monorepo + atomic-deploy environment). The new curve takes effect on next render.

## 12. Deliverable Proof

1. **Unit test** (`VelocityToGain`): assert `VelocityToGain(127) == 1`, `VelocityToGain(0) == 0`, `VelocityToGain(64) < 0.5` (and ≈ 0.254), and monotonicity across a few sample points.
2. **Render-proof test** (parallel to `MixBusRenderProofTests`, skips gracefully when dev-tree assets absent): render `1-02-Balamb_Garden.mid` through Florestan; assert the **dynamic range widened** — the ratio of a loud-passage RMS/peak to a soft-passage RMS/peak is greater than it would be under a linear map. A robust, render-internal formulation: assert peak ≤ 1, non-silent, and that the loud/soft amplitude spread exceeds a concrete threshold derived from the concave curve.
3. **A/B re-render:** re-render Balamb to `renders/song-ff8-balamb-VEL.wav` via `tools/Pooshit.AudioSynth.MidiRender` and report the measured soft-vs-loud dynamic-range delta against the existing `renders/song-ff8-balamb-MIX.wav`.

(`renders/` is git-ignored — the WAVs are local proof artifacts, not committed.)

## 13. Open Questions

1. **Velocity-split resolver test.** §5.1 notes there is no explicit unit test for a velocity-split SF2 instrument, though the resolution logic exists and works. Recommend a separate small test-hardening task (behavior-neutral). Bundling it here would break one-feature-per-PR. **Filed as a follow-up task if you agree.**
2. **Master loudness perception.** If, after the curve lands, the overall render feels too quiet (risk in §10), that is a master-gain tuning question, not a velocity-curve question — flag for a possible mix-bus follow-up rather than softening this curve.

## 14. Implementation Guidance for the Next Agent

Small, ordered work breakdown (all on branch `feature/velocity-curve`, one PR):

1. **`SamplePatch.cs`:** add `public static float VelocityToGain(int velocity)` returning `(velocity / 127f) * (velocity / 127f)`. Replace line 36 (`float targetGain = velocity / 127f;`) with `float targetGain = VelocityToGain(velocity);`. Update the class-level `<summary>` and the `StartVoice` `<summary>`/`<param name="velocity">` doc to say "concave (velocity/127)² velocity-to-gain mapping" instead of "linear." No body comments (Code Contracts §6.10 grep-0). One type per file (already satisfied).
2. **Unit test:** add `SamplePatchVelocityCurveTests` (one type per file) asserting the §12.1 properties against `SamplePatch.VelocityToGain`.
3. **Render-proof test:** add a velocity-dynamics proof test paralleling `MixBusRenderProofTests` (§12.2), asset-guarded with `Assert.Ignore` when the dev tree is absent.
4. **Build gate:** `dotnet build -c Release` for both TFMs, 0 warnings; `dotnet test` green.
5. **A/B render:** run `MidiRender 1-02-Balamb_Garden.mid __Florestan_Basic_GM_GS.sf2 renders/song-ff8-balamb-VEL.wav`; measure and report the soft/loud dynamic-range delta vs `song-ff8-balamb-MIX.wav`.
6. **Commit** design doc + code together; open one PR (design + increment bundled, per #1165). Do not merge.

---

*Author: sarah-software-architect. Design + implementation ship in one PR (#1165). Contracts cited: #1136, #114, #1165.*
