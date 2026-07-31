import { useState, useEffect, useRef, useContext } from "react";
import { bindValue, useValue, trigger } from "cs2/api";
import { FloatingButton, ConfirmationDialog, DialogStack, DialogContext } from "cs2/ui";
import { useT } from "mods/i18n";
import ICON from "../transittimetables-icon.svg";

const G = "TransitParams";

// Selected LINE timetable (drives the editor injected into the line panel).
const selHas$ = bindValue<boolean>(G, "selHas", false);
const selTtEnabled$ = bindValue<boolean>(G, "selTtEnabled", false);
const selTtFirst$ = bindValue<number>(G, "selTtFirst", 300);
const selTtPeak$ = bindValue<number>(G, "selTtPeak", 8);
const selTtOffPeak$ = bindValue<number>(G, "selTtOffPeak", 12);
const selTtNight$ = bindValue<number>(G, "selTtNight", 30);
const selTtInterval$ = bindValue<number>(G, "selTtInterval", 0);
const selTtFleet$ = bindValue<number>(G, "selTtFleet", 0);
const selTtNext$ = bindValue<string>(G, "selTtNext", "");
const selTtRealInfo$ = bindValue<string>(G, "selTtRealInfo", "");
// Per-line custom peak (PR #5): enable + interval + two hour windows.
const selCustomPeakEnabled$ = bindValue<boolean>(G, "selCustomPeakEnabled", false);
const selCustomPeakInterval$ = bindValue<number>(G, "selCustomPeakInterval", 5);
const selCustomPeakStart1$ = bindValue<number>(G, "selCustomPeakStart1", 7);
const selCustomPeakEnd1$ = bindValue<number>(G, "selCustomPeakEnd1", 9);
const selCustomPeakStart2$ = bindValue<number>(G, "selCustomPeakStart2", 16);
const selCustomPeakEnd2$ = bindValue<number>(G, "selCustomPeakEnd2", 18);

// Which windows apply + their hours, so the editor shows only relevant intervals and communicates the times.
const selSchedule$ = bindValue<number>(G, "selSchedule", 2); // 0=Day, 1=Night, 2=DayAndNight
const peakHours$ = bindValue<string>(G, "peakHours", "");
const nightHours$ = bindValue<string>(G, "nightHours", "");

// Selected STOP departure board (drives the floating panel).
const selStopHas$ = bindValue<boolean>(G, "selStopHas", false);
const selStopBoard$ = bindValue<string>(G, "selStopBoard", "[]");
const autoOpen$ = bindValue<number>(G, "autoOpen", 0);
// One-time migration notice. A counter rather than a flag, so a late-mounting host still sees the change.
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

const hm = (min: number) => {
    let m = ((Math.round(min) % 1440) + 1440) % 1440;
    const h = Math.floor(m / 60), mm = m % 60;
    return (h < 10 ? "0" : "") + h + ":" + (mm < 10 ? "0" : "") + mm;
};

const stepBtn = {
    cursor: "pointer", width: "24rem", height: "22rem", fontSize: "14rem", color: "white",
    background: "rgba(255,255,255,0.12)", borderRadius: "4rem",
} as const;

// Coarse step (±1h on the clock, ±10 on an interval) — wider so a two-character label fits, smaller type so it sits
// level with the ± glyphs. Paired with stepBtn everywhere: coarse outside, fine inside, value in the middle.
const stepBtnCoarse = {
    cursor: "pointer", width: "30rem", height: "22rem", fontSize: "11rem", color: "white",
    background: "rgba(255,255,255,0.12)", borderRadius: "4rem",
} as const;

// First departure is a CLOCK time, so stepping WRAPS rather than clamping: -5 from 00:00 gives 23:55, which is how
// you reach a late-night first departure without 280 clicks. The C# trigger clamps to 0..1439 anyway, and this is
// already normalized into that range, so the clamp is a no-op.
const wrapMin = (v: number) => ((Math.round(v) % 1440) + 1440) % 1440;

