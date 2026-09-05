// kai_routes.cs
//
// KAI ADDITION. Not part of CS2-Bot-Improver.
//
// Static, reusable routes across the map, generated once from the breadcrumb
// graph and then read back every round.
//
// WHY STATIC
//
// A route computed fresh each round would be different each round, which
// sounds like unpredictability but is not: it is noise. Real unpredictability
// is a fixed set of genuinely distinct routes chosen from at random, because
// each one has been checked to be walkable and to differ from the others,
// while which one gets used is unknowable in advance. So the expensive graph
// work happens once, the result is written to disk, and the round-time job is
// reduced to picking and following.
//
// WHAT A ROUTE IS
//
// A list of waypoints from a spawn region to a bombsite, extracted by A* over
// the breadcrumb graph. Because consecutive graph nodes are one cell apart and
// a bot has physically walked between them, the straight line between two
// consecutive waypoints is safe to steer along, which is what makes the crude
// steering in this plugin usable for something as long as a full rotation.
//
// ROUTE KINDS
//
//   Execute   spawn to a site. What an attack or a retake runs on.
//   Patrol    a loop through contested ground, for holding map control.
//   Rotate    site to site, for responding to information.
//
// A fake rotation is not a separate kind. It is a Rotate route abandoned
// partway and reversed, which is exactly what the deception is: the enemy
// hears the movement, reads the rotation, and the rotation does not arrive.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace KaiBotTactics;

public enum KaiRouteKind
{
    Execute = 0,
    Patrol = 1,
    Rotate = 2,
}

public sealed class KaiRoute
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("kind")]
    public KaiRouteKind Kind { get; set; }

    // 2 Terrorist, 3 CounterTerrorist, 0 either. Routes are mostly reusable
    // between sides, since the ground does not change, but the ones that start
    // in a spawn are not.
    [JsonPropertyName("team")]
    public int Team { get; set; }

    // Index into PlantSites, or -1 for a route that is not site-bound.
    [JsonPropertyName("fromSite")]
    public int FromSite { get; set; } = -1;

    [JsonPropertyName("toSite")]
    public int ToSite { get; set; } = -1;

    [JsonPropertyName("waypoints")]
    public List<KaiPoint> Waypoints { get; set; } = new();

    [JsonPropertyName("length")]
    public float Length { get; set; }

    // How many known duel angles are seen from anywhere along the route. A
    // high number means an informative route; a low one means a quiet flank.
    [JsonPropertyName("coverage")]
    public int Coverage { get; set; }

    [JsonPropertyName("generatedUtc")]
    public string GeneratedUtc { get; set; } = "";
}

public sealed class KaiRouteBook
{
    [JsonPropertyName("mapName")]
    public string MapName { get; set; } = "";

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("generatedUtc")]
    public string GeneratedUtc { get; set; } = "";

    // Where each side starts. Learned rather than configured, by averaging
    // where bots are standing when a round goes live.
    [JsonPropertyName("spawns")]
    public Dictionary<string, KaiPoint> Spawns { get; set; } = new();

    [JsonPropertyName("routes")]
    public List<KaiRoute> Routes { get; set; } = new();
}

// A* over the breadcrumb graph.
//
// Built once from the recorder's node and edge sets, then queried repeatedly
// during generation. Kept separate from KaiBreadcrumbs so the recorder stays a
// recorder and nothing about routing leaks into it.
public sealed class KaiRouteGraph
{
    // Closest two waypoints may sit. Comfortably above the 90-unit arrival
    // radius, so a bot cannot be inside the next waypoint before it has left
    // the last, which is what turned the bearing between them into noise.
    public const float MinWaypointSpacing = 180.0f;

    // Hard floor on waypoint spacing. Above the 90-unit arrival radius by a
    // margin, so a bot can never be standing inside the next waypoint before
    // it has left the last: that is what reduced the bearing between them to
    // noise and had bots walking backwards.
    public const float AbsoluteMinSpacing = 110.0f;

    // A turn this sharp is kept regardless of the angle tolerance. Smoothing a hairpin
    // produces a straight line through whatever the corner went around.
    public const float SharpTurnDeg = 60.0f;

    private readonly Dictionary<string, KaiPoint> _points = new();
    private readonly Dictionary<string, List<(string To, float Cost)>> _adjacency = new();

    public int NodeCount => _points.Count;

    public bool Build(KaiBreadcrumbs crumbs)
    {
        _points.Clear();
        _adjacency.Clear();

        foreach (var pair in crumbs.GraphEdges())
        {
            var from = pair.From;
            var to = pair.To;

            _points[from.Key] = from.Position;
            _points[to.Key] = to.Position;

            float cost = from.Position.DistanceXY(to.Position.X, to.Position.Y)
                         + (MathF.Abs(from.Position.Z - to.Position.Z) * 2.0f);

            // Jump links cost extra so a route prefers walking where walking
            // is possible. A bot that has to jump a gap mid-rotation is a bot
            // that gets stuck on the lip of it.
            if (pair.NeedsJump)
            {
                cost += 400.0f;
            }

            AddLink(from.Key, to.Key, cost);
            AddLink(to.Key, from.Key, cost);
        }

        KaiLog.Event(nameof(Build),
            $"route graph built: {_points.Count} nodes, {_adjacency.Sum(a => a.Value.Count)} links");

        return _points.Count > 0;
    }

    private void AddLink(string from, string to, float cost)
    {
        if (!_adjacency.TryGetValue(from, out var list))
        {
            list = new List<(string, float)>();
            _adjacency[from] = list;
        }

        list.Add((to, cost));
    }

    public string? NearestKey(KaiPoint target, float maxDistance)
    {
        string? best = null;
        float bestDist = maxDistance;

        foreach (var kv in _points)
        {
            float dx = kv.Value.X - target.X;
            float dy = kv.Value.Y - target.Y;
            float dz = kv.Value.Z - target.Z;

            float dist = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz * 4.0f));

