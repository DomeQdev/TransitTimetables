using System.Collections.Generic;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Pathfind;
using Game.Routes;
using Game.Simulation;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using PublicTransport = Game.Vehicles.PublicTransport;

namespace TransitTimetables
{
    // T2 — fixed-departure timetabling for opted-in lines. Owns three things per timetabled line:
    //
    //  1. FLEET: derived from the current window's headway (round-trip / interval) and applied via the vanilla
    //     vehicle-count policy (HourlyFleetSystem.TrySetLineFleet). The player sets departures, the fleet follows.
    //  2. NO MID-ROUTE IDLE: the schedule itself supplies the spacing — the hold below OVERWRITES m_DepartureFrame
    //     every tick, so vanilla's unbunching delay (which only feeds RouteUtils.CalculateDepartureFrame) can never
    //     apply to a line we're holding. TransportLine.m_UnbunchingFactor is deliberately left at the prefab default
    //     and never written: it is serialized, nothing in vanilla restores it, and no UI exposes it, so writing it
    //     would outlive the mod. Earlier versions zeroed it; (2) in OnUpdate now heals that.
    //  3. STOP HOLD (the timing point): each stop's boarding vehicle is held to that stop's next scheduled clock
    //     departure if it is EARLY (writes PublicTransport.m_DepartureFrame); if on-time or late it departs
    //     immediately. Only this line's own vehicles are ever touched (a shared stop's single boarding slot may hold
    //     another line's bus), and a hold can never exceed one headway (see the clamp in HoldStop).
    //
    // Runs every 8 frames so it always re-asserts the hold before the vanilla 16-frame AI release.
    //
    // ============================ DESIGN DECISIONS — deliberate, NOT bugs ============================
    // Both of these look like defects to anyone (or any audit) reading them cold. One already WAS mistaken for a bug
    // and "fixed", which silently broke the timetable. Read this before changing either.
    //
    //  A. A VEHICLE DEPARTS ON ITS POSTED MINUTE AND NEVER WAITS FOR ANYONE. Once the scheduled minute arrives it
    //     leaves — over a cim walking up, over a passenger still boarding. Waiting would drag the line off schedule,
    //     and staying on schedule is the entire product. Stragglers take the next slot, exactly as with a real
    //     printed timetable. Implemented by the frame-1800 cutoff in HoldStop's GO branch (see the note there).
    //
    //  B. SURPLUS VEHICLES FINISH THEIR LOOP — THEY NEVER ABANDON MID-ROUTE. When the headway widens (peak ->
    //     off-peak) vanilla flags the highest-ODOMETER vehicles and would retire each one wherever it stands,
    //     dumping its passengers. Block (3b) strips that flag back off every tick for any vehicle not on its final
    //     approach, so it keeps serving; it may only go once it is back at the terminus, has completed a full
    //     serving lap (m_LapServed), and another vehicle is covering this slot's departure. That is what stops the
    //     deploy-then-instantly-recall yo-yo and the run of dead departure slots.
    // ===============================================================================================
    public partial class TimetableDispatchSystem : GameSystemBase
    {
        private SimulationSystem m_Sim;
        private TimeSystem m_Time;
        private HourlyFleetSystem m_Fleet;
        private TimebaseSystem m_Timebase;
        // Per-tick snapshot of the runtime frame<->minute scale (from TimebaseSystem): frames per in-game minute and
        // in-game minutes per route "duration unit". Snapshotted once at the top of OnUpdate so all math in a tick uses
        // one consistent value; the HoldAllStops/HoldStop helpers read these fields directly (no signature churn).
        private float m_Fpm;
        private float m_Um;
        // Last day-length "regime" we saw. When TimebaseSystem's generation changes (a real day-length change, e.g. a
        // slow-time mod toggled), the per-vehicle slots were scaled by the OLD frames/minute, so drop them and let each
        // bus re-derive its slot against the new scale on its next terminus visit.
        private uint m_TimebaseGen;
        private EntityQuery m_LineQuery;
        // ALL transport lines (timetabled or not), for the one-time-per-load unbunching-residue repair. Separate from
        // m_LineQuery because that one requires TimetableSchedule and so misses lines that were damaged by an old
        // version and are no longer timetabled. See GlobalHealUnbunching.
        private EntityQuery m_HealQuery;
        // Set on every game/save load; the next OnUpdate runs the global unbunching heal once, then clears it. Init true
        // so the sweep still fires if the system is created AFTER OnGameLoadingComplete already ran (mod-loads-late).
        private bool m_GlobalHealPending = true;
        // Set by the Options "clean uninstall" button (UI thread); consumed once on the next simulation tick to strip
        // every mod component + mutation from the save so the player can remove the mod with no residue. volatile:
        // written on the UI thread, read on the sim thread.
        private static volatile bool s_cleanUninstallPending;
        public static void RequestCleanUninstall() => s_cleanUninstallPending = true;
        private readonly Dictionary<Entity, int> m_LastFleet = new Dictionary<Entity, int>();
        // Flood-on-load guard. The raw line-duration estimate (m_Fleet.LineStableDurationUnits) is transiently inflated
        // for the first ticks after a save loads: the game re-paths the line and zeroes each line's transport speed at
        // the top of every tick before re-filling it, and a segment path's duration scales inversely with speed, so the
        // estimate spikes (users saw ~10x) then settles. Sizing the fleet off that spike floods the city with vehicles.
        // m_LastDur/m_DurStable require the estimate to agree with the previous tick (within 5%) for kDurStableTicks
        // consecutive ticks before we act on it, so the unsettled spike is skipped and only a stable value ever reaches
        // TrySetLineFleet. Purely transient (rebuilt from live reads; nothing here is serialized).
        private readonly Dictionary<Entity, float> m_LastDur = new Dictionary<Entity, float>();
        private readonly Dictionary<Entity, int> m_DurStable = new Dictionary<Entity, int>();
        private const int kDurStableTicks = 3;
        // Per line: vehicles the game flagged to retire that we're driving to the terminus before letting them go.
        private readonly Dictionary<Entity, HashSet<Entity>> m_PendingRetire = new Dictionary<Entity, HashSet<Entity>>();
        // Per line: buses seen AWAY from the terminus (serving the loop) since they appeared — i.e. that have earned
        // a full loop and may now retire on their next return. A freshly-deployed bus is absent from this set until
        // it leaves the terminus, so it always completes one serving lap before it can be recalled.
        private readonly Dictionary<Entity, HashSet<Entity>> m_LapServed = new Dictionary<Entity, HashSet<Entity>>();
        // Vehicles whose retirement we have COMMITTED to (flag asserted at the terminus with the slot covered). Lets the
        // decision survive the brief ticks where no bus is boarding the terminus, without falling back to the old
        // "any vanilla-flagged bus may retire" rule that bypassed the slot-covered check entirely. Keyed by vehicle;
        // pruned against the live-vehicle set each tick. Transient — nothing here is serialized.
        private readonly HashSet<Entity> m_Committed = new HashSet<Entity>();
        // MISSED-TRIP CATCH-UP: per LINE, the sim FRAME of the most recent scheduled slot a bus was assigned/dispatched
        // on ("claimed"). When a bus reaches the terminus and a scheduled slot has since passed UNCOVERED (its frame is
        // newer than this) it is dispatched IMMEDIATELY to fill the gap instead of idling to the next slot — provided
        // that next slot is more than kCatchUpMinNextLeadFrac of a headway away, so the catch-up bus can't bunch with
        // the imminent on-time one. Recording the claim on every (re)assignment stops a second bus re-covering the same
        // slot. A monotonic frame (midnight-safe); transient (cleared on load/regime change, pruned per tick).
        private readonly Dictionary<Entity, uint> m_LastSlotFrame = new Dictionary<Entity, uint>();
        // The one live-tunable knob (see the risk analysis): only catch up when the next slot is at least this fraction
        // of a headway away. Lower = more aggressive gap-filling (risks bunching); higher = leaves more gaps uncovered.
        // Raised from 0.5 for an UNTESTABLE ship. A catch-up departure is deliberately OFF the posted grid, and the board
        // (TransitParamsUISystem) knows nothing about it — so every catch-up is a departure the printed timetable does
        // not show. At 0.5 that could become routine on an under-provisioned line (the default config), which would read
        // as "the mod broke my timetable". At 0.65 it only fires when it recovers at least ~two thirds of a headway,
        // i.e. when the gap it fills clearly outweighs the off-grid departure. Lower = fills more gaps but drifts off the
        // board; higher = stays on the board but leaves gaps unserved.
        private const float kCatchUpMinNextLeadFrac = 0.65f;
        private uint m_LastCatchUpLog; // throttle for the catch-up log
        // Reused scratch for pruning the above dicts against the live query (a line bulldozed while enabled leaves the
        // query without hitting the disable branch, so its keys would otherwise leak). Members = no per-update alloc.
        private readonly HashSet<Entity> m_LiveScratch = new HashSet<Entity>();
        private readonly List<Entity> m_StaleScratch = new List<Entity>();
        private uint m_LastLog;
        // Throttle for the hold-clamp warning (see HoldStop): it fires per stop per 8-frame tick, so rate-limit it to
        // the [SelfTest] cadence — one WARN is a signal, one every 8 frames is noise.
        private uint m_LastClampWarn;

        // PER-VEHICLE SLOT (issue #4): the sim FRAME at which each vehicle is scheduled to depart the TERMINUS on its
        // current run. Holding a bus to ITS slot (shifted by each stop's offset) — rather than to "the next slot after
        // now" — means a bus that falls slightly behind rides its own slot LATE instead of being bumped to the next
        // cycle and stranded for a whole interval. A frame (not a minute) so comparisons are monotonic across midnight.
        // Keyed by vehicle Entity (globally unique); pruned each tick against m_LiveVehScratch so despawned buses drop.
        private readonly Dictionary<Entity, uint> m_RunSlotFrame = new Dictionary<Entity, uint>();
        private readonly HashSet<Entity> m_LiveVehScratch = new HashSet<Entity>();

        // Minimum stop dwell (minutes) for a bus that arrives ON its slot or LATE, so it still boards/offloads instead
        // of being force-departed the instant it pulls in. Early buses are unaffected (they board during their hold).
        // Vanilla's own boarding grace: StopBoarding gives up and departs when frame >= m_DepartureFrame + this
        // (TransportCarAISystem:1262, and byte-identical in the Train :1068, Watercraft :804 and Aircraft :833
        // systems, so one constant covers every transport type). At the vanilla clock this is ~9.9 in-game minutes.
        // We do not fight this window, we ANCHOR it — see HoldStop's GO branch — so the player's "maximum stop time"
        // becomes the moment it expires. It is therefore also the hard ceiling on that setting.
        private const uint kVanillaBoardingGraceFrames = 1800u;
        // Minutes a catch-up dispatch is placed into the future, purely so the bus does not re-enter the terminus
        // reassignment gate on the same visit and cancel its own catch-up. Not a dwell setting; see the catch-up branch.
        private const int kCatchUpNudgeMinutes = 1;
        // The frame each vehicle started boarding its CURRENT stop. Presence == "boarding now"; stamped on the first
        // tick boarding and dropped when it leaves (so the same stop next loop re-stamps). Feeds HoldStop's min-dwell.
        private readonly Dictionary<Entity, uint> m_ArrivedFrame = new Dictionary<Entity, uint>();

