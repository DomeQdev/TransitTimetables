using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TransitTimetables
{
    // THE SERVICE PLAN of a line, in the unit the player actually controls: HOW MANY VEHICLES run on it in each
    // time-of-day window. This replaced the old headway model (m_PeakInterval / m_OffPeakInterval / m_NightInterval on
    // TimetableSchedule), and the direction of the whole mod reversed with it:
    //
    //   before   the player set a headway, the mod DERIVED a vehicle count (ceil(loop / headway)) and then tried to
    //            hold every vehicle to an absolute clock grid. Every number in that chain came from the measured loop,
    //            so a noisy measurement moved the fleet, the posted times and the holds all at once.
    //   now      the player sets the FLEET. The headway is whatever that fleet plus the route produce
    //            (headway = cycle / vehicles), and the mod's only job is to keep those vehicles EVENLY SPACED by
    //            extending the layover at the terminus. Nothing is posted to a clock, so nothing can be "late".
    //
    // A SEPARATE sibling component — never grow the shipped TimetableSchedule (adding a field to a shipped
    // ISerializable breaks every save). Leading version byte so THIS one can gain fields later behind a version gate,
    // the same growth path LineMeasuredTravel / CustomPeakSchedule / LineLayover follow.
    //
    // ABSENT means "not migrated yet", not "zero vehicles": a line saved by an older version carries a TimetableSchedule
    // with intervals and no plan at all, and TimetableDispatchSystem.MigrateFleetPlan converts those intervals into
    // counts the first time it sees such a line. That is why the dispatch never treats a missing component as a plan.
    public struct LineFleetPlan : IComponentData, ISerializable
    {
        public ushort m_PeakVehicles;        // vehicles during the global peak windows
        public ushort m_OffPeakVehicles;     // ...outside peak and night
        public ushort m_NightVehicles;       // ...inside the global night window
        public ushort m_CustomPeakVehicles;  // ...inside this line's own peak windows, when CustomPeakSchedule is on

        private const byte kVersion = 1;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_PeakVehicles);
            writer.Write(m_OffPeakVehicles);
            writer.Write(m_NightVehicles);
            writer.Write(m_CustomPeakVehicles);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte _);   // version (reserved for future field growth)
            reader.Read(out m_PeakVehicles);
            reader.Read(out m_OffPeakVehicles);
            reader.Read(out m_NightVehicles);
            reader.Read(out m_CustomPeakVehicles);
        }

        // Only ever used for a line the player creates fresh in the panel; an UPGRADED line gets its counts from
        // MigrateFleetPlan instead, so its old headways carry over rather than being replaced by these.
        public static LineFleetPlan Default() => new LineFleetPlan
        {
            m_PeakVehicles = 6,
            m_OffPeakVehicles = 4,
            m_NightVehicles = 2,
            m_CustomPeakVehicles = 8,
        };
    }
}
