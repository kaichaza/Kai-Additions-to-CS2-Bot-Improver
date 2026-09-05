# KaiBotTactics

This project is an extension of @ed0ard's CS2 Bot Improver:
**[CS2-Bot-Improver](https://github.com/ed0ard/CS2-Bot-Improver)** by ed0ard,
and it needs ed0ard's bot improver to be installed since it plugs directly into his library. Provided there are no major breaking changes to ed0ard's project. This source shouldn't need recompiling each time CS2 releases a new update. But having said that I can't guarantee it won't break if I haven't played CS2 vs bots in a few weeks on my machine.

Kai Chaza, 4th September 2026, Sweden.

 I used Claude Code to create parts of this documentation, and I used Claude Code throughout my debugging sessions.

 Most of the code is my own but I got stuck on a lot of algorithms that weren't working so I used Claude Code to correct them when they were breaking unfixably. Like when the bots would just run straight into walls or get stuck trying to run off the map.

The plugin depends on the two below being installed in the Steam library, but ed0ard's plugin loads these anyway.

**[CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)** by
roflmuffin and contributors, the C# framework it is written in; and
**[Metamod:Source](https://github.com/alliedmodders/metamod-source)** by
AlliedModders, the loader underneath it all. All three, like this project,
are GPL-3.0.

What this library adds on top: teamplay. I made all the bots learn the different routes used in each game by making them each play 200 or so rounds per map, and then recording where every duel took place and where every death took place. This was done in casual mode with no utility use, so all fights are gunfights, no flashes, no nades, no AWP. 

I don't change bot aiming, all I change is pre-aiming, routes, and team behaviour, and retake and post plant behaviour, and introduce fake defusing and team comms and strats.

I remove all utility, and I remove all collision, and I remove friendly fire, since I am trying to solve a simpler, mathematical problem of gunfights and pre-aiming and strats.

The game mode I use is called gungame, which is 1st to 8 wins the match, guns only, no nades, no AWP: **`gungame_pro`**, a gun-only offline
defusal match against maximum-difficulty bots — no utility for anyone, no
snipers for anyone, real economy, collisions off (full description below).

**For ed0ard's current release (v1.4.1) and the Metamod and CounterStrikeSharp
versions it ships with, no editing and no compiling is needed**: the
repository carries the prebuilt `KaiBotTactics.dll`, its dependencies, and
fully trained data for four maps — run `setup_no_compile.bat` and play. If it
does not load on your particular install (usually a CounterStrikeSharp
version drift), it can always be recompiled against your own setup with
`setup_with_compile.bat`.

A full technical treatment — the graph mathematics, the learning pipeline, the
per-tick behaviour chain, every measured failure and its fix — lives in
`KaiBotTactics_architecture.md` alongside this file.

---

## Areas I tried to improve after playing the ed0ard CS2 Bot Improver, and what I changed

The stock bots were losing a lot of rounds for what I felt were really tactical and strat reasons, not so much bad aim. In fact, I actually play the aim on low, basically easy bots, because at higher levels it was really just inhuman reaction time winning every battle against me. These are
the key repairs, each described fully in the accompanying architecture document:

**They did not know the map.** Navigation-mesh data is unreachable from a
plugin, so the plugin records its own: a breadcrumb graph built from where
bots actually walk, saturating automatically when a map has been covered.
Routes, holding positions and pre-aim angles are all generated from recorded
play rather than authored by hand, based on about 200 or so rounds of recorded bot matches in casual mode gungame defusal mode.

**They arrived one at a time.** I didn't like how the CTs were trickling into the site one at a time on retakes, I was just picking them off, so I changed the logic to make them more coordinated so they could use cross-fire and hit the site at the same time. Bots that are close to the site will now wait for their friends, and bots that are far away will now run with knives out. The T side hit site in a coordinated manner when they attack, and the CTs retake in a coordinated manner. They engage in 2 v 1s wherever possible, separating out the enemy and overwhelming them.

**Post-plant was a huddle.** Terrorists now defend a plant as a spaced,
outward-facing ring anchored on the likely retake direction, with every
defender required to see the bomb, watch a known entry, and keep clear of its
team-mates' positions and arcs. Late arrivals hold an overwatch line onto the
bomb from outside instead of walking into a broken ring.

**The retake had no plan.** The Counter-Terrorist side now runs a four-phase
retake — rally, inspect, bait, commit — with a designated defuser, a
partitioned sweep of known lurk spots, a fake defuse tap to draw a hidden
player out, and the bots cover the defuser, facing outward in a circle, spread out
so that any Ts trying to attack the defuser have to deal with several friends first,
who are all standing in a ring around the defuser. 

**They could not handle a human.** A human can potentially find the one angle bots handle
badly and farms it forever. The answer is a knowledge handicap, not an aim
handicap: after a delay, the bots are told where the human is, and they spend
that knowledge only on positioning and attention — which site to defend,
which doorway to watch, which angle to settle on. Line of sight is always
required before a bot aims at anything, and the duel itself is always fought
by the native AI. Basically, during retake, the CTs have real knowledge of where the single
human player in the lobby is, and they can pre-aim where the human player will likely come from.

---



## The features

### 1. A playbook of real strategies

Each team of bots uses a playbook to gain map control and to gather info: fast executes, split hits through two approaches, slow defaults that take map control first; on the defence, site stacks, spread holds, early aggression, and playing the bomb rather than the site. Play selection uses a shuffled bag rather than repeating winning plays to avoid a predictable pattern of e.g. only hitting one site in the same way, so the bots stay varied, which is more fun when practicing alone with bots.

### 2. Audibles for mid-round plan changes

Each team has an in-game leader, who calls the plays as the round progresses, and the plays are visible in the team chat in green text. This adapts to incoming information about the round, and is compared to the original strategy determined for the round. The in-game leader calls an audible when the plan diverges from what was planned, or when the team is down unexpectedly: a site turns out to be stacked, the bomb ends up somewhere unexpected, the side goes down bodies. Site switches, rotations (including deliberate fake rotations), pull-backs, and the guard call when the bomb is loose on the ground. 

### 3. Coordinated executes

 The T side hits sites the way a team of humans does: decoys peel off first to put noise in the wrong place, the main group stages short of the site and waits for each other, and then everybody commits at the same time. Each side has a stable leader for the whole match who anchors the synchronisation. With a human carrying the bomb, the bots react to the site where you've actually taken the bomb and commit to holding angles on the site, they have the capability of taking out their knives and running if they are too far away when the bomb is planted.

### 4. A four-phase retake with a designated defuser

When the bomb has been planted, the CT side runs a retake plan: **rally** (gather on a ring short of the site and enter together, as a crossfire), **inspect** (sweep the site), **bait** (the fake defuse, sometimes, but not always), **commit** (the real one). One bot is designated defuser — and the rest form a defensive ring around the defuser.

### 5. Fake defusing

During the bait phase, the defuser walks to the bomb and will occasionally tap the defuse to fake out the Ts — a genuine begin-and-abort that produces the real defuse sound — to draw a hidden lurker out of the one corner the sweep could not see into.

### 6. Clearing angles and lurk-spot sweeps

Before anyone touches the bomb, the site is inspected properly. The plugin works out where a lurker could plausibly be hiding near this plant — built from the same learned duel geometry — divides those spots between the covering bots in bearing arcs, and sweeps them, announcing each spot in team chat as it is cleared.

### 7. A ring around the defuser

While the defuse runs, the covering CTs form a spread, outward-facing ring around the bomb spaced apart in distance and in bearing around the clock face, each watching outward toward the approaches. Any Terrorist trying to break the defuse has to win several different gunfights from several different angles.

### 8. T-side site holding after the plant

The mirror image on offence: Terrorists defend a plant as a spaced, outward-facing ring anchored on the likely retake direction, with every defender required to see the bomb, watch a known entry, and stay clear of team-mates' positions and arcs. Late arrivals take an overwatch line onto the bomb from outside rather than walking into a broken ring.

### 9. Pre-aiming from learned duel geometry

Every recorded death is a measurement: who could see whom, from where. The bots use that bank of evidence to pre-aim the angles that history says matter from wherever they are standing. Line of sight is always required before a bot aims at anything, and the duel itself is always fought by the native AI since that already has built in counter-strafing, reloading and gradual aim micro adjustments.

### 10. Weapon awareness and picking up guns

The plugin also addresses empty guns by having every bot who sees a gun on the floor actually remember where it was last seen, allowing team mates to go back there to pick it up. A bot that runs out of bullets can go and resupply from the floor, and a weapon seen by anybody is remembered by everybody — "there is an AK on the ground at Mid Doors" is a real callout that stays true after the spotter has moved on or died.

### 11. Knifing when out of ammo

A dry bot caught in a fight draws the knife and commits to it — moving erratically at close range. If the rush lands, the bot takes the gun off whoever it killed. If not, it at least served as a stall.

### 12. Bot team comms and callouts

The bots use fixed radio names — Wei, Bullseye, Tank and Private, and they make callouts throughout the game in team chat: the play being run, the audibles, the execute stages, the retake release ("all in together, now"), what each covering bot is watching, spots as they are swept, the fake defuse and the commit. Position callouts come from per-map anchor tables ("Triple", "Palace"). Chat volume is adjustable at runtime.

### 13. A fair answer to the human problem

A human can find the one angle bots handle badly and farm it forever. The answer here is deliberately a knowledge handicap, not an aim handicap: after a 30 second delay, the bots learn where the human is, and they use it to improve their positioning and pre-aiming — which site to defend, which doorway to watch, which angle to settle on. They don't have wall hacks, and they don't wall bang, they just have a sense of the ping of where the human player is playing from and they can pre-aim that angle. Every duel is still fought by the native AI on even terms. It should still feel like playing against a team that reads the game.

### 14. Everything is observable and tunable

Every decision the bots make is logged with the function that made it and the values it used, to a per-session file you can read back afterwards — the log is the debugger by design. Runtime commands tune the retake, the rotations, the bomb guard, the weapon awareness, the comms volume and more, without a rebuild.

### 15. Bots learnt all the walkable paths on the map

Each of the four shipped maps was trained by having the bots play roughly 200 unguided rounds while the plugin recorded where they actually walked, where every duel happened and where every death happened. From that recorded evidence the breadcrumb algorithm generates the navigation graph, the routes, the holding positions and the pre-aim angles.


## Current limits

The four shipped maps are essentially single-plane layouts, and much of the plugin reasons about distance flat; vertically stacked maps like Nuke and Vertigo could still be trained on, but I would expect poorer proximity judgement there. On a map with no data, the bots start close to stock and learn as they play. 

This project is based on ed0ard's CS2-Bot-Improver, CounterStrikeSharp and Metamod:Source and inherits both their strengths and their occasional version drift.

---


## Callouts: what the bots say in game

The squad talks in team chat.

Bot names assigned by the game change between rounds, so the squad hands out
four fixed radio names instead — **Wei, Bullseye, Tank and Private** — sticky
by slot and held for as long as the bot lives, with a rank prefix that follows
whichever side the human is on. On the CT side they are all called Op something, and on the T side they are all called Cde something.

What they call out:

- **Plans and audibles.** The play being run, site switches when a site turns
  out to be stacked, rotations (including deliberate fakes), pull-backs when
  the side is down bodies, and the guard call when the bomb is loose on the
  ground.
- **The execute.** Decoys peeling, the group staging, and the commit call when
  everyone goes.
- **The retake.** The rally release ("all in together, now"), what each
  covering bot is watching, the sweep — spots announced as they are cleared —
  the fake defuse, and the commit.
- **Positions.** Callouts are derived from geometry against per-map anchor
  tables ("Triple", "Palace"), degrading gracefully to bearings and distances
  on maps without a table.

Chat volume is controlled at runtime with `kai_comms`, and every call the
bots make is also written to the session log, so a round can be read back
afterwards exactly as the squad experienced it.

---

## The game mode: gungame_pro, a gun-only offline defusal match

This plugin was developed and is tuned under `gungame_pro.cfg`: an offline
defusal match against bots where **no grenades or utility exist for anyone,
humans included; no sniper rifles exist for anyone, humans included; bots are
locked at maximum difficulty and never auto-nerfed; and the bots play the
objective** rather than wandering. Friendly collisions are off via the casual
base mode's defaults, so the plugin's synchronised group movement — rally
rings, execute stacks, same-tick releases through chokes — never jams in a
doorway.

The config lives in the game's cfg folder
(`game/csgo/cfg/gungame_pro.cfg` under the location in
`counterstrike_location.txt`) and **nobody has to type anything**: the plugin
executes it automatically a few seconds after every map load. `gungame_pro`
is the built-in default of the plugin's autoexec mechanism, so a correctly
installed setup runs the mode from the first map. The `kai_autoexec` command
manages it at runtime — `kai_autoexec <name>` points it at a different
config, `kai_autoexec now` re-runs it immediately, `kai_autoexec delay <s>`
adjusts the timing, and `on`/`off` toggle it.

What that environment means for the plugin:

- **No utility** means the tactics never depend on smokes, flashes or
  mollies. Fakes are made with movement and sound — decoy runs, the fake
  defuse tap — and defensive spacing assumes bullets, not lineups.
- **No snipers** (and no helmets with `mp_free_armor 2`) keeps every duel a
  rifle duel, which is what the learned duel geometry is trained on.
- **A real economy** (`mp_startmoney 800`, default after-round money, the
  shorthanded bonus removed) keeps round wins meaningful, which is what makes
  the plugin's economy handling worth having. The one deliberate asymmetry is
  `sv_bot_buy_armor_weight 1000`, so bots reliably turn up armoured.
- **Match structure**: 14 rounds with halftime, clinching and overtime
  enabled, eight seconds of freezetime (which the plugin's planners use for
  play calling and the position solver), and death-cam locked to your own
  team so you cannot watch the enemy execute onto you.

The config ends with `mp_restartgame 1` so everything takes effect in a clean
round. For unattended data gathering on a new map, the commented variant at
the bottom of the file (`mp_maxrounds 300`, `mp_winlimit 150`,
`bot_defer_to_human_items 0`) pairs with the plugin's `kai_ghost on`.

---

## Trained maps, and training your own

**Shipped fully trained: de_cache, de_dust2, de_inferno and de_mirage.** The
repository's data folder carries each map's navigation graph, duel samples,
generated routes, holding positions, callout tables and play records, all at
the mature learning stage — install and the bots play these four maps at full
strength from the first round.

**How the training was done.**  the plugin learns by playing 200 unguided rounds of all bot matches with the human player as a spectator.
Bot-only matches run unattended (with `kai_ghost on` so no human contaminates
the map data) while the breadcrumb recorder builds a navigation graph from
where bots actually walk and every death is banked as duel geometry — who
could see whom, from where. Once the graph saturates and the sample quotas
fill, routes, holding posts, pre-aim angles and plays are generated from the
recorded evidence, and the map advances through three stages — fresh, mapped,
mature — visible at any time with `kai_maturity`. Cache matured after 198
rounds across 11 matches; Inferno after 228 across 12; on both, the graph and
sample quotas were met around the 150-round mark. Once mature, a map stops
recording and plays from what it learned.

**How many rounds a new map needs.** Budget roughly **150 rounds of bot-only
play for the training to become relevant** — that is where the shipped maps
hit their graph and sample ceilings and reached the *mapped* stage, where the
generated tactics actually take over from stock behaviour — and around **200
or more to mature**. At 14 rounds a match that is a lot of matches; the
data-gathering variant in `gungame_pro.cfg` (300-round matches) covers it in
one or two unattended sessions.

**No guarantees on untrained maps.**
The learning pipeline is map-agnostic and has worked on everything it has been
given, but the four shipped maps are all essentially single-plane layouts.
Much of the plugin measures proximity flat — carrier reads, staging readiness,
the rally ring, site radii are largely XY distance — which is a fine estimate
when a map's fights happen on one level and a misleading one when they stack. 
There is a Z component, for stairs and raised surfaces, which largely works,
but I haven't trained the bots on Nuke and Vertigo deliberately because sites 
and approaches sit directly above and below each other, so a flat distance 
can call a bot "close" to a bomb it is two floors from. The maps will train
 and the bots will play, but expect the distance and proximity estimates to 
 make poorer decisions there.

For the operational side of training — the recorder, regeneration and
hand-authoring commands — see "First run on a fresh map" below.

---

## Requirements

| Component | What it is | Where |
|---|---|---|
| Counter-Strike 2 | the retail game itself; this setup runs as a local offline server against bots | Steam |
| Metamod:Source (2.x, CS2 branch) | the loader everything else sits on | AlliedModders |
| CounterStrikeSharp | the C# plugin framework (this project builds against API 1.0.371) | github.com/roflmuffin/CounterStrikeSharp |
| CS2-Bot-Improver | ed0ard's bot foundation; provides the BotController and RayTrace shared APIs this plugin calls | github.com/ed0ard/CS2-Bot-Improver |
| .NET SDK 10.0 | only needed if you are compiling from source | dot.net |

The reference setup installs the whole stack into the retail game's own
`game/csgo/` folder and plays offline with bots: start a map, and the plugin
brings the mode up by itself — `gungame_pro.cfg` is executed automatically a
few seconds after load, no console typing required. A SteamCMD dedicated
server works the same way with the same folder layout, and is the better home
for unattended map learning, but it is not required.

---

## Four things worth knowing

**1. Edit `counterstrike_location.txt` before running.** The file
ships with my reference machine's path in it, and the setup scripts assume CS2 is
installed on the D drive. If your CS2 is on C drive, then you will need to edit this file
first; every copy the scripts make derives from this line.

**2. The plugin takes over your server config by default.** `gungame_pro` is
the built-in autoexec: it runs a few seconds after **every** map load, out of
the box. If you already run your own configs and settings, the plugin will
silently override them each map until you either point the autoexec at your
own config (`kai_autoexec <name>`) or turn it off (`kai_autoexec off`).
Relatedly, the setup scripts copy `gungame_pro.cfg` into `game/csgo/cfg/` and
will overwrite a file of the same name — back yours up first if you have one.

**3. The prebuilt DLL is built against CounterStrikeSharp API 1.0.371**,
matching ed0ard's release. If your CounterStrikeSharp is meaningfully newer
or older, the plugin may not load at all — the symptom is KaiBotTactics
missing from `css_plugins list` with no other drama. The fix is to build
against your own install: `setup_with_compile.bat` exists for exactly this.

**4. The setup scripts are Windows batch files.** I have not tested on linux.


---

## counterstrike_location.txt

A single file in this project's root holds the location of your
Counter-Strike installation. The install steps below, and any copy script you
write around them, read this file to find where the game lives, so the path
is typed once and only once.

Format: lines beginning with `#` are comments; the first line that is not a
comment is the absolute path to the folder that **contains the `game`
directory** of your Counter-Strike installation. Edit it before installing.

```
# Retail game in a Steam library (the reference setup)
D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive

# SteamCMD dedicated server examples
# C:\cs2server
# /home/steam/cs2server
```

Everything below writes into `<that path>/game/csgo/...`.

---

## Installation

### The quick way: the setup scripts

Two scripts sit in the project root; both read `counterstrike_location.txt`
to find the game, so edit that first.

- **`setup_no_compile.bat`** — installs the **prebuilt** plugin. No SDK, no
  build. It checks Metamod, CounterStrikeSharp and CS2-Bot-Improver are in
  place, then copies the shipped `KaiBotTactics.dll`, its `deps.json`, the
  whole `kai_tactics` data folder (fully learned maps included), and
  `gungame_pro.cfg` into the game. If you are already running ed0ard's
  plugins, this is the entire installation: the DLL loads and plays alongside
  his as-is.
- **`setup_with_compile.bat`** — for fresh builds from source. Everything the
  other script does, plus: fetches the two shared reference assemblies from
  your installed game into `libs\`, runs `dotnet build -c Release` (needs the
  .NET 10.0 SDK), refreshes the repository's prebuilt payload with the new
  DLL, and then installs it.

Both scripts protect learned data: files already in the game's `kai_tactics`
folder are never overwritten by older repository copies, so re-running setup
after playtests does not clobber what your bots have learned.

### Repository layout

The project folder mirrors the game tree, so the copy is a straight overlay:

```
CS2KaiAdditionstoBotImprover\
    readme.md
    counterstrike_location.txt
    setup_no_compile.bat
    setup_with_compile.bat
    kai_bot_tactics.csproj
    kai_*.cs                               <- the sources
    libs\                                  <- shared reference DLLs (build only)
    game\
        csgo\
            cfg\gungame_pro.cfg            <- the mode config
            addons\counterstrikesharp\plugins\
                KaiBotTactics\             <- the prebuilt payload
                    KaiBotTactics.dll
                    KaiBotTactics.deps.json
                    kai_tactics\           <- the data directory
                        breadcrumbs\       <- navigation graphs, per map
                        callouts\          <- callout anchor tables
                        learned\           <- raw duel samples
                        maturity\          <- learning stage, per map
                        patrol_routes\     <- generated route books
                        playbook\          <- play records (de_<map>.plays.json)
                        logs\              <- session logs land here
                        de_<map>.json      <- per-map tactics (+ .backup)
```

The project's `game\` folder is a verbatim overlay of the game's own `game\`
folder — the payload already sits at its final in-game path, so installing is
nothing more than copying `game\*` on top of the install. That overlay copy,
plus the cfg, is all the scripts do.

### By hand

Paths below are relative to the location in `counterstrike_location.txt`.

**1. Install Metamod:Source.** Extract the CS2 build of Metamod:Source into
`game/csgo/`, producing `game/csgo/addons/metamod/`. Add the Metamod entry to
`game/csgo/gameinfo.gi` as its instructions describe (one `Game csgo/addons/metamod`
line in the SearchPaths block — game updates sometimes revert this file, so
re-check it after updates). Verify by typing `meta list` in the server
console.

**2. Install CounterStrikeSharp.** Extract the CounterStrikeSharp "with
runtime" release into `game/csgo/`, producing
`game/csgo/addons/counterstrikesharp/`. Verify with `css_plugins list` in the
server console.

**3. Install CS2-Bot-Improver.** Follow ed0ard's installation instructions.
When it is in place you will have, among others,
`game/csgo/addons/counterstrikesharp/shared/BotControllerApi/BotControllerApi.dll`
— this plugin depends on that shared assembly existing at runtime.

**4. Install KaiBotTactics.** Copy the repository's prebuilt payload folder
`game\csgo\addons\counterstrikesharp\plugins\KaiBotTactics\` — the DLL, its `deps.json`, and the
`kai_tactics` data directory shown in the layout above — to:

```
game/csgo/addons/counterstrikesharp/plugins/KaiBotTactics/
```

The folder name must be exactly `KaiBotTactics`, matching the DLL name —
CounterStrikeSharp loads `plugins/<Name>/<Name>.dll` and nothing else.

The `kai_tactics` data directory is where everything the plugin learns is
kept, organised into the subfolders shown in the layout: breadcrumb graphs,
callout tables, raw samples, maturity records, route books, play records and
logs, with the per-map tactics files (and their automatic `.backup` copies)
at its root. Shipping it gives you fully trained bots on the included maps
from the first round; starting without it is also fine — see "First run on a
fresh map" below.

**5. Install the mode config.** Copy `gungame_pro.cfg` from this repository
into `game/csgo/cfg/`. Nothing further: the plugin executes it automatically
a few seconds after every map load (`gungame_pro` is the autoexec default;
`kai_autoexec` changes or disables this at runtime).

**6. Start and verify.** Load a map with bots, then in the console:

```
meta list               // Metamod attached
css_plugins list        // KaiBotTactics should be listed and loaded
                        // ~3s after load, the prohibited items print by
                        // name: that is the mode config auto-executing
kai_maturity            // shows the learning stage of the current map
kai_log 1               // normal logging (2 for full per-tick detail)
```

If the item printout ever fails to appear, `kai_autoexec now` re-runs the
config immediately and replies with what it executed.

A per-session log appears under
`plugins/KaiBotTactics/kai_tactics/logs/<map>_<timestamp>.log`.

**7. Hot reloading.** `css_plugins reload KaiBotTactics` swaps a new DLL in
without restarting; the plugin opens a fresh log file on reload and its data
files are re-read per map, so iteration is quick.

---

## Building from source (fresh installs)

`setup_with_compile.bat` automates all of this — including fetching the two
shared assemblies into `libs\` from your installed game — so the steps below
are the by-hand reference.

**1. Install the .NET 10.0 SDK** for your platform from dot.net. Verify with
`dotnet --version`.

**2. Provide the two shared reference assemblies.** Create a `libs/` folder
next to `kai_bot_tactics.csproj` and copy into it, from your installed game
(the path in `counterstrike_location.txt`):

```
game/csgo/addons/counterstrikesharp/shared/BotControllerApi/BotControllerApi.dll
game/csgo/addons/counterstrikesharp/shared/RayTraceApi/RayTraceApi.dll
```

These are referenced with `Private=false` on purpose: the server runtime
already has both loaded, and shipping second copies would produce two
distinct types with the same names, at which point the capability lookups
silently return null and features such as the fake defuse stop working. Do
not copy these DLLs into the plugin output folder.

**3. Build.**

```
dotnet restore
dotnet build -c Release
```

The CounterStrikeSharp.API package (1.0.371) restores from NuGet
automatically. The output lands at
`bin/Release/net10.0/KaiBotTactics.dll`; copy that single file into
`plugins/KaiBotTactics/` as in step 4 above. If ed0ard's release moves to a
newer CounterStrikeSharp version, bump the package version in the csproj to
match his, or the API surface can drift between his plugins and this one.

---

## First run on a fresh map

On a map with no data files, the bots start close to stock and the plugin
records: the breadcrumb graph fills in as bots walk, deaths become duel
geometry, and the learning passes through three stages — fresh, mapped,
mature — visible at any time with `kai_maturity`. Useful commands for the
learning period:

```
kai_crumbs              // breadcrumb recorder status and controls
kai_learn build         // regenerate spots/routes from the banked samples
kai_solve               // pre-compute the best holding positions (freezetime)
kai_ghost on            // exclude humans from map learning, for unattended mapping
kai_routes              // inspect the generated route book
```

Leaving a server running bot matches unattended with `kai_ghost on` is the
fastest way to take a new map to maturity. Once matured, recording stops and
the map plays from what it learned; the raw sample bank is retained, so
`kai_learn build` can regenerate the derived files at any time.

Hand authoring is also supported when you want to teach a specific position:
`kai_thold`, `kai_ctclear` and `kai_preaim` record your current position as a
T post-plant hold, a CT clearing angle, or a pre-aim trigger respectively,
and `kai_save` writes the map's tactics file.

---

## Runtime commands

```
kai_enable       enable or disable all overrides at runtime
kai_log          verbosity: 0 errors, 1 info, 2 verbose
kai_logfile      per-map log file on/off
kai_comms        what the bots say in team chat
kai_maturity     learning stage for the current map
kai_plays        tactical controller and its win record
kai_retake       tune the CT retake director
kai_rotate       tune how Ts abandon a hold under fire
kai_guard        tune how CTs hold a loose bomb
kai_arsenal      weapon awareness: dropped guns, dry bots, knife rushes
kai_autoexec     config executed automatically on every map load
kai_save / kai_reload / kai_list    tactics file I/O and inspection
```

Every data file write goes through a loader that takes a backup first, so an
experiment can always be rolled back.

---

## Troubleshooting

- **KaiBotTactics missing from `css_plugins list` entirely** — the usual
  cause is a CounterStrikeSharp version mismatch with the prebuilt DLL (see
  "Read this first", note 3). Rebuild against your own install with
  `setup_with_compile.bat`.
- **Plugin listed but features missing** — almost always the shared
  assemblies: confirm CS2-Bot-Improver is installed and that you did not copy
  `BotControllerApi.dll` or `RayTraceApi.dll` into the KaiBotTactics folder.
- **Your own server settings keep being replaced** — that is the autoexec
  doing its job on every map load; see "Read this first", note 2.
  `kai_autoexec off` or point it at your own config.
- **No log file** — `kai_logfile on`, and check the server user can write to
  `kai_tactics/logs/`.
- **Nothing loads after a game update** — re-check the Metamod line in
  `gameinfo.gi`, then `meta list`.
- **Bots wander like stock bots on a map** — that map has no data yet; see
  "First run on a fresh map".
- **Something behaves oddly mid-round** — `kai_log 2` and read the session
  log: every decision carries the function that made it and the values it
  used. The log is the debugger; that is a design principle, not an accident.

---

## License

KaiBotTactics is released under the **GNU General Public License v3.0** — the
full text is in the `LICENSE` file. This matches the licensing of the whole
stack it builds on: CS2-Bot-Improver, CounterStrikeSharp and Metamod:Source
are all GPL-3.0. Copyright (C) 2026 Kai Chaza, Sweden. This program comes with absolutely no warranty; see sections 15 and 16 of the license.

---

## Attribution

This project is an addon to the below projects.

**ed0ard — CS2-Bot-Improver** (github.com/ed0ard/CS2-Bot-Improver). The
foundation this plugin extends: the bot controller, the aim improver, the
shared BotController and RayTrace APIs, and the project conventions this
codebase mirrors. This project loads alongside this plugin.

**CounterStrikeSharp** (roflmuffin and contributors). The C# plugin framework for hooks, listeners, entities, the plugin lifecycle.

**Metamod:Source** (AlliedModders).

**The mathematics this project builds on**:

- A* heuristic search — Hart, Nilsson and Raphael (1968), with Russell and Norvig's treatment.
- Polyline simplification for route generation — Ramer (1972) and Douglas and Peucker (1973).
- Leader (sequential) clustering for duel-geometry learning — Hartigan,
  *Clustering Algorithms* (1975).
- Circular means and directional statistics for facing angles — Mardia and Jupp, *Directional Statistics*.
- Steering behaviours for characters — Reynolds (1999).
- Sampling and decision-making under uncertainty, including why the play
  selector does not optimise win rate — Sutton and Barto, *Reinforcement Learning: An Introduction*.
- General graph algorithms and data structures — Cormen, Leiserson, Rivest and Stein; Skiena; Sedgewick and Wayne.

