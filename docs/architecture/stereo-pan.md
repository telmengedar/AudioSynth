# Architectural Document: Stereo Pan — CC10 channel pan + per-voice SF2 pan (gen 17)

**Author:** Sarah · **Date:** 2026-07-27 · **Source task:** DiVoid #7127 (PR 12b) · **Project:** #6128 · **Map root:** #6708 · **Roadmap:** #7098 "PR 12b pan"
**Builds on:** Mix-bus precedent #7126 (per-channel-state + `SetChannelGain` seam + `GainRamp` + master soft-clip), MidiSequencer #7114, ISynthesizer #6726, Synthesizer #6734, SampleRegion #6736, Sf2RegionResolver #6752.
**Load-bearing contracts:** Code Contracts (DiVoid #114) §0/§1 (KISS/DRY/YAGNI), §1 one-type-per-file, §4 comments; Design Contracts (DiVoid #1136) §1–§5; PR-shape (DiVoid #1165).

> **Repo path (source of truth):** `docs/architecture/stereo-pan.md` on branch `feature/stereo-pan` of `telmengedar/AudioSynth` (branched from `origin/main` @ `70c6cbf`, PR 14 merge). This design ships in the **same PR** as the implementation (#1165). The DiVoid documentation node is the graph-discoverable copy.

---

## 1. Problem Statement

Every instrument in the mix is currently summed to the **dead centre** of the stereo field. `Synthesizer.Read` renders each mono voice, scales it by the channel mix gain, then adds the *same* scaled value to **all** output channels with a fixed `panGain = 1/√channels`. The result is a mono signal duplicated across L and R: L and R are bit-for-bit identical, and no instrument has a stereo position.

The composer's spatial intent — carried by **MIDI CC10 (Pan)** per channel and by **SF2 generator 17 (Pan)** per voice — is discarded on two fronts:

- **CC10 (Pan)** arrives on `ChannelCommandType.Controller` messages but `MidiSequencer.ApplyMessage` handles only CC7 (Volume) and CC11 (Expression); CC10 falls through the `else → break` and is dropped.
- **SF2 gen 17 (Pan)** is parsed into `Sf2GeneratorType.Pan` (value 17, confirmed in the enum) but `Sf2RegionResolver.BuildRegion` never reads it, and `SampleRegion` has no `Pan` property to carry it. Per-voice pan is lost at resolve time.

**Goal:** give the mix real stereo placement — instruments spread across the field per CC10 and per-voice SF2 pan — replacing the mono-summed-to-centre output. Success criteria: after this change the L and R channels of a real render **differ** (they were identical), instruments/channels sit at **distinct** stereo positions, a **centred** pan still produces equal L/R (no regression to the centre case), and the master soft-clip + `Finalize` safety path (INV-2) and the allocation-free `Read` hot path are preserved.

**User context (verbatim):** *"pan and stereo sounds good, widens up the character of our perceived sound."* Greenlit as the fast-follow after pitch-bend merged.

---

## 2. Scope & Non-Scope

**In scope**

- **CC10 → per-channel pan** in `MidiSequencer` (MIDI-neutral seam `SetChannelPan(channel, pan)`, pan ∈ [-1,1]; mirrors `SetChannelGain`). GM-reset all 16 channels to centre.
- **SF2 gen 17 → per-voice pan**: plumb generator 17 into a new `SampleRegion.Pan`, surfaced through the voice to the engine (following the Envelope/Filter/Lfo "ride on `SampleRegion`" pattern).
- **Equal-power L/R placement** in the render loop, replacing the fixed centre `panGain` for stereo output. Combined pan = channel pan + region pan, clamped to [-1,1].
- Composition onto the **PR-12 mix-bus**: pan L/R gains multiply *alongside* the existing per-channel `channelGainBlock`; the master soft-clip and `Finalize` choke point are untouched.

**Out of scope** (explicitly)

- Voice-stealing; velocity perceptual curve (PR 13, already merged separately); CC1 mod-wheel / CC64 sustain (#7155); reverb/chorus/effects sends (SF2 gen 15/16); `SoundBank` internals; large-soundfont/Crisis artefacts (#7151/#7152).
- **CC10 fine (CC42 `PanFine`)** — 14-bit pan resolution. Game-MIDI targets use only coarse CC10; a second 7 bits of pan resolution is inaudible in equal-power placement. YAGNI.
- **Pan smoothing / gliding** on mid-note CC10 changes — see §10 for the reasoned trade-off; not built now, seam left forward-compatible.

---

## 3. Assumptions & Constraints

| # | Assumption / Constraint | Confidence |
|---|---|---|
| A1 | Output is **stereo** (`SynthesizerOptions.DefaultChannels = 2`) for all real renders. Equal-power pan is a stereo concept; mono/other channel counts must still render (pan collapses to centre). | High — confirmed in options + render tool. |
| A2 | SF2 gen 17 raw amount is a signed value in the SF2 range **[-500, +500]** (0.1% units per SF2 2.04 §8.1.3: -500 = full left, +500 = full right, 0 = centre). Normalised pan = `raw / 500`, clamped [-1,1]. | High on the spec; see O1 on the enum's doc-comment discrepancy. |
| A3 | CC10 is 7-bit: 0 = hard left, 64 = centre, 127 = hard right. | High — GM standard. |
| A4 | `MidiSequencer` chunks `Read` at event boundaries (`OfflineRenderer.Render(..., gap)`), so a channel-pan change lands at a `Read` boundary; within a `Read` the channel pan is constant. Internal `Read` blocks are `BlockFrames = 64` (~1.45 ms @ 44.1 kHz). | High — confirmed in code. |
| A5 | Offline renderer, not real-time; no per-block latency deadline. Trig cost is bounded by "render a song in reasonable time," not a hard budget. | High. |
| C1 | `Read` must stay **allocation-free** in steady state; all buffers ctor-sized. | Hard invariant. |
| C2 | Master soft-clip + `Finalize` (INV-2) remain the sole NaN/Inf choke and final guard, unchanged. | Hard invariant. |

---

## 4. Architectural Overview

Two additive concerns, cleanly separated by layer, composed in the render loop — exactly mirroring how the mix-bus split "synth = gains" from "sequencer = CC semantics":

```
  SF2 file ──► Sf2RegionResolver ──► SampleRegion.Pan ──► SamplePlaybackVoice.Pan ┐
  (gen 17)     (read raw/500)         (new property)       (surfaces region pan)   │
                                                                                   ▼
  MIDI CC10 ──► MidiSequencer ──► ISynthesizer.SetChannelPan ──► channelPan[16] ─► Synthesizer.Read
                (value→[-1,1])      (MIDI-neutral seam)          (per-channel)      │
                                                                                   ▼
                                          combined = clamp(channelPan[ch] + voice.Pan, -1, 1)
                                          (leftGain, rightGain) = EqualPowerGains(combined)
                                                                                   │
                                          masterSlice[L] += pre · leftGain          ▼
                                          masterSlice[R] += pre · rightGain   ──► ApplyMasterBus ──► Finalize
                                          (pre = mono · channelGainBlock)          (INV-2, unchanged)
```

- **SF2 side (per-voice, static):** the region carries a fixed pan; the voice surfaces it; the engine reads it. Rides on `SampleRegion` like Envelope/Filter/Lfo already do.
- **MIDI side (per-channel, dynamic):** the sequencer owns GM semantics (CC10 → pan value, GM-reset to centre) and calls a MIDI-neutral engine seam that takes a pan ∈ [-1,1], never a CC number — mirroring `SetChannelGain`.
- **Engine side (the mix):** for stereo output, combine the two pan sources per voice, convert once to equal-power L/R gains, and place the voice. The per-channel `channelGainBlock` multiplies through unchanged; the master bus is untouched.

---

## 5. Components & Responsibilities

| Component | Owns (new/changed) | Does NOT own |
|---|---|---|
| **`SampleRegion`** (#6736) | A new immutable `Pan` property (float, [-1,1]) added to the ctor and surface, alongside Envelope/Filter/Lfo. | Any dynamic/channel pan; L/R gain computation. |
| **`Sf2RegionResolver`** | Reading generator 17 in `BuildRegion` and normalising `raw/500` → [-1,1], passing it to the `SampleRegion` ctor. Single home for SF2 interpretation. | The pan law; how pan is mixed. |
| **`IVoice` / `SamplePlaybackVoice`** | A new read-only `Pan` property. `SamplePlaybackVoice.Pan => region.Pan`. Surfaces the static per-voice pan to the engine. | Combining with channel pan; L/R placement (engine's job). |
| **`InactiveVoice`, test `StubVoice`, `NanEmittingVoice`** | Implement `IVoice.Pan => 0f` (centre). | — |
| **`ISynthesizer` / `Synthesizer`** | New `SetChannelPan(channel, pan)` seam; `channelPan[16]` state; the render-loop change from mono-to-all to per-voice equal-power L/R (stereo) with a graceful non-stereo fallback; a private `EqualPowerGains` pure helper. | GM/MIDI semantics; SF2 interpretation. |
| **`MidiSequencer`** | CC10 → pan mapping (`ControllerType.Pan = 10`); GM-reset to centre; calling `SetChannelPan`. Owns all GM semantics. | Equal-power law; per-voice pan. |
| **test `RecordingSynthesizer`** | Record `SetChannelPan` calls (new `ChannelPanCalls` list) so sequencer tests can assert the CC10 → pan mapping. | — |

**Single-responsibility framing:** the *pan position* (a signed scalar) is owned in two independent places — the region (static, per note) and the channel (dynamic, per CC10). The *pan law* (position → L/R gains) is owned in exactly one place — a private engine helper. Nothing else knows the law.

---

## 6. Interactions & Data Flow

**Static per-voice pan (resolve time, once per note):**
1. `Sf2RegionResolver.BuildRegion` reads generator 17 via the existing `GetEffectiveInt16(zone, globalZone, Sf2GeneratorType.Pan, defaultValue: 0)` (zone-then-global fallback, same as every other generator), clamps to [-500, +500], normalises to `raw / 500f` in [-1,1], and passes it to the `SampleRegion` ctor.
2. `SampleRegion.Pan` holds it immutably. `SamplePlaybackVoice.Pan` returns `region.Pan`.

**Dynamic per-channel pan (render time, on CC10):**
1. `MidiSequencer.ApplyMessage` sees a `Controller` message with `Data1 == ControllerType.Pan`, maps `Data2` (0-127) → pan ∈ [-1,1], and calls `synthesizer.SetChannelPan(channel, pan)`. It `break`s early — the CC7/CC11 gain path is untouched.
2. At GM reset (top of `Render`), every channel is set to centre: `SetChannelPan(channel, 0f)`.
3. `Synthesizer.SetChannelPan` validates the channel range (mirrors `SetChannelGain`) and stores the value in `channelPan[channel]`.

**The mix (per voice, per internal block):**
1. Before the frame loop for a voice, the engine computes `combined = clamp(channelPan[slot.Channel] + slot.Voice.Pan, -1, 1)`.
2. For **stereo output** (`channels == 2`): `EqualPowerGains(combined, out leftGain, out rightGain)` once; then per frame `pre = mono · channelGainBlock[...]`, `masterSlice[baseIndex] += pre · leftGain`, `masterSlice[baseIndex+1] += pre · rightGain`.
3. For **non-stereo output** (mono or other): the existing channel-count-agnostic path is kept verbatim — `mixed = pre · panGain` summed to every channel. Pan collapses to centre (it is a stereo concept).

The `channels == 2` decision is hoisted **outside** the frame loop (once per voice per block), so there is no per-sample branch.

---

## 7. Data Model (Conceptual)

| Entity | New/changed field | Semantics |
|---|---|---|
| `SampleRegion` | `Pan : float` | Static per-region pan ∈ [-1,1], -1 = full left, 0 = centre, +1 = full right. Immutable; sourced from SF2 gen 17. Default 0 when the generator is absent. |
| `Synthesizer` | `channelPan : float[16]` | Dynamic per-channel pan ∈ [-1,1]; default 0 (centre). Set by `SetChannelPan`; read in the mix. Plain array — **not** a `GainRamp` (see §10). |

No new types are introduced. `SampleRegion.Pan` is a scalar property on an existing type; `channelPan` is a private field. This is a "column on an existing entity," not a new layer (Design Contracts §2).

---

## 8. Contracts & Interfaces (Abstract)

**`ISynthesizer.SetChannelPan(int channel, float pan)`** (new)
- **Input:** `channel` ∈ [0,15]; `pan` a signed position where -1 = full left, 0 = centre, +1 = full right.
- **Semantics:** sets the channel's pan for all subsequent mixing; applies to currently-sounding *and* future voices on the channel (it is read live each block, not captured at note-on). MIDI-neutral — takes a position, not a CC number. Mirrors `SetChannelGain`.
- **Invariants:** out-of-range `channel` throws `ArgumentOutOfRangeException` (same guard shape as the other seams). `pan` is accepted as-is (any float); the combined value is clamped at mix time, so no entry clamp is required — consistent with `SetChannelGain` accepting any gain.

**`IVoice.Pan { get; }`** (new)
- **Output:** the voice's static pan ∈ [-1,1]. Immutable for the voice's lifetime.
- **Semantics:** the per-voice contribution to stereo placement, independent of the channel. `SamplePlaybackVoice` returns its region's pan; voices with no pan concept return 0 (centre).

**`EqualPowerGains(float pan, out float left, out float right)`** (new, **private** to `Synthesizer`)
- **Law:** `θ = (pan + 1) · π/4` (pan ∈ [-1,1] → θ ∈ [0, π/2]); `left = cos θ`, `right = sin θ`.
- **Invariants:** `left² + right² = 1` (constant perceived power across the field). `pan = -1 → (1, 0)`; `pan = 0 → (0.7071, 0.7071)`; `pan = +1 → (0, 1)`. Private because the engine is the sole consumer (KISS — no public helper class, no new file).

**`SampleRegion.Pan` ctor parameter** — a new trailing/positioned parameter on the `SampleRegion` ctor carrying the [-1,1] value (default 0 when the SF2 generator is absent). Every `SampleRegion` construction site must pass it; the only production site is `Sf2RegionResolver.BuildRegion`. Test builders that construct regions directly must pass 0 (centre) unless exercising pan.

**MIDI CC10 → pan** (in `MidiSequencer`, private) — `pan = (value - 64) / 64f`, clamped [-1,1]. Value 0 → -1.0 (full left), 64 → 0.0 (centre), 127 → +0.984 (≈ full right; the ~1.6% short-of-rail is inaudible in equal-power placement — see O2).

---

## 9. Cross-Cutting Concerns

- **Allocation-free hot path (C1):** no new per-`Read` allocations. `channelPan` is a ctor-sized `float[16]`. Pan and L/R gains are stack locals computed per voice per block. `EqualPowerGains` is a static method with `out` params — no allocation. **No** per-channel pan block buffer is added (unlike the mix-bus gain block) because channel pan is not smoothed (§10).
- **INV-2 (safety choke) preserved:** `ApplyMasterBus` and `Finalize` are not touched. The only render-loop change is *how a voice's mono sample maps to output channels* — upstream of the master bus. NaN/Inf from a voice still flows into the master slice and is caught by `Finalize` exactly as today.
- **Concurrency / idempotency:** none introduced. Rendering is single-threaded and deterministic; pan is a pure function of (channel state, region state).
- **Error handling:** channel-range validation on `SetChannelPan` mirrors the existing seams; SF2 pan reading is defensive (absent generator → default 0; out-of-range raw → clamped), consistent with the resolver's "degrade, never throw on the note path" contract.
- **Observability:** the deliverable proof (L ≠ R divergence, per-channel positioning) is the observable behaviour; captured as automated tests (§14 / test plan below), not logging.

---

## 10. Quality Attributes & Trade-offs

### 10.1 Decision: channel pan is **not** smoothed (no `GainRamp`) — resolved per voice per block

The mix-bus smooths per-channel *gain* with a `GainRamp` and a per-frame gain block (INV-1, no zipper). The brief asks explicitly whether per-channel *pan* needs the same. **Decision: no smoothing.** Rationale, grounded (Design Contracts §1 KISS/YAGNI, §4 less-is-better):

1. **The cost is asymmetric and lands in the hottest loop.** Gain smoothing is a per-frame *multiply* — cheap. Pan smoothing is fundamentally different: two equal-power rotations do **not** compose (`equalpower(a) ∘ equalpower(b) ≠ equalpower(a+b)`), so a glided pan position must be re-converted to L/R **every frame, per voice**, i.e. `cos`/`sin` per voice per frame. For 32 voices @ 44.1 kHz that is ~2.8 M trig calls/sec **per voice** — order ~10⁹–10¹⁰ trig ops for a 3-minute song. Resolving pan **per voice per block** instead (pan is constant within a `Read`, A4) costs 2 trig calls per voice per block — `block_size × site_count` here is the wrong axis; the axis is **trig-per-block (≈17 k/song/voice) vs trig-per-frame (≈8 M/song/voice)**, a ~500× reduction for an artefact that is rare in the target material.
2. **The artefact it would prevent is rare and bounded.** A pan step only clicks when CC10 changes on a *sounding* note. Game-MIDI targets (DKC2, FF8) set pan **statically per channel**, typically before the channel's notes sound — that is a single centre→position step with no note playing, i.e. **inaudible**. Only a swept CC10 on a sustained note would zip, and even then the step granularity is `BlockFrames = 64` ≈ 1.45 ms (A4).
3. **INV-1 is conditional.** Its statement is *"if per-channel pan glides, no zipper."* Choosing **not** to glide satisfies the invariant vacuously; it is not a violation. The gain seam glides because expression automation is common; pan automation is rarer (the brief's own framing).
4. **The seam is forward-compatible at zero cost.** `SetChannelPan(channel, pan)` is identical whether or not smoothing is later added. If swept-pan material ever reveals an audible zipper, promote `channelPan` from `float[16]` to `GainRamp[16]` + a per-channel pan block and move the L/R resolution from per-block to per-frame — **no interface, sequencer, SF2, or test-contract change.** Filed as a follow-up note (§13 O3), not built now (YAGNI — no 4-week-named need).

**Trade-off named explicitly (Design Contracts §4):** the downside of not smoothing is an audible click *iff* a song sweeps CC10 across a sustained note. Probability in the target corpus: low (static per-channel pan is the norm). Cost if it materialises: one follow-up promotion, seam unchanged. Present cost of smoothing: ~500× trig in the hot path + a new 16×`BlockFrames` buffer, permanently, for all material. The simple version ships.

### 10.2 Other attributes

- **Scalability / performance:** pan adds 2 trig calls per voice per block and 2 multiply-adds per frame (replacing the mono-to-all inner channel loop, which for stereo was itself 2 adds). Net hot-path cost is negligible.
- **Maintainability:** no new types, no new files for production types (the pan law is a private method; `channelPan` is a field; `SampleRegion.Pan` is a property). Mirrors three existing patterns (SampleRegion-rides, MIDI-neutral seam, per-voice surface).
- **Correctness at centre (regression guard):** equal-power at `pan = 0` gives `cos(π/4) = sin(π/4) = 1/√2 ≈ 0.7071`, which equals the old stereo `panGain = 1/√2`. A fully-centred render therefore reproduces the pre-pan stereo mix **within floating-point tolerance** (not necessarily bit-exact, since `(float)Math.Cos(π/4)` and `(float)(1/√2)` may differ by ≤1 ULP). This is asserted as a test.

### 10.3 Alternatives rejected

- **Smooth channel pan with `GainRamp`** — rejected, §10.1.
- **Public `EqualPowerPan` helper class / new file** — rejected: single consumer (the engine). A private static method is the KISS form; a public class with one caller is indirection (Design Contracts §4). The pan *law* is engine-internal; tests exercise it through the render seam.
- **Capture region pan into `VoiceSlot` at note-on** — rejected: requires threading pan through `NoteOn`/`StartVoice` and a new `VoiceSlot` field, when `slot.Voice.Pan` is a cheap live read and pan is immutable per voice (so live == captured). Reading live off `IVoice.Pan` touches neither `VoiceSlot` nor `NoteOn`.
- **Expose pan via `IPatch` instead of `IVoice`** — rejected: a patch resolves *different* regions per (key, velocity), so "the patch's pan" is ill-defined; pan is only known once a voice is started. `IVoice.Pan` is the correct seam.
- **Fold channel + region pan additively in gain space** — rejected: incorrect. Equal-power gains don't add; positions must be summed *then* converted once.

---

## 11. Risks & Mitigations

| # | Risk | Mitigation |
|---|---|---|
| R1 | The `SampleRegion` ctor gains a parameter — every construction site must be updated or the build breaks. | Blast radius is bounded and compile-enforced: the one production site (`Sf2RegionResolver.BuildRegion`) plus any test builders that construct regions directly. The compiler flags every missed site. Enumerate them in implementation (§14). |
| R2 | Adding `Pan` to `IVoice` breaks all four implementers. | Exactly four (`SamplePlaybackVoice`, `InactiveVoice`, test `StubVoice`, `NanEmittingVoice`); compile-enforced; three return `0f`. Small, mechanical. |
| R3 | SF2 gen-17 normalisation divisor wrong (enum comment says "-50..+50", SF2 spec says ±500). | Design mandates the **spec** divisor (500) and clamps; O1 flags the enum doc-comment for correction. A wrong divisor only scales pan magnitude, never breaks the render; validated by the resolver unit test asserting a known raw → expected [-1,1] value. |
| R4 | Hard-panned voices (pan = ±1) put zero signal in one channel; if a whole channel pans hard, that side could feel empty. | This is *correct* equal-power behaviour and matches the composer's intent. Not mitigated — it is the feature. The A/B render is the sanity check. |
| R5 | Non-stereo output path regresses. | The non-stereo branch keeps the existing mono-to-all code **verbatim**; only the `channels == 2` branch is new. Mono render remains bit-identical. |

---

## 12. Migration / Rollout Strategy

Not applicable in the deprecation-window sense (single binary, atomic build — Design Contracts §4/§6 forbid transition shims). The change is a straight in-place replacement of the centre-mix inner loop plus additive seams. One PR, design + implementation together (#1165). No data migration, no config flag.

---

## 13. Open Questions

- **O1 — SF2 gen-17 units.** SF2 2.04 §8.1.3 specifies pan range [-500, +500] (0.1% units); the `Sf2GeneratorType.Pan` doc-comment in this repo reads "-50=left, +50=right". The design uses the **spec** divisor `raw / 500`. Recommend the implementer correct the enum doc-comment in the same PR (one-line doc fix, no behaviour change). Confirm no bundled SF2 relies on the ±50 reading. *(Non-blocking; spec is authoritative.)*
- **O2 — CC10 → pan mapping at the rails.** The symmetric map `(value-64)/64` gives value 127 → +0.984, ~1.6% short of the hard-right rail. Inaudible in equal-power placement. If exact rails are wanted, an asymmetric divisor (`/63` above centre) is a one-line change — flagged, not taken (KISS). Confirm the symmetric map is acceptable.
- **O3 — pan smoothing follow-up.** Should the "promote to `GainRamp` if swept-pan material zips" note (§10.1) be filed as a standing DiVoid task now, or left as a documented seam property? *(My recommendation: a short `new`/`open` note task linked to this design, not built.)*
- **O4 — #7141 cymbal observation.** The user noted percussion (channel 9) reads "too wide/filling." Pan will place percussion off-centre *if* the song/soundfont specifies it. Worth measuring in the A/B whether percussion moves off-centre and whether that plausibly shifts the observation — but this is a *measurement note*, not a design target for this PR.

---

## 14. Implementation Guidance for the Next Agent

Ordered build phases; **no code in this document** — this is the architectural work breakdown. Chain: this design → **john-backend-dev** (implementation + one PR) → jenny-qa-reviewer. Both TFMs must be 0-warning and `dotnet test` green.

1. **SF2 region carries pan (data model).**
   - Add an immutable `Pan` property (float, [-1,1]) to `SampleRegion`, plumbed through the ctor with an XML `<summary>` and `<param>` matching the Envelope/Filter/Lfo style. Default/centre = 0.
   - Update every `SampleRegion` construction site (compile-enumerated): `Sf2RegionResolver.BuildRegion` (real value) and any test builders (pass 0 unless exercising pan).

2. **Resolver reads generator 17.**
   - In `Sf2RegionResolver.BuildRegion`, read gen 17 via the existing `GetEffectiveInt16(zone, globalZone, Sf2GeneratorType.Pan, defaultValue: 0)`, clamp raw to [-500, +500], normalise `raw / 500f`, pass to the ctor. Add a named `const` for the ±500 divisor (magic-number discipline).
   - Correct the `Sf2GeneratorType.Pan` doc-comment per O1.

3. **Voice surfaces pan.**
   - Add `float Pan { get; }` to `IVoice` (XML summary). `SamplePlaybackVoice.Pan => region.Pan`; `InactiveVoice`, `StubVoice`, `NanEmittingVoice` → `0f`.

4. **Engine seam + state + pan law.**
   - Add `SetChannelPan(int channel, float pan)` to `ISynthesizer` (MIDI-neutral, mirror `SetChannelGain` doc/guard) and implement on `Synthesizer` with a `channelPan[16]` field (default 0).
   - Add the private static `EqualPowerGains(float pan, out float left, out float right)` (θ = (pan+1)·π/4; left = cos θ, right = sin θ). Add a named `const` for π/4 or derive from `Math.PI` — implementer's discretion, keep it a pure helper.

5. **Render-loop change (the core).**
   - Replace the centre `mixed = … · panGain` inner channel loop. Per voice, before the frame loop: `combined = clamp(channelPan[slot.Channel] + slot.Voice.Pan, -1, 1)`.
   - Hoist the `channels == 2` check outside the frame loop. Stereo branch: `EqualPowerGains` once, then per frame `pre = mono · channelGainBlock[…]`, add `pre·leftGain` to L and `pre·rightGain` to R. Non-stereo branch: keep the existing mono-to-all `panGain` loop **verbatim**.
   - Keep `panGain` (used by the non-stereo fallback). Update the `Synthesizer` class `<summary>` (currently says "equal-power centre pan") to describe real per-voice stereo placement.
   - Preserve `ApplyMasterBus` + `Finalize` unchanged; keep `Read` allocation-free.

6. **Sequencer: CC10 semantics.**
   - In `MidiSequencer.ApplyMessage` `Controller` case, add an early branch: `Data1 == ControllerType.Pan` → map `Data2` via a private `ControllerToPan` (`(value-64)/64f`, clamped) → `SetChannelPan`, then `break`. Leave the CC7/CC11 gain path intact.
   - GM reset (top of `Render`): `SetChannelPan(channel, 0f)` for all 16. Add named `const` for the CC centre (64).

7. **Test double.**
   - `RecordingSynthesizer`: add `ChannelPanCalls` list + `SetChannelPan` recording (mirror `ChannelGainCalls`).

8. **Tests (all `[TestFixture, Parallelizable]` / `[Test, Parallelizable]`, `Assert.That(…, Is.EqualTo(…))`, explicit types, no body comments — Code Contracts §4/§13):**
   - **Resolver:** gen 17 present → `SampleRegion.Pan` equals expected normalised value (e.g. raw -500 → -1, +250 → +0.5, 0/absent → 0). Follow `Sf2RegionResolverTests` zone-building style.
   - **Engine unit:** a single voice panned hard-left → R ≈ 0, L ≈ full; hard-right → L ≈ 0; centre → L == R (equal). A region-pan + channel-pan combination clamps and places as expected. Assert the equal-power ratio (`left/right = cot θ`) for a mid pan.
   - **Centre regression:** a fully-centred stereo render reproduces the pre-pan mix within float tolerance (centre gains = old `panGain`).
   - **Mono regression:** `channels == 1` render is bit-identical to pre-change (non-stereo path verbatim).
   - **Sequencer:** CC10 message → `SetChannelPan` recorded with the mapped pan; GM reset issues centre pan to all 16 (extend `MidiSequencerChannelGainTests` style).
   - **Deliverable proof (mirror `MixBusRenderProofTests`, skip-gracefully on absent assets):** render `07dkc2bram.mid` through Florestan; assert **L ≠ R** — the even-index (L) and odd-index (R) sample streams now diverge (sum-of-squared-difference above a threshold; they were bit-identical before) — and that at least two channels/instruments sit at distinct positions. A/B note vs a centre render. Note (O4) whether percussion moves off-centre.

9. **Manual A/B render (deliverable evidence):** run `tools/Pooshit.AudioSynth.MidiRender` on `07dkc2bram.mid` + Florestan; measure L vs R divergence vs a centre baseline; capture the before/after in the PR body.

**Definition of done:** both TFMs 0-warning; `dotnet test` green; the L ≠ R proof passes; centre and mono regressions pass; design doc committed on the branch; one PR (#1165) bundling design + implementation.
