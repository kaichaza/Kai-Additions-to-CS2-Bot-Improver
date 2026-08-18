# Mirage T routes

Generated `2026-08-16 10:02:47Z` from the breadcrumb navigation graph, after the
detour cap was applied. Ten Terrorist execute routes: five to A, five to B.

All start from the learned T spawn at `(1220, -118)`.

- **A site** ends at `(-471, -2136)`
- **B site** ends at `(-2043, 306)`

---

## All ten, sorted by waypoints

| Route | Waypoints | Length | Angles | To | Path |
|---|---:|---:|---:|:---:|---|
| `t_exec_s1_05` | **75** | 9,183 | 283 | B | T Spawn → Palace → **A Site** → Jungle → Connector → Catwalk → Mid → Underpass → B |
| `t_exec_s1_04` | 63 | 8,386 | 179 | B | T Spawn → T Ramp → **Back Alley → B Apartments** → Mid → Catwalk → Underpass → B |
| `t_exec_s1_02` | 61 | 7,529 | 174 | B | T Spawn → Palace → Jungle → **Sniper's Nest → Market** → B |
| `t_exec_s0_04` | 51 | 6,493 | 217 | A | T Spawn → T Ramp → **Back Alley → B Apartments** → Connector → Catwalk → A |
| `t_exec_s1_03` | 43 | 5,753 | 171 | B | T Spawn → T Ramp → Mid → Catwalk → Underpass → B |
| `t_exec_s1_01` | 40 | 5,027 | 158 | B | T Spawn → T Ramp → Mid → Catwalk → Underpass → B |
| `t_exec_s0_02` | 37 | 4,766 | 206 | A | T Spawn → T Ramp → Mid → Catwalk → Connector → A |
| `t_exec_s0_03` | 33 | 4,485 | 111 | A | T Spawn → Palace → A |
| `t_exec_s0_05` | 30 | 3,879 | 138 | A | T Spawn → Palace → A |
| `t_exec_s0_01` | 25 | 3,114 | 129 | A | T Spawn → Palace → A |

**Angles** is how many learned duel spots the route passes within 400 units of —
a rough measure of how contested it is. A high number means an informative,
dangerous route; a low one means a quiet flank.

---

## The B routes

### `t_exec_s1_05` — the long rotate, 75 waypoints

T Spawn → Palace → **through A site** → Jungle → Connector → Catwalk → Mid →
Underpass → B.

The longest route in the book at 9,183 units, and it crosses the A bombsite to
reach B. As a concept this is a real strategy: show at A, then rotate through
the middle to hit B. In practice it walks past most of the CT side to get
anywhere, and at 283 angles passed it is by a distance the most contested route
generated. A bot on this one is unlikely to arrive.

### `t_exec_s1_04` — apartments, 63 waypoints

T Spawn → T Ramp → **Back Alley → B Apartments** → Mid → Catwalk → Underpass →
B.

The standard B execute a person would run, arriving from the north through
apartments. It does dip back toward mid partway, which is not ideal, but the
apartments section is genuine and it is the only route in the book that uses
that approach.

### `t_exec_s1_02` — the southern flank, 61 waypoints

T Spawn → Palace → Jungle → **Sniper's Nest → Market** → B.

The only route that never touches mid. It runs down through Palace, across the
bottom of the map past Sniper's Nest, through Market, and into B from the
south-west. A deep flank through CT territory — slow, but it arrives from a
direction the defence is rarely set up for.

### `t_exec_s1_03` and `t_exec_s1_01` — mid to underpass

Both take T Ramp → Mid → Catwalk → Underpass → B, the direct route. `_01` is
the shortest path to B in the book at 5,027 units; `_03` is a 700-unit variation
on it.

---

## The A routes

### `t_exec_s0_04` — the long way, 51 waypoints

T Spawn → T Ramp → **Back Alley → B Apartments** → Connector → Catwalk → A.

Goes north toward B first, then cuts back down through connector to A. At 217
angles it is the second most contested route in the book. As a fake it is
plausible; as an execute it is a long walk.

### `t_exec_s0_02` — mid to connector, 37 waypoints

T Spawn → T Ramp → Mid → Catwalk → Connector → A. The standard mid-to-A route.

### `t_exec_s0_03`, `t_exec_s0_05`, `t_exec_s0_01` — Palace

All three are T Spawn → Palace → A, differing only in detail. See below.

---

## Two caveats

### A is far less varied than B

Three of the five A routes are the same approach. The dissimilarity check
compares how many graph nodes two routes share and rejects anything above 55%
overlap, but the Palace approach on Mirage is narrow enough that three variants
through it still pass that test.

So in practice an A execute has roughly **two distinct approaches**, not five,
and is much more predictable than a B execute. If that matters, `MaxSimilarity`
in `kai_routes.cs` is the lever: raising it toward 0.7 would admit A routes that
share the Palace entry but split later, through Ramp or Ninja.

B, by contrast, has four genuinely different approaches: mid/underpass,
apartments, the southern Market flank, and the long rotate through A.

### The callouts are inferred, not authoritative

The route files hold coordinates, not names. The callouts above come from
fitting boxes to the standard Mirage overview and asking which box each waypoint
falls in, so a route clipping the edge of a box gets attributed to it.

Treat the sequences as a good guide to where a route goes rather than as exact
radio calls. Where a route appears to enter and leave the same area twice, that
is usually a path running along a boundary rather than a genuine detour.

---

## Reading the routes live

Every waypoint arrival is logged from v1.16.0:

```
slot 4 waypoint 7/40 on 't_exec_s1_01' at (-412,-1180) | sweeping 3 angle(s), currently spot 118 | walking
slot 4 waypoint 8/40 on 't_exec_s1_01' at (-598,-940) | no known angle in view
slot 4 COMPLETED route 't_exec_s1_01', all 40 waypoint(s) reached
```

A bot that stops advancing partway through is visible immediately: the waypoint
count stops climbing while the round carries on.
