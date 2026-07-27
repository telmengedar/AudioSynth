# Architectural Document: MIDI Sustain Pedal (CC64 / HoldPedal1) — deferred voice release

**Author:** Sarah · **Date:** 2026-07-28 · **Source task:** DiVoid #7155 · **Project:** #6128 · **Map root:** #6708 · **Precedent:** pitch-bend design #7154, CC1 deferral #7155.
**Integration nodes:** MidiSequencer #7114, ISynthesizer #6726, Synthesizer #6734, VoiceSlot (in #6734).
**Load-bearing contracts:** Code Contracts #114 (§0/§1/§5.5), Design Contracts #1136 (§1 KISS/DRY/YAGNI, §2, §3, §4).
**DiVoid copy:** #7179 (graph-discoverable mirror of this file). **Repo copy (authoritative):** this file, on branch `feature/sustain-pedal`, shipped in the same PR as the implementation (DiVoid #1165).

## 1. Problem Statement
`ControllerType.HoldPedal1` (CC64) is parsed by the MIDI layer but **silently ignored** by `MidiSequencer.ApplyMessage` — there is no `case` for it. On a real sustain pedal, holding the pedal makes released notes keep ringing until the pedal is lifted; without it, every note stops the instant its `NoteOff` arrives, so piano/pad passages that lean on the pedal sound clipped and staccato. Usage is real and broad in the deliverable corpus: `3-20-Eyes_On_Me_2.mid` has **500** CC64 events on channel 0, `3-07-Fishermans_Horizon.mid` **427** on channel 0, `1-06-Find_Your_Way.mid` **167** across channels 0/1/3/8/11. **Success:** while the pedal is down on a channel, a `NoteOff` defers the voice's release (the voice keeps sounding); when the pedal lifts, every note that received a `NoteOff` since pedal-down releases into its envelope tail. Songs that never send CC64 render bit-for-bit identically to today.

## 2. Scope & Non-Scope
**In scope:** decode CC64 in `MidiSequencer`; a MIDI-neutral per-channel sustain seam on `ISynthesizer`; per-channel hold state + a per-voice-slot deferred-release marker in `Synthesizer`; the `NoteOff` / pedal-up release semantics; tests.

