// kai_spot_learner.cs
//
// KAI ADDITION. Not part of CS2-Bot-Improver.
// Schema version 2.
//
// THE PRINCIPLE, UNCHANGED
//
// A death is a measurement. One player stood at a specific reachable position
// and another stood within line of sight of it. That is the anchor and watch
// point a hold spot needs, measured rather than guessed.
//
// WHAT CHANGED IN V2, AND WHY
//
// 1. LEADER CLUSTERING REPLACES GRID BINNING.
//    V1 snapped every sample to a fixed grid cell and grouped identical keys.
//    Fixed cells have boundaries, and two samples 5 units apart on opposite
//    sides of a boundary never merged while two 95 units apart inside one cell
//    always did. Measured on a real 133-sample bank, that wasted 83% of the
//    data: three separate pre-aim spots 62 to 115 units apart, all facing
//    within 9 degrees of each other, were the same position fragmented three
//    ways. Greedy radius clustering against a running centroid has no
//    boundaries. On that same bank it lifted samples used from 23 to 54 and
//    merged those three spots into one cluster of ten.
//
// 2. HEIGHT IS JUDGED SEPARATELY FROM HORIZONTAL DISTANCE.
//    V1 binned Z on the same 96 unit grid as X and Y, which split one pre-aim
//    spot in two over a 31 unit step. Now the horizontal radius and the
//    vertical tolerance are independent. The vertical tolerance is set tight
//    enough to keep Nuke upper and lower, and Vertigo's levels, as distinct
//    positions, while still merging steps, crouches and boxes.
//
// 3. BOTH SIDES OF EVERY ENGAGEMENT ARE RECORDED.
//    V1 kept the attacker's position post-plant and the victim's position
//    otherwise, discarding the other half of every duel. A CT who kills a T
//    near the bomb is standing in a good retake position, which V1 threw away
//    entirely. Recording both sides roughly doubles the bank for the same
//    playtime.
//
// 4. AIRBORNE DEATHS ARE REJECTED.
//    A watch point recorded from a jumping player sits above where anyone will
//    actually be, and a bot aiming at a fixed point in space is then high at
//    every range. Palace on Mirage and the fall hazards on Vertigo generate
//    this repeatedly, so it clusters rather than being filtered by recurrence.
//
// 5. CLEAR ANGLES ARE SELECTED FOR ANGULAR SPREAD, NOT JUST FREQUENCY.
//    Three bots watching the same lane is not clearing a site. After
//    clustering, clear spots are chosen greedily so each new one covers a
//    direction the already-chosen ones do not.
//
// 6. CT CLEAR SAMPLES ON TOP OF THE BOMB ARE DROPPED.
//    On the measured bank, 7 of 32 clear samples were within 100 units of the
//    bomb. Those are CTs dying while defusing. As a clearing angle that is the
//    defuse position, and the plugin then suppresses USE on a bot standing
//    there, which is the worst of both.

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

// One measured half of one engagement.
public sealed class KaiSample
{
    // "postPlant", "ctClear" or "preAim".
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    // The team this spot is FOR. 2 Terrorist, 3 CounterTerrorist.
    [JsonPropertyName("team")]
    public int Team { get; set; }

    // Where the bot should stand.
    [JsonPropertyName("pos")]
    public KaiPoint Pos { get; set; } = new();

    // Where the bot should be looking.
    [JsonPropertyName("look")]
    public KaiPoint Look { get; set; } = new();

    // Distance from the planted bomb, or -1 pre-plant.
    [JsonPropertyName("bombDist")]
    public float BombDist { get; set; } = -1.0f;

    // When this was measured. Human readable so the bank can be skimmed.
    [JsonPropertyName("utc")]
    public string Utc { get; set; } = "";

    // Same instant as a unix second, for sorting and any future recency
    // weighting without reparsing the string.
    [JsonPropertyName("unix")]
    public long Unix { get; set; }

