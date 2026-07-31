# Usage Guide

`Pooshit.AudioSynth` loads a SoundFont 2 (`.sf2`) sound bank and a Standard MIDI File, then plays the
song through a GM-routed synthesizer engine. There are two ways to drive it — pick whichever matches
your consumer:

- **Offline render** — render a whole song to a `.wav` file (or any `IAudioSink`) in one call.
- **Real-time playback** — pull fixed-size audio blocks on demand, live, with optional looping. This
  is what a game engine (Godot, Unity, etc.) needs to feed a procedural audio buffer.

Both paths share one dispatch core (`MidiTimelineImporter` → `Timeline` → `RealtimeSequencer`), so GM
routing (program/bank select, volume/expression, pan, pitch-bend + RPN range, modulation, sustain
pedal, reverb/chorus sends, channel-mode controllers) behaves identically either way, and the two
outputs are proven bit-identical for the same MIDI + SF2 input.

## Consuming the library

There is no NuGet package yet. Reference the `Pooshit.AudioSynth` project directly, or build it and
reference the resulting `netstandard2.0` or `net8.0` `Pooshit.AudioSynth.dll`. Rebuild from `main` to
pick up the real-time sequencer core (PR #38) if you have an older checkout/binary.

## Offline render

Load a soundfont, parse a MIDI file, build the synthesizer, and render straight to a WAV file:

```csharp
using System.IO;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Formats.Midi;
using Pooshit.AudioSynth.Formats.Sf2;
using Pooshit.AudioSynth.Sequencing;
using Pooshit.AudioSynth.Synthesis;

// 1. Load the SF2 soundfont.
Sf2SoundBankLoader loader = new Sf2SoundBankLoader(outputSampleRate: 44100);
SoundBank bank;
using (FileStream sf2Stream = File.OpenRead("soundfont.sf2"))
    bank = loader.Load(sf2Stream);

// 2. Parse the MIDI file and flatten it to a time-ordered event stream.
MidiFile midiFile;
using (FileStream midiStream = File.OpenRead("song.mid"))
    midiFile = MidiFile.Read(midiStream);
TimedMessageSequence sequence = new TimedMessageSequence(midiFile);

// 3. Build the synth. bank.GetPatch(0, 0) is only the *initial* default patch for every
//    channel — MidiSequencer.Render reprograms channels from the song's own ProgramChange events.
SynthesizerOptions options = new SynthesizerOptions(sampleRate: 44100, channels: 2);
Synthesizer synthesizer = new Synthesizer(options, bank.GetPatch(0, 0));

// 4. Render the whole song to a WAV file.
AudioFormat format = new AudioFormat(options.SampleRate, options.Channels);
using (WavFileSink sink = new WavFileSink("song.wav", format))
    MidiSequencer.Render(sequence, synthesizer, sink, bank);
```

`MidiSequencer.Render` GM-resets all 16 channels, plays the whole song, and appends a fixed release
tail (`MidiSequencer.ReleaseTailSeconds`, 3s) so envelopes finish audibly. See
`tools/Pooshit.AudioSynth.MidiRender/Program.cs` for the working CLI this snippet is based on.

## Real-time / live playback

For a live, pull-driven, loopable source — the shape a game engine's audio callback needs — import
the MIDI into a `Timeline` and drive it with a `RealtimeSequencer` instead of calling
`MidiSequencer.Render`:

```csharp
using System;
using System.IO;
using Pooshit.AudioSynth.Formats.Midi;
using Pooshit.AudioSynth.Formats.Sf2;
using Pooshit.AudioSynth.Sequencing;
using Pooshit.AudioSynth.Sequencing.Timeline;
using Pooshit.AudioSynth.Synthesis;

Sf2SoundBankLoader loader = new Sf2SoundBankLoader(outputSampleRate: 44100);
SoundBank bank;
using (FileStream sf2Stream = File.OpenRead("soundfont.sf2"))
    bank = loader.Load(sf2Stream);

MidiFile midiFile;
using (FileStream midiStream = File.OpenRead("song.mid"))
    midiFile = MidiFile.Read(midiStream);
TimedMessageSequence sequence = new TimedMessageSequence(midiFile);

SynthesizerOptions options = new SynthesizerOptions(sampleRate: 44100, channels: 2);
Synthesizer synthesizer = new Synthesizer(options, bank.GetPatch(0, 0));

// Import the MIDI-neutral GM decode once, then compile it to the driver's immutable schedule.
Timeline timeline = MidiTimelineImporter.Import(sequence, options.SampleRate);
CompiledSchedule schedule = timeline.Compile();

// The same fixed release tail the offline path uses, so a non-looping song ends the same way.
long releaseTailFrames = (long)(MidiSequencer.ReleaseTailSeconds * options.SampleRate);

// Optional whole-song loop: loopStart/loopEnd are sample offsets into the compiled schedule.
// Omit both (or pass null) to play once and stop.
long loopStart = 0;
long loopEnd = schedule.Count > 0 ? schedule.Entries[schedule.Count - 1].SampleOffset : 0;

RealtimeSequencer sequencer = new RealtimeSequencer(
    schedule, synthesizer, bank, releaseTailFrames, loopStart: loopStart, loopEnd: loopEnd);

// Pull fixed-size blocks whenever your audio callback / game loop needs more audio.
// destination length must be a multiple of options.Channels (interleaved samples, not frames).
float[] block = new float[512 * options.Channels];
int written = sequencer.Read(block);
// written == block.Length while playing/looping; a shorter return means true end-of-stream
// (only possible when looping is disabled).
```

`RealtimeSequencer` dispatches every MIDI event at its exact sample offset inside the block (no
block-quantization), so live output matches the offline render bit-for-bit at any block size. Adjust
overall output level at any time via `synthesizer.SetMasterGain(gain)` (applied before the final
soft-clip stage), or set the initial level via `SynthesizerOptions`'s `masterGain` parameter.

## Godot integration sketch

A Godot 4 (C#) BGM player is a thin consumer on top of `RealtimeSequencer` — it is not part of this
library. The pattern below (`AudioStreamPlayer` + `AudioStreamGenerator`, pulling frames in
`_Process`) follows Godot's documented procedural-audio approach; **the lib-side calls
(`Sf2SoundBankLoader`, `MidiFile`, `TimedMessageSequence`, `MidiTimelineImporter`,
`RealtimeSequencer`) are verified against the current API — the Godot-side calls are the standard
`AudioStreamGenerator`/`AudioStreamGeneratorPlayback` pattern and are not build-verified in this repo.**

```csharp
using Godot;
using System;
using System.IO;
using Pooshit.AudioSynth.Formats.Midi;
using Pooshit.AudioSynth.Formats.Sf2;
using Pooshit.AudioSynth.Sequencing;
using Pooshit.AudioSynth.Sequencing.Timeline;
using Pooshit.AudioSynth.Synthesis;

public partial class BgmPlayer : AudioStreamPlayer {

    [Export] public string Sf2Path = "res://audio/soundfont.sf2";
    [Export] public string MidiPath = "res://audio/song.mid";

    Synthesizer synthesizer;
    RealtimeSequencer sequencer;
    AudioStreamGeneratorPlayback playback;
    float[] scratchSamples = Array.Empty<float>();
    Vector2[] scratchFrames = Array.Empty<Vector2>();

    public override void _Ready() {
        SoundBank bank;
        using (FileStream sf2Stream = File.OpenRead(ProjectSettings.GlobalizePath(Sf2Path)))
            bank = new Sf2SoundBankLoader(44100).Load(sf2Stream);

        MidiFile midiFile;
        using (FileStream midiStream = File.OpenRead(ProjectSettings.GlobalizePath(MidiPath)))
            midiFile = MidiFile.Read(midiStream);
        TimedMessageSequence sequence = new TimedMessageSequence(midiFile);

        SynthesizerOptions options = new SynthesizerOptions(sampleRate: 44100, channels: 2);
        synthesizer = new Synthesizer(options, bank.GetPatch(0, 0));

        Timeline timeline = MidiTimelineImporter.Import(sequence, options.SampleRate);
        CompiledSchedule schedule = timeline.Compile();
        long releaseTailFrames = (long)(MidiSequencer.ReleaseTailSeconds * options.SampleRate);
        long loopEnd = schedule.Count > 0 ? schedule.Entries[schedule.Count - 1].SampleOffset : 0;
        sequencer = new RealtimeSequencer(schedule, synthesizer, bank, releaseTailFrames, loopStart: 0, loopEnd: loopEnd);

        AudioStreamGenerator generator = (AudioStreamGenerator)Stream; // set in the editor
        generator.MixRate = 44100; // match the engine's sample rate
        Play();
        playback = (AudioStreamGeneratorPlayback)GetStreamPlayback();
    }

    public override void _Process(double delta) {
        int framesAvailable = playback.GetFramesAvailable();
        if (framesAvailable <= 0)
            return;

        if (scratchSamples.Length < framesAvailable * 2) {
            scratchSamples = new float[framesAvailable * 2];
            scratchFrames = new Vector2[framesAvailable];
        }

        Span<float> destination = scratchSamples.AsSpan(0, framesAvailable * 2);
        int written = sequencer.Read(destination);
        int writtenFrames = written / 2;
        for (int i = 0; i < writtenFrames; i++)
            scratchFrames[i] = new Vector2(scratchSamples[2 * i], scratchSamples[2 * i + 1]);

        playback.PushBuffer(scratchFrames.AsSpan(0, writtenFrames).ToArray());
    }
}
```

Key points carried over from the engine's own contract:

- `RealtimeSequencer.Read` must only ever be called from one thread (either always `_Process`, or
  always a dedicated feeder thread — never both), because the underlying `Synthesizer` mutates its
  voice pool without locks.
- Match `AudioStreamGenerator.MixRate` to the engine's sample rate (44100 above) to avoid Godot
  resampling the output.
- `PushBuffer` marshals a `Vector2[]` per call; reuse the scratch arrays instead of allocating a
  fresh one every frame, as shown above.
