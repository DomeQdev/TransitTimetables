import { ModRegistrar, getModule } from "cs2/modding";
import { Safe } from "mods/safe";
import { TimetableEditor, TransitButton, TransitPanelHost, MigrationNotice } from "mods/transit-panel";

const register: ModRegistrar = (moduleRegistry) => {
    console.info("[TransitTimetables] register() running");

    // Inject the timetable editor into the native line info panel. The panel resolves each section through the
    // selectedInfoSectionComponents map (captured by value at module init), so we mutate the map entry directly
    // rather than moduleRegistry.extend. The editor self-hides unless a transport line is selected.
    try {
        const SECTIONS = "game-ui/game/components/selected-info-panel/selected-info-sections/selected-info-sections.tsx";
        const map: any = getModule(SECTIONS, "selectedInfoSectionComponents");
        const wrap = (typeName: string) => {
            const Orig = map[typeName];
            map[typeName] = (props: any) => (
                <>
                    {Orig ? <Orig {...props} /> : null}
                    <Safe><TimetableEditor /></Safe>
                </>
            );
        };
        wrap("Game.UI.InGame.LineSection");
        console.info("[TransitTimetables] line section wrapped");
    } catch (e) {
        console.info("[TransitTimetables] section wrap error: " + String(e));
    }

    // Floating departure board + toolbar button.
    try {
        moduleRegistry.append("GameTopRight", TransitButton);
        moduleRegistry.append("Game", TransitPanelHost);
        // One-time migration notice. Inside the Safe boundary so a cs2/ui shape change across a game patch degrades to
        // "no notice" rather than taking the HUD down with it.
        moduleRegistry.append("Game", () => (
            <Safe>
                <MigrationNotice />
            </Safe>
        ));
        console.info("[TransitTimetables] panel registered");
    } catch (e) {
        console.info("[TransitTimetables] panel error: " + String(e));
    }
};

export default register;
