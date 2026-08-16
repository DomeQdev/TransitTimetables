using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;

namespace TransitTimetables
{
    // NAME IS LOAD-BEARING, and it must stay UNIQUE across this author's mods. Setting.ApplyAndSave() calls
    // AssetDatabase.SaveSpecificSetting(GetType().Name), which resolves the target settings asset by the CLASS SIMPLE
    // NAME and stops at the FIRST match. While every mod called this class "Setting" a save from any one of them could
    // land in another mod's file and skip its own (observed live: one session wrote only TransitTimetables.coc and left
    // the other six byte-identical). Nothing persisted derives from this name — the on-disk identity is [FileLocation]
    // below plus the name passed to LoadSettings — so renaming the class is safe, but renaming it back is not.
    [FileLocation(nameof(TransitTimetables))]
    public class TransitTimetablesSetting : ModSetting
    {
        public const string Section = "Main";
        public const string GroupWindows = "Peak windows";
        // GroupRealism is EMPTY now — both settings that lived in it are [SettingsUIHidden] storage-only fields (see
        // ProvisionRealFleet / RealisticTravelTime). The constant survives because Mod.cs registers a locale entry for
        // it and because an empty group renders nothing; deleting it would be pure churn for no user-visible change.
        public const string GroupRealism = "Realistic travel time";
        public const string GroupStops = "Stops";
        public const string GroupCompat = "Compatibility";
        public const string GroupGeneral = "General";

        public TransitTimetablesSetting(IMod mod) : base(mod) { }

        // NOTE: initializers double as the settings-migration failsafe (missing keys in an old .coc keep these
        // values instead of defaulting to 0/false).

        // ---- Master switch ----
        // Turn the whole mod on/off without uninstalling. OFF = exact vanilla: every line stops being managed (held
        // buses are released, and the mod-applied fleet count and unbunching factor are reverted), while your per-line
        // timetables are kept so re-enabling resumes them. Default ON.
        [SettingsUISection(Section, GroupGeneral)]
        public bool Enabled { get; set; } = true;

        // ---- Vehicle-count management ----
        // RETIRED to migration-only. This dropdown briefly replaced the "Manage vehicle count" checkbox below; the
        // checkbox is the live setting again and this field survives ONLY to carry a stored 0.5.x choice across. It is
        // never shown and never read outside Migrate().
        [SettingsUIHidden]
        public VehicleCountMode VehicleCounts { get; set; } = VehicleCountMode.Unset;

        // ---- Legacy settings, retained ONLY so their stored values can be migrated ----
        // Do NOT delete these and do NOT change their type. The .coc is a DIFF against a default instance, so a value is
        // only written when it differs from the default — which means a stored value is a deliberate choice worth
        // preserving, and a deleted property drops it silently. Changing a shipped property's TYPE is worse than
        // deleting it: Colossal.Json fails Enum.Parse on the old literal, logs a warning, and writes ordinal 0.
        // [SettingsUIHidden] removes them from the Options page while Colossal.Json still serializes and decodes them.

        // BACK ON THE OPTIONS PAGE as the pre-0.5 checkbox it always was — same name, same type, same default, so a
        // .coc written by any version decodes straight into it. This is now the live setting again and the dropdown
        // above is the legacy one; Migrate() below moves the value in that direction.
        [SettingsUISection(Section, GroupGeneral)]
        public bool ManageVehicleCount { get; set; } = true;

        // RETIRED to storage-only. It asked whether to size a line's fleet from the MEASURED loop rather than the
        // game's estimate — a question that only exists while the mod derives the count. It does not any more: the
        // player states the count and the mod derives the HEADWAY from the measured loop, always, because the headway
        // IS cycle/vehicles and computing it from a number the game is 2x wrong about would make the regulation
        // meaningless. Kept (never deleted, never retyped) so the stored value in an existing .coc still decodes.
        [SettingsUIHidden]
        public bool ProvisionRealFleet { get; set; } = false;

        // RESTORED. Deleted outright in v0.5's coherence pass, so as far as the CODE is concerned this is a new
        // property — but the stored file is a different matter, and the assumption first written here was wrong.
        // A real pre-0.5 .coc was found still holding "RealisticTravelTime": true after several 0.5.x releases: the
        // .coc is a diff against a default instance and is only rewritten when settings are saved, so a key whose
        // property no longer exists is not purged, it simply sits there unread until something claims it again.
        // Re-adding the property therefore RESURRECTS the pre-0.5 preference rather than starting from this
        // initializer. That is the behaviour we want — a returning player keeps what they chose — but it means the
        // default below only ever applies to someone who never set it pre-0.5, and it is why the realism notice
        // gates on the VALUES rather than on the presence of the key.
        //
        // RETIRED to storage-only, for the same reason as ProvisionRealFleet above. It gated whether POSTED TIMES used
        // the measured loop; there are no posted times any more — a headway-regulated line has no clock grid — and the
        // one thing the loop still feeds (the headway itself, and the "reaches you ~N minutes after the terminus"
        // figure on the stop board) cannot sensibly be switched off. Kept so an existing .coc still decodes.
        [SettingsUIHidden]
        public bool RealisticTravelTime { get; set; } = false;

