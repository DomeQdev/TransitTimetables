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
        public static Setting ActiveSetting;

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));

            ActiveSetting = new Setting(this);
            ActiveSetting.RegisterInOptionsUI();
            var lm = GameManager.instance.localizationManager;
            foreach (var locale in lm.GetSupportedLocales())
                lm.AddSource(locale, new LocaleEn(ActiveSetting, locale));
            AssetDatabase.global.LoadSettings(nameof(TransitTimetables), ActiveSetting, new Setting(this));
            // Persist every settings change to disk the moment it is applied (survives a crash / non-clean exit).
            ActiveSetting.onSettingsApplied += OnSettingsApplied;

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

        // Persist a settings change to disk as soon as it is applied (guard: ApplyAndSave re-raises onSettingsApplied).
        private static bool s_savingReentrant;
        private static void OnSettingsApplied(Game.Settings.Setting setting)
        {
            if (s_savingReentrant)
                return;
            s_savingReentrant = true;
            try { ActiveSetting?.ApplyAndSave(); }
            finally { s_savingReentrant = false; }
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
            if (ActiveSetting != null)
            {
                ActiveSetting.onSettingsApplied -= OnSettingsApplied;
                ActiveSetting.UnregisterInOptionsUI();
                ActiveSetting = null;
            }
        }
    }

    // Minimal English locale (full localization once mechanics are proven, same pipeline as EconomyTweaks).
    public class LocaleEn : IDictionarySource
    {
        private readonly Setting m_S;
        private readonly string m_L;
        public LocaleEn(Setting setting, string locale) { m_S = setting; m_L = locale; }
        private string T(string k) => Translations.Get(k, m_L);

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_S.GetSettingsLocaleID(), "Transit Timetables" },
                { m_S.GetOptionTabLocaleID(Setting.Section), "Main" },
                { "TransitTimetables.ui.to", T("ui.to") },
                { "TransitTimetables.ui.timetable", T("ui.timetable") },
                { "TransitTimetables.ui.on", T("ui.on") },
                { "TransitTimetables.ui.off", T("ui.off") },
                { "TransitTimetables.ui.ttNow", T("ui.ttNow") },
                { "TransitTimetables.ui.ttNext", T("ui.ttNext") },
                { "TransitTimetables.ui.firstDeparture", T("ui.firstDeparture") },
                { "TransitTimetables.ui.peakInterval", T("ui.peakInterval") },
                { "TransitTimetables.ui.offPeakInterval", T("ui.offPeakInterval") },
                { "TransitTimetables.ui.otherHours", T("ui.otherHours") },
                { "TransitTimetables.ui.nightInterval", T("ui.nightInterval") },
                { "TransitTimetables.ui.customLinePeak", T("ui.customLinePeak") },
                { "TransitTimetables.ui.morningPeak", T("ui.morningPeak") },
                { "TransitTimetables.ui.eveningPeak", T("ui.eveningPeak") },
                { "TransitTimetables.ui.customPeakInterval", T("ui.customPeakInterval") },
                { "TransitTimetables.ui.terminusHint", T("ui.terminusHint") },
                { "TransitTimetables.ui.noLines", T("ui.noLines") },
                { "TransitTimetables.ui.line", T("ui.line") },
                { "TransitTimetables.ui.terminusBadge", T("ui.terminusBadge") },
                { "TransitTimetables.ui.departs", T("ui.departs") },
                { "TransitTimetables.ui.noDepartures", T("ui.noDepartures") },
                { "TransitTimetables.ui.notTimetabled", T("ui.notTimetabled") },
                { "TransitTimetables.ui.setTerminusThis", T("ui.setTerminusThis") },
                { "TransitTimetables.ui.setTerminusAll", T("ui.setTerminusAll") },
                { "TransitTimetables.ui.setTerminusHint", T("ui.setTerminusHint") },
                { "TransitTimetables.ui.buttonTooltip", T("ui.buttonTooltip") },
                { "TransitTimetables.ui.panelTitle", T("ui.panelTitle") },
                { "TransitTimetables.ui.panelHint", T("ui.panelHint") },
                { m_S.GetOptionGroupLocaleID(Setting.GroupWindows), T("grp.GroupWindows") },
                { m_S.GetOptionGroupLocaleID(Setting.GroupRealism), T("grp.GroupRealism") },
                { m_S.GetOptionGroupLocaleID(Setting.GroupCompat), T("grp.GroupCompat") },

                { m_S.GetOptionLabelLocaleID(nameof(Setting.RealisticTravelTime)), T("opt.RealisticTravelTime.L") },
                { m_S.GetOptionDescLocaleID(nameof(Setting.RealisticTravelTime)), T("opt.RealisticTravelTime.D") },
                { m_S.GetOptionLabelLocaleID(nameof(Setting.ProvisionRealFleet)), T("opt.ProvisionRealFleet.L") },
                { m_S.GetOptionDescLocaleID(nameof(Setting.ProvisionRealFleet)), T("opt.ProvisionRealFleet.D") },

                { m_S.GetOptionGroupLocaleID(Setting.GroupStops), T("grp.GroupStops") },
                { m_S.GetOptionLabelLocaleID(nameof(Setting.StopAtEveryStop)), T("opt.StopAtEveryStop.L") },
                { m_S.GetOptionDescLocaleID(nameof(Setting.StopAtEveryStop)), T("opt.StopAtEveryStop.D") },
                { m_S.GetOptionLabelLocaleID(nameof(Setting.MaxDwellRoad)), T("opt.MaxDwellRoad.L") },
                { m_S.GetOptionDescLocaleID(nameof(Setting.MaxDwellRoad)), T("opt.MaxDwellRoad.D") },
                { m_S.GetOptionLabelLocaleID(nameof(Setting.MaxDwellRail)), T("opt.MaxDwellRail.L") },
                { m_S.GetOptionDescLocaleID(nameof(Setting.MaxDwellRail)), T("opt.MaxDwellRail.D") },

                { m_S.GetOptionLabelLocaleID(nameof(Setting.RealisticTripsCompat)), T("opt.RealisticTripsCompat.L") },
                { m_S.GetOptionDescLocaleID(nameof(Setting.RealisticTripsCompat)), T("opt.RealisticTripsCompat.D") },

                { m_S.GetOptionLabelLocaleID(nameof(Setting.MorningPeakStart)), T("opt.MorningPeakStart.L") },
                { m_S.GetOptionDescLocaleID(nameof(Setting.MorningPeakStart)), T("opt.MorningPeakStart.D") },
                { m_S.GetOptionLabelLocaleID(nameof(Setting.MorningPeakEnd)), T("opt.MorningPeakEnd.L") },
                { m_S.GetOptionDescLocaleID(nameof(Setting.MorningPeakEnd)), T("opt.MorningPeakEnd.D") },
                { m_S.GetOptionLabelLocaleID(nameof(Setting.EveningPeakStart)), T("opt.EveningPeakStart.L") },
                { m_S.GetOptionDescLocaleID(nameof(Setting.EveningPeakStart)), T("opt.EveningPeakStart.D") },
                { m_S.GetOptionLabelLocaleID(nameof(Setting.EveningPeakEnd)), T("opt.EveningPeakEnd.L") },
                { m_S.GetOptionDescLocaleID(nameof(Setting.EveningPeakEnd)), T("opt.EveningPeakEnd.D") },
                { m_S.GetOptionLabelLocaleID(nameof(Setting.NightStart)), T("opt.NightStart.L") },
                { m_S.GetOptionDescLocaleID(nameof(Setting.NightStart)), T("opt.NightStart.D") },
                { m_S.GetOptionLabelLocaleID(nameof(Setting.NightEnd)), T("opt.NightEnd.L") },
                { m_S.GetOptionDescLocaleID(nameof(Setting.NightEnd)), T("opt.NightEnd.D") },

                { m_S.GetOptionGroupLocaleID(Setting.GroupGeneral), T("grp.GroupGeneral") },
                { m_S.GetOptionLabelLocaleID(nameof(Setting.Enabled)), T("opt.Enabled.L") },
                { m_S.GetOptionDescLocaleID(nameof(Setting.Enabled)), T("opt.Enabled.D") },
                { m_S.GetOptionLabelLocaleID(nameof(Setting.ManageVehicleCount)), T("opt.ManageVehicleCount.L") },
                { m_S.GetOptionDescLocaleID(nameof(Setting.ManageVehicleCount)), T("opt.ManageVehicleCount.D") },
                { m_S.GetOptionLabelLocaleID(nameof(Setting.CleanUninstall)), T("opt.CleanUninstall.L") },
                { m_S.GetOptionDescLocaleID(nameof(Setting.CleanUninstall)), T("opt.CleanUninstall.D") },
                // Confirmation-dialog body. Without this the destructive button's [SettingsUIConfirmation] prompt shows
                // a RAW LOCALE KEY instead of a warning — unacceptable for an action that clears every timetable.
                { m_S.GetOptionWarningLocaleID(nameof(Setting.CleanUninstall)), T("opt.CleanUninstall.W") },
            };
        }

        public void Unload() { }
    }
}
