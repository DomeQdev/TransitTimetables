import { useLocalization } from "cs2/l10n";

// Panel localization: every user-visible string goes through t(key, fallback, vars). Ids are
// "TransitTimetables.ui.<key>". Every ui.* key in Translations.En is registered by LocaleEn (enumerated,
// not hand-listed), so the inline English here is only the fallback for a locale gap or a load race.
export function useT() {
    const loc = useLocalization();
    return (key: string, fallback: string, vars?: Record<string, string | number>) => {
        let s = fallback;
        try {
            const r = loc && loc.translate("TransitTimetables.ui." + key, fallback);
            if (r) s = r;
        } catch { /* fall back to English */ }
        if (vars) {
            for (const k in vars) s = s.split("{" + k + "}").join(String(vars[k]));
        }
        return s;
    };
}
