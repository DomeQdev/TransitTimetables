import { useState, useEffect, useRef, useContext } from "react";
import { bindValue, useValue, trigger } from "cs2/api";
import { FloatingButton, ConfirmationDialog, DialogStack, DialogContext } from "cs2/ui";
import { useT } from "mods/i18n";
import ICON from "../transittimetables-icon.svg";

const G = "TransitParams";

// Selected LINE service plan (drives the editor injected into the line panel).
const selHas$ = bindValue<boolean>(G, "selHas", false);
const selTtEnabled$ = bindValue<boolean>(G, "selTtEnabled", false);
// THE PLAN: vehicles per time-of-day window. These are the only numbers the player sets now — the headway is what
// comes out the other end, not what goes in.
const selPeakVeh$ = bindValue<number>(G, "selPeakVeh", 6);
const selOffPeakVeh$ = bindValue<number>(G, "selOffPeakVeh", 4);
const selNightVeh$ = bindValue<number>(G, "selNightVeh", 2);
// ...and what the mod made of them. All three are produced by the dispatch and merely displayed here.
const selTtFleet$ = bindValue<number>(G, "selTtFleet", 0);     // the count it applied
const selTtServing$ = bindValue<number>(G, "selTtServing", 0); // how many are really out
const selTtHeadway$ = bindValue<number>(G, "selTtHeadway", 0); // resulting spacing, minutes (0 = not regulating yet)
const selTtNextMin$ = bindValue<number>(G, "selTtNextMin", -1);
const selTtRealInfo$ = bindValue<string>(G, "selTtRealInfo", "");
const selTtTerminus$ = bindValue<number>(G, "selTtTerminus", 0);
const selTtLayover$ = bindValue<number>(G, "selTtLayover", 0);
const selTtLayoverMin$ = bindValue<number>(G, "selTtLayoverMin", 0);
// Stop rules left on stops this line no longer serves — invisible on the stop board (no row exists for them), so the
// line panel is their only home. Same problem, same shape as Terminus B's orphan state.
const selTtRuleOrphans$ = bindValue<number>(G, "selTtRuleOrphans", 0);
// Per-line custom peak: enable + its own vehicle count + two hour windows.
const selCustomPeakEnabled$ = bindValue<boolean>(G, "selCustomPeakEnabled", false);
const selCustomPeakVeh$ = bindValue<number>(G, "selCustomPeakVeh", 8);
const selCustomPeakStart1$ = bindValue<number>(G, "selCustomPeakStart1", 7);
const selCustomPeakEnd1$ = bindValue<number>(G, "selCustomPeakEnd1", 9);
const selCustomPeakStart2$ = bindValue<number>(G, "selCustomPeakStart2", 16);
const selCustomPeakEnd2$ = bindValue<number>(G, "selCustomPeakEnd2", 18);

// Which windows apply + their hours, so the editor shows only relevant rows and communicates the times.
const selSchedule$ = bindValue<number>(G, "selSchedule", 2); // 0=Day, 1=Night, 2=DayAndNight
const peakHours$ = bindValue<string>(G, "peakHours", "");
const nightHours$ = bindValue<string>(G, "nightHours", "");

// Selected STOP arrivals board (drives the floating panel).
const selStopHas$ = bindValue<boolean>(G, "selStopHas", false);
const selStopBoard$ = bindValue<string>(G, "selStopBoard", "[]");
const autoOpen$ = bindValue<number>(G, "autoOpen", 0);
// Selected VEHICLE spacing status.
const selVehInfo$ = bindValue<string>(G, "selVehInfo", "");
// One-time notice. A counter rather than a flag, so a late-mounting host still sees the change.
const noticeSeq$ = bindValue<number>(G, "noticeSeq", 0);
// Last counter value we have RAISED a dialog for. Module-level, NOT a useRef inside the component, and this matters
// twice over:
//  1. `useRef(seq)` would seed the baseline from whatever the counter already holds at first render. If C# bumped it
//     before the Game module mounted, the very first render would see 1 and treat it as already-seen — no dialog,
//     ever, while C# sat waiting for an answer that could not come. That is the exact opposite of the late-mount
//     guarantee the counter exists to provide.
//  2. It has to OUTLIVE the component. The C# counter is static and survives across city loads within one session,
//     so a per-mount baseline of 0 would re-fire the dialog every time the host remounted.
let _noticeSeen = 0;

// Module-level open state for the floating stop panel.
let _open = false;
const _subs = new Set<() => void>();
function setOpen(v: boolean) {
    if (_open !== v) { _open = v; _subs.forEach((f) => f()); }
}
function useOpen() {
    const [, force] = useState(0);
    useEffect(() => {
        const f = () => force((x) => x + 1);
        _subs.add(f);
        return () => { _subs.delete(f); };
    }, []);
    return _open;
}

const stepBtn = {
    cursor: "pointer", width: "24rem", height: "22rem", fontSize: "14rem", color: "white",
    background: "rgba(255,255,255,0.12)", borderRadius: "4rem",
} as const;

// Coarse step (±1h on a window edge, ±5 on a vehicle count) — wider so a two-character label fits, smaller type so it
// sits level with the ± glyphs. Paired with stepBtn everywhere: coarse outside, fine inside, value in the middle.
const stepBtnCoarse = {
    cursor: "pointer", width: "30rem", height: "22rem", fontSize: "11rem", color: "white",
    background: "rgba(255,255,255,0.12)", borderRadius: "4rem",
} as const;