        // Has the one-time "vehicle counts are changing" notice been answered? Global rather than per-city on purpose:
        // the choice it offers IS global (it sets VehicleCounts), so asking again in the next city would only let a
        // second answer silently overwrite the first. Not shown, and never written until the notice is answered.
        [SettingsUIHidden]
        public bool MigrationNoticeAnswered { get; set; } = false;

        // Has the one-time "realistic timings are switched off" notice been answered? A SECOND flag rather than a reuse
        // of the one above: everyone who answered the v0.5 vehicle-count notice already has that one set to true, and
        // they are precisely the players this notice exists for — they ran a version where both realism features were
        // unconditional and would otherwise lose them silently. Global, not per-city, because the choice it offers is
        // global. Never written until the notice is actually answered.
        [SettingsUIHidden]
        public bool RealismNoticeAnswered { get; set; } = false;

        // Has the one-time "this mod now works by vehicle count" notice been answered? A THIRD flag rather than a reuse
        // of either above, and for the same reason the second one existed: everyone who answered an earlier notice
        // already has those flags set to true, and they are precisely the players this notice exists for — they ran a
        // version that asked for a headway and would otherwise find the panel replaced with no explanation. Global, not
        // per-city, because what it describes is a property of the mod. Never written until the notice is answered.
        [SettingsUIHidden]
        public bool CountModelNoticeAnswered { get; set; } = false;

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
        // that a free-flow estimate ignores). The mod measures each line's real loop and uses the measurement. That is
        // no longer optional and has no toggle: the headway a line runs IS its cycle divided by its vehicles, so a loop
        // figure that is half the truth would space vehicles at half the interval they can keep and the regulation
        // would never bind at all. The measurement is RT-invariant (frame-based), so it composes with the slow-time
        // compatibility below.
        //
        // Force buses to physically STOP even when nobody is boarding or alighting. Vanilla lets a bus SKIP an empty
        // stop — it only slows and rolls through, never pulling in. That matters most at a TIMING POINT: a vehicle that
        // rolls past its terminus never enters Boarding, so the mod never gets to space it and the departure it was
        // meant to regulate simply does not happen. Both timing points are therefore ALWAYS forced to stop (no setting
        // for it). This toggle extends the forced stop to EVERY stop on a managed line, at the cost of a short dwell at
        // each empty one — which lengthens the loop, so the headway widens a little. OFF by default. Buses/road
        // vehicles only — trains, trams, ships and planes already stop at every station in the base game.
        [SettingsUISection(Section, GroupStops)]
        public bool StopAtEveryStop { get; set; } = false;

        // MAXIMUM extra time a vehicle may stand at a stop once it is due to leave, waiting for passengers who are
        // ALREADY boarding. It is a CEILING, not a floor: a vehicle with nobody boarding leaves the moment it is due.
        // At an ordinary stop "due" means "as soon as it arrived", which is what makes an ordinary stop take exactly as
        // long as the boarding does and no longer. At a timing point it means "once its headway is up".
        //
        // This replaced a "minimum dwell" plus an unconditional force-departure. That combination was actively harmful:
        // the base game counts a citizen as a passenger the moment they START WALKING to the vehicle, so forcing the
        // departure ejected everyone still crossing the platform and put them back in the queue (reported live as
        // "the bus fills up, then empties down to a certain amount, then leaves").
        //
        // The mechanism is now the GAME'S OWN, not a fight with it: vanilla already waits for boarding passengers after
        // the scheduled time and gives up 1800 frames later (StopBoarding's cutoff). We simply ANCHOR that window so it
        // expires at this setting instead of at vanilla's ~10 minutes — see ReleaseVehicle. Everything in between
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

        // MINIMUM LAYOVER AT A TIMING POINT. A vehicle that reaches the terminus exactly on its headway still stands
        // here for at least this long before it sets off again.
        //
        // It is not padding. Without a floor the regulation has NO SLACK TO GIVE BACK: the headway is cycle/vehicles,
        // and if the cycle is exactly the driving time then a vehicle that loses two minutes in traffic can never
        // recover them — every vehicle behind it inherits the delay and the line drifts permanently. A minute or two
        // of scheduled standing time is the buffer a real operator builds into every turn for exactly this reason, and
        // it is what lets a late vehicle catch up instead of dragging the whole line down with it.
        //
        // The cost is honest and worth stating: this time is part of the round trip, so it is part of the headway. At
        // 2 minutes on a line with 10 vehicles the headway is 12 seconds wider than it would otherwise be — nothing.
        // On a 2-vehicle shuttle it is a full minute wider. Set it to 0 only if you want vehicles to turn round the
        // instant they arrive and you accept that the line can never recover from a delay.
        //
        // A Terminus B, if you set one, carries its OWN minimum (the number on the stop board) and both are counted.
        [SettingsUISlider(min = 0f, max = 15f, step = 1f, unit = "integer")]
        [SettingsUISection(Section, GroupStops)]
        public int MinTerminusDwell { get; set; } = 2;

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

