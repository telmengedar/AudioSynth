using System;
using System.Collections.Generic;

namespace Pooshit.AudioSynth.Sequencing.Timeline {

    /// <summary>
    /// A conditionally-dispatched unit for the rhythm-game gate (Phase 3 seam): a real payload, a
    /// substitute payload, and a decision window. Not constructed by Phase 1 BGM playback.
    /// </summary>
    public sealed class GateGroup {

        /// <summary>Creates a <see cref="GateGroup"/>.</summary>
        /// <param name="gateId">the id shared by every <see cref="TimelineEntry.GateId"/> in this group</param>
        /// <param name="realEventIds">event ids dispatched when the policy decides <see cref="GateDecision.Real"/></param>
        /// <param name="substituteEventIds">event ids dispatched when the policy decides <see cref="GateDecision.Substitute"/></param>
        /// <param name="windowStart">earliest sample offset a trigger is accepted</param>
        /// <param name="nominalOnset">the group's authored/quantized onset, in samples</param>
        /// <param name="windowEnd">the decision deadline, in samples</param>
        public GateGroup(int gateId, IReadOnlyList<long> realEventIds, IReadOnlyList<long> substituteEventIds,
            long windowStart, long nominalOnset, long windowEnd) {
            GateId = gateId;
            RealEventIds = realEventIds ?? throw new ArgumentNullException(nameof(realEventIds));
            SubstituteEventIds = substituteEventIds ?? throw new ArgumentNullException(nameof(substituteEventIds));
            WindowStart = windowStart;
            NominalOnset = nominalOnset;
            WindowEnd = windowEnd;
        }

        /// <summary>The id shared by every member <see cref="TimelineEntry.GateId"/>.</summary>
        public int GateId { get; }

        /// <summary>Event ids dispatched when the policy decides <see cref="GateDecision.Real"/>.</summary>
        public IReadOnlyList<long> RealEventIds { get; }

        /// <summary>Event ids dispatched when the policy decides <see cref="GateDecision.Substitute"/>.</summary>
        public IReadOnlyList<long> SubstituteEventIds { get; }

        /// <summary>Earliest sample offset a trigger is accepted.</summary>
        public long WindowStart { get; }

        /// <summary>The group's authored/quantized onset, in samples.</summary>
        public long NominalOnset { get; }

        /// <summary>The decision deadline, in samples.</summary>
        public long WindowEnd { get; }
    }
}
