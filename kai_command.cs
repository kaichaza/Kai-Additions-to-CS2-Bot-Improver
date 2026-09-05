// kai_command.cs
//
// KAI ADDITION. Not part of CS2-Bot-Improver.
//
// Who calls the play, and how the team arrives together.
//
// LEADERS
//
// Each side has one. It is a bot, never the human, and it stays the leader for
// the whole match rather than being recomputed every round, because a leader
// that changes every thirty seconds is not a leader.
//
// The leader matters in two concrete ways rather than being decoration. It is
// the anchor the rest of the side synchronises to when hitting a site, and it
// is never sent on decoy duty, because the bot calling the play should be with
// the play.
//
// READING THE CARRIER
//
// The site a T side hits is not chosen in the abstract: it is wherever the
// bomb is going, because a site take without the bomb is just a fight. When a
// bot carries it the play picks the site and the carrier is routed there. When
// the HUMAN carries it, the play has to follow instead of lead, so the carrier
// is watched and the site inferred from which one they are actually closing
// on. Bots then commit to the human's choice rather than executing somewhere
// else and leaving them alone with it.
//
// ARRIVING TOGETHER
//
// A site take that trickles in is five duels in sequence, each of which the
// defence wins. The point of hitting from several angles is that they happen
// at once, so the main group gathers at a staging distance and holds until
// enough of it is ready, then commits on the same tick. The decoys leave
// first, deliberately, so the noise is already in the wrong place before the
// real hit starts.

using System;
using System.Collections.Generic;
using System.Linq;

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace KaiBotTactics;

public enum KaiExecutePhase
{
    // Nothing running.
    Idle = 0,

    // Decoys are on their way to the fake site. The main group is still
    // moving up but will not commit yet.
    Peeling = 1,

    // Main group gathering at the staging distance.
    Staging = 2,

    // Everybody in at once.
    Committed = 3,
}

public sealed class KaiCommand
{
    // ------------------------------------------------------------------
    // Tunables
    // ------------------------------------------------------------------

    // How far from the site the main group gathers before committing. Far
    // enough not to be in the defenders' angles while waiting, close enough
    // that the final push is quick once it starts.
    public float StagingDistance = 900.0f;

    // A bot within this of its staging position counts as ready.
    public float StagingTolerance = 260.0f;

    // Fraction of the main group that must be ready before the push starts.
    // Not all of it: waiting for a straggler who is dead or stuck means never
    // going at all.
    public float ReadyFraction = 0.7f;

    // Longest the group will wait for stragglers, measured from the moment
    // the FIRST bot arrives at the staging distance, not from the start of
    // the phase.
    //
    // The distinction is the whole feature. Executes begin at round start,
    // during freezetime, and the routes to a site are five to ten thousand
    // units long, so measured from phase start this timer expired while the
    // whole group was still mid-map: every commit in two full playtest
    // sessions read "0 of N in position after 12.0s (staging timed out)".
    // Measured from first arrival it does what the name says: the early
    // arrivals hold, the stragglers get this long to join them, and then
    // the group goes.
    public float MaxStagingSeconds = 12.0f;

    // Absolute ceiling on the whole approach, measured from the start of the
    // staging phase. If NOBODY has arrived by this point the group is stuck,
    // dead, or fighting its way in, and holding the phase open any longer
    // just delays whatever the round has become. Generous on purpose: the
    // longest recorded exec route is under ten thousand units, which a
    // running bot covers well inside this.
    public float MaxApproachSeconds = 45.0f;

    // How long the decoys get to be heard before the main group is allowed to
    // commit, so the fake lands first rather than at the same time.
    public float PeelSeconds = 5.0f;

    // ------------------------------------------------------------------
    // State
    // ------------------------------------------------------------------

    // team -> the slot leading it this match.
    private readonly Dictionary<int, int> _leaders = new();

    public KaiExecutePhase Phase { get; private set; } = KaiExecutePhase.Idle;

    private float _phaseSince;
    private int _targetSite = -1;
    private readonly HashSet<int> _mainGroup = new();

