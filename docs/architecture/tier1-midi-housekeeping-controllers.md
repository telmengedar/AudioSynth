# Architectural Document: Tier-1 GM Housekeeping Channel-Mode Controllers (CC120 / CC121 / CC123)

Source task: DiVoid #7243 · Audit: #7240 · Fix sites: `MidiSequencer` #7114, `Synthesizer` #6734 · Project #6128

## 1. Problem Statement

The sequencer's `ApplyMessage` Controller switch dispatches CC1/7/10/11/64/91/93 and RPN, but silently ignores three GM channel-mode controllers that appear in the corpus:

- **CC120 All Sound Off** — hard-silence every sounding voice on the channel, ignoring sustain, with no normal release (must declick via the existing ~5ms fast-fade, not an instant zero).
- **CC123 All Notes Off** — release every held note on the channel as if a NoteOff arrived for each; must respect the sustain pedal (defer if pedal down) and run the normal envelope release.
- **CC121 Reset All Controllers** — reset a defined subset of the channel's controller state to GM defaults, leaving volume / pan / program / bank / sends untouched.

Success: the three CCs route to correct engine behavior; the CC120 demo (`076s-Bossbat2.mid`, 6 voices still sounding at the cut) is audibly silenced; the CC121 demo (`ys2lb4.mid`, mod-wheel hot at reset) stops the residual vibrato; no regression on songs that never send these CCs.

## 2. Scope & Non-Scope

**In scope:** three new sub-cases in `MidiSequencer.ApplyMessage`; a CC121 reset helper in the sequencer; two new channel-scoped engine operations on `ISynthesizer` / `Synthesizer`; unit tests.

**Out of scope:** CC122 Local Control, CC124-127 Omni/Mono/Poly mode (mode messages, separate audit tier); NRPN reset (we do not implement NRPN — noted below); any new envelope machinery. These CCs reuse existing release / fast-fade paths only.

## 3. Assumptions & Constraints

- The engine stays **MIDI-neutral**: all GM semantics live in `MidiSequencer`; the two new engine methods are generic channel-scoped voice operations, not MIDI-aware.
- Reuse only existing voice-lifecycle paths: `IVoice.Release()` (normal envelope tail), `IVoice.FastFadeForSteal()` (the ~5ms click-free declick already used by voice-stealing and exclusive-class choke), and the sustain-deferral logic already in `NoteOff` / `SetChannelSustain`.
- Per-channel controller state the sequencer owns locally: `cc7[]`, `cc11[]`, `selectedRpn[]`, `bendRange[]`. State the engine owns: patch, gain, pan, bend factor, mod-wheel, reverb/chorus send, sustain flag, `PendingRelease` markers.
- No allocation added to steady-state `Read`; the new engine methods run outside `Read` (single linear pool scan each, no allocation), consistent with existing `SetChannel*` seams.

## 4. Architectural Overview

```
Controller message ──► MidiSequencer.ApplyMessage (Controller switch)
   CC120 AllSoundOff      ─► synthesizer.SilenceChannel(ch)      ──► pool scan: FastFadeForSteal() every voice on ch, ignore sustain
   CC123 AllNotesOff      ─► synthesizer.ReleaseAllNotes(ch)     ──► pool scan: same sustain-aware branch as NoteOff, all keys
   CC121 AllControllersOff ─► ResetAllControllers(ch, ...)       ──► composes EXISTING seams + resets cc11[]/selectedRpn[]
                                                                     (SetChannelModulation 0, SetChannelGain(cc7,127),
                                                                      SetChannelSustain false, SetChannelPitchBend 0)
```

CC120 and CC123 each need one new **engine** method (they touch the private voice pool). CC121 needs **no** new engine method — it is pure composition of controller seams that already exist, plus resetting two sequencer-local arrays.

## 5. Components & Responsibilities

