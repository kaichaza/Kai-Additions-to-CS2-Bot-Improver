// kai_breadcrumbs.cs
//
// KAI ADDITION. Not part of CS2-Bot-Improver.
//
// Builds a navigation graph out of where bots have actually walked.
//
// WHY THIS EXISTS
//
// CounterStrikeSharp exposes a CCSNavArea wrapper but nothing that returns
// one, so the engine's own nav mesh is unreachable without signature scanning
// the TheNavMesh global and learning the CNavMesh layout. That is real reverse
// engineering work and there is no signature for it in ed0ard's gamedata.
//
// This sidesteps it entirely. Every position a bot occupies is walkable by
// definition, so recording those positions produces a set of known-good
// standing spots. The part that makes it a nav mesh rather than a point cloud
// is that two CONSECUTIVE positions from the same bot are a proven traversable
// link: something got from one to the other, under its own power, in a fiftieth
// of a second. Those links are graph edges, and a graph is what pathfinding
// needs.
//
// It also makes the plugin's crude steering safe. Steering has no obstacle
// avoidance, so it can only be trusted over short straight lines. Consecutive
// nodes on a path are one cell apart, and a straight line between two cells a
// bot has already walked between has nothing in it to walk into.
//
// WHY IT IS QUANTISED
//
// Recording raw samples at ten a second for ten bots is roughly a quarter of a
// million records per match, most of them describing ground already covered.
// Positions are snapped to a grid instead, and a cell is only written the first
// time anybody enters it. A bot re-walking known ground costs nothing, the file
// converges rather than growing without bound, and the cell transitions ARE the
// edges, so the graph falls out of the deduplication instead of having to be
// reconstructed afterwards.
//
// WHAT IT CANNOT DO
//
// Bots only walk where the native AI takes them, so this maps the parts of the
// map bots use, not the whole map. For ordering bot rotations that is exactly
// the right subset, but it is not a general nav mesh and should not be treated
// as one.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace KaiBotTactics;

// One cell of walkable ground.
public sealed class KaiCrumbNode
{
    // Grid coordinates, and the key they form.
    [JsonPropertyName("cx")] public int Cx { get; set; }
    [JsonPropertyName("cy")] public int Cy { get; set; }
    [JsonPropertyName("cz")] public int Cz { get; set; }

    // Running mean of the real positions seen in this cell, so a node sits
    // where bots actually stand rather than at an arbitrary grid corner.
    [JsonPropertyName("x")] public float X { get; set; }
    [JsonPropertyName("y")] public float Y { get; set; }
    [JsonPropertyName("z")] public float Z { get; set; }

    [JsonPropertyName("visits")] public int Visits { get; set; }

    // Was anybody ever standing here with their feet on something? A cell only
    // ever seen airborne is a place bots pass THROUGH mid-jump, not a place
    // they can stand, and pathing through it would drop a bot down a hole.
    [JsonPropertyName("ground")] public bool Ground { get; set; }

    // Seen standing, seen crouched. A cell only ever seen crouched is a vent
    // or a gap that cannot be walked upright.
    [JsonPropertyName("stand")] public bool Stand { get; set; }
    [JsonPropertyName("crouch")] public bool Crouch { get; set; }

    // Seen on a ladder. Traversable, but not on foot and not at walking speed.
    [JsonPropertyName("ladder")] public bool Ladder { get; set; }

    // Which teams have been here. Some ground is only reachable by one side
    // inside round time, which matters when planning a rotation.
    [JsonPropertyName("teams")] public int TeamMask { get; set; }

    // Eight-way histogram of which way people faced while standing here.
    // Movement direction is already implicit in the edges; this captures where
    // people LOOK from a cell, which is the useful half for choosing a hold.
    [JsonPropertyName("face")] public int[] FaceHistogram { get; set; } = new int[8];

    [JsonPropertyName("firstSeen")] public string FirstSeen { get; set; } = "";
    [JsonPropertyName("lastSeen")] public string LastSeen { get; set; } = "";

    public string Key()
    {
        return KaiBreadcrumbs.CellKey(Cx, Cy, Cz);
    }
}

// A proven traversable link between two cells.
public sealed class KaiCrumbEdge
{
    [JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonPropertyName("to")] public string To { get; set; } = "";

    // How many times the transition has been observed. A link crossed once
    // could be a glitch; one crossed fifty times is a route.
    [JsonPropertyName("count")] public int Count { get; set; }

    // The bot left the ground during this transition, so the link needs a jump
    // and should not be handed to a bot that is not allowed to make one.
    [JsonPropertyName("jump")] public bool Jump { get; set; }

    // Largest height change seen across this link. A big positive value is a
    // step up or a boost; a big negative one is a drop that may be one-way.
    [JsonPropertyName("dz")] public float MaxRise { get; set; }

    // Fastest observed crossing, in seconds. A usable proxy for edge cost.
    [JsonPropertyName("t")] public float FastestSeconds { get; set; } = 999.0f;
}

public sealed class KaiCrumbGraph
{
    [JsonPropertyName("mapName")] public string MapName { get; set; } = "";
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("cellSizeXY")] public float CellSizeXY { get; set; }
    [JsonPropertyName("cellSizeZ")] public float CellSizeZ { get; set; }
    [JsonPropertyName("lastWrittenUtc")] public string LastWrittenUtc { get; set; } = "";
    [JsonPropertyName("nodes")] public List<KaiCrumbNode> Nodes { get; set; } = new();
    [JsonPropertyName("edges")] public List<KaiCrumbEdge> Edges { get; set; } = new();
}