// Native close button: the game's Close glyph as a mask tinted with the panel text colour (matches the native panels,
// which use url(Media/Glyphs/...) masks — not a literal "X"). pointerEvents:auto for reliable clicks.
const CloseGlyph = ({ onClick }: { onClick: () => void }) => (
    <button
        onClick={onClick}
        style={{ cursor: "pointer", width: "24rem", height: "24rem", border: "none", background: "transparent", padding: 0, pointerEvents: "auto" } as any}
    >
        <div style={{
            width: "24rem", height: "24rem", margin: "auto", backgroundColor: "var(--textColor)",
            maskImage: "url(Media/Glyphs/Close.svg)", WebkitMaskImage: "url(Media/Glyphs/Close.svg)",
            maskSize: "contain", WebkitMaskSize: "contain", maskRepeat: "no-repeat", WebkitMaskRepeat: "no-repeat",
            maskPosition: "center", WebkitMaskPosition: "center",
        } as any} />
    </button>
);

// One window's VEHICLE COUNT. ±5 outside, ±1 inside — the same coarse/fine idiom the hour windows use, sized for the
// range that actually matters: a city line runs somewhere between one and about forty vehicles, so ±5 crosses that
// range in a handful of clicks while ±1 still lands on any exact number. Floors at 1: "none in this window" is what
// the line's own day/night schedule is for, and a zero would ask the game for an undefined vehicle interval.
const CountRow = ({ label, hours, value$, trig }: { label: string; hours?: string; value$: any; trig: string }) => {
    const t = useT();
    const v = useValue(value$) as number;
    const set = (nv: number) => trigger(G, trig, Math.max(1, Math.round(nv)));
    return (
        <div style={{ display: "flex", alignItems: "center", padding: "3rem 0" }}>
            <div style={{ flex: 1 }}>
                <div style={{ fontSize: "13rem" }}>{label}</div>
                {hours ? <div style={{ fontSize: "10rem", opacity: 0.5 }}>{hours}</div> : null}
            </div>
            {/* Margins, not `gap` (cohtml has no flex gap). */}
            <button style={{ ...stepBtnCoarse, marginRight: "5rem" }} onClick={() => set(v - 5)}>−5</button>
            <button style={stepBtn} onClick={() => set(v - 1)}>−</button>
            <div style={{ width: "54rem", textAlign: "center", fontSize: "13rem" }}>
                {Math.round(v)} {t("vehiclesUnit", "veh.")}
            </div>
            <button style={stepBtn} onClick={() => set(v + 1)}>+</button>
            <button style={{ ...stepBtnCoarse, marginLeft: "5rem" }} onClick={() => set(v + 5)}>+5</button>
        </div>
    );
};

// Hour-of-day steps WRAP (mod 24) so a window can cross midnight (e.g. a night-shift line 22 -> 06). The C# trigger
// clamps to 0..23 anyway.
const wrapHr = (v: number) => ((Math.round(v) % 24) + 24) % 24;
const hhmm = (h: number) => (Math.round(h) < 10 ? "0" : "") + Math.round(h) + ":00";

// One custom-peak WINDOW: a start-hour and end-hour range, each stepped ±1h with wrap.
const WindowRow = ({ label, start$, end$, trigStart, trigEnd }:
    { label: string; start$: any; end$: any; trigStart: string; trigEnd: string }) => {
    const t = useT();
    const s = useValue(start$) as number;
    const e = useValue(end$) as number;
    return (
        <div style={{ display: "flex", alignItems: "center", padding: "3rem 0" }}>
            <div style={{ flex: 1, fontSize: "13rem" }}>{label}</div>
            <button style={stepBtn} onClick={() => trigger(G, trigStart, wrapHr(s - 1))}>−</button>
            <div style={{ width: "44rem", textAlign: "center", fontSize: "13rem" }}>{hhmm(s)}</div>
            <button style={stepBtn} onClick={() => trigger(G, trigStart, wrapHr(s + 1))}>+</button>
            <div style={{ padding: "0 6rem", fontSize: "11rem", opacity: 0.6 }}>{t("to", "to")}</div>
            <button style={stepBtn} onClick={() => trigger(G, trigEnd, wrapHr(e - 1))}>−</button>
            <div style={{ width: "44rem", textAlign: "center", fontSize: "13rem" }}>{hhmm(e)}</div>
            <button style={stepBtn} onClick={() => trigger(G, trigEnd, wrapHr(e + 1))}>+</button>
        </div>
    );
};

