# KaiBotTactics

Architecture, algorithms, and design notes.

A CounterStrikeSharp plugin that makes CS2 bots play tactically, by learning
the map from what actually happens in it rather than from hand-authored data.

---

## Table of contents

1. [The problem](#1-the-problem)
2. [The core idea](#2-the-core-idea)
3. [Architecture at a glance](#3-architecture-at-a-glance)
4. [The data layer](#4-the-data-layer)
5. [The learning layer](#5-the-learning-layer)
6. [The navigation layer](#6-the-navigation-layer)
7. [The decision layer](#7-the-decision-layer)
8. [The execution layer](#8-the-execution-layer)
9. [The human-facing layer](#9-the-human-facing-layer)
10. [How a round actually runs](#10-how-a-round-actually-runs)
11. [Algorithm theory](#11-algorithm-theory)
12. [Recurring design principles](#12-recurring-design-principles)
13. [Failure modes found in playtesting](#13-failure-modes-found-in-playtesting)
14. [Known limitations](#14-known-limitations)
15. [File reference](#15-file-reference)

---

## 1. The problem

CS2's built-in bots are not tactically stupid so much as tactically absent.
They have no notion of a team plan, no memory of where duels happen, no
concept of holding an angle, and no response to the bomb beyond walking at it.
Raising the difficulty setting does not fix any of that. It raises reaction
time and accuracy, so a high-difficulty bot is the same bot with a faster
trigger — which is a harder opponent in exactly one dimension and a less
interesting one in every other.

The obvious fix is to author tactical data by hand: mark the angles, mark the
holding positions, mark the routes, per map. That works and it does not scale.
Every map is a week of work, every map update invalidates it, and community
maps get nothing.

There is a second obstacle specific to the platform. CounterStrikeSharp
exposes a `CCSNavArea` wrapper but nothing that returns one, so the engine's
own navigation mesh is unreachable without signature-scanning the `TheNavMesh`
global and reverse-engineering the `CNavMesh` layout. There is no published
signature for it. So the plugin has no map geometry, no nav mesh, and no
pathfinding — only ray traces and the positions of players.

**What this project aims to solve:** make bots that play like they understand
the map, on any map, with no authored data, no nav mesh, and no engine
patching — and make them harder by making them smarter rather than faster.

---

## 2. The core idea

Three observations, each of which turns a missing capability into a
measurement.

### A death is a measurement

When one player kills another, two facts are established at once. The victim
was standing at a position that is reachable and worth standing at, and the
killer was standing within line of sight of it. That is precisely the anchor
and watch point a "hold this angle" instruction needs, except measured rather
than guessed.

Record enough deaths and the map's duel geometry falls out of the data. No
authoring required, and it works on any map anybody plays on.

### A bot's position is proof of walkability

Every position a bot occupies is walkable, by definition. It got there. So
recording positions produces a set of known-good standing spots for free.

The step that turns a point cloud into a navigation mesh is this: two
*consecutive* positions from the same bot are a proven traversable link.
Something got from one to the other, under its own power, in a fiftieth of a
second. Those links are graph edges, and a graph is what pathfinding needs.

### Short straight lines between adjacent nodes are safe

The plugin's only movement primitive is a directional shove with no obstacle
avoidance (see [Steering](#steering-and-why-it-constrains-everything)). That
is unusable over long distances and perfectly safe over one cell, because a
straight line between two cells a bot has *already walked between* has nothing
in it to walk into.

So the graph does double duty: it provides paths, and it guarantees that each
leg of a path is short enough and clear enough for the crude steering to
follow.

---

## 3. Architecture at a glance

```
                       ┌─────────────────────────────┐
   observation         │  kai_spot_learner           │  deaths  -> duel geometry
                       │  kai_breadcrumbs            │  walking -> nav graph
                       └──────────────┬──────────────┘
                                      │ (offline, per map, written to JSON)
                       ┌──────────────▼──────────────┐
   derivation          │  kai_solver                 │  positions -> ranked posts
                       │  kai_routes (generator)     │  graph     -> named routes
                       │  kai_maturity               │  when to stop learning
                       └──────────────┬──────────────┘
                                      │ (per round)
                       ┌──────────────▼──────────────┐
   decision            │  kai_playbook               │  what is the team doing
                       │  kai_command                │  who leads, when to commit
                       │  kai_retake_director        │  post-plant CT plan
                       └──────────────┬──────────────┘
                                      │ (per tick, per bot)
                       ┌──────────────▼──────────────┐
   execution           │  kai_tactics_plugin         │  the behaviour chain
                       │  kai_routes (follower)      │  walking a path
                       │  kai_arsenal                │  ammo and weapons
                       └──────────────┬──────────────┘
                                      │
                       ┌──────────────▼──────────────┐
   output              │  KaiBotIntent -> native hooks│  aim and movement
                       │  kai_comms                  │  team chat
                       │  kai_tactics_log            │  the log file
                       └─────────────────────────────┘
```

The important structural property is that **the expensive work happens once
and the per-tick work is reduced to lookup and assignment**. Clustering deaths
into hold spots, scoring every standable position against every known angle,
running A* across a whole map to extract routes — all of that is done at map
load or on command, written to JSON, and read back. What runs sixty-four times
a second is a chain of cheap tests over pre-computed answers.

---

## 4. The data layer

### `kai_tactics_data.cs`

The shared vocabulary. Every other file speaks in these types.

| Type | What it is |
|---|---|
| `KaiPoint` | A world position. `distance_xy` and `distance_sqr` helpers. Height is usually compared separately, because a single 3D radius on Nuke or Vertigo will match the floor above. |
| `KaiHoldSpot` | A learned position to stand at: `anchor`, `watch`, `crouch`, `site`, `team`, `priority`, `samples`, `bomb_dist`. Used for both T post-plant holds and CT clearing spots. |
| `KaiPreAimSpot` | A learned angle: `trigger` (where the bot must be), `trigger_radius`, `trigger_height`, `watch` (where to look), `facing_tolerance_deg`, `priority`, `samples`. |
| `KaiSolvedPost` | A pre-computed holding position with its `coverage` (how many angles it sees), `covers` (which ones), `bearing`, `distance`, `back_wall`, `score`. |
| `KaiMapTactics` | The per-map file: plant sites, post-plant spots, CT clear spots, pre-aim angles, solved posts. |
| `KaiBotIntent` | **The single output type of the whole decision system.** See below. |
| `KaiTacticsLoader` | JSON load/save with automatic backup. |
| `KaiTime` | UTC stamping. |

#### `KaiBotIntent` is the narrow waist

Everything the plugin decides, for one bot on one tick, ends up in one object:

```
watch            KaiPoint?   where to look
force_aim        bool        override the native AI's own aim
anchored         bool        do not move
steer_towards    KaiPoint?   push in this direction
walk             bool        walking pace (silent in CS2)
erratic          bool        strafe and jump while moving
jump             bool        a deliberate, wanted jump
crouch           bool
source_name      string      which decision produced this, for the log
```

That is the entire interface between "what should this bot do" and "make the
bot do it". Two native hooks consume it — one for aim, one for movement — and
nothing else touches the game directly. Every behaviour in the plugin competes
to write this object, and `source_name` records which one won, which is what
makes the log readable after the fact.

### `kai_tactics_log.cs`

A logging layer that exists because of where this code runs: inside native
hooks and per-tick listeners, where there is no debugger and no useful stack
trace.

Three levels (`Error`, `Info`, `Verbose`), changeable at runtime with
`kai_log N` and no rebuild. Every function in the plugin calls
`KaiLog.Event` at least once. Per-tick paths use `KaiLog.Throttled`, which
rate-limits by caller-supplied key so a line inside a tick hook does not print
sixty-four times a second per bot.

Output goes to the console and to a timestamped file under `kai_tactics/logs/`,
rolled on map change, pruned to the newest twenty. Two details worth noting
because both were bugs first:

- Flush timing uses `Environment.TickCount64`, not `Server.CurrentTime`. Every
  `Server` property is a call through to native code that is not ready during
  plugin load, and the game clock restarts from zero on map change.
- The throttle table is cleared on map change for the same reason: a stored
  timestamp from a previous map is in the future relative to the new one, and
  would suppress its key until the clock caught up.

---

## 5. The learning layer

### `kai_spot_learner.cs` — deaths into geometry

**Input:** every player death, with both participants' positions and facings.
**Output:** `KaiHoldSpot` and `KaiPreAimSpot` lists in the map JSON.

#### Sampling

`OnPlayerDeath` builds one or more `KaiSample` records. Each carries a
position, a look direction, a team, a kind (`postPlant`, `ctClear`, `preAim`),
a distance to the bomb where relevant, a timestamp, and an **engagement id**
shared by both samples from the same duel.

The engagement id matters for honesty in the statistics: one duel that
contributes two samples to the same cluster must not count as two independent
pieces of evidence.

Samples are filtered before they are stored — a death in the air, or in a
position that fails a ground check, teaches nothing about where to stand.

#### Clustering: greedy leader clustering, not grid binning

Version 1 snapped each sample to a fixed grid cell and grouped identical keys.
Fixed cells have boundaries, and boundaries are arbitrary:

```
two samples 5 units apart, either side of a cell boundary  -> never merged
two samples 95 units apart, inside one cell                -> always merged
```

Measured on a real 133-sample bank this discarded 83% of the data. Three
separate "pre-aim spots" 62 to 115 units apart, all facing within 9 degrees of
each other, were one position fragmented three ways.

`build_clusters` replaces it with greedy leader clustering against a running
centroid:

```
for each sample in samples_ordered_by_time:
    target = null
    for each cluster in clusters:
        if cluster.team           != sample.team:          continue
        if distance_xy(cluster.mean_pos, sample.pos) > XY_RADIUS:    continue
        if abs(cluster.mean_pos.z - sample.pos.z)   > Z_TOLERANCE:   continue
        if angle_gap(cluster.mean_yaw, yaw_of(sample)) > YAW_TOLERANCE: continue
        target = cluster
        break
    if target == null:
        target = new_cluster()
        clusters.add(target)
    target.members.add(sample)

kept = clusters
        .where(c => c.count >= MIN_SAMPLES)
        .order_by_descending(c => c.engagements)
        .then_by_descending(c => c.count)
```

No boundaries. A cluster's centre moves as it absorbs members, so a genuine
position gathers its own evidence regardless of where the coordinate grid
happens to fall. On the same bank this lifted samples used from 23 to 54.

Three separate tolerances, deliberately:

- **`XY_RADIUS`** — horizontal, generous, because "the same spot" is a couple
  of steps wide.
- **`Z_TOLERANCE`** — vertical, tight, and judged independently. This is the
  single thing that keeps the levels of Vertigo and the floors of Nuke apart.
- **`YAW_TOLERANCE`** — facing. Two players standing in the same doorway
  looking opposite ways are holding two different angles, not one.

#### Circular mean for facings

Yaw cannot be averaged arithmetically. Two samples at 179 and -179 degrees are
2 degrees apart and average to 0, which points the exact wrong way.

```
sum_x = sum over members of cos(radians(yaw))
sum_y = sum over members of sin(radians(yaw))
mean_yaw = degrees(atan2(sum_y, sum_x))
```

Averaging as unit vectors and taking the resultant's direction is the standard
fix and it is what `mean_yaw` does.

#### Emission

`emit_holds` turns clusters into `KaiHoldSpot`s (anchor = cluster mean
position, watch = cluster mean look, priority from engagement count).
`emit_pre_aim` turns them into `KaiPreAimSpot`s, deriving `trigger_radius` and
`facing_tolerance_deg` from the spread of the cluster's own members — a tight
cluster produces a tight trigger, a loose one produces a forgiving one.

### `kai_maturity.cs` — when to stop learning

Every learning system here grows a file. Left alone they grow forever, and
long before that they stop learning anything, because a map has a finite
number of angles and a finite number of ways to walk between them.

An earlier version counted completed matches. That was wrong twice: a match
abandoned after three rounds counts for nothing despite having taught
something, and a match count measures how long you played rather than what
came of it.

So maturity is measured against **the evidence itself**, in three stages:

| Stage | Meaning | Criterion |
|---|---|---|
| `Seeded` | has something to work with | enough samples to emit any spots at all |
| `Mapped` | the geometry is known | post-plant and clear sample counts past a ceiling, with a round floor |
| `Mature` | the plays are known too | every play tried at least `MIN_CALLS` times |

Real recorded reasons from the shipped files:

```
mapped:  "150 rounds reached the ceiling of 150 with 142/150 post-plant
          and 236/150 clear samples"
matured: "198 rounds, all 11 plays tried at least 8 times (96 calls in total)"
```

Rounds are counted, but only as a **floor**, to stop a quiet start being
mistaken for a finished map. The thresholds are evidence counts; the round
count only prevents premature latching.

---

## 6. The navigation layer

### `kai_breadcrumbs.cs` — walking into a graph

**Input:** bot positions sampled on a timer.
**Output:** a quantised node/edge graph, saved as `<map>_graph.json`.

#### Quantisation

Raw sampling at ten a second for ten bots is roughly a quarter of a million
records per match, nearly all describing ground already covered. So positions
are quantised into cells:

```
cell_x    = floor(x / CELL_SIZE_XY)      // 48 units
cell_y    = floor(y / CELL_SIZE_XY)      // 48 units
cell_z    = floor(z / CELL_SIZE_Z)       // 32 units
cell_key  = cell_x + ":" + cell_y + ":" + cell_z
```

48 units horizontally is about the width of a player. 32 vertically is enough
to separate a walkway from the floor beneath it. A node records its cell key,
a representative position, a visit count, and whether it has ever been seen
with the occupant grounded.

#### Edges

The interesting half:

```
on each sample for bot b:
    key = cell_key(position)
    update_node(key)
    if last_key[b] != null and last_key[b] != key:
        update_edge(last_key[b], key, needs_jump = was_airborne_between)
    last_key[b] = key
```

An edge means *a bot physically travelled between these two cells*. That is a
far stronger guarantee than geometric adjacency, and it comes free.

`needs_jump` records that the transition required leaving the ground, so the
route generator can penalise those links and the follower can press the jump
button for them.

#### The `ground` flag

A cell only ever observed mid-jump is somewhere bots pass *through*, not
somewhere they can stand. `standable_nodes()` filters on `ground`, and the
solver only ever considers those as candidate positions.

#### Nearest-node lookup

`nearest_standable` and `nearest_standable_set` answer "what is the closest
recorded standing position to this arbitrary point". The distance measure
weights height:

```
dist = sqrt(dx*dx + dy*dy + dz*dz*4.0)
```

so a node on the floor above is not chosen as the nearest match for one below.

The search walks outward in **cell rings** from the query point's own cell
rather than scanning every node:

```
for ring in 0 .. max_ring:
    if found_at_ring >= 0 and ring > found_at_ring + 1: break
    scan_shell(centre_cell, ring)     // Chebyshev shell, not the solid block
```

It continues one ring past the first hit, because a node in the next ring out
can still be nearer in straight-line terms than one at the corner of this one.
This matters because the original implementation was a linear pass over the
dictionary — 2,541 entries on Mirage — which was affordable only while it was
called rarely.

#### Saturation: knowing when the map is walked

Recording stops when the graph stops growing:

```
if new_nodes_this_round <= SATURATION_NEW_NODES and node_count >= MIN_USABLE_NODES:
    quiet_rounds += 1

may_latch = map_is_exempt
            or node_count    >= SATURATION_MIN_NODES     // coverage floor
            or rounds_recorded >= SATURATION_MAX_ROUNDS   // patience limit

if quiet_rounds >= SATURATION_ROUNDS and may_latch:
    saturated = true
```

The coverage floor exists because the original test measured the wrong thing.
Three quiet rounds of five bots running the same corridor looks exactly like a
finished map. Measured across four maps:

| Map | Nodes | Edges | Avg degree | Bounding-box fill |
|---|---|---|---|---|
| de_mirage | 2,541 | 5,607 | 4.41 | 46.4% |
| de_dust2 | 1,457 | 2,454 | 3.37 | 26.6% |
| de_inferno | 1,040 | 1,563 | 3.01 | 17.8% |
| de_cache | 969 | 1,463 | 3.02 | 14.1% |

Cache latched at 14% coverage. It is also the physically largest of the four.
The floor prevents that; the round ceiling prevents a map with genuinely less
walkable ground from recording forever; and `MAX_NODES` remains the hard cap
above both. Growth is bounded at every level.

### `kai_routes.cs` — graph into named routes

Two halves in one file: a generator that runs once, and a follower that runs
every tick.

#### `KaiRouteGraph` — A* over breadcrumbs

```
build(crumbs):
    for each node in crumbs.graph_nodes():   points[key] = position
    for each edge in crumbs.graph_edges():
        cost = distance(points[edge.from], points[edge.to])
        if edge.needs_jump: cost += JUMP_PENALTY     // 400
        add_link(edge.from, edge.to, cost)

find_path(from_key, to_key):        // standard A*, euclidean heuristic
heuristic(a, b) = distance(points[a], points[b])
```

The jump penalty is a soft preference, not a prohibition: a route round the
long way is better than a route that requires every bot to hit a jump
precisely, but a jump is better than no path.

`simplify(path, angle_tolerance_deg)` collapses collinear runs — a corridor of
thirty cells becomes two waypoints. This is Ramer-Douglas-Peucker in spirit,
implemented as a bearing-change test, and it is what makes routes small enough
to store and read.

#### `snap_key` — getting onto the graph

Snapping an arbitrary point to a graph node is the operation that failed most
often in play. The fixed 400-unit radius meant that on a sparse map, a bot
standing on unrecorded floor could not be pathed *at all*. Measured: 32 of 38
pathing failures were the bot's own position failing to snap, not the
destination being unreachable.

```
for radius in [400, 800, 1600]:
    candidates = nodes within radius, sorted by weighted distance
    if candidates is empty: continue
    if eye is not null:
        for candidate in candidates.take(8):        // trace budget
            if can_see(eye, candidate + chest_height):
                return candidate                     // nearest VISIBLE
    return candidates[0]                             // nearest, unseen
return null
```

Two ideas. Escalating radius, because a start 900 units away is a far better
start than no start. And a visibility preference, because the nearest node is
regularly on the other side of the wall the bot is stuck against, and a path
starting there begins by walking through masonry.

The eye parameter is passed for the *start* of a path and deliberately not for
the *destination* — a holding position behind cover is meant to be out of
sight.

#### Route generation

```
generate():
    for each spawn_region, for each plant_site:
        paths = k distinct A* paths, each penalising the cells used by the last
        for each path: routes.add(simplify(path))
    patrol routes:  loops through contested ground
    rotate routes:  site to site
    count_coverage(route): how many known pre-aim angles the route passes
```

Route kinds:

| Kind | From | To | Used for |
|---|---|---|---|
| `Execute` | spawn | site | attacks and retakes |
| `Patrol` | loop | loop | CT map control |
| `Rotate` | site | site | responding to information |

Routes are **static and named**. A route computed fresh each round would be
different each round, which sounds like unpredictability but is noise. Real
unpredictability is a fixed set of genuinely distinct routes chosen from at
random: each has been verified walkable and verified different, while which
one gets used is unknowable in advance.

#### `KaiPathFollower` — walking a path

The runtime half. Caches a solved path per bot and steers at the *next node*
rather than at the destination.

```
steer(slot, origin, destination):
    if distance_xy(origin, destination) <= ARRIVE_RADIUS: return false   // arrived

    on_graph = is_reachable(origin, SNAP_RADIUS)
    if on_graph:
        last_good[slot] = origin                 // remember a proven position
        end_escape_if_running(slot)
    else if run_escape(slot, origin): return true

    leg = leg_for(slot, origin, destination)     // solve or reuse
    advance_cursor(leg, origin)                  // step past reached nodes
    check_progress(leg, origin, now)             // stall detection
    intent.steer_towards = leg.nodes[leg.cursor]
```

`check_progress` implements a two-stage stall response:

```
if distance_to_node < best - STALL_IMPROVEMENT:
    best = distance_to_node; best_at = now; return

if now - best_at < STALL_SECONDS: return

if not resolved:
    resolved = true
    new_path = solve(origin, destination)   // re-solve from where it ACTUALLY is
    if new_path: replace and return
cursor += 1                                  // skip the node it cannot reach
```

Solve first, because usually the bot simply was not given the right path.
Skip second, because a node that cannot be reached twice is one the graph is
wrong about.

#### The escape ladder

A bot can end up somewhere the graph has never been. The escape is layered
cheapest-and-most-certain first, and never hands the bot back to the native AI.

| Stage | What it does | Why it is in this order |
|---|---|---|
| `Retreating` | walk back to `last_good[slot]` | guaranteed walkable — the bot walked *out* of it. No traces, no guessing. |
| `Candidates` | try the 5 nearest recorded nodes in turn, 3s each | the single nearest node to a wedged bot is often on the far side of what wedged it |
| `Unsticking` | shove backwards, then left, then right, then backwards-with-jump, 0.7s each | ordered, not random: whatever it is wedged against, it arrived from somewhere it fitted |

The whole ladder is capped at `ESCAPE_MAX_SECONDS`. On expiry it stands down
and the ordinary follower tries again — still the plugin driving.

The shoves are computed relative to the bot's own approach direction, not to
compass directions:

```
back_x, back_y = normalise(came_from - origin)
step 0:  ( back_x,  back_y)      // backwards
step 1:  (-back_y,  back_x)      // left of approach
step 2:  ( back_y, -back_x)      // right of approach
step 3:  ( back_x,  back_y) + jump
```

### `kai_solver.cs` — ranking holding positions

**The inversion.** Every position chooser before this started from where a bot
happened to be standing and searched outward, which makes the answer depend on
the accident of where the bot was. The solver inverts it: score *every*
standable position against *every* known angle ahead of time, keep the best
few, and reduce the round-time job to assignment.

```
for each candidate in standable_nodes within SITE_RADIUS of site:
    eye = candidate + eye_height
    covers = []
    for each angle in pre_aim_spots for this team:
        if distance_xy(angle.trigger, candidate) > 1600: continue   // cheap reject
        if can_see(eye, angle.trigger + chest_height): covers.add(angle)

    if covers.count < MIN_COVERAGE: continue
    if not can_see(eye, site_centre + bomb_watch_height): continue   // must see the bomb

    back_wall = trace_fraction(eye, eye + 300 units away from site) * 300
    score     = covers.count * COVERAGE_WEIGHT          // 10.0
              + distance_to_site * DISTANCE_WEIGHT      //  0.004
              + (back_wall < 120 ? COVER_WEIGHT * (1 - back_wall/120) : 0)   // 3.0
```

The weights encode a clear priority. Coverage dominates because it is the
entire point. Distance is a mild pull *outward*, so that between two positions
covering the same angles the one that sees an attacker earlier wins. Cover
rewards a wall close behind, capped so a bot wedged in a corner with no view
cannot outscore a real position.

Two hard filters rather than score terms: a post that cannot see the bomb is
not defending it, and a post that sees nothing is not a post.

Selection is greedy with a spacing constraint:

```
for candidate in scored.order_by_descending(score):
    if chosen.count >= POSTS_PER_SITE: break
    if not far_enough_from(candidate, taken, POST_SPACING): continue
    chosen.add(candidate); taken.add(candidate.position)
```

Best first, then anything far enough from what is already taken, so the posts
are spread rather than clustered on the single best piece of ground.

**Why it runs in-game and incrementally:** scoring needs line of sight, line
of sight needs the map loaded, so it cannot be done offline against the JSON.
A few hundred candidates against 257 angles is tens of thousands of traces, so
it cannot be done in one tick either. The solver holds state between ticks and
spends a fixed `TRACES_PER_TICK` budget until it finishes, reporting progress.
It only ever runs during freezetime or warmup.

---

## 7. The decision layer

### `kai_playbook.cs` — what is the team trying to do

Before this existed, the answer was hardcoded: Ts always execute a random
site, CTs always patrol. A team with no plan running the same play every
round.

Eleven plays per map, generated to fit whatever sites the map turns out to
have:

```
T:   t_exec_s{n}      fast direct hit
     t_split_s{n}     two groups, two approaches
     t_default_s{n}   map control first, hit late
CT:  ct_hold_s{n}     weight one site
     ct_hold_spread   even
     ct_aggro         contest early
     ct_guard_bomb    play the bomb rather than the site
```

#### Selection: a shuffled bag, not a win-rate maximiser

The original implementation scored plays by win rate and called the best. That
was the wrong objective, and it is worth being explicit about why.

A round in this game turns on aim, timing, one lucky spray, and a dozen things
no play controls. The outcome carries far more noise than signal. Selection
that chases it converges on whatever happened to win early — and a side that
converges is a side you can read after three rounds, which defeats the entire
purpose of having a playbook.

This is the exploration/exploitation trade-off, and the correct answer here is
*not* the usual one. In a multi-armed bandit you want to converge on the best
arm. Here, convergence is itself a failure, because the opponent is an
adaptive human who profits from predictability more than they lose from
facing a slightly weaker play.

So selection is sampling without replacement:

```
draw_from_bag(team, options):
    if bag[team] is empty:
        bag[team] = shuffle(options.select(name))
        if bag[team][0] == last_called[team]:
            swap(bag[team][0], bag[team][1])    // no back-to-back repeat
    name = bag[team].remove_first()
    return options.first(p => p.name == name)
```

This gives the strongest variety guarantee available: **every play runs once
before any play runs twice**, and the order within each bag is unpredictable.
Pure random selection cannot promise the first property; win-rate selection
actively destroys it.

The swap on reshuffle addresses the one repeat a bag cannot otherwise avoid —
the last play of one bag being the first of the next — which without the check
is the most frequent back-to-back pairing in the whole system.

Win/loss records are still kept. They are used for reporting and for the
maturity criterion (every play tried at least `MIN_CALLS` times), not for
selection.

#### Audibles

`consider` watches how the round develops against what the play assumed, and
calls an audible when they diverge — contact on the wrong site, the bomb
somewhere unexpected, the side down numbers. `record_outcome` writes the
result to `<map>_plays.json`.

### `kai_command.cs` — leadership and synchronised commitment

#### Leaders

One per side, always a bot, never the human, and stable for the whole match
rather than recomputed each round — a leader that changes every thirty seconds
is not a leader.

```
ensure_leaders():
    for team in [T, CT]:
        if is_eligible_leader(current[team], team): continue   // keep the incumbent
        replacement = lowest valid living bot slot on that team
        leaders[team] = replacement
```

Lowest slot, purely so the choice is deterministic and the same bot keeps the
job across rounds. The leader is the anchor the side synchronises to, and it
is never sent on decoy duty.

#### Reading the carrier

The site a T side hits is not chosen in the abstract: it is wherever the bomb
is going, because a site take without the bomb is just a fight.

With a bot carrier, the play picks the site and the carrier is routed there.
With a **human** carrier there is no plan to read, so the site is inferred
from movement:

```
read_carrier_site(carrier, sites, planned_site):
    nearest, nearest_dist, second_dist = two closest sites to the carrier
    separation = 1.0 - (nearest_dist / second_dist)      // confidence

    // hysteresis: a new read must beat the standing one by a margin
    if nearest != current_read and separation < current_confidence + 0.15:
        return current_read

    current_read = nearest; current_confidence = separation
    return nearest
```

`separation` is a confidence measure with a natural interpretation: standing
midway between two sites gives 0, being twice as close to one as the other
gives 0.5. Acting on a weak read is worse than acting on none — a human still
in spawn is equidistant from everything — and the 0.15 hysteresis margin stops
the whole side thrashing while the human wanders.

#### Arriving together

A site take that trickles in is five duels in sequence, each of which the
defence wins. So:

```
Peeling    decoys leave first; the main group may not commit yet
Staging    main group gathers at STAGING_DISTANCE and waits
Committed  everybody goes on the same tick

ready  = count of main group within STAGING_DISTANCE + STAGING_TOLERANCE of the site
enough = ready >= ceil(alive * READY_FRACTION)        // 0.7
commit = enough or (elapsed >= MAX_STAGING_SECONDS)   // 12.0
```

`READY_FRACTION` is 0.7 rather than 1.0 because waiting for a straggler who is
dead or stuck means never going at all. The timeout is the same insurance from
the other direction.

The decoys leaving first is deliberate: the noise should already be in the
wrong place before the real hit starts.

### `kai_retake_director.cs` — the post-plant CT problem

The largest single file, and the one with the most awkward relationship to the
platform.

**The context.** ed0ard's BotAI plugin patches four things that together make
CT bots beeline to the bomb and defuse without clearing anything: the team
gate in `CSGameState::OnBombPlanted`, the 1500-unit bomb-beep hearing check,
the `IsVisible` gate in `MoveToState::OnUpdate`, and the disposition rewrite
to `ENGAGE_AND_INVESTIGATE`. Unpatching means forking his plugin and redoing
it every release.

This takes the opposite approach: **let the native AI path them in as it does
now, then take over on arrival.**

#### Three phases

| Phase | What happens |
|---|---|
| `Clear` | one bot is designated defuser and held back. Everyone else clears assigned lurk spots. |
| `Inspect` | the site is swept — beats are assigned so the sweep is divided rather than duplicated |
| `Defuse` | the defuser commits; others hold the angles covering the bomb |

#### Lurk spots and inspection beats

```
build_lurk_spots():   positions near the bomb where a T could plausibly be hiding,
                      taken from learned hold spots plus solved posts
assign_inspection_beats():  divide the uncleared spots among available bots so
                      the sweep is partitioned, not duplicated
sweep_opportunistically():  a bot that happens to walk into sight of an uncleared
                      spot marks it cleared in passing — free progress
```

#### Defuser discipline

The one behaviour that must not be interruptible:

```
if intent.source_name == "defusing:committed":
    do not release the pin under any circumstance
```

Being shot at while defusing is not a reason to stop. Measured across
sessions: `defusing:committed` was held through contact 11 times and
`planting:committed` 12 times, with the bot's forward speed forced to zero
while the bomb ticked down. That is exactly the intended behaviour.

#### Solo retake

A separate state machine for the 1vN case, because a lone defuser has a
genuinely different problem: `SoloSweep`, `SoloTap`, `SoloWithdraw`,
`SoloListen`, `SoloDefuse`. The `SoloListen` stage exists because a single
defender's best information source is sound, and standing still to get it is
worth the time.

#### Fake defuse

`DriveFakeDefuse` taps the defuse to produce the sound and stops, to draw a
hidden T out of position. A small thing that reads as genuinely human.

#### Watchdog

```
if bomb_planted_for > WATCHDOG_SECONDS and no defuse has started:
    log ERROR, drop all CT overrides for the rest of the round
```

An explicit admission of failure that hands the side back rather than leaving
them stuck under a plan that is not working. Its firing rate is one of the
better health metrics for the whole system.

---

## 8. The execution layer

### `kai_tactics_plugin.cs` — the behaviour chain

The main file. 137 methods. Its central structure is a **priority chain**
evaluated per bot per tick, first match wins:

```
1.  celebration fire        (round over, purely cosmetic)
2.  resupply                (out of ammo — an empty gun outranks any angle)
3.  contact support         (a team mate is in a fight)
4.  plant / defuse commitment
5.  T post-plant hold
6.  loose bomb guard
7.  route follower
8.  pre-aim hold
9.  glance sweep
```

Each layer returns a bool. Returning true writes the `KaiBotIntent` and stops
the chain, and `intent.source_name` records which layer won.

**The chain's ordering is load-bearing and it is where the subtlest bugs
live.** A layer that returns true suppresses everything below it. Two of the
three worst bugs found in playtesting were layers claiming bots they had no
business claiming, and the symptom in both cases was not the layer misbehaving
but the *layers below it silently not running*.

#### Aim, and its own precedence

`OnUpdateLookAnglesPre` is the aim hook, with its own separate ordering:

```
1. real contact          -> hand straight back to the native AI (better at duels)
2. try_threat_aim        -> noise or recent damage
3. the AI's own SetLookAt
4. authored angles       (pre-aim, glance, watch targets)
```

Handing real duels back to the native AI is a deliberate choice: it is better
at them than anything here, and the plugin's job is deciding *where to be
looking before the duel starts*, not winning it.

`try_threat_aim` filters noise by **travel distance rather than straight-line
distance**, capped at `NOISE_RANGE` (1500 units):

```
if noise.travel_distance <= NOISE_RANGE and (now - noise.at) <= YIELD_SECONDS:
    aim at noise.position
```

Travel distance is the right measure: gunfire through a wall across the map
should not drag a holding bot off its angle, but footsteps in the next room
should. Measured latency: median 0.7s, p90 2.0s, max 3.0s.

Critically, hearing something turns the **head** but does not release the
movement pin. `should_release_pin` returns true only on `is_enemy_visible`,
`is_attacking` or `is_aiming_at_enemy`. A noise does not make a holding bot
wander off, which is the correct trade.

#### Steering, and why it constrains everything

The movement hook is the whole reason the architecture looks like it does:

```
forward = dot(desired_direction, bot_forward_vector)
left    = dot(desired_direction, bot_left_vector)
pawn.m_forwardSpeed = forward * speed
pawn.m_leftSpeed    = left    * speed
```

That is a shove in a direction. There is no obstacle avoidance, no pathing, no
collision awareness. It is safe over one graph cell and catastrophic over a
hundred, and every movement bug in the project's history traces back to
something using it as though it were a "go here" command.

`KaiBotIntent` has no "go here" field for exactly this reason. Anything that
wants a bot to travel must go through `KaiPathFollower`.

#### Jump suppression, and the exemption

The native anti-stuck reflex fires when a bot's own state machine notices its
movement being overridden. Left alone this produced bots hopping on the spot,
so both the pinned path and the steered path clear the jump button. The
`intent.jump` flag is the exemption, so a jump the plugin actually *asked* for
— the escape ladder's last resort — still happens.

#### Transit clearing

What makes a route a push rather than a march:

```
apply_transit_clearing(bot):
    heading = travel_heading(bot)            // from velocity, not from facing
    candidates = pre_aim angles within COVERAGE_RANGE
                 and within TRANSIT_ARC_DEG (70) of heading
                 and confirmed by can_see(eye, angle.trigger + chest)
    sort by most-directly-ahead, then by distance
    intent.watch = candidates.first
```

Three filters — range, arc, and a confirming trace — so a bot moving forward
pre-aims spots it is actually approaching rather than staring at walls.
Measured: 14% of forward-arc scans find nothing, which is the honest failure
rate for "there is no known angle ahead of me".

There is a hard backstop: if an authored watch point is more than 95 degrees
off the bot's actual velocity, it is discarded and replaced with a point down
the direction of travel. Backwards aiming while walking forwards is the single
most obviously wrong thing a bot can do, and this catches it regardless of
which layer produced the angle.

#### CT zones and spacing

```
refresh_ct_zones():   divide the bearing circle around the map centre evenly
                      among living CTs  (4 CTs -> 90 degrees each, 3 -> 120)
apply_pre_aim():      hold only if the trigger is in THIS bot's zone
                      and no anchored team mate is within MIN_BOT_SPACING (200)
```

Both gates are needed. Without the zone check the whole CT side converges on
whichever triggers happen to be nearest spawn; without the spacing check two
bots share a spray transfer.

#### Route selection and fitting

```
pick_route(bot, kind):
    candidates = routes matching kind, team, and destination site
    free       = candidates not already taken by a team mate
    pool       = free if free is not empty else candidates
    chosen     = pool[random]
    return fit_route_to_bot(bot, chosen)
```

De-duplication by name is what keeps a CT patrol from becoming a conga line —
the candidate pool visibly shrinks 12, 11, 10, 9 as each bot takes one.

`fit_route_to_bot` solves a subtler problem. A route is a fixed path between
fixed endpoints; the bot being given it is wherever it happens to be standing.
Measured: the median distance from a bot to waypoint zero of its newly
assigned route was **1,462 units**, maximum 4,522.

```
fit_route_to_bot(bot, route):
    join = index of nearest waypoint, height-weighted
    gap  = distance from bot to route.waypoints[join]
    if gap > ROUTE_APPROACH_DISTANCE:
        approach = solve_path(bot_position, route.waypoints[join])
        waypoints = approach + route.waypoints[join..]
    else:
        waypoints = route.waypoints[join..]
    return copy_of(route) with waypoints        // same Name, own list
```

The copy is essential: the stall check splices waypoints into whatever route a
bot is running, and handing out the shared instance would edit the route book
in memory for every bot that ever takes that route.

### `kai_arsenal.cs` — ammo and weapons

**The problem:** bots keep pulling the trigger on empty guns with loaded
rifles on the floor beside them. The native AI has no concept of resupplying
mid-round.

Three responses, in the order a person would choose them:

```
is_dry(bot):  (magazine + reserve) <= DRY_THRESHOLD    // 5, not 0
              and no other loaded weapon in the inventory

dry and safe:      go and pick something up
dry and in a fight: knife rush, if the enemy is within KNIFE_RUSH_RANGE
dry and far from a fight: break contact, go for a gun
```

`DRY_THRESHOLD` is 5 rather than 0 because a bot with three bullets left is
about to have a problem and should solve it before it becomes one. The check
correctly treats a full pistol as not-dry even with an empty rifle.

#### Shared weapon memory

A weapon seen by anybody is remembered by everybody, for the round:

```
scan():                 note dropped weapons in view
first_to_see(weapon):   whoever saw it first gets the credit
claim(weapon, slot):    one bot per weapon
still_there(weapon):    re-verify before travelling
release(slot):          on death, or on giving up
```

"There is an AK on the ground at Mid Doors" is a real callout and it stays
true after the bot that saw it has moved on or died. The memory lasts the
round, which is exactly as long as the weapon does.

#### Knife rush

```
if enemy_distance <= KNIFE_RUSH_RANGE:      // 600
    draw knife, intent.erratic = true, close the distance
else:
    restore weapon, break off, go for a gun
```

The `erratic` flag strafes and jumps, which meaningfully helps at 200 units
and does nothing at 1500. The range cap exists because the original had none:
of 111 measured charges the median was 481 units but 20 exceeded 800 and the
longest was 1,516 — a bot sprinting most of the length of the map at somebody
with a rifle.

`is_on_the_objective` refuses to leave a plant or defuse for a weapon, whatever
the ammo situation.

---

## 9. The human-facing layer

### `kai_comms.cs` — the squad talks

Everything the plugin decides was visible only in a log file read afterwards.
That is fine for finding bugs and useless while playing: from inside the game,
a coordinated execute and five bots wandering look identical until somebody
dies.

#### Sticky identities

Bot names are assigned by the game and change between rounds, which makes them
useless as identities — "Bot Zane" means nothing on round two. So four fixed
names are handed out, sticky by slot, held for as long as the bot lives. The
prefix follows the human's side: Counter-Terrorist makes them Operators,
Terrorist makes them Comrades.

#### Callouts from geometry

```
nearest(position):   the closest known callout anchor
describe(position):  "A Site", "Mid Doors", "Long"
approach_name(from, to): "through apartments"
```

Built-in anchor tables exist for Inferno, Dust2 and Cache; any other map
degrades to bearings and distances rather than failing.

#### Team only, always

Every message goes to one team. This is not etiquette — a call of "taking B
through apartments" broadcast to the server hands the defence the round.

Verbosity is tiered (`Call` for decisions, `Detail` for colour) so the chat can
be turned down without turning it off.

### Console commands

Roughly twenty, all on `kai_` prefixes:

| Command | Purpose |
|---|---|
| `kai_log N` / `kai_logfile` | verbosity, file sink |
| `kai_learn on/off/build` | recording and rebuilding the tactics file |
| `kai_crumbs` | breadcrumb status, resume, clear |
| `kai_routes` | route book status, regenerate |
| `kai_solve` | run the position solver |
| `kai_plays` | playbook status and records |
| `kai_maturity` | learning stage and what is still needed |
| `kai_retake` / `kai_rotate` / `kai_guard` | tune the respective subsystems |
| `kai_thold` / `kai_ctclear` / `kai_preaim` | manual spot authoring |
| `kai_ghost` | debug visualisation |

`RunBuild` refuses to run outside freezetime without `force`, because it
resets the retake director and clears every intent.

---

## 10. How a round actually runs

**Map load.** Breadcrumbs and the tactics file load from JSON. Maturity
reports its stage. The route graph is built lazily on first use. The path
follower is constructed and handed to the retake director. If the tactics file
has angles but no solved posts, the solver is queued for the next freezetime.

**Freezetime.** The playbook draws a play for each side from its bag. The
solver spends its trace budget if it has work. Leaders are confirmed. Routes
are cleared from the previous round.

**Round start.** Each bot is assigned a route fitted to where it is standing.
CT zones are divided by bearing. Decoys are assigned if the play calls for
them. The squad announces the play in team chat.

**Play.** The per-tick chain runs for every bot. Routes are followed with
transit clearing along the way; stalls are detected and re-pathed. CTs reaching
their zones anchor onto pre-aim angles and glance-sweep between the angles
their position covers. Contacts are refreshed from sightings and attributed to
sites. The playbook watches for a reason to call an audible.

**T execute.** The command layer peels decoys, stages the main group at
`STAGING_DISTANCE`, and commits everyone on the same tick. The bomb carrier is
routed to the site — or, if the human is carrying, the site is *read from their
movement* and the bots commit to their choice.

**Plant.** T bots take assigned ring posts around the bomb, pathed rather than
shoved, each covering a bearing sector with line of sight to the bomb. The
retake director takes over the CT side: designate a defuser, assign clearers,
sweep the lurk spots, then defuse under cover.

**Round end.** The playbook records the outcome. Maturity re-evaluates. The
breadcrumb graph checks whether it learned anything, and saves if it did.

---

## 11. Algorithm theory

Collected here so the mathematics is in one place. All in `snake_case` or
C#-style names rather than symbols.

### A* on the breadcrumb graph

Standard A* with an admissible Euclidean heuristic:

```
f_score(node)  = g_score(node) + heuristic(node, goal)
heuristic(a,b) = sqrt((a.x-b.x)^2 + (a.y-b.y)^2 + (a.z-b.z)^2)
edge_cost(a,b) = distance(a,b) + (needs_jump ? JUMP_PENALTY : 0)
```

The heuristic is admissible because straight-line distance never exceeds path
distance, so A* returns optimal paths. The jump penalty breaks strict
admissibility relative to true traversal cost, which is deliberate and
harmless: it biases away from jump links rather than guaranteeing a minimum.

### Greedy leader clustering

Single-pass, order-dependent, O(n·k) for n samples and k clusters. Chosen over
k-means for three reasons:

- **k is not known.** The number of genuine duel positions on a map is exactly
  what is being discovered.
- **The radius is physically meaningful.** "Within 90 units" is "the same
  doorway"; a k-means cluster count is not interpretable in map terms.
- **It is incremental.** New samples extend clusters without re-solving.

Order dependence is real and mitigated by processing samples chronologically,
which is also the order in which the map was actually learned.

### Circular statistics for facings

```
mean_yaw = degrees(atan2(sum(sin(radians(yaw_i))), sum(cos(radians(yaw_i)))))
```

The resultant vector's length also measures concentration, which is what
`facing_tolerance_deg` is derived from: a tightly agreeing cluster produces a
tight tolerance.

### Confidence as relative separation

```
separation = 1.0 - (nearest_distance / second_nearest_distance)
```

Scale-free and bounded in `[0, 1)`. Equidistant gives 0; twice as close gives
0.5. Compared against a standing read plus a hysteresis margin:

```
if new_read != current_read and separation < current_confidence + HYSTERESIS:
    keep current_read
```

This is Schmitt-trigger logic. Without it, any measurement near a decision
boundary oscillates.

### Sampling without replacement

For n plays, a bag guarantees each appears exactly once per n draws.
Compared with the alternatives:

| Scheme | Repeat possible next round | Predictable after k rounds |
|---|---|---|
| uniform random | yes, with probability 1/n | no, but clumps badly |
| win-rate greedy | yes, and likely | **yes, quickly** |
| shuffled bag | only across a bag boundary | no |

The bag maximises entropy subject to the constraint that every play is used
equally, which is the right objective when the opponent adapts and the
outcome signal is mostly noise.

### Weighted linear scoring with hard filters

The solver's score is a weighted sum, but the two most important criteria are
**filters rather than terms**:

```
if not can_see(candidate, bomb):        reject      // hard
if covers.count < MIN_COVERAGE:         reject      // hard
score = covers.count * 10.0 + distance * 0.004 + cover_bonus   // soft
```

Putting "can see the bomb" in the score with a large weight would let a
position with spectacular coverage outrank it. Some criteria are not
negotiable and should not be priced.

### Greedy selection with a diversity constraint

```
for candidate in sorted_by_score_descending:
    if far_enough_from(candidate, already_taken, MIN_SPACING):
        take(candidate)
```

This is greedy maximal-marginal-relevance. It does not produce the optimal
spread — that is NP-hard — but it produces a good one in one pass, and the
spacing constraint is what stops five bots stacking on the single best piece
of ground.

### Saturation as a stopping rule

```
quiet_round     = new_nodes_this_round <= SATURATION_NEW_NODES
may_latch       = exempt or node_count >= FLOOR or rounds >= MAX_ROUNDS
saturated       = consecutive_quiet_rounds >= SATURATION_ROUNDS and may_latch
```

The naive form (quiet rounds alone) conflates *the map is fully explored* with
*the bots repeated themselves*. The coverage floor discriminates between them,
and the round ceiling guarantees termination for maps that genuinely have less
ground than the floor assumes.

### Two-stage stall response

```
stalled = (distance_to_target has not improved by IMPROVEMENT within SECONDS)

first stall  -> re-solve the path from the CURRENT position
second stall -> skip the target
```

The escalation encodes a belief about causes. The most likely reason a bot is
not progressing is that its path was solved from somewhere it no longer is.
The second most likely is that the graph is wrong about a link. Trying the
cheap, likely fix first and the destructive one second is the general shape,
and it recurs in the escape ladder too.

---

## 12. Recurring design principles

**Measure, do not author.** Every piece of tactical knowledge comes from
something that happened. This is the whole project.

**Expensive once, cheap forever.** Clustering, solving, route extraction — all
done at load or on command and cached to disk.

**Filters before scores.** Non-negotiable criteria are rejections, not
weighted terms.

**Hysteresis on every switching decision.** Site reads, route choices, target
selection. Anything that can oscillate does, unless a margin stops it.

**Fail visibly, hand back deliberately.** The defuse watchdog and the escape
ladder's expiry both log at ERROR and relinquish control explicitly. Silent
degradation is worse than loud failure.

**Never silently hand back.** Where the plugin *can* keep driving, it does.
The escape ladder exists so that a stuck bot gets four increasingly aggressive
attempts before anything gives up.

**Log every function.** Not a style rule — a consequence of running inside
native hooks with no debugger.

**Suspect the layer above.** When a behaviour is not happening, the cause is
usually that something higher in the priority chain claimed the bot. Two of the
three worst bugs were exactly this.

---

## 13. Failure modes found in playtesting

Worth publishing alongside the design, because each was invisible from inside
the game and obvious from the log.

### Bots supporting their own fights

`refresh_contacts` records a contact against the slot that saw the enemy.
`apply_contact_support` then scanned every contact for one it could see — and
the bot that saw an enemy trivially satisfies "can see it". **722 of 1,032
support responses were a bot swinging onto its own fight.**

The aim override was harmless, since real contact hands straight back to the
native AI a layer above. The *priority* was not: the function returns true, so
every bot that saw an enemy dropped its route, its hold and its retake
assignment and reverted to native wandering.

The fix is one line: `if (contact.reported_by == player.slot) continue;`

### Clearers that never moved

`drive_clearer` cleared the anchor, set a source name, logged "en route", and
returned — having issued no movement command, on the assumption that native
pathing would carry the bot in. It did not. **Of 23 measured approach runs, 18
finished further from the assigned spot than they started**, several by more
than 1,000 units, while the log reported them en route throughout.

This is why inspection ended with most of the site unswept in 17 rounds, and
why the defuse watchdog fired in 9.

### Bots frozen against walls

Route waypoints were followed by pointing `steer_towards` at them, and steering
has no obstacle avoidance. **27 mid-round freezes totalling 270 seconds**, the
worst a slot frozen 48 seconds of a 90-second round with its distance logged
unchanged at 3,337 units throughout.

Two contributing causes: `pick_route` never checked whether the bot was near
the route's start (median 1,462 units, max 4,522), and nothing ever noticed
that a bot had stopped making progress.

### Posts that were never reached

T ring posts were assigned by score and reached by shoving. **191 lines of
"moving to its ring post" against 13 of "holding its ring post"**; of 28
approach runs only 3 ever got within 120 units.

After pathing was added, 18 of 24 runs ended closer — but only 4 reached the
post. The remaining problem is *assignment*, not movement: `claim_solved_post`
scores on coverage, distance and back wall, and never on whether the post is
reachable in the time the bomb has left.

### The handicap that made bots worse

Adding continuous knowledge of the human's position (see below) initially fed
that knowledge into `apply_contact_support`. Comparing behaviour inside and
outside the tracked windows, per minute:

| Behaviour | Untracked | Tracked |
|---|---|---|
| contact support | 1.5 | 15.7 |
| T hold | 0.4 | 10.7 |
| reroute | 0.8 | 2.2 |
| rotations | 0.2 | 0.7 |

The bots left their angles and walked toward a known position, arriving strung
out and one at a time. **In Counter-Strike the player holding an angle beats
the player walking into it**, so perfect information had converted a defensive
problem into a queue.

The correction was to feed the knowledge to the *site attribution* and to
*pre-aim angle selection* instead — deciding which angle bots already holding
should hold, rather than sending them anywhere. Site attribution was also
rate-limited and given a decay, because per-tick contribution had inflated
per-site counts to 3,908 against a handful from real sightings, turning a
pressure measure into a dwell-time measure.

---

## 14. Known limitations

**Post assignment ignores reachability.** The solver scores positions on
coverage, distance and cover. It does not know how long a bot will take to get
there, so a post 1,900 units away at plant time is a post that will not be
occupied before the bomb goes off.

**Route join can skip a route's early angles.** `fit_route_to_bot` joins at the
nearest waypoint, which is geometrically right but means a bot standing near
the far end of a route takes it without clearing the angles on the early legs.

**Sparse maps path badly.** The breadcrumb graph only knows where bots have
walked. On a map at 14% bounding-box fill there is a great deal of floor more
than the snap radius from anything recorded. The escalating snap and the escape
ladder mitigate it; more recording fixes it.

**No peeking.** There is no jiggle peek, shoulder peek, or jump spot anywhere
in the codebase. Bots hold or they walk. `intent.erratic` is the only movement
modifier and only `knife_rush` uses it.

**Jump edges are recorded but under-used.** `needs_jump` is consumed by the
route generator as a cost penalty. The follower can now press jump on the
escape ladder's last step, but does not yet press it for a path link that
requires one, so a route needing a hop stalls there.

**Difficulty is still bounded by the native duel.** Everything here decides
where bots are and what they are looking at. Once a duel starts, the native AI
takes over, and its aim is whatever the difficulty setting says.

### The human-tracking handicap

Because of that last limitation, there is an explicit and deliberately visible
handicap, controlled by three flags at the very top of the plugin class:

```
BOT_GOD_MODE_VS_HUMAN_TRACKING    = true      the whole handicap
BOT_GOD_MODE_VS_HUMAN_DELAY       = 30.0f     seconds from round start
BOT_GOD_MODE_VS_HUMAN_POST_PLANT  = true      keep working after the plant
```

After the delay, the enemy side is told where the human is, continuously. It
is written into the contact list with `reported_by = -1` — a slot belonging to
no bot — and refreshed every tick so it never ages out.

What it is for: countering the same hiding spot round after round. The learned
data has no notion that a position was used last round, and the bots have no
memory of dying to it, so a human who finds one angle the bots handle badly can
farm it indefinitely.

What it deliberately is **not**: line of sight is always required before a bot
aims at the tracked contact, and there is no flag to change that. Knowledge and
aim are different handicaps. Knowledge changes where the side sets up; aiming
through walls is a wallhack and would also wreck the behaviour, since contact
support outranks the movement layers and every bot on the side would stand
still with its crosshair on masonry.

The flags are `static readonly` rather than `const` specifically so that
toggling them does not produce unreachable-code warnings in either position.

---

## 15. File reference

| File | Lines | Role |
|---|---|---|
| `kai_tactics_plugin.cs` | ~9,700 | Main plugin. Hooks, per-tick behaviour chain, console commands, route following, pre-aim, contacts, zones. |
| `kai_retake_director.cs` | ~2,930 | Post-plant CT plan: clear, inspect, defuse. Solo retake state machine. Fake defuse. |
| `kai_routes.cs` | ~1,730 | Route graph (A*), route generation, route book I/O, and `KaiPathFollower` with the escape ladder. |
| `kai_breadcrumbs.cs` | ~1,420 | Navigation graph recorded from bot movement. Quantisation, edges, saturation, nearest-node search. |
| `kai_comms.cs` | ~965 | Team chat. Sticky squad identities, callout tables, verbosity tiers. |
| `kai_spot_learner.cs` | ~964 | Deaths into hold spots and pre-aim angles. Sample bank, clustering, emission. |
| `kai_playbook.cs` | ~815 | Play definitions, bag-based selection, audibles, win/loss records. |
| `kai_tactics_data.cs` | ~565 | Shared types. `KaiPoint`, `KaiHoldSpot`, `KaiPreAimSpot`, `KaiSolvedPost`, `KaiBotIntent`, JSON loader. |
| `kai_arsenal.cs` | ~548 | Dry detection, shared dropped-weapon memory, knife rush, resupply. |
| `kai_maturity.cs` | ~538 | Learning stages and stopping criteria, per map. |
| `kai_command.cs` | ~425 | Leaders, reading the bomb carrier, synchronised execute phases. |
| `kai_solver.cs` | ~411 | Incremental scoring of every standable position against every known angle. |
| `kai_tactics_log.cs` | ~406 | Levelled, throttled, file-backed logging. |

### On-disk artefacts, per map

```
<map>.json            plant sites, hold spots, pre-aim angles, solved posts
<map>_graph.json      breadcrumb nodes and edges
<map>_routes.json     generated routes with coverage counts
<map>_plays.json      playbook records: called, won, abandoned
<map>_maturity.json   learning stage and the reason it was reached
<map>_bank.json       raw death samples
logs/<map>_<stamp>.log
```

Every write goes through `KaiTacticsLoader`, which takes a backup first.
