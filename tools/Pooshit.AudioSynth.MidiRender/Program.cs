using System;
using System.IO;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Formats;
using Pooshit.AudioSynth.Formats.Midi;
using Pooshit.AudioSynth.Formats.Sf2;
using Pooshit.AudioSynth.Sequencing;
using Pooshit.AudioSynth.Synthesis;

const int MaxVoicesForSongRender = 128;

string? songPath = args.Length > 0 ? args[0] : FindDefaultAsset("Midi", "07dkc2bram.mid");
if (songPath is null || !File.Exists(songPath)) {
    Console.Error.WriteLine(
        "No MIDI song supplied and no default song was found in the dev tree. " +
        "Usage: MidiRender <song.mid> <soundfont.sf2> <out.wav>");
    return 1;
}

string? soundfontPath = args.Length > 1 ? args[1] : FindDefaultAsset("Soundfonts", "__Florestan_Basic_GM_GS.sf2");
if (soundfontPath is null || !File.Exists(soundfontPath)) {
    Console.Error.WriteLine(
        "No SoundFont supplied and the default Florestan test SoundFont was not found in the dev tree. " +
        "Usage: MidiRender <song.mid> <soundfont.sf2> <out.wav>");
    return 1;
}

string outputPath = args.Length > 2 ? args[2] : "midirender.wav";

AudioFormat format = new AudioFormat(SynthesizerOptions.DefaultSampleRate, SynthesizerOptions.DefaultChannels);

ISoundBankLoader loader = new Sf2SoundBankLoader(format.SampleRate);
SoundBank bank;
using (FileStream soundfontStream = File.OpenRead(soundfontPath))
    bank = loader.Load(soundfontStream);

if (bank.Count == 0) {
    Console.Error.WriteLine($"SoundFont '{soundfontPath}' contains no presets.");
    return 1;
}

MidiFile midiFile;
using (FileStream songStream = File.OpenRead(songPath))
    midiFile = MidiFile.Read(songStream);

TimedMessageSequence sequence = new TimedMessageSequence(midiFile);
SynthesizerOptions options = new SynthesizerOptions(format.SampleRate, format.Channels, SynthesizerOptions.DefaultBlockFrames, MaxVoicesForSongRender, ReverbSettings.Default);
Synthesizer synthesizer = new Synthesizer(options, bank.GetPatch(0, 0));

long frames;
using (WavFileSink sink = new WavFileSink(outputPath, format))
    frames = MidiSequencer.Render(sequence, synthesizer, sink, bank);

Console.WriteLine(
    $"Rendered {sequence.Messages.Length} MIDI event(s) from '{Path.GetFileName(songPath)}' " +
    $"through '{Path.GetFileName(soundfontPath)}' to '{outputPath}' " +
    $"({frames} frames, {(double)frames / format.SampleRate:F2}s).");
return 0;

static string? FindDefaultAsset(string subfolder, string fileName) {
    string? directory = AppContext.BaseDirectory;
    while (directory != null) {
        string candidate = Path.Combine(directory, "Source", "AudioSynthesis.Tests", subfolder, fileName);
        if (File.Exists(candidate))
            return candidate;
        directory = Path.GetDirectoryName(directory);
    }
    return null;
}
