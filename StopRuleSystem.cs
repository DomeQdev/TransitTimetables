using System.Collections.Generic;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace TransitTimetables
{
    // Enforces the per-line stop rules (LineStopRule) by editing the PASSENGER PATHFIND GRAPH, which is the only place
    // in this game where "may a cim get on / off here" is actually decided.
    //
    // ============================ HOW BOARDING REALLY WORKS ============================
    // A cim never chooses to board at the kerb. It boards because its PATH says to: ResidentAISystem's
    // TransportStopReached only looks at the next two PathElements and the stop's BoardingVehicle slot. Alighting is
    // the same — RouteUtils.ShouldExitVehicle just compares the path's next lane. So the decision was made much
    // earlier, by the pathfinder, and the pathfinder models each stop as ONE EDGE per line:
    //
    //     [pedestrian side] --(waypoint stop edge)--> [route side]
    //          EdgeFlags.Forward  = pedestrian -> route = BOARDING
    //          EdgeFlags.Backward = route -> pedestrian = ALIGHTING
    //
    // (See Game.Pathfind.PathUtils.GetTransportStopSpecification: Forward is granted only when the stop carries
    // StopFlags.Active — which is exactly why a closed station still lets its passengers off — and Backward only when
    // the waypoint is connected to a real stop rather than being a bare routing waypoint.)
    //
    // Crucially the edge is owned by the WAYPOINT, and a waypoint belongs to exactly one line. So clearing a direction
    // there is per line AND per stop, which is the whole point: a kerb shared by four lines keeps working normally for
    // the other three. Nothing else in the game exposes that granularity — StopFlags.Active is a property of the
    // physical stop and would shut boarding for every line calling at it.
    //
    // WHY NOT HARMONY. The systems that consume all of this (ResidentAISystem.ResidentTickJob, BoardingJob) are
    // [BurstCompile] and the shipped game is Burst-AOT compiled (Cities2_Data/Plugins/x86_64/lib_burst_generated.dll),
    // so a Harmony patch on their managed IL would simply never run. Editing the graph they read is not a workaround
    // for that — it is the correct layer, and it keeps the mod's no-patches property intact.
    // ==================================================================================
    //
    // THE TECHNICAL STOP CUTS THE LINE. Clearing both directions at the stop stops anyone getting on or off, but a
    // passenger who boarded earlier would still simply ride THROUGH it, and the rule is that nobody may be aboard at
    // all. Riding through is the route SEGMENT edge (waypoint -> waypoint, owned by the RouteSegment entity), so the
    // inbound segment's Forward is cleared too. The line is then severed for passengers at that stop: nobody can plan
    // a journey that passes it, so the vehicle provably arrives empty. Only the INBOUND segment needs cutting — with
    // no one aboard on arrival and no one able to board, the outbound segment carries nobody anyway, and cutting one
    // edge instead of two keeps the damage minimal and the reason obvious.
    //
    // Vehicles are unaffected by any of this: they follow RouteSegment PathInformation (a road/track path), not the
    // passenger transit graph. A technical stop is still called at, and TimetableDispatchSystem.ForceStops makes that
    // call mandatory.
    //
    // ---- MECHANISM: TimeAction, not UpdateAction ----
    // Both can rewrite an edge. TimeAction is used because its disable path is provably safe: SetEdgeDirections
    // removes a direction using the edge's OWN stored m_StartID/m_EndID and ignores the nodes we pass; only ENABLING
    // a direction consumes them. So a mistake in our node arithmetic cannot rewire anything — at worst it does
    // nothing. We therefore never enable: the desired state is always vanilla's own baseline MINUS what the rule
    // forbids, and anything that would need a direction back (rule relaxed, rule removed, stop left the route) is
    // handed to the game instead, by tagging the waypoint PathfindUpdated so RoutesModifiedSystem rebuilds the edge
    // from scratch. See ReleaseOwner.
    //
    // ---- WHY IT RE-ASSERTS EVERY TICK ----
    // Vanilla rebuilds a stop edge whenever the line's headway or ticket price changes, whenever the stop's comfort
    // or active state changes, and on every route edit — each of which restores Forward/Backward. Re-applying every
    // tick is what makes the rule stick; it is idempotent (SetEdgeDirections is a no-op when the direction already
    // matches) and costs one small job over a handful of entities. The residual window between a vanilla rebuild and
    // our next tick is ~16 frames, in which a few cims may plan a boarding that the rule would have refused. They are
    // not ejected — they simply complete the trip they planned.
    //
    // ---- SAVE SAFETY ----
    // Nothing here is serialized. The pathfind graph is rebuilt from components on every load (RoutesModifiedSystem
    // switches to its all-elements query when GetLoaded() is true), so a save carries only the LineStopRule buffer.
    // Remove the mod and the next load is plain vanilla with no repair step needed.
    public partial class StopRuleSystem : GameSystemBase
    {
        private PathfindQueueSystem m_PathfindQueue;
        private EntityQuery m_RuleQuery;

        // Edge owner (a waypoint, or a RouteSegment for the technical cut) -> the rule mode we have applied to it.
        // The mode is tracked, not just membership, because a CHANGED rule may need a direction back and we never
        // enable one ourselves — see ReleaseOwner.
        private readonly Dictionary<Entity, byte> m_Applied = new Dictionary<Entity, byte>();
        // Rebuilt from the world each tick, then diffed against m_Applied.
        private readonly Dictionary<Entity, byte> m_Desired = new Dictionary<Entity, byte>();
        // Owners handed back to vanilla THIS tick. They are skipped when the actions are built, so the rebuild we just
        // asked for lands before we restrict the edge again on a later tick.
        private readonly HashSet<Entity> m_Released = new HashSet<Entity>();
        private readonly List<TimeActionData> m_Actions = new List<TimeActionData>();
        private readonly List<Entity> m_Scratch = new List<Entity>();

        // Every 16 frames. Twice the dispatch's interval: nothing here is timing-critical (it only has to beat the
        // player noticing), and each tick is a full walk of every ruled line.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 16;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PathfindQueue = World.GetOrCreateSystemManaged<PathfindQueueSystem>();
            m_RuleQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<TransportLine>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<RouteWaypoint>(),
                    ComponentType.ReadOnly<LineStopRule>(),
                },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() },
            });
            // Deliberately NOT RequireForUpdate(m_RuleQuery), for the same reason the dispatch avoids it: the tick
            // where the LAST rule disappears is precisely the tick that has to hand the edge back to vanilla. With
            // RequireForUpdate the system would stop dead on the now-empty query and the restriction would be frozen
            // into the session. The empty walk is trivial.
        }

        // A city just loaded: the pathfind graph is being rebuilt from scratch, so nothing we applied in the previous
        // city still exists. Forget it all rather than trying to "release" edges that are already gone (or, worse,
        // belong to a different city's entities now).
        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            m_Applied.Clear();
        }

        protected override void OnUpdate()
        {
            m_Desired.Clear();
            m_Actions.Clear();
            m_Released.Clear();

            // MASTER SWITCH OFF => want nothing restricted. Deliberately expressed as an empty m_Desired rather than
            // an early return, so the diff below HANDS EVERY EDGE BACK on the tick the switch flips. Returning early
            // would freeze the current restrictions into the session with no way to reach them: switching the mod off
            // also blanks the stop board, so the buttons that set these rules would be gone while the rules stayed on.
            TransitTimetablesSetting s = Mod.ActiveSetting;
            bool active = s != null && s.Enabled;

            if (active && !m_RuleQuery.IsEmptyIgnoreFilter)
            {
                NativeArray<Entity> lines = m_RuleQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < lines.Length; i++)
                    CollectLine(lines[i]);
                lines.Dispose();
            }

            // (1) Hand back anything we no longer want restricted, or want restricted DIFFERENTLY. Both cases can
            // need a direction re-enabled, which only vanilla is allowed to do here.
            m_Scratch.Clear();
            foreach (KeyValuePair<Entity, byte> kv in m_Applied)
                if (!m_Desired.TryGetValue(kv.Key, out byte want) || want != kv.Value)
                    m_Scratch.Add(kv.Key);
            for (int i = 0; i < m_Scratch.Count; i++)
            {
                ReleaseOwner(m_Scratch[i]);
                m_Applied.Remove(m_Scratch[i]);
                m_Released.Add(m_Scratch[i]);
            }

            // (2) Apply / re-assert everything else.
            foreach (KeyValuePair<Entity, byte> kv in m_Desired)
            {
                if (m_Released.Contains(kv.Key))
                    continue;                       // rebuild pending; restrict it again on a later tick
                if (BuildAction(kv.Key, kv.Value))
                    m_Applied[kv.Key] = kv.Value;
            }

            if (m_Actions.Count == 0)
                return;

            // One TimeAction for the whole city. The queue system takes ownership of the allocation and disposes it
            // once the modification job has run, exactly as it does for vanilla's own callers
            // (e.g. OutsideConnectionDelaySystem), so this must NOT be disposed here.
            TimeAction action = new TimeAction(Allocator.Persistent);
            for (int i = 0; i < m_Actions.Count; i++)
                action.m_TimeData.Enqueue(m_Actions[i]);
            m_PathfindQueue.Enqueue(action, default(JobHandle));
        }

        // Gather one line's desired edge restrictions into m_Desired.
        private void CollectLine(Entity line)
        {
            DynamicBuffer<LineStopRule> rules = EntityManager.GetBuffer<LineStopRule>(line, isReadOnly: true);
            if (rules.Length == 0)
                return;
            // Only while the line's TIMETABLE is on. The rules are set from the stop board, and the board only draws
            // its controls on timetabled rows — so enforcing them on a line whose timetable was switched off would
            // leave a live restriction with no visible switch to undo it. The buffer stays in the save, so switching
            // the timetable back on brings the rules back exactly as they were.
            if (!EntityManager.HasComponent<TimetableSchedule>(line)
                || !EntityManager.GetComponentData<TimetableSchedule>(line).m_Enabled)
                return;
            // Cargo lines have no passengers to restrict, and the stop edge's costs come from passenger pathfind data
            // that a cargo prefab does not carry. Nothing to do.
            PrefabRef prefab = EntityManager.GetComponentData<PrefabRef>(line);
            if (!EntityManager.HasComponent<TransportLineData>(prefab.m_Prefab))
                return;
            TransportLineData lineData = EntityManager.GetComponentData<TransportLineData>(prefab.m_Prefab);
            if (!lineData.m_PassengerTransport)
                return;

            DynamicBuffer<RouteWaypoint> wps = EntityManager.GetBuffer<RouteWaypoint>(line, isReadOnly: true);
            if (wps.Length == 0)
                return;

            // THE TERMINUS IS NOT EXEMPT. An earlier revision skipped it, on the theory that closing the schedule
            // anchor would strangle the line — it does not. Everything the terminus is for survives these rules:
            // FindTerminus resolves it from the stop's BoardingVehicle component (a physical thing, untouched here),
            // the departure hold writes PublicTransport.m_DepartureFrame on whatever vehicle occupies that slot, and
            // a retiring vehicle has no passengers to strand precisely because the rule emptied it. A technical
            // terminus — depot access, a driver change at the end of the run — is a normal thing to want.
            for (int r = 0; r < rules.Length; r++)
            {
                byte mode = rules[r].m_Mode;
                if (mode == LineStopRule.None)
                    continue;
                Entity stop = rules[r].m_Stop;
                // Find this rule's waypoint AND its index in one pass — the index is what locates the inbound segment
                // for a technical stop.
                int k = -1;
                for (int i = 0; i < wps.Length; i++)
                {
                    Entity w = wps[i].m_Waypoint;
                    if (EntityManager.HasComponent<Connected>(w)
                        && EntityManager.GetComponentData<Connected>(w).m_Connected == stop)
                    { k = i; break; }
                }
                if (k < 0)
                    continue;                       // orphaned rule: the stop is no longer on this route

                m_Desired[wps[k].m_Waypoint] = mode;

                if (mode != LineStopRule.Technical)
                    continue;
                // The cut. The segment buffer runs parallel to the waypoint buffer (segments[j] carries
                // waypoints[j] -> waypoints[j+1], wrapping), so the segment arriving at k is the one before it.
                if (!EntityManager.HasBuffer<RouteSegment>(line))
                    continue;
                DynamicBuffer<RouteSegment> segs = EntityManager.GetBuffer<RouteSegment>(line, isReadOnly: true);
                if (segs.Length != wps.Length || wps.Length < 2)
                    continue;                       // mid-edit or a degenerate route: leave the graph alone
                Entity inbound = segs[(k + segs.Length - 1) % segs.Length].m_Segment;
                if (inbound != Entity.Null && EntityManager.Exists(inbound))
                    m_Desired[inbound] = LineStopRule.Technical;
            }
        }

        // Build the TimeActionData that takes one edge to its restricted state. Returns false when the owner has gone
        // or is not shaped the way this expects, so the caller does not record it as applied.
        //
        // The direction bits are ALWAYS vanilla's own baseline minus what the rule forbids — never more. If vanilla
        // would not have granted a direction here (an inactive stop, a bare routing waypoint, a zero-length segment)
        // we must not grant it either, or SetEdgeDirections would add a connection the game never made.
        private bool BuildAction(Entity owner, byte mode)
        {
            if (!EntityManager.Exists(owner))
                return false;

            // ---- the route SEGMENT edge (technical cut): kill Forward, which is the only direction it ever has ----
            if (EntityManager.HasComponent<Game.Routes.Segment>(owner))
            {
                if (!EntityManager.HasComponent<Owner>(owner))
                    return false;
                Entity line = EntityManager.GetComponentData<Owner>(owner).m_Owner;
                if (!EntityManager.HasBuffer<RouteWaypoint>(line))
                    return false;
                DynamicBuffer<RouteWaypoint> wps = EntityManager.GetBuffer<RouteWaypoint>(line, isReadOnly: true);
                int j = EntityManager.GetComponentData<Game.Routes.Segment>(owner).m_Index;
                if (j < 0 || j >= wps.Length)
                    return false;
                int next = (j + 1) % wps.Length;
                TimeActionData seg = default;
                seg.m_Owner = owner;
                seg.m_StartNode = new PathNode(wps[j].m_Waypoint, (ushort)0);
                seg.m_EndNode = new PathNode(wps[next].m_Waypoint, (ushort)0);
                // No EnableForward, no EnableBackward: the segment is closed to passengers in both senses. Its cost is
                // unreachable while it is closed, and vanilla restores the real one when the edge is rebuilt (which is
                // exactly what ReleaseOwner asks for when the rule is lifted).
                seg.m_Flags = TimeActionFlags.SetPrimary;
                seg.m_Time = 0f;
                m_Actions.Add(seg);
                return true;
            }

            // ---- the WAYPOINT stop edge: Forward is boarding, Backward is alighting ----
            if (!EntityManager.HasComponent<Waypoint>(owner) || !EntityManager.HasComponent<Connected>(owner))
                return false;
            Entity stop = EntityManager.GetComponentData<Connected>(owner).m_Connected;
            bool isWaypoint = !EntityManager.HasComponent<Game.Routes.TransportStop>(stop);
            Game.Routes.TransportStop ts = isWaypoint
                ? default
                : EntityManager.GetComponentData<Game.Routes.TransportStop>(stop);

            // Vanilla's baseline, mirroring GetTransportStopSpecification exactly.
            bool canBoard = (ts.m_Flags & StopFlags.Active) != 0;
            bool canAlight = !isWaypoint;
            if (mode == LineStopRule.DropOffOnly || mode == LineStopRule.Technical) canBoard = false;
            if (mode == LineStopRule.PickUpOnly || mode == LineStopRule.Technical) canAlight = false;

            TimeActionData d = default;
            d.m_Owner = owner;
            d.m_StartNode = StopEdgeStartNode(owner);
            d.m_EndNode = new PathNode(owner, (ushort)0);
            d.m_Flags = TimeActionFlags.SetPrimary
                      | (canBoard ? TimeActionFlags.EnableForward : 0)
                      | (canAlight ? TimeActionFlags.EnableBackward : 0);
            d.m_Time = StopEdgeTime(owner, stop, ts, isWaypoint);
            m_Actions.Add(d);
            return true;
        }

        // The pedestrian-side node of a waypoint's stop edge, derived the same way RoutesModifiedSystem derives it.
        // Only ever consumed if a direction were being ENABLED, which this system never does — it is computed anyway
        // so the action is honest about the edge it describes rather than carrying a placeholder.
        private PathNode StopEdgeStartNode(Entity waypoint)
        {
            if (EntityManager.HasComponent<AccessLane>(waypoint))
            {
                AccessLane al = EntityManager.GetComponentData<AccessLane>(waypoint);
                if (EntityManager.HasComponent<Game.Net.Lane>(al.m_Lane))
                    return new PathNode(EntityManager.GetComponentData<Game.Net.Lane>(al.m_Lane).m_MiddleNode, al.m_CurvePos);
                if (EntityManager.HasComponent<Game.Routes.TransportStop>(al.m_Lane))
                    return new PathNode(al.m_Lane, (ushort)2);
            }
            return new PathNode(waypoint, (ushort)2);
        }

        // The stop edge's time cost, reproducing GetTransportStopSpecification's own arithmetic. SetTimeJob always
        // writes this field, so it has to be the real value rather than a placeholder: on a PICK-UP-ONLY stop
        // boarding is still allowed, and this is the wait the pathfinder charges for it. (On the other two modes
        // boarding is closed and the number is unreachable, but there is no reason to compute it differently.)
        private float StopEdgeTime(Entity waypoint, Entity stop, Game.Routes.TransportStop ts, bool isWaypoint)
        {
            Entity line = EntityManager.HasComponent<Owner>(waypoint)
                ? EntityManager.GetComponentData<Owner>(waypoint).m_Owner : Entity.Null;
            if (line == Entity.Null || !EntityManager.HasComponent<TransportLine>(line)
                || !EntityManager.HasComponent<PrefabRef>(line))
                return 0f;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            if (!EntityManager.HasComponent<TransportLineData>(prefab))
                return 0f;
            TransportLineData lineData = EntityManager.GetComponentData<TransportLineData>(prefab);
            if (!EntityManager.HasComponent<PathfindTransportData>(lineData.m_PathfindPrefab))
                return 0f;
            PathfindTransportData pfd = EntityManager.GetComponentData<PathfindTransportData>(lineData.m_PathfindPrefab);
            TransportLine tl = EntityManager.GetComponentData<TransportLine>(line);
            WaitingPassengers wp = EntityManager.HasComponent<WaitingPassengers>(waypoint)
                ? EntityManager.GetComponentData<WaitingPassengers>(waypoint) : default;
            float stopDuration = RouteUtils.GetStopDuration(lineData, ts);
            float wait = math.max(tl.m_VehicleInterval * 0.5f, (int)wp.m_AverageWaitingTime) - stopDuration;
            return math.max(0f, pfd.m_StartingCost.m_Value.x + wait);
        }

        // Give one edge back to the game. Tagging the owner PathfindUpdated puts it in RoutesModifiedSystem's update
        // query, which recomputes the whole specification — flags, directions and costs — from the live components.
        // That is the ONLY route back to a granted direction here: this system never enables one itself, because
        // enabling is the one SetEdgeDirections path that would consume our node arithmetic and could therefore wire
        // an edge to the wrong node.
        private void ReleaseOwner(Entity owner)
        {
            if (owner == Entity.Null || !EntityManager.Exists(owner))
                return;
            if (!EntityManager.HasComponent<PathfindUpdated>(owner))
                EntityManager.AddComponent<PathfindUpdated>(owner);
        }
    }
}
