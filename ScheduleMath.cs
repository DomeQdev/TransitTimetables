using System;

namespace TransitTimetables
{
    // Pure service math shared by the dispatch system and the UI. Everything here is day-length AGNOSTIC: the
    // window logic works in plain minutes-of-day, and the headway logic works in whatever unit the caller passes
    // (frames or minutes — it only ever divides). The ONE day-length-dependent conversion — route "duration units"
    // <-> in-game minutes — is supplied at runtime by TimebaseSystem and passed in as `unitMinutes`, so this stays a
    // pure static helper with no hidden global read. A route "duration unit" is a fixed 60 sim frames (RouteUtils);
    // only the day length varies: vanilla = 262144 frames/day -> unitMinutes ~0.3296, and slow-time mods (Time2Work)
    // stretch it.
    //
    // ===================== WHAT WAS DELETED HERE, AND WHY IT IS NOT COMING BACK =====================
    // This file used to own an absolute DEPARTURE GRID: FirstDeparture / NextDeparture / PreviousDeparture /
    // DayFromFirst / Upcoming / IntervalFor / MaxInterval / DerivedFleet. The mod set a clock time for every stop of
    // every line and held vehicles to it.
    //
    // The model is gone. A player now states how many VEHICLES a line runs in each window; the headway is then a
    // CONSEQUENCE (cycle / vehicles), not an input, and the mod's only intervention is to even the spacing out at the
    // terminus. Nothing is posted against a wall clock, so there is no grid to walk, no "missed slot" to catch up, and
    // no way for the printed board to disagree with the vehicles — the two failure modes that most of the deleted code
    // existed to paper over.
    //
    // If you are tempted to re-add a grid: the reason a fixed grid could not work here is that the mod does not own
    // the vehicles. Vanilla spawns them from depots on its own schedule, lets them skip stops, and retires them by
    // odometer. A grid assumes a vehicle is available for every slot; a headway assumes only that the vehicles which
    // exist should be spread out. The second assumption is the true one.
    // ===============================================================================================
    //
    // Everything here is SCHEDULE-AWARE via a `schedule` argument (LineSchedule.Day/Night/DayAndNight): a night-only
    // line only ever runs its night vehicle count, and only inside the night window; a day-only line never runs the
    // night count and does not run at night at all.
    public static class ScheduleMath
    {
        // How many vehicles this line should be running at a given minute-of-day, respecting its operating schedule.
        // A per-line CUSTOM PEAK, when enabled and the hour falls inside either custom window, OVERRIDES the global
        // peak/off-peak/night choice for this line only — exactly as the old custom-peak INTERVAL did.
        //
        // Returns 0 when the line is out of service (a day-only line at night, a night-only line by day). Zero is
        // load-bearing and means "we have no opinion": the dispatch skips the fleet write entirely and lets vanilla
        // shut the line down, which is what vanilla already does for an out-of-window line.
        public static int VehiclesFor(TransitTimetablesSetting s, LineFleetPlan plan, CustomPeakSchedule customSch, int minuteOfDay, int schedule)
        {
            if (!InService(s, schedule, minuteOfDay))
                return 0;
            int hour = Hour(minuteOfDay);
            if (customSch.m_Enabled
                && (InWindow(hour, customSch.m_Start1, customSch.m_End1) || InWindow(hour, customSch.m_Start2, customSch.m_End2)))
                return Pos(plan.m_CustomPeakVehicles);
            if (schedule == LineSchedule.Night) return Pos(plan.m_NightVehicles);                                    // night-only
            if (schedule == LineSchedule.Day) return s.InPeakWindow(hour) ? Pos(plan.m_PeakVehicles) : Pos(plan.m_OffPeakVehicles); // day-only, never night
            if (s.InNightWindow(hour)) return Pos(plan.m_NightVehicles);
            if (s.InPeakWindow(hour)) return Pos(plan.m_PeakVehicles);
            return Pos(plan.m_OffPeakVehicles);
        }

        // Half-open [start, end) hour window, wrapping past midnight when start > end (mirrors
        // TransitTimetablesSetting.InWindow). Public so the per-line custom-peak windows use the same rule the global
        // windows do.
        public static bool InWindow(int hour, int start, int end)
        {
            if (start == end) return false;
            return start < end ? (hour >= start && hour < end) : (hour >= start || hour < end);
        }

        // Is a minute-of-day inside the line's operating window? (Night-only: the night window; day-only: everything
        // else; both: always.)
        public static bool InService(TransitTimetablesSetting s, int schedule, int minuteOfDay)
        {
            int hour = Hour(minuteOfDay);
            if (schedule == LineSchedule.Night) return s.InNightWindow(hour);
            if (schedule == LineSchedule.Day) return !s.InNightWindow(hour);
            return true;
        }