            if (dist < bestDist)
            {
                bestDist = dist;
                best = kv.Key;
            }
        }

        return best;
    }

    // Snap a point onto the graph, widening the search and preferring nodes
    // that can actually be seen from it.
    //
    // The flat 400 unit snap was the single largest cause of pathing failure
    // in play: 32 of 38 measured failures were the bot's own position failing
    // to snap, not the destination. On a sparse map, de_cache covers 14% of
    // its own bounding box, there is a great deal of floor more than 400 units
    // from anything recorded, and a bot standing on it could not be pathed at
    // all.
    //
    // Two changes. The radius escalates rather than giving up, because a node
    // 900 units away is a far better start than no start. And at each radius
    // the candidates are filtered by line of sight where a tracer is
    // available, because the nearest node is regularly on the other side of
    // the wall the bot is stuck against, and starting a path there produces a
    // route that begins by walking through masonry.
    //
    // eye is where to trace from, usually the bot's eye position. Pass null to
    // skip the visibility test, which is right for destinations: a hold post
    // behind cover is meant to be out of sight.
    public string? SnapKey(
        KaiPoint target,
        KaiPoint? eye,
        IReadOnlyList<float> radii,
        out float snappedAt,
        out bool sawIt)
    {
        snappedAt = -1.0f;
        sawIt = false;

        foreach (float radius in radii)
        {
            var ranked = new List<(float Dist, string Key, KaiPoint Pos)>();

            foreach (var kv in _points)
            {
                float dx = kv.Value.X - target.X;
                float dy = kv.Value.Y - target.Y;
                float dz = kv.Value.Z - target.Z;

                float dist = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz * 4.0f));

                if (dist <= radius)
                {
                    ranked.Add((dist, kv.Key, kv.Value));
                }
            }

            if (ranked.Count == 0)
            {
                continue;
            }

            ranked.Sort((a, b) => a.Dist.CompareTo(b.Dist));

            if (eye != null)
            {
                // Nearest node with a clear line to it. Capped at a handful of
                // traces so a wide radius on a dense map cannot cost hundreds
                // of them in one tick.
                int traced = 0;

                foreach (var candidate in ranked)
                {
                    if (traced >= 8)
                    {
                        break;
                    }

                    traced++;

                    var from = new Vector(eye.X, eye.Y, eye.Z);
                    var to = new Vector(
                        candidate.Pos.X, candidate.Pos.Y, candidate.Pos.Z + KaiHeights.Chest);

                    if (KaiRayTraceBridge.CanSee(from, to))
                    {
                        snappedAt = candidate.Dist;
                        sawIt = true;

                        return candidate.Key;
                    }
                }
            }

            // Nothing visible, or no tracer wanted. The nearest will do: a
            // start on the wrong side of a wall still beats no start, and the
            // stall check will notice if the resulting path does not work.
            snappedAt = ranked[0].Dist;
            sawIt = false;

            return ranked[0].Key;
        }

        return null;
    }

    public KaiPoint PointOf(string key)
    {
        return _points[key];
    }

    // Shortest path between two graph nodes. Returns null when there is none,
    // which after the connectivity guarantee in the recorder should only
    // happen if one of the endpoints could not be snapped.
    public List<string>? FindPath(string from, string to)
    {
        return FindPath(from, to, null, 0.0f);
    }

    // As above, but charging extra for ground already spoken for. This is how
    // several distinct routes are pulled out of one graph: penalise what the
    // last route used and the search is forced to find another way round.
    public List<string>? FindPath(
        string from, string to, HashSet<string>? penalised, float penalty)
    {
        if (!_points.ContainsKey(from) || !_points.ContainsKey(to))
        {
            return null;
        }

        var goal = _points[to];

        var open = new PriorityQueue<string, float>();
        var cameFrom = new Dictionary<string, string>();
        var best = new Dictionary<string, float> { [from] = 0.0f };

        open.Enqueue(from, Heuristic(from, goal));

        while (open.Count > 0)
        {
            string current = open.Dequeue();

            if (current == to)
            {
                var path = new List<string> { current };

                while (cameFrom.TryGetValue(current, out string? previous))
                {
                    current = previous;
                    path.Add(current);
                }

                path.Reverse();
                return path;
            }

            if (!_adjacency.TryGetValue(current, out var links))
            {
                continue;
            }

            float currentCost = best[current];

            foreach (var (next, cost) in links)
            {
                float candidate = currentCost + cost;

                if (penalised != null && penalised.Contains(next))
                {
                    candidate += penalty;
                }

                if (candidate >= best.GetValueOrDefault(next, float.MaxValue))
                {
                    continue;
                }

                best[next] = candidate;
                cameFrom[next] = current;
                open.Enqueue(next, candidate + Heuristic(next, goal));
            }
        }

        return null;
    }

    private float Heuristic(string key, KaiPoint goal)
    {
        var p = _points[key];
        return p.DistanceXY(goal.X, goal.Y);
    }
    // Thin a path down to the turns that matter.
    //
    // A raw path is one waypoint every 48 units, which is far more than a bot
    // needs, and the naive version of this dropped almost nothing: at that
    // resolution nearly every node is a turn of more than a few degrees, so
    // the angle test kept them all. On a measured de_mirage route book that
    // left 59% of waypoint gaps at or under the 90-unit arrival radius, with a
    // minimum gap of one unit.
    //
    // That is not a cosmetic problem. A bot reaches a waypoint it is already
    // standing past, immediately reaches the next, and the bearing to it
    // becomes noise pointing in an arbitrary direction. Bots were walking
    // whole routes facing backwards and hopping on the spot as the steer
    // target flipped every tick.
    //
    // So spacing is enforced as well as angle: a point is only kept if it is a
    // real corner AND far enough from the last one kept, with sharp turns
    // allowed to override the spacing so a hairpin is not smoothed into a
    // wall.
    public List<KaiPoint> Simplify(List<string> path, float angleToleranceDeg)
    {
        var result = new List<KaiPoint>();

        if (path.Count == 0)
        {
            return result;
        }

        result.Add(_points[path[0]]);

        for (int i = 1; i < path.Count - 1; i++)
        {
            var previous = result[result.Count - 1];
            var current = _points[path[i]];
            var next = _points[path[i + 1]];

            float inBearing = KaiFormation.Bearing(previous.X, previous.Y, current.X, current.Y);
            float outBearing = KaiFormation.Bearing(current.X, current.Y, next.X, next.Y);
            float turn = KaiFormation.AngleGap(inBearing, outBearing);

            float fromLast = current.DistanceXY(previous.X, previous.Y);

            // An absolute floor, checked before anything else.
            //
            // Nothing is emitted closer than this to the last point kept, not
            // even a hairpin, because a corner within the arrival radius is
            // one the previous waypoint already covers. Allowing sharp turns
            // to bypass the spacing left 37% of gaps under the arrival radius
            // on real data; this floor takes it to 3%.
            if (fromLast < AbsoluteMinSpacing)
            {
                continue;
            }

            // A hairpin is kept whatever the angle tolerance says: smoothing
            // one produces a straight line through whatever the corner went
            // around.
            bool hairpin = turn > SharpTurnDeg;

            // Anything else has to be both a corner and far enough along to be
            // worth stopping for.
            bool worthKeeping = turn > angleToleranceDeg && fromLast >= MinWaypointSpacing;

            // Keep a point on any real height change too: a staircase is a
            // straight line from above and not one to walk through.
            bool climbing = MathF.Abs(current.Z - previous.Z) > 40.0f;

            if (hairpin || worthKeeping || climbing)
            {
                result.Add(current);
            }
        }

        var last = _points[path[path.Count - 1]];

        // Never let the final waypoint collapse onto the one before it, or a
        // bot arrives at its destination and immediately "arrives" again.
        if (result.Count == 0
            || last.DistanceXY(result[result.Count - 1].X, result[result.Count - 1].Y) > 32.0f)
        {
            result.Add(last);
        }
        else
        {
            result[result.Count - 1] = last;
        }

        return result;
    }
}

