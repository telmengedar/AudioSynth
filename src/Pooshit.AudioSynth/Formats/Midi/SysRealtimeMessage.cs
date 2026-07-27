namespace Pooshit.AudioSynth.Formats.Midi {

    /// <summary>
    /// A system realtime message. Every value is one of the fixed singletons below;
    /// not interpreted in increment 1.
    /// </summary>
    public sealed class SysRealtimeMessage : ShortMessage {

        /// <summary>The timing clock message.</summary>
        public static readonly SysRealtimeMessage ClockMessage = new SysRealtimeMessage(SysRealtimeType.Clock);

        /// <summary>The tick message.</summary>
        public static readonly SysRealtimeMessage TickMessage = new SysRealtimeMessage(SysRealtimeType.Tick);

        /// <summary>The start message.</summary>
        public static readonly SysRealtimeMessage StartMessage = new SysRealtimeMessage(SysRealtimeType.Start);

        /// <summary>The continue message.</summary>
        public static readonly SysRealtimeMessage ContinueMessage = new SysRealtimeMessage(SysRealtimeType.Continue);

        /// <summary>The stop message.</summary>
        public static readonly SysRealtimeMessage StopMessage = new SysRealtimeMessage(SysRealtimeType.Stop);

        /// <summary>The active sensing message.</summary>
        public static readonly SysRealtimeMessage ActiveSenseMessage = new SysRealtimeMessage(SysRealtimeType.ActiveSense);

        /// <summary>The system reset message.</summary>
        public static readonly SysRealtimeMessage ResetMessage = new SysRealtimeMessage(SysRealtimeType.Reset);

        SysRealtimeMessage(SysRealtimeType type) : base((byte)type, 0, 0) {
        }

        /// <summary>
        /// The system realtime sub-type carried in the status byte.
        /// </summary>
        public SysRealtimeType SysRealtimeType => (SysRealtimeType)Status;

        /// <inheritdoc/>
        public override MessageType MessageType => MessageType.SystemRealtime;
    }
}