        // ===== DIAGNOSTIC (read-only): the game's ESTIMATED line duration vs the MEASURED actual loop time =====
        // Purpose: gather hard per-line data on how well the pathfinder estimate (ComputeStableDuration) matches the
        // real time a bus takes, with and without a slow-time mod, so we can decide whether a travel-time correction is
        // warranted (issue: reporter measured ~150m estimated vs ~180m actual). Measures each vehicle's terminus
        // DEPARTURE -> next terminus ARRIVAL span (travel + intermediate dwells, EXCLUDING the terminus hold) so it is
        // directly comparable to the estimate. EMA'd per line. Writes NOTHING to the world — pure observation.
        private readonly Dictionary<Entity, Entity> m_LapFront = new Dictionary<Entity, Entity>();        // line -> vehicle at its terminus now
        private readonly Dictionary<Entity, uint>   m_VehTerminusDepart = new Dictionary<Entity, uint>(); // vehicle -> frame it last left the terminus
        private readonly Dictionary<Entity, float>  m_LineLoopEma = new Dictionary<Entity, float>();      // line -> EMA of measured loop frames
        private readonly Dictionary<Entity, int>    m_LineLoopSamples = new Dictionary<Entity, int>();    // line -> loop samples so far
        private readonly Dictionary<Entity, float>  m_LineLoopMin = new Dictionary<Entity, float>();      // line -> running MIN loop (the true single loop; doubles sit above it)
        private readonly Dictionary<Entity, int>    m_LineRejectStreak = new Dictionary<Entity, int>();   // line -> consecutive gate rejects (drives the stale-anchor reset)
        // ===== PER-STOP measured arrival offset (frames from the terminus) — the fix for "buses leave early" AND the
        // feedback loop. Learned ONLY from buses that ran the loop with NO early-arrival hold (m_VehHeld), so the value
        // is real travel + natural dwell, never the mod's own holds. Drives each stop's posted time directly (per-stop
        // accurate), replacing the uniform loop-factor that mis-distributed the correction across stops. =====
        private readonly Dictionary<Entity, float>  m_StopOffsetEma = new Dictionary<Entity, float>();     // waypoint -> EMA arrival offset (frames)
        private readonly Dictionary<Entity, int>    m_StopOffsetSamples = new Dictionary<Entity, int>();   // waypoint -> samples
        private readonly HashSet<Entity>            m_VehHeld = new HashSet<Entity>();                      // vehicles EARLY-HELD this loop (excluded from measurement)
        private readonly Dictionary<Entity, Entity> m_VehLastRecordedStop = new Dictionary<Entity, Entity>(); // veh -> last stop recorded (once per arrival)
        private const uint  kMinLoopFrames = 1000u;      // ignore absurdly short spans (jitter / same-tick slot churn)
        private const uint  kMaxLoopFrames = 4194304u;   // ...and absurdly long ones (a loop can't exceed a stretched day)
        private const float kLoopAlpha     = 0.30f;      // EMA smoothing for the measured loop
        private const int   kMinTrustSamples = 4;        // measured correction is trusted over the density prior at >= this
        private const int   kResetAfterRejects = 4;      // consecutive rejects => the min anchor is stale (route edit / glitch): re-anchor
        // Absolute per-line vehicle sanity cap — a runaway BACKSTOP, not a design limit, and it must sit far clear of any
        // legitimate need: the drain treats everything above the target as surplus, so a line that genuinely wants more
        // than the cap is not merely capped, it is actively DRAINED down to it. Now that the cap applies on every path
        // (not just the opt-in correction) 100 was too tight — a long metro loop at a tight headway can exceed it
        // legitimately (a 271-minute loop on a 2-minute headway needs ~136). The real flood protection is the
        // duration-stability gate above; this only stops an absurd reading running away.
        private const int   kFleetCap        = 150;

        // Read by VehicleLimitSystem to auto-uncap the vehicle ceiling while any line is timetabled.
        public static bool TimetableInUse;

