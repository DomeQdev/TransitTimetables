# Live store state — TransitTimetables (Paradox Mods 150546)

Captured **2026-08-06** from https://mods.paradoxplaza.com/mods/150546/Windows

**Why this file exists.** `PublishConfiguration.xml` is the COMPLETE store state on publish, not a patch:
whatever it contains replaces what is live, and whatever it omits is destroyed. The live page has drifted a
long way from the config through manual edits on the website. This is the record of what a publish would
currently overwrite. See `reference_cs2_modpublisher_destructive` in the project memory.

> Caveat: the description below is the **rendered text** of the page. Paradox Mods accepts a minimal markdown
> subset, and the exact source markup (heading syntax, link syntax) is not recoverable from the rendered DOM.
> The wording, ordering and emoji are exact; the markup around them may need re-deriving.

---

## Metadata (live)

| Field | Live value |
|---|---|
| ID | 150546 |
| Mod version | 0.5.3 |
| Suggested game version | 1.6.* |
| Subscribers | 10,139 |
| Likes | 188 |
| Size | 120.79 KB |
| Created | 2026-07-08 20:00 |
| Last updated | 2026-08-03 19:55 |
| Tags | Code Mod |
| External links | GitHub, Paradox Forums (both present and working) |

## Screenshots (live gallery, 5 items)

The live gallery does **NOT** match the config. These filenames indicate they were uploaded manually
through the website, not published from the repo:

```
3e8d3860-8f64-11f1-9f2a-0922bbbdeab4_image_2026-08-03_195333406.png
6d0965b0-8f64-11f1-9f2a-0922bbbdeab4_image_2026-08-03_195451397.png
8547c1d0-8f64-11f1-9f2a-0922bbbdeab4_SS-Bus.jpg
8a0067e0-8f64-11f1-9f2a-0922bbbdeab4_SS-Train.jpg
screenshot_01.jpg
```

Cover image: `content/covers/cover_27.jpg`

The config instead lists four: `01-banner.jpg`, `02-bus.jpg`, `03-train.jpg`, `04-options.jpg`.
**A publish replaces the live five with the config's four.**

---

## Description (live, verbatim rendered text)

Turn any public-transport line into a scheduled service. Works for every passenger type: buses, trams, trains, metros, ferries and aircraft.

WHAT'S NEW IN 0.5.3:

The realism checkboxes from before 0.5 are back on the Options page, including the one a lot of you were using: let the mod manage vehicle counts, but size them from the game's own estimate instead of the measured loop. High-frequency lines no longer have to be provisioned for their full real loop to be managed.
Both realism settings start off. If you had them on before 0.5 they come back on by themselves. If you started during 0.5.x your vehicle counts will fall, and switching "Provision fleet for real travel time" back on restores them.
A one-time notice explains this when you load a city with timetables, and can switch both on for you.
Posted stop times and vehicle counts are now worked out from the same measurement.
Three settings that had only ever been in English are now translated into all 11 languages.

Earlier in 0.5.2.2:

Select a vehicle to see where it stands against the schedule and when it is due at its next stop. A vehicle held at a stop says it is waiting for its departure rather than reporting it as a delay.

Earlier in 0.5.1:

Vehicle counts stop over-shooting. The mod times how long a line really takes to go round, and on a busy line it could mistake two loops for one and then average that mistake in permanently. It now takes the middle reading rather than the average, so a stray bad timing no longer drags the answer up.
Lines stop suddenly asking for far more vehicles for no visible reason. A safety net meant for edited routes was firing on healthy lines and throwing away everything they had learned. It now keeps the learning and simply recalibrates.
The TIMETABLE panel no longer appears twice after loading a second city without restarting the game.

Earlier in 0.5.0:

Your settings are saved properly now. All of my mods shared one internal name for their settings, and the game resolves them by that name, so all but one wrote nowhere and anything you changed came back as default the next session.
Vehicle counts are now a single setting with two states: the mod sizes your fleets, or you do.
Realistic travel time is no longer optional. It is always on, it costs nothing, and it is the point of the mod.
Fleet growth is staggered. Extra vehicles used to leave the depot together and enter the line as one clump, which is the bunching this mod exists to prevent. They now arrive one per departure interval.
Vehicles no longer throw off passengers who are already boarding, and "Maximum stop time" caps how long they will wait for them.
Missed departures are caught up, measured travel times survive quitting, and there is a one-click clean uninstall.