// Builds the route book from the graph.
//
// Runs in one pass rather than incrementally, unlike the position solver: A*
// over a few thousand nodes is fast, and the number of routes wanted is in the
// tens rather than the thousands. The expensive part of the solver was the
// line-of-sight tracing, which this does not need.
public static class KaiRouteGenerator
{
    // How many distinct routes to try for per origin and destination pair.
    public const int RoutesPerPair = 5;

    // Two routes sharing more than this fraction of their waypoints are the
    // same route. Without a similarity test, asking for five routes returns
    // the shortest path five times over with trivial variations, which is the
    // conga line the whole exercise is meant to prevent.
    public const float MaxSimilarity = 0.55f;

    // Penalty applied to nodes already used by a chosen route, to push the
    // next search onto different ground.
    //
    // Was 2500, which bought dissimilarity at any price. By the fourth search
    // every sensible corridor was penalised into oblivion and A* returned
    // whatever absurd loop remained: on a measured de_mirage book this
    // produced an 8834 unit route for a 1242 unit journey, a seven-fold
    // detour that would have a bot walking for the whole round.
    public const float ReusePenalty = 1200.0f;

    // How much longer than the shortest route a route may be before it is
    // rejected as a detour rather than an alternative.
    //
    // Two and a half times covers a genuine long way round, such as reaching
    // B through apartments instead of mid, while cutting the paths that only
    // exist because everything sensible had been penalised. Four good routes
    // beat five where the fifth never arrives.
    public const float MaxDetourRatio = 2.5f;

    public static KaiRouteBook Generate(
        string mapName,
        KaiBreadcrumbs crumbs,
        KaiMapTactics tactics,
        Dictionary<string, KaiPoint> spawns)
    {
        string stamp = KaiTime.NowUtc();

        var book = new KaiRouteBook
        {
            MapName = mapName,
            GeneratedUtc = stamp,
            Spawns = new Dictionary<string, KaiPoint>(spawns),
        };

        var graph = new KaiRouteGraph();

        if (!graph.Build(crumbs))
        {
            KaiLog.Event(nameof(Generate), "route graph is empty, nothing to generate",
                KaiLogLevel.Error);
            return book;
        }

        // Executes: every spawn to every bombsite.
        foreach (var spawn in spawns)
        {
            int team = spawn.Key == "t" ? (int)CsTeam.Terrorist : (int)CsTeam.CounterTerrorist;

            for (int site = 0; site < tactics.PlantSites.Count; site++)
            {
                AddRoutes(book, graph, tactics, stamp,
                    spawn.Value, tactics.PlantSites[site],
                    KaiRouteKind.Execute, team, -1, site,
                    $"{spawn.Key}_exec_s{site}");
            }
        }

        // Rotations: every bombsite to every other. Team-agnostic, because the
        // ground between two sites is the same ground whoever is crossing it.
        for (int a = 0; a < tactics.PlantSites.Count; a++)
        {
            for (int b = 0; b < tactics.PlantSites.Count; b++)
            {
                if (a == b)
                {
                    continue;
                }

                AddRoutes(book, graph, tactics, stamp,
                    tactics.PlantSites[a], tactics.PlantSites[b],
                    KaiRouteKind.Rotate, 0, a, b,
                    $"rotate_s{a}_s{b}");
            }
        }

        // Patrols: spawn to the highest coverage solved CT post. Held ground
        // rather than an objective, so the destination is wherever sees the
        // most.
        if (spawns.TryGetValue("ct", out var ctSpawn) && tactics.SolvedCtPosts.Count > 0)
        {
            var ordered = tactics.SolvedCtPosts.OrderByDescending(p => p.Coverage).Take(4);
            int index = 0;

            foreach (var post in ordered)
            {
                index++;

                AddRoutes(book, graph, tactics, stamp,
                    ctSpawn, post.Position,
                    KaiRouteKind.Patrol, (int)CsTeam.CounterTerrorist, -1, -1,
                    $"ct_patrol_{index:D2}");
            }
        }

        KaiLog.Event(nameof(Generate),
            $"generated {book.Routes.Count} route(s) for '{mapName}': " +
            $"{book.Routes.Count(r => r.Kind == KaiRouteKind.Execute)} execute, " +
            $"{book.Routes.Count(r => r.Kind == KaiRouteKind.Rotate)} rotate, " +
            $"{book.Routes.Count(r => r.Kind == KaiRouteKind.Patrol)} patrol");

        return book;
    }