    // Round number this came from, straight off CCSGameRules.
    [JsonPropertyName("round")]
    public int Round { get; set; } = -1;

    // Shared by every sample generated from the same death, so counting can be
    // done per engagement rather than per sample.
    [JsonPropertyName("engagement")]
    public long Engagement { get; set; }

    // True if the sample came from the player who won the duel. Kept for
    // auditing; the clustering does not currently weight on it.
    [JsonPropertyName("won")]
    public bool Won { get; set; }
}

public sealed class KaiSampleBank
{
    [JsonPropertyName("mapName")]
    public string MapName { get; set; } = "";

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 2;

    [JsonPropertyName("lastWrittenUtc")]
    public string LastWrittenUtc { get; set; } = "";

    [JsonPropertyName("nextEngagement")]
    public long NextEngagement { get; set; } = 1;

    [JsonPropertyName("samples")]
    public List<KaiSample> Samples { get; set; } = new();
}

public sealed class KaiSpotLearner
{
    public const string GeneratorVersion = "0.3.0";

    // ------------------------------------------------------------------
    // Tunables
    // ------------------------------------------------------------------

    // Record new samples.
    public bool Enabled = true;

    // Horizontal clustering radius against the running cluster centroid.
    // Larger than the old 96 unit grid because a radius has no boundaries to
    // fall across, so it can afford to be generous.
    public float XyRadius = 110.0f;

    // Vertical tolerance, judged separately. This is the multi-level map
    // control: Nuke's floors are roughly 250 units apart and Vertigo's levels
    // more, so 70 keeps them distinct while merging a step, a crouch or a box.
    public float ZTolerance = 70.0f;

    // How far two samples may differ in facing and still be the same angle.
    public float YawTolerance = 35.0f;

    // How many samples a cluster needs before it is emitted.
    public int MinSamples = 2;

    // Upper bounds per category. These are sanity limits, not tuning knobs.
    //
    // They were originally set low on the theory that overlapping triggers
    // would make a bot's crosshair snap between competing angles. That was
    // wrong: the aim hook writes m_lookYaw and m_lookPitch and lets the native
    // spring interpolate towards them, so a bot moving between overlapping
    // triggers eases from one angle to the next rather than snapping. Real
    // players check a great many angles in quick succession, and denser data
    // reproduces that rather than spoiling it.
    //
    // The cost argument was also wrong. ApplyPreAim is a handful of float
    // comparisons per spot per bot per tick; a thousand spots against ten bots
    // is well under a million cheap operations a second, which is nothing.
    //
    // Overridable at runtime with kai_learn maxpre / maxpost / maxclear.
    public int MaxPostPlant = 64;
    public int MaxCtClear = 64;
    public int MaxPreAim = 1000;

    // Two clear spots whose watch points are closer than this cover the same
    // lane. Used to force angular spread across the clearing team.
    public float ClearWatchSeparation = 260.0f;

    // Clear samples closer than this to the bomb are the defuse position, not
    // a clearing angle.
    public float ClearBombFloor = 150.0f;

    // Chest height above origin. Aiming at an origin points at the floor.
    // Shared with the rest of the plugin so a stored watch point can be
    // lowered back to feet by exactly the amount it was raised.
    private const float ChestOffsetZ = KaiHeights.Chest;

    // Below this the direction between two players is meaningless as an angle.
    private const float MinPairDistance = 120.0f;

    // ------------------------------------------------------------------
    // State
    // ------------------------------------------------------------------

    private KaiSampleBank _bank = new();
    private string _dataDir = "";
    private string _mapName = "";

    // Counters for the last build, reported by kai_learn status.
    public string LastBuildUtc { get; private set; } = "never";

    public int SampleCount
    {
        get { return _bank.Samples.Count; }
    }

    // Per-kind counts, because the categories fill at wildly different rates
    // and the scarce ones are what decide whether the map is learned. Pre-plant
    // samples come from every death; post-plant ones need a round to reach a
    // plant and then produce a kill near the bomb, so they are the bottleneck
    // and the only ones worth measuring readiness against.
    public int PostPlantSamples => _bank.Samples.Count(x => x.Kind == "postPlant");

