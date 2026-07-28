# Architectural Document: MIDI Modulation Wheel (CC1) — mod-wheel vibrato depth

**Author:** Sarah · **Date:** 2026-07-28 · **Source task:** #7155 · **Project:** #6128 · **Map root:** #6708 · **Precedent:** pitch-bend design #7154 (per-channel factor + IVoice fan-out), mod-LFO design #7076.
**Integration nodes:** MidiSequencer #7114, ISynthesizer #6726, Synthesizer #6734, SamplePlaybackVoice #6736, ModulationLfo #7076, IVoice (in #6736).
**Load-bearing contracts:** Code Contracts #114 (§0/§1/§5.5), Design Contracts #1136 (§1 KISS/DRY/YAGNI, §2, §3, §4).
**DiVoid copy:** #7180 (graph-discoverable mirror of this file). **Repo copy (authoritative):** this file, on branch `feature/mod-wheel`, shipped in the same PR as the implementation (DiVoid #1165). **Second of two PRs** for MIDI-expression task #7155; ships after the sustain PR (#18, merged).

## 1. Problem Statement
`ControllerType.ModulationWheel` (CC1) is parsed but **silently ignored** by `MidiSequencer.ApplyMessage`. By GM/DLS convention the mod wheel adds **vibrato** (a pitch LFO) whose depth tracks the wheel, letting held leads swell with expression. Usage is heavy: `4-11-Ending_Theme.mid` **4030** CC1 events (peak 76), `1-19-The_Man_With_the_Machine_Gun.mid` **2880** (peak 52), and the pitch-bend deliverable `1-01-Liberi_Fatali.mid` **471** (peak 90, ch 2/3/5). **Success:** CC1 drives a per-channel vibrato depth so a sounding note's pitch oscillates proportionally; a channel that never sends CC1 renders bit-for-bit identically.

**Why harder than pitch bend.** Pitch bend folds a scalar into the increment. Mod-wheel vibrato needs an **oscillator**, and the voice's only LFO (`ModulationLfo` from the region's immutable `LfoParameters`, #7076) **self-bypasses when the region's baked depths are all zero** — the common case for GM melodic instruments — emitting a constant 0. So CC1 needs its own oscillator source, not a tweak to the region path.

## 2. Scope & Non-Scope
**In scope:** decode CC1 in `MidiSequencer`; MIDI-neutral per-channel modulation seam on `ISynthesizer`; per-channel mod-wheel amount in `Synthesizer` fanned out to voices (mirrors pitch bend); a dedicated, gated mod-wheel vibrato in `SamplePlaybackVoice`; tests.

