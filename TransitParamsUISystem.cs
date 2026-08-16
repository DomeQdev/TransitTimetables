using System.Collections.Generic;
using System.Text;
using Colossal.UI.Binding;
using Game.Common;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.Tools;
using Game.UI;
using Unity.Entities;

namespace TransitTimetables
{
    // Backs both mod UIs, driven by the current tool selection:
    //   * A transport LINE is selected  -> the service-plan editor (injected into the native line info panel).
    //   * A STOP is selected            -> the arrivals board (every line's expected arrivals here), shown in the
    //                                      floating panel, which auto-opens; plus the timing-point controls.
    //
    // ONE RULE GOVERNS THIS WHOLE FILE: every number that describes what the vehicles are doing is READ FROM THE
    // DISPATCH, never re-derived here. The headway, the vehicle count, each stop's travel offset and the time until the
    // next departure all come from TimetableDispatchSystem. A previous version recomputed them from the same raw inputs
    // and the two drifted apart until the printed board disagreed with the vehicles by about 45 minutes. Two
    // calculations of one quantity will always diverge eventually; there is only one now.
    public partial class TransitParamsUISystem : UISystemBase
    {
        private const string Group = "TransitParams";

        private TimeSystem m_TimeSystem;
        private HourlyFleetSystem m_Fleet;
        private TimebaseSystem m_Timebase;
        private TimetableDispatchSystem m_Dispatch;
        private ToolSystem m_ToolSystem;
        private NameSystem m_NameSystem;              // #10: the line's editable/custom name for the board

        // Selected LINE service plan cache.
        private bool m_SelHas;
        private bool m_SelTtEnabled;
        // The three (four with a custom peak) numbers the player now actually sets: VEHICLES per window.
        private int m_SelPeakVeh = 6, m_SelOffPeakVeh = 4, m_SelNightVeh = 2;
        // ...and what the mod makes of them: the count it applied, how many are really out, and the resulting headway.
        private int m_SelTtFleet, m_SelTtServing, m_SelTtHeadway, m_SelTtNextMin = -1;
        private string m_SelVehInfo = "";           // selected VEHICLE: spacing status
        private string m_SelTtRealInfo = "";        // real loop vs the game's estimate, and what is driving it
        private int m_SelTtTerminus;                // 0 chosen+usable, 1 never chosen, 2 chosen but no longer usable
        private int m_SelTtLayover;                 // 0 none, 1 active, 2 blocked (it IS the terminus), 3 orphaned (off-route)
        private int m_SelTtLayoverMin;              // the configured minimum dwell at Terminus B
        // Stop rules set on stops this line no longer serves. The stop board can only offer controls on a row it
        // lists, and an orphaned rule's stop produces no row — so without this the setting would be invisible AND
        // unremovable, and would spring back to life if the route were ever edited back.
        private int m_SelTtRuleOrphans;
        private int m_SelSchedule = 2;              // RouteSchedule: 0=Day, 1=Night, 2=DayAndNight (which counts apply)
        private string m_PeakHours = "", m_NightHours = "";
        private GetterValueBinding<bool> m_SelHasB, m_SelTtEnabledB;
        private GetterValueBinding<int> m_SelPeakVehB, m_SelOffPeakVehB, m_SelNightVehB;
        private GetterValueBinding<int> m_SelTtFleetB, m_SelTtServingB, m_SelTtHeadwayB, m_SelTtNextMinB, m_SelScheduleB;
        private GetterValueBinding<string> m_PeakHoursB, m_NightHoursB, m_SelTtRealInfoB, m_SelVehInfoB;
        private GetterValueBinding<int> m_SelTtTerminusB;
        private GetterValueBinding<int> m_SelTtLayoverB, m_SelTtLayoverMinB;
        private GetterValueBinding<int> m_SelTtRuleOrphansB;
        // Per-line custom peak: enabled + its own vehicle count + two hour windows.
        private bool m_SelCustomPeakEnabled;
        private int m_SelCustomPeakVeh = 8, m_SelCustomPeakStart1 = 7, m_SelCustomPeakEnd1 = 9, m_SelCustomPeakStart2 = 16, m_SelCustomPeakEnd2 = 18;
        private GetterValueBinding<bool> m_SelCustomPeakEnabledB;
        private GetterValueBinding<int> m_SelCustomPeakVehB, m_SelCustomPeakStart1B, m_SelCustomPeakEnd1B, m_SelCustomPeakStart2B, m_SelCustomPeakEnd2B;

        // Selected STOP arrivals board.
        private bool m_SelStopHas;
        private string m_SelStopBoard = "[]";
        private int m_AutoOpen;
        private Entity m_LastSel = Entity.Null;
        // RESOLVED selection: the stop(s) whose lines the board shows — one for a roadside bus/tram stop; ALL platform
        // sub-objects for a station BUILDING (train / metro / airport / harbor). Cached (keyed by the raw selection) so
        // a selected station isn't re-walked every UI tick.
        private readonly List<Entity> m_SelStops = new List<Entity>();
        // Where a selection's stops are gathered before they are allowed to replace m_SelStops — see ResolveSelectedStops.
        private readonly List<Entity> m_ScratchStops = new List<Entity>();
        private Entity m_ResolveRawSel = Entity.Null;
        // Per board ROW, the (line, stop) it represents — so each row's OWN buttons target exactly that line at exactly
        // the platform it uses here. Built in lockstep with the board JSON (row i == m_BoardRows[i]).
        private readonly List<(Entity line, Entity stop)> m_BoardRows = new List<(Entity, Entity)>();
        // Bumped once when the dispatch decides this city needs the one-time notice; the React side raises the dialog
        // on the change. static so the dispatch (a different system) can request it without a lookup.
        private static int m_NoticeSeq;
        private GetterValueBinding<int> m_NoticeSeqB;
        public static void RaiseMigrationNotice() => m_NoticeSeq++;

        private GetterValueBinding<bool> m_SelStopHasB;
        private GetterValueBinding<string> m_SelStopBoardB;
        private GetterValueBinding<int> m_AutoOpenB;

        // "Selected line" context for the stop board's per-line terminus button: the last TransportLine the player
        // opened (the one whose route shows in the left panel), so "terminus for Line X" targets exactly that line.
        private Entity m_LastLine = Entity.Null;
        private int m_SelStopLineNum;
        private bool m_SelStopLineServes;          // does m_LastLine serve the selected stop AND carry a plan?
        private GetterValueBinding<int> m_SelStopLineNumB;
        private GetterValueBinding<bool> m_SelStopLineServesB;

        private static TransitTimetablesSetting S => Mod.ActiveSetting;

        // Bound on a per-window vehicle count. The upper end mirrors the dispatch's own runaway backstop; the lower end
        // is 1 rather than 0 because "no vehicles in this window" is expressed by the line's day/night schedule (which
        // vanilla owns), not by typing a zero here — a zero would ask vanilla for an undefined vehicle interval.
        private const int kMinVehicles = 1;
        private const int kMaxVehicles = 150;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_TimeSystem = World.GetOrCreateSystemManaged<TimeSystem>();
            m_Fleet = World.GetOrCreateSystemManaged<HourlyFleetSystem>();
            m_Timebase = World.GetOrCreateSystemManaged<TimebaseSystem>();
            m_Dispatch = World.GetOrCreateSystemManaged<TimetableDispatchSystem>();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_NameSystem = World.GetOrCreateSystemManaged<NameSystem>();