// The "real loop" line. C# sends NUMBERS, not a sentence, and the whole sentence is built here from a per-language
// template — this was the last user-facing text in the mod that was assembled in C# and therefore English in all 11
// translated languages. It cannot be done by translating fragments and gluing them in English order, because clause
// order differs between languages, so each language gets a complete template with {placeholders}.
//
// It matters more than it used to. The headway a player gets for N vehicles is loop/N, so this figure is the entire
// explanation of why ten vehicles buy the interval they buy — and of why a line whose route the game underestimates by
// 2x needs about twice the vehicles a player would guess.
const RealInfo = ({ raw }: { raw: string }) => {
    const t = useT();
    if (!raw) return null;
    let d: { real: number; est: number; corr: string; meas: boolean; mode: string; n: number; laps?: number; need?: number; stage?: number; need2?: number; wlaps?: number };
    try { d = JSON.parse(raw); } catch { return null; }
    if (!d || typeof d.real !== "number") return null;
    const vars = { real: d.real, est: d.est, corr: d.corr, n: d.n, laps: d.laps ?? 0, need: d.need ?? 0, need2: d.need2 ?? 0, wlaps: d.wlaps ?? 0 };
    // THREE STAGES, matching the ladder in LineCorrection. The point is that the loop figure — and therefore the
    // headway derived from it — comes from a DIFFERENT estimator at each stage, and the player deserves to know which:
    //   0  nothing measured yet -- the spacing is derived from the game's own estimate, not from anything observed
    //   1  the conservative anchor is driving it. It errs LOW on purpose, so the headway will usually WIDEN when the
    //      median takes over. Saying so here is the whole reason this stage has its own text.
    //   2  the median is driving it. Still a rolling window, so it can drift -- "measured", never "final".
    const stage = d.stage ?? (d.meas ? 2 : 0);
    const head =
        stage === 0 ? t("loopMeasuring", "Real loop is still being measured ({laps} of {need} laps).", vars)
        : stage === 1 ? t("loopRefining", "Real loop is about {real} min, still refining ({wlaps} of {need2} laps).", vars)
        : t("loopMeasured", "Real loop is {real} min, measured ({corr}x the {est}-min estimate).", vars);
    const tail =
        d.mode === "prov" ? t("provisioning", "Running {n} vehicles on it.", vars)
        : d.mode === "notmine" ? t("notSetByMod", "This mod is not setting this line's vehicle count.", vars)
        : t("sizingSoon", "Setting this line's vehicle count as soon as its duration estimate settles.");
    // Only stage 1 gets a caveat, and it is specifically about the spacing WIDENING. Stage 0 already says it is
    // measuring; stage 2 has nothing left to warn about.
    const caveat = stage === 1
        ? " " + t("headwayMayWiden", "That loop is deliberately cautious, so the spacing may widen once measuring completes.", vars)
        : "";
    return (
        <div style={{ fontSize: "11rem", color: "rgb(224, 186, 120)", marginBottom: "6rem", lineHeight: 1.35 }}>
            {head + " " + tail + caveat}
        </div>
    );
};

// One native-looking info row: label on the left, value on the right. Mirrors the game's own Owner / Destination /
// Line rows in the same panel rather than sitting under them as a paragraph of loose text.
const InfoRow = ({ label, children }: { label: string; children: any }) => (
    <div style={{ display: "flex", alignItems: "center", padding: "4rem 14rem", minHeight: "28rem" }}>
        <div style={{ flex: 1, fontSize: "14rem", color: "var(--textColor)" } as any}>{label}</div>
        {children}
    </div>
);

// The status pill. Colour IS the message — you should be able to read a vehicle's state from across the panel without
// parsing a sentence — so each state gets a distinct, conventional one:
//   green  correctly spaced from the vehicle in front
//   amber  bunched up behind it, or trailing a gap
//   grey   standing at a timing point waiting for its slot in the spacing (the normal state of a terminus)
const Chip = ({ text, tone }: { text: string; tone: "red" | "green" | "grey" | "amber" }) => {
    const bg = tone === "red" ? "rgba(200, 70, 60, 0.95)"
        : tone === "green" ? "rgba(60, 160, 90, 0.95)"
        : tone === "amber" ? "rgba(214, 145, 45, 0.95)"
        : "rgba(120, 128, 140, 0.9)";
    return (
        <div style={{
            padding: "2rem 10rem", borderRadius: "10rem", background: bg,
            fontSize: "12rem", color: "white", whiteSpace: "nowrap",
        } as any}>
            {text}
        </div>
    );
};

// Injected into the native PublicTransportVehicleSection, so it shows when a bus/train/tram is selected. Renders
// nothing unless that vehicle is on a managed line, so it is inert on freight and on unmanaged lines.
//
// THERE IS NO "LATE" HERE, and that is a deliberate position rather than a missing feature. Nothing was promised for a
// particular minute, so nothing can miss it. What can honestly be said is whether this vehicle is correctly spaced
// from the one in front of it — which is the thing the mod is actually managing — and, when it is standing still,
// whether that is the regulation holding it or simply passengers boarding.
const VehicleSpacing = ({ raw }: { raw: string }) => {
    const t = useT();
    if (!raw) return null;
    let d: { held: boolean; hold: number; term: boolean; haveGap: boolean; gap: number; h: number; stage: number };
    try { d = JSON.parse(raw); } catch { return null; }
    if (!d) return null;

    let tone: "red" | "green" | "grey" | "amber" = "green";
    let text: string;
    if (d.held) {
        // Being held at a timing point. At the terminus that is simply what a terminus does; at Terminus B it is the
        // same mechanism at the other end of the line. Neither is a fault, so neither gets a warning colour.
        tone = "grey";
        const vars = { n: Math.max(0, Math.round(d.hold)) };
        text = d.term
            ? t("vehChipWaitTerm", "at terminus · departs in {n} min", vars)
            : t("vehChipWaitB", "at Terminus B · departs in {n} min", vars);
    } else if (d.haveGap && d.h > 0) {
        // Not held, and we have seen it leave the terminus at least once, so we know the gap it actually achieved.
        // Compared against the line's target headway rather than reported bare: "7 minutes" means nothing without
        // knowing whether the line is aiming for 6 or for 20.
        const vars = { n: Math.round(d.gap), h: Math.round(d.h) };
        const ratio = d.gap / d.h;
        if (ratio < 0.75) { tone = "amber"; text = t("vehChipBunched", "bunched — {n} min behind (target {h})", vars); }
        else if (ratio > 1.25) { tone = "amber"; text = t("vehChipGap", "trailing a gap — {n} min (target {h})", vars); }
        else { tone = "green"; text = t("vehChipEven", "evenly spaced — {n} min", vars); }
    } else {
        // In service but never yet observed leaving the terminus: it joined mid-route from the depot, which is where
        // the game spawns line vehicles. It is not mis-spaced, we simply have nothing to say about it yet.
        tone = "grey";
        text = t("vehChipRunning", "in service");
    }
    // Same honesty as the line panel: a line still measuring its loop does not yet know its own target, so a firm
    // comparison against it would be overstated. One short line under the row rather than glued onto the chip, which
    // has to stay scannable.
    const caveat = d.stage < 2
        ? t("vehSpacingNote", "This line is still measuring its real loop, so its spacing target will shift.")
        : "";
    return (
        <>
            <InfoRow label={t("vehStatusLabel", "Spacing")}>
                <Chip tone={tone} text={text} />
            </InfoRow>
            {caveat ? (
                <div style={{ fontSize: "11rem", color: "rgb(224, 186, 120)", padding: "0 14rem 6rem", lineHeight: 1.35 }}>
                    {caveat}
                </div>
            ) : null}
        </>
    );
};