    // When the first main-group bot reached the staging distance, or a
    // negative value while nobody has. The staging timeout counts from here,
    // because a clock that starts before anyone can possibly have arrived is
    // a clock that only ever times out.
    private float _firstReadyAt = -1.0f;

    // Which site the human carrier appears to be committing to, and how
    // confident that read is.
    private int _readCarrierSite = -1;
    private float _readCarrierConfidence;

    public int TargetSite => _targetSite;
    public int LeaderOf(int team) => _leaders.GetValueOrDefault(team, -1);
    public bool IsLeader(int slot, int team) => _leaders.GetValueOrDefault(team, -1) == slot;

    public bool IsInMainGroup(int slot) => _mainGroup.Contains(slot);

    public string Summary()
    {
        return $"phase={Phase} site={_targetSite} main={_mainGroup.Count} " +
               $"leaders=T{_leaders.GetValueOrDefault((int)CsTeam.Terrorist, -1)}/" +
               $"CT{_leaders.GetValueOrDefault((int)CsTeam.CounterTerrorist, -1)} " +
               $"carrierRead=site{_readCarrierSite}@{_readCarrierConfidence:F2}";
    }

    // ------------------------------------------------------------------
    // Leaders
    // ------------------------------------------------------------------

    // Choose a leader for each side, keeping the existing one while it lives.
    //
    // Stability matters more than optimality here: a side whose leader changes
    // mid-round has two bots' worth of plan and no continuity between them.
    // The replacement is only chosen when the incumbent is gone.
    public void EnsureLeaders()
    {
        foreach (int team in new[] { (int)CsTeam.Terrorist, (int)CsTeam.CounterTerrorist })
        {
            int current = _leaders.GetValueOrDefault(team, -1);

            if (current >= 0 && IsEligibleLeader(current, team))
            {
                continue;
            }

            int replacement = -1;

            foreach (var p in KaiPlayers.All())
            {
                if (p == null || !p.IsValid || !p.IsBot || p.IsHLTV)
                {
                    continue;
                }

                if ((int)p.TeamNum != team)
                {
                    continue;
                }

                // Lowest slot, purely so the choice is deterministic and the
                // same bot keeps the job across rounds rather than shuffling.
                if (replacement < 0 || p.Slot < replacement)
                {
                    replacement = p.Slot;
                }
            }

            if (replacement < 0)
            {
                continue;
            }

            _leaders[team] = replacement;

            var leader = Utilities.GetPlayerFromSlot(replacement);

            KaiLog.Event(nameof(EnsureLeaders),
                $"team {team} is led by slot {replacement} " +
                $"('{leader?.PlayerName ?? "unknown"}')" +
                (current >= 0 ? $", replacing slot {current}" : ""));
        }
    }

    // A leader must be a living bot on the right side. The human is never
    // leader: the whole point is that the bots organise around whatever the
    // human does, not that they take orders from a slot that might go idle.
    private static bool IsEligibleLeader(int slot, int team)
    {
        var p = Utilities.GetPlayerFromSlot(slot);

        return p != null
               && p.IsValid
               && p.IsBot
               && p.PawnIsAlive
               && (int)p.TeamNum == team;
    }

    // ------------------------------------------------------------------
    // Reading the carrier
    // ------------------------------------------------------------------

