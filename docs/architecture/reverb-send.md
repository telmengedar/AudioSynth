# Architectural Document: Per-Channel Reverb Send (CC91 + SF2 gen-16)

**Author:** Sarah (software architect) · 2026-07-27 · Source task **DiVoid #7165** (PR 17) · Project #6128 · Map root #6708.
**Repo copy (authoritative):** `docs/architecture/reverb-send.md` on branch `feature/reverb-send`.
**Contracts (load-bearing):** Design Contracts #1136 (§1 KISS/DRY/YAGNI, §2 existing-systems-first, §3 configurability, §4 less-is-better, §5 checklist), Code Contracts #114 (§0 principles, §1, §5.5), PR-shape #1165.
**Precedent (reused patterns):** reverb master-insert design #7164 (the `Reverb` DSP, reused); stereo-pan #7127 (`SetChannelPan` seam + `SampleRegion.Pan` from gen-17 — mirrored here); mix-bus #7126 (per-channel gain-block, master soft-clip, INV-2).

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
- Combined per-voice send = `channelReverbSend[ch] × voice.ReverbSend`.
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
                 │        └─ ×(channelSend[ch]×voice.ReverbSend) ─► SEND BUS (per-channel mode only)
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
   - **Per-channel mode only:** compute per-voice scalar `send = channelReverbSend[ch] × voice.ReverbSend`; in a second per-voice frame loop, accumulate `sendSlice[bi]   += pre·send·leftGain` and `sendSlice[bi+1] += pre·send·rightGain`. (Separate loop keeps the hot direct-mix loop pristine; skippable when `send == 0`.)
3. After the loop, dispatch the reverb (when `reverb != null`):
   - **Per-channel:** `reverb.Process(sendSlice, masterSlice)` — wet from the send bus, added to master.
   - **Global:** `reverb.Process(masterSlice, masterSlice)` — send aliases master (insert); no send buffer built.
4. `ApplyMasterBus(masterSlice)` → `Finalize(masterSlice)` → copy out. **Untouched (INV-2).**

**Mono output path** (`Channels != 2`): reverb is never constructed (A4); send accumulation is skipped entirely. Unchanged.

---

## 7. Data Model (Conceptual)

| Entity | New field | Type / range | Source | Neutral/absent value |
|---|---|---|---|---|
| `SampleRegion` | `ReverbSend` | float [0,1] | SF2 gen-16 (0.1% units) | **1.0** (see §9.3) |
| `Synthesizer` (state) | `channelReverbSend[16]` | float [0,1] per channel | CC91 / GM default | GM default 40/127 ≈ 0.315 |
| `SynthesizerOptions` | `GlobalReverb` | bool | caller config | `false` (per-channel) |

Ownership: send levels are engine state (channel array) and immutable region data (region property) — mirroring exactly how `channelPan` and `region.Pan` are owned today.

---

## 8. Contracts & Interfaces (Abstract)

**`ISynthesizer.SetChannelReverbSend(int channel, float level)`**
- *Input:* `channel ∈ [0,15]` (throws `ArgumentOutOfRangeException` otherwise, mirroring the other seams); `level` a send scalar, expected [0,1].
- *Semantics:* sets the channel's reverb-send weight, applied live to the channel's currently-sounding and future voices, read each block (not captured at note-on). MIDI-neutral. Idempotent per value.
- *Invariant:* `level == 0` on a channel contributes nothing to the send bus from that channel; combined with `voice.ReverbSend` multiplicatively.

**`IVoice.ReverbSend { get; }`**
- *Output:* the voice's static per-voice send in [0,1], immutable for the voice's lifetime; combined with the channel send at mix time. `0` for inactive/silent voices.

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

### 9.3 Send combination & the gen-16 absent-default — **decision**
Combined per-voice send = **`channelReverbSend[ch] × voice.ReverbSend`** (product, both [0,1]), per task #7165.

The one degree of freedom the task leaves open is the **gen-16 absent-default**. SF2's literal default for `reverbEffectsSend` is 0, but a strict 0 makes CC91 impotent for every region without an explicit gen-16 (a pure product with 0 is always dry) — which contradicts real GM/GS synth behaviour (CC91 drives reverb even for instruments with no explicit send) **and** the deliverable proof ("high-CC91 channels have an audible tail"). Mirroring how gen-17/`Pan` uses a *neutral* absent-default (0 = centre), the neutral absent-default for a send **multiplier** is **1.0** (fully honour the channel send). So:
- Region without gen-16 → `ReverbSend = 1.0` → combined = channel CC91 (channel drives fully).
- Region with gen-16 → `ReverbSend = raw/1000 ∈ [0,1]` → per-instrument attenuation of the channel send.

