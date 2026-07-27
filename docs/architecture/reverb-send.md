# Architectural Document: Per-Channel Reverb Send (CC91 + SF2 gen-16)

**Author:** Sarah (software architect) · 2026-07-27 · Source task **DiVoid #7165** (PR 17) · Project #6128 · Map root #6708.
**Repo copy (authoritative):** `docs/architecture/reverb-send.md` on branch `feature/reverb-send`.
**Contracts (load-bearing):** Design Contracts #1136 (§1 KISS/DRY/YAGNI, §2 existing-systems-first, §3 configurability, §4 less-is-better, §5 checklist), Code Contracts #114 (§0 principles, §1, §5.5), PR-shape #1165.
**Precedent (reused patterns):** reverb master-insert design #7164 (the `Reverb` DSP, reused); stereo-pan #7127 (`SetChannelPan` seam + `SampleRegion.Pan` from gen-17 — mirrored here); mix-bus #7126 (per-channel gain-block, master soft-clip, INV-2).

> **REVISION 2026-07-27 (post-PR-#17 empirical probe).** The first cut of this design combined CC91 and gen-16 **multiplicatively** (`channelCC91 × regionGen16`) with a gen-16 absent-default of 1.0. An empirical probe against the real assets proved that model wrong: `07dkc2bram.mid` carries rich per-channel CC91 automation (16 events, e.g. ch0=95, ch2=35, ch4=127, ch7=127, ch9=49, ch12=113, ch14=9), but Florestan sets gen-16 = **explicit 0** on every probed region (programs 0/11/12/48/49 → `voice.ReverbSend = 0.0`). Multiplicatively, `CC91 × 0 = 0` for every voice → the whole song rendered bit-for-bit **dry**, discarding all of DKC2's CC91 intent. The 1.0 absent-default did not rescue it because Florestan's gen-16 is *explicit* 0, not *absent*. **Corrected model (this revision): additive, clamped — `sendWeight = clamp01(channelCC91 + regionGen16)`**, mirroring the SF2 default modulator that routes CC91 additively to reverb send; gen-16 absent-default reverts to the SF2 spec **0**. CC91 is the primary send; gen-16=0 means "no per-instrument override, use the channel send," not "mute reverb." §9.3, §7, §8, §10, §13, §14 are updated accordingly; the routing architecture (§4-§6) is unchanged — only the per-voice scalar changes.

---

## 1. Problem Statement

The engine's reverb (PR 16, merged) is a **master insert**: one Freeverb processes the whole summed stereo master, so every instrument gets the same uniform wash. GM songs do not work that way — an artist places a dry, tight snare next to a hall-drenched string pad by setting **different reverb-send depths per channel** (MIDI **CC91**, "Effects 1 Depth / reverb send") and per instrument (**SF2 generator 16**, `reverbEffectsSend`). A flat global reverb cannot reproduce that character.

**Goal.** Make the reverb honour a **per-channel, per-voice send level** so each instrument gets individual wetness, the way GM intends — while **retaining the uniform/global reverb as a selectable option** (explicit user requirement 2026-07-27: *"global can stay as an option"*).

**Success criteria.**
- Reverb send is driven per channel by CC91 and per voice by SF2 gen-16; channels/voices with high send get an audible tail, low/zero-send ones stay dry.
- The **global/uniform** behaviour remains reachable via an explicit selector and, when selected, reproduces the PR-16 master-insert render **bit-for-bit**.
- **All-sends-zero ⇒ dry**: bit-identical to the reverb-absent render.
- `Read` stays allocation-free (INV: send buffer is ctor-allocated); `Finalize`/`ApplyMasterBus` stay the sole NaN/clip choke points (INV-2); the direct (dry) master mix is bit-identical to today's in both modes.
- The `Reverb` DSP core (comb/allpass banks, wet mix) is **reused unchanged**; only its I/O wrapper generalises from "insert" to "send-return".

---

## 2. Scope & Non-Scope

**In scope**
- A `SetChannelReverbSend(channel, level)` MIDI-neutral seam on `ISynthesizer` (mirrors `SetChannelPan`).
- CC91 (`ControllerType.EffectsLevel = 91`) → the seam in `MidiSequencer`; GM reset sets the CC91 default (40/127).
- SF2 gen-16 (`Sf2GeneratorType.ReverbEffectsSend = 16`) → `SampleRegion.ReverbSend` → `IVoice.ReverbSend` (mirrors gen-17/`Pan`).
- A **per-channel-weighted stereo send bus** in `Synthesizer.Read`, feeding the existing `Reverb`; wet-only added back to the master before `ApplyMasterBus`.
- Combined per-voice send = `clamp01(channelReverbSend[ch] + voice.ReverbSend)` (additive; CC91 primary, gen-16 additive bias — §9.3).
- A routing selector on `SynthesizerOptions` that keeps the **global/uniform** reverb selectable.

**Out of scope** (explicitly)
- Any new reverb algorithm/DSP — the PR-16 `Reverb` is reused (its comb/allpass/wet-mix core is untouched).
- `IAudioEffect` abstraction (still one effect family; born with the 2nd effect — #7164 decision 2, unchanged).
- Chorus / delay / EQ (CC93 chorus, etc.), preset-level SF2 generator addition, voice-stealing, per-channel *pre-delay* or independent reverb params per channel.
- CC91 *fine* controller, RPN, or a global-reverb *level* other than "uniform full" (YAGNI — the requirement is "the old global behaviour stays", not "a tunable global level").

---

## 3. Assumptions & Constraints

| # | Assumption / Constraint | Confidence | Impact if wrong |
|---|---|---|---|
| A1 | CC91 in this codebase is `ControllerType.EffectsLevel = 91` (GM "Effects 1 Depth" = reverb send). | Confirmed (read enum). | — |
| A2 | SF2 gen-16 = `Sf2GeneratorType.ReverbEffectsSend = 16`, expressed in 0.1% units (0..1000 → 0..1). | Confirmed (enum) + SF2 spec. | Normalisation divisor. |
| A3 | The `Reverb` sums its input to mono internally (`(L+R)·InputGain`) and produces decorrelated stereo wet; feeding it a *send* signal instead of the master changes only what it hears, not its stability (feedback still < 1 by construction). | Confirmed (read `Reverb`). | — |
| A4 | Reverb only runs when output is stereo (`Channels == 2`) and `options.Reverb != null` — unchanged from PR-16. | Confirmed. | Mono path untouched. |
| A5 | gen-16 is applied **instrument-level only** (no preset-level addition), matching how gen-17/Pan is currently resolved (v1 subset). | Confirmed (resolver doc + `BuildPan`). | Parity with Pan. |
| C1 | Private monorepo, atomic deploy — no deprecation window / compat shim (Design Contracts §4). | — | — |
| C2 | `Read` must stay allocation-free in steady state; all buffers ctor-sized. | Hard invariant. | Send buffer must be ctor-allocated. |

---

## 4. Architectural Overview

The change reroutes the reverb from **insert** (processes the master) to **send-return** (processes a separately-accumulated, per-channel-weighted bus and adds only its wet output back to the master). The master-insert becomes the special case *"every voice sends fully"*.

```
                 per voice (in the existing voice-mix loop)
                 ┌───────────────────────────────────────────────┐
 voice mono ──►  │ ×channelGain ×equal-power(L,R)                 │
                 │        │                                       │
                 │        ├──────────────────────────► MASTER  (direct / dry, UNCHANGED)
                 │        │                                       │
                 │        └─ ×clamp01(channelSend[ch]+voice.ReverbSend) ─► SEND BUS (per-channel mode only)
                 └───────────────────────────────────────────────┘
                                                  │
        PER-CHANNEL mode:  Reverb.Process(sendBus, master)  ── wet added to master
        GLOBAL mode:       Reverb.Process(master,  master)  ── send == master (insert; PR-16 exact)
                                                  │
                                          ApplyMasterBus (soft-clip)
                                                  │
                                          Finalize (INV-2 NaN/clip)
                                                  │
                                             destination
```

**The unifying idea.** A single `Reverb.Process(send, master)` computes wet from the `send` span and **adds it** to `master` (dry is never touched — it is already in `master`). Global mode aliases `master` as its own send, which is arithmetically the PR-16 insert (`master[i] = left + wet`). Per-channel mode feeds the weighted send bus. **One DSP method, one routing shape; the mode only changes which buffer feeds the send.** This is exactly task #7165's framing — "the send bus is the general form; the master insert is the special case *all channels send fully*."

---

## 5. Components & Responsibilities

| Component | Owns (new/changed) | Does NOT own |
|---|---|---|
| **`ISynthesizer`** | New seam `SetChannelReverbSend(channel, level)` — MIDI-neutral, `level` a send scalar in [0,1]. Mirrors `SetChannelPan`. | MIDI/CC decoding (sequencer's job). |
| **`Synthesizer`** | `channelReverbSend[16]` state (live-read per block, like `channelPan`); the ctor-allocated **stereo send buffer**; the per-voice send accumulation; the reverb call dispatch (per-channel vs global). Builds send buffer only in per-channel mode. | The reverb DSP; the direct mix loop (bit-unchanged). |
| **`Reverb`** | Generalised routing: `Process(ReadOnlySpan<float> send, Span<float> master)` reads `send`, computes wet, **adds wet to `master`**. Comb/allpass/wet-mix core **unchanged**. | Send weighting / mode selection (synth's job). |
| **`SynthesizerOptions`** | The routing selector (`GlobalReverb` flag, default `false` = per-channel). | — |
| **`SampleRegion`** | New immutable `ReverbSend` property (gen-16 normalised, [0,1]). Mirrors `Pan`. | Resolution logic. |
| **`IVoice` / `SamplePlaybackVoice` / `InactiveVoice`** | `ReverbSend` accessor: voice returns `region.ReverbSend`; inactive returns `0f` (mirrors `Pan`). | — |
| **`Sf2RegionResolver`** | `BuildReverbSend(zone, globalZone)` reading gen-16 → [0,1]; passed to `SampleRegion` ctor (mirrors `BuildPan`). | — |
| **`MidiSequencer`** | CC91 → `SetChannelReverbSend(ch, data2/127)`; GM reset sets default send (40/127). Mirrors CC10/Pan. | Send mixing. |

No new type is a mirror of an existing one; no new abstraction/service/interface is introduced (Design Contracts §2 satisfied — every change lands on an existing surface).

---

## 6. Interactions & Data Flow

### 6.1 Control-plane (set send level)
1. `MidiSequencer.Render` GM-reset loop: for each of 16 channels, `SetChannelReverbSend(ch, DefaultReverbSend)` where `DefaultReverbSend = 40f/127f` (GM1 CC91 default 40) — alongside the existing pan/gain reset.
2. On a `Controller` message with `Data1 == ControllerType.EffectsLevel` (91): `SetChannelReverbSend(ch, Data2 / 127f)`. (New `switch` arm next to the Pan arm; the seam is MIDI-neutral so the sequencer owns the 0-127 → [0,1] mapping, exactly as CC10 does for pan.)
3. `Synthesizer.SetChannelReverbSend` stores into `channelReverbSend[ch]` (read live each block; not captured at note-on — matches `channelPan`).

### 6.2 Note resolution (per-voice send)
1. `Sf2RegionResolver.BuildRegion` calls `BuildReverbSend(zone, globalZone)` → normalised [0,1], passed as the new last ctor arg to `SampleRegion`.
2. `SamplePlaybackVoice.ReverbSend => region.ReverbSend`; `InactiveVoice.ReverbSend => 0f`.

### 6.3 Audio-plane (`Synthesizer.Read`, per block) — **key flow**
1. Clear `masterSlice`. In **per-channel mode**, also clear the `sendSlice` (ctor-allocated stereo buffer, same frame count).
2. Voice-mix loop, per occupied voice, stereo branch:
   - Compute `pre` and equal-power `(leftGain,rightGain)` from `combinedPan` — **unchanged direct mix into `masterSlice`** (bit-identical to today).
   - **Per-channel mode only:** compute per-voice scalar `send = clamp01(channelReverbSend[ch] + voice.ReverbSend)`; in a second per-voice frame loop, accumulate `sendSlice[bi]   += pre·send·leftGain` and `sendSlice[bi+1] += pre·send·rightGain`. (Separate loop keeps the hot direct-mix loop pristine; skippable when `send == 0`.)
3. After the loop, dispatch the reverb (when `reverb != null`):
   - **Per-channel:** `reverb.Process(sendSlice, masterSlice)` — wet from the send bus, added to master.
   - **Global:** `reverb.Process(masterSlice, masterSlice)` — send aliases master (insert); no send buffer built.
4. `ApplyMasterBus(masterSlice)` → `Finalize(masterSlice)` → copy out. **Untouched (INV-2).**

**Mono output path** (`Channels != 2`): reverb is never constructed (A4); send accumulation is skipped entirely. Unchanged.

---

## 7. Data Model (Conceptual)

| Entity | New field | Type / range | Source | Neutral/absent value |
|---|---|---|---|---|
| `SampleRegion` | `ReverbSend` | float [0,1] | SF2 gen-16 (0.1% units) | **0.0** (SF2 spec — gen-16 absent means no per-instrument reverb *addition*; see §9.3) |
| `Synthesizer` (state) | `channelReverbSend[16]` | float [0,1] per channel | CC91 / GM default | GM default 40/127 ≈ 0.315 |
| `SynthesizerOptions` | `GlobalReverb` | bool | caller config | `false` (per-channel) |

Ownership: send levels are engine state (channel array) and immutable region data (region property) — mirroring exactly how `channelPan` and `region.Pan` are owned today. The channel send is the **primary** driver; the region send is an **additive** per-instrument bias (§9.3).

---

## 8. Contracts & Interfaces (Abstract)

**`ISynthesizer.SetChannelReverbSend(int channel, float level)`**
- *Input:* `channel ∈ [0,15]` (throws `ArgumentOutOfRangeException` otherwise, mirroring the other seams); `level` a send scalar, expected [0,1].
- *Semantics:* sets the channel's reverb-send weight, applied live to the channel's currently-sounding and future voices, read each block (not captured at note-on). MIDI-neutral. Idempotent per value.
- *Invariant:* the per-voice send weight is `clamp01(channelReverbSend[ch] + voice.ReverbSend)` — additive, clamped (§9.3). A channel with `level == 0` still lets a region's own gen-16 send it to the reverb, and a region with `ReverbSend == 0` still receives the channel's CC91 send. Only when **both** are 0 is the voice dry.

**`IVoice.ReverbSend { get; }`**
- *Output:* the voice's static per-voice reverb-send *bias* in [0,1], immutable for the voice's lifetime; **added** to the channel send at mix time (not multiplied). `0` for inactive/silent voices and for regions with no gen-16 (SF2 default).

**`Reverb.Process(ReadOnlySpan<float> send, Span<float> master)`**
- *Input:* `send` and `master` interleaved stereo, equal length, multiple of 2. `send` may alias `master` (global mode).
- *Semantics:* computes the wet signal from `send` and **adds** it to `master` in place (`master[i] += wetL`, `master[i+1] += wetR`); dry is never added (the caller's `master` already carries dry). Reads each frame's `send` values before writing that frame's `master` (alias-safe read-before-write). Allocation-free; delay-line state carries across calls (tail spans blocks).
- *Invariants preserved from #7164:* feedback < 1 by construction; `wet == 0` ⇒ adds exactly `0.0f` ⇒ float-exact dry passthrough; never relies on the master clamp/`Finalize` to stay bounded.

**`SynthesizerOptions.GlobalReverb { get; }`** — `false` (default): per-channel send routing honouring CC91 × gen-16. `true`: uniform/global insert (send == master), reproducing the PR-16 master-insert render bit-for-bit.

---

## 9. Cross-Cutting Concerns

### 9.1 Allocation / real-time safety
The stereo send buffer is **ctor-allocated** at `master.Length` (sized `BlockFrames × Channels`), reused every block via a cleared slice — same pattern as `master`/`channelGainBlock`. `Read` stays allocation-free. Build it only when reverb is present and `!GlobalReverb` (global mode needs no send buffer — it aliases master).

### 9.2 Error handling / consistency
- Channel-range guard on the new seam mirrors the existing seams (throw on out-of-range).
- The reverb never sees NaN it did not already tolerate; `Finalize` remains the sole NaN/Inf guard (INV-2).
- Determinism: identical inputs ⇒ identical output. Global mode is bit-identical to PR-16; per-channel dry mix is bit-identical to today; both are covered by regression tests (§11 / §14).

### 9.3 Send combination & the gen-16 absent-default — **decision (corrected)**
Combined per-voice send = **`clamp01(channelReverbSend[ch] + voice.ReverbSend)`** — **additive, clamped to [0,1]**. CC91 (channel) is the **primary** reverb send; gen-16 (region) is an **additive per-instrument bias**. gen-16 absent-default = **0** (SF2 spec).

**Why additive, not multiplicative (empirical, verified against real assets — supersedes the first cut).** The first design multiplied (`channelCC91 × regionGen16`). A probe of the real assets disproved it:
- `07dkc2bram.mid` carries **rich per-channel CC91 automation** — 16 events, varied per channel (ch0=95, ch2=35, ch4=127, ch7=127, ch9=49, ch12=113, ch14=9). This *is* the artist's per-instrument reverb intent.
- Florestan sets **gen-16 = explicit 0** on every probed region (programs 0/11/12/48/49 → `voice.ReverbSend = 0.0`). This is the SF2 default and is common across GM soundfonts.
- Multiplicatively, `CC91 × 0 = 0` for **every** voice → the entire song renders bit-for-bit **dry**, discarding all of DKC2's automation. The gen-16-absent→1.0 band-aid could not save it, because Florestan's gen-16 is *explicit* 0, not *absent*.

The flaw is structural: multiplying lets a soundfont's `gen-16 = 0` (the SF2 default!) **nullify** the channel's CC91. That is backwards from GM playback, where CC91 is the primary send and `gen-16 = 0` means *"no per-instrument override — use the channel's send,"* not *"mute reverb."* The additive/clamped form mirrors the **SF2 default modulator** that routes CC91 additively into the reverb-send amount, with gen-16 as an additional generator contribution. Consequences:
- **Florestan (gen-16 = 0):** `send = clamp01(CC91 + 0) = CC91` → DKC2 renders with per-channel reverb varying by CC91 (ch4/ch7 = 127 → very wet; ch14 = 9 → nearly dry). Artist intent honoured. ✔ deliverable.
- **A soundfont that *does* set gen-16 (> 0):** that region gets an extra reverb bias on top of the channel send, clamped at full. Correct per-instrument behaviour.
- **No CC91 automation in a song:** channels sit at the GM default 40/127 → a moderate uniform tail, matching the global reverb the user liked.
- **gen-16 absent-default reverts to the SF2 spec 0** — the 1.0 default existed only to rescue the multiplicative model and is no longer needed. This removes the deviation-from-SF2 concern entirely (Open Question Q1 is resolved).

Trade-off (Design Contracts §4): additive-then-clamp is one `+` and one clamp per voice per block — no added surface versus the product. The clamp is load-bearing (CC91 near full + a non-zero gen-16 would otherwise exceed 1.0 and over-drive the send).

### 9.4 Percussion (channel 9)
No special-casing. The GM reset gives channel 9 the same default send (40/127); a song sets CC91=0 on channel 9 for dry drums, or high CC91 for gated ambience — exactly the "dry snare vs wet pad" use case. (If a future need for a distinct drum-send default appears, it is a one-line change; not designed now — YAGNI.)

---

## 10. Quality Attributes & Trade-offs

| Attribute | How addressed |
|---|---|
| **Simplicity (KISS)** | One `Reverb.Process(send,master)` serves both modes; global = aliasing. No parallel routing path, no new abstraction, no effect graph. Every change lands on an existing surface/pattern. |
| **DRY** | Reuses the `Reverb` DSP (comb/allpass/wet) verbatim; reuses the `SetChannelPan` seam pattern, the `region.Pan`/gen-17 pattern, the `channelPan` live-read pattern, the ctor-allocated-buffer pattern. Zero duplicated DSP. |
| **Performance** | Global mode: unchanged cost. Per-channel mode: one extra ctor-allocated buffer + one extra per-voice frame loop (skipped when send==0) + one buffer pass in `Process`. No per-sample branches in the hot direct-mix loop. |
| **Maintainability** | Master-insert literally *is* "send == master", so there is one mental model, not two. |
| **Backward compat** | Default per-channel mode with GM defaults still yields an audible tail; `GlobalReverb=true` reproduces PR-16 bit-for-bit; `wet=0` and all-sends-zero reproduce dry bit-for-bit. |

### Rejected alternatives
1. **Keep `Process(Span)` (insert) and add a separate `ProcessSend(send,master)`.** Rejected: two public methods duplicating the comb/allpass loop (~30 lines) → DRY violation (#1267), or a shared private helper + two public entry points + two synth branches. The unified single method (global via aliasing) is strictly simpler and makes the "special case" real in code. Cost of unification: change one signature + update 3 call sites (Synthesizer line 210, `ReverbStabilityTests` ×2). Trivial.
2. **A second, independent reverb instance for global mode.** Rejected: parallel layer (Design Contracts §2 Form 2); doubles delay-line memory for a mode that is a strict special case of the send path.
3. **Multiplicative combination (`channelCC91 × regionGen16`), the first-cut model.** Rejected on empirical evidence (§9.3): a soundfont's `gen-16 = 0` (the SF2 default, explicit in Florestan) zeroes the product for every voice, discarding DKC2's entire CC91 automation and rendering the song dry. No absent-default value rescues it, because the offending gen-16 is *explicit* 0. The additive/clamped model is GM-accurate and shipped.
4. **A tunable global send *level* (not just on/off).** Rejected: YAGNI — the requirement is "the old global behaviour stays selectable", a binary. A per-channel user who wants a specific uniform level sets CC91 uniformly.
5. **`ReverbRouting` enum instead of a bool.** Considered; a 2-value domain has no third member in sight, so a `bool GlobalReverb` (default false) is the KISS choice. (Promote to an enum only if a third routing ever materialises.)

---

## 11. Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Signature change breaks global bit-identicality via aliasing read/write ordering. | `Process` reads both `send[i]`, `send[i+1]` into locals at the top of each frame before any `master` write; documented as an alias-safe invariant; covered by a `GlobalReverb` render == pre-existing-insert regression (§14). |
| Per-channel mode accidentally alters the dry signal. | The direct-mix loop is left byte-for-byte; send accumulation is a *separate* loop into a *separate* buffer. Covered by an all-sends-zero ⇒ dry bit-identical test. |
| Send buffer allocated on the hot path. | Ctor-allocated, cleared-slice reuse; asserted by the existing alloc-free discipline (mirror the reverb-buffer approach). |
| Deliverable proof shows *uniform* ambience (no per-channel difference). | Empirically resolved: DKC2 carries varied per-channel CC91 (§9.3), and the additive model lets CC91 drive reverb even with Florestan's gen-16 = 0 → the render is now audibly wet and non-uniform. Also backed by a **deterministic synth-level test** (two channels, different sends, assert per-channel wet differs) independent of asset content. See §14. |
| A soundfont that sets a high gen-16 on a channel already near full CC91 over-drives the send. | The `clamp01` on the sum bounds the send at 1.0; the reverb feedback is < 1 by construction regardless of send level, so this is a loudness bound, not a stability one. |

---

## 12. Migration / Rollout Strategy

Atomic — single PR, no migration window. Default `GlobalReverb=false` means existing callers (MidiRender, tests) move to per-channel mode automatically; the merged reverb render becomes a per-channel render with GM defaults (still audibly reverberant). Any caller wanting the exact PR-16 wash sets `GlobalReverb=true`. No compat shim (Design Contracts §4 / C1).

---

## 13. Open Questions

- ~~**Q1 (send combination default).**~~ **RESOLVED** by the empirical probe (§9.3): the combination is **additive/clamped**, and gen-16 absent-default is the SF2 spec **0**. The multiplicative model and its 1.0 band-aid are dropped.
- ~~**Q2 (real-song proof feasibility).**~~ **RESOLVED**: `07dkc2bram.mid` carries 16 varied per-channel CC91 events; Florestan's gen-16 is explicit 0. Under the additive model CC91 drives reverb, so the DKC2 render is audibly wet and non-uniform. The deterministic synth-level test remains as an asset-independent backstop.
- **Q3 (MidiRender default).** Should the `MidiRender` tool default to per-channel (`GlobalReverb=false`) — recommended, it is the intended behaviour — or keep a flag to render both for A/B? Recommend per-channel default + an optional CLI arg only if A/B rendering is wanted (otherwise YAGNI).

---

## 14. Implementation Guidance for the Next Agent

This revision lands as a **follow-up commit on the existing branch `feature/reverb-send`, amending PR #17** (not a new branch/PR). If the first-cut multiplicative combination was already implemented, this is a change to the send-scalar computation (step 7) + the gen-16 default (step 2) + the proof (step 11); the routing/seam/plumbing (steps 1,3-6,8,9) is unchanged from what PR #17 already carries. Cite Code Contracts #114 and Design Contracts #1136. Build order:

1. **`SampleRegion.ReverbSend`** — add immutable property + ctor param (last), XML-doc mirroring `Pan`. Update the ctor call in `Sf2RegionResolver.BuildRegion`.
2. **`Sf2RegionResolver.BuildReverbSend`** — mirror `BuildPan`: read gen-16 via `GetEffectiveInt16(zone, globalZone, ReverbEffectsSend, defaultValue: 0)` (SF2 spec default), clamp raw to `[0, MaxReverbSendUnits=1000]`, divide by `ReverbSendUnitsDivisor=1000f`. Named consts alongside the pan consts. (Absent gen-16 → 0.0, an additive bias of nothing — the channel CC91 still drives the send.)
3. **`IVoice.ReverbSend`** + `SamplePlaybackVoice.ReverbSend => region.ReverbSend` + `InactiveVoice.ReverbSend => 0f`.
4. **`ISynthesizer.SetChannelReverbSend`** + `Synthesizer` impl: `channelReverbSend[16]` field (init to GM default is the sequencer's job — the array itself may init to 0 or the default; the sequencer resets it explicitly, matching pan/gain), range-guarded setter mirroring `SetChannelPan`.
5. **`SynthesizerOptions.GlobalReverb`** — bool, default `false`, XML-doc; thread through the ctor.
6. **`Reverb.Process(ReadOnlySpan<float> send, Span<float> master)`** — change the routing wrapper: read `send[i]`/`send[i+1]` into locals, compute wet exactly as now, `master[i] += outL; master[i+1] += outR`. Update the XML summary (insert → send-return; note alias-safety). **Do not touch** the comb/allpass/wet-mix math.
7. **`Synthesizer`** — ctor-allocate the stereo `send` buffer (only when `reverb != null && !options.GlobalReverb`); in `Read`, clear the send slice (per-channel mode), add the per-voice send accumulation loop (stereo branch) using the scalar `send = clamp01(channelReverbSend[ch] + voice.ReverbSend)` (a small private `Clamp01` mirroring the existing `Clamp` helper), and dispatch: `reverb.Process(sendSlice, masterSlice)` (per-channel) vs `reverb.Process(masterSlice, masterSlice)` (global). Direct-mix loop unchanged.
8. **`MidiSequencer`** — `DefaultReverbSend = 40` const; reset loop calls `SetChannelReverbSend(ch, DefaultReverbSend / (float)ControllerFullScale)`; add the `EffectsLevel` arm in `ApplyMessage` → `SetChannelReverbSend(ch, Data2 / (float)ControllerFullScale)`.
9. **Update call sites** for the `Reverb.Process` signature: `Synthesizer.cs:210` (done in step 7) and `ReverbStabilityTests.cs` ×2 → `reverb.Process(block, block)` (aliased insert preserves the stability test's intent).
10. **Tests** (deterministic-first):
    - **Additive combination (deliverable core):** assert `send = clamp01(channelSend + regionSend)` — a channel with CC91 > 0 and a region with gen-16 = 0 (the Florestan case) still sends to the reverb (**this is the regression the multiplicative model failed**); a channel with CC91 = 0 and a region with gen-16 > 0 still sends; only both-zero is dry; and CC91 near full + gen-16 > 0 clamps at 1.0. Asset-free synth-level test.
    - **Per-channel routing:** two channels, one voice each, distinct channel sends (ch A high, ch B = 0), gen-16 = 0; assert channel B's contribution decays to silence (dry) while channel A carries a tail; assert per-channel wet differs. Asset-free (à la `SynthesizerChannelPanTests`).
    - **All-sends-truly-zero ⇒ dry:** reverb=Default, per-channel mode, all channel sends 0 **and** all region gen-16 = 0 ⇒ bit-identical to reverb=null.
    - **Global reproduces insert:** `GlobalReverb=true` render == `GlobalReverb=false` with all channel sends forced to 1.0 and gen-16 = 0 (bit-identical — proves "global = all voices send fully"); and tail-RMS > dry (reuse the `ReverbRenderProofTests` shape in global mode).
    - **gen-16 → `SampleRegion.ReverbSend`** unit test in `Sf2RegionResolverTests` (present→raw/1000, absent→**0.0**, clamps at 1000), mirroring the pan test.
    - **CC91 → seam** in `MidiSequencer…Tests` mirroring the pan test (`RecordingSynthesizer` records `SetChannelReverbSend`).
11. **Render proof (updated — must now be audibly wet AND non-uniform):** render `07dkc2bram.mid` (Florestan) per-channel; confirm within [-1,1]; assert the render is **not** bit-identical to the dry render (reverb present despite Florestan's gen-16 = 0 — proves the additive fix) **and** differs from the flat-`GlobalReverb=true` render (per-channel non-uniformity — high-CC91 channels ch4/ch7 carry more tail than low-CC91 ch14). A per-channel wet-contribution measurement (or a per-channel-band RMS comparison) should show the variation tracks the CC91 map. A/B vs `renders/song-dkc2-REVERB.wav` (old global) and dry `renders/song-dkc2-PAN.wav`.
12. **Gates:** both TFMs 0-warning; `dotnet test` green; §6.10 self-audit (body-comment grep 0; XML-summary on all new public/internal members; one-type-per-file).

**Chain:** Sarah (this doc, branch pushed) → **john-backend-dev** (implement on `feature/reverb-send`, one PR incl. this doc) → jenny-qa-reviewer.