            m_SelHasB = new GetterValueBinding<bool>(Group, "selHas", () => m_SelHas);
            m_SelTtEnabledB = new GetterValueBinding<bool>(Group, "selTtEnabled", () => m_SelTtEnabled);
            m_SelPeakVehB = new GetterValueBinding<int>(Group, "selPeakVeh", () => m_SelPeakVeh);
            m_SelOffPeakVehB = new GetterValueBinding<int>(Group, "selOffPeakVeh", () => m_SelOffPeakVeh);
            m_SelNightVehB = new GetterValueBinding<int>(Group, "selNightVeh", () => m_SelNightVeh);
            m_SelTtFleetB = new GetterValueBinding<int>(Group, "selTtFleet", () => m_SelTtFleet);
            m_SelTtServingB = new GetterValueBinding<int>(Group, "selTtServing", () => m_SelTtServing);
            m_SelTtHeadwayB = new GetterValueBinding<int>(Group, "selTtHeadway", () => m_SelTtHeadway);
            m_SelTtNextMinB = new GetterValueBinding<int>(Group, "selTtNextMin", () => m_SelTtNextMin);
            m_SelTtRealInfoB = new GetterValueBinding<string>(Group, "selTtRealInfo", () => m_SelTtRealInfo ?? "");
            m_SelTtTerminusB = new GetterValueBinding<int>(Group, "selTtTerminus", () => m_SelTtTerminus);
            m_SelTtLayoverB = new GetterValueBinding<int>(Group, "selTtLayover", () => m_SelTtLayover);
            m_SelTtLayoverMinB = new GetterValueBinding<int>(Group, "selTtLayoverMin", () => m_SelTtLayoverMin);
            m_SelTtRuleOrphansB = new GetterValueBinding<int>(Group, "selTtRuleOrphans", () => m_SelTtRuleOrphans);
            m_SelVehInfoB = new GetterValueBinding<string>(Group, "selVehInfo", () => m_SelVehInfo ?? "");
            m_SelCustomPeakEnabledB = new GetterValueBinding<bool>(Group, "selCustomPeakEnabled", () => m_SelCustomPeakEnabled);
            m_SelCustomPeakVehB = new GetterValueBinding<int>(Group, "selCustomPeakVeh", () => m_SelCustomPeakVeh);
            m_SelCustomPeakStart1B = new GetterValueBinding<int>(Group, "selCustomPeakStart1", () => m_SelCustomPeakStart1);
            m_SelCustomPeakEnd1B = new GetterValueBinding<int>(Group, "selCustomPeakEnd1", () => m_SelCustomPeakEnd1);
            m_SelCustomPeakStart2B = new GetterValueBinding<int>(Group, "selCustomPeakStart2", () => m_SelCustomPeakStart2);
            m_SelCustomPeakEnd2B = new GetterValueBinding<int>(Group, "selCustomPeakEnd2", () => m_SelCustomPeakEnd2);
            m_SelScheduleB = new GetterValueBinding<int>(Group, "selSchedule", () => m_SelSchedule);
            m_PeakHoursB = new GetterValueBinding<string>(Group, "peakHours", () => m_PeakHours ?? "");
            m_NightHoursB = new GetterValueBinding<string>(Group, "nightHours", () => m_NightHours ?? "");
            m_SelStopHasB = new GetterValueBinding<bool>(Group, "selStopHas", () => m_SelStopHas);
            m_SelStopBoardB = new GetterValueBinding<string>(Group, "selStopBoard", () => m_SelStopBoard ?? "[]");
            m_AutoOpenB = new GetterValueBinding<int>(Group, "autoOpen", () => m_AutoOpen);
            m_SelStopLineNumB = new GetterValueBinding<int>(Group, "selStopLineNum", () => m_SelStopLineNum);
            m_SelStopLineServesB = new GetterValueBinding<bool>(Group, "selStopLineServes", () => m_SelStopLineServes);
            AddBinding(m_SelHasB);
            AddBinding(m_SelTtEnabledB);
            AddBinding(m_SelPeakVehB);
            AddBinding(m_SelOffPeakVehB);
            AddBinding(m_SelNightVehB);
            AddBinding(m_SelTtFleetB);
            AddBinding(m_SelTtServingB);
            AddBinding(m_SelTtHeadwayB);
            AddBinding(m_SelTtNextMinB);
            AddBinding(m_SelTtRealInfoB);
            AddBinding(m_SelTtTerminusB);
            AddBinding(m_SelTtLayoverB);
            AddBinding(m_SelTtLayoverMinB);
            AddBinding(m_SelTtRuleOrphansB);
            AddBinding(m_SelVehInfoB);
            AddBinding(m_SelCustomPeakEnabledB);
            AddBinding(m_SelCustomPeakVehB);
            AddBinding(m_SelCustomPeakStart1B);
            AddBinding(m_SelCustomPeakEnd1B);
            AddBinding(m_SelCustomPeakStart2B);
            AddBinding(m_SelCustomPeakEnd2B);
            AddBinding(m_SelScheduleB);
            AddBinding(m_PeakHoursB);
            AddBinding(m_NightHoursB);
            AddBinding(m_SelStopHasB);
            AddBinding(m_SelStopBoardB);
            AddBinding(m_AutoOpenB);
            AddBinding(m_SelStopLineNumB);
            AddBinding(m_SelStopLineServesB);
            // One-time notice: a COUNTER, not a bool. A React effect that watches a counter cannot miss the signal even
            // if it mounts after the bump — useValue hands it the current value on mount — whereas a fire-and-forget
            // event raised while the Game screen is still mounting is simply dropped.
            m_NoticeSeqB = new GetterValueBinding<int>(Group, "noticeSeq", () => m_NoticeSeq);
            AddBinding(m_NoticeSeqB);
            AddBinding(new TriggerBinding<bool>(Group, "noticeAnswer", TimetableDispatchSystem.AnswerMigrationNotice));

            AddBinding(new TriggerBinding<bool>(Group, "setSelTtEnabled", SetSelEnabled));
            // THE PLAN. Every one of these is an absolute vehicle count for one time-of-day window.
            AddBinding(new TriggerBinding<int>(Group, "setSelPeakVeh", v => MutatePlan(v, (ref LineFleetPlan p, int x) => p.m_PeakVehicles = (ushort)Clamp(x, kMinVehicles, kMaxVehicles))));
            AddBinding(new TriggerBinding<int>(Group, "setSelOffPeakVeh", v => MutatePlan(v, (ref LineFleetPlan p, int x) => p.m_OffPeakVehicles = (ushort)Clamp(x, kMinVehicles, kMaxVehicles))));
            AddBinding(new TriggerBinding<int>(Group, "setSelNightVeh", v => MutatePlan(v, (ref LineFleetPlan p, int x) => p.m_NightVehicles = (ushort)Clamp(x, kMinVehicles, kMaxVehicles))));
            AddBinding(new TriggerBinding<int>(Group, "setSelCustomPeakVeh", v => MutatePlan(v, (ref LineFleetPlan p, int x) => p.m_CustomPeakVehicles = (ushort)Clamp(x, kMinVehicles, kMaxVehicles))));
            // Per-line custom peak: enable + two hour windows (its vehicle count lives on the plan, above).
            AddBinding(new TriggerBinding<bool>(Group, "setSelCustomPeakEnabled", v => MutateCustomPeak(v, (ref CustomPeakSchedule c, bool on) => c.m_Enabled = on)));
            AddBinding(new TriggerBinding<int>(Group, "setSelCustomPeakStart1", v => MutateCustomPeak(v, (ref CustomPeakSchedule c, int x) => c.m_Start1 = (ushort)Clamp(x, 0, 23))));
            AddBinding(new TriggerBinding<int>(Group, "setSelCustomPeakEnd1", v => MutateCustomPeak(v, (ref CustomPeakSchedule c, int x) => c.m_End1 = (ushort)Clamp(x, 0, 23))));
            AddBinding(new TriggerBinding<int>(Group, "setSelCustomPeakStart2", v => MutateCustomPeak(v, (ref CustomPeakSchedule c, int x) => c.m_Start2 = (ushort)Clamp(x, 0, 23))));
            AddBinding(new TriggerBinding<int>(Group, "setSelCustomPeakEnd2", v => MutateCustomPeak(v, (ref CustomPeakSchedule c, int x) => c.m_End2 = (ushort)Clamp(x, 0, 23))));
            // Terminus scopes: one board row (its own line at its own platform), the open line, or every line here.
            AddBinding(new TriggerBinding<int>(Group, "setTerminusRow", SetTerminusRow));
            AddBinding(new TriggerBinding(Group, "setSelTerminusAll", () => SetSelectedStopAsTerminus(Entity.Null)));
            AddBinding(new TriggerBinding(Group, "setSelTerminusLine", () => { if (m_LastLine != Entity.Null) SetSelectedStopAsTerminus(m_LastLine); }));
            // Terminus B: make one board row's stop this line's SECOND timing point, with a minimum layover of N
            // minutes, sent as the ABSOLUTE value (the stepper idiom every other numeric trigger uses); 0 clears it.
            AddBinding(new TriggerBinding<int, int>(Group, "setLayoverRow", SetLayoverRow));
            // Clear the OPEN line's Terminus B from the line panel. The stop board can only offer removal on a row it
            // still lists, so one whose stop left the route would otherwise be unreachable — see LayoverState 3.
            AddBinding(new TriggerBinding(Group, "clearSelLayover", () =>
            {
                if (m_LastLine != Entity.Null && EntityManager.Exists(m_LastLine)
                    && EntityManager.HasComponent<LineLayover>(m_LastLine))
                { EntityManager.RemoveComponent<LineLayover>(m_LastLine); m_UiDirty = true; }
            }));
            // Per-stop boarding rule for one board row's (line, stop): 0 normal, 1 drop-off only, 2 pick-up only,
            // 3 technical. See LineStopRule.
            AddBinding(new TriggerBinding<int, int>(Group, "setStopRuleRow", SetStopRuleRow));
            // Drop the OPEN line's rules on stops it no longer serves — the only way to reach them, since the stop
            // board cannot list a row for a stop that left the route.
            AddBinding(new TriggerBinding(Group, "clearSelRuleOrphans", () =>
            {
                if (m_LastLine != Entity.Null && EntityManager.Exists(m_LastLine)
                    && StopRules.ClearOrphans(EntityManager, m_LastLine) > 0)
                    m_UiDirty = true;
            }));
        }

