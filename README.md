# Transit Timetables

Even, reliable headways for public transport in **Cities: Skylines II**. You say how many vehicles a line runs; the mod keeps them evenly spaced. Works for buses, trams, metros, trains, ferries and aircraft.

**Paradox Mods:** https://mods.paradoxplaza.com/mods/150546/Windows

## What it does
Open a line, switch its service plan on, and set the number of vehicles it runs at **peak**, **off-peak** and **at night** (plus an optional per-line custom peak). Then:

- The mod provisions exactly that many vehicles and raises the game's vehicle-count ceiling so the fleet isn't clamped.
- At the **terminus**, a vehicle is held until one headway after the previous one left — so a bunch is broken up instead of going round again. `headway = round trip ÷ vehicles`.
- At **every other stop**, the vehicle waits for boarding and alighting to finish and then leaves. Nothing else holds it.
- Optionally name a second timing point (**Terminus B**) and the same spacing rule applies at the other end of the line.
- Surplus vehicles finish their loop and retire at the terminus, never mid-route.

No service plan set = the line runs exactly like vanilla. A stop's panel shows how often each line comes and when it is next expected.

### Why vehicles, not a timetable
The mod used to ask for a headway and work out the fleet. That inverts badly in practice: the mod doesn't own the vehicles — the game spawns them from depots on its own schedule, lets them skip stops and retires them by odometer — so a printed departure grid was a promise it couldn't always keep, and every missed slot looked like a bug.

Vehicle count is the thing you actually control and pay for. The headway becomes a consequence of it, nothing is promised for a particular minute, and the only intervention left is the one that genuinely helps: even the spacing out where vehicles turn round.

Upgrading an existing city converts each line automatically — its old intervals become the number of vehicles that was already running to hold them, so service shouldn't change on the ground.

### A note on the round trip
CS2's own estimate of how long a line takes systematically undershoots the real, simulated loop (measured live at ~1.7x on sparse lines up to ~2.5x on stop-dense ones — the acceleration and braking at every stop that a free-flow estimate ignores). Since `headway = round trip ÷ vehicles`, the mod measures each line's real loop and uses that. It takes a few laps; the line panel says so while it's learning.

## Under the hood (for the curious / security-minded)
- **Pure ECS — no Harmony patches.** It writes `PublicTransport.m_DepartureFrame` to hold a vehicle at a timing point (and, at ordinary stops, only ever to *clear* vanilla's unbunching delay), drives the game's own vehicle-count policy for fleet sizing, and manages retirement via the vehicle's own route flags.
- **No network access at all** — nothing leaves your machine.
- **Filesystem:** writes only its own settings file and a log (`TransitTimetables.Mod.log`). Nothing else.
- **UI:** ships an in-game panel (React module, `.mjs`). It's UI only — it reads/writes the mod's own settings, no external calls.
- **Dependencies:** none beyond the base game.

Full source is here; the compiled DLL decompiles cleanly if you'd like to verify it matches.

## Build from source
Requires the official CS2 modding toolchain. `dotnet build -c Release` compiles the C#, builds the UI, and deploys to your local Mods folder.

## License
[MIT](LICENSE).

---

*Made with [Claude Code](https://claude.com/claude-code), Anthropic's agentic coding tool.*
