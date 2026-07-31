using System;

namespace TransitTimetables
{
    // Pure timetable math shared by the dispatch system and the UI. Everything here is day-length AGNOSTIC: all the
    // clock/interval logic works in plain minutes-of-day. The ONE day-length-dependent conversion — route "duration
    // units" <-> in-game minutes — is supplied at runtime by TimebaseSystem and passed in as `unitMinutes` (so this
    // stays a pure static helper with no hidden global read). A route "duration unit" is a fixed 60 sim frames
    // (RouteUtils); only the day length varies: vanilla = 262144 frames/day -> unitMinutes ~0.3296, and slow-time mods
    // (Time2Work) stretch it. The dispatch/UI convert minutes<->frames with the matching TimebaseSystem.FramesPerMinute.
    //
    // Everything here is SCHEDULE-AWARE via a `schedule` argument (LineSchedule.Day/Night/DayAndNight): a night-only
    // line only ever runs the night interval inside the night window (its first departure is interpreted within that
    // window); a day-only line never uses the night interval and does not run at night.
    public static class ScheduleMath
    {
        // Headway (minutes) in effect at a given minute-of-day, respecting the line's operating schedule. A per-line
        // CUSTOM PEAK (PR #5), when enabled and the hour falls inside either custom window, OVERRIDES the global
        // peak/off-peak/night for this line only.
        public static int IntervalFor(TransitTimetablesSetting s, TimetableSchedule sch, CustomPeakSchedule customSch, int minuteOfDay, int schedule)
        {
            int hour = Hour(minuteOfDay);
            if (customSch.m_Enabled
                && (InWindow(hour, customSch.m_Start1, customSch.m_End1) || InWindow(hour, customSch.m_Start2, customSch.m_End2)))
                return Pos(customSch.m_Interval);
            if (schedule == LineSchedule.Night) return Pos(sch.m_NightInterval);                          // night-only
            if (schedule == LineSchedule.Day) return s.InPeakWindow(hour) ? Pos(sch.m_PeakInterval) : Pos(sch.m_OffPeakInterval); // day-only, never night
            if (s.InNightWindow(hour)) return Pos(sch.m_NightInterval);
            if (s.InPeakWindow(hour)) return Pos(sch.m_PeakInterval);
            return Pos(sch.m_OffPeakInterval);
        }

        // Half-open [start, end) hour window, wrapping past midnight when start > end (mirrors TransitTimetablesSetting.InWindow). Public
        // so the per-line custom-peak windows use the same rule the global windows do.
        public static bool InWindow(int hour, int start, int end)
        {
            if (start == end) return false;
            return start < end ? (hour >= start && hour < end) : (hour >= start || hour < end);
        }

        // The LONGEST headway this line can legitimately run, mirroring IntervalFor's own branches (a day-only line
        // never uses the night interval, a night-only line uses nothing else). Any real gap between two consecutive
        // slots equals some IntervalFor(...) value, so it is bounded by this — which makes it the safe ceiling for a
        // hold. Deliberately NOT IntervalFor(now): the interval can CHANGE across a window boundary (a 04:50 night
        // slot at interval 30 schedules 05:20, but IntervalFor(05:00) is the off-peak 12), so a per-minute bound would
        // spuriously release a bus that is waiting a legitimate headway across the crossover.
        public static int MaxInterval(TimetableSchedule sch, CustomPeakSchedule customSch, int schedule)
        {
            int max;
            if (schedule == LineSchedule.Night) max = Pos(sch.m_NightInterval);
            else
            {
                max = Pos(sch.m_PeakInterval);
                int o = Pos(sch.m_OffPeakInterval);
                if (o > max) max = o;
                if (schedule != LineSchedule.Day) { int n = Pos(sch.m_NightInterval); if (n > max) max = n; } // day-only never runs night
            }
            // A custom-peak interval is also a headway this line can legitimately run, so the hold bound must include it.
            if (customSch.m_Enabled) { int c = Pos(customSch.m_Interval); if (c > max) max = c; }
            return max;
        }