        // Switching a line ON creates BOTH components in one go. Creating only the marker and letting the dispatch
        // supply the plan on its next tick would leave the panel with nothing to show while the game is paused — which
        // is exactly when people set a line up.
        private void SetSelEnabled(bool on)
        {
            Entity sel = m_ToolSystem != null ? m_ToolSystem.selected : Entity.Null;
            if (sel == Entity.Null || !EntityManager.HasComponent<TransportLine>(sel))
                return;
            bool had = EntityManager.HasComponent<TimetableSchedule>(sel);
            TimetableSchedule sch = had ? EntityManager.GetComponentData<TimetableSchedule>(sel) : TimetableSchedule.Default();
            // Resolve the plan BEFORE adding the marker, so a line upgraded from an older save converts its stored
            // headways rather than being handed flat defaults (see TimetableDispatchSystem.ResolvePlan).
            LineFleetPlan plan = m_Dispatch != null ? m_Dispatch.ResolvePlan(sel) : LineFleetPlan.Default();
            sch.m_Enabled = on;
            if (!had)
                EntityManager.AddComponent<TimetableSchedule>(sel);
            EntityManager.SetComponentData(sel, sch);
            if (!EntityManager.HasComponent<LineFleetPlan>(sel))
                EntityManager.AddComponentData(sel, plan);
            m_UiDirty = true;
        }

        private delegate void RefPlanAction<T>(ref LineFleetPlan plan, T value);

        // Read-modify-write the selected line's vehicle plan, creating the component on first touch. The BASE it edits
        // is ResolvePlan's answer, never LineFleetPlan.Default(): on a line upgraded from an older save the component
        // may not exist yet, and starting from defaults would silently discard the service level its stored headways
        // convert into.
        private void MutatePlan<T>(T value, RefPlanAction<T> action)
        {
            Entity sel = m_ToolSystem != null ? m_ToolSystem.selected : Entity.Null;
            if (sel == Entity.Null || !EntityManager.HasComponent<TransportLine>(sel))
                return;
            LineFleetPlan plan = m_Dispatch != null ? m_Dispatch.ResolvePlan(sel) : LineFleetPlan.Default();
            action(ref plan, value);
            if (!EntityManager.HasComponent<LineFleetPlan>(sel))
                EntityManager.AddComponentData(sel, plan);
            else
                EntityManager.SetComponentData(sel, plan);
            m_UiDirty = true;   // the player just edited: recompute now, don't wait for the minute to tick
        }

        private delegate void RefCustomPeakAction<T>(ref CustomPeakSchedule c, T value);

        // Read-modify-write the selected line's CUSTOM PEAK component, creating it on first touch.
        private void MutateCustomPeak<T>(T value, RefCustomPeakAction<T> action)
        {
            Entity sel = m_ToolSystem != null ? m_ToolSystem.selected : Entity.Null;
            if (sel == Entity.Null || !EntityManager.HasComponent<TransportLine>(sel))
                return;
            bool had = EntityManager.HasComponent<CustomPeakSchedule>(sel);
            CustomPeakSchedule c = had ? EntityManager.GetComponentData<CustomPeakSchedule>(sel) : CustomPeakSchedule.Default();
            action(ref c, value);
            if (!had)
                EntityManager.AddComponent<CustomPeakSchedule>(sel);
            EntityManager.SetComponentData(sel, c);
            m_UiDirty = true;
        }

        // Make board rows their line's terminus. onlyLine == Entity.Null -> every line on the board (each to the
        // platform it uses here); otherwise -> just that one line. Works off m_BoardRows, so a station's multiple
        // platforms are each targeted correctly (each line -> its own platform).
        private void SetSelectedStopAsTerminus(Entity onlyLine)
        {
            for (int i = 0; i < m_BoardRows.Count; i++)
            {
                if (onlyLine != Entity.Null && m_BoardRows[i].line != onlyLine)
                    continue;
                SetLineTerminus(m_BoardRows[i].line, m_BoardRows[i].stop);
            }
        }

        // Set one board row's terminus (the per-row buttons). i is the JSON row index == m_BoardRows index.
        private void SetTerminusRow(int i)
        {
            if (i < 0 || i >= m_BoardRows.Count)
                return;
            SetLineTerminus(m_BoardRows[i].line, m_BoardRows[i].stop);
        }

        // Make one board row's stop this line's Terminus B with a minimum layover of `minutes`; 0 clears it.
        // Guarded to MANAGED lines only: CleanUninstall iterates the TimetableSchedule query, so a LineLayover on an
        // unmanaged line would be unreachable save residue — and the stop board does list unmanaged lines, so the
        // guard is load-bearing, not belt-and-braces.
        private void SetLayoverRow(int i, int minutes)
        {
            if (i < 0 || i >= m_BoardRows.Count)
                return;
            Entity line = m_BoardRows[i].line;
            Entity stop = m_BoardRows[i].stop;
            if (line == Entity.Null || stop == Entity.Null || !EntityManager.HasComponent<TimetableSchedule>(line))
                return;
            // -1 is the "set this stop" sentinel the button sends: when Terminus B is being MOVED from another stop,
            // keep the line's configured minutes — clicking Set on a new stop must not silently reset a tuned 15 back
            // to the default. A fresh set starts at 2. Resolved BEFORE the clamp: Clamp would turn the sentinel into 0,
            // which means "clear" — the exact opposite of the button's intent.
            if (minutes < 0)
            {
                minutes = EntityManager.HasComponent<LineLayover>(line)
                    ? EntityManager.GetComponentData<LineLayover>(line).m_HoldMinutes : 0;
                if (minutes <= 0) minutes = 2;
            }
            m_UiDirty = true;
            minutes = Clamp(minutes, 0, 60);
            if (minutes == 0)
            {
                // Cleared: REMOVE the component rather than storing zeros, so an unused Terminus B leaves no trace in
                // the save and TryActiveLayover's "no component" path stays the single meaning of "no second point".
                if (EntityManager.HasComponent<LineLayover>(line))
                    EntityManager.RemoveComponent<LineLayover>(line);
                return;
            }
            // Never on the effective terminus. The button is hidden on rows already wearing the terminus star, but the
            // first-boarding-stop fallback can move the effective terminus under us; the dispatch would silently drop
            // such a Terminus B (terminus wins), so refusing here keeps the UI honest with behaviour.
            TimetableSchedule sch = EntityManager.GetComponentData<TimetableSchedule>(line);
            Entity termWp = TerminusWaypoint(line, sch);
            Entity termStop = termWp != Entity.Null && EntityManager.HasComponent<Connected>(termWp)
                ? EntityManager.GetComponentData<Connected>(termWp).m_Connected : Entity.Null;
            if (termStop == stop)
                return;
            LineLayover lay = new LineLayover { m_Stop = stop, m_HoldMinutes = (ushort)minutes };
            if (EntityManager.HasComponent<LineLayover>(line)) EntityManager.SetComponentData(line, lay);
            else EntityManager.AddComponentData(line, lay);
        }

