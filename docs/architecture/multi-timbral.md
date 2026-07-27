# Architectural Document: Multi-Timbral Per-Channel Instrument Routing (PR 11)

**Author:** Sarah (architect) · **Date:** 2026-07-27 · **Source task:** DiVoid #7115 · **Project:** #6128 · **Map root:** #6708
**Roadmap:** MIDI-integration design #7098 PR 11 (recommended immediate next; biggest "sounds like the song" jump).
**Builds on:** MIDI integration PR 10 (sequencer #7114, task #7095), SF2 loader #6754 / resolver #6752, Synthesizer #6734.
**Load-bearing conventions:** Code Contracts #114 (§0 body-comment ban, §1 naming, §5.5 XML-doc gate, one-type-per-file). Design Contracts #1136 (§1 KISS/DRY/YAGNI, §2 existing-systems-first, §3 configurability, §4 less-is-better, §5 checklist). PR-shape #1165 (design + first increment in ONE PR).

---

## 1. Problem Statement

PR 10 renders a real multi-track MIDI song to WAV, but **every channel plays the single default patch** (`Synthesizer` holds one `defaultPatch`; `NoteOn(channel, …)` ignores the channel for patch selection; `MidiSequencer` parses but ignores `ProgramChange`). The audible result is "all piano" — the DKC2 and FF8 renders are recognizable melodically but timbrally uniform.

Real songs assign an instrument per MIDI channel via **ProgramChange**, and reserve **MIDI channel 10 (index 9) for percussion**, where each *key* is a different drum. The goal of this PR is that a song's bass, lead, pad, and drums render as themselves.

**Success criteria:**
1. A `ProgramChange` on a channel causes subsequent `NoteOn`s on that channel to start voices from the SF2 preset selected by that program number.
2. Channel index 9 routes to the percussion bank (bank 128), so drums render as drums (per-key drum samples), not a pitched instrument.
3. A note that sounds before any `ProgramChange` (sparse/mid-song program events) still plays a sensible GM default (piano for melodic channels, standard kit for channel 9).
4. Missing/out-of-range program or bank never crashes — it degrades to a safe fallback patch.
5. Re-rendering `07dkc2bram.mid` and `a-FF8battle-theme.mid` through the Florestan GM SF2 produces audibly distinct instruments (bass vs lead vs drums), A/B-distinct from the PR-10 all-piano render.

---

## 2. Scope & Non-Scope

### In scope
- Per-channel current-patch state in the synth engine + a seam to set it.
- `NoteOn` starts the voice from the channel's current patch.
- `ProgramChange` application in the sequencer/driver (currently ignored).
- SF2 preset lookup by **(bank, program)** with a safe fallback chain.
- GM channel-9 → percussion bank (128) routing.
- GM default program per channel so a note before any ProgramChange sounds right.
- Unit fixtures (deterministic) + a graceful-skip real-song integration test; CLI wiring; manual A/B WAV deliverable.

### Out of scope (explicitly deferred — do NOT bundle)
| Deferred | Target PR |
|---|---|
| CC7/CC10/CC11 channel volume / pan / expression, per-voice pan, voice-stealing, mix headroom / limiter | PR 12 |
| **The DKC2 clipping / distortion** (a mixing problem, not a routing problem) | PR 12 |
| PitchWheel, mod-wheel CC1, sustain CC64 | PR 13 |
| Bank-select CC0/CC32 beyond what GM drums strictly need | PR 13+ |
| SMPTE timing, real-time NAudio sink, SMF format-0/2 edge cases | PR 14 |

Multi-timbral routing will change the overall mix balance (more/louder simultaneous instruments). That is expected and acceptable in this PR; the clipping fix is PR 12's job. Do not add a limiter or gain-staging here.

---

## 3. Assumptions & Constraints

- **A1** — `Sf2PresetHeader` already carries `BankNumber` and `PatchNumber` (verified: `Sf2PresetHeader.cs:27,32`), and `Sf2Patch` exposes its `Preset` publicly (`Sf2Patch.cs:49`). The (bank, program) key is available at load time without new parsing.
- **A2** — The SF2 percussion preset (bank 128) resolves per-key drums *inside its own zones* via `KeyRange` generators. `Sf2RegionResolver.TryResolve(key, velocity, …)` already selects a different instrument/sample per key (`Sf2RegionResolver.cs:108–132`). **Therefore channel-9 percussion needs only bank routing — no per-key logic in the synth or driver.** Selecting the bank-128 patch and calling its existing `StartVoice(key, …)` yields per-key drums for free.
- **A3** — `ProgramChange` carries the program number in the single data byte `Data1` (`ChannelMessage.cs:16,50–53`; `DataBytesPerType(ProgramChange) => 1`). Program = `channel.Data1`.
- **A4** — GM convention: channel index 9 = percussion (bank 128, program 0 = standard kit); all other channels default to program 0 (acoustic grand piano) in bank 0 on reset.
- **A5** — The Florestan Basic GM/GS SF2 contains a bank-128 drum preset. If a supplied SoundFont lacks one, the fallback chain (§8) degrades drums to a melodic patch (pitched, non-crashing). Flagged as a known degradation, not a defect.
- **Constraint** — Offline render path only; no real-time constraint. Steady-state `Read` must still allocate nothing (INV: pre-sized buffers). Per-channel patch state is a fixed 16-element array sized at construction — no steady-state allocation.
- **Constraint** — Both target framework builds must be 0-warning; XML-doc gate applies to all accessibilities (Code Contracts §5.5).

---

## 4. Architectural Overview

Three coordinated seams, respecting the PR-10 dependency direction (parser is synth-free; the **synth is MIDI-agnostic**; the **sequencer is the only MIDI↔synth coupling**, and is the home for GM/MIDI semantics).

```
  Formats.Sf2 (SF2-aware)          Synthesis (MIDI-agnostic)         Sequencing (MIDI↔synth coupling)
  ┌───────────────────┐           ┌─────────────────────────┐       ┌──────────────────────────────┐
  │ Sf2SoundBankLoader│  builds   │ SoundBank (concrete)     │ used  │ MidiSequencer.Render(..,bank)│
  │  .Load(stream)    │──────────▶│  GetPatch(bank,program)  │◀──────│  • init 16 chan GM defaults  │
  │  → SoundBank      │           │  Patches (enumeration)   │       │  • on ProgramChange:         │
  └───────────────────┘           │  + safe fallback chain   │       │      bank = ch==9?128:0      │
                                   ├─────────────────────────┤       │      patch = bank.GetPatch() │
                                   │ ISynthesizer            │       │      synth.SetChannelPatch() │
                                   │  + SetChannelPatch(ch,p) │◀──────│  • NoteOn/NoteOff as before   │
                                   │ Synthesizer:            │       └──────────────────────────────┘
                                   │  IPatch[16] channelPatch│
                                   │  NoteOn ⇒ channelPatch[ch].StartVoice
                                   └─────────────────────────┘
```

**Key decision — the synth deals in patches, the driver deals in program numbers and banks.** The synth gains "one current patch per channel"; it never learns what a program number, a bank, SF2, or GM is. All GM/MIDI semantics (program→bank rule, channel-9 = percussion, program 0 default) live in the sequencer. This is the same boundary discipline #7098 established (synth stays MIDI-agnostic) and is why the `ISynthesizer` change is `SetChannelPatch(channel, IPatch)` — matching #7098's own wording "patch per channel" — rather than `ProgramChange(channel, program)` which would drag bank/GM knowledge into the engine.

**Key decision — `SoundBank` is a concrete, format-neutral class in `Synthesis`, not an interface.** It holds a `(bank, program) → IPatch` lookup plus the fallback chain. Living in `Synthesis` (alongside `IPatch`), it lets `Sequencing` consume it without depending on `Formats.Sf2` (the concrete boundary that justifies it — not "future SFZ", which would be YAGNI). One implementation, no interface: an interface here would be indirection (Design Contracts §4).

---

## 5. Components & Responsibilities

### 5.1 `SoundBank` (new — `Pooshit.AudioSynth.Synthesis`)
- **Owns:** the mapping from a MIDI (bank, program) address to an `IPatch`, and the fallback policy when an address is absent.
- **Does:** `GetPatch(int bank, int program)` → never-null, never-throws patch (fallback chain §8); expose the loaded patches for enumeration/count (`Patches`, `Count`) for existing consumers.
- **Does NOT own:** parsing, SF2 knowledge, MIDI knowledge, GM defaults, or channel state. It is a passive keyed lookup. It is **not** an "instrument manager" — resist adding channel state, program-change history, or CC handling here.
- **Constructed from:** an ordered collection of `(bank, program, IPatch)` entries supplied by whatever loader built them.

### 5.2 `ISynthesizer` / `Synthesizer` (change — `Pooshit.AudioSynth.Synthesis`)
- **Adds to the contract:** `void SetChannelPatch(int channel, IPatch patch)` — sets the current patch for one of the 16 channels.
- **Owns:** a fixed `IPatch[16]` current-patch-per-channel array, initialized to the constructor's patch (subsuming the single `defaultPatch` field — the field is no longer needed as a stored field once the array holds the fill value).
- **Changes:** `NoteOn(channel, key, velocity)` starts the voice from `channelPatch[channel]` instead of `defaultPatch`.
- **Unchanged:** constructor signature (`Synthesizer(options, IPatch)` — the patch becomes the initial fill for all 16 slots and the safety net for channels never set); `NoteOff` (matches sounding voices by channel+key, patch-independent); `Read`/`Finalize`/voice pool.
- **Invariant:** `channel` must be in `[0,15]` for `SetChannelPatch` and `NoteOn`; a public entry point indexing a 16-element array must guard the range (throw `ArgumentOutOfRangeException`). This is a real API invariant, not impossible-scenario defense (Design Contracts §6) — the driver always passes a 4-bit `MidiChannel`, but direct callers must be guarded against an index-out-of-range crash.

### 5.3 `MidiSequencer` (change — `Pooshit.AudioSynth.Sequencing`)
- **`Render` gains a `SoundBank soundBank` parameter** and becomes the home of all GM/MIDI routing semantics.
- **Owns:** (a) channel initialization to GM defaults before the schedule loop; (b) the program→(bank, patch) resolution on each `ProgramChange`; (c) the channel-9 = percussion rule.
- **Does, before the loop:** for each channel 0–15, `SetChannelPatch(ch, soundBank.GetPatch(ch == 9 ? 128 : 0, 0))` — GM reset. (Reuses the same resolution path as ProgramChange — DRY.)
- **Does, in the loop:** the existing message-apply step now also handles `ProgramChange`: `bank = channel.MidiChannel == 9 ? 128 : 0`, `patch = soundBank.GetPatch(bank, channel.Data1)`, `synth.SetChannelPatch(channel.MidiChannel, patch)`. NoteOn/NoteOff behavior is unchanged (they route by channel; the synth now uses the channel's assigned patch).
- **Unchanged:** `BuildSchedule` (ProgramChange already flows through as a `ChannelMessage` in the schedule — only the apply step ignored it; the pure schedule tests stay green), `ReleaseTailSeconds`, the gap-render/tail structure.
- The private `ApplyNoteMessage` is renamed to `ApplyMessage` (it now applies more than notes) and takes the `soundBank` (or a small bound closure) to resolve program changes.

### 5.4 `ISoundBankLoader` / `Sf2SoundBankLoader` (change — `Pooshit.AudioSynth.Formats` / `.Sf2`)
- **`ISoundBankLoader.Load` return type changes** from `IReadOnlyList<IPatch>` to `SoundBank`. A "sound-bank loader" honestly returns a sound bank; the flat list was the v1 stopgap when only `patches[0]` was consumed.
- `Sf2SoundBankLoader` builds the `SoundBank` at the existing `BuildPatches` step, keying each `Sf2Patch` by its `preset.BankNumber`/`preset.PatchNumber` (both already in hand there — no new parsing, no downcast, since the loader legitimately knows `Sf2PresetHeader`).
- **Does NOT own:** the fallback policy (that is `SoundBank`'s) or GM defaults (the driver's).

### 5.5 `Pooshit.AudioSynth.MidiRender` CLI (change — `tools/`)
- `SoundBank bank = loader.Load(stream);` replaces the `IReadOnlyList<IPatch> patches` local.
- Emptiness check uses `bank.Count == 0` (or `bank.Patches.Count`).
- Synth constructed with `new Synthesizer(options, bank.GetPatch(0, 0))` (GM piano as the safety-net default).
- `MidiSequencer.Render(sequence, synth, sink, bank)` — passes the bank.

---

## 6. Interactions & Data Flow

**Render sequence (conceptual):**
1. CLI/test loads the SF2 → `SoundBank` (keyed by bank/program, fallback ready).
2. CLI/test builds the `Synthesizer` (ctor patch = GM piano safety net) and the `TimedMessageSequence`.
3. `MidiSequencer.Render(sequence, synth, sink, bank)`:
   a. **GM reset:** for ch 0–15, resolve the GM default patch and `SetChannelPatch`. Channel 9 → bank 128.
   b. Build the sample-offset schedule (unchanged).
   c. For each scheduled event, render the gap, then apply:
      - `ProgramChange` → resolve `(bank(ch), program=Data1)` → `SetChannelPatch(ch, patch)`.
      - `NoteOn` → `synth.NoteOn(ch, key, vel)` → engine starts a voice from `channelPatch[ch]`.
      - `NoteOff` → unchanged.
      - other channel messages → ignored (deferred PRs).
   d. Render the release tail (unchanged).

**Concurrency / voice interaction:** a mid-song `ProgramChange` changes only which patch *future* `NoteOn`s use; voices already sounding keep the patch they were started with (correct GM behavior — no retro-active timbre change). No synchronization concern (offline, single-threaded).

---

## 7. Data Model (Conceptual)

| Entity | Key | Owns | Notes |
|---|---|---|---|
| SoundBank entry | (bank:int, program:int) | one `IPatch` | Built once at load; immutable |
| Channel patch state | channel index 0–15 | current `IPatch` | Lives in `Synthesizer`; mutated by `SetChannelPatch` |
| GM routing rule | channel index | bank number | `ch == 9 ? 128 : 0`; lives in `MidiSequencer` |

No persistence, no schema. The (bank, program) key may be represented as a packed integer (`(bank << 8) | program`, both 0–255/0–127) or a value tuple — an implementation choice, not an architectural one; a dictionary is sufficient (Design Contracts §2 — resist a general abstraction).

---

## 8. Contracts & Interfaces (Abstract)

### `SoundBank.GetPatch(bank, program)` — the fallback contract (never null, never throws)
Resolution order:
1. **Exact** `(bank, program)` present → return it.
2. **Same bank, nearest program** — if the bank exists but not that program, return the same bank's numerically-nearest present program (a GM-adjacent instrument beats a wrong-family one). *(Simplest acceptable form: same-bank lowest-numbered present program. "Nearest" is a refinement the implementer may keep or simplify to "lowest present" — either satisfies "never crash, stay in-family"; pick the simpler that passes the fixtures.)*
3. **Melodic default** — for a melodic bank (≠128) with no match, return bank 0 program 0 (GM piano) if present.
4. **Percussion default** — for bank 128 with no exact match, return any bank-128 preset (standard kit). If bank 128 is entirely absent, fall through to (5) — drums degrade to a melodic patch (pitched), non-crashing (A5).
5. **Absolute fallback** — the first patch in the bank (`Patches[0]`). A `SoundBank` is only constructed from a non-empty preset set (the CLI already rejects empty SoundFonts), so (5) always yields a patch.

The fallback chain is real, testable logic and is the single reason `SoundBank` is a class rather than a raw `Dictionary`. Keep the chain small and covered by unit fixtures.

### `ISynthesizer.SetChannelPatch(channel, patch)`
- **Input:** channel 0–15 (guarded), non-null patch.
- **Effect:** subsequent `NoteOn(channel, …)` start voices from `patch`. No effect on already-sounding voices.
- **Semantics:** idempotent; last-write-wins; no history retained.

### `MidiSequencer.Render(sequence, synth, sink, soundBank)`
- **Added input:** `soundBank` (non-null). Single signature — no overload retaining the old 3-arg form (an overload would be a compat shim, Design Contracts §6; the one existing caller updates).

---

## 9. Cross-Cutting Concerns

- **Error handling / robustness:** the note path must never throw on musically-imperfect input. `GetPatch` never throws/returns null; out-of-range channel is the only guarded API misuse (throws, as a programmer-error signal). Consistent with the SF2 resolver's "degrade to no-match, don't throw on the note path" precedent (#6752).
- **Observability:** none added. (No logging framework in this offline engine; deferred.)
- **Performance:** per-channel patch array is 16 references, allocated once at construction. `GetPatch` runs at ProgramChange frequency (rare relative to notes) and at the 16-channel reset — dictionary lookups, negligible. Steady-state `Read` is untouched → still zero-allocation.
- **Idempotency / determinism:** `BuildSchedule` stays pure; the render is deterministic for a given (song, SoundBank).

---

## 10. Quality Attributes & Trade-offs

| Decision | Chosen | Alternative rejected | Why |
|---|---|---|---|
| Synth seam shape | `SetChannelPatch(channel, IPatch)` — synth holds patches | `ProgramChange(channel, program)` — synth resolves program→patch | Keeping program/bank/GM out of the engine preserves #7098's MIDI-agnostic-synth boundary; matches #7098's "patch per channel" wording. Alternative would couple the engine to bank semantics. |
| Bank lookup | concrete `SoundBank` class in `Synthesis` | `ISoundBank` interface | One implementation ⇒ interface is indirection (§4). Concrete-in-`Synthesis` still lets `Sequencing` consume it without a `Formats.Sf2` dependency — the actual boundary concern (not "future SFZ", which is YAGNI). |
| GM semantics home | `MidiSequencer` (driver) | Synth or `SoundBank` | The driver is the MIDI↔synth coupling layer (#7098); GM conventions (ch9=drums, program 0 default, program→bank) are MIDI semantics and belong there, keeping synth and bank format/MIDI-neutral. |
| Channel-9 per-key drums | reuse existing resolver via bank-128 patch `StartVoice(key)` | new per-key drum-mapping layer in synth/driver | The SF2 percussion preset already maps keys→drums in its zones (A2). A new layer would duplicate existing resolution — YAGNI + DRY. |
| Loader return type | change `Load` → `SoundBank` | keep `IReadOnlyList<IPatch>`, build bank externally | External build needs (bank,program), which only the SF2 layer knows; keeping the flat list forces every real consumer to rebuild the index (a restatement smell, §2). Honest evolution: a bank loader returns a bank. Trade-off: ~14 call-site edits (§11 inventory) — mechanical and bounded. |
| GM default init | driver initializes all 16 channels | rely on synth ctor patch for melodic + only override ch9 | Putting *all* GM-default knowledge in the driver keeps the synth ctor patch a pure safety net (its musical identity stops mattering to the render path). Trivial 16-iteration cost. |
| Voice pool | unchanged (fixed `MaxVoices`, drop-on-full) | add voice-stealing now | Voice-stealing is PR 12. See §11 risk R1. |

**No new config knobs.** Bank numbers (0, 128), default program (0), and channel-9 index are fixed GM constants, named clearly in code as `const` (Design Contracts §3 — magic numbers stay magic; none of them vary by environment or operator).

---

## 11. Risks & Mitigations

- **R1 — Voice starvation with many simultaneous instruments.** With bass + lead + pads + drums live, more voices sound at once than in the all-piano render. The pool is fixed (`MaxVoices`, 128 in the render path) and `NoteOn` silently drops when full (no steal). *Mitigation:* 128 voices is comfortably above typical SNES/PS1-era MIDI polyphony; percussion voices are short one-shots that free quickly. **Accept for this PR; flag voice-stealing as PR 12.** Watch-item, no change here.
- **R2 — SoundFont without a bank-128 drum preset.** Drums degrade to a melodic (pitched) patch via the fallback chain. *Mitigation:* documented degradation (A5); the deliverable-proof render confirms Florestan has drums. Not a crash.
- **R3 — Loader return-type change breaks call sites.** *Mitigation:* full reader inventory below; all are mechanical.
- **R4 — Channel index out of range from a malformed message.** *Mitigation:* the synth guards `[0,15]`; the driver only ever passes a 4-bit `MidiChannel`.

**Reader inventory — every consumer of `ISoundBankLoader.Load` / `IReadOnlyList<IPatch> patches` (Design Contracts §5 carrier-swap rule):**

| Site | Current use | Change |
|---|---|---|
| `Sf2SoundBankLoader.cs` (`Load`, `ParseSeekable`, `ParseSoundFont`, `BuildPatches`) | returns `IReadOnlyList<IPatch>` | return `SoundBank`; `BuildPatches` builds the keyed bank |
| `ISoundBankLoader.cs` | `IReadOnlyList<IPatch> Load(Stream)` | `SoundBank Load(Stream)` |
| `tools/MidiRender/Program.cs:35–50` | `patches`, `patches.Count`, `patches[0]` | `bank`, `bank.Count`, `bank.GetPatch(0,0)`; pass `bank` to `Render` |
| `test/…/Midi/MidiSongRenderTests.cs:65–83` | `patches`, `patches.Count`, `patches[0]`, `Render(seq,synth,sink)` | `bank.Patches`/`Count`, `bank.GetPatch(0,0)`, `Render(seq,synth,sink,bank)` |
| `test/…/Sf2/Sf2FirstAudioTests.cs:52,76,88,108` | `patches`, `patches.Count`, `patches[0]`, `patches[0].StartVoice` | `bank.Patches[0]` / `bank.GetPatch(...)` (parse-focused; minimal LHS retype) |
| `test/…/Sf2/Sf2LoaderTests.cs` (~8 assignment sites: lines 25,44,69,139,150,168,183,199) | `IReadOnlyList<IPatch> patches = Loader.Load(...)` then `patches[0]`/`Count` | `SoundBank bank = Loader.Load(...)` then `bank.Patches[0]`/`Count` |
| `test/…/Sf2/Sf2LoaderTests.cs` (~20 `Assert.Throws(() => Loader.Load(...))` sites) | return discarded | **no change** (return type change does not affect discarded-return lambdas) |

---

## 12. Migration / Rollout Strategy

Single atomic PR (private repo, atomic deploy — no deprecation window, no compat overload; Design Contracts §6). The three seams change together because the loader-return-type change and the `Render` signature change are compile-coupled to their call sites. No feature flag.

---

## 13. Open Questions

- **O1 — "Nearest program" vs "lowest present" in §8 step 2.** Recommendation: implement the simpler "lowest-numbered present program in the same bank" first; upgrade to true numeric-nearest only if a fixture demonstrates an audibly wrong pick. Either satisfies success-criterion 4. *(Architect's call: start simple; no user input needed unless a render sounds wrong.)*
- **O2 — FF8 track for the A/B deliverable.** `a-FF8battle-theme.mid` is present in the dev tree next to `07dkc2bram.mid`. Confirm it is the intended FF8 track (the existing PR-10 render is `song-ff8-balamb.wav`; the battle theme is the available FF8 asset). Non-blocking — both are valid.
- **O3 — GM bank-select CCs.** GM drums are addressed purely by channel-9 = bank 128 here; true CC0/CC32 bank select is deferred (PR 13+). Confirm no target song depends on non-drum bank switching in this PR. Assumed no.

---

## 14. Implementation Guidance for the Next Agent

Ordered build phases (all in ONE PR with this design doc; no code in this document):

1. **`SoundBank` (new type, `Synthesis`).** Keyed (bank, program) → IPatch lookup + `GetPatch` fallback chain (§8) + `Patches`/`Count`. Unit-test the fallback chain against stub `IPatch` identities first (pure, deterministic).
2. **`Synthesizer` per-channel patch.** Add `IPatch[16]` filled from the ctor patch; add `SetChannelPatch` (guard channel 0–15); route `NoteOn` through `channelPatch[channel]`. Drop the now-subsumed `defaultPatch` field if unused. Unit-test: NoteOn on distinct channels starts voices from distinct patches (two distinguishable stub patches); an unset channel uses the ctor patch.
3. **Loader → `SoundBank`.** Change `ISoundBankLoader.Load` return type; build the bank in `Sf2SoundBankLoader.BuildPatches` from `preset.BankNumber`/`PatchNumber`. Update the full reader inventory (§11).
4. **`MidiSequencer` GM routing.** Add the `soundBank` param to `Render`; GM-reset the 16 channels; apply `ProgramChange` (ch9→bank128) in the renamed `ApplyMessage`. Named `const`s for bank 0 / bank 128 / percussion channel index / default program. Unit-test with a recording `ISynthesizer` fake + stub `SoundBank`: a `ProgramChange` on a channel causes the next `SetChannelPatch` to carry the program-resolved patch; channel-9 resolves bank 128 (percussion routing assertion — success criteria 1 & 2).
5. **CLI wiring.** `MidiRender/Program.cs` per §5.5.
6. **Integration (graceful-skip).** Render `07dkc2bram.mid` and `a-FF8battle-theme.mid` through Florestan-as-`SoundBank`: non-silent, bounded (mirror the existing `MidiSongRenderTests` shape). Optionally assert the drum channel produced voices.
7. **Deliverable proof (manual).** Re-render both songs via the CLI → WAVs with audibly distinct instruments; A/B against the PR-10 all-piano `song-ff8-balamb.wav`.

**Pre-flight before PR:** `dotnet build` both TFMs 0-warning; `dotnet test` green; body-comment grep = 0; XML-doc gate over all accessibilities; one-type-per-file (each new type — `SoundBank` — in its own file).

---

## 15. Pre-Design Checklist (Design Contracts #1136 §5)

**KISS / DRY / YAGNI**
- [x] No new type mirroring an existing type's value-space. `SoundBank` is a keyed lookup with no equivalent today.
- [x] No new abstraction with one impl and no second planned — `SoundBank` is **concrete**, not an interface, precisely for this reason.
- [x] No element justified by "might need later" — channel-9-per-key reuses existing resolution; no speculative CC/pan/steal hooks.
- [x] No deprecation window / feature flag / compat overload (atomic deploy).
- [x] No inline-vs-extract duplication above threshold — the program→(bank,patch) resolution is used by both GM-reset and ProgramChange and is a **single** shared path (DRY by construction), not inlined at 2 sites.

**Existing systems first**
- [x] Audited: `SoundBank` earns its place via the fallback chain (real logic) + the `Sequencing`-must-not-depend-on-`Formats.Sf2` boundary — named concrete concerns, not "cleanliness."
- [x] Channel-9 drums reuse `Sf2RegionResolver` rather than a new per-key layer.
- [x] Loader return-type change avoids every consumer rebuilding a (bank,program) index (restatement smell).

**Configurability**
- [x] No new config knobs. Bank 0/128, program 0, channel index 9 are fixed GM `const`s (§3-compliant).

**Less is better**
- [x] Synth ctor unchanged; single `Render` signature (no overload); `defaultPatch` field dropped if subsumed.
- [x] Trade-offs named explicitly (§10) where a change (loader return type) carries a cost (call-site churn).
- [x] Reader inventory enumerates **every** affected call site incl. the no-change `Assert.Throws` sites (§11).

**Document discipline**
- [x] Cites Code Contracts #114 and Design Contracts #1136 as load-bearing.
- [x] Out-of-scope listed explicitly (§2), not merely absent.
- [x] No multi-paragraph "why we keep X" padding.
- [x] Does not supersede a prior design (extends #7098's roadmap); no predecessor banner needed.
```