Trade-off (Design Contracts §4): this deviates from SF2's literal gen-16 default of 0. Cost of the deviation: an SF2 author who omitted gen-16 *intending* zero reverb would still get channel reverb — but in practice SF2 authors rely on the synth's CC91 send, not on gen-16=0 meaning "never any reverb". Cost of the alternative (default 0): CC91 becomes inaudible for typical soundfonts, the feature's central promise ("sounds like the artist intended") fails, and the deliverable proof cannot demonstrate CC91-driven non-uniformity. The GM-accurate, deliverable-satisfying choice wins. **Flagged as Open Question Q1 for Toni to confirm.**

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
3. **gen-16 absent-default = 0 (strict SF2).** Rejected — see §9.3 (breaks CC91 audibility and the deliverable).
4. **A tunable global send *level* (not just on/off).** Rejected: YAGNI — the requirement is "the old global behaviour stays selectable", a binary. A per-channel user who wants a specific uniform level sets CC91 uniformly.
5. **`ReverbRouting` enum instead of a bool.** Considered; a 2-value domain has no third member in sight, so a `bool GlobalReverb` (default false) is the KISS choice. (Promote to an enum only if a third routing ever materialises.)

---

## 11. Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Signature change breaks global bit-identicality via aliasing read/write ordering. | `Process` reads both `send[i]`, `send[i+1]` into locals at the top of each frame before any `master` write; documented as an alias-safe invariant; covered by a `GlobalReverb` render == pre-existing-insert regression (§14). |
| Per-channel mode accidentally alters the dry signal. | The direct-mix loop is left byte-for-byte; send accumulation is a *separate* loop into a *separate* buffer. Covered by an all-sends-zero ⇒ dry bit-identical test. |
| Send buffer allocated on the hot path. | Ctor-allocated, cleared-slice reuse; asserted by the existing alloc-free discipline (mirror the reverb-buffer approach). |
| Deliverable proof shows *uniform* ambience (no per-channel difference) because the MIDI has no varied CC91 and the SF2 has no varied gen-16. | Prove per-channel routing with a **deterministic synth-level test** (two channels, different sends, assert per-channel wet differs) independent of asset content; treat the real-song render as illustrative. See §14 + Open Question Q2. |
| gen-16 default choice diverges from strict SF2. | Documented trade-off §9.3; raised as Q1; behaviourally correct for GM and required by the deliverable. |

---

## 12. Migration / Rollout Strategy

Atomic — single PR, no migration window. Default `GlobalReverb=false` means existing callers (MidiRender, tests) move to per-channel mode automatically; the merged reverb render becomes a per-channel render with GM defaults (still audibly reverberant). Any caller wanting the exact PR-16 wash sets `GlobalReverb=true`. No compat shim (Design Contracts §4 / C1).

---

## 13. Open Questions

- **Q1 (send combination default).** Confirm the gen-16 **absent-default = 1.0** (§9.3) — GM-accurate, deliverable-satisfying, deviates from SF2's literal 0. If Toni wants strict SF2 (default 0), the deliverable proof must inject CC91 *and* gen-16, and CC91-only songs render dry.
- **Q2 (real-song proof feasibility).** Does `07dkc2bram.mid` contain CC91 events, and/or does Florestan set gen-16 on any region? John should inspect during implementation. If neither varies per channel, the *non-uniformity* claim is demonstrated by the deterministic synth-level test (§14), and the real-song render is kept as a "reverb present, within [-1,1]" illustrative render rather than a non-uniformity proof.
- **Q3 (MidiRender default).** Should the `MidiRender` tool default to per-channel (`GlobalReverb=false`) — recommended, it is the intended behaviour — or keep a flag to render both for A/B? Recommend per-channel default + an optional CLI arg only if A/B rendering is wanted (otherwise YAGNI).

---

## 14. Implementation Guidance for the Next Agent

One PR on branch `feature/reverb-send` (design doc already committed). Cite Code Contracts #114 and Design Contracts #1136. Suggested build order:

1. **`SampleRegion.ReverbSend`** — add immutable property + ctor param (last), XML-doc mirroring `Pan`. Update the ctor call in `Sf2RegionResolver.BuildRegion`.
2. **`Sf2RegionResolver.BuildReverbSend`** — mirror `BuildPan`: read gen-16 via `GetEffectiveInt16(zone, globalZone, ReverbEffectsSend, defaultValue: NeutralReverbSendUnits=1000)`, clamp raw to `[0, MaxReverbSendUnits=1000]`, divide by `ReverbSendUnitsDivisor=1000f`. Named consts alongside the pan consts.
3. **`IVoice.ReverbSend`** + `SamplePlaybackVoice.ReverbSend => region.ReverbSend` + `InactiveVoice.ReverbSend => 0f`.
4. **`ISynthesizer.SetChannelReverbSend`** + `Synthesizer` impl: `channelReverbSend[16]` field (init to GM default is the sequencer's job — the array itself may init to 0 or the default; the sequencer resets it explicitly, matching pan/gain), range-guarded setter mirroring `SetChannelPan`.
5. **`SynthesizerOptions.GlobalReverb`** — bool, default `false`, XML-doc; thread through the ctor.
6. **`Reverb.Process(ReadOnlySpan<float> send, Span<float> master)`** — change the routing wrapper: read `send[i]`/`send[i+1]` into locals, compute wet exactly as now, `master[i] += outL; master[i+1] += outR`. Update the XML summary (insert → send-return; note alias-safety). **Do not touch** the comb/allpass/wet-mix math.
7. **`Synthesizer`** — ctor-allocate the stereo `send` buffer (only when `reverb != null && !options.GlobalReverb`); in `Read`, clear the send slice (per-channel mode), add the per-voice send accumulation loop (stereo branch), and dispatch: `reverb.Process(sendSlice, masterSlice)` (per-channel) vs `reverb.Process(masterSlice, masterSlice)` (global). Direct-mix loop unchanged.
8. **`MidiSequencer`** — `DefaultReverbSend = 40` const; reset loop calls `SetChannelReverbSend(ch, DefaultReverbSend / (float)ControllerFullScale)`; add the `EffectsLevel` arm in `ApplyMessage` → `SetChannelReverbSend(ch, Data2 / (float)ControllerFullScale)`.
9. **Update call sites** for the `Reverb.Process` signature: `Synthesizer.cs:210` (done in step 7) and `ReverbStabilityTests.cs` ×2 → `reverb.Process(block, block)` (aliased insert preserves the stability test's intent).
10. **Tests** (deterministic-first):
    - **Per-channel routing (deliverable core):** two channels, one voice each, distinct sends (ch A=1.0, ch B=0.0); assert channel B's contribution decays to silence (dry) while channel A carries a tail; assert per-channel wet contribution differs. Asset-free (synthetic patch, à la `SynthesizerChannelPanTests`).
    - **All-sends-zero ⇒ dry:** reverb=Default, per-channel mode, all channel sends 0 ⇒ bit-identical to reverb=null.
    - **Global reproduces insert:** `GlobalReverb=true` render == `GlobalReverb=false` with all sends forced to 1.0 (bit-identical — proves "global = all channels send fully"); and tail-RMS > dry (reuse the `ReverbRenderProofTests` shape in global mode).
    - **gen-16 → `SampleRegion.ReverbSend`** unit test in `Sf2RegionResolverTests` (present, absent→1.0, clamps), mirroring the pan test.
    - **CC91 → seam** in `MidiSequencer…Tests` mirroring the pan test (`RecordingSynthesizer` records `SetChannelReverbSend`).
11. **Render proof:** render `07dkc2bram.mid` (Florestan) per-channel; confirm within [-1,1], tail present; A/B vs `renders/song-dkc2-REVERB.wav` (global) and dry `renders/song-dkc2-PAN.wav`. Report whether the song/SF2 actually vary per channel (Q2).
12. **Gates:** both TFMs 0-warning; `dotnet test` green; §6.10 self-audit (body-comment grep 0; XML-summary on all new public/internal members; one-type-per-file).

**Chain:** Sarah (this doc, branch pushed) → **john-backend-dev** (implement on `feature/reverb-send`, one PR incl. this doc) → jenny-qa-reviewer.
