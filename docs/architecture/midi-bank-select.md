# Architectural Document: MIDI Bank Select (CC0 / CC32) Support

> Status: design complete, ready for implementation.
> Source task context: MIDI feature-completeness audit — DiVoid #7240 (Tier 2, top gap by file count).
> Affected units: `Sequencing/MidiSequencer.cs` (primary), `Synthesis/SoundBank.cs` (one additive fallback rung).
> Demo soundfonts for before/after rendering:
> - GS: `C:\dev\claude\OmegaGMGS2.sf2` (484 presets, 50 banks; bank 0 = GM, bank 8 = GS variations, banks 127/128 = drums)
> - XG: `C:\dev\claude\yamaha_xg_sound_set_re-map.sf2` (482 presets, 47 banks; banks 1–8 melodic variations, 64–72 SFX, 128 drums)
> - GM-only regression baseline: the existing Florestan test bank (single melodic bank 0 + drum bank 128).

---

## 1. Problem Statement

The engine renders every instrument from **bank 0** because the sequencer captures Program Change but silently drops **CC0 (Bank Select MSB)** and **CC32 (Bank Select LSB)**. `MidiSequencer.ResolveProgramPatch` hard-codes `bank = 0` for melodic channels and `bank = 128` for the percussion channel, so `SoundBank.GetPatch(bank, program)` is never asked for any GS/XG variation, SFX, or alternate-drum bank. Per the audit (#7240) CC0 appears in **304 of 361** corpus files and CC32 in **166** — this is the single most prevalent unhandled control. With a GM-only soundfont the gap is mostly benign (the requested variations don't exist), but with the two real multi-bank soundfonts we now build against, the majority of their presets are unreachable.

**Goal:** capture CC0/CC32 per channel, latch them on the next Program Change, and route to the correct SF2 `wBank` — covering the common GS and XG cases with one soundfont-agnostic rule — while guaranteeing that a GM-only soundfont behaves *exactly* as it does today (zero regression).

**Success criteria:**
1. A song that sends `CC0 → CC32 → ProgramChange` on a melodic channel resolves to the requested SF2 bank when that bank exists in the loaded soundfont.
2. Bank-select never switches the instrument on its own; only the subsequent Program Change latches it.
3. A requested bank/program absent from the soundfont degrades gracefully (never silence, never crash), preferring the **same program in the GM bank** over an unrelated substitute.
4. The percussion channel (MIDI ch 10 / index 9) still always resolves within the drum bank (128); alternate kits are selected by program number.
5. Rendering the existing GM-only bank is byte-for-byte unchanged for songs whether or not they send bank-select.

---

## 2. Scope & Non-Scope

**In scope**
- Capturing CC0 and CC32 into per-channel MSB/LSB state in `MidiSequencer`.
- The (MSB, LSB, program) → `GetPatch(bank, program)` resolution rule for melodic and percussion channels.
- One additive fallback rung in `SoundBank.GetPatch` ("bank 0, same program") that is the regression-safety mechanism.
- Interaction with the start-of-song GM reset and CC121 Reset All Controllers.
- Unit + integration test plan.

**Out of scope (explicit non-goals)**
- NRPN (CC98/99), SysEx GS/XG mode messages (GS "reset", XG "System On"). These *declare* which standard a file targets; our rule is deliberately designed to work **without** knowing the standard, so we do not parse them here. (Tier 3 in the audit.)
- Using CC32 (LSB) as a second bank dimension. SF2's `wBank` keyspace is one-dimensional (0–127 melodic, 128 drums); see §7 for why LSB is captured but not used for SF2 bank selection in this increment.
- XG "melodic-channel-becomes-drums" via MSB 126/127 on a non-percussion channel — surfaced as the single **open product decision** (§13), defaulted to *not honored* in increment 1.
- Any change to voice allocation, envelopes, effects, or the patch-resolution path *below* `GetPatch` (the SF2 resolver, `SampleRegion`, voices). Bank select changes *which* patch is chosen, not how a patch sounds.

---

## 3. Assumptions & Constraints

| # | Assumption / Constraint | Confidence | Notes |
|---|---|---|---|
| A1 | SF2 soundfonts key melodic presets by `wBank` = the Bank Select **MSB** value, and place all drum kits on `wBank` 128. | High | SF2 spec convention; both demo fonts follow it (GS bank 8, XG banks 1–8; drums on 128). Loader already stores `(preset.BankNumber, preset.PatchNumber)` verbatim (`Sf2SoundBankLoader.BuildPatches`). |
| A2 | Bank select is "pending" until a Program Change latches it (MIDI running-status behavior). | High | Matches every hardware/software sequencer; confirmed against GM/GS/XG practice. |
| A3 | CC121 Reset All Controllers does **not** reset bank/program selection. | High | GM1 RAC resets *sound* controllers (mod, expression, pedals, pitch bend, RPN); program/bank are explicitly out of RAC scope. The existing `ResetAllControllers` already omits program/bank (design #7245 §4/§11). |
| A4 | `SetChannelPatch` affects only future `NoteOn`s; already-sounding voices keep their patch. | High | Stated contract in `ISynthesizer.SetChannelPatch`. Mid-note bank changes therefore need no special handling. |
| A5 | `GetPatch` is the sole fallback owner (never null, never throws except on an empty bank). | High | `SoundBank` doc #7123; the sequencer cannot layer its own fallback because `GetPatch` never returns a "miss" signal — so the new rung must live inside `SoundBank`. |
| C1 | Channel index 9 is percussion (0-based); MIDI channel 10 (1-based). | High | Existing `PercussionChannel = 9` constant. |
| C2 | MSB/LSB are 7-bit (0–127); corpus shows MSB 0–127, LSB 0–66 (#7240). | High | No 14-bit combination needed for SF2. |

---

## 4. Architectural Overview

Bank select is a **small, self-contained state addition inside the existing sequencer** plus **one fallback rung** in the sound bank. No new components, no new interfaces, no changes to the synthesizer or the SF2 loader.

```
                MidiSequencer (per-channel state, 16 channels)
   ┌──────────────────────────────────────────────────────────────┐
   │  cc7[] cc11[] selectedRpn[] bendRange[]   ← existing            │
   │  bankMsb[]  bankLsb[]                       ← NEW (this design)  │
   └──────────────────────────────────────────────────────────────┘
        │ CC0  → bankMsb[ch] = value      (no patch change)
        │ CC32 → bankLsb[ch] = value      (no patch change)
        │ ProgramChange(ch, prog):
        │        bank = ResolveBank(ch, bankMsb[ch], bankLsb[ch])
        │        SetChannelPatch(ch, soundBank.GetPatch(bank, prog))   ← LATCH
        ▼
   ┌──────────────────────────────────────────────────────────────┐
   │  SoundBank.GetPatch(bank, program)  — fallback ladder          │
   │   1 exact(bank,program)                                        │
   │   2 bank-0 same program   ← NEW rung (variation banks only)    │
   │   3 same-bank lowest present program                           │
   │   4 melodic default (0,0)          (skipped for bank 128)      │
   │   5 any percussion (128)                                       │
   │   6 patches[0]                                                 │
   └──────────────────────────────────────────────────────────────┘
```

The whole feature is: two `int[16]` arrays, a change to the CC handler, a change to `ResolveProgramPatch`, and rung 2 in `GetPatch`.

---

## 5. Components & Responsibilities

### 5.1 `MidiSequencer` (modified)
- **Owns** per-channel bank-select intent: `bankMsb[16]`, `bankLsb[16]`.
- **Owns** the (MSB, LSB, program, channel) → SF2 `wBank` mapping policy (`ResolveBank`), because this is MIDI/GM/GS/XG semantics — and by the codebase's established boundary (#7117, #7123), *all* MIDI/GM meaning lives in `MidiSequencer`, never in `SoundBank`.
- **Owns** the latch timing: CC0/CC32 mutate state only; Program Change reads state and calls `SetChannelPatch`.
- **Does NOT own** the graceful-degradation fallback (that is `SoundBank`'s), the patch's sound, or knowledge of which SF2 banks exist.

### 5.2 `SoundBank` (modified — one additive rung)
- **Owns** the never-null graceful-degradation ladder from any `(bank, program)` request to a concrete `IPatch`.
- **Gains** responsibility for the "requested variation bank/program absent → same program in the GM bank" rung. This is format- and MIDI-neutral: it is a pure "a variation of instrument N is unavailable, give me instrument N" rule expressed in the bank keyspace, so it correctly belongs to `SoundBank`, not the sequencer.
- **Does NOT own** any MSB/LSB/GS/XG interpretation — it only ever sees a resolved integer `bank`.

### 5.3 Unchanged
`Synthesizer`, `ISynthesizer`, `Sf2SoundBankLoader`, `Sf2Patch`, the SF2 resolver, voices, effects. **Verified:** the `SoundBank` keyspace already expresses drum bank 128 and melodic banks 0–127 simultaneously (loader stores raw `wBank`), so **no loader or keyspace change is required** — item #4 of the brief resolved in the negative.

---

## 6. Interactions & Data Flow

### 6.1 Control flow inside `ApplyMessage` (Controller branch)
Two new cases, placed alongside the existing CC handlers, before the generic Volume/Expression tail:

- **`Data1 == BankSelect` (CC0):** `bankMsb[ch] = Data2`. No synthesizer call. `break`.
- **`Data1 == BankSelectFine` (CC32):** `bankLsb[ch] = Data2`. No synthesizer call. `break`.

Both enum members already exist in `ControllerType` (`BankSelect = 0`, `BankSelectFine = 32`).

### 6.2 Program Change (modified)
`ResolveProgramPatch` gains the channel's bank state:

```
ProgramChange(ch, program):
    bank    = ResolveBank(ch, bankMsb[ch], bankLsb[ch])
    patch   = soundBank.GetPatch(bank, program)
    synthesizer.SetChannelPatch(ch, patch)      // affects future NoteOns only (A4)
```

Because the arrays live in `Render` and are threaded through `ApplyMessage` exactly as `cc7`/`cc11`/`selectedRpn`/`bendRange` are today, this is the same plumbing pattern already established for every other per-channel controller.

### 6.3 Key sequences (conceptual)

**GS variation (Omega GS):** `CC0=8` → `bankMsb[ch]=8`; `CC32=0` → `bankLsb[ch]=0`; `ProgramChange(ch, 25)` → `ResolveBank → 8` → `GetPatch(8, 25)` → the GS variation of program 25. NoteOns after this play the variation; any voice already sounding keeps its prior patch.

**XG melodic variation:** `CC0=1` → `bankMsb[ch]=1`; (often no CC32, or `CC32=0`); `ProgramChange(ch, 48)` → `GetPatch(1, 48)` → the XG bank-1 variation of program 48.

**Bank select with no following Program Change:** state is updated, no patch change — matches A2. The stale bank is latched by the *next* Program Change whenever it arrives (could be much later, or never).

**Mid-note bank change:** `NoteOn` … `CC0=8` … `ProgramChange` … `NoteOn`. The first note keeps its patch to its natural release (A4); only the second `NoteOn` uses the variation. Falls out naturally — no code handles it explicitly.

---

## 7. Data Model (Conceptual)

Per-channel sequencer state (16 channels), additions in **bold**:

| Field | Meaning | Init (GM reset) | Reset by CC121 (RAC)? |
|---|---|---|---|
| cc7[] | Channel Volume | 100 | recomputed (gain), value preserved |
| cc11[] | Expression | 127 | reset to 127 |
| selectedRpn[] | armed RPN | RPN-null | reset to RPN-null |
| bendRange[] | pitch-bend semitones | 2 | preserved |
| **bankMsb[]** | **CC0 latch-pending MSB** | **0** | **preserved (A3)** |
| **bankLsb[]** | **CC32 latch-pending LSB** | **0** | **preserved (A3)** |

Ownership: this state is private to `MidiSequencer.Render`, threaded to `ApplyMessage`. It is *intent* (what the next Program Change will use), not *current instrument* (which lives in the synthesizer as the channel patch).

**Why LSB is captured but not used for SF2 bank selection (the crux mapping decision):**
SF2 has a single `wBank` integer per preset (0–127 melodic, 128 drums). It has no room to encode `MSB×128 + LSB` (that exceeds 128 for any non-zero MSB), so **soundfont authors collapse bank onto the MSB**: GS puts variations at `wBank = MSB` (e.g. 8) with LSB conventionally 0; XG-derived SF2 fonts likewise map their melodic-variation banks to `wBank = MSB` (1–8). Therefore the robust, standard-agnostic SF2 mapping is **`bank = MSB`**, and CC32 does not participate in bank selection for SF2. We still *store* `bankLsb` so the state model is complete and a future NRPN/XG-LSB feature (Tier 3) can consume it without a data-model change.

---

## 8. Contracts & Interfaces (Abstract)

### 8.1 `ResolveBank(channel, msb, lsb) → int` (new private policy in `MidiSequencer`)
The exact resolution rule:

| Channel | Rule | Result |
|---|---|---|
| **Percussion (index 9)** | Drums are forced regardless of bank-select MSB/LSB. | `128` |
| **Melodic (any other)** | SF2 keys melodic banks by MSB (§7). | `msb` |

That is the entire rule. `lsb` is accepted for signature completeness and future use but does not affect the result in increment 1.

Worked cases:

| Standard | Sends | channel | ResolveBank | GetPatch called with |
|---|---|---|---|---|
| GM | (no bank select), PC 40 | melodic | 0 | (0, 40) — identical to today |
| GS | CC0=8, CC32=0, PC 25 | melodic | 8 | (8, 25) |
| GS drums | (CC0 ignored on ch9), PC 25 | 9 | 128 | (128, 25) — kit 25 |
| XG | CC0=1, PC 48 | melodic | 1 | (1, 48) |
| XG SFX | CC0=64, PC 0 | melodic | 64 | (64, 0) |
| XG alt kit | (CC0 ignored on ch9), PC 8 | 9 | 128 | (128, 8) — "room" kit |

### 8.2 `SoundBank.GetPatch(bank, program)` (modified ladder)
New contract (rung 2 added), for a non-empty bank:

1. **exact** `(bank, program)` → return it.
2. **bank-0 same program** — *only when `bank` is a melodic variation bank* (`bank != 0` and `bank != 128`): if `(0, program)` exists, return it. *(NEW)*
3. **same-bank lowest present program** — if `bank` has any preset, return its lowest-numbered one.
4. **melodic default** `(0, 0)` — skipped when `bank == 128`.
5. **any percussion** — lowest preset in bank 128.
6. **absolute fallback** — `patches[0]`.

Invariants preserved: never null, throws only on an entirely empty bank. Rung 2 is guarded to `bank ∉ {0, 128}`, so:
- **Bank-0 requests are untouched** (rung 2 is redundant with rung 1 and is skipped) → GM-only fonts unaffected (see §10.1).
- **Percussion (128) requests are untouched** → drums never degrade to a melodic instrument.

**Why rung 2 precedes rung 3:** a variation bank that lacks the requested program should fall to *the same instrument in the GM bank* (you asked for a viola variation; give the plain viola), not to *a different instrument that happens to be the lowest preset in the variation bank*. Rung 3 remains as the last same-bank resort (e.g. variation bank present, GM program also absent).

---

## 9. Cross-Cutting Concerns

- **State ordering / latch semantics:** the only correctness-critical timing rule. CC0/CC32 must be pure state writes; the patch change happens exclusively on Program Change. Enforced by *not* calling any synthesizer method in the CC0/CC32 cases.
- **GM reset (start of song):** `Render` initializes `bankMsb[ch]=0`, `bankLsb[ch]=0` for all 16 channels in the existing per-channel init loop. This makes the pre-first-ProgramChange bank deterministically 0 (GM), matching today.
- **CC121 Reset All Controllers:** leaves `bankMsb`/`bankLsb` untouched (A3). `ResetAllControllers` must **not** be extended to touch bank state — this is consistent with its existing deliberate exclusion of program/bank (design #7245). A comment should record that bank state is intentionally excluded from RAC.
- **Idempotency / determinism:** resolution is a pure function of `(channel, msb, lsb, program)` and the loaded bank; offline render is fully deterministic. No concurrency (single render thread), no retries, no caching needed.
- **Error handling:** unchanged — `GetPatch` still cannot throw for a non-empty bank; a hostile/sparse font degrades through the ladder. Out-of-range values are impossible (7-bit CC data bytes are 0–127 by parse).
- **Observability:** no new logging required; the render CLIs already expose selected patches. For before/after verification the integration test asserts patch identity directly (§11).

---

## 10. Quality Attributes & Trade-offs

### 10.1 Regression safety (the headline guarantee)
A **GM-only soundfont** (only `wBank` 0 and 128 present) must render identically before and after this change, for songs that do *and* do not send bank-select.

- **Song without bank select:** `bankMsb=0` → `ResolveBank` → 0 → `GetPatch(0, program)` → identical call to today. ✔
- **Song with bank select the font can't honor** (e.g. `CC0=8, PC=40` on a GM font): today bank-select is dropped, so `GetPatch(0, 40)` → GM viola. After the change, `GetPatch(8, 40)`: rung 1 miss → **rung 2** `(0, 40)` → **the same GM viola**. ✔ **Rung 2 is precisely what preserves parity** — without it the ladder would fall to `(0,0)` (acoustic grand), a regression. This is why rung 2 is a *required* part of the design, not an optional nicety.

Verified against the current `SoundBankTests` suite: all seven existing fallback tests remain green because each either requests bank 0 (rung 2 guarded off) or requests a variation/percussion bank where the "same program in bank 0" is also absent (so control still flows to the existing rung). Only the *description* of `GetPatch_MelodicBankAbsent_FallsBackToBankZeroProgramZero` becomes slightly narrower than its name implies (it now really tests "bank absent **and** GM program absent"); recommend renaming for clarity and adding positive tests for rung 2 (§11).

### 10.2 Simplicity vs. standard-awareness
We chose a **single MSB-based rule** over a GS/XG-detecting mapping. Trade-off: we do not honor XG's rarer channel-mode tricks (melodic-channel-to-drums via MSB 126/127; §13), and we ignore LSB. Benefit: one rule, no SysEx/NRPN parsing, no mode state machine, and it correctly covers the dominant cases in the corpus (GS variations, XG melodic variations, SFX banks, forced drums). Complexity is only added if §13 is accepted.

### 10.3 Boundary integrity
MIDI/GM/GS/XG semantics stay in `MidiSequencer`; `SoundBank` stays format/MIDI-neutral (it only ever sees an integer bank). Rung 2 respects this: "same program in bank 0" is a keyspace rule, not a MIDI rule.

**Alternatives rejected:**
- *Give `SoundBank` a `TryGetPatch`/null-returning API so the sequencer builds the ladder.* Rejected: fragments the never-null guarantee across two components and duplicates fallback logic; the ladder belongs in one place (A5).
- *Encode `bank = MSB*128 + LSB`.* Rejected: SF2 `wBank` cannot represent it; no demo font uses it; would break A1.
- *Resolve patches lazily at NoteOn.* Rejected: eager resolution at Program Change matches the existing architecture, keeps NoteOn hot-path allocation-free, and makes mid-note semantics fall out for free (A4).

---

## 11. Risks & Mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Rung 2 accidentally changes bank-0 or drum behavior | Silent regression across all songs | Guard rung 2 to `bank ∉ {0,128}`; the existing 7 `SoundBankTests` are the regression net; add explicit "bank-0 request unaffected" test. |
| CC0/CC32 mistakenly triggers a patch change (breaks latch semantics) | Wrong instrument mid-phrase | Assert in a unit test that bank-select alone produces **no** `SetChannelPatch` call. |
| Drum channel honoring bank-select and leaving bank 128 | Percussion becomes a melodic instrument | `ResolveBank` hard-returns 128 for ch 9 regardless of MSB/LSB; unit test asserts it. |
| Bank state leaking across a GM reset | Second song in a batch inherits stale bank | Init `bankMsb/bankLsb = 0` in the `Render` reset loop; covered by the GM-reset test. |
| Variation bank present but requested program absent falls to a random instrument | Audibly wrong instrument | Rung 2 (bank-0 same program) precedes rung 3 (same-bank lowest); unit test covers it. |
| Font stores drums only on `wBank` 127 (Omega has 127 **and** 128) and a song needs the 127-only kit via ch9 | Minor: ch9 gets the 128 kit instead | Accepted limitation; documented. Standard GM drum kits live on 128 and are reachable by program number. |

---

## 12. Migration / Rollout Strategy

Single self-contained change on the current working branch; no data migration, no staged rollout. Because rung 2 is guarded and the sequencer default bank is 0, the feature is inert for GM-only fonts and activates only when a multi-bank font is loaded *and* a song sends bank-select. Ships as **one PR** (one feature). The `SoundBank` rung and the `MidiSequencer` capture are two edits to the same feature and belong in the same PR (they are meaningless apart: the rung without the capture is dead code; the capture without the rung regresses GM-only fonts).

---

## 13. Open Questions

**OQ-1 (the one genuine product decision — needs Toni).** XG lets a *melodic* channel be switched to drums by sending **CC0 = 126 or 127** (plus a Program Change), independent of channel 9. Our default rule (`bank = MSB`) does **not** honor this: on a font that keeps drums only on `wBank` 128, `GetPatch(127, prog)` falls through rung 2/3 to a melodic instrument, not drums.

- **Option A (recommended for increment 1):** plain passthrough. Drums are forced only on ch 9. Simplest, regression-safe, covers the bulk of GS/XG usage. XG channel-to-drum switching is not honored when the font keeps drums on 128.
- **Option B:** treat `MSB ∈ {126, 127}` as "route to drum bank 128" on **any** channel. Honors XG channel-mode drums and happens to fit Omega GS (which stores drum kits on 127 as well as 128). Cost: injects standard-specific magic into `ResolveBank` and could misfire on a hypothetical font with genuine melodic presets on `wBank` 126/127.

Recommendation: ship **A** now; revisit **B** only if a corpus XG file demonstrably relies on it. This is the only decision that should block implementation, and only if Toni wants B in the first PR.

**OQ-2 (non-blocking):** should the `GetPatch_MelodicBankAbsent…` test be renamed to reflect its now-narrower meaning? Recommend yes (doc hygiene), done as part of this PR.

---

## 14. Implementation Guidance for the Next Agent (John)

Ordered build phases; all within one PR on the current branch. **No public API changes** — `MidiSequencer.Render` signature and `SoundBank.GetPatch` signature are unchanged.

1. **SoundBank rung 2 (do first — it's the regression-safety keystone).** Add the "bank-0 same program" rung to `GetPatch`, positioned after the exact match and before same-bank-lowest, guarded to `bank ∉ {MelodicBank, PercussionBank}`. Reuse the existing `TryExactMatch(MelodicBank, program, …)` helper. Confirm all 7 existing `SoundBankTests` still pass unchanged.
2. **Add rung-2 unit tests** to `SoundBankTests`: (a) variation bank absent but `(0, program)` present → returns `(0, program)`; (b) variation bank present but requested program absent, `(0, program)` present → returns `(0, program)` (rung 2 beats rung 3); (c) bank-0 request behavior unchanged; (d) percussion request unchanged. Rename `GetPatch_MelodicBankAbsent…` per OQ-2.
3. **Per-channel bank state in `MidiSequencer`.** Add `int[] bankMsb`, `int[] bankLsb` alongside the existing arrays in `Render`; initialize both to 0 in the per-channel reset loop; thread both into `ApplyMessage` exactly as `cc7`/`cc11` are threaded.
4. **Capture CC0/CC32.** In the Controller branch of `ApplyMessage`, add cases for `ControllerType.BankSelect` (store MSB) and `ControllerType.BankSelectFine` (store LSB), each a pure state write with **no** synthesizer call. Place them among the other CC cases.
5. **Latch on Program Change.** Introduce `ResolveBank(channel, msb, lsb)` (ch 9 → 128; else → msb) and route `ProgramChange` through `soundBank.GetPatch(ResolveBank(...), program)`. Fold the existing `ResolveProgramPatch` percussion/melodic split into `ResolveBank`. Keep the start-of-song GM reset calling `GetPatch(ResolveBank(ch, 0, 0), DefaultProgram)` so its behavior is provably identical to today.
6. **Guard the RAC boundary.** Add a comment in `ResetAllControllers` recording that bank MSB/LSB are intentionally *not* reset (A3), mirroring the existing program/bank exclusion note.
7. **Sequencer unit tests** (new file `MidiSequencerBankSelectTests.cs`, following the `MidiSequencerProgramChangeTests` pattern with `RecordingSynthesizer` + `StubPatch` + `MidiTrackEventBuilder`):
   - CC0/CC32 alone produce **no** additional `SetChannelPatch` call (latch semantics).
   - `CC0=8, CC32=0, ProgramChange(5)` on a melodic channel selects the `(8, 5)` stub patch.
   - Bank select then Program Change on **ch 9** still resolves within bank 128 regardless of MSB/LSB.
   - GM-only bank: `CC0=8, ProgramChange(40)` resolves to the `(0, 40)` stub (rung-2 parity, no regression).
   - Bank state resets to 0 across the GM reset (a fresh `Render` does not inherit a prior bank).
8. **Integration / render test** (the before/after proof the brief asks for):
   - Load `C:\dev\claude\OmegaGMGS2.sf2`, render a short synthetic sequence that sends `CC0=8 → ProgramChange` on a melodic channel plus a NoteOn, and assert the channel's resolved patch is a **bank-8 preset**, not the bank-0 GM preset. Use `RecordingSynthesizer` to capture the `SetChannelPatch` patch identity (map it back to the loaded preset's bank via the loader, or assert the selected preset name differs from the bank-0 program name).
   - **GM-only no-regression render:** with the Florestan/GM bank, render the same bank-select sequence and assert the selected patch equals `GetPatch(0, program)` (identical to pre-change behavior).
   - Optionally include the XG font (`yamaha_xg_sound_set_re-map.sf2`) with `CC0=1 → ProgramChange` to prove a bank-1 variation is reached.
9. **Manual before/after render for the PR** (John): render one corpus file that uses bank-select (e.g. any of the 304 CC0 files) through Omega GS both with the change and, for contrast, note that the pre-change build always played bank 0. Attach the WAVs / spectrogram note to the PR.

---

## Appendix — Verified facts (so the implementer need not re-derive)

- `SoundBank` keyspace already holds melodic banks 0–127 and drum bank 128 together (`Sf2SoundBankLoader.BuildPatches` stores raw `preset.BankNumber`); **no loader/keyspace change needed** (brief item #4, negative).
- `ControllerType.BankSelect = 0` and `ControllerType.BankSelectFine = 32` already exist — no enum change.
- `ChannelMessage.Data1` carries the CC number, `Data2` the value; `MidiChannel` is the 0–15 index. `MidiTrackEventBuilder.Controller(delta, channel, controller, value)` already exists for tests.
- `SetChannelPatch` affects only future NoteOns (`ISynthesizer` contract) → mid-note bank change needs no code.
- Existing per-channel arrays (`cc7`, `cc11`, `selectedRpn`, `bendRange`) are the established plumbing pattern to copy for `bankMsb`/`bankLsb`.
