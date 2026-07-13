namespace Pooshit.AudioSynth.Formats.Sf2 {

    /// <summary>
    /// One SF2 modulator record (SF2 section 7.4/7.8): describes a signal-routing connection from a
    /// source controller to a destination generator parameter.
    /// </summary>
    public sealed class Sf2Modulator {

        /// <summary>
        /// Creates an <see cref="Sf2Modulator"/>.
        /// </summary>
        public Sf2Modulator(
            ushort sourceOper,
            Sf2GeneratorType destination,
            short amount,
            ushort amountSourceOper,
            Sf2TransformType transform) {
            SourceOper = sourceOper;
            Destination = destination;
            Amount = amount;
            AmountSourceOper = amountSourceOper;
            Transform = transform;
        }

        /// <summary>
        /// Raw source modulation operation field (sfModSrcOper); encodes controller type, polarity,
        /// direction, and source shape in a 16-bit bitfield per SF2 §8.2.
        /// </summary>
        public ushort SourceOper { get; }

        /// <summary>
        /// The generator parameter this modulator drives.
        /// </summary>
        public Sf2GeneratorType Destination { get; }

        /// <summary>
        /// Signed scaling amount applied to the modulator output before summing.
        /// </summary>
        public short Amount { get; }

        /// <summary>
        /// Raw source operation for the modulation amount controller (sfModAmtSrcOper).
        /// </summary>
        public ushort AmountSourceOper { get; }

        /// <summary>
        /// Transformation applied to the output signal before it reaches the destination.
        /// </summary>
        public Sf2TransformType Transform { get; }

        /// <summary>
        /// True when the source polarity flag indicates a bipolar source.
        /// </summary>
        public bool SourceIsBipolar => (SourceOper & 0x0200) != 0;

        /// <summary>
        /// True when the source direction flag indicates MaxToMin (decreasing) direction.
        /// </summary>
        public bool SourceIsDecreasing => (SourceOper & 0x0100) != 0;

        /// <summary>
        /// True when the source is a MIDI Continuous Controller rather than a general controller.
        /// </summary>
        public bool SourceIsMidiCC => (SourceOper & 0x0080) != 0;

        /// <inheritdoc/>
        public override string ToString() => $"Mod src={SourceOper:X4} -> {Destination} amt={Amount}";
    }
}
