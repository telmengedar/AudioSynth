# Architectural Document: MIDI Pitch Bend (PitchWheel) — per-channel glide

**Author:** Sarah · **Date:** 2026-07-27 · **Source task:** DiVoid #7140 · **Project:** #6128 · **Map root:** #6708 · **Roadmap:** #7098 "PR 13 expression"
**Builds on:** MidiSequencer #7114 (per-channel-state + `SetChannelPatch`/`SetChannelGain` pattern), ISynthesizer #6726, Synthesizer #6734, SamplePlaybackVoice #6736, ModulationLfo #7076 (mutable effective-increment seam).
**Load-bearing contracts:** Code Contracts (DiVoid #114) §0/§1/§5.5, Design Contracts (DiVoid #1136) §1–§5, PR-shape (DiVoid #1165).

> **Repo path (source of truth):** `docs/architecture/pitch-bend.md` on branch `feature/pitch-bend` of `telmengedar/AudioSynth`. This design ships in the same PR as the implementation (#1165). The DiVoid documentation node is the graph-discoverable copy.

---

## 1. Problem Statement

On the PR-12/PR-13 renders the user observed: *"several sections where flutes/pipes jumped immediately to pitches where the original had more of a glide."* Lead lines that the composer wrote to **slide/scoop** into a note instead **snap** to discrete keys.

**Root cause (verified in code + data):** `ChannelCommandType.PitchWheel` (0xE0) is parsed into the message model but **silently ignored** by `MidiSequencer.ApplyMessage` — its `switch` has cases for NoteOn/NoteOff/ProgramChange/Controller only. A bent note therefore plays at its nominal key with no pitch offset, so a written glide reads as a step.

**Data confirmation** (scan of the bundled test songs):

| Song | PitchWheel events | Concentrated on | RPN bend-range set? |
|------|-------------------|-----------------|---------------------|
| `07dkc2bram.mid` (DKC2 Bramble Blast) | **1196** | lead channels 10–13 | **No** — relies on GM default ±2 |
| `1-01-Liberi_Fatali.mid` (FF8) | **1004** | lead channel 0 (920) | No |
| `1-02-Balamb_Garden.mid` | 0 | — | — |

DKC2's PitchWheel values span `0..8192` (center 8192) — i.e. **downward scoops** up to the full ∓2-semitone range, exactly the "glide into the note" the user heard missing. Neither deliverable song emits RPN (CC100/101) or portamento (CC5/CC65). **The diagnosis is pitch bend, not portamento**, and the **GM default ±2 semitone range is sufficient** for the deliverable proof.

**Success criteria:** a channel's PitchWheel messages continuously shift the pitch of that channel's sounding voices; center (8192) = no shift; the DKC2 lead lines glide instead of stepping; the change is bit-for-bit inert for songs with no PitchWheel.

## 2. Scope & Non-Scope

**In scope (this PR, one feature):**
- Apply `PitchWheel` per channel → per-voice pitch bend, default **±2 semitones** (GM standard).
- A **MIDI-neutral per-channel bend seam** on `ISynthesizer` (takes a semitone offset, not raw bytes), mirroring `SetChannelPatch`/`SetChannelGain`.
- Bend composed into the voice's **existing mutable effective-increment** path (the same seam vibrato already modulates). No new pitch mechanism.
- Notes started while a bend is active **inherit** the current channel bend.

**Explicitly out of scope (deferred, see §12 + follow-up task):**
- **CC1 modulation wheel → LFO/vibrato depth.** Different seam: the LFO depth today is an immutable `LfoParameters` descriptor baked into the `SampleRegion` at voice construction; making it dynamically channel-modulated is a distinct, more invasive change. Not the observed problem.
- **CC64 sustain pedal → hold released notes.** A note-lifecycle concern (deferred voice release), orthogonal to pitch. Different subsystem.
- **RPN 0 bend-range parsing (CC100/101 + Data Entry CC6/38).** Neither deliverable song sets it; ±2 default covers them. Deferred as YAGNI (§3, §12).
- Pan (#7127), portamento (CC5/CC65), channel/poly aftertouch, per-note (MPE) bend.

**The cut — pitch bend ships ALONE.** Rationale (Design Contracts §4 + PR-scope one-feature-per-PR): pitch bend is the observed defect, is cohesive (one seam, one mechanism, reuses the increment path), and is independently valuable. Bundling CC1 (a different, harder seam) and CC64 (a different subsystem) into the same PR would tangle the review across three unrelated mechanisms. CC1 + CC64 are filed as one follow-up task.

## 3. Assumptions & Constraints

- **KISS/DRY/YAGNI are bouncing-grade** (Design Contracts §1). No new pitch subsystem, no config knobs, no RPN speculation.
- Bend range is a fixed **named `const` ±2 semitones**, not a config knob (Design Contracts §3) — no operator tunes it, it does not vary by environment. RPN would make it per-channel-dynamic, but no deliverable song needs that (§12 defers it with the concrete follow-up).
- Modulation cadence is the voice's existing **control-rate tick = 64 frames** (~1.45 ms @ 44.1 kHz). Bend takes effect at the next tick — the same inaudible latency vibrato already has. No separate smoothing path.
- The synthesizer is constructed **fresh per render** (the CLI builds one per song); its neutral bend state is the construction default, so **no GM-reset loop for bend is required** (see §9, INV-3).
- One-type-per-file, XML-doc on all public members, zero body comments (Code Contracts §5.5 / §6.10) — the change adds no new files except tests; the new interface members and the modified methods carry XML summaries.

## 4. Architectural Overview

Three layers, each owning exactly one concern, matching the established MIDI→synth boundary (#7098):

```
  MidiFile / TimedMessageSequence        (Formats/Midi — MIDI model, unchanged)
              │  PitchWheel ChannelMessage (Data1=LSB, Data2=MSB)
              ▼
  ┌───────────────────────────────────────────────────────────┐
  │ MidiSequencer  (Sequencing/)                                │  GM SEMANTICS
  │  ApplyMessage: case PitchWheel →                            │  ── owns the
  │    value14 = (Data2<<7)|Data1                               │  14-bit decode
  │    semitones = (value14 − 8192)/8192 × ±2                   │  + range → semitones
  │    synth.SetChannelPitchBend(channel, semitones)            │
  └───────────────────────────────────────────────────────────┘
              │  SetChannelPitchBend(channel, semitones)   ← MIDI-NEUTRAL seam
              ▼
  ┌───────────────────────────────────────────────────────────┐
  │ Synthesizer  (Synthesis/)                                   │  PER-CHANNEL STATE
  │  channelBendFactor[16]  (2^(semitones/12), init 1.0)        │  ── owns channel→voice
  │  SetChannelPitchBend: store factor; push to occupied        │  fan-out + NoteOn
  │                       voices of the channel                 │  inheritance
  │  NoteOn: new voice inherits channelBendFactor[channel]      │
  └───────────────────────────────────────────────────────────┘
              │  IVoice.SetPitchBend(pitchFactor)          ← internal ratio seam
              ▼
  ┌───────────────────────────────────────────────────────────┐
  │ SamplePlaybackVoice  (Synthesis/Voices/)                    │  PITCH APPLICATION
  │  bendFactor field (init 1.0)                                │  ── folds bend into
  │  control-tick recompute (every 64 frames):                  │  the SAME effective
  │   effectiveIncrement =                                      │  increment vibrato
  │     pitchIncrement × vibratoFactor × bendFactor             │  already modulates
  └───────────────────────────────────────────────────────────┘
```

**One sentence:** the sequencer turns raw PitchWheel bytes into a semitone offset (GM semantics), the synthesizer holds per-channel bend and fans it out to the channel's voices (and to new notes), and the voice multiplies the bend ratio into the effective read-position increment it already recomputes each control tick for vibrato.

## 5. Components & Responsibilities

### 5.1 `MidiSequencer` (`Sequencing/MidiSequencer.cs`) — GM decode
- **Owns:** the 14-bit PitchWheel decode and the bend-range → semitone conversion (GM semantics live here, exactly as CC7/CC11 → gain and ProgramChange → bank/preset already do).
- **Does NOT own:** any per-channel bend *state*. Unlike CC7/CC11 (which combine two controllers and so need `cc7[]`/`cc11[]` arrays threaded through `ApplyMessage`), pitch bend is a **single stateless conversion per event** — decode, convert, forward. No new array is threaded through `ApplyMessage`. The current bend value that a later NoteOn must inherit is owned by the synthesizer, not the sequencer.
- **Change:** add `case ChannelCommandType.PitchWheel` to `ApplyMessage`; add named consts `PitchBendSemitoneRange = 2f`, `PitchWheelCenter = 8192`, `PitchWheelSpan = 8192`.

### 5.2 `ISynthesizer` (`Synthesis/ISynthesizer.cs`) — the MIDI-neutral seam
- **Change:** add `void SetChannelPitchBend(int channel, float semitones);` — a signed semitone pitch offset (0 = centered), mirroring the shape and doc style of `SetChannelPatch`/`SetChannelGain`. MIDI-neutral: no raw bytes, no bend-range knowledge — the engine is told "bend this channel by N semitones".

### 5.3 `Synthesizer` (`Synthesis/Synthesizer.cs`) — per-channel bend state + fan-out
- **Owns:** the authoritative **per-channel current bend** and the channel→voice fan-out.
- **State:** `readonly float[] channelBendFactor` sized `ChannelCount`, each element initialized to `1.0f` (2^0 = centered).
- **`SetChannelPitchBend(channel, semitones)`:** validate channel range (mirror the existing guard); compute `factor = 2^(semitones/12)` once; store in `channelBendFactor[channel]`; iterate the voice pool and call `SetPitchBend(factor)` on every occupied slot whose `Channel == channel`.
- **`NoteOn`:** after the voice is constructed, call `voice.SetPitchBend(channelBendFactor[channel])` so a note started during an active bend is born already bent (the DKC2 scoop-into-note case).
- **Does NOT own:** the semitone→ratio *meaning* of MIDI (that arrived pre-converted) nor the per-frame application (the voice does that). It converts semitones→ratio once at the seam and distributes it.

### 5.4 `IVoice` (`Synthesis/IVoice.cs`) — internal pitch-ratio seam
- **Change:** add `void SetPitchBend(float pitchFactor);` — a dimensionless multiplicative pitch ratio (1.0 = no bend), the same species as the frequency ratios the voice already multiplies into its increment.

### 5.5 `SamplePlaybackVoice` (`Synthesis/Voices/SamplePlaybackVoice.cs`) — pitch application
- **Owns:** applying the bend ratio to its pitch, composed with vibrato and the base increment.
- **State:** `float bendFactor` field, init `1.0f`.
- **`SetPitchBend(float pitchFactor)`:** store `bendFactor = pitchFactor`.
- **Application:** in the existing control-tick recompute (currently `effectiveIncrement = pitchIncrement * pow(2, vibratoCents/1200)`), multiply in `bendFactor`:
  `effectiveIncrement = pitchIncrement × vibratoFactor × bendFactor`.
  This runs every control tick regardless of vibrato depth (the recompute is unconditional today), so bend works with or without vibrato and glides in ≤64-frame steps — the identical held-then-stepped cadence vibrato already uses, so it introduces no amplitude discontinuity by the same INV-1 argument the voice already documents.
- **Does NOT own:** any MIDI/semitone knowledge — it receives a plain ratio.

### 5.6 `InactiveVoice`, and test doubles (`NanEmittingVoice`, `StubVoice`) — no-op `SetPitchBend`
- Each implements the new `IVoice.SetPitchBend` as a no-op (mirrors their no-op `Release`). `RecordingSynthesizer` (test double for `ISynthesizer`) implements `SetChannelPitchBend` by recording the call (see §8, §14).

## 6. Interactions & Data Flow

**Bend of a sounding note (the steady-state glide):**
1. Sequencer reaches a `PitchWheel` event at its sample offset (between rendered gaps, as CC7/CC11 already are).
2. `ApplyMessage` decodes `value14 = (Data2<<7)|Data1`, computes `semitones = (value14 − 8192)/8192 × 2`, calls `synth.SetChannelPitchBend(channel, semitones)`.
3. Synthesizer computes `factor = 2^(semitones/12)`, stores it, pushes it to every occupied voice on that channel.
4. Each such voice stores `bendFactor`; at its next control tick (≤64 frames) it recomputes `effectiveIncrement` including the new factor. Pitch glides.

**Note started during an active bend (scoop-into-note):**
1. Sequencer applies an earlier `PitchWheel` → synthesizer's `channelBendFactor[ch]` now ≠ 1.
2. A later `NoteOn` on `ch` constructs a voice; synthesizer immediately calls `voice.SetPitchBend(channelBendFactor[ch])`.
3. The voice's first control tick (frame 0) uses the inherited factor — the note sounds bent from its onset.

**Song with no PitchWheel:** no `SetChannelPitchBend` is ever called; every voice keeps `bendFactor == 1.0f`; `effectiveIncrement` is bit-for-bit identical to today (multiplying by `1.0f` is exact in IEEE-754). Zero regression.

## 7. Data Model (Conceptual)

No persisted data. Two pieces of transient per-render state:
- **Per-channel bend (synthesizer):** 16 floats, `channelBendFactor`, the current pitch ratio per MIDI channel; neutral = 1.0.
- **Per-voice bend (voice):** one float, `bendFactor`, the ratio currently applied to that sounding note; neutral = 1.0. Set on note birth (inheritance) and on every channel bend change while the note sounds.

The per-channel value is the source of truth; the per-voice value is a push-cached copy so the audio inner loop never reaches back to channel state (matching how the voice already caches its region/increment rather than consulting the channel).

## 8. Contracts & Interfaces (Abstract)

| Interface member | Input | Output / effect | Invariant |
|---|---|---|---|
| `ISynthesizer.SetChannelPitchBend(channel, semitones)` | channel 0–15; signed semitone offset (0 = centered) | future & currently-sounding voices on the channel bend by `2^(semitones/12)` | `semitones == 0` ⇒ no pitch change; out-of-range channel throws (mirror existing guard) |
| `IVoice.SetPitchBend(pitchFactor)` | dimensionless pitch ratio, >0 (1.0 = no bend) | voice's effective increment is scaled by the factor from its next control tick | `pitchFactor == 1.0f` ⇒ increment unchanged bit-for-bit |
| `MidiSequencer` PitchWheel case (internal) | `ChannelMessage` with `Command == PitchWheel` | one `SetChannelPitchBend` call | `value14 == 8192` ⇒ `semitones == 0` |

**Decode semantics (fixed):** `value14 = (Data2 << 7) | Data1` (MSB=Data2, LSB=Data1); `semitones = (value14 − PitchWheelCenter) / PitchWheelSpan × PitchBendSemitoneRange`. Full-down (0) → −2.000; center (8192) → 0; full-up (16383) → +1.99976 (the standard asymmetric-by-one-LSB result; acceptable and conventional).

## 9. Cross-Cutting Concerns

- **Invariants (extend the voice/engine invariant set):**
  - **INV-3 (new): bend neutrality by construction.** A fresh synthesizer and a fresh voice are centered (`1.0f`) with no explicit reset. No GM-reset loop for bend is added — the construction default is the neutral state, and each render builds a fresh synthesizer. Adding a reset "for safety" would be defensive code for a scenario that cannot occur (a synthesizer is never reused across songs with a dirty bend), an anti-pattern under Design Contracts §6.
  - **INV-1 preservation:** bend enters via the same control-tick recompute as vibrato; the increment is held-then-stepped, never introducing an amplitude discontinuity. The existing vibrato no-regression/no-click tests are the template.
- **Concurrency:** none new — the offline render path is single-threaded; bend events are applied between rendered gaps exactly like every other channel message.
- **Idempotency/consistency:** setting the same bend twice is a no-op; the last PitchWheel before a render gap wins (correct — MIDI is last-value-wins per channel).
- **Error handling:** channel-range validation mirrors `SetChannelGain`/`SetChannelPatch`. No new exception types. Malformed PitchWheel (missing data byte) is a parser concern already handled upstream (`InvalidMidiFileException`); the sequencer trusts the parsed model.
- **Observability:** none required; the deliverable proof is the render + unit tests.

## 10. Quality Attributes & Trade-offs

- **Performance:** one `pow` per PitchWheel event (in the synthesizer, not per frame); one extra float multiply per voice per control tick (once per 64 frames). Negligible, and it reuses the existing recompute — no new per-sample work. The steady-state `Read` loop still allocates nothing.
- **Simplicity (Design Contracts §4):** every element passes can-it-be-deleted / merged / inlined:
  - *Could bend live on an existing seam instead of a new `SetChannelPitchBend`?* No — pitch cannot be applied post-voice like gain (gain is a mix multiply; pitch is intrinsic to read-position advance). The voice must know its bend, so `IVoice` must carry it and `ISynthesizer` must set it. Both additions are irreducible.
  - *Could the sequencer store bend like it stores CC7/CC11?* It could, but it **shouldn't** — CC7/CC11 need per-channel arrays only because gain *combines two controllers*; bend is a single stateless conversion, and the state a NoteOn must inherit already lives in the synthesizer. Adding sequencer-side bend state would be redundant (DRY). The design deliberately keeps the sequencer stateless for bend.
  - *Could the semitone→ratio conversion live in the voice?* It could, but the voice would then `pow` every control tick even though bend changes only on discrete events. Converting once at the synthesizer seam and pushing a ratio is both cheaper and keeps the voice's hot path a single multiply. The `ISynthesizer` seam stays semitones (MIDI-neutral, per the brief); the internal `IVoice` seam carries the ratio.
- **Trade-off — control-rate latency vs. sample-accurate bend:** bend takes effect at the next control tick (≤64 frames ≈ 1.45 ms), not the exact sample. *Downside:* a theoretical ≤1.45 ms quantization of bend onset. *Probability/cost of it mattering:* nil — it is the identical cadence vibrato already ships with, well below perceptual threshold, and MIDI PitchWheel streams are themselves coarser than 1.45 ms apart in these songs. *Cost of the alternative* (sample-accurate per-voice bend scheduling): a parallel sub-tick modulation path — large surface for zero audible gain. **Call: reuse the control-tick cadence.** No sample-accurate path.
- **Trade-off — fixed ±2 vs. RPN-configurable range:** *Downside of fixed:* a song that sets a wider range via RPN 0 would bend too little. *Probability:* zero across both deliverable songs (neither emits RPN); low generally for the SNES/PS1-era game MIDIs this engine targets. *Cost of RPN now:* per-channel RPN-state machine (CC101/100 select + CC6/38 data entry) for a feature no current input exercises — textbook YAGNI. **Call: fixed ±2 `const`; RPN deferred as a filed follow-up** so it returns with a concrete driving song if one appears.

## 11. Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Adding an `IVoice`/`ISynthesizer` member breaks the 6 implementers' compile | §14 enumerates all 6 (2 synth, 4 voice) — the implementer updates each in the same change; real ones fold/fan-out, doubles no-op/record. |
| Regression: bend math perturbs non-bent renders | `bendFactor` init is exactly `1.0f`; `x * 1.0f == x` in IEEE-754 → bit-for-bit. A no-regression test asserts default render is unchanged (mirror `DefaultLfo_ReadPositionAdvancesByExactBasePitchIncrement`). |
| Note started mid-bend plays unbent (missed inheritance) | Explicit NoteOn inheritance step (§5.3) + a unit test for it. |
| Zipper/click from stepping the increment | Bend enters via the exact control-tick seam vibrato uses (held-then-stepped); the existing no-click regression test (`ControlTick_IntroducesNoAmplitudeDiscontinuity`) covers the class. |
| Sign/endianness error in the 14-bit decode | Unit tests pin center=0, full-up=+2, full-down=−2 at the sequencer level. |

## 12. Migration / Rollout Strategy

None. Private repo, atomic build; no feature flag, no deprecation window (Design Contracts §5 checklist). The change is additive (new interface members + one new `switch` case + one multiply) and inert for PitchWheel-free input. Ships and takes effect on merge.

## 13. Open Questions

- **O1 — Bend-range default.** Confirming **±2 semitones** (GM standard) as the fixed default. Verified: neither deliverable song sets RPN, so ±2 is correct for the proof. *Recommendation: accept ±2 const; defer RPN.* (No blocker.)
- **O2 — CC1 + CC64 timing.** Filed as a single follow-up task (mod-wheel → LFO depth; sustain → held release). Confirm you want them as the *next* expression PR after this one, or parked lower. (No blocker for this PR.)
- **O3 — Full-up asymmetry.** Full-up PitchWheel (16383) maps to +1.99976 rather than exactly +2 under the conventional `/8192` divisor. This is standard and inaudible; flagging only for completeness. *Recommendation: keep the conventional divisor.*

## 14. Implementation Guidance for the Next Agent

Ordered build phases (all in one PR on `feature/pitch-bend`; design doc already committed on the branch). Cite Code Contracts #114 §0/§5.5 and Design Contracts #1136. No body comments; XML summaries on the new public members and the `PitchWheel` case doc.

1. **Seam — `ISynthesizer.SetChannelPitchBend(int channel, float semitones)`** with an XML summary mirroring `SetChannelGain`'s wording (MIDI-neutral, glided-not-stepped).
2. **`IVoice.SetPitchBend(float pitchFactor)`** with an XML summary (dimensionless pitch ratio, 1.0 = no bend).
3. **`SamplePlaybackVoice`:** add `float bendFactor` (init `1.0f`); implement `SetPitchBend`; multiply `bendFactor` into the `effectiveIncrement` recompute line. Verify the `1.0f` default preserves the current expression bit-for-bit.
4. **No-op `SetPitchBend` in `InactiveVoice`, `NanEmittingVoice`, `StubVoice`** (mirror their no-op `Release`).
5. **`Synthesizer`:** add `readonly float[] channelBendFactor` (init all `1.0f` in ctor); implement `SetChannelPitchBend` (validate channel, compute `2^(semitones/12)`, store, fan out to occupied voices of the channel); in `NoteOn`, after voice construction, call `voice.SetPitchBend(channelBendFactor[channel])`.
6. **`MidiSequencer`:** consts `PitchBendSemitoneRange = 2f`, `PitchWheelCenter = 8192`, `PitchWheelSpan = 8192`; add `case ChannelCommandType.PitchWheel` to `ApplyMessage` — decode `(Data2<<7)|Data1`, convert to semitones, call `SetChannelPitchBend`. Do **not** thread a new per-channel array through `ApplyMessage` (bend is stateless here).
7. **`RecordingSynthesizer` (test helper):** implement `SetChannelPitchBend` recording `(channel, semitones)` into a new `ChannelPitchBendCalls` list.
8. **`MidiTrackEventBuilder` (test helper):** add `PitchWheel(int deltaTicks, byte channel, int value14)` emitting `0xE0|ch, value14 & 0x7F, (value14>>7) & 0x7F`.
9. **Tests:**
   - `MidiSequencerPitchBendTests` (mirror `MidiSequencerChannelGainTests`): center→0 semitones; full-up→≈+2; full-down→−2; a non-PitchWheel message adds no bend call.
   - `SamplePlaybackVoicePitchBendTests` (mirror `SamplePlaybackVoiceVibratoTests`): a sounding voice bent by +N semitones shifts its measured read-increment by `2^(N/12)`; center = no shift; a note started after a bend inherits it; default (no bend) reproduces the base increment bit-for-bit.
   - A render-proof test (mirror `MixBusRenderProofTests`/`VelocityDynamicsRenderProofTests`) rendering `07dkc2bram.mid` with the Florestan SF2, asserting the lead channel exhibits a continuous pitch change (graceful-skip if the asset is absent).
10. **Manual deliverable proof:** render `07dkc2bram.mid` via `tools/Pooshit.AudioSynth.MidiRender` with `Source/AudioSynthesis.Tests/Soundfonts/__Florestan_Basic_GM_GS.sf2` → the lead flute/pipe lines glide instead of stepping.
11. **Gate:** both TFMs 0-warning; `dotnet test` green. Run the §6 self-audit (0 body comments; XML-summary size gate; one-type-per-file — no new production types, so this is just the modified files).

**Follow-up task to file (deferred scope):** *"MIDI expression — CC1 modulation wheel → LFO/vibrato depth, CC64 sustain pedal → held release"*, linked to #7140 and roadmap #7098, noting CC1 needs a mutable per-channel LFO-depth seam (the current `LfoParameters` is a construction-time descriptor) and CC64 needs deferred voice release.

---

*Contracts cited as load-bearing: Code Contracts #114 (§0 KISS/DRY/YAGNI, §5.5 doc/one-type-per-file, §6.10 self-audit), Design Contracts #1136 (§1–§5), PR-shape #1165 (design + implementation, one PR).*