**Out of scope:** CC64 sustain (sibling PR, shipped first); routing CC1 to volume/filter (GM maps the wheel to vibrato/pitch only — tremolo/filter are the region LFO's job, YAGNI); per-region mod-wheel-LFO generators (SF2 vibLfo) — YAGNI; **any change to the region `ModulationLfo` bypass or the existing vibrato/tremolo/filter-sweep paths** (explicitly untouched — the rejected Approach A); `GainRamp`/attack #7064 (CC7/CC11 gain path, unaffected).

## 3. Assumptions & Constraints
- `Synthesizer` built fresh per render → `float[]` mod-wheel array default 0 is a neutral GM-reset, **no reset loop** (pitch-bend INV-3 #7154).
- Bit-for-bit: a channel with no CC1 is identical — a zero amount makes the mod-wheel factor an assigned literal `1f` and the mod-wheel LFO never advances (gated).
- Reuse `ModulationLfo`/`LfoParameters` (#7076) as the **oscillator type** (a second instance, not a new oscillator); the region's own LFO instance and its bypass optimisation are untouched.
- `Read` stays allocation-free (mod-wheel LFO is a ctor-built struct; amount is a primitive).
- Mod-wheel vibrato updates on the existing control-rate tick (`ControlRateFrames=64`) — no new timing concept.

## 4. Architectural Overview
Mirrors the pitch-bend fan-out (per-channel state → IVoice push → NoteOn inheritance), with voice-side application being a dedicated, gated **vibrato** instead of a scalar multiply:
CC1 0..127 → decode in `MidiSequencer` → `SetChannelModulation(ch, 0..1)` on `ISynthesizer` → `channelModWheel[16]` in `Synthesizer` (push to sounding voices via `IVoice.SetModWheel`; NoteOn inherits) → `SamplePlaybackVoice` folds a second `modWheelLfo` (fixed rate) scaled by peak-depth const × live amount into the same control-tick increment recompute that already carries region vibrato + pitch bend. Wheel at zero → whole branch skipped, increment unchanged.

## 5. Components & Responsibilities
- `MidiSequencer`: decode CC1 → amount=value/127 and call the seam.
- `ISynthesizer`: contract `SetChannelModulation(int, float)` (0..1 depth).
- `Synthesizer`: `channelModWheel[16]`; fan-out to sounding voices; NoteOn inheritance. Does not own the oscillator.
- `IVoice`: contract `SetModWheel(float amount)`.
- `SamplePlaybackVoice`: a dedicated `modWheelLfo`, the peak-depth const, the gated vibrato fold. Does not own per-channel state; does not touch the region LFO.
- stub voices (`InactiveVoice`/`NanEmittingVoice`/`StubVoice`): no-op `SetModWheel`.

## 6. Interactions & Data Flow
(1) CC1=v → `SetChannelModulation(ch, v/127f)` → store + fan out `Voice.SetModWheel(amount)` to occupied slots on ch. (2) `NoteOn` → after `StartVoice`, `voice.SetModWheel(channelModWheel[ch])` (inherits raised wheel). (3) Voice control tick: region-vibrato factor unchanged; then **only if `modWheelAmount != 0f`** advance `modWheelLfo` by `ControlRateFrames` and compute `modVibrato = 2^(modLfoValue * MaxModWheelVibratoCents * modWheelAmount / 1200)`; else `modVibrato = 1f` (literal) and the LFO is **not** advanced. `effectiveIncrement = pitchIncrement * regionVibrato * modVibrato * bendFactor`.
Onset/continuity: mod-wheel LFO starts at phase 0 (triangle 0) → first engagement adds no pitch step; gating freezes phase when idle and resumes — no discontinuity (INV-1, #7076).

## 7. Data Model (Conceptual)
- Per-channel mod-wheel amount: 16 floats in [0,1] (0=down) in `Synthesizer` beside `channelBendFactor`.
- Per-voice mod-wheel vibrato: a `ModulationLfo` instance (fixed rate) + `float modWheelAmount` (init 0) on `SamplePlaybackVoice` — a second, wheel-scaled vibrato source independent of the region's baked LFO.

## 8. Contracts & Interfaces (Abstract)
`ISynthesizer.SetChannelModulation(int channel, float amount)` — MIDI-neutral, mirrors `SetChannelPitchBend`. channel ∈ [0,15] (range-checked); amount ∈ [0,1] = depth. Applies to sounding voices; inherited by future NoteOns.
`IVoice.SetModWheel(float amount)` — sets live mod-wheel vibrato depth (0=none). Stubs no-op; `SamplePlaybackVoice` stores for the next control tick.
Fixed named consts in `SamplePlaybackVoice` (§3, not configurable): `MaxModWheelVibratoCents = 50f` (GM/DLS default-modulator convention, CC1→vibLfoToPitch scale 50 cents); mod-wheel LFO rate = reused `LfoParameters.Sf2DefaultFrequencyHz` (8.176 Hz), delay 0f. The mod-wheel LFO's `LfoParameters` carries `MaxModWheelVibratoCents` as its pitch depth purely so `ModulationLfo` is non-bypassed — the same value the voice reads for the factor (one const, two readers — exactly the region-LFO pattern).

## 9. Cross-Cutting Concerns
Bit-for-bit: amount==0 ⇒ `modVibrato` literal `1f` and LFO never advances ⇒ increment + all LFO state identical; region LFO path physically unchanged ⇒ every existing vibrato/tremolo/filter-sweep/pitch-bend test unaffected. Common-path cost: songs without CC1 pay nothing (gate skips the branch — the decisive advantage over Approach A). Allocation: one ctor struct + one float per voice; `Read` allocation-free. Concurrency/error handling/observability: as siblings.

## 10. Quality Attributes & Trade-offs
**Chosen — Approach B: a dedicated, gated mod-wheel vibrato LFO (a second `ModulationLfo` instance).**
Rejected — **Approach A** (reuse region LFO, relax its bypass, add wheel depth to `region.Lfo.PitchDepthCents`): rejected because (1) making the wheel work when the region has no baked vibrato forces the region LFO to run **always** — a global per-voice per-tick cost on every song incl. the majority with no LFO/wheel; (2) it risks the region-vibrato bit-for-bit invariant (tested, #7076) by editing shared code; (3) it couples the wheel's rate/phase to whatever the region LFO is (possibly a tremolo LFO at an unrelated frequency). Approach B is spec-correct (GM/SF2/DLS route CC1 to a separate vibrato LFO), leaves the region path physically untouched, and costs nothing when idle.
DRY (§2): reuses `ModulationLfo` the **type** — a second instance, not a parallel oscillator. Second LFO earns its keep (§4): a genuinely independent modulation source; the region LFO can't serve it without Approach-A costs; CC1 is concrete (4000+ corpus events), not speculative. Fixed depth/rate stay `const` (§3) — no operator tunes them, no environment varies them.

## 11. Risks & Mitigations
- Editing shared modulation code breaks region vibrato → Approach B touches no region-LFO code; existing LFO tests guard.
- Zipper/click on wheel move → recomputed at the existing control tick, folded into the increment slope like region vibrato (held-then-stepped, INV-1); click-regression test mirrors the vibrato discontinuity test.
- Pitch step on engagement → LFO starts at phase 0; gating preserves phase across idle; discontinuity test.
- 8.176 Hz feels fast → spec-authoritative default; flagged O1. A slower rate is a magic const without authority (§3).

## 12. Migration / Rollout
Additive, single PR, atomic deploy. Ships after the sustain PR (#18, merged). Branch from `origin/main` (carries pitch bend + reverb send + sustain + the exponential-envelope fix).

## 13. Open Questions (resolved)
- O1 — mod-wheel vibrato rate: `Sf2DefaultFrequencyHz` (8.176 Hz), spec-authoritative + DRY. **Accepted.**
- O2 — peak depth 50 cents (GM/DLS default-modulator). **Accepted.**
- O3 — render-proof song: `1-01-Liberi_Fatali.mid` (471 CC1 ch 2/3/5 — continuity with the pitch-bend deliverable). **Accepted.**

## 14. Implementation Guidance for the Next Agent
1. `IVoice`: add `void SetModWheel(float amount);` (XML: live mod-wheel vibrato depth, 0=none).
2. stub voices `InactiveVoice`/`NanEmittingVoice`/`StubVoice`: no-op `SetModWheel` (mirror their `SetPitchBend`).
3. `SamplePlaybackVoice`: add `const float MaxModWheelVibratoCents = 50f;`; a `ModulationLfo modWheelLfo` ctor-built from `new LfoParameters(0f, LfoParameters.Sf2DefaultFrequencyHz, MaxModWheelVibratoCents, 0f, 0f)`; a `float modWheelAmount` (init 0f); `SetModWheel` stores it; in the control-tick block, after the region-vibrato pow, compute the gated mod-wheel factor (advance LFO + pow only when `modWheelAmount != 0f`, else literal `1f`) and fold into `effectiveIncrement`. Update the class XML summary.
4. `ISynthesizer`: add `void SetChannelModulation(int channel, float amount);` (XML mirroring `SetChannelPitchBend`).
5. `Synthesizer`: add `readonly float[] channelModWheel` (`new float[ChannelCount]`, default 0, no init loop); implement `SetChannelModulation` (range-check; store; fan out to occupied same-channel voices via `SetModWheel`) — copy `SetChannelPitchBend` shape; in `NoteOn` after `StartVoice`, `voice.SetModWheel(channelModWheel[channel])`.
6. `MidiSequencer`: add a `Controller`-switch branch: `if (channel.Data1 == (byte)ControllerType.ModulationWheel) { synth.SetChannelModulation(channel.MidiChannel, channel.Data2 / (float)ControllerFullScale); break; }` (reuses `ControllerFullScale`). No GM-reset line.
7. `RecordingSynthesizer`: add `ChannelModulationCalls : List<(int,float)>` + recording `SetChannelModulation`.

Implementer inventory: `ISynthesizer` impls — `Synthesizer`, `RecordingSynthesizer`. `IVoice` impls — `SamplePlaybackVoice` (folds), `InactiveVoice`/`NanEmittingVoice`/`StubVoice` (no-op). No new test-builder method (use `Controller(delta,channel,1,value)`).

Tests: `MidiSequencerModulationTests` (mirror ReverbSend: CC1=127→1.0; =0→0; mid→value/127; **zero** ChannelModulationCalls on a no-event render; unrelated CC adds none). `SamplePlaybackVoiceModWheelTests` (mirror Vibrato/PitchBend: amount=1 on inert-region-LFO ramp introduces vibrato with peak factor 2^(±50/1200) tracking an independently-advanced ModulationLfo at 8.176 Hz; amount=0 reproduces base increment bit-for-bit; amount=0.5 halves the cents; note started after SetChannelModulation inherits it + mid-note fan-out; region with baked vibrato and amount=0 identical to pre-CC1; control-tick click regression). `MidiModulationRenderProofTests` (real asset, graceful skip: count regression on `1-01-Liberi_Fatali.mid` = 471 SetChannelModulation calls; Florestan non-silent in-range render).

Gate: both TFMs 0-warning; `dotnet test` green (long foreground timeout up to 600000 ms — #7173). Self-audit §6.10: XML summary on every new type/member (all accessibilities); body-comment grep = 0; one type per file; named consts only.
