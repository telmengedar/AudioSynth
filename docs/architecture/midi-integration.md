# Architectural Document: Legacy MIDI Library Integration + First Real-Song Render

**Author:** Sarah (software architect) · **Date:** 2026-07-27 · **Source task:** DiVoid #7095 · **Project:** #6128
**Map root:** #6708 · **Builds on:** SF2 loader (#6754, the defensive-parse precedent), Synthesizer (#6734), OfflineRenderer (#6722), `Formats/` (#6714).
**Load-bearing contracts:** Code Contracts #114 (§0 KISS/DRY/YAGNI, §1 one-type-per-file, §4 comments/bicameral), Design Contracts #1136 (§1–§4, §5 checklist). PR-shape #1165 (design + implementation in ONE PR).
**DiVoid copy:** documentation node linked to #7095 + #6128 (this repo file is the authoritative wording).

> This is **PR 10** on the roadmap. It folds the user's legacy MIDI library (`C:\dev\claude\Midi`,
> assembly `NightlyCode.Midi`) into `Pooshit.AudioSynth` as one library, refactored to this project's
> contracts, and adds the **sequencer→synth driver** that renders a real MIDI song to WAV. This is the
> audible base every later increment (multi-timbral, mixing, expression) is tested against: render a
> song, hear the gap, fix it, hear it improve. Increment 1 ships the base — a real multi-track song
> rendered on a **single instrument** (ProgramChange ignored) with correct **timing and polyphony**.

---

## 1. Problem Statement

The synth can render one note from one SF2 patch (`Synthesizer` + `OfflineRenderer`, proven by the first-audio milestone). It cannot yet play a **song**: there is no component that reads a `.mid` file, converts its tick-based events into a wall-clock timeline, and drives `ISynthesizer.NoteOn`/`NoteOff` at the right moments. The user already owns a small, clean MIDI library that does the parsing and the ticks→seconds conversion; the natural move is to fold it in (not fragment into a second package) and add the missing glue — the driver.

**Success criteria:**
1. A real multi-track `.mid` (e.g. `07dkc2bram.mid` or a Final Fantasy VIII track) loaded, parsed, and rendered through a real SF2 to a `.wav` whose notes and timing match the song (single-instrument in inc.1 — expected to "sound like all one instrument", the baseline we improve from).
2. A **deterministic** automated fixture: a small synthetic MIDI drives the sequencer and its NoteOn/NoteOff events are asserted to land at the correct **sample offsets**.
3. The ported parser survives truncated / malformed input with an explicit MIDI exception (not `NullReferenceException` / `EndOfStreamException` leakage), mirroring the SF2 loader's untrusted-input posture.
4. The whole MIDI core compiles clean on **both** target frameworks — `netstandard2.0` must stay green.

---

## 2. Scope & Non-Scope

**In scope (this PR / increment 1):**

- **Ported + cleaned MIDI core** folded into `src/Pooshit.AudioSynth/Formats/Midi/`:
  - The parser (`MThd`/`MTrk`, variable-length quantities, running status, channel/meta/sysex/syscommon/realtime messages).
  - The message model (`ChannelMessage`, `MetaMessage`, sysex/syscommon/realtime, `IMidiMessage`, the enums).
  - The header + track model.
  - The tick→seconds timed sequencing (`TimedMessageSequence` → time-ordered events).