        // The effective first-departure minute-of-day. A night-only line's first departure is interpreted within the
        // night window: a value outside [NightStart, NightEnd) is clamped to the night window start.
        public static int FirstDeparture(TransitTimetablesSetting s, TimetableSchedule sch, int schedule)
        {
            int first = sch.m_FirstDeparture;
            // Clamp a first departure that falls outside the line's operating window to the window's start, so the
            // day's schedule always begins on an in-service minute: night-only -> NightStart, day-only -> NightEnd.
            if (schedule == LineSchedule.Night && !s.InNightWindow(Hour(first)))
                return (((s.NightStart % 24) + 24) % 24) * 60;
            if (schedule == LineSchedule.Day && s.InNightWindow(Hour(first)))
                return (((s.NightEnd % 24) + 24) % 24) * 60;
            return first;
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

        // Next scheduled departure as an ABSOLUTE minute from today's midnight (may be negative when tonight's night
        // service actually started yesterday, or exceed 1439 for tomorrow) that is >= nowMinute AND in-service. Scans
        // yesterday/today/tomorrow so a night window that wraps past midnight resolves correctly. Used by the hold.
        public static int NextDeparture(TransitTimetablesSetting s, TimetableSchedule sch, CustomPeakSchedule customSch, int schedule, int nowMinute)
        {
            int first = FirstDeparture(s, sch, schedule);
            for (int day = -1; day <= 1; day++)
            {
                int t = first + day * 1440;
                int dayEnd = t + 1440; // keep each day's progression ANCHORED at `first`: don't let it step across the
                                       // midnight boundary onto a different residue when the interval doesn't divide
                                       // 1440 — that drifted the whole day's schedule off the set first-departure.
                int guard = 0;
                while (guard < 4000 && t < dayEnd)
                {
                    int minute = Mod1440(t);
                    if (!InService(s, schedule, minute)) break; // left the operating window -> this block is done
                    if (t >= nowMinute) return t;               // earliest in-window departure at/after now
                    t += IntervalFor(s, sch, customSch, minute, schedule);
                    guard++;
                }
            }
            return first;
        }

        // The most recent scheduled departure STRICTLY BEFORE nowMinute, as an ABSOLUTE minute from today's midnight
        // (may be negative when it belongs to yesterday's service). Mirror of NextDeparture with the SAME anchored
        // day-stepping, so a night window that wraps past midnight and a non-dividing interval stay aligned. Used by the
        // missed-trip catch-up to tell whether a scheduled slot has passed UNCOVERED. Returns int.MinValue when no
        // in-service departure precedes now (e.g. before the very first departure of a day-only line).
        public static int PreviousDeparture(TransitTimetablesSetting s, TimetableSchedule sch, CustomPeakSchedule customSch, int schedule, int nowMinute)
        {
            int first = FirstDeparture(s, sch, schedule);
            int best = int.MinValue;
            for (int day = -1; day <= 1; day++)
            {
                int t = first + day * 1440;
                int dayEnd = t + 1440;
                int guard = 0;
                while (guard < 4000 && t < dayEnd)
                {
                    int minute = Mod1440(t);
                    if (!InService(s, schedule, minute)) break; // left the operating window -> this block is done
                    if (t < nowMinute) best = t;                // latest in-window departure strictly before now...
                    else break;                                 // ...t reached now; nothing later in the scan is earlier
                    t += IntervalFor(s, sch, customSch, minute, schedule);
                    guard++;
                }
            }
            return best;
        }

        // The day's departures listed FROM the first departure (printed-timetable style), as minute-of-day 0..1439,
        // stopping at the operating-window boundary. Fills up to `count`; returns how many.
        public static int DayFromFirst(TransitTimetablesSetting s, TimetableSchedule sch, CustomPeakSchedule customSch, int schedule, int[] outMin, int count)
        {
            int t = FirstDeparture(s, sch, schedule);
            int n = 0, guard = 0;
            while (n < count && guard < 4000)
            {
                int minute = Mod1440(t);
                outMin[n++] = minute;
                int next = t + IntervalFor(s, sch, customSch, minute, schedule);
                if (!InService(s, schedule, Mod1440(next))) break; // reached the window boundary
                t = next;
                guard++;
            }
            return n;
        }

        // The next `count` departures at/after nowMinute (as minute-of-day 0..1439), schedule-aware. Steps via
        // NextDeparture so night/day operating windows and midnight wrap are handled. Returns how many filled.
        public static int Upcoming(TransitTimetablesSetting s, TimetableSchedule sch, CustomPeakSchedule customSch, int schedule, int nowMinute, int[] outMin, int count)
        {
            int t = NextDeparture(s, sch, customSch, schedule, nowMinute);
            int n = 0, guard = 0;
            while (n < count && guard < 4000)
            {
                outMin[n++] = Mod1440(t);
                int next = NextDeparture(s, sch, customSch, schedule, t + 1);
                if (next <= t) break; // safety: schedule not advancing
                t = next;
                guard++;
            }
            return n;
        }

        // Round-trip time of one vehicle over the whole line, in in-game minutes. `unitMinutes` is the runtime
        // route-unit->minute scale from TimebaseSystem (vanilla ~0.3296; smaller under a stretched day).
        public static float RoundTripMinutes(float stableDurationUnits, float unitMinutes) => stableDurationUnits * unitMinutes;

        // Vehicles needed to sustain the given headway = ceil(round-trip / interval), at least 1.
        public static int DerivedFleet(float stableDurationUnits, int intervalMinutes, float unitMinutes)
        {
            intervalMinutes = Pos(intervalMinutes);
            int fleet = (int)Math.Ceiling(RoundTripMinutes(stableDurationUnits, unitMinutes) / intervalMinutes);
            return fleet < 1 ? 1 : fleet;
        }

        // ===== Real-travel-time correction (issue: the game's path estimate undershoots real loop time) =====
        // The correction is a DIMENSIONLESS factor = (real loop) / (estimated loop). It multiplies the estimate to
        // recover the real value, and it is RT-INVARIANT (both quantities are frame-based, so a stretched clock cancels).

        // COLD-START prior for a line with no measured loops yet. Live data from one city (2026-07) showed the undershoot
        // rises with stop density (stops per loop-minute): ~1.7x on sparse lines, ~2.5x on stop-dense ones, plateauing
        // near ~2.5x. Rough linear fit with an intercept near 1 (a hypothetical stopless express would match the
        // estimate). RT-invariant: uses the FIXED vanilla unit->minute constant, not the live scale, so a slow-time mod
        // does not move the prior. The caller clamps the result.
        public static float DensityPriorRatio(int stops, float stableDurationUnits)
        {
            if (stops <= 0 || stableDurationUnits <= 1f) return 1f;
            const float kVanillaUnitMinutes = 0.32958984f; // 675/2048, the vanilla 262144-frames/day scale (fixed reference)
            float estMinutes = stableDurationUnits * kVanillaUnitMinutes;
            if (estMinutes < 0.01f) return 1f;
            float density = stops / estMinutes;            // stops per reference-minute (matches how the 7.7 slope was fit)
            float r = 1.1f + 7.7f * density;               // linear fit over the OBSERVED density range (0.08-0.18)
            // The live data PLATEAUED near ~2.5x — the undershoot stops climbing once stops are close — and we have no
            // evidence above that, so a very dense line must NOT be linearly extrapolated toward the 4x safety clamp on a
            // cold start. Cap the PRIOR at the observed plateau; live measurement (which may legitimately exceed it)
            // takes over after a few loops. The caller still clamps.
            return r > 2.6f ? 2.6f : r;
        }

        // Clamp a correction factor into a safe range. For FLEET sizing it is grow-only (>= 1): never cut a line's
        // vehicle count below the estimate on a possibly-noisy low reading, which would strand passengers. For the
        // schedule/offsets a genuinely fast line may legitimately post EARLIER than the estimate, so the floor is 0.5.
        // Both are capped at 4x so a bad measurement can never blow the numbers up.
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
