// kai_solver.cs
//
// KAI ADDITION. Not part of CS2-Bot-Improver.
//
// Picks the best holding positions on a map, once, so bots do not have to
// work them out from scratch every round.
//
// WHY SOLVE AHEAD OF TIME
//
// Every position chooser in this plugin so far starts from where a bot happens
// to be standing when the bomb lands and searches outward. That makes the
// answer depend on the accident of where the bot was at that moment: a
// defender that spawned on the wrong side of the site gets the best position
// reachable from there, not the best position on the site.
//
// Solving ahead of time inverts it. Every standable position the breadcrumb
// recorder has seen is scored against every known duel angle, the best few are
// kept, and at round time a bot is simply handed one. The expensive part
// happens once and the runtime part becomes an assignment problem.
//
// WHY IT RUNS IN GAME AND INCREMENTALLY
//
// Scoring needs line of sight, and line of sight needs the map loaded, so this
// cannot be done offline against the JSON. And it cannot be done in one tick
// either: a few hundred candidate positions against thirty-odd angles is tens
// of thousands of traces. So the solver holds its state between ticks and
// spends a fixed budget of traces per tick until it finishes, reporting
// progress as it goes.

using System;
using System.Collections.Generic;
using System.Linq;

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace KaiBotTactics;

public enum KaiSolveStage
{
    Idle = 0,
    Scoring = 1,
    Selecting = 2,
    Done = 3,
}

public sealed class KaiSolver
{
    // ------------------------------------------------------------------
    // Tunables
    // ------------------------------------------------------------------

    // Traces per tick. The whole budget is spent every tick until the solve
    // finishes, so this trades solve time against frame time. Deliberately
    // modest: the solve only ever runs during freezetime or warmup, but a
    // stutter there is still a stutter.
    public int TracesPerTick = 400;

    // How far from a site centre a candidate can be and still count as
    // holding that site.
    public float SiteRadius = 1800.0f;

    // How close together two chosen posts may be.
    public float PostSpacing = 250.0f;

    // How many posts to keep per site, and for the CT early round. Five is a
    // full side; keeping a couple more gives the assignment something to fall
    // back on when the best are unreachable.
    public int PostsPerSite = 7;

    // A candidate must see at least this many angles to be worth keeping.
    public int MinCoverage = 1;

    // Weightings. Coverage dominates because it is the whole point; distance
    // from the bomb and having a wall behind are tie-breakers that decide
    // between positions covering the same angles.
    public float CoverageWeight = 10.0f;
    public float DistanceWeight = 0.004f;
    public float CoverWeight = 3.0f;

    // ------------------------------------------------------------------
    // State
    // ------------------------------------------------------------------

    public KaiSolveStage Stage { get; private set; } = KaiSolveStage.Idle;

    private sealed class Candidate
    {
        public KaiPoint Position = new();
        public int SiteIndex = -1;
        public int Coverage;
        public List<int> Covers = new();
        public float Distance;
        public float BackWall = -1.0f;
        public float Score;
        public float Bearing;
    }

    private readonly List<KaiPoint> _standable = new();
    private readonly List<Candidate> _scored = new();
    private int _cursor;
    private int _team;
    private int _siteIndex;
    private KaiPoint? _siteCentre;
    private KaiMapTactics? _map;
    private float _eyeHeight = 64.0f;

    private int _tracesThisRun;
    private float _startedAt;

    public string Summary()
    {
        return $"stage={Stage} candidates={_standable.Count} scored={_scored.Count} " +
               $"cursor={_cursor} traces={_tracesThisRun}";
    }

    // ------------------------------------------------------------------
    // Driving the solve
    // ------------------------------------------------------------------