    // Find several genuinely different ways between two points.
    //
    // The first is simply the shortest. Each subsequent search penalises the
    // ground already used, so it is pushed onto a different corridor, and the
    // result is rejected outright if it still overlaps too much with one
    // already accepted.
    private static void AddRoutes(
        KaiRouteBook book,
        KaiRouteGraph graph,
        KaiMapTactics tactics,
        string stamp,
        KaiPoint from,
        KaiPoint to,
        KaiRouteKind kind,
        int team,
        int fromSite,
        int toSite,
        string label)
    {
        string? start = graph.NearestKey(from, 400.0f);
        string? finish = graph.NearestKey(to, 400.0f);

        if (start == null || finish == null)
        {
            KaiLog.Event(nameof(AddRoutes),
                $"'{label}': could not snap {(start == null ? "the start" : "the end")} onto the " +
                $"graph within 400 units, skipping",
                KaiLogLevel.Error);
            return;
        }

        var accepted = new List<HashSet<string>>();
        var used = new HashSet<string>();
        int made = 0;
        int rejectedForLength = 0;
        float shortest = 0.0f;

        for (int attempt = 0; attempt < RoutesPerPair * 3 && made < RoutesPerPair; attempt++)
        {
            var path = graph.FindPath(start, finish, used, ReusePenalty);

            if (path == null || path.Count < 2)
            {
                break;
            }

            var keys = new HashSet<string>(path);

            float candidateLength = 0.0f;

            for (int i = 1; i < path.Count; i++)
            {
                var a = graph.PointOf(path[i - 1]);
                var c = graph.PointOf(path[i]);
                candidateLength += a.DistanceXY(c.X, c.Y);
            }

            // The first accepted route is the shortest, so it sets the scale
            // everything else is judged against.
            if (shortest > 0.0f && candidateLength > shortest * MaxDetourRatio)
            {
                rejectedForLength++;

                // Still mark the ground used, or the next search finds the
                // same overlong path again.
                foreach (string k in keys)
                {
                    used.Add(k);
                }

                continue;
            }

            bool tooSimilar = false;

            foreach (var previous in accepted)
            {
                float shared = keys.Count(k => previous.Contains(k));
                float similarity = shared / MathF.Max(1.0f, MathF.Min(keys.Count, previous.Count));

                if (similarity > MaxSimilarity)
                {
                    tooSimilar = true;
                    break;
                }
            }

            if (tooSimilar)
            {
                // Push harder onto new ground and try again.
                foreach (string k in keys)
                {
                    used.Add(k);
                }

                continue;
            }

            made++;
            accepted.Add(keys);

            if (shortest <= 0.0f)
            {
                shortest = candidateLength;
            }

            foreach (string k in keys)
            {
                used.Add(k);
            }

            var waypoints = graph.Simplify(path, 25.0f);

            float length = 0.0f;

            for (int i = 1; i < waypoints.Count; i++)
            {
                length += waypoints[i].DistanceXY(waypoints[i - 1].X, waypoints[i - 1].Y);
            }

            book.Routes.Add(new KaiRoute
            {
                Name = $"{label}_{made:D2}",
                Kind = kind,
                Team = team,
                FromSite = fromSite,
                ToSite = toSite,
                Waypoints = waypoints,
                Length = length,
                Coverage = CountCoverage(waypoints, tactics),
                GeneratedUtc = stamp,
            });

            KaiLog.Event(nameof(AddRoutes),
                $"'{label}_{made:D2}': {waypoints.Count} waypoint(s) over {length:F0} units, " +
                $"{path.Count} graph nodes");
        }

        if (made == 0)
        {
            KaiLog.Event(nameof(AddRoutes), $"'{label}': no route found", KaiLogLevel.Error);
        }
        else if (rejectedForLength > 0)
        {
            KaiLog.Event(nameof(AddRoutes),
                $"'{label}': kept {made} route(s), rejected {rejectedForLength} for exceeding " +
                $"{MaxDetourRatio:F1}x the shortest ({shortest:F0} units). Fewer good routes " +
                $"beat more that never arrive.");
        }
    }

    // How many known duel angles sit near the route. A rough measure of how
    // contested it is, used to tell an informative path from a quiet flank
    // without needing a trace from every waypoint.
    private static int CountCoverage(List<KaiPoint> waypoints, KaiMapTactics tactics)
    {
        var seen = new HashSet<int>();

        for (int i = 0; i < tactics.PreAim.Count; i++)
        {
            var spot = tactics.PreAim[i];

            foreach (var point in waypoints)
            {
                if (spot.Trigger.DistanceXY(point.X, point.Y) < 400.0f)
                {
                    seen.Add(i);
                    break;
                }
            }
        }

        return seen.Count;
    }
}

public static class KaiRouteStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Dir(string dataDir)
    {
        return Path.Combine(dataDir, "patrol_routes");
    }

    private static string PathFor(string dataDir, string mapName)
    {
        return Path.Combine(Dir(dataDir), $"{mapName}.routes.json");
    }

    public static KaiRouteBook Load(string dataDir, string mapName)
    {
        var empty = new KaiRouteBook { MapName = mapName };

        try
        {
            string path = PathFor(dataDir, mapName);

            if (!File.Exists(path))
            {
                KaiLog.Event(nameof(Load),
                    $"no route book for '{mapName}' yet at '{path}'. Routes are generated once " +
                    $"from the breadcrumb graph and reused; until then bots move on native " +
                    $"pathing. This is expected on a new map.");
                return empty;
            }

            var book = JsonSerializer.Deserialize<KaiRouteBook>(File.ReadAllText(path), Options);

            if (book == null)
            {
                KaiLog.Event(nameof(Load), $"'{path}' deserialised to null", KaiLogLevel.Error);
                return empty;
            }

            book.MapName = mapName;

            KaiLog.Event(nameof(Load),
                $"loaded {book.Routes.Count} route(s) for '{mapName}', generated " +
                $"{book.GeneratedUtc}: " +
                $"{book.Routes.Count(r => r.Kind == KaiRouteKind.Execute)} execute, " +
                $"{book.Routes.Count(r => r.Kind == KaiRouteKind.Rotate)} rotate, " +
                $"{book.Routes.Count(r => r.Kind == KaiRouteKind.Patrol)} patrol");

            return book;
        }
        catch (Exception ex)
        {
            KaiLog.Event(nameof(Load), $"failed to load routes for '{mapName}': {ex.Message}",
                KaiLogLevel.Error);
            return empty;
        }
    }

    public static bool Save(string dataDir, KaiRouteBook book)
    {
        try
        {
            Directory.CreateDirectory(Dir(dataDir));

            string path = PathFor(dataDir, book.MapName);

            // Same protection as the tactics file: never replace a populated
            // route book with an empty one.
            if (book.Routes.Count == 0 && File.Exists(path))
            {
                KaiLog.Event(nameof(Save),
                    $"refused to overwrite '{path}' with an empty route book",
                    KaiLogLevel.Error);
                return false;
            }

            KaiTacticsLoader.Backup(path, "routes");

            File.WriteAllText(path, JsonSerializer.Serialize(book, Options));

            KaiLog.Event(nameof(Save),
                $"wrote {book.Routes.Count} route(s) to '{path}'");

            return true;
        }
        catch (Exception ex)
        {
            KaiLog.Event(nameof(Save), $"failed: {ex.Message}", KaiLogLevel.Error);
            return false;
        }
    }
}

// Walks a bot along a real path to an arbitrary destination.
//
// WHY THIS EXISTS
//
// KaiBotIntent has no "go here" field. The only movement lever is
// SteerTowards, which is applied by projecting a world direction onto the
// bot's own forward and left vectors and writing m_forwardSpeed and
// m_leftSpeed. That is a shove in a direction, not a route: it has no
// obstacle avoidance whatsoever, so the moment the straight line between the
// bot and its destination crosses a wall, the bot walks into that wall and
// stays there.
//
// Measured over three playtest sessions that produced 270 seconds of bots
// standing still mid-round, one of them frozen against the same wall for 48
// seconds of a 90 second round, with its distance-to-waypoint unchanged at
// 3337 units the whole time.
//
// The fix is the one ConvergeOnLooseBomb already used for the dropped bomb:
// A* over the breadcrumb graph, then steer at the next node on the path
// rather than at the destination. This wraps that pattern up so the T hold,
// the retake clearers and the route follower can all share it instead of each
// reinventing a straight line.
//
// FALLING BACK
//
// A destination with no path to it still gets steered at directly. That is no
// worse than the old behaviour and is sometimes right: the graph only knows
// where bots have actually walked, so a short hop across open ground it has
// never recorded is better attempted than refused. Those cases are counted
// and logged so the difference between "walked a path" and "shoved at it and
// hoped" is visible afterwards.
public sealed class KaiPathFollower
{
    // ------------------------------------------------------------------
    // Tunables
    // ------------------------------------------------------------------

