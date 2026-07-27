> **Repo path (source of truth):** `docs/architecture/mix-bus.md` on branch `feature/mix-bus` of `telmengedar/AudioSynth` (branched from `origin/main` @ PR 11 merge `e03bd1e`). This DiVoid node is the graph-discoverable copy; the repo file ships in the PR.

# Architectural Document: Mix Bus — Gain-Staging + Per-Channel MIDI Mix Controls (PR 12)

**Author:** Sarah · **Date:** 2026-07-27 · **Source task:** #7125 · **Driven by diagnosis:** #7124 · **Project:** #6128 · **Map root:** #6708 · **Roadmap:** #7098 PR 12.
**Load-bearing:** Code Contracts #114 (§0 KISS/DRY/YAGNI, §5.5), Design Contracts #1136 (§1–§5), PR-shape #1165.
**Integration points:** `Synthesizer` #6734, `ISynthesizer` #6726, `MidiSequencer` #7114, precedent seam design multi-timbral #7117.

---

## 1. Problem Statement

Dense passages clip. Diagnosis #7124 **proved** (against a real render of `07dkc2bram.mid` with the Florestan GM SF2) that the distortion the user hears at the DKC2 opening — and mis-read as a "wrong instrument" — is **hard clipping in the mixer**, not a patch-selection bug. `globalPeak = 1.0000` exactly; **7.19% of all samples clip** (`|s| ≥ 1.0`); the delicate marimba/vibraphone arpeggio is distorted from the first note (`t = 0.334s`, first onset `t = 0.304s`).

Two compounding, source-provable causes:

1. **No master headroom or limiter.** `Synthesizer.Read` (the render loop, `Synthesis/Synthesizer.cs:103-124` on origin/main) sums every voice with only a fixed per-voice centre gain `panGain = 1/√channels ≈ 0.707`, then `Finalize` (`:141-152`) **hard-clamps to [-1,1]**. ~4 channels sounding together at the opening sum past 1.0 and clamp to a buzz.
2. **CC7 (Channel Volume) and CC11 (Expression) are ignored.** `MidiSequencer.ApplyMessage` handles only NoteOn / NoteOff / ProgramChange; `Controller` messages are discarded (the `ControllerType` enum even documents "Not interpreted in increment 1"). The composer's intended per-channel attenuation (e.g. ch4 Strings at CC7≈50 / CC11≈32, meant to sit quiet) is thrown away, so every channel mixes at full velocity gain and worsens the sum.

**Success criteria (deliverable proof):** after this change, a re-render of `07dkc2bram.mid` + an FF8 track through the Florestan GM SF2 must (a) drop the clipped-sample fraction sharply from the **~7.19%** baseline (measured), (b) render the DKC2 opening cleanly (no buzz), and (c) make a low-CC7/CC11 channel audibly quieter in the mix. Automated: N loud simultaneous voices no longer produce a clipped output, and CC7/CC11 provably scale a channel's contribution.

Fixing both causes also **restores dynamics** the clipping was crushing — but the perceptual velocity curve is a **separate, explicitly-next PR** (see §3 Non-Scope), not this one.

---

## 2. Scope & Non-Scope

### In scope (the diagnosis fix — one cohesive mix-bus feature)

- **Master mix-bus stage: headroom + soft limiter.** Replace the raw hard clamp as the *only* gain management with a proper master bus stage: a soft-clip saturator that is unity-linear at normal levels and rolls dense peaks off gracefully toward the ceiling instead of cornering. Preserves **INV-2**: `Finalize` stays the NaN/Inf-safe choke point; the soft limiter sits **before** it and does not remove it.
- **Per-channel CC7 (Channel Volume) + CC11 (Expression).** The sequencer maps these controllers to a per-channel linear mix gain via a cited standard curve; the synth scales that channel's voices by a **zipper-free** per-channel gain (INV-1). Requires one additive `ISynthesizer` seam (a per-channel gain setter, analogous to `SetChannelPatch`). The engine stays **MIDI-neutral** — it takes a gain, not a CC number; all GM/CC semantics stay in `MidiSequencer`.

