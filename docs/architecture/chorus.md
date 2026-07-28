# Architectural Document: Chorus — the engine's second effect

**Repo copy (authoritative):** `docs/architecture/chorus.md` on branch `feature/chorus` in `C:\dev\claude\AudioSynth\Pooshit.AudioSynth`. A DiVoid `documentation` node mirrors this.

**Author:** Sarah (software architect) · 2026-07-28 · Source task **#7188** · Project **#6128** · Map root **#6708**.
**Contracts:** Design Contracts #1136, Code Contracts #114 §0/§1/§5.5, PR-shape #1165, Architecture #6401 §10 (the ≥2-impl interface rule).
**Precedent (mirrored):** reverb DSP #7169, reverb master-insert #7164, **per-channel reverb-send #7170** (the send-bus + `Process(send, master)` + additive-combination pattern this design twins).

---

## 1. Problem Statement

The engine has exactly one effect: a stereo algorithmic reverb (space/ambience), routed as an opt-in per-channel send bus honouring MIDI CC91 + SF2 gen-16 additively. It has **no chorus** — the detuned-doubling effect that thickens and stereo-widens a sound. Many GM songs drive chorus depth via CC93, and SF2 presets carry a per-instrument chorus send via generator 15. The goal: add a **stereo modulated-delay chorus** as the engine's second effect, architecturally a twin of the reverb send path, so instruments can be individually thickened/widened, GM-accurately, without disturbing any existing render.

**Success criteria**
- An opt-in stereo chorus that adds detuned-doubling shimmer + stereo width to voices that send to it.
- Per-channel/per-voice send driven by **CC93 (channel) + SF2 gen-15 (region), combined ADDITIVELY and clamped to [0,1]** — never multiplicatively (the reverb-bug lesson).
- A `GlobalChorus` option reproducing a uniform master-chorus (every voice sends fully).
- **Structural dry-passthrough:** `Chorus == null` OR every chorus send at 0 ⇒ the render is **bit-identical** to the pre-chorus engine.
- BIBO-stable by construction; `Read` allocation-free; `Finalize`/`ApplyMasterBus` (INV-2) untouched.

## 2. Scope & Non-Scope

**In scope**
- A `Chorus` effect (modulated-delay DSP) + an immutable `ChorusSettings` parameter type.
- A second per-channel-weighted stereo **send bus** in `Synthesizer`, plus a second effect stage in the block loop.
- The CC93 → `SetChannelChorusSend` seam; SF2 gen-15 → `SampleRegion.ChorusSend` → `IVoice.ChorusSend`; **additive/clamped** combination.
- `SynthesizerOptions.Chorus` (a `ChorusSettings?`, default `null`) + `SynthesizerOptions.GlobalChorus` (default `false`).
- Extraction of a minimal **`IAudioEffect`** seam (Reverb + Chorus as the two impls) — see §10.
- MidiRender opt-in; a full proof suite (synth-level routing + a programmatic CC93 render proof + bit-identical regression).