// Per-bot state between samples, needed to turn a stream of positions into
// edges.
internal sealed class KaiCrumbTrail
{
    public string LastCell = "";
    public float LastCellTime;
    public bool AirborneSinceLastCell;
    public long Sequence;
}

public sealed class KaiBreadcrumbs
{
    // ------------------------------------------------------------------
    // Tunables
    // ------------------------------------------------------------------

    public bool Enabled = true;

    // Horizontal cell size. A player is 32 units wide, so 48 gives roughly one
    // and a half player widths per cell: fine enough that a straight line
    // between neighbouring cells is safe to steer along, coarse enough that
    // the graph converges within a few matches instead of growing forever.
    public float CellSizeXY = 48.0f;

    // Vertical cell size, kept smaller so that stairs and the separate floors
    // of Nuke and Vertigo resolve as different nodes rather than merging into
    // one column of walkable space.
    public float CellSizeZ = 32.0f;

    // How often each bot is sampled. Twenty a second rather than ten: at a
    // sprint a bot crosses a 48 unit cell in under a fifth of a second, and
    // sampling too slowly skips cells, which puts false long edges into the
    // graph that steering would then try to walk in a straight line.
    public float SampleInterval = 0.05f;

    // Ignore transitions slower than this. A bot that stood still for ten
    // seconds and then stepped sideways did not demonstrate a route.
    public float MaxEdgeSeconds = 2.0f;

    // Autosave interval. The graph is worth losing less than the sample bank,
    // but a match is long enough that losing all of it to a crash would sting.
    public float AutosaveSeconds = 120.0f;

    // ------------------------------------------------------------------
    // Knowing when to stop
    //
    // A recorder with no stopping condition writes forever. Measured on a
    // finished de_mirage graph, the map was fully covered at about four
    // thousand nodes and the file was already 1.8 MB, half of which was edges
    // observed exactly once and a third of which was nodes never seen with
    // anybody's feet on the ground.
    //
    // So there are three separate limits, doing three different jobs. A
    // saturation test stops recording once the map has stopped producing new
    // ground. Hard caps stop a pathological case regardless. And a prune drops
    // the observations too thin to trust before every write, which is what
    // keeps the file from growing even while recording continues.
    // ------------------------------------------------------------------

    // Below this the graph is not worth consulting and consumers are told so
    // rather than being handed a handful of nodes that will snap every
    // destination to the same corner of the map.
    public int MinUsableNodes = 600;

    // A round adding fewer than this many new nodes has found nothing new.
    public int SaturationNewNodes = 20;

    // That many quiet rounds in a row and the map is considered covered.
    //
    // Was 3, which is one execute's worth of evidence. Five bots running the
    // same corridor three rounds running looks exactly like a fully walked
    // map, and on de_cache that is precisely what happened: the graph latched
    // at 969 nodes covering 14% of its own bounding box, against de_mirage's
    // 2541 nodes at 46%.
    public int SaturationRounds = 10;

    // Coverage floor. Saturation cannot latch below this many nodes however
    // quiet the rounds are, because a quiet round on a barely-walked map means
    // the bots are repeating themselves, not that the map is finished.
    //
    // This is a floor, not a target. Once the graph is past it the ordinary
    // quiet-round test applies again and recording stops as it always did, so
    // the file settles somewhere near this figure rather than growing without
    // limit. MaxNodes remains the hard ceiling above it.
    //
    // 2000 is chosen from the maps that already work: mirage 2541, dust2 1457,
    // inferno 1040, cache 969. It is above the three thin ones deliberately,
    // since dust2 and inferno are usable and cache is not, and the difference
    // is not in the node count alone but in how much of the map they cover.
    public int SaturationMinNodes = 2000;

    // Absolute stop. Even a map that never reaches the floor gives up after
    // this many recorded rounds, so a map with genuinely less walkable ground
    // than the floor assumes cannot record for ever.
    public int SaturationMaxRounds = 400;

    // Maps that are already known good and should keep the original strict
    // behaviour, latching on quiet rounds alone with no coverage floor.
    //
    // A deliberate band-aid. These three produce usable graphs today and there
    // is nothing to gain from recording more of them, so they opt out and
    // anything else, cache included and any map added later, gets the floor.
    // Emptying this set removes the special case entirely and applies the
    // floor everywhere, which is where this should end up once the thin maps
    // have caught up.
    public readonly HashSet<string> SaturationExemptMaps = new(StringComparer.OrdinalIgnoreCase)
    {
        "de_mirage",
        "de_dust2",
        "de_inferno",
    };

    // Absolute ceilings. Generous, because Nuke and Train are far larger than
    // Mirage, but finite.
    public int MaxNodes = 15000;
    public int MaxEdges = 40000;

    // Minimum evidence for an observation to survive a prune.
    public int MinNodeVisits = 2;
    public int MinEdgeCount = 2;

    // Do not prune a young graph: everything in it has been seen once so far,
    // and pruning at that stage would delete the map.
    public int PruneAboveNodes = 1500;

    public bool Saturated { get; private set; }

    private int _quietRounds;
    private int _nodesAtRoundStart;

    // Rounds this graph has actually recorded, across sessions within a map.
    // Only used to stop a map that never reaches the coverage floor from
    // recording for ever.
    private int _roundsRecorded;

    // ------------------------------------------------------------------
    // State
    // ------------------------------------------------------------------

    private readonly Dictionary<string, KaiCrumbNode> _nodes = new();
    private readonly Dictionary<string, KaiCrumbEdge> _edges = new();
    private readonly Dictionary<int, KaiCrumbTrail> _trails = new();

    private string _dataDir = "";
    private string _mapName = "";
    private float _nextSample;
    private float _nextAutosave;

    private int _nodesThisSession;
    private int _edgesThisSession;