    public int ClearSamples => _bank.Samples.Count(x => x.Kind == "ctClear");

    public int PreAimSamples => _bank.Samples.Count(x => x.Kind == "preAim");

    // Distinct deaths, rather than samples. One death produces several
    // samples, so this is the closer proxy for how much play a bank
    // represents.
    public int EngagementCount => _bank.Samples.Select(x => x.Engagement).Distinct().Count();

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    public void OnMapStart(string dataDir, string mapName)
    {
        _dataDir = dataDir;
        _mapName = mapName;
        _bank = LoadBank(dataDir, mapName);

        KaiLog.Event(
            nameof(OnMapStart),
            $"learner active for '{mapName}', {_bank.Samples.Count} samples banked, " +
            $"last written {_bank.LastWrittenUtc}");
    }

    public string Summary()
    {
        int post = _bank.Samples.Count(s => s.Kind == "postPlant");
        int clear = _bank.Samples.Count(s => s.Kind == "ctClear");
        int pre = _bank.Samples.Count(s => s.Kind == "preAim");
        int engagements = _bank.Samples.Select(s => s.Engagement).Distinct().Count();

        string oldest = "none";
        string newest = "none";

        if (_bank.Samples.Count > 0)
        {
            oldest = _bank.Samples.OrderBy(s => s.Unix).First().Utc;
            newest = _bank.Samples.OrderByDescending(s => s.Unix).First().Utc;
        }

        return $"{_bank.Samples.Count} samples / {engagements} engagements on '{_mapName}' " +
               $"(postPlant={post}, ctClear={clear}, preAim={pre}) " +
               $"first={oldest} last={newest} lastBuild={LastBuildUtc}";
    }

    // ------------------------------------------------------------------
    // Recording
    // ------------------------------------------------------------------

    // Is this pawn standing on something? Two independent reads, because the
    // death that triggers this can clear the entity flag before the handler
    // runs, while m_bOnGroundLastTick still reflects the tick before.
    public static bool IsGrounded(CCSPlayerPawn? pawn)
    {
        if (pawn == null || !pawn.IsValid)
        {
            return false;
        }

        try
        {
            bool flagged = (pawn.Flags & (uint)PlayerFlags.FL_ONGROUND) != 0;
            bool lastTick = pawn.OnGroundLastTick;
            return flagged || lastTick;
        }
        catch (Exception ex)
        {
            KaiLog.Event(nameof(IsGrounded), $"could not read ground state: {ex.Message}",
                KaiLogLevel.Error);
            return false;
        }
    }

    // Record one death. Both halves of the engagement are kept.
    public void OnPlayerDeath(
        CCSPlayerController? victim,
        CCSPlayerController? attacker,
        bool bombPlanted,
        KaiPoint? bombPos,
        float maxBombDistance,
        int roundNumber)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            if (victim == null || !victim.IsValid || attacker == null || !attacker.IsValid)
            {
                return;
            }

            // Suicides, world damage and team kills teach nothing about angles.
            if (victim.Slot == attacker.Slot || victim.TeamNum == attacker.TeamNum)
            {
                return;
            }

            var vPawn = victim.PlayerPawn?.Value;
            var aPawn = attacker.PlayerPawn?.Value;
            var vOrigin = vPawn?.AbsOrigin;
            var aOrigin = aPawn?.AbsOrigin;

            if (vPawn == null || aPawn == null || vOrigin == null || aOrigin == null)
            {
                KaiLog.Event(nameof(OnPlayerDeath), "incomplete pawn data, discarded",
                    KaiLogLevel.Verbose);
                return;
            }

