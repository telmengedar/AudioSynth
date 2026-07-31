# Pooshit.AudioSynth

A modern, cross-platform software synthesizer for .NET — a clean-room rewrite of a
SoundFont/MIDI synth engine built around a sample-rate-agnostic, pull-based audio core.

## Status

A working GM MIDI synthesizer core. It loads SoundFont 2 (`.sf2`) sound banks and plays full
General MIDI songs — with program/bank routing, volume/expression, pan, pitch-bend (+ RPN bend
range), modulation, sustain pedal, reverb/chorus sends, and voice-stealing — both:

- **offline**, rendering a whole song to a `.wav` file or any `IAudioSink`, and
- **real-time**, as a pull-driven, loopable `IAudioSource` (`RealtimeSequencer`) suitable for
  feeding a live game-engine audio buffer (e.g. Godot's `AudioStreamGenerator`).

Both paths share one MIDI-neutral dispatch core (`MidiTimelineImporter` → `Timeline` →
`RealtimeSequencer`) and are proven bit-identical for the same MIDI + SF2 input. See
[`docs/usage.md`](docs/usage.md) for consumer-facing usage with runnable snippets (offline render,
real-time playback, and a Godot integration sketch), and `docs/architecture/` for the design history
(`audiosynth-rewrite.md` for the core engine, `midi-integration.md` for the MIDI/sequencer layer —
both carry STATUS notes marking what has since shipped).

## Targets

Multi-targeted for reach and performance:

- `netstandard2.0` — .NET Framework 4.6.1+, .NET / .NET Core, Unity, Mono.
- `net8.0` — modern LTS runtime for the DSP fast paths (`MathF`, `Span<T>`, SIMD).

The core stays allocation-free on the render hot path. Audio output is abstracted behind
`IAudioSink`; NAudio is planned as one optional adapter, never a core dependency.

## Build and test

```
dotnet build Pooshit.AudioSynth.sln -c Release
dotnet test Pooshit.AudioSynth.sln -c Release
```

## Attribution

This project is a clean-room reimplementation informed by the design ideas and DSP
techniques of **CSharpSynthProject by Alex Veltsistas**, released under the MIT License.
The original code is used as reference only and is not incorporated. Pooshit.AudioSynth is
likewise MIT licensed; see [LICENSE](LICENSE), which reproduces the original copyright
notice in acknowledgement.

The MIDI message model in `Formats/Midi/` is derived from **Leslie Sanford's C# MIDI
Toolkit** (public domain / MIT); the parser and sequencing driver around it are original to
this project.

## License

MIT — see [LICENSE](LICENSE).
