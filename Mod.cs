using System.Collections.Generic;
using Colossal;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Game.Simulation;

namespace TransitTimetables
{
    public class Mod : IMod
    {
        public static ILog log = LogManager.GetLogger($"{nameof(TransitTimetables)}.{nameof(Mod)}").SetShowsErrorsInUI(false);
        public static TransitTimetablesSetting ActiveSetting;

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));

            ActiveSetting = new TransitTimetablesSetting(this);
            ActiveSetting.RegisterInOptionsUI();
            var lm = GameManager.instance.localizationManager;
            foreach (var locale in lm.GetSupportedLocales())
                lm.AddSource(locale, new LocaleEn(ActiveSetting, locale));
            AssetDatabase.global.LoadSettings(nameof(TransitTimetables), ActiveSetting, new TransitTimetablesSetting(this));
            // Unfold a stored v0.5.x dropdown value back onto the checkbox BEFORE anything can read it. Deliberately
            // here and not in OnGameLoadingComplete: that also fires at boot for the main menu, where the Options page
            // could be opened before any city loads. The flush is what clears the consumed dropdown from the .coc.
            if (ActiveSetting.Migrate())
            {
                log.Info($"[SelfTest] migrated vehicle-count setting -> ManageVehicleCount={ActiveSetting.ManageVehicleCount}");
                SaveSettings();
            }

            // Runtime day-length calibrator: keeps the frame<->minute math correct under slow-time mods (Time2Work).
            // Registered FIRST so it refreshes before the dispatch/UI read it within the frame.
            updateSystem.UpdateAt<TimebaseSystem>(SystemUpdatePhase.GameSimulation);
            // Fleet-control helper (per-line vehicle-count via the line's own VehicleInterval modifier).
            updateSystem.UpdateAt<HourlyFleetSystem>(SystemUpdatePhase.GameSimulation);
            // Fixed-departure timetabling: terminus hold, derived fleet, retire-at-terminus.
            updateSystem.UpdateAt<TimetableDispatchSystem>(SystemUpdatePhase.GameSimulation);
            // Line-panel editor + stop departure board bindings (floating overlay, does not pause the game).
            updateSystem.UpdateAt<TransitParamsUISystem>(SystemUpdatePhase.UIUpdate);

            log.Info("[SelfTest] TransitTimetables loaded (fixed-departure timetables).");
        }

        // Flush settings to disk for CODE-DRIVEN writes (the migration above) — the Options page saves itself, since
        // vanilla's AutomaticSettings already calls ApplyAndSave() on every widget change.
        // Deliberately NOT ActiveSetting.ApplyAndSave(): that calls AssetDatabase.SaveSpecificSetting(GetType().Name),
        // which resolves its target by the settings CLASS SIMPLE NAME and stops at the first match. The class is now
        // uniquely named (TransitTimetablesSetting) so that lookup would resolve correctly, but SaveSettings() iterates
        // every registered setting asset and so cannot miss regardless of what other mods name their classes.
        public static void SaveSettings()
        {
            try { _ = AssetDatabase.global.SaveSettings(); }
            catch (System.Exception e) { log.Warn($"settings save failed: {e.Message}"); }
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
            if (ActiveSetting != null)
            {
                ActiveSetting.UnregisterInOptionsUI();
                ActiveSetting = null;
            }
        }
    }

    // Minimal English locale (full localization once mechanics are proven, same pipeline as EconomyTweaks).
    public class LocaleEn : IDictionarySource
    {
        private readonly TransitTimetablesSetting m_S;
        private readonly string m_L;
        public LocaleEn(TransitTimetablesSetting setting, string locale) { m_S = setting; m_L = locale; }
        private string T(string k) => Translations.Get(k, m_L);

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            var entries = new Dictionary<string, string>
            {
                { m_S.GetSettingsLocaleID(), "Transit Timetables" },
                { m_S.GetOptionTabLocaleID(TransitTimetablesSetting.Section), "Main" },
                // The "real loop" line. Built in the UI from these templates, NOT in C# — clause order differs between
                // languages, so each one needs a whole sentence rather than translated fragments glued in English order.
                // One-time migration notice (rendered by the mod's own React dialog, not the Options page).
                { m_S.GetOptionGroupLocaleID(TransitTimetablesSetting.GroupWindows), T("grp.GroupWindows") },
                { m_S.GetOptionGroupLocaleID(TransitTimetablesSetting.GroupCompat), T("grp.GroupCompat") },

                { m_S.GetOptionGroupLocaleID(TransitTimetablesSetting.GroupRealism), T("grp.GroupRealism") },
                { m_S.GetOptionGroupLocaleID(TransitTimetablesSetting.GroupStops), T("grp.GroupStops") },
                { m_S.GetOptionLabelLocaleID(nameof(TransitTimetablesSetting.RealisticTravelTime)), T("opt.RealisticTravelTime.L") },
                { m_S.GetOptionDescLocaleID(nameof(TransitTimetablesSetting.RealisticTravelTime)), T("opt.RealisticTravelTime.D") },
                { m_S.GetOptionLabelLocaleID(nameof(TransitTimetablesSetting.ProvisionRealFleet)), T("opt.ProvisionRealFleet.L") },
                { m_S.GetOptionDescLocaleID(nameof(TransitTimetablesSetting.ProvisionRealFleet)), T("opt.ProvisionRealFleet.D") },
                { m_S.GetOptionLabelLocaleID(nameof(TransitTimetablesSetting.StopAtEveryStop)), T("opt.StopAtEveryStop.L") },
                { m_S.GetOptionDescLocaleID(nameof(TransitTimetablesSetting.StopAtEveryStop)), T("opt.StopAtEveryStop.D") },
                { m_S.GetOptionLabelLocaleID(nameof(TransitTimetablesSetting.MaxDwellRoad)), T("opt.MaxDwellRoad.L") },
                { m_S.GetOptionDescLocaleID(nameof(TransitTimetablesSetting.MaxDwellRoad)), T("opt.MaxDwellRoad.D") },
                { m_S.GetOptionLabelLocaleID(nameof(TransitTimetablesSetting.MaxDwellRail)), T("opt.MaxDwellRail.L") },
                { m_S.GetOptionDescLocaleID(nameof(TransitTimetablesSetting.MaxDwellRail)), T("opt.MaxDwellRail.D") },

                { m_S.GetOptionLabelLocaleID(nameof(TransitTimetablesSetting.RealisticTripsCompat)), T("opt.RealisticTripsCompat.L") },
                { m_S.GetOptionDescLocaleID(nameof(TransitTimetablesSetting.RealisticTripsCompat)), T("opt.RealisticTripsCompat.D") },

                { m_S.GetOptionLabelLocaleID(nameof(TransitTimetablesSetting.MorningPeakStart)), T("opt.MorningPeakStart.L") },
                { m_S.GetOptionDescLocaleID(nameof(TransitTimetablesSetting.MorningPeakStart)), T("opt.MorningPeakStart.D") },
                { m_S.GetOptionLabelLocaleID(nameof(TransitTimetablesSetting.MorningPeakEnd)), T("opt.MorningPeakEnd.L") },
                { m_S.GetOptionDescLocaleID(nameof(TransitTimetablesSetting.MorningPeakEnd)), T("opt.MorningPeakEnd.D") },
                { m_S.GetOptionLabelLocaleID(nameof(TransitTimetablesSetting.EveningPeakStart)), T("opt.EveningPeakStart.L") },
                { m_S.GetOptionDescLocaleID(nameof(TransitTimetablesSetting.EveningPeakStart)), T("opt.EveningPeakStart.D") },
                { m_S.GetOptionLabelLocaleID(nameof(TransitTimetablesSetting.EveningPeakEnd)), T("opt.EveningPeakEnd.L") },
                { m_S.GetOptionDescLocaleID(nameof(TransitTimetablesSetting.EveningPeakEnd)), T("opt.EveningPeakEnd.D") },
                { m_S.GetOptionLabelLocaleID(nameof(TransitTimetablesSetting.NightStart)), T("opt.NightStart.L") },
                { m_S.GetOptionDescLocaleID(nameof(TransitTimetablesSetting.NightStart)), T("opt.NightStart.D") },
                { m_S.GetOptionLabelLocaleID(nameof(TransitTimetablesSetting.NightEnd)), T("opt.NightEnd.L") },
                { m_S.GetOptionDescLocaleID(nameof(TransitTimetablesSetting.NightEnd)), T("opt.NightEnd.D") },

                { m_S.GetOptionGroupLocaleID(TransitTimetablesSetting.GroupGeneral), T("grp.GroupGeneral") },
                { m_S.GetOptionLabelLocaleID(nameof(TransitTimetablesSetting.Enabled)), T("opt.Enabled.L") },
                { m_S.GetOptionDescLocaleID(nameof(TransitTimetablesSetting.Enabled)), T("opt.Enabled.D") },
                { m_S.GetOptionLabelLocaleID(nameof(TransitTimetablesSetting.ManageVehicleCount)), T("opt.ManageVehicleCount.L") },
                { m_S.GetOptionDescLocaleID(nameof(TransitTimetablesSetting.ManageVehicleCount)), T("opt.ManageVehicleCount.D") },
                { m_S.GetOptionLabelLocaleID(nameof(TransitTimetablesSetting.CleanUninstall)), T("opt.CleanUninstall.L") },
                { m_S.GetOptionDescLocaleID(nameof(TransitTimetablesSetting.CleanUninstall)), T("opt.CleanUninstall.D") },
                // Confirmation-dialog body. Without this the destructive button's [SettingsUIConfirmation] prompt shows
                // a RAW LOCALE KEY instead of a warning — unacceptable for an action that clears every timetable.
                { m_S.GetOptionWarningLocaleID(nameof(TransitTimetablesSetting.CleanUninstall)), T("opt.CleanUninstall.W") },
            };
            // Every ui.* key the panel can request, ENUMERATED from the English table rather than hand-listed.
            // The hand-kept list this replaces had silently drifted 19 keys behind the panel — every vehicle-
            // schedule string, the loop-stage strings, rttOffWarning and both terminus warnings were unregistered,
            // so the translations shipped for them were dead and every locale saw the tsx English fallback.
            // The tsx carries an English fallback for every key, so an extra registration is harmless and a
            // missing one is invisible in English — exactly the failure shape that must not depend on memory.
            foreach (var kv in Translations.En)
                if (kv.Key.StartsWith("ui.", System.StringComparison.Ordinal))
                    entries["TransitTimetables." + kv.Key] = T(kv.Key);
            return entries;
        }

        public void Unload() { }
    }
}