export const VehicleScheduleRow = () => {
    const raw = useValue(selVehInfo$) as string;
    return <VehicleSpacing raw={raw} />;
};

export const TimetableEditor = () => {
    const has = useValue(selHas$);
    const on = useValue(selTtEnabled$);
    const fleet = useValue(selTtFleet$) as number;
    const serving = useValue(selTtServing$) as number;
    const headway = useValue(selTtHeadway$) as number;
    const nextMin = useValue(selTtNextMin$) as number;
    const realInfo = useValue(selTtRealInfo$) as string;
    const terminus = useValue(selTtTerminus$) as number;
    const layover = useValue(selTtLayover$) as number;
    const layoverMin = useValue(selTtLayoverMin$) as number;
    const ruleOrphans = useValue(selTtRuleOrphans$) as number;
    const customPeakOn = useValue(selCustomPeakEnabled$) as boolean;
    const schedule = useValue(selSchedule$) as number; // 0=Day, 1=Night, 2=DayAndNight
    const peakHrs = useValue(peakHours$) as string;
    const nightHrs = useValue(nightHours$) as string;
    const t = useT();
    if (!has) return null;
    // Only show the windows the line actually runs: day-only → Peak+Off-peak, night-only → Night, both → all.
    const showDay = schedule === 0 || schedule === 2;
    const showNight = schedule === 1 || schedule === 2;

    return (
        <div style={{ borderTop: "1rem solid rgba(255,255,255,0.15)", padding: "8rem 14rem 10rem", color: "white" }}>
            <div style={{ display: "flex", alignItems: "center", marginBottom: "6rem" }}>
                <div style={{ flex: 1, fontSize: "var(--fontSizeS)", fontWeight: "bold", textTransform: "uppercase", color: "var(--textColor)", opacity: 0.9 } as any}>{t("servicePlan", "SERVICE PLAN")}</div>
                <button
                    onClick={() => trigger(G, "setSelTtEnabled", !on)}
                    style={{
                        cursor: "pointer", padding: "4rem 12rem", borderRadius: "4rem", fontSize: "12rem", color: "white",
                        background: on ? "rgba(60, 160, 90, 0.95)" : "rgba(120, 120, 120, 0.6)",
                    }}
                >
                    {on ? t("on", "ON") : t("off", "OFF")}
                </button>
            </div>
            {on && (
                <>
                    {/* WHAT THE PLAN IS PRODUCING, top line: the count in force and the spacing it buys. The headway is
                        the OUTPUT of this mod, so it belongs here and not among the controls. "~" because it is a
                        consequence of a measured loop, not a promise. */}
                    <div style={{ fontSize: "12rem", color: "rgb(120, 210, 130)", marginBottom: "2rem" }}>
                        {headway > 0
                            ? t("planNow", "{f} vehicles · every ~{i} min", { f: fleet, i: headway })
                            : t("planNowNoHeadway", "{f} vehicles", { f: fleet })}
                    </div>
                    {/* The gap between "applied" and "actually out there" is the single most useful diagnostic during a
                        peak ramp-up: it is the difference between the mod ignoring you and the depot still delivering.
                        Only shown when they differ, so the row is silent in the normal case. */}
                    <div style={{ fontSize: "12rem", opacity: 0.7, marginBottom: "6rem" }}>
                        {serving !== fleet ? t("planRunning", "{r} of {f} running", { r: serving, f: fleet }) + " · " : ""}
                        {nextMin >= 0
                            ? t("planNext", "next departure in {n} min", { n: nextMin })
                            : t("planNextUnknown", "no departure observed yet")}
                    </div>
                    <RealInfo raw={realInfo} />
                    {/* The terminus is where the whole thing happens: it is the stop where vehicles are held to even
                        the line out, and where a surplus vehicle finishes its loop before going back to the depot.
                        Without a chosen one the dispatch silently uses the first stop with a boarding slot, which is
                        route order, not a decision. Two different messages on purpose — never choosing is a gap,
                        having a choice discarded is a loss. */}
                    {terminus !== 0 ? (
                        <div style={{ fontSize: "11rem", color: "rgb(232, 168, 96)", marginBottom: "6rem", lineHeight: 1.35 }}>
                            {terminus === 2
                                ? t("terminusLost", "This line's terminus is no longer on its route, so the mod has fallen back to the first stop. Select a stop this line serves and set it as the terminus.")
                                : t("terminusNone", "No terminus is set for this line, so the mod evens its vehicles out at the first stop on the route. Select a stop this line serves and set it as the terminus to choose where they wait.")}
                        </div>
                    ) : null}
                    {/* A Terminus B the dispatch is NOT applying. State 3 in particular has no other home: the stop no
                        longer lists this line, so the board cannot offer removal and the setting would sit invisible
                        in the save, reactivating if the route were edited back. Remove is offered for both. */}
                    {layover === 2 || layover === 3 ? (
                        <div style={{ fontSize: "11rem", color: "rgb(232, 168, 96)", marginBottom: "6rem", lineHeight: 1.35 }}>
                            {layover === 3
                                ? t("layoverOrphan", "This line has a Terminus B ({n} min minimum layover) set on a stop that is no longer on its route, so it is not being applied.", { n: layoverMin })
                                : t("layoverBlocked", "This line's Terminus B is set on the stop the mod now uses as its terminus, so it is not being applied. Move the terminus, or remove Terminus B.", { n: layoverMin })}
                            <div>
                                <button
                                    onClick={() => trigger(G, "clearSelLayover")}
                                    style={{ marginTop: "4rem", cursor: "pointer", padding: "3rem 10rem", borderRadius: "4rem", fontSize: "11rem", color: "white", background: "rgba(150, 70, 70, 0.9)", pointerEvents: "auto" } as any}
                                >
                                    {t("layoverRemove", "Remove Terminus B")}
                                </button>
                            </div>
                        </div>
                    ) : null}
                    {/* Boarding rules stranded on stops that left the route. Nothing else can reach them: the stop
                        board only draws rows for stops the line still serves, so without this the rule would sit
                        invisible in the save and come back to life the moment the route was edited back. */}
                    {ruleOrphans > 0 ? (
                        <div style={{ fontSize: "11rem", color: "rgb(232, 168, 96)", marginBottom: "6rem", lineHeight: 1.35 }}>
                            {t("ruleOrphan", "This line has boarding rules set on {n} stop(s) that are no longer on its route, so they are not being applied.", { n: ruleOrphans })}
                            <div>
                                <button
                                    onClick={() => trigger(G, "clearSelRuleOrphans")}
                                    style={{ marginTop: "4rem", cursor: "pointer", padding: "3rem 10rem", borderRadius: "4rem", fontSize: "11rem", color: "white", background: "rgba(150, 70, 70, 0.9)", pointerEvents: "auto" } as any}
                                >
                                    {t("ruleOrphanRemove", "Remove them")}
                                </button>
                            </div>
                        </div>
                    ) : null}
                    {showDay ? <CountRow label={t("peakVehicles", "Peak")} hours={peakHrs} value$={selPeakVeh$} trig="setSelPeakVeh" /> : null}
                    {showDay ? <CountRow label={t("offPeakVehicles", "Off-peak")} hours={t("otherHours", "other hours")} value$={selOffPeakVeh$} trig="setSelOffPeakVeh" /> : null}
                    {showNight ? <CountRow label={t("nightVehicles", "Night")} hours={nightHrs} value$={selNightVeh$} trig="setSelNightVeh" /> : null}
                    {/* Per-line custom peak: two windows + its own vehicle count, overriding the global peak for THIS
                        line only. */}
                    <div style={{ marginTop: "6rem", borderTop: "1px solid rgba(255,255,255,0.12)", paddingTop: "4rem" }}>
                        <div style={{ display: "flex", alignItems: "center", padding: "3rem 0" }}>
                            <div style={{ flex: 1, fontSize: "13rem" }}>{t("customLinePeak", "Custom peak (this line)")}</div>
                            <button
                                style={{ ...stepBtn, width: "auto", padding: "0 8rem", background: customPeakOn ? "rgba(120,210,130,0.35)" : "rgba(255,255,255,0.12)" } as any}
                                onClick={() => trigger(G, "setSelCustomPeakEnabled", !customPeakOn)}>
                                {customPeakOn ? t("on", "ON") : t("off", "OFF")}
                            </button>
                        </div>
                        {customPeakOn ? (
                            <>
                                <WindowRow label={t("morningPeak", "Morning peak")} start$={selCustomPeakStart1$} end$={selCustomPeakEnd1$} trigStart="setSelCustomPeakStart1" trigEnd="setSelCustomPeakEnd1" />
                                <WindowRow label={t("eveningPeak", "Evening peak")} start$={selCustomPeakStart2$} end$={selCustomPeakEnd2$} trigStart="setSelCustomPeakStart2" trigEnd="setSelCustomPeakEnd2" />
                                <CountRow label={t("customPeakVehicles", "Custom peak vehicles")} value$={selCustomPeakVeh$} trig="setSelCustomPeakVeh" />
                            </>
                        ) : null}
                    </div>
                    <div style={{ fontSize: "11rem", opacity: 0.45, marginTop: "4rem" }}>
                        {t("terminusHint", "Select a stop to see when this line reaches it, and to set it as this line's terminus.")}
                    </div>
                </>
            )}
        </div>
    );
};

