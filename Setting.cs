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

        // Minimum time a vehicle stands at a stop when it arrives ON its scheduled minute or LATE (an EARLY vehicle is
        // unaffected — it is already waiting for its posted time, which gives passengers longer than any of this).
        // Split road/rail because loading time is dominated by passenger volume: a metro or train exchanging a full
        // platform of people needs materially longer than a bus, and the game's own travel estimate ignores boarding
        // time entirely. Raising these lengthens the real loop, which the mod measures and feeds back into the vehicle
        // count automatically, so a line will ask for more vehicles to hold the same headway.
        //
        // ROAD defaults to 2 — the value hard-coded before this setting existed, so buses are unchanged.
        // RAIL defaults to 5, which IS a deliberate behaviour change for existing users (a new key is absent from their
        // saved settings, so they inherit this initializer). Justification: live measurement on a real city put rail
        // loops at ~3x the game's own estimate, and that estimate ignores passenger boarding time entirely — platform
        // exchange on a train or metro is simply not 2 minutes. 5 encodes the observed reality rather than leaving every
        // rail line permanently behind its own timetable. Players who want the old behaviour set it back to 2.
        [SettingsUISlider(min = 0f, max = 15f, step = 1f, unit = "integer")]
        [SettingsUISection(Section, GroupStops)]
        public int MinDwellRoad { get; set; } = 2;

        [SettingsUISlider(min = 0f, max = 15f, step = 1f, unit = "integer")]
        [SettingsUISection(Section, GroupStops)]
        public int MinDwellRail { get; set; } = 5;

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
            MinDwellRoad = 2; // keep in lockstep with the initializers above (this runs on "reset to defaults")
            MinDwellRail = 5;
            RealisticTripsCompat = false;
        }
    }
}
