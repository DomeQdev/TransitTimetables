using Colossal.Serialization.Entities;
using Unity.Entities;

namespace TransitTimetables
{
    // A SECOND TIMING POINT ("Terminus B") at one player-chosen mid-route stop.
    //
    // The mod evens a line's vehicles out by extending the layover where they turn round. On a there-and-back line
    // that happens at BOTH ends, and regulating only at A lets the outbound direction stay tidy while the return
    // direction bunches for a whole half-loop. Naming a stop as Terminus B makes the dispatch apply the SAME rule
    // there: a vehicle leaves it one headway after the previous vehicle left it, or after m_MinDwellMinutes of
    // boarding, whichever is later.
    //
    // m_MinDwellMinutes is the FLOOR, not the wait. An on-time vehicle at B waits the floor and no more; a vehicle
    // that arrives bunched up behind the one in front waits until the headway is restored; a vehicle arriving into a
    // gap leaves as soon as it has boarded. That is what a real timing point does — it absorbs delay rather than
    // compounding it — and it is why this is not a fixed "hold for X minutes" any more.
    //
    // It is deliberately NOT a second departure grid. One closed loop with a fixed vehicle set must depart both ends
    // at the same rate or vehicles pile up at one end without bound, so B is driven by the SAME headway A is; only
    // the phase differs, and the phase looks after itself.
    //
    // The stop stays an ORDINARY intermediate stop to the rest of the mod — never isTerminus — which is what keeps
    // the wait OUT of the measured loop (the m_VehStopHold banking subtracts it) while still letting the cycle math
    // ADD it: the round trip genuinely is m_MinDwellMinutes longer, and the headway is derived from the cycle.
    //
    // A SEPARATE sibling component — never grow the shipped TimetableSchedule (adding a field to a shipped
    // ISerializable breaks every save). Leading version byte so THIS one can gain fields later behind a version gate,
    // the same growth path LineMeasuredTravel / CustomPeakSchedule / LineFleetPlan follow. Lives on the LINE entity,
    // not the stop: a stop's boarding slot is shared across lines, and the timing point is one line's choice.
    //
    // FIELD NAME NOTE: the serialized field is still m_HoldMinutes because renaming it is a save-format change for no
    // benefit. Its MEANING changed from "wait exactly this long past the scheduled arrival" to "wait at least this
    // long", which is why the accessor below is the name the rest of the mod uses.
    public struct LineLayover : IComponentData, ISerializable
    {
        public Entity m_Stop;        // the chosen second timing point (a stop entity, like TimetableSchedule.m_TerminusStop)
        public ushort m_HoldMinutes; // MINIMUM minutes of layover at that stop; 0 means "no Terminus B" (component removed)

        // The live meaning of m_HoldMinutes, so call sites read as what they do.
        public int MinDwellMinutes => m_HoldMinutes;

        private const byte kVersion = 1;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_Stop);
            writer.Write(m_HoldMinutes);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out m_Stop);
            reader.Read(out m_HoldMinutes);
        }
    }
}
