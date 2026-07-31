using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Pathfind;
using Game.Policies;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.UI.InGame;
using Unity.Collections;
using Unity.Entities;

namespace TransitTimetables
{
    // Fleet-control helper (name kept for compatibility). No longer schedules anything itself — TimetableDispatchSystem
    // owns the timetables and calls the helpers here:
    //   * LineStableDurationUnits(line) — the line's round-trip time in route units, for deriving the fleet.
    //   * TrySetLineFleet(line, n) — set a line's vehicle count to n via the vanilla vehicle-count POLICY (the exact
    //     path the in-game slider uses), so vehicles spawn from depots / retire the same as dragging the slider.
    // The only thing its own OnUpdate does is the optional read-only shared-stop analysis.
    public partial class HourlyFleetSystem : GameSystemBase
    {
        private PrefabSystem m_PrefabSystem;
        private PoliciesUISystem m_Policies;
        private EntityQuery m_ConfigQuery;

        private Entity m_VehicleCountPolicy = Entity.Null;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_Policies = World.GetOrCreateSystemManaged<PoliciesUISystem>();
            m_ConfigQuery = GetEntityQuery(ComponentType.ReadOnly<UITransportConfigurationData>());
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 256;

        protected override void OnUpdate()
        {
            TransitTimetablesSetting s = Mod.ActiveSetting;
            if (s == null)
                return;
            if (m_VehicleCountPolicy == Entity.Null)
                ResolvePolicy();
        }

        private void ResolvePolicy()
        {
            if (m_ConfigQuery.IsEmptyIgnoreFilter)
                return;
            UITransportConfigurationPrefab prefab = m_PrefabSystem.GetSingletonPrefab<UITransportConfigurationPrefab>(m_ConfigQuery);
            if (prefab != null && prefab.m_VehicleCountPolicy != null)
                m_VehicleCountPolicy = m_PrefabSystem.GetEntity(prefab.m_VehicleCountPolicy);
        }

        // Stable line duration in route "units" (segment travel + stop dwell). 0 if not measurable yet.
        public float LineStableDurationUnits(Entity line)
        {
            if (!EntityManager.HasComponent<PrefabRef>(line))
                return 0f;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            if (!EntityManager.HasComponent<TransportLineData>(prefab))
                return 0f;
            return ComputeStableDuration(line, EntityManager.GetComponentData<TransportLineData>(prefab));
        }

        // Set a line's vehicle count to an absolute target by writing the line's OWN VehicleInterval modifier directly
        // — NOT via the shared vehicle-count POLICY. TransportLineSystem derives the count from
        // value = m_DefaultVehicleInterval then RouteUtils.ApplyModifier(VehicleInterval) (value += delta.x;
        // value += value*delta.y). So delta.x = wanted - default makes value == wanted and the count == target,
        // UNCAPPED and PER-LINE. This is the fix for issue #2: the mod no longer widens the shared policy's modifier
        // RANGE, which is what distorted the vanilla "Assigned Vehicles" slider on EVERY line. (That was done by a
        // VehicleLimitSystem of the mod's own, deleted in v0.2.8 — it is not a vanilla system, and nothing reads it now.)
        // The buffer is type-indexed, so pad it exactly the way vanilla's RouteModifierInitializeSystem.AddModifier
        // does. Must be re-asserted every tick by the dispatch: a line edit or route (re)creation rebuilds this buffer
        // from the line's policies (RouteModifierInitializeSystem runs on Created / a policy Modify). True if applied.
        public bool TrySetLineFleet(Entity line, int target)
        {
            if (!EntityManager.HasComponent<PrefabRef>(line) || !EntityManager.HasBuffer<RouteModifier>(line))
                return false;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            if (!EntityManager.HasComponent<TransportLineData>(prefab))
                return false;
            TransportLineData tld = EntityManager.GetComponentData<TransportLineData>(prefab);
            float duration = ComputeStableDuration(line, tld);
            if (duration <= 1f)
                return false;

            DynamicBuffer<RouteModifier> mods = EntityManager.GetBuffer<RouteModifier>(line);
            int idx = (int)RouteModifierType.VehicleInterval;
            while (mods.Length <= idx)
                mods.Add(default(RouteModifier));
            float wantX = TransportLineSystem.CalculateVehicleInterval(duration, target) - tld.m_DefaultVehicleInterval;
            RouteModifier m = mods[idx];
            if (m.m_Delta.x != wantX || m.m_Delta.y != 0f)
            {
                m.m_Delta.x = wantX;
                m.m_Delta.y = 0f;
                mods[idx] = m;
            }
            return true;
        }