// The stop arrivals board — when every line here is next expected, projected from its live spacing.
const StopBoard = () => {
    const raw = useValue(selStopBoard$) as string;
    const t = useT();
    let board: Array<{ n: number; nm?: string; tt: boolean; term: boolean; h?: number; est?: boolean; lay?: number; a?: string; layOff?: boolean; rule?: number; d: string }> = [];
    try { board = JSON.parse(raw || "[]"); } catch { board = []; }
    const ttCount = board.filter((e) => e.tt).length;
    const termBtn = {
        cursor: "pointer", display: "block", width: "100%", padding: "7rem 12rem", borderRadius: "4rem",
        fontSize: "13rem", color: "white", pointerEvents: "auto", textAlign: "center",
    } as const;
    // Per-stop boarding rule. Codes match LineStopRule: 0 normal, 1 set-down only, 2 pick-up only, 3 technical.
    const ruleLabel = (r: number) =>
        r === 1 ? t("ruleDropOff", "drop-off only")
        : r === 2 ? t("rulePickUp", "pick-up only")
        : r === 3 ? t("ruleTechnical", "technical stop")
        : t("ruleBoth", "normal");
    // Two rows of two rather than one row of four: these labels are long in most languages (German's "nur Ausstieg"
    // is the short one), the panel is 360rem wide, and cohtml has no flex-wrap to fall back on.
    // A plain function, NOT a component: a component declared inside this render would be a new type on every render,
    // so React would unmount and remount all four buttons every time the board refreshes (which is every minute, and
    // on every edit). Returning the element directly keeps them as ordinary siblings.
    const ruleButton = (row: number, mode: number, active: boolean) => (
        <button
            onClick={() => trigger(G, "setStopRuleRow", row, mode)}
            style={{
                flex: 1, marginRight: "3rem", cursor: "pointer", padding: "3rem 4rem", borderRadius: "3rem",
                fontSize: "10rem", color: "white", pointerEvents: "auto",
                background: active ? "rgba(120, 210, 130, 0.35)" : "rgba(90, 100, 115, 0.9)",
            } as any}
        >
            {ruleLabel(mode)}
        </button>
    );
    return (
        <div style={{ padding: "8rem 0 12rem" }}>
            {board.length === 0 ? (
                <div style={{ padding: "0 14rem", fontSize: "12rem", opacity: 0.5 }}>{t("noLines", "No lines serve this stop.")}</div>
            ) : (
                board.map((e, i) => (
                    <div key={i} style={{ padding: "5rem 14rem", borderTop: i > 0 ? "1rem solid rgba(255,255,255,0.08)" : undefined }}>
                        <div style={{ display: "flex", alignItems: "center", fontSize: "13rem", fontWeight: "bold" }}>
                            <div style={{ flex: 1 }}>{e.nm ? e.nm : t("line", "Line {n}", { n: e.n })}</div>
                            {e.term ? <div style={{ fontSize: "11rem", color: "rgb(120, 210, 130)" }}>★ {t("terminusBadge", "terminus")}</div> : null}
                            {e.lay ? (
                                <div style={{ fontSize: "11rem", color: e.layOff ? "rgba(224, 186, 120, 0.5)" : "rgb(224, 186, 120)" }}>
                                    {/* NO GLYPH. This carried a U+23F8 pause symbol, which the game's UI font does
                                        not contain, so it drew a tofu box on every row (seen in two players'
                                        screenshots). The terminus badge's ★ (U+2605) renders because it predates the
                                        emoji blocks; anything from those blocks is a gamble here. The amber colour
                                        already distinguishes this badge from the green terminus one. */}
                                    {e.layOff ? t("layoverOff", "Terminus B (inactive)") : t("layoverBadge", "Terminus B")}
                                </div>
                            ) : null}
                            {e.rule ? (
                                <div style={{
                                    marginLeft: "6rem", fontSize: "11rem",
                                    color: e.rule === 3 ? "rgb(224, 140, 120)" : "rgb(150, 190, 230)",
                                }}>
                                    {ruleLabel(e.rule)}
                                </div>
                            ) : null}
                        </div>
                        {/* THE HEADLINE for a headway service is the headway, not a list of times: "every 7 minutes"
                            is what a passenger at a frequent stop actually needs, and it is the number the mod is
                            directly responsible for. The projected times below it answer "but when exactly". */}
                        {e.tt && e.h ? (
                            <div style={{ fontSize: "12rem", color: "rgb(120, 210, 130)" }}>
                                {t("everyMin", "every ~{n} min", { n: e.h })}
                            </div>
                        ) : null}
                        {/* At Terminus B arrival and departure differ: show both rows, arrivals first. Both are derived
                            from ONE projection in C#, so a departure can never print earlier than its own arrival. */}
                        {e.tt && e.lay && e.a ? (
                            <div style={{ fontSize: "12rem", color: "rgba(255,255,255,0.6)" }}>
                                {t("arrives", "arrives: {d}", { d: e.a })}
                            </div>
                        ) : null}
                        <div style={{ fontSize: "12rem", color: e.tt ? "rgba(255,255,255,0.75)" : "rgba(255,255,255,0.45)" }}>
                            {e.tt
                                ? (e.d ? t("expected", "expected: {d}", { d: e.d }) : t("noHeadway", "spacing not established yet"))
                                : t("notTimetabled", "not managed")}
                        </div>
                        {/* These are a PROJECTION from the current spacing, not a printed timetable, and while the mod
                            is still learning the line's real loop even the spacing is a guess. Say so rather than
                            printing them with the confidence of a departure board. */}
                        {e.tt && e.d ? (
                            <div style={{ fontSize: "11rem", opacity: 0.45 }}>
                                {e.est
                                    ? t("estimatedTimes", "estimated — this line's real loop is not measured yet")
                                    : t("projectedTimes", "projected from the current spacing")}
                            </div>
                        ) : null}
                        {/* The Terminus B stepper: same ±1/±10 idiom as every other numeric control, absolute value
                            sent. Stepping down to 0 removes Terminus B, same meaning as remove. */}
                        {e.tt && e.lay ? (
                            <div style={{ display: "flex", alignItems: "center", marginTop: "3rem", fontSize: "11rem" }}>
                                <div style={{ color: "rgb(224, 186, 120)" }}>{t("layoverMin", "min. layover: {n} min", { n: e.lay })}</div>
                                {[-10, -1, 1, 10].map(dv => (
                                    <button
                                        key={dv}
                                        onClick={() => trigger(G, "setLayoverRow", i, Math.max(0, Math.min(60, (e.lay ?? 0) + dv)))}
                                        style={{ marginLeft: "4rem", cursor: "pointer", padding: "2rem 7rem", borderRadius: "3rem", fontSize: "10rem", color: "white", background: "rgba(90, 100, 115, 0.9)", pointerEvents: "auto" } as any}
                                    >
                                        {dv > 0 ? "+" + dv : String(dv)}
                                    </button>
                                ))}
                                <button
                                    onClick={() => trigger(G, "setLayoverRow", i, 0)}
                                    style={{ marginLeft: "6rem", cursor: "pointer", padding: "2rem 7rem", borderRadius: "3rem", fontSize: "10rem", color: "white", background: "rgba(150, 70, 70, 0.9)", pointerEvents: "auto" } as any}
                                >
                                    {t("layoverClear", "remove")}
                                </button>
                            </div>
                        ) : null}
                        {e.tt && !e.term ? (
                            <div style={{ display: "flex", marginTop: "4rem" }}>
                                <button
                                    onClick={() => trigger(G, "setTerminusRow", i)}
                                    style={{ cursor: "pointer", padding: "3rem 10rem", borderRadius: "4rem", fontSize: "11rem", color: "white", background: "rgba(70, 110, 170, 0.9)", pointerEvents: "auto" } as any}
                                >
                                    {t("setTerminusThis", "Set as terminus")}
                                </button>
                                {/* A row can be terminus OR Terminus B, never both (the dispatch drops a B that lands
                                    on the effective terminus), so the offer only appears where it can stick. */}
                                {!e.lay ? (
                                    <button
                                        onClick={() => trigger(G, "setLayoverRow", i, -1)}
                                        style={{ marginLeft: "6rem", cursor: "pointer", padding: "3rem 10rem", borderRadius: "4rem", fontSize: "11rem", color: "white", background: "rgba(150, 120, 60, 0.9)", pointerEvents: "auto" } as any}
                                    >
                                        {t("setLayoverThis", "Set as Terminus B")}
                                    </button>
                                ) : null}
                            </div>
                        ) : null}
                        {/* Who may get on and off here, for THIS line only — a kerb shared with other lines keeps
                            working normally for them. Offered on EVERY stop the line serves, the terminus included —
                            a technical call at the end of a run is a normal operating pattern, and the terminus keeps
                            regulating the spacing either way (see LineStopRule). */}
                        {e.tt ? (
                            <div style={{ marginTop: "5rem" }}>
                                <div style={{ fontSize: "10rem", opacity: 0.5, marginBottom: "2rem" }}>
                                    {t("ruleLabel", "Passengers here")}
                                </div>
                                <div style={{ display: "flex" }}>
                                    {ruleButton(i, 0, !e.rule)}
                                    {ruleButton(i, 1, e.rule === 1)}
                                </div>
                                <div style={{ display: "flex", marginTop: "3rem" }}>
                                    {ruleButton(i, 2, e.rule === 2)}
                                    {ruleButton(i, 3, e.rule === 3)}
                                </div>
                                {/* The technical stop is the one mode with a consequence the player cannot see from
                                    the button: it severs the line for passengers, so no journey can pass this stop. */}
                                {e.rule === 3 ? (
                                    <div style={{ fontSize: "10rem", color: "rgb(224, 140, 120)", marginTop: "3rem", lineHeight: 1.3 }}>
                                        {t("ruleTechnicalNote", "Nobody may be aboard here: the last stop to get off is the one before. No journey on this line can pass this stop, and vehicles always call.")}
                                    </div>
                                ) : null}
                            </div>
                        ) : null}
                    </div>
                ))
            )}
            {ttCount > 0 && (
                <div style={{ padding: "10rem 14rem 2rem" }}>
                    {ttCount >= 2 ? (
                        <button
                            onClick={() => trigger(G, "setSelTerminusAll")}
                            style={{ ...termBtn, background: "rgba(90, 100, 115, 0.9)" } as any}
                        >
                            {t("setTerminusAll", "Set as terminus for all lines here")}
                        </button>
                    ) : null}
                    <div style={{ fontSize: "11rem", opacity: 0.45, marginTop: ttCount >= 2 ? "6rem" : "0" }}>
                        {t("setTerminusHint", "Vehicles are evened out at the terminus, and a surplus vehicle finishes its loop and retires here.")}
                    </div>
                </div>
            )}
        </div>
    );
};

