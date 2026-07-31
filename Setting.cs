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
        // GroupRealism ("Realistic travel time") is GONE: both settings that lived in it are gone too — the posted-time
        // correction is unconditional now, and fleet sizing folded into the VehicleCounts dropdown.
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

        // ---- Vehicle-count management ----
        // Who decides how many vehicles a timetabled line runs. This replaced the old "Manage vehicle count" on/off
        // toggle AND absorbed "Provision fleet": sizing a line from the game's travel ESTIMATE while posting times
        // measured from its REAL loop is incoherent — it advertises a schedule the line physically cannot keep — so
        // "the mod decides" always means "sized for the measured loop", and there is no separate toggle for it.
        //
        // The departure HOLD is independent of all three values and always runs; with too few vehicles a line simply
        // runs wider than its set interval.
        [SettingsUISection(Section, GroupGeneral)]
        public VehicleCountMode VehicleCounts { get; set; } = VehicleCountMode.Unset;

        // ---- Legacy settings, retained ONLY so their stored values can be migrated ----
        // Do NOT delete these and do NOT change their type. The .coc is a DIFF against a default instance, so a value is
        // only written when it differs from the default — which means a stored value is a deliberate choice worth
        // preserving, and a deleted property drops it silently. Changing a shipped property's TYPE is worse than
        // deleting it: Colossal.Json fails Enum.Parse on the old literal, logs a warning, and writes ordinal 0.
        // [SettingsUIHidden] removes them from the Options page while Colossal.Json still serializes and decodes them.
        // Read once by Migrate() and never again.
        [SettingsUIHidden]
        public bool ManageVehicleCount { get; set; } = true;

        [SettingsUIHidden]
        public bool ProvisionRealFleet { get; set; } = false;

        // Has the one-time "vehicle counts are changing" notice been answered? Global rather than per-city on purpose:
        // the choice it offers IS global (it sets VehicleCounts), so asking again in the next city would only let a
        // second answer silently overwrite the first. Not shown, and never written until the notice is answered.
        [SettingsUIHidden]
        public bool MigrationNoticeAnswered { get; set; } = false;

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

        // CS2's own pathfinder estimate of how long a line takes systematically UNDERSHOOTS the real, simulated loop
        // (live-measured ~1.7x on sparse lines to ~2.5x on stop-dense ones — the acceleration and braking at every stop
        // that a free-flow estimate ignores). The mod measures each line's real loop and corrects for it. The correction
        // is RT-invariant (frame-based), so it composes with the slow-time compatibility below.
        //
        // There is NO LONGER a toggle for correcting POSTED TIMES: it is always on. The toggle ("Realistic travel time")
        // asked the player whether they wanted the mod to tell the truth, which is not a real choice — it costs nothing,
        // has no downside, and leaving it off built the whole timetable on a number the game itself is wrong about.
        // It was only ever off by default because the correction was incoherent: offsets were picked PER STOP (measured
        // where a stop had samples, raw estimate elsewhere), so one route interleaved two incompatible clocks. That is
        // fixed — every offset now derives from the line's single measured loop via the ladder in HoldAllStops, and the
        // dispatch publishes what it used so the board cannot disagree with the vehicles.
        //
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

        // True when the MOD is the one sizing lines. "The user decides" still sizes every line the user has not taken
        // over by hand, so it belongs here: until a per-line count is entered, somebody has to pick a number, and
        // leaving it to vanilla would ignore the player's chosen headway entirely.
        public bool ModSizesFleet => VehicleCounts == VehicleCountMode.ModManages
                                  || VehicleCounts == VehicleCountMode.PlayerManages;

        // Resolve the Unset sentinel exactly once, from the two legacy bools. Called from Mod.OnLoad immediately after
        // LoadSettings — NOT from OnGameLoadingComplete, which also fires at boot for the main menu, where an enum whose
        // current value matches no visible member would render the dropdown blank if the player opened Options first.
        //
        // Returns true when it changed something (the caller then flushes to disk).
        //
        // Only ManageVehicleCount is consulted. ProvisionRealFleet is deliberately NOT mapped to a mode: there is no
        // mode meaning "manage counts but size them from the estimate", because that is the incoherent state this
        // redesign exists to remove. A player who had it off is instead TOLD, once, by the migration notice.
        public bool Migrate()
        {
            if (VehicleCounts != VehicleCountMode.Unset)
                return false;
            // A stored false is a deliberate opt-out (the default is true, and only non-default values are written),
            // and it is the setting v0.3.5 shipped so a dedicated fleet mod could own the counts. Losing it would
            // restart exactly the modifier war that release ended.
            VehicleCounts = ManageVehicleCount ? VehicleCountMode.ModManages : VehicleCountMode.OtherModManages;
            return true;
        }

        public override void SetDefaults()
        {
            // NOTE: the game never calls this for a ModSetting — the property initializers above are the real defaults.
            // Kept correct anyway for the explicit "reset to defaults" path, but nothing may depend on it running.
            Enabled = true;
            VehicleCounts = VehicleCountMode.Unset;
            ManageVehicleCount = true;
            MorningPeakStart = 6;
            MorningPeakEnd = 9;
            EveningPeakStart = 15;
            EveningPeakEnd = 19;
            NightStart = 22;
            NightEnd = 6; // keep in lockstep with the initializer above (this runs on an explicit "reset to defaults")
            ProvisionRealFleet = false;
            StopAtEveryStop = false;
            MaxDwellRoad = 3; // keep in lockstep with the initializers above (this runs on "reset to defaults")
            MaxDwellRail = 3;
            RealisticTripsCompat = false;
        }
    }

    // Who sizes a timetabled line's fleet. Backed by int (the default) — the settings widget casts to int, and an
    // enum-typed property with no attribute already renders as a dropdown, so [SettingsUIDropdown] is not wanted here
    // (it selects a different widget that demands a runtime item source).
    public enum VehicleCountMode
    {
        // ORDINAL 0 IS LOAD-BEARING. Colossal.Json writes ordinal 0 whenever it cannot parse a stored enum literal, so
        // anything that goes wrong degrades to "not decided yet" and re-runs the migration, rather than silently
        // selecting a real mode. [SettingsUIHidden] on the MEMBER keeps it out of the dropdown while leaving it a
        // perfectly valid stored and decodable value. It is also never written to disk: the .coc is a diff against a
        // default instance whose value is likewise Unset, so the scheme is self-clearing.
        [SettingsUIHidden]
        Unset = 0,

        // The mod sizes every line for its MEASURED loop, to hold the headway you set. The default for a new city.
        ModManages = 1,

        // The mod never touches vehicle counts. Any line it was sizing is handed back ONCE and then left alone, so a
        // dedicated fleet mod (All Transit + Truck and friends) can own the counts without the two fighting over the
        // same VehicleInterval modifier every tick. This is the old "Manage vehicle count = off".
        OtherModManages = 2,

        // You set the counts per line. Until a line is taken over by hand the mod still sizes it — otherwise a line you
        // have not touched would fall back to vanilla's count, which does not contain your chosen headway at all.
        PlayerManages = 3,
    }
}