        // REMOVED: TryClearLineFleet. It zeroed the VehicleInterval slot AND called SetPolicy(active: false) on the
        // line's vehicle-count policy — and that policy is where the PLAYER's own "Assigned Vehicles" number lives, so
        // every path that used it (timetable off, master switch off, handing counts to another mod) silently destroyed
        // a count the player had set by hand. TryHealLeftoverFleetModifier below does the useful half correctly, so
        // every caller now uses that instead and this had no callers left.
        //
        // One thing it did that the heal does not: deactivate a vehicle-count policy set by a PRE-v0.2.8 version of
        // this mod, which really did drive the shared policy (that VehicleLimitSystem was deleted in v0.2.8). On such
        // an ancient save the heal now preserves that policy rather than clearing it, because nothing distinguishes it
        // from a player's own. That is the intended trade: "off undoes what the mod did, and leaves a hand-set count
        // alone" cannot also mean "guess which counts were really yours".

        // Repair a leftover VehicleInterval RouteModifier this mod wrote DIRECTLY into a line's serialized buffer
        // (TrySetLineFleet, to size the fleet) and never cleaned up when the user removed/disabled the mod. Left
        // behind, that delta makes
        // the game read a far-too-tight vehicle spacing, which collapses the anti-bunching departure hold to ~0
        // (RouteUtils.CalculateDepartureFrame) — the "vehicles leave the stop immediately" residue in issue #7, and it
        // also pins the line's vehicle count. The v0.3.2 unbunching-factor heal never touched this, which is why it
        // didn't fix the report.
        //
        // SAFE by construction: we do NOT blind-zero the slot (the player's manual "Assigned Vehicles" count is ALSO a
        // VehicleInterval modifier, so that would wipe it). Instead we recompute what the slot SHOULD be from the line's
        // OWN active policies — exactly what vanilla RouteModifierInitializeSystem.RefreshRouteModifiers does, restricted
        // to the VehicleInterval slot — and only rewrite when the current value differs. A line with a genuine
        // policy-set count is set to that same value (no-op or corrected TO the player's value); a line with only our
        // orphaned direct write reverts to whatever its policies dictate (automatic if none). Returns true if it
        // rewrote the slot. Idempotent, and a no-op on an undamaged save.
        public bool TryHealLeftoverFleetModifier(Entity line)
        {
            if (!EntityManager.HasBuffer<RouteModifier>(line) || !EntityManager.HasBuffer<Policy>(line))
                return false;
            DynamicBuffer<Policy> policies = EntityManager.GetBuffer<Policy>(line, isReadOnly: true);
            RouteModifier want = default(RouteModifier);
            for (int i = 0; i < policies.Length; i++)
            {
                Policy p = policies[i];
                if ((p.m_Flags & PolicyFlags.Active) == 0 || !EntityManager.HasBuffer<RouteModifierData>(p.m_Policy))
                    continue;
                DynamicBuffer<RouteModifierData> md = EntityManager.GetBuffer<RouteModifierData>(p.m_Policy, isReadOnly: true);
                for (int j = 0; j < md.Length; j++)
                {
                    RouteModifierData d = md[j];
                    if (d.m_Type != RouteModifierType.VehicleInterval)
                        continue;
                    ApplyModifierData(ref want, d, SliderDelta(d, p.m_Adjustment, p.m_Policy));
                }
            }
            DynamicBuffer<RouteModifier> mods = EntityManager.GetBuffer<RouteModifier>(line);
            int idx = (int)RouteModifierType.VehicleInterval;
            RouteModifier cur = mods.Length > idx ? mods[idx] : default(RouteModifier);
            // Epsilon, not exact ==: the stored value was produced by a Burst job and this recompute runs managed, so a
            // healthy (policy-set) line could differ by ~1 ULP. 1e-4 is far below the game's own 1f VehicleInterval
            // change threshold (TransportLineSystem) yet far below any real orphan gap, so this is a true no-op on a
            // healthy save while still catching every orphan.
            if (System.Math.Abs(cur.m_Delta.x - want.m_Delta.x) < 1e-4f && System.Math.Abs(cur.m_Delta.y - want.m_Delta.y) < 1e-4f)
                return false; // already what the line's policies dictate — no orphan to repair
            while (mods.Length <= idx)
                mods.Add(default(RouteModifier));
            mods[idx] = want;
            return true;
        }