Full detail in the changelog.

---

🚌 Transit Timetables

Your buses don't run to a schedule. They run to a vibe.

One vehicle bunches up behind another, three arrive at once, then nothing for ten minutes. Transit Timetables replaces that with real departure times — the kind you could actually print on a sign.

🕐 Set a timetable in about ten seconds

Open any line, flip the timetable ON, and set:

⏰ First departure of the day

📊 Peak / off-peak / night intervals — different frequencies for different hours

📍 A terminus — your timing point, where early vehicles wait for their minute and surplus vehicles retire after finishing their loop, never dumping passengers mid-route

That's it. The mod works out how many vehicles the line needs and raises the game's vehicle cap so your service is never quietly throttled.

📋 A real departure board

Click any stop to see a printed timetable — every line serving it, every departure, from the first bus of the day. Rename a line and the board updates to match.

Night lines show night times. Day-only lines don't pretend to run at 3am. It just makes sense.

🌟 Per-line rush hours

Not every line rushes at the same time.

Any line can override the city-wide peak with its own two peak windows and its own interval — so your factory shift line, school run, or stadium shuttle can run its own rush hour while the rest of the network keeps to the standard one.

💙 This feature was contributed by incconu_two — thank you!

⚡ Optional: make it honest

The game thinks your metro loop takes 50 minutes. It really takes 150. It ignores braking, accelerating, and passengers actually boarding.

🎯 Realistic travel time (free) — the mod measures your line's true loop as it runs and posts real times. Measurements are now saved with your city, so they survive quitting.

🚏 Minimum stop time — buses 2 min, rail 5 min. A metro exchanging a full platform doesn't do it in two minutes.

💰 Provision fleet — size lines to their real loop. Costs money. Worth it.

🛑 Stop at every stop · 🤝 Hand vehicle counts to another mod · 🌙 Slow-time mod support

🧹 Uninstall without a trace

One button: "Remove all mod data from this save." Every line returns to vanilla, all mod data is deleted from your city. No residue, no regrets.

🙏 Credits

💙 Per-line custom peak windows — incconu_two

Community bug reports have driven several releases: the forced terminus stop, the mid-route spawn fix, and missed-trip catch-up. Report things. They get fixed.

🔗 Source — open source, MIT

🌍 Localized into 11 languages (machine-translated — corrections welcome!)

Disclaimer: Made and maintained with Claude Code Opus 4.8, Opus 5 and Fable 5 models.

---

## Divergences from `PublishConfiguration.xml`, and what a publish would do

1. **The whole rich body is missing from the config.** The config's `LongDescription` (8193 chars) carries the
   WHAT'S NEW / Earlier-in blocks and a plain-prose credits paragraph, but none of the emoji-headed marketing
   body above ("🚌 Transit Timetables", "Your buses don't run to a schedule…", the feature sections, "🙏
   Credits"). **A publish today would delete all of it.**
2. **`incconu_two` is a clickable GitHub link twice in the live page** (github.com/incconutwo). The config has
   the bare string "incconutwo" in prose, unlinked, spelled without the underscore.
3. **Screenshots**: five live, manually uploaded, different filenames; the config would replace them with four.
4. **Disclaimer wording**: live says "Made and maintained with Claude Code Opus 4.8, Opus 5 and Fable 5
   models"; the config says "Made with Claude Code's Opus 4.8 and Fable 5 models".

## Two factual errors in the LIVE description, worth correcting whenever it is next rewritten

- **"🚏 Minimum stop time — buses 2 min, rail 5 min."** The shipped settings are *Maximum* stop time,
  `MaxDwellRoad` / `MaxDwellRail`, and both default to **3**. The name, the direction and both numbers are wrong.
- **"💰 Provision fleet — … Costs money. Worth it."** No per-vehicle treasury cost has been found in the
  decompiled source: `ExpenseSource` has no transit-vehicle entry and depot upkeep is billed per building and
  flat. The real cost is depot capacity, not money. Whether transit vehicles cost anything at all is *not
  established*, but the claim as written is unsupported and discourages the setting that fixes the most
  commonly reported complaint.