        // True when the MOD is the one sizing lines. Reads the checkbox directly again. The old note about Unset
        // resolving to false no longer applies: there is no sentinel in this path, and the checkbox's own default
        // (true) is the pre-0.5 behaviour — a fresh install manages counts.
        public bool ModSizesFleet => ManageVehicleCount;

        // Resolve the Unset sentinel exactly once, from the two legacy bools. Called from Mod.OnLoad immediately after
        // LoadSettings — NOT from OnGameLoadingComplete, which also fires at boot for the main menu, where an enum whose
        // current value matches no visible member would render the dropdown blank if the player opened Options first.
        //
        // Returns true when it changed something (the caller then flushes to disk).
        //
        // DIRECTION REVERSED 2026-08-03. It used to fold the pre-0.5 checkbox into the dropdown; now it unfolds a
        // stored dropdown value back onto the checkbox, because the checkbox is the live setting again.
        //
        // Runs exactly once per stored value: after importing, the dropdown is reset to the Unset sentinel, and since
        // the .coc is a DIFF against a default instance whose value is also Unset, the key disappears from the file on
        // the next save. The caller flushes when this returns true, so that save happens immediately and a later
        // checkbox change cannot be overwritten by a stale dropdown value on the following load.
        //
        // A player who never saw 0.5 has no stored dropdown, so nothing happens and their pre-0.5 checkbox stands.
        public bool Migrate()
        {
            if (VehicleCounts == VehicleCountMode.Unset)
                return false;
            ManageVehicleCount = VehicleCounts == VehicleCountMode.ModDecides;
            VehicleCounts = VehicleCountMode.Unset;   // consumed; self-clears from the .coc on the flush below
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
            RealisticTravelTime = false;
            StopAtEveryStop = false;
            MaxDwellRoad = 3; // keep in lockstep with the initializers above (this runs on "reset to defaults")
            MaxDwellRail = 3;
            MinTerminusDwell = 2;
            RealisticTripsCompat = false;
        }
    }

    // LEGACY, migration-only (see the VehicleCounts property). Who sizes a line's fleet. Backed by int (the default) —
    // the settings widget casts to int, and an enum-typed property with no attribute already renders as a dropdown.
    //
    // HISTORICAL NOTE, kept because it records a decision that has since been REVERSED and the reversal is the whole
    // point of the current mod: this enum's doc used to argue that the headway is the cost lever and that a per-window
    // vehicle COUNT should never be exposed, because "an exact count below what the headway needs guarantees missed
    // departures". That objection only bites if the mod is also promising a fixed clock grid — a missed departure is a
    // departure that was PRINTED and not run. Nothing is printed now, so a line with fewer vehicles simply runs a wider
    // headway, which is not a failure but the correct and visible consequence of the choice. The count is the lever.
    public enum VehicleCountMode
    {
        // ORDINAL 0 IS LOAD-BEARING. Colossal.Json writes ordinal 0 whenever it cannot parse a stored enum literal, so
        // anything that goes wrong degrades to "not decided yet" and re-runs the migration, rather than silently
        // selecting a real mode. That also makes RENAMING a member above safe: an older stored literal simply fails to
        // parse and re-migrates. [SettingsUIHidden] on the MEMBER keeps it out of the dropdown while leaving it a
        // perfectly valid stored and decodable value. It is also never written to disk: the .coc is a diff against a
        // default instance whose value is likewise Unset, so the scheme is self-clearing.
        [SettingsUIHidden]
        Unset = 0,

        // The mod sizes every timetabled line for its MEASURED loop, to hold the headway you set — and it means EVERY
        // line. Vanilla's "Assigned Vehicles" slider has no lasting effect on a timetabled line in this mode; the mod
        // re-asserts its own count within a tick. That is intended: the mode says the mod decides.
        ModDecides = 1,

        // The mod never touches vehicle counts. Any line it was sizing is handed back ONCE and then left alone, so
        // either you (via vanilla's slider) or a dedicated fleet mod (All Transit + Truck and friends) can own the
        // counts without the two fighting over the same VehicleInterval modifier every tick. The departure HOLD still
        // runs; a line with too few vehicles simply runs wider than its set interval.
        HandsOff = 2,
    }
}