**Out of scope (explicit):**
- CC1 modulation wheel — separate seam, separate PR (mod-wheel design node, DiVoid #7181).
- Sostenuto (CC66), soft pedal (CC67), HoldPedal2/legato — not requested; distinct semantics; YAGNI.
- Panic (CC120/123) — untouched.
- `GainRamp`/attack revisit (note #7064) — that concerns the CC7/CC11 **gain** path; sustain touches only **release timing**, so #7064 is unaffected and stays out of scope.
- No voice-stealing policy change: a deferred-release voice remains an occupied slot and is subject to the existing pool-full behaviour (a full pool drops new note-ons, exactly as today).

## 3. Assumptions & Constraints
- `Synthesizer` is constructed fresh per `MidiSequencer.Render` call, so a `bool[]` sustain array defaulting to `false` is a neutral GM-reset with **no reset loop needed** (mirrors pitch-bend INV-3 #7154; avoids the "defensive reset for an impossible reuse" anti-pattern, Design Contracts §6).
- Standard MIDI threshold: CC64 value 0–63 = pedal up, 64–127 = pedal down.
- Steady-state `Read` stays allocation-free (adds one `bool[16]` and one `bool` per pool slot, all ctor-allocated).
- The fixed voice pool and its `VoiceSlot` occupancy model (#6734) are the note-lifecycle substrate; this design extends them.

## 4. Architectural Overview
Three thin layers, each owning one concern — the same shape as the CC7/CC10/CC11/CC91/pitch-bend seams:
CC64 value (>=64 = down) → decode in `MidiSequencer` → MIDI-neutral `SetChannelSustain(ch,bool)` on `ISynthesizer` → per-channel `channelSustain[16]` + per-slot `VoiceSlot.PendingRelease` in `Synthesizer` (NoteOff defers-or-releases; pedal-up releases deferred voices).

No new component, no new abstraction. **No `IVoice` change and no `SamplePlaybackVoice` change** — the deferral is purely a pool-lifecycle decision; the voice already exposes `Release()`, and "keep sounding" is simply "don't call it yet".

## 5. Components & Responsibilities
- `MidiSequencer`: decode CC64 → bool (threshold 64) and call the seam. Owns no state.
- `ISynthesizer`: the MIDI-neutral contract `SetChannelSustain(int, bool)`.
- `Synthesizer`: `channelSustain[16]`; defer-vs-release in `NoteOff`; release deferred voices on pedal-up. Does not own release mechanics (voice owns `Release()`).
- `VoiceSlot`: a `PendingRelease` flag = "got a NoteOff while the pedal was down".

## 6. Interactions & Data Flow
Core flow: (1) CC64=127 → `channelSustain[ch]=true`. (2) `NoteOff(ch,key)` while held → matching occupied slots get `PendingRelease=true`, **no `Release()`** — voice keeps sounding. (3) CC64=0 → for each occupied slot on `ch` with `PendingRelease`: `Voice.Release()` + clear; `channelSustain[ch]=false`.
Physically-held note at pedal-up: `PendingRelease` stays false → not released → sounds until its own NoteOff (correct hardware behaviour).
Note-on during hold / re-strike: `NoteOn` allocates a fresh slot, sets `PendingRelease=false`; prior deferred voice for the same key remains a separate slot and still releases on pedal-up (per-slot marker, not per-key).
No sustain: `channelSustain[ch]=false` → `NoteOff` calls `Voice.Release()` as today → bit-for-bit identical.

## 7. Data Model (Conceptual)
- Per-channel sustain: 16 booleans (`false`=up) in the `Synthesizer` beside `channelPan`/`channelReverbSend`/`channelBendFactor`.
- Per-voice deferred-release marker: one boolean per pool slot on `VoiceSlot`. Reset to `false` at the single occupancy point (`NoteOn`); a freed slot (`IsOccupied=false`) is skipped by every pool scan, so a stale marker on a free slot is inert — no second reset site needed.

## 8. Contracts & Interfaces (Abstract)
`ISynthesizer.SetChannelSustain(int channel, bool held)` — MIDI-neutral, mirrors `SetChannelPan`. channel ∈ [0,15] (out-of-range throws, as siblings do). `true` engages hold; `false` disengages and releases every deferred voice on the channel. Idempotent on repeat; only the down→up transition performs releases; no explicit edge-detection state needed.
`Synthesizer.NoteOff(int, int)` extended: if `channelSustain[channel]` → mark matching occupied slots `PendingRelease=true`; else `Voice.Release()` (unchanged).

## 9. Cross-Cutting Concerns
Bit-for-bit: no CC64 → `channelSustain` all-false → `NoteOff` takes the `Release()` branch, `PendingRelease` never read → identical output. Allocation: ctor-time only; `Read` allocation-free. Concurrency: none new (single-threaded per render). Error handling: range-check mirrors siblings. Observability: test synthesizer records seam calls.

## 10. Quality Attributes & Trade-offs
- Simplicity (§4): one branch in `NoteOff` + one loop in the seam. No new type/abstraction/voice change.
- Per-slot marker vs. synth-side held-notes set: a separate set would duplicate `(channel,key)` identity the pool already holds and allocate. `VoiceSlot` already carries Channel/Key/IsOccupied; one `bool` reuses it (DRY §2). **Chosen: per-slot marker.**
- No GM-reset loop: neutral default `false` is the ctor default; a reset would be defensive code for a synth-reuse scenario that never occurs (§6). **Chosen: no reset.** The sequencer test asserts zero sustain calls on a no-event render.
- Performance: `NoteOff` gains one boolean read; pedal-up is one pool sweep per lift (bounded by MaxVoices), negligible/infrequent.

## 11. Risks & Mitigations
- Stale `PendingRelease` reused → reset at NoteOn; free slots skipped; slot-reuse test.
- Pedal-up releasing a still-held note → only `PendingRelease==true` released; dedicated test.
- Pool exhausted by held voices → accepted/unchanged; existing pool-full drop applies.
- Threshold off-by-one → named `SustainPedalThreshold=64` + boundary tests (63=up, 64=down).

## 12. Migration / Rollout
Additive, single PR, atomic deploy — no migration/flag/shim (§5). Mod-wheel (CC1) is a separate follow-up PR; sustain ships first (more broadly audible; contained pool-lifecycle change, no voice-DSP risk).

## 13. Open Questions
- O1 — pedal-up release of a voice that already finished naturally: handled (reclaimed/skipped); reviewer visibility only.
- O2 — render-proof song: recommend `3-20-Eyes_On_Me_2.mid` (500 CC64 ch 0); `1-06-Find_Your_Way.mid` (167, multi-channel) alternative.

## 14. Implementation Guidance for the Next Agent
1. `VoiceSlot`: add `public bool PendingRelease;` (XML summary "NoteOff deferred by a held sustain pedal").
2. `ISynthesizer`: add `void SetChannelSustain(int channel, bool held);` (XML mirroring `SetChannelPan`).
3. `Synthesizer`: add `readonly bool[] channelSustain` (`new bool[ChannelCount]`, default false, no init loop); implement `SetChannelSustain` (range-check; store; on `false` sweep the pool releasing occupied same-channel slots whose `PendingRelease` is set, clearing the flag); branch `NoteOff` on `channelSustain[channel]`; set `slot.PendingRelease=false` in the `NoteOn` occupancy block.
4. `MidiSequencer`: add `const int SustainPedalThreshold = 64;` and a `Controller`-switch branch: `if (channel.Data1 == (byte)ControllerType.HoldPedal1) { synth.SetChannelSustain(channel.MidiChannel, channel.Data2 >= SustainPedalThreshold); break; }`. **Do not** add sustain to the GM-reset loop.
5. `RecordingSynthesizer` (test): add `ChannelSustainCalls : List<(int,bool)>` + recording `SetChannelSustain`.

Implementer inventory (2 `ISynthesizer` impls): `Synthesizer`, `RecordingSynthesizer`. **No `IVoice` impls change.** No new test-builder method (use existing `Controller(delta,channel,64,value)`).

Tests: `MidiSequencerSustainTests` (mirror ReverbSend: CC64=127→true; =0→false; boundary 63/64; **zero** ChannelSustainCalls on a no-event render — the no-GM-reset discriminator; unrelated CC adds none). `SynthesizerSustainTests` (asset-free synth-level, **primary proof**, mirror ReverbSendRoutingTests: looping tone; pedal-down→NoteOff keeps energy; pedal-up decays; control without CC64 stops at NoteOff; physically-held note not released by pedal-up; slot-reuse regression). `MidiSustainRenderProofTests` (real asset, graceful Ignore; count regression on `3-20-Eyes_On_Me_2.mid` = 500 SetChannelSustain calls; Florestan non-silent in-range render).

Gate: both TFMs 0-warning; `dotnet test` green (long foreground timeout up to 600000 ms — suite ~6.5 min, #7173). Self-audit §6.10: XML summary on every new type/member (all accessibilities); body-comment grep = 0; one type per file; named consts only.