| Component | Owns / does | Does NOT do |
|---|---|---|
| `MidiSequencer.ApplyMessage` (new sub-cases) | Recognize CC120/121/123 by `ControllerType`, translate to engine calls / reset helper | Touch the voice pool; know about envelopes |
| `MidiSequencer.ResetAllControllers` (new private helper) | Apply the GM RAC subset via existing seams + reset `cc11[ch]`, `selectedRpn[ch]` | Reset volume/pan/program/bank/sends/bend-range value |
| `Synthesizer.SilenceChannel` (new) | Fast-fade every occupied voice on the channel; cancel any parked pending steal-note on the channel; ignore sustain | Run normal release; touch other channels |
| `Synthesizer.ReleaseAllNotes` (new) | Release every occupied voice on the channel through the **same sustain-aware branch as `NoteOff`** | Fast-fade; touch other channels; care about key |

## 6. Interactions & Data Flow

### CC120 — `SilenceChannel(channel)`
Single linear pool scan. For each occupied slot whose `Channel == channel`: call `slot.Voice.FastFadeForSteal()` (retarget its `GainRamp` to 0 over the existing ~5ms declick) and set `slot.PendingChannel = NoPendingNote` (cancel any incoming note parked behind a steal, so it does not spring to life when the fade completes). **Does not** consult `channelSustain`, **does not** call `Release()`. The slot is reclaimed by `Read`'s existing finished-voice branch once the fade reaches silence. This reuses the exact declick path already proven by voice-stealing (INV-1, click-free).

### CC123 — `ReleaseAllNotes(channel)`
Structurally identical to `NoteOff` with the key match dropped. Read `bool sustained = channelSustain[channel]`. For each occupied slot on the channel: if `sustained` set `slot.PendingRelease = true` (defer — voice keeps ringing until pedal-up sweeps it in the existing `SetChannelSustain(ch,false)` path); else `slot.Voice.Release(); slot.Released = true` (normal envelope tail). Idempotent on already-released voices. Implementers may factor the shared body out of `NoteOff`, or duplicate the three-line branch — either is acceptable; the invariant is that CC123 goes through the **same sustain-aware release** as a real NoteOff.

### CC121 — `ResetAllControllers(channel, synthesizer, cc7, cc11, selectedRpn)`
Composition, order-independent:
1. `synthesizer.SetChannelModulation(channel, 0f)` — mod-wheel (CC1) → 0.
2. `cc11[channel] = DefaultExpression (127); synthesizer.SetChannelGain(channel, ChannelGain(cc7[channel], cc11[channel]))` — expression → 127, gain recomputed with **preserved** `cc7` (volume untouched).
3. `synthesizer.SetChannelSustain(channel, false)` — sustain → off; this call already sweeps `PendingRelease` voices, so a held pedal is lifted and its sustained notes release for free.
4. `synthesizer.SetChannelPitchBend(channel, 0f)` — bend → center (8192 ≡ 0 semitones).
5. `selectedRpn[channel] = RpnNull` — RPN selector → null (stray Data Entry ignored thereafter). NRPN is not implemented, so nothing to reset there.

## 7. Data Model (Conceptual)

No new entities. Reuses: `VoiceSlot` (`IsOccupied`, `Channel`, `Key`, `Voice`, `PendingRelease`, `Released`, `PendingChannel`), the sequencer-local per-channel arrays, and `channelSustain[]` in the engine.

## 8. Contracts & Interfaces (Abstract)

Two additions to `ISynthesizer` (MIDI-neutral, mirroring the existing channel seams):

| Method | Semantics | Invariants |
|---|---|---|
| `SilenceChannel(int channel)` | Every currently-sounding voice on the channel is fast-faded to silence over the standard declick window; sustain state is ignored; no envelope release runs; parked pending steal-notes on the channel are cancelled. | Bit-identical no-op when the channel has no occupied voices. Other channels untouched. Range-checks `channel` like every other seam. |
| `ReleaseAllNotes(int channel)` | Every currently-sounding voice on the channel is released exactly as if `NoteOff` had arrived for its key: deferred to `PendingRelease` if the channel's pedal is down, else released into its envelope tail. | Same sustain semantics as `NoteOff`. Idempotent on already-released voices. Other channels untouched. |

CC121 adds no interface member; it is internal sequencer composition.