    // Begin scoring one site for one team. siteIndex of -1 means the CT early
    // round, which is not tied to a plant position.
    public bool Begin(
        KaiMapTactics map,
        KaiBreadcrumbs crumbs,
        int team,
        int siteIndex,
        float eyeHeight)
    {
        if (!crumbs.IsUsable)
        {
            KaiLog.Event(nameof(Begin),
                "breadcrumb graph is not usable yet, so there is nothing to solve over. " +
                "Record more rounds first.",
                KaiLogLevel.Error);
            return false;
        }

        _map = map;
        _team = team;
        _siteIndex = siteIndex;
        _eyeHeight = eyeHeight;
        _cursor = 0;
        _tracesThisRun = 0;
        _startedAt = Server.CurrentTime;
        _scored.Clear();
        _standable.Clear();

        if (siteIndex >= 0)
        {
            if (siteIndex >= map.PlantSites.Count)
            {
                KaiLog.Event(nameof(Begin),
                    $"site {siteIndex} does not exist, only {map.PlantSites.Count} recorded",
                    KaiLogLevel.Error);
                return false;
            }

            _siteCentre = map.PlantSites[siteIndex];
        }
        else
        {
            _siteCentre = null;
        }

        // Candidate positions: every standable cell, filtered to the site when
        // solving for one.
        foreach (var node in crumbs.StandableNodes())
        {
            if (_siteCentre != null
                && node.DistanceXY(_siteCentre.X, _siteCentre.Y) > SiteRadius)
            {
                continue;
            }

            _standable.Add(node);
        }

        if (_standable.Count == 0)
        {
            KaiLog.Event(nameof(Begin),
                $"no standable positions within {SiteRadius:F0} units of the target",
                KaiLogLevel.Error);
            return false;
        }

        Stage = KaiSolveStage.Scoring;

        KaiLog.Event(nameof(Begin),
            $"solving team {team} site {siteIndex}: {_standable.Count} candidate positions " +
            $"against {map.PreAim.Count} known angles, {TracesPerTick} traces per tick");

        return true;
    }

    // One tick's worth of work. Returns true while still running.
    public bool Pump(float now)
    {
        if (Stage != KaiSolveStage.Scoring || _map == null)
        {
            return false;
        }

        int budget = TracesPerTick;

        while (_cursor < _standable.Count && budget > 0)
        {
            var position = _standable[_cursor];
            _cursor++;

            var eye = new Vector(position.X, position.Y, position.Z + _eyeHeight);

            var covers = new List<int>();

            for (int i = 0; i < _map.PreAim.Count && budget > 0; i++)
            {
                var spot = _map.PreAim[i];

                if (spot.Team != 0 && spot.Team != _team)
                {
                    continue;
                }

                // Cheap rejection before paying for a trace.
                if (spot.Trigger.DistanceXY(position.X, position.Y) > 1600.0f)
                {
                    continue;
                }

                var target = new Vector(
                    spot.Trigger.X, spot.Trigger.Y, spot.Trigger.Z + KaiHeights.Chest);

                budget--;
                _tracesThisRun++;

                if (KaiRayTraceBridge.CanSee(eye, target))
                {
                    covers.Add(i);
                }
            }

            if (covers.Count < MinCoverage)
            {
                continue;
            }

            float distance = 0.0f;
            float bearing = 0.0f;
            bool seesBomb = true;

            if (_siteCentre != null)
            {
                distance = position.DistanceXY(_siteCentre.X, _siteCentre.Y);
                bearing = KaiFormation.Bearing(
                    _siteCentre.X, _siteCentre.Y, position.X, position.Y);

                // A post that cannot see the bomb is not defending it.
                var bombTarget = new Vector(
                    _siteCentre.X, _siteCentre.Y, _siteCentre.Z + KaiHeights.BombWatch);

                budget--;
                _tracesThisRun++;
                seesBomb = KaiRayTraceBridge.CanSee(eye, bombTarget);
            }

            if (!seesBomb)
            {
                continue;
            }

            // How close the nearest wall behind is. Measured away from the
            // site, which is the direction the threat arrives from.
            float backWall = -1.0f;

            if (_siteCentre != null)
            {
                var back = new Vector(
                    position.X + (MathF.Cos(bearing * MathF.PI / 180.0f) * 300.0f),
                    position.Y + (MathF.Sin(bearing * MathF.PI / 180.0f) * 300.0f),
                    position.Z + _eyeHeight);

                budget--;
                _tracesThisRun++;
                backWall = KaiRayTraceBridge.TraceFraction(eye, back) * 300.0f;
            }

            _scored.Add(new Candidate
            {
                Position = position,
                SiteIndex = _siteIndex,
                Coverage = covers.Count,
                Covers = covers,
                Distance = distance,
                Bearing = bearing,
                BackWall = backWall,
                Score = ScoreOf(covers.Count, distance, backWall),
            });
        }

        if (_cursor >= _standable.Count)
        {
            Stage = KaiSolveStage.Selecting;

            KaiLog.Event(nameof(Pump),
                $"scoring finished in {now - _startedAt:F1}s and {_tracesThisRun} traces: " +
                $"{_scored.Count} of {_standable.Count} positions are worth considering");
        }

        return Stage == KaiSolveStage.Scoring;
    }