- **The sequencer→synth driver** (new glue, in `Sequencing/`): walk the timed events, build a sample-offset schedule, drive `ISynthesizer.NoteOn`/`NoteOff` against the current single default patch, pumping audio between events via `OfflineRenderer`, plus a release tail.
- **A MIDI render CLI** (`<song.mid> <soundfont.sf2> <out.wav>`) rendering a real song to WAV.
- **Tests:** parser round-trip on a synthetic `.mid`; driver schedule offsets (deterministic, no audio); defensive parse of a truncated file; a graceful-skip real-song integration render.
- Refactor to contracts: one-type-per-file, XML docs on all public surface, defensive untrusted-input parsing, dead-code removal, drop of the unused note-grouping subtree, MIDI-specific exception type.
- README attribution note (Leslie Sanford's C# MIDI Toolkit, if confirmed — see §13).

**Explicitly out of scope (each a NAMED follow-up PR — see §12):**

- **PR 11 — Multi-timbral routing:** `ProgramChange` → per-channel SF2 preset/bank selection; channel 9 = GM drum kit. *(The single biggest "sounds like the real song" step after this PR.)*
- **PR 12 — Channel mixing:** CC7 volume / CC10 pan / CC11 expression, per-voice SF2 pan, voice-stealing, mix headroom / limiter.
- **PR 13 — Expression:** PitchWheel bend, modulation (CC1), sustain pedal (CC64).
- **PR 14 — Real-time playback:** NAudio sink driving a sequence live; SMF format-0/1/2 edge cases; live MIDI input.
- **Not ported at all (see §5.4):** the `Tracks/Groups/` note-grouping subtree (`GroupedTrack`, `TrackNote`, the second `TrackMessage`) — it has no consumer in the offline-driver path; porting it now is speculative (YAGNI). It comes back with PR 14 only if a real-time player actually needs per-note grouping.
- **Not ported:** `Midi.Windows` (winmm P/Invoke device I/O — platform-specific, and the core stays sink-abstracted per the project's NAudio-is-optional rule).

---

## 3. Assumptions & Constraints

| # | Assumption / Constraint | Confidence | Impact if wrong |
|---|---|---|---|
| A1 | The legacy core uses only `BinaryReader`, `System.Linq`, `Encoding.ASCII`, `Math`, generics — all `netstandard2.0`-safe. Verified by reading every core file. | High | If a 3.5-only API surfaces, port it to a netstandard2.0 equivalent. |
| A2 | Real target songs are **PPQN** division (division bit 15 = 0), not SMPTE. The FF8 / DKC test set is standard SMF PPQN. | High | SMPTE timing path exists but is suspect (see R3); PPQN is the tested path. |
| A3 | `MidiChannel` in a NoteOn is only used by the synth to match a later NoteOff; the single default patch answers every channel. | High (matches `Synthesizer.NoteOn`) | None for inc.1. |
| A4 | Test songs and the Florestan SF2 live in the dev tree under `Source/AudioSynthesis.Tests/` (a sibling of the git repo), found by walking up from the test assembly — the same pattern the SF2 tests already use. They are **not** committed into the repo. | High (glob + existing `FindFlorestanPath` confirm) | Integration render `Assert.Ignore`s when absent; deterministic fixture is self-contained. |
| A5 | `net8.0` `MaxVoices` default is 32; dense songs across 16 channels can exceed that. | High | Inc.1 accepts voice starvation (silent dropped notes); voice-stealing is PR 12. |
| A6 | The MIDI message model derives from Leslie Sanford's C# MIDI Toolkit. Strong textual evidence ("ChannelEventArgs" doc phrasing) but **not** yet confirmed. | Medium | Attribution note added if confirmed; open question O1. |

**Hard constraints:** multi-target `netstandard2.0;net8.0` (MIDI code must compile on both); `Nullable enable`; explicit types (no `var`); one type per file; render hot path stays allocation-free (the driver's per-gap pump reuses the existing block-buffer discipline — see §9).

---

## 4. Architectural Overview

Three layers, one library, clean dependency direction (parser knows nothing of the synth):

```
   .mid bytes                                        .sf2 bytes
      │                                                  │
      ▼                                                  ▼
┌──────────────────────────┐                  ┌────────────────────┐
│ Formats/Midi  (parser)   │                  │ Formats/Sf2 loader │
│  MidiFile.Read(stream)   │                  │  → IReadOnlyList    │
│   → MidiFile             │                  │      <IPatch>       │
│      Header + Track[]    │                  └─────────┬──────────┘
│  TimedMessageSequence    │                            │ patches[0]
│   → TimedMidiMessage[]   │                            ▼
│      (message + seconds) │                  ┌────────────────────┐
└───────────┬──────────────┘                  │ Synthesis          │
            │ timed events                     │  Synthesizer       │
            ▼                                   │  (ISynthesizer)   │
┌──────────────────────────────────────────┐  │  single default   │
│ Sequencing/  MidiSequencer  (new glue)    │─▶│  patch, NoteOn/   │
│  1. build schedule: seconds → sample      │  │  NoteOff          │
│     offsets, NoteOn(vel0)→NoteOff         │  └─────────┬──────────┘
│  2. drive: for each event, render the gap │            │ pull (Read)
│     via OfflineRenderer, apply event,     │            ▼
│     then a release tail                   │  ┌────────────────────┐
└───────────────────────────────────────────┘  │ Audio/OfflineRender│──▶ IAudioSink
                                                 │  + WavFileSink    │   (.wav)
                                                 └────────────────────┘
```

**Why the driver is a separate `Sequencing/` namespace, not inside `Formats/Midi/`:** the parser + timed-sequence layer has **zero** dependency on `Synthesis`/`Audio` — it is a reusable, synth-free MIDI reader (a consumer could parse MIDI without ever touching the synth). The driver is the *only* piece that couples MIDI to the synth. Folding it into `Formats/Midi` would drag a `Synthesis` dependency into the parser namespace; folding it into `Synthesis` would drag a MIDI-format dependency into the engine. A thin `Sequencing/` namespace holding the single driver class keeps both sides clean. This is a **concrete dependency-boundary** justification (Design Contracts §2 "genuinely different… access/dependency"), not a "cleanliness" feeling — see §10 for the trade-off.

---

## 5. Components & Responsibilities

### 5.1 `Formats/Midi/` — the parser + model (namespace decision in §8)

| Component (proposed name) | Owns | Does NOT own |
|---|---|---|
| **`MidiFile`** (was `MidiSequence`) | The parsed model: `Header` + `Track[]`; the static `Read(Stream)` entry point and the defensive parse. | Timing (seconds), synth interaction. |
| **`MidiHeader`** | Format, track count, division, PPQN/SMPTE classification; header parse. | Track bodies. |
| **`Track`** | The raw `TrackMessage[]` (message + absolute tick) for one MTrk chunk. | Cross-track ordering, timing. |
| **`TrackMessage`** | Pairing of one `IMidiMessage` with its absolute tick position. | — |
| **`IMidiMessage` + `ChannelMessage` / `MetaMessage` / `SysExMessage` / `SysCommonMessage` / `SysRealtimeMessage` / `ShortMessage`** | The event value model + status/data decoding. | Parsing, timing. |
| **Enums** (`MessageType`, `ChannelCommandType`, `ControllerType`, `MetaMessageType`, `SysExType`, `SysCommonMessageType`, `SysRealtimeType`, `SequenceType`, `SmpteFrameRate`) | Closed vocabularies of the MIDI spec. | — |
| **`TimedMessageSequence`** | Flatten all tracks, order by tick, convert ticks→**seconds** using division + tempo/time-signature meta → `TimedMidiMessage[]`. | Synth interaction, sample offsets. |
| **`TimedMidiMessage`** (was `TimedMessage`) | Pairing of one `IMidiMessage` with its wall-clock time in seconds. | — |
| **`InvalidMidiFileException`** (new) | The single thrown type for structurally invalid / truncated MIDI, mirroring `InvalidSoundFontException`. | — |
| **`TrackReader`** (internal) | Position-tracking byte/short/int/var-length reads over the chunk. | — |

### 5.2 `Sequencing/` — `MidiSequencer` (the new driver, single responsibility split into two phases)

- **Phase 1 — build schedule (pure, testable, no audio):** from a `TimedMessageSequence` + sample rate, produce an ordered list of `ScheduledMidiEvent { long SampleOffset, IMidiMessage Message }`. `SampleOffset = round(Time × sampleRate)`. This phase is a **pure function** — the deterministic offset fixture (success criterion 2) asserts directly on this list, needing no synth and no audio.
- **Phase 2 — drive (integration):** walk the schedule; for each event render `(eventOffset − cursor)` frames of the synth into the sink via `OfflineRenderer`, then apply the event to `ISynthesizer`; after the last event render a fixed **release tail** so envelope releases finish.

`MidiSequencer` owns: event→synth-call translation (including the **NoteOn-velocity-0 ⇒ NoteOff** rule), the schedule, the gap-pump orchestration, and the tail. It does **not** own: DSP, mixing, voice allocation (all inside the synth behind `ISynthesizer`), file parsing (owned by `Formats/Midi`), or WAV encoding (owned by `WavFileSink`). It depends only on the `ISynthesizer` interface, `IAudioSink`, and `OfflineRenderer` — never on a concrete voice/patch type.

### 5.3 `tools/Pooshit.AudioSynth.MidiRender/` — the CLI (new console project)

A minimal `Exe` sibling of the existing `RenderDemo`: `MidiRender <song.mid> <soundfont.sf2> <out.wav>`. Loads the SF2 (`Sf2SoundBankLoader`, patch 0), parses the MIDI, runs `MidiSequencer` into a `WavFileSink`. Reuses the `RenderDemo` dev-tree-walk to default the SF2/song when args are omitted (nice-to-have; the three-arg form is the contract).

### 5.4 Dropped / not ported

- **`Tracks/MidiTrack.cs`** — empty dead class. **Delete** (do not port).
- **`Tracks/Groups/` subtree** (`GroupedTrack`, `TrackNote`, `Groups/TrackMessage`) — a per-note message-grouping abstraction with **no consumer** in the offline-driver path. The driver walks `TimedMidiMessage[]` directly. **Do not port** (Design Contracts §2 form-2 / §4 YAGNI: don't carry code whose only future consumer is a speculative real-time player). This omission also **resolves the duplicate-`TrackMessage`-name smell by deletion** rather than by rename — the surviving `TrackMessage` is the tick-paired one. If PR 14's real-time player needs note-grouping, port it *then*, with the real consumer in hand.
- **`Midi.Windows`** entire project — winmm P/Invoke, platform-specific.

---

## 6. Interactions & Data Flow

**Load + render a song (the increment-1 flow):**

1. CLI opens the `.sf2`, `Sf2SoundBankLoader.Load` → `IReadOnlyList<IPatch>`; take `patches[0]` as the single default patch.
2. Construct `Synthesizer(new SynthesizerOptions(rate, channels, …), patches[0])`.
3. CLI opens the `.mid`, `MidiFile.Read(stream)` → `MidiFile`; `new TimedMessageSequence(midiFile)` → `TimedMidiMessage[]` (seconds).
4. `MidiSequencer` **Phase 1**: schedule = ordered `ScheduledMidiEvent[]` (sample offsets), NoteOn-vel-0 folded to NoteOff.
5. `MidiSequencer` **Phase 2**, with `cursor = 0`:
   - For each `ScheduledMidiEvent e`: `gap = e.SampleOffset − cursor`; if `gap > 0` → `OfflineRenderer.Render(synth, sink, gap)`; `cursor += gap`. Then translate `e.Message`:
     - `ChannelMessage` NoteOn, `Data2 > 0` → `synth.NoteOn(MidiChannel, Data1, Data2)`.
     - `ChannelMessage` NoteOn, `Data2 == 0`, or NoteOff → `synth.NoteOff(MidiChannel, Data1)`.
     - Any other message (ProgramChange, Controller, PitchWheel, ChannelPressure, PolyPressure, meta, sysex, syscommon, realtime) → **ignored** in inc.1.
   - After the last event: `OfflineRenderer.Render(synth, sink, tailFrames)` where `tailFrames = round(ReleaseTailSeconds × rate)`.
6. `WavFileSink` disposal finalizes the WAV.

**Timing model note:** tempo (`MetaMessageType.Tempo`) and time-signature meta are consumed **inside** `TimedMessageSequence` during the ticks→seconds conversion; by the time the driver sees events, all timing is already absolute seconds. The driver therefore never interprets tempo — it only maps seconds→samples. This keeps tempo logic in exactly one place.

**Same-timestamp events (chords):** consecutive schedule entries with equal `SampleOffset` produce `gap == 0`, render nothing, and apply back-to-back — chords land as simultaneous NoteOns. Correct by construction.

---

## 7. Data Model (Conceptual)

```
MidiFile
 ├─ Header : MidiHeader { Format, TrackCount, Division, SequenceType }
 └─ Tracks : Track[]
                └─ Messages : TrackMessage[] { Message : IMidiMessage, Position : int (abs ticks) }

TimedMessageSequence (derived from MidiFile)
 ├─ Name : string        (first TrackName/Marker meta)
 └─ Messages : TimedMidiMessage[] { Message : IMidiMessage, Time : float (seconds) }

ScheduledMidiEvent (derived from TimedMessageSequence + sampleRate)   ← driver-internal
   { SampleOffset : long, Message : IMidiMessage }

IMidiMessage (value hierarchy)
 ├─ ShortMessage (abstract) → ChannelMessage { Command, MidiChannel, Data1, Data2 }
 ├─ MetaMessage { MetaType, Bytes[] }
 ├─ SysExMessage / SysCommonMessage / SysRealtimeMessage
```

Ownership: `Formats/Midi` owns `MidiFile`, `MidiHeader`, `Track`, `TrackMessage`, `TimedMessageSequence`, `TimedMidiMessage`, and the message hierarchy. `Sequencing` owns `ScheduledMidiEvent` and `MidiSequencer`. No entity is persisted; all are in-memory, single-render-lifetime.

---

## 8. Contracts & Interfaces (Abstract)

**Namespace decision (recommendation, deviates from the brief's literal wording — flagged O2).** The brief wrote namespace `Pooshit.AudioSynth.Midi`. I recommend instead:

- Parser + model + timed sequence → **`Pooshit.AudioSynth.Formats.Midi`** (folder `Formats/Midi/`), matching the `Formats.Sf2` sibling the brief itself cites as the precedent, and honoring this project's folder=namespace convention. A one-off `Pooshit.AudioSynth.Midi` that doesn't match its folder would be an inconsistency against the very sibling it sits next to.
- Driver → **`Pooshit.AudioSynth.Sequencing`** (folder `Sequencing/`), per the dependency-boundary reasoning in §4.

This is a low-stakes call; if Toni prefers the literal `Pooshit.AudioSynth.Midi`, that is a trivial namespace edit. Recommending the consistent form.

| Contract | Input | Output | Semantics / Invariants |
|---|---|---|---|
| `MidiFile.Read(Stream)` | a readable stream | `MidiFile` | Reads `MThd` then `TrackCount` `MTrk` chunks. **Throws `InvalidMidiFileException`** on bad magic, truncation, or a declared size exceeding the stream — never leaks `EndOfStreamException`/`NullReferenceException`. Non-seekable streams handled (buffer to memory) as the SF2 loader does. Unknown meta/sysex payloads are read-and-retained (not silently dropped in a way that desyncs the byte cursor). |
| `new TimedMessageSequence(MidiFile)` | parsed file | timed sequence | Deterministic. Converts absolute ticks → seconds using PPQN division and running tempo meta (default 120 BPM until the first tempo event). Output is time-ordered non-decreasing. |
| `MidiSequencer.BuildSchedule(TimedMessageSequence, sampleRate)` | timed events + rate | `ScheduledMidiEvent[]` | Pure. `SampleOffset = round(Time × rate)`, non-decreasing. NoteOn-vel-0 folded to a NoteOff-equivalent scheduling. **This is the seam the deterministic offset test asserts on.** |
| `MidiSequencer.Render(schedule, ISynthesizer, IAudioSink, tailFrames)` (or a combined `Render(TimedMessageSequence, ISynthesizer, IAudioSink)`) | schedule + engine + sink | frames written | Applies each event at its offset, pumping gaps via `OfflineRenderer`; requires `synth.Format == sink.Format` (delegated to `OfflineRenderer`'s existing check). Ignores non-note messages in inc.1. |

`ReleaseTailSeconds` is a **named `const`** on the driver (e.g. 3.0s), **not** a config knob — no operator tunes it and it does not vary by environment (Design Contracts §3). If a future increment needs "render until silent", that arrives with an active-voice/silence query on `ISynthesizer`, not a speculative knob now.

`ISynthesizer` is used **as-is** — no change to the seam. The driver depends only on the existing `NoteOn(int,int,int)` / `NoteOff(int,int)` / `Read` surface.

---

## 9. Cross-Cutting Concerns

- **Untrusted-input defense:** the parser is the trust boundary. Every count/length derived from file bytes is validated before use; structural violations throw `InvalidMidiFileException` (mirror `Sf2SoundBankLoader`: catch `EndOfStreamException` at the boundary and rethrow as the typed exception; validate declared sizes against remaining stream length). The current legacy code's generic `throw new Exception("Invalid header")` and silent `return null` on unknown status are replaced by explicit, typed handling.
- **Allocation discipline:** the render hot path stays allocation-free per block. `OfflineRenderer.Render` allocates one block buffer **per call**; the driver calls it once per inter-event gap. For a ~3-minute song (~thousands of events) this is a bounded number of short-lived 512-frame buffers — trivially collected, and this is an **offline** path, not the real-time hot path. Accepted for inc.1 (KISS: reuse the proven block-pump). If profiling ever shows it matters, internalizing a single reused buffer in the driver is a local change — deferred as YAGNI now.
- **Concurrency:** none. A single render is single-threaded, start to finish.
- **Error handling:** parser → typed `InvalidMidiFileException`; driver → argument/format guards delegated to `OfflineRenderer`. The CLI prints a usage/error line and returns non-zero, matching `RenderDemo`.
- **Observability:** the CLI prints a one-line summary (song, event count, frames, duration, output path), matching `RenderDemo`'s style. No logging framework in this library.
- **Idempotency / consistency:** rendering is a pure function of (`.mid`, `.sf2`, options) → deterministic PCM. Same inputs, same bytes out.

---

## 10. Quality Attributes & Trade-offs

| Attribute | How the design addresses it |
|---|---|
| **Maintainability** | One library (no fragmentation, per the user's stated preference). Parser stays synth-free and independently testable. One type per file, XML-documented. Dead code and the unused grouping subtree removed rather than carried. |
| **Performance** | Timing computed once (ticks→seconds in the sequence, seconds→samples in the schedule). Exact sample-offset placement (no block quantization ⇒ no timing drift). Hot path per-block allocation-free. |
| **Reusability** | `Formats/Midi` is a standalone MIDI reader; `MidiSequencer` depends only on `ISynthesizer`/`IAudioSink` seams, so it works with any future engine or sink (real-time NAudio in PR 14). |
| **Correctness/timing** | Events placed at `round(seconds×rate)`; chords via zero-gap application; NoteOn-vel-0 folded to NoteOff (a MIDI convention the naïve driver would otherwise get wrong — notes would never release). |

**Trade-offs made explicit:**

1. **Separate `Sequencing/` namespace vs. folding the driver into `Formats/Midi`.** Downside of the split: one extra folder/namespace for (initially) a single class. Upside: the parser keeps a zero-synth dependency footprint and stays reusable; the engine stays MIDI-agnostic. The alternative (driver in `Formats/Midi`) makes the parser namespace depend on `Synthesis` — a concrete, present coupling, not hypothetical. The split wins because the coupling it avoids is real today, not speculative. **Decision: split.**
2. **Reuse `OfflineRenderer` per gap (per-call buffer alloc) vs. a driver-owned reused buffer.** Downside: bounded short-lived garbage on an offline path. Upside: reuses the proven, tested block-pump with zero new DSP surface. The saved allocations are worthless on an offline render. **Decision: reuse `OfflineRenderer`.** (Revisit only under a profiler, PR 14+.)
3. **Fixed release tail (const seconds) vs. render-until-silent.** Downside: a very long final release could be clipped, or a little silence appended. Upside: no new `ISynthesizer` surface, no config knob. **Decision: fixed const tail** for inc.1; render-until-silent arrives with a real silence-query when a consumer needs it.
4. **Single instrument (ProgramChange ignored) — the accepted milestone.** Downside: drums (channel 9) and every non-piano part play as the one default patch; it "sounds like one instrument". This is the *point* of inc.1 — prove timing + polyphony on a real song first, then make it multi-timbral (PR 11). **Decision: accept, name PR 11 as the immediate next step.**

**Alternatives rejected:** (a) a separate `Pooshit.AudioSynth.Midi` *project* — rejected per the user's one-library preference; no dependency or lifecycle reason to fragment (Design Contracts §2). (b) Porting the `Groups/` note-grouping subtree "because it's there" — rejected as YAGNI (§5.4). (c) Quantizing events to block boundaries for a simpler pump loop — rejected: audible timing drift.

---

## 11. Risks & Mitigations

| # | Risk | Mitigation |
|---|---|---|
| R1 | Running-status / variable-length parsing subtly wrong after refactor ⇒ desynced stream, garbage events. | Parser round-trip test against a hand-built synthetic `.mid` with known events; the legacy logic is preserved (refactored, not rewritten) to keep proven behavior. |
| R2 | NoteOn-velocity-0 not treated as NoteOff ⇒ notes never release, voices leak, song turns to a held drone. | Explicit fold in `BuildSchedule`; a unit test asserts a vel-0 NoteOn yields a NoteOff-equivalent schedule entry. |
| R3 | **SMPTE timing path is suspect** — legacy `timepertick = 1000000 / …` yields microseconds while the PPQN path yields seconds (unit mismatch). | Inc.1 targets PPQN (A2). The port preserves the SMPTE branch but the design flags it as **known-suspect**; a follow-up (PR 14 edge cases) fixes/tests SMPTE. Do not claim SMPTE correctness. Open question O3. |
| R4 | Dense songs exceed `MaxVoices` (32) ⇒ dropped notes (silent). | Accepted for inc.1 (A5); voice-stealing is PR 12. Optionally raise `MaxVoices` for the CLI render as a stopgap (a construction arg, not a code change). |
| R5 | `netstandard2.0` build breaks on a ported construct. | CI/build gate must compile **both** TFMs; the deterministic test asserts the parser works. A1 says the surface is safe. |
| R6 | Attribution/provenance (Sanford toolkit) unconfirmed ⇒ license note wrong or missing. | O1: confirm before merge; add the README note if derived, alongside the existing CSharpSynthProject attribution. |

---

## 12. Phased Roadmap (the shape before we commit to order)

Increment 1 ships **only the base** (no speculative code for later phases — YAGNI). The later phases are **named future PRs**, each tested by rendering a real song and listening for the specific improvement:

| PR | Name | What it adds | The audible win |
|---|---|---|---|
| **10 (this)** | **Integration + basic render** | MIDI core folded in; sequencer→synth driver; render CLI; single default patch. | The song's **notes and timing** play back (on one instrument). |
| **11** | **Multi-timbral routing** | Per-channel program state; `ProgramChange` → SF2 preset/bank selection; channel 9 → GM drum kit. Needs the engine to hold a patch **per channel** (a real `ISynthesizer` change) and the SF2 loader to expose bank/preset lookup. | Instruments become **distinct per part** — the biggest "now it sounds like the song" jump. |
| **12** | **Channel mixing** | CC7 volume, CC10 pan, CC11 expression; per-voice SF2 pan; voice-stealing; mix headroom / soft limiter. | **Balance and stereo image**; no more dropped notes on dense passages. |
| **13** | **Expression** | PitchWheel bend, modulation (CC1), sustain pedal (CC64). | **Bends, vibrato, sustained pedal** — musical nuance. |
| **14** | **Real-time playback + edge cases** | NAudio sink driving a sequence live; SMF format-0/1/2 handling; SMPTE timing fix (R3); optional port of the note-grouping subtree if the live player needs it; live MIDI input. | **Play a song live**, not just to a file. |

PR 11 is the recommended immediate next step (largest perceptual gain). PR 11 and PR 12 both require touching the `ISynthesizer` seam (per-channel patch, per-channel gain/pan) — that seam evolution is the main architectural work ahead and should get its own short design when we reach it.

---

## 13. Open Questions

- **O1 — Attribution/provenance.** The message model's XML docs reference "the ChannelEventArgs class" — the exact type name from **Leslie Sanford's C# MIDI Toolkit** (public-domain / MIT). Strong evidence of derivation. Confirm before merge; if derived, add a README attribution note beside the CSharpSynthProject one. *(Recommendation: treat as derived and attribute — the cost of an unnecessary credit is zero; the cost of a missing one is a license problem.)*
- **O2 — Namespace.** Recommending `Pooshit.AudioSynth.Formats.Midi` (parser) + `Pooshit.AudioSynth.Sequencing` (driver) over the brief's literal `Pooshit.AudioSynth.Midi`, for folder=namespace consistency with the `Formats.Sf2` sibling (§8). Confirm or override — trivial either way.
- **O3 — SMPTE timing.** The legacy SMPTE branch has a seconds/microseconds unit mismatch (R3). Inc.1 preserves it untouched and targets PPQN. Confirm SMPTE correctness is out of scope for PR 10 (recommended) and lands in PR 14.
- **O4 — CLI project vs. extend `RenderDemo`.** Recommending a **new** `tools/Pooshit.AudioSynth.MidiRender` console (distinct CLI contract, keeps the note-demo focused). Confirm, or fold a MIDI mode into `RenderDemo` (not recommended — muddies its 8-positional-arg note contract).

---

## 14. Implementation Guidance for the Next Agent (john-backend-dev)

Ordered milestones. **No code appears here by design** — this is the architectural work breakdown. Build and `dotnet test` (both TFMs) green at each milestone boundary.

1. **Scaffold + port the message model.** Create `Formats/Midi/`. Port the message hierarchy (`IMidiMessage`, `ShortMessage`, `ChannelMessage`, `MetaMessage`, `SysExMessage`, `SysCommonMessage`, `SysRealtimeMessage`) and all enums to namespace `Pooshit.AudioSynth.Formats.Midi` (per O2). One type per file (already true in the legacy layout — verify). Replace legacy XML-doc phrasing that references "ChannelEventArgs"/"the Sanford" internals with contract-accurate summaries; keep summaries on **all** public members (§ Code Contracts XML-doc gate over every accessibility). Strip any body-comment cruft (bicameral rule: 0 explanatory body comments unless a non-obvious *why*).
2. **Port the header + track model + reader.** `MidiHeader`, `Track`, `TrackMessage` (the tick-paired one only), internal `TrackReader`. **Delete** the empty `MidiTrack`. Do **not** create `Tracks/Groups/`.
3. **Port the parser with the defensive boundary.** `MidiFile` (renamed from `MidiSequence`) with `Read(Stream)`. Introduce `InvalidMidiFileException` (mirror `InvalidSoundFontException`). Replace generic `throw new Exception(...)` and silent `return null` with typed, explicit handling; validate declared lengths against remaining stream; handle non-seekable streams by buffering (as the SF2 loader does). Preserve the running-status / variable-length-quantity logic (refactor, don't rewrite).
4. **Port the timed sequence.** `TimedMessageSequence` + `TimedMidiMessage` (renamed from `TimedMessage`). Keep the PPQN tempo/time-signature conversion. Leave the SMPTE branch functionally as-is but do not assert its correctness (R3/O3); a short XML/`[Description]` note that SMPTE is unverified is acceptable *only* as a real "why" note, not narration.
5. **Build the driver — Phase 1 (pure schedule).** `Sequencing/MidiSequencer` with `BuildSchedule(TimedMessageSequence, sampleRate)` → `ScheduledMidiEvent[]`. Fold NoteOn-vel-0 → NoteOff. This is pure and independently testable.
6. **Build the driver — Phase 2 (drive).** The gap-pump loop over the schedule using `OfflineRenderer` + `ISynthesizer` + `IAudioSink`, plus the const release tail. Non-note messages ignored.
7. **CLI.** New `tools/Pooshit.AudioSynth.MidiRender` (`<song.mid> <soundfont.sf2> <out.wav>`), reusing the SF2 load + dev-tree-walk patterns from `RenderDemo`.
8. **Tests.**
   - *Parser round-trip:* hand-build a small synthetic `.mid` (MThd + one MTrk with a couple NoteOn/NoteOff + tempo + EndOfTrack) in-memory; assert parsed events/positions.
   - *Defensive parse:* truncated/garbage bytes ⇒ `InvalidMidiFileException` (not a raw stream/null exception).
   - *Deterministic schedule offsets (success criterion 2):* build a synthetic MIDI with known tempo + PPQN; assert `BuildSchedule` places NoteOn/NoteOff at the expected sample offsets and count. No audio needed.
   - *Vel-0 fold:* a NoteOn with velocity 0 schedules as a NoteOff-equivalent.
   - *Real-song integration render:* walk-up-find a real `.mid` + Florestan SF2 (same pattern as `Sf2FirstAudioTests.FindFlorestanPath`); render to a temp WAV; assert non-silent, bounded, non-trivial length; `Assert.Ignore` when the dev-tree assets are absent.
9. **Attribution.** Per O1, add the README note if provenance confirmed.
10. **Self-audit (implementer-side, per task §6.10):** body-comment grep = 0 on every new/ported `.cs`; XML-summary present over **all** accessibilities; one-type-per-file on every file (grep template in Code Contracts §1); both TFMs build; all tests green; the real-song render actually produced an audible WAV (manual listen or the automated non-silent assertion). Commit this design doc on the branch and open **one** PR (design + implementation together, per #1165).

---

*Ends. Load-bearing: Code Contracts #114, Design Contracts #1136, PR-shape #1165. Source task #7095.*