export const TransitButton = () => {
    const t = useT();
    return <FloatingButton src={ICON} tooltipLabel={t("buttonTooltip", "Transit Timetables")} onSelect={() => setOpen(!_open)} />;
};

// The one-time "this mod works differently now" notice. Rendered with the GAME'S OWN dialog component pushed onto its
// dialog stack, so it looks and behaves exactly like a vanilla confirmation (same shell, backdrop, button theme and
// gamepad handling) while keeping our own localized strings and our own callbacks.
//
// Deliberately NOT the native appBindings.ShowConfirmationDialog path: that stores ONE global callback which any other
// dialog silently overwrites, and it delivers over a fire-and-forget event whose listener only exists while the Game
// screen is mounted — which is exactly when we fire.
//
// ONE BUTTON, and no opt-out. This is not a choice: the change has already happened and the dialog exists to explain
// it. Every dismissal path (the button, Escape, the X) therefore routes to the same answer, which merely records that
// it has been read. That is the opposite of the previous notice's mapping, and correct for the opposite situation —
// that one offered to turn something on, so a stray Escape had to mean "no".
export const MigrationNotice = () => {
    const seq = useValue(noticeSeq$) as number;
    const stack = useContext(DialogStack);
    const t = useT();
    useEffect(() => {
        if (seq === 0 || seq === _noticeSeen) return;
        if (!stack || typeof stack.showDialog !== "function") return;
        _noticeSeen = seq;   // only AFTER we know we can actually raise it
        stack.showDialog(
            <ConfirmationDialog
                title={t("noticeTitle", "Transit Timetables now works by vehicle count")}
                // message MUST be a plain string, despite the ReactNode type. The dialog pipes it through the game's
                // own text renderer — Children.toArray(msg).flatMap(e => ET(dc(i, e, "\n"))) — which expects strings
                // and splits paragraphs on "\n". Passing JSX renders the literal text "[div/]" instead of the body.
                message={[
                    t("noticeBody", "Until now you told this mod how often a line should run and it worked out how many vehicles that needed. It is the other way round now: you say how many vehicles run in the peak, off-peak and at night, and the mod keeps them evenly spaced by holding them a little longer at the terminus."),
                    t("noticeAsk", "Your existing lines have been converted — each one's old intervals became the number of vehicles that was already running to hold them, so nothing should change on the ground. There are no fixed departure times any more; a stop now shows how often a line comes and when it is next expected."),
                    t("noticeWhere", "You can change each line's vehicle counts in its info panel, and the minimum terminus layover in Options."),
                ].join("\n")}
                confirm={t("noticeKeep", "Got it")}
                cancellable={false}
                onConfirm={() => trigger(G, "noticeAnswer", true)}
                onCancel={() => trigger(G, "noticeAnswer", true)}
            />
        );
    }, [seq, stack, t]);
    return null;
};

