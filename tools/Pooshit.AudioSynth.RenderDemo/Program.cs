using System;
using System.Globalization;
using System.IO;
using Pooshit.AudioSynth.Audio;
using Pooshit.AudioSynth.Audio.Sinks;
using Pooshit.AudioSynth.Formats;
using Pooshit.AudioSynth.Formats.Sf2;
using Pooshit.AudioSynth.RenderDemo;
using Pooshit.AudioSynth.Synthesis;

string? soundfontPath = args.Length > 0 ? args[0] : FindDefaultSoundfont();
if (soundfontPath is null || !File.Exists(soundfontPath)) {
    Console.Error.WriteLine(
        "No SoundFont supplied and the default Florestan test SoundFont was not found in the dev tree. " +
        "Usage: RenderDemo <soundfont.sf2> [midiNote] [durationSeconds] [out.wav] [lfoRateHz] " +
        "[vibratoDepthCents] [tremoloDepthCentibels] [filterSweepDepthCents]");
    return 1;
}

int midiNote = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 60;
double durationSeconds = args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 2.0;
string outputPath = args.Length > 3 ? args[3] : "render.wav";
float lfoRateHz = args.Length > 4 ? float.Parse(args[4], CultureInfo.InvariantCulture) : 0f;
float vibratoDepthCents = args.Length > 5 ? float.Parse(args[5], CultureInfo.InvariantCulture) : 0f;
float tremoloDepthCentibels = args.Length > 6 ? float.Parse(args[6], CultureInfo.InvariantCulture) : 0f;
float filterSweepDepthCents = args.Length > 7 ? float.Parse(args[7], CultureInfo.InvariantCulture) : 0f;

AudioFormat format = new AudioFormat(SynthesizerOptions.DefaultSampleRate, SynthesizerOptions.DefaultChannels);

ISoundBankLoader loader = new Sf2SoundBankLoader(format.SampleRate);
SoundBank bank;
using (FileStream soundfontStream = File.OpenRead(soundfontPath))
    bank = loader.Load(soundfontStream);

if (bank.Count == 0) {
    Console.Error.WriteLine($"SoundFont '{soundfontPath}' contains no presets.");
    return 1;
}

IPatch patch = bank.GetPatch(0, 0);
bool lfoOverrideRequested = vibratoDepthCents != 0f || tremoloDepthCentibels != 0f || filterSweepDepthCents != 0f;
if (lfoOverrideRequested && patch is Sf2Patch sf2Patch)
    patch = new ModLfoOverridePatch(
        sf2Patch, format.SampleRate, lfoRateHz, vibratoDepthCents, tremoloDepthCentibels, filterSweepDepthCents);

Synthesizer synthesizer = new Synthesizer(new SynthesizerOptions(format.SampleRate, format.Channels), patch);

long frames = (long)(durationSeconds * format.SampleRate);
long holdFrames = frames / 2;
long releaseFrames = frames - holdFrames;

synthesizer.NoteOn(0, midiNote, 100);
using (WavFileSink sink = new WavFileSink(outputPath, format)) {
    OfflineRenderer.Render(synthesizer, sink, holdFrames);
    synthesizer.NoteOff(0, midiNote);
    OfflineRenderer.Render(synthesizer, sink, releaseFrames);
}

Console.WriteLine(
    $"Rendered {frames} frames ({durationSeconds}s) of note {midiNote} from '{Path.GetFileName(soundfontPath)}' " +
    $"to '{outputPath}' (held {holdFrames}, released tail {releaseFrames}).");
return 0;

static string? FindDefaultSoundfont() {
    string? directory = AppContext.BaseDirectory;
    while (directory != null) {
        string candidate = Path.Combine(directory, "Source", "AudioSynthesis.Tests", "Soundfonts", "__Florestan_Basic_GM_GS.sf2");
        if (File.Exists(candidate))
            return candidate;
        directory = Path.GetDirectoryName(directory);
    }
    return null;
}
