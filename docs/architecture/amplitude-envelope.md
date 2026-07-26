# Architectural Document: Amplitude (Volume) Envelope — ADSR Voice Gain Shaping

> DiVoid: design node filed under project #6128, linked to task **#7062**. Repo path of record:
> `docs/architecture/amplitude-envelope.md`. Load-bearing contracts: **Code Contracts #114 §0**
> (KISS/DRY/YAGNI, §4 comments, §5.5 XML-docs) and **Design Contracts #1136** (§1–§4 + the §5
> Pre-Design Checklist, reproduced verbatim at the end of this document).

## 1. Problem Statement

Through PR 5 every sounding note is shaped only by a **`GainRamp`** — a 5 ms per-frame slew that
was introduced to defend **INV-1 (no zipper)**, and which happens to double as a crude amplitude
contour: it glides `0 → velocityGain` at note-on and `velocityGain → 0` at note-off. This is not a
musical envelope. It has no decay, no sustain level, no delay/hold, and its release time is a fixed
5 ms rather than the value the SoundFont author specified. Worse, the offline render demo
(`tools/Pooshit.AudioSynth.RenderDemo`) **never calls `NoteOff`**: it renders a fixed number of
frames of a continuously-looping note and stops, so the note is cut at full sustain amplitude,
producing a hard full-scale discontinuity — the **click** the user heard in the PR-5 smoke render.

**Goal:** give every voice a proper **DAHDSR volume envelope** (delay, attack, hold, decay,
sustain, release) sourced from the SF2 volume-envelope generators where present, with SF2-spec
defaults where absent, so notes fade in on attack and fade out on release instead of clicking; and
make `NoteOff` trigger a release tail after which the voice reports inactive so the pool reclaims it.