        protected override void OnCreate()
        {
            base.OnCreate();
            // TimetableInUse is a static read by VehicleLimitSystem; reset it on every system-creation (i.e. per world /
            // save load) so a stale "true" left over from a previous session can't keep the global vehicle cap uncapped.
            TimetableInUse = false;
            m_Sim = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Time = World.GetOrCreateSystemManaged<TimeSystem>();
            m_Fleet = World.GetOrCreateSystemManaged<HourlyFleetSystem>();
            m_Timebase = World.GetOrCreateSystemManaged<TimebaseSystem>();
            m_LineQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Route>(),
                    ComponentType.ReadWrite<TransportLine>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<RouteWaypoint>(),
                    ComponentType.ReadOnly<TimetableSchedule>(),
                },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() },
            });
            // Intentionally NOT RequireForUpdate(m_LineQuery): OnUpdate must keep ticking when the query EMPTIES (the
            // last timetabled line was deleted) so it can set TimetableInUse=false and let VehicleLimitSystem restore
            // the global vehicle cap. With RequireForUpdate the system stops on an empty query, latching the 8x uncap
            // on forever (and bleeding it into the next save loaded this session). The empty-query loop is trivial.

            // ALL lines, for the unbunching-residue repair (no TimetableSchedule requirement — see GlobalHealUnbunching).
            m_HealQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadWrite<TransportLine>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() },
            });
        }

        // A save (or a new game) just finished loading: schedule the one-time global unbunching heal for the next tick,
        // so an affected save is repaired on load even for lines that are no longer timetabled.
        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            m_GlobalHealPending = true;
            // Drop any clean-uninstall request that was armed BEFORE this city loaded. The flag is static (it must
            // survive the per-load system recreation between the button press and the next tick), and OnUpdate does not
            // tick at the main menu — so a press made outside a city would otherwise sit armed and wipe the timetables of
            // whatever city is loaded NEXT. A legitimate in-game press can't be lost here: no load happens between the
            // press and its consumption on the following tick.
            s_cleanUninstallPending = false;
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 8;

        protected override void OnUpdate()
        {
            Setting s = Mod.ActiveSetting;
            if (s == null)
                return;

            // One-time-per-load repair of the unbunching residue an old version of this mod left in saves.
            if (m_GlobalHealPending)
            {
                m_GlobalHealPending = false;
                GlobalHealUnbunching();
            }

            uint frame = m_Sim.frameIndex;
            int nowMin = (int)(m_Time.normalizedTime * 1440f) % 1440;

            // Runtime frame<->minute scale (vanilla 262144 frames/day unless a slow-time mod stretches the day). One
            // consistent snapshot per tick. On a real day-length change, drop the per-vehicle slots that were scaled by
            // the previous value so each bus re-derives against the new one at its next terminus visit.
            m_Fpm = m_Timebase.FramesPerMinute;
            m_Um = m_Timebase.UnitMinutes;
            uint tbGen = m_Timebase.RegimeGeneration;
            if (tbGen != m_TimebaseGen) { m_TimebaseGen = tbGen; m_RunSlotFrame.Clear(); m_LastSlotFrame.Clear(); }

            // Clean-uninstall button (Options): one-shot wipe of every mod component + mutation, then bail this tick.
            // Runs regardless of the master switch, so a paused mod can still be cleaned out.
            if (s_cleanUninstallPending)
            {
                s_cleanUninstallPending = false;
                CleanUninstall(frame);
                return;
            }

            NativeArray<Entity> lines = m_LineQuery.ToEntityArray(Allocator.Temp);
            // Deferred structural changes (adding the LineMeasuredTravel persistence component to a line that lacks it):
            // recorded during the per-line loop and played back AFTER it, so we never change an archetype mid-iteration
            // while this line's buffer/component handles are live (the ECS structural-change hazard).
            EntityCommandBuffer ecb = new EntityCommandBuffer(Allocator.Temp);
            bool anyEnabled = false;
            int enabledCount = 0;
            string sample = null;
            m_LiveVehScratch.Clear(); // repopulated in the drain below, then used to prune m_RunSlotFrame (issue #4)
            for (int i = 0; i < lines.Length; i++)
            {
                Entity line = lines[i];
                TimetableSchedule sch = EntityManager.GetComponentData<TimetableSchedule>(line);
                CustomPeakSchedule customSch = EntityManager.HasComponent<CustomPeakSchedule>(line)
                    ? EntityManager.GetComponentData<CustomPeakSchedule>(line) : CustomPeakSchedule.Default(); // PR #5 per-line peak
                TransportLine tl = EntityManager.GetComponentData<TransportLine>(line);

                // Master switch OFF => treat EVERY line as un-timetabled: run the same "hand back to vanilla" path as a
                // line whose schedule is switched off (release held buses, clear the fleet policy, restore unbunching),
                // without clearing the user's per-line timetable config. Re-enabling resumes exactly where it left off.
                if (!s.Enabled || !sch.m_Enabled)
                {
                    RestoreUnbunching(line, tl);
                    if (m_LastFleet.ContainsKey(line))
                    {
                        // We were managing this line — hand it back to vanilla EXACTLY ONCE (m_LastFleet is cleared
                        // just below, so later disabled frames skip this): release any bus we were holding so it
                        // departs immediately instead of idling to a stale scheduled frame (#8), and deactivate the
                        // mod-applied vehicle-count policy so the fleet reverts to vanilla's automatic count rather
                        // than staying frozen at the last derived number — which otherwise persists into the save (#4).
                        ReleaseHeldVehicles(line, frame);
                        m_Fleet.TryClearLineFleet(line);
                    }
                    m_LastFleet.Remove(line);
                    m_PendingRetire.Remove(line);
                    m_LapServed.Remove(line);
                    // Drop the LIVE loop-time measurement. NOTE: since the measurement is now persisted in
                    // LineMeasuredTravel (which is deliberately NOT removed here, so it survives a pause), re-enabling
                    // re-seeds these from the component rather than measuring from scratch — intentional. If the route
                    // changed while disabled, the stale value self-heals via the existing reject/re-anchor path.
                    m_LapFront.Remove(line);
                    m_LineLoopEma.Remove(line);
                    m_LineLoopSamples.Remove(line);
                    m_LineLoopMin.Remove(line);
                    m_LineRejectStreak.Remove(line);
                    m_LastDur.Remove(line);
                    m_DurStable.Remove(line);
                    m_LastSlotFrame.Remove(line);
                    continue;
                }
                anyEnabled = true;
                enabledCount++;

                // Rehydrate the measured loop from the persisted component the first time we see this line with empty
                // in-memory measurement (fresh load, or a re-enabled line), so LineCorrection/fleet use the real learned
                // loop immediately instead of the cold density prior. And ensure the component exists (deferred add) so
                // the mirror below has somewhere to write.
                RehydrateMeasured(line);
                if (!EntityManager.HasComponent<LineMeasuredTravel>(line))
                    ecb.AddComponent<LineMeasuredTravel>(line);

                // (2) Leave TransportLine.m_UnbunchingFactor at the PREFAB DEFAULT — and heal it if an older version
                // of this mod zeroed it.
                //
                // Until v0.2.3 this zeroed the factor so a vehicle wouldn't idle mid-route to self-space. That was a
                // serious mistake: m_UnbunchingFactor is SERIALIZED into the save (Game.Routes/TransportLine), NOTHING
                // in vanilla ever restores it (the only assignment is the component's ctor from the prefab default,
                // which runs once at creation), and NO UI anywhere exposes it. So uninstalling the mod — or the mod
                // failing to load after a game patch — left those lines permanently unable to unbunch, invisibly and
                // unrecoverably, looking exactly like a base-game bug.
                //
                // It is also unnecessary. Unbunching only ever feeds RouteUtils.CalculateDepartureFrame, i.e. it just
                // inflates m_DepartureFrame at StartBoarding — and HoldStop now writes m_DepartureFrame
                // authoritatively every 8 frames, so the factor cannot affect a line we are actively holding.
                // Leaving it alone is strictly better: an out-of-service window (day-only line at night) now unbunches
                // normally instead of staying silently crippled.
                //
                // RestoreUnbunching only writes when the value differs from the prefab default, so this is a no-op on
                // a healthy line and a ONE-TIME repair on a save damaged by an earlier version.
                RestoreUnbunching(line, tl);

                // The line's day/night operating schedule — which intervals apply and when it runs.
                int sched = LineSchedule.Of(EntityManager, line);

                // (2b) Keep the STORED first departure equal to the EFFECTIVE one.
                //
                // ScheduleMath.FirstDeparture already clamps a first departure that falls outside the line's operating
                // window (night-only -> NightStart, day-only -> NightEnd) — but only as a RETURN VALUE. The stored
                // field kept whatever the player set, and the panel displays the stored field, so the UI LIED: a
                // night-only line reading "First departure 05:00" was actually running its first bus at 22:00.
                // Writing the clamp back makes stored == effective == displayed, and gives the behaviour you'd expect:
                // switch a line to night-only and its first departure jumps to the start of the night; switch it to
                // day-only and it jumps to the morning. DayAndNight lines are never clamped, so they are untouched.
                //
                // It also gives the steppers honest bounds for free: on a day-only line "-" stops at 06:00 because
                // 05:59 is a minute that line genuinely cannot run, and on a night-only line "+" past 05:59 comes back
                // round to 22:00 instead of escaping into daytime.
                //
                // Cost, accepted deliberately: a value set while the line was DayAndNight is overwritten (not
                // remembered) if the line is later switched to day- or night-only. It was inoperative for that line
                // anyway. Only writes when it actually differs, so this is a one-time correction, not a per-tick write.
                int effFirst = ScheduleMath.FirstDeparture(s, sch, sched);
                if (effFirst != sch.m_FirstDeparture)
                {
                    sch.m_FirstDeparture = (ushort)effFirst; // FirstDeparture returns a minute-of-day, always 0..1439
                    EntityManager.SetComponentData(line, sch);
                }

                // (1) derive + apply fleet for the current headway. Re-assert EVERY tick (not just on change): the
                // fleet is now applied by writing the line's own VehicleInterval modifier directly (see
                // HourlyFleetSystem.TrySetLineFleet), and that buffer is rebuilt from the line's policies whenever the
                // line is edited or its route recreated — so a periodic re-write keeps our derived count in place.
                // TrySetLineFleet only touches the buffer when the value actually differs, so this is cheap.
                int desiredFleet = 0;
                float durUnits = m_Fleet.LineStableDurationUnits(line);
                // Vehicle-count management is OPT-OUT (compat with dedicated fleet mods that write the same per-line
                // VehicleInterval modifier — e.g. All Transit + Truck). When ManageVehicleCount is OFF we never size the
                // fleet; a line we WERE sizing is handed back to vanilla / the other mod EXACTLY ONCE (m_LastFleet-gated,
                // same one-time hand-back as the disable branch) and then left alone, so we can't fight the other mod
                // every tick. The departure HOLD below is independent of the count and still runs. (durUnits is still
                // computed above — MeasureLap needs it regardless.)
                if (!s.ManageVehicleCount)
                {
                    if (m_LastFleet.ContainsKey(line))
                    {
                        m_Fleet.TryClearLineFleet(line);
                        m_LastFleet.Remove(line);
                    }
                }
                else if (durUnits > 1f)
                {
                    // Flood-on-load guard (see m_LastDur): only size once the duration estimate has held steady (within
                    // 5%) for kDurStableTicks consecutive ticks. Right after a load it spikes then settles; requiring
                    // agreement across ticks skips the spike so we never write the inflated count. In steady state the
                    // estimate barely moves, so this passes immediately and sizing behaves exactly as before.
                    float prevDur = m_LastDur.TryGetValue(line, out float pd) ? pd : 0f;
                    m_LastDur[line] = durUnits;
                    bool agrees = prevDur > 1f && durUnits >= prevDur * 0.95f && durUnits <= prevDur * 1.05f;
                    int stable = agrees ? (m_DurStable.TryGetValue(line, out int sc) ? sc : 0) + 1 : 0;
                    m_DurStable[line] = stable;
                    {
                        int interval = ScheduleMath.IntervalFor(s, sch, customSch, nowMin, sched);
                        // Phase 2: size the fleet to the REAL loop when the player opts in (costs money); otherwise the
                        // estimate, exactly as before. LineCorrection is grow-only for fleet; kFleetCap is the hard backstop.
                        float fleetUnits = s.ProvisionRealFleet ? durUnits * LineCorrection(line, durUnits, forFleet: true) : durUnits;
                        desiredFleet = ScheduleMath.DerivedFleet(fleetUnits, interval, m_Um);
                        // Hard sanity cap on EVERY path. It was previously gated behind ProvisionRealFleet (the correction
                        // being the only assumed source of a bad number), which left the DEFAULT estimate path uncapped —
                        // so if a post-load inflated duration ever holds steady long enough to satisfy the stability gate
                        // above, nothing bounded the resulting count. The cap is far above any legitimate line's needs, so
                        // it never binds in normal play; it exists purely so a bad reading can't flood a city with buses.
                        if (desiredFleet > kFleetCap) desiredFleet = kFleetCap;
                        // CRITICAL: desiredFleet must be computed on EVERY tick, NOT only when the stability gate passes.
                        // The drain below derives `surplus` from it, and when desiredFleet is 0 surplus is forced to 0,
                        // which SKIPS THE WHOLE DRAIN BLOCK — including the branch that strips vanilla's AbandonRoute off
                        // a mid-route bus (DESIGN DECISION B). Leaving it unset during an unstable-estimate tick therefore
                        // let vanilla retire buses wherever they stood: buses visibly VANISHING mid-route (live-reported).
                        // So only the fleet WRITE is gated on stability; the target itself is always derived.
                        if (stable >= kDurStableTicks && m_Fleet.TrySetLineFleet(line, desiredFleet))
                            m_LastFleet[line] = desiredFleet;
                        else if (m_LastFleet.TryGetValue(line, out int heldFleet))
                            // The write was suppressed (estimate still settling) but the line already carries a count we
                            // wrote earlier. The drain MUST reason about the number vanilla is actually acting on, not the
                            // one we would like: otherwise our surplus is computed against a target vanilla never saw, and
                            // the two fight — we retire a bus while vanilla buys one back (and vice versa).
                            desiredFleet = heldFleet;
                    }
                }

                // (3) terminus = timing point + retirement anchor (player-chosen stop, or the first stop)
                FindTerminus(line, sch, out Entity terminusStop, out Entity terminusWaypoint);

                // Accumulate this line's measured loop time from terminus front-vehicle changes (feeds LineCorrection).
                MeasureLap(line, terminusStop, frame, durUnits);
                // Persist the freshly-updated measurement into the line's component so it survives save/load (no-op until
                // the component exists — added via the ECB above — and only writes when a value actually changed).
                MirrorMeasured(line);

                // (3-pre) FORCE STOPS: make our buses actually pull in and STOP rather than let vanilla skip a stop
                // where nobody boards or alights — ALWAYS at the terminus (skipping it strands the whole schedule), and
                // at every stop when the player opts in. A skipped stop never enters Boarding, so the hold below can't
                // touch it and the bus rolls on early. See ForceStops.
                int forcedStops = ForceStops(line, terminusWaypoint, s.StopAtEveryStop);

                // (3a) FULL TIMETABLE: hold EACH stop's boarding bus to that stop's scheduled departure — the terminus
                // schedule shifted by the stop's cumulative offset from the terminus (offset 0 at the terminus).
                // Offsets come from the route itself: each RouteSegment's PathInformation.m_Duration PLUS the dwell at
                // each intermediate timed stop (60-frame route units), summed from the terminus and converted to
                // schedule minutes via the runtime unit scale (m_Um) — matching the UI board's TravelUnitsBetween.
                int curInterval = ScheduleMath.IntervalFor(s, sch, customSch, nowMin, sched);
                bool diagLog = frame - m_LastLog >= 16384; // [SelfTest] cadence — dump the hold's numbers periodically

                // Estimated vs measured loop time for this line (the data that tells us whether the pathfinder estimate
                // undershoots the real drive time, and by how much — RT on or off). estDur uses the same durUnits that
                // sizes the fleet; measLoop is the observed terminus-to-terminus travel (see MeasureLap).
                if (diagLog && m_LineLoopSamples.TryGetValue(line, out int loopN) && loopN > 0)
                {
                    float measMin = m_LineLoopEma[line] / m_Fpm;
                    float estMin  = durUnits * m_Um;
                    float ratio   = estMin > 0.01f ? measMin / estMin : 0f;
                    Mod.log.Info($"[SelfTest] laptime line#{line.Index} estDur={estMin:F1}m measLoop={measMin:F1}m " +
                                 $"ratio={ratio:F2} n={loopN} compat={(s.RealisticTripsCompat ? 1 : 0)}");
                }

                HoldAllStops(line, s, sch, customSch, sched, terminusStop, terminusWaypoint, frame, nowMin, curInterval, diagLog);

                // (3b) SLOT-COUPLED DRAIN: shed surplus buses at the terminus WITHOUT skipping departures.
                //
                // When the schedule widens the headway (e.g. 15 buses/4min -> 4 buses/15min) the game wants to cull the
                // extras by odometer (AbandonRoute) and would retire each wherever it sits — dumping passengers mid-route
                // and, worse, retiring the very buses due to depart, so several scheduled departures in a row go unserved
                // (the "10:00/10:10/10:20 dead slots" case). We take ownership of the cull instead.
                //
                // The key: the terminus timing-point bus is held (3a) to the next scheduled departure and OCCUPIES the
                // single boarding spot for the whole headway. So while a bus is boarding the terminus, this slot's
                // departure is guaranteed (exactly one bus leaves per slot) — and ONLY then may an extra that has arrived
                // behind it retire. That gate ("slotCovered") is what turns a burst of retirements into a trickle: one
                // bus departs each slot, the extras drain in the gaps, the fleet glides down to target with no missed
                // departure, and retirement stops on its own once surplus hits zero. Lap-before-retire is preserved
                // (a freshly-deployed peak bus completes one serving loop before it can be recalled), and if the fleet
                // is raised back up the surplus vanishes and every pending retirement is forgotten.
                if (terminusWaypoint != Entity.Null && EntityManager.HasBuffer<RouteVehicle>(line))
                {
                    DynamicBuffer<RouteVehicle> vehicles = EntityManager.GetBuffer<RouteVehicle>(line, isReadOnly: true);
                    if (!m_PendingRetire.TryGetValue(line, out HashSet<Entity> pending))
                        m_PendingRetire[line] = pending = new HashSet<Entity>();
                    if (!m_LapServed.TryGetValue(line, out HashSet<Entity> lapServed))
                        m_LapServed[line] = lapServed = new HashSet<Entity>();

                    // Is a bus boarding the terminus right now as the timing-point front? While one is, this slot's
                    // departure is covered — it holds the single boarding spot until its scheduled time — so extras may
                    // retire without skipping a departure. No front boarding => hold off, let a bus flow through first.
                    // NOTE: slotCovered is computed AFTER pass 1 (below), because it must verify the boarding vehicle
                    // belongs to THIS line, and pass 1 is what builds that set.

                    // Pass 1: count live buses and mark lap-eligibility. A bus whose current target is NOT the terminus
                    // has left the terminus and is serving the loop, so it has earned a retirement on its next return.
                    HashSet<Entity> live = new HashSet<Entity>();
                    int liveCount = 0;
                    int flaggedCount = 0; // vehicles vanilla has already marked for retirement (see protectFromCull)
                    for (int v = 0; v < vehicles.Length; v++)
                    {
                        Entity veh = vehicles[v].m_Vehicle;
                        if (veh == Entity.Null || !EntityManager.HasComponent<PublicTransport>(veh))
                            continue;
                        live.Add(veh);
                        m_LiveVehScratch.Add(veh); // union of all live vehicles -> prunes m_RunSlotFrame after the loop
                        liveCount++;
                        if ((EntityManager.GetComponentData<PublicTransport>(veh).m_State & PublicTransportFlags.AbandonRoute) != 0)
                            flaggedCount++;
                        // Arrival stamp for HoldStop's min-dwell: presence in m_ArrivedFrame == "currently boarding,
                        // arrived at this frame". Stamp on the first boarding tick; drop when it leaves the stop so the
                        // next stop (or the same stop next loop) re-stamps a fresh arrival.
                        if ((EntityManager.GetComponentData<PublicTransport>(veh).m_State & PublicTransportFlags.Boarding) != 0)
                        {
                            if (!m_ArrivedFrame.ContainsKey(veh)) m_ArrivedFrame[veh] = frame;
                        }
                        else m_ArrivedFrame.Remove(veh);
                        if (EntityManager.HasComponent<Target>(veh)
                            && EntityManager.GetComponentData<Target>(veh).m_Target != terminusWaypoint)
                            lapServed.Add(veh);
                    }
                    // Is a bus of OURS boarding the terminus right now as the timing-point front? While one is, this
                    // slot's departure is covered — it holds the single boarding spot until its scheduled time — so
                    // extras may retire without skipping a departure.
                    //
                    // The `live.Contains` test is LOAD-BEARING and was missing: a terminus stop can be SHARED with other
                    // lines, and its single BoardingVehicle slot may hold a FOREIGN line's bus. Without the check we read
                    // another line's bus as "our slot is covered" and retired one of ours while our own next departure
                    // had nobody to run it — live-reported as "buses retire without another bus boarding the stop".
                    // (HoldStop already applies exactly this lineVehicles guard before writing a departure frame.)
                    bool slotCovered = false;
                    if (terminusStop != Entity.Null && EntityManager.HasComponent<BoardingVehicle>(terminusStop))
                    {
                        Entity frontVeh = EntityManager.GetComponentData<BoardingVehicle>(terminusStop).m_Vehicle;
                        if (frontVeh != Entity.Null && live.Contains(frontVeh)
                            && EntityManager.HasComponent<PublicTransport>(frontVeh))
                        {
                            PublicTransport fpt = EntityManager.GetComponentData<PublicTransport>(frontVeh);
                            slotCovered = (fpt.m_State & PublicTransportFlags.Boarding) != 0
                                       && (fpt.m_State & PublicTransportFlags.EnRoute) != 0;
                        }
                    }

                    int surplus = desiredFleet > 0 ? liveCount - desiredFleet : 0;

                    if (diagLog)
                        Mod.log.Info($"[SelfTest] fleet line#{line.Index} now={nowMin}m live={liveCount} target={desiredFleet} surplus={surplus} slotCovered={slotCovered} pending={pending.Count} forced={forcedStops}");

                    // Stale-latch repair. `pending` used to be cleared ONLY when surplus hit 0, so buses latched while the
                    // surplus was larger stayed latched after it shrank — observed live as pending=8 against surplus=4 —
                    // leaving more buses retirement-eligible than the line is actually over target, i.e. retiring BELOW
                    // target. Rebuild it instead: if it holds more than the current surplus, drop the lot and re-latch
                    // this tick from whatever vanilla currently flags. Safe now that the mid-route strip below is
                    // unconditional — a dropped member that still carries the flag gets unflagged on this same pass.
                    // Drop buses that already left the line BEFORE comparing against the surplus — otherwise, on the tick
                    // a retired bus finally leaves the RouteVehicle buffer, a stale entry makes pending.Count exceed the
                    // surplus and wipes the whole latch, dropping the commit of a bus that was one boarding away from
                    // retiring. (`live` is complete here: pass 1 has run.)
                    pending.RemoveWhere(e => !live.Contains(e));
                    if (surplus < 0) surplus = 0;
                    if (pending.Count > surplus) pending.Clear();

                    // MAY we strip vanilla's retirement flag at all this tick? Two states where the answer is NO, both of
                    // which would otherwise leave a line UNABLE TO EVER SHED A VEHICLE (the strip below runs every 8
                    // frames and would simply undo vanilla forever):
                    //
                    //  1. desiredFleet == 0 — we have NO OPINION about this line's size: "Manage vehicle count" is off
                    //     (the 0.3.5 opt-out that hands counts to a dedicated fleet mod), or the duration estimate has
                    //     not resolved. Stripping here would silently break that interop by pinning the fleet forever.
                    //  2. vanilla has flagged EVERY live vehicle — that is not a surplus cull but a LINE SHUTDOWN
                    //     (TransportLineSystem zeroes the target for an Inactive line, a line with no active buildings,
                    //     or a day-only line at night, then abandons the whole fleet in one pass). Legitimate: the line
                    //     is meant to stop running, so a day-only line must not be kept circulating all night.
                    //
                    // Self-detecting on purpose: it reads what vanilla actually DID rather than trying to re-derive when
                    // vanilla considers a line inactive (its hardcoded night does NOT match this mod's configurable
                    // night window, so re-deriving would disagree and surrender buses at the wrong time).
                    //  3. there is no cull of ours actually in progress (no surplus and nothing latched) — then vanilla is
                    //     flagging for a reason we do not model (a vehicle-MODEL change retires mismatched vehicles
                    //     regardless of count, for one), and overruling it would strand those vehicles on the line
                    //     forever. We only ever defend a surplus we ourselves identified.
                    bool protectFromCull = desiredFleet > 0 && flaggedCount < liveCount
                                           && (surplus > 0 || pending.Count > 0);

                    // *** THE LOOP RUNS UNCONDITIONALLY — it is NOT inside an `if (surplus > 0)` guard. ***
                    // It used to be, which meant that whenever a line sat AT or UNDER target (surplus <= 0) the mod
                    // stopped stripping vanilla's AbandonRoute from mid-route buses entirely — so vanilla retired them
                    // wherever they stood and they VANISHED MID-ROUTE (live-reported). Same hole when `pending` was
                    // over-full: a flagged bus failed the latch, hit `continue`, and never got unflagged. DESIGN
                    // DECISION B only holds if the strip is reachable in EVERY state, so `pending`/`surplus` now gate
                    // ONLY the assert, never the protection.
                    for (int v = 0; v < vehicles.Length; v++)
                    {
                        Entity veh = vehicles[v].m_Vehicle;
                        if (veh == Entity.Null || !EntityManager.HasComponent<PublicTransport>(veh))
                            continue;
                        PublicTransport pt = EntityManager.GetComponentData<PublicTransport>(veh);
                        bool flagged = (pt.m_State & PublicTransportFlags.AbandonRoute) != 0;
                        if (flagged && surplus > 0 && pending.Count < surplus)
                            pending.Add(veh); // the game wants this one gone — latch it (bounded by live surplus)
                        // Final approach: not boarding and its target is the terminus (last leg of the loop).
                        bool onFinalApproach = (pt.m_State & PublicTransportFlags.Boarding) == 0
                            && EntityManager.HasComponent<Target>(veh)
                            && EntityManager.GetComponentData<Target>(veh).m_Target == terminusWaypoint;
                        // Retire only once a bus OF OURS is covering this slot's departure (so we never retire the bus
                        // the terminus needs to send out next). m_Committed carries that decision across the brief gaps
                        // when no front is boarding: it replaces the old `flagged ||` arm, which let ANY vanilla-flagged
                        // bus through without consulting slotCovered at all — the second half of the "retires with
                        // nothing covering the slot" report.
                        //
                        // *** DESIGN DECISION B (see the header) — deliberate. Clearing vanilla's AbandonRoute on a
                        // mid-route bus is the POINT, not an oversight: it makes the surplus finish its loop and
                        // drop its passengers at the terminus instead of vanishing wherever it happened to be.
                        // The deferral reliably wins the race — this system runs every 8 frames, the AI that
                        // CONSUMES the flag (StartBoarding) every 16, and vanilla re-flags surplus only every 256,
                        // so there are always two of our ticks between AI ticks. ***
                        bool commit = pending.Contains(veh) && onFinalApproach && lapServed.Contains(veh)
                                      && (slotCovered || m_Committed.Contains(veh));
                        if (commit)
                        {
                            // Record the commit for ANY committed bus, not only the one we assert on this tick: a bus
                            // vanilla had already flagged would otherwise never enter m_Committed, so the moment
                            // slotCovered flickered false it would fall to the strip branch and be un-retired.
                            m_Committed.Add(veh);
                            if (!flagged) // lap done, back at the terminus, slot covered — assert; vanilla retires it here
                            {
                                pt.m_State |= PublicTransportFlags.AbandonRoute;
                                EntityManager.SetComponentData(veh, pt);
                                if (diagLog) // was logged every tick: one bus produced 11 identical lines in 13 game-minutes
                                    Mod.log.Info($"[SelfTest] retire line#{line.Index} veh#{veh.Index} now={nowMin}m live={liveCount} target={desiredFleet}");
                            }
                        }
                        else if (flagged && protectFromCull) // mid-route, not lapped, or slot not covered — keep it serving
                        {
                            // Gated by protectFromCull (see above): we only overrule vanilla when we actually manage this
                            // line's size AND vanilla is culling a surplus rather than shutting the whole line down.
                            pt.m_State &= ~PublicTransportFlags.AbandonRoute;
                            EntityManager.SetComponentData(veh, pt);
                            m_Committed.Remove(veh);
                        }
                    }
                    pending.RemoveWhere(e => !live.Contains(e)); // drop buses that already retired / left the line
                    lapServed.RemoveWhere(e => !live.Contains(e)); // forget buses no longer on the line
                }

                if (sample == null)
                    sample = $"line#{line.Index} sched{sched} every {ScheduleMath.IntervalFor(s, sch, customSch, nowMin, sched)}m";
            }

            // Apply the deferred component adds now that the per-line loop (and its live handles) is done.
            ecb.Playback(EntityManager);
            ecb.Dispose();

            // Prune tracking entries for lines that left the query (e.g. bulldozed while enabled) so they don't leak.
            m_LiveScratch.Clear();
            for (int i = 0; i < lines.Length; i++) m_LiveScratch.Add(lines[i]);
            PruneToLive(m_LastFleet, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_PendingRetire, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_LapServed, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_LapFront, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_LineLoopEma, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_LineLoopSamples, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_LineLoopMin, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_LineRejectStreak, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_LastDur, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_DurStable, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_LastSlotFrame, m_LiveScratch, m_StaleScratch);
            // Drop per-vehicle slots for buses that despawned/retired (m_LiveVehScratch = every live vehicle this tick).
            PruneToLive(m_RunSlotFrame, m_LiveVehScratch, m_StaleScratch);
            PruneToLive(m_ArrivedFrame, m_LiveVehScratch, m_StaleScratch);
            PruneToLive(m_VehTerminusDepart, m_LiveVehScratch, m_StaleScratch);
            PruneToLive(m_VehLastRecordedStop, m_LiveVehScratch, m_StaleScratch);
            m_VehHeld.RemoveWhere(v => !m_LiveVehScratch.Contains(v));
            m_Committed.RemoveWhere(v => !m_LiveVehScratch.Contains(v)); // retired/despawned buses drop their commit
            // Per-stop offsets are keyed by waypoint (not line); drop entries whose waypoint no longer exists (route
            // edited / line deleted). Periodic (aligned with the [SelfTest] cadence) so it's a cheap occasional scan.
            if (frame - m_LastLog >= 16384 && m_StopOffsetSamples.Count > 0)
            {
                m_StaleScratch.Clear();
                foreach (Entity wpKey in m_StopOffsetSamples.Keys)
                    if (!EntityManager.Exists(wpKey)) m_StaleScratch.Add(wpKey);
                for (int i = 0; i < m_StaleScratch.Count; i++)
                { m_StopOffsetSamples.Remove(m_StaleScratch[i]); m_StopOffsetEma.Remove(m_StaleScratch[i]); }
            }

            lines.Dispose();

            TimetableInUse = anyEnabled;

            if (anyEnabled && frame - m_LastLog >= 16384)
            {
                m_LastLog = frame;
                Mod.log.Info($"[SelfTest] timetableDispatch: timetabledLines={enabledCount} nowMin={nowMin} {sample}");
            }
        }

        // (3a helper) Hold every stop's boarding bus to that stop's scheduled departure. Per-stop offset = cumulative
        // route time from the terminus (Σ RouteSegment.PathInformation.m_Duration + dwell at each intermediate timed
        // stop, 60-frame units) -> schedule minutes. Segment i is the leg from waypoint i to waypoint i+1, and the
        // dwell term mirrors HourlyFleetSystem.ComputeStableDuration / the UI board so posted and held times agree.
        private void HoldAllStops(Entity line, Setting s, TimetableSchedule sch, CustomPeakSchedule customSch, int sched, Entity terminusStop,
            Entity terminusWaypoint, uint frame, int nowMin, int interval, bool diagLog)
        {
            // Outside the line's operating window (day-only at night, night-only by day, or a degenerate EMPTY window
            // like NightStart==NightEnd) -> don't hold or force-depart anything; let it run vanilla headway instead of
            // silently churning every bus through the force-depart path (which is what an empty window used to do).
            if (!ScheduleMath.InService(s, sched, nowMin))
                return;
            if (!EntityManager.HasBuffer<RouteWaypoint>(line) || !EntityManager.HasBuffer<RouteSegment>(line))
                return;
            DynamicBuffer<RouteWaypoint> wps = EntityManager.GetBuffer<RouteWaypoint>(line, isReadOnly: true);
            DynamicBuffer<RouteSegment> segs = EntityManager.GetBuffer<RouteSegment>(line, isReadOnly: true);
            int len = wps.Length;
            if (len == 0 || segs.Length < len)
                return;

            // Per-stop dwell (route units), added to each downstream stop's offset so the hold matches the departure
            // board (TravelUnitsBetween / ComputeStableDuration both count intermediate dwell). Without it the offset
            // was travel-only, so downstream stops departed ~1 min (one cumulative dwell) BEFORE their posted time.
            float stopDur = 1f;
            if (EntityManager.HasComponent<PrefabRef>(line))
            {
                Entity pf = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
                if (EntityManager.HasComponent<TransportLineData>(pf))
                    stopDur = EntityManager.GetComponentData<TransportLineData>(pf).m_StopDuration;
            }

            // A shared physical stop exposes ONE BoardingVehicle slot regardless of line, so its boarding bus may
            // belong to a DIFFERENT line. Build this line's own roster so HoldStop only ever holds our own buses.
            HashSet<Entity> lineVehicles = null;
            if (EntityManager.HasBuffer<RouteVehicle>(line))
            {
                DynamicBuffer<RouteVehicle> rv = EntityManager.GetBuffer<RouteVehicle>(line, isReadOnly: true);
                lineVehicles = new HashSet<Entity>();
                for (int i = 0; i < rv.Length; i++)
                    if (rv[i].m_Vehicle != Entity.Null) lineVehicles.Add(rv[i].m_Vehicle);
            }

            // Last tick's lap-served set (buses seen AWAY from the terminus). At the terminus HoldStop uses it to tell a
            // bus that has COMPLETED a lap (reassign it the next slot) from one that merely hasn't left yet (keep its
            // slot, depart late). May be null on a line's first managed tick — treated as "nobody has lapped".
            m_LapServed.TryGetValue(line, out HashSet<Entity> lapServed);

            // Start accumulating at the terminus waypoint (the schedule's timing anchor); fall back to index 0.
            int start = 0;
            if (terminusWaypoint != Entity.Null)
                for (int i = 0; i < len; i++)
                    if (wps[i].m_Waypoint == terminusWaypoint) { start = i; break; }

            // [SelfTest] diagnostic — one line per route dump showing every stop's derived offset (min from terminus)
            // and, for any stop with a boarding bus right now, its until/HOLD-or-GO decision. Read once live to see why
            // intermediate stops aren't waiting (expected suspect: offset==travel -> until~0 -> nothing to hold).
            System.Text.StringBuilder diag = diagLog
                ? new System.Text.StringBuilder("[SelfTest] hold line#").Append(line.Index)
                    .Append(" now=").Append(nowMin).Append("m int=").Append(interval).Append("m stops:")
                : null;

            bool useMeasured = s.RealisticTravelTime;
            // KNOWN ISSUE, deliberately NOT fixed here — read before "fixing" it. Offsets are picked PER STOP: measured
            // once a stop has kMinTrustSamples samples, else the raw pathfinder estimate. On a line whose real loop is
            // ~3x the estimate that interleaves two incompatible clocks on one route (live-observed posted sequence
            // …,35,85,20,144,30,… where the small values are estimate stops and the spikes measured ones), and every
            // observed hold-clamp release was at a far MEASURED stop while near estimate stops held fine.
            //
            // Scaling the estimate fallback by the line's measured LineCorrection looks like the obvious fix and was
            // implemented, reviewed, and REVERTED, because it: (1) makes the printed board disagree with the buses by up
            // to ~45 min — TransitParamsUISystem derives its own offsets and applies no such scale, so the fix must
            // publish the offsets the dispatch actually used and have the UI read THOSE; (2) flips the sign at estimate
            // stops — until = est*(corr - localRatio), so stops whose local ratio is below the line average would begin
            // clamping when they previously never could, i.e. it can produce MORE clamps than it removes; and (3) a
            // monotonicity ratchet added alongside it forced offMin >= waypointIndex, which on a stop-dense line can
            // exceed the real loop. Any real fix needs the UI plumbing plus a tested build.
            float offUnits = 0f;
            int prevOffMin = 0; // previous stop's posted offset, for the monotonic floor below
            for (int j = 0; j < len; j++)
            {
                int wpIdx = start + j; if (wpIdx >= len) wpIdx -= len;
                Entity wp = wps[wpIdx].m_Waypoint;
                // Posted offset for this stop (minutes from the terminus departure): the MEASURED per-stop arrival offset
                // when the feature is on and we have enough clean samples; otherwise the game's estimate. No uniform
                // factor — the per-stop measurement is per-stop accurate, so posted times match what the buses do.
                int offMin = (int)System.Math.Round(offUnits * m_Um);       // estimate fallback (terminus is j==0 -> 0)
                if (useMeasured && j >= 1 && m_StopOffsetSamples.TryGetValue(wp, out int sn) && sn >= kMinTrustSamples
                    && m_StopOffsetEma.TryGetValue(wp, out float emaF) && m_Fpm > 0.01f)
                    offMin = (int)System.Math.Round(emaF / m_Fpm);
                // Offsets accumulate around the loop, so a stop can never be posted EARLIER than the one before it.
                // That can still come out of the per-stop source mix: a measured stop (real time, ~3x the estimate on a
                // busy line) followed by one that has not gathered enough samples and falls back to the estimate. Live
                // evidence of the incoherence it causes: a posted sequence of …35, 85, 20, 144, 30… where the small
                // values are estimate stops and the spikes measured ones. The vehicle is then force-departed at the
                // estimate stop and reads as absurdly early at the next measured one.
                // A FLAT FLOOR, not a +1-per-stop ratchet: two stops 20 seconds apart legitimately share a posted
                // minute, whereas a ratchet forces offMin >= waypoint index and can outgrow the real loop on a
                // stop-dense line. Gated on useMeasured so a player who has not enabled measurement is untouched.
                if (useMeasured && j >= 1 && offMin < prevOffMin) offMin = prevOffMin;
                prevOffMin = offMin;
                bool boarding = false;
                if (EntityManager.HasComponent<Connected>(wp))
                {
                    Entity stop = EntityManager.GetComponentData<Connected>(wp).m_Connected;
                    if (stop != Entity.Null && EntityManager.HasComponent<BoardingVehicle>(stop))
                    {
                        boarding = true;
                        Entity bveh = EntityManager.GetComponentData<BoardingVehicle>(stop).m_Vehicle;
                        // Stamp the arrival frame HERE (before RecordStopOffset/HoldStop) for our own boarding bus. The
                        // drain stamps it too, but one tick LATER — too late for RecordStopOffset to see it before
                        // HoldStop flags a bus that arrives EARLY at THIS stop. Stamping first lets us record this stop's
                        // CLEAN arrival: a hold at THIS stop happens AFTER arrival, so it does not inflate THIS stop's
                        // offset — only an UPSTREAM hold does, and that is already excluded via m_VehHeld. Without this,
                        // only late/dwelling buses ever recorded, biasing each stop's EMA upward toward the slow tail.
                        if (bveh != Entity.Null && (lineVehicles == null || lineVehicles.Contains(bveh))
                            && !m_ArrivedFrame.ContainsKey(bveh))
                            m_ArrivedFrame[bveh] = frame;
                        // Learn this stop's real arrival offset from the (our-line, upstream-unheld) boarding bus.
                        if (j >= 1)
                            RecordStopOffset(line, wp, bveh, lineVehicles, frame);
                        HoldStop(s, sch, customSch, sched, line, stop, frame, nowMin, offMin, stop == terminusStop, lineVehicles, lapServed, diag);
                    }
                }
                if (diag != null && !boarding)
                    diag.Append(" [").Append(j).Append(":off").Append(offMin).Append(']');
                // Add the leg LEAVING this waypoint so the next waypoint's offset is correct.
                int segIdx = start + j; if (segIdx >= len) segIdx -= len;
                Entity seg = segs[segIdx].m_Segment;
                if (seg != Entity.Null && EntityManager.HasComponent<PathInformation>(seg))
                    offUnits += EntityManager.GetComponentData<PathInformation>(seg).m_Duration;
                // ...plus this stop's own dwell so DOWNSTREAM offsets match the board (which counts intermediate
                // dwell). Intermediate timed stops only: j==0 is the terminus, and a stop's own dwell never enters
                // its own offset. Omitting this is what made downstream stops depart a dwell-time early.
                if (j >= 1 && EntityManager.HasComponent<VehicleTiming>(wp))
                    offUnits += stopDur;
            }

            if (diag != null)
                Mod.log.Info(diag.ToString());
        }

        // Hold one stop's in-service boarding bus to its scheduled clock departure (the schedule shifted by offMin), or
        // release it on/after time. EVERY stop (terminus and intermediate) holds to its next scheduled slot: a bus that
        // arrives early waits for its clock minute; one that missed its slot rides the next one — a bounded, ONE-TIME
        // wait, after which it's on time at every later stop, so it never cascades.
        // The bound is now ENFORCED rather than assumed (see the slotInterval clamp below) — the old code trusted it.
        // The m_DepartureFrame bump is honored at all stops per TransportCarAISystem.StopBoarding (line ~1265: while
        // frame < m_DepartureFrame the boarding vehicle stays), not just the terminus.
        // When diag != null, appends this stop's decision (or skip reason) to the route's [SelfTest] dump.
        private void HoldStop(Setting s, TimetableSchedule sch, CustomPeakSchedule customSch, int sched, Entity line, Entity stop, uint frame, int nowMin,
            int offMin, bool isTerminus, HashSet<Entity> lineVehicles, HashSet<Entity> lapServed, System.Text.StringBuilder diag)
        {
            string tag = isTerminus ? "T" : "";
            Entity veh = EntityManager.GetComponentData<BoardingVehicle>(stop).m_Vehicle;
            if (veh == Entity.Null || !EntityManager.HasComponent<PublicTransport>(veh))
            { diag?.Append(" [off").Append(offMin).Append(tag).Append(":noveh]"); return; }
            // The boarding slot at a shared stop can hold ANOTHER line's bus — never write its departure frame.
            if (lineVehicles != null && !lineVehicles.Contains(veh))
            { diag?.Append(" [off").Append(offMin).Append(tag).Append(":foreign]"); return; }
            PublicTransport pt = EntityManager.GetComponentData<PublicTransport>(veh);
            // Only hold an IN-SERVICE boarding bus; a retiring one has EnRoute cleared and must reach the depot.
            bool isBoarding = (pt.m_State & PublicTransportFlags.Boarding) != 0;
            bool isEnRoute = (pt.m_State & PublicTransportFlags.EnRoute) != 0;
            if (!isBoarding || !isEnRoute)
            { diag?.Append(" [off").Append(offMin).Append(tag).Append(":brd").Append(isBoarding ? 1 : 0).Append("/enr").Append(isEnRoute ? 1 : 0).Append(']'); return; }
            // === PER-VEHICLE SLOT (issue #4) ===
            // Hold the bus to ITS OWN departure — the terminus slot it is running, shifted by this stop's cumulative
            // travel+dwell offset — NOT to "the next slot after now". A bus a few minutes behind therefore rides its
            // own slot LATE (departs immediately) instead of being bumped to the next cycle and stranded a whole
            // interval. The slot is a monotonic sim FRAME (no midnight-wrap ambiguity), recorded when the bus boards
            // the terminus and read as-is at every downstream stop of the same run.
            int maxInterval = ScheduleMath.MaxInterval(sch, customSch, sched);
            // MAXIMUM overrun past the posted departure while passengers are still boarding, split road vs rail (trams,
            // metros and trains all carry the Train component; ferries/aircraft take the road value). Converted to
            // frames and clamped to vanilla's own grace window: we anchor that window (see the GO branch), so we can
            // shorten it but never lengthen it beyond the game's built-in kVanillaBoardingGraceFrames.
            int maxDwellCfg = EntityManager.HasComponent<Game.Vehicles.Train>(veh) ? s.MaxDwellRail : s.MaxDwellRoad;
            if (maxDwellCfg < 0) maxDwellCfg = 0;
            uint maxDwellFrames = (uint)(maxDwellCfg * m_Fpm);
            if (maxDwellFrames > kVanillaBoardingGraceFrames) maxDwellFrames = kVanillaBoardingGraceFrames;
            bool haveSlot = m_RunSlotFrame.TryGetValue(veh, out uint slotFrame);
            string slotSrc = "keep"; // reassigned below on every path; init only to satisfy definite-assignment (catch-up branch)
            bool slotless = false; // bus with no terminus slot yet (spawned mid-route): min-dwell and GO, don't hold (#1/#6)
            if (isTerminus)
            {
                // The terminus is the anchor. (Re)assign the next scheduled departure when the bus has no slot yet, or
                // has COMPLETED a lap (it is in lapServed) AND its old slot is already past — i.e. it has come round
                // for its next run. A bus still on its FIRST slot but merely late to LEAVE (past its slot, never
                // lapped) KEEPS that slot and departs late; it must not grab a fresh one — that would be the very
                // "wait a whole cycle" bug we are fixing.
                bool lapped = lapServed != null && lapServed.Contains(veh);
                if (!haveSlot || (lapped && frame >= slotFrame))
                {
                    int untilNext = ScheduleMath.NextDeparture(s, sch, customSch, sched, nowMin) - nowMin; // minutes to next slot
                    int interval = ScheduleMath.IntervalFor(s, sch, customSch, nowMin, sched);            // active headway (hysteresis base)

                    // MISSED-TRIP CATCH-UP: has a scheduled slot passed UNCOVERED since this line last dispatched? If so,
                    // and the next slot is far enough not to bunch, min-dwell and GO now to fill the gap instead of idling
                    // to that next slot — otherwise a long-headway line stacks buses at the terminus while it runs empty.
                    bool caughtUp = false;
                    if (m_LastSlotFrame.TryGetValue(line, out uint lastSlot))
                    {
                        int prevAbs = ScheduleMath.PreviousDeparture(s, sch, customSch, sched, nowMin);
                        // A real, RECENT past slot (within one headway) — never a slot from before an off-service gap.
                        if (prevAbs != int.MinValue && nowMin - prevAbs <= maxInterval)
                        {
                            long prevFrame = (long)frame - (long)((nowMin - prevAbs) * m_Fpm);    // frame that past slot fell at
                            // "Strictly newer than our last claim". The epsilon must exceed the sub-minute jitter between
                            // the two reconstructions: nowMin truncates the fractional minute, so the SAME slot can rebuild
                            // up to a minute apart from the frame stored by the asg path. A full minute is the tight bound
                            // (real distinct slots are >= one headway apart, and the min dwell is already 2 minutes).
                            // +1 because the cast truncates m_Fpm (182.04 -> 182) while the same-slot re-derivation
                            // spread reaches the full 182.04 — without it a sliver of that range fires a false catch-up.
                            bool uncovered = prevFrame - (long)lastSlot > (long)m_Fpm + 2;
                            // The bus will not actually leave for cuDwell minutes, so the gap it really closes is
                            // (untilNext - cuDwell). Comparing the raw untilNext overstates the benefit and lets a
                            // catch-up fire that lands almost on top of the next scheduled departure.
                            // A small FIXED nudge, deliberately not the max-dwell setting: its only job is to put the
                            // catch-up slot slightly in the FUTURE so this lapped bus does not immediately re-enter the
                            // reassignment gate below and undo its own catch-up. Boarding time is bought by the
                            // anchored grace in the GO branch, not here.
                            int cuDwell = System.Math.Min(kCatchUpNudgeMinutes, maxInterval);
                            bool farEnough = untilNext - cuDwell > interval * kCatchUpMinNextLeadFrac;
                            if (uncovered && farEnough)
                            {
                                // Depart a min-dwell from NOW (like an on-slot/late bus): a near-future frame, not `frame`.
                                slotFrame = frame + (uint)(cuDwell * m_Fpm);
                                m_RunSlotFrame[veh] = slotFrame;
                                m_LastSlotFrame[line] = prevFrame > 0 ? (uint)prevFrame : frame;   // claim the slot we just covered
                                // Clear the lap flag so the reassignment gate above ((lapped && frame >= slotFrame)) does
                                // NOT re-fire while this bus is still boarding out its catch-up dwell. Without this, the
                                // moment `frame` reaches the (near-future) catch-up slot the bus re-enters this branch,
                                // finds the slot now covered, and is re-assigned to the NEXT scheduled slot — holding it a
                                // full headway and silently undoing the catch-up. The drain only re-adds to lapServed once
                                // the bus is away from the terminus (Target != terminusWaypoint), so this cannot strand it.
                                lapServed?.Remove(veh);
                                slotSrc = "catchup";
                                caughtUp = true;
                                // Log UNCONDITIONALLY (not only in the periodic route dump). A catch-up lasts a single
                                // tick, so the every-16384-frame dump almost never samples one — during live testing the
                                // feature looked like it had never fired when in fact we simply could not see it.
                                if (frame - m_LastCatchUpLog >= 2048u) // throttled: bus-lap-bounded, but a large city still logs a lot
                                {
                                    m_LastCatchUpLog = frame;
                                    Mod.log.Info($"[SelfTest] catchup line#{line.Index} veh#{veh.Index} now={nowMin}m " +
                                                 $"missedSlot={ScheduleMath.FormatHm(prevAbs)} nextIn={untilNext}m interval={interval}m");
                                }
                            }
                            else if (uncovered)
                            {
                                // A slot WAS missed but the hysteresis blocked the catch-up (the next departure is too
                                // close, so going now would bunch). Logged too: it tells us the detector is working even
                                // when it declines, which is what distinguishes "never triggers" from "triggers and is
                                // correctly conservative" while tuning kCatchUpMinNextLeadFrac.
                                if (diag != null) // throttled to the [SelfTest] dump cadence: the skip path can repeat
                                {
                                    Mod.log.Info($"[SelfTest] catchup-skip line#{line.Index} veh#{veh.Index} now={nowMin}m " +
                                                 $"missedSlot={ScheduleMath.FormatHm(prevAbs)} nextIn={untilNext}m interval={interval}m (too close)");
                                }
                            }
                        }
                    }

                    if (!caughtUp)
                    {
                        if (untilNext >= 0 && untilNext <= maxInterval)
                        {
                            slotFrame = frame + (uint)(untilNext * m_Fpm);
                            m_RunSlotFrame[veh] = slotFrame;
                            // Claim this (future) slot so the next bus doesn't read it as missed. Monotonic: never regress.
                            if (!m_LastSlotFrame.TryGetValue(line, out uint cur) || slotFrame > cur) m_LastSlotFrame[line] = slotFrame;
                            slotSrc = "asg";
                        }
                        else
                        {
                            // No usable slot soon (operating-window edge): don't latch a far/garbage slot — release now.
                            m_RunSlotFrame.Remove(veh);
                            slotFrame = frame;
                            slotSrc = "edge";
                        }
                    }
                }
                else slotSrc = "keep";
                haveSlot = true;
            }
            else if (haveSlot)
            {
                // Same run: this stop's scheduled departure is the terminus slot pushed forward by the stop's offset.
                slotFrame += (uint)(offMin * m_Fpm);
                slotSrc = "run";
            }
            else
            {
                // Downstream bus with NO terminus slot yet. It entered the loop MID-ROUTE: the game spawns a new line
                // vehicle at the nearest reachable waypoint from the depot (SetupTargetType.RouteWaypoints), NOT at the
                // terminus, then flags it EnRoute — so it has never anchored on the timetable grid. (Same for the first
                // tick after enabling, or a cleared slot.) Older code GUESSED "next terminus departure projected to
                // here" and HELD to it, which parked a mid-route bus at a random interior stop for up to a full headway
                // and bunched everything behind it — issue #1's "long waits at regular stops", which are exactly these
                // mid-route spawns. Instead: DON'T follow the timetable yet. Flag it slot-less so the target computation
                // gives it a plain min-dwell and releases it; it picks up its real slot the first time it boards the
                // terminus and self-corrects within one loop. This also makes "Provision fleet for real travel time"
                // safe to leave on, since the extra vehicles it adds are precisely these mid-route entrants.
                slotless = true;
                slotSrc = "slotless";
            }

            // DEPARTURE TARGET. A bus departs at:
            //   - its SLOT, if it arrived EARLY (arrival < slot): it boarded during the hold, so leave ON TIME and
            //     don't wait for a straggler (design A); OR
            //   - ARRIVAL + a minimum dwell, if it arrived ON its slot or LATE (arrival >= slot): it has had no
            //     boarding time, so give it a minimum stop to board/offload, then leave (user request).
            // i.e. depart = max(slot, arrival + minDwell), branched so "early" is strictly arrival < slot. `arrived`
            // is the frame the bus started boarding this stop (m_ArrivedFrame, stamped in the drain); fall back to now
            // for the first tick before the stamp lands. Dwell is capped at one headway so a sub-2-min line can't jam.
            uint arrived = m_ArrivedFrame.TryGetValue(veh, out uint af) ? af : frame;
            // The POSTED departure moment. Early: its slot. On-slot/late: now (it is already due). Slot-less: now.
            // Boarding time is no longer bought by padding this target — the anchored grace below buys it, which is why
            // there is no longer a min-dwell term here.
            uint target = (slotless || arrived >= slotFrame) ? arrived : slotFrame;
            bool dwelling = slotless || arrived >= slotFrame; // already due (not an early-slot hold); excluded from m_VehHeld

            long dframes = (long)target - frame;                                    // >0 -> hold/dwell; <=0 -> depart
            int until = (int)System.Math.Round((double)dframes / m_Fpm);

            // Safety net: a hold should never exceed one headway. With per-vehicle slots this rarely fires (a bus is
            // measured against its OWN near departure, not a distant clock slot), but it still catches a window-edge
            // terminus assignment or a schedule-math regression — release rather than freeze (the 6-16h kerb-freeze,
            // v0.2.1). The dwell branch is capped at one headway above, so only the slot branch can overrun.
            bool overrun = until > maxInterval;
            bool hold = dframes > 0 && !overrun;
            if (overrun && frame - m_LastClampWarn >= 16384u)
            {
                m_LastClampWarn = frame;
                Mod.log.Warn($"[SelfTest] hold clamped: until={until}m exceeds max headway={maxInterval}m at off={offMin} " +
                             $"(src={slotSrc}; window edge or a schedule-math regression) — releasing instead of freezing");
            }
            diag?.Append(" [off").Append(offMin).Append(tag).Append(':').Append(slotSrc).Append(" until").Append(until)
                .Append(hold ? (dwelling ? " DWELL]" : " HOLD]") : (overrun ? " GO-clamped]" : " GO]"));
            if (hold)
            {
                // Mark an EARLY-arrival hold at an INTERMEDIATE stop: this bus's arrival times downstream are pushed
                // later by the wait, so exclude its whole loop from the per-stop / loop measurement (breaks feedback).
                // The min-dwell case (dwelling) and the terminus timing-point hold are natural and stay measurable.
                if (!isTerminus && !dwelling) m_VehHeld.Add(veh);
                // EARLY -> hold to slot; ON-SLOT/LATE -> hold through the min-dwell. Either way write the target frame
                // AUTHORITATIVELY (overrides vanilla's unbunching-inflated value); this cannot cut a boarding short —
                // while held, StopBoarding keeps the bus for a cim walking up (m_MaxBoardingDistance != MaxValue,
                // TransportCarAISystem:1263-1265) and for a not-yet-Ready passenger (:1269-1278). Those guards are
                // bypassed only by the frame-1800 cutoff, which the GO branch below uses on purpose once time is up.
                if (pt.m_DepartureFrame != target) { pt.m_DepartureFrame = target; EntityManager.SetComponentData(veh, pt); }
            }
            else
            {
                // AT/PAST THE POSTED MINUTE: hand the vehicle back to the game, with a BOUNDED boarding grace.
                //
                // *** DESIGN DECISION — read this before changing it. Two earlier versions got it wrong in opposite
                // directions, and this is the reconciliation. ***
                //
                // The rule is: a timetabled vehicle leaves on its posted minute and never waits for a straggler who has
                // not started boarding. A real timetable does not hold the 08:15 for someone jogging up the platform.
                // What it also must not do is throw off passengers who are ALREADY boarding — and that is the trap,
                // because the base game adds a citizen to the vehicle's passenger list the moment they START WALKING
                // to it, long before they are aboard.
                //
                // History, both halves of it:
                //  - v0.2 released with frame-1. That opens only the departure-time gate and leaves BOTH of vanilla's
                //    boarding guards armed with NO BOUND, so arriving cims re-armed the hold indefinitely and the line
                //    slipped further behind on every departure. Reverted in v0.2.3.
                //  - v0.2.3..v0.4.0 used frame-1800, vanilla's own anti-softlock cutoff. That clears both guards, which
                //    stops the slip — but it also CANCELS every passenger still walking to the door, dumping them back
                //    on the platform. Live report: "the bus fills up, then empties down to a certain amount, then
                //    leaves", with the queue growing at busy stations. It fired on every departure of every vehicle.
                //
                // The fix is to use vanilla's mechanism instead of overriding it. StopBoarding gives up when
                // `frame >= m_DepartureFrame + 1800` (TransportCarAISystem:1262, and identically in the Train,
                // Watercraft and Aircraft systems). That window is anchored on m_DepartureFrame — so by writing
                //     m_DepartureFrame = target + maxDwellFrames - 1800
                // the cutoff lands exactly at `target + maxDwellFrames`, i.e. at the player's max dwell. Everything in
                // between is stock game behaviour we do not touch: the widening boarding radius, the wait for
                // passengers not yet seated, late arrivals joining. A vehicle with nobody boarding still departs on its
                // posted minute (the departure time itself is already past), one with passengers boarding gets up to
                // the configured grace, and the overrun can never exceed it. Bounded, so no slip; graceful, so no
                // ejections.
                //
                // maxDwellFrames is clamped to the vanilla window above: we can only ever shorten it. At 0 this is
                // exactly the old frame-1800 behaviour, which is the honest meaning of "wait for nobody".
                long anchor = (long)target + maxDwellFrames - kVanillaBoardingGraceFrames;
                uint force = anchor > 1L ? (uint)anchor : 1u;
                // Never write a FUTURE departure frame from this branch. On the normal path target <= frame, so the
                // anchor is already in the past. But this branch is also reached by the overrun clamp above, where
                // target is far in the future — and there the anchor could land ahead of now, turning the "release
                // rather than freeze" path into a hold, which is the exact opposite of its purpose.
                if (force > frame) force = frame;
                if (pt.m_DepartureFrame > force) { pt.m_DepartureFrame = force; EntityManager.SetComponentData(veh, pt); }
            }
        }

        // Release any bus this line was holding (future m_DepartureFrame) so it departs once the timetable is switched
        // off, instead of idling at the platform until the stale scheduled frame arrives (#8).
        //
        // Deliberately frame-1 (GRACEFUL), NOT the frame-1800 cutoff the GO branch uses — do not "harmonize" them.
        // They serve opposite purposes: the GO branch is ENFORCING a timetable, so it must depart over stragglers
        // (design decision A). This path is HANDING THE LINE BACK to vanilla, so it must only undo our own hold and
        // then let normal boarding behave exactly as vanilla would. Forcing a departure here would be us overriding
        // the game on a line we no longer manage.
        private void ReleaseHeldVehicles(Entity line, uint frame)
        {
            if (!EntityManager.HasBuffer<RouteVehicle>(line))
                return;
            DynamicBuffer<RouteVehicle> vehicles = EntityManager.GetBuffer<RouteVehicle>(line, isReadOnly: true);
            uint past = frame > 1u ? frame - 1u : 1u;
            for (int v = 0; v < vehicles.Length; v++)
            {
                Entity veh = vehicles[v].m_Vehicle;
                if (veh == Entity.Null || !EntityManager.HasComponent<PublicTransport>(veh))
                    continue;
                PublicTransport pt = EntityManager.GetComponentData<PublicTransport>(veh);
                bool boarding = (pt.m_State & PublicTransportFlags.Boarding) != 0;
                bool enroute = (pt.m_State & PublicTransportFlags.EnRoute) != 0;
                if (boarding && enroute && pt.m_DepartureFrame > past) // only lower an active future hold
                {
                    pt.m_DepartureFrame = past;
                    EntityManager.SetComponentData(veh, pt);
                }
            }
        }

        // Force our line's buses to actually STOP at a stop instead of letting vanilla skip it when nobody boards or
        // alights. Vanilla only pulls a bus into a stop when PublicTransportFlags.RequireStop is set — raised by
        // ResidentAISystem when a passenger wants on/off, and read in TransportCarAISystem.CheckNavigationLanes to decide
        // skip-vs-stop. With no demand the flag stays clear and the bus rolls through, so it never enters Boarding and
        // the timetable hold can't act on it (HoldStop early-returns unless Boarding|EnRoute) — the bus then leaves
        // early. We simply OR the flag in ourselves:
        //   - the TERMINUS is forced UNCONDITIONALLY (a skipped terminus strands the schedule — it is the timing anchor);
        //   - every other stop only when `everyStop` (the player's opt-in), which trades a short dwell at empty stops for
        //     an honoured posted time at each one.
        // RequireStop is a TRANSIENT runtime flag: BeginTesting clears it at the start of each boarding test, then the
        // resident AI re-sets it if there is demand (TransportBoardingHelpers). PublicTransport.m_State IS serialized, but
        // a saved RequireStop bit self-clears at the very next BeginTesting — so forcing it is save/uninstall-safe (at
        // worst one extra stop right after an uninstall), unlike m_UnbunchingFactor which nothing ever restores. We
        // re-assert it every tick (this system runs every 8 frames vs the car AI's 16, so the set reliably lands between
        // the BeginTesting clear and the skip read), and we ONLY OR it in — never clear it — so we can never suppress a
        // stop the game genuinely wants. Scoped to THIS line's own RouteVehicles, so buses of other lines sharing a stop
        // are untouched. (The write also lands on any non-road vehicle on the line, but it is inert there — only
        // TransportCarAISystem reads RequireStop for the skip; trains/ships/planes never skip.) Returns count forced (diag).
        private int ForceStops(Entity line, Entity terminusWaypoint, bool everyStop)
        {
            if (!EntityManager.HasBuffer<RouteVehicle>(line))
                return 0;
            DynamicBuffer<RouteVehicle> vehicles = EntityManager.GetBuffer<RouteVehicle>(line, isReadOnly: true);
            int forced = 0;
            for (int v = 0; v < vehicles.Length; v++)
            {
                Entity veh = vehicles[v].m_Vehicle;
                if (veh == Entity.Null || !EntityManager.HasComponent<PublicTransport>(veh))
                    continue;
                PublicTransport pt = EntityManager.GetComponentData<PublicTransport>(veh);
                // Only a bus that is IN SERVICE (EnRoute) and currently DRIVING (not already boarding) can skip an
                // upcoming stop; leave depot-bound / boarding buses alone.
                if ((pt.m_State & PublicTransportFlags.EnRoute) == 0 || (pt.m_State & PublicTransportFlags.Boarding) != 0)
                    continue;
                // Terminus: forced whenever this bus is heading to it (its next waypoint IS the terminus). Same
                // Target==terminusWaypoint test the drain uses for lap-eligibility, so "waypoint" comparison is correct.
                bool approachingTerminus = terminusWaypoint != Entity.Null
                    && EntityManager.HasComponent<Target>(veh)
                    && EntityManager.GetComponentData<Target>(veh).m_Target == terminusWaypoint;
                if (!(everyStop || approachingTerminus))
                    continue;
                forced++;
                if ((pt.m_State & PublicTransportFlags.RequireStop) == 0)
                {
                    pt.m_State |= PublicTransportFlags.RequireStop;
                    EntityManager.SetComponentData(veh, pt);
                }
            }
            return forced;
        }

        // Drop dictionary entries whose line is no longer in the live query (deleted while enabled). Reuses `scratch`
        // to gather stale keys so removal doesn't mutate the dictionary mid-enumeration.
        private static void PruneToLive<T>(Dictionary<Entity, T> dict, HashSet<Entity> live, List<Entity> scratch)
        {
            if (dict.Count == 0)
                return;
            scratch.Clear();
            foreach (Entity key in dict.Keys)
                if (!live.Contains(key)) scratch.Add(key);
            for (int i = 0; i < scratch.Count; i++)
                dict.Remove(scratch[i]);
        }

        // Measure a line's real loop time and EMA it (this now FEEDS the real-travel-time correction, not just the
        // diagnostic). Watches the vehicle occupying the terminus boarding slot; when that front vehicle CHANGES we stamp
        // the outgoing bus's departure frame, and when a bus we previously saw depart returns to the terminus we fold its
        // span (departure -> arrival = travel + intermediate dwells, EXCLUDING the terminus hold) into the line's EMA.
        // Comparable apples-to-apples with the pathfinder estimate (ComputeStableDuration), which also excludes the
        // terminus hold. Reads the world only; the correction it feeds is applied elsewhere. See AcceptLoopSample.
        // Seed the in-memory measurement dicts from the persisted LineMeasuredTravel component the first time a line is
        // seen with no live measurement (fresh load, or a re-enabled line), so LineCorrection/fleet use the real learned
        // loop immediately instead of the cold density prior. Values are raw sim FRAMES (day-length invariant), so they
        // feed straight in; a stored count >= kMinTrustSamples is trusted at once, and any staleness (route changed while
        // disabled) self-heals via the existing reject/re-anchor path. No-op if the dicts already hold data, or the
        // component is absent or empty.
        private void RehydrateMeasured(Entity line)
        {
            if (m_LineLoopSamples.ContainsKey(line)) return;                 // already have live/seeded data
            if (!EntityManager.HasComponent<LineMeasuredTravel>(line)) return;
            LineMeasuredTravel c = EntityManager.GetComponentData<LineMeasuredTravel>(line);
            if (c.m_LoopSamples <= 0 || !(c.m_LoopEmaFrames > 0f)) return;   // nothing meaningful stored (also rejects NaN)
            m_LineLoopEma[line] = c.m_LoopEmaFrames;
            m_LineLoopMin[line] = c.m_LoopMinFrames > 0f ? c.m_LoopMinFrames : c.m_LoopEmaFrames;
            m_LineLoopSamples[line] = c.m_LoopSamples;
        }

        // Write the current in-memory measurement back into the line's LineMeasuredTravel component so it survives
        // save/load. Non-structural (SetComponentData on an existing component), and only when a value actually changed,
        // to avoid churning the chunk version every tick. No-op until the component exists (added via the ECB in
        // OnUpdate) or before the line has any loop sample.
        private void MirrorMeasured(Entity line)
        {
            if (!EntityManager.HasComponent<LineMeasuredTravel>(line)) return;
            if (!m_LineLoopSamples.TryGetValue(line, out int samples) || samples <= 0) return;
            float ema = m_LineLoopEma.TryGetValue(line, out float e) ? e : 0f;
            float min = m_LineLoopMin.TryGetValue(line, out float m) ? m : 0f;
            ushort s = samples > ushort.MaxValue ? ushort.MaxValue : (ushort)samples;
            LineMeasuredTravel cur = EntityManager.GetComponentData<LineMeasuredTravel>(line);
            if (cur.m_LoopEmaFrames == ema && cur.m_LoopMinFrames == min && cur.m_LoopSamples == s) return; // unchanged
            EntityManager.SetComponentData(line, new LineMeasuredTravel { m_LoopEmaFrames = ema, m_LoopMinFrames = min, m_LoopSamples = s });
        }

        // Clean uninstall (Options button): wipe every trace of the mod from the current save. For each timetabled line
        // revert the mutated vanilla state (restore the unbunching factor, release any held bus, drop the mod-applied
        // vehicle count) and REMOVE the mod's serialized components (TimetableSchedule, CustomPeakSchedule,
        // LineMeasuredTravel), then forget all in-memory tracking. After this the save contains no mod data, so the
        // player can save and remove the mod with zero residue. Structural removes go through an ECB (played back after
        // the read pass). Safe to run with the mod still installed — the lines just go back to plain vanilla.
        private void CleanUninstall(uint frame)
        {
            NativeArray<Entity> lines = m_LineQuery.ToEntityArray(Allocator.Temp);
            EntityCommandBuffer ecb = new EntityCommandBuffer(Allocator.Temp);
            int n = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                Entity line = lines[i];
                if (EntityManager.HasComponent<TransportLine>(line))
                    RestoreUnbunching(line, EntityManager.GetComponentData<TransportLine>(line));
                ReleaseHeldVehicles(line, frame);
                // Repair rather than blind-clear. TryClearLineFleet would zero the VehicleInterval slot AND deactivate the
                // vehicle-count policy on EVERY line — including lines this mod never sized (ManageVehicleCount off, or a
                // count the player set by hand), silently wiping their own "Assigned Vehicles". The heal instead rebuilds
                // the slot from the line's OWN active policies, so a mod-written orphan reverts to automatic while a
                // genuine player/policy count is preserved. No-op on a line we never touched.
                m_Fleet.TryHealLeftoverFleetModifier(line);
                ecb.RemoveComponent<TimetableSchedule>(line);
                if (EntityManager.HasComponent<CustomPeakSchedule>(line)) ecb.RemoveComponent<CustomPeakSchedule>(line);
                if (EntityManager.HasComponent<LineMeasuredTravel>(line)) ecb.RemoveComponent<LineMeasuredTravel>(line);
                n++;
            }
            ecb.Playback(EntityManager);
            ecb.Dispose();
            lines.Dispose();
            // Forget ALL in-memory tracking so nothing re-applies to the now-vanilla lines.
            m_LastFleet.Clear(); m_PendingRetire.Clear(); m_LapServed.Clear(); m_LapFront.Clear();
            m_LineLoopEma.Clear(); m_LineLoopSamples.Clear(); m_LineLoopMin.Clear(); m_LineRejectStreak.Clear();
            m_LastDur.Clear(); m_DurStable.Clear(); m_LastSlotFrame.Clear();
            m_RunSlotFrame.Clear(); m_ArrivedFrame.Clear(); m_VehTerminusDepart.Clear(); m_VehLastRecordedStop.Clear();
            m_VehHeld.Clear(); m_StopOffsetEma.Clear(); m_StopOffsetSamples.Clear(); m_Committed.Clear();
            Mod.log.Info($"[SelfTest] clean uninstall: reverted {n} line(s) to vanilla and removed all mod components. " +
                         "Save your city; the mod can now be removed with no residue.");
        }

        private void MeasureLap(Entity line, Entity terminusStop, uint frame, float durUnits)
        {
            Entity curFront = Entity.Null;
            if (terminusStop != Entity.Null && EntityManager.HasComponent<BoardingVehicle>(terminusStop))
            {
                Entity f = EntityManager.GetComponentData<BoardingVehicle>(terminusStop).m_Vehicle;
                if (f != Entity.Null && EntityManager.HasComponent<PublicTransport>(f))
                {
                    PublicTransport pt = EntityManager.GetComponentData<PublicTransport>(f);
                    if ((pt.m_State & PublicTransportFlags.Boarding) != 0 && (pt.m_State & PublicTransportFlags.EnRoute) != 0)
                        curFront = f; // a serving bus is boarding the terminus right now
                }
            }

            m_LapFront.TryGetValue(line, out Entity prevFront);
            if (curFront == prevFront)
                return; // no change at the terminus slot this tick — nothing to measure

            if (prevFront != Entity.Null)
            {
                m_VehTerminusDepart[prevFront] = frame; // the previous front just vacated the terminus — a fresh loop begins
                m_VehHeld.Remove(prevFront);            // clear the early-held flag; measure this new loop clean
                m_VehLastRecordedStop.Remove(prevFront);
            }

            if (curFront != Entity.Null
                && m_VehTerminusDepart.TryGetValue(curFront, out uint dep) && frame > dep
                && !m_VehHeld.Contains(curFront))       // FEEDBACK GUARD: only trust a loop the bus ran WITHOUT an early hold
            {
                uint loop = frame - dep; // this bus's own departure -> return span (one full serving loop)
                if (loop >= kMinLoopFrames && loop <= kMaxLoopFrames && AcceptLoopSample(line, loop, durUnits))
                {
                    if (m_LineLoopSamples.TryGetValue(line, out int n) && n > 0)
                        m_LineLoopEma[line] += kLoopAlpha * (loop - m_LineLoopEma[line]);
                    else
                        m_LineLoopEma[line] = loop;
                    m_LineLoopSamples[line] = (m_LineLoopSamples.TryGetValue(line, out int nn) ? nn : 0) + 1;
                }
            }

            m_LapFront[line] = curFront;
        }

        // Reject implausible loop samples so the measurement survives BUNCHING. On a busy line a bus can roll through the
        // terminus while another occupies the boarding slot, so its pass is missed and the NEXT detected span is a
        // DOUBLE (~2x the real loop — this is what made #991323 read up to 4.75x live). Key insight: a missed pass makes
        // a span a MULTIPLE of the truth, never a fraction, so the TRUE single loop is the MINIMUM of the spans. We gate
        // against a running MIN rather than the EMA: the min drops freely toward the truth (a genuine lower value always
        // lowers the anchor), and anything well above it (a double-count or a stall) is rejected. Unlike an EMA-keyed
        // band this cannot be self-poisoned. A RUN of rejections means the anchor itself is stale — a glitch-low first
        // sample pinned it, or a route edit lengthened the loop — so we re-anchor upward and recalibrate (heals both).
        private bool AcceptLoopSample(Entity line, uint loop, float durUnits)
        {
            float estFrames = durUnits * 60f; // a route "duration unit" is 60 sim frames
            if (estFrames > 1f && (loop < 0.40f * estFrames || loop > 4.5f * estFrames))
                return false; // physically absurd vs the estimate — never trust it

            if (!m_LineLoopMin.TryGetValue(line, out float min))
            {
                m_LineLoopMin[line] = loop;      // first candidate seeds the anchor
                m_LineRejectStreak.Remove(line);
                return true;
            }
            if (loop <= min)
            {
                m_LineLoopMin[line] = loop;      // a lower true value — follow the anchor down
                m_LineRejectStreak.Remove(line);
                return true;
            }
            if (loop <= 1.6f * min)
            {
                m_LineRejectStreak.Remove(line); // a normal single near the min
                return true;
            }
            // loop > 1.6x min: a double-count or stall. Reject — unless it keeps happening, in which case the anchor is
            // stale (route lengthened, or the min was a glitch): re-anchor to this sample and recalibrate the value.
            int streak = (m_LineRejectStreak.TryGetValue(line, out int rs) ? rs : 0) + 1;
            if (streak >= kResetAfterRejects)
            {
                m_LineLoopMin[line] = loop;
                m_LineRejectStreak.Remove(line);
                m_LineLoopEma.Remove(line);      // old baseline was stale — re-measure the value from scratch
                m_LineLoopSamples.Remove(line);
                return true;
            }
            m_LineRejectStreak[line] = streak;
            return false;
        }

        // The per-line real-travel-time correction factor (dimensionless, RT-invariant): (real loop) / (estimated loop).
        // Uses the LIVE measurement once the line has logged enough clean loops; until then, the stop-density prior as a
        // cold-start seed. Clamped for safety (grow-only for fleet). durUnits is the line's estimated loop in route units.
        // Public so the panel/board (TransitParamsUISystem) can post the same corrected times the holds use.
        public float LineCorrection(Entity line, float durUnits, bool forFleet)
        {
            float estFrames = durUnits * 60f;
            float factor;
            if (m_LineLoopSamples.TryGetValue(line, out int n) && n >= kMinTrustSamples
                && m_LineLoopEma.TryGetValue(line, out float ema) && estFrames > 1f)
                factor = ema / estFrames;                                            // measured (frames/frames)
            else
                factor = ScheduleMath.DensityPriorRatio(CountStops(line), durUnits); // bootstrap from stop density
            return ScheduleMath.ClampCorrection(factor, forFleet);
        }

        // True once the line's correction is driven by LIVE measurement (>= kMinTrustSamples clean loops) rather than the
        // density prior — used by the panel to label the real-loop figure "measured" vs "estimated".
        public bool LineCorrectionMeasured(Entity line)
            => m_LineLoopSamples.TryGetValue(line, out int n) && n >= kMinTrustSamples;

        // Count the line's boarding stops (route waypoints connected to a stop platform), for the density prior. Matches
        // how FindTerminus / HoldAllStops identify a "stop" (a Connected waypoint whose target has a BoardingVehicle).
        private int CountStops(Entity line)
        {
            if (!EntityManager.HasBuffer<RouteWaypoint>(line))
                return 0;
            DynamicBuffer<RouteWaypoint> wps = EntityManager.GetBuffer<RouteWaypoint>(line, isReadOnly: true);
            int count = 0;
            for (int i = 0; i < wps.Length; i++)
            {
                Entity wp = wps[i].m_Waypoint;
                if (EntityManager.HasComponent<Connected>(wp))
                {
                    Entity st = EntityManager.GetComponentData<Connected>(wp).m_Connected;
                    if (st != Entity.Null && EntityManager.HasComponent<BoardingVehicle>(st))
                        count++;
                }
            }
            return count;
        }

        // Learn a stop's real arrival offset (frames from the terminus departure) from a boarding bus — but ONLY our own
        // line's bus, and ONLY if it ran this loop UNHELD (m_VehHeld). An unheld bus's arrival reflects real travel +
        // natural dwell, never the mod's holds, so this cannot feed back. Recorded once per arrival (m_VehLastRecordedStop).
        private void RecordStopOffset(Entity line, Entity wp, Entity veh, HashSet<Entity> lineVehicles, uint frame)
        {
            if (veh == Entity.Null || (lineVehicles != null && !lineVehicles.Contains(veh)))
                return;                                                              // foreign / no bus
            if (m_VehHeld.Contains(veh))
                return;                                                              // early-held this loop -> arrival inflated
            if (!m_VehTerminusDepart.TryGetValue(veh, out uint term) || frame <= term)
                return;                                                              // need a known terminus departure this run
            if (!m_ArrivedFrame.TryGetValue(veh, out uint arrived) || arrived <= term)
                return;                                                              // need its arrival time at this stop
            if (m_VehLastRecordedStop.TryGetValue(veh, out Entity last) && last == wp)
                return;                                                              // already recorded this arrival
            m_VehLastRecordedStop[veh] = wp;
            float offset = arrived - term;                                           // pure arrival offset from terminus (frames)
            if (offset < 1f)
                return;
            // Plausibility: a missed upstream-arrival detection could make one "offset" span extra ground; reject an
            // offset larger than the whole measured loop (with margin) so a glitch can't poison a stop.
            if (m_LineLoopEma.TryGetValue(line, out float loopF) && loopF > 1f && offset > 1.25f * loopF)
                return;
            if (m_StopOffsetSamples.TryGetValue(wp, out int n) && n > 0)
                m_StopOffsetEma[wp] += kLoopAlpha * (offset - m_StopOffsetEma[wp]);
            else
                m_StopOffsetEma[wp] = offset;
            m_StopOffsetSamples[wp] = (m_StopOffsetSamples.TryGetValue(wp, out int nn) ? nn : 0) + 1;
        }

        // Measured posted offset (minutes from the terminus) for a stop waypoint, once it has enough clean samples.
        // Public so the UI board posts the SAME per-stop times the holds use. False -> caller uses the estimate.
        public bool TryStopOffsetMinutes(Entity wp, out int minutes)
        {
            minutes = 0;
            if (m_StopOffsetSamples.TryGetValue(wp, out int n) && n >= kMinTrustSamples
                && m_StopOffsetEma.TryGetValue(wp, out float f) && m_Fpm > 0.01f)
            {
                minutes = (int)System.Math.Round(f / m_Fpm);
                return true;
            }
            return false;
        }

        // Resolve the line's terminus stop and its waypoint: the player-chosen stop if set and valid, otherwise the
        // first stop on the line that has a boarding slot.
        private void FindTerminus(Entity line, TimetableSchedule sch, out Entity stop, out Entity waypoint)
        {
            stop = Entity.Null;
            waypoint = Entity.Null;
            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, isReadOnly: true);

            if (sch.m_TerminusStop != Entity.Null && EntityManager.Exists(sch.m_TerminusStop)
                && EntityManager.HasComponent<BoardingVehicle>(sch.m_TerminusStop))
            {
                for (int j = 0; j < waypoints.Length; j++)
                {
                    Entity wp = waypoints[j].m_Waypoint;
                    if (EntityManager.HasComponent<Connected>(wp)
                        && EntityManager.GetComponentData<Connected>(wp).m_Connected == sch.m_TerminusStop)
                    {
                        stop = sch.m_TerminusStop;
                        waypoint = wp;
                        return;
                    }
                }
            }

            for (int j = 0; j < waypoints.Length; j++)
            {
                Entity wp = waypoints[j].m_Waypoint;
                if (!EntityManager.HasComponent<Connected>(wp))
                    continue;
                Entity s = EntityManager.GetComponentData<Connected>(wp).m_Connected;
                if (s != Entity.Null && EntityManager.HasComponent<BoardingVehicle>(s))
                {
                    stop = s;
                    waypoint = wp;
                    return;
                }
            }
        }

        // ONE-TIME-PER-LOAD global repair of the unbunching residue an OLD version (before v0.2.3) of this mod left in
        // saves. That version set TransportLine.m_UnbunchingFactor = 0 on the lines it managed (to stop buses idling
        // mid-route). That field is SERIALIZED into the save and vanilla NEVER restores it, so an affected line — even
        // after the mod is removed or the line is un-timetabled — leaves vehicles unable to unbunch: they depart a stop
        // immediately regardless of spacing (RouteUtils.CalculateDepartureFrame multiplies the hold by this factor).
        // RestoreUnbunching (below) only heals lines that STILL carry a TimetableSchedule (they're in m_LineQuery); this
        // sweep covers EVERY line so a re-subscribe + load + save repairs any affected save with no per-line steps.
        //
        // CONSERVATIVE: only a factor of EXACTLY 0 (the value the old version wrote) is healed, restored to the LINE
        // PREFAB's own m_DefaultUnbunchingFactor; a prefab whose default is genuinely 0 is skipped (nothing to restore).
        // A no-op on a healthy save. Vanilla has no path to a 0 factor (it only ever inits from the prefab default) and
        // no known mod writes one, so the sole theoretical collision — another mod deliberately setting 0 to disable
        // unbunching — would also be reset here; that is an accepted, vanishingly rare trade-off.
        private void GlobalHealUnbunching()
        {
            if (m_HealQuery.IsEmptyIgnoreFilter)
                return;
            // When the master switch is OFF, the dispatch loop manages NO line (every line takes the "hand back to
            // vanilla" branch), so a still-timetabled line's leftover fleet modifier would otherwise be skipped by the
            // `managed` guard below and never cleared on a disabled load. Treat master-off as "nothing managed".
            Setting master = Mod.ActiveSetting;
            bool masterOn = master != null && master.Enabled;
            NativeArray<Entity> lines = m_HealQuery.ToEntityArray(Allocator.Temp);
            int factorHealed = 0, fleetHealed = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                Entity line = lines[i];

                // (1) Unbunching-factor residue (a pre-v0.2.3 version wrote 0f): restore to the prefab default. Only the
                // exact damage (== 0) is touched; a healthy/custom value and a line type whose default is 0 are left alone.
                TransportLine tl = EntityManager.GetComponentData<TransportLine>(line);
                if (tl.m_UnbunchingFactor == 0f && EntityManager.HasComponent<PrefabRef>(line))
                {
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
                    if (EntityManager.HasComponent<TransportLineData>(prefab))
                    {
                        float def = EntityManager.GetComponentData<TransportLineData>(prefab).m_DefaultUnbunchingFactor;
                        if (def != 0f)
                        {
                            tl.m_UnbunchingFactor = def;
                            EntityManager.SetComponentData(line, tl);
                            factorHealed++;
                        }
                    }
                }

                // (2) Leftover fleet (VehicleInterval) RouteModifier — the actual cause of issue #7's "vehicles leave
                // immediately" after a plain uninstall. Skip lines we are ACTIVELY managing (an enabled timetable): the
                // dispatch loop re-asserts their modifier this same tick, and TryHealLeftoverFleetModifier is safe by
                // recomputing from the line's own policies (never clobbers a player's manual vehicle count).
                // "managed" also requires ManageVehicleCount: with it OFF the dispatch loop does NOT re-assert the
                // fleet modifier, so a still-timetabled line's leftover VehicleInterval residue (written while it was
                // ON) must be healed on load instead of skipped — otherwise it freezes in the save (the issue-#7 class
                // of bug the master-toggle review caught).
                bool managed = masterOn && master.ManageVehicleCount
                    && EntityManager.HasComponent<TimetableSchedule>(line)
                    && EntityManager.GetComponentData<TimetableSchedule>(line).m_Enabled;
                if (!managed && m_Fleet.TryHealLeftoverFleetModifier(line))
                    fleetHealed++;
            }
            lines.Dispose();
            if (factorHealed > 0 || fleetHealed > 0)
                Mod.log.Info($"[SelfTest] global heal on load: unbunching factor restored on {factorHealed} line(s), " +
                             $"leftover fleet modifier cleared on {fleetHealed} line(s) (repairing save residue from an older version)");
        }

        // Put the line's spacing behaviour back to the prefab default. Called for EVERY timetabled line (enabled or not),
        // so it also repairs a still-timetabled line that a before-v0.2.3 version damaged. Only writes when the value
        // actually differs from the prefab default, so it is a no-op on a healthy line.
        private void RestoreUnbunching(Entity line, TransportLine tl)
        {
            if (!EntityManager.HasComponent<PrefabRef>(line))
                return;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            if (!EntityManager.HasComponent<TransportLineData>(prefab))
                return;
            float def = EntityManager.GetComponentData<TransportLineData>(prefab).m_DefaultUnbunchingFactor;
            if (tl.m_UnbunchingFactor != def)
            {
                // Logged because this is otherwise INVISIBLE: the game exposes no UI for unbunching, so without a line
                // in the log there is no way to confirm a damaged save was repaired. Fires once per line, then never.
                Mod.log.Info($"[SelfTest] unbunching restored on line#{line.Index}: {tl.m_UnbunchingFactor} -> {def} " +
                             $"(repairing a value written by an older version of this mod)");
                tl.m_UnbunchingFactor = def;
                EntityManager.SetComponentData(line, tl);
            }
        }
    }
}