// Native close button: the game's Close glyph as a mask tinted with the panel text colour (matches the native
// panels, which use url(Media/Glyphs/...) masks — not a literal "X"). pointerEvents:auto for reliable clicks.
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

const IntervalRow = ({ label, hours, value$, trig }: { label: string; hours?: string; value$: any; trig: string }) => {
    const v = useValue(value$) as number;
    const set = (nv: number) => trigger(G, trig, Math.max(1, Math.round(nv)));
    return (
        <div style={{ display: "flex", alignItems: "center", padding: "3rem 0" }}>
            <div style={{ flex: 1 }}>
                <div style={{ fontSize: "13rem" }}>{label}</div>
                {hours ? <div style={{ fontSize: "10rem", opacity: 0.5 }}>{hours}</div> : null}
            </div>
            {/* ±10 keeps a two-digit headway cheap to reach (16 min = +10 then +1 x6, not sixteen clicks), while ±1
                still lands on any exact minute. Adds/subtracts a flat 10 rather than snapping to a multiple of it, so
                the ones digit you dialled in survives: 16 -> 26 -> 16. `set` already clamps at 1. Margins, not `gap`
                (cohtml has no flex gap). Mirrors the First departure row above: coarse outside, fine inside. */}
            <button style={{ ...stepBtnCoarse, marginRight: "5rem" }} onClick={() => set(v - 10)}>−10</button>
            <button style={stepBtn} onClick={() => set(v - 1)}>−</button>
            <div style={{ width: "54rem", textAlign: "center", fontSize: "13rem" }}>{Math.round(v)} min</div>
            <button style={stepBtn} onClick={() => set(v + 1)}>+</button>
            <button style={{ ...stepBtnCoarse, marginLeft: "5rem" }} onClick={() => set(v + 10)}>+10</button>
        </div>
    );
};

// Hour-of-day steps WRAP (mod 24) so a window can cross midnight (e.g. a night-shift line 22 -> 06). The C# trigger
// clamps to 0..23 anyway.
const wrapHr = (v: number) => ((Math.round(v) % 24) + 24) % 24;
const hhmm = (h: number) => (Math.round(h) < 10 ? "0" : "") + Math.round(h) + ":00";

// One custom-peak WINDOW (PR #5): a start-hour and end-hour range, each stepped ±1h with wrap.
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

// The timetable editor — injected into the native line info panel. Renders nothing unless a transport line is
// selected (self-gates on selHas, so it's inert on non-line selections and work routes).
// The "real loop" line. C# sends NUMBERS, not a sentence, and the whole sentence is built here from a per-language
// template — this was the last user-facing text in the mod that was assembled in C# and therefore English in all 11
// translated languages. It cannot be done by translating fragments and gluing them in English order, because clause
// order differs between languages, so each language gets a complete template with {placeholders}.
const RealInfo = ({ raw }: { raw: string }) => {
    const t = useT();
    if (!raw) return null;
    let d: { real: number; est: number; corr: string; meas: boolean; mode: string; n: number };
    try { d = JSON.parse(raw); } catch { return null; }
    if (!d || typeof d.real !== "number") return null;
    const vars = { real: d.real, est: d.est, corr: d.corr, n: d.n };
    const head = d.meas
        ? t("realLoopMeasured", "Real loop ~{real} min ({corr}x the {est}-min estimate, measured).", vars)
        : t("realLoopEstimated", "Real loop ~{real} min ({corr}x the {est}-min estimate, estimated).", vars);
    const tail =
        d.mode === "prov" ? t("provisioning", "Provisioning ~{n} vehicles for it.", vars)
        : d.mode === "notmine" ? t("notSetByMod", "This headway needs ~{n} vehicles. This mod is not setting the count.", vars)
        : t("sizingSoon", "Sizing this line as soon as its duration estimate settles.");
    return (
        <div style={{ fontSize: "11rem", color: "rgb(224, 186, 120)", marginBottom: "6rem", lineHeight: 1.35 }}>
            {head + " " + tail}
        </div>
    );
};

