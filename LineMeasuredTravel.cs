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

        private const byte kVersion = 1;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_LoopEmaFrames);
            writer.Write(m_LoopMinFrames);
            writer.Write(m_LoopSamples);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte _);   // version (reserved for future field growth)
            reader.Read(out m_LoopEmaFrames);
            reader.Read(out m_LoopMinFrames);
            reader.Read(out m_LoopSamples);
        }
    }
}