    // Close enough to a path node to move on to the next one. Matches the
    // route follower's own arrival radius so the two agree about what
    // reaching a waypoint means.
    public float ArriveRadius = 90.0f;

    // Under this, do not bother pathing at all. A destination in the next
    // room is reached faster by walking at it than by snapping to the nearest
    // recorded node and walking a dogleg.
    public float DirectDistance = 500.0f;

    // How far the destination may move before the path is thrown away and
    // solved again. Small enough to follow a bomb that gets picked up and
    // carried, large enough that a jittering target does not cause a re-solve
    // every tick.
    public float RepathDistance = 400.0f;

    // No progress for this long towards the current node means something is
    // in the way that the graph did not know about.
    public float StallSeconds = 4.0f;

    // How much closer the bot has to get for it to count as progress. Loose
    // enough that standing still while turning is not mistaken for movement.
    public float StallImprovement = 32.0f;

    // True while the game is holding every player still, set each tick by the
    // plugin from the game rules' freeze period.
    //
    // The stall detector measures "not getting closer for StallSeconds" and a
    // frozen bot cannot get closer to anything, so without this flag every
    // path handed out at round start reported a stall exactly StallSeconds
    // into freezetime, re-solved or abandoned nodes that were never the
    // problem, and filled the log with failures that were really the game
    // rules working as intended.
    public bool MovementFrozen;

    // ------------------------------------------------------------------
    // Getting unstuck
    // ------------------------------------------------------------------
    //
    // A bot can end up somewhere the graph has never been, wedged against
    // geometry the recorder never walked. Measured in play: 32 of 38 pathing
    // failures were the bot's OWN position failing to snap onto the graph,
    // not the destination being unreachable.
    //
    // The escape is layered, cheapest and most certain first, and never hands
    // the bot back to the native AI. Retreating to a position the bot itself
    // stood on seconds ago needs no guessing and is guaranteed walkable,
    // because the bot walked out of it. Only when that fails does it try
    // other recorded nodes, and only when those fail does it start shoving
    // itself around blindly.

    // How long the bot tries to walk back to its own last good position
    // before deciding it cannot even do that.
    public float RetreatSeconds = 4.0f;

    // How near the anchor counts as recovered.
    public float RetreatArrive = 120.0f;

    // How many recorded nodes to try in turn once the retreat has failed.
    public int EscapeCandidates = 5;

    // How long each candidate gets.
    public float EscapeCandidateSeconds = 3.0f;

    // Hard ceiling on the whole escape, candidates and unstick together. A
    // bot spending half a round trying to free itself is no better off than
    // one standing still, and this is time the round is still running.
    public float EscapeMaxSeconds = 10.0f;

    // How long each blind unstick shove lasts. Short: the point is to change
    // the bot's footing, not to travel anywhere.
    public float UnstickStepSeconds = 0.7f;

    // How far the blind shoves aim. Far enough to register as a direction,
    // near enough that the bot is not sent across the map on a guess.
    public float UnstickReach = 220.0f;

    // ------------------------------------------------------------------
    // State
    // ------------------------------------------------------------------

    private sealed class KaiPathLeg
    {
        // Where this path was solved to. Compared against the requested
        // destination so a moved target triggers a re-solve.
        public KaiPoint Destination = new();

        // The nodes to walk, in order. Empty means no path was found and the
        // bot is being steered straight at the destination.
        public List<KaiPoint> Nodes = new();

        public int Cursor;

        // True when no path could be found, so the log can distinguish a bot
        // walking a solved path from one shoved at a destination.
        public bool Direct;

        // True once this leg has already been re-solved after a stall, so a
        // second stall skips the node rather than solving the same blocked
        // path again.
        public bool Resolved;

        // Progress tracking towards the current node.
        public float BestDistance = float.MaxValue;
        public float BestAt;
    }

    private readonly Dictionary<int, KaiPathLeg> _legs = new();

    // What stage of getting unstuck a bot is at.
    private enum KaiEscapeStage
    {
        // Not stuck.
        None = 0,

        // Walking back to the last position that snapped onto the graph.
        Retreating = 1,

        // Trying recorded nodes in turn.
        Candidates = 2,

        // Blind shoves: back the way it came, then either side, then a jump.
        Unsticking = 3,
    }

    private sealed class KaiEscape
    {
        public KaiEscapeStage Stage = KaiEscapeStage.None;

        // When the whole escape began, for the hard ceiling.
        public float StartedAt;

        // When the current stage or candidate began.
        public float StageAt;

        // Recorded nodes to try, and which one is being tried.
        public List<KaiPoint> Candidates = new();
        public int Candidate;

        // Which blind shove is being attempted.
        public int Step;

        // Where the bot was when the escape began, so the shoves can be aimed
        // relative to where it came from rather than at compass directions.
        public KaiPoint From = new();
        public KaiPoint CameFrom = new();
    }

    private readonly Dictionary<int, KaiEscape> _escapes = new();

    // slot -> the last position this bot occupied that snapped onto the graph,
    // and when. This is the single most useful thing to know about a stuck
    // bot: it is a walkable position, proven by the bot having walked out of
    // it, and it is close by.
    private readonly Dictionary<int, KaiPoint> _lastGood = new();
    private readonly Dictionary<int, float> _lastGoodAt = new();

    // Supplied by the plugin. Returns true when this point is close enough to
    // the graph to be pathed from, which is what "on the graph" means here.
    private readonly Func<KaiPoint, bool>? _onGraph;

    // Supplied by the plugin. Returns the nearest few recorded standing
    // positions to a point, nearest first.
    private readonly Func<KaiPoint, int, List<KaiPoint>>? _nearby;

    private int _escapesStarted;
    private int _escapesByRetreat;
    private int _escapesByCandidate;
    private int _escapesByUnstick;
    private int _escapesFailed;

    // Given two world points, return the walkable nodes between them, or null
    // when there is no path. Supplied by the plugin, which owns the graph.
    private readonly Func<KaiPoint, KaiPoint, List<KaiPoint>?> _solve;

    private int _solved;
    private int _direct;
    private int _stalls;

    public KaiPathFollower(
        Func<KaiPoint, KaiPoint, List<KaiPoint>?> solve,
        Func<KaiPoint, bool>? onGraph = null,
        Func<KaiPoint, int, List<KaiPoint>>? nearby = null)
    {
        _solve = solve;
        _onGraph = onGraph;
        _nearby = nearby;
    }