            // Airborne filter. A position recorded mid-jump is not somewhere a
            // bot can stand, and a watch point recorded from a jumping player
            // sits above where anyone will be.
            if (!IsGrounded(vPawn) || !IsGrounded(aPawn))
            {
                KaiLog.Event(
                    nameof(OnPlayerDeath),
                    $"airborne engagement discarded " +
                    $"(victimGrounded={IsGrounded(vPawn)}, attackerGrounded={IsGrounded(aPawn)})");
                return;
            }

            var vPos = new KaiPoint(vOrigin.X, vOrigin.Y, vOrigin.Z);
            var aPos = new KaiPoint(aOrigin.X, aOrigin.Y, aOrigin.Z);

            float pairDist = MathF.Sqrt(aPos.DistanceSqr(vPos.X, vPos.Y, vPos.Z));

            if (pairDist < MinPairDistance)
            {
                KaiLog.Event(
                    nameof(OnPlayerDeath),
                    $"pair distance {pairDist:F0} below {MinPairDistance:F0}, discarded",
                    KaiLogLevel.Verbose);
                return;
            }

            // Watch points sit at chest height, not at the feet.
            var vLook = new KaiPoint(vPos.X, vPos.Y, vPos.Z + ChestOffsetZ);
            var aLook = new KaiPoint(aPos.X, aPos.Y, aPos.Z + ChestOffsetZ);

            int attackerTeam = (int)attacker.TeamNum;
            int victimTeam = (int)victim.TeamNum;

            long engagement = _bank.NextEngagement;
            _bank.NextEngagement++;

            if (!bombPlanted || bombPos == null)
            {
                // Pre-plant. Both sides are valid pre-aim data for their own
                // team: the winner was looking at a productive angle, and the
                // loser wanted to already be looking back.
                Add(BuildSample("preAim", attackerTeam, aPos, vLook, -1.0f, engagement, true, roundNumber));
                Add(BuildSample("preAim", victimTeam, vPos, aLook, -1.0f, engagement, false, roundNumber));
                return;
            }

            float aBombDist = MathF.Sqrt(bombPos.DistanceSqr(aPos.X, aPos.Y, aPos.Z));
            float vBombDist = MathF.Sqrt(bombPos.DistanceSqr(vPos.X, vPos.Y, vPos.Z));

            // A T who won near the bomb was holding a good post-plant angle.
            if (attackerTeam == (int)CsTeam.Terrorist && aBombDist <= maxBombDistance)
            {
                Add(BuildSample("postPlant", (int)CsTeam.Terrorist, aPos, vLook,
                    aBombDist, engagement, true, roundNumber));
            }

            // A CT who won near the bomb was standing in a good retake
            // position. V1 discarded this entirely; it is the single best
            // source of clearing angles because it is a position that worked.
            if (attackerTeam == (int)CsTeam.CounterTerrorist
                && aBombDist <= maxBombDistance
                && aBombDist >= ClearBombFloor)
            {
                Add(BuildSample("ctClear", (int)CsTeam.CounterTerrorist, aPos, vLook,
                    aBombDist, engagement, true, roundNumber));
            }

