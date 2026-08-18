# KaiBotTactics command reference

Nineteen console commands, ordered by when you would actually reach for them
rather than alphabetically.

All commands work from the client console. None require admin — they carry no
`[RequiresPermissions]` attribute, unlike CounterStrikeSharp's own `css_*`
commands.

---

## The three-phase lifecycle

Almost everything below makes more sense against the lifecycle, so it is worth
having straight first. A map moves through three stages, driven by evidence
rather than by a clock.

| Stage | Bots | Files | Moves on when |
|---|---|---|---|
| **Mapping** | Stock CS2 + ed0ard's stack. Plugin records only. | Sample bank and nav graph growing | 150 post-plant + 150 clear samples, graph settled, 60 rounds |
| **Learning** | Full plugin. Playbook adapting. | Map data frozen, playbook growing | Every play tried 8 times |
| **Mature** | Full plugin. Playbook fixed. | Everything frozen | — |

During **Mapping** the bots deliberately behave as if the plugin were not
installed. That is not a bug to be worked around: samples taken while the
plugin is steering bots describe where the plugin put them, not where fights
naturally happen.

---

## 1. Starting an unattended mapping run on a new map

### `kai_ghost spectate`

**Type this first.** Moves any human to spectator and issues `bot_add_t` or
`bot_add_ct` to replace them, so the map is learned from a full bot match.

The reason it matters: breadcrumbs already ignore humans, but the death sampler
does not. A human parked in spawn for a hundred rounds produces a dense cluster
of samples at the spawn point with whatever killed them recorded as the angle
worth watching. That is not a duel spot, it is a stationary target, and it
poisons the data the whole map model is built from.

| Argument | Effect |
|---|---|
| `on` | Discard engagements involving a human. You stay on your team and play normally. |
| `spectate` | As above, plus move humans to spectator and top the sides back up. |
| `off` | Rejoin. Humans count towards learning again. |
| *(none)* | Report state and how many engagements have been discarded. |

Re-swept every five seconds, so a reconnect cannot quietly slip back in.

### `kai_learn clear`

Only if the map already has contaminated samples. Wipes the bank irreversibly —
copy `kai_tactics/learned/<map>.samples.json` somewhere first if unsure.

### `kai_log 1`

Verbosity defaults to 2 (verbose). On a 200-round unattended run that produces
a very large file. `1` keeps it to decisions and lifecycle.

### `kai_logfile`

Prints the path of the log file currently open, so you know where to look
afterwards. `kai_logfile on 50` raises how many files are kept (default 20).

---

## 2. Checking in during the run

### `kai_maturity`

**The one command that answers "is this working".** Five lines:

```
de_inferno: MAPPING after 47 round(s): still finding kill spots and walkable ground
rounds=47 (matches=3, not used) recordingMap=True learningPlays=False
map evidence: postPlant=154/150 clear=228/150 rounds=47/60 graph=2684 nodes (saturated=True, 0 new this session)
play evidence: least-tried play has 0/8 calls across 0 plays (0 total)
bombsites=2: site0=(-1620,-2300), site1=(-2020,340)
generated: 340 pre-aim, 25 post-plant, 20 clear | solved 0 T / 0 CT posts | 0 route(s)
```

Reading it:

- **Line 3** is the mapping gate. All four conditions must clear.
- **Line 4** is the maturity gate, relevant only once Learning starts.
- **Line 5** is the one people miss. Bombsites gate the playbook, the solver
  *and* the router. `bombsites=0` means none of those can be generated, and the
  only cure is a round ending with a plant on each site.
- `recordingMap` and `learningPlays` tell you which recorders are live.

| Argument | Effect |
|---|---|
| `reset` | Back to Mapping, all recorders live again |
| `samples <n>` | Post-plant and clear sample thresholds |
| `rounds <n>` | The rounds floor |
| `calls <n>` | Calls per play needed before Mature |

### `kai_crumbs coverage`

Nav graph detail: standable nodes, upright-only, crouch-only, ladders, edges
needing a jump, mean degree.

**Watch `new nodes this session`.** Node count only ever rises; a session that
adds almost nothing is a map that has been walked. Mean degree below about 2.0
means a chain rather than a network, and routing will be poor.

### `kai_learn status`

Sample counts by category, with first and last timestamps.

`preAim` runs far ahead of the others because every pre-plant death produces
two samples. `postPlant` and `ctClear` are the bottleneck: they need a round to
reach a plant *and* then produce a kill near the bomb.

### `kai_ghost`

Confirms ghost mode is still on and shows the discard count. A rising number is
the effect being visible rather than assumed.

---

## 3. The transition — nothing to type

At the mapping threshold the plugin rebuilds the tactics file from the finished
sample bank and builds the playbook, in one round. You will see:

```
'de_inferno' has finished MAPPING: 60 rounds, 154 post-plant and 228 clear samples...
mapping is complete, rebuilding the tactics file from the final sample bank
'de_inferno' is now LEARNING: the playbook has been built for 2 bombsite(s)...
```

