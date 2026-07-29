# Architectural Document: Adaptive Master Gain-Staging (Per-Soundfont Loudness Calibration)

**Project:** Pooshit.AudioSynth (#6128) · **Source task:** #7238 (parked "smarter master gain-staging") · **Integration:** `Synthesizer` #6734 · **Predecessor (rejected):** static −6 dB trim, bug #7212 / design #7213 / PR 25 (CLOSED wontfix) · Author: sarah-software-architect, 2026-07-29. · **DiVoid design node:** #7254 · **DiVoid task node:** #7257.

---

## 1. Problem Statement

The master bus runs hot, and *how* hot depends almost entirely on **which soundfont is loaded**, not on the song. Same song (Force Your Way), same engine, differing only by soundfont:

| Font | RMS | near-clip samples | master pinned ≥0.985 | verdict |
|---|---|---|---|---|
| **Florestan** (default test font) | 0.185 | 0.000% | 46 / 337 s | clean |
| **OmegaGMGS2** (278 MB GS font) | 0.421 | **2.116%** | **333 / 337 s** | audible clipping all song |

Omega's raw samples measure **+7.2 dB hotter** than Florestan's. The engine applies no per-font compensation, so a hot font sits inside the `ApplyMasterBus` soft-clip (knee ~0.9) continuously → audible clipping distortion. The goal: **tame inherently-hot soundfonts without penalizing quiet/sparse material** — the explicit lesson from the rejected static trim.

**Success criteria:** Omega Force-Your-Way near-clip% → ~0; Florestan output audibly unchanged (ideally byte-identical); a quiet/sparse song never rendered quieter than it is today.

## 2. Scope & Non-Scope

**In scope:** a per-soundfont loudness measurement at load; derivation of a single attenuate-only calibration gain; one engine seam to receive it; application at the summed master, ahead of the existing soft-clip; the test/acceptance plan.

**Out of scope:** per-channel/per-voice dynamics; a static global trim (rejected, #7213 — do NOT re-propose); makeup/normalization that amplifies content; any change to `Finalize`/INV-2, to per-voice code, to the CC/velocity curves, or to `Read`'s allocation profile; a full look-ahead limiter or AGC (discussed and rejected below); stereo/format changes.

## 3. Assumptions & Constraints

- **Root cause is inherent per-font sample loudness**, evidenced by the +7.2 dB same-song delta. This is a font property, not a song property — the design leans entirely on that.
- **Render chain order** (per internal `BlockFrames`=64 block, confirmed from #6734/#7169): clear master → voice-mix (equal-power L/R, per-channel gain) with chorus + reverb send buses → **Chorus** → **Reverb** → **`ApplyMasterBus`** (per-sample soft-clip, skips non-finite) → **`Finalize`** (NaN/Inf→0, clamp ±1; INV-2) → copy to destination.
- **INV-1** (GainRamp #6731): any gain that *changes during a render* must slew per-frame (block-size-independent), never jump.
- **INV-2** (`Finalize` #6734): the NaN/Inf clamp is the sole, un-bypassable final guard. Nothing may defeat it.
- **Allocation-free steady-state `Read`**: all buffers ctor-allocated; no new per-block allocation permitted.
- **Florestan is the current reference font** the engine is tuned around; keeping it unchanged is the transparency anchor.
- Offline render (`OfflineRenderer` #6722 / `MidiSequencer.Render` #7114) is the primary use today; real-time NAudio playback is a parked goal. A solution good for both is preferred but must not compromise offline quality.

## 4. Options Considered

### Option 1 — Offline two-pass normalization (measure the render, then apply)
Render a measurement pass (or buffer the whole render), measure true-peak/RMS/LUFS-ish, derive an attenuate-only makeup to a target ceiling, apply on a second pass.
- **Pro:** most accurate — measures the actual output. Can be made PR-25-safe and transparent if strictly *attenuate-only-when-over-ceiling* (quiet content untouched, nothing amplified).
- **Con:** offline-only (gives the parked real-time goal nothing); costs a double render (~2× CPU) or full-render buffering (~119 MB for a 337 s stereo 44.1 k float render), which breaks the streaming-to-sink model (`WavFileSink` writes incrementally). It is **per-song**, so it re-solves the same font's hotness on every song rather than once.

### Option 2 — Per-soundfont loudness calibration at load ★ RECOMMENDED
Measure a font's inherent loudness once when the `SoundBank` loads (from the already-decoded normalized sample pool), derive a single **attenuate-only** calibration gain that brings a hot font down to the reference operating point, apply it as one scalar on the summed master.
- **Pro:** attacks the **actual root cause** the evidence identifies (font-inherent level). Cheapest at runtime (one multiply, computed once). **Transparent on the reference font** (Florestan → gain exactly 1.0 → byte-identical). **PR-25-safe by construction** (gain is per-font, not per-content → within a font, a sparse ballad and a dense track scale identically; relative dynamics preserved; nothing ever rendered quieter than the same song on the reference font). **Identical offline and real-time** — a static scalar, no latency, no state, no pumping. Trivially honors INV-1/INV-2/allocation.
- **Con:** aggregate sample-pool loudness is a *first-order* proxy for rendered-mix loudness (ignores which samples play, velocity, polyphony, CC volumes). Mitigated because inherent sample level is the dominant variable (the user's own same-song evidence proves it); residual per-song transients remain the soft-clip's job. If a font proves mis-estimated, the fallback is a bounded per-font *calibration render* (converges toward Option 1 but still per-font).

### Option 3 — Real look-ahead brickwall limiter (replace/augment the soft-clip)
Proper gain reduction with attack/release/look-ahead.
- **Pro:** the pro-audio "correct" transient catcher; more transparent than instantaneous soft-clip on peaks.
- **Con:** does **not** fix the cause — a font sitting 7 dB hot drives the limiter into *continuous* heavy gain reduction → audible pumping/squashing (exactly Omega's 333/337 s pinned state). Adds look-ahead latency (fine offline, real output latency real-time) and envelope state (the pumping risk already rejected in #7126/#7213). A limiter on a chronically-hot signal is worse than calibrating the signal down first. Good only as a *future augmentation* after calibration, not the headline.

### Option 4 — Running auto-gain / AGC
Continuously adapt to recent level.
- **Con:** highest pumping/breathing risk; makes quiet passages louder (destroys intended dynamics); per-content and hard to make transparent. This is the "adaptive level-tracking limiter" already rejected in #7126. **Rejected.**

## 5. Recommendation

**Adopt Option 2 — per-soundfont loudness calibration at load, attenuate-only, anchored to the reference font.** It is the only option that (a) fixes the documented root cause, (b) is transparent on well-behaved content by construction, (c) honors the PR-25 lesson without special-casing, and (d) serves offline and real-time identically with zero latency/pumping. Do **not** pair it with a limiter or AGC now (YAGNI); leave a gentle post-calibration look-ahead limiter as a *possible* future refinement of `ApplyMasterBus` only if residual transients ever bite. An **optional** offline attenuate-only "zero-clip guarantee" pass (a trimmed Option 1) can be added later as a safety net but is deferred.

## 6. Components & Responsibilities

| Component | New responsibility | Does NOT own |
|---|---|---|
| **SF2 sample-data / loader** (`Sf2SampleData` #6751, `Sf2SoundBankLoader` #6754) | Compute one **loudness estimate** over the decoded normalized float pool (silence-gated, outlier-robust). Pure measurement. | Deriving the gain; knowing the reference; applying anything. |
| **`SoundBank`** (#7123) | Carry the measured `LoudnessEstimate` (and/or a derived `CalibrationGain`) as a read-only property of the loaded font. Non-SF2/hand-built banks default to the reference (→ gain 1.0). | Loudness DSP; engine wiring. |
| **Wiring layer** (`MidiSequencer.Render` #7114 / render CLIs) | Derive `CalibrationGain = min(1, ReferenceLoudness / fontEstimate)` against a single tunable `ReferenceLoudness` const, and push it to the engine once, before the first frame. Keeps the reference tunable in one MIDI/GM-owning place. | Loudness DSP; the per-sample multiply. |
| **`Synthesizer`** (#6734) | New MIDI-neutral seam **`SetMasterCalibrationGain(float)`** storing a static scalar; apply that scalar to the summed master at the head of `ApplyMasterBus` (pre soft-clip). | GM/font semantics; measurement; the reference value. |

Single-responsibility framing: the loader *measures*, the wiring *derives and configures*, the engine *applies a number it does not interpret* — mirroring every existing `SetChannelX` seam (the engine stays MIDI/format-neutral).

## 7. Interactions & Data Flow

```
  SF2 bytes ──► Sf2SoundBankLoader.Load
                     │  (decode normalized float pool — already exists)
                     ▼
              measure loudness  ──►  SoundBank.LoudnessEstimate
                                             │
   render setup (MidiSequencer.Render / CLI) │  reads estimate
                                             ▼
        CalibrationGain = min(1, ReferenceLoudness / estimate)
                                             │
                     Synthesizer.SetMasterCalibrationGain(gain)   ← ONCE, before frame 0
                                             │
   ── per block ──►  voice-mix ► Chorus ► Reverb ►
                     ApplyMasterBus:  sample *= calibrationGain ;  soft-clip(sample)
                                                         │
                                                    Finalize (INV-2)  ──► destination
```

- Communication is synchronous, in-process, one-directional configuration then per-block application.
- The gain is **set once before rendering** and constant for the whole render → no zipper (INV-1 satisfied trivially). *If* a future path changes fonts mid-render, that call MUST route through a `GainRamp` per-frame slew; the default set-once path needs no ramp.

## 8. Contracts & Interfaces (Abstract)

- **Loudness estimate:** input = the font's normalized float sample pool; output = one non-negative scalar representing inherent loudness. Semantics: monotonic with perceived font hotness; silence-gated (near-zero samples excluded so silent tails don't deflate it); outlier-robust (a single hot one-shot must not dominate — e.g. a trimmed/percentile RMS or median-of-block-RMS). Invariant: deterministic for a given font.
- **`SoundBank.LoudnessEstimate` / `CalibrationGain`:** read-only; defaults to the reference (gain 1.0) for banks without measurement.
- **`ReferenceLoudness` const:** the anchor, set equal to the reference font's (Florestan's) measured estimate so that the reference font yields gain **exactly 1.0f** (identity multiply → byte-identical output).
- **`SetMasterCalibrationGain(float gain)`:** MIDI-neutral engine seam; `gain ∈ (0, 1]` (attenuate-only clamp applied at derivation); stored as a plain field. Invariant: applied uniformly to every finite master sample; **non-finite samples pass through un-multiplied** so `Finalize` remains their sole choke (INV-2). Default 1.0 (no-op) until set.

## 9. Cross-Cutting Concerns

- **INV-2 (NaN/Inf guard):** the calibration multiply sits *before* `ApplyMasterBus`/`Finalize` and, like the existing soft-clip, must **skip non-finite samples** (NaN·g=NaN, Inf·g=Inf would still be caught, but skipping keeps `Finalize` the single explicit choke and avoids any ambiguity). A finite gain ≤ 1 can never *create* Inf. INV-2 fully preserved.
- **INV-1 (zipper):** set-once-before-render ⇒ constant across blocks ⇒ no boundary jump. Runtime changes (not required now) must slew via `GainRamp`.
- **Allocation:** one scalar field + one multiply folded into the existing `ApplyMasterBus` loop. No new buffer; `Read` stays allocation-free.
- **Determinism/observability:** log the measured estimate and derived gain per font load (diagnostic parity with the empirical acceptance style of #7212/#7124).
- **Error handling:** empty/degenerate sample pool → estimate defaults to reference (gain 1.0, safe no-op).

## 10. Quality Attributes & Trade-offs

- **Transparency:** reference font → gain 1.0f → byte-identical (the key acceptance guarantee). Chosen over per-song normalization precisely because per-song scaling cannot be made byte-identical on Florestan without special-casing.
- **PR-25 alignment:** per-font (not per-content) scaling means quiet/sparse material is never singled out; a font's ballad and its wall-of-sound scale by the same factor, preserving intended dynamics; no song ends up quieter than it would be on the reference font. This is the exact property the static trim lacked.
- **Cost:** near-zero runtime (one multiply); one-time O(pool) measurement at load (a 278 MB font's pool scan is bounded and one-off).
- **Accuracy trade-off:** sample-pool loudness is a first-order proxy; accepted because inherent level dominates (evidence-backed) and the soft-clip still covers residual transients. Rejected the more-accurate Option 1 because offline-only + double-render/buffering cost + per-song re-derivation outweigh the marginal accuracy for the observed problem.
- **Rejected alternatives:** static trim (#7213 — penalizes quiet, inaudible benefit); limiter/AGC (pumping on chronically-hot signal, latency/state, treats symptom not cause).

## 11. Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Pool-RMS mis-estimates a font (e.g. huge silent tails, one hot one-shot) | Silence-gating + outlier-robust statistic; if still wrong, fall back to a bounded per-font *calibration render* (measure a short excerpt), still per-font. |
| Over-attenuation makes Omega feel quiet vs Florestan | Anchor targets the reference operating point, not below it; Omega lands at Florestan's level, not lower. Acceptance render #3 checks the ballad isn't reduced below its Florestan rendering. |
| A finite gain accidentally applied to non-finite sample masks INV-2 intent | Multiply skips non-finite samples (contract §8); INV-2 test with injected NaN/Inf. |
| Reference drift if the default font changes later | `ReferenceLoudness` is one named tunable in the wiring layer; re-anchor there, no engine/loader change. |
| Byte-identity lost on Florestan due to float multiply | Gain is exactly `1.0f` for the reference (identity); assert bytewise in the acceptance test. |

## 12. Migration / Rollout Strategy

Additive and default-neutral: `SetMasterCalibrationGain` defaults to 1.0 (no-op), so until the loader populates an estimate and the wiring pushes a gain, behavior is unchanged. Ship loader-measurement + engine-seam + wiring together in one PR (the change is inert without all three). No data migration.

## 13. Open Questions (product decisions for the user)

1. **Reference anchor / target level.** Anchor to Florestan (the default test font) so it stays byte-identical? Or specify an absolute target (e.g. a target summed-master RMS or a −dBFS peak ceiling)? *Recommendation: anchor to Florestan.*
2. **Attenuate-only vs bidirectional.** Recommend **attenuate-only** (a font quieter than the reference is left at gain 1.0, never boosted). Confirm you don't want quiet fonts brought *up* to the reference (that reintroduces clipping risk and is a louder-is-better policy).
3. **Scope now: calibration only vs + offline safety net.** The recommended calibration serves offline *and* real-time already. Confirm we should **not** also build the offline attenuate-only "zero-clip guarantee" pass now (defer as YAGNI, add only if residual transients bite).
4. **Loudness statistic.** Start with silence-gated, outlier-robust pool RMS and let the acceptance renders arbitrate (mirroring #7213's empiricism)? Or invest up front in a per-font calibration render for accuracy?

**Locked (Toni):** (1) Reference anchor = Florestan. (2) Attenuate-only. (3) Calibration only, no offline safety net (deferred/YAGNI). (4) Silence-gated, outlier-robust sample-pool RMS; let acceptance renders arbitrate accuracy.

## 14. Implementation Guidance for the Next Agent (ordered)

1. **Measurement.** In the SF2 decode path (`Sf2SampleData` #6751 / loader #6754), compute one loudness estimate over the normalized float pool: silence-gate near-zero samples, use an outlier-robust aggregate (trimmed/percentile RMS or median-of-block-RMS). Log it. Keep it a pure function of the pool.
2. **Carrier.** Surface the estimate on `SoundBank` (#7123) as a read-only property; default to the reference for banks without measurement.
3. **Engine seam.** Add MIDI-neutral `Synthesizer.SetMasterCalibrationGain(float)` storing a static scalar (default 1.0), mirroring the existing `SetChannelX` seams. Apply it as the first per-sample op inside `ApplyMasterBus` (summed master, post-effects, pre-soft-clip) — the *exact* insertion point #7213 analyzed, with a font-derived value instead of a const. **Skip non-finite samples** (INV-2).
4. **Wiring.** In `MidiSequencer.Render` (#7114) / render CLIs, derive `CalibrationGain = min(1, ReferenceLoudness / estimate)` against one tunable `ReferenceLoudness` const and call `SetMasterCalibrationGain` once, before the first `OfflineRenderer` block.
5. **Anchor calibration.** Measure Florestan's estimate; set `ReferenceLoudness` equal to it so Florestan's derived gain is exactly `1.0f`. Verify Omega's derived gain reproduces roughly the empirical −7.2 dB (≈0.44).
6. **Test & acceptance (empirical, per #7212 style, run FOREGROUND, both TFMs 0-warning):**
   - **Omega / Force Your Way:** near-clip% 2.116% → **~0** (target <0.05%); master pinned ≥0.985 333 s → small; RMS 0.421 → into the reference band; `Finalize` clamp count ≈ 0; clipping distortion audibly gone.
   - **Florestan / Force Your Way:** gain == 1.0f → output **byte-identical** (assert bytewise); RMS 0.185 / clip 0.000% unchanged.
   - **Quiet/sparse song (e.g. Eyes On Me):** on Florestan → byte-identical (NOT quieter — the PR-25 guard); on Omega → same font gain as the loud Omega song, and RMS not below the same ballad's Florestan rendering. This is the explicit "quiet input must not get quieter" gate.
   - **Estimate unit test:** Omega/Florestan estimate delta ≈ +7.2 dB (within tolerance) → derived gain ≈ 0.44.
   - **INV-2:** inject NaN/Inf pre-master with calibration active → still zeroed by `Finalize`.
   - **Allocation:** steady-state `Read` allocates nothing with calibration active.
   - **Corpus regression:** a handful of other font/song pairs render with no new clipping and no quiet song made quieter.

---

## Implementation Notes (as shipped)

**Summary: shipped exactly as designed above (measurement in `Sf2SampleData`, carrier on `SoundBank`, engine seam on `Synthesizer`, derivation+wiring in `MidiSequencer`), all three landed together in one PR as §12 requires. Every synthetic/mechanism-level acceptance criterion in §14.6 passes. One real, load-bearing deviation from the design's own expectation was found and is documented in full below: for the actual `OmegaGMGS2.sf2` asset in this repo, the whole-file raw-sample-pool statistic (§14.5's "Estimate unit test") does NOT reproduce the expected ≈+7.2 dB / ≈0.44-gain relationship — it measures in the opposite direction — so `DeriveCalibrationGain` resolves to a neutral 1.0f (no-op) for this specific font today. The mechanism itself is proven correct end-to-end (see below); what's missing is a measurement statistic that captures why Omega renders hot for THIS song. This is flagged as a follow-up, not silently absorbed.**

### `ReferenceLoudness` anchor (§8, §14.5)

Measured Florestan's `SoundBank.LoudnessEstimate` (via a throwaway harness loading `__Florestan_Basic_GM_GS.sf2` through `Sf2SoundBankLoader`, deleted before this PR's final commit — not part of the shipped diff) and formatted it with `value.ToString("G9", CultureInfo.InvariantCulture)`:

- Raw measured value: **`0.303088784`** (invariant-culture `G9`; confirmed the *culture-current* `ToString("G9")` without an explicit `CultureInfo` renders with a locale decimal separator on this machine — e.g. `0,303088784` in German locale — which would not even compile as a C# literal, let alone round-trip correctly. `CultureInfo.InvariantCulture` is mandatory for this technique.)
- Verified the round-trip: `float.Parse("0.303088784", CultureInfo.InvariantCulture)` reproduces the identical bit pattern (`BitConverter.SingleToInt32Bits` == `1050357364` both ways).
- Baked as `const float ReferenceLoudness = 0.303088784f;` in `MidiSequencer`.
- Consequence: `MidiSequencer.DeriveCalibrationGain(florestanBank) == 1.0f` exactly, verified by an `Is.EqualTo(1f)` assertion with no tolerance (`Florestan_DeriveCalibrationGain_IsExactlyOne`), and `ApplyMasterBus`'s `x *= masterCalibrationGain` with `masterCalibrationGain == 1.0f` is a true IEEE-754 identity multiply for every finite `x` — so Florestan renders byte-identical to before this feature.

### `LoudnessEstimate` measurement parameters (§8, §14.1)

- Block size: **2048 frames** (≈46.4 ms at 44.1 kHz) — large enough to average out sample-to-sample noise, small enough that a long silent intro/tail doesn't dominate a single block.
- Silence gate: **0.00316** (≈ −50 dBFS) — blocks below this RMS are excluded from the aggregate before it runs.
- Aggregate: **median of the surviving blocks' RMS** (not mean, not global RMS) — outlier-robust per §8's contract.
- Degenerate case (every block gated out, e.g. an all-silence pool): falls back to **`0f`**, verified explicitly (`LoudnessEstimate_AllSilentPool_FallsBackToZero`) and shown to resolve to the neutral gain 1f at the `DeriveCalibrationGain` layer (never a boost), per locked decision #2.

### The Omega finding: measured direction contradicts §14.5's expectation

§14.5 instructed: *"Verify Omega's derived gain reproduces roughly the empirical −7.2 dB (≈0.44)."* §14.6 additionally specifies an *"Estimate unit test: Omega/Florestan estimate delta ≈ +7.2 dB (within tolerance) → derived gain ≈ 0.44"* as an acceptance criterion in its own right.

Measured against the real assets in this repo:

| Font | `LoudnessEstimate` (block=2048, gate=0.00316, median) |
|---|---|
| Florestan (`__Florestan_Basic_GM_GS.sf2`) | 0.303088784 |
| OmegaGMGS2 (`OmegaGMGS2.sf2`) | 0.160494894 |

`DeriveCalibrationGain(omegaBank) = min(1, 0.303088784 / 0.160494894) = min(1, 1.888) = 1.0f` — i.e. **no attenuation at all** for the real Omega bank, the opposite of the ≈0.44 the design expected.

This was investigated exhaustively before accepting it as a genuine finding rather than a bug:

- Re-derived the same statistic at block sizes 512/1024/2048/4096/8192, silence gates from 0.00316 up to 0.3, and a BS.1770-style two-pass relative gate (absolute-silence pass, then a −6…−20 dB relative gate off the first-pass mean): **Florestan's aggregate is higher than Omega's at every combination tried.**
- Checked every percentile from p50 through p99 of the (absolute-silence-gated) per-block RMS distribution for both fonts: **Omega is lower than Florestan at literally every percentile, including p99** — i.e. even Omega's loudest 1% of raw sample content, by RMS, is quieter than Florestan's loudest 1%. Global RMS and peak-of-pool were checked too (peaks are ~1.0 for both fonts — no separation there either).
- Conclusion: this is not a block-size or threshold tuning artifact. **No statistic derivable purely from the raw decoded PCM amplitude pool will show Omega louder than Florestan for these two real files.** Omega's own raw sample recordings are, top to bottom, quieter than Florestan's. The +7.2 dB hotter *rendered* output the original evidence measured must therefore come from somewhere in the synthesis chain that the raw sample pool doesn't see — plausibly SF2 generator-level gain-staging (e.g. `initialAttenuation`, gen 48) tuned lower/hotter for a professional multi-sampled library that records conservatively and compensates downstream, and/or simultaneous multi-zone layering per note — not from the PCM data itself. This is a plausible explanation, not a confirmed one; no generator-level attenuation data was inspected to confirm it (that would be a reasonable first step for whoever picks up the follow-up).

**What this means for what shipped:**

- The **mechanism** (measurement → derivation → engine seam → `ApplyMasterBus`) is fully implemented and independently proven correct: `Omega_ForcedHotEstimate_DramaticallyReducesNearClipPercentage` uses Omega's real, decoded audio (the same patches the real loader resolves) with a synthetic forced `LoudnessEstimate` of `0.69` (chosen so `DeriveCalibrationGain` lands at ≈0.4393, matching the evidence's own implied ≈0.44 ratio) and measures a real near-clip reduction from **3.1451% → 0.0118%** on Force Your Way — i.e. when a font *is* measured as hot, calibration works exactly as designed, with real audio.
- The **measurement**, as specified (whole-file raw-sample-pool RMS), does not currently detect Omega as hot for the real asset in this repo, so it does not close the loop for the motivating case. `Omega_RealMeasuredCalibrationGain_NeverExceedsUnity` documents the real measured numbers (`LoudnessEstimate=0.1604949`, `gain=1.0`) rather than asserting a false improvement.
- Empirically, rendering `1-10-Force_Your_Way.mid` through the real `OmegaGMGS2.sf2` via the shipped `MidiRender` CLI, before and after this change, produced **identical** near-clip percentages (3.176%, 944584/29737260 samples both times) — expected, given `DeriveCalibrationGain` resolves to 1.0f for this font today.
- Florestan is unaffected either way, and the attenuate-only invariant (never boost) holds in every case tested, including this one.

**Recommended follow-up** (not undertaken here — out of this PR's locked scope, and would require either revisiting §2's non-scope or extending §6/§8's carrier contract): investigate a generator-aware or render-time loudness measurement (e.g. factoring in `initialAttenuation`/velocity-curve at the resolved-patch level, or falling back to §11's already-anticipated "bounded per-font calibration render" mitigation) so that fonts like Omega, whose hotness comes from synthesis-chain gain-staging rather than raw PCM level, are correctly detected. A DiVoid task should be filed against this for prioritization.
