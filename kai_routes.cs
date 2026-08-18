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