**Out of scope (explicitly)**
- Delay/echo, EQ, flanger (a chorus variant — a later ticket if wanted), phaser.
- Chorus **feedback** (would push the effect toward flanger; omitted for KISS + unconditional stability — see §4/§10).
- Voice-stealing (#7183); large-soundfont handling; runtime-tunable global level.
- Any generic "effects-chain / effect-registry framework." The `IAudioEffect` interface is extracted (§10) but **no** registry/pipeline abstraction is built — the two effect stages stay explicit and named in the block loop.
- Correcting the stale "multiplicatively" wording in the reverb XML docs (`IVoice.ReverbSend`, `ISynthesizer.SetChannelReverbSend`, `SampleRegion.ReverbSend`) — pre-existing, reverb's concern, flagged in §13.

## 3. Assumptions & Constraints

| # | Assumption / Constraint | Confidence |
|---|---|---|
| A1 | Stereo-only, exactly as reverb: chorus is constructed only when `Channels == 2` and `Chorus != null`. Non-stereo output leaves the master path untouched. | High (mirrors reverb) |
| A2 | `ControllerType.ChorusLevel = 93` and `Sf2GeneratorType.ChorusEffectsSend = 15` already exist in the enums — no format work needed. | Verified in source |
| A3 | No `.mid` or `.sf2` assets are committed to the repo (reverb's render-proof test skips gracefully when dev-tree assets are absent). The primary chorus proof must therefore be **asset-free** (synth-level + a programmatically-built CC93 sequence). | Verified (no assets found) |
| A4 | GM1 default for CC93 (chorus depth) is **0** — chorus is OFF unless a song raises CC93 (unlike reverb's CC91 GM default of 40/127). | High (GM spec) |
| A5 | Offline (non-realtime) render is the only consumer; a handful of sine evaluations per frame across ≤4 chorus voices is acceptable cost. | High |
| A6 | The two `ISynthesizer` implementers are `Synthesizer` (production) and `RecordingSynthesizer` (test helper); both must gain `SetChannelChorusSend`. | Verified in source |

## 4. Architectural Overview

Chorus is a **parallel send-return effect**, structurally identical to the reverb send path. During the voice-mix loop each voice, in addition to its dry contribution to `master` and its weighted contribution to the reverb send bus, adds a **chorus-send-weighted** copy into a second stereo send bus. After the loop, the `Chorus` effect reads that send bus, computes a wet (detuned, stereo-widened) signal, and **adds** it into `master` — never touching the dry already carried there. Then the existing reverb stage, master soft-clip, and finalize run unchanged.

```
                 per voice (in the mix loop)
  voice mono ──► × channelGain ──► × equal-power L/R ──►  master  (dry, unchanged)
        │
        ├─ × clamp01(chChorusSend[ch] + voice.ChorusSend) × L/R ─►  chorusSendBus
        └─ × clamp01(chReverbSend[ch] + voice.ReverbSend) × L/R ─►  reverbSendBus

                 after the loop (fixed order)
  chorusSendBus ─►  Chorus.Process(send, master)  ─► master += chorus wet
  reverbSendBus ─►  Reverb.Process(send, master)  ─► master += reverb wet
  master ─► ApplyMasterBus (soft-clip) ─► Finalize (NaN/Inf guard, INV-2) ─► out
```

Both effects implement `IAudioEffect.Process(ReadOnlySpan<float> send, Span<float> master)`. The routing is **not** collapsed into a generic loop — each effect keeps its own send bus, its own channel-send array, and its own CC/gen source — but the *effect-invocation contract* is the shared, named `IAudioEffect` seam.

**Effect ordering (chorus stage before reverb stage).** In the default per-channel mode both send buses are built from the *dry* voice signal during the loop, so the two `Process` calls only *add* into `master`; addition commutes and order is immaterial. Order matters only in the double-global edge case (both `GlobalChorus` and `GlobalReverb` true, both reading `master` directly); there the fixed order is *chorus then reverb* (reverb reverberates the chorused master), which is the conventional chain and is documented, not specially optimised (YAGNI). The reverb's existing `GlobalReverb` bit-identical guarantee is untouched because those tests run with `Chorus == null`.

**The DSP (modulated-delay chorus).** The stereo send is summed to a mono feed (mirroring reverb's `InputGain` mono-sum) into a single circular delay buffer. `VoiceCount` (1–4) chorus voices each read the buffer at a **fractional, LFO-modulated** delay time `baseDelay + depth·lfo(t)`, evaluated with linear interpolation between adjacent taps. Each chorus voice runs a slow **sine LFO** (~0.3–2 Hz) at a distinct phase (voice *k* at phase *k/N*); the **left** read uses the voice's LFO phase and the **right** read uses that phase plus a fixed quadrature offset (90°), decorrelating L/R for stereo width from a single mono buffer. The voice reads are summed, normalised by voice count, scaled by `Wet`, and added to `master`. **No feedback path** ⇒ the effect is a bounded finite sum of bounded inputs ⇒ BIBO-stable by construction, with no clamp and no reliance on the master soft-clip. `Wet == 0` ⇒ nothing is added ⇒ structural dry passthrough.

## 5. Components & Responsibilities

| Component | Owns | Does NOT own |
|---|---|---|
| **`ChorusSettings`** (new, `Synthesis/`, public sealed, immutable) | The chorus parameter surface: `RateHz`, `DepthMs`, `BaseDelayMs`, `Wet`, `VoiceCount`; clamps that keep params in valid, stable ranges; a `Default` preset. | DSP state, buffers, LFO phase. |
| **`Chorus`** (new, `Synthesis/`, public sealed, implements `IAudioEffect`) | The modulated-delay DSP: ctor-allocated circular delay buffer + per-voice LFO state; `Process(send, master)` computing wet and adding it to master; interpolation; stereo L/R decorrelation. | Send-weight computation, channel state, master soft-clip. |
| **`IAudioEffect`** (new, `Synthesis/`, public interface) | The send-return effect contract: one method `Process(ReadOnlySpan<float> send, Span<float> master)` and its documented invariants (allocation-free, alias-safe, wet-added-into-master, dry-untouched). | Any concrete DSP; any routing/registry. |
| **`Reverb`** (changed: `: IAudioEffect`) | Unchanged behaviour; now nominally implements the named contract it already satisfied structurally. | — |
| **`SampleRegion`** (changed) | A new immutable `ChorusSend` property (gen-15, [0,1]); ctor param `chorusSend` defaulting to **0f**. | Combination with channel send (mix-time concern). |
| **`IVoice`** (changed) | A new `ChorusSend` getter (static per-voice chorus send, [0,1]; 0 for inactive voices). | — |
| **`ISynthesizer`** (changed) | A new `SetChannelChorusSend(int channel, float level)` seam. | — |
| **`Synthesizer`** (changed) | A `channelChorusSend[16]` array; the second send bus (`chorusSendBus`, ctor-sized when per-channel chorus is active); building the chorus effect from options; the chorus stage in `Read`; the additive/clamped per-voice send weight. | The DSP itself. |
| **`SynthesizerOptions`** (changed) | `Chorus` (`ChorusSettings?`, default null) + `GlobalChorus` (bool, default false), validated/stored like `Reverb`/`GlobalReverb`. | — |
| **`MidiSequencer`** (changed) | Mapping CC93 → `SetChannelChorusSend(ch, Data2/127)`; GM-reset seeding CC93 to **0** for all 16 channels. | — |
| **`Sf2RegionResolver`** (changed) | `BuildChorusSend(zone, globalZone)` reading gen-15 (0..1000 → [0,1], absent → 0); passing it to `SampleRegion`. | — |
| **`RecordingSynthesizer`** (test helper, changed) | Recording `SetChannelChorusSend` calls (`ChannelChorusSendCalls`). | — |
| **`MidiRender/Program.cs`** (changed) | Opting into `ChorusSettings.Default` alongside `ReverbSettings.Default` for the CLI deliverable. | — |
| **Voice impl** (whichever concrete `IVoice` exists, e.g. sample voice) (changed) | Surfacing `ChorusSend` from its `SampleRegion` (0 when inactive/silent), mirroring `ReverbSend`. | — |

Every new type is one-type-per-file. `CombFilter`/`AllpassFilter` are reverb-specific and are **not** reused by chorus (a modulated delay is a different primitive); no false DRY is forced.

## 6. Interactions & Data Flow

**Per-channel (default) flow**
1. MIDI CC93 on channel *ch* → `MidiSequencer` → `synth.SetChannelChorusSend(ch, value01)` → stores into `channelChorusSend[ch]` (live-read per block).
2. SF2 gen-15 on a region → `Sf2RegionResolver.BuildChorusSend` → `SampleRegion.ChorusSend` → carried by the voice as `IVoice.ChorusSend`.
3. In `Read`'s voice loop, for each active voice, after the dry mix and (if present) the reverb send: compute `w = clamp01(channelChorusSend[ch] + voice.ChorusSend)`; if `w != 0`, add `pre·w` (pre = post-gain sample) into `chorusSendBus` using the **same equal-power L/R pan gains** as the dry mix.
4. After the loop: `chorus.Process(chorusSendBus, master)` (adds wet), then `reverb.Process(reverbSendBus | master, master)` as today.

**Global flow** (`GlobalChorus == true`): the chorus send bus is not built; `chorus.Process(master, master)` — every voice sends fully, master-insert semantics, the aliased read-before-write path (mirrors `GlobalReverb`).

**Contract sequence for `Chorus.Process(send, master)`** (per interleaved stereo frame): read `sendL`, `sendR` into locals *before* any write to that frame's master (alias-safe for the global case); push `(sendL+sendR)·inputGain` into the delay buffer; for each chorus voice advance its LFO by one frame, compute L/R fractional delay taps, interpolate, accumulate; write `master[i] += wetL`, `master[i+1] += wetR`. Delay-buffer and LFO state persist across calls so the modulation is continuous across block boundaries.

## 7. Data Model (Conceptual)

- **ChorusSettings** — value-config entity: `RateHz` (LFO frequency), `DepthMs` (± delay sweep), `BaseDelayMs` (centre delay), `Wet` ([0,1] mix of the added wet), `VoiceCount` (1..4). Immutable; owns its clamps; provides `Default`.
- **Per-channel chorus send** — 16 scalars in [0,1], owned by `Synthesizer`, sourced from CC93, live-read.
- **Per-voice chorus send** — one scalar in [0,1] per `SampleRegion`/`IVoice`, sourced from gen-15, static for the voice's life.
- **Effective send weight** — derived, not stored: `clamp01(channelChorusSend[ch] + voice.ChorusSend)`, recomputed per voice per block.

Ownership: CC93 depth is the channel's; gen-15 is the instrument/region's; the effective weight is the engine's mix-time derivation. This mirrors the reverb entities exactly.

## 8. Contracts & Interfaces (Abstract)

| Interface / member | Input | Output / effect | Invariants |
|---|---|---|---|
| `IAudioEffect.Process(send, master)` | interleaved stereo `send`; interleaved stereo `master` (equal length, multiple of 2) | wet computed from `send`, **added** into `master`; dry (already in `master`) untouched | allocation-free; alias-safe (`send` may equal `master`); length-validated; state persists across calls |
| `Chorus.Process(...)` | as above | detuned-doubled, stereo-widened wet added | `Wet == 0` ⇒ no-op (structural passthrough); BIBO-stable (no feedback); no NaN introduced for finite input |
| `ISynthesizer.SetChannelChorusSend(ch, level)` | channel [0,15], level (expected [0,1]) | stores channel chorus send; applies to current + future voices, live-read per block | channel out of range ⇒ `ArgumentOutOfRangeException` (mirrors `SetChannelPan`/`SetChannelReverbSend`) |
| `IVoice.ChorusSend` | — | static per-voice send [0,1]; 0 when inactive | immutable for the voice's life |
| `SampleRegion.ChorusSend` | ctor param `chorusSend`, default **0f** | region's gen-15 send | clamped to [0,1] semantics by the resolver |
| `ChorusSettings(...)` | raw params | clamped, immutable settings | `BaseDelayMs > DepthMs` after clamp so the modulated delay never reaches ≤ 0; `RateHz`, `Wet`, `VoiceCount` clamped to valid ranges |

**Additive combination — the load-bearing rule (RULE 1).** The per-voice chorus send weight is `clamp01(channelChorusSend[ch] + voice.ChorusSend)`, **additive**, never multiplicative. CC93 is the primary send; gen-15 is an additive per-instrument bias. gen-15 absent-default is SF2's literal 0, and most GM soundfonts leave it 0 — multiplying would let gen-15=0 nullify CC93 and render chorus dead, exactly the reverb defect already fixed (#7170). `SampleRegion.ChorusSend`'s ctor default is **0f** (not reverb's inherited 1f), so the additive contract and the SF2 default agree at 0 with no special-casing — a small consistency win over the reverb plumbing.

## 9. Cross-Cutting Concerns

- **Stability:** no feedback → unconditionally BIBO-stable; the master soft-clip and `Finalize` remain pure safety nets, never load-bearing for chorus. This is *simpler* than reverb's feedback-clamp story.
- **Allocation:** delay buffer and per-voice LFO/tap arrays are ctor-allocated (sized from `sampleRate`, `BaseDelayMs+DepthMs`, `VoiceCount`); `chorusSendBus` is ctor-allocated only when per-channel chorus is active (`Chorus != null && Channels == 2 && !GlobalChorus`). Steady-state `Read` allocates nothing (INV preserved).
- **Determinism / regression:** `Chorus == null` ⇒ no chorus field, no send bus, no stage → byte-for-byte the pre-chorus render. All-sends-0 in per-channel mode ⇒ `clamp01(0+0)=0` for every voice → empty send bus → `Process` over silence adds a zero (structurally, wet of silence through a linear delay = silence) → bit-identical. Both guarantees are explicit test cases.
- **Concurrency:** none new; the engine is single-threaded per `Read`, matching reverb.
- **Error handling:** argument validation mirrors existing seams (channel range, span length/multiple-of-2, positive sample rate, non-null settings).
- **Observability:** `RecordingSynthesizer` records chorus-send calls for sequencer-level assertions, mirroring reverb.

## 10. Quality Attributes & Trade-offs

**The `IAudioEffect` decision (RULE: #6401 §10 needs ≥2 impls) — EXTRACT, minimally.**
The reverb design deliberately deferred an effect interface "until a second effect exists, shaped by the real impl, not guessed." That moment is now. Reverb and Chorus share an **identical** send-return contract — `Process(ReadOnlySpan<float> send, Span<float> master)`: compute wet from send, add to master, allocation-free, alias-safe. The threshold is met exactly, and the churn is near-zero: `Reverb` already implements this method verbatim, so extraction is "add a one-method interface file + `: IAudioEffect` on `Reverb`," no behaviour change. The interface earns itself by (a) satisfying the ≥2-impl rule precisely, (b) giving the send-return invariants (alias-safety, additive-into-master, allocation-free) a single named home both effects must honour, and (c) being the exact seam reverb pre-planned.

**Bounded, deliberately.** Extraction stops at the interface. The `Synthesizer` does **not** collapse its two effect stages into a generic `IAudioEffect[]` pipeline, because the routing is genuinely per-effect: each effect has its own send bus, its own `channelXSend[16]` array, and its own CC/gen source. A generic loop would abstract only the two-line `Process` call while forcing the divergent send-bus construction into awkward uniformity — negative DRY. So: interface yes (names the shared contract, meets the rule), framework no (the reverb brief's explicit warning against a speculative effects framework). The `Synthesizer` may type its effect fields as `IAudioEffect?` since it only calls `Process`, but constructs them concretely.

**Other trade-offs**
| Decision | Chosen | Alternative rejected | Why |
|---|---|---|---|
| DSP | Modulated fractional delay, no feedback | Feedback chorus | Feedback → flanger character + a stability clamp; YAGNI + KISS. No feedback = unconditional stability. |
| LFO | A small internal sine LFO in `Chorus` | Reuse `ModulationLfo` | `ModulationLfo` is voice-scoped: bypass keyed on pitch/vol/filter depths, an onset delay, triangle shape — none of which fit an always-on stereo effect LFO. Forcing it is a worse fit than a few lines of internal phase accumulation. DRY does not win when responsibilities differ. |
| Stereo width | One mono delay buffer read at L phase and R = L + 90° | Two independent L/R delay lines | Single-buffer quadrature read gives decorrelated width at half the memory and mirrors reverb's mono-sum send. |
| gen-15 default | `SampleRegion.ChorusSend` ctor default **0f** | Inherit reverb's 1f | Additive contract + SF2 default agree at 0 → no impotence special-case; cleaner than the reverb plumbing. |
| Send combination | Additive/clamped | Multiplicative | RULE 1 — the reverb bug. gen-15=0 (SF2 default) must not nullify CC93. |
| Effect opt-in | `ChorusSettings?` null default + `GlobalChorus` | Always-on | Bit-identical regression by default; mirrors reverb. |

## 11. Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Modulated delay reads out of buffer bounds (delay < 0 or > buffer). | `ChorusSettings` clamps `BaseDelayMs > DepthMs` (delay stays > 0) and the buffer is sized `ceil((BaseDelayMs+DepthMs)/1000·sampleRate)+interp guard+1`. Unit test the extremes of the sweep. |
| Zipper/aliasing artefacts from coarse delay modulation. | Linear-interpolated fractional taps + a slow sine LFO; deliberately low rate range. |
| Accidental non-bit-identical when chorus off (regression). | Structural: no field/bus/stage when `Chorus == null`; explicit bit-identical regression test (mirrors `ReverbSendRoutingTests`). |
| Double-global order ambiguity (both effects global). | Fixed documented order (chorus then reverb); reverb's own guarantees hold because its tests use `Chorus == null`. |
| No committed corpus `.mid` carries CC93 → no real-song proof. | Primary proof is asset-free synth-level routing + a **programmatically-built CC93 sequence** render test; the corpus render is best-effort skip-if-absent, mirroring `ReverbRenderProofTests`. |
| Stale "multiplicatively" reverb docs invite a wrong mental model. | Chorus docs state additive explicitly; the stale reverb wording is flagged as a separate follow-up (§13), not bundled. |

## 12. Migration / Rollout Strategy

Purely additive; no migration. Chorus is off by default (`Chorus == null`), so every existing test and render is bit-identical until a caller opts in. The `IAudioEffect` extraction is behaviour-preserving. Rollout is the single feature PR itself.

## 13. Open Questions

1. **Chorus default preset values** — proposed `Default` = `RateHz 0.8`, `DepthMs 3.0`, `BaseDelayMs 20.0`, `Wet 0.5`, `VoiceCount 3`. These are conventional; confirm the taste (especially `Wet 0.5`, which is a strong parallel blend) or accept the proposal. **Recommend: accept.**
2. **Stale reverb XML docs** — `IVoice.ReverbSend`, `ISynthesizer.SetChannelReverbSend`, and `SampleRegion.ReverbSend` still say "combined multiplicatively" (pre-additive-fix wording). Out of scope here (one-feature-one-PR). File a tiny reverb doc-fix follow-up? **Recommend: yes, separate task.**
3. **CC93 GM reset** — proposed: GM-reset all 16 channels' chorus send to 0 explicitly (mirrors the reverb reset shape, testable via `RecordingSynthesizer`). Confirm, or leave it implicit (array default 0). **Recommend: explicit reset.**

## 14. Implementation Guidance for the Next Agent

Ordered milestones (one feature, one PR; commit this doc first). All new types public-sealed/one-per-file; every member XML-documented; **zero body comments**; XML summaries within the size gate (§6.10).

1. **Contract seam.** Add `IAudioEffect` (one method `Process(ReadOnlySpan<float> send, Span<float> master)` with the send-return invariants documented). Mark `Reverb : IAudioEffect` (no body change).
2. **Settings.** Add `ChorusSettings` (immutable, clamped, `Default` preset per §13.1) mirroring `ReverbSettings`' shape.
3. **DSP.** Add `Chorus : IAudioEffect` — ctor-allocated mono delay buffer + per-voice sine-LFO state; `Process` per §6/§8. No feedback. Alias-safe read-before-write.
4. **Send plumbing (region → voice).** Add `SampleRegion.ChorusSend` (ctor param default 0f) + `IVoice.ChorusSend`; surface it from the concrete voice (0 when inactive). Add `Sf2RegionResolver.BuildChorusSend` (gen-15, 0..1000 → [0,1], absent → 0) and pass it into `SampleRegion`.
5. **Send plumbing (channel).** Add `ISynthesizer.SetChannelChorusSend`; implement in `Synthesizer` (`channelChorusSend[16]`, range-checked) and in `RecordingSynthesizer` (record calls).
6. **Options + wiring.** Add `SynthesizerOptions.Chorus` + `GlobalChorus`. In `Synthesizer`: build `chorus` when `Chorus != null && Channels == 2`; allocate `chorusSendBus` when per-channel; add the additive/clamped chorus-send mix in the voice loop and the chorus stage before the reverb stage in `Read`.
7. **MIDI.** In `MidiSequencer`: map `ControllerType.ChorusLevel (93)` → `SetChannelChorusSend(ch, Data2/127)`; GM-reset CC93 to 0 for all channels.
8. **CLI deliverable.** `MidiRender/Program.cs`: opt into `ChorusSettings.Default`.
9. **Proofs** (deterministic-first, mirroring `ReverbSendRoutingTests` + `MidiSequencerChannelReverbSendTests`):
   - CC93 gates whether a channel reaches the chorus (send=1 → wet present; send=0 → exact dry).
   - Additive: (CC93>0, gen15=0) reaches chorus; (CC93=0, gen15>0) reaches chorus; both-0 → dry; sums>1 clamp identically to saturated.
   - All-sends-0 per-channel ⇒ bit-identical to `Chorus == null`.
   - `GlobalChorus` reproduces per-channel-all-sends-1 bit-for-bit.
   - `SetChannelChorusSend` out-of-range throws.
   - `Sf2RegionResolver` gen-15 present/absent → `SampleRegion.ChorusSend` unit.
   - `MidiSequencer` GM-reset + CC93 mapping + unrelated-CC ignored (via `RecordingSynthesizer`).
   - **Render proof (asset-free):** build a `TimedMessageSequence` programmatically with a CC93 event + notes; render through a sample patch with chorus opt-in; assert the chorused-channel output **differs** from the same render with CC93=0. Corpus render optional/skip-if-absent.
   - `Chorus.Process` DSP unit: `Wet==0` ⇒ master unchanged; delay-sweep extremes stay in bounds.

**Deliverable proof.** Both TFMs 0-warning; `dotnet test` green (run FOREGROUND, `timeout 600000`). Confirm `Chorus == null` / all-sends-0 ⇒ bit-identical. §6.10 self-audit on committed code.

**Chain:** Sarah (this doc + branch `feature/chorus` pushed) → john-backend-dev (implement on the branch, one PR incl. this doc) → jenny-qa-reviewer.
