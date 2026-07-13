# Architectural Document: Voice / Synthesis Engine

**Author:** Sarah (software architect) · **Date:** 2026-07-13
**Source task:** DiVoid #6502 · **Project:** #6128
**Builds on:** rewrite design #6401 (+ `docs/architecture/audiosynth-rewrite.md`), direction refinement #6487
**Defect catalog designed out:** #6272 class B (audio-stream artifacts) — the whole point of this PR
**Repo copy:** `docs/architecture/synth-engine.md` (branch `feature/synth-engine`)

> This is the design for the whole voice/synthesis engine (roadmap PR 4 in #6401 §14). It ships
> bundled with **increment 1** — the smallest cohesive slice that proves *patch → voice → mix →
> pull-stream* end to end with the two load-bearing anti-artifact invariants (continuous gain glide,
> NaN-safe mix) implemented and tested. Every other engine capability is a **named follow-up PR** (§14).

---

## 1. Problem Statement

PRs #1 (core seam) and #2 (SF2 loader) are merged. We have the pull seam (`IAudioSource`), the
instrument seams (`ISynthesizer`, `IPatch`, `IVoice`), and an SF2 loader producing
`IReadOnlyList<IPatch>` whose `Sf2Patch.StartVoice` currently throws `NotImplementedException`. What
is missing is the **engine that turns note events into sound**: a `Synthesizer` that *is* an
`IAudioSource`, allocates voices for played notes, renders each voice per block, mixes them, and hands
a clean float PCM stream to whatever pulls it (offline renderer, game engine, playback adapter).

The engine is the point where the legacy's **audio-stream artifact class** lived (clicks, zipper,
NaN pops — catalog #6272 B). The goal is not merely to build the engine but to build it so those
artifacts are **structurally impossible**, not fixed site-by-site.

**Success criteria:** a note played on the synthesizer produces an audible, correctly-pitched,
click-free, amplitude-bounded, NaN-free float PCM stream when pulled through the existing
`OfflineRenderer`; and the anti-artifact properties are enforced by construction and proven by test.

## 2. Scope & Non-Scope

**In scope (design):** the complete engine architecture — the `Synthesizer` pull driver and block
loop, the voice model, the voice pool and allocation/stealing contract, the per-frame gain-glide
primitive, the NaN/Inf-safe mix finalize, the sample-playback voice, pitch/interpolation, note-event
routing, the configuration/options object, and the threading model.

**In scope (increment 1 — code shipped with this doc):** the `Synthesizer` engine, the
`SynthesizerOptions` config object, the per-frame `GainRamp` primitive, a `SamplePlaybackVoice`
(linear interpolation), a `SamplePatch` (a concrete `IPatch` playing a mono sample region), and note
routing driven by `NoteOn`/`NoteOff`. Both anti-artifact invariants implemented; three anti-artifact
tests. (See §14 and §7-contracts for the exact slice.)

**Explicitly out of scope (each a named follow-up PR — §14 roadmap):**

| Deferred | Why deferred |
| --- | --- |
| SF2 zone resolution wiring (`Sf2Patch.StartVoice` → build a voice) | Independent, substantial SF2-semantics unit; own PR keeps review clean |
| ADSR amplitude/mod envelope | Sound-shaping; increment 1 uses a simple gate ramp via `GainRamp` |
| Biquad filter, LFO (vibrato/tremolo) | DSP components; roadmap PR 3/4 |
| FM / analytic-waveform voices (one parameterized generator loop) | Separate generator family |
| Effects (chorus / delay / flanger, fractional-delay taps) | Roadmap PR 6 |
| Voice-stealing recycler policies (`IVoiceRecycler` + 2 impls) | Increment 1 allocates free-slot-or-drop; stealing is its own PR |
| Sample-interpolation quality modes (`ISampleInterpolator` + cubic) | Increment 1 is linear-only behind a seam |
| Real-time playback adapter, lock-free event queue | Separate platform package; queue lands when off-thread events become real |

## 3. Assumptions & Constraints

- **TFM:** `netstandard2.0;net8.0` (match core). Fast paths under `#if NET8_0_OR_GREATER`, scalar
  fallback for netstandard2.0. Increment 1 needs no SIMD; keep it scalar and portable.
- **Core stays strictly platform-independent** (#6487): no playback/OS/NAudio dependency. The engine
  is a pure producer.
- **Configurable-with-defaults** (#6487): block/frame size, max voices, sample rate, channels are an
  options object with sensible defaults — not hardcoded consts, not over-parameterized (YAGNI).
- **`ISynthesizer` IS an `IAudioSource`** — pulled by any consumer. The render hot path allocates
  nothing in steady state.
- **Internal format is 32-bit float, interleaved.** Narrowing to PCM happens sink-side (WAV writer),
  never in the engine. The engine emits *clean* float.
- **Single-threaded engine contract for increment 1** (see §9 threading): `Read` and the note methods
  are called from one thread or externally serialized. The lock-free event queue is the real-time PR.
- **Assumption (flag):** a synthesizer is an *endless* source — `Read` always fills the whole
  destination and returns its full length; it never signals end-of-stream via a short read, even with
  zero active voices (it emits silence). This differs from a finite source like a one-shot sample
  file. Confirmed consistent with `IAudioSource`'s contract (short read = EOS is a producer's choice a
  synth never makes).

## 4. Architectural Overview

The engine is a **pull producer** sitting behind the `IAudioSource` seam. A consumer (the
`OfflineRenderer`, a game loop, a device callback) pulls interleaved float blocks. Internally the
engine renders in fixed-size blocks; each block sums the mono output of every active voice, spreads
it across the output channels (pan), and passes the whole block through a single **finalize** step
that guarantees no non-finite or out-of-range sample leaves the engine.

```
                    NoteOn/NoteOff (same thread as Read, increment 1)
                          |
                          v
  +-----------------------------------------------------------------------+
  |  Synthesizer : ISynthesizer : IAudioSource                            |
  |                                                                       |
  |  Read(dest):  loop internal blocks of options.BlockFrames             |
  |    +---------------------------------------------------------------+  |
  |    | per block:                                                    |  |
  |    |   clear master accumulator (blockFrames x channels)           |  |
  |    |   for each active voice:                                      |  |
  |    |       mono = voice.RenderBlock(scratch)   <-- gen x GainRamp  |  |
  |    |       mix mono into master with pan (per-frame safe)          |  |
  |    |       if !voice.IsActive -> return slot to pool               |  |
  |    |   FINALIZE(master): NaN/Inf -> 0, clamp [-1,1]  <-- one choke |  |
  |    |   copy master -> dest slice                                   |  |
  |    +---------------------------------------------------------------+  |
  |                                                                       |
  |  Voice pool (fixed, options.MaxVoices) | patch-per-channel routing    |
  +-----------------------------------------------------------------------+
        ^                                   ^
        | StartVoice(key,vel)               | reads mono sample @ pitch
   IPatch (SamplePatch / Sf2Patch*)    IVoice (SamplePlaybackVoice)
                                            owns generator + GainRamp

  * Sf2Patch.StartVoice wiring = next PR; increment 1 proves the path with SamplePatch.
```

**Two anti-artifact invariants are the spine of the design** (§9 details each with the anti-pattern
it prevents):

- **INV-1 Continuous gain glide.** All voice mix-gain changes flow through one per-frame `GainRamp`
  primitive whose convergence math never references block size. Zipper/click is impossible because
  there is no "reach the target by end of block" contract to get wrong (legacy #6276 / #6217).
- **INV-2 NaN/Inf-safe, bounded output.** Every output frame passes through one finalize choke point
  that maps non-finite → 0 and clamps to [-1,1] before leaving `Read`. A bad sample can never reach a
  consumer or the PCM narrowing (legacy `SynthHelper.Clamp` passed NaN, #6272 B).

## 5. Components & Responsibilities

| Component | Owns / does | Does NOT own |
| --- | --- | --- |
| **`Synthesizer`** (`ISynthesizer`) | The pull `Read` loop; the internal block loop; the voice pool; note→voice routing; the mix accumulate; the finalize choke point; per-channel patch assignment. | Sample decoding; generator math; envelope shaping; interpolation quality; format narrowing. |
| **`SynthesizerOptions`** | Immutable config: `SampleRate`, `Channels`, `BlockFrames`, `MaxVoices`, each with a sensible default; validation. | Any runtime state. |
| **`GainRamp`** (per-frame smoother primitive) | A single scalar gain that advances one frame at a time toward a target via one update rule; the *only* place a mix gain changes. Block-size-independent by construction. | Envelope semantics (ADSR); pan; sample reading. |
| **`IVoice` impls — `SamplePlaybackVoice`** | Reads a mono sample region at a pitch-derived increment with linear interpolation; multiplies by its own per-frame `GainRamp`; renders a mono block it owns; `Release()` sets gain target to 0; `IsActive` false once the generator is exhausted (no loop) or the release ramp reaches 0. | Writing to the engine buffer; panning; mixing; format concerns (SRP fix vs legacy `VoiceParameters`). |
| **`SampleRegion`** (value object) | Immutable description of a playable region: sample buffer reference, start/end, loop start/end, loop mode, source sample rate, root key, pitch correction. | Playback state (read position lives in the voice). |
| **`IPatch` impls — `SamplePatch`** | Holds a mono sample buffer + a base `SampleRegion`; `StartVoice(key, vel)` builds a `SamplePlaybackVoice` with the pitch ratio for the played key and a velocity-derived target gain. | Voice lifecycle; mixing; SF2 semantics. |
| **`IVoiceRecycler`** (seam, *future PR* — born with its 2 impls) | Strategy for choosing a steal victim when the pool is full (oldest, quietest). | — (declared, not implemented in increment 1). |
| **`ISampleInterpolator`** (seam, *future PR* — born with linear+cubic) | Pluggable interpolation quality. | — (increment 1 inlines linear; extracted when the 2nd mode lands). |

**SRP correction vs legacy:** the legacy `VoiceParameters` was simultaneously voice state + the mixer
+ the writer into the engine buffer, and its gain ramp was wrong (#6217/#6276). Here a voice renders
*only* its own mono block; the engine *only* mixes and finalizes; the gain glide is *one* reusable
primitive. Three responsibilities, three owners.

## 6. Interactions & Data Flow

**Note-on flow (increment 1, synchronous):**
1. Consumer/sequencer calls `synth.NoteOn(channel, key, velocity)`.
2. Synthesizer resolves the patch for `channel`, calls `patch.StartVoice(key, velocity)`.
3. The patch computes the pitch ratio (played key vs region root key, plus tuning) and a
   velocity-derived target gain, constructs a `SamplePlaybackVoice`, and returns it.
4. Synthesizer places the voice in a free pool slot (or, when the stealing PR lands, steals a victim
   via `IVoiceRecycler`); records `(channel, key)` for note-off matching; sets the voice's `GainRamp`
   target to the note gain (it ramps up from 0 → click-free onset).

**Note-off flow:** `synth.NoteOff(channel, key)` finds the matching active voice(s) and calls
`Release()`, which sets the `GainRamp` target to 0. The voice keeps rendering its fade tail; when the
ramp reaches 0 it reports `IsActive == false` and the engine reclaims the slot next block.

**Pull / render flow (`Read`):**
1. `Read(dest)` computes `frames = dest.Length / channels`, then loops internal blocks of
   `min(remaining, options.BlockFrames)`.
2. Per block: clear the master accumulator; for each active voice call `RenderBlock(scratch)` (mono),
   then mix `scratch` into the master with the voice's pan (equal-power center in increment 1),
   advancing per-frame; reclaim any voice that went inactive.
3. **Finalize** the master block: map every non-finite sample to 0 and clamp to [-1,1] — the single
   choke point (INV-2). Copy the finalized master into the `dest` slice.
4. Return `dest.Length` (a synth never signals EOS; silence when no voices).

**Pull invariants (unchanged from core):** buffers are interleaved and frame-aligned; the engine
allocates its scratch + master + pool once at construction; steady-state `Read` allocates nothing.

## 7. Data Model (Conceptual)

| Entity | Key attributes | Ownership / lifetime |
| --- | --- | --- |
| **SynthesizerOptions** | sample rate, channels, block frames, max voices | Immutable; created once, held by the engine |
| **Voice slot** | active flag, channel, key, the `IVoice` instance | Pool-owned; reused across notes |
| **GainRamp** | current gain, target gain, per-frame increment/coefficient | Owned by a voice (one per voice for mix gain) |
| **SampleRegion** | buffer ref, start, end, loopStart, loopEnd, loop mode, source rate, root key, pitch correction | Immutable; shared by all voices a patch starts |
| **SamplePlaybackVoice runtime** | read position (fractional), pitch increment, GainRamp | Per sounding note; discarded on reclaim |

The **descriptor↔runtime split** from #6401 is preserved: `SampleRegion` (descriptor, immutable,
shared) vs the voice's fractional read position + ramp (runtime, per note). No `UnionData`-style
magic-index scratch — every runtime field is typed and named.

## 8. Contracts & Interfaces (Abstract)

Existing seams are used *as-is* (no signature changes): `IAudioSource.Read(Span<float>) → int`,
`IPatch.StartVoice(int key, int velocity) → IVoice`, `IVoice { bool IsActive; int RenderBlock(Span<float>); void Release(); }`,
`ISynthesizer { NoteOn(channel,key,velocity); NoteOff(channel,key); }`.

New logical contracts (described, not code):

- **`Synthesizer` construction:** takes `SynthesizerOptions` and a means to assign a patch to a
  channel (increment 1: a single default patch, or a per-channel assignment; keep minimal). Its
  `Format` is derived from `options.SampleRate` and `options.Channels`.
- **`GainRamp` semantics (INV-1):** exposes "set target", "advance one frame returning the current
  gain", and "current". Guarantee: successive advanced values differ by at most a bounded per-frame
  step derived from a smoothing time and the sample rate; the value converges toward target
  monotonically without overshoot; **block size is never an input** to any of this. Setting a new
  target mid-glide recomputes convergence from the *current* value (never a jump). This is the single
  primitive that makes zipper structurally impossible.
- **`SamplePlaybackVoice.RenderBlock` semantics:** fills the mono block with
  `interpolatedSample(readPos) * gainRamp.AdvanceFrame()` per frame; advances `readPos` by the pitch
  increment; on reaching region end with no loop, emits remaining silence and flips `IsActive` false;
  with a continuous loop, wraps `readPos` within [loopStart, loopEnd). Read position is bounded
  (loop-wrapped or clamped) — never an unbounded accumulator (legacy detune fix, #6272 B).
- **Finalize semantics (INV-2):** a pure per-sample map applied to the whole master block —
  `x → isFinite(x) ? clamp(x, -1, 1) : 0`. Applied exactly once per block, at the single point before
  the block leaves `Read`. No other code path narrows or clamps.
- **Pitch-increment semantics:** `increment = 2^((key - rootKey + tuningSemitones + centsCorrection/100)/12) × (sourceSampleRate / outputSampleRate)`, computed once at voice start; bounded fractional read position.

## 9. Cross-Cutting Concerns

**INV-1 — Continuous gain glide (prevents: zipper/click at block boundaries, legacy #6276/#6217).**
The legacy stereo mix ramp divided the increment by the block size (64) but iterated 32 frames, so
the glide half-completed then hard-jumped to target every block. Structural fix: gain is a **per-frame
smoothed scalar** (`GainRamp`) advanced one frame at a time with a single update rule; block size
never enters the math, so there is no "finish the ramp by end of block" contract to get wrong. The
same primitive serves note-on onset, note-off release, and (future) steal fade — one glide, reused.

**INV-2 — NaN/Inf-safe, bounded output (prevents: NaN pop reaching PCM, legacy `SynthHelper.Clamp`).**
A single finalize choke point sanitizes every output frame before it leaves the engine. Because the
engine emits clean float, *every* downstream consumer (WAV sink, game buffer, device callback) is
protected — not just one sink. Non-finite → 0; out-of-range → hard-clamped to [-1,1] so the later
float→PCM narrowing can never wrap.

**Declicked voice-stealing (design; polish deferred).** When the pool is full, stealing must never
hard-stop a victim. The design: the recycler marks the victim into a **fast release** (a short
`GainRamp` target-0 fade, e.g. a few ms); the victim finishes its fade through the same per-frame
glide (click-free by INV-1) and returns to the pool when its ramp reaches 0; the new note takes a
fresh slot. The effective sounding count may briefly exceed `MaxVoices` by the number of
fast-releasing voices — acceptable and bounded. Increment 1 allocates free-slot-or-drop (no steal
click possible because no steal happens); the recycler + fade is the follow-up PR.

**Single parameterized render loop (prevents: legacy 4-copy generator smell).** Increment 1 has one
sample-playback render loop. When analytic generators land, they are **one** loop parameterized by a
waveform function, not four copies. The voice's block loop is generator-agnostic (it multiplies
generator output by the gain ramp), so adding a generator family never duplicates the mix/gain code.

**Threading model.** Increment 1: **single-threaded contract** — `Read` and `NoteOn`/`NoteOff` run on
one thread or are externally serialized (documented invariant). This is *one defined model* (the
legacy defect was a lock mismatch across two locks on the free-voice set, #6272 E). When real-time
playback lands (off-thread note events from a sequencer/UI while the device thread pulls), the model
becomes a **lock-free single-consumer event queue**: note methods enqueue; `Read` drains at block
boundaries so events take effect at block granularity. No locks in the hot path, deterministic
timing. YAGNI: the queue is not built until a concurrent consumer exists.

**Observability / errors.** Core takes no logging dependency (reach floor). Misuse surfaces as typed
exceptions at construction (bad options) — never in the hot path. The hot path has no throw, no alloc.

**Consistency / idempotency.** `NoteOff` on an already-released or unknown note is a no-op.
Double `NoteOn` for the same key starts a second voice (polyphonic; SF2 exclusive-class muting is a
later concern). Render is deterministic given identical event ordering.

## 10. Quality Attributes & Trade-offs

| Attribute | How addressed |
| --- | --- |
| **Performance** | Alloc-free steady-state hot path (pool + scratch + master pre-allocated); block granularity amortizes per-block overhead; scalar-portable now, SIMD-ready under `#if NET8_0_OR_GREATER` later. |
| **Maintainability** | One primitive per concern (glide, finalize); one-type-per-file; generator-agnostic mix loop; seams (`IVoiceRecycler`, `ISampleInterpolator`) declared only with ≥2 impls (no empty stubs). |
| **Correctness/robustness** | The two artifact classes are invariants proven by test, not site fixes; bounded read position kills long-note detune. |
| **Portability** | No OS/logging deps; multi-target with scalar fallback. |
| **Scalability (polyphony)** | Fixed pool bounds CPU; stealing (future) degrades gracefully under overload. |

**Trade-offs / alternatives rejected:**
- **One-pole exponential smoother vs per-block linear ramp for `GainRamp`.** Chosen: a per-frame
  smoother whose math is block-size-independent (exponential one-pole *or* slew-limited linear behind
  the same contract — implementer's choice, both satisfy INV-1). Rejected: the legacy per-block "reach
  target by frame N" ramp, because a wrong denominator reintroduces the zipper. The whole point is to
  remove block size from the gain math.
- **`SamplePatch` in increment 1 vs wiring `Sf2Patch.StartVoice` now.** Chosen: prove the pipeline
  with a `SamplePatch` over a deterministic in-memory sample; wire SF2 zone→sample resolution as the
  immediate next PR. Rationale: SF2 zone/generator resolution (preset-zone → instrument →
  instrument-zone → generator layering → sample, with key/vel ranges and default generators) is a
  large, independently-testable unit; bundling it would tangle the review and risk encoding throwaway
  SF2 semantics. Splitting honors one-feature-per-PR. (Flagged as open question Q1 — decided by the
  architect, reversible.)
- **NaN guard in the engine finalize vs in each sink.** Chosen: in the engine, so every consumer is
  protected once. Rejected: per-sink guards (only protects that sink, re-introduces the class for
  game/playback consumers).
- **Immediate note application vs event queue in increment 1.** Chosen: immediate (single-thread
  contract), because no concurrent consumer exists yet (YAGNI). The queue is designed and named for
  the real-time PR.

## 11. Risks & Mitigations

| Risk | Mitigation |
| --- | --- |
| Gain glide still audibly steps if smoothing time too short | Test asserts consecutive-sample delta ≤ epsilon across the internal block boundary with a constant sample source; tune smoothing time so the ramp is inaudibly smooth. |
| NaN slips through a path that bypasses finalize | Finalize is the *single* exit; no other narrowing/clamp anywhere; test injects a NaN-emitting voice and asserts a clean, bounded output. |
| Fractional read-position precision drift on long notes | Read position is loop-wrapped/clamped and bounded, never an unbounded accumulator. |
| Voice pool exhaustion drops notes (increment 1) | Acceptable for increment 1 (documented); the stealing PR adds graceful degradation. |
| Multi-target drift | CI builds both TFMs, 0 warnings (XML docs required; §6.10 audit). |

## 12. Migration / Rollout Strategy

Greenfield; no migration. Legacy stays reference-only. The engine accretes capability PR-by-PR (§14),
each landing with its #6272-derived regression tests. `Sf2Patch.StartVoice` flips from
`NotImplementedException` to a real voice in the next PR without changing any seam signature.

## 13. Open Questions

1. **SF2 wiring timing (decided, reversible):** increment 1 proves the pipeline with `SamplePatch`;
   `Sf2Patch.StartVoice` wiring is the immediate next PR. If Toni wants a (minimal) real-SF2 note in
   increment 1 instead, say so and I'll re-scope — but I recommend the split.
2. **Loop handling in increment 1:** support continuous-loop `SampleModes` now, or no-loop one-shot
   only (loop as a follow-up)? Increment 1 designs `SampleRegion` with loop fields present and
   implements no-loop + continuous-loop (cheap, deterministic to test); ping-pong/other modes later.
3. **Master limiter shape:** hard clamp to [-1,1] (increment 1) vs a soft limiter later — hard clamp
   is the safety floor; a soft limiter is a quality follow-up. None of these block increment 1.

## 14. Implementation Guidance for the Next Agent

Milestones in build order (still architectural units, no code prescribed):

1. **`SynthesizerOptions`** — immutable config with defaults (SampleRate 44100, Channels 2,
   BlockFrames 64, MaxVoices 32) and validation.
2. **`GainRamp`** — the per-frame smoother primitive (INV-1). Block-size-independent convergence.
3. **`SampleRegion`** + **`SamplePlaybackVoice`** — mono sample read at pitch increment, linear
   interpolation, gain via `GainRamp`, bounded read position, no-loop + continuous-loop, `Release`.
4. **`SamplePatch`** — `IPatch` over a mono buffer + base region; `StartVoice` computes pitch ratio +
   velocity gain.
5. **`Synthesizer`** — pull `Read` block loop, voice pool (free-slot-or-drop), note routing, mix
   accumulate with pan, and the single **finalize** choke point (INV-2).
6. **Tests (NUnit):** (a) *flow* — NoteOn a patch, render through `OfflineRenderer`, assert
   non-silent, correct length, bounded; (b) *anti-zipper (INV-1)* — constant sample source, gain
   changing across the internal block boundary, assert no consecutive-sample discontinuity > epsilon
   at/around the boundary; (c) *NaN-safe (INV-2)* — a test-only NaN/Inf-emitting `IPatch`/`IVoice`
   fed to a real `Synthesizer`, assert output has no non-finite sample and all |samples| ≤ 1.

**Follow-up PR roadmap (each its own PR):** (i) SF2 zone→sample resolution wiring
`Sf2Patch.StartVoice`; (ii) ADSR envelope; (iii) biquad filter + LFO; (iv) `IVoiceRecycler` (+ oldest/
quietest) with declick fade-on-steal; (v) `ISampleInterpolator` (+ cubic); (vi) analytic generators
as one parameterized loop; (vii) effects with fractional-delay taps; (viii) real-time playback adapter
+ lock-free event queue.
