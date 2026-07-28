using System;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// The send-return effect contract shared by every effect stage in the engine's block loop
    /// (<see cref="Reverb"/>, <see cref="Chorus"/>): computes a wet signal from a caller-supplied send
    /// bus and adds it into a caller-supplied master bus, leaving the dry signal already carried in
    /// master untouched. Implementations are allocation-free in <see cref="Process"/> (all working
    /// buffers are sized at construction) and alias-safe (<c>send</c> may be the same span as
    /// <c>master</c> — the uniform "every voice sends fully" master-insert case — so implementations
    /// must read each frame's send values into locals before writing that frame's master values). This
    /// interface names the shared contract only; routing (send-bus construction,
    /// per-channel weighting, CC/generator sourcing) remains each effect's own concern — there is no
    /// generic effect pipeline or registry.
    /// </summary>
    public interface IAudioEffect {

        /// <summary>
        /// Computes wet from <paramref name="send"/> and adds it into <paramref name="master"/> in
        /// place; dry is never added since <paramref name="master"/> already carries it.
        /// </summary>
        /// <param name="send">interleaved stereo send samples that feed the effect; length must equal <paramref name="master"/>'s and be a multiple of 2</param>
        /// <param name="master">interleaved stereo master samples that the wet signal is added into</param>
        void Process(ReadOnlySpan<float> send, Span<float> master);
    }
}