        // THE HEADWAY. One closed loop, `vehicles` of them, each taking `cycle` to come round: they can only be evenly
        // spaced at cycle/vehicles apart. Unit-agnostic — pass frames, get frames; pass minutes, get minutes — because
        // the dispatch works in frames (monotonic, midnight-safe) while the UI works in minutes.
        //
        // `cycle` must be the FULL round trip INCLUDING the layovers the mod itself imposes at the timing points, not
        // just the driving time. Feeding it the bare loop understates the headway, every vehicle then arrives back
        // later than its target and the regulation degenerates into "leave immediately", which is vanilla.
        public static float Headway(float cycle, int vehicles)
        {
            if (vehicles < 1) vehicles = 1;
            return cycle <= 0f ? 0f : cycle / vehicles;
        }

        // Round-trip time of one vehicle over the whole line, in in-game minutes. `unitMinutes` is the runtime
        // route-unit->minute scale from TimebaseSystem (vanilla ~0.3296; smaller under a stretched day).
        public static float RoundTripMinutes(float stableDurationUnits, float unitMinutes) => stableDurationUnits * unitMinutes;

        // Vehicles needed to sustain a given headway = ceil(round-trip / headway), at least 1. NOT used to size
        // anything any more — the player sizes the line. It survives for exactly one job: converting a save's LEGACY
        // per-window INTERVALS into the per-window vehicle counts that replaced them (see MigrateFleetPlan), so an
        // upgraded city keeps roughly the service level it had instead of jumping to a default.
        public static int VehiclesForHeadway(float roundTripMinutes, int headwayMinutes)
        {
            if (headwayMinutes < 1) headwayMinutes = 1;
            int fleet = (int)Math.Ceiling(roundTripMinutes / headwayMinutes);
            return fleet < 1 ? 1 : fleet;
        }

        // ===== Real-travel-time correction (the game's path estimate undershoots real loop time) =====
        // The correction is a DIMENSIONLESS factor = (real loop) / (estimated loop). It multiplies the estimate to
        // recover the real value, and it is RT-INVARIANT (both quantities are frame-based, so a stretched clock
        // cancels). It is no longer optional: the headway is computed FROM the loop, so an estimate that is 2x short
        // would space vehicles at half the interval they can actually keep and the regulation would never bind.

        // COLD-START prior for a line with no measured loops yet. Live data from one city (2026-07) showed the
        // undershoot rises with stop density (stops per loop-minute): ~1.7x on sparse lines, ~2.5x on stop-dense ones,
        // plateauing near ~2.5x. Rough linear fit with an intercept near 1 (a hypothetical stopless express would match
        // the estimate). RT-invariant: uses the FIXED vanilla unit->minute constant, not the live scale, so a slow-time
        // mod does not move the prior. The caller clamps the result.
        public static float DensityPriorRatio(int stops, float stableDurationUnits)
        {
            if (stops <= 0 || stableDurationUnits <= 1f) return 1f;
            const float kVanillaUnitMinutes = 0.32958984f; // 675/2048, the vanilla 262144-frames/day scale (fixed reference)
            float estMinutes = stableDurationUnits * kVanillaUnitMinutes;
            if (estMinutes < 0.01f) return 1f;
            float density = stops / estMinutes;            // stops per reference-minute (matches how the 7.7 slope was fit)
            float r = 1.1f + 7.7f * density;               // linear fit over the OBSERVED density range (0.08-0.18)
            // The live data PLATEAUED near ~2.5x — the undershoot stops climbing once stops are close — and we have no
            // evidence above that, so a very dense line must NOT be linearly extrapolated toward the 4x safety clamp on
            // a cold start. Cap the PRIOR at the observed plateau; live measurement (which may legitimately exceed it)
            // takes over after a few loops. The caller still clamps.
            return r > 2.6f ? 2.6f : r;
        }

        // Clamp a correction factor into a safe range. The floor is 0.5 (a genuinely fast line may beat the estimate)
        // and the ceiling 4x, so a bad measurement can never blow the headway up. `forFleet` is retained for callers
        // that want the old grow-only behaviour; nothing in the mod sizes a fleet from a measurement any more.
        public static float ClampCorrection(float factor, bool forFleet)
        {
            if (float.IsNaN(factor) || float.IsInfinity(factor)) return 1f;
            float lo = forFleet ? 1.0f : 0.5f;
            const float hi = 4.0f;
            if (factor < lo) return lo;
            if (factor > hi) return hi;
            return factor;
        }

        public static string FormatHm(int minuteOfDay)
        {
            int m = Mod1440(minuteOfDay);
            int hh = m / 60, mm = m % 60;
            return (hh < 10 ? "0" : "") + hh + ":" + (mm < 10 ? "0" : "") + mm;
        }

        private static int Hour(int minuteOfDay) => Mod1440(minuteOfDay) / 60;
        private static int Mod1440(int m) => ((m % 1440) + 1440) % 1440;
        private static int Pos(int v) => v < 1 ? 1 : v;
    }
}
