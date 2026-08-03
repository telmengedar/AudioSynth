# Design: Tracker Channel Panning

> Repo mirror (source of truth): `docs/architecture/tracker-panning.md` on branch `feature/tracker-panning`.
> DiVoid task **#7553** · tick-effects design **#7511** (PR #45, merged) · tracker-format design **#7452** (PR #41, merged) · project **#6128** · map root #6708.
> Load-bearing: Code Contracts #114 §0, Design Contracts #1136 (§1 KISS/DRY/YAGNI), comment/XML-doc contract #2051.
> Scope: the tracker **format + engine** only. No `Synthesizer`, voice, DSP, `NeutralEvent`, or `Timeline` change — pan routes entirely through the already-shipped `ISynthesizer.SetChannelPan` (live) and the already-shipped `NeutralEvent.SetPan` (offline).

---

## 1. Problem

The tracker format has **no panning at all**: no per-channel default pan and no pan effect. Every channel therefore lowers dead-center, and a MIDI→tracker render (the `ff3koltz` proof) sounded dense and mono. The `Synthesizer` already exposes `SetChannelPan(channel, pan)` (the MIDI path uses it); the tracker format simply never surfaced panning.

Toni, verbatim (task #7553):

> "channel panning is a thing trackers understand... MOD had default constant pannings, S3M/IT had an initial channel panning which you could adjust using effects. We definitely need that to widen the sound."

The design adds exactly that — an **S3M/IT-style per-channel initial pan** plus a **`SetPan` effect** to move pan mid-song — routed through the existing pan control, no DSP change.

## 2. Scope & Non-Scope

**In scope**
- A per-channel **initial pan** on `Song` (POD, serializable), applied at channel init on both playback paths.
- A **default pan layout** the engine computes when the song leaves initial pan unset (so a naive song still widens).
- A **`SetPan` effect** (append-only `TrackerEffectCommand` value = 13) letting a cell set a channel's pan mid-song.
- Routing on **both** playback paths — live (`TrackerSequencer`/`TrackerEffectEngine`) and offline (`TrackerTimelineImporter`) — because pan is a discrete control with a first-class offline representation (`NeutralEvent.SetPan`), unlike the per-tick pitch effects that #7511 deferred offline. Result: **pan renders identical live and offline.**
- One shared pan-scale helper (`TrackerPan`) so the byte→signed mapping and the default layout live in exactly one place.
- Unit tests + one audible/asserting proof.

**Out of scope (explicit)**
- Any `Synthesizer` / voice / DSP / mixer change. `SetChannelPan` is used as-is.
- Any new `NeutralEvent` / `Timeline` factory — `NeutralEvent.SetPan` already exists.
- **Pan-slide** (auto-pan motion, S3M `Pxy` / IT `Yxy`). Deferred as a **named follow-up** (§10) — YAGNI for "widen the dense render". If built, it slots into the per-tick engine exactly like `VolumeSlide`, live-only per #7511.
- Growth of `ITrackerCellSink` or `ChannelEffectState` — see §9 (neither earns its place in v1).
- Tracker-file (.mod/.s3m/.it) parsing; JSON (the engine serializes the POD itself, per #7452).

## 3. Assumptions & Constraints

- **POD constraint (#7452):** the format is plain value data (value types / arrays) a game engine serializes itself. Initial pan must be an array of value types, no object graph, `default`-friendly.
- **16-channel synth ceiling** (unchanged): `Song.ChannelCount ∈ [1,16]`, already validated at both entry points.
- **`SetChannelPan` range/convention (verified in `ISynthesizer.cs`):** signed float, `-1` = full left, `0` = center, `+1` = full right; read live each block, applies to currently-sounding and future voices on the channel. This is the single target both features map onto.
- **`NeutralEvent.SetPan(int channel, float pan)` exists** and carries the same signed-float pan; the importer already emits it (seed at offset 0).
- The applier is deliberately **effect-agnostic** (#7511) — it never reads `Cell.Effect`. This design preserves that: effect interpretation stays in the engine (live) and importer (offline), never in the applier.

## 4. Pre-Design Checklist (#1136 §5) answered in order

1. **Mirror enum?** No new mirrored enum — `SetPan` is an **append** (`=13`) to the open `TrackerEffectCommand`; unknown values already pass through (#7452).
2. **Single-impl abstraction?** None added. No new interface, no sink verb, no `ChannelEffectState` field. Pan routes through the *existing* `SetChannelPan` / `NeutralEvent.SetPan`; the one new type (`TrackerPan`) is a pure static value-map, not an abstraction seam.
3. **Speculative generality?** None. Pan-slide, per-element pan overrides, and >128 pan resolution are all declined with named triggers (§10), at zero present cost.
4. **DRY math:** the byte→signed mapping + default layout is the only logic that would otherwise recur across the two interpreters (live engine, offline importer) and the two entry-point validators — 3–4 sites. It is centralized once in `TrackerPan` (see §9 DRY note). Per-path *emission* is a single primitive call each (`synth.SetChannelPan` vs `timeline.Add(..SetPan..)`), inherent to the two-path architecture and below the shared-helper threshold.
5. **Additive / no shim:** existing songs are **bit-identical** — an unset `ChannelPan` yields the computed default layout, which for `ChannelCount == 1` is dead-center (today's behavior) and for N>1 is the new intended spread. Songs with no `SetPan` effect are unaffected by the effect path. No migration, no compat shim.
6. **Reuse:** reuses `SetChannelPan`, `NeutralEvent.SetPan`, the `TrackerNotes`-style static-helper pattern, the engine's existing per-row decode switch, and the importer's existing per-row effect pre-pass.
7. **Scales are named consts, not config:** the 0/64/128 pan endpoints and the default-layout offsets are named constants in `TrackerPan`.
8. **Trade-offs explicit** (§9, §10); **contracts cited**; **neither predecessor design superseded** (#7452 format and #7511 tick-engine are both extended, not replaced).

## 5. Architectural Overview

```
                         Song (POD)
                   ┌───────────────────────────┐
                   │ + byte[] ChannelPan        │  0..128 scale, empty = default layout
                   └───────────────────────────┘
                                │
        ┌───────────────────────┴────────────────────────┐
        │ LIVE path                                       │ OFFLINE path
        ▼                                                 ▼
 TrackerSequencer                                 TrackerTimelineImporter
   • seed initial pan once at start ──┐             • seed initial pan at offset 0 ──┐
     (synth.SetChannelPan)            │               (NeutralEvent.SetPan)          │
   • per row → engine.EnterRow        │             • per row → applier.Apply        │
        │                             │             • per row → SetPan effect ───────┤
        ▼                             │                    (NeutralEvent.SetPan)     │
 TrackerEffectEngine                  │                                              │
   • SetPan effect at tick 0 ─────────┤                                              │
     (synth.SetChannelPan)            │                                              │
                                      ▼                                              ▼
                                TrackerPan (static)  ◄── single source of pan math ──┘
                                  • ToSignedPan(byte) : 0→-1, 64→0, 128→+1
                                  • DefaultByte(count, index)
                                  • InitialSigned(song, channel)
                                             │
                                             ▼
                        ISynthesizer.SetChannelPan  /  NeutralEvent.SetPan   (UNCHANGED)
```

Both paths converge on the same `TrackerPan` math and the same two pre-existing pan primitives. No component below `TrackerPan` changes.

## 6. Components & Responsibilities

| Component | Change | Owns | Does NOT own |
|--|--|--|--|
| `Song` (POD) | **+ `byte[] ChannelPan`** (`Array.Empty` default) | The per-channel initial-pan config as plain data | Any layout/mapping logic (that's `TrackerPan`) |
| `TrackerEffectCommand` | **append `SetPan = 13`** | The effect's identity | Its semantics (engine/importer) |
| `TrackerPan` (NEW static, `Formats/Tracker/`) | new file | The **only** pan math: byte↔signed mapping, endpoint consts, default-layout formula, `InitialSigned` resolver | Emission (never calls synth/timeline) |
| `TrackerSequencer` | seed initial pan once per playback run; validate `ChannelPan` length | Transport-lifecycle seeding of initial pan (holds `synth` + `song`) | Effect decoding (that's the engine) |
| `TrackerEffectEngine` | handle `SetPan` at row-enter (tick 0) | Live effect interpretation → `synth.SetChannelPan` | Initial-pan seeding (sequencer); per-tick pan state (none in v1) |
| `TrackerTimelineImporter` | initial-pan seed at offset 0 (replace the hardcoded `0f`); emit `SetPan` per row; validate `ChannelPan` length | Offline effect interpretation → `NeutralEvent.SetPan` | — |
| `TrackerCellApplier` | **unchanged** | Effect-agnostic cell→verb decode | Anything pan (pan is not a cell verb; it's an effect/init concern) |
| `ISynthesizer`, `NeutralEvent`, `ChannelEffectState`, `ITrackerCellSink`, sinks | **unchanged** | — | — |

## 7. Data Model (POD)

**`Song.ChannelPan : byte[]`** (default `Array.Empty<byte>()`)
- Per-channel initial pan on the **0..128 scale**: `0` = full left, `64` = center, `128` = full right (values `129..255` clamp to full right).
- **Empty array (length 0) = "unset" → the engine applies the computed default layout** (§8). This is the *only* "absent" signal; it lives at the array (container) level, deliberately mirroring `Pattern.Rows`'s nullable "not overridden" semantics (#7452): a sparse, once-per-channel config expresses absence at the container, not per element.
- A **provided** array must have `Length == ChannelCount` (validated at both entry points; else `ArgumentException`). Within a provided array there is **no per-element "unset"** — `0` means full left (an explicit value), not absent. Documented asymmetry vs the dense `Cell` 0-sentinels; per-element nullability is declined (YAGNI, §10).

**Pan scale is shared with the `SetPan` effect param** — one 0..128 byte scale for both initial pan and the effect (DRY; §9). Endpoints are exact: `(value − 64) / 64` gives `0→−1`, `64→0`, `128→+1`.

`TrackerPan` (static, behavior-only, no state — same shape as `TrackerNotes`):
- consts `Left = 0`, `Center = 64`, `Right = 128`.
- `ToSignedPan(byte value)` → `clamp((value − 64) / 64, −1, +1)`.
- `DefaultByte(int channelCount, int channelIndex)` → the default-layout byte (§8).
- `InitialSigned(Song song, int channel)` → if `song.ChannelPan.Length == song.ChannelCount` then `ToSignedPan(song.ChannelPan[channel])` else `ToSignedPan(DefaultByte(song.ChannelCount, channel))`. **This is the single resolver both paths call for initial pan.**

## 8. Default pan layout (when `ChannelPan` is empty)

`DefaultByte(channelCount, channelIndex)`:
- `channelCount == 1` → `Center (64)` (single channel stays dead-center; preserves today's behavior).
- otherwise, an **alternating symmetric half-spread**: even channel index → `32` (half-left, signed −0.5); odd channel index → `96` (half-right, signed +0.5).

Rationale: the goal is to *widen a dense render* without ear fatigue. Alternating (rather than an even left→right ramp) is the historical tracker behavior (MOD hardware alternated L/R) and avoids clumping consecutive channels — which in a MIDI-derived song are unrelated instruments — onto the same side. Half-width (±0.5) rather than MOD's hard ±1.0 widens audibly while staying comfortable on headphones. It degrades gracefully to center for a mono (1-channel) song and is fully deterministic.

**This is the one taste/domain call flagged for Toni (§13).** It is settled as the shipped default (an architect call under YAGNI — a concrete, non-blocking default is required), but the exact width and shape are Toni's to confirm; alternatives are hard-alternating (0/128) or an even console-ramp, each a one-line change in `DefaultByte`.

## 9. Contracts, Interactions & Data Flow

**Initial pan — where and when applied**

- **Offline (`TrackerTimelineImporter`):** the importer already seeds each channel at offset 0 with `SetGain(ch,1)` + `SetPan(ch, 0f)`. Replace the pan seed with `NeutralEvent.SetPan(ch, TrackerPan.InitialSigned(song, ch))`. One-line change; the seed already exists at exactly the right place.
- **Live (`TrackerSequencer`):** the live path currently seeds nothing (it relies on synth defaults). Add a **once-per-run** seed: a `bool channelsSeeded` field, `false` at construction and reset to `false` in `SeekTo`. On the first `ApplyRow` after a (re)start, before `engine.EnterRow`, loop channels calling `synth.SetChannelPan(ch, TrackerPan.InitialSigned(song, ch))`, then set the flag. `Play()` (resume) does **not** clear the flag, so resuming mid-song preserves any pan a `SetPan` effect already moved.

**Coexistence: initial pan vs mid-song `SetPan` vs the existing effect state**
- Initial pan establishes the channel baseline once at start (and re-establishes after `SeekTo`, matching "channel init" — seeking to the middle does not replay earlier `SetPan` effects, so channels sit at their initial pan until a `SetPan` is hit).
- A mid-song `SetPan` is a discrete override that simply calls `SetChannelPan` again; last-writer-wins, exactly like the synth's live-read pan.
- **Pan needs no per-channel accumulator in v1** → `ChannelEffectState` is **not** touched. `SetPan` is not a per-tick command (`SetPan (13) > NoteDelay (12)`, so `IsPerTickCommand` returns `false`); its `ActiveEffect` stays `None` and it consumes no param-memory (param `0` = full left, an absolute value, **not** "reuse last"). This keeps v1 pan orthogonal to the pitch/volume effect state entirely.

**`SetPan` effect — decode sites**
- **Live (`TrackerEffectEngine.EnterRowForChannel`):** add `case TrackerEffectCommand.SetPan:` → apply the fresh cell as usual (its note/instrument/volume still play — a cell can carry both a note and `SetPan`), then `synth.SetChannelPan(channel, TrackerPan.ToSignedPan(param))`. `ActiveEffect` remains `None` (no per-tick work).
- **Offline (`TrackerTimelineImporter`):** in the per-row apply loop, after `applier.Apply(...)`, when `cell.Effect == SetPan` emit `timeline.Add(sink.Offset, NeutralEvent.SetPan(channel, TrackerPan.ToSignedPan(cell.EffectParam)))`. Consistent with how the importer already `Add`s its seed events directly.

**Logical contract of `SetPan`**

| Aspect | Value |
|--|--|
| Command value | `13` (append) |
| Param | pan on the 0..128 scale (`0`=left, `64`=center, `128`=right; `>128` clamps right) |
| Param `0` | full left (absolute) — **not** param-memory reuse |
| Applied | once at row enter (tick 0); no per-tick advance |
| Coexists with note | yes — note plays and channel is panned in the same cell |
| Both paths | yes — identical result live vs offline |

**KISS note (delete/inline check):** the tempting extra abstraction is a `SetPan` verb on `ITrackerCellSink` (symmetric with `SetGain`). It is **declined**: the sink exists to bridge verbs the *shared, effect-agnostic applier* emits; initial pan and `SetPan` are emitted by the *non-shared interpreters* (sequencer/engine live, importer offline), which each already hold their native primitive (`synth` / `timeline`). Adding a sink method + an applier pass-through to save two one-line native calls fails the inline check and would force `TimelineCellSink` to grow a method — the very cost #7511 avoided for pitch-bend. The difference from #7511: there, the verb had *no* offline counterpart; here the native offline primitive already exists, so calling it directly is both cheaper and consistent.

**DRY note (`block_size × site_count`):** the byte→signed map and the default-layout formula would otherwise recur at up to 4 sites (live seed, live effect, offline seed, offline effect) plus 2 validators. Centralized once in `TrackerPan` (`ToSignedPan`, `DefaultByte`, `InitialSigned`). What remains per-path is a single primitive call (`synth.SetChannelPan(...)` vs `timeline.Add(.., NeutralEvent.SetPan(...))`), ≤1 line each — below the >5-line×>2-site helper threshold and inherent to the two-path architecture (identical to how gain is `SetChannelGain` live vs `NeutralEvent.SetGain` offline). The `ChannelPan.Length` validation is duplicated across the two entry points, matching the *existing* accepted pattern where each entry point independently validates `ChannelCount`/`Bpm`/`Speed`.

## 10. Quality Attributes & Trade-offs

- **Maintainability:** one new static type, one new `Song` field, one enum append, and small local edits at four existing sites. No interface or state-struct growth. All pan math has a single home.
- **Consistency (live == offline):** pan is discrete, so both paths produce the same audible result — a stronger guarantee than #7511's per-tick effects (which are live-only). This is a deliberate divergence from #7511's live-only stance, justified because pan needs no new offline primitive.
- **YAGNI trade-offs, each with a named trigger:**
  - **Pan-slide** declined for v1. Trigger to build: a concrete need for *moving* pan (auto-pan / rotary). When it arrives, add `PanSlide` as a per-tick command mirroring `VolumeSlide` (arm a `PanLevel`/delta in `ChannelEffectState`, advance per tick, `synth.SetChannelPan`), live-only per #7511. Cost today: zero.
  - **Per-element pan override** (pan some channels, default the rest) declined. Trigger: authors asking to mix defaults with overrides. Today: specify all channels or none. Cost: zero.
  - **>128 pan resolution / surround** declined — 129 steps is ample; `>128` clamps.
- **Rejected alternative — signed `sbyte` pan (0 = center):** would make an all-zero provided array read as all-center (friendlier `default`), but forces the initial-pan scale (`−64..+64`) to differ from the unsigned `SetPan` effect-param scale (`Cell.EffectParam` is `byte`), yielding **two** pan scales. Rejected to keep **one** pan scale everywhere (DRY, less author confusion). The price — a *provided* array's `0` means full left, not center — is documented and matches the S3M convention Toni referenced; the common path (empty array → spread) is unaffected.
- **Rejected alternative — `SetPan` as a per-tick effect:** unnecessary; pan is set once, needs no accumulator. Keeping it discrete avoids touching `ChannelEffectState`.

## 11. Risks & Mitigations

- **R1 — provided `ChannelPan` zero-means-left footgun.** An author writing `new byte[N]` expecting center gets hard-left. Mitigation: XML-doc on `Song.ChannelPan` states plainly "empty = default layout; `0` = full left, `64` = center"; the common path (empty → spread) sidesteps it; matches S3M. Low severity.
- **R2 — default layout is a taste call.** The half-alternating spread may not match Toni's ear. Mitigation: it is a one-line change in `TrackerPan.DefaultByte`, isolated behind the resolver; flagged for confirmation (§13). Non-blocking.
- **R3 — live/offline drift** if a future edit adds pan logic to only one path. Mitigation: both paths call the same `TrackerPan` resolver; a proof test asserts a `SetPan` song renders equivalent pan events on both paths.

## 12. Migration / Rollout

Purely additive. Existing songs: `ChannelPan` deserializes to empty → default layout; a 1-channel song stays center (bit-identical); multi-channel songs gain the intended spread (the desired behavior change). No stored data migration. No API break (`ISynthesizer`, sinks, `NeutralEvent`, `Timeline` all unchanged).

## 13. Open Questions for Toni

1. **Default layout (the one domain/taste call).** Shipped default = **alternating half-spread** (even ch → half-left, odd ch → half-right; 1 channel = center). Confirm, or choose an alternative (hard-alternating ±1.0 like classic MOD, or an even left→right console ramp). One-line change in `TrackerPan.DefaultByte`; not build-blocking — John ships the alternating half-spread unless told otherwise.

*(No other open questions. Pan representation = 0..128 byte scale, and pan-slide = named follow-up, are settled as architect calls below.)*

**Settled as architect calls (not requiring Toni input, but surfaced for visibility):**
- **Pan representation:** one shared **0..128 byte scale** (0=left, 64=center, 128=right, exact endpoints), used by *both* initial pan and the `SetPan` effect param, mapped to the synth's signed −1..+1. Chosen over signed `sbyte` to keep a single scale (§10).
- **Pan-slide:** **not** in v1; named per-tick follow-up (§10).
- **Both playback paths** supported (pan renders identical live/offline).

## 14. Implementation Guidance (build order for John)

1. **`TrackerPan`** (`Formats/Tracker/TrackerPan.cs`, new): consts `Left/Center/Right = 0/64/128`; `ToSignedPan(byte)`; `DefaultByte(channelCount, index)`; `InitialSigned(Song, channel)`. Pure, static, no state — twin of `TrackerNotes`. XML-doc summaries ≤2 lines (#2051).
2. **`Song.ChannelPan`** (`byte[]`, `Array.Empty<byte>()` default) with the doc from §7 (empty = default layout; 0=left, 64=center).
3. **`TrackerEffectCommand.SetPan = 13`** (append) with a one-line summary.
4. **`TrackerTimelineImporter`** (offline): validate `ChannelPan.Length ∈ {0, ChannelCount}`; replace the offset-0 `SetPan(ch, 0f)` seed with `NeutralEvent.SetPan(ch, TrackerPan.InitialSigned(song, ch))`; in the per-row apply loop emit `NeutralEvent.SetPan` for `SetPan` cells.
5. **`TrackerSequencer`** (live): validate `ChannelPan.Length` in the ctor; add `channelsSeeded` (false in ctor + `SeekTo`); seed initial pan once in `ApplyRow` before `engine.EnterRow`.
6. **`TrackerEffectEngine`** (live): add `case SetPan` in `EnterRowForChannel` → `ApplyFreshCell` then `synth.SetChannelPan(channel, TrackerPan.ToSignedPan(param))`; confirm `ActiveEffect` stays `None`.
7. **Tests** (`test/.../Tracker/`):
   - `TrackerPan` unit: endpoint exactness (0/64/128 → −1/0/+1), clamp >128, default layout (1 ch → center; N ch → alternating 32/96), `InitialSigned` empty-vs-provided.
   - `Song` invariant: empty `ChannelPan` round-trips as unset.
   - Live: initial pan seeded once at start (spy synth records `SetChannelPan` per channel); re-seeded after `SeekTo`; resume does not re-seed; `SetPan` cell moves pan and its note still sounds; provided-array length mismatch throws.
   - Offline: importer emits `SetPan` at offset 0 with the layout value; `SetPan` cell emits `NeutralEvent.SetPan` at the row offset; length mismatch throws.
   - **Parity proof:** a small song with initial pan + a mid-song `SetPan` produces equivalent pan values on both paths; renders non-silent bounded audio through both `TrackerSequencer` and `RealtimeSequencer`.
   - No-effect / no-pan song remains bit-identical (regression guard).

**Respect:** no `Synthesizer`/DSP/`NeutralEvent`/`Timeline`/`ITrackerCellSink`/`ChannelEffectState`/applier change; append-only enum; XML-doc summaries ≤2 lines (#2051); one type per file.