        // Inline mirror of RouteModifierInitializeSystem.GetModifierDelta: lerp the modifier's range by the policy
        // slider fraction. Local copy so the heal never depends on that vanilla system's lookups being current-frame.
        private float SliderDelta(RouteModifierData d, float adjustment, Entity policy)
        {
            if (EntityManager.HasComponent<PolicySliderData>(policy))
            {
                PolicySliderData sl = EntityManager.GetComponentData<PolicySliderData>(policy);
                float span = sl.m_Range.max - sl.m_Range.min;
                float f = span == 0f ? 0f : (adjustment - sl.m_Range.min) / span;
                f = f < 0f ? 0f : (f > 1f ? 1f : f);
                return d.m_Range.min + (d.m_Range.max - d.m_Range.min) * f;
            }
            return d.m_Range.min;
        }

        // Inline mirror of RouteModifierInitializeSystem.AddModifierData (accumulate one policy's delta into the slot).
        private static void ApplyModifierData(ref RouteModifier m, RouteModifierData d, float delta)
        {
            switch (d.m_Mode)
            {
                case ModifierValueMode.Relative:
                    m.m_Delta.y = m.m_Delta.y * (1f + delta) + delta;
                    break;
                case ModifierValueMode.Absolute:
                    m.m_Delta.x += delta;
                    break;
                case ModifierValueMode.InverseRelative:
                    delta = 1f / System.Math.Max(0.001f, 1f + delta) - 1f;
                    m.m_Delta.y = m.m_Delta.y * (1f + delta) + delta;
                    break;
            }
        }

        // Stable line duration = sum of segment path durations + stop dwell per timed stop. Mirrors
        // VehicleCountSection.CalculateVehicleCountJob.CalculateStableDuration so our count math matches the game's.
        private float ComputeStableDuration(Entity line, TransportLineData tld)
        {
            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, isReadOnly: true);
            DynamicBuffer<RouteSegment> segments = EntityManager.GetBuffer<RouteSegment>(line, isReadOnly: true);
            int len = waypoints.Length;
            if (len == 0 || segments.Length < len)
                return 0f;

            int start = 0;
            for (int i = 0; i < len; i++)
            {
                if (EntityManager.HasComponent<VehicleTiming>(waypoints[i].m_Waypoint))
                {
                    start = i;
                    break;
                }
            }

            float total = 0f;
            for (int j = 0; j < len; j++)
            {
                int segIdx = start + j;
                int wpIdx = segIdx + 1;
                if (segIdx >= len) segIdx -= len;
                if (wpIdx >= len) wpIdx -= len;
                Entity segment = segments[segIdx].m_Segment;
                Entity waypoint = waypoints[wpIdx].m_Waypoint;
                if (EntityManager.HasComponent<PathInformation>(segment))
                    total += EntityManager.GetComponentData<PathInformation>(segment).m_Duration;
                if (EntityManager.HasComponent<VehicleTiming>(waypoint))
                    total += tld.m_StopDuration;
            }
            return total;
        }
    }
}