### Deferred to a named fast-follow (PR 12b) — **not in this PR**

- **CC10 Pan + per-voice SF2 pan** (real stereo placement replacing the current mono-summed-to-both-channels centre mix). **Cut rationale in §11.** This is a *different concern* (stereo imaging, not gain-staging), lands in a *different layer* (`Formats/Sf2` schema + resolver, plus a stereo change to the render loop), and does **not** resolve the clipping. Filed as DiVoid task; the mix-bus seam this PR builds is the clean foundation it slots onto.

### Out of scope (later PRs, per roadmap #7098)

| Item | Why out | Home |
|---|---|---|
| Note-velocity perceptual curve / velocity layers | The user's **explicit next PR**; a distinct concern (velocity→gain shaping). Diagnosis #7124 confirmed velocity→gain is currently linear and that this is *polish, not the clipping cause*. | PR 13 (velocity) |
| Voice-stealing on a full pool | Voice-allocation policy, separable from gain-staging. Not trivially cohesive here (it changes `FindFreeSlot`, not the mix). | later |
| PitchWheel / modulation / sustain | Expression controllers, unrelated to the mix bus. | PR 13 |
| **`SoundBank` / patch-selection / fallback** | Diagnosis #7124 **exonerated it** — the opening resolves every requested program exactly; do **not** touch it. | — |

---

## 3. Assumptions & Constraints

- **Stereo output, mono voices.** `IVoice.RenderBlock` renders a **mono** block; the engine mixes each voice into all output channels. Default `Channels = 2`. The current mix is mono-summed-to-both (centre). This PR keeps that mono-to-all-channels topology (pan, which would break it into per-L/R placement, is deferred).
- **Allocation-free render hot path (hard constraint).** `Synthesizer.Read` allocates nothing in steady state; every buffer is ctor-sized. Any new per-block working buffer this PR adds **must** be pre-allocated at construction.
- **INV-1 (no zipper).** Any gain that can change mid-note must glide, not step. CC7/CC11 change mid-note (expression swells), so per-channel gain **must** be smoothed. The `GainRamp` struct (`Synthesis/GainRamp.cs`) is the established per-frame slew primitive and is reused unchanged.
- **INV-2 (Finalize is the NaN/Inf choke point).** `Finalize` remains the single place that neutralises NaN/Inf and guarantees the final [-1,1] bound. The master bus stage composes *before* it.
- **MIDI-neutral engine.** Per roadmap #7098's boundary: the synth knows nothing about MIDI/GM/CC; `MidiSequencer` is the only MIDI↔synth coupling and owns all CC semantics.
- **Interface-change blast radius is small.** Only two `ISynthesizer` implementers exist: `Synthesizer` and the test-only `RecordingSynthesizer`. `MidiSequencer.Render`'s signature is unchanged (the new seam is additive), so `OfflineRenderer` and the `MidiRender` CLI are untouched.
- **Magic numbers stay magic (Design Contracts §3).** The headroom/knee constants and the CC-curve are fixed engine behaviour with no operator who tunes them and no environment that varies them — they ship as named `const`, not config.

---

## 4. Architectural Overview

Two seams, one per subsystem, meeting the roadmap boundary (synth = gains; sequencer = CC semantics):

