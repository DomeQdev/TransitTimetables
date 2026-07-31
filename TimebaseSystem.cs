using System;
using System.Reflection;
using Game;
using Game.Simulation;
using Unity.Entities;

namespace TransitTimetables
{
    // Supplies the ONE runtime-variable quantity the timetable math needs: D = sim-frames per in-game DAY.
    //
    // Vanilla: 1 day = TimeSystem.kTicksPerDay = 262144 frames. Slow-time / "Realistic Trips" mods (Time2Work) STRETCH
    // the day: Time2Work sets its own kTicksPerDay = floor(slow_time_factor * 262144) (3.5x by default -> 917504) and
    // overwrites TimeSystem.m_Time so normalizedTime stays a 0..1 fraction of the now-longer day. So the time-of-day
    // read (normalizedTime*1440) is already correct under any such mod; ONLY the minute<->frame scale is wrong when it
    // is hardcoded to 262144 (buses depart early, stop offsets & derived fleets inflate ~3.5x).
    //
    // OPT-IN: gated by Setting.RealisticTripsCompat (default OFF). When OFF, D is pinned to EXACTLY 262144, so every
    // consumer produces bit-identical numbers to the pre-fix hardcoded constants — a true no-op. When ON with no
    // slow-time mod present, D still measures to 262144 (snap-to-vanilla), so those users are also unchanged.
    //
    // When ON, D is learned two ways, both WITHOUT any compile-time dependency on Time2Work:
    //   (1) A best-effort REFLECTION seed (reads Time2Work's public static kTicksPerDay) so D is correct from frame one,
    //       INCLUDING while the sim is PAUSED (the primary timetable-editing state), when (2) cannot run. Absent mod =>
    //       returns nothing => vanilla fallback. No hard reference; a renamed/absent member just falls through.
    //   (2) The OBSERVED SLOPE of two quantities the game already exposes, authoritative and mod-agnostic:
    //         F = SimulationSystem.frameIndex   (monotonic frame counter, unaffected by clock stretch)
    //         N = TimeSystem.normalizedTime     (0..1 sawtooth of the day, slope 1/D away from midnight)
    //       Locally D = dF/dN. Invariant to the speed slider (frames/sec, not frames/day) and to Time2Work's
    //       daysPerMonth (calendar-only; GetTimeOfDay uses % kTicksPerDay).
    //
    // At D=262144 the getters are BIT-IDENTICAL to the deleted consts (86400/262144 = 675/2048 is dyadic):
    // FramesPerMinute -> 182.0444489 (0x43360B61), UnitMinutes -> 0.32958984375 (0x3EA8C000). The "60" sim-frames per
    // route "duration unit" is fixed and stays baked into UnitMinutes; only the day length (262144) becomes runtime.
    public partial class TimebaseSystem : GameSystemBase
    {
        private const double VANILLA     = 262144.0;   // 1x day length in frames
        private const double D_MIN       = 65536.0;    // 0.25x  — faster is a sampling glitch, reject
        private const double D_MAX       = 4194304.0;  // 16x    — slower is a sampling glitch, reject
        private const uint   MIN_FRAMES  = 2048u;      // accumulate >= this many frames before trusting a window
        private const double MIN_DN      = 2e-4;       // ...and >= this much clock progress (float32 ULP guard)
        private const double SNAP_TOL    = 0.01;       // within 1% of vanilla -> pin to EXACTLY 262144 (bit-exact parity)
        private const double EMA_ALPHA   = 0.30;       // smoothing for small steady-state jitter
        private const double REGIME_JUMP = 0.05;       // > 5% from current D -> a real regime change: snap, don't EMA
        private const int    REFLECT_TRIES = 16;       // bounded reflection attempts while unseeded (then measurement owns it)

        private SimulationSystem m_Sim;
        private TimeSystem m_Time;

        private double m_D = VANILLA;   // current best estimate of frames/day; all getters derive from this
        private bool m_ReflectSeeded;   // got a trustworthy D from a slow-time mod's kTicksPerDay
        private bool m_SlopeSeeded;     // at least one measured window has landed
        private uint m_Gen;             // bumps when D moves >5% so consumers can drop stale per-vehicle slots
        private int  m_ReflectTries;

        private bool m_HaveAnchor;
        private uint m_F0;
        private double m_N0;
        private uint m_LastHeartbeat;

