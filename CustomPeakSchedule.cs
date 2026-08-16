using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TransitTimetables
{
    // Per-line peak-time override (community contribution, PR #5). Stored as a SEPARATE component rather than fields on
    // TimetableSchedule so it can't break existing save games — ECS deserialization safely ignores a component that is
    // missing on an old save. When m_Enabled is true, these two local windows replace the GLOBAL peak windows for THIS
    // line only, and inside them the line runs LineFleetPlan.m_CustomPeakVehicles.
    //
    // m_Interval is LEGACY and no longer read at runtime. It held the line's custom-peak HEADWAY back when the player
    // set headways; the vehicle count that replaced it lives on LineFleetPlan, because that is the component the
    // redesign added and this one cannot grow a field without a version gate it never had for its own layout. It is
    // still SERIALIZED (removing a field from a shipped ISerializable breaks every save) and is read exactly once, by
    // TimetableDispatchSystem.MigrateFleetPlan, to convert an upgraded line's custom peak into a vehicle count.
    //
    // A version byte is written FIRST (and read/discarded first) so this component can gain fields later WITHOUT breaking
    // saves that already contain it — the same sibling-component + version-byte growth path the mod's other components
    // must follow (you can never add a field to a shipped ISerializable without a version gate).
    public struct CustomPeakSchedule : IComponentData, ISerializable
    {
        public bool m_Enabled;
        public ushort m_Interval;   // LEGACY (migration only) — see above
        public ushort m_Start1;
        public ushort m_End1;
        public ushort m_Start2;
        public ushort m_End2;

        private const byte kVersion = 1;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_Enabled);
            writer.Write(m_Interval);
            writer.Write(m_Start1);
            writer.Write(m_End1);
            writer.Write(m_Start2);
            writer.Write(m_End2);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte _);   // version (reserved for future field growth)
            reader.Read(out m_Enabled);
            reader.Read(out m_Interval);
            reader.Read(out m_Start1);
            reader.Read(out m_End1);
            reader.Read(out m_Start2);
            reader.Read(out m_End2);
        }

        public static CustomPeakSchedule Default() => new CustomPeakSchedule
        {
            m_Enabled = false,
            m_Interval = 5,
            m_Start1 = 7,
            m_End1 = 9,
            m_Start2 = 16,
            m_End2 = 18,
        };
    }
}