    public int NodeCount => _nodes.Count;

    // New ground found since this session started. The number that actually
    // says whether the map is still being discovered: the total only ever
    // rises, but a session that adds nothing new is a session on a map that
    // has already been walked.
    public int NewNodesThisSession => _nodesThisSession;
    public int EdgeCount => _edges.Count;

    public static string CellKey(int cx, int cy, int cz)
    {
        return $"{cx}:{cy}:{cz}";
    }

    public string Summary()
    {
        return $"enabled={Enabled} saturated={Saturated} usable={IsUsable} floor={SaturationMinNodes} " +
               $"nodes={_nodes.Count}/{MaxNodes} edges={_edges.Count}/{MaxEdges} " +
               $"newThisSession={_nodesThisSession}n/{_edgesThisSession}e " +
               $"quietRounds={_quietRounds}/{SaturationRounds} " +
               $"cell={CellSizeXY:F0}x{CellSizeZ:F0} rate={1.0f / SampleInterval:F0}Hz";
    }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    public void OnMapStart(string dataDir, string mapName)
    {
        _dataDir = dataDir;
        _mapName = mapName;
        _trails.Clear();
        _nodesThisSession = 0;
        _edgesThisSession = 0;
        _nextSample = 0.0f;
        _nextAutosave = 0.0f;

        Load();

        // A new session gets a fresh hearing.
        //
        // Saturation is a claim about the map, but it is only ever evidenced
        // by what the bots did in one stretch of play. A map that latched
        // after three quiet rounds of the same execute has not been walked,
        // it has been sampled narrowly, and the next session with different
        // plays and different spawns will find ground the last one never
        // touched. Below the coverage floor the latch is therefore released
        // and has to be earned again.
        //
        // Above the floor, and for the exempt maps, it stays latched: those
        // graphs are finished and reopening them would only grow the file.
        if (Saturated
            && !SaturationExemptMaps.Contains(_mapName)
            && _nodes.Count < SaturationMinNodes)
        {
            Saturated = false;
            _quietRounds = 0;

            KaiLog.Event(nameof(OnMapStart),
                $"'{_mapName}' was saturated at {_nodes.Count} nodes, below the floor of " +
                $"{SaturationMinNodes}. Reopening recording for this session: the previous " +
                $"latch was three quiet rounds of one route, not a walked map.");
        }
    }

    public void OnRoundStart()
    {
        // Trails do not survive a round boundary. A bot that died at A and
        // respawned at B did not walk between them, and recording that as an
        // edge would put a link straight through the middle of the map into
        // the graph.
        _trails.Clear();

        // How much new ground did the round that just ended find? A map that
        // has stopped producing new nodes has been walked, and continuing to
        // record only grows the file.
        int found = _nodes.Count - _nodesAtRoundStart;

        if (_nodesAtRoundStart > 0 && !Saturated)
        {
            _roundsRecorded++;

            if (found <= SaturationNewNodes && _nodes.Count >= MinUsableNodes)
            {
                _quietRounds++;

                // Is this map allowed to latch yet?
                //
                // A quiet round means the bots found nothing new. On a map
                // that has been walked, that means it is finished. On a map
                // that has barely been walked, it means the bots have been
                // running the same route, which is a fact about the bots
                // rather than about the map.
                bool exempt = SaturationExemptMaps.Contains(_mapName);
                bool covered = _nodes.Count >= SaturationMinNodes;
                bool outOfPatience = _roundsRecorded >= SaturationMaxRounds;
                bool mayLatch = exempt || covered || outOfPatience;

                KaiLog.Event(nameof(OnRoundStart),
                    $"round added only {found} new node(s), {_quietRounds} quiet round(s) " +
                    $"of {SaturationRounds} needed before the map counts as covered " +
                    $"({_nodes.Count} nodes against a floor of {SaturationMinNodes}, " +
                    $"round {_roundsRecorded} of at most {SaturationMaxRounds}, " +
                    $"exempt={exempt})");

                if (_quietRounds >= SaturationRounds && !mayLatch)
                {
                    // Quiet, but nowhere near enough ground covered to
                    // believe it. Hold the counter at the threshold rather
                    // than letting it run away, so the moment the floor is
                    // reached the next quiet round latches immediately.
                    _quietRounds = SaturationRounds;

                    KaiLog.Throttled("satfloor", nameof(OnRoundStart),
                        $"'{_mapName}' has been quiet for {_quietRounds} round(s) but holds " +
                        $"only {_nodes.Count} nodes against a floor of {SaturationMinNodes}. " +
                        $"That reads as bots repeating a route rather than a finished map, " +
                        $"so recording continues.", 60.0f);
                }

                if (_quietRounds >= SaturationRounds && mayLatch)
                {
                    Saturated = true;

                    string why;

                    if (exempt)
                    {
                        why = "it is on the exempt list and keeps the original strict rule";
                    }
                    else if (covered)
                    {
                        why = $"it is past the coverage floor of {SaturationMinNodes} nodes";
                    }
                    else
                    {
                        why = $"it has recorded {_roundsRecorded} rounds without reaching the " +
                              $"floor, which is as long as it gets";
                    }

                    KaiLog.Event(nameof(OnRoundStart),
                        $"breadcrumb graph for '{_mapName}' is saturated at {_nodes.Count} nodes " +
                        $"and {_edges.Count} edges, because {why}. Recording stops here; the map " +
                        $"has stopped producing new ground and further sampling would only grow " +
                        $"the file. Use kai_crumbs resume to override.");

                    Save("saturated");
                }
            }
            else
            {
                // Still finding new ground. Any quiet rounds so far were a
                // lull rather than completion.
                if (_quietRounds > 0)
                {
                    KaiLog.Event(nameof(OnRoundStart),
                        $"round added {found} new node(s), resetting the saturation count");
                }

                _quietRounds = 0;
            }
        }

        _nodesAtRoundStart = _nodes.Count;
    }