    public string Summary()
    {
        return $"legs={_legs.Count} solved={_solved} direct={_direct} stalls={_stalls} " +
               $"arrive={ArriveRadius:F0} directUnder={DirectDistance:F0} " +
               $"escaping={_escapes.Count} started={_escapesStarted} " +
               $"freedBy(retreat={_escapesByRetreat},node={_escapesByCandidate}," +
               $"unstick={_escapesByUnstick}) failed={_escapesFailed}";
    }

    // Steer this bot towards the destination along a real path.
    //
    // Returns true when a steer target was written. False means the bot is
    // already there and the caller should do whatever it does on arrival.
    public bool Steer(
        int slot,
        KaiPoint origin,
        KaiPoint destination,
        float now,
        KaiBotIntent intent,
        string label)
    {
        float straight = destination.DistanceXY(origin.X, origin.Y);

        if (straight <= ArriveRadius)
        {
            // Arrived. Drop the leg so the next request solves fresh rather
            // than resuming a path to somewhere the bot is already standing.
            Forget(slot);
            EndEscape(slot, "arrived", true);
            return false;
        }

        // Where is this bot standing, and is that anywhere the graph knows?
        //
        // Everything below depends on the answer. A bot on the graph can be
        // pathed normally. A bot off it cannot be pathed at all until it gets
        // back on, and that is what the escape does.
        bool onGraph = _onGraph == null || _onGraph(origin);

        if (onGraph)
        {
            _lastGood[slot] = origin;
            _lastGoodAt[slot] = now;

            if (_escapes.ContainsKey(slot))
            {
                EndEscape(slot, "back on the graph", true);
            }
        }
        else if (RunEscape(slot, origin, now, intent, label))
        {
            // The escape owns this bot's movement until it is back on ground
            // the graph recognises.
            return true;
        }

        var leg = LegFor(slot, origin, destination, now, straight, label);

        if (leg.Nodes.Count == 0)
        {
            // No path. Straight at it, which is what the old code did
            // everywhere and is still the only option available.
            intent.SteerTowards = destination;

            KaiLog.Throttled($"pathdirect:{slot}", nameof(Steer),
                $"slot {slot} has no walkable path for '{label}', steering directly at " +
                $"the destination {straight:F0} units away", 3.0f);

            return true;
        }

        AdvanceCursor(slot, leg, origin, label);

        if (leg.Cursor >= leg.Nodes.Count)
        {
            // Walked the whole path and the destination is still out of
            // arrival range, which happens when the nearest recorded node is
            // some way short of it. Close the last stretch directly.
            intent.SteerTowards = destination;

            KaiLog.Throttled($"pathtail:{slot}", nameof(Steer),
                $"slot {slot} has walked its whole path for '{label}' and is closing the " +
                $"last {straight:F0} units directly", 3.0f);

            return true;
        }

        var node = leg.Nodes[leg.Cursor];
        float toNode = node.DistanceXY(origin.X, origin.Y);

        CheckProgress(slot, leg, origin, destination, now, toNode, label);

        // The leg may have been re-solved or advanced by the stall check, so
        // read the node again rather than using the one captured above.
        if (leg.Cursor >= leg.Nodes.Count || leg.Nodes.Count == 0)
        {
            intent.SteerTowards = destination;
            return true;
        }

        intent.SteerTowards = leg.Nodes[leg.Cursor];

        KaiLog.Throttled($"path:{slot}", nameof(Steer),
            $"slot {slot} walking '{label}' node {leg.Cursor + 1}/{leg.Nodes.Count}, " +
            $"{toNode:F0} units to it and {straight:F0} to the destination", 2.0f);

        return true;
    }

    // Get the leg for this bot, solving a new one when there is none or when
    // the destination has moved far enough to invalidate it.
    private KaiPathLeg LegFor(
        int slot, KaiPoint origin, KaiPoint destination, float now, float straight, string label)
    {
        if (_legs.TryGetValue(slot, out var existing))
        {
            float moved = existing.Destination.DistanceXY(destination.X, destination.Y);

            if (moved <= RepathDistance)
            {
                return existing;
            }

            KaiLog.Event(nameof(LegFor),
                $"slot {slot} destination for '{label}' moved {moved:F0} units, " +
                $"solving a new path");
        }

        var leg = new KaiPathLeg
        {
            Destination = destination,
            BestAt = now,
            BestDistance = float.MaxValue,
        };

        if (straight > DirectDistance)
        {
            var nodes = _solve(origin, destination);

            if (nodes != null && nodes.Count > 0)
            {
                leg.Nodes = nodes;
                _solved++;

                KaiLog.Event(nameof(LegFor),
                    $"slot {slot} has a {nodes.Count} node path for '{label}' covering " +
                    $"{straight:F0} units");
            }
            else
            {
                leg.Direct = true;
                _direct++;

                KaiLog.Event(nameof(LegFor),
                    $"slot {slot} has no path for '{label}' over {straight:F0} units, " +
                    $"steering directly and hoping the ground is clear");
            }
        }
        else
        {
            leg.Direct = true;

            KaiLog.Throttled($"pathnear:{slot}", nameof(LegFor),
                $"slot {slot} is {straight:F0} units from its '{label}' destination, " +
                $"near enough to walk at it", 5.0f);
        }

        _legs[slot] = leg;

        return leg;
    }

    // Step past every node already reached. More than one can be cleared in a
    // tick when the path doubles back on itself near the bot.
    private void AdvanceCursor(int slot, KaiPathLeg leg, KaiPoint origin, string label)
    {
        while (leg.Cursor < leg.Nodes.Count)
        {
            var node = leg.Nodes[leg.Cursor];

            if (node.DistanceXY(origin.X, origin.Y) > ArriveRadius)
            {
                break;
            }

            leg.Cursor++;
            leg.Resolved = false;
            leg.BestDistance = float.MaxValue;

            KaiLog.Throttled($"pathnode:{slot}", nameof(AdvanceCursor),
                $"slot {slot} reached node {leg.Cursor} of {leg.Nodes.Count} on '{label}'",
                2.0f);
        }
    }