// Kept mounted but unused by the notice above — see the DialogContext note. Retained because the dialog's content slot
// is the only way this mod can add a control to a native dialog, and the next notice may need one again.
export const NoticeOptOut = () => {
    const dlg = useContext(DialogContext);
    const t = useT();
    return (
        <div style={{ display: "flex", justifyContent: "center", marginTop: "4rem" }}>
            <button
                onClick={() => {
                    trigger(G, "noticeAnswer", true);
                    if (dlg && typeof dlg.onClose === "function") dlg.onClose();
                }}
                style={{
                    cursor: "pointer", padding: "5rem 14rem", borderRadius: "4rem", fontSize: "12rem",
                    color: "white", opacity: 0.75, background: "rgba(90, 100, 115, 0.9)", pointerEvents: "auto",
                } as any}
            >
                {t("noticeKeep", "Got it")}
            </button>
        </div>
    );
};

export const TransitPanelHost = () => {
    const open = useOpen();
    const stopHas = useValue(selStopHas$);
    const auto = useValue(autoOpen$) as number;
    const t = useT();

    // Auto-open when a new stop is selected (auto counter increments C#-side).
    const lastAuto = useRef<number>(auto);
    useEffect(() => {
        if (auto !== lastAuto.current) {
            lastAuto.current = auto;
            setOpen(true);
        }
    }, [auto]);

    // Close the panel when the selection stops being a stop — e.g. the player clicks a transport LINE — so the empty
    // "select a stop" hint doesn't linger over an unrelated panel (issue #3). Only the true->false transition closes
    // it, so a panel opened from the toolbar button while nothing is selected still shows its hint.
    const prevStopHas = useRef(stopHas);
    useEffect(() => {
        if (prevStopHas.current && !stopHas) setOpen(false);
        prevStopHas.current = stopHas;
    }, [stopHas]);

    // X MEANS CLOSED — render nothing at all.
    //
    // This used to fall back to a slim "DEPARTURES ▸" bar at the panel's own position whenever a stop was still
    // selected, so pressing X visibly shrank the panel instead of dismissing it and the corner never went empty.
    // It existed for a real reason: re-clicking an ALREADY-SELECTED stop fires no event, so the C# autoOpen counter
    // never increments and the selection alone cannot bring the panel back.
    //
    // That reason is already covered. TransitButton is appended to GameTopRight (index.tsx), is always mounted, and
    // its onSelect toggles this same module-level _open — so the toolbar icon reopens the panel for the currently
    // selected stop, which is exactly what the bar was standing in for. Nothing is stranded by closing here.
    if (!open) return null;
    return (
        <div
            style={{
                position: "fixed", top: "90rem", right: "56rem", width: "360rem", zIndex: 99999,
                pointerEvents: "auto", background: "rgba(13, 21, 33, 0.97)", borderRadius: "6rem",
                display: "flex", flexDirection: "column", color: "white",
                boxShadow: "0 4rem 24rem rgba(0,0,0,0.5)",
            }}
        >
            <div style={{ display: "flex", alignItems: "center", padding: "10rem 14rem", borderBottom: "1rem solid rgba(255,255,255,0.12)" }}>
                <div style={{ flex: 1, fontSize: "var(--fontSizeM)", fontWeight: "bold", textTransform: "uppercase", color: "var(--textColor)" } as any}>{t("panelTitle", "ARRIVALS")}</div>
                <CloseGlyph onClick={() => setOpen(false)} />
            </div>
            {stopHas ? (
                <StopBoard />
            ) : (
                <div style={{ padding: "12rem 14rem", fontSize: "12rem", opacity: 0.6 }}>
                    {t("panelHint", "Select a stop to see how often each line comes and when it is next expected. To change a line's vehicle counts, select the line — its controls are in the line's info panel.")}
                </div>
            )}
        </div>
    );
};