    // ------------------------------------------------------------------
    // Recording
    // ------------------------------------------------------------------

    public void Tick(float now, bool roundLive)
    {
        if (!Enabled || string.IsNullOrEmpty(_mapName))
        {
            return;
        }

        // Nothing further to learn, so nothing further is written. Existing
        // data stays queryable; only the recording stops.
        if (Saturated)
        {
            return;
        }

        if (now >= _nextAutosave && _nextAutosave > 0.0f)
        {
            Save("autosave");
        }

        if (_nextAutosave <= 0.0f)
        {
            _nextAutosave = now + AutosaveSeconds;
        }

        if (now < _nextSample)
        {
            return;
        }

        _nextSample = now + SampleInterval;

        // Warmup and freezetime wandering is not route information. Bots mill
        // about in spawn during freezetime and would carve a dense blob of
        // nodes there that no rotation will ever use.
        if (!roundLive)
        {
            return;
        }

        foreach (var player in KaiPlayers.All())
        {
            if (player == null || !player.IsValid || !player.IsBot || player.IsHLTV)
            {
                continue;
            }

            if (!player.PawnIsAlive)
            {
                _trails.Remove(player.Slot);
                continue;
            }

            var pawn = player.PlayerPawn?.Value;

            if (pawn == null || !pawn.IsValid)
            {
                continue;
            }

            RecordSample(now, player, pawn);
        }
    }

    private void RecordSample(float now, CCSPlayerController player, CCSPlayerPawn pawn)
    {
        try
        {
            var origin = pawn.AbsOrigin;

            if (origin == null)
            {
                return;
            }

            // Anything not moving under its own feet is not describing
            // walkable ground: noclip, observer mode and being carried by a
            // moving platform all produce positions no bot can path to.
            var moveType = pawn.MoveType;

            if (moveType == MoveType_t.MOVETYPE_NOCLIP
                || moveType == MoveType_t.MOVETYPE_OBSERVER
                || moveType == MoveType_t.MOVETYPE_NONE)
            {
                return;
            }

            bool onLadder = moveType == MoveType_t.MOVETYPE_LADDER;
            bool grounded = KaiSpotLearner.IsGrounded(pawn);

            int cx = (int)MathF.Floor(origin.X / CellSizeXY);
            int cy = (int)MathF.Floor(origin.Y / CellSizeXY);
            int cz = (int)MathF.Floor(origin.Z / CellSizeZ);
            string cell = CellKey(cx, cy, cz);

            bool crouched = IsCrouched(pawn);
            float yaw = pawn.EyeAngles.Y;

            UpdateNode(cell, cx, cy, cz, origin, grounded, crouched, onLadder,
                (int)player.TeamNum, yaw);

            // Edges.
            if (!_trails.TryGetValue(player.Slot, out var trail))
            {
                trail = new KaiCrumbTrail
                {
                    LastCell = cell,
                    LastCellTime = now,
                    AirborneSinceLastCell = !grounded,
                };

                _trails[player.Slot] = trail;
                return;
            }

            if (!grounded)
            {
                // Remember that this bot left the ground somewhere between the
                // previous cell and the next one, so the resulting link is
                // marked as needing a jump.
                trail.AirborneSinceLastCell = true;
            }

            if (trail.LastCell == cell)
            {
                return;
            }

            float elapsed = now - trail.LastCellTime;

            if (elapsed > 0.0f && elapsed <= MaxEdgeSeconds && trail.LastCell.Length > 0)
            {
                float rise = 0.0f;

                if (_nodes.TryGetValue(trail.LastCell, out var fromNode))
                {
                    rise = origin.Z - fromNode.Z;
                }

                UpdateEdge(trail.LastCell, cell, trail.AirborneSinceLastCell, rise, elapsed);
            }

            trail.LastCell = cell;
            trail.LastCellTime = now;
            trail.AirborneSinceLastCell = !grounded;
            trail.Sequence++;
        }
        catch (Exception ex)
        {
            KaiLog.Throttled("crumb_err", nameof(RecordSample),
                $"sample failed: {ex.Message}", 10.0f, KaiLogLevel.Error);
        }
    }

    // m_flDuckAmount runs 0 to 1. Anything meaningfully off zero is a bot that
    // is at least partway down, which is what matters for whether a cell can
    // be walked upright.
    private static bool IsCrouched(CCSPlayerPawn pawn)
    {
        try
        {
            var movement = pawn.MovementServices;

            if (movement == null)
            {
                return false;
            }

            return new CCSPlayer_MovementServices(movement.Handle).DuckAmount > 0.5f;
        }
        catch
        {
            return false;
        }
    }