export const TimetableEditor = () => {
    const has = useValue(selHas$);
    const on = useValue(selTtEnabled$);
    const first = useValue(selTtFirst$) as number;
    const interval = useValue(selTtInterval$) as number;
    const fleet = useValue(selTtFleet$) as number;
    const next = useValue(selTtNext$) as string;
    const realInfo = useValue(selTtRealInfo$) as string;
    const customPeakOn = useValue(selCustomPeakEnabled$) as boolean;
    const schedule = useValue(selSchedule$) as number; // 0=Day, 1=Night, 2=DayAndNight
    const peakHrs = useValue(peakHours$) as string;
    const nightHrs = useValue(nightHours$) as string;
    const t = useT();
    if (!has) return null;
    // Only show the intervals the line actually runs: day-only → Peak+Off-peak, night-only → Night, both → all.
    const showDay = schedule === 0 || schedule === 2;
    const showNight = schedule === 1 || schedule === 2;

    return (
        <div style={{ borderTop: "1rem solid rgba(255,255,255,0.15)", padding: "8rem 14rem 10rem", color: "white" }}>
            <div style={{ display: "flex", alignItems: "center", marginBottom: "6rem" }}>
                <div style={{ flex: 1, fontSize: "var(--fontSizeS)", fontWeight: "bold", textTransform: "uppercase", color: "var(--textColor)", opacity: 0.9 } as any}>{t("timetable", "TIMETABLE")}</div>
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
                    <div style={{ fontSize: "12rem", color: "rgb(120, 210, 130)", marginBottom: "2rem" }}>
                        {t("ttNow", "now every {i} min · {f} vehicles", { i: interval, f: fleet })}
                    </div>
                    <div style={{ fontSize: "12rem", opacity: 0.7, marginBottom: "6rem" }}>
                        {t("ttNext", "next: {n}", { n: next || "—" })}
                    </div>
                    <RealInfo raw={realInfo} />
                    {/* ±1 / ±10, deliberately identical to the interval rows below — one mental model for the panel.
                        ±1 matters: staggering first departures a minute apart across lines that share a stop is a real
                        technique, and the old ±15 (then ±5) could not express it.
                        Why not an ±1h coarse step, given this is a clock? It would make crossing hours cheaper, but the
                        long moves it helps with barely happen: ScheduleMath.FirstDeparture already auto-clamps a
                        night-only line's first departure to the night window start (and a day-only line's into the
                        day), so the extremes are set for you. Real edits are 5-60 min — exactly ±10's range. */}
                    <div style={{ display: "flex", alignItems: "center", padding: "3rem 0" }}>
                        <div style={{ flex: 1, fontSize: "13rem" }}>{t("firstDeparture", "First departure")}</div>
                        {/* Margins, not `gap`: the game's cohtml UI has no flex gap. Coarse buttons sit slightly apart
                            from the fine pair so the two granularities read as groups. */}
                        <button style={{ ...stepBtnCoarse, marginRight: "5rem" }} onClick={() => trigger(G, "setSelTtFirst", wrapMin(first - 10))}>−10</button>
                        <button style={stepBtn} onClick={() => trigger(G, "setSelTtFirst", wrapMin(first - 1))}>−</button>
                        <div style={{ width: "54rem", textAlign: "center", fontSize: "13rem" }}>{hm(first)}</div>
                        <button style={stepBtn} onClick={() => trigger(G, "setSelTtFirst", wrapMin(first + 1))}>+</button>
                        <button style={{ ...stepBtnCoarse, marginLeft: "5rem" }} onClick={() => trigger(G, "setSelTtFirst", wrapMin(first + 10))}>+10</button>
                    </div>
                    {showDay ? <IntervalRow label={t("peakInterval", "Peak")} hours={peakHrs} value$={selTtPeak$} trig="setSelTtPeak" /> : null}
                    {showDay ? <IntervalRow label={t("offPeakInterval", "Off-peak")} hours={t("otherHours", "other hours")} value$={selTtOffPeak$} trig="setSelTtOffPeak" /> : null}
                    {showNight ? <IntervalRow label={t("nightInterval", "Night")} hours={nightHrs} value$={selTtNight$} trig="setSelTtNight" /> : null}
                    {/* Per-line custom peak (PR #5): two windows + interval, overriding the global peak for THIS line only. */}
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
                                <IntervalRow label={t("customPeakInterval", "Custom peak interval")} value$={selCustomPeakInterval$} trig="setSelCustomPeakInterval" />
                            </>
                        ) : null}
                    </div>
                    <div style={{ fontSize: "11rem", opacity: 0.45, marginTop: "4rem" }}>
                        {t("terminusHint", "Select a stop to see its departures and set it as this line's terminus.")}
                    </div>
                </>
            )}
        </div>
    );
};

