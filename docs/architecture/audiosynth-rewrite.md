# Architectural Document: Pooshit.AudioSynth — Clean-Room Rewrite

**Author:** Sarah (software architect) · **Date:** 2026-07-09
**Source task:** DiVoid #6398 · **Project:** #6128 · **Verdict that led here:** #6369 (REWRITE)
**Load-bearing contracts:** Design Contracts #1136 (KISS/DRY/YAGNI + Pre-Design Checklist) and Code Contracts #114 §0.
**Reference inputs:** legacy map root #6131 (concepts #6268–#6271), defect catalog #6272, legacy tree `C:\dev\claude\AudioSynth\Source` (reference only, not imported).

> This document governs the whole rewrite. Increment 1 (the scaffold that ships with this doc)
> implements only the central pull seam; every other component named here is a **future PR**,
> enumerated in §14. The "What does NOT go in" list (§2) is the YAGNI boundary for increment 1.

---

## 1. Problem Statement

The legacy CSharpSynthProject-derived synth does not have a coherent, buildable architecture
to import (verdict #6369: the canonical library does not compile standalone; the only buildable
variant targets a 2007 end-of-life framework; a half-finished refactor is duplicated across two
source trees). Its **conceptual bones and DSP math are good**; its **code is not salvageable**.

The goal is a modern, greenfield C# software synthesizer that:

- Turns a SoundFont (SF2) sound bank plus note events into a PCM audio stream.
- Runs both **live** (real-time playback) and **offline** (render to a buffer/file) through one core.
- Reaches the widest possible set of .NET consumers (Framework, Core, Unity) while still using
  modern DSP fast paths where the runtime supports them.
- Structurally designs OUT the legacy defect classes (clicks/zipper, untrusted-input crashes,
  concurrency races) rather than patching them one site at a time.

**Success criteria:** a sample-rate-agnostic pull-based core with a clean sink abstraction; SF2 as
the only v1 format with clean seams for SFZ/.bank later; the render hot path allocation-free; the
#6272 defect catalog re-usable as the regression suite; MIT attribution to the reference origin.

## 2. Scope & Non-Scope

**In scope for this design:** the full target architecture (core seams, engine model, format
boundary, cross-cutting strategy) plus the increment-1 scaffold that proves the central seam.

**In scope for increment 1 (this PR):**

- Solution + multi-targeted core library + test project.
- The central seam materialised: `AudioFormat`, `IAudioSource`, `IAudioSink`, `OfflineRenderer`,
  `InMemoryAudioSink`, `SineSource`.
- The top-level engine/instrument seams as **declarations only**: `ISynthesizer`, `IPatch`,
  `IVoice`, and the format-loader seam `ISoundBankLoader`.
- An end-to-end test proving a sine source flows through the renderer into a sink on the built TFMs.
- README (MIT attribution) + LICENSE.

**What does NOT go in increment 1 (explicit YAGNI boundary — each is a named future PR, §14):**