    private void UpdateNode(
        string cell, int cx, int cy, int cz, Vector origin,
        bool grounded, bool crouched, bool ladder, int team, float yaw)
    {
        if (!_nodes.TryGetValue(cell, out var node))
        {
            // Ceiling reached. Existing cells keep updating so the running
            // means and flags stay accurate, but no new ground is admitted.
            if (_nodes.Count >= MaxNodes)
            {
                KaiLog.Throttled("nodecap", nameof(UpdateNode),
                    $"node ceiling of {MaxNodes} reached for '{_mapName}', no longer adding new " +
                    $"cells. Raise kai_crumbs max or accept this as the map's coverage.",
                    30.0f, KaiLogLevel.Error);
                return;
            }

            node = new KaiCrumbNode
            {
                Cx = cx,
                Cy = cy,
                Cz = cz,
                X = origin.X,
                Y = origin.Y,
                Z = origin.Z,
                FirstSeen = KaiTime.NowUtc(),
            };

            _nodes[cell] = node;
            _nodesThisSession++;
        }
        else
        {
            // Running mean, so the stored position drifts towards where bots
            // actually stand inside the cell rather than sitting wherever the
            // first visitor happened to be.
            float n = node.Visits + 1;
            node.X += (origin.X - node.X) / n;
            node.Y += (origin.Y - node.Y) / n;
            node.Z += (origin.Z - node.Z) / n;
        }

        node.Visits++;
        node.LastSeen = KaiTime.NowUtc();

        if (grounded)
        {
            node.Ground = true;
        }

        if (ladder)
        {
            node.Ladder = true;
        }

        if (crouched)
        {
            node.Crouch = true;
        }
        else
        {
            node.Stand = true;
        }

        node.TeamMask |= 1 << team;

        int bucket = (int)MathF.Floor(((yaw + 180.0f) % 360.0f) / 45.0f);

        if (bucket >= 0 && bucket < 8)
        {
            node.FaceHistogram[bucket]++;
        }
    }

    private void UpdateEdge(string from, string to, bool jump, float rise, float seconds)
    {
        string key = from + ">" + to;

        if (!_edges.TryGetValue(key, out var edge))
        {
            if (_edges.Count >= MaxEdges)
            {
                KaiLog.Throttled("edgecap", nameof(UpdateEdge),
                    $"edge ceiling of {MaxEdges} reached for '{_mapName}', no longer adding new " +
                    $"links", 30.0f, KaiLogLevel.Error);
                return;
            }

            edge = new KaiCrumbEdge { From = from, To = to };
            _edges[key] = edge;
            _edgesThisSession++;
        }

        edge.Count++;

        if (jump)
        {
            edge.Jump = true;
        }

        if (MathF.Abs(rise) > MathF.Abs(edge.MaxRise))
        {
            edge.MaxRise = rise;
        }

        if (seconds < edge.FastestSeconds)
        {
            edge.FastestSeconds = seconds;
        }
    }

    // ------------------------------------------------------------------
    // Persistence
    // ------------------------------------------------------------------

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private string Dir()
    {
        return Path.Combine(_dataDir, "breadcrumbs");
    }

    private string GraphPath()
    {
        return Path.Combine(Dir(), $"{_mapName}.graph.json");
    }

    private void Load()
    {
        _nodes.Clear();
        _edges.Clear();

        try
        {
            string path = GraphPath();

            if (!File.Exists(path))
            {
                KaiLog.Event(nameof(Load),
                    $"no breadcrumb graph for '{_mapName}' yet, starting from nothing. " +
                    $"Position snapping falls back to learned spot data until {MinUsableNodes} " +
                    $"nodes have been recorded.");
                return;
            }

            var graph = JsonSerializer.Deserialize<KaiCrumbGraph>(File.ReadAllText(path), Options);

            if (graph == null)
            {
                KaiLog.Event(nameof(Load), $"'{path}' deserialised to null", KaiLogLevel.Error);
                return;
            }

            // A graph recorded at a different resolution cannot be merged with
            // one at this resolution; the cell keys mean different things.
            if (MathF.Abs(graph.CellSizeXY - CellSizeXY) > 0.1f
                || MathF.Abs(graph.CellSizeZ - CellSizeZ) > 0.1f)
            {
                KaiLog.Event(nameof(Load),
                    $"'{path}' was recorded at {graph.CellSizeXY:F0}x{graph.CellSizeZ:F0} but this " +
                    $"build uses {CellSizeXY:F0}x{CellSizeZ:F0}. Cell keys are not comparable, " +
                    $"so the old graph is ignored rather than merged. Delete it or restore the " +
                    $"old cell size to keep it.",
                    KaiLogLevel.Error);
                return;
            }

            foreach (var node in graph.Nodes)
            {
                if (node.FaceHistogram == null || node.FaceHistogram.Length != 8)
                {
                    node.FaceHistogram = new int[8];
                }

                _nodes[node.Key()] = node;
            }

            foreach (var edge in graph.Edges)
            {
                _edges[edge.From + ">" + edge.To] = edge;
            }

            KaiLog.Event(nameof(Load),
                $"loaded breadcrumb graph for '{_mapName}': {_nodes.Count} nodes, " +
                $"{_edges.Count} edges, last written {graph.LastWrittenUtc}");

            ReportUsability();
        }
        catch (Exception ex)
        {
            KaiLog.Event(nameof(Load), $"failed: {ex.Message}", KaiLogLevel.Error);
        }
    }