        private bool m_NudgedRtOff;    // logged the "slow-time mod detected but compat is OFF" nudge once
        private int  m_OffDetectTries; // bounded RT-detection attempts while compat is off (then give up scanning)

        // ---- Public read-only surface consumed by the dispatch + UI systems ----
        public double TicksPerDay     => m_D;
        public float  FramesPerMinute => (float)(m_D / 1440.0);
        public float  UnitMinutes     => (float)(86400.0 / m_D);
        public bool   Seeded          => m_ReflectSeeded || m_SlopeSeeded;
        public uint   RegimeGeneration => m_Gen;

        private static bool CompatEnabled()
        {
            Setting s = Mod.ActiveSetting;
            return s != null && s.RealisticTripsCompat; // default OFF (pure vanilla clock) until the setting says otherwise
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Sim  = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Time = World.GetOrCreateSystemManaged<TimeSystem>();

            m_D = VANILLA;
            m_ReflectSeeded = false;
            m_SlopeSeeded = false;
            m_HaveAnchor = false;
            m_Gen = 0;
            m_ReflectTries = 0;
            m_NudgedRtOff = false;
            m_OffDetectTries = 0;

            // Correct-from-frame-one seed (works while paused, when OnUpdate cannot run). Best effort; vanilla otherwise.
            if (CompatEnabled())
            {
                m_ReflectTries++;
                TrySeedFromReflection();
            }

            Mod.log.Info($"[SelfTest] timebase OnCreate ticksPerDay={m_D:F0} fpm={FramesPerMinute:F2} " +
                         $"um={UnitMinutes:F5} src={(m_ReflectSeeded ? "reflect" : "default")} compat={CompatEnabled()}");
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 16;

        protected override void OnUpdate()
        {
            if (!CompatEnabled())
            {
                // Feature OFF -> behave EXACTLY like the pre-fix hardcoded constants (D == 262144, bit-identical getters).
                if (m_D != VANILLA)
                {
                    double old = m_D;
                    m_D = VANILLA;
                    if (Math.Abs(old - VANILLA) > REGIME_JUMP * old) m_Gen++;
                    Mod.log.Info($"[SelfTest] timebase off ticksPerDay=262144 fpm={FramesPerMinute:F2} um={UnitMinutes:F5} gen={m_Gen}");
                }
                m_ReflectSeeded = false;
                m_SlopeSeeded = false;
                m_HaveAnchor = false;
                m_ReflectTries = 0;

                // Compat is OFF, but a slow-time mod may still be installed. Detect it (bounded) and nudge ONCE so the
                // user knows to switch this on — WITHOUT changing any timing behaviour (m_D stays pinned to vanilla).
                if (!m_NudgedRtOff && m_OffDetectTries < REFLECT_TRIES)
                {
                    m_OffDetectTries++;
                    double d = TryReadDayLengthFrames();
                    if (d >= D_MIN && d <= D_MAX && Math.Abs(d - VANILLA) > SNAP_TOL * VANILLA)
                    {
                        m_NudgedRtOff = true;
                        Mod.log.Warn($"[SelfTest] slow-time mod detected (day ~{d:F0} frames vs vanilla {VANILLA:F0}) but " +
                                     "'Realistic Trips / slow-time compatibility' is OFF — turn it ON in the mod settings so timetables stay accurate.");
                    }
                }
                return;
            }

            // Best-effort reflection while we haven't locked on (covers a toggle flipped ON mid-game, or a slow-time mod
            // that initialized after us). Bounded so a vanilla session doesn't scan assemblies forever.
            if (!Seeded && m_ReflectTries < REFLECT_TRIES)
            {
                m_ReflectTries++;
                TrySeedFromReflection();
            }

            uint f = m_Sim.frameIndex;
            float n = m_Time.normalizedTime;

            if (!m_HaveAnchor) { Anchor(f, n); return; }

            uint dF = unchecked(f - m_F0);       // wrap-safe modulo 2^32 for any window < 2^32
            double dN = (double)n - m_N0;

            if (dF == 0u) return;                        // paused / no progress — keep the anchor, retry later
            if (dN < 0.0) { Anchor(f, n); return; }      // crossed midnight inside the window
            if (dF > 0.5 * m_D) { Anchor(f, n); return; } // huge frame gap (load / long stall): wrap-count ambiguous

            if (dF < MIN_FRAMES || dN < MIN_DN) return;  // accumulate against the SAME anchor to beat float32 ULP noise

            double candidate = dF / dN;
            Anchor(f, n);                                // independent, non-overlapping windows

            if (candidate < D_MIN || candidate > D_MAX) return; // one bad window never poisons the estimate

            if (Math.Abs(candidate - VANILLA) <= SNAP_TOL * VANILLA)
                candidate = VANILLA;                     // deterministic bit-identical vanilla parity

            if (!m_SlopeSeeded)
            {
                Commit(candidate, "seed");
                m_SlopeSeeded = true;
            }
            else if (Math.Abs(candidate - m_D) > REGIME_JUMP * m_D)
            {
                // Real day-length change (slow_time_factor toggled) OR a blended straddle window. Snap straight to it —
                // never crawl through an EMA intermediate that matches NEITHER regime. A straddle heals on the next
                // clean window; steady-state jitter is < 5% so this branch is never triggered by noise.
                Commit(candidate, "snap");
            }
            else
            {
                Commit(m_D + EMA_ALPHA * (candidate - m_D), "ema"); // smooth residual jitter
            }

            if (f - m_LastHeartbeat >= 16384u)
            {
                m_LastHeartbeat = f;
                Mod.log.Info($"[SelfTest] timebase heartbeat ticksPerDay={m_D:F0} fpm={FramesPerMinute:F2} " +
                             $"um={UnitMinutes:F5} seeded={Seeded}");
            }
        }

        private void Anchor(uint f, float n) { m_F0 = f; m_N0 = n; m_HaveAnchor = true; }

        private void Commit(double newD, string src)
        {
            double old = m_D;
            m_D = newD;
            // Only a MATERIAL change invalidates in-flight per-vehicle slots; sub-5% EMA nudges don't.
            if (Math.Abs(newD - old) > REGIME_JUMP * old) m_Gen++;
            if (src != "ema")
                Mod.log.Info($"[SelfTest] timebase {src} ticksPerDay={m_D:F0} fpm={FramesPerMinute:F2} " +
                             $"um={UnitMinutes:F5} gen={m_Gen}");
        }

        private void TrySeedFromReflection()
        {
            double reflected = TryReadDayLengthFrames();
            if (reflected < D_MIN || reflected > D_MAX)
                return;
            if (Math.Abs(reflected - VANILLA) <= SNAP_TOL * VANILLA)
                reflected = VANILLA;
            double old = m_D;
            m_D = reflected;
            m_ReflectSeeded = true;
            if (Math.Abs(reflected - old) > REGIME_JUMP * old) m_Gen++;
            Mod.log.Info($"[SelfTest] timebase reflect ticksPerDay={m_D:F0} fpm={FramesPerMinute:F2} " +
                         $"um={UnitMinutes:F5} gen={m_Gen}");
        }

        // Time2Work ("Realistic Trips") Time2WorkTimeSystem.kTicksPerDay (public static int = floor(slow_time_factor *
        // 262144)). Returns 0 if the mod / type / member is absent so the caller falls back to vanilla. Best effort;
        // any reflection failure is swallowed. Scans all loaded assemblies by type name, so the assembly name is not
        // needed and cross-version renames of the assembly don't matter.
        private static double TryReadDayLengthFrames()
        {
            string[] typeNames =
            {
                "Time2Work.Time2WorkTimeSystem",
                "Time2Work.Systems.Time2WorkTimeSystem",
                "NightShift.Time2WorkTimeSystem",
            };
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            try
            {
                Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < asms.Length; i++)
                {
                    Type t = null;
                    for (int c = 0; c < typeNames.Length && t == null; c++)
                    {
                        try { t = asms[i].GetType(typeNames[c], throwOnError: false); }
                        catch { t = null; }
                    }
                    if (t == null) continue;

                    FieldInfo fi = t.GetField("kTicksPerDay", F);
                    if (fi != null) { double d = ToPositive(fi.GetValue(null)); if (d > 0) return d; }
                    PropertyInfo pi = t.GetProperty("kTicksPerDay", F);
                    if (pi != null) { double d = ToPositive(pi.GetValue(null)); if (d > 0) return d; }
                }
            }
            catch { /* any reflection failure -> vanilla fallback */ }
            return 0.0;
        }

        private static double ToPositive(object v)
        {
            if (v is int i && i > 0) return i;
            if (v is long l && l > 0) return l;
            if (v is uint u && u > 0) return u;
            return 0.0;
        }
    }
}