    // Notice a bot that is not getting any closer and do something about it.
    //
    // First stall on a node: solve the path again from where the bot actually
    // is, since it has moved since the original solve and a different route
    // may now be available. Second stall: give up on that node and skip it,
    // because a node that cannot be reached twice is one the graph is wrong
    // about.
    private void CheckProgress(
        int slot,
        KaiPathLeg leg,
        KaiPoint origin,
        KaiPoint destination,
        float now,
        float toNode,
        string label)
    {
        // A frozen bot is not stalled, it is obeying the rules. Push the
        // measurement forward so the stall clock starts counting from the
        // moment movement is actually possible.
        if (MovementFrozen)
        {
            leg.BestAt = now;

            KaiLog.Throttled($"pathfrozen:{slot}", nameof(CheckProgress),
                $"slot {slot} is in freezetime, stall clock for '{label}' held", 5.0f);

            return;
        }

        if (toNode < leg.BestDistance - StallImprovement)
        {
            leg.BestDistance = toNode;
            leg.BestAt = now;
            return;
        }

        if (now - leg.BestAt < StallSeconds)
        {
            return;
        }

        _stalls++;

        if (!leg.Resolved)
        {
            leg.Resolved = true;
            leg.BestAt = now;
            leg.BestDistance = float.MaxValue;

            var nodes = _solve(origin, destination);

            if (nodes != null && nodes.Count > 0)
            {
                leg.Nodes = nodes;
                leg.Cursor = 0;

                KaiLog.Event(nameof(CheckProgress),
                    $"slot {slot} made no progress on '{label}' for {StallSeconds:F0}s at " +
                    $"{toNode:F0} units from its node. Solved a new {nodes.Count} node path " +
                    $"from where it is actually standing.");

                return;
            }

            KaiLog.Event(nameof(CheckProgress),
                $"slot {slot} made no progress on '{label}' for {StallSeconds:F0}s and no " +
                $"new path could be found. Skipping the node instead.");
        }

        leg.Cursor++;
        leg.Resolved = false;
        leg.BestAt = now;
        leg.BestDistance = float.MaxValue;

        KaiLog.Event(nameof(CheckProgress),
            $"slot {slot} abandoned a node on '{label}' it could not reach, now on " +
            $"{leg.Cursor + 1} of {leg.Nodes.Count}");
    }

    public void Forget(int slot)
    {
        if (_legs.Remove(slot))
        {
            KaiLog.Throttled($"pathforget:{slot}", nameof(Forget),
                $"slot {slot} path dropped", 5.0f);
        }
    }

    public void Clear()
    {
        int had = _legs.Count;
        int escaping = _escapes.Count;

        _legs.Clear();
        _escapes.Clear();

        // Last good positions are deliberately NOT cleared here on a per-round
        // basis by the caller; they are cleared with everything else because a
        // position from the previous round is a position this bot's corpse was
        // standing in. ForgetAll is the one that keeps nothing.
        _lastGood.Clear();
        _lastGoodAt.Clear();

        KaiLog.Event(nameof(Clear),
            $"dropped {had} path leg(s) and {escaping} escape(s) in progress");
    }

    // ------------------------------------------------------------------
    // Getting a stuck bot back onto the graph
    // ------------------------------------------------------------------

    // Drive one tick of the escape. Returns true while the escape owns this
    // bot's movement.
    //
    // Never returns the bot to the native AI. The worst outcome here is that
    // every stage is exhausted, in which case the escape gives up for a few
    // seconds and the ordinary path follower has another go, which is still
    // the plugin driving rather than a handover.
    private bool RunEscape(
        int slot, KaiPoint origin, float now, KaiBotIntent intent, string label)
    {
        if (!_escapes.TryGetValue(slot, out var esc))
        {
            esc = BeginEscape(slot, origin, now, label);
        }

        if (now - esc.StartedAt > EscapeMaxSeconds)
        {
            _escapesFailed++;

            KaiLog.Event(nameof(RunEscape),
                $"slot {slot} has been trying to get back onto the graph for " +
                $"{now - esc.StartedAt:F1}s on '{label}' and has run out of time. " +
                $"Standing down; the follower will try again from wherever it now is.");

            EndEscape(slot, "out of time", false);

            return false;
        }

        if (esc.Stage == KaiEscapeStage.Retreating)
        {
            return Retreat(slot, esc, origin, now, intent, label);
        }

        if (esc.Stage == KaiEscapeStage.Candidates)
        {
            return TryCandidates(slot, esc, origin, now, intent, label);
        }

        return Unstick(slot, esc, origin, now, intent, label);
    }

    private KaiEscape BeginEscape(int slot, KaiPoint origin, float now, string label)
    {
        var esc = new KaiEscape
        {
            StartedAt = now,
            StageAt = now,
            From = origin,
        };

        // Where the bot came from, for aiming the blind shoves later. The last
        // good position is the best answer; failing that the bot's current
        // path node, failing that nothing and the shoves fall back to its own
        // facing.
        if (_lastGood.TryGetValue(slot, out var good))
        {
            esc.CameFrom = good;
            esc.Stage = KaiEscapeStage.Retreating;
        }
        else
        {
            esc.CameFrom = origin;
            esc.Stage = KaiEscapeStage.Candidates;
            esc.Candidates = NearbyNodes(origin);
        }

        _escapes[slot] = esc;
        _escapesStarted++;

        float age = 0.0f;

        if (_lastGoodAt.TryGetValue(slot, out float at))
        {
            age = now - at;
        }

        KaiLog.Event(nameof(BeginEscape),
            $"slot {slot} is off the graph on '{label}' at " +
            $"({origin.X:F0},{origin.Y:F0},{origin.Z:F0}) and cannot be pathed from there. " +
            (esc.Stage == KaiEscapeStage.Retreating
                ? $"Walking back to the position it stood on {age:F1}s ago, which is walkable " +
                  $"because it walked out of it."
                : $"No recorded position for this bot, so going straight to trying " +
                  $"{esc.Candidates.Count} nearby node(s)."));

        return esc;
    }

    // Stage one. Walk back to where the bot itself last stood on the graph.
    private bool Retreat(
        int slot, KaiEscape esc, KaiPoint origin, float now,
        KaiBotIntent intent, string label)
    {
        float back = esc.CameFrom.DistanceXY(origin.X, origin.Y);

        if (back <= RetreatArrive)
        {
            KaiLog.Event(nameof(Retreat),
                $"slot {slot} walked itself back onto the graph on '{label}', " +
                $"{now - esc.StartedAt:F1}s after getting stuck");

            EndEscape(slot, "retreated", true);

            return false;
        }

        if (now - esc.StageAt > RetreatSeconds)
        {
            esc.Stage = KaiEscapeStage.Candidates;
            esc.StageAt = now;
            esc.Candidates = NearbyNodes(origin);
            esc.Candidate = 0;

            KaiLog.Event(nameof(Retreat),
                $"slot {slot} could not get back to where it came from in " +
                $"{RetreatSeconds:F0}s, still {back:F0} units short. Trying " +
                $"{esc.Candidates.Count} other recorded position(s) instead.");

            return true;
        }

        intent.Anchored = false;
        intent.SteerTowards = esc.CameFrom;
        intent.SourceName = $"escape:retreat:{label}";

        KaiLog.Throttled($"escretreat:{slot}", nameof(Retreat),
            $"slot {slot} retreating to its last good position, {back:F0} units back", 2.0f);

        return true;
    }

