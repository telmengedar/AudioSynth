using System;

namespace Pooshit.AudioSynth.Synthesis {

    /// <summary>
    /// A single sounding note in flight; renders its own mono block that the engine mixes and pans.
    /// </summary>
    public interface IVoice {

        /// <summary>
        /// True while the voice is producing sound, including its release tail.
        /// </summary>
        bool IsActive { get; }

        /// <summary>
        /// Renders one mono block and returns the sample count produced; zero once finished.
        /// </summary>
        int RenderBlock(Span<float> block);

        /// <summary>
        /// Enters the release phase on note-off.
        /// </summary>
        void Release();
    }
}