**Success criteria (verbatim from task #7062):** re-render the same Florestan note from the PR-5
smoke test; the WAV must show a smooth amplitude ramp at onset and a release fade at note end (no
full-scale discontinuity at the edges), verifiable by (a) a unit test asserting the envelope's
boundary samples ramp rather than jump, and (b) an audible before/after.

## 2. Scope & Non-Scope

**In scope (ONE feature, ONE PR):**

- A per-frame **`AmplitudeEnvelope`** (DAHDSR) that produces the note's amplitude contour `[0..1]`.
- Its integration into `SamplePlaybackVoice`'s render path.
- SF2 **volume**-envelope generator mapping (delay 33 / attack 34 / hold 35 / decay 36 /
  sustain 37 / release 38) → envelope parameters, with **SF2-spec defaults** where a generator is
  absent, resolved in `Sf2RegionResolver` (the single home for SF2 interpretation).
- Release-tail → voice-inactive lifecycle so the pool reclaims released voices after the tail.
- The render demo updated to schedule a `NoteOff` so the release fade is demonstrable.
- Regression tests for the class-B (click/zipper) defect family from catalog #6272.

**Explicitly out of scope (separate later PRs — do NOT bundle):**

- Biquad filter, LFO, the parameterised generator-loop DRY refactor.
- A **modulation** envelope (as distinct from the volume envelope) — generators 25–32.
- Initial attenuation (48), pan (17), key-number-to-hold/decay (39/40) scaling, exclusive class,
  all modulators — consistent with the existing v1 generator subset (concept #6757).
- Velocity → envelope-time / velocity → attenuation curves beyond the existing linear
  velocity → gain (`velocity / 127`).
- Exponential (dB-linear) stage curves — v1 stages are **linear** (see §10, §11).
- The MathF/SIMD fast-path and the netstandard2.0 MathF polyfill — **not pulled in**; the envelope
  needs no per-sample transcendental math (§10). This is a deliberate YAGNI deferral, justified below.
- One-shot (NoLoop) sample-exhaustion declick — the Florestan test note loops continuously and ends
  only via release; hard-stop-on-exhaustion is a pre-existing, separate concern (§11 R4).

## 3. Assumptions & Constraints

| # | Assumption / Constraint | Confidence |
|---|---|---|
| A1 | The Florestan preset[0] instrument uses a continuous loop (SampleModes 1/3), so the rendered note never self-exhausts and ends only via release. | High — concept #6757 + PR-5 render behaviour. |
| A2 | SF2 volume-envelope generators (33–38) are already parsed and reachable on instrument zones via `Sf2Zone.Generators`; the enum members already exist (`Sf2GeneratorType.DelayVolumeEnvelope`…`ReleaseVolumeEnvelope`). | High — verified in `Sf2GeneratorType.cs`. |
| A3 | `System.Math.Pow` is available on **both** target frameworks (netstandard2.0 + net8.0); `SamplePatch` already uses it. `MathF` is **not** used. | High — verified. |
| A4 | Engine invariants INV-1 (per-frame zipper-free gain; block size never an input) and INV-2 (every output frame passes `Synthesizer.Finalize`) must remain intact. | Hard constraint (brief + concept #6756). |
| A5 | v1 sources the volume envelope from the **instrument** zone (local, then global instrument zone) — preset-level generator addition is not implemented, matching the existing resolver behaviour for every other generator. | High — concept #6757; mirrors `BuildRegion`. |

## 4. Architectural Overview

The change adds **one new value type**, **one new runtime type**, and **one new enum**, and touches
the voice render loop, the SF2 resolver, the region descriptor, and the render demo. No new service,
no new abstraction layer, no signature change to `IVoice` / `IPatch` / `SamplePatch` / `Sf2Patch`.

```
NoteOn(key,vel)
   │
   ▼
Sf2Patch.StartVoice ── resolver.TryResolve ──► SampleRegion  (now also carries EnvelopeParameters,
   │                                             │             computed once per zone from the SF2
   │  (cached SamplePatch per zone)              │             volume-env generators + defaults)
   ▼                                             │
SamplePatch.StartVoice ───────────────────────► SamplePlaybackVoice(region, incr, velGain, rate)
                                                   │  builds:  gainRamp   (velocity scalar, INV-1 seam)
                                                   │           envelope   = AmplitudeEnvelope(region.Envelope, rate)
                                                   ▼
                       per frame:  gain = envelope.AdvanceFrame() × gainRamp.AdvanceFrame()
                                   block[i] = sample × gain
                                   Release()  → envelope.Release()  (NOT gainRamp)
                                   IsActive   → envelope not Finished
                                                   │
                                                   ▼
                       Synthesizer.Read → mix → Finalize (INV-2, unchanged)
```

**The one-sentence relationship (mandated by the brief):** the **`AmplitudeEnvelope`** is the note's
intended amplitude *contour* over its life (DAHDSR); **`GainRamp`** is the zipper-free per-frame
*slew of the voice's scalar gain* (velocity in v1; MIDI CC volume/expression later). They are
orthogonal per-frame gain sources and the voice multiplies them. Because both advance one frame at a
time with block size never an input, the product is zipper-free by construction — **INV-1 is
preserved, not weakened.**

## 5. Components & Responsibilities

### 5.1 `EnvelopeParameters` (new — immutable value type, `Synthesis/EnvelopeParameters.cs`)

- **Owns:** the six DAHDSR parameters in **rate-independent, SF2-unit-free** form — delay, attack,
  hold, decay times in **seconds**; sustain as a **linear level `[0..1]`** (1 = peak); release in
  seconds.
- **Owns:** a `Default` matching the SF2 spec defaults (times ≈ 0.977 ms from −12000 timecents;
  sustain = 1.0 / 0 cB). This is the single source of the default contour — DRY, no invented numbers.
- **Does NOT own:** SF2-unit interpretation (timecents / centibels), sample-rate awareness, or any
  runtime state. It is a plain descriptor.

### 5.2 `EnvelopeStage` (new — enum, `Synthesis/EnvelopeStage.cs`)

- The seven lifecycle stages: `Delay, Attack, Hold, Decay, Sustain, Release, Finished`. Nothing else.

### 5.3 `AmplitudeEnvelope` (new — runtime state machine, `Synthesis/AmplitudeEnvelope.cs`)

- **Owns:** the per-note progression through the stages and the current level `[0..1]`. A mutable
  struct held in a voice field and advanced in place (same zero-alloc idiom as `GainRamp` / the
  footgun note in concept #6756: never copied by value).
- **Owns:** conversion of `EnvelopeParameters` (seconds) into per-frame linear increments at
  construction, given the output sample rate — exactly as `GainRamp` derives `maxStepPerFrame`.
- **Responsibilities (contract in §8):** `AdvanceFrame()` returns the current level and advances one
  frame; `Release()` transitions to the Release stage *from the current level* (so a note released
  mid-attack fades from wherever it was, no jump); `IsFinished` is true once the Release stage has
  reached zero.
- **Does NOT own:** velocity scaling, panning, the sample read, or NaN/clamp (that is `Finalize`).

### 5.4 `SampleRegion` (modified — `Synthesis/SampleRegion.cs`)

- **Gains one property:** `EnvelopeParameters Envelope`. The region is already the immutable,
  per-zone "everything needed to play this note" descriptor the resolver builds and `SamplePatch`
  caches; the volume envelope is resolved from the same zone and travels with it. Per Design
  Contracts §4 ("can the new field live on an existing descriptor instead of a new sub-object threaded
  separately?") this is the merge-with-existing choice, chosen over a parallel sibling that would
  otherwise be threaded through `TryResolve`, `Sf2Patch`, `SamplePatch`, and the voice ctor (and break
  18 existing `TryResolve` test call-sites). See §11 T1 for the trade-off.
- Constructor gains one parameter (`envelope`); the three construction sites are updated
  (`Sf2RegionResolver.BuildRegion` supplies the resolved envelope; the two DC-region test helpers
  supply `EnvelopeParameters.Default`).

### 5.5 `SamplePlaybackVoice` (modified — `Synthesis/Voices/SamplePlaybackVoice.cs`)

- **Gains:** an `AmplitudeEnvelope` field, built in the ctor from `region.Envelope` + the sample rate
  it already receives. **No ctor signature change.**
- **Render loop change:** per-frame gain becomes `envelope.AdvanceFrame() × gainRamp.AdvanceFrame()`.
- **`Release()` change:** calls `envelope.Release()` **only** — it no longer retargets `gainRamp` to
  zero. The envelope is now the single owner of the release contour (so the SF2 release time is
  honoured instead of a fixed 5 ms).
- **`IsActive` change:** deactivates when `envelope.IsFinished` (release reached zero) — generalising
  the current `released && gain == 0` condition. The one-shot `sampleExhausted && !released`
  deactivation path is retained unchanged (§11 R4).

### 5.6 `Sf2RegionResolver` (modified — `Formats/Sf2/Sf2RegionResolver.cs`)

- **Gains:** a private `BuildEnvelopeParameters(zone, globalZone)` that reads generators 33–38 using
  the **existing** `GetEffectiveInt16` local-then-global-zone helper (DRY — same fallback already used
  for FineTune/CoarseTune), converts timecents → seconds and centibels → linear sustain, and returns
  an `EnvelopeParameters`. `BuildRegion` passes the result into the `SampleRegion` ctor.
- **`TryResolve` signature is unchanged** (envelope rides on the region it already returns).

### 5.7 `RenderDemo/Program.cs` (modified — deliverable-proof tool)

- Schedules a `NoteOff` before the render ends (hold for most of the duration, then release, then
  render the release tail) so the WAV exhibits the release fade instead of a full-scale cut. This is
  the only way the continuously-looping Florestan note can demonstrate a release edge.

## 6. Interactions & Data Flow

**Resolution (once per zone, cached):** `Sf2Patch.StartVoice → resolver.TryResolve → BuildRegion`.
`BuildRegion` now also calls `BuildEnvelopeParameters`, folding the six volume-env generators into an
`EnvelopeParameters` carried on the returned `SampleRegion`. The per-zone `SamplePatch` cache is
unchanged: the envelope is zone-stable, so it is computed once and reused for every note on that zone.

**Note-on (per note):** `SamplePatch.StartVoice` constructs `SamplePlaybackVoice`, which builds its
`AmplitudeEnvelope` from `region.Envelope` + rate. The envelope starts in `Delay` at level 0.

**Per block (per frame):** the voice multiplies the interpolated sample by
`envelope.AdvanceFrame() × gainRamp.AdvanceFrame()`. Both are per-frame; block size is never an input
→ INV-1 holds. The mixed frame still passes through `Finalize` → INV-2 holds.

**Note-off:** `Synthesizer.NoteOff → voice.Release() → envelope.Release()`. The voice stays active
through the release tail. When the envelope reaches `Finished`, `IsActive` flips false and
`Synthesizer.Read` reclaims the slot on its existing `if (!slot.Voice.IsActive)` check.

## 7. Data Model (Conceptual)

| Entity | Fields (conceptual) | Owner | Lifetime |
|---|---|---|---|
| `EnvelopeParameters` | delay/attack/hold/decay/release (seconds), sustain (linear `[0..1]`) | resolved by `Sf2RegionResolver`; default from `EnvelopeParameters.Default` | immutable; per-zone, cached on `SampleRegion` |
| `AmplitudeEnvelope` | current stage, current level, per-frame step per stage, frames-remaining-in-stage | one per voice | mutable; per-note, lives inside the voice |
| `SampleRegion.Envelope` | reference to the zone's `EnvelopeParameters` | region | per-zone |

**SF2 unit conversions (computed once per zone, in the resolver):**

- **Timecents → seconds:** `seconds = 2^(timecents / 1200)`, via `Math.Pow(2.0, tc / 1200.0)`.
  SF2-spec default when the generator is absent: **−12000 tc ≈ 0.977 ms**. Clamped to a sane maximum
  (envelope times above ~20 s are treated as 20 s) to avoid pathological input producing
  never-ending stages.
- **Sustain centibels → linear level:** `level = 10^(−centibels / 200)` (centibels of attenuation
  below peak). Default 0 cB → 1.0 (peak). Clamped: cB ≤ 0 → 1.0; cB ≥ 1440 → 0.0.

## 8. Contracts & Interfaces (Abstract)

**`AmplitudeEnvelope` (per-frame gain source):**

| Operation | Input | Output | Semantics / Invariant |
|---|---|---|---|
| construct | `EnvelopeParameters`, `sampleRate` | envelope in `Delay` at level 0 | pre-computes per-stage per-frame linear step from seconds × sampleRate |
| `AdvanceFrame()` | — | level `[0..1]` for this frame | advances exactly one frame; monotone-non-decreasing until the peak of Attack; non-increasing once in Decay/Release; **block size is never an input** (INV-1) |
| `Release()` | — | — | transitions to `Release` starting from the **current** level; idempotent if already releasing/finished; a mid-attack release fades from the current level (no jump) |
| `IsFinished` | — | bool | true once the `Release` stage has driven the level to 0 |

**`EnvelopeParameters`:** immutable; `Default` = SF2-spec defaults. No behaviour.

**`SampleRegion`:** gains read-only `Envelope`; all existing invariants (start/end/loop validation)
unchanged.

**Composition invariant (voice):** `frameGain = envelope.AdvanceFrame() × gainRamp.AdvanceFrame()`,
both `[0..1]` per-frame ⇒ `frameGain ∈ [0..1]` and free of block-boundary steps. The release contour
is owned solely by the envelope; `gainRamp` is never retargeted to zero on release.

## 9. Cross-Cutting Concerns

- **INV-1 (no zipper):** preserved. The envelope is a per-frame generator (like `GainRamp`); their
  product is per-frame continuous. The existing `GainGlideTests` (which exercise the full voice path,
  not `GainRamp` directly) continue to pass and now additionally pin the envelope's zipper-freeness.
- **INV-2 (NaN/Inf-safe):** untouched. The envelope produces finite `[0..1]` values; `Finalize`
  remains the single choke point.
- **Portability (netstandard2.0 + net8.0):** no `MathF`, no `System.Numerics` SIMD, no polyfill. The
  only transcendental calls are per-zone `Math.Pow` in the resolver (netstandard2.0-safe). Per-frame
  work is integer counting + float addition.
- **Allocation:** zero steady-state allocation preserved. `EnvelopeParameters` is a value type carried
  on the already-allocated region; `AmplitudeEnvelope` is a mutable struct field on the voice (no heap
  churn per note beyond the voice itself, which already allocates).
- **Concurrency:** unchanged; voices remain single-threaded within `Read`.

## 10. Quality Attributes & Trade-offs

**Why linear stages (not exponential/dB-linear).** SF2 attack is convex and decay/release are
dB-linear in the strict spec. v1 uses **linear** stages because: (a) linear per-frame stepping needs
only float addition — **no per-sample `exp`/`pow`**, which is precisely what lets the netstandard2.0
MathF-polyfill and SIMD fast-path defer cleanly (a stated non-goal); (b) for the declick goal, the
perceptual difference between a linear and an exponential 1–200 ms fade is minor; (c) it is the
smallest thing that delivers "the audible envelope." Exponential curves are recorded as an explicit
future refinement, not a v1 requirement. **YAGNI: the polyfill/SIMD work is not pulled in because the
envelope demonstrably does not need it.**

**Why the envelope needs no per-sample transcendental math** (the decision the brief left to the
architect): the only non-linear maths are the timecents→seconds and centibels→linear conversions,
which are **per-zone, once**, in the resolver, using `Math.Pow` (available on both TFMs). The
per-frame hot path is addition and comparison. Therefore the fast-path/polyfill bundle **defers**.

**Trade-offs made explicit (§4 exercise):** see §11 T1 (envelope-on-region vs sibling) and §11 T2
(retain `GainRamp` vs delete it) — each names the downside, its probability/cost, and the call.

## 11. Risks & Mitigations · Trade-offs

**T1 — Envelope on `SampleRegion` vs a separately-threaded sibling.**
*Downside of chosen (on region):* `SampleRegion`'s responsibility broadens slightly from
"sample geometry + pitch" to "…+ amplitude contour." *Downside of the alternative (sibling):* a new
out-parameter on `TryResolve` (breaking 18 existing test call-sites) plus new ctor parameters on
`Sf2Patch` and `SamplePatch`, threading a parallel value that always travels with the region anyway.
*Call:* on the region. Design Contracts §4 explicitly prefers merging a field onto an existing
descriptor over a new sub-object threaded separately; the region and the envelope are both products of
the same zone resolution and are cached together. Cost of the broadening is one property; cost avoided
is four signature changes + 18 call-site edits.

**T2 — Retain `GainRamp` in the voice vs delete it.**
With the envelope owning the note's onset and release contour, and velocity constant in v1,
`GainRamp`'s only live action is the initial `0 → velocityGain` glide; it no longer owns release.
*Option A (retain, chosen):* keep `gainRamp` as the velocity-scalar slew and INV-1 seam; the voice
gain is `envelope × gainRamp`. *Downside:* the ~5 ms `gainRamp` onset glide floors the **effective**
minimum attack at ~5 ms, so an SF2 instrument specifying a sub-5 ms attack is softened to ~5 ms;
attacks slower than 5 ms (the musically interesting swells/pads/strings) are honoured fully. In v1
`gainRamp` performs exactly one dynamic action per note (the onset glide) — it is not dead code.
*Option B (delete `GainRamp`, fold velocity to a plain float):* buys sub-5 ms attack fidelity but
orphans/removes the documented INV-1 seam (map #6731 + two design docs), expands this envelope-only PR
into a `GainRamp` removal + doc supersession, and discards the smoother that MIDI CC volume/expression
will need. *Call:* Option A. The brief explicitly frames `GainRamp` as a live, distinct concern to be
related to — not removed — and the ~5 ms attack floor is below the audible-envelope threshold for the
slow attacks that matter. The sub-5 ms fidelity gap is filed as a follow-up to revisit when the CC
volume path (which restores `GainRamp`'s dynamic role) lands.

**R1 — Class-B declick regression (catalog #6272 §B).** *Mitigation:* encode as tests first — an
envelope-boundary unit test (attack/release ramp, not jump) and a full-voice declick test (bounded
consecutive-sample delta across the note-off edge). These are the primary deliverable proof.

**R2 — Existing convergence tests assume release reaches zero within ~500–600 frames.**
`NoteOffReleasesVoiceIntoSilence` and `GainGlideConvergesToZeroAfterRelease` render ≤600 frames after
`NoteOff`. *Mitigation:* the non-SF2 default release (SF2-spec ≈ 1 ms) converges in ~44 frames; the
constraint is documented so no future default-release change silently breaks these windows.

**R3 — Existing monotonic note-on test.** `GainGlideIsMonotonicDuringNoteOn` asserts non-decreasing
gain during note-on with the default (non-SF2) envelope. *Mitigation:* the default sustain is
**peak** (SF2 default 0 cB), so the default contour has no decay drop — attack→hold→decay(no
change)→sustain is monotone non-decreasing. (An SF2 patch with sustain < peak *will* dip after
attack; that path is not covered by this test, which uses `SamplePatch` defaults.)

**R4 — One-shot sample-exhaustion hard-stop.** A NoLoop sample that exhausts mid-note before release
still hard-stops (pre-existing behaviour). *Mitigation:* out of scope and explicitly noted — the
Florestan test note loops continuously and never exhausts. Not introduced or worsened by this change.

**R5 — Florestan instrument with a slow attack lowering early peak.** If preset[0]'s instrument has a
slow attack, the first ~100 ms are quieter. *Mitigation:* the existing Florestan integration test
threshold is a very low `peak > 0.01`; a slow-attack envelope still clears it. Noted, not blocking.

## 12. Migration / Rollout Strategy

Private repo, atomic deploy, no consumers of an intermediate state — **no phased rollout, no feature
flag, no compatibility shim.** The whole feature is one cohesive increment shipped in one PR
(design + implementation), per Design Contracts #1165. There is no design-only PR.

## 13. Open Questions

1. **Default release time.** v1 uses the SF2-spec default (≈1 ms) for absent generators. A ~5 ms
   default would match the previous `GainRamp` release and add declick margin, at the cost of
   deviating from the SF2 spec for absent-generator SF2 content. v1 keeps the spec default; flag for
   the reviewer if 1 ms proves audibly clicky on the DC-source tests.
2. **Sub-5 ms attack fidelity** (T2) — accepted for v1; revisit with the CC-volume path.

Neither blocks implementation; both are recorded for the reviewer.

## 14. Implementation Guidance for the Next Agent

Build in this order (each step compiles and keeps the suite green):

1. **`EnvelopeStage` enum** + **`EnvelopeParameters`** value type (with `Default` = SF2-spec defaults).
   One type per file; XML `<summary>` on every public member.
2. **`AmplitudeEnvelope`** state machine (seconds → per-frame steps at construction; `AdvanceFrame`,
   `Release`, `IsFinished`). Unit-test in isolation first: stage durations, boundary-sample ramp (not
   jump) at attack start and release start, mid-attack release fades from current level, `IsFinished`
   timing.
3. **`SampleRegion`** — add `Envelope` property + ctor parameter; update the two DC-region test
   helpers to pass `EnvelopeParameters.Default`.
4. **`SamplePlaybackVoice`** — build the envelope in the ctor; change the per-frame gain to
   `envelope × gainRamp`; `Release()` → `envelope.Release()`; `IsActive` → not `envelope.IsFinished`.
5. **`Sf2RegionResolver`** — `BuildEnvelopeParameters` (reads gens 33–38 via `GetEffectiveInt16`,
   converts units) wired into `BuildRegion`. Add resolver tests: generator present → mapped seconds/
   level; generator absent → SF2-spec default; local-overrides-global for a vol-env generator.
6. **`RenderDemo/Program.cs`** — schedule `NoteOff` partway so the release fade is rendered.
7. **Regression/declick tests** (class-B, #6272 §B): full-voice onset ramp and note-off release fade
   with bounded consecutive-sample deltas; keep all existing tests green.
8. **Verify:** `dotnet build` both TFMs (0 warnings), `dotnet test` green, re-render the Florestan
   note, confirm the WAV ramps at onset and fades at note-off.

**Do not:** add exponential curves, a modulation envelope, initial-attenuation/velocity-attenuation
scaling, the MathF polyfill, or any config knob for envelope times (SF2 generators + spec defaults are
the source of truth; §3 Design Contracts — magic numbers stay magic).

---

## Appendix — Design Contracts #1136 §5 Pre-Design Checklist (verbatim)

**KISS / DRY / YAGNI**
- [x] No new type whose value-space substantially mirrors an existing type. `EnvelopeParameters`,
  `AmplitudeEnvelope`, `EnvelopeStage` have no existing counterpart; they do not mirror `GainRamp`
  (a single-target slew), they compose with it.
- [x] No new abstraction with only one implementation and no concrete plan for a second. No new
  interface is introduced; the envelope is a concrete type used directly by the voice.
- [x] No design element justified by "we might need X later." The polyfill/SIMD and exponential
  curves are *deferred*, not stubbed; nothing is built for them now.
- [x] No deprecation period / feature flag / compatibility shim / transition window. §12.
- [x] For every "inline vs extract" decision: the unit conversions are extracted into named private
  helpers (`BuildEnvelopeParameters`, timecents/centibels conversion) — reused across the six
  generators; no multi-line block is inlined at multiple sites.

**Existing systems first**
- [x] Audited whether an existing type covers the concern: `GainRamp` was assessed (T2) and retained;
  the envelope is a genuinely different thing (multi-stage contour vs single slew).
- [x] New field justified on the existing descriptor: `SampleRegion.Envelope` rides the existing
  per-zone region rather than a new threaded sibling (§5.4, T1) — the §4 merge-with-existing choice.
- [x] No new persisted data (in-memory engine; N/A).
- [x] Consumer chain is real: `SampleRegion.Envelope` → `AmplitudeEnvelope` → per-frame gain → audible
  output. Not a dump.

**Configurability**
- [x] No new config knob. Envelope times/levels come from SF2 generators; defaults are SF2-spec
  constants named clearly (`EnvelopeParameters.Default`, timecent/centibel defaults) — magic numbers
  stay `const`/`static readonly`, no environment/operator variance.

**Less is better**
- [x] can-it-be-deleted / merged / inlined applied: envelope merged onto region; no new interface;
  no signature change to `IVoice`/`IPatch`/`SamplePatch`/`Sf2Patch`.
- [x] Trade-offs named explicitly where a simpler alternative was rejected: T1, T2, §10 linear-vs-exp.
- [x] Where an existing surface had no consumer for a sibling shape, the merge-onto-existing (radical
  clean) choice was taken over a parallel threaded value (T1).

**Data deliverables (SQL/migrations):** N/A — no schema, no SQL.

**Document discipline**
- [x] Cites Code Contracts #114 and Design Contracts #1136 as load-bearing (header).
- [x] Scope inventories explicit (§2 in/out); construction-site inventory explicit (§5.4 — 3 sites).
- [x] Out-of-scope items listed explicitly (§2), not merely absent.
- [x] No superseded predecessor design (this is the first envelope design; nothing to banner).
