# Architectural Document: Tracker-Style Music Format + Timeline Adapter

> Repo path: `docs/architecture/tracker-format.md`. Mirror of DiVoid documentation node (see task #7451, project #6128, foundation #7361). Design + first implementation increment ship in ONE PR (DiVoid #1165).
> Load-bearing contracts: **Code Contracts #114 §0** (KISS/DRY/YAGNI) and **Design Contracts #1136** (§1–§4). The Pre-Design Checklist (#1136 §5) is walked verbatim in §16 below.

---

## 1. Problem Statement

Toni wants an **in-engine, tracker-style music format** (S3M/MOD/IT lineage) plus an adapter that makes it playable through the already-shipped AudioSynth Timeline core. MIDI is unsuitable as an authoring model because it is "just one long event stream"; tracker music is authored as a **grid of patterns** (rows × channels) plus an **order list** (the sequence of pattern indices to play). Each grid **cell** carries an optional note and an optional effect/command.

The deliverable is two things:

1. A **plain-old-data (POD) model** — `Song` / `Pattern` / `Cell` / instrument table + supporting enums — that an external game engine can serialize to JSON **itself** (JSON is explicitly not this library's job).
2. A **`TrackerTimelineImporter`** that lowers a `Song` onto the existing `Timeline`, so the unchanged `RealtimeSequencer` plays it as live audio — structurally the twin of the existing `MidiTimelineImporter`.

**Success criteria.** (a) The model is trivially serializable (public value types / arrays / enums; no behavior, no object graphs, no engine handles). (b) A trivial one-pattern song lowers to a `Timeline`, `Compile()`s, and renders non-silent, bounded audio through a real SoundBank via `RealtimeSequencer` — exactly as the MIDI path does. (c) The effect column is an extensible seam that interprets a minimal set now and ignores unknown commands cleanly. (d) No change to `Synthesizer`, `RealtimeSequencer`, `NeutralEvent`, `Timeline`, or `SoundBank`.

## 2. Scope & Non-Scope

**In scope**
- POD types: `Song`, `Pattern`, `Cell`, `Instrument`, and the `TrackerEffectCommand` enum + a `TrackerNotes` constants/helper holder.
- `TrackerTimelineImporter.Import(Song, int sampleRate) → Timeline` — order-list walk, row→sample-offset lowering, per-cell `NeutralEvent` emission.
- Minimal interpreted behavior: note trigger, monophonic-channel retrigger/note-off/note-cut, volume column → channel gain, and two accumulator-only effect commands (Set Speed, Set Tempo).
- Unit tests for the model invariants and the importer core, plus one end-to-end "trivial song renders bounded non-silent audio" proof mirroring `MidiSongRenderTests`.

**Explicitly out of scope**
- **JSON / serialization code.** Not one line. The model is *made* serializable; serializing is the caller's concern.
- **The per-tick effect engine** (arpeggio, volume/pitch slides, vibrato, tremolo, retrigger-on-tick, note-delay, note-cut-on-tick). v1 emits at row boundaries only. The timing model is chosen so this can be added later without reshaping the format.
- **An editor / UI.** The model is shaped to be editor-friendly (see §10) but no editor ships here.
- **Any synth/driver/engine change.** If the design appears to need one, it bounces to the orchestrator (it does not).
- **Loop-region wiring.** Looping is already a `RealtimeSequencer` capability (`loopStart`/`loopEnd`); the caller chooses it. See §13 open question OQ-3 for the optional length seam.
- **Tracker-file import** (reading actual `.mod`/`.s3m`/`.it` binaries). This is a *native* format, not a MOD parser.
- **>16 channels.** The shipped `Synthesizer` is a fixed 16-channel engine (`Synthesizer.cs:17`, throws for `channel ≥ 16`). See §3 CN-1.

## 3. Assumptions & Constraints

| Id | Constraint / Assumption | Source (verified in real code) |
|----|--------------------------|-------------------------------|
| CN-1 | **The synth has exactly 16 channels.** `Synthesizer` allocates `[16]` per-channel arrays and `SetChannelPatch/Gain/...` throw `ArgumentOutOfRangeException` for `channel ∉ [0,15]`. → `Song.ChannelCount` must be in `[1,16]`; the importer maps tracker channel `i` → synth channel `i` 1:1 and throws a clear error at import if `ChannelCount > 16`. | `Synthesis/Synthesizer.cs:17,116` |
| CN-2 | **A tracker channel is monophonic** (one sounding note at a time) — the classic MOD/S3M/IT model. This makes `SetChannelGain` the *exact* per-note volume control (one voice per channel), not an approximation. | Tracker domain; `ISynthesizer` gain is per-channel |
| CN-3 | **Patch is selected by the instrument column, not fixed per channel.** The synth's `SetChannelPatch` only affects *future* `NoteOn`s on that channel (`ISynthesizer.cs:22`), which matches tracker semantics exactly (an instrument number in a cell selects the timbre for the note that follows). | `ISynthesizer.cs:22`; tracker domain |
| CN-4 | **`SoundBank.GetPatch(bank, program)` never returns null and never throws** for a non-empty bank (documented fallback chain). The importer emits symbolic `(bank, program)` in `SetPatch`; the `RealtimeSequencer` resolves it live. The importer never touches a `SoundBank` or `IPatch` — it stays fully symbolic. | `Synthesis/SoundBank.cs:57` |
| CN-5 | **The importer mirrors `MidiTimelineImporter`**: a `static` class, `Import(...) → Timeline`, seeding per-channel defaults at offset 0, then emitting offset-addressed `NeutralEvent`s. No new sequencing seam is introduced. | `Sequencing/MidiTimelineImporter.cs:38` |
| CN-6 | Target frameworks `netstandard2.0;net8.0`, `Nullable=enable`, XML docs required (`GenerateDocumentationFile=true`), namespace-block style, explicit types, K&R braces, one type per file. | `Pooshit.AudioSynth.csproj` |
| CN-7 | The consuming engine is Godot/.NET-family (csproj description names Godot's `AudioStreamGenerator`). The serializer is the caller's; standard .NET property serialization is assumed (see OQ-1). | `Pooshit.AudioSynth.csproj:20` |
| AS-1 | Classic tracker timing: **tick rate = `BPM × 2 / 5` Hz**, i.e. `samplesPerTick = sampleRate × 2.5 / BPM`; `samplesPerRow = speed × samplesPerTick`. Default tempo 125 BPM, default speed 6 ticks/row (the ProTracker/S3M defaults). | Tracker domain (standardized) |

## 4. Architectural Overview

Two units, one new namespace + one new importer, sitting entirely *beside* the existing pipeline. Nothing downstream of `Timeline` changes.

```
   AUTHORING / SERIALIZATION (caller's world — engine + JSON, NOT ours)
   ┌───────────────────────────────────────────────┐
   │  Song ── Pattern[] ── Cell[]   Instrument[]    │   POD: value types,
   │   │        (rows×chan grid)     (slot→bank/prog)│   arrays, enums.
   │  Order[] (pattern indices)                     │   Engine serializes
   └───────────────────────────────────────────────┘   this to JSON itself.
                     │  (in-memory Song)
                     ▼
   ╔═══════════════════════════════════════════════╗   NEW: the adapter.
   ║  TrackerTimelineImporter.Import(song, srate)   ║   Pure, static, no I/O.
   ║   • walk Order → Pattern → rows → channels     ║   Emits NeutralEvents at
   ║   • accumulate sample offset (speed+tempo)     ║   accumulated offsets.
   ║   • per cell → NeutralEvent(s)                 ║
   ╚═══════════════════════════════════════════════╝
                     │  Timeline (MIDI-neutral)
                     ▼
   ┌───────── EXISTING, UNCHANGED ──────────────────┐
   │  Timeline.Compile() → CompiledSchedule         │
   │  RealtimeSequencer(schedule, synth, soundBank) │
   │  → IAudioSource → live audio buffer            │
   └────────────────────────────────────────────────┘
```

The `Timeline` is already the MIDI-neutral seam. A tracker `Song` is "just another importer's input", exactly as a MIDI file is. The importer's whole job is **row-grid → offset-addressed neutral events**. Everything after `Timeline` is reused verbatim.

## 5. Components & Responsibilities

Each type is single-responsibility. "Owns" = is the source of truth for; "does NOT own" = deliberately excluded.

| Component | Kind | Owns | Does NOT own |
|-----------|------|------|--------------|
| `Cell` | `struct` (POD) | The five sub-columns of one grid position: Note, Instrument, Volume, Effect, EffectParam. | Any interpretation, any timing, any synth binding. It is inert data. |
| `Pattern` | `class` (POD) | One pattern's row height and its flat row-major `Cell[]` grid. | The channel count (that is the Song's — see §7 DRY note). Playback order. |
| `Instrument` | `struct` (POD) | The mapping of one instrument slot to a symbolic SoundBank `(Bank, Program)` + an editor label. | The actual `IPatch` / audio data (the SoundBank owns that). |
| `Song` | `class` (POD) | The whole composition: defaults (BPM, speed, rows), channel count, instrument table, pattern bank, order list, title. | Any behavior. Any synth/engine reference. |
| `TrackerEffectCommand` | `enum : byte` | The library-known effect command vocabulary (`None`, `SetSpeed`, `SetTempo`; future commands append). | The interpretation (that is the importer's). Being closed — unknown byte values are legal and pass through. |
| `TrackerNotes` | `static` constants holder | The Note-column sentinel constants (`Empty`, `Off`, `Cut`) and the key⇄note-value mapping helpers. Pure functions over `byte`, no state. | Anything on the `Cell` itself (keeps `Cell` behavior-free for serialization). |
| `TrackerTimelineImporter` | `static` class | Lowering a `Song` → `Timeline`: order walk, offset accumulation, per-cell `NeutralEvent` emission, per-channel importer state (current instrument, active key), note-linking. | The POD types' shape. Anything past `Timeline`. Any `SoundBank`/`IPatch` handle. |

## 6. Interactions & Data Flow

All flow is **synchronous, single-pass, allocation-light**, mirroring `MidiTimelineImporter`. There is no async, no I/O, no message bus — this is an in-memory lowering pass.

**Import sequence (conceptual):**

1. **Validate & seed.** Assert `Song.ChannelCount ∈ [1,16]`. For each channel `c`, seed at offset 0: `SetGain(c, 1.0)` and `SetPan(c, 0.0)` — the channel init, mirroring the MIDI importer's offset-0 reset. **No** `SetPatch` is seeded (patch arrives with the first instrument-bearing note — CN-3).
2. **Initialize the running clock.** `currentTempo = Song.DefaultBpm`, `currentSpeed = Song.DefaultSpeed`, `cursorSamples = 0.0` (double accumulator — see §9 for the anti-drift rationale).
3. **Walk the order list.** For each `orderIndex` in `Song.Order`, resolve `pattern = Song.Patterns[orderIndex]`.
4. **Walk rows.** For each row `r` in `[0, effectiveRows)`, where `effectiveRows = pattern.Rows ?? Song.DefaultRows` (a `null` per-pattern row count falls back to the song default):
   a. **Timing pre-pass:** scan the row's channels (ascending) for `SetSpeed` / `SetTempo` effects and apply them to `currentSpeed` / `currentTempo` *before* the row's duration is computed (classic tracker semantics — the directive governs its own row).
   b. **Emit pass:** `long offset = round(cursorSamples)`. For each channel `c` in ascending order, interpret `pattern.Cells[r*ChannelCount + c]` and emit its `NeutralEvent`s at `offset` (see §8). Ascending channel order + `Timeline`'s insertion-order tie-break guarantee deterministic same-offset dispatch.
   c. **Advance:** `cursorSamples += currentSpeed * sampleRate * 2.5 / currentTempo`.
5. **Return** the populated `Timeline`. The caller `Compile()`s it and constructs a `RealtimeSequencer` (optionally with a loop region).

**Per-channel importer state** (arrays sized `ChannelCount`, private to the pass): `currentInstrumentSlot[c]` (the instrument last selected on the channel, for patch reuse and to detect changes) and `activeKey[c]` (the MIDI key currently sounding, `-1 = none`, enforcing monophony). A note-off/retrigger consults `activeKey[c]` to release the prior note and `LinkNote`s the on/off pair (editor parity with the MIDI importer).

## 7. Data Model (Conceptual)

All types are POD: public members only, no methods that carry logic, no references to engine/synth types, a pure tree (no cycles). The **golden invariant**: `default(Cell)` (all-zero) is a *fully empty cell that triggers nothing*. Every optional sub-column therefore uses `0 = absent`, so a freshly-allocated `Cell[]` grid is already a valid empty pattern at zero cost — the property an editor and a JSON round-trip both rely on.

**`Cell`** (value type — the numerous grid leaf):

| Member | Type | Semantics |
|--------|------|-----------|
| `Note` | `byte` | `0` = empty (no note event). `1..120` = playable, MIDI key = `Note − 1` (range keys 0..119, ~10 octaves). `254` = note-off (release). `255` = note-cut (immediate silence). `121..253` reserved. Sentinels exposed as `TrackerNotes.Off`/`.Cut`/`.Empty`. |
| `Instrument` | `byte` | `0` = none (no instrument change — reuse channel's current). `1..N` = **1-based** instrument slot → `Song.Instruments[value − 1]`. 1-based matches the tracker convention (instrument 0 = "no instrument"). |
| `Volume` | `byte` | `0` = not set (note keeps default loudness). `1..64` = explicit channel volume level (→ gain `value/64`). Values `>64` clamped. True silence is expressed via note-cut, not volume 0 — so `0` is free to mean "absent". |
| `Effect` | `TrackerEffectCommand` (`: byte`) | `None(0)` = no effect. Otherwise a command; unknown/unnamed byte values are legal and pass through uninterpreted. |
| `EffectParam` | `byte` | Command-specific parameter (0..255). For `SetSpeed`: ticks/row. For `SetTempo`: BPM. |

**`Instrument`** (value type): `Bank` (`int`, SoundBank bank), `Program` (`int`, SoundBank program), `Name` (`string`, editor label — not read by the importer; see §10 for the YAGNI justification).

**`Pattern`** (reference type — owns arrays): `Rows` (**`int?`** — this pattern's height, or `null` to inherit `Song.DefaultRows`), `Cells` (`Cell[]`, flat **row-major**, length `= effectiveRows × Song.ChannelCount` where `effectiveRows = Rows ?? Song.DefaultRows`, index `= row × ChannelCount + channel`). The importer reads the effective count everywhere (row walk and grid-size validation). `Rows` is **nullable by deliberate design** — a non-nullable height always serializes a redundant number even when it equals the song default; `null` lets the engine's JSON serializer omit the field and expresses "not overridden" far more cleanly than a magic number that happens to match the default.

**`Song`** (reference type — the aggregate root): `Title` (`string`), `DefaultBpm` (`int`), `DefaultSpeed` (`int`), `DefaultRows` (`int`, the per-pattern row fallback when `Pattern.Rows` is `null`, and an editor's new-pattern default), `ChannelCount` (`int`), `Instruments` (`Instrument[]`), `Patterns` (`Pattern[]`), `Order` (`int[]` — indices into `Patterns`, in play order).

**Serialization-friendliness (the brief's explicit ask):** the grid is a **flat `Cell[]` + effective `Rows`** and a stride of `Song.ChannelCount`. This deliberately rejects `Cell[,]` (multidimensional arrays are *not* serializable by System.Text.Json and many engine serializers) and `Cell[][]` (jagged — the brief flags it as resisting serialization). A flat array of a 5-byte value struct is the friendliest possible shape: a JSON array of small objects, universally supported. There are **no `[,]`, no jagged arrays, no object references between siblings**.

**Nullable policy — sparse yes, dense no.** Optionality is expressed two different ways on purpose:
- The **dense per-cell sub-columns** (`Note`/`Instrument`/`Volume`/`Effect`) use a `0`-sentinel and are **never nullable**. Each cell recurs thousands of times across a pattern grid; a nullable-per-sub-column encoding would bloat every serialized cell and destroy the `default(Cell) == empty` invariant that makes a freshly-allocated grid valid at zero cost.
- The **sparse per-pattern `Pattern.Rows`** is **`int?`**. It occurs once per pattern (a handful per song), carries a genuine "not overridden — inherit the song default" meaning, and benefits from serializer field-omission. A `0`-sentinel here would be a magic number colliding with a legitimate (if unusual) zero-row pattern and would force writing a value even when none was intended.

This asymmetry is the point: nullable earns its keep for the rare field with a true "absent" meaning; the 0-sentinel wins for the compact, ubiquitous one.

**Conceptual relationships:** `Song 1──* Pattern`, `Song 1──* Instrument`, `Pattern 1──* Cell` (dense grid), `Song.Order *──1 Pattern` (by index, not reference — indices keep it a flat serializable tree, and let the same pattern appear many times in the order). Ownership is strictly top-down; there are no back-references.

## 8. Contracts & Interfaces (Abstract)

**`TrackerTimelineImporter.Import`** — the single public entry point.

| Aspect | Contract |
|--------|----------|
| Input | A `Song` (assumed structurally consistent: `Cells.Length == effectiveRows × ChannelCount` where `effectiveRows = Pattern.Rows ?? Song.DefaultRows`, `Order` entries in range) and a positive `sampleRate`. |
| Output | A fresh, populated (uncompiled) `Timeline`. |
| Preconditions | `ChannelCount ∈ [1,16]`; `DefaultBpm > 0`, `DefaultSpeed > 0`. Violations throw `ArgumentException`/`ArgumentOutOfRangeException` at entry (fail fast, clear message). |
| Postconditions | Every emitted entry's offset is `≥ 0` and non-decreasing across the walk. Each `NoteOn` is preceded on its channel by the release of any prior note (monophony). On/off pairs are `LinkNote`d. |
| Purity | No I/O, no static mutable state, no `SoundBank`/`IPatch` contact — fully symbolic and deterministic. Same `(Song, sampleRate)` ⇒ byte-identical `Timeline`. |
| Invariants | Emits only the existing `NeutralEvent` factories (§ below). Adds nothing to the neutral vocabulary. |

**Per-cell interpretation** (the lowering rules — prose, deterministic):

| Cell shape | Emitted at the row offset (in this order) |
|------------|--------------------------------------------|
| Instrument set (`Instrument ≠ 0`) | Record `currentInstrumentSlot[c]`. If the resolved `(Bank, Program)` differs from the channel's last-applied patch, emit `SetPatch(c, bank, program)`. (Patch takes effect for the following `NoteOn` — CN-3.) |
| Volume set (`Volume ≠ 0`) | `SetGain(c, Volume/64)`. Emitted *before* a same-cell `NoteOn` so the note starts at the intended level. |
| Playable note (`Note ∈ [1,120]`) | If `activeKey[c] ≠ -1`: `NoteOff(c, activeKey[c])` (+`LinkNote`). Then `NoteOn(c, Note−1, DefaultVelocity)`; set `activeKey[c] = Note−1`. Patch = channel's current instrument (seed `SetPatch` here if a note carries no instrument but the channel has one pending and unappplied). |
| Note-off (`Note = 254`) | If `activeKey[c] ≠ -1`: `NoteOff(c, activeKey[c])` (+`LinkNote`); `activeKey[c] = -1`. |
| Note-cut (`Note = 255`) | If `activeKey[c] ≠ -1`: `SilenceChannel(c)` (immediate declick, no envelope release — the tracker "cut" semantics); `activeKey[c] = -1`. |
| Effect `SetSpeed` / `SetTempo` | Consumed in the timing pre-pass (§6 step 4a); emits **no** timeline event. |
| Effect (other / unknown) | Ignored in v1 (pass-through). No event, no error. The data survives for a future interpreter. |
| Empty (`default`) | Nothing. |

`DefaultVelocity` is a named `const` (proposed `127`) — under monophonic channels, loudness is controlled by `SetGain` (the volume column), so velocity stays constant. This is the S3M/IT model (volume column, not per-note velocity). It is **not** a config knob (§3 configurability — no operator, no environment variance; stays `const`).

**Neutral vocabulary used** (all pre-existing, unchanged): `NoteOn`, `NoteOff`, `SetPatch`, `SetGain`, `SetPan`, `SilenceChannel`. The importer touches **six** of the twelve existing factories and adds none.

## 9. Cross-Cutting Concerns

- **Timing model & sample-offset math (the headline decision).** v1 uses the **classic two-parameter speed (ticks/row) + tempo (BPM)** model, not rows-per-beat + BPM. Rationale: (1) Toni is S3M/IT-native and expects Axx/Txx. (2) The custom-effect roadmap (arpeggio cycles per tick, retrigger every N ticks, per-tick slides) is **only expressible on a tick grid** — rows-per-beat has no sub-row quantum and would have to be torn out to add them. Choosing it now would *preclude* the roadmap the brief names. (3) The math is trivial and integer-friendly: `samplesPerRow = speed × sampleRate × 2.5 / tempo`. The per-tick engine is explicitly **not built now** — v1 emits at row boundaries — but the model does not stand in its way. This is a seam at zero present cost (KISS-compatible: v1 code computes one row length, no tick loop).
- **Offset accumulation & drift.** The cursor is a **`double` accumulator**, rounded to `long` only at each emit. This bounds absolute error to `< 1` sample at *every* row (the accumulator holds the exact fractional position; rounding is local, never fed back), so tempo is stable across thousands of rows — unlike summing per-row rounded integers, which drifts. Determinism holds: same operation order ⇒ same doubles ⇒ same rounded offsets.
- **Mid-song tempo/speed changes.** Handled by the same accumulator: the timing pre-pass (§6 4a) mutates `currentSpeed`/`currentTempo` before the row's advance is computed, so a `Txx`/`Axx` on row *k* changes the length of row *k* onward. Because offsets accumulate (not `row × fixed_len`), this is automatic — the two effects are the *only* v1 effect-column commands precisely because they cost nothing beyond the accumulator the importer already needs (no tick engine). They are timing directives, not DSP effects.
- **Error handling.** Fail-fast at `Import` entry on structural violations (channel count, non-positive tempo/speed) with clear `Argument*Exception` messages. Within the walk, tolerate authored imperfections that can't corrupt output: an out-of-range instrument slot ⇒ skip the patch change (no throw); an unknown effect ⇒ ignore. No defensive guards for impossible states (§0 YAGNI).
- **Concurrency / idempotency / consistency.** None introduced. `Import` is a pure function; the `Timeline`/`RealtimeSequencer` single-thread contract is unchanged and the importer runs before playback.
- **Security / auth / observability / caching.** Not applicable — this is an in-memory data transform in a synthesis library, no boundaries, no I/O, no logging surface added (consistent with the silent `MidiTimelineImporter`).

## 10. Quality Attributes & Trade-offs

- **Maintainability.** The importer is one static class paralleling `MidiTimelineImporter`; a maintainer who knows the MIDI path knows this one. The POD model has no behavior to maintain.
- **Performance.** Flat `Cell[]` of a 5-byte value struct is cache-friendly and alloc-free to traverse; the import is single-pass O(cells). Playback path is the already-tuned `RealtimeSequencer` — untouched.
- **Extensibility (earned, not speculative).** The effect seam is a `switch` over `TrackerEffectCommand` with an ignore-default; adding a command = append an enum member + a case. The tick-based clock leaves room for a per-tick pass. Both are **zero present cost** — no abstraction, no interface, no hook is built ahead of need (§4 less-is-better).
- **Serialization (the core user requirement).** §7 chooses the friendliest shape at every fork (flat vs jagged/multidim; 0-sentinels vs nullables; value structs vs graphs).

**Trade-offs made explicit** (per §4 — name the downside, weigh it):

| Decision | Alternative rejected | Downside of the choice | Why it still wins |
|----------|---------------------|------------------------|-------------------|
| Instrument **table** indexed by cell (CN-3) | Per-channel fixed patch | Importer carries per-channel "current instrument" state. | Per-channel-fixed *cannot represent* a channel that changes timbre across rows — the essence of tracker music. Every MOD/S3M/IT has a per-cell instrument column. Non-negotiable for faithfulness. |
| Volume → **`SetChannelGain`**, constant velocity | Fold volume into `NoteOn` velocity | A sustained-note volume change and a trigger volume use the same path; relies on channel monophony. | Under CN-2 (one voice/channel) channel gain *is* the note's volume — exact, not approximate — and it uniformly handles trigger-time and standalone volume changes. Matches the S3M/IT volume-column model (velocity is not a MOD/S3M concept). |
| **Flat `Cell[]` + effective Rows** | `Cell[,]` / `Cell[][]` | Caller indexes `row×stride+col` rather than `[r,c]`. | `[,]` is not JSON-serializable in STJ/most engine serializers; jagged is flagged as resisting. Flat is universally serializable. The stride math is trivial and lives in the importer. |
| **`Pattern.Rows` is `int?`** (null = inherit `Song.DefaultRows`) | Non-nullable `int` height | One nullable field in an otherwise sentinel-based model. | The sparse per-pattern field has a real "not overridden" meaning and lets the serializer omit it; a non-nullable height always writes a redundant number. Cell sub-columns stay non-nullable (dense grid — see §7 nullable policy). |
| `Song.ChannelCount` capped at **16**, 1:1 map | Fold >16 tracker channels onto 16 voices | Songs authored for >16 channels are rejected. | The synth is a fixed 16-channel engine (CN-1); folding is a synth-capacity concern and "no synth changes" is a hard constraint. 16 channels is ample for game BGM (MOD=4, S3M≈16). Revisit only if Toni needs it (OQ-2). |
| **`double` cursor**, round at emit | Sum per-row integer sample counts | Float reasoning in a bit-parity-conscious codebase. | Integer summing drifts (±0.5/row × thousands); the double accumulator is exact-position and deterministic. The MIDI path already reasons in float sample math, so this is house-consistent. |

## 11. Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|-----------|
| Serializer target mismatch (property vs field serialization; mutable-struct handling) | Model doesn't round-trip in the caller's engine | Default to public auto-properties (STJ/Newtonsoft/Pooshit.Json friendly). Surface serializer target as OQ-1 before John hard-codes field vs property. Conversion is mechanical if wrong. |
| A note plays on a channel that never received an instrument | Note sounds with the synth's default patch (possibly wrong timbre) | Documented behavior; well-formed songs always set an instrument on a channel's first note. No defensive machinery (YAGNI) — the fallback is the synth's existing never-null patch (CN-4). |
| `SetGain` is glided, not stepped (`ISynthesizer` doc) | A volume change ramps over a few ms rather than snapping | Acceptable and click-free by design; emitting `SetGain` before `NoteOn` at the same offset lets the ramp settle at attack. If a hard step is later required, that's a synth concern, not this format's. |
| Channel-count / grid-length inconsistency in authored data | Index out of range at import | Fail-fast validation at `Import` entry with a clear message; unit test covers the malformed-length case. |

## 12. Migration / Rollout Strategy

Not applicable — this is **purely additive**. New files only (`Formats/Tracker/*`, `Sequencing/TrackerTimelineImporter.cs`, tests). No existing type, signature, or behavior changes; nothing to migrate, no compatibility window (§0 YAGNI: no shims in an additive change). The MIDI path and the tracker path coexist as two importers over one `Timeline`.

## 13. Open Questions

- **OQ-1 (serializer target — minor, non-blocking).** Which serializer/engine consumes the POD? Default assumption: standard .NET property serialization (auto-properties). If the target is Unity `JsonUtility` (public-fields-only), the members switch from auto-properties to public fields — a mechanical, whole-model change. Proceeding with auto-properties; flag for Toni.
- **OQ-2 (channel ceiling — product).** Is 16 channels sufficient for the intended in-game BGM? Assumed yes (well above MOD/S3M norms). If Toni wants IT-scale (>16), that is a *synth-capacity* change, out of this scope.
- **OQ-3 (loop seam — minor).** Should `Import` also report the song's total length in samples (natural `loopEnd` for a seamless BGM loop)? v1 returns `Timeline` only (mirrors MIDI); the caller can loop the whole timeline but needs the end offset. Cheap to add later as a result overload if Toni wants turnkey looping. Not built now (YAGNI until the loop use case is concrete).
- **OQ-4 (default velocity value).** `127` proposed for `DefaultVelocity`. Confirm; it stays a `const` regardless.

## 14. Implementation Guidance for the Next Agent (John)

Build in this order; the whole thing is ONE PR on branch `feature/tracker-format` (design doc already committed there).

1. **POD model + enums** in `src/Pooshit.AudioSynth/Formats/Tracker/` — one type per file: `Cell.cs` (struct), `Instrument.cs` (struct), `Pattern.cs` (class), `Song.cs` (class), `TrackerEffectCommand.cs` (`enum : byte`), `TrackerNotes.cs` (static constants + `IsPlayable`/`KeyOf` helpers over `byte`). Public auto-properties, XML `<summary>` on every public member (csproj requires docs). Namespace `Pooshit.AudioSynth.Formats.Tracker`.
2. **`TrackerTimelineImporter`** in `src/Pooshit.AudioSynth/Sequencing/TrackerTimelineImporter.cs`, namespace `Pooshit.AudioSynth.Sequencing` — `static`, `Import(Song, int sampleRate) → Timeline`. Implement: entry validation, offset-0 channel seed (gain 1.0, pan 0), the order→rows→channels walk, the `double` accumulator with the timing pre-pass, and the per-cell interpretation table (§8). Per-channel state arrays for current instrument + active key; `LinkNote` on on/off pairs. Interpret `SetSpeed`/`SetTempo`; ignore other effects.
3. **Unit tests** in `test/Pooshit.AudioSynth.Tests/Tracker/`:
   - Model: `default(Cell)` is empty; flat index math; round-trip-shape sanity (no reference cycles).
   - Importer timing: a known `(speed, tempo, sampleRate)` produces the expected row offsets; a mid-song `SetTempo` shifts subsequent offsets; the accumulator does not drift over many rows.
   - Importer events: a note emits `SetPatch`(on instrument change)+`NoteOn`; a retrigger emits prior `NoteOff` then `NoteOn`; note-off → `NoteOff`; note-cut → `SilenceChannel`; volume → `SetGain(v/64)`; unknown effect → no event; `ChannelCount>16` throws.
   - Use the existing `CallLoggingSynthesizer`/`StubPatch` test doubles and the `Timeline`→`RealtimeSequencer` assertion style from `RealtimeSequencerTests`.
4. **End-to-end proof** mirroring `MidiSongRenderTests`: build a trivial in-code one-pattern `Song`, `Import` → `Compile` → `RealtimeSequencer` over a small `SoundBank` (the `SinglePatchBank` helper pattern), pump it dry, assert non-silent + bounded. This closes success-criterion (b).
5. **Commit the design doc** (`docs/architecture/tracker-format.md`) alongside the code in the same PR. Run the §16 self-audit (comment grep = 0; XML summaries 1–2 lines) before opening.

**Follow-up (name in PR body, do NOT build here):** the per-tick effect engine (arpeggio/slides/vibrato/retrigger/note-delay/tick-cut) and its effect-column commands; the optional loop-length result seam (OQ-3). File as a DiVoid task linked to this design.

## 15. Reader / Scope Inventories

- **New files (additive only):** `Formats/Tracker/{Cell,Instrument,Pattern,Song,TrackerEffectCommand,TrackerNotes}.cs`; `Sequencing/TrackerTimelineImporter.cs`; `test/.../Tracker/*Tests.cs`. No existing file is modified.
- **Existing types consumed (read-only, unchanged):** `Timeline` (`Add`, `LinkNote`), `NeutralEvent` (`NoteOn`, `NoteOff`, `SetPatch`, `SetGain`, `SetPan`, `SilenceChannel`), `Synthesizer` channel ceiling (invariant only). No string-literal / reflection references to rename.

## 16. Pre-Design Checklist (Design Contracts #1136 §5 — verbatim walk)

| # | Checklist item | Verdict | Note |
|---|----------------|---------|------|
| KISS/DRY/YAGNI | No new type mirroring an existing type's value-space (§5.4) | PASS | `TrackerEffectCommand` is a new vocabulary, not a mirror of any existing enum; `Cell`/`Pattern`/`Song` have no counterpart. |
| | No new abstraction with one impl and no concrete second | PASS | No interfaces introduced. Importer is a static class (like the MIDI one), not an `ITrackerImporter`. |
| | No element justified by "might need X later" without concrete X | PASS | The tick-clock and effect-`switch` are seams at zero present cost, justified by the *named* roadmap (editor + custom effects in the brief), and nothing is *built* ahead of need. |
| | No deprecation period / feature flag / compat shim | PASS | Purely additive; none present. |
| | For any "inline N sites" decision: `block_size × site_count` quoted | N/A | No inline-vs-extract decision arises; the importer is a single site. |
| Existing systems first | Audited whether an existing surface covers this | PASS | The `Timeline` seam is reused wholesale; only the *importer* (which genuinely doesn't exist for tracker input) and the *format* (genuinely new) are added. No parallel sequencing layer. |
| | New layer's concrete reason to exist named | PASS | `Song`/`Pattern`/`Cell` model a grid MIDI cannot express (§1); the importer is the required lowering, paralleling `MidiTimelineImporter`. |
| | New persisted data point → 4-week decision named | N/A | Nothing is *persisted by this library*; serialization is the caller's. The model fields are the minimum a tracker song needs (§7). |
| | "Existing reader projects it" fields → consumer chain recursed | N/A | No field is justified by an existing reader; this is greenfield data. `Instrument.Name`/`Song.Title` are justified by the named editor consumer (§10), not a transitive reader. |
| Configurability | Every knob has a named operator/env difference | PASS | No config knobs. `DefaultVelocity` stays a `const` (§8) — no operator, no env variance. |
| | Telemetry-then-tune knobs paired with a filed task | N/A | None. |
| | Magic numbers that needn't vary stay `const` | PASS | Tick constant `2.5`, `DefaultVelocity`, channel ceiling `16` are named `const`s, not config. |
| Less is better | Every element passed delete/merge/inline check | PASS | `Pattern.Channels` was **deleted** (DRY — `Song.ChannelCount` is the single stride source, §7). `TrackerNotes` kept off `Cell` to preserve serializability. No mergeable/inlinable residue. |
| | Trade-offs named when a complex design wins over a simpler one | PASS | §10 trade-off table (instrument table, volume→gain, flat array, channel cap, double cursor). |
| | No-consumer surface → radical-clean shape | PASS | Effect column stores raw bytes + ignores unknowns (no speculative per-command fields); no compromise "half-effect" structure. |
| | Reader-inventory covers AST **and** string-literal refs | N/A | Additive; no renames, no string-literal predicates in this codebase. §15 inventory provided. |
| | Carrier-swap tables enumerate every affected DTO | N/A | No DTO carrier swap. |
| Data deliverables | Schema casing verified / backfill idempotent / verify-before-destroy | N/A | No SQL, no schema, no migration — in-memory POD only. |
| Document discipline | Cites Code Contracts #114 + Design Contracts #1136 | PASS | Header + this section. |
| | Reader/scope inventories explicit | PASS | §15. |
| | Out-of-scope items listed explicitly | PASS | §2 Non-Scope. |
| | No multi-paragraph "rationale for keeping X" for obvious stays | PASS | Trade-offs are tabular; no filler defense. |
| | Superseded predecessor gets a banner / archive | N/A | No predecessor design; this is the first tracker-format doc. |

**Result: no FAIL rows.** The design ships the minimum the brief asks (format + playback), with the editor and per-tick effects as zero-cost seams, and no speculative complexity.
