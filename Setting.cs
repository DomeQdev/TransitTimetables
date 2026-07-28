using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;

namespace TransitTimetables
{
    [FileLocation(nameof(TransitTimetables))]
    public class Setting : ModSetting
    {
        public const string Section = "Main";
        public const string GroupWindows = "Peak windows";
        public const string GroupRealism = "Realistic travel time";
        public const string GroupStops = "Stops";
        public const string GroupCompat = "Compatibility";
        public const string GroupGeneral = "General";

        public Setting(IMod mod) : base(mod) { }

        // NOTE: initializers double as the settings-migration failsafe (missing keys in an old .coc keep these
        // values instead of defaulting to 0/false).

        // ---- Master switch ----
        // Turn the whole mod on/off without uninstalling. OFF = exact vanilla: every line stops being managed (held
        // buses are released, and the mod-applied fleet count and unbunching factor are reverted), while your per-line
        // timetables are kept so re-enabling resumes them. Default ON.
        [SettingsUISection(Section, GroupGeneral)]
        public bool Enabled { get; set; } = true;

        // ---- Vehicle-count management (opt-out for fleet-mod compatibility) ----
        // ON (default): the mod sets each timetabled line's vehicle count (via its VehicleInterval modifier) to hold the
        // chosen headway. OFF: the mod NEVER touches vehicle counts — it hands any line it was sizing back to vanilla /
        // your other fleet mod ONCE and leaves it alone, so a dedicated fleet mod (e.g. All Transit + Truck) can own the
        // counts without the two fighting over the same modifier every tick. The departure HOLD still works either way;
        // with too few vehicles a line just runs wider than the set interval. (Also gates "Provision fleet".)
        [SettingsUISection(Section, GroupGeneral)]
        public bool ManageVehicleCount { get; set; } = true;

        // ---- Clean uninstall ----
        // One-shot button: wipe every trace of the mod from the CURRENT save — revert each line to vanilla (release held
        // buses, restore the unbunching factor, drop the mod-applied vehicle count) AND remove the mod's saved components
        // (the per-line timetables, custom peaks and measured travel). Press it, then SAVE your city, and the mod can be
        // removed with zero residue. This CLEARS your per-line timetables (unlike the master switch, which keeps them),
        // so it is confirmed before it runs. Harmless if pressed with the mod still installed — you just start fresh.
        [SettingsUIButton]
        [SettingsUIConfirmation]
        [SettingsUISection(Section, GroupGeneral)]
        public bool CleanUninstall { set { TimetableDispatchSystem.RequestCleanUninstall(); } }

        // Peak windows (hour of day, 0-23). A line's per-window timetable intervals switch by these: hours inside a
        // morning or evening window are "peak", night hours are "night", everything else is "off-peak". The night
        // window may wrap past midnight (start > end).
        //
        // The NIGHT window defaults to vanilla's own transport night, 22:00-06:00 (TransportLineSystem hardcodes
        // isNight = normalizedTime < 0.25f || normalizedTime >= 11f/12f). Matching it matters: for a day-only or
        // night-only line VANILLA decides whether the line runs at all — it forces the vehicle count to 0 outside its
        // own window — so any disagreement means this mod posts departures for buses vanilla has already retired, or
        // silently stops holding while the line is still running. Existing players keep whatever their .coc already
        // holds (the loader overwrites these initializers), so changing this default never moves anyone's setting.
        [SettingsUISlider(min = 0f, max = 23f, step = 1f, unit = "integer")]
        [SettingsUISection(Section, GroupWindows)]
        public int MorningPeakStart { get; set; } = 6;

        [SettingsUISlider(min = 0f, max = 23f, step = 1f, unit = "integer")]
        [SettingsUISection(Section, GroupWindows)]
        public int MorningPeakEnd { get; set; } = 9;

        [SettingsUISlider(min = 0f, max = 23f, step = 1f, unit = "integer")]
        [SettingsUISection(Section, GroupWindows)]
        public int EveningPeakStart { get; set; } = 15;

        [SettingsUISlider(min = 0f, max = 23f, step = 1f, unit = "integer")]
        [SettingsUISection(Section, GroupWindows)]
        public int EveningPeakEnd { get; set; } = 19;

        [SettingsUISlider(min = 0f, max = 23f, step = 1f, unit = "integer")]
        [SettingsUISection(Section, GroupWindows)]
        public int NightStart { get; set; } = 22;

        [SettingsUISlider(min = 0f, max = 23f, step = 1f, unit = "integer")]
        [SettingsUISection(Section, GroupWindows)]
        public int NightEnd { get; set; } = 6; // 06:00 = vanilla's transport day start (RouteUtils.TRANSPORT_DAY_START_TIME 0.25f)

        // Realistic travel time: CS2's own pathfinder estimate of how long a line takes systematically UNDERSHOOTS the
        // real, simulated loop time (live-measured ~1.7x on sparse lines to ~2.5x on stop-dense ones — acceleration and
        // braking at every stop that the free-flow estimate ignores). The mod measures each line's real loop live and
        // corrects for it. Both toggles default OFF so existing timetables are unchanged until the player opts in; the
        // correction is RT-invariant (frame-based), so it composes with the slow-time compatibility above.
        //
        // Master toggle — apply the correction to POSTED TIMES and stop holds so the board matches reality (no cost).
        //
        // STAYS OFF BY DEFAULT. Flipping it on was prepared for v0.4.1 and deliberately reverted before release, for
        // two reasons worth keeping written down. (1) Per-stop offsets are chosen PER STOP — measured once a stop has
        // enough samples, else the raw estimate — so on a line whose real loop is ~3x the estimate the two interleave
        // and the posted schedule is internally incoherent until every stop has warmed up, which is not guaranteed to
        // happen (a hold at a near stop excludes that vehicle from measuring every stop downstream). (2) It is the only
        // change of its kind not bounded by vanilla's own behaviour, and it would alter what the entire user base sees
        // by default with no play-test behind it. Turn it on per-city instead; revisit the default once the
        // measured/estimate mixing is fixed (that needs the dispatch to publish its offsets to the UI board).
        [SettingsUISection(Section, GroupRealism)]
        public bool RealisticTravelTime { get; set; } = false;