```
                         MidiSequencer  (owns GM/CC semantics)
                         ┌───────────────────────────────────────────────┐
  Controller CC7/CC11 →  │ per-channel cc7[16], cc11[16] state (per Render)│
                         │ ChannelGain(cc7,cc11)  = curve(cc7)·curve(cc11) │  ── cited pure fn
                         └───────────────┬───────────────────────────────┘
                                         │ SetChannelGain(channel, gain)      ── NEW additive seam
                                         ▼                                       (MIDI-neutral: a gain)
                         Synthesizer  (MIDI-agnostic voice engine)
                         ┌───────────────────────────────────────────────┐
                         │ GainRamp[16] channelGain   (INV-1 smoothed)    │
                         │                                                 │
                         │ Read (per block):                               │
                         │   1. pre-compute per-channel per-frame gain     │  ── pre-alloc buffer,
                         │      buffer  (advance the 16 ramps frame-wise)  │     allocation-free
                         │   2. voice mix:  sample · chGain[ch,frame]       │
                         │                 · panGain  → sum into master    │
                         │   3. ApplyMasterBus(master)  ── trim + soft clip │  ── NEW master stage
                         │   4. Finalize(master)        ── NaN/Inf + guard  │  ── INV-2 unchanged
                         └───────────────────────────────────────────────┘
```

Everything downstream of the summed master bus is new; everything upstream (voice rendering, patch routing, pooling) is untouched.

---

## 5. Components & Responsibilities

### 5.1 `ISynthesizer` — additive seam (owns: the contract)

Gains one method, mirroring `SetChannelPatch`:

- **`SetChannelGain(int channel, float gain)`** — sets the current linear mix gain for a channel; only affects how that channel's voices are scaled going forward (glided, not stepped). Guard `channel ∈ [0,15]`.

**Naming:** `SetChannelGain`, not `SetChannelVolume` — the engine is MIDI-neutral and receives a *linear gain*, not a CC7 "volume" value. The name states what the engine sees. (Contrast the sequencer, which owns the CC7 "volume" concept and converts it to a gain.)

Does **not** own: the CC→gain curve, GM defaults, controller numbers — all of that is the sequencer's.

### 5.2 `Synthesizer` — per-channel gain state + master bus (owns: the mix)

New state:
- **`GainRamp[16] channelGain`** — one per-frame slew ramp per channel, targeting the last `SetChannelGain` value. Reuses `GainRamp` unchanged (INV-1). Default target `1.0` (MIDI-neutral: no attenuation until the sequencer applies GM defaults).
- **`float[ChannelCount * BlockFrames] channelGainBlock`** — a pre-allocated per-block working buffer holding each channel's gain at each frame of the current block. Ctor-sized; keeps `Read` allocation-free.

New responsibilities inside `Read`, per block:
1. **Pre-compute the channel-gain block.** Before the voice loop, advance each of the 16 `channelGain` ramps frame-by-frame into `channelGainBlock`. This decouples ramp advancement from the voice loop — critical, because a channel with N voices must **not** advance its ramp N× per frame (that would corrupt the slew and the INV-1 guarantee). All 16 advance every block (16×64 ≈ 1k trivial float ops), so a channel's gain is current even while it has no sounding voices.
2. **Apply channel gain in the voice mix.** Each voice frame is scaled by `channelGainBlock[voice.Channel, frame]` in addition to the existing `panGain`.
3. **`ApplyMasterBus(master)`** — new master-bus stage: an optional headroom trim (named `const`) then the soft-clip transfer (§5.3). Runs once over the summed master slice, before `Finalize`.