The auto-solve then runs at the next freezetime and routes generate after it.

To confirm it all landed:

| Command | What to look for |
|---|---|
| `kai_maturity` | `LEARNING`, `recordingMap=False`, `across 11 plays` |
| `kai_list` | The generated spots, with sample counts per spot |
| `kai_solve` | `sites=2 tPosts=14 ctPosts=7` |
| `kai_routes` | Route count and `spawns=2` |
| `kai_plays list` | All plays present at `0/0` |

If `kai_plays list` is empty after the transition, the playbook was built with
zero bombsites. Plant on each site and it repopulates.

---

## 4. During the play-learning phase

### `kai_plays list`

Every play with `won/called`, win rate, and `abandoned` — rounds where an
audible pulled the team off it. Abandoned is tracked separately on purpose: a
play that keeps getting abandoned is telling you something different from one
that gets run and loses.

**Early ordering is meaningless.** Selection is upper confidence bound, so
untried plays score infinite and every play is run once before any repeats.

| Argument | Effect |
|---|---|
| `list` | Full record |
| `reset` | Clear win records, keep the plays |
| `explore <n>` | Exploration constant. Higher retries losing plays more readily; default 1.4. |

### `kai_routes list`

Every generated route: kind, team, destination site, waypoint count, length,
and how many known angles it passes.

### `kai_solve`

Solver state and solved post counts per site.

---

## 5. When something looks wrong

| Command | Use |
|---|---|
| `kai_log 2` | Per-bot decisions, throttled. The detail lives here. |
| `kai_enable 0` | All overrides off. Isolates the plugin from stock behaviour. |
| `kai_learn build` | Force a rebuild. Freezetime only; `build force` overrides. |
| `kai_solve run` | Force a re-solve. Freezetime only; `force` overrides. |
| `kai_routes regen` | Force route regeneration |
| `kai_maturity reset` | Back to Mapping |
| `kai_crumbs resume` | Restart recording after the graph saturated |

`kai_learn build` and `kai_solve run` refuse outside freezetime because both
clear live assignments — run mid-round they silently discard that round's
post-plant behaviour.

---

## 6. Behaviour tuning

Rarely needed. Defaults were set against measured data.

### `kai_retake` — the CT retake director

`inspect <sec>` sweep duration · `dwell <sec>` per lurk spot · `bait <sec>` ·
`standoff <u>` how far the defuser is held back · `fake on|off`

### `kai_rotate` — T holds, crossfire and glancing

`seconds <s>` how long a T stays off its hold after being shot ·
`radius <u>` teammate-death pressure range · `yield <s>` threat-response window ·
`noise <u>` how far a noise still counts · `ctpin 0|1` CT pre-plant pinning ·
`thold <u>` T hold radius from the bomb · `cover 0|1` and `coverback <u>` ·
`support 0|1` and `supportrange <u>` crossfire · `sep <deg>` watch separation ·
`glance <s>` and `coverage <u>` angle sweeping

`ctpin 0` is the first dial to try if CT rounds feel one-sided — it stops CTs
locking down pre-plant while leaving the aiming intact.

### `kai_guard` — CTs on a dropped bomb

`radius <u>` · `hold <u>` · `los 0|1` require line of sight · `sweep 0|1` cycle
approach angles as well as the bomb · `seek <u>` and `seektime <s>`

### `kai_crumbs` — the recorder

`max <n>` node ceiling · `minusable <n>` nodes before the graph is trusted ·
`cell <xy> <z>` resolution (**invalidates the existing graph**) · `rate <hz>`

---

## 7. Manual authoring — superseded

`kai_thold`, `kai_ctclear`, `kai_preaim`, `kai_save`, `kai_reload`

These predate the learner. Stand somewhere, look at something, type the
command. **Anything authored by hand is wiped by the next `kai_learn build`**,
so if you use them, `kai_learn off` first.

---

## The short version for a new map

```
kai_ghost spectate
kai_log 1
```

Then play. Check `kai_maturity` occasionally. Everything else happens by itself.

The one thing to verify before the transition fires is that `kai_maturity`
shows **two bombsites**. Sites are learned by watching where the bomb is
planted, so if the bots only ever take one site, plant on the other yourself.

---

## Where the files live

```
addons/counterstrikesharp/plugins/KaiBotTactics/kai_tactics/
    <map>.json                          generated spots
    <map>.json.backup
    learned/<map>.samples.json          raw samples — the irreplaceable one
    breadcrumbs/<map>.graph.json        nav graph
    patrol_routes/<map>.routes.json     static routes
    playbook/<map>.plays.json           plays and win records
    maturity/<map>.maturity.json        lifecycle state
    logs/<map>_<timestamp>.log
```

Back up `learned/<map>.samples.json`. Everything else regenerates from it; that
file cannot be regenerated from anything.