    // Drop observations too thin to trust, before every write.
    //
    // Three categories go, measured against a finished de_mirage graph where
    // they were 1.75 MB between them:
    //
    //   Nodes never seen with feet on the ground. A cell only ever observed
    //   mid-jump or mid-fall is not somewhere a bot can stand, and
    //   NearestStandable already refuses to return them, so they are pure
    //   file weight.
    //
    //   Nodes visited once. One sample through a cell is as likely to be a
    //   bot clipping a corner as a position worth knowing about.
    //
    //   Edges observed once, but ONLY where both endpoints still have another
    //   link. Dropping the last edge of a node would strand it, and a stranded
    //   node is worse than a thin one because pathing can reach it and never
    //   leave.
    //
    // Nothing is pruned below PruneAboveNodes, because everything in a young
    // graph has been seen once and pruning at that stage deletes the map.
    // Discard every node and edge outside the biggest connected island.
    private void KeepLargestComponent()
    {
        if (_nodes.Count == 0)
        {
            return;
        }

        var adjacency = new Dictionary<string, List<string>>();

        foreach (var edge in _edges.Values)
        {
            if (!adjacency.TryGetValue(edge.From, out var fromList))
            {
                fromList = new List<string>();
                adjacency[edge.From] = fromList;
            }

            fromList.Add(edge.To);

            if (!adjacency.TryGetValue(edge.To, out var toList))
            {
                toList = new List<string>();
                adjacency[edge.To] = toList;
            }

            toList.Add(edge.From);
        }

        var seen = new HashSet<string>();
        List<string>? biggest = null;

        foreach (string start in _nodes.Keys)
        {
            if (seen.Contains(start))
            {
                continue;
            }

            var component = new List<string>();
            var queue = new Queue<string>();

            queue.Enqueue(start);
            seen.Add(start);

            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                component.Add(current);

                if (!adjacency.TryGetValue(current, out var neighbours))
                {
                    continue;
                }

                foreach (string next in neighbours)
                {
                    if (seen.Add(next))
                    {
                        queue.Enqueue(next);
                    }
                }
            }

            if (biggest == null || component.Count > biggest.Count)
            {
                biggest = component;
            }
        }

        if (biggest == null || biggest.Count == _nodes.Count)
        {
            return;
        }

        var keep = new HashSet<string>(biggest);
        int dropped = _nodes.Count - keep.Count;

        var doomedNodes = new List<string>();

        foreach (string key in _nodes.Keys)
        {
            if (!keep.Contains(key))
            {
                doomedNodes.Add(key);
            }
        }

        foreach (string key in doomedNodes)
        {
            _nodes.Remove(key);
        }

        var doomedEdges = new List<string>();

        foreach (var kv in _edges)
        {
            if (!keep.Contains(kv.Value.From) || !keep.Contains(kv.Value.To))
            {
                doomedEdges.Add(kv.Key);
            }
        }

        foreach (string key in doomedEdges)
        {
            _edges.Remove(key);
        }