`SetChannelGain(channel, gain)` sets `channelGain[channel]` target. Does **not** own NaN/Inf safety (Finalize's job, unchanged).

### 5.3 Master bus soft-clip — the limiter (owns: bounding the summed mix musically)

A **stateless, per-sample soft-clip transfer function** applied to the summed master bus:
- **Unity-linear below a knee threshold** (named `const`, e.g. ~0.6–0.7): normal-level material passes through **unchanged** — low-level dynamics are preserved, no global coloration (this is why a bare `tanh` on everything is rejected: it attenuates even quiet signals).
- **Smooth compressive knee above the threshold**, asymptotically approaching the ±1 ceiling, so dense peaks round off gracefully instead of cornering into a buzz.
- **Strictly bounded to [-1,1] for every finite input** (piecewise: linear below the knee, a bounded polynomial/rational knee between the knee and the ceiling, hard ceiling at ±1). Monotonic.

Because it is bounded and monotonic, it **composes** with `Finalize`: for finite inputs the subsequent hard clamp is a no-op (the soft clip already bounded them), so `Finalize` retains **only** its NaN/Inf-guard role — INV-2 is preserved exactly, not weakened. NaN/Inf handling stays exclusively `Finalize`'s responsibility; the soft clip makes no NaN/Inf guarantee and needs none (the summed bus is finite unless a voice misbehaves, and Finalize is the backstop).

Stateless ⇒ no attack/release constants, no look-ahead latency, no per-channel state, allocation-free, deterministic — the KISS choice the brief calls for ("a soft limiter is a small, well-known DSP block — don't over-architect a mix graph"). A stateful look-ahead peak limiter with an envelope follower was considered and **rejected** (§10): it buys transparency this offline render doesn't need, at the cost of state, latency, and tuning constants.

### 5.4 `MidiSequencer` — CC7/CC11 → gain (owns: GM/CC semantics)

New responsibilities within `Render` (still a single static call; new state is call-local, so the method stays reentrant/thread-safe-per-call):
- **Per-channel controller state** `cc7[16]`, `cc11[16]`, allocated at `Render` entry.
- **GM reset** (extends the existing channel-reset loop): initialise `cc7[ch] = 100`, `cc11[ch] = 127` (GM defaults), compute the channel gain, and call `SetChannelGain(ch, gain)` for all 16 channels alongside the existing `SetChannelPatch` reset.
- **`Controller` message handling** (new `case` in `ApplyMessage`): when `Data1` is `ControllerType.Volume` (7) or `ControllerType.Expression` (11), update the stored value, recompute the channel gain, and call `SetChannelGain(channel, gain)`. All other controllers remain ignored (unchanged).
- **`ChannelGain(int cc7, int cc11) → float`** — a private static **pure** function (the CC→gain curve, §8). Single location, cited, A/B-validated.

Does **not** own: the smoothing (the synth's `GainRamp`), the master bus.

### 5.5 `RecordingSynthesizer` (test helper) — records the new seam

Implements the added `SetChannelGain` by appending to a new `ChannelGainCalls` list (mirrors `ChannelPatchCalls`), so sequencer-level tests can assert the exact (channel, gain) calls without rendering.

---

## 6. Interactions & Data Flow

**GM reset (once, at `Render` start):** for each channel 0–15, sequencer computes `ChannelGain(100, 127)` and calls `SetChannelGain(ch, gain)`; the synth sets `channelGain[ch]` target. (Ramps start at 0 and glide to target over one `GainRamp` smoothing window ≈ 5 ms — an inaudible, click-free fade-in at absolute `t=0`; §10 notes why this is acceptable and not worth a snap-to-target addition.)

**A CC7/CC11 controller event mid-song:**
1. Sequencer's `ApplyMessage` sees a `Controller` message; `Data1 ∈ {7,11}`.
2. Updates `cc7[ch]` or `cc11[ch]`, recomputes `gain = ChannelGain(cc7[ch], cc11[ch])`.
3. Calls `synth.SetChannelGain(ch, gain)` → sets `channelGain[ch]` target.
4. On subsequent `Read` blocks, `channelGain[ch]` **glides** to the new target frame-by-frame (INV-1), so the channel's contribution swells/dips smoothly with no zipper — even if the change lands mid-note.

**Per render block (`Read`):**
1. Pre-compute `channelGainBlock` by advancing all 16 ramps across the block's frames.
2. For each occupied voice: render mono → scratch; for each frame, `mixed = scratch[frame] · channelGainBlock[voice.Channel, frame] · panGain`; sum into master (all output channels).
3. `ApplyMasterBus(master)` — trim + soft clip.
4. `Finalize(master)` — NaN/Inf → 0, [-1,1] guard.

Communication is entirely synchronous, in-process, pull-based — no change to the existing model.

---

## 7. Data Model (Conceptual)

No persisted data. Transient state only:

| State | Owner | Lifetime | Meaning |
|---|---|---|---|
| `channelGain[16]` (GainRamp) | `Synthesizer` | instance | per-channel smoothed linear mix gain |
| `channelGainBlock[16·frames]` | `Synthesizer` | instance (reused per block) | pre-computed per-frame channel gains for the current block |
| `cc7[16]`, `cc11[16]` | `MidiSequencer.Render` | per `Render` call | last-seen raw controller values per channel |

---

## 8. Contracts & Interfaces (Abstract)

### 8.1 `SetChannelGain(channel, gain)`

| Aspect | Contract |
|---|---|
| Input | `channel ∈ [0,15]` (else `ArgumentOutOfRangeException`); `gain` a finite linear scalar, nominally `[0,1]` |
| Semantics | Sets the channel's **target** gain; the actual applied gain **glides** to it (INV-1). Affects all future rendering of that channel's voices. Does not retro-scale already-summed audio. |
| Invariant | Idempotent for a repeated value; changing it never steps the output. |

### 8.2 `ChannelGain(cc7, cc11) → float` (sequencer-private pure function)

| Aspect | Contract |
|---|---|
| Input | `cc7, cc11 ∈ [0,127]` |
| Output | linear gain `∈ [0,1]`, `ChannelGain(127,127) = 1.0`, `ChannelGain(0,·) = 0`, monotonic non-decreasing in each argument |
| Curve | **GM/SF2 concave (dB) curve per controller, product-combined**: `gain = (cc7/127)² · (cc11/127)²`. See §9 for derivation and citation. |

### 8.3 Master bus soft-clip (synth-private)

| Aspect | Contract |
|---|---|
| Input | one summed master sample (finite) |
| Output | `∈ [-1,1]`, equal to input in the linear region below the knee, monotonic, saturating toward ±1 above it |
| Invariant | Never widens magnitude; never introduces NaN/Inf from finite input; NaN/Inf pass-through is Finalize's concern, not this stage's. |

---

## 9. The CC7/CC11 → gain curve (cited)

Each of CC7 (Channel Volume) and CC11 (Expression) maps 0..127 → linear gain via the **GM1 / SF2 2.04 concave volume characteristic**: attenuation(dB) = `40·log10(127/value)`, which in the linear domain is exactly **`gain = (value/127)²`**. The channel's combined gain is the **product** of the two (dB-additive attenuation, spec-faithful):

```
channelGain = (cc7/127)² · (cc11/127)²
```

- Use the `(value/127)²` linear form (not the log form) — it handles `value = 0 → 0` cleanly and avoids a log-domain singularity.
- **Why this restores the composer's intent (the diagnosis's own example):** ch4 Strings at CC7≈50, CC11≈32 → `(50/127)²·(32/127)² ≈ 0.155 · 0.063 ≈ 0.0098` (≈ −40 dB) — exactly the "sit quiet under the melody" the composer wrote and PR 11 was throwing away.
- **Free headroom side-effect:** the GM default CC7=100 → `(100/127)² ≈ 0.62` per channel (CC11=127→1.0). Every channel that never sends CC7 now sits ~4 dB down by GM-correct default, which *by itself* meaningfully lowers the summed level and helps the clipping — before the soft clip even engages.

**Architectural point that de-risks the curve choice:** the curve is a single private pure function fed into `SetChannelGain`. The seam (sequencer computes gain → synth glides to it) is invariant to which curve is chosen. If the A/B render shows the concave-squared curve too aggressive, swapping to linear (`(cc7/127)·(cc11/127)`) or single-concave is a one-function edit with zero structural impact. The concave-squared curve is the GM-faithful **recommended default**; §13-O1 flags the A/B as the validation gate.

---

## 10. Quality Attributes & Trade-offs

- **Correctness / faithfulness:** applying CC7/CC11 with the GM concave curve reproduces the composer's intended balance; the soft clip removes the buzz. Together they directly resolve #7124.
- **Performance:** master bus stage + channel-gain pre-compute are O(block) float work on a buffer already in cache; `Read` stays allocation-free (the one new buffer is ctor-allocated). Offline render — no real-time budget pressure regardless.
- **INV-1 preserved:** per-channel gain rides `GainRamp` at frame rate; the pre-compute-then-mix structure guarantees each ramp advances exactly once per frame regardless of voice count. Rejected alternative: advancing the ramp inside the voice loop (corrupts slew for polyphonic channels) or once per block (reintroduces block-boundary steps — the exact zipper `GainRamp` exists to prevent).
- **INV-2 preserved:** soft clip sits before `Finalize`; `Finalize` is untouched and remains the NaN/Inf choke point + final guard.
- **Simplicity trade-off (soft clip vs. true limiter):** a **stateless soft-clip** is chosen over a **look-ahead peak limiter with attack/release envelope**. Downside named concretely: a soft clip colors *peaks* (harmonic rounding) where a look-ahead limiter would preserve peak transient shape transparently. Probability/cost this matters: low — offline game-music render, dense peaks that today clip to a hard buzz; graceful saturation is a strict, audible improvement, and transparency of the top 2 dB is not a stated requirement. Present cost of the limiter: per-sample envelope state, a look-ahead delay line (latency + buffer), and two tuning constants (attack, release) that become magic numbers no one owns (Design Contracts §3). The stateless soft clip wins decisively. If a future requirement demands transparent peak limiting, it comes back with the actual shape in hand.
- **GainRamp initial fade:** ramps glide 0→target over ≈5 ms at `t=0`. Rejected adding a `GainRamp.Snap(value)` — a 5 ms click-free fade-in at absolute render start is inaudible and arguably beneficial (no sample-0 transient); adding a snap API is YAGNI for a non-problem.
- **Maintainability:** two additive seams, one new pure function, one new stateless DSP method, one reused primitive. No new type, no new interface, no new config. The mix-bus concept lives in the two subsystems that already own mix and CC — no "mix graph" abstraction (YAGNI).

---

## 11. Pan cut — why deferred to PR 12b (explicit)

The brief permits deferring pan "if the limiter + CC7/CC11 alone is already a full PR." It is. Cut rationale:

1. **Different concern.** Pan is stereo *imaging*; this PR is *gain-staging*. Pan does not touch the clipping. Bundling mixes "fix the buzz" with "add stereo width" in one review — exactly the tangle the one-feature-per-PR rule (Code Contracts, #1165) prevents.
2. **Different layer + bigger blast radius.** Per-voice SF2 pan requires a **schema change to `SampleRegion`** (a new `Pan` property — the SF2 `Pan` generator #17 is parsed in the enum but **not** plumbed into `SampleRegion` today), a change to `Sf2RegionResolver` to read generator 17, resolver test updates, **and** a change to the render loop's mono-summed-to-all-channels topology into per-L/R placement (which hardcodes stereo, breaking the current channel-count-agnostic mix). That is a self-contained, reviewable feature of its own.
3. **Clean foundation.** The per-channel gain seam + master bus this PR builds is exactly what pan composes onto (pan becomes per-voice L/R gains multiplied alongside `channelGain`). Deferring costs nothing structurally; it sequences the work cleanly.
4. **Deliverable is fully met without pan.** The clipped-fraction drop and audible CC7/CC11 attenuation need no stereo placement.

Filed as DiVoid task **PR 12b — CC10 pan + per-voice SF2 pan** (scope: plumb SF2 `Pan` gen #17 → `SampleRegion.Pan`; `Sf2RegionResolver` reads it; CC10 → per-channel pan in sequencer; render loop applies per-voice equal-power L/R placement replacing the fixed `panGain`).

---

## 12. Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Channel-gain ramp advanced per-voice instead of per-frame → corrupted slew, INV-1 broken | Architecture mandates the **pre-compute-then-mix** structure: ramps advance once per frame into `channelGainBlock` *before* the voice loop reads it. Called out explicitly in §5.2 and §10. |
| Soft clip still lets the summed bus exceed 1 (under-designed knee) | Contract §8.3 requires strict `[-1,1]` bound for all finite inputs (hard ceiling above the knee); `Finalize` remains the backstop. Automated test: N loud voices → clipped fraction ≈ 0. |
| CC curve too aggressive/soft → mix balance wrong | Curve is a single cited pure function (§9); A/B render is the validation gate (§13-O1); swap is a one-function edit with zero structural impact. |
| New per-block buffer allocated in `Read` → breaks allocation-free hot path | Buffer is **ctor-allocated** (`ChannelCount · BlockFrames`), sized from options; §3 + §5.2 make this explicit. Reviewer checks `Read` for allocations. |
| GM-default CC7=100 changes the baseline vs the PR-11 render (channels now ~4 dB down) | This is **GM-correct** and intended (part of restoring balance); the A/B is against the *clipping*, not level-matching. Noted for the reviewer so the quieter, cleaner render isn't flagged as a regression. |
| Interface change misses an implementer → build break | Only two implementers (`Synthesizer`, `RecordingSynthesizer`), both enumerated in §2/§5; compiler enforces the rest. |

---

## 13. Open Questions

- **O1 (curve validation).** Recommended default is the GM/SF2 concave-squared curve (§9). The A/B re-render is the gate: if the mix reads too aggressive (channels vanishing), fall back to linear or single-concave — a one-function swap. Confirm the A/B is acceptable before merge. *(Not a blocker for design; a tuning validation.)*
- **O2 (headroom trim const).** Start with the soft clip alone (knee threshold as the sole lever). If the measured post-fix loudness/clipped-fraction wants more margin, add a named master-trim `const` (≤1) ahead of the soft clip. Implementer sets it to hit the deliverable metric; both knee and trim stay `const` (Design Contracts §3).
- **O3 (FF8 asset).** Which FF8 track is the second proof render? (Same open question shape as PR 10/11's O2.)

---

## 14. Implementation Guidance for the Next Agent

Ordered build phases (each independently verifiable; all in ONE PR on `feature/mix-bus` with this design doc):

1. **`ISynthesizer` seam.** Add `SetChannelGain(int channel, IPatch… )` → `SetChannelGain(int channel, float gain)`. Update both implementers: `Synthesizer` (stub first) and `RecordingSynthesizer` (record to new `ChannelGainCalls`).
2. **`Synthesizer` per-channel gain.** Add `GainRamp[16] channelGain` (default target 1.0) and the ctor-allocated `channelGainBlock`. Implement `SetChannelGain` (guard channel, set ramp target). Restructure `Read`: pre-compute the channel-gain block (advance all 16 ramps frame-wise), then multiply each voice frame by its channel's per-frame gain alongside `panGain`.
3. **Master bus stage.** Add `ApplyMasterBus(master)` (named-`const` knee threshold; optional trim `const` per O2) implementing the §5.3/§8.3 soft-clip. Call it after the voice loop, **before** `Finalize`. Leave `Finalize` untouched.
4. **`MidiSequencer` CC7/CC11.** Add call-local `cc7[16]`/`cc11[16]`; extend GM reset to set defaults (100/127) and call `SetChannelGain`; add the `Controller` case in `ApplyMessage` for CC7/CC11; add the private static `ChannelGain(cc7,cc11)` pure function (§9 curve, cited in a doc-comment).
5. **Tests.**
   - Unit: `ChannelGain` known values (127,127→1.0; 0→0; 100,127→≈0.62).
   - Unit: soft clip — N loud simultaneous voices → assert clipped-sample fraction ≈ 0 and `max|sample| ≤ 1`.
   - Unit: `SetChannelGain` scales a channel — two identical voices on two channels, gains 1.0 vs 0.5, assert ~2:1 contribution once ramps settle.
   - Unit: INV-1 — a mid-note channel-gain change produces a glide, not a single-frame step.
   - Unit (sequencer): a CC7/CC11 `Controller` message → expected `SetChannelGain` call recorded on `RecordingSynthesizer`; GM reset issues 16 default-gain calls.
   - Integration (graceful-skip): re-render `07dkc2bram.mid` + FF8 track through Florestan → non-silent, bounded, **measured clipped fraction ≪ 7.19%**. A/B vs `renders/song-dkc2-brambles-MT.wav`.
6. **Verify:** both TFMs 0-warning; `dotnet test` green; produce the A/B renders and record the measured clipped-fraction drop in the PR body.

**Chain:** sarah (this design, committed to `feature/mix-bus`) → **john-backend-dev** (real `ISynthesizer` contract change + render-hot-path restructuring + a new DSP stage + sequencer CC state + tests across ~5 prod files and the test project is substantial — handed off, mirroring PR 10/11) → jenny-qa-reviewer. One PR (#1165). Do **not** modify `SoundBank` (#7124). Do **not** bundle pan (§11).

---

## Appendix — Design Contracts §5 Pre-Design Checklist (verbatim walk)

**KISS / DRY / YAGNI**
- [x] No new type mirroring an existing type — no new type at all (reuses `GainRamp`; adds methods/state to existing classes).
- [x] No new abstraction with one implementation — no new interface; the seam is a method on the existing `ISynthesizer`.
- [x] No element justified by "might need later" — pan/velocity/voice-stealing explicitly deferred with named homes, not speculatively hooked.
- [x] No deprecation period / feature flag / compat shim — additive interface method, atomic change.
- [x] No inline-vs-extract DRY violation — the CC curve is a single pure function; the soft clip a single method; no multi-site block duplication.

**Existing systems first**
- [x] Audited: per-channel gain lives on the existing `Synthesizer` (new state), CC semantics on the existing `MidiSequencer`, smoothing on the existing `GainRamp`. No new layer.
- [x] New-layer justification: none proposed. The one new method (`SetChannelGain`) is justified by the MIDI-neutral boundary (#7098) — the synth cannot take a CC number.
- [x] No new persisted data (all transient).
- [x] No "existing reader projects it" chains (no data model).

**Configurability**
- [x] No config knob added. Knee threshold + optional trim + curve are named `const`/pure-fn — no operator tunes them, no environment varies them (Design Contracts §3). O2 keeps any trim as `const`.
- [x] No telemetry-then-tune compound.
- [x] Magic numbers (knee, trim, GM defaults 100/127) are named `const`.

**Less is better**
- [x] can-delete / can-merge / can-inline run: master stage can't merge into Finalize without breaking the INV-2 single-responsibility split; channel-gain can't inline into the voice loop without breaking INV-1 (both justified in §10).
- [x] Trade-offs named explicitly: soft-clip vs look-ahead limiter (§10); pan deferral (§11); no snap-to-target (§10).
- [x] No consumer-less compromise shape.
- [x] Reader-inventory: interface implementers enumerated (2), both AST (no string-literal CC references — `ControllerType` is an enum compared to `Data1`).

**Document discipline**
- [x] Cites Code Contracts (#114) and Design Contracts (#1136) as load-bearing.
- [x] Scope/non-scope explicit (§2), including the exonerated `SoundBank` and the deferred pan.
- [x] No multi-paragraph "why keep X" for obvious retentions.
- [x] Not superseding a prior design (extends the roadmap #7098 sequence); no predecessor banner needed.