            // A CT who died near the bomb was caught on an angle nobody was
            // covering. Standing there already looking that way flips it.
            // Excluded when they died on the bomb itself, because that is the
            // defuse position rather than a clearing angle.
            if (victimTeam == (int)CsTeam.CounterTerrorist
                && vBombDist <= maxBombDistance
                && vBombDist >= ClearBombFloor)
            {
                Add(BuildSample("ctClear", (int)CsTeam.CounterTerrorist, vPos, aLook,
                    vBombDist, engagement, false, roundNumber));
            }
        }
        catch (Exception ex)
        {
            KaiLog.Event(nameof(OnPlayerDeath), $"exception: {ex.Message}", KaiLogLevel.Error);
        }
    }

    private static KaiSample BuildSample(
        string kind, int team, KaiPoint pos, KaiPoint look,
        float bombDist, long engagement, bool won, int round)
    {
        return new KaiSample
        {
            Kind = kind,
            Team = team,
            Pos = pos,
            Look = look,
            BombDist = bombDist,
            Utc = KaiTime.NowUtc(),
            Unix = KaiTime.NowUnix(),
            Round = round,
            Engagement = engagement,
            Won = won,
        };
    }

    private void Add(KaiSample sample)
    {
        _bank.Samples.Add(sample);

        KaiLog.Event(
            nameof(Add),
            $"sample '{sample.Kind}' team={sample.Team} won={sample.Won} " +
            $"eng={sample.Engagement} round={sample.Round} " +
            $"pos=({sample.Pos.X:F0},{sample.Pos.Y:F0},{sample.Pos.Z:F0}) " +
            $"look=({sample.Look.X:F0},{sample.Look.Y:F0},{sample.Look.Z:F0}) " +
            $"bank={_bank.Samples.Count}");
    }

    // ------------------------------------------------------------------
    // Clustering
    // ------------------------------------------------------------------

    private sealed class KaiCluster
    {
        public readonly List<KaiSample> Members = new();

        public int Count
        {
            get { return Members.Count; }
        }

        // Distinct engagements, so one duel contributing two samples to the
        // same cluster cannot count twice.
        public int Engagements
        {
            get { return Members.Select(m => m.Engagement).Distinct().Count(); }
        }

        public int Team
        {
            get { return Members[0].Team; }
        }

        public KaiPoint MeanPos
        {
            get { return Mean(Members.Select(m => m.Pos)); }
        }

        public KaiPoint MeanLook
        {
            get { return Mean(Members.Select(m => m.Look)); }
        }

        public float MeanBombDist
        {
            get { return Members.Average(m => m.BombDist); }
        }

        public string NewestUtc
        {
            get { return Members.OrderByDescending(m => m.Unix).First().Utc; }
        }

        // Mean facing, averaged as unit vectors so that samples either side of
        // the 180 degree wrap do not average to the exact opposite direction.
        public float MeanYaw
        {
            get
            {
                float sx = 0.0f;
                float sy = 0.0f;

                foreach (var m in Members)
                {
                    float y = YawOf(m) * MathF.PI / 180.0f;
                    sx += MathF.Cos(y);
                    sy += MathF.Sin(y);
                }

                return MathF.Atan2(sy, sx) * 180.0f / MathF.PI;
            }
        }

        private static KaiPoint Mean(IEnumerable<KaiPoint> points)
        {
            var list = points.ToList();

            if (list.Count == 0)
            {
                return new KaiPoint();
            }

            return new KaiPoint(
                list.Average(p => p.X),
                list.Average(p => p.Y),
                list.Average(p => p.Z));
        }
    }

    private static float YawOf(KaiSample s)
    {
        return MathF.Atan2(s.Look.Y - s.Pos.Y, s.Look.X - s.Pos.X) * 180.0f / MathF.PI;
    }

    // Fold an angular difference into 0..180.
    private static float AngleGap(float a, float b)
    {
        float d = (a - b + 180.0f) % 360.0f;

        if (d < 0.0f)
        {
            d += 360.0f;
        }

        return MathF.Abs(d - 180.0f);
    }

    // Greedy leader clustering. Each sample joins the first existing cluster
    // whose running centroid it is close enough to in horizontal distance,
    // height and facing, otherwise it starts a new one.
    //
    // The point of this over grid binning is that the acceptance test is
    // relative to a moving centroid rather than to fixed cell boundaries, so
    // there is no arbitrary line that two nearby samples can fall either side
    // of. Newest samples are processed last so an established cluster is not
    // dragged around by a single recent outlier.
    private List<KaiCluster> BuildClusters(string kind)
    {
        var input = _bank.Samples
            .Where(s => s.Kind == kind)
            .OrderBy(s => s.Unix)
            .ToList();

        var clusters = new List<KaiCluster>();

        foreach (var s in input)
        {
            KaiCluster? target = null;

            foreach (var c in clusters)
            {
                if (c.Team != s.Team)
                {
                    continue;
                }

                var centre = c.MeanPos;

                if (centre.DistanceXY(s.Pos.X, s.Pos.Y) > XyRadius)
                {
                    continue;
                }

                // Height judged on its own. This is what keeps the floors of
                // Nuke and the levels of Vertigo apart.
                if (MathF.Abs(centre.Z - s.Pos.Z) > ZTolerance)
                {
                    continue;
                }

                if (AngleGap(YawOf(s), c.MeanYaw) > YawTolerance)
                {
                    continue;
                }

                target = c;
                break;
            }

            if (target == null)
            {
                target = new KaiCluster();
                clusters.Add(target);
            }

            target.Members.Add(s);
        }

        var kept = clusters
            .Where(c => c.Count >= MinSamples)
            .OrderByDescending(c => c.Engagements)
            .ThenByDescending(c => c.Count)
            .ToList();

        KaiLog.Event(
            nameof(BuildClusters),
            $"kind='{kind}': {input.Count} samples -> {clusters.Count} clusters, " +
            $"{kept.Count} passed minSamples={MinSamples}, " +
            $"{kept.Sum(c => c.Count)} samples used");

        return kept;
    }
    // Turn the sample bank into a tactics file.
    //
    // Anything derived from the samples is regenerated; anything learned by
    // other means is carried across. The distinction matters: bombsites come
    // from watching where planted_c4 ends up, which the sample bank knows
    // nothing about, so rebuilding without carrying them forward silently
    // erased every site the map had learned. That in turn emptied the
    // playbook, since plays are generated per site.
    public KaiMapTactics Build(KaiMapTactics? existing = null)
    {
        string stamp = KaiTime.NowUtc();

        var result = new KaiMapTactics
        {
            MapName = _mapName,
            SchemaVersion = 2,
            GeneratedUtc = stamp,
            GeneratorVersion = GeneratorVersion,
            SourceSamples = _bank.Samples.Count,
            SourceEngagements = _bank.Samples.Select(s => s.Engagement).Distinct().Count(),
        };

        result.PostPlant = EmitHolds("postPlant", MaxPostPlant, stamp, false);
        result.CtClear = EmitHolds("ctClear", MaxCtClear, stamp, true);
        result.PreAim = EmitPreAim(MaxPreAim, stamp);

        MarkStage(result);

        // Carried, not rebuilt. Bombsites are observed rather than derived,
        // and losing them costs the playbook, the solver and the router all at
        // once.
        if (existing != null)
        {
            result.PlantSites = existing.PlantSites;

            // Solved posts are deliberately NOT carried. They were scored
            // against the previous angle set, which has just been replaced, so
            // keeping them would leave bots holding positions chosen for
            // angles that no longer exist. The auto-solve regenerates them.
            KaiLog.Event(nameof(Build),
                $"carried {result.PlantSites.Count} recorded bombsite(s) across the rebuild; " +
                $"solved posts dropped and will be recomputed against the new angles");
        }

        LastBuildUtc = stamp;

        KaiLog.Event(
            nameof(Build),
            $"built '{_mapName}' at {stamp}: {result.PostPlant.Count} postPlant, " +
            $"{result.CtClear.Count} ctClear, {result.PreAim.Count} preAim, " +
            $"{result.PlantSites.Count} bombsite(s) " +
            $"from {result.SourceSamples} samples / {result.SourceEngagements} engagements");

        return result;
    }

    // Emit hold spots. When spreadAngles is set, spots are chosen so that each
    // new one covers a lane the already-chosen ones do not, which is the
    // difference between a team clearing a site and three bots staring down
    // the same corridor.
    private List<KaiHoldSpot> EmitHolds(string kind, int max, string stamp, bool spreadAngles)
    {
        var clusters = BuildClusters(kind);
        var chosen = new List<KaiCluster>();

        if (!spreadAngles)
        {
            chosen = clusters.Take(max).ToList();
        }
        else
        {
            var remaining = new List<KaiCluster>(clusters);

            while (chosen.Count < max && remaining.Count > 0)
            {
                KaiCluster? pick = null;

                // Prefer the strongest cluster covering a lane nobody has yet.
                foreach (var c in remaining)
                {
                    bool distinct = true;
                    var look = c.MeanLook;

                    foreach (var already in chosen)
                    {
                        var other = already.MeanLook;

                        if (look.DistanceXY(other.X, other.Y) < ClearWatchSeparation)
                        {
                            distinct = false;
                            break;
                        }
                    }

                    if (distinct)
                    {
                        pick = c;
                        break;
                    }
                }

                if (pick == null)
                {
                    // Every remaining lane duplicates one already covered.
                    // Stop rather than pad the list with redundant angles.
                    KaiLog.Event(
                        nameof(EmitHolds),
                        $"'{kind}': {remaining.Count} clusters left but all duplicate a covered lane, stopping");
                    break;
                }

                chosen.Add(pick);
                remaining.Remove(pick);
            }
        }

        var output = new List<KaiHoldSpot>();
        int index = 0;

        foreach (var c in chosen)
        {
            index++;

            var spot = new KaiHoldSpot
            {
                Name = $"{kind}_{index:D3}",
                Site = "",
                Team = c.Team,
                Anchor = c.MeanPos,
                Watch = c.MeanLook,
                Crouch = false,
                Stage = false,
                Priority = c.Engagements,
                Samples = c.Count,
                BombDist = c.MeanBombDist,
                Recorded = stamp,
            };

            output.Add(spot);

            KaiLog.Event(
                nameof(EmitHolds),
                $"'{spot.Name}' anchor=({spot.Anchor.X:F0},{spot.Anchor.Y:F0},{spot.Anchor.Z:F0}) " +
                $"watch=({spot.Watch.X:F0},{spot.Watch.Y:F0},{spot.Watch.Z:F0}) " +
                $"yaw={c.MeanYaw:F0} samples={c.Count} engagements={c.Engagements} " +
                $"bombDist={c.MeanBombDist:F0} newest={c.NewestUtc}");
        }

        return output;
    }

    private List<KaiPreAimSpot> EmitPreAim(int max, string stamp)
    {
        var clusters = BuildClusters("preAim");
        var output = new List<KaiPreAimSpot>();
        int index = 0;

        foreach (var c in clusters.Take(max))
        {
            index++;

            var spot = new KaiPreAimSpot
            {
                Name = $"preaim_{index:D3}",
                Team = c.Team,
                Trigger = c.MeanPos,
                // Radius follows the clustering radius plus a margin, so a bot
                // walking the same route reliably enters the trigger without
                // it bleeding into a neighbouring one.
                TriggerRadius = XyRadius + 50.0f,
                TriggerHeight = ZTolerance,
                Watch = c.MeanLook,
                FacingToleranceDeg = 100.0f,
                Priority = c.Engagements,
                Samples = c.Count,
                Recorded = stamp,
            };

            output.Add(spot);

            KaiLog.Event(
                nameof(EmitPreAim),
                $"'{spot.Name}' team={spot.Team} " +
                $"trigger=({spot.Trigger.X:F0},{spot.Trigger.Y:F0},{spot.Trigger.Z:F0}) " +
                $"r={spot.TriggerRadius:F0} h={spot.TriggerHeight:F0} " +
                $"watch=({spot.Watch.X:F0},{spot.Watch.Y:F0},{spot.Watch.Z:F0}) " +
                $"yaw={c.MeanYaw:F0} samples={c.Count}");
        }

        return output;
    }

    // The defuser needs one waiting position. Pick the clear spot furthest
    // from the bomb, on the reasoning that it has the most room behind it and
    // the longest sightline into the site.
    private void MarkStage(KaiMapTactics map)
    {
        if (map.CtClear.Count == 0)
        {
            KaiLog.Event(
                nameof(MarkStage),
                "no clear spots generated, the defuser will fall back to a bomb-relative standoff");
            return;
        }

        var stage = map.CtClear.OrderByDescending(s => s.BombDist).First();
        stage.Stage = true;

        KaiLog.Event(
            nameof(MarkStage),
            $"'{stage.Name}' marked as the CT defuser stage position " +
            $"at {stage.BombDist:F0} units from the bomb");
    }

    // ------------------------------------------------------------------
    // Persistence
    // ------------------------------------------------------------------

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static string BankDir(string dataDir)
    {
        return Path.Combine(dataDir, "learned");
    }

    private static string BankPath(string dataDir, string mapName)
    {
        return Path.Combine(BankDir(dataDir), $"{mapName}.samples.json");
    }

    private static KaiSampleBank LoadBank(string dataDir, string mapName)
    {
        var empty = new KaiSampleBank { MapName = mapName };

        try
        {
            string path = BankPath(dataDir, mapName);

            if (!File.Exists(path))
            {
                KaiLog.Event(nameof(LoadBank), $"no sample bank at '{path}', starting empty");
                return empty;
            }

            var loaded = JsonSerializer.Deserialize<KaiSampleBank>(File.ReadAllText(path), Options);

            if (loaded == null)
            {
                KaiLog.Event(nameof(LoadBank), $"'{path}' deserialised to null", KaiLogLevel.Error);
                return empty;
            }

            loaded.MapName = mapName;

            if (loaded.SchemaVersion < 2)
            {
                KaiLog.Event(
                    nameof(LoadBank),
                    $"'{path}' is a v{loaded.SchemaVersion} bank. v1 samples have no timestamps, " +
                    $"no engagement ids and were recorded without the airborne filter. " +
                    $"Discarding rather than mixing schemas. The file is untouched on disk.",
                    KaiLogLevel.Error);
                return empty;
            }

            // Make sure the id counter is ahead of anything already banked.
            if (loaded.Samples.Count > 0)
            {
                long maxEngagement = loaded.Samples.Max(s => s.Engagement);

                if (loaded.NextEngagement <= maxEngagement)
                {
                    loaded.NextEngagement = maxEngagement + 1;
                }
            }

            KaiLog.Event(
                nameof(LoadBank),
                $"loaded {loaded.Samples.Count} samples from '{path}', " +
                $"last written {loaded.LastWrittenUtc}, nextEngagement={loaded.NextEngagement}");

            return loaded;
        }
        catch (Exception ex)
        {
            KaiLog.Event(nameof(LoadBank), $"failed: {ex.Message}", KaiLogLevel.Error);
            return empty;
        }
    }

    // Write the raw samples, backing up the previous bank first. This is the
    // irreplaceable file: the tactics file can always be rebuilt from it, but
    // nothing can rebuild it.
    public bool SaveBank(string caller)
    {
        try
        {
            if (string.IsNullOrEmpty(_dataDir) || string.IsNullOrEmpty(_mapName))
            {
                return false;
            }

            Directory.CreateDirectory(BankDir(_dataDir));

            string path = BankPath(_dataDir, _mapName);

            KaiTacticsLoader.Backup(path, caller);

            _bank.MapName = _mapName;
            _bank.SchemaVersion = 2;
            _bank.LastWrittenUtc = KaiTime.NowUtc();

            File.WriteAllText(path, JsonSerializer.Serialize(_bank, Options));

            KaiLog.Event(
                nameof(SaveBank),
                $"[{caller}] wrote {_bank.Samples.Count} samples to '{path}' at {_bank.LastWrittenUtc}");

            return true;
        }
        catch (Exception ex)
        {
            KaiLog.Event(nameof(SaveBank), $"[{caller}] failed: {ex.Message}", KaiLogLevel.Error);
            return false;
        }
    }

    public void ClearBank()
    {
        int had = _bank.Samples.Count;
        _bank.Samples.Clear();
        _bank.NextEngagement = 1;

        KaiLog.Event(nameof(ClearBank), $"discarded {had} samples for '{_mapName}'");
    }
}