        KaiLog.Event(nameof(KeepLargestComponent),
            $"dropped {dropped} node(s) and {doomedEdges.Count} edge(s) stranded outside the main " +
            $"connected component, leaving {_nodes.Count} nodes that can all reach each other");
    }

    private void Prune()
    {
        if (_nodes.Count < PruneAboveNodes)
        {
            return;
        }

        int nodesBefore = _nodes.Count;
        int edgesBefore = _edges.Count;

        var doomed = new List<string>();

        foreach (var kv in _nodes)
        {
            var node = kv.Value;

            bool standable = node.Ground || node.Ladder;

            if (!standable || node.Visits < MinNodeVisits)
            {
                doomed.Add(kv.Key);
            }
        }

        foreach (string key in doomed)
        {
            _nodes.Remove(key);
        }

        // Any edge pointing at something that no longer exists goes with it.
        var orphaned = new List<string>();

        foreach (var kv in _edges)
        {
            if (!_nodes.ContainsKey(kv.Value.From) || !_nodes.ContainsKey(kv.Value.To))
            {
                orphaned.Add(kv.Key);
            }
        }

        foreach (string key in orphaned)
        {
            _edges.Remove(key);
        }

        // Count remaining links per node so connectivity can be preserved.
        var degree = new Dictionary<string, int>();

        foreach (var edge in _edges.Values)
        {
            degree[edge.From] = degree.GetValueOrDefault(edge.From) + 1;
            degree[edge.To] = degree.GetValueOrDefault(edge.To) + 1;
        }

        var thin = new List<string>();

        foreach (var kv in _edges)
        {
            var edge = kv.Value;

            if (edge.Count >= MinEdgeCount)
            {
                continue;
            }

            // Only drop it if neither end depends on it.
            if (degree.GetValueOrDefault(edge.From) > 1
                && degree.GetValueOrDefault(edge.To) > 1)
            {
                thin.Add(kv.Key);
                degree[edge.From] = degree[edge.From] - 1;
                degree[edge.To] = degree[edge.To] - 1;
            }
        }

        foreach (string key in thin)
        {
            _edges.Remove(key);
        }

        // Keep only the largest connected component.
        //
        // Measured on a real graph, pruning single-visit nodes cut bridges and
        // split one connected map into 220 pieces, stranding 488 nodes in 219
        // islands. An island is unreachable by definition, so it is pure file
        // weight, and worse than that: a bot snapped onto one can path
        // nowhere at all. Dropping everything outside the main component
        // guarantees that any two nodes in the graph can reach each other,
        // which is the property routing depends on.
        KeepLargestComponent();

        if (nodesBefore != _nodes.Count || edgesBefore != _edges.Count)
        {
            KaiLog.Event(nameof(Prune),
                $"pruned '{_mapName}': nodes {nodesBefore} -> {_nodes.Count}, " +
                $"edges {edgesBefore} -> {_edges.Count} " +
                $"({doomed.Count} unstandable or single-visit, {orphaned.Count} orphaned, " +
                $"{thin.Count} single-observation links that nothing depended on)");
        }
    }

    public bool Save(string caller)
    {
        if (string.IsNullOrEmpty(_dataDir) || string.IsNullOrEmpty(_mapName))
        {
            return false;
        }

        Prune();

        _nextAutosave = Server.CurrentTime + AutosaveSeconds;

        try
        {
            Directory.CreateDirectory(Dir());

            string path = GraphPath();
            KaiTacticsLoader.Backup(path, caller);

            var graph = new KaiCrumbGraph
            {
                MapName = _mapName,
                CellSizeXY = CellSizeXY,
                CellSizeZ = CellSizeZ,
                LastWrittenUtc = KaiTime.NowUtc(),
                Nodes = _nodes.Values.ToList(),
                Edges = _edges.Values.ToList(),
            };

            File.WriteAllText(path, JsonSerializer.Serialize(graph, Options));

            KaiLog.Event(nameof(Save),
                $"[{caller}] wrote breadcrumb graph '{path}': {graph.Nodes.Count} nodes, " +
                $"{graph.Edges.Count} edges ({_nodesThisSession} nodes and " +
                $"{_edgesThisSession} edges new this session)");

            return true;
        }
        catch (Exception ex)
        {
            KaiLog.Event(nameof(Save), $"[{caller}] failed: {ex.Message}", KaiLogLevel.Error);
            return false;
        }
    }

    // Start recording again after saturation, for a map that has changed or a
    // graph that was cut short.
    public void Resume()
    {
        Saturated = false;
        _roundsRecorded = 0;
        _quietRounds = 0;
        _nodesAtRoundStart = _nodes.Count;

        KaiLog.Event(nameof(Resume), $"recording resumed for '{_mapName}'");
    }

    public void Clear()
    {
        int n = _nodes.Count;
        int e = _edges.Count;

        _nodes.Clear();
        _edges.Clear();
        _trails.Clear();
        _nodesThisSession = 0;
        _edgesThisSession = 0;
        Saturated = false;
        _roundsRecorded = 0;
        _quietRounds = 0;
        _nodesAtRoundStart = 0;

        KaiLog.Event(nameof(Clear), $"discarded {n} nodes and {e} edges for '{_mapName}'");
    }

    // ------------------------------------------------------------------
    // Using the graph
    //
    // The recorder's first consumer. Every node is somewhere a bot physically
    // stood, so the node set is an authoritative answer to "can a bot be
    // here", which nothing else in this plugin had. Ring positions computed
    // by trigonometry alone were landing outside the map entirely, and a bot
    // sent to one walks towards it for the rest of the round.
    // ------------------------------------------------------------------

    // Nearest recorded standing position to an arbitrary point.
    //
    // Only nodes seen with somebody's feet on the ground are candidates: a
    // cell only ever observed mid-jump is somewhere bots pass through, not
    // somewhere they can stand.
    //
    // Searched outward from the target's own cell rather than by scanning
    // every node. The old version was a linear pass over the whole dictionary,
    // 2541 entries on Mirage, which was affordable only because it was called
    // rarely. It is now called on every failed snap, at several radii, for
    // several candidates, so it walks rings of cells instead and stops one
    // ring after it finds anything.
    public KaiPoint? NearestStandable(float x, float y, float z, float maxDistance)
    {
        var found = NearestStandableSet(x, y, z, maxDistance, 1);

        if (found.Count == 0)
        {
            return null;
        }

        return found[0];
    }

    // The nearest few standing positions, nearest first.
    //
    // Wanted because "the single nearest node" is a poor answer for a bot that
    // is wedged: the nearest node is regularly on the other side of whatever
    // it is wedged against. Handing back several lets the caller try them in
    // turn, or filter them by what it can actually see.
    public List<KaiPoint> NearestStandableSet(
        float x, float y, float z, float maxDistance, int want)
    {
        var result = new List<KaiPoint>();

        if (want <= 0 || _nodes.Count == 0)
        {
            return result;
        }

        int cx = (int)MathF.Floor(x / CellSizeXY);
        int cy = (int)MathF.Floor(y / CellSizeXY);
        int cz = (int)MathF.Floor(z / CellSizeZ);

        // How many rings out the radius could possibly reach. The vertical
        // term is weighted by four in the distance measure, so a ring in Z
        // buys half as much reach as one in XY.
        int maxRingXY = (int)MathF.Ceiling(maxDistance / CellSizeXY) + 1;
        int maxRingZ = (int)MathF.Ceiling((maxDistance / 2.0f) / CellSizeZ) + 1;

        var hits = new List<(float Dist, KaiCrumbNode Node)>();
        int ringWithFirstHit = -1;

        for (int ring = 0; ring <= maxRingXY; ring++)
        {
            // Stop one ring after the first hit. The extra ring matters
            // because a node in the next ring out can still be nearer in
            // straight-line terms than one at the corner of this one.
            if (ringWithFirstHit >= 0 && ring > ringWithFirstHit + 1)
            {
                break;
            }

            ScanRing(cx, cy, cz, ring, maxRingZ, x, y, z, maxDistance, hits);

            if (hits.Count > 0 && ringWithFirstHit < 0)
            {
                ringWithFirstHit = ring;
            }
        }

        hits.Sort((a, b) => a.Dist.CompareTo(b.Dist));

        foreach (var hit in hits)
        {
            if (result.Count >= want)
            {
                break;
            }

            result.Add(new KaiPoint(hit.Node.X, hit.Node.Y, hit.Node.Z));
        }

        return result;
    }

    // One shell of cells at Chebyshev distance `ring` from the centre cell,
    // across every Z layer within reach.
    private void ScanRing(
        int cx, int cy, int cz, int ring, int maxRingZ,
        float x, float y, float z, float maxDistance,
        List<(float, KaiCrumbNode)> hits)
    {
        for (int dz = -maxRingZ; dz <= maxRingZ; dz++)
        {
            for (int dx = -ring; dx <= ring; dx++)
            {
                for (int dy = -ring; dy <= ring; dy++)
                {
                    // Only the shell, not the solid block, or every ring
                    // would rescan everything inside it.
                    if (ring > 0
                        && Math.Abs(dx) != ring
                        && Math.Abs(dy) != ring)
                    {
                        continue;
                    }

                    string key = CellKey(cx + dx, cy + dy, cz + dz);

                    if (!_nodes.TryGetValue(key, out var node))
                    {
                        continue;
                    }

                    if (!node.Ground)
                    {
                        continue;
                    }

                    float ddx = node.X - x;
                    float ddy = node.Y - y;
                    float ddz = node.Z - z;

                    // Height is weighted heavily so a node on the floor above
                    // is not picked as the nearest match for one below.
                    float dist = MathF.Sqrt(
                        (ddx * ddx) + (ddy * ddy) + (ddz * ddz * 4.0f));

                    if (dist <= maxDistance)
                    {
                        hits.Add((dist, node));
                    }
                }
            }
        }
    }

    // The graph as traversable links, for the router.
    //
    // Exposed as edges rather than as the raw dictionaries so that the
    // recorder keeps ownership of its own storage and nothing about routing
    // has to know how a cell key is built.
    public readonly record struct GraphNodeRef(string Key, KaiPoint Position);

    public readonly record struct GraphEdgeRef(
        GraphNodeRef From, GraphNodeRef To, bool NeedsJump, int Count);

    public List<GraphEdgeRef> GraphEdges()
    {
        var result = new List<GraphEdgeRef>();

        foreach (var edge in _edges.Values)
        {
            if (!_nodes.TryGetValue(edge.From, out var from))
            {
                continue;
            }

            if (!_nodes.TryGetValue(edge.To, out var to))
            {
                continue;
            }

            // Only links between places a bot can actually stand. A cell only
            // ever seen mid-air is not a waypoint.
            if (!from.Ground && !from.Ladder)
            {
                continue;
            }

            if (!to.Ground && !to.Ladder)
            {
                continue;
            }

            result.Add(new GraphEdgeRef(
                new GraphNodeRef(edge.From, new KaiPoint(from.X, from.Y, from.Z)),
                new GraphNodeRef(edge.To, new KaiPoint(to.X, to.Y, to.Z)),
                edge.Jump,
                edge.Count));
        }

        return result;
    }

    // Every recorded standing position, for solvers that need to consider all
    // of them rather than just the nearest.
    // Every standable node with its traffic count.
    //
    // Added for the lurk finder, which needs to know not just WHERE a bot can
    // stand but how many bots have stood there. Traffic is the whole basis of
    // choosing a hiding place: somewhere nobody walks is somewhere nobody
    // clears, and somewhere everybody walks is what a hiding place should be
    // looking at.
    //
    // Returned as a parallel list rather than exposing the node type, so the
    // internal representation stays private.
    public List<(KaiPoint Position, int Visits)> StandableNodesWithTraffic()
    {
        var result = new List<(KaiPoint, int)>();

        foreach (var node in _nodes.Values)
        {
            if (!node.Ground)
            {
                continue;
            }

            result.Add((new KaiPoint(node.X, node.Y, node.Z), node.Visits));
        }

        return result;
    }

    public List<KaiPoint> StandableNodes()
    {
        var result = new List<KaiPoint>();

        foreach (var node in _nodes.Values)
        {
            if (!node.Ground)
            {
                continue;
            }

            result.Add(new KaiPoint(node.X, node.Y, node.Z));
        }

        return result;
    }

    // Is anywhere within reach of this point somewhere a bot has stood?
    public bool IsReachable(float x, float y, float z, float tolerance)
    {
        return NearestStandable(x, y, z, tolerance) != null;
    }

    // Is the graph worth consulting?
    //
    // A handful of nodes is worse than none: it would snap every destination
    // to whichever corner of the map happened to get recorded first, sending
    // bots confidently to the wrong place. Below the threshold consumers are
    // told to ignore it and fall back to whatever they did before.
    public bool IsUsable => _nodes.Count >= MinUsableNodes;

    public bool HasData => _nodes.Count > 0;

    // Reported once per map so an unusable graph is visible without having to
    // go looking for it.
    public void ReportUsability()
    {
        if (IsUsable)
        {
            KaiLog.Event(nameof(ReportUsability),
                $"breadcrumb graph for '{_mapName}' is usable: {_nodes.Count} nodes, " +
                $"{_edges.Count} edges, saturated={Saturated}");
            return;
        }

        KaiLog.Event(nameof(ReportUsability),
            $"breadcrumb graph for '{_mapName}' has only {_nodes.Count} of the " +
            $"{MinUsableNodes} nodes needed to be trusted. Position snapping will fall back to " +
            $"learned spot data until more rounds have been recorded. This is expected on a new " +
            $"map and is not an error.");
    }

    // ------------------------------------------------------------------
    // Coverage reporting
    //
    // The useful question is not how many nodes there are but whether the
    // graph has stopped growing. A session that adds almost nothing new is a
    // session where the map is already covered.
    // ------------------------------------------------------------------

    public string CoverageReport()
    {
        int grounded = _nodes.Values.Count(n => n.Ground);
        int standOnly = _nodes.Values.Count(n => n.Stand && !n.Crouch);
        int crouchOnly = _nodes.Values.Count(n => n.Crouch && !n.Stand);
        int ladder = _nodes.Values.Count(n => n.Ladder);
        int jumpEdges = _edges.Values.Count(e => e.Jump);

        float meanDegree = 0.0f;

        if (_nodes.Count > 0)
        {
            meanDegree = (float)_edges.Count / _nodes.Count;
        }

        return $"{_nodes.Count} nodes ({grounded} standable, {standOnly} upright only, " +
               $"{crouchOnly} crouch only, {ladder} ladder), {_edges.Count} edges " +
               $"({jumpEdges} need a jump), mean degree {meanDegree:F2}, " +
               $"{_nodesThisSession} new nodes this session";
    }
}
