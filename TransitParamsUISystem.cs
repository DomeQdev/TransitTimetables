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
    // Backs both timetable UIs, driven by the current tool selection:
    //   * A transport LINE is selected  -> the timetable editor (injected into the native line info panel).
    //   * A STOP is selected            -> the departure board (every line's next departures from this stop), shown
    //                                      in the floating panel, which auto-opens; plus "set as terminus".
    public partial class TransitParamsUISystem : UISystemBase
    {
        private const string Group = "TransitParams";

        private TimeSystem m_TimeSystem;
        private HourlyFleetSystem m_Fleet;
        private TimebaseSystem m_Timebase;
        private TimetableDispatchSystem m_Dispatch;   // for the shared real-travel-time LineCorrection (board == holds)
        private ToolSystem m_ToolSystem;
        private NameSystem m_NameSystem;              // #10: the line's editable/custom name for the departure board

        // Selected LINE timetable cache.
        private bool m_SelHas;
        private bool m_SelTtEnabled;
        private int m_SelTtFirst, m_SelTtPeak, m_SelTtOffPeak, m_SelTtNight, m_SelTtInterval, m_SelTtFleet;
        private string m_SelTtNext = "";
        private string m_SelVehInfo = "";           // selected VEHICLE: late/early + next stop time (community request)
        private string m_SelTtRealInfo = "";        // honest real-travel-time line (real loop vs estimate + fleet consequence)
        private int m_SelTtTerminus;                // 0 chosen+usable, 1 never chosen, 2 chosen but no longer usable
        private int m_SelTtLayover;                 // 0 none, 1 active, 2 blocked (it IS the terminus), 3 orphaned (off-route)
        private int m_SelTtLayoverMin;              // the configured X, so the panel can name it while warning
        // Stop rules set on stops this line no longer serves. The stop board can only offer controls on a row it
        // lists, and an orphaned rule's stop produces no row — so without this the setting would be invisible AND
        // unremovable, and would spring back to life if the route were ever edited back. Same reasoning, same shape
        // as the layover's state 3.
        private int m_SelTtRuleOrphans;
        private int m_SelSchedule = 2;              // RouteSchedule: 0=Day, 1=Night, 2=DayAndNight (which intervals apply)
        private string m_PeakHours = "", m_NightHours = "";
        private GetterValueBinding<bool> m_SelHasB, m_SelTtEnabledB;
        private GetterValueBinding<int> m_SelTtFirstB, m_SelTtPeakB, m_SelTtOffPeakB, m_SelTtNightB, m_SelTtIntervalB, m_SelTtFleetB, m_SelScheduleB;
        private GetterValueBinding<string> m_SelTtNextB, m_PeakHoursB, m_NightHoursB, m_SelTtRealInfoB, m_SelVehInfoB;
        private GetterValueBinding<int> m_SelTtTerminusB;
        private GetterValueBinding<int> m_SelTtLayoverB, m_SelTtLayoverMinB;
        private GetterValueBinding<int> m_SelTtRuleOrphansB;
        // Per-line custom peak (PR #5): enabled + interval + two hour windows.
        private bool m_SelCustomPeakEnabled;
        private int m_SelCustomPeakInterval = 5, m_SelCustomPeakStart1 = 7, m_SelCustomPeakEnd1 = 9, m_SelCustomPeakStart2 = 16, m_SelCustomPeakEnd2 = 18;
        private GetterValueBinding<bool> m_SelCustomPeakEnabledB;
        private GetterValueBinding<int> m_SelCustomPeakIntervalB, m_SelCustomPeakStart1B, m_SelCustomPeakEnd1B, m_SelCustomPeakStart2B, m_SelCustomPeakEnd2B;

        // Selected STOP departure board.
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
        // Per board ROW, the (line, stop) it represents — so each row's OWN "Set as terminus" button targets exactly that
        // line at exactly the platform it uses here. Built in lockstep with the board JSON (row i == m_BoardRows[i]).
        private readonly List<(Entity line, Entity stop)> m_BoardRows = new List<(Entity, Entity)>();
        // Bumped once when the dispatch decides this city needs the one-time migration notice; the React side raises
        // the dialog on the change. static so the dispatch (a different system) can request it without a lookup.
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
        private bool m_SelStopLineServes;          // does m_LastLine serve the selected stop AND carry a timetable?
        private GetterValueBinding<int> m_SelStopLineNumB;
        private GetterValueBinding<bool> m_SelStopLineServesB;

        private static TransitTimetablesSetting S => Mod.ActiveSetting;

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
            m_SelTtFirstB = new GetterValueBinding<int>(Group, "selTtFirst", () => m_SelTtFirst);
            m_SelTtPeakB = new GetterValueBinding<int>(Group, "selTtPeak", () => m_SelTtPeak);
            m_SelTtOffPeakB = new GetterValueBinding<int>(Group, "selTtOffPeak", () => m_SelTtOffPeak);
            m_SelTtNightB = new GetterValueBinding<int>(Group, "selTtNight", () => m_SelTtNight);
            m_SelTtIntervalB = new GetterValueBinding<int>(Group, "selTtInterval", () => m_SelTtInterval);
            m_SelTtFleetB = new GetterValueBinding<int>(Group, "selTtFleet", () => m_SelTtFleet);
            m_SelTtNextB = new GetterValueBinding<string>(Group, "selTtNext", () => m_SelTtNext ?? "");
            m_SelTtRealInfoB = new GetterValueBinding<string>(Group, "selTtRealInfo", () => m_SelTtRealInfo ?? "");
            m_SelTtTerminusB = new GetterValueBinding<int>(Group, "selTtTerminus", () => m_SelTtTerminus);
            m_SelTtLayoverB = new GetterValueBinding<int>(Group, "selTtLayover", () => m_SelTtLayover);
            m_SelTtLayoverMinB = new GetterValueBinding<int>(Group, "selTtLayoverMin", () => m_SelTtLayoverMin);
            m_SelTtRuleOrphansB = new GetterValueBinding<int>(Group, "selTtRuleOrphans", () => m_SelTtRuleOrphans);
            m_SelVehInfoB = new GetterValueBinding<string>(Group, "selVehInfo", () => m_SelVehInfo ?? "");
            m_SelCustomPeakEnabledB = new GetterValueBinding<bool>(Group, "selCustomPeakEnabled", () => m_SelCustomPeakEnabled);
            m_SelCustomPeakIntervalB = new GetterValueBinding<int>(Group, "selCustomPeakInterval", () => m_SelCustomPeakInterval);
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
            AddBinding(m_SelTtFirstB);
            AddBinding(m_SelTtPeakB);
            AddBinding(m_SelTtOffPeakB);
            AddBinding(m_SelTtNightB);
            AddBinding(m_SelTtIntervalB);
            AddBinding(m_SelTtFleetB);
            AddBinding(m_SelTtNextB);
            AddBinding(m_SelTtRealInfoB);
            AddBinding(m_SelTtTerminusB);
            AddBinding(m_SelTtLayoverB);
            AddBinding(m_SelTtLayoverMinB);
            AddBinding(m_SelTtRuleOrphansB);
            AddBinding(m_SelVehInfoB);
            AddBinding(m_SelCustomPeakEnabledB);
            AddBinding(m_SelCustomPeakIntervalB);
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
            // One-time migration notice: a COUNTER, not a bool. A React effect that watches a counter cannot miss the
            // signal even if it mounts after the bump — useValue hands it the current value on mount — whereas a
            // fire-and-forget event raised while the Game screen is still mounting is simply dropped.
            m_NoticeSeqB = new GetterValueBinding<int>(Group, "noticeSeq", () => m_NoticeSeq);
            AddBinding(m_NoticeSeqB);
            AddBinding(new TriggerBinding<bool>(Group, "noticeAnswer", TimetableDispatchSystem.AnswerMigrationNotice));

            AddBinding(new TriggerBinding<bool>(Group, "setSelTtEnabled", v => MutateSchedule(v, (ref TimetableSchedule sch, bool on) => sch.m_Enabled = on)));
            AddBinding(new TriggerBinding<int>(Group, "setSelTtFirst", v => MutateSchedule(v, (ref TimetableSchedule sch, int x) => sch.m_FirstDeparture = (ushort)Clamp(x, 0, 1439))));
            AddBinding(new TriggerBinding<int>(Group, "setSelTtPeak", v => MutateSchedule(v, (ref TimetableSchedule sch, int x) => sch.m_PeakInterval = (ushort)Clamp(x, 1, 240))));
            AddBinding(new TriggerBinding<int>(Group, "setSelTtOffPeak", v => MutateSchedule(v, (ref TimetableSchedule sch, int x) => sch.m_OffPeakInterval = (ushort)Clamp(x, 1, 240))));
            AddBinding(new TriggerBinding<int>(Group, "setSelTtNight", v => MutateSchedule(v, (ref TimetableSchedule sch, int x) => sch.m_NightInterval = (ushort)Clamp(x, 1, 240))));
            // Per-line custom peak (PR #5): enable + interval + two hour windows.
            AddBinding(new TriggerBinding<bool>(Group, "setSelCustomPeakEnabled", v => MutateCustomPeak(v, (ref CustomPeakSchedule c, bool on) => c.m_Enabled = on)));
            AddBinding(new TriggerBinding<int>(Group, "setSelCustomPeakInterval", v => MutateCustomPeak(v, (ref CustomPeakSchedule c, int x) => c.m_Interval = (ushort)Clamp(x, 1, 240))));
            AddBinding(new TriggerBinding<int>(Group, "setSelCustomPeakStart1", v => MutateCustomPeak(v, (ref CustomPeakSchedule c, int x) => c.m_Start1 = (ushort)Clamp(x, 0, 23))));
            AddBinding(new TriggerBinding<int>(Group, "setSelCustomPeakEnd1", v => MutateCustomPeak(v, (ref CustomPeakSchedule c, int x) => c.m_End1 = (ushort)Clamp(x, 0, 23))));
            AddBinding(new TriggerBinding<int>(Group, "setSelCustomPeakStart2", v => MutateCustomPeak(v, (ref CustomPeakSchedule c, int x) => c.m_Start2 = (ushort)Clamp(x, 0, 23))));
            AddBinding(new TriggerBinding<int>(Group, "setSelCustomPeakEnd2", v => MutateCustomPeak(v, (ref CustomPeakSchedule c, int x) => c.m_End2 = (ushort)Clamp(x, 0, 23))));
            // Terminus scopes: one board row (its own line at its own platform), the open line, or every line here.
            AddBinding(new TriggerBinding<int>(Group, "setTerminusRow", SetTerminusRow));
            AddBinding(new TriggerBinding(Group, "setSelTerminusAll", () => SetSelectedStopAsTerminus(Entity.Null)));
            AddBinding(new TriggerBinding(Group, "setSelTerminusLine", () => { if (m_LastLine != Entity.Null) SetSelectedStopAsTerminus(m_LastLine); }));
            // Layover ("Terminus B"): give one board row's stop a scheduled layover of N minutes for its line, sent as
            // the ABSOLUTE value (the stepper idiom every other numeric trigger uses); 0 clears it.
            AddBinding(new TriggerBinding<int, int>(Group, "setLayoverRow", SetLayoverRow));
            // Clear the OPEN line's layover from the line panel. The stop board can only offer removal on a row it
            // still lists, so a layover whose stop left the route would otherwise be unreachable — see LayoverState 3.
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

        private delegate void RefSchedAction<T>(ref TimetableSchedule sch, T value);

        // Read-modify-write the selected line's timetable, creating the component on first touch.
        private void MutateSchedule<T>(T value, RefSchedAction<T> action)
        {
            Entity sel = m_ToolSystem != null ? m_ToolSystem.selected : Entity.Null;
            if (sel == Entity.Null || !EntityManager.HasComponent<TransportLine>(sel))
                return;
            bool had = EntityManager.HasComponent<TimetableSchedule>(sel);
            TimetableSchedule sch = had ? EntityManager.GetComponentData<TimetableSchedule>(sel) : TimetableSchedule.Default();
            action(ref sch, value);
            if (!had)
                EntityManager.AddComponent<TimetableSchedule>(sel);
            EntityManager.SetComponentData(sel, sch);
            m_UiDirty = true;   // the player just edited: recompute now, don't wait for the minute to tick
        }

        private delegate void RefCustomPeakAction<T>(ref CustomPeakSchedule c, T value);

        // Read-modify-write the selected line's CUSTOM PEAK component (PR #5), creating it on first touch.
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
            m_UiDirty = true;   // ditto
        }

        // Make board rows their line's terminus. onlyLine == Entity.Null → every line on the board (each to the platform
        // it uses here); otherwise → just that one line. Works off m_BoardRows, so a station's multiple platforms are
        // each targeted correctly (each line → its own platform).
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

        // Give one board row's stop a scheduled layover ("Terminus B") of `minutes` for its line; 0 clears it.
        // Guarded to TIMETABLED lines only: CleanUninstall iterates the TimetableSchedule query, so a LineLayover on an
        // untimetabled line would be unreachable save residue (same reasoning as SetLineTerminus's guard) — and the
        // stop board does list untimetabled lines, so the guard is load-bearing, not belt-and-braces.
        private void SetLayoverRow(int i, int minutes)
        {
            if (i < 0 || i >= m_BoardRows.Count)
                return;
            Entity line = m_BoardRows[i].line;
            Entity stop = m_BoardRows[i].stop;
            if (line == Entity.Null || stop == Entity.Null || !EntityManager.HasComponent<TimetableSchedule>(line))
                return;
            // -1 is the "set this stop" sentinel the button sends: when the layover is being MOVED from another stop,
            // keep the line's configured minutes — clicking Set on a new stop must not silently reset a tuned 15 back
            // to the default (review-caught). A fresh set starts at 3. Resolved BEFORE the clamp: Clamp would turn the
            // sentinel into 0, which means "clear" — the exact opposite of the button's intent.
            if (minutes < 0)
            {
                minutes = EntityManager.HasComponent<LineLayover>(line)
                    ? EntityManager.GetComponentData<LineLayover>(line).m_HoldMinutes : 0;
                if (minutes <= 0) minutes = 3;
            }
            m_UiDirty = true;
            minutes = Clamp(minutes, 0, 60);
            if (minutes == 0)
            {
                // Cleared: REMOVE the component rather than storing zeros, so an unused layover leaves no trace in the
                // save and TryActiveLayover's "no component" path stays the single meaning of "no layover".
                if (EntityManager.HasComponent<LineLayover>(line))
                    EntityManager.RemoveComponent<LineLayover>(line);
                return;
            }
            // Never on the effective terminus. The button is hidden on rows already wearing the terminus star, but the
            // first-boarding-stop fallback can move the effective terminus under us; the dispatch would silently drop
            // such a layover (TryActiveLayover — terminus wins), so refusing here keeps the UI honest with behaviour.
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
        // Guarded to TIMETABLED lines only, exactly like SetLayoverRow: the board lists untimetabled lines too, and a
        // rule set on one would be save residue the player could not see. (The CleanUninstall sweep does cover them —
        // this guard is about not creating the situation in the first place.)
        //
        // The TERMINUS is allowed, unlike the layover. A layover on the terminus is meaningless (the terminus hold
        // already IS the wait), but a restricted terminus is a real operating pattern and nothing in the timetable
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

        // Point a timetabled line's terminus at a stop it serves. No-op if the line has no timetable or already points there.
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
        // rebuilds the departure board JSON and re-derives every panel value — and it was running ~60x a second.
        //
        // Nothing it produces changes faster than the in-game MINUTE (departure times, the active interval, the
        // window labels) except things the player just did, which set m_UiDirty. So recompute on: a selection
        // change, a minute boundary, or an explicit edit. That is roughly one refresh per 182 frames instead of
        // every frame, and the visible behaviour is identical because the bindings already suppressed the
        // unchanged writes — we were paying to compute values that were then thrown away.
        private int m_LastRefreshMinute = -1;
        private Entity m_LastRefreshSel = Entity.Null;
        private bool m_UiDirty = true;

        protected override void OnUpdate()
        {
            base.OnUpdate();
            Entity sel = m_ToolSystem != null ? m_ToolSystem.selected : Entity.Null;
            int nowMin = (int)(m_TimeSystem.normalizedTime * 1440f) % 1440;
            // Vehicle row: cheap, and must track the LIVE selection every frame, not only when the refresh gate
            // opens - a selected bus moves between stops without the minute changing.
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
            m_SelTtFirstB.Update();
            m_SelTtPeakB.Update();
            m_SelTtOffPeakB.Update();
            m_SelTtNightB.Update();
            m_SelTtIntervalB.Update();
            m_SelTtFleetB.Update();
            m_SelTtNextB.Update();
            m_SelTtRealInfoB.Update();
            m_SelTtTerminusB.Update();
            m_SelTtLayoverB.Update();
            m_SelTtLayoverMinB.Update();
            m_SelTtRuleOrphansB.Update();
            m_SelVehInfoB.Update();
            m_SelCustomPeakEnabledB.Update();
            m_SelCustomPeakIntervalB.Update();
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
                m_SelSchedule = ScheduleOf(sel);  // which intervals apply: 0=Day, 1=Night, 2=DayAndNight
            }
            if (isLine && EntityManager.HasComponent<TimetableSchedule>(sel))
            {
                TimetableSchedule sch = EntityManager.GetComponentData<TimetableSchedule>(sel);
                CustomPeakSchedule cps = EntityManager.HasComponent<CustomPeakSchedule>(sel)
                    ? EntityManager.GetComponentData<CustomPeakSchedule>(sel) : CustomPeakSchedule.Default(); // PR #5 per-line peak
                m_SelTtEnabled = sch.m_Enabled;
                m_SelTtFirst = sch.m_FirstDeparture;
                m_SelTtPeak = sch.m_PeakInterval;
                m_SelTtOffPeak = sch.m_OffPeakInterval;
                m_SelTtNight = sch.m_NightInterval;
                m_SelCustomPeakEnabled = cps.m_Enabled;
                m_SelCustomPeakInterval = cps.m_Interval;
                m_SelCustomPeakStart1 = cps.m_Start1; m_SelCustomPeakEnd1 = cps.m_End1;
                m_SelCustomPeakStart2 = cps.m_Start2; m_SelCustomPeakEnd2 = cps.m_End2;
                m_SelTtInterval = ScheduleMath.IntervalFor(s, sch, cps, nowMin, m_SelSchedule);
                float dur = m_Fleet != null ? m_Fleet.LineStableDurationUnits(sel) : 0f;
                float um = m_Timebase.UnitMinutes;
                // The panel's fleet count = EXACTLY the number the dispatch settled on. It used to re-derive it here
                // from the same raw inputs, which silently dropped the three things the dispatch applies afterwards:
                // the hard cap, the shrink hysteresis, and the post-load stability gate. The panel could therefore
                // advertise a count the dispatch was actively refusing to write. Same rule as the departure board:
                // one number, produced once, read here.
                if (!s.Enabled)
                    m_SelTtFleet = 0;   // handed back to vanilla; we are not setting a count, so do not show one
                else if (m_Dispatch != null && m_Dispatch.TryPostedFleet(sel, out int postedFleet))
                    m_SelTtFleet = postedFleet;
                else
                {
                    // Not sized by the mod (another mod owns the count, or the line has no usable estimate yet) — show
                    // what this headway WOULD need so the row is not blank, and let BuildRealInfo say who owns it.
                    float fleetUnits = (m_Dispatch != null && dur > 1f && m_Dispatch.LineCorrectionMeasured(sel))
                        ? dur * m_Dispatch.LineCorrection(sel, dur, true) : dur;
                    // Mirror the dispatch's layover term (same gate): a fallback figure that ignored the layover would
                    // advertise a count the dispatch is not applying — the exact divergence m_PostedFleet exists to kill.
                    if (s.ProvisionRealFleet && um > 0.01f && m_Dispatch != null
                        && m_Dispatch.TryActiveLayover(sel, out _, out int layAdd))
                        fleetUnits += layAdd / um;
                    m_SelTtFleet = dur > 1f ? ScheduleMath.DerivedFleet(fleetUnits, m_SelTtInterval, um) : 0;
                }
                // Master switch OFF => the dispatch has already handed this line back to vanilla (holds released, our
                // fleet modifier healed, measurement dropped). Claiming "Provisioning ~6 vehicles for it" then is
                // simply false, and the correction it quotes has degraded to the cold-start density prior anyway.
                // Same rule as the departure prediction below: report nothing rather than something untrue.
                m_SelTtRealInfo = s.Enabled ? BuildRealInfo(sel, dur, um) : "";
                m_SelTtTerminus = TerminusState(sel, sch);
                m_SelTtLayover = LayoverState(sel, sch, out m_SelTtLayoverMin);
                m_SelTtRuleOrphans = StopRules.CountOrphans(EntityManager, sel);
                Entity term = TerminusWaypoint(sel, sch);
                // No departure predictions while the master switch is off — buses run vanilla, so posting scheduled
                // times would mislead. The config above stays visible/editable; only the live prediction is suppressed.
                m_SelTtNext = s.Enabled ? DeparturesAtStop(sel, sch, term, term, m_SelSchedule, nowMin) : "";
            }
            else
            {
                m_SelTtEnabled = false;
                m_SelTtFirst = 300; m_SelTtPeak = 8; m_SelTtOffPeak = 12; m_SelTtNight = 30;
                m_SelTtInterval = 0; m_SelTtFleet = 0; m_SelTtNext = ""; m_SelTtRealInfo = ""; m_SelTtTerminus = 0;
                m_SelTtLayover = 0; m_SelTtLayoverMin = 0; m_SelTtRuleOrphans = 0;
                m_SelCustomPeakEnabled = false; m_SelCustomPeakInterval = 5;
                m_SelCustomPeakStart1 = 7; m_SelCustomPeakEnd1 = 9; m_SelCustomPeakStart2 = 16; m_SelCustomPeakEnd2 = 18;
            }

            // Stop selection -> departure board. A roadside bus/tram stop IS the selected entity; a train / metro /
            // airport / harbor STATION is a building whose boarding points are platform sub-objects, so resolve the
            // selection to the stop(s): the one roadside stop, or ALL of a station's platforms — so every line at the
            // station is listed, each on its own row.
            ResolveSelectedStops(s, sel);
            bool isStop = m_SelStops.Count > 0;
            m_SelStopHas = isStop;
            if (isStop)
            {
                // Master switch off => show no mod departure board (vanilla); clear the row map the terminus buttons use.
                if (!s.Enabled) { m_SelStopBoard = "[]"; m_BoardRows.Clear(); }
                else m_SelStopBoard = BuildStopBoard(s, nowMin); // also (re)builds m_BoardRows in lockstep with the JSON
                // Per-line terminus context (for "Set as terminus for Line N"): is the open line timetabled AND on the
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
        // station platform). Same test the departure board / terminus logic relies on.
        private bool IsStopEntity(Entity e)
            => e != Entity.Null
               && EntityManager.HasComponent<BoardingVehicle>(e)
               && EntityManager.HasBuffer<ConnectedRoute>(e);

        // Resolve a tool selection into m_SelStops — the stop(s) the mod acts on. A roadside bus/tram stop IS the stop
        // (one entry). A train / metro / airport / harbor STATION is a building whose boarding points are platform
        // sub-objects, so collect ALL of them (the same graph vanilla walks in BuildingUtils.GetNumberOfConnectedLines)
        // so every line at the station is listed. Cached by the raw selection so a station isn't re-walked every UI tick.
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
            // panel — so looking up the line you were reading departures for threw those departures away.
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

        // Depth-bounded descent of a building's sub-object graph, adding every platform stop to m_ScratchStops (deduped).
        // Recurses into every sub-object, matching vanilla's connected-line walk. The depth cap is pure defense; real
        // station nesting is 2-3 levels. Fills the SCRATCH list, not m_SelStops: the caller only promotes it when the
        // walk actually found something, so a non-stop selection cannot blank an open board.
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

        // JSON: [{ "n": <lineNumber>, "tt": <bool>, "term": <bool>, "d": "<HH:MM, HH:MM, ...>" }, ...]
        // term = this stop is the line's EFFECTIVE terminus (explicit m_TerminusStop, else the first-stop fallback
        // that the dispatch system actually holds/retires at) — matches TerminusWaypoint below.
        private string BuildStopBoard(TransitTimetablesSetting s, int nowMin)
        {
            // One row per DISTINCT line across all resolved stops (a station's platforms); the first stop a line is
            // found on wins. m_BoardRows is kept in lockstep with the JSON (row i == m_BoardRows[i]) so each row's own
            // "Set as terminus" button (setTerminusRow) targets that line at the platform it uses here.
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
                int lay = 0;        // this row's stop is its line's active layover ("Terminus B"): X minutes
                string arr = "";    // ...and these are its pre-layover arrivals (departures = the ordinary dep list)
                bool layOff = false; // a layover is SET on this stop but the dispatch dropped it (inactive)
                int rule = 0;       // per-stop boarding rule for THIS line at THIS stop (LineStopRule mode)
                if (tt)
                {
                    Entity terminusWp = TerminusWaypoint(line, sch);
                    // The stop this line EFFECTIVELY terminates at (explicit m_TerminusStop, else the first-boarding
                    // waypoint) — where the dispatch actually holds/retires vehicles.
                    Entity termStop = terminusWp != Entity.Null && EntityManager.HasComponent<Connected>(terminusWp)
                        ? EntityManager.GetComponentData<Connected>(terminusWp).m_Connected : Entity.Null;
                    // If the line already terminates at ANOTHER platform of the SAME selected station, re-anchor this
                    // row to THAT platform. A two-direction rail/metro line uses two platforms, and sub-object order may
                    // attach the row to the non-terminus one — which would drop the star and offer a "Set as terminus"
                    // button that silently MOVES an already-correct anchor. Re-anchoring lands the star right, hides that
                    // button, and shows departures from the real terminus. Keeps row i == m_BoardRows[i].
                    if (termStop != Entity.Null && termStop != stop && m_SelStops.Contains(termStop))
                    {
                        stop = termStop;
                        m_BoardRows[i] = (line, stop);
                    }
                    Entity stopWp = WaypointForStop(line, stop);
                    dep = DeparturesAtStop(line, sch, terminusWp, stopWp, ScheduleOf(line), nowMin, out est);
                    term = termStop != Entity.Null && termStop == stop;
                    // AFTER the re-anchor above, never before: on a multi-platform station the row can still move to
                    // another platform here, and the rule belongs to whichever stop the row ends up representing.
                    rule = StopRules.ModeForStop(EntityManager, line, stop);
                    // Layover row ("Terminus B"): only when THIS stop is the line's ACTIVE layover — TryActiveLayover
                    // applies the dispatch's own validity rules, so the board can never advertise a layover the
                    // dispatch has dropped (deleted stop, edited route, or the terminus fallback landing on it).
                    // The dep list above is already the DEPARTURES (the posted offset includes X); the arrivals come
                    // from the arrival offset the same walk published, through the same formatter.
                    if (m_Dispatch != null && m_Dispatch.TryActiveLayover(line, out Entity layStop, out int layMin) && layStop == stop)
                    {
                        lay = layMin;
                        // Both rows come from ONE slot set here, and the departure row REPLACES the one computed above:
                        // dep from DeparturesAtStop is seeded independently and can print earlier than arr. See
                        // LayoverTimes. Only when the dispatch has published an arrival — before that we leave the
                        // ordinary (already layover-aware) dep row alone rather than invent a pairing.
                        if (stopWp != Entity.Null && m_Dispatch.TryPostedArrivalMinutes(stopWp, out int arrOff))
                            LayoverTimes(line, sch, ScheduleOf(line), nowMin, arrOff, layMin, out arr, out dep);
                    }
                    // A layover SET on this stop but dropped by the dispatch (the effective terminus moved onto it)
                    // would otherwise be invisible AND unremovable — the component silently persists and reactivates
                    // whenever the terminus moves again. Review-caught. Show it dimmed, stepper and remove still live.
                    // (Residual gap, accepted: if the ROUTE is edited so the line no longer serves the stop at all,
                    // the stop's board no longer lists the line and the set layover is unreachable until the player
                    // re-adds the stop or sets a layover elsewhere, which overwrites it.)
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

        // Is this line's layover being APPLIED, and if not, why not? The stop board can only speak for a stop it
        // lists, so these two failure states need a home on the LINE panel:
        //   0  no layover set
        //   1  set and active
        //   2  set, but the stop is now the effective terminus — the dispatch drops it (terminus wins) and the board
        //      shows it dimmed, but only if the player happens to open that stop
        //   3  set, but the stop is destroyed or no longer on this route. The board CANNOT show it at all (the stop
        //      no longer lists the line), so without this the component is invisible and unremovable, and silently
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
        //   1  never chosen. FindTerminus falls back to the first stop with a boarding slot, so the line WORKS;
        //      what the player is missing is that they never picked where the clock is anchored.
        //   2  chosen once, but the stop is gone or no longer on this route. Same silent fallback as (1), except
        //      here the player DID make a choice and it was quietly discarded — worth saying differently.
        //
        // Mirrors FindTerminus in TimetableDispatchSystem, NOT TerminusWaypoint below: the dispatch is what
        // actually drives the holds, and it additionally requires Exists + BoardingVehicle. A warning that
        // disagreed with the behaviour it describes would be worse than no warning at all.
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

        // The honest "real travel time" line for the selected timetabled line: how far its measured (or density-estimated)
        // real loop is from the game's own estimate, and the fleet consequence. Shown REGARDLESS of the toggles so the
        // player can SEE the gap and decide whether to correct the clock / provision the fleet. Empty when the estimate is
        // already close or there is nothing to measure.
        // Selected VEHICLE: how far off its schedule it is, and when it is due at its next stop. Empty string when
        // the selection is not a public-transport vehicle on an enabled timetable, so the row renders nothing at all.
        // "onTt" false means the vehicle has not reached the terminus yet and has no slot - not late, just not on the
        // timetable. "stage" mirrors the line panel so a still-measuring line does not present a firm number.
        private string BuildVehInfo(Entity sel)
        {
            if (m_Dispatch == null || sel == Entity.Null) return "";
            if (!EntityManager.HasComponent<Game.Vehicles.PublicTransport>(sel)) return "";
            if (!EntityManager.HasComponent<CurrentRoute>(sel)) return "";
            Entity line = EntityManager.GetComponentData<CurrentRoute>(sel).m_Route;
            if (line == Entity.Null || !EntityManager.HasComponent<TimetableSchedule>(line)) return "";
            if (!EntityManager.GetComponentData<TimetableSchedule>(line).m_Enabled) return "";

            bool onTt = m_Dispatch.TryVehicleSchedule(sel, out int lateMin, out int nextMin, out int stage);
            // Is it STATIONARY at a stop right now? While Boarding, Target is the stop it is sitting at, not the next
            // one — see the drain's lapServed test ("a bus whose current target is NOT the terminus has left the
            // terminus"). So the same scheduled minute means "when it departs from here" rather than "when it arrives
            // there", and an EARLY vehicle at a stop is not drifting, it is being HELD by the mod to keep the
            // timetable. Reported as "2 min early" while visibly standing still, that reads as a fault; it is the
            // whole point of the mod. The panel needs the flag to say which of those it is.
            bool boarding = (EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(sel).m_State
                             & Game.Vehicles.PublicTransportFlags.Boarding) != 0;
            var sb = new StringBuilder();
            sb.Append("{\"onTt\":").Append(onTt ? "true" : "false")
              .Append(",\"brd\":").Append(boarding ? "true" : "false")
              .Append(",\"late\":").Append(lateMin)
              .Append(",\"next\":").Append(nextMin)
              .Append(",\"stage\":").Append(stage).Append('}');
            return sb.ToString();
        }

        private string BuildRealInfo(Entity line, float dur, float um)
        {
            if (m_Dispatch == null || dur <= 1f) return "";
            float corr = m_Dispatch.LineCorrection(line, dur, false);
            if (corr > 0.98f && corr < 1.03f) return ""; // estimate is close enough — nothing worth saying
            int interval = m_SelTtInterval < 1 ? 1 : m_SelTtInterval;
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
            //   notmine   — the mod is not setting this line's count; n = what the headway would need
            //   prov      — the mod is sizing it; n = the count it actually SETTLED on (after the cap, the shrink
            //               hysteresis and the stability gate). Re-deriving n here is what once made the panel print
            //               one number in the row above and a different one in this sentence, two rows apart.
            //   settling  — the mod will size it, but the duration estimate has not held steady yet
            TransitTimetablesSetting s = S;
            string mode;
            int n;
            if (s == null || !s.ModSizesFleet)
            {
                // Deliberately does NOT claim another mod owns the count. The migration notice's opt-out lands here
                // too, and that player may have no fleet mod at all — their counts are simply back on vanilla's
                // automatic sizing, or on whatever they set with the Assigned Vehicles slider.
                mode = "notmine";
                // Mirror the dispatch's own gate: with provisioning off, the count it WOULD apply is the plain
                // estimate, so quoting the measured-loop figure here would advertise a number the mod is not using.
                bool prov = s != null && s.ProvisionRealFleet && measured;
                float advUnits = dur * (prov ? m_Dispatch.LineCorrection(line, dur, true) : 1f);
                // The layover term, same gate as the dispatch (ProvisionRealFleet, NOT measured — a layover is a
                // player instruction, not a measurement), so this advisory matches what the mod would actually apply.
                if (s != null && s.ProvisionRealFleet && um > 0.01f
                    && m_Dispatch.TryActiveLayover(line, out _, out int layAdv))
                    advUnits += layAdv / um;
                n = ScheduleMath.DerivedFleet(advUnits, interval, um);
            }
            else if (m_Dispatch.TryPostedFleet(line, out int postedFleet))
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
              // Which estimator is actually driving the count, so the panel can say so rather than implying the
              // number is measured when it is still the game's plain estimate.
              .Append(",\"stage\":").Append(m_Dispatch.LineCorrectionStage(line))
              .Append(",\"need2\":").Append(TimetableDispatchSystem.MedianMinSamples)
              // Is the measured correction actually APPLIED? Both toggles default OFF since 2026-08-03, so a line can
              // carry a perfectly good measurement that nothing is using. The panel says so, rather than quoting a
              // real-loop figure the player would reasonably assume is in effect.
              .Append(",\"rtt\":").Append(s != null && s.RealisticTravelTime ? "true" : "false")
              .Append(",\"wlaps\":").Append(m_Dispatch.LineLoopWindowCount(line))
              .Append(",\"n\":").Append(n).Append('}');
            return sb.ToString();
        }

        // The NEXT departures (from now) as seen AT `stopWp`: each terminus departure shifted by travel terminus ->
        // stopWp. A terminus departure D appears here as D+offset, so we list terminus departures from now-offset.
        // Up to 6, "HH:MM, ...".
        private string DeparturesAtStop(Entity line, TimetableSchedule sch, Entity terminusWp, Entity stopWp, int schedule, int nowMin)
            => DeparturesAtStop(line, sch, terminusWp, stopWp, schedule, nowMin, out _);

        private string DeparturesAtStop(Entity line, TimetableSchedule sch, Entity terminusWp, Entity stopWp, int schedule, int nowMin,
                                        out bool estimated)
        {
            estimated = false;
            if (terminusWp == Entity.Null || stopWp == Entity.Null)
                return "";
            // Post EXACTLY the offset the dispatch used to hold the vehicles. Deriving our own here is what made the
            // printed board and the actual departures disagree — two calculations from the same inputs will always
            // drift apart eventually, and once the dispatch started correcting for measured travel they diverged by
            // up to ~45 minutes. There is now ONE number, produced by the dispatch and read here.
            // The estimate below is a genuine fallback only: a line the dispatch is not ticking (no timetable) or a
            // waypoint it has not walked yet. It cannot be corrected, because the correction lives in the dispatch.
            int offset;
            if (stopWp == terminusWp)
                offset = 0;                                          // the terminus itself: exact by definition, no estimate
            else if (m_Dispatch != null && m_Dispatch.TryPostedOffsetMinutes(stopWp, out int postedOff))
            {
                offset = postedOff;
                // A posted offset is not automatically a MEASURED one. Until the line has timed enough real laps the
                // ladder is inert and the dispatch posts the game's raw estimate — a correct number to hold to, but
                // still a guess, and it will move once laps land. Flag it, or the board claims a precision it does not
                // have. (Measurement is per LINE now, not per stop, so this is the whole line's state.)
                estimated = m_Dispatch != null && !m_Dispatch.LineCorrectionMeasured(line);
            }
            else
            {
                // Pre-publish fallback: the dispatch has not walked this line yet (first tick after a load, or a line
                // it is not ticking). The layover has to be added HERE too, or for that window the board prints
                // departures WITHOUT X at the layover stop and everywhere after it — times the vehicles will not keep.
                // This is a second implementation of the estimate, not of the layover RULE: which stops owe X is
                // decided once, by walking the route in LayoverMinutesUpTo, exactly as the dispatch's carry does.
                offset = (int)System.Math.Round(TravelUnitsBetween(line, terminusWp, stopWp) * m_Timebase.UnitMinutes)
                       + LayoverMinutesUpTo(line, terminusWp, stopWp);
                estimated = true;                                    // say so on the board rather than implying precision
            }
            // Clamp the seed so a stop whose first arrival is still ahead (offset > now, early morning) advertises the
            // real first bus rather than extrapolating yesterday's sequence backwards across midnight.
            return FormatTimesFromOffset(line, sch, schedule, nowMin, offset);
        }

        // Format up to 6 upcoming clock times at a given offset from the terminus grid ("HH:MM, ..."). Shared by the
        // departure list and the layover stop's ARRIVALS list, so both are the same pipeline over a dispatch-published
        // offset — the arrivals row must never become a second, independent derivation (that is how the board and the
        // buses once disagreed by ~45 minutes). The seed clamp keeps a stop whose first arrival is still ahead
        // (offset > now, early morning) advertising the real first vehicle rather than extrapolating yesterday's
        // sequence backwards across midnight.
        // BOTH rows for the layover stop, from ONE set of slots.
        //
        // Formatting them as two independent calls to FormatTimesFromOffset is wrong, and wrong in a way that only
        // shows up live (GameBurrow, issue #9): each call seeds itself at `now - itsOwnOffset`, so the departure list
        // — whose offset is X larger — reaches one X further back and can pick up a slot the arrival list has already
        // passed. Row k of the two lists is then a DIFFERENT vehicle, and while X is smaller than the headway the
        // departure row prints times EARLIER than the arrival row. It self-corrects the moment the clock crosses the
        // next slot boundary, which is exactly the "corrects itself after 10-60 seconds" that was reported.
        //
        // One slot set, two offsets: row k is the same vehicle in both rows by construction, and a departure can never
        // precede its own arrival because it is literally that arrival plus X.
        private void LayoverTimes(Entity line, TimetableSchedule sch, int schedule, int nowMin, int arrOff, int layMin,
                                  out string arrivals, out string departures)
        {
            arrivals = "";
            departures = "";
            int seed = nowMin - arrOff;
            if (seed < 0) seed = 0;
            int[] slots = new int[6];
            CustomPeakSchedule customSch = EntityManager.HasComponent<CustomPeakSchedule>(line)
                ? EntityManager.GetComponentData<CustomPeakSchedule>(line) : CustomPeakSchedule.Default();
            int n = ScheduleMath.Upcoming(S, sch, customSch, schedule, seed, slots, 6);
            var a = new StringBuilder();
            var d = new StringBuilder();
            for (int k = 0; k < n; k++)
            {
                if (k > 0) { a.Append(", "); d.Append(", "); }
                a.Append(ScheduleMath.FormatHm(slots[k] + arrOff));
                d.Append(ScheduleMath.FormatHm(slots[k] + arrOff + layMin));
            }
            arrivals = a.ToString();
            departures = d.ToString();
        }

        private string FormatTimesFromOffset(Entity line, TimetableSchedule sch, int schedule, int nowMin, int offset)
        {
            int seed = nowMin - offset;
            if (seed < 0) seed = 0;
            int[] deps = new int[6];
            CustomPeakSchedule customSch = EntityManager.HasComponent<CustomPeakSchedule>(line)
                ? EntityManager.GetComponentData<CustomPeakSchedule>(line) : CustomPeakSchedule.Default(); // PR #5 per-line peak
            int n = ScheduleMath.Upcoming(S, sch, customSch, schedule, seed, deps, 6);
            var sb = new StringBuilder();
            for (int k = 0; k < n; k++)
            {
                if (k > 0) sb.Append(", ");
                sb.Append(ScheduleMath.FormatHm(deps[k] + offset));
            }
            return sb.ToString();
        }

        // Layover minutes already incurred by the time a vehicle DEPARTS `toWp`, walking the route from `fromWp`
        // (the terminus). The layover stop's own X counts AT that stop — its departure is its arrival plus X — and at
        // every stop after it, which is precisely the dispatch's layoverCarry rule. 0 before the layover, and 0 when
        // the line has no active one. Only the estimate fallback in DeparturesAtStop uses this: once the dispatch has
        // published an offset that number already contains X, and nothing may recompute it.
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
                // Layover tested FIRST so the layover stop itself owes its own X (arrival + X is its departure).
                if (wp == layWp) return layMin;
                if (wp == toWp) return 0;               // reached the target first: the layover is downstream of it
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