| Deferred item | Why deferred |
|---|---|
| SF2 parsing / `ISoundBankLoader` implementation | Largest surface; needs the untrusted-input boundary designed first. Seam only in increment 1. |
| Voice engine (`Synthesizer`, voice pool, recycler) | Depends on the block-render + mix path; built after the seam is proven. |
| Generators, envelope, filter, LFO (the DSP components) | Ported carefully from legacy reference; not needed to prove the seam. |
| Effects (delay/chorus/flanger) | Bus post-processing; last in the chain. |
| SFZ and `.bank` loaders | v1 format scope is SF2 only. Seam exists; no implementation (YAGNI per #6398). |
| NAudio adapter project | Optional real-time adapter; named future PR. Not a core dependency. |
| `MathF`/SIMD polyfill helper | No hot-path DSP exists yet in increment 1; the polyfill arrives with the first generator. |
| Component-family interfaces (`IVoiceRecycler`, `ISampleInterpolator`, `IAudioEffect`) | Born with their first two implementations in the engine/effects PRs, not as empty stubs now. |

## 3. Assumptions & Constraints

Confirmed constraints from #6398 (fixed — designed to, not relitigated):

1. **Reach floor `netstandard2.0`**, multi-targeted with a modern LTS for DSP performance.
2. **Pull-based core**, sample-rate-agnostic, driven by either a real-time sink or an offline renderer.
3. **Audio output behind a sink abstraction**; NAudio is one optional adapter, not a core dependency.
4. **v1 format scope = SF2 only**, with clean loader seams for SFZ/.bank (not implemented).
5. **MIT attribution** to CSharpSynthProject by Alex Veltsistas (task #6367).

Assumptions:

- Fully greenfield; no external consumer of the legacy public API to preserve (per #6398 and
  open question #4 in verdict #6369 — treated as confirmed by the clean-repo brief).
- Internal audio representation is 32-bit float, interleaved. Sinks convert to their delivery
  format (e.g. 16-bit PCM for WAV). Rationale: float keeps the mix path headroom-safe and
  NaN-guardable before the single narrowing cast, which is where the legacy pops originated.
- SDKs 8/9/10 are installed on the build machine (verified).

## 4. Architectural Overview

The system is a **pull pipeline**. Production is driven by demand: whoever needs audio (a real-time
device callback, or an offline renderer loop) pulls fixed blocks from an `IAudioSource`. The
synthesizer engine *is* an `IAudioSource`. Delivery is abstracted behind `IAudioSink`.

```
  note events                +-----------------------------+
  (MIDI / API) ───────────▶  |        Synthesizer          |  implements IAudioSource
                             |  (ISynthesizer)             |
                             |                             |
                             |  VoiceManager ── voices ──▶ mix path (declick + NaN-safe)
                             |     │  steal (IVoiceRecycler)                 │
                             |     ▼                                         ▼
                             |   IPatch ──StartVoice──▶ IVoice.RenderBlock ─▶ block mix (float)
                             +-----------------────────────+
                                        ▲                         │ pull (Read span)
             load (untrusted boundary)  │                         ▼
        Stream ─▶ ISoundBankLoader ─▶ patches           +-------------------+
                    (SF2 v1)                             |   pull driver     |
                                                         +-------------------+
                                                          /                 \
                                          real-time (device pulls)      OfflineRenderer
                                          NAudio adapter wraps           pumps source ─▶ sink
                                          IAudioSource                   (IAudioSink)
                                                                          /        \
                                                             InMemoryAudioSink   WavFileSink
```

**Two drive modes, one core:**

- **Offline:** `OfflineRenderer.Render(source, sink, frames)` pulls blocks from the source and
  pushes them to an `IAudioSink` (in-memory, WAV). Explicit driver loop.
- **Real-time:** the audio device is itself the puller. A thin NAudio adapter (future PR) wraps an
  `IAudioSource` and answers the device callback by pulling. No separate driver, no `IAudioSink`.

This is why the pull direction is the core seam: it is the one shape both modes share. The push
`IAudioSink` exists only for the offline/capture side, where something must *receive* the audio.

## 5. Components & Responsibilities

| Component | Owns | Does NOT own |
|---|---|---|
| `AudioFormat` (struct) | Sample rate + channel count; equality. | Buffer sizing, format conversion. |
| `IAudioSource` | The pull contract: fill a span, report count, signal end. | Threading, device I/O, mixing policy. |
| `IAudioSink` | The push contract: consume a span. | Producing audio, format conversion beyond its own delivery format. |
| `OfflineRenderer` | Pumping a source into a sink block-by-block, allocation-free steady state, format-match guard. | Real-time timing (that is the device's job). |
| `InMemoryAudioSink` | Capturing samples in memory for offline/test use. | Any DSP. |
| `SineSource` | A bounded-phase proof tone. | Anything beyond proving the seam (throwaway-grade utility, kept as a test/demo primitive). |
| `ISynthesizer` (seam) | Top-level engine contract: it is an `IAudioSource` plus `NoteOn`/`NoteOff`. | Format parsing, device I/O. |
| `IPatch` (seam) | Instrument archetype: start a runtime voice for a note. | Voice pooling, mixing. |
| `IVoice` (seam) | One sounding note: render its own mono block, release, report active. | Panning, summing, stealing (engine concern). |
| `ISoundBankLoader` (seam) | Turning an untrusted bank stream into patches, validating all input. | Playback, voice lifecycle. |

Single-responsibility note: the legacy `VoiceParameters` was both a data object and the owner of
the mixing methods that reached back into the global engine buffer (KISS/coupling defect, verdict
§3). The rewrite splits these: `IVoice` renders a **mono block it owns**; the engine's mix path
(future PR) owns panning and summing into the output buffer. A voice never touches the engine buffer.

## 6. Interactions & Data Flow

**Offline render (proven in increment 1):**

1. Caller builds a source (increment 1: `SineSource`; later: a configured `Synthesizer`) and a sink.
2. `OfflineRenderer.Render` verifies `source.Format == sink.Format`, allocates one reusable block
   buffer, then loops: `source.Read(slice)` → `sink.Write(written)` until the requested frame count
   is met or the source signals end (a short read).
3. Returns the frame count actually rendered.

**Real-time playback (future PR):** the NAudio adapter's device callback calls `source.Read(deviceBuffer)`
directly. The synthesizer, as the source, renders one engine block per pull: dispatch due note events,
advance each active voice one block, mix + declick + NaN-guard, narrow to the callback's buffer.

**Note lifecycle (future engine PR):** `NoteOn` → `VoiceManager` finds a free voice or steals via
`IVoiceRecycler` (with a declick fade on the victim) → `patch.StartVoice` → voice renders per block →
`NoteOff` → `voice.Release()` → envelope tail → voice returns to the pool.

**Contract semantics (the pull invariant):** buffers are interleaved and frame-aligned
(length is a multiple of `Channels`). `Read` returns the sample count written; a return equal to the
span length means "more available", a short return means end of stream. An infinite source (sine,
idling synth emitting silence) always fills fully.

## 7. Data Model (Conceptual)

Carrying forward the legacy **descriptor ↔ runtime** split (verdict §4, concept #6269), cleaned of
the `UnionData` magic-index scratchpad:

- **Descriptor (data, serializable):** declares an instrument's components — generator, envelope,
  filter, LFO parameters. Loaded from a bank; never executes DSP. (Future PR.)
- **Patch (`IPatch`):** an instrument built from descriptors + samples; starts voices.
- **Voice (`IVoice`):** runtime state of one sounding note. Replaces `UnionData[] pData` (untyped
  scratch indexed by bare magic numbers) with **typed, named per-archetype voice state** — the KISS
  fix for the legacy severe defect.
- **Sound bank:** a set of patches produced by a loader from one stream. Modelled simply as
  `IReadOnlyList<IPatch>` (no bespoke container type until a concrete need appears — KISS/YAGNI).

Ownership: a loader owns parsing→patches; the synthesizer owns patches→voices→mix; a voice owns only
its own note state and its mono output block.

## 8. Contracts & Interfaces (Abstract)

| Interface | Inputs | Outputs / semantics | Invariants |
|---|---|---|---|
| `IAudioSource.Read(Span<float>)` | interleaved, frame-aligned destination | count written; short = EOS | never writes past the span; count is a multiple of channels |
| `IAudioSink.Write(ReadOnlySpan<float>)` | interleaved block in the sink's format | consumed | does not retain the span beyond the call |
| `OfflineRenderer.Render(source, sink, frames)` | matching-format source + sink, frame count | frames rendered | throws on format mismatch; no per-block allocation |
| `ISynthesizer` | note events; is an `IAudioSource` | rendered mix on pull | thread-affinity of note events vs. render defined by the engine PR |
| `IPatch.StartVoice(key, velocity)` | note + velocity | a live `IVoice` | pure factory; no shared mutable state leak |
| `IVoice.RenderBlock(Span<float>)` | mono block destination | samples produced; 0 = finished | renders only its own note; never touches engine buffers |
| `ISoundBankLoader.Load(Stream)` | untrusted bank stream | patches | validates every size/offset; throws a typed "invalid bank" error, never an opaque NRE/OOM |

## 9. Cross-Cutting Concerns

The three legacy defect classes are designed out **structurally** (this is the core value of the
rewrite over fix-in-place; catalog #6272 B/D/E):

- **Clicks / zipper (class B):** one centralised mix path owns ramp math with the **correct
  denominator** (the legacy stereo bug divided the gain ramp by the block size but advanced it half
  as many times). Voice-stealing applies a **declick fade** to the victim rather than a hard stop.
  Effects use **fractional-delay** taps, not integer truncation.
- **NaN / denormal safety:** a NaN/denormal guard runs **before** the single float→PCM narrowing
  cast (the legacy `Clamp` passed NaN straight through to the 16-bit cast). Float internal format
  keeps the guard in one place.
- **Untrusted input (classes D/E):** all format readers treat input as hostile — validate declared
  sizes against actual stream length, allocate only after bounds-checking counts, use
  `InvariantCulture` for any numeric text parse, do explicit-endianness raw-byte reads, and throw a
  typed invalid-bank error. No parser trusts a size field or dereferences an unvalidated array.
- **Concurrency:** a single defined threading model — note events and rendering are marshalled so the
  voice pool is never read under one lock and written under another (the legacy free-voice race). The
  render path is single-threaded per source; event ingestion hands off via one documented mechanism.
  (Detailed model is an engine-PR deliverable; the constraint is set here.)
- **Observability / errors:** typed exceptions at the parse boundary; the core library takes no
  logging dependency (a `netstandard2.0` reach concern) — diagnostics surface as typed errors and
  return values the host can log.
- **Allocation discipline:** the render hot path allocates nothing. Buffers are caller-owned spans;
  drivers allocate their block buffer once. `InMemoryAudioSink` is a capture utility, not hot-path.

## 10. Quality Attributes & Trade-offs

### Decision 1 — TFM set: `netstandard2.0;net8.0`

**Chosen:** multi-target `netstandard2.0` (reach floor) + `net8.0` (modern LTS fast paths).

- KISS: **one** modern TFM, not two. `net8.0` already carries every fast path we need — `MathF`,
  `Span<T>` intrinsic, `System.Numerics` hardware intrinsics / SIMD. Adding `net10.0` as a third TFM
  now buys marginal perf for a narrower consumer base and triples the fast-path test matrix. YAGNI:
  a third TFM is a one-line addition **if** a consumer ever needs it; the future shape is trivial.
- Reach: `netstandard2.0` covers Framework 4.6.1+, Core, Unity, Mono — the "max reach" mandate.
- Fast-path strategy: `Span<T>` on `netstandard2.0` via the `System.Memory` package (intrinsic on
  `net8.0`); `MathF`/SIMD are `#if NET8_0_OR_GREATER` fast paths with a scalar `double`-based
  fallback on `netstandard2.0`. The polyfill ships with the **first generator** that needs it, not
  speculatively now (increment 1 has no hot-path DSP).
- **Trade-off named:** `netstandard2.0` consumers get the scalar fallback, not SIMD. Concrete cost:
  slower render on legacy runtimes. Probability the fast path matters there: low (Unity/Framework
  users prioritise reach over max throughput). Present cost of forcing everyone modern: losing the
  reach mandate outright. The reach floor wins; the fast path is additive on the modern TFM.

### Decision 2 — Pull core over push core

The core is a **pull** `IAudioSource`. Both drive modes (real-time device callback, offline loop)
are natural pullers; a push core would force the real-time device to be fed by a separate buffering
thread, adding a queue and its latency/backpressure complexity. Pull keeps the real-time path a
direct function call from the device callback. **Trade-off:** offline needs an explicit driver
(`OfflineRenderer`) to do the pulling — a ~40-line loop, cheaply justified.

### Decision 3 — Float internal, sink-side narrowing

Internal float mix with narrowing to PCM at the sink boundary. Keeps headroom and puts the single
NaN/denormal guard at one choke point (the legacy pops came from narrowing NaN mid-path). Trade-off:
float buffers are 2× the bytes of 16-bit — negligible at block granularity, bought back by safety.

### Decision 4 — Seams with ≥2 named implementations only

Every interface committed in increment 1 has ≥2 concrete implementations already named by the
verdict: `IPatch` → 5 archetypes; `ISoundBankLoader` → SF2/SFZ/.bank; `IVoice` → per-archetype
state; `ISynthesizer` → the engine plus test doubles. This clears the §4 "abstraction earns its
keep" bar. The component-family interfaces (`IVoiceRecycler`, `ISampleInterpolator`, `IAudioEffect`)
are **not** created as empty stubs now — they are born with their first two implementations in the
engine/effects PRs (YAGNI: an interface with no implementation in the same PR is indirection).

## 11. Risks & Mitigations

| Risk | Mitigation |
|---|---|
| `netstandard2.0` fast-path divergence (scalar vs SIMD produce different samples) | Golden-file tests assert bit-tolerance across TFMs; the scalar path is the reference. |
| Real-time threading model under-specified | The threading model is a first-class deliverable of the engine PR, constrained here (§9); not deferred silently. |
| SF2 parser is the largest untrusted surface | Built behind the validation boundary (§9) with the #6272 D-class defects as regression tests before any happy-path work. |
| Porting DSP math wrong while reading legacy reference | Each ported routine gets a regression test derived from the matching #6272 defect (the catalog is the safety net the original never had). |
| Multi-target build drift (one TFM silently breaks) | CI builds the whole solution (both TFMs) on every PR; increment 1 already builds both with 0 warnings. |

## 12. Migration / Rollout Strategy

Greenfield — no migration. The legacy tree stays reference-only (never imported). Rollout is the
incremental PR sequence in §14. Each increment is independently buildable and testable; the #6272
catalog accretes into the regression suite as each DSP component lands.

## 13. Open Questions

1. **Real-time priority for v1:** is live playback in scope for v1, or is offline WAV render the
   only v1 delivery (deferring the NAudio adapter + threading model)? The seam supports both; this
   only sets which driver PR comes first.
2. **Default polyphony / block size:** the legacy used a 64-frame block and a fixed voice pool. Keep
   64, or revisit for the modern SIMD width? Non-blocking; a `const` decision at engine-PR time.
3. **WAV sink in v1:** is a `WavFileSink` wanted in v1 (natural first real sink), or is
   `InMemoryAudioSink` plus the host's own writer enough? Cheap either way.

None of these block increment 1 or the SF2 loader PR.

## 14. Implementation Guidance for the Next Agent (roadmap)

Ordered by dependency. Each is its own PR (one feature per PR, per the PR-scope discipline).

1. **Increment 1 (this PR):** central pull seam + engine/instrument/loader seam declarations + proof
   test + README/LICENSE. *(Done.)*
2. **SF2 loader (`ISoundBankLoader` for SF2):** the untrusted-input boundary + SF2 chunk parsing →
   patches. Bring the #6272 D-class defects in as regression tests first. Largest single PR.
3. **DSP components:** generators (one parameterised analytic loop, not four copies — kills the
   legacy DRY defect), envelope, biquad filter, LFO — ported carefully from legacy reference, each
   with its #6272-derived regression test. Introduces the `MathF`/SIMD fast-path + `netstandard2.0`
   polyfill.
4. **Voice engine:** `Synthesizer` (as `ISynthesizer`), voice pool, `IVoiceRecycler` (+ its two
   policies), the centralised declicking + NaN-safe mix path. Kills defect class B structurally.
5. **Real-time adapter:** NAudio adapter project wrapping `IAudioSource` (optional dependency).
   Sequence vs. the WAV sink per open question #1.
6. **Effects:** `IAudioEffect` (+ its implementations) with fractional-delay taps.
7. **Later formats:** SFZ and `.bank` loaders behind the existing seam (post-v1).

---

## Appendix A — Pre-Design Checklist (Design Contracts #1136 §5, walked verbatim)

**KISS / DRY / YAGNI**
- [x] No new type mirroring an existing type. (No mirror enums/constants; single `AudioFormat`.)
- [x] No abstraction with one implementation and no concrete second. Every committed interface has
  ≥2 named implementations (§10 Decision 4); component-family interfaces deferred until their
  implementations exist.
- [x] No element justified by "might need later" without a concrete X. The deferred list (§2) names
  the concrete future PR for each.
- [x] No deprecation period / feature flag / compatibility shim / transition window. (Greenfield.)
- [x] No "do not extract / inline N sites" decisions in increment 1 (no duplicated blocks;
  `SineSource` is the single analytic loop, and the four-copy legacy generator DRY defect is called
  out to be built as one parameterised loop in PR 3).

**Existing systems first**
- [x] Audited: no existing system to extend (greenfield repo). New layers are the product itself.
- [x] Each new seam's concrete reason to exist is named (the ≥2-implementations table, §10).
- [x] No speculative persisted data (no bank container type invented; `IReadOnlyList<IPatch>` reuses
  the patch seam).

**Configurability**
- [x] No config knobs. `OfflineRenderer.BlockFrames` and `SineSource` amplitude are named `const`/
  parameters with defaults, not configuration surfaces. No "for future tuning" knobs.

**Less is better**
- [x] Delete/merge/inline check run: `SilenceSource` dropped (SineSource alone proves the seam);
  NAudio stub project dropped from increment 1 (named future PR); `MathF` polyfill dropped
  (no hot-path DSP yet).
- [x] Trade-offs named explicitly where a simpler alternative was rejected (§10 Decisions 1–4).
- [x] No compromise shapes; increment 1 commits the minimal proven seam, not a half-built engine.

**Document discipline**
- [x] Cites Code Contracts #114 and Design Contracts #1136 as load-bearing.
- [x] Out-of-scope items listed explicitly (§2 deferred table).
- [x] No multi-paragraph rationale for things that obviously stay.
- [x] No predecessor design to supersede (first design for this repo).

## Appendix B — Legacy defect classes this architecture designs out

| Class (#6272) | Legacy root cause | Structural fix in this design |
|---|---|---|
| B — clicks/zipper | ramp math hand-inlined per mix method, wrong denominator; hard voice-steal; integer effect taps | one centralised declicking mix path; declick fade on steal; fractional-delay effects (§9, PR 4/6) |
| D — untrusted-input crashes | every reader trusts sizes/arrays inline | one validation boundary; typed invalid-bank errors (§9, PR 2) |
| E — concurrency/portability | free-voice read/write under mismatched locks; culture-sensitive parse; endianness assumptions | one defined threading model; `InvariantCulture`; explicit-endianness reads (§9, PR 2/4) |
| A — 24-bit sign extension (dual-site) | identical wrong shift duplicated in two decoders | one decode routine (DRY), regression-tested (PR 2/3) |