        // Also size the FLEET to the real loop. This is the one that COSTS MONEY: holding a tight headway on a line whose
        // real loop is ~2x the estimate needs ~2x the vehicles (and upkeep). OFF = keep the estimate-based count; ON =
        // provision for the real loop (grow-only; never cuts a line below the estimate; capped).
        [SettingsUISection(Section, GroupRealism)]
        public bool ProvisionRealFleet { get; set; } = false;

        // Force buses to physically STOP even when nobody is boarding or alighting. Vanilla lets a bus SKIP an empty
        // stop — it only slows and rolls through, never pulling in — and a skipped stop is never held to its scheduled
        // time, so the bus runs ahead of its timetable. That is worst at the TERMINUS, which anchors the whole schedule,
        // so the terminus is now ALWAYS forced to stop (no setting for it). This toggle extends the forced stop to EVERY
        // stop on a timetabled line: every posted time is then honoured, at the cost of a short dwell at each empty stop
        // (lines run a little slower). OFF by default. Buses/road vehicles only — trains, trams, ships and planes already
        // stop at every station in the base game.
        [SettingsUISection(Section, GroupStops)]
        public bool StopAtEveryStop { get; set; } = false;

        // MAXIMUM extra time a vehicle may stand at a stop past its posted departure, waiting for passengers who are
        // already boarding. It is a CEILING on lateness, not a floor on dwell: a vehicle with nobody boarding still
        // leaves exactly on its posted minute.
        //
        // This replaced a "minimum dwell" plus an unconditional force-departure. That combination was actively harmful:
        // the base game counts a citizen as a passenger the moment they START WALKING to the vehicle, so forcing the
        // departure ejected everyone still crossing the platform and put them back in the queue (reported live as
        // "the bus fills up, then empties down to a certain amount, then leaves").
        //
        // The mechanism is now the GAME'S OWN, not a fight with it: vanilla already waits for boarding passengers after
        // the scheduled time and gives up 1800 frames later (StopBoarding's cutoff). We simply ANCHOR that window so it
        // expires at this setting instead of at vanilla's ~10 minutes — see HoldStop's GO branch. Everything in between
        // (the widening boarding radius, the wait for passengers not yet seated) is stock behaviour, untouched.
        //
        // Split road/rail so rail can be raised independently — a train or metro exchanging a full platform takes far
        // longer to load than a bus, its passengers spread along the platform to reach their own carriage, and unlike a
        // bus it can never skip a stop. Both DEFAULT TO 3: the grace applies at every stop including the terminus and
        // including early arrivals, so a per-stop value multiplies across a loop (a 15-station line at 5 could add well
        // over an hour), and 3 keeps the release close to a strict improvement rather than a bet. Raise rail if you see
        // passengers piling up on a busy platform.
        // Capped at 10, just under vanilla's own ~9.9-minute ceiling — a larger value could not be honoured, since we
        // can only ever shorten that window, never extend it.
        [SettingsUISlider(min = 0f, max = 10f, step = 1f, unit = "integer")]
        [SettingsUISection(Section, GroupStops)]
        public int MaxDwellRoad { get; set; } = 3;

        [SettingsUISlider(min = 0f, max = 10f, step = 1f, unit = "integer")]
        [SettingsUISection(Section, GroupStops)]
        public int MaxDwellRail { get; set; } = 3;

        // Compatibility: adapt the timetable's frame<->minute math to slow-time mods (Time2Work / "Realistic Trips")
        // that lengthen the in-game day. Default OFF, so the base mod runs its pure vanilla-clock timing for the vast
        // majority who use no such mod. Turn it ON only under a slow-time mod: it then AUTO-DETECTS the real day length
        // at runtime so departures/stop times/fleet stay correct instead of running early. OFF pins it to the vanilla
        // 262144 frames/day (exact original behaviour). TimebaseSystem nudges (logs) if it detects a slow-time mod
        // while this is OFF. See TimebaseSystem.
        [SettingsUISection(Section, GroupCompat)]
        public bool RealisticTripsCompat { get; set; } = false;

        // Which time-of-day window an hour falls in (the timetable interval switches on these).
        public bool InNightWindow(int hour) => InWindow(hour, NightStart, NightEnd);
        public bool InPeakWindow(int hour) => InWindow(hour, MorningPeakStart, MorningPeakEnd) || InWindow(hour, EveningPeakStart, EveningPeakEnd);

        // Half-open [start, end); wraps past midnight when start > end (e.g. night 22..5).
        private static bool InWindow(int hour, int start, int end)
        {
            if (start == end) return false;
            return start < end ? (hour >= start && hour < end) : (hour >= start || hour < end);
        }

        public override void SetDefaults()
        {
            Enabled = true;
            ManageVehicleCount = true;
            MorningPeakStart = 6;
            MorningPeakEnd = 9;
            EveningPeakStart = 15;
            EveningPeakEnd = 19;
            NightStart = 22;
            NightEnd = 6; // keep in lockstep with the initializer above (this runs on an explicit "reset to defaults")
            RealisticTravelTime = false;
            ProvisionRealFleet = false;
            StopAtEveryStop = false;
            MaxDwellRoad = 3; // keep in lockstep with the initializers above (this runs on "reset to defaults")
            MaxDwellRail = 3;
            RealisticTripsCompat = false;
        }
    }
}