    // Work out which site the bomb is actually going to.
    //
    // With a bot carrier the answer is whatever the play said, because the
    // carrier is being routed there. With a human carrier there is no plan to
    // read, so the site is inferred from movement: whichever one they are
    // closing on, with confidence rising the nearer they get and the longer
    // the answer holds steady.
    //
    // Confidence matters because acting on a weak read is worse than acting on
    // none. A human still in spawn is equidistant from everything.
    public int ReadCarrierSite(
        int carrierSlot,
        bool carrierIsHuman,
        List<KaiPoint> sites,
        int plannedSite)
    {
        if (sites.Count == 0)
        {
            return plannedSite;
        }

        if (carrierSlot < 0)
        {
            // Nobody has it. The bomb is on the floor somewhere and the play
            // stands until somebody picks it up.
            _readCarrierSite = -1;
            _readCarrierConfidence = 0.0f;
            return plannedSite;
        }

        if (!carrierIsHuman)
        {
            _readCarrierSite = plannedSite;
            _readCarrierConfidence = 1.0f;
            return plannedSite;
        }

        var carrier = Utilities.GetPlayerFromSlot(carrierSlot);
        var origin = carrier?.PlayerPawn?.Value?.AbsOrigin;

        if (origin == null)
        {
            return plannedSite;
        }

        int nearest = -1;
        float nearestDist = float.MaxValue;
        float secondDist = float.MaxValue;

        for (int i = 0; i < sites.Count; i++)
        {
            float d = sites[i].DistanceXY(origin.X, origin.Y);

            if (d < nearestDist)
            {
                secondDist = nearestDist;
                nearestDist = d;
                nearest = i;
            }
            else if (d < secondDist)
            {
                secondDist = d;
            }
        }

        if (nearest < 0)
        {
            return plannedSite;
        }

        // Confidence is how much nearer the closest site is than the next
        // one. Standing between two sites reads as nothing; being twice as
        // close to one as the other is a commitment.
        float separation = secondDist <= 0.0f ? 0.0f : 1.0f - (nearestDist / secondDist);

        // Hysteresis: a read has to beat the standing one to replace it, so
        // the team does not thrash while the human wanders.
        if (nearest != _readCarrierSite && separation < _readCarrierConfidence + 0.15f)
        {
            return _readCarrierSite >= 0 ? _readCarrierSite : plannedSite;
        }

        if (nearest != _readCarrierSite)
        {
            KaiLog.Event(nameof(ReadCarrierSite),
                $"the human carrier looks committed to site {nearest}: {nearestDist:F0} units " +
                $"away against {secondDist:F0} to the next, confidence {separation:F2}. " +
                $"The team follows the bomb.");
        }

        _readCarrierSite = nearest;
        _readCarrierConfidence = separation;

        return nearest;
    }

    // ------------------------------------------------------------------
    // Synchronised execute
    // ------------------------------------------------------------------

    public void BeginExecute(int site, IEnumerable<int> mainGroup, bool hasDecoys)
    {
        _targetSite = site;
        _mainGroup.Clear();
        _firstReadyAt = -1.0f;

        foreach (int slot in mainGroup)
        {
            _mainGroup.Add(slot);
        }

        // An execute with nobody in it is not an execute. This happens at map
        // start and map end, when plays are called before any bot has spawned
        // or after they have gone, and it used to open a staging phase that
        // logged "0 of 0 in position" for twelve seconds and then committed
        // nothing. Idle says what is true instead.
        if (_mainGroup.Count == 0)
        {
            Phase = KaiExecutePhase.Idle;

            KaiLog.Event(nameof(BeginExecute),
                $"execute on site {site} has an empty main group, staying idle. " +
                $"Nothing to synchronise.");

            return;
        }

        Phase = hasDecoys ? KaiExecutePhase.Peeling : KaiExecutePhase.Staging;
        _phaseSince = Server.CurrentTime;

        KaiLog.Event(nameof(BeginExecute),
            $"execute on site {site}: {_mainGroup.Count} in the main group, " +
            (hasDecoys
                ? $"decoys peeling first for {PeelSeconds:F0}s before the main group may commit"
                : "no decoys, going straight to staging"));
    }

