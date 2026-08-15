using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TransitTimetables
{
    // Persisted measured travel for a line: the learned REAL loop time, so it survives save/load instead of being
    // re-measured from cold every load. Without this the in-memory measurement dictionaries start empty on every load,
    // so the correction reverts to the density prior for a day or two (the "loses the real times when I close the game"
    // report) and the fleet briefly sizes off the unsettled estimate.
    //
    // A SEPARATE sibling component — never grow the shipped TimetableSchedule/CustomPeakSchedule (adding a field to a
    // shipped ISerializable breaks every save). A leading version byte lets THIS one gain fields later behind a version
    // gate, the same growth path the mod's other components follow.
    //
    // Stores ONLY raw measured spans in sim FRAMES, which are day-length (Realtime/slow-time) INVARIANT — never durUnits,
    // a derived fleet count, or an absolute frame index (those are transient / meaningless after reload, and persisting
    // them would bypass the flood-on-load guard). On load the dispatch system re-derives the fleet from these via the
    // same stability/correction/cap path as a live measurement. Absent or samples==0 => no measurement yet (measure live).
    public struct LineMeasuredTravel : IComponentData, ISerializable
    {
        public float m_LoopEmaFrames;   // EMA of the measured terminus->terminus loop (frames); mirrors m_LineLoopEma
        public float m_LoopMinFrames;   // running MIN loop (frames); mirrors m_LineLoopMin (the true single loop; doubles sit above)
        public ushort m_LoopSamples;    // clamped loop-sample count; mirrors m_LineLoopSamples (>= kMinTrustSamples => trusted on load)
        // v2. MEDIAN of the recent-lap window (frames) - the value the correction uses once a line has enough laps.
        // Persisted so a reload does not drop back to the (lower, conservative) anchor and churn every fleet down and
        // then back up. 0 when the line has not yet earned a median.
        public float m_LoopMedianFrames;
        // v3. EMA of the DEPOT LEAD: frames from a vehicle first appearing on the line to it first reaching the
        // terminus and taking a slot — i.e. how long the drive out of the depot actually takes. The fleet look-ahead
        // spends this to raise the count EARLY, so a vehicle is standing at the terminus when a peak (or the first
        // departure) starts, instead of only then leaving the depot. Persisted for the same reason the loop is: it is
        // learned from real spawns, and a reload would otherwise drop the lead to zero and re-learn it by being late
        // for the next peak. 0 when the line has never been observed spawning one.
        public float m_DepotLeadFrames;

        // v1 -> v2 added m_LoopMedianFrames; v2 -> v3 added m_DepotLeadFrames. Deserialize GATES on the stored byte,
        // so an older save loads with the new fields at 0 and simply re-earns them. This is the growth path the
        // version byte exists for.
        private const byte kVersion = 3;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_LoopEmaFrames);
            writer.Write(m_LoopMinFrames);
            writer.Write(m_LoopSamples);
            writer.Write(m_LoopMedianFrames);
            writer.Write(m_DepotLeadFrames);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out m_LoopEmaFrames);
            reader.Read(out m_LoopMinFrames);
            reader.Read(out m_LoopSamples);
            if (version >= 2) reader.Read(out m_LoopMedianFrames);
            else m_LoopMedianFrames = 0f;   // v1 save: nothing stored, fall back to the anchor until re-earned
            if (version >= 3) reader.Read(out m_DepotLeadFrames);
            else m_DepotLeadFrames = 0f;    // v1/v2 save: no lead learned yet, so the look-ahead is simply off
        }
    }
}
