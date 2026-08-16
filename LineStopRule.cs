using Colossal.Serialization.Entities;
using Game.Routes;
using Unity.Entities;

namespace TransitTimetables
{
    // A per-line BOARDING RULE for one stop: may passengers get on here, get off here, both, or neither.
    //
    //   DropOffOnly   alighting only — you may leave the vehicle or stay on it, but you may not join here
    //   PickUpOnly    boarding only  — you may join here or stay on, but you may not leave here
    //   Technical     an operational call — nobody boards, nobody alights, and NOBODY MAY BE ABOARD AT ALL:
    //                 the last chance to get off is the previous stop. The vehicle still calls, always.
    //
    // Any stop may carry any rule, INCLUDING THE TERMINUS. That is deliberate: a terminus is exactly where a real
    // operator puts a technical call (depot access, driver change), and nothing in the spacing depends on the terminus
    // being open to passengers — the departure clock, the hold and the retirement point are all resolved from the
    // physical stop's BoardingVehicle slot, which these rules never touch.
    //
    // A BUFFER on the LINE entity, keyed by STOP entity — the same shape (and the same reasons) as LineLayover:
    // a physical stop's boarding slot is shared between every line that calls there, so "no boarding" has to be one
    // line's choice about its own call, not a property of the kerb. Storing the STOP and resolving it to this line's
    // waypoint at runtime (StopRules.WaypointForStop) is what makes the setting survive route edits — waypoint
    // entities are destroyed and rebuilt when the player edits a line, a stop entity is not.
    //
    // Leading version byte per element, the same growth path every other component in this mod follows.
    //
    // NOTHING HERE LEAKS INTO THE PATHFIND GRAPH ACROSS A LOAD. The graph is rebuilt from scratch on every load
    // (RoutesModifiedSystem takes its all-elements query when GetLoaded() is true), so the restriction lives only for
    // as long as StopRuleSystem keeps re-applying it. Remove the mod and the very next load is plain vanilla, with
    // only this buffer left in the save — which CleanUninstall strips.
    public struct LineStopRule : IBufferElementData, ISerializable
    {
        public Entity m_Stop;
        public byte m_Mode;

        public const byte None = 0;
        public const byte DropOffOnly = 1;
        public const byte PickUpOnly = 2;
        public const byte Technical = 3;