    // Stage two. Try the nearest recorded nodes in turn.
    //
    // Several rather than one, because the nearest node to a wedged bot is
    // regularly on the far side of whatever wedged it.
    private bool TryCandidates(
        int slot, KaiEscape esc, KaiPoint origin, float now,
        KaiBotIntent intent, string label)
    {
        if (esc.Candidates.Count == 0 || esc.Candidate >= esc.Candidates.Count)
        {
            esc.Stage = KaiEscapeStage.Unsticking;
            esc.StageAt = now;
            esc.Step = 0;

            KaiLog.Event(nameof(TryCandidates),
                $"slot {slot} tried {esc.Candidate} recorded position(s) on '{label}' and " +
                $"reached none of them. It is wedged rather than merely lost, so it will " +
                $"now try to change its footing.");

            return true;
        }

        var target = esc.Candidates[esc.Candidate];
        float gap = target.DistanceXY(origin.X, origin.Y);

        if (gap <= RetreatArrive)
        {
            KaiLog.Event(nameof(TryCandidates),
                $"slot {slot} reached recorded position {esc.Candidate + 1} of " +
                $"{esc.Candidates.Count} and is back on the graph on '{label}'");

            EndEscape(slot, "reached a recorded node", true);

            return false;
        }

        if (now - esc.StageAt > EscapeCandidateSeconds)
        {
            esc.Candidate++;
            esc.StageAt = now;

            KaiLog.Event(nameof(TryCandidates),
                $"slot {slot} gave up on recorded position {esc.Candidate} after " +
                $"{EscapeCandidateSeconds:F0}s, still {gap:F0} units short. " +
                $"{esc.Candidates.Count - esc.Candidate} left to try.");

            return true;
        }

        intent.Anchored = false;
        intent.SteerTowards = target;
        intent.SourceName = $"escape:node{esc.Candidate}:{label}";

        KaiLog.Throttled($"esccand:{slot}", nameof(TryCandidates),
            $"slot {slot} heading for recorded position {esc.Candidate + 1} of " +
            $"{esc.Candidates.Count}, {gap:F0} units away", 2.0f);

        return true;
    }

    // Stage three. Blind shoves, in a fixed order rather than at random.
    //
    // Ordered by how likely each is to work. Backwards first: whatever the
    // bot is wedged against, it arrived from somewhere it fitted. Then either
    // side. Then a jump, which is the only one of the four that changes the
    // bot's height and is the answer when it is caught on a lip or a step.
    //
    // Deliberately not random. Random probing takes many attempts to converge,
    // and on screen it reads as a broken bot rather than a stuck one.
    private bool Unstick(
        int slot, KaiEscape esc, KaiPoint origin, float now,
        KaiBotIntent intent, string label)
    {
        int step = esc.Step;

        if (now - esc.StageAt > UnstickStepSeconds)
        {
            esc.Step++;
            esc.StageAt = now;
            step = esc.Step;

            if (step > 3)
            {
                // All four tried. Go round again from the candidate list,
                // which may now be reachable from the new footing.
                esc.Stage = KaiEscapeStage.Candidates;
                esc.StageAt = now;
                esc.Candidates = NearbyNodes(origin);
                esc.Candidate = 0;

                KaiLog.Event(nameof(Unstick),
                    $"slot {slot} has tried all four shoves on '{label}'. Its footing has " +
                    $"changed, so the recorded positions are worth another attempt.");

                return true;
            }
        }

        // The direction the bot came from, normalised. Used as the axis for
        // every shove so they are relative to its approach rather than to the
        // map's compass.
        float bx = esc.CameFrom.X - origin.X;
        float by = esc.CameFrom.Y - origin.Y;
        float len = MathF.Sqrt((bx * bx) + (by * by));

        if (len < 1.0f)
        {
            // No usable back direction, so pick an arbitrary but stable axis
            // rather than dividing by zero.
            bx = 1.0f;
            by = 0.0f;
            len = 1.0f;
        }

        bx /= len;
        by /= len;

        float dx;
        float dy;
        string what;

        if (step == 0)
        {
            dx = bx;
            dy = by;
            what = "backwards, the way it came";
        }
        else if (step == 1)
        {
            dx = -by;
            dy = bx;
            what = "sideways, left of its approach";
        }
        else if (step == 2)
        {
            dx = by;
            dy = -bx;
            what = "sideways, right of its approach";
        }
        else
        {
            dx = bx;
            dy = by;
            what = "backwards and jumping, in case it is caught on a lip";
        }

        intent.Anchored = false;
        intent.SteerTowards = new KaiPoint(
            origin.X + (dx * UnstickReach),
            origin.Y + (dy * UnstickReach),
            origin.Z);

        // The jump is requested on the last step only. The plugin's movement
        // hook suppresses native jumps while a bot is being steered, so this
        // is the flag that tells it this particular jump is wanted.
        intent.Jump = step == 3;
        intent.SourceName = $"escape:unstick{step}:{label}";

        KaiLog.Throttled($"escunstick:{slot}", nameof(Unstick),
            $"slot {slot} shoving {what} to free itself", 1.0f);

        return true;
    }

    // The nearest recorded standing positions to a point, or an empty list
    // when the plugin gave us no way to ask.
    private List<KaiPoint> NearbyNodes(KaiPoint origin)
    {
        if (_nearby == null)
        {
            return new List<KaiPoint>();
        }

        return _nearby(origin, EscapeCandidates);
    }

    private void EndEscape(int slot, string why, bool freed)
    {
        if (!_escapes.TryGetValue(slot, out var esc))
        {
            return;
        }

        _escapes.Remove(slot);

        // Credit whichever stage was running when the bot came free.
        //
        // This used to be counted inside the stages themselves, which was
        // wrong twice over. Steer notices the bot is back on the graph before
        // any stage gets to run its own arrival check, so the stage counters
        // almost never fired; and the unstick stage has no arrival check at
        // all, because getting back onto the graph IS its success condition,
        // so its counter could never be incremented by anything. The compiler
        // spotted the second half of that as an unassigned field.
        //
        // Counting here instead means every escape is attributed to the stage
        // that actually ended it, which is the number worth having: it says
        // whether retreating is doing the work, or whether bots are routinely
        // getting wedged badly enough to need shoving.
        if (freed)
        {
            if (esc.Stage == KaiEscapeStage.Retreating)
            {
                _escapesByRetreat++;
            }
            else if (esc.Stage == KaiEscapeStage.Candidates)
            {
                _escapesByCandidate++;
            }
            else if (esc.Stage == KaiEscapeStage.Unsticking)
            {
                _escapesByUnstick++;
            }
        }

        KaiLog.Event(nameof(EndEscape),
            $"slot {slot} escape ended in stage {esc.Stage} ({why}), freed={freed}, " +
            $"after {esc.Candidate} candidate(s) and {esc.Step} shove(s)");
    }
}