    // Coverage dominates. Distance is a mild pull outward, so that between two
    // positions covering the same angles the one that sees an attacker earlier
    // wins. Cover is a reward for having a wall close behind, capped so a bot
    // wedged into a corner with no view does not outscore a good position.
    private float ScoreOf(int coverage, float distance, float backWall)
    {
        float score = coverage * CoverageWeight;

        score += distance * DistanceWeight;

        if (backWall >= 0.0f && backWall < 120.0f)
        {
            score += CoverWeight * (1.0f - (backWall / 120.0f));
        }

        return score;
    }

    // Choose the final set: best first, then anything far enough from what has
    // already been chosen, so the posts are spread rather than clustered on
    // the single best piece of ground.
    public List<KaiSolvedPost> Select(string stamp)
    {
        var chosen = new List<KaiSolvedPost>();

        if (Stage != KaiSolveStage.Selecting)
        {
            return chosen;
        }

        var ordered = _scored.OrderByDescending(c => c.Score).ToList();
        var taken = new List<KaiPoint>();
        int index = 0;

        foreach (var candidate in ordered)
        {
            if (chosen.Count >= PostsPerSite)
            {
                break;
            }

            if (!KaiFormation.FarEnoughFrom(candidate.Position, taken, PostSpacing))
            {
                continue;
            }

            index++;
            taken.Add(candidate.Position);

            string label;

            if (_siteIndex >= 0)
            {
                label = $"t_post_s{_siteIndex}_{index:D2}";
            }
            else
            {
                label = $"ct_post_{index:D2}";
            }

            chosen.Add(new KaiSolvedPost
            {
                Name = label,
                SiteIndex = _siteIndex,
                Team = _team,
                Position = candidate.Position,
                Bearing = candidate.Bearing,
                Distance = candidate.Distance,
                Coverage = candidate.Coverage,
                Covers = candidate.Covers,
                BackWall = candidate.BackWall,
                Score = candidate.Score,
                SolvedUtc = stamp,
            });

            KaiLog.Event(nameof(Select),
                $"{label} at ({candidate.Position.X:F0},{candidate.Position.Y:F0}) " +
                $"covers {candidate.Coverage} angle(s), {candidate.Distance:F0} units out on " +
                $"bearing {candidate.Bearing:F0}, wall {candidate.BackWall:F0} behind, " +
                $"score {candidate.Score:F1}");
        }

        Stage = KaiSolveStage.Done;

        KaiLog.Event(nameof(Select),
            $"selected {chosen.Count} post(s) from {_scored.Count} scored positions");

        return chosen;
    }

    public void Reset()
    {
        Stage = KaiSolveStage.Idle;
        _scored.Clear();
        _standable.Clear();
        _cursor = 0;
        _tracesThisRun = 0;
    }
}