## 9. Cross-Cutting Concerns

- **Declick / INV-1:** CC120 must use `FastFadeForSteal()`, never an instant gain zero — reusing the proven ramp guarantees no click.
- **INV-2 (NaN/Inf finalize):** untouched; both new methods only retarget ramps / flip flags, all output still flows through `Finalize`.
- **Idempotency:** all three CCs are safe to receive repeatedly and safe as no-ops on a silent channel — matching their common use as defensive song-boundary housekeeping.
- **Allocation:** each engine method is one linear pool scan, no allocation, run outside `Read`.

## 10. Quality Attributes & Trade-offs

- **Reuse over new machinery:** CC120→`FastFadeForSteal`, CC123→`NoteOff`'s branch, CC121→existing seams. No new envelope/release code, minimizing regression surface. Rejected alternative: a dedicated "channel panic" envelope — unjustified complexity.
- **Engine neutrality:** naming the engine methods `SilenceChannel` / `ReleaseAllNotes` (not `AllSoundOff` / `AllNotesOff`) keeps the MIDI vocabulary in the sequencer where the rest of GM semantics live. Trade-off: a small naming indirection between CC name and method name, documented here.
- **CC121 as composition:** keeping RAC in the sequencer (no engine method) means the "which controllers reset" policy is GM policy and stays out of the engine. Trade-off: the reset list is spread across a few seam calls rather than one call — acceptable and more honest about what each reset touches.

## 11. CC121 Reset List — mapped to our implemented seams

| Controller | Our seam / state | RAC resets? | Action |
|---|---|---|---|
| CC1 Modulation | `SetChannelModulation` | **Yes → 0** | `SetChannelModulation(ch, 0f)` |
| CC11 Expression | `cc11[]` + `SetChannelGain` | **Yes → 127** | `cc11[ch]=127`, recompute gain |
| CC64 Sustain | `SetChannelSustain` + `PendingRelease` | **Yes → off** (lifts pedal, releases sustained) | `SetChannelSustain(ch, false)` |
| Pitch bend | `SetChannelPitchBend` | **Yes → center** | `SetChannelPitchBend(ch, 0f)` |
| RPN selector | `selectedRpn[]` | **Yes → null** | `selectedRpn[ch]=RpnNull` |
| NRPN param | (not implemented) | Yes per GM, but N/A here | none — document N/A |
| CC7 Volume | `cc7[]` + gain | **No** | preserved (feeds gain recompute) |
| CC10 Pan | `SetChannelPan` / `channelPan[]` | **No** | untouched |
| Program / Bank | `SetChannelPatch` / `channelPatch[]` | **No** | untouched |
| CC91 Reverb send | `SetChannelReverbSend` | **No** | untouched |
| CC93 Chorus send | `SetChannelChorusSend` | **No** | untouched |
| RPN 0 bend-range value | `bendRange[]` | **No** — RAC nulls the *selector*, not the stored range | untouched |

Note the divergence from the `Render` start-up GM-reset loop: that loop initializes the **full** set (patch, cc7=100, cc11=127, pan=center, reverb=40/127, chorus=0, rpn=null, bendRange=2). CC121 is a strict **subset** re-init — it deliberately omits patch, cc7, pan, sends, and bendRange, because GM RAC preserves those. Do not refactor CC121 to call the start-up loop.

## 12. Edge Cases

- **CC121 while sustain is down:** step 3's `SetChannelSustain(ch, false)` runs the existing pedal-up sweep, so any note that received a NoteOff while the pedal was down (now `PendingRelease`) releases. Notes still physically held (no NoteOff yet) keep sounding — correct: RAC resets controllers, it does not stop held notes.
- **CC120 vs CC123:** CC120 = hard ~5ms fast-fade, ignores sustain, no envelope tail (kills even a pedal-sustained ring). CC123 = normal envelope release, respects sustain (defers under pedal). They are not interchangeable; the demo `076s-Bossbat2.mid` needs CC120's hard cut.
- **Drum channel (index 9):** no special-casing — all three apply identically. Drum voices are typically one-shot and already decaying, so CC123 is often a near-no-op there, but CC120 will still fast-fade any drum voice still sounding. Confirm channel 9 is not excluded from the scan.
- **Empty channel:** all three are no-ops on a channel with no occupied voices (scan finds nothing) — matches defensive-housekeeping usage.
- **Parked steal-note under CC120:** cancelled (`PendingChannel = NoPendingNote`) so All Sound Off does not resurrect a note that was about to start behind a fading victim.

