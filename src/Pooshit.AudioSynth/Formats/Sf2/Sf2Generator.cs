namespace Pooshit.AudioSynth.Formats.Sf2 {

    /// <summary>
    /// One SF2 generator record (SF2 section 7.5/7.9): a typed parameter with a 16-bit raw amount
    /// whose interpretation depends on <see cref="Type"/>.
    /// </summary>
    public sealed class Sf2Generator {

        /// <summary>
        /// Creates an <see cref="Sf2Generator"/>.
        /// </summary>
        public Sf2Generator(Sf2GeneratorType type, ushort rawAmount) {
            Type = type;
            RawAmount = rawAmount;
        }

        /// <summary>
        /// The generator operation code identifying the parameter being set.
        /// </summary>
        public Sf2GeneratorType Type { get; }

        /// <summary>
        /// Raw 16-bit amount field; interpretation varies by <see cref="Type"/>.
        /// </summary>
        public ushort RawAmount { get; }

        /// <summary>
        /// Amount interpreted as a signed 16-bit integer (most numeric generators).
        /// </summary>
        public short AmountInt16 => (short)RawAmount;

        /// <summary>
        /// Low byte of the amount (used for key-range and velocity-range generators).
        /// </summary>
        public byte LowByte => (byte)(RawAmount & 0x00FF);

        /// <summary>
        /// High byte of the amount (used for key-range and velocity-range generators).
        /// </summary>
        public byte HighByte => (byte)((RawAmount & 0xFF00) >> 8);

        /// <inheritdoc/>
        public override string ToString() => $"Gen {Type} amount={RawAmount}";
    }
}
