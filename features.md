# KaiBotTactics — Features

**Teamplay for Counter-Strike 2 bots, in a gun-only casual defuse mode, on four fully trained maps: Mirage, Dust2, Cache and Inferno.**

KaiBotTactics is an extension built on top of **[ed0ard's CS2-Bot-Improver](https://github.com/ed0ard/CS2-Bot-Improver)**, and it plugs directly into his library. His project provides the bot controller, the aim improver and the shared APIs this plugin calls every tick; without it, nothing here runs. What KaiBotTactics adds on top is the layer his foundation made possible: **teamplay**. I didn't edit the default aim settings outside of the recommended vpk files with botprofile.db. I made the bots a little easier to fight against since I'm pretty low elo in CS2 and I hardly ever play in ranked modes. What this plugin focuses on is team strats, play calling, positioning, routes, pre-aiming, team coordination, timing, cross-fire, post-plant behaviour, defuse faking, and defuser cover fire, and communication — the tactical game, not so much the mechanical one.

## My custom casual gungame mode, no snipers, no nades

Everything here was developed, trained and tuned for one specific way of playing: **`gungame_pro`**, an offline casual defusal match against bots, first to eight rounds.

- **No grenades, no utility, for anyone** — humans included. No smokes to hide behind, no flashes to entry off. Fakes are made with movement and sound instead.
- **No sniper rifles, for anyone.** Every duel is a rifle duel, which is exactly what the bots' learned duel geometry is trained on.
- **A real economy.** Round wins matter, buys matter, and the bots turn up armoured.
- **Friendly collisions off and friendly fire off**, so the coordinated group movement — rally rings, staged executes, same-tick releases through chokes — never jams in a doorway.

The mode config installs and executes itself automatically a few seconds after every map load. Load a map with bots and play; nobody has to type anything.

The point of stripping the game down this far is to allow the AI math to work using basic utility theory, graph theory and search algorithms without relying on any deep learning. The training of the bots is limited to path learning and killspot learning and pre-aim spots. All I ultimately solve here is the mathematical problem of gunfights, angles, timing and strategy.

## The features

### 1. A playbook of real strategies

Each team of bots uses a playbook to gain map control and to gather info: fast executes, split hits through two approaches, slow defaults that take map control first; on the defence, site stacks, spread holds, early aggression, and playing the bomb rather than the site. Play selection uses a shuffled bag rather than repeating winning plays to avoid a predictable pattern of e.g. only hitting one site in the same way, so the bots stay varied, which is more fun when practicing alone with bots.

### 2. Audibles for mid-round plan changes

Each team has an In game leader, who calls the plays as the round progresses, and the plays are visible in the team chat in green text. This adapts to incoming information about the round, and is compared to the original strategy determined for the round. The in game leader calls an audible when the plan diverges from what was planned, or when the team is down unexpectedly: a site turns out to be stacked, the bomb ends up somewhere unexpected, the side goes down bodies. Site switches, rotations (including deliberate fake rotations), pull-backs, and the guard call when the bomb is loose on the ground. 

### 3. Coordinated executes

 The T side hits sites the way a team of humans does: decoys peel off first to put noise in the wrong place, the main group stages short of the site and waits for each other, and then everybody commits at the same time. Each side has a stable leader for the whole match who anchors the synchronisation. With a human carrying the bomb, the bots react to the site where you've actually taken the bomb and commit to holding angles on the site, they have the capability of taking out their knives and running if they are too far away when the bomb is planted.

### 4. A four-phase retake with a designated defuser

When the bomb has been planted, the CT side runs a retake plan: **rally** (gather on a ring short of the site and enter together, as a crossfire), **inspect** (sweep the site), **bait** (the fake defuse, sometimes, but not always), **commit** (the real one). One bot is designated defuser — and the rest form a defensive ring around the defuser.

### 5. Fake defusing

During the bait phase, the defuser walks to the bomb and will occassionally tap the defuse to fake out the Ts — a genuine begin-and-abort that produces the real defuse sound — to draw a hidden lurker out of the one corner the sweep could not see into.

### 6. Clearing angles and lurk-spot sweeps

Before anyone touches the bomb, the site is inspected properly. The plugin works out where a lurker could plausibly be hiding near this plant — built from the same learned duel geometry — divides those spots between the covering bots in bearing arcs, and sweeps them, announcing each spot in team chat as it is cleared.

### 7. A ring around the defuser

While the defuse runs, the covering CTs form a spread, outward-facing ring around the bomb spaced apart in distance and in bearing around the clock face, each watching outward toward the approaches. Any Terrorist trying to break the defuse has to win several different gunfights from several different angles.

### 8. T-side site holding after the plant

The mirror image on offence: Terrorists defend a plant as a spaced, outward-facing ring anchored on the likely retake direction, with every defender required to see the bomb, watch a known entry, and stay clear of team-mates' positions and arcs. Late arrivals take an overwatch line onto the bomb from outside rather than walking into a broken ring.

### 9. Pre-aiming from learned duel geometry

Every recorded death is a measurement: who could see whom, from where. The bots use that bank of evidence to pre-aim the angles that history says matter from wherever they are standing. Line of sight is always required before a bot aims at anything, and the duel itself is always fought by the native AI since that already has built in counter-strafing, reloading and gradual aim micro adjustments.

### 10. Weapon awareness and picking up guns

The plugin also addresses empty guns be having every bot who sees a gun on the floor actually remember where it was last seen, allowing team mates to go back there to pick it up. A bot that runs out of bullets can go and resupply from the floor, and a weapon seen by anybody is remembered by everybody — "there is an AK on the ground at Mid Doors" is a real callout that stays true after the spotter has moved on or died.

### 11. Knifing when out of ammo

A dry bot caught in a fight draws the knife and commits to it — moving erratically at close range. If the rush lands, the bot takes the gun off whoever it killed. If not it at least served as a stall.

### 12. Bot team comms and callouts

The bots use fixed radio names — Wei, Bullseye, Tank and Private, and they make callouts throughout the game in team chat: the play being run, the audibles, the execute stages, the retake release ("all in together, now"), what each covering bot is watching, spots as they are swept, the fake defuse and the commit. Position callouts come from per-map anchor tables ("Triple", "Palace"). Chat volume is adjustable at runtime.

### 13. A fair answer to the human problem

A human can find the one angle bots handle badly and farm it forever. The answer here is deliberately a knowledge handicap, not an aim handicap: after a 30 second delay, the bots learn where the human is, and they use it to improve their positioning and pre aiming — which site to defend, which doorway to watch, which angle to settle on. They don't have wall hacks, and they don't wall bang, they just have a sense of the ping of where the human player is playing from and they can pre-aim that angle. Every duel is still fought by the native AI on even terms. It should still feels like playing against a team that reads the game.

### 14. Everything is observable and tunable

Every decision the bots make is logged with the function that made it and the values it used, to a per-session file you can read back afterwards — the log is the debugger by design. Runtime commands tune the retake, the rotations, the bomb guard, the weapon awareness, the comms volume and more, without a rebuild.

### 15. Bots learnt all the walkable paths on the map

Each of the four shipped maps was trained by having the bots play roughly 200 unguided rounds while the plugin recorded where they actually walked, where every duel happened and where every death happened. From that recorded evidence the breadcrumb algorithm generates the navigation graph, the routes, the holding positions and the pre-aim angles.


## Current limits

The four shipped maps are essentially single-plane layouts, and much of the plugin reasons about distance flat; vertically stacked maps like Nuke and Vertigo could still be trained on, but I would expect poorer proximity judgement there. On a map with no data, the bots start close to stock and learn as they play. 

This project is based on ed0ard's CS2-Bot-Improver, CounterStrikeSharp and Metamod:Source and inherits both their strengths and their occasional version drift.


