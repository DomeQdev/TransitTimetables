using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TransitTimetables
{
    // Per-line opt-in marker + the terminus anchor. Added to a line when the player switches its service plan on.
    //
    //   m_Enabled        — this line is managed by the mod: its vehicle count comes from LineFleetPlan and its
    //                      vehicles are evenly spaced by extending the layover at the terminus. Off = plain vanilla.
    //   m_TerminusStop   — the stop the player designated as this line's TIMING POINT: where spacing is regulated and
    //                      where a retiring vehicle finishes its loop before returning to the depot. Entity.Null =
    //                      fall back to the first stop on the route.
    //
    // ---- THE THREE LEGACY FIELDS BELOW ARE DEAD WEIGHT, AND THEY MUST STAY ----
    // m_FirstDeparture / m_PeakInterval / m_OffPeakInterval / m_NightInterval belonged to the old fixed-departure
    // model: the player set a clock time and a headway, and the mod derived the fleet from them. That model is gone
    // (see the note at the top of ScheduleMath) — the player now sets VEHICLE COUNTS in LineFleetPlan and the headway
    // falls out of them.
    //
    // They are NOT removed, for two independent reasons:
    //  1. This is a SHIPPED ISerializable with no version byte. Dropping a field changes the byte layout and every
    //     existing save fails to deserialize this component. There is no safe way to shrink it, ever.
    //  2. They are the ONLY record of what service level an upgraded city was running.
    //     TimetableDispatchSystem.MigrateFleetPlan reads the intervals once, converts each into a vehicle count for
    //     the matching window, and writes a LineFleetPlan — so a player's tuned 8/12/30-minute line comes back as a
    //     line with the vehicle counts that sustain roughly those headways instead of a flat default.
    // After that one read, nothing in the mod consults them again. Do not wire them back into behaviour.
    public struct TimetableSchedule : IComponentData, ISerializable
    {
        public bool m_Enabled;
        public ushort m_FirstDeparture;    // LEGACY (migration only) — see above
        public ushort m_PeakInterval;      // LEGACY (migration only)
        public ushort m_OffPeakInterval;   // LEGACY (migration only)
        public ushort m_NightInterval;     // LEGACY (migration only)
        public Entity m_TerminusStop;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Enabled);
            writer.Write(m_FirstDeparture);
            writer.Write(m_PeakInterval);
            writer.Write(m_OffPeakInterval);
            writer.Write(m_NightInterval);
            writer.Write(m_TerminusStop);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Enabled);
            reader.Read(out m_FirstDeparture);
            reader.Read(out m_PeakInterval);
            reader.Read(out m_OffPeakInterval);
            reader.Read(out m_NightInterval);
            reader.Read(out m_TerminusStop);
        }

        // m_Enabled is false so the component only becomes active when the player explicitly toggles it on. The
        // legacy interval values are still filled in with the old defaults: a line created NOW gets its counts from
        // LineFleetPlan.Default() and never consults them, but leaving them at 0 would make the migration path
        // (which floors a zero interval at 1 minute) produce an absurd count if it ever ran over a fresh component.
        public static TimetableSchedule Default() => new TimetableSchedule
        {
            m_Enabled = false,
            m_FirstDeparture = 300, // 05:00
            m_PeakInterval = 8,
            m_OffPeakInterval = 12,
            m_NightInterval = 30,
        };
    }
}
