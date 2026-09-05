# KaiBotTactics

An extension to ed0ard's CS2-Bot-Improver that gives Counter-Strike 2 bots a
learned navigation graph, a tactical memory of the map, and a genuine
post-plant game — built entirely from what happens during play, with no
authored data, no engine navigation mesh, and no patching of the game's own
binaries.

Architecture, algorithms, design rationale, and the reasoning behind every
significant decision.

---

## Table of contents

1. [What this is, and what it extends](#1-what-this-is-and-what-it-extends)
2. [The problem in full](#2-the-problem-in-full)
3. [Mathematical and algorithmic foundations](#3-mathematical-and-algorithmic-foundations)
4. [The breadcrumb navigation graph](#4-the-breadcrumb-navigation-graph)
5. [Routes: from graph to named paths](#5-routes-from-graph-to-named-paths)
6. [Walking a path, and getting unstuck](#6-walking-a-path-and-getting-unstuck)
7. [Learning the map's duel geometry](#7-learning-the-maps-duel-geometry)
8. [The post-plant problem](#8-the-post-plant-problem)
9. [The retake: how the CT side answers](#9-the-retake-how-the-ct-side-answers)
10. [The handicap: knowledge, not aim](#10-the-handicap-knowledge-not-aim)
11. [Team decisions: playbook and command](#11-team-decisions-playbook-and-command)
12. [Execution: the per-tick behaviour chain](#12-execution-the-per-tick-behaviour-chain)
13. [Weapons, ammunition, and engagement range](#13-weapons-ammunition-and-engagement-range)
14. [Knowing when to stop learning](#14-knowing-when-to-stop-learning)
15. [Observability](#15-observability)
16. [Failure modes found in playtesting](#16-failure-modes-found-in-playtesting)
17. [Known limitations](#17-known-limitations)
18. [File reference](#18-file-reference)
19. [Bibliography and a note on sources](#19-bibliography-and-a-note-on-sources)

---

## 1. What this is, and what it extends

### ed0ard's CS2-Bot-Improver

The base project addresses a specific and well-identified set of defects in
CS2's native bot AI by patching the game's own decision points. Its most
important intervention, for our purposes, concerns post-plant behaviour. The
stock game contains four separate mechanisms that together cause Counter-
Terrorist bots to run directly at a planted bomb and attempt a defuse without
clearing anything:

- a team gate in `CSGameState::OnBombPlanted` that suppresses the
  Counter-Terrorist reaction entirely under some conditions,
- a hearing check on the bomb's beep limited to 1500 units,
- an `IsVisible` gate inside `MoveToState::OnUpdate` that stops a bot
  approaching a bomb it cannot see,
- a disposition rewrite that sets bots to `ENGAGE_AND_INVESTIGATE`.

CS2-Bot-Improver patches all four. The result is bots that reliably go to the
bomb, which is a substantial improvement over bots that mill about, and it is
the foundation everything here is built on.

### What this project adds, and why it does not unpatch anything

Reliably going to the bomb is necessary but not sufficient. A bot that walks
straight at a bomb, in the open, one at a time, while four Terrorists hold
angles onto it, is a bot that dies in a predictable order. The stock behaviour
and the patched behaviour are both, in tactical terms, the same behaviour
performed with different levels of enthusiasm.

The obvious response is to unpatch the movement so bots approach cautiously.
That was rejected. It would mean forking CS2-Bot-Improver, reversing four
carefully-found patches, and redoing that work on every upstream release. It
also throws away something valuable: the patched pathing genuinely does bring
bots onto the site, which is a hard problem that has already been solved.

So this project takes the opposite approach. **Let the native AI path them in,
then take over on arrival.** The plugin does not fight the movement; it adds a
layer of decision-making on top of it, and intervenes only where the native AI
has no opinion at all — which angle to hold, where to stand relative to the
bomb, when to commit to a defuse, who leads, what the team's plan is.

Concretely, the additions are:

| Addition | Purpose |
|---|---|
| Breadcrumb navigation graph | A pathfinding graph built from bot movement, because the engine's navigation mesh is unreachable from the plugin API |
| Route generation and following | Named, distinct, walkable paths between spawns and sites |
| Spot learning | Duel geometry derived from where players actually die |
| Position solver | Pre-computed, ranked holding positions per site |
| Playbook | Team-level plans with deliberate unpredictability |
| Command layer | Leadership, bomb-carrier reading, synchronised execution |
| Retake director | A three-phase Counter-Terrorist post-plant plan |
| Terrorist post-plant hold | Ring formation, sector division, overwatch for late arrivals |
| Rotation sprint | Run the first half of a post-plant rotation, clear the second |
| Arsenal | Ammunition awareness, dropped-weapon memory, engagement ranges |
| Maturity tracking | Knowing when a map has been learned and recording should stop |
| Communications | Team chat so the plan is visible from inside the game |
| Handicap | Optional continuous knowledge of the human's position, used for positioning only |

---

## 2. The problem in full

### Difficulty in Counter-Strike bots is one-dimensional

Raising the difficulty setting on a CS2 bot changes its reaction time and its
aim error. It does not change a single decision the bot makes. A hard bot and
an easy bot take the same route, hold the same nothing, and respond to a bomb
plant identically; the hard one simply kills you faster once it sees you.

This produces a specific and unsatisfying experience for a human practising
against them. Below a certain difficulty the bots are trivial. Above it they
become effectively aim-hacking, killing you the instant you enter their view
cone regardless of what either of you did tactically. There is no setting at
which they are *interesting*, because the axis being adjusted is not the axis
on which interest lies.

The project's central objective follows directly: **make bots harder by making
them smarter, and keep their aim mechanics at a human-like level throughout.**
Every design decision below is subordinate to that. Where a choice existed
between an intervention that improves decisions and one that improves shooting,
the shooting was left alone.

### Authored tactical data does not scale

The conventional solution to "bots do not know where to hold" is to author the
data. Mark the angles, mark the positions, mark the routes, per map, by hand.

This works and it does not scale. Each map is days to weeks of careful work,
each map update invalidates some of it, community maps get nothing, and the
quality of the result depends on the author's own understanding of the map. A
project that requires that work per map is a project that supports four maps.

### The navigation mesh is unreachable

There is a further obstacle specific to the platform. CounterStrikeSharp
exposes a `CCSNavArea` wrapper type but provides no function that returns one.
Reaching the engine's own navigation mesh would require signature-scanning for
the `TheNavMesh` global and reverse-engineering the `CNavMesh` structure
layout, for which no published signature exists.

So the plugin has: ray traces, player positions, player velocities, weapon
states, and game events. No geometry, no mesh, no pathfinding, no collision
queries. Everything else has to be constructed from those primitives.

---

## 3. Mathematical and algorithmic foundations

This section comes early because everything downstream rests on it. The central
insight of the project is that a navigation graph — the thing the plugin cannot
obtain from the engine — can be *measured* rather than requested, and once you
have a graph, a large body of classical algorithm theory becomes immediately
applicable.

All notation below is written in `snake_case` or in the style of C# and Python
identifiers. No Greek symbols are used anywhere in this document.

### 3.1 A graph is a set of vertices and a set of edges

The formal object is standard. A graph consists of a vertex set and an edge
set, where each edge relates a pair of vertices. Cormen, Leiserson, Rivest and
Stein's *Introduction to Algorithms* develops the representations and
traversals used here in its graph algorithms part; Skiena's *Algorithm Design
Manual* is the more practical companion, and is particularly good on the
question this project actually faced, which is not "which algorithm" but "how
do I model my problem as a graph in the first place".

For our purposes:

```
vertex        = a small region of the map a player has stood in
edge          = an observed transition between two such regions
edge_weight   = the cost of making that transition
```

The vertex set is discovered by sampling. The edge set is discovered by
watching consecutive samples from the same player. Neither requires any
knowledge of the map's geometry.

### 3.2 Why observed adjacency is stronger than geometric adjacency

A conventional navigation mesh derives connectivity from geometry: two polygons
are connected if they share an edge and the height difference is traversable.
This requires the geometry.

The breadcrumb graph derives connectivity from history: two regions are
connected if something has actually moved between them. This is a *stronger*
guarantee, not a weaker one. Geometric adjacency can be wrong — a shared edge
with a clip brush across it, a ledge that looks traversable and is not, a gap
that requires a jump the pathfinder does not know about. Observed adjacency
cannot be wrong about traversability, because the traversal is the evidence.

Its weakness is coverage rather than correctness. The graph knows only where
players have been. This is discussed at length in section 4.

### 3.3 Shortest paths and the A* algorithm

Given a weighted graph, the shortest-path problem is classical. Dijkstra's
algorithm solves the single-source case and is covered in CLRS. A* is the
informed variant, introduced by Hart, Nilsson and Raphael in their 1968 paper
*A Formal Basis for the Heuristic Determination of Minimum Cost Paths*, and
treated thoroughly in Russell and Norvig's *Artificial Intelligence: A Modern
Approach* under informed search.

The plugin uses A* with a straight-line heuristic:

```
f_score(node)   = g_score(node) + heuristic(node, goal)

g_score(node)   = accumulated cost from the start
heuristic(a, b) = sqrt((a.x - b.x)^2 + (a.y - b.y)^2 + (a.z - b.z)^2)
```

**Admissibility.** A heuristic is admissible if it never overestimates the true
remaining cost. Straight-line distance between two points cannot exceed the
length of any path between them, so this heuristic is admissible, and A* with
an admissible heuristic is guaranteed to return an optimal path. This is the
standard result and the reason the heuristic was chosen despite better-informed
alternatives being conceivable.

**The jump penalty and a deliberate departure.** Edge costs are not pure
distance:

```
edge_cost(a, b) = distance(a, b) + (needs_jump ? JUMP_PENALTY : 0)
JUMP_PENALTY    = 400
```

Adding a constant to certain edges means the heuristic is no longer strictly
admissible with respect to true traversal cost, so optimality is no longer
guaranteed. This is intentional and the trade is worth stating plainly: a route
that requires every bot to land a jump precisely is a route that will strand
bots, and a slightly longer route with no jumps in it is operationally better
than a shorter one that fails intermittently. The penalty biases against jump
links without forbidding them, so a jump remains available when it is the only
connection.

### 3.4 Line simplification

A raw A* path through 48-unit cells produces one waypoint every 48 units. A
corridor of thirty cells becomes thirty waypoints describing a straight line.

The classical solution is polyline simplification, introduced independently by
Ramer in 1972 and by Douglas and Peucker in 1973, and universally known as the
Ramer-Douglas-Peucker algorithm. The canonical form recursively discards points
within a distance tolerance of the chord between the endpoints.

The plugin implements the same idea in the bearing domain rather than the
distance domain, which is cheaper and better suited to a path that will be
walked rather than drawn:

```
simplify(path, angle_tolerance_deg):
    kept = [path[0]]
    for i in 1 .. path.length - 2:
        bearing_in  = bearing(path[i - 1], path[i])
        bearing_out = bearing(path[i], path[i + 1])
        if angle_gap(bearing_in, bearing_out) > angle_tolerance_deg:
            kept.append(path[i])
    kept.append(path[last])
    return kept
```

A point is kept only where the path actually turns. In practice this reduces a
several-hundred-node path to between eight and thirty waypoints.

### 3.5 Clustering without knowing the number of clusters

The map's duel geometry is derived by clustering the positions where players
die. The number of genuine duel positions on a map is precisely what is being
discovered, so any algorithm requiring the cluster count as an input is
unsuitable. That rules out k-means and its relatives.

The algorithm used is **greedy leader clustering**, described in Hartigan's
*Clustering Algorithms* (1975) and often called the leader algorithm. It makes
a single pass, assigning each sample either to the first existing cluster
within tolerance or to a new cluster of its own:

```
for sample in samples_ordered_by_time:
    target = null
    for cluster in clusters:
        if cluster.team != sample.team:                                continue
        if distance_xy(cluster.mean_pos, sample.pos) > XY_RADIUS:      continue
        if abs(cluster.mean_pos.z - sample.pos.z)   > Z_TOLERANCE:     continue
        if angle_gap(cluster.mean_yaw, sample.yaw)  > YAW_TOLERANCE:   continue
        target = cluster
        break
    if target == null:
        target = new_cluster()
        clusters.append(target)
    target.add(sample)
    target.recompute_means()
```

Three properties made it the right choice:

- **The cluster count is an output.** The map tells you how many positions it
  has.
- **The tolerance is physically meaningful.** "Within ninety units and forty
  degrees" means "the same doorway, held the same way". A k-means cluster count
  has no such interpretation in map terms.
- **It is incremental.** New samples extend existing clusters without
  re-solving from scratch, which matters because the sample bank grows across
  sessions.

Its known weakness is order dependence: the same samples in a different order
can produce different clusters. This is mitigated by processing chronologically,
which is also the order in which the map was genuinely learned.

**Why this replaced fixed-grid binning.** The first implementation snapped each
sample to a fixed grid cell and grouped identical keys. Fixed cells have
boundaries and boundaries are arbitrary:

```
two samples 5 units apart, either side of a boundary   -> never merged
two samples 95 units apart, inside one cell            -> always merged
```

Measured on a real 133-sample bank, this discarded 83% of the data. Three
"separate" pre-aim spots 62 to 115 units apart, all facing within 9 degrees of
each other, were one position fragmented three ways. Switching to leader
clustering lifted samples used from 23 to 54 on the same bank.

### 3.6 Circular statistics

Facing directions cannot be averaged arithmetically. Two samples at 179 degrees
and -179 degrees are two degrees apart, and their arithmetic mean is zero, which
points in exactly the wrong direction.

The correct treatment is from directional statistics — Mardia and Jupp's
*Directional Statistics* is the standard reference. Each angle is converted to a
unit vector, the vectors are summed, and the direction of the resultant is the
mean:

```
sum_x    = sum over members of cos(radians(yaw))
sum_y    = sum over members of sin(radians(yaw))
mean_yaw = degrees(atan2(sum_y, sum_x))
```

The resultant's *length* is also useful: it measures concentration, running
from zero for uniformly scattered angles to one for perfect agreement. The
plugin derives each learned angle's facing tolerance from it, so a tightly
agreeing cluster produces a tight tolerance and a loose one produces a forgiving
one.

### 3.7 Confidence as relative separation, and hysteresis

Several decisions require choosing between competing options where the choice
must not oscillate. The most important is inferring which bombsite a human
bomb carrier is heading for.

Confidence is expressed as relative separation:

```
separation = 1.0 - (nearest_distance / second_nearest_distance)
```

This is scale-free and bounded between zero and one. Standing exactly midway
between two sites gives zero. Being twice as close to one as to the other gives
0.5. A raw distance would be meaningless here because maps differ in size.

The switching rule adds a margin:

```
if new_choice != current_choice and separation < current_confidence + HYSTERESIS:
    keep current_choice
```

This is Schmitt-trigger logic, standard in control and signal processing:
requiring a challenger to beat the incumbent by a margin rather than merely
match it. Without it, any measurement near a decision boundary oscillates, and
in practice that means an entire team changing its mind about which site to
attack several times a second while a human wanders around near the middle of
the map.

The same pattern recurs throughout: route choice, target selection, contact
attribution. Any decision that can flip does flip, unless something stops it.

### 3.8 Sampling without replacement, and why not to optimise win rate

The playbook must choose a team plan each round. The obvious approach is to
track win rates and prefer the best-performing plays.

This is the multi-armed bandit framing, treated at length in Sutton and Barto's
*Reinforcement Learning: An Introduction*, and the usual objective is to
converge on the best arm while exploring enough to be confident it is the best.

**That objective is wrong here, and it is worth being explicit about why.**

A round of Counter-Strike turns on aim, timing, one lucky spray, a mis-thrown
grenade, and a dozen factors no team plan controls. The outcome signal carries
far more noise than signal with respect to the play chosen. A selector chasing
it will converge on whatever happened to win early.

Worse, convergence is itself the failure mode. The opponent is an adaptive
human who profits from predictability far more than they lose from facing a
slightly weaker plan. A side that has converged on its best play is a side you
can read after three rounds and beat for the remaining twenty-seven.

So the selector maximises variety subject to using every play equally, which is
achieved by sampling without replacement — a shuffled bag:

```
draw_from_bag(team, options):
    if bag[team] is empty:
        bag[team] = shuffle(options.names)
        if bag[team][0] == last_called[team]:
            swap(bag[team][0], bag[team][1])
    name = bag[team].remove_first()
    return options.find(name)
```

| Scheme | Immediate repeat possible | Readable after k rounds |
|---|---|---|
| uniform random | yes, probability one over n | no, but clumps badly |
| win-rate greedy | yes, and likely | **yes, quickly** |
| shuffled bag | only across a bag boundary | no |

The bag gives the strongest guarantee available: every play runs once before any
play runs twice, and the order within each bag is unpredictable. The swap on
reshuffle handles the single repeat a bag cannot otherwise avoid, where the last
play of one bag is the first of the next.

Win and loss records are still kept. They are used for reporting and for the
maturity criterion — every play must have been tried a minimum number of times
before a map counts as learned — but never for selection.

### 3.9 Weighted scoring with hard filters

The position solver ranks candidate holding positions. Its score is a weighted
linear sum, but the two most important criteria are implemented as **filters,
not terms**:

```
if not can_see(candidate, bomb):     reject      # hard filter
if coverage_count < MIN_COVERAGE:    reject      # hard filter

score = coverage_count * COVERAGE_WEIGHT     # 10.0
      + distance_to_site * DISTANCE_WEIGHT   #  0.004
      + back_wall_bonus                      #  up to 3.0
```

The distinction matters. If "can see the bomb" were a heavily weighted score
term rather than a filter, a position with spectacular angle coverage could
outrank it and a defender would be placed somewhere it cannot see the thing it
is defending. Some criteria are not negotiable and should not be priced.

### 3.10 Greedy selection with a diversity constraint

Having scored candidates, the solver must choose several that are spread out
rather than clustered on the single best piece of ground:

```
for candidate in sorted_by_score_descending:
    if chosen.count >= POSTS_PER_SITE:                         break
    if not far_enough_from(candidate, taken, MIN_SPACING):     continue
    chosen.append(candidate)
    taken.append(candidate.position)
```

This is greedy selection under a dispersion constraint, closely related to
maximal marginal relevance in information retrieval. It does not produce the
optimal spread — the general problem of choosing a maximally dispersed subset is
NP-hard — but it produces a good one in a single pass, which is the correct
engineering trade for something that runs during freezetime.

---

## 4. The breadcrumb navigation graph

`kai_breadcrumbs.cs`, 1,447 lines.

### The core idea

Every position a bot occupies is walkable. It got there. Recording positions
therefore produces a set of known-good standing locations for free, requiring
no geometry and no authoring.

The step that converts a point cloud into a graph is this: **two consecutive
positions from the same bot constitute a proven traversable link.** Something
got from one to the other, under its own power, within a fraction of a second.

### Quantisation

Sampling ten bots ten times a second for a match produces roughly a quarter of a
million records, almost all describing ground already covered. Positions are
therefore quantised into cells:

```
cell_x   = floor(x / CELL_SIZE_XY)     # 48 units
cell_y   = floor(y / CELL_SIZE_XY)     # 48 units
cell_z   = floor(z / CELL_SIZE_Z)      # 32 units
cell_key = cell_x + ":" + cell_y + ":" + cell_z
```

Forty-eight units horizontally is approximately the width of a player, so one
cell is one standing position. Thirty-two vertically is enough to keep a walkway
distinct from the floor beneath it, which matters enormously on maps with
vertical structure.

A node records its cell key, a representative position, a visit count, and a
flag recording whether it has ever been observed with the occupant grounded.

### The ground flag

A cell observed only mid-jump is somewhere bots pass *through*, not somewhere
they can stand. Only grounded nodes are offered as candidate positions to the
solver or as snap targets for pathfinding. Without this distinction, defenders
get assigned to hold positions in mid-air.

### Edges and the jump flag

```
on sample for bot b at position p:
    key = cell_key(p)
    record_or_update_node(key, p, grounded)
    if last_key[b] exists and last_key[b] != key:
        record_or_update_edge(last_key[b], key, needs_jump = was_airborne)
    last_key[b] = key
```

The `needs_jump` flag records that a transition required leaving the ground. It
feeds the A* cost penalty described in section 3.3.

### Nearest-node lookup by cell ring

Answering "what is the nearest recorded standing position to this arbitrary
point" is the single most frequently asked question of the graph. The distance
measure weights height heavily:

```
distance = sqrt(dx * dx + dy * dy + dz * dz * 4.0)
```

so that a node on the floor above is not selected as the nearest match for one
below.

The search walks outward in Chebyshev rings from the query point's own cell,
rather than scanning every node:

```
for ring in 0 .. max_ring:
    if first_hit_ring >= 0 and ring > first_hit_ring + 1:
        break
    scan_shell(centre_cell, ring)
```

It continues one ring past the first hit, because a node in the next ring can
still be nearer in straight-line terms than one at the corner of the current
ring. The original implementation was a linear scan over the entire dictionary —
2,541 entries on Mirage — which was affordable only while the function was
called rarely. It is now called at multiple radii, for multiple candidates,
whenever a bot fails to snap onto the graph.

### Saturation: knowing when the map has been walked

Recording must stop, or the file grows without limit and the plugin spends
processing time learning nothing.

```
quiet_round = new_nodes_this_round <= SATURATION_NEW_NODES

may_latch   = map_is_exempt
              or node_count >= SATURATION_MIN_NODES       # coverage floor
              or rounds_recorded >= SATURATION_MAX_ROUNDS # patience limit

saturated   = consecutive_quiet_rounds >= SATURATION_ROUNDS and may_latch
```

The coverage floor exists because the naive test conflates two very different
situations: *the map has been fully explored* and *the bots repeated
themselves*. Three quiet rounds of five bots running the same corridor looks
identical to a finished map.

Measured across four maps:

| Map | Nodes | Edges | Average degree | Bounding-box fill |
|---|---|---|---|---|
| de_mirage | 2,541 | 5,607 | 4.41 | 46.4% |
| de_dust2 | 1,457 | 2,454 | 3.37 | 26.6% |
| de_inferno | 1,040 | 1,563 | 3.01 | 17.8% |
| de_cache | 969 | 1,463 | 3.02 | 14.1% |

Cache latched at 14% coverage while also being the physically largest of the
four. The floor rule would have kept it recording toward approximately 2,000
nodes, but the round ceiling got there first: recording terminated at 969 nodes
and 1,463 edges, and the map has since matured at that size (198 rounds over 11
matches, every play tried at least eight times). The graph has not grown since,
and it will not — a matured map keeps using what it learned and stops adding to
it. The practical consequence of settling sparse is that a route book generated
over a thin graph carries the occasional waypoint the graph cannot actually
path to, which is now handled at runtime rather than by re-recording: the stall
check registers a proven-unreachable waypoint by position for the map session,
and route fitting prunes it from every copy handed out afterwards (section 6).

**Connectivity check.** All four graphs are effectively single connected
components — Mirage and Dust2 exactly one, Inferno and Cache one plus a single
isolated node. This was verified explicitly, because a graph in several
components would produce pathfinding failures that look like bugs but are data
problems.

---

## 5. Routes: from graph to named paths

`kai_routes.cs`, 1,758 lines. The file contains a route generator that runs once
and a route follower that runs every tick.

### Generation

```
for each spawn_region, for each plant_site:
    for k in 1 .. ROUTES_PER_PAIR:
        path = a_star(spawn, site, penalising cells used by previous paths)
        routes.append(simplify(path, ANGLE_TOLERANCE))

patrol routes: loops through contested ground
rotate routes: site to site
coverage(route) = number of learned pre-aim angles the route passes
```

Penalising previously-used cells is a simple k-shortest-paths approximation that
produces genuinely distinct routes rather than minor variations of one.

| Kind | From | To | Used for |
|---|---|---|---|
| Execute | spawn | site | attacks and retakes |
| Patrol | loop | loop | Counter-Terrorist map control |
| Rotate | site | site | responding to information |

### Why routes are static and named

A route computed fresh each round would differ each round. That sounds like
unpredictability and is actually noise: each fresh route is unverified,
possibly bad, and indistinguishable from the last one in any way a human could
read or exploit.

Real unpredictability is a fixed set of genuinely distinct routes, each verified
walkable and verified different from the others, with the choice among them
made at random. The human cannot know which will be used; each one, when used,
is good.

Names also do practical work. Route de-duplication, the converge-on-loose-bomb
special case, and every log line that makes a round readable afterwards all key
off the route name.

### Snapping onto the graph

Snapping an arbitrary world point to a graph node is the operation that failed
most often in play. Measured over two sessions, **32 of 38 pathfinding failures
were the bot's own position failing to snap**, not the destination being
unreachable.

```
for radius in [400, 800, 1600]:
    candidates = nodes within radius, sorted by weighted distance
    if candidates is empty: continue
    if eye_position is provided:
        for candidate in candidates.take(8):     # trace budget
            if can_see(eye_position, candidate + chest_height):
                return candidate                 # nearest VISIBLE node
    return candidates[0]                         # nearest node, unseen
return null
```

Two ideas here. The radius escalates rather than giving up, because a start
point 900 units away is a far better start than no start at all. And candidates
are filtered by line of sight where a tracer is available, because the nearest
node is frequently on the other side of the wall the bot is stuck against, and
a path beginning there begins by walking through masonry.

The eye position is supplied for the *start* of a path and deliberately not for
the *destination*: a holding position behind cover is supposed to be out of
sight.

---

## 6. Walking a path, and getting unstuck

### Steering, and why it constrains the entire architecture

The plugin's only movement primitive is:

```
forward = dot(desired_direction, bot_forward_vector)
left    = dot(desired_direction, bot_left_vector)
pawn.m_forwardSpeed = forward * speed
pawn.m_leftSpeed    = left * speed
```

This is a shove in a direction. There is no obstacle avoidance, no path
following, no collision awareness. Reynolds' 1999 *Steering Behaviors for
Autonomous Characters* and Millington and Funge's *Artificial Intelligence for
Games* both treat steering as one layer of a stack whose lower layers handle
collision and whose upper layers handle path planning; here only the middle
layer exists.

It is perfectly safe over one graph cell, because a straight line between two
cells a bot has already walked between contains nothing to walk into. It is
catastrophic over a hundred cells.

**Every movement bug in this project's history traces to something using
steering as though it were a "go here" command.** The intent structure
deliberately contains no "go here" field for that reason; anything wanting a bot
to travel must go through the path follower.

### The path follower

```
steer(slot, origin, destination):
    if distance_xy(origin, destination) <= ARRIVE_RADIUS:
        return false                                    # arrived

    on_graph = is_reachable(origin, SNAP_RADIUS)
    if on_graph:
        last_good[slot] = origin                        # remember a proven position
        end_escape_if_running(slot)
    else if run_escape(slot, origin):
        return true                                     # escape owns movement

    leg = leg_for(slot, origin, destination)
    advance_cursor(leg, origin)
    check_progress(leg, origin, now)
    intent.steer_towards = leg.nodes[leg.cursor]
    return true
```

### Two-stage stall response

```
if distance_to_node < best_distance - STALL_IMPROVEMENT:
    best_distance = distance_to_node
    best_at = now
    return

if now - best_at < STALL_SECONDS:
    return

if not already_resolved:
    already_resolved = true
    new_path = solve(origin, destination)   # re-solve from where it ACTUALLY is
    if new_path is not empty:
        replace path; return

cursor += 1                                  # skip the unreachable node
```

The escalation encodes a belief about causes. The most likely reason a bot is
not progressing is that its path was solved from a position it has since left.
The second most likely is that the graph is wrong about a particular link.
Trying the cheap and likely fix first, and the destructive one second, is a
pattern that recurs throughout the codebase.

### Freezetime is not a stall

Both stall detectors — the route follower's and the plugin's — measure "not
getting closer for N seconds", and a bot held motionless by the game's freeze
period cannot get closer to anything. Executes are assigned at round start,
during freezetime, so every bot on a fresh route tripped its stall detector
exactly N seconds in: measured, all five bots on an execute logged "made no
progress for 4.0s" simultaneously and spliced approach paths that were never
needed, sometimes skipping a perfectly good first waypoint, at the start of
every single round.

The fix is a flag computed once per tick from the game rules (every rules read
is a native call, so once, not per bot) and pushed into the path follower,
which deliberately knows nothing about game state. While the flag is up, both
detectors push their measurement clocks forward instead of measuring, so the
stall clock starts counting from the moment movement is actually possible.
After the gate: zero splices within eight seconds of any round start across
forty-five measured rounds.

### Dead waypoints are remembered

When the stall check proves a waypoint unreachable — the graph offers no path
to it from a bot standing nearby — the waypoint's rounded position is
registered for the rest of the map session, and route fitting prunes every
registered position from every route copy it hands out from then on (the final
waypoint, the route's destination, is never pruned). The second bot handed the
route never walks into the wall the first one found.

Position-keyed rather than index-keyed, because fitting and splicing renumber
the waypoints per bot. Runtime memory rather than editing the route books,
because the books are per map, matured maps stop regenerating them, and a code
answer carries to every map — including ones whose bad waypoints have not been
discovered yet — while a data patch fixes exactly one file. The registry clears
on map change, since the coordinates mean nothing anywhere else.

### The escape ladder

A bot can end up somewhere the graph has never been. The response is layered
cheapest-and-most-certain first, and **never hands the bot back to the native
AI**:

| Stage | Action | Rationale |
|---|---|---|
| Retreating | walk back to the last position that snapped onto the graph | guaranteed walkable, because the bot walked out of it; needs no traces and no guessing |
| Candidates | try the five nearest recorded nodes in turn, three seconds each | the single nearest node to a wedged bot is often on the far side of what wedged it |
| Unsticking | shove backwards, then left, then right, then backwards with a jump | ordered rather than random: whatever it is wedged against, it arrived from somewhere it fitted |

The shoves are computed relative to the bot's own approach direction:

```
back_x, back_y = normalise(came_from - origin)
step 0:  ( back_x,  back_y)         # backwards
step 1:  (-back_y,  back_x)         # left of approach
step 2:  ( back_y, -back_x)         # right of approach
step 3:  ( back_x,  back_y) + jump  # backwards, changing height
```

Random probing was rejected: it takes many attempts to converge and, on screen,
reads as a broken bot rather than a stuck one.

The whole ladder is capped in duration. On expiry it stands down and the
ordinary follower tries again from wherever the bot now is, which is still the
plugin driving.

---

## 7. Learning the map's duel geometry

`kai_spot_learner.cs`, 964 lines.

### A death is a measurement

When one player kills another, two facts are established simultaneously:

- the victim was standing somewhere reachable and worth standing at,
- the killer was standing within line of sight of that place.

That is exactly the anchor-and-watch pair a "hold this angle" instruction needs,
except measured rather than guessed. Record enough deaths and the map's duel
geometry falls out of the data.

### Sampling and engagement identity

Each death produces one or more sample records carrying position, look
direction, team, kind, distance to the bomb where relevant, timestamp, and an
**engagement identifier shared by both participants**.

The engagement identifier exists for statistical honesty. One duel contributing
two samples to the same cluster must not be counted as two independent pieces of
evidence, or a single frequently-repeated fight inflates a position's apparent
importance.

Samples are filtered before storage. A death in mid-air, or in a position
failing a ground check, teaches nothing about where to stand.

### Emission

Clusters become two kinds of output. Hold spots carry an anchor (cluster mean
position), a watch point (cluster mean look direction projected forward), a
crouch flag, and a priority derived from the engagement count. Pre-aim spots
carry a trigger — where a bot must be standing for the angle to apply — a
trigger radius and height, a watch point, and a facing tolerance derived from
the cluster's own angular concentration.

### The position solver

`kai_solver.cs`, 411 lines.

**The inversion.** Every position chooser before the solver began from where a
bot happened to be standing and searched outward, which makes the answer depend
on the accident of the bot's position at the moment the bomb landed. A defender
that spawned on the wrong side of a site received the best position reachable
from there, not the best position on the site.

The solver inverts this: score every standable position against every known
angle in advance, keep the best few, and reduce the round-time job to
assignment.

```
for candidate in standable_nodes within SITE_RADIUS of site:
    eye = candidate + eye_height
    covers = []
    for angle in pre_aim_spots for this team:
        if distance_xy(angle.trigger, candidate) > 1600:   continue   # cheap reject
        if can_see(eye, angle.trigger + chest_height):     covers.append(angle)

    if covers.count < MIN_COVERAGE:                        continue   # hard filter
    if not can_see(eye, site_centre + bomb_watch_height):  continue   # hard filter

    back_wall = trace_fraction(eye, eye + 300 units away from site) * 300
    score     = covers.count * 10.0
              + distance_to_site * 0.004
              + (back_wall < 120 ? 3.0 * (1 - back_wall / 120) : 0)
```

The weights encode a clear priority. Coverage dominates because it is the entire
point of a holding position. Distance is a mild pull *outward*, so that between
two positions covering the same angles, the one that sees an attacker earlier
wins. The cover term rewards a wall close behind, capped so that a bot wedged
into a corner with no view cannot outscore a genuine position.

**Why it runs in-game and incrementally.** Scoring requires line of sight, line
of sight requires the map loaded, so it cannot be done offline against the JSON
files. Several hundred candidates against several hundred angles is tens of
thousands of traces, so it cannot be done in a single tick either. The solver
holds state between ticks and spends a fixed trace budget per tick until
complete, and only ever runs during freezetime or warmup.

---

## 8. The post-plant problem

This is the largest single body of work in the project and deserves treating at
length, because post-plant is where Counter-Strike rounds are actually decided
and where the native bots are least competent.

### 8.1 What changes when the bomb goes down

The moment a bomb is planted, the game the two sides are playing changes
completely.

**Before the plant**, the Terrorists must take ground and the Counter-Terrorists
must deny it. Time pressure is on the Terrorists. Trading kills favours the
defence, because a defender who trades has done their job.

**After the plant**, everything inverts. The Terrorists no longer need to kill
anybody; they need only prevent a defuse for forty seconds. The
Counter-Terrorists must now take ground, under time pressure, against an enemy
that knows exactly where they must eventually go. The bomb is a fixed point that
one side must physically stand on, motionless, for five to ten seconds.

Almost every element of good post-plant play follows from that single asymmetry.

### 8.2 The elements of good post-plant defence

These are the things a competent Terrorist side does, each of which had to be
built:

**Hold a ring, not a huddle.** Defenders spread around the bomb so that
approaches from every direction are covered, and so that no single grenade or
spray transfer kills two of them. A huddle is one well-thrown grenade from
losing the round.

**Every defender must see the bomb.** A defender who cannot see the bomb cannot
punish a defuse. This is the entire job. A position with a beautiful angle down
a corridor and no sight of the bomb is not a defensive position, it is a
bystander.

**Cover the entries, not the compass.** Dividing the circle into even arcs is a
reasonable default and an inferior answer to covering the specific doorways the
retake will actually come through. Those doorways are a property of the map and
of how the map is played.

**Do not overlap.** Two defenders watching the same doorway from different
positions have wasted one defender, because a third doorway is now unwatched.

**Have a wall behind you.** A defender with open ground behind them can be
flanked and shot in the back. A defender with a wall behind them can only be
approached from the front.

**Distance discipline.** A defender too close to the bomb dies to the grenades
thrown at the bomb. A defender too far away cannot punish the defuse in time.
There is a band, and it depends on the weapon.

**Late arrivals are a different problem.** A defender who reaches the site
twenty-five seconds after the plant is arriving into a fight that has already
started, into a ring that is already broken. Walking into the middle of it is
the worst available option.

**Speed of rotation matters more than caution on the way.** A defender crossing
empty map to reach a site under attack is spending the round's most precious
resource on corners that contain nobody.

### 8.3 The ring: sector division and post claiming

```
assign_terrorist_sectors():
    slots = living terrorists within holding range of the bomb
    base_bearing = bearing from bomb to the tracked human, if known,
                   else bearing to the busiest known approach
    arc_size = 360 / slots.count
    for i, slot in enumerate(slots):
        sectors[slot] = base_bearing + (i * arc_size)
```

The fan is even; the only free parameter is where it starts. Anchoring it on the
most likely threat direction puts one defender's arc straight down the line the
retake is expected from, and the rest spread away from that — so the side covers
the real threat first and the theoretical ones afterwards.

This deliberately does not point everybody at the threat. Five defenders all
facing one entrance leaves every other entrance open, which against a competent
retake is worse than facing nothing in particular.

### 8.4 Post selection: three filters that were added one at a time

Each of the following was added in response to an observed failure.

**Filter one: the post must be near the bomb.** The solver works to an
1,800-unit site radius, which is correct for "positions belonging to this site"
and far too wide for "positions holding this plant". Measured on Mirage, six of
sixteen occupied posts were 1,315 to 1,374 units from the bomb — not a ring
around it, simply bots standing elsewhere while the bomb was defused without
them. A separate and much tighter cap now applies: a ring is only a ring if
everybody on it can see the middle.

**Filter two: the post must watch a known entry.** The threat points are built
from Counter-Terrorist clearing anchors and Counter-Terrorist-side pre-aim
triggers near the bomb, de-duplicated at 250 units. This is as close to "the five
or six doorways the retake comes through" as the recorded data provides. A
candidate post is traced against each; covering at least one is preferred over a
better bearing. A defender that can see none of them is decoration.

**Filter three: posts must be spaced.** A minimum separation is enforced between
claimed posts, so two defenders cannot end up sharing a spray transfer.

Additionally, the watch directions themselves are separated, which is a
different constraint from separating the positions. Two defenders on opposite
sides of a site can still both be looking down the same lane.

### 8.5 The wall-facing bug, and verification from the real position

An observed and initially puzzling behaviour: defenders would take a correct
ring position and then face a wall.

The cause was that the covered-angle set was computed by tracing from the
**assigned post's coordinates**, while the bot holds from wherever the path
follower actually stopped it — anywhere within a ninety-unit arrival radius.
Ninety units is easily enough to put a doorframe between the bot's eye and an
angle that was cleanly visible from the post. The set was cached and never
re-checked, so the bot faced that wall for the remainder of the round.

Compounding it: only 3 of 32 defenders in one session reached within 120 units
of their assigned post, and the median post covers eleven angles, so there was
ample scope for the selected one to be the blocked one.

Three fixes, layered:

- the covered set is now traced from the bot's actual position,
- the position at which the set was scored is recorded, and drift beyond a
  threshold forces a re-score,
- before the crosshair is committed each tick, the chosen angle is traced from
  the bot's real eye; if blocked, that angle is struck from the set permanently
  and the caller falls back to watching the bomb.

The third is self-healing: a set scored slightly wrongly prunes itself within a
few sweeps.

### 8.6 The rotation sprint

A defender crossing the map after a plant was clearing every corner on the way,
which is correct pre-plant behaviour and wrong post-plant. The corners contain
nobody, and the thirty seconds spent on them is the round.

```
if not post_plant:                    return CAREFUL
if enemy_visible or under_fire:       return CAREFUL
if arsenal_says_bot_is_dry:           return CAREFUL   # its weapon state is not ours

journey_start = distance to bomb when this rotation began
threshold     = max(journey_start * SPRINT_FRACTION, SPRINT_DANGER_RADIUS)

if distance_to_bomb > threshold:      return SPRINT
return CAREFUL
```

Sprinting means: knife drawn for the movement speed bonus, walking disabled,
and the watch target set to a point down the direction of travel rather than at
any corner.

**Why the split is by fraction rather than a fixed range.** Half the journey
means half of *this bot's* journey. A bot caught in spawn when the bomb goes
down and a bot fifty units outside the site have wildly different amounts of
empty map ahead of them, and any single distance threshold would make one
careful far too early and the other reckless right up to the defensive ring.

The danger radius is the floor beneath that. However long the journey, the final
approach into the defence is always walked properly, because that is where
somebody is genuinely waiting.

**A leak worth recording.** The sprint function only runs for a bot the route
follower is driving. If a higher-priority behaviour claimed the bot mid-rotation
— contact support, or a resupply — the function would never run again, the
weapon would never be restored, and the bot would spend the rest of the round
holding a knife while believing it had a rifle. A per-tick sweep now ends any
sprint that has not been confirmed within half a second.

### 8.7 Overwatch: a role for the late arrival

A defender arriving twenty-five seconds after the plant cannot usefully join the
ring. The ring is what the retake is currently breaking, and a bot arriving
alone into a broken ring dies without trading.

The alternative is to stop outside and hold a line onto the bomb itself. This is
worth something the ring is not: whoever defuses must stand still, in the open,
on a known spot, for five to ten seconds. A rifle looking at that spot from
outside the fight beats a rifle inside it.

**How far outside is a question about the weapon, not about the map:**

| Class | Holding range |
|---|---|
| Negev, M249 | 1800 |
| Rifles (AK, M4, AUG, SG, Galil, Famas) | 1400 |
| Sniper rifles, if picked up | 2000 |
| Submachine guns | 800 |
| Deagle, Revolver | 900 |
| Other pistols | 650 |
| Shotguns | 400 |

These are *holding* distances, not maximum ranges. A rifle can hit at three
thousand units; it is worth *sitting* at fourteen hundred, far enough to see a
defuse begin and to be outside the fight on the site, near enough that the shots
land. A shotgun at fourteen hundred is a spectator. There are no AWPs in this
game mode, so nothing here assumes one; the sniper rifles are listed only
because one can be picked up from the ground.

**Finding the position by tracing ahead.** The first implementation tested only
the bot's current position: is it within holding range, and can it see the bomb?
Both conditions rarely coincided. Measured over two sessions: 38 evaluations, 29
still closing, 8 in range but with no line of sight, and exactly one bot ever
settled. On real maps the line to the bomb opens later than the range band
begins, by which point the bot has walked inside the ring and is no longer a
candidate.

The fix is to look ahead:

```
find_overwatch_ahead(bot, range):
    floor = range * (1 - RANGE_TOLERANCE)
    heading = travel_heading(bot)
    for step in 0, 200, 400, ... LOOKAHEAD:
        probe = origin + heading * step
        probe_to_bomb = distance_xy(probe, bomb)
        if probe_to_bomb > range:                     continue   # still too far
        if probe_to_bomb < floor and inside_ring:     break      # past the band
        if not is_reachable(probe, SNAP_RADIUS):      continue   # inside a wall
        if not can_see(probe + eye, bomb):            continue   # no line from there
        if not can_see(origin + eye, probe + eye):    continue   # cannot get there
        return probe
    return null
```

The bot finds the spot where the line opens *before* it walks past it, and stops
there. Two guards on each probe matter: the probe must be within the breadcrumb
snap radius, or a point eight hundred units down a heading can land inside a
wall; and there must be a clear line from the bot's current eye to the probe,
which rules out probes on the far side of the wall the bot is presently behind.

Once settled, the bot anchors, crouches, and watches the bomb — with **no glance
sweeping**. The entire value of the position is that it covers the one place a
defuser must stand still, and a bot that looks away to check a corridor has
surrendered the only thing it was contributing.

---

## 9. The retake: how the CT side answers

`kai_retake_director.cs`, 4,028 lines.

### Four phases

| Phase | Behaviour |
|---|---|
| Rally | everyone converges on a ring short of the site, knives out on the long legs, and holds until enough of the side is set — then the whole retake enters on the same tick |
| Inspect | the site is swept, with beats assigned so the sweep is partitioned rather than duplicated; the designated defuser stages with eyes on the bomb |
| Bait | the defuser walks to the bomb and taps a fake defuse to draw a hidden lurker out; clearers hold their angles |
| Commit | the defuser defuses; the others hold cover through the bar |

The bomb clock outranks all of it: a defuse already running, spare time
exhausted, or no Terrorists left alive each jump the machine straight to
Commit from any phase, Rally included, so the choreography can never delay a
forced defuse.

### Rally: arriving together on defence

The phase clocks originally started at the plant and every bot walked its own
leg from wherever the round had left it, which made arrival order a function
of walk distance. Measured over thirteen plants: assigned walks ranged from 97
to 2,095 units against a twelve-second inspect window, in eight of the
thirteen at least one assigned clearer never reached its spot before the round
resolved, and the typical picture when the defuse began was the defuser plus
zero or one clearer actually set — a queue of fair duels for the lurker,
presented in arrival order. The single genuinely coordinated plant in that
sample, three of four clearers set at the commit, was also the only one where
the defuse bar ran twice.

This is the same disease the Terrorist execute had (section 11), and the cure
is the Counter-Terrorist flavour of the same medicine, with one constraint the
T side does not have: the bomb clock is real, so the gather must live inside
the spare-time arithmetic rather than beside it.

At the plant, given at least two live CTs and spare time above a floor
(roughly the inspect and bait budgets with margin), the machine opens in Rally
instead of Inspect. Every CT — the defuser included, heading for its staging
spot — is steered to a ring short of the site. On legs longer than a knife
threshold the bot sprints with the knife drawn for the movement speed,
borrowing the rotation sprint's rules wholesale: contact ends the knife
immediately, the final stretch is always approached with the gun up, a dry
bot's weapon state belongs to the arsenal and is never fought over, and
restoration goes through the inventory-aware restore rather than a blind slot
switch. On the ring the bot stops, gun out, facing the site.

Release is the first of three triggers: enough of the side set on the ring
(`ceil(alive * 0.66)` — everyone but one, for three to five alive, which is
the intended tolerance for a straggler), a hard cap on the gather, or spare
time reaching the floor. The release is a single phase flip, so every bot
leaves the ring on the same tick, and the inspect clock starts at that moment
rather than at the plant, so the sweep keeps its full window instead of losing
it to the gather. Measured after the change: releases at five to seven seconds
with three of four set.

Arriving together is also what makes the focus fire work. Contact support
already stacks every bot with line of sight onto the first fight; what it
lacked was teammates present when the first fight started. A synchronised
entry means the first Terrorist seen is looked at by several guns at once
instead of meeting the side one bot at a time.

### The ring around the defuse

Authored clearing spots are assigned greedily, nearest bot first, and there
are only a handful of them near any plant — so on most retakes at least one
clearer has no authored spot at all. During Inspect that bot sweeps a beat
like everyone else. From Bait onward it used to be left entirely stock, and
stock is worse than nothing here: with USE suppressed (so covering bots never
wander off to defuse), native post-plant logic walks a stock CT to the bomb to
defuse it, the suppression blocks the actual defuse, and the bot simply stands
on the bomb. Several spotless clearers produce a pile of CTs standing on the
one thing that cannot hurt anybody, covering nothing, backs to everything.
The assignment code carries a comment documenting this exact fault being fixed
for the Inspect sweep; the fix had stopped one phase short.

From Bait onward a spotless clearer is now given a computed ring post: a real
recorded position — the same pool the scans use, lurk spots and learned duel
angles, never free geometry that might sit inside a crate — in a donut around
the bomb, selected to maximise bearing separation around the clock face from
every position the defence already occupies, with a hard angular floor that
only bends when the map physically cannot meet it and a linear spacing floor
that never bends. The watch point faces outward, from the bomb through the
post and beyond, because the threat arrives from outside the ring and the one
direction guaranteed to hold nothing dangerous is inward. The fabricated post
goes into the same assignment table as the authored ones, so from the next
tick the bot is driven by the identical en-route, arrive, and cover machinery
— one assignment path, two sources of spots.

The authored spots gained the linear spacing test at assignment time as well:
a candidate anchor within the spacing floor of one already assigned is
skipped, because two bots in one pocket are one grenade, one spray, and one
uncovered approach.

### Lurk spots and inspection beats

```
build_lurk_spots():        positions near the bomb where a Terrorist could
                           plausibly be hiding, from learned hold spots and
                           solved posts
assign_inspection_beats(): divide uncleared spots among available bots so the
                           sweep is partitioned, not duplicated
sweep_opportunistically(): a bot that happens to walk into sight of an
                           uncleared spot marks it cleared in passing
```

The opportunistic sweep is free progress and matters more than it sounds: a
significant fraction of the site gets cleared by bots simply walking past.

### Defuser discipline

This is the behaviour that must not be interruptible, and it was the subject of
repeated iteration.

The rule is simple to state: **once a bot has begun a defuse, it does not come
off it.** Being shot at while defusing is not a reason to stop. The team-mates
are there to take the fights. A defuse abandoned at two seconds has cost the
round for nothing, because the two seconds are lost and the bomb is still armed.

Implementation:

```
if intent.source_name == "defusing:committed":
    never release the movement pin, under any circumstance

if bomb_is_being_defused():
    phase = Commit          # checked FIRST in the phase machine, unconditionally
```

The second rule was added after observing the phase machine, which recomputes
every tick, transition from Commit back to Bait twice in one session — calling
off a running defuse to go and bluff again.

**The stage give-up.** The defuser waits out the sweep on an authored staging
spot with eyes on the bomb — when it can reach one. Some staging spots are
simply unreachable from where the round put the defuser, and the same spot
proved unreachable on every plant of a session on two different maps: the
defuser spent the entire inspect window "enroute", skipping node after node,
never anchored and never inspecting. A spot not reached by sixty percent of
the window is now written off, the path is forgotten, and the plain bomb-watch
standoff takes over — a worse position that actually exists. Measured from the
inspect clock, not the plant, so a rally in front of the phase does not burn
the give-up budget on the gather.

### The fake defuse

Tapping the defuse produces the defuse sound and then stopping is a genuine
technique: it draws a hidden Terrorist out of a corner the sweep could not see
into.

**The tap that never happened.** The Bait branch originally released the
defuser on the assumption that native pathing would carry it to the bomb — the
comment said so verbatim — and the logs proved it wrong: across two full
sessions the fake defuser produced thirty-five "no tap yet" lines and not one
actual tap, closing on the bomb at walking-wounded pace or not at all, because
nothing native was taking it there. This is the third appearance of the same
released-and-assumed fault (clearer approaches and the defuser's staging
branch being the first two). The defuser is now actively steered to the bomb
along the graph during Bait, with a direct fallback for the final stretch
inside the follower's arrive radius, against the same tap-range constant the
tap logic itself uses. The following session produced a tap on every bait
phase that reached one — walk-ins of three hundred units in three seconds
where the released version managed a hundred units in six.

**One tap.** The bluff has either worked or failed after the first one; a second
tap tells a lurker nothing the first did not, and simply spends bomb timer. The
Bait phase originally ran a six-second timer regardless, which is six seconds
of clock spent on a bluff whose result was already known.

Two subtleties in the implementation:

- the tap counter increments when the hold *starts*, so a naive check flips the
  phase mid-tap and cuts the sound short; the condition waits for the release,
- the check must be a **latch**, not a live test, because the fake-defuse driver
  schedules a repeat hold shortly after each release, which flips a live test
  back and lets the phase fall into Bait a second time.

### Cover that follows the human

Three retake-side consumers of the handicap's tracked position (section 10),
all added after a session established that a human Terrorist defending their
own plant could sit on a coordinate the contact list knew exactly and pick the
cover off one by one.

**Hold angles prefer the human's doorway.** A covering bot's angle is chosen
far, distinct, and never the bomb; a candidate within the covering radius of
the tracked human now outranks every merely-distant one, with the arc
separation still applied — so the first clearer claims the human's doorway
and the rest spread across the other approaches. One dedicated pair of eyes
per known threat, not the whole rotation.

**Held angles follow.** An angle once taken was sticky for the round, which
is right in general and wrong when the one known threat has relocated: the
held angle is dropped and reselected when the human moves outside its covering
radius, rate-limited to a few seconds so the cover rotates on the human's
moves, not their strafing.

**Inside close range, the angle yields to the fact.** A pinned bot's view
cone is wherever its assigned angle points, and native vision only acquires
what falls inside the cone — with the human's exact position in hand, bots
were still stared at for seconds before reacting, because their forced watch
faced somewhere else and native eyes never got the chance. Within a close
threat range the assigned angle is abandoned and the cone goes to the human's
actual position, so the moment they peek they are already in view. The
defuser is excluded, its eyes belong on the bomb.

All three are attention only: the view cone moves, the trigger stays native.

### The watchdog

```
if no defuse has started within WATCHDOG_SECONDS of the clearing phases ending:
    log at ERROR level, with the phase it tripped in
    drop all Counter-Terrorist overrides for the remainder of the round
```

This is an explicit admission of failure that hands the side back rather than
leaving them stuck under a plan that is not working. Its firing rate is among
the better health metrics for the whole system: it fell from 7 firings across
19 planted rounds to 2 across 30 after the staging and phase fixes.

Two later refinements. The message originally read "planted for N seconds",
which was the threshold constant, not a measurement — the watchdog re-arms
while the retake inspects and baits, so the bomb had often been down for twice
that; it now reports what the timer actually knows, the seconds since arming
and the phase at the trip. And the trips that remain have changed character:
they now occur in Commit, after the choreography has completed cleanly —
walk-in done, tap made, defuser released — because the released native AI
declines to start a defuse while enemies are alive, or the defuser died
mid-bar and the promoted replacement hunted instead. That is a shallower
failure than the one the watchdog was built for, and it is the plugin's
current known limitation on the CT side (section 17): during those trips the
"overrides" being dropped are, mostly, nothing, since Commit deliberately
writes none.

### Measured effect of the post-plant work

| Metric | Before | After |
|---|---|---|
| Defuses committed | 6 | 33 |
| Watchdog firings | 7 | 2 |
| Time from plant to first commit | only ever with about 5s left | 16 to 27 seconds, median 21 |

The single largest contributor was unglamorous. The defuser's staging branch
wrote a source name and issued no movement command at all, on the assumption
that native pathing would carry it to the staging position. It did not. Every
hold-back log line read `stage:ctClear_020:enroute` and the bot never arrived,
never anchored, never inspected, and was still wandering when the phase timer
expired.

A second measured round followed the walk-in, give-up, and rally work. Fake
taps went from zero in thirty-five attempts across two sessions to one per
bait phase; watchdog firings fell again and moved entirely into the
post-release Commit signature described above; contested defuse bars ran to
within three seconds and one second of completion where they previously never
started; and rally releases measured five to seven seconds with three of four
set, against a baseline where the typical commit had zero or one clearer in
position.

---

## 10. The handicap: knowledge, not aim

### Why a handicap exists at all

The project's premise is that bots should be made harder by improving their
decisions. That premise has a ceiling, and the ceiling was reached.

After the work described above, the bots execute, rotate, hold angles, retake,
trade, and manage their post-plant properly. A competent human still beats them
comfortably, and the reason is not tactical. It is that a human improvises. A
human tries something the recorded data has never seen, notices that it worked,
and does it again. The learned map data has no notion that a position was used
last round, and the bots have no memory of having died to it, so **a human who
finds one angle the bots handle badly can farm it indefinitely.**

Raising the difficulty setting would address this by making the bots shoot
better, which defeats the entire purpose of the project.

### What the handicap does

Three constants at the very top of the plugin class:

```
BOT_GOD_MODE_VS_HUMAN_TRACKING    = true      the whole handicap
BOT_GOD_MODE_VS_HUMAN_DELAY       = 30.0f     seconds from round start
BOT_GOD_MODE_VS_HUMAN_POST_PLANT  = true      keep working after the plant
```

After thirty seconds of each round, the enemy side is told where the human is,
continuously. The position is written into the contact list under a reporter
identifier belonging to no bot, and refreshed every tick so it never ages out.

These are declared `static readonly` rather than `const` specifically so that
toggling them does not produce unreachable-code warnings in either position —
a compile-time constant makes one branch of every test provably dead.

### Why thirty seconds

Perfect knowledge from the first tick removes the opening of the round entirely:
five bots converge on the human's spawn position and every round becomes the
same fight. The delay leaves the early round genuinely open, so the human still
wins or loses it on their own play, and the handicap engages only once the round
has developed into something the bots would otherwise have to read.

### What it deliberately is not

**Line of sight is always required before a bot aims at the tracked position,
and there is no flag to change that.**

This is the line between a difficulty handicap and a wallhack, and it is drawn
deliberately. Knowledge and aim are different things:

- Knowing where the human is changes *where the side goes*: which site it
  defends, who rotates, where it clears first, which angle it holds. All of that
  reads, on screen, as bots that have worked something out.
- Aiming through a wall reads as exactly what it is.

There is also an engineering reason. Contact support outranks the route
follower, the post-plant hold, and the pre-aim layer in the behaviour chain. If
the tracked contact bypassed line of sight, every bot on the enemy side would
select it every tick, drop whatever it was doing, and stand motionless with its
crosshair on masonry for the remainder of the round.

### The version that made the bots worse, and what it taught

The first implementation fed the tracked position into the contact-support
layer. Comparing behaviour inside and outside the tracked windows, per minute:

| Behaviour | Untracked | Tracked |
|---|---|---|
| Contact support | 1.5 | 15.7 |
| Post-plant hold | 0.4 | 10.7 |
| Reroute | 0.8 | 2.2 |
| Team rotation | 0.2 | 0.7 |

The moment they knew where the human was, they **came to them**. Rotations
tripled, rerouting nearly tripled, contact support went up tenfold.

And the bots got measurably easier to beat. In Counter-Strike, the player
holding an angle beats the player walking into it, almost regardless of skill.
Perfect information had converted a defensive problem into a queue of bots
walking one at a time into a held crosshair.

The correction was to feed the knowledge to **positioning decisions only**:

- **Site attribution**, so the side sets up on the site the human is actually
  approaching. This was additionally rate-limited to once per second and given a
  decay, because a per-tick contribution had inflated per-site contact counts to
  3,908 against a handful from real sightings, converting a pressure measure
  into a dwell-time measure.
- **Pre-aim angle selection**, so a bot already holding an angle holds the one
  covering the human rather than an arbitrary one.
- **Glance-sweep settling**, so a defender on a post stops cycling twenty angles
  every four-tenths of a second and settles on the one covering the human. A bot
  cycling twenty angles is looking at the right one five percent of the time; a
  bot that has settled is looking at the doorway before the human comes through
  it.
- **Sector anchoring**, so the post-plant defensive fan starts on the bearing to
  the human.

After the correction, per-minute rates inside the tracked windows: rotations at
0.26 times the untracked rate, rerouting 0.70, route picking 0.10. The bots now
*stay put* when they know where the human is, which is the correct response and
the opposite of the first attempt.

**A bug worth recording.** The first version of the pre-aim bias had no team
check, so it applied to every bot regardless of side — including the human's own
bot team-mates, who preferentially held the angle covering their own team-mate.
Five bots watching a friendly and nobody watching the way in. There is now a
single accessor that returns the tracked position only when the handicap is
enabled, the position is current, and the asking bot is on the opposing side.

### The window the handicap went dark in

The scenario the handicap matters most is precisely the one it originally
skipped: a human Terrorist defending their own plant. A human post-plant does
the one thing the recorded data handles worst — finds a corner the sweep
cannot reach, waits for the defuse sound, and takes the fight on their own
terms — and it is repeatable every round. This is the improvisation problem
from the top of this section at its sharpest, and it is exactly where the
tracked position should have been earning its keep.

It was not, for four separate reasons that only added up when read together.
The pre-aim bias — the one mechanism that points a crosshair at the human's
doorway — is hard-disabled for the whole CT side the moment the bomb is
planted, a side effect of an earlier fix that stopped pre-aim writing
horizontal watch targets over the defuser's need to look down at the bomb. A
measurement comment in the glance-sweep code had already caught the
consequence from the other side: the bias fired 69 times in 659 tracked
seconds, none of it during a retake. Contact support deliberately ignores the
synthetic contact (the lesson of the version that made the bots worse). Site
attribution is moot once the bomb is down. And the retake director, which owns
every CT from plant to round end, contained no reference to the tracked
position at all — clearers held angles frozen at plant time while the contact
list knew the human's exact coordinates, and the human reported standing in
front of pinned bots "for quite some time" before they reacted, because a
forced view cone facing elsewhere is a cone native vision never acquires
through.

The correction wires the retake director to the same single gated accessor as
everything else and spends the knowledge in the same currency — attention,
never aim: hold angles prefer and follow the human's doorway, and inside close
range the assigned angle yields to the human's actual position (all three
consumers are described from the retake's side in section 9). The line drawn
in this section holds throughout: the bots watch the recorded angle nearest
the human, or at close range the position itself, and the duel that follows is
still fought entirely by the native AI.

### The result

The bots' aim mechanics are untouched by all of this. Their reaction time, their
spray control, and their accuracy are whatever the game's difficulty setting
says they are. What changed is that they are now looking at the right doorway
when the human comes through it — which is what a good human opponent does, and
what these bots cannot work out for themselves.

---

## 11. Team decisions: playbook and command

### The playbook

`kai_playbook.cs`, 882 lines. Eleven plays per map, generated to fit whatever
sites the map turns out to have:

```
Terrorist:            t_exec_s{n}       fast direct hit
                      t_split_s{n}      two groups, two approaches
                      t_default_s{n}    map control first, hit late

Counter-Terrorist:    ct_hold_s{n}      weight one site
                      ct_hold_spread    even
                      ct_aggro          contest early
                      ct_guard_bomb     play the bomb rather than the site
```

Selection is the shuffled bag described in section 3.8. The playbook also
watches how a round develops against what the play assumed and calls an audible
when they diverge: contact on the wrong site, the bomb somewhere unexpected, the
side down numbers.

**Audibles latch.** The conditions behind most audibles persist once true —
the bomb stays on the floor, the same site stays stacked, the side stays down
bodies — so on a cooldown alone the same call re-fired every twelve seconds
for as long as its condition held: fifteen GuardBomb calls and nineteen
SwitchSite calls in single sessions, each repeat tearing down and re-issuing
the same routes mid-walk. The last audible's kind and target site are now
remembered per team, and a call that merely repeats the standing one is
suppressed; a different call, or the same call to a different site, always
goes through, a fake rotate and a real one count as the same decision, and the
latch clears whenever a new play is called. After the change: zero to one
repeats per session, with the suppressions logged.

### Command

`kai_command.cs`, 505 lines.

**Leaders.** One per side, always a bot, never the human, stable for the whole
match rather than recomputed each round — a leader that changes every thirty
seconds is not a leader. The replacement is chosen only when the incumbent is
gone, by lowest slot, purely so the choice is deterministic and the same bot
keeps the job. The leader is the anchor the side synchronises to and is never
sent on decoy duty.

**Reading the carrier.** The site a Terrorist side hits is not chosen in the
abstract; it is wherever the bomb is going, because a site take without the bomb
is just a fight. With a bot carrier the play decides. With a **human** carrier
there is no plan to read, so the site is inferred from movement using the
relative-separation confidence and hysteresis described in section 3.7. The bots
then commit to the human's choice rather than executing elsewhere and leaving
them alone with it.

**Arriving together.** A site take that trickles in is five duels in sequence,
each of which the defence wins.

```
Peeling    decoys leave first; the main group may not commit yet
Staging    main group gathers at STAGING_DISTANCE and waits
Committed  everybody goes on the same tick

ready     = main group members within STAGING_DISTANCE + TOLERANCE of the site
enough    = ready >= ceil(alive * READY_FRACTION)              # 0.7
stragglers_out = first_arrival set and now - first_arrival >= MAX_STAGING   # 12.0
approach_out   = nobody arrived and staging_elapsed >= MAX_APPROACH         # 45.0
commit    = enough or stragglers_out or approach_out
```

The straggler clock starts when the **first** bot arrives at the staging
distance, not when the phase opens. The distinction is the whole feature, and
it was learned the hard way: executes begin at round start, during freezetime,
against routes five to ten thousand units long, and a clock started at the
phase opening expired while the whole group was still mid-map — every commit
across two full sessions, fifteen of fifteen, read "0 of N in position,
staging timed out, going anyway", which is the trickle the module exists to
prevent, wearing a synchronisation costume. Measured from first arrival the
timer does what its name says: early arrivals hold, stragglers get their
twelve seconds, and the group goes. The approach ceiling is the backstop for a
group that never arrives at all, a wiped main group ends the execute cleanly
rather than waiting out a timer, and an execute that begins with an empty
roster — a map-boundary artefact — now stays idle instead of staging nobody
for twelve seconds.

The ready fraction is 0.7 rather than 1.0 because waiting for a straggler who is
dead or stuck means never going at all. The decoys leaving first is deliberate:
the noise should already be in the wrong place before the real hit begins.

---

## 12. Execution: the per-tick behaviour chain

`kai_tactics_plugin.cs`, 12,495 lines. The main file, and the place where all
decisions are resolved into a single output object per bot per tick.

### The narrow waist

Everything the plugin decides ends up in one structure:

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

Two native hooks consume it — one for aim, one for movement — and nothing else
touches the game directly. Every behaviour competes to write this object, and
`source_name` records which one won, which is what makes a session log readable
afterwards.

### The chain

Evaluated per bot per tick, first match wins:

```
1.  celebration fire        (round over, purely cosmetic)
2.  resupply                (out of ammo; an empty gun outranks any angle)
3.  contact support         (a team mate is in a fight)
4.  plant / defuse commitment
5.  Terrorist post-plant hold  (including overwatch for late arrivals)
6.  loose bomb guard
7.  route follower          (including the rotation sprint)
8.  pre-aim hold
9.  glance sweep
```

**The ordering carries the whole design, and it is where the subtlest bugs live.** A
layer returning true suppresses everything below it. Two of the three worst bugs
found in playtesting were layers claiming bots they had no business claiming,
and in both cases the symptom was not the layer misbehaving but the layers below
it silently not running.

### Aim precedence

The aim hook has its own separate ordering:

```
1. real contact          -> hand straight back to the native AI
2. threat aim            -> noise, or recent damage
3. the AI's own look-at
4. authored angles       (pre-aim, glance, watch targets)
```

**Handing real duels back to the native AI is deliberate.** It is better at them
than anything here, and this project's job is deciding where a bot should be
looking *before* the duel starts, not winning the duel. This is also what keeps
the aim mechanics human-like: no code in this project ever improves a bot's aim
during a fight.

Threat aim filters noise by **travel distance rather than straight-line
distance**, capped at fifteen hundred units. Travel distance is the correct
measure: gunfire through a wall across the map should not drag a holding bot off
its angle, but footsteps in the next room should. Measured reaction latency:
median 0.7 seconds, ninetieth percentile 2.0, maximum 3.0.

Critically, hearing something turns the **head** but does not release the
movement pin. A noise does not make a holding bot wander off, which is the
correct trade.

### Transit clearing

What makes a route a push rather than a march:

```
apply_transit_clearing(bot):
    heading = travel_heading(bot)              # from velocity, not from facing
    candidates = pre_aim angles within COVERAGE_RANGE
                 and within TRANSIT_ARC_DEG (70) of heading
                 and confirmed by can_see(eye, angle.trigger + chest)
    sort by most-directly-ahead, then by distance
    intent.watch = candidates.first
```

Three filters — range, arc, and a confirming trace — so a moving bot pre-aims
spots it is genuinely approaching rather than staring at walls. Roughly fourteen
percent of forward-arc scans find nothing, which is the honest rate for "there
is no known angle ahead of me".

There is a hard backstop: if an authored watch point is more than ninety-five
degrees away from the bot's actual velocity, it is discarded and replaced with a
point down the direction of travel. Aiming backwards while walking forwards is
the single most obviously wrong thing a bot can do, and this catches it
regardless of which layer produced the angle.

### Zones and spacing

```
refresh_ct_zones():   divide the bearing circle around the map centre evenly
                      among living Counter-Terrorists
apply_pre_aim():      hold only if the trigger is in THIS bot's zone
                      and no anchored team mate is within MIN_BOT_SPACING
```

Both gates are required. Without the zone check the entire side converges on
whichever triggers happen to be nearest spawn. Without the spacing check, two
bots share a spray transfer.

### Route fitting

A route is a fixed path between fixed endpoints; the bot receiving it is
wherever it happens to be standing. Measured: the median distance from a bot to
waypoint zero of its newly assigned route was **1,462 units**, with a maximum of
4,522.

```
fit_route_to_bot(bot, route):
    join = index of nearest waypoint, height-weighted
    gap  = distance from bot to route.waypoints[join]
    if gap > ROUTE_APPROACH_DISTANCE:
        approach = solve_path(bot_position, route.waypoints[join])
        waypoints = approach + route.waypoints[join:]
    else:
        waypoints = route.waypoints[join:]
    return copy_of(route) with waypoints        # same name, own list
```

The copy is essential. The stall check splices waypoints into whatever route a
bot is running, and handing out the shared instance would edit the route book in
memory for every bot that ever takes that route.

Fitting is also where the dead-waypoint registry from section 6 is applied:
every copy handed out has the session's proven-unreachable waypoints already
pruned, destination excepted, so a route's known walls are hit at most once per
map.

---

## 13. Weapons, ammunition, and engagement range

`kai_arsenal.cs`, 657 lines.

**The problem:** bots pull the trigger on empty guns while loaded rifles lie on
the floor beside them. The native AI has no concept of resupplying mid-round.

```
is_dry(bot) = (magazine + reserve) <= DRY_THRESHOLD      # 5, not 0
              and no other loaded weapon in inventory

dry and safe:             go and pick something up
dry and in a close fight: knife rush
dry and far from a fight: break contact, go for a gun
```

The threshold is five rather than zero because a bot with three bullets left is
about to have a problem and should solve it before it becomes one. The check
correctly treats a full pistol as not-dry even with an empty rifle.

**Shared weapon memory.** A weapon seen by anybody is remembered by everybody
for the round. "There is a rifle on the ground at Mid Doors" is a real callout
and it remains true after the bot that saw it has moved on or died. One bot
claims each weapon; the claim is released on death or on giving up; the weapon
is re-verified before travelling to it.

**Knife rush range.** The original implementation had no distance ceiling: any
visible enemy triggered a charge. Of 111 measured charges the median covered 481
units, but twenty exceeded 800 and the longest was 1,516 — a bot sprinting most
of the length of the map at somebody holding a rifle. The ceiling is now 600
units, roughly two seconds of running, which is about as long as anybody
survives crossing open ground toward a loaded weapon. Beyond it the bot restores
its weapon and goes for a gun instead.

**Holding ranges** are described in section 8.7 and are used by the overwatch
role.

---

## 14. Knowing when to stop learning

`kai_maturity.cs`, 538 lines.

Every learning system in this project writes a file, and every one of them
eventually stops learning anything, because a map has a finite number of angles
and a finite number of ways to walk between them.

An earlier version counted completed matches. That was wrong twice over: a match
abandoned after three rounds counts for nothing despite having taught something,
and a match count measures how long you played rather than what came of it.

Maturity is therefore measured against **the evidence itself**:

| Stage | Meaning | Criterion |
|---|---|---|
| Seeded | has something to work with | enough samples to emit any spots at all |
| Mapped | the geometry is known | post-plant and clearing sample counts past a ceiling, with a round-count floor |
| Mature | the plays are known too | every play tried at least a minimum number of times |

Real recorded reasons from shipped files:

```
mapped:  "150 rounds reached the ceiling of 150 with 142/150 post-plant
          and 236/150 clear samples"
matured: "198 rounds, all 11 plays tried at least 8 times (96 calls in total)"
```

Rounds are counted only as a floor, to prevent a quiet start being mistaken for
a finished map. The thresholds themselves are evidence counts.

---

## 15. Observability

`kai_tactics_log.cs`, 406 lines, and `kai_comms.cs`, 965 lines.

### Logging

This code runs inside native hooks and per-tick listeners, where there is no
debugger and no useful stack trace. Every function in the project calls the
logger at least once. Per-tick paths use a throttled variant, rate-limited by
caller-supplied key, so a line inside a tick hook does not print sixty-four
times a second per bot.

Three levels, changeable at runtime with no rebuild. Output goes to the console
and to a timestamped file, rolled on map change and pruned to the newest twenty.

Two details worth recording because both were bugs first:

- Flush timing uses a managed monotonic clock, not the game clock. Every game
  clock property is a call through to native code that is not ready during
  plugin load, and the game clock restarts from zero on map change.
- The throttle table is cleared on map change, because a stored timestamp from a
  previous map is in the future relative to the new one and would suppress its
  key until the clock caught up.

### Communications

Everything the plugin decided was previously visible only in a log file read
afterwards. That is fine for finding bugs and useless while playing: from inside
the game, a coordinated execute and five bots wandering look identical until
somebody dies.

Bot names are assigned by the game and change between rounds, so they are
useless as identities. Four fixed names are handed out, sticky by slot, held for
as long as the bot lives, with the prefix following the human's side.

Callouts are derived from geometry against per-map anchor tables, degrading to
bearings and distances on maps without one.

Every message goes to one team only. This is not etiquette: a call of "taking B
through apartments" broadcast to the server hands the defence the round.

---

## 16. Failure modes found in playtesting

Worth publishing alongside the design, because each was invisible from inside
the game and obvious from the log.

### Bots supporting their own fights

The contact refresh records a contact against the slot that saw the enemy. The
contact-support layer then scanned every contact for one it could see — and the
bot that saw an enemy trivially satisfies "can see it". **722 of 1,032 support
responses were a bot swinging onto its own fight.**

The aim override was harmless, since real contact hands straight back to the
native AI a layer above. The *priority* was not: the function returns true, so
every bot that saw an enemy dropped its route, its hold, and its retake
assignment, and reverted to native wandering.

The fix was one line: skip any contact this bot reported itself.

### Clearers that never moved

The clearing driver cleared the anchor, set a source name, logged "en route",
and returned — having issued no movement command, on the assumption that native
pathing would carry the bot in. It did not. **Of 23 measured approach runs, 18
finished further from the assigned spot than they started**, several by more
than a thousand units, while the log reported them en route throughout.

The identical fault appeared later in the defuser's staging branch, with the
identical symptom.

### Bots frozen against walls

Route waypoints were followed by pointing the steering at them, and steering has
no obstacle avoidance. **27 mid-round freezes totalling 270 seconds**, the worst
a bot frozen for 48 seconds of a 90-second round with its distance to the
waypoint logged unchanged at 3,337 units throughout.

Two contributing causes: route assignment never checked whether the bot was near
the route's start, and nothing ever noticed that a bot had stopped making
progress.

### Posts that were never reached

Ring posts were assigned by score and reached by shoving. **191 log lines of
"moving to its ring post" against 13 of "holding its ring post"**; of 28 approach
runs only 3 ever got within 120 units.

After pathing was added, 18 of 24 runs ended closer — but only 4 reached the
post. The remaining problem is assignment rather than movement: post selection
scores on coverage, distance, and cover, and never on whether the post is
reachable in the time the bomb has left.

### The handicap that made the bots worse

Described in full in section 10.

### The phase machine that called off its own defuse

Described in section 9. A live test where a latch was required, in a state
machine that recomputes every tick.

### The staging clock that started before anyone could arrive

The Terrorist execute's synchronisation measured its readiness correctly and
its time wrongly: the straggler timeout ran from the phase opening, at round
start, during freezetime, against routes five to ten thousand units long.
**Fifteen of fifteen commits across two sessions logged "0 of N in position,
staging timed out, going anyway"** — a synchronisation feature whose every
single activation was the trickle it existed to prevent. The clock now starts
at the first arrival (section 11).

### Freezetime read as a stall

Every bot handed a route at round start tripped the stall detector exactly
four seconds in, because a frozen bot makes no progress and the detector could
not tell obedience from obstruction. Five simultaneous "made no progress"
splices at the start of every execute, every round. Described in section 6;
after the gate, zero splices within eight seconds of any round start.

### The pile on the bomb

From Bait onward, a retake clearer with no authored spot was left entirely
stock — with USE suppressed so it could not defuse. Native post-plant logic
walks a stock CT to the bomb to defuse; the suppression blocked the defuse;
the bot stood on the bomb. Several such bots stood on it together, covering
nothing, and the human reported standing in front of them uncontested. The
assignment code already carried a comment fixing this exact fault for the
Inspect phase; the fix had stopped one phase short. Described in section 9.

### The fake defuse that never tapped

The Bait branch released the defuser to native pathing to walk it to the bomb
— the comment promised it would — and across two sessions the result was
**thirty-five "no tap yet" lines and zero taps**. The third appearance of the
released-and-assumed fault in this list, after the clearers and the staging
branch. The lesson generalises: in this codebase, "native pathing will take it
there" has been wrong every single time it has been written down.

---

## 17. Known limitations

**Post assignment ignores reachability.** The solver scores positions on
coverage, distance, and cover. It does not know how long a bot will take to
reach one, so a post nineteen hundred units away at plant time is a post that
will not be occupied before the bomb detonates.

**Route joining can skip a route's early angles.** Fitting joins at the nearest
waypoint, which is geometrically correct but means a bot standing near the far
end of a route takes it without clearing the angles on the early legs.

**Sparse maps path badly.** The breadcrumb graph knows only where bots have
walked. On a map at fourteen percent bounding-box fill there is a great deal of
floor more than the snap radius from anything recorded. The escalating snap and
the escape ladder mitigate it; more recording fixes it, which is what the
coverage floor now enables.

**No peeking.** There is no jiggle peek, shoulder peek, or jump spot anywhere in
the codebase. Bots hold or they walk.

**Jump edges are recorded but under-used.** The jump flag is consumed by the
route generator as a cost penalty, and the escape ladder can press jump as its
last resort, but the path follower does not yet press it for a path link that
requires one. A route needing a hop stalls there.

**The commit handoff under fire.** Commit deliberately writes nothing for the
defuser — the native defuse logic was never the problem — but the native AI
declines to start a defuse while enemies are alive if hunting looks more
attractive, and a promoted replacement after a mid-bar death routinely never
touches the bomb. The remaining watchdog firings all carry this signature. The
obvious fix, steering the released defuser onto the bomb and injecting USE the
way the fake tap already does, would override that deliberate design decision
and has not been taken.

**Ring posts depend on recorded ground.** A computed ring post is only ever a
recorded position, which is what keeps it out of crates — and also means a
plant in a corner of the map with thin sample coverage can offer nothing in
the donut that clears the spacing tests. The bot stays stock for that plant,
with an error logged; more recorded rounds is the only real answer, and
matured maps no longer record.

**Difficulty remains bounded by the native duel.** Everything here decides where
bots are and what they are looking at. Once a duel begins, the native AI takes
over, and its aim is whatever the difficulty setting says. That is by design,
and the handicap in section 10 is the deliberate answer to it.

---

## 18. File reference

| File | Lines | Role |
|---|---|---|
| `kai_tactics_plugin.cs` | 12,495 | Main plugin: hooks, per-tick behaviour chain, console commands, route following, pre-aim, contacts, zones, post-plant hold, overwatch, rotation sprint, handicap, dead-waypoint memory, defuse watchdog |
| `kai_retake_director.cs` | 4,028 | Post-plant Counter-Terrorist plan: rally, inspect, bait, commit. Ring posts and tracked-threat cover. Solo retake state machine. Fake defuse with the walk-in. |
| `kai_routes.cs` | 1,758 | Route graph and A*, route generation, route book I/O, freeze-aware path follower and escape ladder |
| `kai_breadcrumbs.cs` | 1,447 | Navigation graph recorded from bot movement: quantisation, edges, saturation, ring search |
| `kai_comms.cs` | 965 | Team chat: sticky squad identities, callout tables, verbosity tiers |
| `kai_spot_learner.cs` | 964 | Deaths into hold spots and pre-aim angles: sample bank, clustering, emission |
| `kai_playbook.cs` | 882 | Play definitions, bag-based selection, audibles with the repeat latch, win and loss records |
| `kai_arsenal.cs` | 657 | Dry detection, shared dropped-weapon memory, knife rush, resupply, holding ranges |
| `kai_tactics_data.cs` | 565 | Shared types and JSON loader |
| `kai_maturity.cs` | 538 | Learning stages and stopping criteria, per map |
| `kai_command.cs` | 505 | Leaders, bomb-carrier reading, synchronised execute with first-arrival staging |
| `kai_solver.cs` | 411 | Incremental scoring of every standable position against every known angle |
| `kai_tactics_log.cs` | 406 | Levelled, throttled, file-backed logging |

Total: approximately 25,600 lines.

### On-disk artefacts, per map

```
<map>.json            plant sites, hold spots, pre-aim angles, solved posts
<map>_graph.json      breadcrumb nodes and edges
<map>_routes.json     generated routes with coverage counts
<map>_plays.json      playbook records: called, won, abandoned
<map>_maturity.json   learning stage and the reason it was reached
<map>_samples.json    raw death samples and the engagement counter
logs/<map>_<stamp>.log
```

Every write goes through a loader that takes a backup first.

---

## 19. Bibliography and a note on sources


### Relevant texts

**Graph representation, traversal, and shortest paths**

- Cormen, T. H., Leiserson, C. E., Rivest, R. L., and Stein, C. *Introduction to
  Algorithms*, 3rd edition, MIT Press, 2009. Graph representations, breadth-first
  and depth-first search, Dijkstra's algorithm, greedy algorithms and the
  greedy-choice property.
- Skiena, S. S. *The Algorithm Design Manual*. Particularly valuable on the
  modelling question — how to recognise that your problem is a graph problem in
  the first place.
- Sedgewick, R., and Wayne, K. *Algorithms*, 4th edition, Addison-Wesley.
  Practical treatment of graph data structures and priority queues.
- Goodrich, M. T., Tamassia, R., and Goldwasser, M. H. *Data Structures and
  Algorithms in Python*, Wiley, 2013. The adjacency-map graph representation
  used here — a dictionary of vertex keys to their incident edges — follows this
  book's graph ADT directly, which is why the breadcrumb graph stores string
  cell keys rather than integer indices into an array. Its chapters on priority
  queues and heaps cover the structure A* needs for its frontier, and its
  worked treatment of depth-first and breadth-first search is the clearest
  short account of the traversals used to check that a map's graph is a single
  connected component. Its pseudocode convention is also the one this document
  borrows: `snake_case` identifiers rather than mathematical symbols.

**Heuristic search**

- Hart, P. E., Nilsson, N. J., and Raphael, B. "A Formal Basis for the Heuristic
  Determination of Minimum Cost Paths." *IEEE Transactions on Systems Science
  and Cybernetics*, 1968. The original A* paper, including the admissibility
  condition relied on in section 3.3.
- Russell, S., and Norvig, P. *Artificial Intelligence: A Modern Approach*.
  Informed search, admissible and consistent heuristics, and the consequences of
  giving up admissibility deliberately.

**Line simplification**

- Ramer, U. "An iterative procedure for the polygonal approximation of plane
  curves." *Computer Graphics and Image Processing*, 1972.
- Douglas, D. H., and Peucker, T. K. "Algorithms for the reduction of the number
  of points required to represent a digitized line or its caricature." *The
  Canadian Cartographer*, 1973.

**Clustering**

- Hartigan, J. A. *Clustering Algorithms*, Wiley, 1975. The leader algorithm,
  and a clear treatment of why the number of clusters is sometimes an output
  rather than an input.

**Directional statistics**

- Mardia, K. V., and Jupp, P. E. *Directional Statistics*, Wiley. Circular means,
  resultant length as a concentration measure, and the reasons arithmetic means
  of angles are wrong.

**Decision-making under uncertainty**

- Sutton, R. S., and Barto, A. G. *Reinforcement Learning: An Introduction*, 2nd
  edition, MIT Press, 2018. Multi-armed bandits and the
  exploration-exploitation trade-off — cited here principally to explain why
  this project deliberately declines the usual objective.

**Game AI and steering**

- Millington, I., and Funge, J. *Artificial Intelligence for Games*, 2nd edition.
  Navigation meshes, waypoint graphs, path smoothing, and the layered movement
  model whose lower layers this project has to live without.
- Reynolds, C. W. "Steering Behaviors for Autonomous Characters." *Game
  Developers Conference*, 1999. The origin of the steering model, and useful for
  understanding exactly what a steering primitive does and does not promise.

### Upstream project

- ed0ard, *CS2-Bot-Improver*. The base plugin this work extends, and the source
  of the native post-plant patches described in section 1.
- CounterStrikeSharp, the .NET scripting framework this plugin is written
  against.