// The stop departure board — every line's next departures from the selected stop.
const StopBoard = () => {
    const raw = useValue(selStopBoard$) as string;
    const t = useT();
    let board: Array<{ n: number; nm?: string; tt: boolean; term: boolean; est?: boolean; d: string }> = [];
    try { board = JSON.parse(raw || "[]"); } catch { board = []; }
    const ttCount = board.filter((e) => e.tt).length;
    const termBtn = {
        cursor: "pointer", display: "block", width: "100%", padding: "7rem 12rem", borderRadius: "4rem",
        fontSize: "13rem", color: "white", pointerEvents: "auto", textAlign: "center",
    } as const;
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
                        </div>
                        <div style={{ fontSize: "12rem", color: e.tt ? "rgb(120, 210, 130)" : "rgba(255,255,255,0.45)" }}>
                            {e.tt ? (e.d ? t("departs", "departs: {d}", { d: e.d }) : t("noDepartures", "no departures scheduled")) : t("notTimetabled", "not timetabled")}
                        </div>
                        {/* The mod has not measured this stop yet, so these times come from the game's own travel
                            estimate. Say so rather than printing them with the same confidence as measured ones. */}
                        {e.tt && e.est && e.d ? (
                            <div style={{ fontSize: "11rem", opacity: 0.45 }}>{t("estimatedTimes", "estimated, not yet measured")}</div>
                        ) : null}
                        {e.tt && !e.term ? (
                            <button
                                onClick={() => trigger(G, "setTerminusRow", i)}
                                style={{ marginTop: "4rem", cursor: "pointer", padding: "3rem 10rem", borderRadius: "4rem", fontSize: "11rem", color: "white", background: "rgba(70, 110, 170, 0.9)", pointerEvents: "auto" } as any}
                            >
                                {t("setTerminusThis", "Set as terminus")}
                            </button>
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
                        {t("setTerminusHint", "The terminus anchors the schedule and the vehicle hold; buses retire here.")}
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

// The one-time "vehicle counts are changing" notice. Rendered with the GAME'S OWN dialog component pushed onto its
// dialog stack, so it looks and behaves exactly like a vanilla confirmation (same shell, backdrop, button theme and
// gamepad handling) while keeping our own localized strings and our own callbacks.
//
// Deliberately NOT the native appBindings.ShowConfirmationDialog path: that stores ONE global callback which any other
// dialog silently overwrites, and it delivers over a fire-and-forget event whose listener only exists while the Game
// screen is mounted — which is exactly when we fire.
//
// The opt-out, rendered as dialog CONTENT rather than as the dialog's cancel button — see MigrationNotice for why.
// Closes the dialog itself via DialogContext.onClose, which is the very context the dialog component consumes
// internally (verified: cs2/ui exports DialogContext as the same object the dialog reads). Calling it directly closes
// WITHOUT firing onCancel, so this cannot double-answer.
const NoticeOptOut = () => {
    const dlg = useContext(DialogContext);
    const t = useT();
    return (
        <div style={{ display: "flex", justifyContent: "center", marginTop: "4rem" }}>
            <button
                onClick={() => {
                    trigger(G, "noticeAnswer", false);
                    if (dlg && typeof dlg.onClose === "function") dlg.onClose();
                }}
                style={{
                    cursor: "pointer", padding: "5rem 14rem", borderRadius: "4rem", fontSize: "12rem",
                    color: "white", opacity: 0.75, background: "rgba(90, 100, 115, 0.9)", pointerEvents: "auto",
                } as any}
            >
                {t("noticeOff", "Do not let the mod decide")}
            </button>
        </div>
    );
};

// BUTTON MAPPING — this looks odd and is deliberate. The requirement is BOTH that the recommended action is the green
// one AND that Escape / the X mean "let the mod decide". Those cannot both be met with the dialog's own two buttons:
// Escape, the X and the cancel button all route to the SAME onCancel handler, and nothing distinguishes them.
//
// So the dialog is given only ONE button. `confirm` (green) is "Let the mod decide"; `cancellable={false}` suppresses
// the red cancel button entirely; and onCancel — which is still what Escape and the X fire — ALSO answers "let the mod
// decide". The opt-out moves into the dialog's content as our own button, which we control completely.
//
// Net: green is the recommended action, every dismissal path is the safe one, and opting out takes a deliberate click.
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
                title={t("noticeTitle", "Vehicle counts are changing")}
                // message MUST be a plain string, despite the ReactNode type. The dialog pipes it through the game's
                // own text renderer — Children.toArray(msg).flatMap(e => ET(dc(i, e, "\n"))) — which expects strings
                // and splits paragraphs on "\n". Passing JSX renders the literal text "[div/]" instead of the body.
                message={[
                    t("noticeBody", "This version measures each line's real loop and provisions what the timetable actually requires, which means more vehicles than before."),
                    t("noticeAsk", "Would you rather set vehicle counts yourself?"),
                    t("noticeWhere", "You can change this at any time in Options, under Vehicle count."),
                ].join("\n")}
                confirm={t("noticeKeep", "Let the mod decide")}
                cancellable={false}
                onConfirm={() => trigger(G, "noticeAnswer", true)}
                onCancel={() => trigger(G, "noticeAnswer", true)}
            >
                <NoticeOptOut />
            </ConfirmationDialog>
        );
    }, [seq, stack, t]);
    return null;
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

    // Close the panel when the selection stops being a stop — e.g. the player clicks a transport LINE — so the
    // empty "select a stop" hint doesn't linger over an unrelated panel (issue #3). Only the true->false transition
    // closes it, so a panel opened from the toolbar button while nothing is selected still shows its hint.
    const prevStopHas = useRef(stopHas);
    useEffect(() => {
        if (prevStopHas.current && !stopHas) setOpen(false);
        prevStopHas.current = stopHas;
    }, [stopHas]);

    if (!open) {
        // Closed but a stop is still selected: keep a slim reopen bar. Re-clicking the SAME stop can't reopen the
        // panel (the game fires no reselect event for an already-selected entity), so offer this affordance instead.
        if (!stopHas) return null;
        return (
            <div
                onClick={() => setOpen(true)}
                style={{
                    position: "fixed", top: "90rem", right: "56rem", zIndex: 99999, pointerEvents: "auto",
                    cursor: "pointer", background: "rgba(13, 21, 33, 0.97)", borderRadius: "6rem",
                    padding: "8rem 12rem", color: "white", fontSize: "var(--fontSizeM)", fontWeight: "bold",
                    textTransform: "uppercase", boxShadow: "0 4rem 24rem rgba(0,0,0,0.5)",
                } as any}
            >
                {t("panelTitle", "DEPARTURES")} ▸
            </div>
        );
    }
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
                <div style={{ flex: 1, fontSize: "var(--fontSizeM)", fontWeight: "bold", textTransform: "uppercase", color: "var(--textColor)" } as any}>{t("panelTitle", "DEPARTURES")}</div>
                <CloseGlyph onClick={() => setOpen(false)} />
            </div>
            {stopHas ? (
                <StopBoard />
            ) : (
                <div style={{ padding: "12rem 14rem", fontSize: "12rem", opacity: 0.6 }}>
                    {t("panelHint", "Select a stop to see every line's departures from it. To edit a line's timetable, select the line — its controls are in the line's info panel.")}
                </div>
            )}
        </div>
    );
};