        private const byte kVersion = 1;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(kVersion);
            writer.Write(m_Stop);
            writer.Write(m_Mode);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out byte version);
            reader.Read(out m_Stop);
            reader.Read(out m_Mode);
        }
    }

    // Shared read/write helpers for the rule buffer. The dispatch (forced calls), the UI (board badges + buttons) and
    // StopRuleSystem (the pathfind edges) all have to agree on what a line's rules ARE, so they all come through here
    // rather than each walking the buffer their own way — the layover's board-vs-dispatch drift is the bug this avoids.
    public static class StopRules
    {
        // The rule this line applies at this stop. None when there is no buffer, no entry, or the entry is None.
        public static byte ModeForStop(EntityManager em, Entity line, Entity stop)
        {
            if (stop == Entity.Null || line == Entity.Null || !em.HasBuffer<LineStopRule>(line))
                return LineStopRule.None;
            DynamicBuffer<LineStopRule> rules = em.GetBuffer<LineStopRule>(line, isReadOnly: true);
            for (int i = 0; i < rules.Length; i++)
                if (rules[i].m_Stop == stop) return rules[i].m_Mode;
            return LineStopRule.None;
        }

        // Same, addressed by this line's waypoint — the form the dispatch has to hand (a vehicle's Target is a
        // waypoint, never a stop).
        public static byte ModeForWaypoint(EntityManager em, Entity line, Entity waypoint)
        {
            if (waypoint == Entity.Null || !em.HasComponent<Connected>(waypoint))
                return LineStopRule.None;
            return ModeForStop(em, line, em.GetComponentData<Connected>(waypoint).m_Connected);
        }

        // This line's waypoint at a given stop, or Entity.Null when the stop is no longer on the route (an ORPHANED
        // rule — see the line panel's warning, which is the only place such a rule can still be reached).
        public static Entity WaypointForStop(EntityManager em, Entity line, Entity stop)
        {
            if (stop == Entity.Null || !em.HasBuffer<RouteWaypoint>(line))
                return Entity.Null;
            DynamicBuffer<RouteWaypoint> wps = em.GetBuffer<RouteWaypoint>(line, isReadOnly: true);
            for (int i = 0; i < wps.Length; i++)
            {
                Entity wp = wps[i].m_Waypoint;
                if (em.HasComponent<Connected>(wp) && em.GetComponentData<Connected>(wp).m_Connected == stop)
                    return wp;
            }
            return Entity.Null;
        }

        // Set (or clear) one line's rule at one stop. Clearing REMOVES the entry rather than storing None, and an
        // emptied buffer is removed outright, so an unused rule leaves no trace in the save and "has a buffer" never
        // has to mean anything more subtle than "has at least one rule". Structural changes only ever happen here.
        public static void SetMode(EntityManager em, Entity line, Entity stop, byte mode)
        {
            if (line == Entity.Null || stop == Entity.Null)
                return;
            if (!em.HasBuffer<LineStopRule>(line))
            {
                if (mode == LineStopRule.None)
                    return;                                  // nothing set, nothing to clear
                em.AddBuffer<LineStopRule>(line);
            }
            DynamicBuffer<LineStopRule> rules = em.GetBuffer<LineStopRule>(line);
            for (int i = 0; i < rules.Length; i++)
            {
                if (rules[i].m_Stop != stop)
                    continue;
                if (mode == LineStopRule.None) rules.RemoveAt(i);
                else rules[i] = new LineStopRule { m_Stop = stop, m_Mode = mode };
                if (rules.Length == 0) em.RemoveComponent<LineStopRule>(line);
                return;
            }
            if (mode != LineStopRule.None)
                rules.Add(new LineStopRule { m_Stop = stop, m_Mode = mode });
            else if (rules.Length == 0)
                em.RemoveComponent<LineStopRule>(line);
        }

        // Drop every rule whose stop this line no longer serves. Returns how many went. The line panel's "remove"
        // button for orphaned rules; also the only way to reach one, since the stop board can only offer controls on
        // a row it still lists and an orphaned rule's stop no longer produces a row.
        public static int ClearOrphans(EntityManager em, Entity line)
        {
            if (line == Entity.Null || !em.HasBuffer<LineStopRule>(line))
                return 0;
            DynamicBuffer<LineStopRule> rules = em.GetBuffer<LineStopRule>(line);
            int removed = 0;
            for (int i = rules.Length - 1; i >= 0; i--)
            {
                Entity stop = rules[i].m_Stop;
                if (stop != Entity.Null && em.Exists(stop) && WaypointForStop(em, line, stop) != Entity.Null)
                    continue;
                rules.RemoveAt(i);
                removed++;
            }
            if (removed > 0 && rules.Length == 0)
                em.RemoveComponent<LineStopRule>(line);
            return removed;
        }

        // How many of this line's rules are orphaned (stop deleted, or edited off the route). Read-only counterpart of
        // ClearOrphans, for the line panel's warning.
        public static int CountOrphans(EntityManager em, Entity line)
        {
            if (line == Entity.Null || !em.HasBuffer<LineStopRule>(line))
                return 0;
            DynamicBuffer<LineStopRule> rules = em.GetBuffer<LineStopRule>(line, isReadOnly: true);
            int n = 0;
            for (int i = 0; i < rules.Length; i++)
            {
                Entity stop = rules[i].m_Stop;
                if (stop == Entity.Null || !em.Exists(stop) || WaypointForStop(em, line, stop) == Entity.Null)
                    n++;
            }
            return n;
        }
    }
}
