using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Pooshit.AudioSynth.Formats.Midi {

    /// <summary>
    /// Flattens every track of a <see cref="MidiFile"/> into one time-ordered stream, converting ticks
    /// to seconds via the header's division and any running tempo meta (default 120 BPM until the first).
    /// </summary>
    public sealed class TimedMessageSequence {

        const int DefaultMicrosecondsPerQuarterNote = 500000;

        /// <summary>
        /// Creates a <see cref="TimedMessageSequence"/> by scanning every track of <paramref name="file"/>.
        /// </summary>
        /// <param name="file">the parsed MIDI file</param>
        public TimedMessageSequence(MidiFile file) {
            if (file is null)
                throw new ArgumentNullException(nameof(file));

            string? name = null;
            Messages = Scan(file, ref name).ToArray();
            Name = name;
        }

        /// <summary>
        /// The sequence's name, taken from the first track-name or marker meta event encountered;
        /// <c>null</c> if none is present.
        /// </summary>
        public string? Name { get; }

        /// <summary>
        /// Every message in the file, ordered non-decreasing by <see cref="TimedMidiMessage.Time"/>.
        /// </summary>
        public TimedMidiMessage[] Messages { get; }

        static IEnumerable<TimedMidiMessage> Scan(MidiFile file, ref string? name) {
            TrackMessage[] ordered = OrderByPosition(file);

            int currentPosition = 0;
            float currentTime = 0f;
            float secondsPerTick = InitialSecondsPerTick(file.Header);
            string? scannedName = null;

            List<TimedMidiMessage> result = new List<TimedMidiMessage>(ordered.Length);
            foreach (TrackMessage message in ordered) {
                if (message.Position != currentPosition) {
                    currentTime += (message.Position - currentPosition) * secondsPerTick;
                    currentPosition = message.Position;
                }

                if (message.Message is MetaMessage meta) {
                    if (meta.MetaType == MetaMessageType.Tempo && file.Header.SequenceType == SequenceType.Ppqn)
                        secondsPerTick = TempoToSecondsPerTick(meta, file.Header.Division);
                    else if ((meta.MetaType == MetaMessageType.TrackName || meta.MetaType == MetaMessageType.Marker)
                             && string.IsNullOrEmpty(scannedName))
                        scannedName = Encoding.ASCII.GetString(meta.Bytes);
                }

                result.Add(new TimedMidiMessage(message.Message, currentTime));
            }

            name = scannedName;
            return result;
        }

        static TrackMessage[] OrderByPosition(MidiFile file) {
            List<TrackMessage> all = new List<TrackMessage>();
            foreach (Track track in file.Tracks)
                all.AddRange(track.Messages);
            return all.OrderBy(m => m.Position).ToArray();
        }

        /// <summary>
        /// Preserved legacy quirk (DiVoid #7098 R3/O3): the SMPTE branch yields microseconds-per-tick,
        /// not seconds-per-tick, a unit mismatch left unfixed and unverified; PPQN is the tested path.
        /// </summary>
        static float InitialSecondsPerTick(MidiHeader header) {
            if (header.SequenceType == SequenceType.Ppqn)
                return MicrosecondsToSecondsPerTick(DefaultMicrosecondsPerQuarterNote, header.Division);

            float frames = (header.Division & (127 << 8)) >> 8;
            if (Math.Abs(frames - 29.0f) < 0.001f)
                frames = 29.97f;
            return 1_000_000f / ((header.Division & 255) * frames);
        }

        static float TempoToSecondsPerTick(MetaMessage tempoMeta, short division) {
            int microsecondsPerQuarterNote = 0;
            for (int i = 0; i < tempoMeta.Length; i++) {
                microsecondsPerQuarterNote <<= 8;
                microsecondsPerQuarterNote |= tempoMeta[i];
            }
            return MicrosecondsToSecondsPerTick(microsecondsPerQuarterNote, division);
        }

        static float MicrosecondsToSecondsPerTick(int microsecondsPerQuarterNote, short division) {
            return microsecondsPerQuarterNote / 1_000_000f / division;
        }
    }
}