        // Set one board row's per-stop boarding rule (mode per LineStopRule; 0 clears it).
        //
        // Guarded to MANAGED lines only, exactly like SetLayoverRow: the board lists unmanaged lines too, and a rule set
        // on one would be save residue the player could not see.
        //
        // The TERMINUS is allowed, unlike Terminus B. A second timing point on the terminus is meaningless (the
        // terminus already IS one), but a restricted terminus is a real operating pattern and nothing in the spacing
        // depends on that stop being open to passengers — see the note in LineStopRule.
        private void SetStopRuleRow(int i, int mode)
        {
            if (i < 0 || i >= m_BoardRows.Count)
                return;
            Entity line = m_BoardRows[i].line;
            Entity stop = m_BoardRows[i].stop;
            if (line == Entity.Null || stop == Entity.Null || !EntityManager.HasComponent<TimetableSchedule>(line))
                return;
            if (mode < LineStopRule.None || mode > LineStopRule.Technical)
                return;
            StopRules.SetMode(EntityManager, line, stop, (byte)mode);
            m_UiDirty = true;
        }

        // Point a managed line's terminus at a stop it serves. No-op if the line has no plan or already points there.
        private void SetLineTerminus(Entity line, Entity stop)
        {
            m_UiDirty = true;   // terminus moved: the board's offsets and the star change immediately
            if (line == Entity.Null || stop == Entity.Null || !EntityManager.HasComponent<TimetableSchedule>(line))
                return;
            TimetableSchedule sch = EntityManager.GetComponentData<TimetableSchedule>(line);
            if (sch.m_TerminusStop != stop)
            {
                sch.m_TerminusStop = stop;
                EntityManager.SetComponentData(line, sch);
            }
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        // Escape a user-typed line name for inclusion in the hand-built board JSON string.
        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        // RouteSchedule of a line (mirrors the game's ScheduleSection): 0=Day-only, 1=Night-only, 2=DayAndNight.
        private int ScheduleOf(Entity line) => LineSchedule.Of(EntityManager, line);

        private static string Hr(int h) => (h < 10 ? "0" : "") + h;
        private static string Range(int a, int b) => Hr(a) + "-" + Hr(b);

        // Recompute gate. THIS PHASE IGNORES GetUpdateInterval: only GameSimulation / EditorSimulation /
        // LoadSimulation call the interval-aware UpdateSystem.Update overload, so a UIUpdate system runs on EVERY
        // RENDERED FRAME no matter what interval it declares. Refresh() is not cheap — it walks the route waypoints,
        // rebuilds the board JSON and re-derives every panel value — and it was running ~60x a second.
        //
        // Nothing it produces changes faster than the in-game MINUTE except things the player just did, which set
        // m_UiDirty. So recompute on: a selection change, a minute boundary, or an explicit edit. That is roughly one
        // refresh per 182 frames instead of every frame, and the visible behaviour is identical because the bindings
        // already suppressed the unchanged writes — we were paying to compute values that were then thrown away.
        private int m_LastRefreshMinute = -1;
        private Entity m_LastRefreshSel = Entity.Null;
        private bool m_UiDirty = true;

        protected override void OnUpdate()
        {
            base.OnUpdate();
            Entity sel = m_ToolSystem != null ? m_ToolSystem.selected : Entity.Null;
            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;
            // Vehicle row: cheap, and must track the LIVE selection every frame, not only when the refresh gate opens —
            // a selected vehicle moves between stops without the minute changing.
            m_SelVehInfo = BuildVehInfo(sel);
            if (m_UiDirty || sel != m_LastRefreshSel || nowMin != m_LastRefreshMinute)
            {
                m_UiDirty = false;
                m_LastRefreshSel = sel;
                m_LastRefreshMinute = nowMin;
                Refresh();
            }
            m_SelHasB.Update();
            m_SelTtEnabledB.Update();
            m_SelPeakVehB.Update();
            m_SelOffPeakVehB.Update();
            m_SelNightVehB.Update();
            m_SelTtFleetB.Update();
            m_SelTtServingB.Update();
            m_SelTtHeadwayB.Update();
            m_SelTtNextMinB.Update();
            m_SelTtRealInfoB.Update();
            m_SelTtTerminusB.Update();
            m_SelTtLayoverB.Update();
            m_SelTtLayoverMinB.Update();
            m_SelTtRuleOrphansB.Update();
            m_SelVehInfoB.Update();
            m_SelCustomPeakEnabledB.Update();
            m_SelCustomPeakVehB.Update();
            m_SelCustomPeakStart1B.Update();
            m_SelCustomPeakEnd1B.Update();
            m_SelCustomPeakStart2B.Update();
            m_SelCustomPeakEnd2B.Update();
            m_SelScheduleB.Update();
            m_PeakHoursB.Update();
            m_NightHoursB.Update();
            m_SelStopHasB.Update();
            m_SelStopBoardB.Update();
            m_AutoOpenB.Update();
            m_SelStopLineNumB.Update();
            m_SelStopLineServesB.Update();
            m_NoticeSeqB.Update();
        }

        private void Refresh()
        {
            TransitTimetablesSetting s = S;
            Entity sel = m_ToolSystem != null ? m_ToolSystem.selected : Entity.Null;
            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;

            // Window hours (global) so the editor can show WHEN peak/night apply.
            if (s != null)
            {
                m_PeakHours = Range(s.MorningPeakStart, s.MorningPeakEnd) + ", " + Range(s.EveningPeakStart, s.EveningPeakEnd);
                m_NightHours = Range(s.NightStart, s.NightEnd);
            }

            bool isLine = s != null && sel != Entity.Null
                && EntityManager.HasComponent<TransportLine>(sel)
                && EntityManager.HasComponent<RouteWaypoint>(sel);
            m_SelHas = isLine;
            if (isLine)
            {
                m_LastLine = sel;                 // remember the open line for the stop board's per-line terminus button
                m_SelSchedule = ScheduleOf(sel);  // which window's count applies: 0=Day, 1=Night, 2=DayAndNight
            }
            if (isLine && EntityManager.HasComponent<TimetableSchedule>(sel))
            {
                TimetableSchedule sch = EntityManager.GetComponentData<TimetableSchedule>(sel);
                CustomPeakSchedule cps = EntityManager.HasComponent<CustomPeakSchedule>(sel)
                    ? EntityManager.GetComponentData<CustomPeakSchedule>(sel) : CustomPeakSchedule.Default();
                LineFleetPlan plan = m_Dispatch != null ? m_Dispatch.ResolvePlan(sel) : LineFleetPlan.Default();
                m_SelTtEnabled = sch.m_Enabled;
                m_SelPeakVeh = plan.m_PeakVehicles;
                m_SelOffPeakVeh = plan.m_OffPeakVehicles;
                m_SelNightVeh = plan.m_NightVehicles;
                m_SelCustomPeakVeh = plan.m_CustomPeakVehicles;
                m_SelCustomPeakEnabled = cps.m_Enabled;
                m_SelCustomPeakStart1 = cps.m_Start1; m_SelCustomPeakEnd1 = cps.m_End1;
                m_SelCustomPeakStart2 = cps.m_Start2; m_SelCustomPeakEnd2 = cps.m_End2;

                float dur = m_Fleet != null ? m_Fleet.LineStableDurationUnits(sel) : 0f;
                float um = m_Timebase.UnitMinutes;

                // How many are actually out there. The gap between this and the count the mod applied IS the answer to
                // "why is my interval not what I expected" during a peak ramp-up, so it gets its own row rather than
                // being inferred.
                m_SelTtServing = s.Enabled && m_Dispatch != null ? m_Dispatch.ServingVehicles(sel) : 0;
                // The count the mod SETTLED on — never a recomputation. When the mod is NOT setting this line's count
                // (master switch off, "another mod decides", or the line is out of service) there is no target to
                // report, so the headline falls back to what is actually running. Reporting 0 there was simply false:
                // the vehicles exist, the mod just isn't the one that put them here.
                if (s.Enabled && m_Dispatch != null && m_Dispatch.TryPostedFleet(sel, out int postedFleet) && postedFleet > 0)
                    m_SelTtFleet = postedFleet;
                else
                    m_SelTtFleet = m_SelTtServing;

                // The headway those vehicles are producing, and when the next one is due out of the terminus.
                m_SelTtHeadway = 0;
                m_SelTtNextMin = -1;
                if (s.Enabled && sch.m_Enabled && m_Dispatch != null
                    && m_Dispatch.TryLineHeadway(sel, out float hMin, out float nextIn))
                {
                    m_SelTtHeadway = (int)System.Math.Round(hMin);
                    m_SelTtNextMin = (int)System.Math.Round(nextIn);
                }

                // Master switch OFF => the dispatch has already handed this line back to vanilla, so claiming anything
                // about its loop or its provisioning would simply be false. Report nothing rather than something untrue.
                m_SelTtRealInfo = s.Enabled ? BuildRealInfo(sel, dur, um) : "";
                m_SelTtTerminus = TerminusState(sel, sch);
                m_SelTtLayover = LayoverState(sel, sch, out m_SelTtLayoverMin);
                m_SelTtRuleOrphans = StopRules.CountOrphans(EntityManager, sel);
            }
            else
            {
                m_SelTtEnabled = false;
                m_SelPeakVeh = 6; m_SelOffPeakVeh = 4; m_SelNightVeh = 2;
                m_SelTtFleet = 0; m_SelTtServing = 0; m_SelTtHeadway = 0; m_SelTtNextMin = -1;
                m_SelTtRealInfo = ""; m_SelTtTerminus = 0;
                m_SelTtLayover = 0; m_SelTtLayoverMin = 0; m_SelTtRuleOrphans = 0;
                m_SelCustomPeakEnabled = false; m_SelCustomPeakVeh = 8;
                m_SelCustomPeakStart1 = 7; m_SelCustomPeakEnd1 = 9; m_SelCustomPeakStart2 = 16; m_SelCustomPeakEnd2 = 18;
            }

            // Stop selection -> arrivals board. A roadside bus/tram stop IS the selected entity; a train / metro /
            // airport / harbor STATION is a building whose boarding points are platform sub-objects, so resolve the
            // selection to the stop(s): the one roadside stop, or ALL of a station's platforms.
            ResolveSelectedStops(s, sel);
            bool isStop = m_SelStops.Count > 0;
            m_SelStopHas = isStop;
            if (isStop)
            {
                // Master switch off => show no mod board (vanilla); clear the row map the buttons use.
                if (!s.Enabled) { m_SelStopBoard = "[]"; m_BoardRows.Clear(); }
                else m_SelStopBoard = BuildStopBoard(s, nowMin); // also (re)builds m_BoardRows in lockstep with the JSON
                // Per-line terminus context (for "Set as terminus for Line N"): is the open line managed AND on the
                // board (i.e. serves one of the resolved stops)? LineRowIndex reads the board built just above.
                bool lastOk = m_LastLine != Entity.Null && EntityManager.Exists(m_LastLine)
                    && EntityManager.HasComponent<TimetableSchedule>(m_LastLine)
                    && LineRowIndex(m_LastLine) >= 0;
                m_SelStopLineServes = lastOk;
                m_SelStopLineNum = lastOk && EntityManager.HasComponent<RouteNumber>(m_LastLine)
                    ? EntityManager.GetComponentData<RouteNumber>(m_LastLine).m_Number : 0;
            }
            else
            {
                m_SelStopBoard = "[]";
                m_BoardRows.Clear();
                m_SelStopLineServes = false;
                m_SelStopLineNum = 0;
            }

            // Auto-open the floating panel the first tick a stop (or a station resolving to one) becomes the selection.
            Entity primary = m_SelStops.Count > 0 ? m_SelStops[0] : Entity.Null;
            if (isStop && primary != m_LastSel)
                m_AutoOpen++;
            m_LastSel = primary;
        }

        // A stop the mod can act on: has both a boarding slot and a connected-routes buffer (a roadside stop, or a
        // station platform). Same test the board / terminus logic relies on.
        private bool IsStopEntity(Entity e)
            => e != Entity.Null
               && EntityManager.HasComponent<BoardingVehicle>(e)
               && EntityManager.HasBuffer<ConnectedRoute>(e);

        // Resolve a tool selection into m_SelStops — the stop(s) the mod acts on. A roadside bus/tram stop IS the stop
        // (one entry). A train / metro / airport / harbor STATION is a building whose boarding points are platform
        // sub-objects, so collect ALL of them (the same graph vanilla walks in BuildingUtils.GetNumberOfConnectedLines)
        // so every line at the station is listed. Cached by the raw selection so a station isn't re-walked every tick.
        private void ResolveSelectedStops(TransitTimetablesSetting s, Entity sel)
        {
            if (s == null || sel == Entity.Null)
            {
                m_SelStops.Clear();
                m_ResolveRawSel = Entity.Null;
                return;
            }
            if (sel == m_ResolveRawSel)
                return; // cached — m_SelStops already holds this selection's stops
            m_ResolveRawSel = sel;
            m_ScratchStops.Clear();
            if (IsStopEntity(sel))
                m_ScratchStops.Add(sel);
            else
                CollectAllStationStops(sel, 0);
            // KEEP THE LAST BOARD when the new selection is not a stop at all. Clicking the magnifier next to a line in
            // a stop's info panel selects the LINE, which used to resolve to zero stops, blank the board and close the
            // panel — so looking up the line you were reading arrivals for threw those arrivals away.
            //
            // Only a selection that DOES resolve to stops replaces the board. Anything else (a line, a building, empty
            // ground) leaves the previous stop showing, and the panel's own X still closes it. This does not revive
            // issue #3: that was an EMPTY "select a stop" hint lingering over an unrelated panel, and an empty result
            // no longer overwrites anything.
            if (m_ScratchStops.Count > 0)
            {
                m_SelStops.Clear();
                m_SelStops.AddRange(m_ScratchStops);
            }
        }

        // Depth-bounded descent of a building's sub-object graph, adding every platform stop to m_ScratchStops
        // (deduped). Recurses into every sub-object, matching vanilla's connected-line walk. The depth cap is pure
        // defense; real station nesting is 2-3 levels. Fills the SCRATCH list, not m_SelStops: the caller only promotes
        // it when the walk actually found something, so a non-stop selection cannot blank an open board.
        private void CollectAllStationStops(Entity root, int depth)
        {
            if (depth > 5)
                return;
            if (IsStopEntity(root) && !m_ScratchStops.Contains(root))
                m_ScratchStops.Add(root);
            if (!EntityManager.HasBuffer<Game.Objects.SubObject>(root))
                return;
            DynamicBuffer<Game.Objects.SubObject> subs = EntityManager.GetBuffer<Game.Objects.SubObject>(root, isReadOnly: true);
            for (int i = 0; i < subs.Length; i++)
                CollectAllStationStops(subs[i].m_SubObject, depth + 1);
        }

        // JSON: [{ "n": <lineNumber>, "tt": <bool>, "term": <bool>, "h": <headway min>, "d": "<HH:MM, ...>" }, ...]
        // term = this stop is the line's EFFECTIVE terminus (explicit m_TerminusStop, else the first-stop fallback that
        // the dispatch actually regulates at) — matches TerminusWaypoint below.
        private string BuildStopBoard(TransitTimetablesSetting s, int nowMin)
        {
            // One row per DISTINCT line across all resolved stops (a station's platforms); the first stop a line is
            // found on wins. m_BoardRows is kept in lockstep with the JSON (row i == m_BoardRows[i]) so each row's own
            // buttons target that line at the platform it uses here.
            m_BoardRows.Clear();
            var seenLines = new HashSet<Entity>();
            for (int si = 0; si < m_SelStops.Count; si++)
            {
                Entity stop = m_SelStops[si];
                foreach (Entity line in StopLines(stop))
                    if (seenLines.Add(line))
                        m_BoardRows.Add((line, stop));
            }
            // Float the currently-open line to the top of the list.
            if (m_LastLine != Entity.Null)
            {
                int oi = LineRowIndex(m_LastLine);
                if (oi > 0)
                {
                    var row = m_BoardRows[oi];
                    m_BoardRows.RemoveAt(oi);
                    m_BoardRows.Insert(0, row);
                }
            }
            var sb = new StringBuilder("[");
            for (int i = 0; i < m_BoardRows.Count; i++)
            {
                Entity line = m_BoardRows[i].line;
                Entity stop = m_BoardRows[i].stop;
                int number = EntityManager.HasComponent<RouteNumber>(line) ? EntityManager.GetComponentData<RouteNumber>(line).m_Number : line.Index;
                // #10: the line's editable NAME (set by renaming the line) — shown on the board instead of "Line <n>"
                // when present; falls back to the route number when the line has no custom name.
                string nm = (m_NameSystem != null && m_NameSystem.TryGetCustomName(line, out string cn) && !string.IsNullOrEmpty(cn)) ? cn : null;
                bool hasSched = EntityManager.HasComponent<TimetableSchedule>(line);
                TimetableSchedule sch = hasSched ? EntityManager.GetComponentData<TimetableSchedule>(line) : default;
                bool tt = hasSched && sch.m_Enabled;
                string dep = "";
                bool term = false;
                bool est = false;
                int headway = 0;
                int lay = 0;        // this row's stop is its line's active Terminus B: minimum layover in minutes
                string arr = "";    // ...and these are its pre-layover arrivals (departures = the ordinary dep list)
                bool layOff = false; // a Terminus B is SET on this stop but the dispatch dropped it (inactive)
                int rule = 0;       // per-stop boarding rule for THIS line at THIS stop (LineStopRule mode)
                if (tt)
                {
                    Entity terminusWp = TerminusWaypoint(line, sch);
                    // The stop this line EFFECTIVELY terminates at (explicit m_TerminusStop, else the first-boarding
                    // waypoint) — where the dispatch actually regulates and retires vehicles.
                    Entity termStop = terminusWp != Entity.Null && EntityManager.HasComponent<Connected>(terminusWp)
                        ? EntityManager.GetComponentData<Connected>(terminusWp).m_Connected : Entity.Null;
                    // If the line already terminates at ANOTHER platform of the SAME selected station, re-anchor this
                    // row to THAT platform. A two-direction rail/metro line uses two platforms, and sub-object order
                    // may attach the row to the non-terminus one — which would drop the star and offer a "Set as
                    // terminus" button that silently MOVES an already-correct anchor. Keeps row i == m_BoardRows[i].
                    if (termStop != Entity.Null && termStop != stop && m_SelStops.Contains(termStop))
                    {
                        stop = termStop;
                        m_BoardRows[i] = (line, stop);
                    }
                    Entity stopWp = WaypointForStop(line, stop);
                    term = termStop != Entity.Null && termStop == stop;
                    // AFTER the re-anchor above, never before: on a multi-platform station the row can still move to
                    // another platform here, and the rule belongs to whichever stop the row ends up representing.
                    rule = StopRules.ModeForStop(EntityManager, line, stop);
                    ArrivalsAtStop(line, stopWp, terminusWp, nowMin, out headway, out est, out dep, out arr);
                    // Terminus B row: only when THIS stop is the line's ACTIVE one — TryActiveLayover applies the
                    // dispatch's own validity rules, so the board can never advertise a Terminus B the dispatch has
                    // dropped (deleted stop, edited route, or the terminus fallback landing on it). `arr` is already
                    // filled by the walk above wherever the dispatch published a separate arrival offset.
                    if (m_Dispatch != null && m_Dispatch.TryActiveLayover(line, out Entity layStop, out int layMin) && layStop == stop)
                    {
                        lay = layMin;
                    }
                    // A Terminus B SET on this stop but dropped by the dispatch (the effective terminus moved onto it)
                    // would otherwise be invisible AND unremovable — the component silently persists and reactivates
                    // whenever the terminus moves again. Show it dimmed, stepper and remove still live.
                    else if (EntityManager.HasComponent<LineLayover>(line))
                    {
                        LineLayover ll = EntityManager.GetComponentData<LineLayover>(line);
                        if (ll.m_Stop == stop && ll.m_HoldMinutes > 0) { lay = ll.m_HoldMinutes; layOff = true; }
                    }
                }
                if (i > 0) sb.Append(',');
                sb.Append("{\"n\":").Append(number);
                if (nm != null) sb.Append(",\"nm\":\"").Append(JsonEscape(nm)).Append('"');
                sb.Append(",\"tt\":").Append(tt ? "true" : "false")
                  .Append(",\"term\":").Append(term ? "true" : "false")
                  .Append(",\"h\":").Append(headway)
                  .Append(",\"est\":").Append(est ? "true" : "false");    // times derived from the game's estimate, not measured
                if (lay > 0)
                {
                    sb.Append(",\"lay\":").Append(lay)
                      .Append(",\"a\":\"").Append(arr).Append('"');
                    if (layOff) sb.Append(",\"layOff\":true");
                }
                if (rule > 0) sb.Append(",\"rule\":").Append(rule);
                sb.Append(",\"d\":\"").Append(dep).Append("\"}");
            }
            sb.Append(']');
            return sb.ToString();
        }

        // Index of a line's row in m_BoardRows (rebuilt by BuildStopBoard each tick), or -1.
        private int LineRowIndex(Entity line)
        {
            for (int i = 0; i < m_BoardRows.Count; i++)
                if (m_BoardRows[i].line == line) return i;
            return -1;
        }

        // Distinct lines serving a stop (via each connected waypoint's Owner).
        private IEnumerable<Entity> StopLines(Entity stop)
        {
            var seen = new HashSet<Entity>();
            if (!EntityManager.HasBuffer<ConnectedRoute>(stop))
                yield break;
            DynamicBuffer<ConnectedRoute> routes = EntityManager.GetBuffer<ConnectedRoute>(stop, isReadOnly: true);
            for (int i = 0; i < routes.Length; i++)
            {
                Entity wp = routes[i].m_Waypoint;
                if (!EntityManager.HasComponent<Owner>(wp))
                    continue;
                Entity line = EntityManager.GetComponentData<Owner>(wp).m_Owner;
                if (line != Entity.Null && EntityManager.HasComponent<TransportLine>(line) && seen.Add(line))
                    yield return line;
            }
        }

        // The waypoint on a line connected to a given stop (Entity.Null if none).
        private Entity WaypointForStop(Entity line, Entity stop)
        {
            if (!EntityManager.HasBuffer<RouteWaypoint>(line))
                return Entity.Null;
            DynamicBuffer<RouteWaypoint> wps = EntityManager.GetBuffer<RouteWaypoint>(line, isReadOnly: true);
            for (int i = 0; i < wps.Length; i++)
            {
                Entity wp = wps[i].m_Waypoint;
                if (EntityManager.HasComponent<Connected>(wp) && EntityManager.GetComponentData<Connected>(wp).m_Connected == stop)
                    return wp;
            }
            return Entity.Null;
        }

        // ===================== THE ARRIVALS BOARD =====================
        // A headway-regulated line has no printed timetable, so this is a PROJECTION and is labelled as one: the next
        // vehicle leaves the terminus in `nextIn` minutes, they leave every `headway` minutes after that, and this stop
        // is `offset` minutes down the route from the terminus. Everything in that sentence is a number the DISPATCH
        // produced — none of it is re-derived here.
        //
        // The projection is honest about what it can and cannot know. It assumes the line stays evenly spaced, which is
        // the thing the mod is actively enforcing, so it is right whenever the mod is working and visibly wrong when it
        // is not — which is the correct failure mode for a diagnostic the player is reading to check exactly that.
        // ONE SLOT SET, TWO OFFSETS. `departures` is what this stop shows normally; `arrivals` is filled only at
        // Terminus B, the one stop where a vehicle's arrival and its departure differ (it stands there for the layover).
        //
        // Both are walked from the SAME projection, and that is not a tidiness point — it is the fix for issue #9.
        // Projecting them separately seeds each list at its own offset, so the departure list (offset larger by the
        // layover) reaches one layover further back and can pick up a slot the arrival list has already passed. Row k
        // is then a DIFFERENT vehicle in each row, and while the layover is shorter than the headway the departure row
        // prints times EARLIER than the arrival row — self-correcting on the next slot boundary, which is exactly the
        // "corrects itself after 10-60 seconds" that was reported. Here a departure is literally its own arrival plus
        // the layover, by construction.
        private void ArrivalsAtStop(Entity line, Entity stopWp, Entity terminusWp, int nowMin,
                                    out int headwayMin, out bool estimated, out string departures, out string arrivals)
        {
            headwayMin = 0;
            estimated = false;
            departures = "";
            arrivals = "";
            if (m_Dispatch == null || stopWp == Entity.Null || terminusWp == Entity.Null)
                return;
            if (!m_Dispatch.TryLineHeadway(line, out float h, out float nextIn) || h <= 0.01f)
                return;                                           // not being regulated yet — say nothing, invent nothing
            headwayMin = (int)System.Math.Round(h);
            // We know how OFTEN it comes but not yet WHEN: no vehicle has been seen leaving the terminus (the line was
            // just switched on, or its first vehicle is still driving out of the depot). Publish the headway and stop —
            // projecting from a departure that never happened would print a confident list of fictional times.
            if (nextIn < 0f)
                return;

            int offset;
            if (stopWp == terminusWp)
                offset = 0;                                       // the terminus itself: exact by definition
            else if (m_Dispatch.TryPostedOffsetMinutes(stopWp, out int postedOff))
                offset = postedOff;
            else
            {
                // Pre-publish fallback: the dispatch has not walked this line yet (first tick after a load). Estimate
                // from the route, and say so.
                offset = (int)System.Math.Round(TravelUnitsBetween(line, terminusWp, stopWp) * m_Timebase.UnitMinutes)
                       + LayoverMinutesUpTo(line, terminusWp, stopWp);
                estimated = true;
            }
            // A posted offset is not automatically a MEASURED one: until the line has timed enough real laps the ladder
            // is inert and the dispatch publishes the game's raw estimate — a usable number, but still a guess that
            // will move once laps land. Flag it, or the board claims a precision it does not have.
            if (!m_Dispatch.LineCorrectionMeasured(line))
                estimated = true;
            // The pre-layover arrival offset, published by the same dispatch walk that produced `offset`. Present only
            // at Terminus B; everywhere else arrival and departure are the same moment and there is one row.
            bool haveArrival = m_Dispatch.TryPostedArrivalMinutes(stopWp, out int arrivalOffset);

            var dep = new StringBuilder();
            var arr = haveArrival ? new StringBuilder() : null;
            for (int k = 0; k < 6; k++)
            {
                double slot = nextIn + k * h;                     // one slot set...
                if (k > 0) { dep.Append(", "); arr?.Append(", "); }
                dep.Append(ScheduleMath.FormatHm(nowMin + (int)System.Math.Round(slot + offset)));
                arr?.Append(ScheduleMath.FormatHm(nowMin + (int)System.Math.Round(slot + arrivalOffset)));
            }
            departures = dep.ToString();
            arrivals = arr != null ? arr.ToString() : "";
        }

        // Is this line's Terminus B being APPLIED, and if not, why not? The stop board can only speak for a stop it
        // lists, so these two failure states need a home on the LINE panel:
        //   0  none set
        //   1  set and active
        //   2  set, but the stop is now the effective terminus — the dispatch drops it (terminus wins) and the board
        //      shows it dimmed, but only if the player happens to open that stop
        //   3  set, but the stop is destroyed or no longer on this route. The board CANNOT show it at all (the stop no
        //      longer lists the line), so without this the component is invisible and unremovable, and silently
        //      reactivates if the route is ever edited back. This state is why clearSelLayover exists.
        private int LayoverState(Entity line, TimetableSchedule sch, out int minutes)
        {
            minutes = 0;
            if (!EntityManager.HasComponent<LineLayover>(line))
                return 0;
            LineLayover lay = EntityManager.GetComponentData<LineLayover>(line);
            if (lay.m_HoldMinutes == 0 || lay.m_Stop == Entity.Null)
                return 0;
            minutes = lay.m_HoldMinutes;
            if (!EntityManager.Exists(lay.m_Stop) || !EntityManager.HasComponent<BoardingVehicle>(lay.m_Stop)
                || WaypointForStop(line, lay.m_Stop) == Entity.Null)
                return 3;
            Entity termWp = TerminusWaypoint(line, sch);
            Entity termStop = termWp != Entity.Null && EntityManager.HasComponent<Connected>(termWp)
                ? EntityManager.GetComponentData<Connected>(termWp).m_Connected : Entity.Null;
            return termStop == lay.m_Stop ? 2 : 1;
        }

        // Does this line have a terminus the player actually chose, and can the dispatch still use it?
        //   0  a chosen stop, still on the route, still boardable — nothing to say
        //   1  never chosen. FindTerminus falls back to the first stop with a boarding slot, so the line WORKS; what
        //      the player is missing is that they never picked where their vehicles turn round and wait.
        //   2  chosen once, but the stop is gone or no longer on this route. Same silent fallback as (1), except here
        //      the player DID make a choice and it was quietly discarded — worth saying differently.
        //
        // Mirrors FindTerminus in TimetableDispatchSystem, NOT TerminusWaypoint below: the dispatch is what actually
        // drives the regulation, and it additionally requires Exists + BoardingVehicle. A warning that disagreed with
        // the behaviour it describes would be worse than no warning at all.
        private int TerminusState(Entity line, TimetableSchedule sch)
        {
            if (sch.m_TerminusStop == Entity.Null)
                return 1;
            if (!EntityManager.Exists(sch.m_TerminusStop)
                || !EntityManager.HasComponent<BoardingVehicle>(sch.m_TerminusStop)
                || WaypointForStop(line, sch.m_TerminusStop) == Entity.Null)
                return 2;
            return 0;
        }

        // The line's terminus waypoint: chosen stop's waypoint, else the first stop's waypoint.
        private Entity TerminusWaypoint(Entity line, TimetableSchedule sch)
        {
            if (sch.m_TerminusStop != Entity.Null)
            {
                Entity wp = WaypointForStop(line, sch.m_TerminusStop);
                if (wp != Entity.Null) return wp;
            }
            if (!EntityManager.HasBuffer<RouteWaypoint>(line))
                return Entity.Null;
            DynamicBuffer<RouteWaypoint> wps = EntityManager.GetBuffer<RouteWaypoint>(line, isReadOnly: true);
            for (int i = 0; i < wps.Length; i++)
            {
                Entity wp = wps[i].m_Waypoint;
                if (EntityManager.HasComponent<Connected>(wp)
                    && EntityManager.HasComponent<BoardingVehicle>(EntityManager.GetComponentData<Connected>(wp).m_Connected))
                    return wp;
            }
            return Entity.Null;
        }

        // Selected VEHICLE: how it is doing against the spacing. Empty string when the selection is not a public
        // transport vehicle on a managed line, so the row renders nothing at all.
        //
        // There is no "late" any more, and that is the honest position rather than a missing feature: nothing was
        // promised for a particular minute, so nothing can miss it. What CAN be said is whether this vehicle is
        // correctly spaced from the one in front, and — if it is standing still — whether that is the regulation
        // holding it (normal, and the whole point of a timing point) or just boarding.
        private string BuildVehInfo(Entity sel)
        {
            if (m_Dispatch == null || sel == Entity.Null) return "";
            if (!EntityManager.HasComponent<Game.Vehicles.PublicTransport>(sel)) return "";
            if (!m_Dispatch.TryVehicleStatus(sel, out TimetableDispatchSystem.VehicleStatus st)) return "";
            var sb = new StringBuilder();
            sb.Append("{\"held\":").Append(st.m_Held ? "true" : "false")
              .Append(",\"hold\":").Append(st.m_HoldMinutes)
              .Append(",\"term\":").Append(st.m_AtTerminus ? "true" : "false")
              .Append(",\"haveGap\":").Append(st.m_HaveGap ? "true" : "false")
              .Append(",\"gap\":").Append(st.m_GapMinutes)
              .Append(",\"h\":").Append(st.m_HeadwayMinutes)
              .Append(",\"stage\":").Append(st.m_Stage).Append('}');
            return sb.ToString();
        }

        // The "real loop" line for the selected line: how far its measured (or density-estimated) real loop is from the
        // game's own estimate, and what the mod is doing about the count. This matters more than it used to: the
        // headway a player gets for N vehicles is loop/N, so the loop figure is the whole explanation of why ten
        // vehicles buy the interval they buy. Empty when there is nothing to measure.
        private string BuildRealInfo(Entity line, float dur, float um)
        {
            if (m_Dispatch == null || dur <= 1f) return "";
            float corr = m_Dispatch.LineCorrection(line, dur, false);
            int estMin  = (int)System.Math.Round(dur * um);
            int realMin = (int)System.Math.Round(dur * um * corr);
            bool measured = m_Dispatch.LineCorrectionMeasured(line);
            // EMIT DATA, NOT A SENTENCE. This used to build the English text here with a StringBuilder, which meant all
            // 11 translated languages saw English — it was the last piece of user-facing text not going through the
            // localization pipeline. It cannot be fixed by translating the fragments and gluing them back together in
            // English order, because clause order differs between languages. So the numbers go to the UI and the whole
            // sentence is assembled there from a per-language template.
            //
            // "mode" picks which second sentence the UI renders:
            //   notmine   — the mod is not setting this line's count
            //   prov      — the mod is sizing it; n = the count it actually applied
            //   settling  — the mod will size it, but the duration estimate has not held steady yet
            TransitTimetablesSetting s = S;
            string mode;
            int n;
            if (s == null || !s.ModSizesFleet)
            {
                // Deliberately does NOT claim another mod owns the count — the player may have no fleet mod at all and
                // simply be back on vanilla's automatic sizing, or on whatever the Assigned Vehicles slider says.
                mode = "notmine";
                n = 0;
            }
            else if (m_Dispatch.TryPostedFleet(line, out int postedFleet) && postedFleet > 0)
            {
                mode = "prov";
                n = postedFleet;
            }
            else
            {
                mode = "settling";
                n = 0;
            }
            var sb = new StringBuilder();
            sb.Append("{\"real\":").Append(realMin)
              .Append(",\"est\":").Append(estMin)
              .Append(",\"corr\":\"").Append(corr.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append('"')
              .Append(",\"meas\":").Append(measured ? "true" : "false")
              .Append(",\"mode\":\"").Append(mode).Append('"')
              // Lap-timing progress, so a line that is still learning says so instead of silently showing a number
              // derived from the cold-start estimate. Only meaningful while meas is false; the UI ignores it after.
              .Append(",\"laps\":").Append(m_Dispatch.LineLoopSampleCount(line))
              .Append(",\"need\":").Append(TimetableDispatchSystem.MinTrustSamples)
              // Which estimator is driving the loop, so the panel can say so rather than implying the number is
              // measured when it is still the game's plain estimate.
              .Append(",\"stage\":").Append(m_Dispatch.LineCorrectionStage(line))
              .Append(",\"need2\":").Append(TimetableDispatchSystem.MedianMinSamples)
              .Append(",\"wlaps\":").Append(m_Dispatch.LineLoopWindowCount(line))
              .Append(",\"n\":").Append(n).Append('}');
            return sb.ToString();
        }

        // Minimum-layover minutes already incurred by the time a vehicle DEPARTS `toWp`, walking the route from
        // `fromWp` (the terminus). Terminus B's own minimum counts AT that stop — its departure is its arrival plus the
        // layover — and at every stop after it, which is precisely the dispatch's layoverCarry rule. Only the estimate
        // fallback in ArrivalsAtStop uses this: once the dispatch has published an offset that number already contains
        // it, and nothing may recompute it.
        private int LayoverMinutesUpTo(Entity line, Entity fromWp, Entity toWp)
        {
            if (m_Dispatch == null || !m_Dispatch.TryActiveLayover(line, out Entity layStop, out int layMin))
                return 0;
            Entity layWp = WaypointForStop(line, layStop);
            if (layWp == Entity.Null || layWp == fromWp || !EntityManager.HasBuffer<RouteWaypoint>(line))
                return 0;
            DynamicBuffer<RouteWaypoint> wps = EntityManager.GetBuffer<RouteWaypoint>(line, isReadOnly: true);
            int len = wps.Length;
            int start = 0;
            for (int i = 0; i < len; i++)
                if (wps[i].m_Waypoint == fromWp) { start = i; break; }
            for (int j = 0; j < len; j++)
            {
                int wi = start + j; if (wi >= len) wi -= len;
                Entity wp = wps[wi].m_Waypoint;
                // Terminus B tested FIRST so it owes its own layover (arrival + layover is its departure).
                if (wp == layWp) return layMin;
                if (wp == toWp) return 0;               // reached the target first: B is downstream of it
            }
            return 0;
        }

        // Travel time (route units) from waypoint `fromWp` forward around the loop to `toWp`, including dwell at
        // intermediate stops. 0 if `fromWp == toWp` (the terminus itself).
        private float TravelUnitsBetween(Entity line, Entity fromWp, Entity toWp)
        {
            if (fromWp == toWp)
                return 0f;
            if (!EntityManager.HasBuffer<RouteWaypoint>(line) || !EntityManager.HasBuffer<RouteSegment>(line) || !EntityManager.HasComponent<PrefabRef>(line))
                return 0f;
            DynamicBuffer<RouteWaypoint> wps = EntityManager.GetBuffer<RouteWaypoint>(line, isReadOnly: true);
            DynamicBuffer<RouteSegment> segs = EntityManager.GetBuffer<RouteSegment>(line, isReadOnly: true);
            int len = wps.Length;
            if (len == 0 || segs.Length < len)
                return 0f;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(line).m_Prefab;
            float stopDur = EntityManager.HasComponent<TransportLineData>(prefab) ? EntityManager.GetComponentData<TransportLineData>(prefab).m_StopDuration : 1f;

            int fromPos = -1, toPos = -1;
            for (int k = 0; k < len; k++)
            {
                if (wps[k].m_Waypoint == fromWp) fromPos = k;
                if (wps[k].m_Waypoint == toWp) toPos = k;
            }
            if (fromPos < 0 || toPos < 0)
                return 0f;

            float total = 0f;
            int idx = fromPos, guard = 0;
            while (idx != toPos && guard <= len)
            {
                Entity seg = segs[idx].m_Segment;
                if (EntityManager.HasComponent<PathInformation>(seg))
                    total += EntityManager.GetComponentData<PathInformation>(seg).m_Duration;
                int nextPos = (idx + 1) % len;
                Entity nextWp = wps[nextPos].m_Waypoint;
                if (nextPos != toPos && EntityManager.HasComponent<VehicleTiming>(nextWp))
                    total += stopDur;
                idx = nextPos;
                guard++;
            }
            return total;
        }
    }
}
