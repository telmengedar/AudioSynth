using System;

namespace Pooshit.AudioSynth.Formats.Sf2 {

    /// <summary>
    /// The raw sample-data pool from the SF2 sdta chunk: either 16-bit (smpl only) or 24-bit
    /// (smpl + sm24 extension).  Sample values are stored in the original little-endian SF2 layout
    /// and are accessed via <see cref="GetSample"/> which applies correct 24-bit sign-extension.
    /// A lazily-built, cached normalized <see cref="FloatPool"/> is shared by all patches from the file.
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

        float[]? floatPool;
        float? loudnessEstimate;

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
        /// Shared cached normalized float pool: one float per frame, values in [−1, 1],
        /// built once on first access via <c>GetSample(i) / 2^(BitsPerSample−1)</c>.
        /// The same array is returned on every call for the lifetime of this instance.
        /// </summary>
        public float[] FloatPool {
            get {
                if (floatPool is null) {
                    float scale = 1f / (1 << (BitsPerSample - 1));
                    float[] built = new float[Smpl.Length];
                    for (int i = 0; i < built.Length; i++)
                        built[i] = GetSample(i) * scale;
                    floatPool = built;
                }
                return floatPool;
            }
        }

        /// <summary>
        /// Block size, in frames, used by <see cref="LoudnessEstimate"/>'s block-RMS analysis
        /// (DiVoid #7257): large enough to average out sample-to-sample noise, small enough that a
        /// long silent intro/tail doesn't dominate a single block's RMS.
        /// </summary>
        const int LoudnessBlockSize = 2048;

        /// <summary>
        /// Silence gate for <see cref="LoudnessEstimate"/> (DiVoid #7257): a block whose RMS is below
        /// this magnitude (~-50 dBFS) is excluded from the aggregate so silent intros/tails/decays
        /// don't deflate the estimate.
        /// </summary>
        const float LoudnessSilenceThreshold = 0.00316f;

        /// <summary>
        /// Lazily-built, cached, silence-gated, outlier-robust loudness estimate of this pool's
        /// <see cref="FloatPool"/> (DiVoid #7254/#7257): the pool is split into fixed-size blocks of
        /// <see cref="LoudnessBlockSize"/> frames, each block's RMS is computed, blocks whose RMS falls
        /// below <see cref="LoudnessSilenceThreshold"/> are gated out (so silent tails/intros can't
        /// deflate the result), and the aggregate is the <em>median</em> of the surviving blocks' RMS
        /// values — not a plain global RMS — so a single hot one-shot block can't dominate it. If every
        /// block is gated out (an all-silence or near-silence pool) this is <c>0f</c>, never NaN/Inf and
        /// never a spuriously low-but-nonzero value that could imply a huge calibration gain: a caller
        /// deriving <c>gain = min(1, reference/estimate)</c> must treat 0f as "unmeasured" and clamp to
        /// 1.0, never boost. Pure and deterministic: the same pool always yields the same value, and the
        /// value is cached after first access exactly like <see cref="FloatPool"/>.
        /// </summary>
        public float LoudnessEstimate {
            get {
                if (loudnessEstimate is null)
                    loudnessEstimate = ComputeLoudnessEstimate(FloatPool);
                return loudnessEstimate.Value;
            }
        }

        static float ComputeLoudnessEstimate(float[] pool) {
            if (pool.Length == 0)
                return 0f;

            int blockCount = (pool.Length + LoudnessBlockSize - 1) / LoudnessBlockSize;
            float[] blockRms = new float[blockCount];
            int survivingCount = 0;

            for (int b = 0; b < blockCount; b++) {
                int start = b * LoudnessBlockSize;
                int length = Math.Min(LoudnessBlockSize, pool.Length - start);

                double sumSquares = 0.0;
                for (int i = 0; i < length; i++) {
                    float s = pool[start + i];
                    sumSquares += (double)s * s;
                }
                float rms = (float)Math.Sqrt(sumSquares / length);

                if (rms >= LoudnessSilenceThreshold)
                    blockRms[survivingCount++] = rms;
            }

            if (survivingCount == 0)
                return 0f;

            float[] surviving = new float[survivingCount];
            Array.Copy(blockRms, surviving, survivingCount);
            Array.Sort(surviving);

            int mid = survivingCount / 2;
            return (survivingCount % 2 == 0)
                ? (surviving[mid - 1] + surviving[mid]) / 2f
                : surviving[mid];
        }

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
