# Pooshit.AudioSynth

A modern, cross-platform software synthesizer for .NET — a clean-room rewrite of a
SoundFont/MIDI synth engine built around a sample-rate-agnostic, pull-based audio core.

## Status

Early scaffold. The central seam (a pull-based `IAudioSource` driven by an offline
renderer or a real-time sink) is in place and proven end to end by tests. Sound-bank
loading (SF2), the voice engine, and effects are not yet implemented — see
`docs/architecture/audiosynth-rewrite.md` for the full design and the roadmap.

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
