# TransitTimetables — v0.5 update (DRAFT notes)

Status: **not yet shipped.** Working draft for the Paradox changelog box + store description. Trim to taste.
Last published version is **0.3.5**, so this release also carries everything drafted for 0.4.0, 0.4.1 and 0.4.2, which
were built but never published. The changelog below is written as one release covering that whole span.

⚠️ Before publishing, check `Properties/PublishConfiguration.xml`:
- bump `ModVersion` (currently still says 0.4.2)
- the Paradox changelog box has a **5000-character server-side limit** — it counts the XML-parsed text
- **escape any raw `&` as `&amp;`** or the publisher throws
- do **not** empty `Screenshot` / `Tag` / `ForumLink` / `ExternalLink` — the file is the complete store state

---

## Short changelog (for the Paradox changelog box)

**v0.5 — The board now tells the truth, and one setting instead of three**

Still BETA. Reviewed closely and play-tested, but this release changes how stop times are calculated and how vehicle
counts are decided, so please report anything odd on GitHub. You can step back without uninstalling: set "Maximum stop
time" to 0 for the old departure behaviour, set "Vehicle count" to "Do not let the mod decide" to keep counts to
yourself, and the master switch pauses the mod entirely.

**The printed board now matches the buses.** Previously the departure board and the vehicles were working from two
separate calculations of the same thing, and they drifted — by as much as 45 minutes on a long line. There is now one
number: the mod publishes what it actually used, and the board reads that. It never works it out a second time.

**Stop times are measured properly.** The old approach tried to time each stop individually, which could never work —
a bus rolls straight through a stop with nobody waiting, so those stops were never timed at all, and the times that did
arrive were biased. Every stop is now derived from one reliable number, the line's real loop time, spread across the
route. Times increase steadily along the line instead of jumping around, and a line that has not timed itself yet says
so on the board rather than pretending.

**"Realistic travel time" is gone as a setting — it is simply always on.** It cost nothing and had no downside, so
asking was pointless. Lines that have not measured themselves yet still use the game's estimate until they have.

**"Manage vehicle count" and "Provision fleet" are now one setting: "Vehicle count".** Two choices — *Let the mod
decide*, or *Do not let the mod decide*. Sizing a line from the game's estimate is no longer possible, because posting
real stop times while running an estimate-sized fleet advertises a schedule the line physically cannot keep.

⚠️ **This means more vehicles on existing cities.** The game's own travel estimate is well short of reality (roughly
1.7x on sparse lines, up to 2.5x on stop-dense ones), so a line sized from it runs fewer vehicles than your chosen
interval actually needs. Sized properly, a busy 12-stop bus line at an 8-minute peak headway goes from about 7 vehicles
to about 19. **Existing cities get a one-time notice explaining this before anything changes**, with the option to keep
vehicle counts to yourself. Transit vehicles cost nothing to run, but each depot only holds around ten — so if you see
"not enough vehicles" warnings, either build depot capacity or widen the line's interval. The interval is the real
control: ask for a tighter headway, get more vehicles.

**Turning the mod off now leaves your own settings alone.** Previously, switching off also wiped any vehicle count you
had set by hand with the game's own "Assigned Vehicles" slider. Off now undoes what the mod did and nothing else.

**Settings now save reliably.** A name clash between this author's mods meant settings could be written to the wrong
mod's file. Each mod now has its own name. If you had configured this mod before, check your settings after updating.

Also in this release, from the unpublished 0.4.x work:

- **Missed trips are caught up.** A scheduled departure that passes with no vehicle at the terminus is now covered by
  the next arrival instead of being cancelled, which used to leave vehicles parked while the line ran empty.
- **Vehicles no longer leave passengers at the door.** The game counts someone as a passenger the moment they start
  walking, so departing on time was ejecting everyone still crossing the platform. "Minimum stop time" became
  **"Maximum stop time"** and works the other way round: it caps how long a vehicle may wait past its posted departure
  for people already boarding. A vehicle with nobody boarding still leaves exactly on time.
- **Measured travel times are saved with your city**, so the board is right the moment you load instead of after a day
  or two of relearning.
- **"Remove all mod data from this save"** button for a clean uninstall with nothing left behind.
- **Steadier vehicle counts** — a reduction of one vehicle must hold for about 20 minutes before it is acted on, so a
  line whose loop sits near a multiple of its interval stops buying and selling the same bus twice an hour. Adding is
  never delayed.
- Assorted fixes: vehicles disappearing mid-route, lines shedding below their target, and retirements at a shared stop
  leaving nothing to cover the next departure.

---

## User guide (for the store description "how it works" section)

### What this release changes for you

**If you already use the mod:** you will see a one-time notice the first time you load a city, explaining that vehicle
counts are about to be sized properly and offering to leave them alone. Read it before clicking. Your timetables,
terminus choices and measured travel times are all preserved.

**If you are new:** nothing to configure. Set a line's first departure and its intervals; everything else is automatic.

### The one setting worth understanding

**Vehicle count** (Options → General)

- **Let the mod decide** *(recommended, default)* — every timetabled line is sized by measuring how long its route
  really takes, so it can actually hold the interval you set. No attention needed. In this mode the mod owns the count
  on every timetabled line, and the game's own "Assigned Vehicles" slider will not stick, because the mod re-applies
  its own number.
- **Do not let the mod decide** — the mod hands every line back and never touches counts again, so you or a dedicated
  fleet mod can own them. Departure holds still work exactly the same; a line with too few vehicles simply runs a wider
  gap than the interval you set.

### If a line wants more vehicles than you want to give it

Widen its interval. That is the real control, and it keeps the timetable honest. Pinning a smaller number by hand would
just mean the posted times get missed.

### What to expect

- **It learns each line automatically.** A new line uses the game's estimate until it has timed a few real loops, and
  the board says "estimated, not yet measured" while that is true.
- **Per city.** Measured on your roads, in your traffic — not a baked-in number.
- **Reversible.** Every setting can be turned off, and the mod restores what it changed.
- **Slow-time mods** (Realistic Trips / Time2Work): still supported — enable the compatibility setting and the
  correction works on top of it, since it is measured in simulation frames rather than clock minutes.

---

## Notes to self (not for the store)

- The previous draft of this file claimed provisioning "costs money" and "twice the upkeep". **That was wrong** —
  transit vehicles are not charged for on spawn, have no upkeep field, and increase ticket income. The real costs are
  depot slots (about 10 per depot) and `NotEnoughVehicles` problem icons. Do not let that phrasing come back.
- The fleet increase is the single most likely source of "this mod broke my city" reports. Lead with it, do not bury it.
- Still open and deliberately NOT in this release: per-line per-window vehicle counts (designed and dropped — the
  reasoning is in PR #14), the options page split into everyday/advanced, and the per-line peak-hours gate.
