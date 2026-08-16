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
    // HEADWAY REGULATION for opted-in lines. The player states how many vehicles a line runs in each time-of-day
    // window (LineFleetPlan); the mod provisions exactly that many and then keeps them EVENLY SPACED. It owns three
    // things per managed line:
    //
    //  1. FLEET: the player's count for the current window, applied through the vanilla vehicle-count policy
    //     (HourlyFleetSystem.TrySetLineFleet). Nothing is derived from a measurement any more — the number on screen
    //     is the number the player typed.
    //  2. SPACING AT THE TIMING POINTS: at the terminus (and at an optional second timing point, "Terminus B") a
    //     vehicle is held until ONE HEADWAY after the previous vehicle left that same stop, where
    //     headway = cycle / vehicles. Arrive bunched and you wait; arrive into a gap and you go. This is the whole
    //     product.
    //  3. NO MID-ROUTE HOLDING: at every other stop the vehicle waits for boarding and alighting to finish and then
    //     leaves. The mod writes PublicTransport.m_DepartureFrame there only to CLEAR vanilla's unbunching delay —
    //     never to add one. TransportLine.m_UnbunchingFactor is deliberately left at the prefab default and never
    //     written: it is serialized, nothing in vanilla restores it, and no UI exposes it, so writing it would outlive
    //     the mod. Earlier versions zeroed it; (2) in OnUpdate now heals that.
    //
    // Runs every 8 frames so it always re-asserts before the vanilla 16-frame AI release.
    //
    // ============================ DESIGN DECISIONS — deliberate, NOT bugs ============================
    // All three look like defects to anyone (or any audit) reading them cold. Read this before changing them.
    //
    //  A. A VEHICLE AT A TIMING POINT LEAVES ON ITS HEADWAY AND DOES NOT WAIT FOR A STRAGGLER. Once its slot in the
    //     spacing arrives it goes, over a cim still walking up. Waiting would drag the line out of shape, and even
    //     spacing is the entire product. Implemented by the frame-1800 anchoring in the GO branch (see ReleaseStop).
    //     Passengers ALREADY boarding are not ejected — that is what the anchored grace buys.
    //
    //  B. AN ORDINARY STOP IS NEVER USED TO REGULATE. It would be easy (and wrong) to spread the correction across
    //     every stop instead of banking it at the ends. A vehicle held mid-route blocks the kerb for other lines,
    //     strands its own passengers for no gain, and — because the correction is applied where the vehicle has the
    //     least information — tends to overshoot and create the next bunch. Real operators regulate at terminals and
    //     a small number of named timing points, and so does this.
    //
    //  C. SURPLUS VEHICLES FINISH THEIR LOOP — THEY NEVER ABANDON MID-ROUTE. When the player's count steps down
    //     (peak -> off-peak) vanilla flags the highest-ODOMETER vehicles and would retire each one wherever it
    //     stands, dumping its passengers. Block (8) strips that flag back off every tick for any vehicle not on its
    //     final approach, so it keeps serving; it may only go once it is back at the terminus, has completed a full
    //     serving lap (m_LapServed), and another vehicle is covering the terminus. That is what stops the
    //     deploy-then-instantly-recall yo-yo.
    // ===============================================================================================
    public partial class TimetableDispatchSystem : GameSystemBase
    {
        private SimulationSystem m_Sim;
        private TimeSystem m_Time;
        private HourlyFleetSystem m_Fleet;
        private TimebaseSystem m_Timebase;
        // Per-tick snapshot of the runtime frame<->minute scale (from TimebaseSystem): frames per in-game minute and
        // in-game minutes per route "duration unit". Snapshotted once at the top of OnUpdate so all math in a tick uses
        // one consistent value; the helpers read these fields directly (no signature churn).
        private float m_Fpm;
        private float m_Um;
        // Last day-length "regime" we saw. When TimebaseSystem's generation changes (a real day-length change, e.g. a
        // slow-time mod toggled), every stored FRAME span was scaled by the OLD frames/minute, so drop the ones that
        // are only meaningful relative to the clock and let them be re-observed.
        private uint m_TimebaseGen;
        private EntityQuery m_LineQuery;
        // ALL transport lines (managed or not), for the one-time-per-load unbunching-residue repair. Separate from
        // m_LineQuery because that one requires TimetableSchedule and so misses lines that were damaged by an old
        // version and are no longer managed. See GlobalHealUnbunching.
        private EntityQuery m_HealQuery;
        // Lines carrying per-stop boarding rules, managed or not — the clean-uninstall sweep (see OnCreate).
        private EntityQuery m_StopRuleQuery;
        // Set on every game/save load; the next OnUpdate runs the global unbunching heal once, then clears it. Init true
        // so the sweep still fires if the system is created AFTER OnGameLoadingComplete already ran (mod-loads-late).
        private bool m_GlobalHealPending = true;
        // ---- One-time "this mod works differently now" notice ----
        // Armed on a city load, decided on the next tick (ECS reads are only safe there), then handed to the UI system
        // which raises the dialog. Same pending-flag shape as m_GlobalHealPending above, for the same reason.
        //
        // WHY IT EXISTS: every previous version asked for a HEADWAY and worked out the vehicle count itself. This one
        // asks for the VEHICLE COUNT and works out the headway. A player who loads an existing city finds the panel
        // showing different controls and their lines converted; that is not something to do silently, however much
        // better the new model is.
        private bool m_NoticeCheckPending = true;
        // Set while the notice is waiting to be answered: the fleet WRITE is suppressed so we do not change the city
        // before the player has read it. Only the write — desiredFleet is still computed every tick, because the drain
        // below derives `surplus` from it and a zero there disables the mid-route AbandonRoute protection (that exact
        // mistake was live-reported as buses VANISHING mid-route).
        public static bool NoticeAwaitingAnswer { get; private set; }
        // ...but never forever. If the dialog cannot render — a cs2/ui shape change across a game patch, caught by the
        // Safe boundary — nobody can answer, and without this the mod would silently stop sizing fleets for the whole
        // session with no error anywhere. Give up after a bounded wait and resume normal behaviour instead.
        // Ticks, not frames: OnUpdate runs every 8 frames and does NOT tick while the game is paused, so a player who
        // pauses to read the dialog (or to translate it) cannot burn the budget just by taking their time.
        private const int kNoticeAnswerTimeoutTicks = 1800;
        private int m_NoticeTimeout;
        // Set by the Options "clean uninstall" button (UI thread); consumed once on the next simulation tick to strip
        // every mod component + mutation from the save so the player can remove the mod with no residue. volatile:
        // written on the UI thread, read on the sim thread.
        private static volatile bool s_cleanUninstallPending;
        public static void RequestCleanUninstall() => s_cleanUninstallPending = true;
        // Requests a heal sweep from outside the simulation tick (the notice dialog's answer). volatile for the same
        // reason as the flag above: written on the UI thread, read on the sim thread.
        private static volatile bool s_healRequest;
        private readonly Dictionary<Entity, int> m_LastFleet = new Dictionary<Entity, int>();
        // Flood-on-load guard. The raw line-duration estimate (m_Fleet.LineStableDurationUnits) is transiently wrong
        // for the first ticks after a save loads: the game re-paths the line and zeroes each line's transport speed at
        // the top of every tick before re-filling it, and a segment path's duration scales inversely with speed, so the
        // estimate spikes (users saw ~10x) then settles. The vehicle COUNT is the player's now and cannot be flooded by
        // that — but TrySetLineFleet converts the count into a VehicleInterval using this duration, so writing during
        // the spike hands vanilla an interval that resolves to the wrong count until it settles. m_LastDur/m_DurStable
        // require the estimate to agree with the previous tick (within 5%) for kDurStableTicks consecutive ticks before
        // we act on it. Purely transient (rebuilt from live reads; nothing here is serialized).
        private readonly Dictionary<Entity, float> m_LastDur = new Dictionary<Entity, float>();
        private readonly Dictionary<Entity, int> m_DurStable = new Dictionary<Entity, int>();
        private const int kDurStableTicks = 3;
        // Per line: vehicles the game flagged to retire that we're driving to the terminus before letting them go.
        private readonly Dictionary<Entity, HashSet<Entity>> m_PendingRetire = new Dictionary<Entity, HashSet<Entity>>();
        // Per line: vehicles seen AWAY from the terminus (serving the loop) since they appeared — i.e. that have earned
        // a full loop and may now retire on their next return. A freshly-deployed vehicle is absent from this set until
        // it leaves the terminus, so it always completes one serving lap before it can be recalled.
        private readonly Dictionary<Entity, HashSet<Entity>> m_LapServed = new Dictionary<Entity, HashSet<Entity>>();
        // Vehicles whose retirement we have COMMITTED to (flag asserted at the terminus with the terminus covered).
        // Lets the decision survive the brief ticks where nothing is boarding the terminus, without falling back to the
        // old "any vanilla-flagged vehicle may retire" rule that bypassed the covered check entirely. Keyed by vehicle;
        // pruned against the live-vehicle set each tick. Transient — nothing here is serialized.
        private readonly HashSet<Entity> m_Committed = new HashSet<Entity>();
        // STAGGERED FLEET GROWTH: per LINE, the sim FRAME of the last accepted one-vehicle ramp step.
        //
        // Vanilla does not store a vehicle count. TransportLineSystem re-derives it every 256 frames from the line's
        // VehicleInterval — count = round(stableDuration / interval) (TransportLineSystem.CalculateVehicleCount) — and
        // this mod steers exactly that number by writing the line's VehicleInterval RouteModifier
        // (HourlyFleetSystem.TrySetLineFleet). So the instant the player's window count moves 4 -> 12, vanilla wants
        // eight more vehicles AT ONCE.
        //
        // It does not spawn them in one frame: transportLine.m_VehicleRequest is a SINGLE entity field, and
        // RequestNewVehicleIfNeeded only creates the next request once the previous one has been dispatched
        // (TransportLineSystem.CheckRequests). But that loop turns over once per 256-frame tick, far shorter than any
        // real headway — so the depot empties eight vehicles back-to-back and they enter the line as ONE CLUMP. That is
        // precisely the bunching this mod exists to remove, created by the mod itself, and the timing points then have
        // to spend laps unwinding it.
        //
        // So: move the count ONE VEHICLE PER HEADWAY. Vanilla's own one-request-at-a-time path then spaces the arrivals
        // for free — no depot control, no new component written, nothing added to the save. The rate is not a throttle
        // for its own sake: a line can only absorb one extra vehicle per headway without bunching, so this IS the
        // maximum useful rate. (As the count climbs the headway shrinks, so the ramp accelerates itself.)
        //
        // GROWTH ONLY — deliberately NOT symmetric. Shrinking is already handled, and better, by the drain in (8),
        // which releases at most one vehicle per terminus departure: the same one-per-headway pacing arrived at from
        // the departure side rather than the count side.
        //
        // Deliberately does NOT apply to a line with no applied count yet (no m_LastFleet entry): that is INITIAL
        // provisioning, where there is no established service to bunch against and ramping would instead leave a new
        // line running one vehicle for many headways. First sizing lands whole; only subsequent CHANGES are staggered.
        private readonly Dictionary<Entity, uint> m_RampSince = new Dictionary<Entity, uint>();
        // ---- DEPOT LEAD: get the vehicle to the terminus BY the minute it is needed, not on its way then ----
        //
        // Vanilla owns the depot. The mod only steers the COUNT, and the moment the count rises vanilla dispatches a
        // vehicle which then has to DRIVE from the depot to the line. Raising the count at 06:00 for a 06:00 peak
        // therefore misses it by the length of that drive.
        //
        // So measure the drive and spend it. m_VehFirstSeen stamps the frame a vehicle first appears in a line's
        // RouteVehicle buffer; when it first boards the terminus the span between the two IS the depot lead, and it
        // goes into a per-line EMA. The fleet look-ahead then sizes the line for the LARGEST count it will want within
        // one lead from now, so the dispatch happens early enough for the vehicle to be standing at the terminus on the
        // minute. Nothing about the spacing moves: the look-ahead is used only where the count is chosen.
        private readonly Dictionary<Entity, uint> m_VehFirstSeen = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, float> m_LineDepotLead = new Dictionary<Entity, float>();
        // Every vehicle we have already seen on some line. A vehicle absent from this set is genuinely NEW — just
        // dispatched — which is what makes m_VehFirstSeen a depot stamp rather than a "we started looking" stamp.
        private readonly HashSet<Entity> m_KnownVeh = new HashSet<Entity>();
        // On a load every vehicle is new to us but none of them came from a depot just now, and timing them from the
        // load to their next terminus would fold most of a LOOP into the lead. So the first tick after a load only
        // takes the census; measurement starts from the tick after that.
        private bool m_VehCensusPending = true;
        // Sanity bound on a single sample and on the applied lead. A depot run is minutes; anything beyond this is a
        // vehicle that was already out (a missed census, a route edit mid-drive) and must not skew the EMA.
        private const float kMaxDepotLeadMinutes = 45f;
        // Reused scratch for pruning the dicts against the live query (a line bulldozed while enabled leaves the
        // query without hitting the disable branch, so its keys would otherwise leak). Members = no per-update alloc.
        private readonly HashSet<Entity> m_LiveScratch = new HashSet<Entity>();
        private readonly HashSet<Entity> m_LiveVehScratch = new HashSet<Entity>();
        private readonly List<Entity> m_StaleScratch = new List<Entity>();
        private uint m_LastLog;

        // ===================== THE SPACING STATE — this is the heart of the mod =====================
        // Per LINE, the sim FRAME at which a vehicle last LEFT each timing point. A vehicle boarding that stop is held
        // until lastDeparture + headway, which is the entire regulation rule. A FRAME (not a minute) so comparisons are
        // monotonic across midnight and immune to the clock being stretched by a slow-time mod.
        //
        // "Left" is detected as a change of the stop's BoardingVehicle occupant, the same observation MeasureLap makes
        // for the loop timing — one mechanism, two consumers, so they can never disagree about when a vehicle departed.
        private readonly Dictionary<Entity, uint> m_LastDepA = new Dictionary<Entity, uint>();   // line -> frame at terminus A
        private readonly Dictionary<Entity, uint> m_LastDepB = new Dictionary<Entity, uint>();   // line -> frame at Terminus B
        private readonly Dictionary<Entity, Entity> m_FrontB = new Dictionary<Entity, Entity>(); // line -> vehicle boarding B now
        // The headway the line is currently being regulated to, in FRAMES. Published for the panel and the stop board,
        // which must never re-derive it (two independent derivations of the same number is exactly how the old printed
        // board came to disagree with the vehicles by ~45 minutes).
        private readonly Dictionary<Entity, float> m_LineHeadway = new Dictionary<Entity, float>();
        // Per VEHICLE: the frame we are holding it until at a timing point (absent = not being held), and the gap it
        // actually achieved when it last left terminus A. Both exist for the vehicle info panel, which is the only
        // place a player can see whether the regulation is working on the vehicle in front of them.
        private readonly Dictionary<Entity, uint> m_VehHoldUntil = new Dictionary<Entity, uint>();
        private readonly Dictionary<Entity, uint> m_VehGap = new Dictionary<Entity, uint>();
        // Absolute bounds on the derived headway, in MINUTES, so a wild loop measurement can never freeze a line at a
        // kerb for hours (the v0.2.1 bug class) nor collapse the regulation into a no-op.
        private const float kMinHeadwayMinutes = 0.5f;
        private const float kMaxHeadwayMinutes = 180f;

        // Minimum stop dwell for a vehicle that has already satisfied its headway, so it still boards/offloads instead
        // of being force-departed the instant it pulls in. Vanilla's own boarding grace: StopBoarding gives up and
        // departs when frame >= m_DepartureFrame + this (TransportCarAISystem:1262, and byte-identical in the Train
        // :1068, Watercraft :804 and Aircraft :833 systems, so one constant covers every transport type). At the
        // vanilla clock this is ~9.9 in-game minutes. We do not fight this window, we ANCHOR it — see ReleaseStop — so
        // the player's "maximum stop time" becomes the moment it expires. It is therefore also the hard ceiling on that
        // setting.
        private const uint kVanillaBoardingGraceFrames = 1800u;
        // The frame each vehicle started boarding its CURRENT stop. Presence == "boarding now"; stamped on the first
        // tick boarding and dropped when it leaves (so the same stop next loop re-stamps). Feeds the regulation.
        private readonly Dictionary<Entity, uint> m_ArrivedFrame = new Dictionary<Entity, uint>();

        // ===== The measured loop: what the headway is computed FROM =====
        // Measures each vehicle's terminus DEPARTURE -> next terminus ARRIVAL span (travel + intermediate dwells,
        // EXCLUDING the terminus hold) so it is directly comparable to the game's estimate (ComputeStableDuration).
        // This is no longer an optional realism nicety: headway = cycle / vehicles, so the loop IS the headway.
        private readonly Dictionary<Entity, Entity> m_LapFront = new Dictionary<Entity, Entity>();        // line -> vehicle at its terminus now
        private readonly Dictionary<Entity, uint>   m_VehTerminusDepart = new Dictionary<Entity, uint>(); // vehicle -> frame it last left the terminus
        private readonly Dictionary<Entity, float>  m_LineLoopEma = new Dictionary<Entity, float>();      // line -> EMA of measured loop frames
        // ROLLING WINDOW of recent accepted laps, and its MEDIAN. The median is what the headway uses once a line has
        // enough laps, because the three candidate estimators fail in different directions:
        //   mean/EMA  every double drags it up permanently
        //   minimum   one unusually FAST lap redefines the line, then ordinary laps look like doubles and the
        //             re-anchor path fires. Observed live on line#1349029: the correction jumped 1.79 -> 2.68 and
        //             discarded 68 samples in one step.
        //   median    a double must become the MAJORITY before it moves the answer
        // KNOWN LIMIT: if a line is so busy that OVER HALF its readings are doubles, the median is captured too. The
        // anchor is still tracked for precisely that reason — median ~= 2x anchor is the tell.
        private readonly Dictionary<Entity, List<float>> m_LineLoopWindow = new Dictionary<Entity, List<float>>();
        private readonly Dictionary<Entity, float>  m_LineLoopMedian = new Dictionary<Entity, float>();
        private const int kLoopWindow      = 32;   // laps retained per line
        private const int kMedianMinSample = 10;   // below this a median is no better than the anchor — see LineCorrection
        private readonly Dictionary<Entity, int>    m_LineLoopSamples = new Dictionary<Entity, int>();    // line -> loop samples so far
        private readonly Dictionary<Entity, float>  m_LineLoopMin = new Dictionary<Entity, float>();      // line -> running MIN loop (the true single loop; doubles sit above it)
        private readonly Dictionary<Entity, int>    m_LineRejectStreak = new Dictionary<Entity, int>();   // line -> consecutive gate rejects (drives the stale-anchor reset)
        // Frames the mod itself made a vehicle wait at an intermediate stop, so a lap can be measured despite our own
        // Terminus B layover instead of being discarded. m_VehStopHold is the CURRENT stop's hold, rewritten every tick
        // so it cannot double-count; it is folded into the per-lap total when the vehicle leaves the stop. The terminus
        // hold never enters this, because the lap is measured departure-to-arrival and excludes it by construction.
        private readonly Dictionary<Entity, uint>   m_VehStopHold = new Dictionary<Entity, uint>();   // veh -> hold at the stop it is at now
        private readonly Dictionary<Entity, uint>   m_VehHoldFrames = new Dictionary<Entity, uint>(); // veh -> hold accumulated this lap
        // Travel offset (minutes from the terminus departure) per waypoint, derived from the measured loop. NOTHING is
        // held to these any more — they exist so the stop board can say "this line reaches you about N minutes after it
        // leaves its terminus", which is what turns a bare headway into a usable arrival estimate.
        private readonly Dictionary<Entity, int>    m_PostedOffset = new Dictionary<Entity, int>();    // waypoint -> minutes after terminus departure
        // Terminus B's pre-layover ARRIVAL offset. Only that waypoint gets an entry — everywhere else arrival ==
        // departure and m_PostedOffset is the single number.
        private readonly Dictionary<Entity, int>    m_PostedArrival = new Dictionary<Entity, int>();   // Terminus B waypoint -> arrival minutes
        // The vehicle count the dispatch settled on, after the cap, the ramp and the stability gate. The panel shows
        // THIS, never its own recomputation.
        private readonly Dictionary<Entity, int>    m_PostedFleet = new Dictionary<Entity, int>();     // line -> vehicles the mod is provisioning
        private const uint  kMinLoopFrames = 1000u;      // ignore absurdly short spans (jitter / same-tick slot churn)
        private const uint  kMaxLoopFrames = 4194304u;   // ...and absurdly long ones (a loop can't exceed a stretched day)
        private const float kLoopAlpha     = 0.30f;      // EMA smoothing for the measured loop
        private const int   kMinTrustSamples = 4;        // measured loop is trusted over the density prior at >= this
        private const int   kResetAfterRejects = 4;      // consecutive rejects => the min anchor is stale (route edit / glitch): re-anchor
        // Absolute per-line vehicle sanity cap. The count is the player's now, so this is only a guard against a
        // nonsense value reaching vanilla; it sits far above any legitimate need (a long metro loop at a tight headway
        // can want well over a hundred vehicles).
        private const int   kFleetCap        = 150;

        protected override void OnCreate()
        {
            base.OnCreate();
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
            // last managed line was deleted) so the per-load one-shots still run — the unbunching-residue repair
            // (GlobalHealUnbunching, which uses its own all-lines query) and the clean-uninstall request. With
            // RequireForUpdate the system stops dead on an empty query and neither would fire. The empty loop is trivial.

            // ALL lines, for the unbunching-residue repair (no TimetableSchedule requirement — see GlobalHealUnbunching).
            m_HealQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadWrite<TransportLine>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() },
            });
            // Every line carrying stop rules, managed or not. CleanUninstall's main sweep runs over m_LineQuery,
            // which requires TimetableSchedule — so a line whose service plan was switched off AFTER a rule was set on
            // it would keep the buffer forever and the "no residue" promise would be false.
            m_StopRuleQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<LineStopRule>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() },
            });
        }

        // Does this city need the one-time "the mod works differently now" notice? Both conditions must hold:
        //
        //  1. It has not already been answered. The flag is GLOBAL rather than per-city because the change it
        //     describes is global — it is a property of the mod, not of a save.
        //  2. This city has ACTUALLY USED the mod — at least one line carries a TimetableSchedule. That component is
        //     added only when the player switches a line's plan on in the panel, never automatically, so it is a sound
        //     "existing user" signal. A brand-new city gets the new model as its baseline and is never interrupted;
        //     and because the flag is only written once the notice is ANSWERED, a player who happens to load a fresh
        //     city first still gets the notice later, when they open the city that actually has managed lines.
        private bool ShouldShowMigrationNotice(TransitTimetablesSetting s)
        {
            if (s.CountModelNoticeAnswered)
                return false;
            return !m_LineQuery.IsEmptyIgnoreFilter;
        }

        // Answer from the dialog. There is only one answer — the change has already happened, and the dialog exists to
        // explain it, not to offer a choice that no longer exists. Every dismissal path (the button, Escape, the X)
        // routes here and simply records that it has been seen, so it is never shown again.
        public static void AnswerMigrationNotice(bool enable)
        {
            NoticeAwaitingAnswer = false;
            TransitTimetablesSetting s = Mod.ActiveSetting;
            if (s == null)
                return;
            s.CountModelNoticeAnswered = true;
            Mod.SaveSettings();
            // Force a heal sweep on the next tick. Anyone arriving from an older version may carry a fleet modifier
            // that version wrote from a DERIVED count; the per-line path corrects it on the next write, but under
            // "another mod decides" there IS no next write, so the residue would sit pinned on every line and freeze
            // into the save (the issue-#7 class of bug). The sweep is idempotent and cheap.
            s_healRequest = true;
            Mod.log.Info("[SelfTest] vehicle-count model notice acknowledged");
        }

        // A save (or a new game) just finished loading: schedule the one-time global unbunching heal for the next tick,
        // so an affected save is repaired on load even for lines that are no longer managed.
        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            m_GlobalHealPending = true;
            // Re-take the vehicle census: every vehicle in the freshly loaded city is new to us but none of them was
            // just dispatched, and timing them from here would fold most of a loop into the depot lead.
            m_VehCensusPending = true;
            m_KnownVeh.Clear();
            m_VehFirstSeen.Clear();
            // Every spacing observation is a frame stamp taken before the load; none of them describe the world we are
            // about to tick. Drop them and re-observe — the first vehicle through each timing point re-seeds it.
            m_LastDepA.Clear();
            m_LastDepB.Clear();
            m_FrontB.Clear();
            m_LapFront.Clear();
            m_VehTerminusDepart.Clear();
            m_VehHoldUntil.Clear();
            m_VehGap.Clear();
            // Gate on GameMode.Game: this also fires at boot for the main menu and for the editor, where there is no
            // city to inspect and no HUD to draw a dialog over.
            m_NoticeCheckPending = mode == GameMode.Game;
            NoticeAwaitingAnswer = false;   // a fresh load re-decides; never carry a pending state across cities
            // Drop any clean-uninstall request that was armed BEFORE this city loaded. The flag is static (it must
            // survive the per-load system recreation between the button press and the next tick), and OnUpdate does not
            // tick at the main menu — so a press made outside a city would otherwise sit armed and wipe the plans of
            // whatever city is loaded NEXT. A legitimate in-game press can't be lost here: no load happens between the
            // press and its consumption on the following tick.
            s_cleanUninstallPending = false;
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 8;

        protected override void OnUpdate()
        {
            TransitTimetablesSetting s = Mod.ActiveSetting;
            if (s == null)
                return;

            // One-time-per-load repair of the unbunching residue an old version of this mod left in saves.
            if (m_GlobalHealPending || s_healRequest)
            {
                m_GlobalHealPending = false;
                s_healRequest = false;
                GlobalHealUnbunching();
            }

            // Decide whether this city earns the one-time notice. Runs here rather than in OnGameLoadingComplete
            // because it inspects the world, and entity queries are only safe on a tick.
            if (m_NoticeCheckPending)
            {
                m_NoticeCheckPending = false;
                if (ShouldShowMigrationNotice(s))
                {
                    NoticeAwaitingAnswer = true;
                    m_NoticeTimeout = kNoticeAnswerTimeoutTicks;
                    TransitParamsUISystem.RaiseMigrationNotice();
                    Mod.log.Info("[SelfTest] vehicle-count model notice raised (city has managed lines from an older version)");
                }
            }
            // Bounded wait for the answer (see kNoticeAnswerTimeoutTicks). Deliberately does NOT mark the notice as
            // answered: a player who alt-tabbed away should still be told next time, and if the dialog is genuinely
            // broken this costs one bounded delay per load rather than permanently disabling fleet sizing.
            else if (NoticeAwaitingAnswer && --m_NoticeTimeout <= 0)
            {
                NoticeAwaitingAnswer = false;
                Mod.log.Warn("[SelfTest] model-change notice was never answered; resuming normal fleet sizing");
            }

            uint frame = m_Sim.frameIndex;
            int nowMin = (int)(m_Time.normalizedTime * 1440f) % 1440;

            // Runtime frame<->minute scale (vanilla 262144 frames/day unless a slow-time mod stretches the day). One
            // consistent snapshot per tick. On a real day-length change, drop the spacing stamps that were taken
            // against the previous scale so each timing point re-observes against the new one.
            m_Fpm = m_Timebase.FramesPerMinute;
            m_Um = m_Timebase.UnitMinutes;
            uint tbGen = m_Timebase.RegimeGeneration;
            if (tbGen != m_TimebaseGen)
            {
                m_TimebaseGen = tbGen;
                m_LastDepA.Clear(); m_LastDepB.Clear(); m_VehHoldUntil.Clear(); m_VehGap.Clear();
            }

            // Clean-uninstall button (Options): one-shot wipe of every mod component + mutation, then bail this tick.
            // Runs regardless of the master switch, so a paused mod can still be cleaned out.
            if (s_cleanUninstallPending)
            {
                s_cleanUninstallPending = false;
                CleanUninstall(frame);
                return;
            }

            int minDwellA = s.MinTerminusDwell < 0 ? 0 : s.MinTerminusDwell;

            NativeArray<Entity> lines = m_LineQuery.ToEntityArray(Allocator.Temp);
            // Deferred structural changes (adding the persistence / plan components to a line that lacks them):
            // recorded during the per-line loop and played back AFTER it, so we never change an archetype mid-iteration
            // while this line's buffer/component handles are live (the ECS structural-change hazard).
            EntityCommandBuffer ecb = new EntityCommandBuffer(Allocator.Temp);
            bool anyEnabled = false;
            int enabledCount = 0;
            string sample = null;
            m_LiveVehScratch.Clear(); // repopulated in the drain below, then used to prune the per-vehicle dicts
            for (int i = 0; i < lines.Length; i++)
            {
                Entity line = lines[i];
                TimetableSchedule sch = EntityManager.GetComponentData<TimetableSchedule>(line);
                CustomPeakSchedule customSch = EntityManager.HasComponent<CustomPeakSchedule>(line)
                    ? EntityManager.GetComponentData<CustomPeakSchedule>(line) : CustomPeakSchedule.Default();
                TransportLine tl = EntityManager.GetComponentData<TransportLine>(line);

                // Master switch OFF => treat EVERY line as unmanaged: run the same "hand back to vanilla" path as a
                // line whose plan is switched off (release held vehicles, clear the fleet policy, restore unbunching),
                // without clearing the user's per-line configuration. Re-enabling resumes exactly where it left off.
                if (!s.Enabled || !sch.m_Enabled)
                {
                    RestoreUnbunching(line, tl);
                    if (m_LastFleet.ContainsKey(line))
                    {
                        // We were managing this line — hand it back EXACTLY ONCE (m_LastFleet is cleared just below, so
                        // later disabled frames skip this): release any vehicle we were holding so it departs
                        // immediately instead of idling to a stale frame (#8), and drop the mod-applied vehicle count
                        // so it does not stay frozen at the last number and persist into the save (#4).
                        //
                        // HEAL, not clear. TryClearLineFleet ALSO deactivates the line's vehicle-count policy — and
                        // that policy is where the player's own "Assigned Vehicles" number lives, so switching the mod
                        // off used to throw away a count they had set by hand. "Off" must undo what the MOD did and
                        // nothing else. The heal rebuilds the slot from the line's own policies: our orphaned delta
                        // goes, a hand-set count survives untouched, and a line with no policy reverts to automatic.
                        ReleaseHeldVehicles(line, frame);
                        m_Fleet.TryHealLeftoverFleetModifier(line);
                    }
                    m_LastFleet.Remove(line);
                    m_PostedFleet.Remove(line);   // stop advertising a count for a line we no longer size
                    m_PendingRetire.Remove(line);
                    m_LapServed.Remove(line);
                    // Drop the LIVE loop measurement and every spacing stamp. NOTE: since the measurement is persisted
                    // in LineMeasuredTravel (which is deliberately NOT removed here, so it survives a pause),
                    // re-enabling re-seeds it from the component rather than measuring from scratch — intentional. If
                    // the route changed while disabled, the stale value self-heals via the reject/re-anchor path.
                    m_LapFront.Remove(line);
                    m_FrontB.Remove(line);
                    m_LastDepA.Remove(line);
                    m_LastDepB.Remove(line);
                    m_LineHeadway.Remove(line);
                    m_LineLoopEma.Remove(line);
                    m_LineLoopWindow.Remove(line);
                    m_LineLoopMedian.Remove(line);
                    m_LineLoopSamples.Remove(line);
                    m_LineLoopMin.Remove(line);
                    m_LineRejectStreak.Remove(line);
                    m_LastDur.Remove(line);
                    m_DurStable.Remove(line);
                    m_RampSince.Remove(line);     // no applied count to ramp from; a stale timer would stall the
                                                  // first step by up to one headway when the line is re-enabled
                    continue;
                }
                anyEnabled = true;
                enabledCount++;

                // Rehydrate the measured loop from the persisted component the first time we see this line with empty
                // in-memory measurement (fresh load, or a re-enabled line), so the headway uses the real learned loop
                // immediately instead of the cold density prior. And ensure the component exists (deferred add) so the
                // mirror below has somewhere to write.
                RehydrateMeasured(line);
                if (!EntityManager.HasComponent<LineMeasuredTravel>(line))
                    ecb.AddComponent<LineMeasuredTravel>(line);

                float durUnits = m_Fleet.LineStableDurationUnits(line);

                // THE PLAN. A line saved by a version older than this one has no LineFleetPlan at all, only the legacy
                // per-window INTERVALS on its TimetableSchedule — so convert them once, here, rather than dropping the
                // player onto a default. Until the conversion lands (it is a deferred structural add) the line runs on
                // the same converted numbers, computed inline, so there is no tick where it is unmanaged.
                bool hasPlan = EntityManager.HasComponent<LineFleetPlan>(line);
                LineFleetPlan plan = hasPlan
                    ? EntityManager.GetComponentData<LineFleetPlan>(line)
                    : MigrateFleetPlan(line, sch, customSch, durUnits, log: true);
                if (!hasPlan)
                    ecb.AddComponent(line, plan);

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
                // inflates m_DepartureFrame at StartBoarding — and this system now writes m_DepartureFrame
                // authoritatively at every stop of a managed line every 8 frames, so the factor cannot affect one.
                // Leaving it alone is strictly better: an out-of-service window (day-only line at night) now unbunches
                // normally instead of staying silently crippled.
                RestoreUnbunching(line, tl);

                // The line's day/night operating schedule — which window's count applies, and whether it runs at all.
                int sched = LineSchedule.Of(EntityManager, line);

                // (3) The timing points: the terminus (player-chosen stop, or the first stop) and the optional
                // Terminus B. Both are resolved once per tick and threaded through — ForceStops must pull vehicles
                // into them even with no demand (an empty stop is otherwise rolled past, the vehicle never enters
                // Boarding, and the regulation silently does nothing).
                FindTerminus(line, sch, out Entity terminusStop, out Entity terminusWaypoint);
                bool hasB = TryActiveLayover(line, sch, out Entity stopB, out Entity waypointB, out int minDwellB);
                if (!hasB) { stopB = Entity.Null; waypointB = Entity.Null; minDwellB = 0; }

                // (4) OBSERVE the timing points BEFORE acting on them: a vehicle that left this tick must set the
                // clock the next one is measured against, or the whole line drifts by one tick per departure.
                MeasureLap(line, terminusStop, frame, durUnits);       // also stamps m_LastDepA / m_VehGap
                TrackTerminusB(line, stopB, frame);                    // stamps m_LastDepB
                // Persist the freshly-updated measurement into the line's component so it survives save/load (no-op
                // until the component exists — added via the ECB above — and only writes when a value actually changed).
                MirrorMeasured(line);

                // THE HEADWAY. cycle = the real loop + every layover the mod itself imposes; headway = cycle / the
                // vehicles actually out there. Deliberately the LIVE count, not the player's target: while the depot is
                // still delivering the peak vehicles, spacing the three that exist evenly is right and spacing them as
                // if there were twelve would just make them all leave immediately and stay bunched.
                bool inService = ScheduleMath.InService(s, sched, nowMin);
                int wantVehicles = ScheduleMath.VehiclesFor(s, plan, customSch, nowMin, sched);
                int serving = CountServingVehicles(line);
                float headwayFrames = ComputeHeadway(line, durUnits, serving, minDwellA + minDwellB);
                // Publish only while the line is actually running. Out of service (a day-only line at night) vanilla
                // has retired its fleet, nothing is departing, and a headway on the stop board would be a promise about
                // vehicles that do not exist.
                if (headwayFrames > 0f && inService) m_LineHeadway[line] = headwayFrames;
                else m_LineHeadway.Remove(line);

                // (5) FLEET. The count is the player's; all this does is decide WHEN to hand it to vanilla.
                int desiredFleet = 0;
                int rampTarget = 0;   // diagnostics only: while a staggered change is in flight, the FINAL count the
                                      // one-per-headway walk is heading for (desiredFleet holds the current step)
                //
                // "Another mod decides" (compat with dedicated fleet mods that write the same per-line VehicleInterval
                // modifier — e.g. All Transit + Truck): we never size the fleet; a line we WERE sizing is handed back
                // to vanilla / the other mod EXACTLY ONCE (m_LastFleet-gated, same one-time hand-back as the disable
                // branch) and then left alone, so we can't fight the other mod every tick. The SPACING is independent
                // of the count and still runs — a line with too few vehicles simply runs a wider headway.
                if (!s.ModSizesFleet)
                {
                    if (m_LastFleet.ContainsKey(line))
                    {
                        // HEAL, not clear — see the note in the disable branch above.
                        m_Fleet.TryHealLeftoverFleetModifier(line);
                        m_LastFleet.Remove(line);
                    }
                    m_RampSince.Remove(line);   // nothing to ramp when the player owns the vehicle counts
                    m_PostedFleet.Remove(line); // and no opinion to publish
                }
                else if (durUnits > 1f && inService && wantVehicles > 0)
                {
                    // Flood-on-load guard (see m_LastDur): only write once the duration estimate has held steady
                    // (within 5%) for kDurStableTicks consecutive ticks. Right after a load it spikes then settles, and
                    // TrySetLineFleet turns our count into an interval THROUGH that duration, so acting on the spike
                    // hands vanilla an interval that resolves to the wrong count.
                    float prevDur = m_LastDur.TryGetValue(line, out float pd) ? pd : 0f;
                    m_LastDur[line] = durUnits;
                    bool agrees = prevDur > 1f && durUnits >= prevDur * 0.95f && durUnits <= prevDur * 1.05f;
                    int stable = agrees ? (m_DurStable.TryGetValue(line, out int sc) ? sc : 0) + 1 : 0;
                    m_DurStable[line] = stable;

                    // DEPOT LEAD LOOK-AHEAD (see m_VehFirstSeen). Provision for the LARGEST count this line will want
                    // within one depot-drive from now, so the extra peak vehicles are standing at the terminus when the
                    // peak opens rather than pulling out of the depot then. MAX, not "the count at now+lead": raising
                    // early is useful, dropping early would strip vehicles out of a peak that is still running.
                    desiredFleet = LookaheadVehicles(s, plan, customSch, sched, nowMin, DepotLeadMinutes(line));
                    if (desiredFleet < 1) desiredFleet = 1;
                    if (desiredFleet > kFleetCap) desiredFleet = kFleetCap;

                    // STAGGERED GROWTH (see m_RampSince): raise the applied count ONE VEHICLE PER HEADWAY instead of
                    // stepping it, so vanilla's one-request-per-tick depot path spaces the arrivals instead of emptying
                    // the depot in a clump. A DECREASE falls straight through untouched, because the drain in (8)
                    // already paces retirement at one vehicle per terminus departure. A line with no applied count yet
                    // is skipped entirely (initial provisioning lands whole).
                    bool rampStepped = false;
                    if (m_LastFleet.TryGetValue(line, out int rampFrom) && desiredFleet > rampFrom)
                    {
                        rampTarget = desiredFleet;   // remember where we are heading before taking one step
                        // Floor at one frame so a degenerate zero headway can never divide the ramp by zero or stall it.
                        uint stepFrames = (uint)System.Math.Max(1f, headwayFrames > 0f ? headwayFrames : m_Fpm);
                        if (!m_RampSince.TryGetValue(line, out uint lastStep) || frame - lastStep >= stepFrames)
                        {
                            // Take one step toward the target. m_RampSince is advanced ONLY if the write below actually
                            // lands — if the stability gate or TrySetLineFleet rejects it, vanilla never saw this step,
                            // so it must not consume the headway budget.
                            desiredFleet = rampFrom + 1;
                            rampStepped = true;
                        }
                        else
                        {
                            desiredFleet = rampFrom;   // between steps: hold the count vanilla is already acting on
                        }
                    }
                    else m_RampSince.Remove(line);     // at target, shrinking, or never sized: no growth ramp running

                    // CRITICAL: desiredFleet must be non-zero on every tick where we have an opinion, NOT only when the
                    // stability gate passes. The drain below derives `surplus` from it, and when desiredFleet is 0
                    // surplus is forced to 0, which SKIPS THE WHOLE DRAIN BLOCK — including the branch that strips
                    // vanilla's AbandonRoute off a mid-route vehicle (DESIGN DECISION C). Leaving it unset during an
                    // unstable-estimate tick therefore let vanilla retire vehicles wherever they stood: vehicles
                    // visibly VANISHING mid-route (live-reported). So only the WRITE is gated on stability.
                    // ...and NOT while the notice is on screen: the point of showing it is to explain the change before
                    // the city visibly changes.
                    if (!NoticeAwaitingAnswer && stable >= kDurStableTicks && m_Fleet.TrySetLineFleet(line, desiredFleet))
                    {
                        m_LastFleet[line] = desiredFleet;
                        if (rampStepped) m_RampSince[line] = frame;
                    }
                    else if (m_LastFleet.TryGetValue(line, out int heldFleet))
                        // The write was suppressed but the line already carries a count we wrote earlier. The drain
                        // MUST reason about the number vanilla is actually acting on, not the one we would like:
                        // otherwise our surplus is computed against a target vanilla never saw, and the two fight — we
                        // retire a vehicle while vanilla buys one back (and vice versa).
                        desiredFleet = heldFleet;
                    m_PostedFleet[line] = desiredFleet;
                }
                else
                {
                    // Out of service (day-only line at night, night-only by day) or no usable duration yet. We have NO
                    // opinion: leave the count to vanilla, which zeroes an out-of-window line's fleet on its own, and
                    // let the drain's `desiredFleet == 0` path stand aside while it does (a line SHUTDOWN is
                    // legitimate; DESIGN DECISION C only ever defends a surplus WE identified).
                    m_RampSince.Remove(line);
                    m_PostedFleet[line] = 0;
                }

                bool diagLog = frame - m_LastLog >= 16384; // [SelfTest] cadence — dump the numbers periodically
                if (diagLog && m_LineLoopSamples.TryGetValue(line, out int loopN) && loopN > 0 && m_Fpm > 0.01f)
                {
                    float measMin = m_LineLoopEma[line] / m_Fpm;
                    float estMin  = durUnits * m_Um;
                    float anchorMin = m_LineLoopMin.TryGetValue(line, out float lm) ? lm / m_Fpm : 0f;
                    float medMin   = m_LineLoopMedian.TryGetValue(line, out float mdf) ? mdf / m_Fpm : 0f;
                    int   rej      = m_LineRejectStreak.TryGetValue(line, out int rr) ? rr : 0;
                    // All three estimators side by side, with `used` naming the one actually driving the headway.
                    // med ~= 2 x anchor is the tell that over half this line's laps are doubles and the median has been
                    // captured — the one case where the anchor would have been the better value.
                    Mod.log.Info($"[SelfTest] laptime line#{line.Index} est={estMin:F1}m anchor={anchorMin:F1}m " +
                                 $"median={medMin:F1}m ema={measMin:F1}m " +
                                 $"used={(loopN >= kMedianMinSample && medMin > 0f ? "median" : loopN >= kMinTrustSamples ? "anchor" : "prior")} " +
                                 $"rej={rej} stops={CountStops(line)} n={loopN} compat={(s.RealisticTripsCompat ? 1 : 0)}");
                }

                // (6) FORCE STOPS: make our vehicles actually pull in and STOP rather than let vanilla skip a stop
                // where nobody boards or alights — ALWAYS at both timing points (a skipped timing point is a departure
                // the mod never got to regulate), and at every stop when the player opts in.
                int forcedStops = ForceStops(line, terminusWaypoint, waypointB, s.StopAtEveryStop);

                // (7) THE STOPS THEMSELVES: publish each stop's travel offset for the board, hold the timing points to
                // the headway, and release everything else as soon as boarding is done.
                RunStops(line, s, terminusStop, stopB, minDwellA, minDwellB, frame, headwayFrames, inService, diagLog);

                // (8) SLOT-COUPLED DRAIN: shed surplus vehicles at the terminus WITHOUT leaving a gap.
                //
                // When the player's count steps down (12 at peak -> 4 off-peak) the game wants to cull the extras by
                // odometer (AbandonRoute) and would retire each wherever it sits — dumping passengers mid-route and,
                // worse, retiring the very vehicle due out next, so several headways in a row go unserved.
                // We take ownership of the cull instead.
                //
                // The key: the vehicle boarding the terminus is being held to the headway and OCCUPIES the single
                // boarding spot until then. So while one of ours is boarding the terminus, the next departure is
                // guaranteed — and ONLY then may an extra that has arrived behind it retire. That gate ("slotCovered")
                // is what turns a burst of retirements into a trickle: one vehicle leaves each headway, the extras
                // drain in the gaps, the fleet glides down to target with no gap in service, and retirement stops on
                // its own once surplus hits zero.
                if (terminusWaypoint != Entity.Null && EntityManager.HasBuffer<RouteVehicle>(line))
                {
                    DynamicBuffer<RouteVehicle> vehicles = EntityManager.GetBuffer<RouteVehicle>(line, isReadOnly: true);
                    if (!m_PendingRetire.TryGetValue(line, out HashSet<Entity> pending))
                        m_PendingRetire[line] = pending = new HashSet<Entity>();
                    if (!m_LapServed.TryGetValue(line, out HashSet<Entity> lapServed))
                        m_LapServed[line] = lapServed = new HashSet<Entity>();

                    // Pass 1: count live vehicles and mark lap-eligibility. A vehicle whose current target is NOT the
                    // terminus has left it and is serving the loop, so it has earned a retirement on its next return.
                    HashSet<Entity> live = new HashSet<Entity>();
                    int liveCount = 0;
                    int flaggedCount = 0; // vehicles vanilla has already marked for retirement (see protectFromCull)
                    for (int v = 0; v < vehicles.Length; v++)
                    {
                        Entity veh = vehicles[v].m_Vehicle;
                        if (veh == Entity.Null || !EntityManager.HasComponent<PublicTransport>(veh))
                            continue;
                        live.Add(veh);
                        m_LiveVehScratch.Add(veh); // union of all live vehicles -> prunes the per-vehicle dicts
                        liveCount++;
                        // DEPOT LEAD (see m_VehFirstSeen): stamp a genuinely new vehicle, and close the measurement the
                        // tick it first pulls into ANY stop on the line — m_ArrivedFrame is that moment, and RunStops
                        // has already stamped it this tick since it runs before this drain. Any stop, not specifically
                        // the terminus: the game spawns a line vehicle at the nearest reachable waypoint, so the drive
                        // it actually made is depot -> wherever it joined, and waiting for it to reach the terminus
                        // would fold most of a loop into what is supposed to be a depot lead.
                        // On the census tick nothing is timed, only recorded as known.
                        if (!m_KnownVeh.Contains(veh))
                        {
                            m_KnownVeh.Add(veh);
                            if (!m_VehCensusPending) m_VehFirstSeen[veh] = frame;
                        }
                        else if (m_VehFirstSeen.TryGetValue(veh, out uint firstSeen) && m_ArrivedFrame.ContainsKey(veh))
                        {
                            m_VehFirstSeen.Remove(veh);
                            AcceptDepotLead(line, frame - firstSeen);
                        }
                        PublicTransport ptv = EntityManager.GetComponentData<PublicTransport>(veh);
                        if ((ptv.m_State & PublicTransportFlags.AbandonRoute) != 0)
                            flaggedCount++;
                        // Arrival stamp: presence in m_ArrivedFrame == "currently boarding, arrived at this frame".
                        // Stamp on the first boarding tick; drop when it leaves the stop so the next stop (or the same
                        // stop next loop) re-stamps a fresh arrival.
                        if ((ptv.m_State & PublicTransportFlags.Boarding) != 0)
                        {
                            if (!m_ArrivedFrame.ContainsKey(veh)) m_ArrivedFrame[veh] = frame;
                        }
                        else
                        {
                            m_ArrivedFrame.Remove(veh);
                            m_VehHoldUntil.Remove(veh);   // it left: nothing is holding it any more
                            // Left the stop: bank whatever we held it for into this lap's total (see MeasureLap).
                            if (m_VehStopHold.TryGetValue(veh, out uint stopHold))
                            {
                                m_VehHoldFrames[veh] = (m_VehHoldFrames.TryGetValue(veh, out uint acc) ? acc : 0u) + stopHold;
                                m_VehStopHold.Remove(veh);
                            }
                        }
                        if (EntityManager.HasComponent<Target>(veh)
                            && EntityManager.GetComponentData<Target>(veh).m_Target != terminusWaypoint)
                            lapServed.Add(veh);
                    }
                    // Is a vehicle of OURS boarding the terminus right now? While one is, the next departure is
                    // covered, so extras may retire without leaving a gap.
                    //
                    // The `live.Contains` test is LOAD-BEARING: a terminus stop can be SHARED with other lines, and its
                    // single BoardingVehicle slot may hold a FOREIGN line's vehicle. Without the check we read another
                    // line's vehicle as "our terminus is covered" and retired one of ours while our own next departure
                    // had nobody to run it — live-reported as "vehicles retire without another one boarding the stop".
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

                    // Measure the surplus against the RAMP TARGET, not the step the ramp happens to be on. The growth
                    // ramp paces what vanilla is TOLD; it must not change what the mod BELIEVES the line needs.
                    // Observed live at a 06:00 fleet-up: line#873674 read live=30 target=16 ramp=53 surplus=14 —
                    // the drain latched three vehicles for retirement on a line the mod was actively growing to
                    // fifty-three, purely because the ramp was still on step 16.
                    int surplus = desiredFleet > 0 ? liveCount - System.Math.Max(desiredFleet, rampTarget) : 0;

                    if (diagLog)
                        Mod.log.Info($"[SelfTest] fleet line#{line.Index} now={nowMin}m live={liveCount} serving={serving} " +
                                     $"target={desiredFleet} ramp={(rampTarget != 0 ? rampTarget.ToString() : "-")} " +
                                     $"headway={(headwayFrames > 0f && m_Fpm > 0.01f ? (headwayFrames / m_Fpm).ToString("F1") : "-")}m " +
                                     $"surplus={surplus} slotCovered={slotCovered} pending={pending.Count} forced={forcedStops} " +
                                     $"depotLead={DepotLeadMinutes(line)}m");

                    // Stale-latch repair. `pending` used to be cleared ONLY when surplus hit 0, so vehicles latched
                    // while the surplus was larger stayed latched after it shrank — leaving more of them
                    // retirement-eligible than the line is actually over target, i.e. retiring BELOW target. Rebuild it
                    // instead. Drop vehicles that already left the line BEFORE comparing against the surplus —
                    // otherwise, on the tick a retired one finally leaves the RouteVehicle buffer, a stale entry makes
                    // pending.Count exceed the surplus and wipes the whole latch, dropping the commit of a vehicle that
                    // was one boarding away from retiring. (`live` is complete here: pass 1 has run.)
                    pending.RemoveWhere(e => !live.Contains(e));
                    if (surplus < 0) surplus = 0;
                    if (pending.Count > surplus) pending.Clear();

                    // MAY we strip vanilla's retirement flag at all this tick? Three states where the answer is NO, and
                    // each would otherwise leave a line UNABLE TO EVER SHED A VEHICLE (the strip below runs every 8
                    // frames and would simply undo vanilla forever):
                    //
                    //  1. desiredFleet == 0 — we have NO OPINION about this line's size: "Manage vehicle count" is off,
                    //     the line is out of service, or the duration estimate has not resolved. Stripping here would
                    //     silently break that interop by pinning the fleet forever.
                    //  2. vanilla has flagged EVERY live vehicle — that is not a surplus cull but a LINE SHUTDOWN
                    //     (TransportLineSystem zeroes the target for an Inactive line, a line with no active buildings,
                    //     or a day-only line at night, then abandons the whole fleet in one pass). Legitimate.
                    //     Self-detecting on purpose: it reads what vanilla actually DID rather than re-deriving when
                    //     vanilla considers a line inactive (its hardcoded night does NOT match this mod's configurable
                    //     night window, so re-deriving would disagree and surrender vehicles at the wrong time).
                    //  3. there is no cull of ours actually in progress (no surplus and nothing latched) — then vanilla
                    //     is flagging for a reason we do not model (a vehicle-MODEL change retires mismatched vehicles
                    //     regardless of count, for one), and overruling it would strand those vehicles forever.
                    bool protectFromCull = desiredFleet > 0 && flaggedCount < liveCount
                                           && (surplus > 0 || pending.Count > 0);

                    // *** THE LOOP RUNS UNCONDITIONALLY — it is NOT inside an `if (surplus > 0)` guard. ***
                    // It used to be, which meant that whenever a line sat AT or UNDER target the mod stopped stripping
                    // vanilla's AbandonRoute from mid-route vehicles entirely — so vanilla retired them wherever they
                    // stood and they VANISHED MID-ROUTE (live-reported). DESIGN DECISION C only holds if the strip is
                    // reachable in EVERY state, so `pending`/`surplus` gate ONLY the assert, never the protection.
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
                        // A commitment is only valid while the vehicle is STILL LATCHED. Reconcile before reading it,
                        // because the commitment can otherwise outlive its reason: when a line's target RISES, vanilla
                        // cancels the abandon (so `flagged` goes false) and `pending` is cleared, so the strip branch
                        // never runs and never removes the entry. It then survives until the next step-down re-latches
                        // the same vehicle, at which point m_Committed satisfies the `||` and slotCovered is BYPASSED —
                        // a retirement with nothing covering the departure. And re-selection is LIKELY rather than
                        // incidental: vanilla abandons by highest odometer, which is the same vehicle it picked before.
                        if (!pending.Contains(veh)) m_Committed.Remove(veh);
                        bool commit = pending.Contains(veh) && onFinalApproach && lapServed.Contains(veh)
                                      && (slotCovered || m_Committed.Contains(veh));
                        if (commit)
                        {
                            // Record the commit for ANY committed vehicle, not only the one we assert on this tick: one
                            // vanilla had already flagged would otherwise never enter m_Committed, so the moment
                            // slotCovered flickered false it would fall to the strip branch and be un-retired.
                            m_Committed.Add(veh);
                            if (!flagged) // lap done, back at the terminus, covered — assert; vanilla retires it here
                            {
                                pt.m_State |= PublicTransportFlags.AbandonRoute;
                                EntityManager.SetComponentData(veh, pt);
                                if (diagLog)
                                    Mod.log.Info($"[SelfTest] retire line#{line.Index} veh#{veh.Index} now={nowMin}m live={liveCount} target={desiredFleet}");
                            }
                        }
                        else if (flagged && protectFromCull) // mid-route, not lapped, or not covered — keep it serving
                        {
                            // *** DESIGN DECISION C (see the header) — deliberate. Clearing vanilla's AbandonRoute on a
                            // mid-route vehicle is the POINT, not an oversight: it makes the surplus finish its loop
                            // and drop its passengers at the terminus instead of vanishing wherever it happened to be.
                            // The deferral reliably wins the race — this system runs every 8 frames, the AI that
                            // CONSUMES the flag (StartBoarding) every 16, and vanilla re-flags surplus only every 256,
                            // so there are always two of our ticks between AI ticks. ***
                            pt.m_State &= ~PublicTransportFlags.AbandonRoute;
                            EntityManager.SetComponentData(veh, pt);
                            m_Committed.Remove(veh);
                        }
                    }
                    pending.RemoveWhere(e => !live.Contains(e)); // drop vehicles that already retired / left the line
                    lapServed.RemoveWhere(e => !live.Contains(e));
                }

                if (sample == null)
                    sample = $"line#{line.Index} sched{sched} {wantVehicles} vehicles"
                           + (headwayFrames > 0f && m_Fpm > 0.01f ? $" every ~{headwayFrames / m_Fpm:F1}m" : "");
            }

            // Apply the deferred component adds now that the per-line loop (and its live handles) is done.
            ecb.Playback(EntityManager);
            ecb.Dispose();

            // Prune tracking entries for lines that left the query (e.g. bulldozed while enabled) so they don't leak.
            m_LiveScratch.Clear();
            for (int i = 0; i < lines.Length; i++) m_LiveScratch.Add(lines[i]);
            PruneToLive(m_LastFleet, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_PostedFleet, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_PendingRetire, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_LapServed, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_LapFront, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_FrontB, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_LastDepA, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_LastDepB, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_LineHeadway, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_LineLoopEma, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_LineLoopWindow, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_LineLoopMedian, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_LineLoopSamples, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_LineLoopMin, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_LineRejectStreak, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_LastDur, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_DurStable, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_RampSince, m_LiveScratch, m_StaleScratch);
            PruneToLive(m_LineDepotLead, m_LiveScratch, m_StaleScratch);
            // Drop per-vehicle state for vehicles that despawned/retired (m_LiveVehScratch = every live one this tick).
            PruneToLive(m_VehFirstSeen, m_LiveVehScratch, m_StaleScratch);
            m_KnownVeh.RemoveWhere(v => !m_LiveVehScratch.Contains(v));
            // The census tick is over: every vehicle that existed at load is now in m_KnownVeh, so from here on an
            // unknown vehicle really is one the depot just dispatched.
            m_VehCensusPending = false;
            PruneToLive(m_ArrivedFrame, m_LiveVehScratch, m_StaleScratch);
            PruneToLive(m_VehTerminusDepart, m_LiveVehScratch, m_StaleScratch);
            PruneToLive(m_VehStopHold, m_LiveVehScratch, m_StaleScratch);
            PruneToLive(m_VehHoldFrames, m_LiveVehScratch, m_StaleScratch);
            PruneToLive(m_VehHoldUntil, m_LiveVehScratch, m_StaleScratch);
            PruneToLive(m_VehGap, m_LiveVehScratch, m_StaleScratch);
            m_Committed.RemoveWhere(v => !m_LiveVehScratch.Contains(v)); // retired/despawned vehicles drop their commit
            // Published offsets are keyed by WAYPOINT (not line); drop entries whose waypoint no longer exists (route
            // edited / line deleted). Periodic (aligned with the [SelfTest] cadence) so it's a cheap occasional scan.
            if (frame - m_LastLog >= 16384 && m_PostedOffset.Count > 0)
            {
                m_StaleScratch.Clear();
                foreach (Entity wpKey in m_PostedOffset.Keys)
                    if (!EntityManager.Exists(wpKey)) m_StaleScratch.Add(wpKey);
                for (int i = 0; i < m_StaleScratch.Count; i++) m_PostedOffset.Remove(m_StaleScratch[i]);
                m_StaleScratch.Clear();
                foreach (Entity wpKey in m_PostedArrival.Keys)
                    if (!EntityManager.Exists(wpKey)) m_StaleScratch.Add(wpKey);
                for (int i = 0; i < m_StaleScratch.Count; i++) m_PostedArrival.Remove(m_StaleScratch[i]);
            }

            lines.Dispose();

            if (anyEnabled && frame - m_LastLog >= 16384)
            {
                m_LastLog = frame;
                Mod.log.Info($"[SelfTest] headwayDispatch: managedLines={enabledCount} nowMin={nowMin} {sample}");
            }
        }

        // ============================== THE PER-STOP PASS ==============================
        // Walk the route once from the terminus. For every waypoint: publish how far it is from the terminus (for the
        // board), and if a vehicle of ours is boarding there, decide what to do with it:
        //
        //   at the terminus / Terminus B  -> REGULATE: hold until one headway after the previous departure from THIS
        //                                    stop, or until the minimum layover has elapsed, whichever is later.
        //   anywhere else                 -> RELEASE: let boarding and alighting finish, then go. We write
        //                                    m_DepartureFrame only to clear vanilla's unbunching delay, never to add
        //                                    a wait of our own (DESIGN DECISION B).
        private void RunStops(Entity line, TransitTimetablesSetting s, Entity terminusStop, Entity stopB,
            int minDwellA, int minDwellB, uint frame, float headwayFrames, bool inService, bool diagLog)
        {
            if (!EntityManager.HasBuffer<RouteWaypoint>(line) || !EntityManager.HasBuffer<RouteSegment>(line))
                return;
            DynamicBuffer<RouteWaypoint> wps = EntityManager.GetBuffer<RouteWaypoint>(line, isReadOnly: true);
            DynamicBuffer<RouteSegment> segs = EntityManager.GetBuffer<RouteSegment>(line, isReadOnly: true);
            int len = wps.Length;
            if (len == 0 || segs.Length < len)
                return;

            // Per-stop dwell (route units), added to each downstream stop's offset so the published offsets match how
            // the game itself counts a line's duration (ComputeStableDuration counts intermediate dwell too).
            float stopDur = 1f;
            if (EntityManager.HasComponent<PrefabRef>(line))
            {
                Entity pf = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
                if (EntityManager.HasComponent<TransportLineData>(pf))
                    stopDur = EntityManager.GetComponentData<TransportLineData>(pf).m_StopDuration;
            }

            // A shared physical stop exposes ONE BoardingVehicle slot regardless of line, so its boarding vehicle may
            // belong to a DIFFERENT line. Build this line's own roster so we only ever write to our own vehicles.
            HashSet<Entity> lineVehicles = null;
            if (EntityManager.HasBuffer<RouteVehicle>(line))
            {
                DynamicBuffer<RouteVehicle> rv = EntityManager.GetBuffer<RouteVehicle>(line, isReadOnly: true);
                lineVehicles = new HashSet<Entity>();
                for (int i = 0; i < rv.Length; i++)
                    if (rv[i].m_Vehicle != Entity.Null) lineVehicles.Add(rv[i].m_Vehicle);
            }

            // Start accumulating at the terminus waypoint (the offsets' origin); fall back to index 0.
            int start = 0;
            for (int i = 0; i < len && terminusStop != Entity.Null; i++)
            {
                Entity w = wps[i].m_Waypoint;
                if (EntityManager.HasComponent<Connected>(w)
                    && EntityManager.GetComponentData<Connected>(w).m_Connected == terminusStop)
                { start = i; break; }
            }

            System.Text.StringBuilder diag = diagLog
                ? new System.Text.StringBuilder("[SelfTest] stops line#").Append(line.Index)
                    .Append(" h=").Append(m_Fpm > 0.01f ? (headwayFrames / m_Fpm).ToString("F1") : "-").Append("m:")
                : null;

            // ===== THE LADDER: every published offset derives from ONE measured number, the line's LOOP. =====
            // We know the line's REAL total time but not how it distributes, so we use the game's estimate for the
            // SHAPE and the measurement for the SCALE.
            //
            // ADDITIVE, NOT MULTIPLICATIVE — this is the whole design and reversing it reintroduces a reverted bug.
            // Multiplying each stop's estimate by the correction makes the error POSITIVE at every stop whose own ratio
            // is below the line average. Adding a fixed amount per intermediate stop makes the ladder sum to the
            // measured loop EXACTLY, so the residual is a walk that must return to zero at the terminus and cannot
            // accumulate one-sided.
            //
            // A line running FASTER than its estimate (negative excess) uses a multiplicative shrink instead: additive
            // subtraction could take more off a short leg than that leg contains and post a stop earlier than its
            // predecessor, whereas scaling a monotone sequence by a positive factor stays monotone by construction.
            int nTimedIntermediate = 0;
            float estTotalUnits = 0f;
            for (int j = 0; j < len; j++)
            {
                int wi = start + j; if (wi >= len) wi -= len;
                int si = start + j; if (si >= len) si -= len;
                Entity segE = segs[si].m_Segment;
                if (segE != Entity.Null && EntityManager.HasComponent<PathInformation>(segE))
                    estTotalUnits += EntityManager.GetComponentData<PathInformation>(segE).m_Duration;
                if (j >= 1 && EntityManager.HasComponent<VehicleTiming>(wps[wi].m_Waypoint))
                { estTotalUnits += stopDur; nTimedIntermediate++; }
            }
            float perStopExtraMin = 0f;   // additive ladder step (minutes), 0 = publish the raw estimate
            float shrinkScale = 1f;       // multiplicative path, used only when the line beats its estimate
            if (TryMeasuredLoopFrames(line, out float loopFrames) && m_Fpm > 0.01f && estTotalUnits > 0.01f)
            {
                float estLoopMin = estTotalUnits * m_Um;
                float realLoopMin = loopFrames / m_Fpm;
                float excessMin = realLoopMin - estLoopMin;
                if (excessMin >= 0f)
                {
                    // No intermediate timed stop to hang the excess on (a 2-waypoint shuttle): fall back to scaling,
                    // otherwise the correction would be silently dropped.
                    if (nTimedIntermediate >= 1) perStopExtraMin = excessMin / nTimedIntermediate;
                    else shrinkScale = realLoopMin / estLoopMin;
                }
                else shrinkScale = realLoopMin / estLoopMin;
            }

            // The two departure clocks, read ONCE. MeasureLap and TrackTerminusB have already run this tick, so these
            // are current; hoisting them also makes it obvious that the walk below cannot move them mid-route (a stop
            // that changed the clock it is being measured against would regulate against itself).
            bool haveDepA = m_LastDepA.TryGetValue(line, out uint lastDepA);
            bool haveDepB = m_LastDepB.TryGetValue(line, out uint lastDepB);

            int timedPassed = 0;   // intermediate timed stops already departed — the ladder rung for this stop
            float offUnits = 0f;
            // Terminus B's minimum layover shifts every downstream published time, so it accumulates the same way the
            // travel does — as its OWN minutes term, never folded into offUnits (which shrinkScale multiplies, and a
            // player-set layover must not shrink because the line is quick).
            int layoverCarry = 0;
            for (int j = 0; j < len; j++)
            {
                int wpIdx = start + j; if (wpIdx >= len) wpIdx -= len;
                Entity wp = wps[wpIdx].m_Waypoint;
                Entity stop = EntityManager.HasComponent<Connected>(wp)
                    ? EntityManager.GetComponentData<Connected>(wp).m_Connected : Entity.Null;
                bool isTerminus = stop != Entity.Null && stop == terminusStop;
                bool isB = stop != Entity.Null && stopB != Entity.Null && stop == stopB;
                int layAtThis = isB ? minDwellB : 0;

                // The ladder: the estimate's SHAPE, lifted onto the measured SCALE. With no trusted measurement both
                // terms are inert (perStopExtraMin 0, shrinkScale 1) and this is the raw estimate.
                int offArr = (int)System.Math.Round(offUnits * m_Um * shrinkScale + timedPassed * perStopExtraMin) + layoverCarry;
                if (offArr < 0) offArr = 0;
                layoverCarry += layAtThis;
                int offMin = offArr + layAtThis;
                m_PostedOffset[wp] = offMin;
                if (layAtThis > 0) m_PostedArrival[wp] = offArr;
                else m_PostedArrival.Remove(wp);

                if (stop != Entity.Null && EntityManager.HasComponent<BoardingVehicle>(stop))
                {
                    Entity bveh = EntityManager.GetComponentData<BoardingVehicle>(stop).m_Vehicle;
                    if (bveh != Entity.Null && (lineVehicles == null || lineVehicles.Contains(bveh))
                        && !m_ArrivedFrame.ContainsKey(bveh))
                        m_ArrivedFrame[bveh] = frame;   // stamp on the FIRST boarding tick; the drain is a tick later
                    if (inService)
                    {
                        if (isTerminus)
                            RegulateStop(s, stop, frame, headwayFrames, minDwellA, lastDepA, haveDepA,
                                         isPrimary: true, lineVehicles: lineVehicles, diag: diag, tag: "T");
                        else if (isB)
                            RegulateStop(s, stop, frame, headwayFrames, minDwellB, lastDepB, haveDepB,
                                         isPrimary: false, lineVehicles: lineVehicles, diag: diag, tag: "B");
                        else
                            ReleaseStop(s, stop, frame, lineVehicles, diag, offMin);
                    }
                }
                else if (diag != null)
                    diag.Append(" [").Append(j).Append(":off").Append(offMin).Append(']');

                // Add the leg LEAVING this waypoint so the next waypoint's offset is correct.
                int segIdx = start + j; if (segIdx >= len) segIdx -= len;
                Entity seg = segs[segIdx].m_Segment;
                if (seg != Entity.Null && EntityManager.HasComponent<PathInformation>(seg))
                    offUnits += EntityManager.GetComponentData<PathInformation>(seg).m_Duration;
                // ...plus this stop's own dwell so DOWNSTREAM offsets match how the game counts. Intermediate timed
                // stops only: j==0 is the terminus, and a stop's own dwell never enters its own offset.
                if (j >= 1 && EntityManager.HasComponent<VehicleTiming>(wp))
                { offUnits += stopDur; timedPassed++; }
            }

            if (diag != null)
                Mod.log.Info(diag.ToString());
        }

        // ============================== REGULATION AT A TIMING POINT ==============================
        // Hold this stop's boarding vehicle until ONE HEADWAY after the previous vehicle left the SAME stop, or until
        // it has had its minimum layover, whichever is later. That single rule is the whole mod:
        //
        //   arrives into a gap (late)   -> lastDeparture + headway is already past -> leaves after the minimum layover
        //   arrives on the headway      -> lastDeparture + headway is about now    -> leaves after the minimum layover
        //   arrives bunched up (early)  -> waits out the remainder of the headway  -> the bunch is broken
        //
        // Note what it does NOT do: it never asks where the vehicle "should" be on a clock, so a line that is running
        // late as a whole simply runs late as a whole, evenly spaced, instead of every vehicle fighting a grid it
        // cannot reach. And because the target is anchored on the PREVIOUS DEPARTURE rather than on this vehicle's
        // arrival, delay is absorbed here rather than compounded.
        private void RegulateStop(TransitTimetablesSetting s, Entity stop, uint frame, float headwayFrames,
            int minDwellMinutes, uint lastDeparture, bool haveLastDeparture, bool isPrimary,
            HashSet<Entity> lineVehicles, System.Text.StringBuilder diag, string tag)
        {
            Entity veh = EntityManager.GetComponentData<BoardingVehicle>(stop).m_Vehicle;
            if (veh == Entity.Null || !EntityManager.HasComponent<PublicTransport>(veh))
            { diag?.Append(" [").Append(tag).Append(":noveh]"); return; }
            // The boarding slot at a shared stop can hold ANOTHER line's vehicle — never write its departure frame.
            if (lineVehicles != null && !lineVehicles.Contains(veh))
            { diag?.Append(" [").Append(tag).Append(":foreign]"); return; }
            PublicTransport pt = EntityManager.GetComponentData<PublicTransport>(veh);
            // Only regulate an IN-SERVICE boarding vehicle; a retiring one has EnRoute cleared and must reach the depot.
            bool isBoarding = (pt.m_State & PublicTransportFlags.Boarding) != 0;
            bool isEnRoute = (pt.m_State & PublicTransportFlags.EnRoute) != 0;
            if (!isBoarding || !isEnRoute)
            { diag?.Append(" [").Append(tag).Append(":idle]"); return; }

            uint arrived = m_ArrivedFrame.TryGetValue(veh, out uint af) ? af : frame;
            if (minDwellMinutes < 0) minDwellMinutes = 0;
            uint minDwellFrames = (uint)(minDwellMinutes * m_Fpm);

            long target = (long)arrived + minDwellFrames;
            if (haveLastDeparture && headwayFrames > 0f)
            {
                long byHeadway = (long)lastDeparture + (long)headwayFrames;
                if (byHeadway > target) target = byHeadway;
            }
            // SAFETY CEILING — release rather than freeze (the 6-16h kerb-freeze, v0.2.1). No legitimate regulation can
            // ask for more than one headway on top of the minimum layover: the previous vehicle left at most one
            // headway ago by construction, so anything beyond this is a stale or nonsensical stamp, not a wait.
            long ceiling = (long)arrived + (long)headwayFrames + minDwellFrames;
            if (headwayFrames > 0f && target > ceiling) target = ceiling;

            if (frame < target)
            {
                // HOLD. Write the target frame AUTHORITATIVELY (this also overrides vanilla's unbunching-inflated
                // value). It cannot cut a boarding short: while held, StopBoarding keeps the vehicle for a cim walking
                // up (m_MaxBoardingDistance != MaxValue, TransportCarAISystem:1263-1265) and for a not-yet-Ready
                // passenger (:1269-1278). Those guards are bypassed only by the frame-1800 cutoff, which the release
                // branch below uses on purpose once the wait is over.
                uint t = (uint)target;
                if (pt.m_DepartureFrame != t) { pt.m_DepartureFrame = t; EntityManager.SetComponentData(veh, pt); }
                m_VehHoldUntil[veh] = t;
                // Terminus B's wait is MID-ROUTE, so it lands inside the measured loop and has to be subtracted from
                // it or the line would appear to get slower every time the player lengthens the layover — and, because
                // the headway is derived from the loop, that error would feed straight back into the wait itself.
                // Terminus A's wait is outside the measurement by construction (the lap is departure-to-arrival).
                // Rewritten every tick with the same value (target and arrived are both fixed for this stop), so
                // re-running cannot double-count; the drain folds it into the lap total when the vehicle leaves.
                if (!isPrimary && target > arrived) m_VehStopHold[veh] = (uint)(target - arrived);
                diag?.Append(" [").Append(tag).Append(":hold ")
                     .Append(m_Fpm > 0.01f ? ((target - frame) / m_Fpm).ToString("F1") : "?").Append("m]");
            }
            else
            {
                m_VehHoldUntil.Remove(veh);
                ReleaseVehicle(s, veh, ref pt, (uint)target, frame);
                diag?.Append(" [").Append(tag).Append(":go]");
            }
        }

        // ============================== AN ORDINARY STOP ==============================
        // "Wait until everybody is on and off, then leave." There is nothing to schedule here, so the only thing the
        // mod does is make sure the vehicle is not held for a reason of the GAME'S — vanilla's unbunching delay inflates
        // m_DepartureFrame at StartBoarding, which under headway regulation is a second, uncoordinated spacing
        // mechanism fighting the first. We pull the departure frame back to now, and vanilla's own boarding guards then
        // keep the vehicle exactly as long as somebody is still getting on or off.
        private void ReleaseStop(TransitTimetablesSetting s, Entity stop, uint frame, HashSet<Entity> lineVehicles,
            System.Text.StringBuilder diag, int offMin)
        {
            Entity veh = EntityManager.GetComponentData<BoardingVehicle>(stop).m_Vehicle;
            if (veh == Entity.Null || !EntityManager.HasComponent<PublicTransport>(veh))
            { diag?.Append(" [off").Append(offMin).Append(":noveh]"); return; }
            if (lineVehicles != null && !lineVehicles.Contains(veh))
            { diag?.Append(" [off").Append(offMin).Append(":foreign]"); return; }
            PublicTransport pt = EntityManager.GetComponentData<PublicTransport>(veh);
            if ((pt.m_State & PublicTransportFlags.Boarding) == 0 || (pt.m_State & PublicTransportFlags.EnRoute) == 0)
            { diag?.Append(" [off").Append(offMin).Append(":idle]"); return; }
            uint arrived = m_ArrivedFrame.TryGetValue(veh, out uint af) ? af : frame;
            m_VehHoldUntil.Remove(veh);
            ReleaseVehicle(s, veh, ref pt, arrived, frame);
            diag?.Append(" [off").Append(offMin).Append(":brd]");
        }

        // Hand a vehicle back to the game with a BOUNDED boarding grace, anchored so it expires at
        // `dueFrame + maxDwell`.
        //
        // *** DESIGN DECISION — read this before changing it. Two earlier versions got it wrong in opposite
        // directions, and this is the reconciliation. ***
        //
        // The rule is: a regulated vehicle leaves when it is due and does not wait for a straggler who has not started
        // boarding. What it also must not do is throw off passengers who are ALREADY boarding — and that is the trap,
        // because the base game adds a citizen to the vehicle's passenger list the moment they START WALKING to it,
        // long before they are aboard.
        //
        // History, both halves of it:
        //  - v0.2 released with frame-1. That opens only the departure-time gate and leaves BOTH of vanilla's boarding
        //    guards armed with NO BOUND, so arriving cims re-armed the hold indefinitely and the line slipped further
        //    behind on every departure. Reverted in v0.2.3.
        //  - v0.2.3..v0.4.0 used frame-1800, vanilla's own anti-softlock cutoff. That clears both guards, which stops
        //    the slip — but it also CANCELS every passenger still walking to the door, dumping them back on the
        //    platform. Live report: "the bus fills up, then empties down to a certain amount, then leaves", with the
        //    queue growing at busy stations. It fired on every departure of every vehicle.
        //
        // The fix is to use vanilla's mechanism instead of overriding it. StopBoarding gives up when
        // `frame >= m_DepartureFrame + 1800` (TransportCarAISystem:1262, and identically in the Train, Watercraft and
        // Aircraft systems). That window is anchored on m_DepartureFrame — so by writing
        //     m_DepartureFrame = due + maxDwellFrames - 1800
        // the cutoff lands exactly at `due + maxDwellFrames`, i.e. at the player's max stop time. Everything in
        // between is stock game behaviour we do not touch: the widening boarding radius, the wait for passengers not
        // yet seated, late arrivals joining. A vehicle with nobody boarding departs immediately; one with passengers
        // boarding gets up to the configured grace, and the overrun can never exceed it.
        //
        // maxDwellFrames is clamped to the vanilla window: we can only ever shorten it. At 0 this is exactly the old
        // frame-1800 behaviour, which is the honest meaning of "wait for nobody".
        private void ReleaseVehicle(TransitTimetablesSetting s, Entity veh, ref PublicTransport pt, uint dueFrame, uint frame)
        {
            // Split road/rail: trams, metros and trains all carry the Train component; ferries/aircraft take the road
            // value. A train exchanging a full platform takes far longer to load than a bus.
            int maxDwellCfg = EntityManager.HasComponent<Game.Vehicles.Train>(veh) ? s.MaxDwellRail : s.MaxDwellRoad;
            if (maxDwellCfg < 0) maxDwellCfg = 0;
            uint maxDwellFrames = (uint)(maxDwellCfg * m_Fpm);
            if (maxDwellFrames > kVanillaBoardingGraceFrames) maxDwellFrames = kVanillaBoardingGraceFrames;
            long anchor = (long)dueFrame + maxDwellFrames - kVanillaBoardingGraceFrames;
            uint force = anchor > 1L ? (uint)anchor : 1u;
            // Never write a FUTURE departure frame from this branch — that would turn "release" into a hold, which is
            // the exact opposite of its purpose. (Reachable when the safety ceiling in RegulateStop clamps a target
            // that is still ahead of now.)
            if (force > frame) force = frame;
            if (pt.m_DepartureFrame > force) { pt.m_DepartureFrame = force; EntityManager.SetComponentData(veh, pt); }
        }

        // ============================== THE HEADWAY ITSELF ==============================
        // cycle = the REAL loop (measured, or the estimate lifted by the density prior until it is) plus every layover
        // the mod imposes; headway = cycle / vehicles actually serving. Returns 0 when the line has no usable duration
        // at all, which switches regulation down to the bare minimum layover — the honest answer for a route the game
        // itself cannot yet cost.
        private float ComputeHeadway(Entity line, float durUnits, int serving, int extraDwellMinutes)
        {
            if (m_Fpm <= 0.01f)
                return 0f;
            if (!TryMeasuredLoopFrames(line, out float loopFrames))
            {
                float estFrames = durUnits * 60f;   // a route "duration unit" is 60 sim frames
                if (estFrames <= 1f)
                    return 0f;
                // Cold start: the game's estimate is systematically short (it ignores the acceleration and braking at
                // every stop), and the headway is derived from it, so using it raw would space vehicles at roughly half
                // the interval they can actually keep and the regulation would never bind. Lift it by the stop-density
                // prior until the line has timed real laps.
                loopFrames = estFrames * ScheduleMath.ClampCorrection(
                    ScheduleMath.DensityPriorRatio(CountStops(line), durUnits), forFleet: false);
            }
            float cycle = loopFrames + extraDwellMinutes * m_Fpm;
            float h = ScheduleMath.Headway(cycle, serving);
            float lo = kMinHeadwayMinutes * m_Fpm, hi = kMaxHeadwayMinutes * m_Fpm;
            if (h < lo) h = lo;
            if (h > hi) h = hi;
            return h;
        }

        // Vehicles actually out on the line right now: in service (EnRoute) and not on their way to the depot
        // (AbandonRoute). This — not the player's target — is what the spacing is computed against, because spacing
        // three vehicles as if there were twelve makes all three leave immediately and stay bunched. As the depot
        // delivers, the count rises and the headway tightens on its own.
        private int CountServingVehicles(Entity line)
        {
            if (!EntityManager.HasBuffer<RouteVehicle>(line))
                return 0;
            DynamicBuffer<RouteVehicle> vehicles = EntityManager.GetBuffer<RouteVehicle>(line, isReadOnly: true);
            int n = 0;
            for (int v = 0; v < vehicles.Length; v++)
            {
                Entity veh = vehicles[v].m_Vehicle;
                if (veh == Entity.Null || !EntityManager.HasComponent<PublicTransport>(veh))
                    continue;
                PublicTransportFlags st = EntityManager.GetComponentData<PublicTransport>(veh).m_State;
                if ((st & PublicTransportFlags.EnRoute) != 0 && (st & PublicTransportFlags.AbandonRoute) == 0)
                    n++;
            }
            return n;
        }

        // The LARGEST vehicle count this line will want between now and `leadMin` from now.
        //
        // Largest, not "the one at now+lead", and that asymmetry is the point: raising the count early is useful (the
        // vehicle spends the lead driving out of the depot and arrives as the peak opens), while LOWERING it early
        // would strip vehicles out of a peak that is still running. Sampled rather than evaluated at the two ends
        // because a custom peak window can be shorter than the lead and would otherwise be stepped straight over.
        private static int LookaheadVehicles(TransitTimetablesSetting s, LineFleetPlan plan,
            CustomPeakSchedule customSch, int sched, int nowMin, int leadMin)
        {
            int best = ScheduleMath.VehiclesFor(s, plan, customSch, nowMin, sched);
            if (leadMin <= 0)
                return best;
            for (int m = 5; m <= leadMin; m += 5)
            {
                int v = ScheduleMath.VehiclesFor(s, plan, customSch, (nowMin + m) % 1440, sched);
                if (v > best) best = v;
            }
            int end = ScheduleMath.VehiclesFor(s, plan, customSch, (nowMin + leadMin) % 1440, sched);
            return end > best ? end : best;
        }

        // ONE-TIME CONVERSION of a save written by the headway-based versions of this mod. The old model stored a
        // target INTERVAL per window; the new one stores a vehicle COUNT. ceil(loop / interval) is exactly the number
        // the old mod would have derived and run, so converting through it means an upgraded line keeps the service it
        // had — the same vehicles, doing the same job — rather than snapping to a default the player never chose.
        //
        // Uses the same loop the headway does (measured when available, the density-lifted estimate before that), so
        // the conversion is consistent with what the line will actually do next tick. A line whose duration is not
        // usable yet converts off the plain default, which is the only honest answer; the counts are the player's to
        // adjust afterwards either way.
        // `log` is false when the PANEL asks (ResolvePlan): that call happens on every UI refresh for a line whose
        // component has not landed yet, and logging there would emit the same line many times a second.
        private LineFleetPlan MigrateFleetPlan(Entity line, TimetableSchedule sch, CustomPeakSchedule customSch, float durUnits, bool log)
        {
            LineFleetPlan plan = LineFleetPlan.Default();
            // Read the scale from the timebase directly rather than from the per-tick snapshot: ResolvePlan below
            // calls this from the UI phase, which can run before the first simulation tick has taken that snapshot,
            // and a zero there would silently downgrade every conversion to the flat default.
            float fpm = m_Timebase.FramesPerMinute;
            if (fpm <= 0.01f)
                return plan;
            float loopFrames;
            if (!TryMeasuredLoopFrames(line, out loopFrames))
            {
                float estFrames = durUnits * 60f;
                if (estFrames <= 1f)
                {
                    if (log)
                        Mod.log.Info($"[SelfTest] fleet plan for line#{line.Index}: no usable duration, using defaults " +
                                     $"({plan.m_PeakVehicles}/{plan.m_OffPeakVehicles}/{plan.m_NightVehicles})");
                    return plan;
                }
                loopFrames = estFrames * ScheduleMath.ClampCorrection(
                    ScheduleMath.DensityPriorRatio(CountStops(line), durUnits), forFleet: false);
            }
            float loopMin = loopFrames / fpm;
            plan.m_PeakVehicles = ToCount(ScheduleMath.VehiclesForHeadway(loopMin, sch.m_PeakInterval));
            plan.m_OffPeakVehicles = ToCount(ScheduleMath.VehiclesForHeadway(loopMin, sch.m_OffPeakInterval));
            plan.m_NightVehicles = ToCount(ScheduleMath.VehiclesForHeadway(loopMin, sch.m_NightInterval));
            plan.m_CustomPeakVehicles = ToCount(ScheduleMath.VehiclesForHeadway(loopMin, customSch.m_Interval));
            if (log)
                Mod.log.Info($"[SelfTest] fleet plan for line#{line.Index} converted from headways " +
                             $"{sch.m_PeakInterval}/{sch.m_OffPeakInterval}/{sch.m_NightInterval}m over a {loopMin:F1}m loop -> " +
                             $"{plan.m_PeakVehicles}/{plan.m_OffPeakVehicles}/{plan.m_NightVehicles} vehicles");
            return plan;
        }

        private static ushort ToCount(int n) => (ushort)(n < 1 ? 1 : (n > kFleetCap ? kFleetCap : n));

        // THE line's plan, whether or not the component exists yet. The panel must read this rather than falling back
        // to LineFleetPlan.Default() on its own, and the reason is a real hazard rather than tidiness: on a line
        // upgraded from an older save the component is created by the DISPATCH, on its next simulation tick. A player
        // who opens that line while the game is PAUSED would otherwise be shown flat defaults, and the moment they
        // nudged any one of them the panel would write those defaults into the component — permanently discarding the
        // service level their old headways would have converted into, with no way back. Reading through here means the
        // panel shows, and edits from, exactly the numbers the dispatch is about to write.
        public LineFleetPlan ResolvePlan(Entity line)
        {
            if (EntityManager.HasComponent<LineFleetPlan>(line))
                return EntityManager.GetComponentData<LineFleetPlan>(line);
            if (!EntityManager.HasComponent<TimetableSchedule>(line))
                return LineFleetPlan.Default();
            TimetableSchedule sch = EntityManager.GetComponentData<TimetableSchedule>(line);
            CustomPeakSchedule customSch = EntityManager.HasComponent<CustomPeakSchedule>(line)
                ? EntityManager.GetComponentData<CustomPeakSchedule>(line) : CustomPeakSchedule.Default();
            return MigrateFleetPlan(line, sch, customSch, m_Fleet.LineStableDurationUnits(line), log: false);
        }

        // Vehicles this line actually has out on the road right now, for the panel's "N of M running" row. The gap
        // between this and the applied count is the most useful thing a player can see while a peak ramps up: it is
        // the difference between "the mod is not doing what I asked" and "the depot is still delivering".
        public int ServingVehicles(Entity line) => CountServingVehicles(line);

        // Release any vehicle this line was holding (future m_DepartureFrame) so it departs once the line is handed
        // back, instead of idling at the platform until a stale frame arrives (#8).
        //
        // Deliberately frame-1 (GRACEFUL), NOT the frame-1800 cutoff ReleaseVehicle uses — do not "harmonize" them.
        // They serve opposite purposes: ReleaseVehicle is ENFORCING the spacing, so it must depart over stragglers
        // (design decision A). This path is HANDING THE LINE BACK to vanilla, so it must only undo our own hold and
        // then let normal boarding behave exactly as vanilla would.
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
                m_VehHoldUntil.Remove(veh);
            }
        }

        // Force our line's vehicles to actually STOP at a stop instead of letting vanilla skip it when nobody boards or
        // alights. Vanilla only pulls a vehicle into a stop when PublicTransportFlags.RequireStop is set — raised by
        // ResidentAISystem when a passenger wants on/off, and read in TransportCarAISystem.CheckNavigationLanes to
        // decide skip-vs-stop. With no demand the flag stays clear and the vehicle rolls through, so it never enters
        // Boarding and the regulation can't act on it. We simply OR the flag in ourselves:
        //   - BOTH TIMING POINTS are forced UNCONDITIONALLY (a skipped timing point is a departure we never spaced);
        //   - every other stop only when `everyStop` (the player's opt-in), which trades a short dwell at empty stops
        //     for a guaranteed call.
        // RequireStop is a TRANSIENT runtime flag: BeginTesting clears it at the start of each boarding test, then the
        // resident AI re-sets it if there is demand (TransportBoardingHelpers). PublicTransport.m_State IS serialized,
        // but a saved RequireStop bit self-clears at the very next BeginTesting — so forcing it is save/uninstall-safe
        // (at worst one extra stop right after an uninstall), unlike m_UnbunchingFactor which nothing ever restores. We
        // re-assert it every tick (this system runs every 8 frames vs the car AI's 16, so the set reliably lands
        // between the BeginTesting clear and the skip read), and we ONLY OR it in — never clear it — so we can never
        // suppress a stop the game genuinely wants. Scoped to THIS line's own RouteVehicles. Returns count forced.
        private int ForceStops(Entity line, Entity terminusWaypoint, Entity waypointB, bool everyStop)
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
                // Only a vehicle that is IN SERVICE (EnRoute) and currently DRIVING (not already boarding) can skip an
                // upcoming stop; leave depot-bound / boarding vehicles alone.
                if ((pt.m_State & PublicTransportFlags.EnRoute) == 0 || (pt.m_State & PublicTransportFlags.Boarding) != 0)
                    continue;
                Entity tgt = EntityManager.HasComponent<Target>(veh)
                    ? EntityManager.GetComponentData<Target>(veh).m_Target : Entity.Null;
                bool approachingTerminus = terminusWaypoint != Entity.Null && tgt == terminusWaypoint;
                bool approachingB = waypointB != Entity.Null && tgt == waypointB;
                // A TECHNICAL stop is called at unconditionally — that is half of what the mode means ("the vehicle
                // stops here regardless"). It has to be forced for a stronger reason than a timing point does: the rule
                // has already emptied the vehicle and closed boarding (StopRuleSystem cuts the pathfind edges), so
                // there is BY CONSTRUCTION never any demand here and vanilla would roll past every single time.
                bool approachingTechnical = tgt != Entity.Null
                    && StopRules.ModeForWaypoint(EntityManager, line, tgt) == LineStopRule.Technical;
                if (!(everyStop || approachingTerminus || approachingB || approachingTechnical))
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

        // Drop dictionary entries whose key is no longer live. Reuses `scratch` to gather stale keys so removal doesn't
        // mutate the dictionary mid-enumeration.
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

        // Watch the terminus boarding slot. When the vehicle occupying it CHANGES, the previous occupant has just
        // LEFT — which is simultaneously (a) the clock the NEXT vehicle's headway is measured from, (b) the start of
        // that vehicle's lap, and (c) the end of its previous lap, if we saw it leave last time round.
        //
        // One observation, three consumers, so the spacing and the measurement can never disagree about when a vehicle
        // departed. Reads the world only.
        private void MeasureLap(Entity line, Entity terminusStop, uint frame, float durUnits)
        {
            Entity curFront = Entity.Null;
            if (terminusStop != Entity.Null && EntityManager.HasComponent<BoardingVehicle>(terminusStop))
            {
                Entity f = EntityManager.GetComponentData<BoardingVehicle>(terminusStop).m_Vehicle;
                // The vehicle MUST belong to THIS line. A terminus stop can be shared with other lines and exposes a
                // single BoardingVehicle slot, so without this the occupant may be a FOREIGN line's vehicle — and we
                // would both time ITS loop as ours and reset OUR headway clock off its movements.
                if (f != Entity.Null && EntityManager.HasComponent<PublicTransport>(f)
                    && EntityManager.HasComponent<CurrentRoute>(f)
                    && EntityManager.GetComponentData<CurrentRoute>(f).m_Route == line)
                {
                    PublicTransport pt = EntityManager.GetComponentData<PublicTransport>(f);
                    if ((pt.m_State & PublicTransportFlags.Boarding) != 0 && (pt.m_State & PublicTransportFlags.EnRoute) != 0)
                        curFront = f; // a serving vehicle OF OURS is boarding the terminus right now
                }
            }

            m_LapFront.TryGetValue(line, out Entity prevFront);
            if (curFront == prevFront)
                return; // no change at the terminus slot this tick — nothing to observe

            if (prevFront != Entity.Null)
            {
                // THE DEPARTURE. Everything downstream hangs off this one stamp.
                if (m_LastDepA.TryGetValue(line, out uint prevDep) && frame > prevDep)
                    m_VehGap[prevFront] = frame - prevDep;   // the spacing this vehicle actually achieved
                m_LastDepA[line] = frame;
                m_VehTerminusDepart[prevFront] = frame;      // a fresh lap begins for it
                m_VehHoldFrames.Remove(prevFront);           // reset the hold accumulator for the lap that starts now
                m_VehStopHold.Remove(prevFront);
            }

            if (curFront != Entity.Null
                && m_VehTerminusDepart.TryGetValue(curFront, out uint dep) && frame > dep)
            {
                // SUBTRACT our own mid-route holds rather than DISCARDING the lap. The old code skipped any lap
                // containing a hold, to keep the mod's own waits out of its own measurement. That was affordable only
                // while holds were rare; with a Terminus B layover a vehicle is held on every single lap, so "discard"
                // would throw away every lap and measurement would stop dead.
                //
                // It deliberately over-subtracts: the recorded hold spans arrival->release, which also contains the
                // natural dwell the vehicle would have taken anyway. That biases the measured loop DOWN, i.e. toward a
                // SHORTER headway — the safe direction, because it damps the feedback (a longer measured loop would
                // mean a longer hold at B, which would lengthen the measurement again) rather than amplifying it.
                // Do NOT "fix" this into exact compensation; that moves it toward the unstable edge.
                uint held = m_VehHoldFrames.TryGetValue(curFront, out uint hf) ? hf : 0u;
                uint span = frame - dep;
                uint loop = span > held ? span - held : span; // never let compensation invert the span

                if (loop >= kMinLoopFrames && loop <= kMaxLoopFrames && AcceptLoopSample(line, loop, durUnits))
                {
                    if (m_LineLoopSamples.TryGetValue(line, out int n) && n > 0)
                        m_LineLoopEma[line] += kLoopAlpha * (loop - m_LineLoopEma[line]);
                    else
                        m_LineLoopEma[line] = loop;
                    PushLoopSample(line, loop);
                    m_LineLoopSamples[line] = (m_LineLoopSamples.TryGetValue(line, out int nn) ? nn : 0) + 1;
                }
            }

            m_LapFront[line] = curFront;
        }

        // The same departure observation for Terminus B, minus the lap timing (a lap is defined terminus-to-terminus,
        // and adding a second definition is how two numbers that must agree stop agreeing). B needs only its own
        // departure clock, because its headway target is measured from the last vehicle to leave B — not from A.
        private void TrackTerminusB(Entity line, Entity stopB, uint frame)
        {
            if (stopB == Entity.Null)
            {
                m_FrontB.Remove(line);
                return;
            }
            Entity cur = Entity.Null;
            if (EntityManager.HasComponent<BoardingVehicle>(stopB))
            {
                Entity f = EntityManager.GetComponentData<BoardingVehicle>(stopB).m_Vehicle;
                if (f != Entity.Null && EntityManager.HasComponent<PublicTransport>(f)
                    && EntityManager.HasComponent<CurrentRoute>(f)
                    && EntityManager.GetComponentData<CurrentRoute>(f).m_Route == line)
                {
                    PublicTransport pt = EntityManager.GetComponentData<PublicTransport>(f);
                    if ((pt.m_State & PublicTransportFlags.Boarding) != 0 && (pt.m_State & PublicTransportFlags.EnRoute) != 0)
                        cur = f;
                }
            }
            m_FrontB.TryGetValue(line, out Entity prev);
            if (cur == prev)
                return;
            if (prev != Entity.Null)
                m_LastDepB[line] = frame;
            m_FrontB[line] = cur;
        }

        // Reject implausible loop samples so the measurement survives BUNCHING. On a busy line a vehicle can roll
        // through the terminus while another occupies the boarding slot, so its pass is missed and the NEXT detected
        // span is a DOUBLE (~2x the real loop). Key insight: a missed pass makes a span a MULTIPLE of the truth, never
        // a fraction, so the TRUE single loop is the MINIMUM of the spans. We gate against a running MIN rather than
        // the EMA: the min drops freely toward the truth, and anything well above it (a double-count or a stall) is
        // rejected. Unlike an EMA-keyed band this cannot be self-poisoned. A RUN of rejections means the anchor itself
        // is stale — a glitch-low first sample pinned it, or a route edit lengthened the loop — so we re-anchor upward
        // and recalibrate (heals both).
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
            // stale (route lengthened, or the min was a glitch): re-anchor and recalibrate.
            int streak = (m_LineRejectStreak.TryGetValue(line, out int rs) ? rs : 0) + 1;
            if (streak >= kResetAfterRejects)
            {
                // RECALIBRATE THE ANCHOR, KEEP THE MEASUREMENT.
                //
                // This path exists for one real case: the route genuinely got longer, so the old minimum is now
                // impossibly low, every honest lap trips the 1.6x band, and without an escape the line would reject
                // samples forever. What it must NOT do is throw the line's history away.
                //
                // Observed live, three times in ten minutes, each worse than the last:
                //   line#322238  67 samples discarded, correction 2.68 -> 3.34   (+25%)
                //   line#322237  129 samples discarded, dropped to the density PRIOR, 1.90 -> 2.79   (+47%)
                //   line#1580787 anchor 95.2 -> 324.1, correction 1.05 -> 3.58   (+240%)
                // The last had the best-measured value in the city and re-anchored onto a TRIPLE-counted lap.
                //
                // The mechanism is perverse: the anchor is only vulnerable ONCE IT IS CORRECT. While it sits on a
                // double, ordinary doubles look normal and nothing is rejected. The moment it finds the true single
                // loop, every double becomes an outlier, the streak builds, and the reset replaces the correct value
                // with one of the very readings it was right to distrust. Better measurement caused the destruction.
                //
                // Two changes. First, re-anchor to the MINIMUM OF THE RECENT WINDOW rather than to `loop` — that
                // triggering sample is by definition an outlier (> 1.6x the anchor) and may be a double or a triple.
                // Second, keep the window, median, EMA and sample count: a rolling window already ages out stale data,
                // which is the job this reset was invented to do.
                float reanchor = loop;
                if (m_LineLoopWindow.TryGetValue(line, out List<float> win) && win.Count > 0)
                {
                    float wmin = win[0];
                    for (int i = 1; i < win.Count; i++) if (win[i] < wmin) wmin = win[i];
                    if (wmin > 0f) reanchor = wmin;
                }
                m_LineLoopMin[line] = reanchor;
                m_LineRejectStreak.Remove(line);
                return true;                     // accepted: it flows into the window and EMA like any other sample
            }
            m_LineRejectStreak[line] = streak;
            return false;
        }

        // Add an accepted lap to the line's rolling window and recompute its median. Called only from MeasureLap, so
        // the sample has already passed AcceptLoopSample. Recomputing on INSERT rather than on read keeps the readers
        // allocation-free — they run from the UI on every panel refresh as well as from the dispatch.
        private void PushLoopSample(Entity line, uint loop)
        {
            if (!m_LineLoopWindow.TryGetValue(line, out List<float> w))
                m_LineLoopWindow[line] = w = new List<float>(kLoopWindow);
            w.Add(loop);
            if (w.Count > kLoopWindow)
                w.RemoveAt(0);              // oldest out: a rolling window also self-heals a route that got longer
            // ONLY publish a median once the WINDOW itself is deep enough — never off the persisted sample count.
            // n is restored from the save but the window is not, so a reloaded line sits at n=106 with an empty window;
            // without this guard its very first fresh lap would publish a ONE-SAMPLE "median" and, because
            // n >= kMedianMinSample already passes, immediately drive the headway off that single lap. That is
            // precisely the fragility the median exists to remove.
            if (w.Count >= kMedianMinSample)
                m_LineLoopMedian[line] = Median(w);
        }

        // Median of the window. Sorts a scratch COPY so the window itself stays in arrival order — the ring has to drop
        // the OLDEST entry, not the smallest. Even counts average the two middles.
        private readonly List<float> m_MedianScratch = new List<float>(kLoopWindow);
        private float Median(List<float> w)
        {
            if (w == null || w.Count == 0) return 0f;
            m_MedianScratch.Clear();
            m_MedianScratch.AddRange(w);
            m_MedianScratch.Sort();
            int c = m_MedianScratch.Count;
            return (c & 1) == 1 ? m_MedianScratch[c / 2]
                                : 0.5f * (m_MedianScratch[c / 2 - 1] + m_MedianScratch[c / 2]);
        }

        // The per-line real-travel-time factor (dimensionless, RT-invariant): (real loop) / (estimated loop). Nothing
        // derives BEHAVIOUR from this any more — the headway reads the loop directly — but the panel still shows it,
        // because "the game thinks this line takes 40 minutes and it really takes 95" is the single most useful thing
        // a player can know about why their vehicle count buys the headway it does.
        public float LineCorrection(Entity line, float durUnits, bool forFleet)
        {
            float estFrames = durUnits * 60f;
            float factor;
            // THREE-STAGE LADDER; each rung is only taken once the data supports it.
            //   < kMinTrustSamples   density prior
            //   >= kMinTrustSamples  the ANCHOR, conservative. It errs LOW on purpose.
            //   >= kMedianMinSample  the MEDIAN, accurate and robust to the doubles that made the EMA useless.
            //                        Deliberately NOT at 4: a median of four is barely more robust than a minimum,
            //                        because two doubles out of four already captures it.
            bool haveN = m_LineLoopSamples.TryGetValue(line, out int n);
            if (haveN && n >= kMedianMinSample
                && m_LineLoopMedian.TryGetValue(line, out float med) && med > 0f && estFrames > 1f)
                factor = med / estFrames;                                            // measured, median of the window
            else if (haveN && n >= kMinTrustSamples
                && m_LineLoopMin.TryGetValue(line, out float minLoop) && estFrames > 1f)
                factor = minLoop / estFrames;                                        // measured, conservative anchor
            else
                factor = ScheduleMath.DensityPriorRatio(CountStops(line), durUnits); // bootstrap from stop density
            return ScheduleMath.ClampCorrection(factor, forFleet);
        }

        // True once the line's loop is driven by LIVE measurement (>= kMinTrustSamples clean loops) rather than the
        // density prior — used by the panel to label the figure "measured" vs "estimated".
        public bool LineCorrectionMeasured(Entity line)
            => m_LineLoopSamples.TryGetValue(line, out int n) && n >= kMinTrustSamples;

        // The measured loop in FRAMES, down the SAME ladder LineCorrection walks: window median once there are enough
        // samples for a median to mean anything, the conservative anchor before that, nothing at all below the trust
        // threshold. ONE function, so the headway, the published offsets and the panel can never be reading three
        // different numbers — they were, once, and the printed board ended up ~45 minutes from the vehicles.
        //
        // The EMA is still maintained — the [SelfTest] laptime line reports it next to the anchor and the median, which
        // is how divergence becomes visible — but nothing derives behaviour from it.
        public bool TryMeasuredLoopFrames(Entity line, out float frames)
        {
            frames = 0f;
            if (!m_LineLoopSamples.TryGetValue(line, out int n) || n < kMinTrustSamples)
                return false;
            if (n >= kMedianMinSample && m_LineLoopMedian.TryGetValue(line, out float med) && med > 0f)
            {
                frames = med;
                return true;
            }
            if (m_LineLoopMin.TryGetValue(line, out float min) && min > 0f)
            {
                frames = min;
                return true;
            }
            return false;
        }

        // Lap-timing progress for the panel: how many clean loops this line has contributed, and how many it needs
        // before the measured loop takes over from the cold-start estimate.
        public int LineLoopSampleCount(Entity line)
            => m_LineLoopSamples.TryGetValue(line, out int n) ? n : 0;
        public static int MinTrustSamples => kMinTrustSamples;
        public static int MedianMinSamples => kMedianMinSample;

        // Laps in the ROLLING WINDOW, which is what actually gates the median — not LineLoopSampleCount, which is the
        // lifetime total and is restored from the save while the window is not. Showing the lifetime count against the
        // median threshold produced "42 of 10 laps" in the panel.
        public int LineLoopWindowCount(Entity line)
            => m_LineLoopWindow.TryGetValue(line, out List<float> w) ? w.Count : 0;

        // Which rung of the ladder this line is on, for the panel: 0 = nothing measured yet, 1 = the conservative
        // ANCHOR is driving it, 2 = the MEDIAN is. Mirrors the branch order in LineCorrection exactly, so the label can
        // never disagree with the number.
        public int LineCorrectionStage(Entity line)
        {
            int n = m_LineLoopSamples.TryGetValue(line, out int c) ? c : 0;
            if (n >= kMedianMinSample && m_LineLoopMedian.TryGetValue(line, out float med) && med > 0f) return 2;
            if (n >= kMinTrustSamples && m_LineLoopMin.ContainsKey(line)) return 1;
            return 0;
        }

        // ===================== WHAT THE UI READS =====================
        // Every one of these hands back a number the DISPATCH produced. None of them may be re-derived on the UI side:
        // two independent calculations of the same quantity is exactly how the printed board once came to disagree with
        // the vehicles by ~45 minutes, and the fix was to have one producer and one consumer.
        //
        // Safe to call from the UI phase without locking: UI runs BEFORE the simulation each frame, so these are always
        // the last COMPLETED dispatch tick (at most 8 sim frames old, and not advancing at all while paused).

        // The line's current headway in MINUTES, and how long until the next vehicle is due to leave its terminus.
        // False when the line is not being regulated at all (disabled, out of service, or no usable loop).
        //
        // nextDepartureInMinutes is NEGATIVE when no departure has been observed yet — a line that has just been
        // switched on, or one whose first vehicle is still driving out of the depot. That is a genuinely different
        // state from "due now", and collapsing the two to 0 made the panel announce an imminent departure on a line
        // with nothing on it. The caller must branch on the sign rather than printing the number.
        public bool TryLineHeadway(Entity line, out float headwayMinutes, out float nextDepartureInMinutes)
        {
            headwayMinutes = 0f;
            nextDepartureInMinutes = -1f;
            if (m_Fpm <= 0.01f || !m_LineHeadway.TryGetValue(line, out float hf) || hf <= 0f)
                return false;
            headwayMinutes = hf / m_Fpm;
            if (m_LastDepA.TryGetValue(line, out uint last))
            {
                double due = (double)last + hf;
                double delta = (due - m_Sim.frameIndex) / m_Fpm;
                nextDepartureInMinutes = delta > 0.0 ? (float)delta : 0f;
            }
            return true;
        }

        // Travel offset (minutes after the terminus departure) for one waypoint. False => the dispatch has not walked
        // this line yet (no plan, or the first tick after a load) and the caller falls back to the raw estimate.
        public bool TryPostedOffsetMinutes(Entity wp, out int minutes)
            => m_PostedOffset.TryGetValue(wp, out minutes);

        // Terminus B's pre-layover ARRIVAL offset — set only for that waypoint, where arrival and departure differ.
        public bool TryPostedArrivalMinutes(Entity wp, out int minutes)
            => m_PostedArrival.TryGetValue(wp, out minutes);

        // The vehicle count the dispatch settled on for this line. False => the mod is not sizing this line (count
        // management off, or the line is disabled) and the panel says so instead of inventing a number.
        public bool TryPostedFleet(Entity line, out int vehicles)
            => m_PostedFleet.TryGetValue(line, out vehicles);

        // How a single vehicle is doing against the spacing, for the vehicle info panel. Community request in its
        // original form was "is this train late" — under headway regulation there is no timetable to be late against,
        // and the honest equivalent is "is it evenly spaced from the one in front".
        public struct VehicleStatus
        {
            public bool m_Held;        // being held at a timing point right now
            public int  m_HoldMinutes; // ...for this many more minutes
            public bool m_AtTerminus;  // ...and that timing point is the line's terminus (normal) vs Terminus B
            public bool m_HaveGap;     // we have observed this vehicle leaving the terminus at least once
            public int  m_GapMinutes;  // the spacing it achieved on that departure
            public int  m_HeadwayMinutes;
            public int  m_Stage;       // loop-measurement stage, so a still-learning line does not present firm numbers
        }

        public bool TryVehicleStatus(Entity veh, out VehicleStatus st)
        {
            st = default;
            if (veh == Entity.Null || m_Fpm <= 0.01f || !EntityManager.HasComponent<CurrentRoute>(veh))
                return false;
            Entity line = EntityManager.GetComponentData<CurrentRoute>(veh).m_Route;
            if (line == Entity.Null || !EntityManager.HasComponent<TimetableSchedule>(line))
                return false;
            if (!EntityManager.GetComponentData<TimetableSchedule>(line).m_Enabled)
                return false;
            st.m_Stage = LineCorrectionStage(line);
            if (m_LineHeadway.TryGetValue(line, out float hf) && hf > 0f)
                st.m_HeadwayMinutes = (int)System.Math.Round(hf / m_Fpm);
            if (m_VehHoldUntil.TryGetValue(veh, out uint until))
            {
                uint now = m_Sim.frameIndex;
                st.m_Held = true;
                st.m_HoldMinutes = until > now ? (int)System.Math.Round((until - now) / m_Fpm) : 0;
                st.m_AtTerminus = IsVehicleAtTerminus(veh);
            }
            if (m_VehGap.TryGetValue(veh, out uint gap))
            {
                st.m_HaveGap = true;
                st.m_GapMinutes = (int)System.Math.Round(gap / m_Fpm);
            }
            return true;
        }

        // Is this vehicle standing at its line's terminus right now? The panel needs it to tell "waiting at the
        // terminus for its slot in the spacing" (normal, and the whole purpose of a terminus) apart from "waiting at
        // Terminus B" — two states a player reads differently.
        public bool IsVehicleAtTerminus(Entity veh)
        {
            if (veh == Entity.Null || !EntityManager.HasComponent<CurrentRoute>(veh)
                || !EntityManager.HasComponent<Target>(veh))
                return false;
            Entity line = EntityManager.GetComponentData<CurrentRoute>(veh).m_Route;
            if (line == Entity.Null || !EntityManager.HasComponent<TimetableSchedule>(line)
                || !EntityManager.HasBuffer<RouteWaypoint>(line))
                return false;
            FindTerminus(line, EntityManager.GetComponentData<TimetableSchedule>(line), out _, out Entity termWp);
            return termWp != Entity.Null && EntityManager.GetComponentData<Target>(veh).m_Target == termWp;
        }

        // Count the line's boarding stops (route waypoints connected to a stop platform), for the density prior.
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

        // Resolve this line's Terminus B to a usable (stop, waypoint, minimum-dwell) triple. False when there is
        // nothing usable: no component, zero minutes, the stop deleted or no longer boardable / on the route — the same
        // silent-fallback validity rules FindTerminus applies to the terminus itself — or the stop IS the effective
        // terminus. That last one matters: a second timing point on the same stop as the first is not a second timing
        // point, and it can happen without player error (delete the explicit terminus and the first-boarding-stop
        // fallback can land ON the B stop). Dropping B is the honest resolution — terminus wins.
        private bool TryActiveLayover(Entity line, TimetableSchedule sch, out Entity stop, out Entity waypoint, out int minutes)
        {
            stop = Entity.Null;
            waypoint = Entity.Null;
            minutes = 0;
            if (!EntityManager.HasComponent<LineLayover>(line))
                return false;
            LineLayover lay = EntityManager.GetComponentData<LineLayover>(line);
            if (lay.m_HoldMinutes == 0 || lay.m_Stop == Entity.Null || !EntityManager.Exists(lay.m_Stop)
                || !EntityManager.HasComponent<BoardingVehicle>(lay.m_Stop))
                return false;
            if (!EntityManager.HasBuffer<RouteWaypoint>(line))
                return false;
            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(line, isReadOnly: true);
            Entity wp = Entity.Null;
            for (int j = 0; j < waypoints.Length; j++)
            {
                Entity w = waypoints[j].m_Waypoint;
                if (EntityManager.HasComponent<Connected>(w)
                    && EntityManager.GetComponentData<Connected>(w).m_Connected == lay.m_Stop)
                { wp = w; break; }
            }
            if (wp == Entity.Null)
                return false;                                       // stop no longer on this route
            FindTerminus(line, sch, out Entity termStop, out _);
            if (termStop == lay.m_Stop)
                return false;                                       // B == effective terminus: terminus wins
            stop = lay.m_Stop;
            waypoint = wp;
            minutes = lay.MinDwellMinutes;
            return true;
        }

        // UI-facing overload: reads the schedule itself and applies the SAME validity rules the dispatch uses, so the
        // panel can never advertise a Terminus B the dispatch dropped.
        public bool TryActiveLayover(Entity line, out Entity stop, out int minutes)
        {
            stop = Entity.Null;
            minutes = 0;
            if (!EntityManager.HasComponent<TimetableSchedule>(line))
                return false;
            TimetableSchedule sch = EntityManager.GetComponentData<TimetableSchedule>(line);
            return TryActiveLayover(line, sch, out stop, out _, out minutes);
        }

        // Seed the in-memory measurement dicts from the persisted LineMeasuredTravel component the first time a line is
        // seen with no live measurement (fresh load, or a re-enabled line), so the headway uses the real learned loop
        // immediately instead of the cold density prior. Values are raw sim FRAMES (day-length invariant), so they feed
        // straight in; a stored count >= kMinTrustSamples is trusted at once, and any staleness (route changed while
        // disabled) self-heals via the existing reject/re-anchor path.
        private void RehydrateMeasured(Entity line)
        {
            if (!EntityManager.HasComponent<LineMeasuredTravel>(line)) return;
            // The depot lead is seeded INDEPENDENTLY of the loop measurement, and before the loop's early-outs: a line
            // can have a stored lead and no usable loop yet (it spawned a vehicle but has not completed a lap), and
            // that lead is exactly what the first peak after a reload needs.
            if (!m_LineDepotLead.ContainsKey(line))
            {
                float storedLead = EntityManager.GetComponentData<LineMeasuredTravel>(line).m_DepotLeadFrames;
                if (storedLead > 0f) m_LineDepotLead[line] = storedLead;
            }
            if (m_LineLoopSamples.ContainsKey(line)) return;                 // already have live/seeded data
            LineMeasuredTravel c = EntityManager.GetComponentData<LineMeasuredTravel>(line);
            if (c.m_LoopSamples <= 0 || !(c.m_LoopEmaFrames > 0f)) return;   // nothing meaningful stored (also rejects NaN)
            m_LineLoopEma[line] = c.m_LoopEmaFrames;
            m_LineLoopMin[line] = c.m_LoopMinFrames > 0f ? c.m_LoopMinFrames : c.m_LoopEmaFrames;
            m_LineLoopSamples[line] = c.m_LoopSamples;
            // The WINDOW is not persisted (32 floats per line is not worth the save space), only its median. So a
            // reloaded line keeps using the stored median until it re-earns kMedianMinSample fresh laps, instead of
            // dropping to the lower anchor and churning every headway down and back up on every single load.
            if (c.m_LoopMedianFrames > 0f) m_LineLoopMedian[line] = c.m_LoopMedianFrames;
        }

        // Write the current in-memory measurement back into the line's LineMeasuredTravel component so it survives
        // save/load. Non-structural (SetComponentData on an existing component), and only when a value actually
        // changed, to avoid churning the chunk version every tick.
        private void MirrorMeasured(Entity line)
        {
            if (!EntityManager.HasComponent<LineMeasuredTravel>(line)) return;
            float lead = m_LineDepotLead.TryGetValue(line, out float dl) ? dl : 0f;
            // NOT gated on having a loop sample, unlike the fields below: the depot lead is learned from a single spawn
            // and is useful long before the line has completed a timed lap, so gating it on the loop would throw away
            // exactly the case it is there for (a brand-new line's first peak).
            if (!m_LineLoopSamples.TryGetValue(line, out int samples) || samples <= 0)
            {
                LineMeasuredTravel leadOnly = EntityManager.GetComponentData<LineMeasuredTravel>(line);
                if (leadOnly.m_DepotLeadFrames != lead)
                {
                    leadOnly.m_DepotLeadFrames = lead;
                    EntityManager.SetComponentData(line, leadOnly);
                }
                return;
            }
            float ema = m_LineLoopEma.TryGetValue(line, out float e) ? e : 0f;
            float min = m_LineLoopMin.TryGetValue(line, out float m) ? m : 0f;
            float med = m_LineLoopMedian.TryGetValue(line, out float md) ? md : 0f;
            ushort sc = samples > ushort.MaxValue ? ushort.MaxValue : (ushort)samples;
            LineMeasuredTravel cur = EntityManager.GetComponentData<LineMeasuredTravel>(line);
            if (cur.m_LoopEmaFrames == ema && cur.m_LoopMinFrames == min && cur.m_LoopSamples == sc
                && cur.m_LoopMedianFrames == med && cur.m_DepotLeadFrames == lead) return;              // unchanged
            EntityManager.SetComponentData(line, new LineMeasuredTravel {
                m_LoopEmaFrames = ema, m_LoopMinFrames = min, m_LoopSamples = sc, m_LoopMedianFrames = med,
                m_DepotLeadFrames = lead });
        }

        // Fold one observed depot->terminus drive into this line's lead EMA. Samples outside the sanity bound are
        // DISCARDED rather than clamped: an over-long span means the vehicle was not actually fresh out of the depot
        // (a census miss, a route edit mid-drive), so it carries no information about the drive and clamping it would
        // quietly bias every future peak early by the bound.
        private void AcceptDepotLead(Entity line, uint spanFrames)
        {
            if (m_Fpm <= 0.01f || spanFrames == 0)
                return;
            float minutes = spanFrames / m_Fpm;
            if (minutes <= 0f || minutes > kMaxDepotLeadMinutes)
                return;
            // Weighted toward the running value: depot runs are consistent, and one unlucky sample (traffic, a vehicle
            // that spawned into a jam) should not swing the next peak's timing.
            m_LineDepotLead[line] = m_LineDepotLead.TryGetValue(line, out float prev) && prev > 0f
                ? prev * 0.7f + spanFrames * 0.3f
                : spanFrames;
        }

        // This line's measured depot lead in in-game MINUTES, 0 when it has never been observed dispatching one. Zero
        // is the honest default and it simply switches the look-ahead off — the first spawn teaches the line its own
        // number, and it is persisted (LineMeasuredTravel v3) so a reload does not have to re-learn it by being late
        // for one more peak.
        private int DepotLeadMinutes(Entity line)
        {
            if (m_Fpm <= 0.01f || !m_LineDepotLead.TryGetValue(line, out float frames) || frames <= 0f)
                return 0;
            int min = (int)System.Math.Round(frames / m_Fpm);
            if (min < 0) min = 0;
            if (min > (int)kMaxDepotLeadMinutes) min = (int)kMaxDepotLeadMinutes;
            return min;
        }

        // Clean uninstall (Options button): wipe every trace of the mod from the current save. For each managed line
        // revert the mutated vanilla state (restore the unbunching factor, release any held vehicle, drop the
        // mod-applied vehicle count) and REMOVE the mod's serialized components, then forget all in-memory tracking.
        // After this the save contains no mod data, so the player can save and remove the mod with zero residue.
        // Structural removes go through an ECB (played back after the read pass). Safe to run with the mod still
        // installed — the lines just go back to plain vanilla.
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
                // Repair rather than blind-clear. TryClearLineFleet would zero the VehicleInterval slot AND deactivate
                // the vehicle-count policy on EVERY line — including lines this mod never sized ("another mod
                // decides", or a count the player set by hand), silently wiping their own "Assigned Vehicles". The heal
                // instead rebuilds the slot from the line's OWN active policies. No-op on a line we never touched.
                m_Fleet.TryHealLeftoverFleetModifier(line);
                ecb.RemoveComponent<TimetableSchedule>(line);
                if (EntityManager.HasComponent<LineFleetPlan>(line)) ecb.RemoveComponent<LineFleetPlan>(line);
                if (EntityManager.HasComponent<CustomPeakSchedule>(line)) ecb.RemoveComponent<CustomPeakSchedule>(line);
                if (EntityManager.HasComponent<LineMeasuredTravel>(line)) ecb.RemoveComponent<LineMeasuredTravel>(line);
                if (EntityManager.HasComponent<LineLayover>(line)) ecb.RemoveComponent<LineLayover>(line);
                n++;
            }
            // Stop rules, over their OWN query: a line that was un-managed after a rule was set on it is not in
            // m_LineQuery at all, so the loop above cannot reach it. Removing the buffer is the whole revert — the
            // restriction itself lives only in the pathfind graph, and StopRuleSystem hands those edges back to vanilla
            // on its next tick when it sees the rules gone.
            NativeArray<Entity> ruled = m_StopRuleQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < ruled.Length; i++)
                ecb.RemoveComponent<LineStopRule>(ruled[i]);
            ecb.Playback(EntityManager);
            ecb.Dispose();
            ruled.Dispose();
            lines.Dispose();
            // Forget ALL in-memory tracking so nothing re-applies to the now-vanilla lines.
            m_LastFleet.Clear(); m_PendingRetire.Clear(); m_LapServed.Clear(); m_LapFront.Clear();
            m_LineLoopEma.Clear(); m_LineLoopWindow.Clear(); m_LineLoopMedian.Clear();
            m_LineLoopSamples.Clear(); m_LineLoopMin.Clear(); m_LineRejectStreak.Clear();
            m_LastDur.Clear(); m_DurStable.Clear(); m_RampSince.Clear();
            m_LastDepA.Clear(); m_LastDepB.Clear(); m_FrontB.Clear(); m_LineHeadway.Clear();
            m_VehHoldUntil.Clear(); m_VehGap.Clear();
            m_ArrivedFrame.Clear(); m_VehTerminusDepart.Clear(); m_Committed.Clear();
            m_LineDepotLead.Clear(); m_VehFirstSeen.Clear(); m_KnownVeh.Clear(); m_VehCensusPending = true;
            m_VehStopHold.Clear(); m_VehHoldFrames.Clear();
            m_PostedOffset.Clear(); m_PostedArrival.Clear(); m_PostedFleet.Clear();
            Mod.log.Info($"[SelfTest] clean uninstall: reverted {n} line(s) to vanilla and removed all mod components. " +
                         "Save your city; the mod can now be removed with no residue.");
        }

        // ONE-TIME-PER-LOAD global repair of the unbunching residue an OLD version (before v0.2.3) of this mod left in
        // saves. That version set TransportLine.m_UnbunchingFactor = 0 on the lines it managed. That field is
        // SERIALIZED into the save and vanilla NEVER restores it, so an affected line — even after the mod is removed
        // or the line is un-managed — leaves vehicles unable to unbunch: they depart a stop immediately regardless of
        // spacing (RouteUtils.CalculateDepartureFrame multiplies the hold by this factor). RestoreUnbunching only heals
        // lines that STILL carry a TimetableSchedule; this sweep covers EVERY line so a re-subscribe + load + save
        // repairs any affected save with no per-line steps.
        //
        // CONSERVATIVE: only a factor of EXACTLY 0 (the value the old version wrote) is healed, restored to the LINE
        // PREFAB's own m_DefaultUnbunchingFactor; a prefab whose default is genuinely 0 is skipped. A no-op on a
        // healthy save. Vanilla has no path to a 0 factor and no known mod writes one, so the sole theoretical
        // collision — another mod deliberately setting 0 to disable unbunching — would also be reset here; that is an
        // accepted, vanishingly rare trade-off.
        private void GlobalHealUnbunching()
        {
            if (m_HealQuery.IsEmptyIgnoreFilter)
                return;
            // When the master switch is OFF, the dispatch loop manages NO line (every line takes the "hand back to
            // vanilla" branch), so a still-managed line's leftover fleet modifier would otherwise be skipped by the
            // `managed` guard below and never cleared on a disabled load. Treat master-off as "nothing managed".
            TransitTimetablesSetting master = Mod.ActiveSetting;
            bool masterOn = master != null && master.Enabled;
            NativeArray<Entity> lines = m_HealQuery.ToEntityArray(Allocator.Temp);
            int factorHealed = 0, fleetHealed = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                Entity line = lines[i];

                // (1) Unbunching-factor residue (a pre-v0.2.3 version wrote 0f): restore to the prefab default. Only
                // the exact damage (== 0) is touched; a healthy/custom value and a line type whose default is 0 are
                // left alone.
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
                // immediately" after a plain uninstall. Skip lines we are ACTIVELY managing: the dispatch loop
                // re-asserts their modifier this same tick, and TryHealLeftoverFleetModifier is safe by recomputing
                // from the line's own policies (never clobbers a player's manual vehicle count). "managed" also
                // requires that the MOD is the one sizing: under "another mod decides" the dispatch loop does NOT
                // re-assert the fleet modifier, so a still-managed line's leftover residue must be healed on load
                // instead of skipped — otherwise it freezes in the save (the issue-#7 class of bug).
                bool managed = masterOn && master.ModSizesFleet
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

        // Put the line's spacing behaviour back to the prefab default. Called for EVERY line carrying a
        // TimetableSchedule (enabled or not), so it also repairs a still-managed line that a before-v0.2.3 version
        // damaged. Only writes when the value actually differs from the prefab default, so it is a no-op on a healthy
        // line.
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