## 13. Open Questions

- Engine method names `SilenceChannel` / `ReleaseAllNotes` are a recommendation; if John prefers `AllSoundOff` / `AllNotesOff` for symmetry with the CC names, that is fine — pick one and keep it consistent with the interface doc-comments.
- Whether to physically factor `NoteOff` and `ReleaseAllNotes` onto a shared private helper vs. duplicate the 3-line branch — implementer's call; behavior is the contract, not the factoring.

## 14. Implementation Guidance for the Next Agent (ordered)

1. Add `SilenceChannel(int channel)` and `ReleaseAllNotes(int channel)` to `ISynthesizer` with doc-comments mirroring the existing channel seams (MIDI-neutral wording).
2. Implement both in `Synthesizer`: `SilenceChannel` = pool scan → `FastFadeForSteal()` + cancel pending; `ReleaseAllNotes` = `NoteOff`'s sustain-aware branch generalized to all keys. Range-check `channel`.
3. Add the three Controller sub-cases in `ApplyMessage`: `AllSoundOff`→`SilenceChannel`, `AllNotesOff`→`ReleaseAllNotes`, `AllControllersOff`→ new private `ResetAllControllers`. The RAC helper needs `synthesizer`, `cc7`, `cc11`, `selectedRpn` in scope (thread them through `ApplyMessage`'s existing parameter list — `cc7`/`cc11`/`selectedRpn` are already passed).
4. Implement `ResetAllControllers` per §6 (5 steps, cc7 preserved, bendRange preserved).
5. Add unit tests per §15.
6. Produce the BEFORE/AFTER WAV render of `076s-Bossbat2.mid` (per task #7243 deliverable) and spot-check the CC121 case on `ys2lb4.mid`.

## 15. Test Plan Outline (unit-level)

**Sequencer routing (spy `ISynthesizer` recording calls):**
- CC120 message → exactly one `SilenceChannel(ch)`; no `Release`/`NoteOff`-style calls.
- CC123 message → exactly one `ReleaseAllNotes(ch)`.
- CC121 message → asserts `SetChannelModulation(ch,0)`, `SetChannelGain(ch, ChannelGain(cc7,127))`, `SetChannelSustain(ch,false)`, `SetChannelPitchBend(ch,0)` all fired; and `SetChannelPan`, `SetChannelReverbSend`, `SetChannelChorusSend`, `SetChannelPatch` are **not** fired. Drive CC7 to a non-default first and assert the post-CC121 gain uses the preserved cc7. Arm an RPN, send CC121, then a Data Entry, and assert the Data Entry is ignored (selector was nulled).

**Engine (`Synthesizer` with a test patch/voice):**
- `SilenceChannel`: NoteOn, render a block so the voice sounds at gain>0, `SilenceChannel(ch)`, render past the ~5ms window → output ≈ 0 and slot reclaimed; assert silencing happens within the fast-fade window, not the full release tail (time-to-silence distinguishes fast-fade from `Release`). Assert sustain ignored: pedal down + NoteOn + `SilenceChannel` still silences.
- `ReleaseAllNotes`: NoteOn two keys on a channel, `ReleaseAllNotes(ch)` → both enter release (decay to silence over the normal tail). With pedal down: NoteOn + `SetChannelSustain(ch,true)` + `ReleaseAllNotes(ch)` → still sounding (deferred); then `SetChannelSustain(ch,false)` → releases. Assert a voice on another channel is untouched.
- CC121 sustain interaction (end-to-end): pedal down, NoteOn, NoteOff (→ PendingRelease), CC121 → the `SetChannelSustain(false)` step releases the deferred voice.
```
