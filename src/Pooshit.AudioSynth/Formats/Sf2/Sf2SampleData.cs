using System;

namespace Pooshit.AudioSynth.Formats.Sf2 {

    /// <summary>
    /// The raw sample-data pool from the SF2 sdta chunk: either 16-bit (smpl only) or 24-bit
    /// (smpl + sm24 extension).  Sample values are stored in the original little-endian SF2 layout
    /// and are accessed via <see cref="GetSample"/> which applies correct 24-bit sign-extension.
    /// </summary>
    public sealed class Sf2SampleData {

        /// <summary>
        /// Creates a 16-bit sample-data pool.
        /// </summary>
        /// <param name="smpl">Signed 16-bit sample words, one per sample frame, little-endian.</param>
        public Sf2SampleData(short[] smpl) {
            Smpl = smpl ?? throw new ArgumentNullException(nameof(smpl));
            Sm24Lsb = null;
            BitsPerSample = 16;
        }

        /// <summary>
        /// Creates a 24-bit sample-data pool from an smpl block and its sm24 extension.
        /// </summary>
        /// <param name="smpl">High 16 bits of each 24-bit sample, as signed int16.</param>
        /// <param name="sm24Lsb">Low 8 bits of each 24-bit sample; length must equal smpl.Length.</param>
        public Sf2SampleData(short[] smpl, byte[] sm24Lsb) {
            if (smpl is null) throw new ArgumentNullException(nameof(smpl));
            if (sm24Lsb is null) throw new ArgumentNullException(nameof(sm24Lsb));
            if (sm24Lsb.Length != smpl.Length)
                throw new ArgumentException("sm24Lsb length must equal smpl length.", nameof(sm24Lsb));
            Smpl = smpl;
            Sm24Lsb = sm24Lsb;
            BitsPerSample = 24;
        }

        /// <summary>
        /// The smpl chunk data: one signed int16 per sample frame.  For 16-bit pools this is the
        /// full sample.  For 24-bit pools this holds the high 16 bits of each 24-bit sample.
        /// </summary>
        public short[] Smpl { get; }

        /// <summary>
        /// The sm24 extension bytes, or null for 16-bit pools.  One byte per sample frame: the
        /// least-significant 8 bits of each 24-bit sample.
        /// </summary>
        public byte[]? Sm24Lsb { get; }

        /// <summary>
        /// Bits per sample: 16 or 24.
        /// </summary>
        public int BitsPerSample { get; }

        /// <summary>
        /// Total number of sample frames in the pool.
        /// </summary>
        public int FrameCount => Smpl.Length;

        /// <summary>
        /// Returns the sample at <paramref name="index"/> as a sign-extended 32-bit integer.
        /// For 16-bit pools this is simply the stored int16.  For 24-bit pools the 24-bit raw
        /// value is reconstructed as <c>(high16 &lt;&lt; 8) | low8</c> and then sign-extended
        /// with the correct <c>(&lt;&lt;8)&gt;&gt;8</c> shift (not the legacy wrong
        /// <c>(&lt;&lt;12)&gt;&gt;12</c>).
        /// </summary>
        public int GetSample(int index) {
            if (Sm24Lsb is null)
                return Smpl[index];

            int hi = (ushort)Smpl[index];
            int lo = Sm24Lsb[index] & 0xFF;
            int raw24 = (hi << 8) | lo;
            return (raw24 << 8) >> 8;
        }
    }
}