    // Advance the execute. Returns true once the group has committed.
    public bool Update(float now, KaiPoint site, int decoysActive)
    {
        if (Phase == KaiExecutePhase.Idle)
        {
            return false;
        }

        if (Phase == KaiExecutePhase.Committed)
        {
            return true;
        }

        float elapsed = now - _phaseSince;

        if (Phase == KaiExecutePhase.Peeling)
        {
            // Wait for the fake to be heard. Either the decoys have had their
            // time, or they are already dead, in which case whatever they were
            // going to achieve has happened.
            if (elapsed < PeelSeconds && decoysActive > 0)
            {
                return false;
            }

            Phase = KaiExecutePhase.Staging;
            _phaseSince = now;

            KaiLog.Event(nameof(Update),
                $"decoys have had {elapsed:F1}s at the fake site, the main group may now stage");

            return false;
        }

        // Staging. Count who is in position.
        int ready = 0;
        int alive = 0;

        foreach (int slot in _mainGroup)
        {
            var p = Utilities.GetPlayerFromSlot(slot);

            if (p == null || !p.IsValid || !p.PawnIsAlive)
            {
                continue;
            }

            alive++;

            var origin = p.PlayerPawn?.Value?.AbsOrigin;

            if (origin == null)
            {
                continue;
            }

            // Ready means close enough to the site to be part of the hit,
            // measured against the site itself.
            //
            // This used to compare against a per-bot staging coordinate that
            // nothing could actually reach, so readiness was always zero and
            // every execute timed out after twelve seconds having staged
            // nobody at all. The bot has walked a real route to get here; how
            // near the site it now stands is the only thing that matters.
            if (site.DistanceXY(origin.X, origin.Y) <= StagingDistance + StagingTolerance)
            {
                ready++;
            }
        }

        // The whole group is dead. There is nothing left to synchronise and
        // nothing a commit would release, so the execute is simply over.
        if (alive == 0)
        {
            Phase = KaiExecutePhase.Idle;

            KaiLog.Event(nameof(Update),
                $"execute on site {_targetSite} abandoned: the whole main group is dead " +
                $"after {elapsed:F1}s of staging");

            return false;
        }

        // The straggler clock starts when the first bot arrives, not when the
        // phase opens. Before that the group is still travelling and the only
        // limit that applies is the approach ceiling.
        if (ready > 0 && _firstReadyAt < 0.0f)
        {
            _firstReadyAt = now;

            KaiLog.Event(nameof(Update),
                $"first of the main group is staged on site {_targetSite} after {elapsed:F1}s " +
                $"of approach, stragglers have {MaxStagingSeconds:F0}s to join before the " +
                $"group goes without them");
        }

        bool enough = ready >= MathF.Ceiling(alive * ReadyFraction);

        // Two distinct timeouts, one of which is always armed. Stragglers are
        // measured from the first arrival; a group where nobody has arrived at
        // all is measured against the approach ceiling instead.
        bool stragglersOutOfTime = _firstReadyAt >= 0.0f
                                   && now - _firstReadyAt >= MaxStagingSeconds;
        bool approachOutOfTime = _firstReadyAt < 0.0f && elapsed >= MaxApproachSeconds;
        bool waitedLongEnough = stragglersOutOfTime || approachOutOfTime;

        if (!enough && !waitedLongEnough)
        {
            KaiLog.Throttled("staging", nameof(Update),
                $"staging for site {_targetSite}: {ready} of {alive} in position, " +
                $"{elapsed:F1}s since staging began" +
                (_firstReadyAt >= 0.0f
                    ? $", {now - _firstReadyAt:F1}s since the first arrival"
                    : ", nobody staged yet"), 2.0f);

            return false;
        }

        Phase = KaiExecutePhase.Committed;
        _phaseSince = now;

        KaiLog.Event(nameof(Update),
            $"COMMIT on site {_targetSite}: {ready} of {alive} in position after {elapsed:F1}s" +
            (stragglersOutOfTime && !enough ? " (stragglers out of time, going anyway)" : "") +
            (approachOutOfTime ? " (approach ceiling reached with nobody staged, going anyway)" : "") +
            ". The whole group goes at once.");

        return true;
    }

    public void Reset()
    {
        Phase = KaiExecutePhase.Idle;
        _targetSite = -1;
        _mainGroup.Clear();
        _firstReadyAt = -1.0f;
        _readCarrierSite = -1;
        _readCarrierConfidence = 0.0f;
    }

    // Leaders survive a round but not a match.
    public void OnMatchEnd()
    {
        _leaders.Clear();
        Reset();
    }
}
