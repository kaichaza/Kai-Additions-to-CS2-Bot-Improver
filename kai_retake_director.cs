// kai_retake_director.cs
//
// KAI ADDITION. Not part of CS2-Bot-Improver.
//
// The CT side of the post-plant problem.
//
// WHY THIS EXISTS
//
// ed0ard's BotAI plugin patches four things that together make CT bots beeline
// to the bomb and defuse without clearing anything:
//
//   OnBombPlanted_AllBotsLearnSite   NOPs the team == TERRORIST gate in
//                                    CSGameState::OnBombPlanted, so every CT
//                                    learns the plant position instantly
//   BombBeep_CT_GlobalHearRange      removes the 1500 unit hearing check
//   DefuseBomb_SkipIsVisibleCheck    NOPs the IsVisible gate in
//                                    MoveToState::OnUpdate, so a CT enters
//                                    DefuseBombState from 72 units with no
//                                    line of sight at all
//   CT_Defuse_EngageAndInvestigate   and its two siblings, rewriting
//                                    SetDisposition(SELF_DEFENSE) to
//                                    ENGAGE_AND_INVESTIGATE
//
// Unpatching means forking his plugin and redoing it every release. This takes
// the opposite approach: let the native AI path them in as it does now, then
// take over on arrival.
//
// THE THREE PHASES
//
//   Clear   one bot is designated defuser and held back. Everyone else holds
//           an authored clearing angle. USE suppressed for all of them, so
//           nobody touches the bomb.
//
//   Bait    the defuser is released and walks to the bomb, but chops USE into
//           taps instead of holding it. Each tap fires bomb_begindefuse then
//           bomb_abortdefuse, which is a genuine fake defuse: the Ts hear it
//           and have to reveal themselves. Clearers stay on their angles,
//           covering the reveal.
//
//   Commit  the defuser is fully released and defuses normally.
//
// WHAT CHANGED IN V2
//
//   Clearers now keep holding their angles through Commit. In v1 the whole
//   directive released the moment the phase advanced, so the exact moment the
//   defuse started, every covering bot went back to wandering. Holding through
//   the defuse is the entire point of clearing first, and it is why the
//   defuser is the only role that Commit affects.
//
//   Clear spots are now selected by the learner for angular spread, so a team
//   of clearers covers several lanes rather than three bots on one corridor.
//
// TWO MECHANISMS, DIFFERENT CONFIDENCE LEVELS
//
// Forcing USE for the fake taps goes through BotController's InjectUsercmd,
// the same call BotState uses for the knife inspect animation. That path is
// proven in his code.
//
// Suppressing USE writes to CBot::m_buttonFlags in a post hook on
// CCSBot::Update. That is not proven, which is why the standoff pin exists as
// well: a bot pinned 190 units from the bomb cannot defuse regardless of what
// its buttons say. Run kai_log 2 and watch the buttonFlags readback to see
// which mechanism is doing the work on your build.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace KaiBotTactics;

public enum KaiRetakePhase
{
    // No bomb down, or CT retake handling is switched off.
    Idle = 0,

    // Sweeping the known lurk spots. Nobody may touch the bomb.
    Inspect = 1,

    // Defuser is at the bomb faking, clearers still holding.
    Bait = 2,

    // Defuser is defusing. Clearers are STILL holding.
    Commit = 3,

    // Gathering at a ring short of the site before anyone goes in, knives
    // out on the run when the legs are long. The site is hit by everyone on
    // the same tick, because a retake that trickles in is a queue of fair
    // duels for the lurker, and the whole point of arriving together is that
    // the first T seen gets shot at from several angles at once.
    //
    // Appended after Commit so the existing values keep their numbers in
    // old logs; the phase ORDER at runtime is Rally, Inspect, Bait, Commit.
    Rally = 4,
}

public sealed class KaiRetakeDirector
{
    // Walks bots to positions along the breadcrumb graph. Handed in by the
    // plugin, which owns the graph, so the director and the route follower
    // share one cache rather than each keeping its own.
    //
    // Null is tolerated everywhere it is used. Without it the director falls
    // back to the straight line steering it used before, which is what it did
    // for its whole life up to now, so a missing follower degrades rather
    // than breaks.
    public KaiPathFollower? Pathing;

    // Asks the plugin where the tracked human is, if the handicap is on,
    // fresh, and the asking team is entitled to know. Wired to
    // TrackedTargetFor at map start, so the three gate checks live in one
    // place and this class never has to know whether god mode is running.
    // Null, or a null return, simply means no bias is applied and every
    // covering bot behaves exactly as before.
    public Func<int, KaiPoint?>? TrackedEnemy;

    // Asks the arsenal whether this bot is already knifing for its own
    // reasons (a dry bot's weapon state belongs to the arsenal, and the
    // rally must not fight it over which slot is selected). Wired at map
    // start; null means assume no.
    public Func<int, bool>? IsKnifing;

    // Puts a bot's best gun back in its hands when the rally run is over.
    // Wired to the plugin's RestoreBestWeapon, which knows the inventory;
    // when null the director falls back to a plain slot1.
    public Action<CCSPlayerController>? RestoreWeapon;

    // ------------------------------------------------------------------
    // Tunables. Public so kai_retake can change them mid-session.
    // ------------------------------------------------------------------

    public bool Enabled = true;

    // How long the inspection sweep lasts before the bait phase starts.
    public float InspectSeconds = 12.0f;

    // How long the crosshair rests on each angle while scanning. Short enough
    // that the movement is obvious from outside, long enough to react to
    // somebody standing there.
    public float ScanDwellSeconds = 0.9f;

    // Most angles a bot will cycle. Beyond about five it is on each one too
    // rarely to react.
    public int ScanMaxAngles = 5;

    // Minimum angle between two covering bots' held arcs. Two CTs on the same
    // doorway is one doorway covered and one uncovered.
    public float HoldAngleSeparationDeg = 45.0f;

    // An angle nearer than this is not an approach, it is a spot somebody
    // would already have walked past.
    public float HoldAngleMinDistance = 400.0f;

    // How far from the bomb a candidate angle can be and still be part of
    // defending it.
    public float HoldAngleMaxFromBomb = 2000.0f;

    // How near a bot must be for a spot to count as cleared just by being
    // seen. Deliberately short: anything further has to be walked to.
    public float OpportunisticRange = 450.0f;

    // How long each bot dwells on one lurk spot before moving to the next.
    // Long enough for the view to arrive and settle, short enough that a team
    // gets through the whole list.
    public float InspectDwellSeconds = 1.1f;

    // How long the bait phase lasts before committing.
    public float BaitSeconds = 6.0f;

    // How many fake defuse taps before the side stops baiting and goes.
    //
    // One. The tap produces the defuse sound, a lurker either reacts to it or
    // does not, and a second tap tells them nothing the first did not. The
    // Bait phase used to run its full timer regardless, which is six seconds
    // of bomb clock spent on a bluff that had already worked or already
    // failed.
    public int FakeTapsBeforeCommit = 1;

    // How far from the bomb the designated defuser is pinned during Clear.
    // Must be comfortably more than the 72 unit defuse radius.
    public float DefuserStandoff = 190.0f;

    // Furthest from the bomb the designated defuser will stage while the site
    // is being swept. Close enough that Commit means stepping onto the bomb
    // rather than crossing the site to reach it.
    public float DefuserStageMaxBombDistance = 800.0f;

    // Fraction of the inspect window the defuser gets to reach its staging
    // spot before the spot is written off and the plain bomb-watch standoff
    // takes over instead.
    //
    // Some staging spots are simply not reachable from where the round has
    // put the defuser: a full session showed one spending the entire inspect
    // phase 'enroute', skipping node after node, never anchoring and never
    // inspecting. A spot that has not been reached by this point of the
    // window is not going to be reached at all, and the standoff is a worse
    // position that actually exists.
    public float StageGiveUpFraction = 0.6f;

    // How far from the bomb a recorded position can be and still be swept as
    // a lurk spot before anyone touches the bomb.
    //
    // This was a constant 2200 units, and at that range the sweep never once
    // finished: every plant across two full playtest sessions ended its
    // inspect phase with roughly half the list unseen, because twelve
    // seconds is not enough time to walk eyes onto that much ground. A
    // lurker further out than this can still shoot, but the clearers'
    // held angles cover that; the sweep is for the corners close enough to
    // break a defuse, and a list short enough to actually finish clears
    // more of them than a long one ever did.
    public float LurkSpotMaxRange = 1500.0f;

    // How near a scan or hold candidate must be to the tracked human's
    // position to count as covering them.
    //
    // Matches the plugin's pre-aim bias radius, because it is the same idea
    // applied to the retake: the handicap decides WHICH authored angle a
    // covering bot watches, never where it walks and never what it can
    // shoot at. The bot watches the doorway nearest the human, not the
    // human through a wall, so the line between god mode and a wallhack
    // stays exactly where the contact support code drew it.
    //
    // This existed everywhere except here. Pre-plant, the pre-aim bias
    // points bots at the human's doorway; post-plant the retake owns every
    // CT and never read the tracked position at all, which is why a human
    // could sit on a known coordinate and pick off the cover while three
    // clearers stared at angles frozen at plant time.
    public float TrackedWatchRadius = 700.0f;

    // How often a covering bot may abandon its held angle for one that
    // covers the tracked human's new position. Sticky angles are the whole
    // point of AssignHoldAngle; this is the one sanctioned reason to
    // reshuffle, and rate limiting it stops a strafing human from spinning
    // the entire cover rotation like a weathervane.
    public float TrackedRepointSeconds = 3.0f;

    // ------------------------------------------------------------------
    // Rally: arriving together
    // ------------------------------------------------------------------

    // The ring the retake gathers on, measured from the bomb. Far enough
    // out not to be standing in the defence's angles while waiting, close
    // enough that the final push is seconds, not a journey. Sits just
    // inside DefenceRadius on purpose: a bot on the ring already counts as
    // covering the defuse the moment the push starts.
    public float RallyHoldDistance = 1100.0f;

    // A bot within HoldDistance plus this counts as set for the release
    // arithmetic, so somebody easing to a stop on the line is not the one
    // straggler holding the whole side.
    public float RallyHoldTolerance = 250.0f;

    // Fraction of the alive CTs that must be on the ring before the side
    // goes in. Two thirds, deliberately: with three, four or five alive
    // that is everyone but one, which is the requested tolerance. One bot
    // stuck across the map must not turn a synchronised hit back into the
    // trickle it replaces.
    public float RallyReadyFraction = 0.66f;

    // Longest the rally may hold from the plant, whatever the ring looks
    // like. The bomb does not wait, and neither does this.
    public float RallyMaxSeconds = 15.0f;

    // The rally only holds while the spare-time arithmetic still leaves at
    // least this much on the clock, so gathering can never eat the time the
    // inspect, the bait and the defuse itself need. Roughly InspectSeconds
    // plus BaitSeconds with a little margin.
    public float RallySpareFloor = 20.0f;

    // Knife out only while further than this from the bomb. The last
    // stretch to the ring is approached with the gun up, same principle as
    // the rotation sprint's danger radius: however long the run, the part
    // where somebody might actually be waiting is walked armed.
    public float RallyKnifeMinDistance = 1400.0f;

    // ------------------------------------------------------------------
    // The defensive ring around the defuse
    // ------------------------------------------------------------------

    // The donut around the bomb a computed ring post may sit in. The floor
    // keeps a post off the defuser's toes; the ceiling keeps it close
    // enough that it is defending the defuse rather than patrolling.
    public float RingRadiusMin = 260.0f;
    public float RingRadiusMax = 750.0f;

    // Furthest from the bomb an authored clear spot is worth assigning to a
    // clearer. Candidates are still gathered out to maxSpotDistance, because
    // the defuser's staging fallback needs the wide net, but a clearer posted
    // beyond this is not defending the defuse, it is commuting. Left
    // unassigned instead, the bot sweeps its beat through Inspect and then
    // takes a computed ring post from Bait onward, which is the circle the
    // defuser was always supposed to have. Matches RingRadiusMax so authored
    // and computed posts obey the same ceiling.
    public float ClearSpotMaxFromBomb = 750.0f;

    // Minimum bearing separation, in degrees around the bomb, between any
    // two defensive positions. This is what turns a pile of CTs into a
    // circle facing outwards: posts are chosen to spread around the clock
    // face, each owning its own slice of the approaches.
    public float RingMinAngularGap = 55.0f;

    // Minimum straight-line spacing between any two assigned anchors,
    // authored or computed. Two bots standing in each other's pockets are
    // one grenade, one spray, and one uncovered site.
    public float MinPostSpacing = 220.0f;

    // When the tracked human is within this of a covering bot, the bot
    // watches the human's actual position instead of its assigned angle.
    // The handicap knew exactly where the human stood while pinned bots
    // stared down authored angles ninety degrees away and were looked at
    // 'for quite some time' before reacting; inside this range the theory
    // yields to the fact. Still attention only: the view cone goes to the
    // doorway the human occupies, and the shooting remains native.
    public float TrackedThreatRange = 1000.0f;

    // Extra headroom beyond the defuse time. When the bomb has less than
    // defuseTime plus this remaining, commit regardless of phase, because a
    // fake defuse that loses the round is not clever.
    public float MustCommitReserve = 2.5f;

    // Fake defuse duty cycle. The hold has to be long enough for the
    // begindefuse event and its sound to actually fire.
    public float FakeHoldSeconds = 0.7f;
    public float FakeGapSeconds = 1.6f;

    public bool FakeDefuseEnabled = true;

    // Inside this many seconds of finishing, a lone defuser stays on the bomb
    // regardless. The only way to lose a defuse that is nearly complete is to
    // stop doing it.
    public float LastSecondCommitment = 1.0f;

    // How near a team mate has to be to count as covering the defuse.
    public float DefenceRadius = 1200.0f;

    // Defuse durations. Used to work out how much time is actually available
    // for inspecting and baiting before the defuse has to start.
    private const float DefuseWithKit = 5.0f;
    private const float DefuseWithoutKit = 10.0f;

    // How close a clearer must get to its angle before it pins.
    private const float ArriveRadius = 90.0f;

    // Inside this of the bomb, holding USE starts a defuse. The engine
    // radius is 72 units; a little less is used so a marginal position does
    // not produce a tap that fires nothing. Shared between the fake defuse
    // and the bait walk-in, which have to agree about where "close enough"
    // begins.
    private const float FakeTapRange = 62.0f;

    // ------------------------------------------------------------------
    // Per-round state
    // ------------------------------------------------------------------

    public KaiRetakePhase Phase { get; private set; } = KaiRetakePhase.Idle;

    // Who currently owns the defuse, for the per-tick chain to yield to.
    // -1 outside an active retake. Exposed because the route follower and
    // contact support both proved capable of claiming the defuser after the
    // Commit release handed it to native, and they need to know whose wheel
    // not to touch.
    public int DefuserSlot
    {
        get { return _defuserSlot; }
    }

    private float _plantTime;
    private int _defuserSlot = -1;
    private bool _defuserHasKit;
    private KaiHoldSpot? _defuserStage;
    private KaiPoint _bombPos = new();

    // Value may be null: a bot with no authored angle is still a sweeper.
    private readonly Dictionary<int, KaiHoldSpot?> _clearAssignments = new();

    // Places a lurker could be, swept during the Inspect phase. Built from the
    // learner: T post-plant anchors are where Ts stand, and ctClear watch
    // points are where a T was when it killed a CT. Both are lurk positions.
    private readonly List<KaiPoint> _lurkSpots = new();

    // slot -> which lurk spot that bot is currently looking at, and when it
    // should move to the next. Each bot starts at a different index so the
    // team covers the list in parallel rather than all staring at one spot.
    private readonly Dictionary<int, int> _inspectIndex = new();
    private readonly Dictionary<int, float> _inspectNext = new();

    // slot -> the arc of the site this bot is responsible for sweeping.
    // Without this every sweeper walks the same list in the same order and the
    // team checks one corner five times while five others go unlooked at.
    private readonly Dictionary<int, float> _inspectSectors = new();

    // slot -> the indices into _lurkSpots that fall inside that bot's arc.
    private readonly Dictionary<int, List<int>> _inspectBeat = new();

    // slot -> the angle this covering bot holds once the sweep is done. Sticky
    // for the round, and kept clear of every other bot's arc.
    private readonly Dictionary<int, KaiPoint> _holdAngle = new();

    // slot -> when this bot last checked its held angle against the tracked
    // human's position. Rate limits the one sanctioned reshuffle so the
    // cover follows the human at a walk rather than a twitch.
    private readonly Dictionary<int, float> _holdRepointAt = new();

    // When the Inspect phase actually began. With a rally in front of it,
    // "seconds since the plant" no longer measures the inspect window, so
    // every window below Rally is measured from here instead. Zero means
    // Inspect has not started yet.
    private float _inspectStartedAt;

    // Which bots the rally has put a knife in the hands of, so the gun goes
    // back the moment the run ends and never a moment later.
    private readonly HashSet<int> _rallyKnife = new();

    // Bots for whom no ring post could be computed this plant, so the
    // search does not run again every tick for the rest of the round.
    private readonly HashSet<int> _ringPostFailed = new();

    // How many ring posts have been fabricated this plant, for naming.
    private int _ringPostCount;

    // Per-bot scan state: the angles visible from where it is standing, where
    // it is in the cycle, and where it was standing when the set was built.
    private readonly Dictionary<int, List<KaiPoint>> _scanSet = new();
    private readonly Dictionary<int, int> _scanIndex = new();
    private readonly Dictionary<int, float> _scanNext = new();
    private readonly Dictionary<int, KaiPoint> _scanFrom = new();

    // The map data, kept from the plant so per-tick code can reach the learned
    // angles without every caller having to thread it through.
    private KaiMapTactics _map = new();

    // slot -> the approach position that sweeper is currently walking to.
    // Kept so that other sweepers can be steered clear of it.
    private readonly Dictionary<int, KaiPoint> _sweepApproach = new();

    // One announcement per bot per spot, so the log reads as a sequence of
    // discrete actions rather than the same line repeating every tick.
    private readonly HashSet<(int Slot, int Spot)> _announcedApproach = new();
    private readonly HashSet<(int Slot, int Spot)> _announcedArrival = new();

    // The central tally: which lurk spot indices have actually been seen by
    // somebody this round, and by whom.
    //
    // Without this the inspection was a stopwatch pretending to be a check.
    // The phase advanced after InspectSeconds whether or not a single corner
    // had been looked at, and a sweeper that spent the whole phase failing to
    // get line of sight to one spot still counted as having finished. A spot
    // only enters this set when a bot has held an unobstructed trace to it for
    // a full dwell, which is the same standard a human would use for calling
    // an angle cleared.
    private readonly Dictionary<int, int> _clearedSpots = new();

    // Set once every spot has been cleared, so the phase machine can commit
    // early instead of burning the rest of the timer on an empty site.
    private bool _sweepComplete;

    // ------------------------------------------------------------------
    // Lone defuser routine
    //
    // The retake logic assumes a team: somebody sweeps while somebody else
    // waits to defuse. With one CT left that premise collapses. There is
    // nobody to cover him while he sweeps and nobody to defuse while he
    // covers, so holding him at a standoff for the full inspection buys
    // nothing and spends the clock.
    //
    // Alone he runs a different routine entirely: check a handful of the
    // nearest spots while moving, tap the bomb once to make the Ts think the
    // defuse has started, then WALK a few steps off it so his own footsteps
    // do not mask theirs, and listen. Anyone still alive has to either show
    // themselves or let the bomb be defused.
    // ------------------------------------------------------------------

    private enum KaiSoloStage
    {
        Sweep = 0,
        Tap = 1,
        Withdraw = 2,
        Listen = 3,
        Defuse = 4,
    }

    private KaiSoloStage _soloStage = KaiSoloStage.Sweep;
    private float _soloStageUntil;
    private int _soloSwept;

    // Spots this bot has walked to and held itself, as distinct from the
    // shared cleared tally that anybody can contribute to.
    private readonly HashSet<int> _soloSweptSpots = new();
    private KaiPoint? _soloWithdrawTo;

    // How many spots a lone defuser checks before committing, clock allowing.
    public int SoloSweepSpots = 4;

    // The floor when the clock is comfortable. A lone retake that checks one
    // corner and dives on the bomb is a coin flip rather than a retake.
    public int SoloMinSweepSpots = 3;

    // Spare seconds above which the clock counts as comfortable.
    public float SoloComfortableSeconds = 20.0f;

    // Seconds allowed per spot during the solo sweep.
    public float SoloSweepDwell = 1.2f;

    // How long the single fake tap is held.
    public float SoloTapSeconds = 0.7f;

    // Four walking steps. A step is roughly forty units, and walking rather
    // than running is the whole point: a running withdrawal makes exactly the
    // noise the pause is meant to listen through.
    public float SoloWithdrawDistance = 160.0f;

    // How long to stand still and listen after withdrawing.
    public float SoloListenSeconds = 2.5f;

    // How many enemies are alive right now. Refreshed every tick and used by
    // every branch that has a cheaper option when the site is empty.
    public int EnemiesAlive { get; private set; }

    private float _nextFakeToggle;
    private bool _fakeHolding;
    private int _fakeTapCount;

    // True once the side has finished baiting and moved on.
    //
    // A latch, not a live test. The tap-count condition alone was wrong:
    // DriveFakeDefuse schedules a repeat hold 1.6s after each release, so
    // _fakeHolding goes true again, the guard flips back to false, and the
    // phase falls through into Bait a second time. Observed twice in one
    // session as "phase Commit -> Bait", which is a defuse being called off to
    // go and bluff again.
    private bool _baitDone;

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    public void Reset(string reason)
    {
        Phase = KaiRetakePhase.Idle;
        _plantTime = 0.0f;
        _defuserSlot = -1;
        _defuserHasKit = false;
        _defuserStage = null;
        _clearAssignments.Clear();
        _lurkSpots.Clear();
        _inspectIndex.Clear();
        _inspectNext.Clear();
        _inspectSectors.Clear();
        _inspectBeat.Clear();
        _sweepApproach.Clear();
        _announcedApproach.Clear();
        _announcedArrival.Clear();
        _holdAngle.Clear();
        _holdRepointAt.Clear();
        _inspectStartedAt = 0.0f;
        _ringPostFailed.Clear();
        _ringPostCount = 0;

        // Anyone still holding a rally knife gets its gun back before the
        // state is dropped, or a round-end reset leaves a bot knifing into
        // the next engagement.
        EndRallyKnives("director reset");
        _scanSet.Clear();
        _scanIndex.Clear();
        _scanNext.Clear();
        _scanFrom.Clear();
        _clearedSpots.Clear();
        _sweepComplete = false;
        _soloStage = KaiSoloStage.Sweep;
        _soloStageUntil = 0.0f;
        _soloSwept = 0;
        _soloSweptSpots.Clear();
        _soloWithdrawTo = null;
        _nextFakeToggle = 0.0f;
        _fakeHolding = false;
        _fakeTapCount = 0;
        _baitDone = false;

        // Every cached path was solved to an assignment that no longer
        // exists. Left in place they would walk bots to last round's spots.
        Pathing?.Clear();

        KaiLog.Event(nameof(Reset), $"retake director reset ({reason})");
    }

    public string StatusLine()
    {
        return $"enabled={Enabled} fake={FakeDefuseEnabled} inspect={InspectSeconds:F1}s " +
               $"bait={BaitSeconds:F1}s standoff={DefuserStandoff:F0}u phase={Phase} " +
               $"defuserSlot={_defuserSlot} clearers={_clearAssignments.Count} " +
               $"lurkSpots={_lurkSpots.Count} swept={_clearedSpots.Count} " +
               $"enemiesAlive={EnemiesAlive} solo={IsSoloRetake()} stage={_soloStage} " +
               $"complete={_sweepComplete} taps={_fakeTapCount}";
    }

    // Assign CT roles the moment the bomb lands. Nobody is moved here; the
    // native state machine still paths every CT to the bomb exactly as it does
    // today. All this decides is who owns which job when they arrive.
    public void OnBombPlanted(KaiPoint bombPos, KaiMapTactics map, float maxSpotDistance)
    {
        Reset("new plant");

        if (!Enabled)
        {
            KaiLog.Event(nameof(OnBombPlanted), "director disabled, CT side left stock");
            return;
        }

        _bombPos = bombPos;
        _plantTime = Server.CurrentTime;
        Phase = KaiRetakePhase.Inspect;

        var cts = AliveBots(CsTeam.CounterTerrorist);

        if (cts.Count == 0)
        {
            KaiLog.Event(nameof(OnBombPlanted),
                "no alive CT bots, nothing to direct. Census follows, showing every field " +
                "the filter tests so the disagreement can be identified rather than guessed at.",
                KaiLogLevel.Error);

            KaiCensus.Dump("OnBombPlanted/noCTs");

            Phase = KaiRetakePhase.Idle;
            return;
        }

        CCSPlayerController? defuser = PickDefuser(cts, bombPos);

        if (defuser == null)
        {
            KaiLog.Event(nameof(OnBombPlanted), "could not resolve a defuser, CT side left stock",
                KaiLogLevel.Error);
            Phase = KaiRetakePhase.Idle;
            return;
        }

        _defuserSlot = defuser.Slot;

        // Candidate clear spots near this bomb.
        var candidates = map.CtClear
            .Where(s => s.Anchor.DistanceSqr(bombPos.X, bombPos.Y, bombPos.Z)
                        <= maxSpotDistance * maxSpotDistance)
            .Where(s => s.Team == 0 || s.Team == (int)CsTeam.CounterTerrorist)
            .OrderByDescending(s => s.Priority)
            .ToList();

        // Prefer a staging spot the defuser can reach and still be on the bomb
        // quickly from. A staging position on the far side of the site is a
        // defuser that has to cross it again the moment Commit arrives, and
        // crossing a site you have just declared uncleared is the slowest and
        // most dangerous way to start a defuse.
        _defuserStage = candidates
            .Where(s => s.Stage)
            .Where(s => s.Anchor.DistanceXY(bombPos.X, bombPos.Y) <= DefuserStageMaxBombDistance)
            .OrderBy(s => s.Anchor.DistanceXY(bombPos.X, bombPos.Y))
            .FirstOrDefault();

        if (_defuserStage == null)
        {
            // Nothing close enough. Fall back to the old behaviour rather
            // than leaving the defuser with no staging spot at all, which
            // sends it to the bare standoff branch.
            _defuserStage = candidates.FirstOrDefault(s => s.Stage);

            if (_defuserStage != null)
            {
                KaiLog.Event(nameof(OnBombPlanted),
                    $"no staging spot within {DefuserStageMaxBombDistance:F0} units of the " +
                    $"bomb, falling back to '{_defuserStage.Name}' at " +
                    $"{_defuserStage.Anchor.DistanceXY(bombPos.X, bombPos.Y):F0} units");
            }
        }

        if (_defuserStage != null)
        {
            KaiLog.Event(nameof(OnBombPlanted), $"defuser stage spot = '{_defuserStage.Name}'");
        }
        else
        {
            KaiLog.Event(
                nameof(OnBombPlanted),
                $"no stage spot near this plant, defuser pins at {DefuserStandoff:F0} units " +
                $"from the bomb and watches it");
        }

        _map = map;

        BuildLurkSpots(bombPos, map);
        AssignInspectionBeats(cts, bombPos);
        AssignClearers(cts, candidates);

        // Gather before going in, when the clock and the headcount allow.
        //
        // The retake used to start its phase clocks at the plant and let
        // every bot walk its own leg, so the side arrived in walk-distance
        // order: measured over thirteen plants, the typical picture at
        // Commit was the defuser plus one clearer set and the rest strung
        // out en route, which is a queue of fair duels for the lurker. The
        // rally holds everyone on a ring short of the site and releases
        // them together, so the sweep opens as a crossfire instead.
        //
        // A lone CT has nobody to synchronise with, and a plant with no
        // spare time has nothing to spend on gathering; both go straight to
        // Inspect exactly as before.
        float remainingAtPlant = RemainingBombSeconds(_plantTime);
        float defuseTimeAtPlant;

        if (_defuserHasKit)
        {
            defuseTimeAtPlant = DefuseWithKit;
        }
        else
        {
            defuseTimeAtPlant = DefuseWithoutKit;
        }

        float spareAtPlant = remainingAtPlant - defuseTimeAtPlant - MustCommitReserve;
        bool rallying = cts.Count >= 2 && spareAtPlant > RallySpareFloor;

        if (rallying)
        {
            Phase = KaiRetakePhase.Rally;
            _inspectStartedAt = 0.0f;
        }
        else
        {
            Phase = KaiRetakePhase.Inspect;
            _inspectStartedAt = _plantTime;
        }

        KaiLog.Event(
            nameof(OnBombPlanted),
            $"phase -> {Phase}" +
            (rallying
                ? $" (gathering on the {RallyHoldDistance:F0} ring, {spareAtPlant:F1}s spare)"
                : $" (no rally: {cts.Count} CT(s), {spareAtPlant:F1}s spare)") +
            $", defuser slot {_defuserSlot} ('{defuser.PlayerName}') kit={_defuserHasKit}, " +
            $"{_clearAssignments.Count} clearers, {_lurkSpots.Count} lurk spots, " +
            $"inspect={InspectSeconds:F1}s then bait={BaitSeconds:F1}s");

        // Announce the shape of the retake. Who has the defuse and how many
        // are clearing first is exactly what somebody joining the retake needs
        // to know, and none of it was being said.
        KaiComms.Call((int)CsTeam.CounterTerrorist, _defuserSlot, "retakeroles",
            _clearAssignments.Count > 0
                ? $"retake on, I have the defuse, {_clearAssignments.Count} of you clear first"
                : "retake on, I have the defuse and I am alone on this",
            10.0f);
    }

    // A kit halves the defuse time, so a bot holding one is worth more at the
    // bomb than on an angle. Proximity breaks the tie.
    private CCSPlayerController? PickDefuser(List<CCSPlayerController> cts, KaiPoint bombPos)
    {
        CCSPlayerController? best = null;
        float bestScore = float.MaxValue;

        foreach (var bot in cts)
        {
            var pawn = bot.PlayerPawn?.Value;
            var origin = pawn?.AbsOrigin;

            if (pawn == null || origin == null)
            {
                continue;
            }

            float dist = MathF.Sqrt(bombPos.DistanceSqr(origin.X, origin.Y, origin.Z));
            bool hasKit = HasDefuser(pawn);

            // A kit is worth a 4000 unit head start in the ranking.
            float score = dist;

            if (hasKit)
            {
                score = dist - 4000.0f;
            }

            if (score < bestScore)
            {
                bestScore = score;
                best = bot;
                _defuserHasKit = hasKit;
            }
        }

        return best;
    }

    // Everyone who is not the defuser gets a clearing angle. Greedy
    // nearest-bot assignment in priority order, so the strongest angles get
    // covered first and get the shortest walks.
    private void AssignClearers(List<CCSPlayerController> cts, List<KaiHoldSpot> candidates)
    {
        var clearers = cts.Where(b => b.Slot != _defuserSlot).ToList();
        var taken = new HashSet<int>();
        var takenAnchors = new List<KaiPoint>();

        foreach (var spot in candidates)
        {
            if (spot.Stage)
            {
                continue;
            }

            // A post that cannot see the defuse cannot defend it. In the
            // session of 2026-09-05 all three clearers drew authored spots
            // 800-2000+ units from the plant, spent the whole defuse "en
            // route", and stood stacked on the bomb while native post-plant
            // logic dragged them back every contact window. Skipping the far
            // spots here routes those bots into AssignRingPost instead.
            float fromBomb = spot.Anchor.DistanceXY(_bombPos.X, _bombPos.Y);

            if (fromBomb > ClearSpotMaxFromBomb)
            {
                KaiLog.Event(nameof(AssignClearers),
                    $"'{spot.Name}' skipped: {fromBomb:F0} units from the bomb against a " +
                    $"ceiling of {ClearSpotMaxFromBomb:F0}. Too far to defend the defuse, " +
                    $"the bot will take a ring post instead.");

                continue;
            }

            // Spacing between the posts themselves. The authored spots carry
            // no such guarantee, and two anchors in the same pocket put two
            // bots in one spray pattern while an approach goes unowned.
            if (!KaiFormation.FarEnoughFrom(spot.Anchor, takenAnchors, MinPostSpacing))
            {
                KaiLog.Event(nameof(AssignClearers),
                    $"'{spot.Name}' skipped: within {MinPostSpacing:F0} units of an anchor " +
                    $"already assigned. Two bots in one pocket is one covered approach.");

                continue;
            }

            CCSPlayerController? best = null;
            float bestDistSqr = float.MaxValue;

            foreach (var bot in clearers)
            {
                if (taken.Contains(bot.Slot))
                {
                    continue;
                }

                var origin = bot.PlayerPawn?.Value?.AbsOrigin;

                if (origin == null)
                {
                    continue;
                }

                float d = spot.Anchor.DistanceSqr(origin.X, origin.Y, origin.Z);

                if (d < bestDistSqr)
                {
                    bestDistSqr = d;
                    best = bot;
                }
            }

            if (best == null)
            {
                break;
            }

            taken.Add(best.Slot);
            takenAnchors.Add(spot.Anchor);
            _clearAssignments[best.Slot] = spot;

            KaiLog.Event(
                nameof(AssignClearers),
                $"slot {best.Slot} ('{best.PlayerName}') clears '{spot.Name}' at " +
                $"({spot.Anchor.X:F0},{spot.Anchor.Y:F0},{spot.Anchor.Z:F0}) watching " +
                $"({spot.Watch.X:F0},{spot.Watch.Y:F0},{spot.Watch.Z:F0}), " +
                $"walk={MathF.Sqrt(bestDistSqr):F0} units");
        }

        // Everybody who is not defusing sweeps, whether or not an authored
        // angle was available for them.
        //
        // Previously a bot with no angle was left entirely stock, which meant
        // it walked straight to the bomb while two team mates cleared. On a
        // five man side with only a handful of angles near the plant that is
        // most of the team ignoring the sweep, and it is why the clearing was
        // invisible from the outside. A bot with no authored angle still has
        // eyes and can still be given a beat.
        int adopted = 0;

        foreach (var bot in clearers)
        {
            if (taken.Contains(bot.Slot))
            {
                continue;
            }

            _clearAssignments[bot.Slot] = null;
            adopted++;
        }

        if (adopted > 0)
        {
            KaiLog.Event(
                nameof(AssignClearers),
                $"{adopted} CT bot(s) have no authored angle near this plant but will still " +
                $"sweep the site on their own beat");
        }
    }

    // Work out where a lurker could be hiding near this bomb.
    //
    // Same trick as the T side, run in reverse. A postPlant anchor is a
    // position a T stood in and won from. A ctClear watch point is where a T
    // was when it killed a CT. Both are places a T waits, so both are places
    // worth checking before anyone touches the bomb.
    private void BuildLurkSpots(KaiPoint bombPos, KaiMapTactics map)
    {
        _lurkSpots.Clear();

        const float spacing = 220.0f;

        // The reasoning behind the range lives on the tunable itself: a list
        // the sweep can actually finish beats a longer one it never does.
        float maxRange = LurkSpotMaxRange;

        // Same vertical rule as the T side: everything pooled here is stored
        // at feet level, and the chest offset is added once when a bot is
        // actually pointed at it. An anchor is already at the feet, but a
        // learned watch point carries the chest offset from when the sample
        // was recorded, so it is lowered back before pooling. Skipping this
        // leaves half the sweep aimed a chest height above the lurker.
        var raw = new List<KaiPoint>();

        foreach (var spot in map.PostPlant)
        {
            raw.Add(spot.Anchor);
        }

        foreach (var spot in map.CtClear)
        {
            raw.Add(new KaiPoint(
                spot.Watch.X, spot.Watch.Y, spot.Watch.Z - KaiHeights.Chest));
        }

        foreach (var candidate in raw)
        {
            if (candidate.DistanceXY(bombPos.X, bombPos.Y) > maxRange)
            {
                continue;
            }

            bool duplicate = false;

            foreach (var kept in _lurkSpots)
            {
                if (kept.DistanceXY(candidate.X, candidate.Y) < spacing
                    && MathF.Abs(kept.Z - candidate.Z) < 100.0f)
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
            {
                _lurkSpots.Add(candidate);
            }
        }

        _lurkSpots.Sort((a, b) =>
            a.DistanceXY(bombPos.X, bombPos.Y).CompareTo(b.DistanceXY(bombPos.X, bombPos.Y)));

        KaiLog.Event(nameof(BuildLurkSpots),
            $"{raw.Count} candidates reduced to {_lurkSpots.Count} distinct lurk spots to sweep");
    }

    // Divide the lurk spots between the sweepers by arc.
    //
    // Every spot is scored against every bot's bearing and handed to the
    // closest one, so the site is partitioned rather than duplicated. Five
    // bots then sweep five different parts of the site simultaneously instead
    // of queueing through the same list.
    //
    // A bot whose arc happens to contain nothing still gets the single nearest
    // spot, so no sweeper stands idle while corners go unchecked.
    private void AssignInspectionBeats(List<CCSPlayerController> cts, KaiPoint bombPos)
    {
        _inspectSectors.Clear();
        _inspectBeat.Clear();

        if (_lurkSpots.Count == 0 || cts.Count == 0)
        {
            return;
        }

        var slots = new List<int>();

        foreach (var bot in cts)
        {
            slots.Add(bot.Slot);
        }

        float baseBearing = KaiFormation.Bearing(
            bombPos.X, bombPos.Y, _lurkSpots[0].X, _lurkSpots[0].Y);

        var sectors = KaiFormation.AssignSectors(slots, baseBearing);

        foreach (var kv in sectors)
        {
            _inspectSectors[kv.Key] = kv.Value;
            _inspectBeat[kv.Key] = new List<int>();
        }

        // Hand each spot to whichever arc it sits closest to.
        for (int i = 0; i < _lurkSpots.Count; i++)
        {
            float bearing = KaiFormation.Bearing(
                bombPos.X, bombPos.Y, _lurkSpots[i].X, _lurkSpots[i].Y);

            int bestSlot = -1;
            float bestGap = float.MaxValue;

            foreach (var kv in sectors)
            {
                float gap = KaiFormation.AngleGap(bearing, kv.Value);

                if (gap < bestGap)
                {
                    bestGap = gap;
                    bestSlot = kv.Key;
                }
            }

            if (bestSlot >= 0)
            {
                _inspectBeat[bestSlot].Add(i);
            }
        }

        foreach (var kv in _inspectBeat)
        {
            if (kv.Value.Count == 0)
            {
                // Nothing in this arc. Give it the nearest spot rather than
                // leaving a sweeper with nothing to do.
                kv.Value.Add(0);
            }

            KaiLog.Event(nameof(AssignInspectionBeats),
                $"slot {kv.Key} sweeps {kv.Value.Count} lurk spot(s) on bearing " +
                $"{_inspectSectors[kv.Key]:F0}");
        }

        KaiLog.Event(nameof(AssignInspectionBeats),
            $"{_lurkSpots.Count} lurk spots divided between {slots.Count} sweepers " +
            $"in {360.0f / slots.Count:F0} degree arcs");
    }

    // Sweep the lurk spots on this bot's beat.
    //
    // A sweep is not a bot standing still turning its head. It only counts as
    // cleared if the bot could actually SEE the spot, so a sweeper that has no
    // line to its current target walks towards it until it does. That is what
    // makes the inspection visible on screen and meaningful in effect: the
    // team physically moves through the site, each bot working its own arc,
    // and a corner nobody could see is a corner nobody has cleared.
    //
    // Approach positions are kept apart by MinBotSpacing so two sweepers never
    // end up in the same doorway, where one spray transfer would take both.
    private bool DriveInspection(
        float now, CCSPlayerController bot, Vector origin, KaiBotIntent intent)
    {
        if (_lurkSpots.Count == 0)
        {
            return false;
        }

        if (!_inspectBeat.TryGetValue(bot.Slot, out var beat) || beat.Count == 0)
        {
            return false;
        }

        if (!_inspectIndex.TryGetValue(bot.Slot, out int cursor))
        {
            cursor = 0;
            _inspectIndex[bot.Slot] = cursor;
            _inspectNext[bot.Slot] = now + InspectDwellSeconds;

            KaiLog.Event(nameof(DriveInspection),
                $"slot {bot.Slot} starts its beat, {beat.Count} spot(s) to check");

            KaiComms.Call((int)CsTeam.CounterTerrorist, bot.Slot, "sweepstart",
                $"sweeping the site, {beat.Count} spots to check", 12.0f);
        }

        int spotIndex = beat[cursor % beat.Count];

        if (spotIndex < 0 || spotIndex >= _lurkSpots.Count)
        {
            return false;
        }

        var target = _lurkSpots[spotIndex];
        var pawn = bot.PlayerPawn?.Value;

        if (pawn == null || !pawn.IsValid)
        {
            return false;
        }

        var eye = new Vector(origin.X, origin.Y, origin.Z + pawn.ViewOffset.Z);
        var aim = new Vector(target.X, target.Y, target.Z + KaiHeights.Head);

        bool canSee = KaiRayTraceBridge.CanSee(eye, aim);

        intent.Watch = new KaiPoint(target.X, target.Y, target.Z + KaiHeights.Head);

        if (!canSee)
        {
            // Cannot see it, so it is not cleared. Close the distance, keeping
            // clear of the other sweepers' approach lanes.
            var approach = ApproachPositionFor(bot.Slot, target, origin);

            if (_announcedApproach.Add((bot.Slot, spotIndex)))
            {
                KaiLog.Event(nameof(DriveInspection),
                    $"slot {bot.Slot} MOVING to clear lurk spot {spotIndex} " +
                    $"({cursor + 1} of {beat.Count} on its beat), " +
                    $"{target.DistanceXY(origin.X, origin.Y):F0} units away and no line to it yet");

                KaiComms.Call((int)CsTeam.CounterTerrorist, bot.Slot, $"clearing:{bot.Slot}",
                    $"taking {KaiCallouts.Describe(target, _bombPos)}, " +
                    $"{beat.Count - cursor - 1} more after it", 3.0f);
            }

            // Walked, not shoved. The approach position is chosen for its
            // sightline and its spacing from the other sweepers, neither of
            // which cares whether there is a wall between the bot and it, so
            // the straight line this used to steer along was frequently into
            // one.
            var closing = new KaiPoint(origin.X, origin.Y, origin.Z);

            if (Pathing != null)
            {
                Pathing.Steer(
                    bot.Slot, closing, approach, now, intent, $"sweep:{spotIndex}");
            }
            else
            {
                intent.SteerTowards = approach;
            }

            intent.SourceName = $"sweep:{spotIndex}:closing";

            // Hold the dwell timer while closing, so the spot gets its full
            // look once the bot can finally see it.
            _inspectNext[bot.Slot] = now + InspectDwellSeconds;

            KaiLog.Throttled($"sweepmove:{bot.Slot}", nameof(DriveInspection),
                $"slot {bot.Slot} cannot see lurk spot {spotIndex}, closing to clear it " +
                $"({target.DistanceXY(origin.X, origin.Y):F0} units out)", 2.0f);

            return true;
        }

        intent.SourceName = $"inspect:{spotIndex}";

        if (_announcedArrival.Add((bot.Slot, spotIndex)))
        {
            KaiLog.Event(nameof(DriveInspection),
                $"slot {bot.Slot} ARRIVED with eyes on lurk spot {spotIndex}, " +
                $"{target.DistanceXY(origin.X, origin.Y):F0} units out, holding it for " +
                $"{InspectDwellSeconds:F1}s");
        }

        if (_inspectNext.TryGetValue(bot.Slot, out float due) && now >= due)
        {
            MarkCleared(spotIndex, bot.Slot);

            cursor = (cursor + 1) % beat.Count;
            _inspectIndex[bot.Slot] = cursor;
            _inspectNext[bot.Slot] = now + InspectDwellSeconds;

            KaiLog.Event(nameof(DriveInspection),
                $"slot {bot.Slot} cleared lurk spot {spotIndex} " +
                $"({_clearedSpots.Count}/{_lurkSpots.Count} site cleared), moving to " +
                $"{cursor + 1} of {beat.Count} on its beat");

            // Say what was cleared AND what is next. A clear on its own tells
            // a team mate where not to look; a clear plus a destination tells
            // them what is still uncovered and for how long.
            string nextUp = "";

            int following = (cursor + 1) % beat.Count;

            if (following < beat.Count && beat[following] < _lurkSpots.Count)
            {
                nextUp = KaiCallouts.Describe(_lurkSpots[beat[following]], _bombPos);
            }

            KaiComms.Call((int)CsTeam.CounterTerrorist, bot.Slot, $"clear:{bot.Slot}",
                nextUp.Length > 0 && beat.Count > 1
                    ? $"{KaiCallouts.Describe(target, _bombPos)} clear, moving to {nextUp}"
                    : $"{KaiCallouts.Describe(target, _bombPos)} clear",
                3.0f);
        }

        KaiLog.Throttled($"inspect:{bot.Slot}", nameof(DriveInspection),
            $"slot {bot.Slot} clearing lurk spot {spotIndex} " +
            $"({cursor + 1} of {beat.Count}) at ({target.X:F0},{target.Y:F0},{target.Z:F0})", 2.0f);

        return true;
    }

    // Record that a spot has been held in view for a full dwell.
    //
    // Opportunistic as well as deliberate: if a sweeper happens to have a
    // clear line to a spot that is not on its own beat, that spot is cleared
    // too. Somebody looked at it, which is the only thing that matters.
    private void MarkCleared(int spotIndex, int bySlot)
    {
        if (spotIndex < 0 || spotIndex >= _lurkSpots.Count)
        {
            return;
        }

        if (_clearedSpots.ContainsKey(spotIndex))
        {
            return;
        }

        _clearedSpots[spotIndex] = bySlot;

        if (_clearedSpots.Count >= _lurkSpots.Count && !_sweepComplete)
        {
            _sweepComplete = true;

            KaiLog.Event(nameof(MarkCleared),
                $"site fully swept: all {_lurkSpots.Count} lurk spots cleared, " +
                $"no need to run the rest of the inspection timer");
        }
    }

    // Which spots nobody has looked at yet, for the log and for deciding
    // whether a bait is even worth doing.
    private List<int> UnclearedSpots()
    {
        var result = new List<int>();

        for (int i = 0; i < _lurkSpots.Count; i++)
        {
            if (!_clearedSpots.ContainsKey(i))
            {
                result.Add(i);
            }
        }

        return result;
    }

    // Every sweeper also clears anything else it can see from where it stands.
    // A bot walking its own beat frequently has a clear line across half the
    // site, and pretending it did not see those corners would leave the tally
    // reporting work outstanding that has plainly been done.
    private void SweepOpportunistically(CCSPlayerController bot, Vector origin)
    {
        if (_sweepComplete || _lurkSpots.Count == 0)
        {
            return;
        }

        var pawn = bot.PlayerPawn?.Value;

        if (pawn == null || !pawn.IsValid)
        {
            return;
        }

        var eye = new Vector(origin.X, origin.Y, origin.Z + pawn.ViewOffset.Z);

        foreach (int index in UnclearedSpots())
        {
            var spot = _lurkSpots[index];

            // Close range only.
            //
            // This was 1400 units, which was far too generous: a bot standing
            // at the edge of a site can trace to most of it at once, so the
            // whole sweep was being marked complete without anybody taking a
            // step. On a measured round that produced 64 spots cleared in
            // passing against 2 cleared deliberately, and the inspection phase
            // ended before a single sweeper had walked anywhere.
            //
            // At this range the bot is genuinely on top of the spot, which is
            // the only case where seeing it and having checked it are the same
            // thing. Everything else has to be walked to.
            if (spot.DistanceXY(origin.X, origin.Y) > OpportunisticRange)
            {
                continue;
            }

            var target = new Vector(spot.X, spot.Y, spot.Z + KaiHeights.Chest);

            if (KaiRayTraceBridge.CanSee(eye, target))
            {
                MarkCleared(index, bot.Slot);

                KaiLog.Event(nameof(SweepOpportunistically),
                    $"slot {bot.Slot} is right on top of lurk spot {index} and can see it, " +
                    $"clearing it in passing ({_clearedSpots.Count}/{_lurkSpots.Count} " +
                    $"of the site)");
            }
        }
    }

    // Somewhere to stand that should see a lurk spot, without crowding another
    // sweeper. Steps back from the spot towards the bot's own side of it, then
    // nudges the bearing round if that lane is already taken.
    private KaiPoint ApproachPositionFor(int slot, KaiPoint target, Vector origin)
    {
        float bearing = KaiFormation.Bearing(target.X, target.Y, origin.X, origin.Y);

        var taken = new List<KaiPoint>();

        foreach (var kv in _sweepApproach)
        {
            if (kv.Key != slot)
            {
                taken.Add(kv.Value);
            }
        }

        // Try the direct line first, then fan outwards either side of it.
        float[] offsets = { 0.0f, 25.0f, -25.0f, 50.0f, -50.0f, 75.0f, -75.0f };

        foreach (float offset in offsets)
        {
            var candidate = KaiFormation.StepBack(
                target, KaiFormation.Normalize(bearing + offset), 260.0f);

            if (KaiFormation.FarEnoughFrom(candidate, taken, KaiFormation.MinBotSpacing))
            {
                _sweepApproach[slot] = candidate;
                return candidate;
            }
        }

        var fallback = KaiFormation.StepBack(target, bearing, 260.0f);
        _sweepApproach[slot] = fallback;
        return fallback;
    }

    // ------------------------------------------------------------------
    // Per-tick
    // ------------------------------------------------------------------

    // Called every tick while the bomb is down, even when the director gave up
    // at plant time. If it bailed because no CT bots were alive then, and one
    // is alive now, it starts properly rather than staying idle for the round.
    public void Retry(float now, KaiPoint bombPos, KaiMapTactics map, float maxSpotDistance)
    {
        if (!Enabled || Phase != KaiRetakePhase.Idle)
        {
            return;
        }

        if (AliveBots(CsTeam.CounterTerrorist).Count == 0)
        {
            return;
        }

        KaiLog.Event(nameof(Retry),
            "CT bots are alive now but the director was idle, initialising late");

        OnBombPlanted(bombPos, map, maxSpotDistance);
    }

    public void Tick(
        float now,
        Func<int, KaiBotIntent> intentFor,
        object? botController,
        HashSet<int> supporting)
    {
        if (!Enabled || Phase == KaiRetakePhase.Idle)
        {
            return;
        }

        // Refreshed before any decision is made, because almost every branch
        // below has a cheaper option when there is nobody left to fight.
        EnemiesAlive = AliveCount(CsTeam.Terrorist);

        UpdatePhase(now);

        // Checked every tick, not just at the plant. A CT can respawn, finish
        // a takeover, or simply be the last one standing after the designated
        // defuser died, and without this the whole team stands and stares at
        // a bomb nobody is permitted to touch.
        EnsureDefuserAlive();

        foreach (var bot in AliveBots(CsTeam.CounterTerrorist))
        {
            var pawn = bot.PlayerPawn?.Value;
            var origin = pawn?.AbsOrigin;

            if (pawn == null || origin == null)
            {
                continue;
            }

            // Anything a bot can see counts, whoever it is and whatever it
            // was told to do. The defuser waiting at its standoff has eyes on
            // the site too.
            // No enemies left means nothing to sweep for. Checking corners
            // for an opponent who is already dead is pure clock.
            if (Phase == KaiRetakePhase.Inspect && EnemiesAlive > 0)
            {
                SweepOpportunistically(bot, origin);
            }

            // A bot already swinging onto a team mate's fight keeps doing
            // that. Sweeping a corner while somebody beside you is being shot
            // is worse than not sweeping at all, and the sweep resumes on its
            // own the moment the fight is over.
            // The defuser is never skipped, whatever else it is doing.
            //
            // Swinging onto a team mate's fight sets the supporting flag, and
            // skipping on it meant a defuser that saw an enemy mid-defuse
            // never reached its commitment at all. It came off the bomb to
            // take the duel, which is the one moment the commitment exists to
            // prevent: being shot at is not a reason to stop defusing, it is
            // the reason the rest of the side is on the site.
            if (supporting.Contains(bot.Slot) && bot.Slot != _defuserSlot)
            {
                KaiLog.Throttled($"supportskip:{bot.Slot}", nameof(Tick),
                    $"slot {bot.Slot} is supporting a fight, retake orders held", 2.0f);
                continue;
            }

            if (bot.Slot == _defuserSlot)
            {
                DriveDefuser(now, bot, origin, intentFor, botController);
            }
            else
            {
                // Note this runs in every phase including Commit. Covering
                // bots hold their angles right through the defuse, which is
                // the whole reason for clearing first.
                DriveClearer(now, bot, origin, intentFor, _map);
            }
        }
    }

    // Decide the phase from the time actually left on the bomb.
    //
    // The rule is explicit: inspect first, bait second, and only rush the
    // defuse when the clock no longer allows anything else. Every CT carries a
    // kit in this setup, so the defuse itself is five seconds and the budget
    // is generous, but the arithmetic still has to be done because a fake
    // defuse that loses the round is not clever.
    // Make sure somebody is still able to defuse.
    //
    // The defuser is chosen once, at the plant. If that bot then dies, every
    // remaining CT is a clearer, and clearers have USE suppressed in every
    // phase so that they never abandon their angle to help. The result is a
    // team that has cleared the site perfectly and then stands there while the
    // bomb goes off, because nobody left is allowed to touch it.
    private void EnsureDefuserAlive()
    {
        var cts = AliveBots(CsTeam.CounterTerrorist);

        if (cts.Count == 0)
        {
            return;
        }

        bool stillAlive = false;

        foreach (var bot in cts)
        {
            if (bot.Slot == _defuserSlot)
            {
                stillAlive = true;
                break;
            }
        }

        if (stillAlive)
        {
            return;
        }

        var replacement = PickDefuser(cts, _bombPos);

        if (replacement == null)
        {
            return;
        }

        int previous = _defuserSlot;
        _defuserSlot = replacement.Slot;

        // Whoever is promoted stops being a clearer, so their old angle is
        // released rather than left claimed by a bot now walking to the bomb.
        _clearAssignments.Remove(_defuserSlot);

        KaiLog.Event(nameof(EnsureDefuserAlive),
            $"defuser slot {previous} is dead, promoting slot {_defuserSlot} " +
            $"('{replacement.PlayerName}') kit={_defuserHasKit}");
    }

    private void UpdatePhase(float now)
    {
        var previous = Phase;

        int aliveTs = AliveCount(CsTeam.Terrorist);
        float elapsed = now - _plantTime;

        float defuseTime;

        if (_defuserHasKit)
        {
            defuseTime = DefuseWithKit;
        }
        else
        {
            defuseTime = DefuseWithoutKit;
        }

        float remaining = RemainingBombSeconds(now);

        // Seconds available for anything other than defusing.
        float spare = remaining - defuseTime - MustCommitReserve;

        // Is the gather still on? Only meaningful while already rallying:
        // nothing below ever re-enters Rally, so a released side stays
        // released. The three release triggers are, in order: enough of the
        // side is set on the ring, the rally has held as long as it may, or
        // the spare-time floor has been reached and gathering is no longer
        // affordable.
        int onRing = 0;
        int aliveCts = 0;
        bool rallying = Phase == KaiRetakePhase.Rally
                        && spare > RallySpareFloor
                        && elapsed < RallyMaxSeconds
                        && !RallyReady(out onRing, out aliveCts);

        // The inspect clock starts the moment the rally is over, or at the
        // plant when there was no rally. Every window below Rally measures
        // from here, because a window measured from the plant would have
        // been consumed by the gathering it now sits behind.
        if (!rallying && _inspectStartedAt <= 0.0f)
        {
            _inspectStartedAt = now;
        }

        float sinceInspect = _inspectStartedAt > 0.0f ? now - _inspectStartedAt : 0.0f;

        if (KaiBombState.IsBeingDefused())
        {
            // A defuse in progress ends the argument.
            //
            // Checked first, and unconditionally, because the phase machine
            // recomputes every tick and any branch that sent the bot back to
            // Inspect or Bait would take it off a bar that is already running.
            // Barely started is still started: the team mates are there to
            // take the fights, and a defuse abandoned at two seconds has cost
            // the round for nothing.
            Phase = KaiRetakePhase.Commit;
        }
        else if (remaining > 0.0f && spare <= 0.0f)
        {
            // No time on the clock. Rush it.
            Phase = KaiRetakePhase.Commit;
        }
        else if (aliveTs == 0)
        {
            // Nobody left to find. Nothing to inspect for, nobody to bait,
            // and nobody worth arriving in formation against.
            Phase = KaiRetakePhase.Commit;
        }
        else if (rallying)
        {
            Phase = KaiRetakePhase.Rally;

            KaiLog.Throttled("rally", nameof(UpdatePhase),
                $"rallying: {onRing} of {aliveCts} CT(s) on the ring, {elapsed:F1}s since " +
                $"the plant, {spare:F1}s spare against a floor of {RallySpareFloor:F0}", 2.0f);
        }
        else if (sinceInspect < InspectSeconds && !_sweepComplete)
        {
            // Still corners nobody has looked at, and time to look at them.
            // The sweep finishing early is what usually ends this phase now;
            // the timer is the backstop for a site the team cannot fully see.
            Phase = KaiRetakePhase.Inspect;
        }
        else if (_baitDone || (_fakeTapCount >= FakeTapsBeforeCommit && !_fakeHolding))
        {
            // The fake has been done. One tap is the whole idea: it makes the
            // defuse sound, which is what draws a lurker out of a corner the
            // sweep could not see into. Repeating it does not draw anybody
            // else out, it just spends bomb timer, and spending bomb timer is
            // how the side ended up committing with five seconds left.
            //
            // The !_fakeHolding term matters: the counter increments when the
            // hold STARTS, so without it the phase would flip mid-tap and cut
            // the sound short. Waiting for the release costs 700ms and is the
            // difference between a bluff a lurker can hear and a click.
            Phase = KaiRetakePhase.Commit;
        }
        else if (sinceInspect < InspectSeconds + BaitSeconds && spare > BaitSeconds)
        {
            // Either the site is swept and nobody was found, or the timer ran
            // out with corners still unchecked. Both are reasons to fake: the
            // first to draw out someone hiding where the sweep could not
            // reach, the second to draw out someone in a corner nobody saw.
            Phase = KaiRetakePhase.Bait;
        }
        else
        {
            Phase = KaiRetakePhase.Commit;
        }

        if (previous == KaiRetakePhase.Bait && Phase != KaiRetakePhase.Bait)
        {
            // Left Bait. Whatever happens to the tap counter from here, the
            // bluff has been made and the side does not go back to it.
            _baitDone = true;
        }

        if (Phase != previous)
        {
            KaiLog.Event(nameof(UpdatePhase),
                $"phase {previous} -> {Phase} (elapsed={elapsed:F1}s " +
                $"sinceInspect={sinceInspect:F1}s bombRemaining={remaining:F1}s " +
                $"defuseTime={defuseTime:F1}s spare={spare:F1}s aliveTs={aliveTs} " +
                $"swept={_clearedSpots.Count}/{_lurkSpots.Count} complete={_sweepComplete} " +
                $"taps={_fakeTapCount})");

            if (previous == KaiRetakePhase.Rally)
            {
                // The release. Everyone goes in on this tick, and everyone
                // goes in with a gun: any knife still out from the run is
                // put away here, including on bots that never made the ring,
                // because they are entering contested ground regardless.
                EndRallyKnives("rally released, going in together");

                RallyReady(out int setNow, out int aliveNow);

                KaiComms.Call((int)CsTeam.CounterTerrorist, _defuserSlot, "rallygo",
                    $"all in together, now: {setNow} of {aliveNow} on the ring", 10.0f);

                KaiLog.Event(nameof(UpdatePhase),
                    $"RALLY RELEASE after {elapsed:F1}s: {setNow} of {aliveNow} CT(s) set " +
                    $"on the ring, the whole retake enters the site at once. Contact support " +
                    $"stacks everyone onto the first fight from here.");
            }

            // Say the phase change out loud. Knowing the side has stopped
            // clearing and started baiting is the difference between reading
            // the round and watching it.
            if (Phase == KaiRetakePhase.Bait && previous == KaiRetakePhase.Inspect)
            {
                KaiComms.Call((int)CsTeam.CounterTerrorist, _defuserSlot, "phasebait",
                    $"{_clearedSpots.Count} of {_lurkSpots.Count} cleared, faking the defuse to "
                    + "draw the rest out", 10.0f);
            }
            else if (Phase == KaiRetakePhase.Commit)
            {
                KaiComms.Call((int)CsTeam.CounterTerrorist, _defuserSlot, "phasecommit",
                    $"going for the defuse, {EnemiesAlive} of them still up", 10.0f);
            }

            if (previous == KaiRetakePhase.Inspect)
            {
                var outstanding = UnclearedSpots();

                if (outstanding.Count == 0)
                {
                    KaiLog.Event(nameof(UpdatePhase),
                        $"inspection finished with every one of {_lurkSpots.Count} lurk spots seen");
                }
                else
                {
                    KaiLog.Event(nameof(UpdatePhase),
                        $"inspection ended with {outstanding.Count} lurk spot(s) never seen: " +
                        $"[{string.Join(",", outstanding)}]. Anyone hiding there is still there.",
                        KaiLogLevel.Error);
                }
            }
        }
    }

    // Is enough of the side set on the ring to go?
    //
    // Everyone alive on the CT side counts, defuser included: the defuser is
    // part of the hit, not a spectator to it. Set means within the hold
    // distance plus tolerance of the bomb, measured flat, the same way the
    // T-side staging measures readiness against the site.
    private bool RallyReady(out int onRing, out int aliveCts)
    {
        onRing = 0;
        aliveCts = 0;

        foreach (var bot in AliveBots(CsTeam.CounterTerrorist))
        {
            var origin = bot.PlayerPawn?.Value?.AbsOrigin;

            if (origin == null)
            {
                continue;
            }

            aliveCts++;

            if (_bombPos.DistanceXY(origin.X, origin.Y)
                <= RallyHoldDistance + RallyHoldTolerance)
            {
                onRing++;
            }
        }

        if (aliveCts == 0)
        {
            return true;
        }

        bool ready = onRing >= (int)MathF.Ceiling(aliveCts * RallyReadyFraction);

        if (ready)
        {
            KaiLog.Throttled("rallyready", nameof(RallyReady),
                $"{onRing} of {aliveCts} CT(s) set on the ring, that is enough to go", 2.0f);
        }

        return ready;
    }

    // Is this a one-man retake?
    //
    // Judged on whether anybody is actually assigned to sweep, not on the
    // headcount: four CTs with no clearing angles near this bomb leave the
    // defuser exactly as alone as one CT does.
    // Seconds until the running defuse completes, or -1 if none is running.
    //
    // m_flDefuseCountDown is the absolute game time the bar finishes, so the
    // remaining time is that minus now.
    private static float DefuseSecondsRemaining()
    {
        try
        {
            var c4 = Utilities
                .FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4")
                .FirstOrDefault(e => e.IsValid);

            if (c4 == null || !c4.BeingDefused)
            {
                return -1.0f;
            }

            float remaining = c4.DefuseCountDown - Server.CurrentTime;

            return remaining < 0.0f ? 0.0f : remaining;
        }
        catch
        {
            return -1.0f;
        }
    }

    // Living team mates close enough to actually be covering this bot.
    //
    // The distinction that matters for a commitment: somebody who can trade
    // for you is on the site, and somebody who is alive on the other side of
    // the map is not, however encouraging the headcount looks.
    private static int CountLivingMatesNear(int slot, Vector origin, float radius)
    {
        var self = Utilities.GetPlayerFromSlot(slot);

        if (self == null || !self.IsValid)
        {
            return 0;
        }

        int team = (int)self.TeamNum;
        int count = 0;

        foreach (var p in KaiPlayers.All())
        {
            if (p == null || !p.IsValid || p.Slot == slot || p.IsHLTV)
            {
                continue;
            }

            if (!p.PawnIsAlive || (int)p.TeamNum != team)
            {
                continue;
            }

            var mate = p.PlayerPawn?.Value?.AbsOrigin;

            if (mate == null)
            {
                continue;
            }

            float dx = mate.X - origin.X;
            float dy = mate.Y - origin.Y;

            if ((dx * dx) + (dy * dy) <= radius * radius)
            {
                count++;
            }
        }

        return count;
    }

    // How many living team mates this bot has, excluding itself.
    //
    // The test for whether a defuse is worth committing to: with somebody left
    // to fight for it the bomb comes first, and without one it does not.
    private static int CountLivingTeamMates(int slot)
    {
        int team = -1;
        var self = Utilities.GetPlayerFromSlot(slot);

        if (self == null || !self.IsValid)
        {
            return 0;
        }

        team = (int)self.TeamNum;

        int count = 0;

        foreach (var p in KaiPlayers.All())
        {
            if (p == null || !p.IsValid || p.Slot == slot || p.IsHLTV)
            {
                continue;
            }

            if (p.PawnIsAlive && (int)p.TeamNum == team)
            {
                count++;
            }
        }

        return count;
    }

    private bool IsSoloRetake()
    {
        return _clearAssignments.Count == 0;
    }

    // The lone defuser's routine. Returns true if it handled the bot.
    //
    // Runs instead of the team logic, not alongside it. Each stage ends on its
    // own terms rather than on the global inspection timer, because a single
    // bot doing four things in sequence needs a sequence, not a stopwatch.
    private bool DriveSoloDefuser(
        float now,
        CCSPlayerController bot,
        Vector origin,
        KaiBotIntent intent,
        object? botController)
    {
        float remaining = RemainingBombSeconds(now);

        float defuseTime;

        if (_defuserHasKit)
        {
            defuseTime = DefuseWithKit;
        }
        else
        {
            defuseTime = DefuseWithoutKit;
        }

        float spare = remaining - defuseTime - MustCommitReserve;

        // Nobody left to hide. Sweeping an empty site and baiting an audience
        // of nobody are both pure waste, so go straight to the bomb.
        if (EnemiesAlive == 0 && _soloStage != KaiSoloStage.Defuse)
        {
            KaiLog.Event(nameof(DriveSoloDefuser),
                $"slot {bot.Slot} is alone and no enemies are alive, skipping " +
                $"straight to the defuse");

            _soloStage = KaiSoloStage.Defuse;
        }

        // Out of clock. Whatever stage it was in, the bomb comes first.
        if (spare <= 0.0f && _soloStage != KaiSoloStage.Defuse)
        {
            KaiLog.Event(nameof(DriveSoloDefuser),
                $"slot {bot.Slot} out of time ({remaining:F1}s left, needs {defuseTime:F1}s), " +
                $"abandoning the {_soloStage} stage and defusing now");

            _soloStage = KaiSoloStage.Defuse;
        }

        switch (_soloStage)
        {
            case KaiSoloStage.Sweep:
                return SoloSweep(now, bot, origin, intent, spare);

            case KaiSoloStage.Tap:
                return SoloTap(now, bot, origin, intent, botController);

            case KaiSoloStage.Withdraw:
                return SoloWithdraw(now, bot, origin, intent);

            case KaiSoloStage.Listen:
                return SoloListen(now, bot, origin, intent);

            default:
                return SoloDefuse(now, bot, origin, intent);
        }
    }

    // Check a handful of the nearest spots, moving rather than pinned. How
    // many depends on what the clock can afford: each spot costs roughly its
    // dwell plus the walking, so a tight clock buys fewer of them.
    private bool SoloSweep(
        float now, CCSPlayerController bot, Vector origin, KaiBotIntent intent, float spare)
    {
        // How many spots the clock can afford, but never fewer than the
        // minimum unless there is genuinely no time.
        //
        // A lone retake that checks one corner and dives on the bomb is not a
        // retake, it is a coin flip. Three or four distinct clears is what a
        // person would do with a comfortable clock, so that is the floor
        // whenever the clock is comfortable.
        int affordable = (int)MathF.Floor(
            (spare - SoloTapSeconds - SoloListenSeconds) / (SoloSweepDwell * 2.0f));

        int target = Math.Clamp(affordable, 0, SoloSweepSpots);

        if (spare > SoloComfortableSeconds && target < SoloMinSweepSpots)
        {
            target = Math.Min(SoloMinSweepSpots, _lurkSpots.Count);
        }

        // Counted on what this bot has deliberately walked to and held, not on
        // the shared cleared tally. Otherwise a lone bot that happens to have
        // sight of the site from its entry point considers the whole thing
        // swept without moving, which is exactly the behaviour that made the
        // sweep invisible in the first place.
        if (target <= 0 || _lurkSpots.Count == 0 || _soloSwept >= target)
        {
            KaiLog.Event(nameof(SoloSweep),
                $"slot {bot.Slot} SWEEP DONE: {_soloSwept} of {target} spot(s) checked " +
                $"({_clearedSpots.Count}/{_lurkSpots.Count} of the site). Next: tap the bomb to " +
                $"sound like a defuse, then walk off it and listen.");

            _soloStage = KaiSoloStage.Tap;
            _soloStageUntil = 0.0f;
            return true;
        }

        intent.SuppressUse = true;

        // Nearest uncleared spot first. A lone bot has no time to cross the
        // site for a corner when there is one beside it.
        int chosen = -1;
        float bestDist = float.MaxValue;

        for (int index = 0; index < _lurkSpots.Count; index++)
        {
            // Personally swept, not merely tallied. A spot somebody else
            // glanced at is not one this bot has checked.
            if (_soloSweptSpots.Contains(index))
            {
                continue;
            }

            float d = _lurkSpots[index].DistanceXY(origin.X, origin.Y);

            if (d < bestDist)
            {
                bestDist = d;
                chosen = index;
            }
        }

        if (chosen < 0)
        {
            _soloStage = KaiSoloStage.Tap;
            return true;
        }

        var spot = _lurkSpots[chosen];
        var pawn = bot.PlayerPawn?.Value;

        if (pawn == null)
        {
            return true;
        }

        var eye = new Vector(origin.X, origin.Y, origin.Z + pawn.ViewOffset.Z);
        var aim = new Vector(spot.X, spot.Y, spot.Z + KaiHeights.Head);

        intent.Watch = new KaiPoint(spot.X, spot.Y, spot.Z + KaiHeights.Head);

        if (!KaiRayTraceBridge.CanSee(eye, aim))
        {
            if (_announcedApproach.Add((bot.Slot, chosen)))
            {
                KaiLog.Event(nameof(SoloSweep),
                    $"slot {bot.Slot} alone, MOVING to lurk spot {chosen} " +
                    $"({_soloSwept + 1} of {target}), {bestDist:F0} units away, expecting a fight");
            }

            intent.SteerTowards = ApproachPositionFor(bot.Slot, spot, origin);
            intent.SourceName = $"solo_sweep:{chosen}:closing";
            _soloStageUntil = now + SoloSweepDwell;
            return true;
        }

        intent.SourceName = $"solo_sweep:{chosen}";

        if (_soloStageUntil <= 0.0f)
        {
            _soloStageUntil = now + SoloSweepDwell;
        }

        if (now >= _soloStageUntil)
        {
            MarkCleared(chosen, bot.Slot);
            _soloSweptSpots.Add(chosen);
            _soloSwept++;
            _soloStageUntil = 0.0f;

            KaiLog.Event(nameof(SoloSweep),
                $"slot {bot.Slot} CLEARED lurk spot {chosen} alone " +
                $"({_soloSwept} of {target} it intends to check), moving to the next");

            KaiComms.Call((int)CsTeam.CounterTerrorist, bot.Slot, "soloclear",
                $"{KaiCallouts.Describe(spot, _bombPos)} clear, {_soloSwept} of {target}", 3.0f);
        }

        return true;
    }

    // One tap on the bomb. Long enough to start a defuse, which is what the Ts
    // hear, and released immediately.
    private bool SoloTap(
        float now, CCSPlayerController bot, Vector origin, KaiBotIntent intent,
        object? botController)
    {
        float range = MathF.Sqrt(_bombPos.DistanceSqr(origin.X, origin.Y, origin.Z));

        // Aim at the bomb the whole way in: the tap only registers if the bot
        // is looking at it, same as a real defuse.
        intent.Watch = new KaiPoint(_bombPos.X, _bombPos.Y, _bombPos.Z + 10.0f);
        intent.ForceAim = true;
        intent.SourceName = "solo_tap";

        if (range > 55.0f)
        {
            intent.SuppressUse = true;
            return true;
        }

        if (_soloStageUntil <= 0.0f)
        {
            intent.SuppressUse = false;

            if (botController != null)
            {
                long token = KaiBotControllerBridge.InjectUsercmd(
                    botController, bot.Slot, (ulong)PlayerButtons.Use,
                    (int)(SoloTapSeconds * 1000.0f));

                KaiLog.Event(nameof(SoloTap),
                    $"slot {bot.Slot} tapping the bomb for {SoloTapSeconds:F1}s to sound like a " +
                    $"defuse, injection token {token}");

                KaiComms.Call((int)CsTeam.CounterTerrorist, bot.Slot, "solotap",
                    "faking the defuse, backing off to listen", 10.0f);
            }

            _soloStageUntil = now + SoloTapSeconds;
            return true;
        }

        if (now >= _soloStageUntil)
        {
            // Withdraw straight back from the bomb, which is the direction
            // that puts the most ground between the bot and whoever comes to
            // stop it.
            float bearing = KaiFormation.Bearing(
                _bombPos.X, _bombPos.Y, origin.X, origin.Y);

            _soloWithdrawTo = KaiFormation.StepBack(
                new KaiPoint(origin.X, origin.Y, origin.Z), bearing, SoloWithdrawDistance);

            _soloStage = KaiSoloStage.Withdraw;
            _soloStageUntil = 0.0f;

            KaiLog.Event(nameof(SoloTap),
                $"slot {bot.Slot} released the tap, walking {SoloWithdrawDistance:F0} units off " +
                $"the bomb on bearing {bearing:F0}");
        }

        intent.SuppressUse = true;
        return true;
    }

    // Walk, do not run, off the bomb.
    //
    // The entire value of the pause is hearing somebody move. A bot that
    // sprints away spends those seconds drowning out the footsteps it is
    // supposed to be listening for, so this forces walk speed and clears the
    // run flag rather than simply steering.
    private bool SoloWithdraw(
        float now, CCSPlayerController bot, Vector origin, KaiBotIntent intent)
    {
        intent.SuppressUse = true;
        intent.Watch = new KaiPoint(_bombPos.X, _bombPos.Y, _bombPos.Z + KaiHeights.Chest);
        intent.SourceName = "solo_withdraw";

        if (_soloWithdrawTo == null)
        {
            _soloStage = KaiSoloStage.Listen;
            _soloStageUntil = now + SoloListenSeconds;
            return true;
        }

        float left = _soloWithdrawTo.DistanceXY(origin.X, origin.Y);

        if (left <= 30.0f)
        {
            _soloStage = KaiSoloStage.Listen;
            _soloStageUntil = now + SoloListenSeconds;

            KaiLog.Event(nameof(SoloWithdraw),
                $"slot {bot.Slot} is off the bomb, listening for {SoloListenSeconds:F1}s");

            return true;
        }

        intent.SteerTowards = _soloWithdrawTo;
        intent.Walk = true;

        KaiLog.Throttled($"solowithdraw:{bot.Slot}", nameof(SoloWithdraw),
            $"slot {bot.Slot} walking off the bomb, {left:F0} units to go", 1.0f);

        return true;
    }

    // Stand still and watch the bomb. Anyone who took the bait has to move.
    private bool SoloListen(
        float now, CCSPlayerController bot, Vector origin, KaiBotIntent intent)
    {
        intent.SuppressUse = true;
        intent.Anchored = true;
        intent.Watch = new KaiPoint(_bombPos.X, _bombPos.Y, _bombPos.Z + KaiHeights.Chest);
        intent.SourceName = "solo_listen";

        if (now >= _soloStageUntil)
        {
            _soloStage = KaiSoloStage.Defuse;

            KaiLog.Event(nameof(SoloListen),
                $"slot {bot.Slot} heard nothing, going back in to defuse " +
                $"({EnemiesAlive} enemies still alive)");

            KaiComms.Call((int)CsTeam.CounterTerrorist, bot.Slot, "solodefuse",
                "nothing moved, going for the defuse", 10.0f);
        }

        return true;
    }

    private bool SoloDefuse(
        float now, CCSPlayerController bot, Vector origin, KaiBotIntent intent)
    {
        float range = MathF.Sqrt(_bombPos.DistanceSqr(origin.X, origin.Y, origin.Z));

        // Deliberately no commitment pin here, unlike the team Commit. A lone
        // defuser has nobody to trade for it, so breaking off to fight is a
        // legitimate choice and the native AI is left free to make it.
        //
        // Released still means scrubbed, same rule as the team Commit: the
        // per-tick chain runs before this director, so anything upstream that
        // wrote movement or aim this tick would otherwise keep the wheel: a
        // stale route steered the released team defuser off the bomb, and a
        // lone defuser is exposed to exactly the same theft.
        intent.SteerTowards = null;
        intent.Anchored = false;
        intent.Watch = null;
        intent.ForceAim = false;
        intent.SuppressUse = false;
        intent.Walk = false;
        intent.Erratic = false;
        intent.SourceName = "defuse:native";

        KaiLog.Throttled($"solodefuse:{bot.Slot}", nameof(SoloDefuse),
            $"slot {bot.Slot} released to the native defuse logic from {range:F0} units, " +
            $"upstream overrides scrubbed", 3.0f);

        return true;
    }

    private void DriveDefuser(
        float now,
        CCSPlayerController bot,
        Vector origin,
        Func<int, KaiBotIntent> intentFor,
        object? botController)
    {
        // One man alone runs a different routine entirely. Checked before the
        // phase machine, because the phases describe a team's retake and none
        // of them describe what one bot should do on its own.
        if (IsSoloRetake())
        {
            var soloIntent = intentFor(bot.Slot);

            if (DriveSoloDefuser(now, bot, origin, soloIntent, botController))
            {
                return;
            }
        }

        if (Phase == KaiRetakePhase.Commit)
        {
            // Stick the defuse.
            //
            // Once the bar is actually running, a defuser with living team
            // mates must not come off it. Its job is the objective; the team
            // mates are perfectly able to fight whoever turns up, and a defuse
            // abandoned at three seconds has cost the round for nothing.
            //
            // This exists because the native AI will voluntarily break off:
            // ed0ard's BotState rolls a fake-defuse chance on bomb_begindefuse
            // and jumps the bot clear when it hits. That is sensible for a
            // lone CT baiting a lurker and wrong for a supported one, so the
            // bot is pinned for the duration instead.
            //
            // Alone is the exception, with an exception of its own. With
            // nobody left to trade for it, coming off the bomb to fight is a
            // legitimate choice, unless the defuse is within a second of
            // finishing: at that point the only way to lose it is to stop.
            // Cover is counted on the site, not across the map.
            //
            // "Surrounded by friends who can protect them" is the rule, and a
            // team mate holding an angle two hundred metres away protects
            // nobody. Team-wide counting meant a defuser alone on the site
            // committed anyway because somebody was alive in spawn.
            int covering = CountLivingMatesNear(bot.Slot, origin, DefenceRadius);
            bool covered = covering > 0;

            bool nearlyDone = DefuseSecondsRemaining() >= 0.0f
                              && DefuseSecondsRemaining() <= LastSecondCommitment;

            if (KaiBombState.IsBeingDefused() && (covered || nearlyDone))
            {
                var stick = intentFor(bot.Slot);

                stick.Anchored = true;
                stick.SuppressUse = false;
                stick.SourceName = "defusing:committed";

                // Nothing may walk it off the bomb. Anything upstream that set
                // a destination this tick, most likely the resupply logic
                // sending a dry bot for a gun, is overruled: the steering runs
                // before the pin in the movement hook, so leaving a stale
                // SteerTowards in place moves the bot regardless of the pin.
                stick.SteerTowards = null;
                stick.Walk = false;
                stick.Erratic = false;

                KaiComms.Call((int)CsTeam.CounterTerrorist, bot.Slot, "sticking", "on the bomb, cover me", 10.0f);

                KaiLog.Throttled(
                    $"stick:{bot.Slot}",
                    nameof(DriveDefuser),
                    $"slot {bot.Slot} is committed to the defuse: " +
                    $"{covering} team mate(s) on the site, " +
                    $"{DefuseSecondsRemaining():F1}s left on it" +
                    (nearlyDone && !covered ? " and alone but too close to stop" : ""),
                    2.0f);

                return;
            }

            // Hand the bot back completely and let the native AI defuse.
            //
            // Two earlier attempts got this wrong in opposite directions.
            // Releasing it first time did nothing because pre-aim was still
            // writing a horizontal watch target to CTs post-plant, so the bot
            // stood on the bomb looking at a corner. Driving it manually
            // second time aimed correctly but pinned it 56 units short: its
            // movement was zeroed every tick while DefuseBombState was trying
            // to walk it the last few units in.
            //
            // The native defuse logic already knows how to approach the bomb,
            // face it and hold USE. It was never the problem. The problem was
            // this plugin fighting it.
            //
            // The third wrong version was "Commit writes nothing at all", on
            // the belief that with pre-aim excluded post-plant nothing else
            // was left holding the wheel. Something was: the per-tick chain
            // runs BEFORE this director, and a rotation route that survived
            // the plant kept writing SteerTowards for the released defuser
            // every tick, walking it AWAY from the bomb while this branch
            // logged that native had it (mirage session, 2026-09-05: bomb ran
            // 20s down to 16s with beingDefused=False and the defuser mid-map
            // on 'rotate_s1_s0_04'). This director runs last precisely so it
            // can have the final word, so released now means SCRUBBED: every
            // movement and aim override written upstream this tick is erased,
            // and the native defuse logic truly is the only thing driving.
            var release = intentFor(bot.Slot);

            release.SteerTowards = null;
            release.Anchored = false;
            release.Watch = null;
            release.ForceAim = false;
            release.SuppressUse = false;
            release.Walk = false;
            release.Erratic = false;
            release.SourceName = "defuse:native";

            KaiLog.Throttled(
                $"commit:{bot.Slot}",
                nameof(DriveDefuser),
                $"slot {bot.Slot} released to the native defuse logic, upstream " +
                $"overrides scrubbed",
                3.0f);

            return;
        }

        var intent = intentFor(bot.Slot);

        // Gathering. The defuser rallies with everyone else, toward its
        // staging spot when one is authored, and enters the site on the
        // same release tick as its cover. A defuser that walks in early is
        // the exact bot the lurker is waiting for.
        if (Phase == KaiRetakePhase.Rally)
        {
            DriveRally(now, bot, origin, intent, _defuserStage);
            return;
        }

        if (Phase == KaiRetakePhase.Inspect)
        {
            intent.SuppressUse = true;

            // A staging spot the defuser has failed to reach for most of the
            // window is written off. The spot exists to give the inspect
            // phase a defuser with eyes on the site; a defuser spending the
            // whole phase 'enroute' has neither anchored nor inspected, and
            // the standoff below is a worse position that is at least
            // reachable. The forgotten path matters too: the follower would
            // otherwise keep walking the bot at the abandoned destination.
            //
            // Measured from when Inspect actually began, not from the plant:
            // with a rally in front of it, plant-relative timing would burn
            // the whole give-up budget on the gather.
            if (_defuserStage != null)
            {
                float stageDistSqr =
                    _defuserStage.Anchor.DistanceSqr(origin.X, origin.Y, origin.Z);

                float inspectRef = _inspectStartedAt > 0.0f ? _inspectStartedAt : _plantTime;

                if (stageDistSqr > ArriveRadius * ArriveRadius
                    && now - inspectRef > InspectSeconds * StageGiveUpFraction)
                {
                    KaiLog.Event(nameof(DriveDefuser),
                        $"slot {bot.Slot} has not reached stage spot '{_defuserStage.Name}' " +
                        $"with {StageGiveUpFraction:P0} of the inspect window gone, still " +
                        $"{MathF.Sqrt(stageDistSqr):F0} units out. Abandoning the spot for " +
                        $"the plain standoff.");

                    Pathing?.Forget(bot.Slot);
                    _defuserStage = null;
                }
            }

            if (_defuserStage != null)
            {
                float distSqr = _defuserStage.Anchor.DistanceSqr(origin.X, origin.Y, origin.Z);

                if (distSqr <= ArriveRadius * ArriveRadius)
                {
                    intent.Watch = _defuserStage.Watch;
                    intent.Anchored = true;
                    intent.Crouch = _defuserStage.Crouch;
                    intent.SourceName = $"stage:{_defuserStage.Name}";
                }
                else
                {
                    // Tell it to go there.
                    //
                    // This branch used to write a source name and nothing
                    // else, on the assumption that native pathing would carry
                    // the defuser to its staging spot. It did not: measured
                    // over a session, every hold-back line read
                    // 'stage:ctClear_020:enroute' and the bot never arrived,
                    // so it never anchored, never inspected, and was still
                    // wandering when the phase timer expired. That is the
                    // whole reason defuses were starting with five seconds on
                    // the clock.
                    //
                    // Same fault, and same fix, as DriveClearer had.
                    intent.Anchored = false;
                    intent.SourceName = $"stage:{_defuserStage.Name}:enroute";

                    var here = new KaiPoint(origin.X, origin.Y, origin.Z);
                    var to = new KaiPoint(
                        _defuserStage.Anchor.X,
                        _defuserStage.Anchor.Y,
                        _defuserStage.Anchor.Z);

                    bool steered = false;

                    if (Pathing != null)
                    {
                        steered = Pathing.Steer(
                            bot.Slot, here, to, now, intent, $"stage:{_defuserStage.Name}");
                    }

                    if (!steered)
                    {
                        intent.SteerTowards = to;
                    }
                }
            }
            else
            {
                // No authored stage. Walk in natively, stop inside the
                // standoff radius, and watch the bomb from there.
                float dist = MathF.Sqrt(_bombPos.DistanceSqr(origin.X, origin.Y, origin.Z));

                if (dist <= DefuserStandoff)
                {
                    intent.Watch = _bombPos;
                    intent.Anchored = true;
                    intent.SourceName = "standoff";
                }
                else
                {
                    intent.SourceName = "standoff:enroute";
                }
            }

            // The designated defuser inspects too while it waits. It is
            // standing there anyway, and an extra pair of eyes on the lurk
            // spots costs nothing.
            if (intent.Anchored && DriveInspection(now, bot, origin, intent))
            {
                intent.Anchored = true;
            }

            KaiLog.Throttled(
                $"defuser:{bot.Slot}",
                nameof(DriveDefuser),
                $"slot {bot.Slot} holding back, source='{intent.SourceName}' anchored={intent.Anchored}",
                3.0f);

            return;
        }

        // Bait. The defuser walks in for the fake.
        intent.Anchored = false;
        intent.SourceName = "bait";

        if (!FakeDefuseEnabled)
        {
            intent.SuppressUse = false;
            return;
        }

        intent.SuppressUse = true;

        // Steer it to the bomb rather than merely releasing it.
        //
        // "Released to walk in; native pathing takes it to the bomb" was the
        // old comment here, and the logs proved it wrong: across two full
        // playtest sessions the fake defuser produced thirty-five 'no tap
        // yet' lines and not one actual tap, closing on the bomb at walking-
        // wounded pace or not at all, because nothing native was actually
        // taking it there. The same steer the stage approach uses fixes it:
        // the follower carries the bot to the bomb along the graph, drops
        // the leg inside its own arrive radius, and the direct fallback
        // closes the last stretch into tap range.
        float toBomb = MathF.Sqrt(_bombPos.DistanceSqr(origin.X, origin.Y, origin.Z));

        if (toBomb > FakeTapRange)
        {
            var here = new KaiPoint(origin.X, origin.Y, origin.Z);
            var to = new KaiPoint(_bombPos.X, _bombPos.Y, _bombPos.Z);

            bool steered = false;

            if (Pathing != null)
            {
                steered = Pathing.Steer(bot.Slot, here, to, now, intent, "bait:walkin");
            }

            if (!steered)
            {
                intent.SteerTowards = to;
            }

            KaiLog.Throttled($"baitwalk:{bot.Slot}", nameof(DriveDefuser),
                $"slot {bot.Slot} walking in for the fake, {toBomb:F0} units from the bomb",
                2.0f);
        }

        DriveFakeDefuse(now, bot, origin, intent, botController);
    }

    // Chop USE into taps. Each hold that lands inside the defuse radius fires
    // bomb_begindefuse, and each release fires bomb_abortdefuse. To a T holding
    // an angle that is indistinguishable from a real defuse being interrupted.
    private void DriveFakeDefuse(
        float now,
        CCSPlayerController bot,
        Vector origin,
        KaiBotIntent intent,
        object? botController)
    {
        // Only meaningful once close enough that USE would start a defuse.
        // FakeTapRange sits a little inside the engine's 72 unit radius so a
        // marginal position does not produce a tap that fires nothing, and it
        // is the same constant the walk-in above steers against, so the two
        // agree about where "close enough" begins.
        float dist = MathF.Sqrt(_bombPos.DistanceSqr(origin.X, origin.Y, origin.Z));

        if (dist > FakeTapRange)
        {
            KaiLog.Throttled(
                $"fakefar:{bot.Slot}",
                nameof(DriveFakeDefuse),
                $"slot {bot.Slot} still {dist:F0} units from bomb, no tap yet",
                2.0f);
            return;
        }

        if (botController == null)
        {
            KaiLog.Throttled(
                "nofakeapi",
                nameof(DriveFakeDefuse),
                "BotController API unavailable, fake defuse cannot force USE",
                10.0f,
                KaiLogLevel.Error);
            return;
        }

        if (now < _nextFakeToggle)
        {
            return;
        }

        if (_fakeHolding)
        {
            // End of a hold. Let go and let the abort event fire.
            _fakeHolding = false;
            _nextFakeToggle = now + FakeGapSeconds;
            intent.SuppressUse = true;

            KaiLog.Event(
                nameof(DriveFakeDefuse),
                $"slot {bot.Slot} fake tap {_fakeTapCount} released, next hold in {FakeGapSeconds:F1}s");

            return;
        }

        // Start of a hold. Suppression must come off or the button clear in
        // the Update hook would immediately undo the injection.
        _fakeHolding = true;
        _fakeTapCount++;
        _nextFakeToggle = now + FakeHoldSeconds;
        intent.SuppressUse = false;

        // Same requirement as a real defuse: a tap only produces the
        // begindefuse the bait depends on if the bot is actually looking at
        // the bomb while it presses USE.
        intent.Watch = new KaiPoint(_bombPos.X, _bombPos.Y, _bombPos.Z + 10.0f);
        intent.ForceAim = true;

        int durationMs = (int)(FakeHoldSeconds * 1000.0f);

        long token = KaiBotControllerBridge.InjectUsercmd(
            botController, bot.Slot, (ulong)PlayerButtons.Use, durationMs);

        if (token > 0)
        {
            KaiLog.Event(
                nameof(DriveFakeDefuse),
                $"slot {bot.Slot} fake tap {_fakeTapCount} held {durationMs}ms, " +
                $"token {token}, dist={dist:F0}");
        }
        else
        {
            KaiLog.Event(
                nameof(DriveFakeDefuse),
                $"slot {bot.Slot} fake tap {_fakeTapCount} injection failed (token={token})",
                KaiLogLevel.Error);
        }
    }

    // Sweep the crosshair across everything visible from where this bot stands.
    //
    // Distinct from the beat sweep, which sends a bot walking to spots. This
    // is for a bot that has arrived and is covering: it flicks between the
    // angles in view, holding each briefly, and calls them as it goes.
    //
    // The scan set is built once per position and recomputed if the bot moves,
    // because the traces are the expensive part and a stationary bot's view
    // does not change.
    private bool ScanFromPosition(
        float now,
        CCSPlayerController bot,
        Vector origin,
        KaiBotIntent intent,
        KaiMapTactics map)
    {
        var pawn = bot.PlayerPawn?.Value;

        if (pawn == null || !pawn.IsValid || _bombPos == null)
        {
            return false;
        }

        var here = new KaiPoint(origin.X, origin.Y, origin.Z);

        // Rebuild if the bot has moved since the set was made.
        if (_scanFrom.TryGetValue(bot.Slot, out var anchoredAt)
            && anchoredAt.DistanceXY(origin.X, origin.Y) > 160.0f)
        {
            _scanSet.Remove(bot.Slot);
        }

        if (!_scanSet.TryGetValue(bot.Slot, out var visible))
        {
            visible = new List<KaiPoint>();

            var eye = new Vector(origin.X, origin.Y, origin.Z + pawn.ViewOffset.Z);

            foreach (var candidate in ScanCandidates(map))
            {
                float d = candidate.DistanceXY(origin.X, origin.Y);

                if (d < 250.0f || d > 2000.0f)
                {
                    continue;
                }

                var target = new Vector(
                    candidate.X, candidate.Y, candidate.Z + KaiHeights.Head);

                if (!KaiRayTraceBridge.CanSee(eye, target))
                {
                    continue;
                }

                // Keep them apart, or the sweep is four flicks at the same
                // doorway and looks like a twitch rather than a scan.
                if (!KaiFormation.FarEnoughFrom(candidate, visible, 350.0f))
                {
                    continue;
                }

                visible.Add(candidate);

                if (visible.Count >= ScanMaxAngles)
                {
                    break;
                }
            }

            _scanSet[bot.Slot] = visible;
            _scanFrom[bot.Slot] = here;
            _scanIndex[bot.Slot] = 0;
            _scanNext[bot.Slot] = now + ScanDwellSeconds;

            if (visible.Count > 0)
            {
                KaiLog.Event(nameof(ScanFromPosition),
                    $"slot {bot.Slot} covering from ({origin.X:F0},{origin.Y:F0}) with " +
                    $"{visible.Count} angle(s) in view, scanning between them");

                KaiComms.Call((int)CsTeam.CounterTerrorist, bot.Slot, $"scan:{bot.Slot}",
                    $"scanning {visible.Count} angles from " +
                    $"{KaiCallouts.Describe(here, _bombPos)}", 8.0f);
            }
        }

        if (visible.Count == 0)
        {
            return false;
        }

        // Settle on the angle covering the tracked human instead of cycling.
        //
        // The mirror of the plugin's glance-sweep settle bias, which its own
        // comment measured firing 69 times in 659 tracked seconds with none
        // of it during a retake. A bot flicking five angles every 0.9s is
        // looking at the right one a fifth of the time; a bot settled on the
        // doorway the human is behind is looking at it when they step out.
        // The candidates were all built with a line-of-sight trace, so this
        // watches a visible authored angle near the human, never the human
        // through a wall, and it moves nobody. The defuser is excluded by
        // construction (only clearers reach this function) and by the guard,
        // so nothing here can ever pull a crosshair off the bomb.
        var tracked = TrackedHumanTarget();

        if (tracked != null && bot.Slot != _defuserSlot)
        {
            KaiPoint? covering = null;
            float coveringGap = TrackedWatchRadius;

            foreach (var candidate in visible)
            {
                float gap = candidate.DistanceXY(tracked.X, tracked.Y);

                if (gap < coveringGap)
                {
                    coveringGap = gap;
                    covering = candidate;
                }
            }

            if (covering != null)
            {
                intent.Watch = new KaiPoint(
                    covering.X, covering.Y, covering.Z + KaiHeights.Head);
                intent.SourceName = "scan:tracked";

                KaiLog.Throttled($"scantracked:{bot.Slot}", nameof(ScanFromPosition),
                    $"slot {bot.Slot} has stopped cycling and settled on the angle " +
                    $"{coveringGap:F0} units from the tracked human", 4.0f);

                return true;
            }
        }

        if (!_scanIndex.TryGetValue(bot.Slot, out int cursor))
        {
            cursor = 0;
        }

        if (_scanNext.TryGetValue(bot.Slot, out float due) && now >= due)
        {
            // Call the angle just finished, then move on.
            var done = visible[cursor % visible.Count];

            KaiComms.Detail((int)CsTeam.CounterTerrorist, bot.Slot, $"scancall:{bot.Slot}:{cursor % visible.Count}",
                $"{KaiCallouts.Describe(done, _bombPos)} clear", 4.0f);

            cursor = (cursor + 1) % visible.Count;
            _scanIndex[bot.Slot] = cursor;
            _scanNext[bot.Slot] = now + ScanDwellSeconds;
        }

        var looking = visible[cursor % visible.Count];

        intent.Watch = new KaiPoint(looking.X, looking.Y, looking.Z + KaiHeights.Head);
        intent.SourceName = $"scan:{cursor + 1}of{visible.Count}";

        KaiLog.Throttled($"scanning:{bot.Slot}", nameof(ScanFromPosition),
            $"slot {bot.Slot} on angle {cursor + 1} of {visible.Count}, " +
            $"{KaiCallouts.Describe(looking, _bombPos)}", 2.0f);

        return true;
    }

    // Everything worth pointing a gun at near this bomb.
    private List<KaiPoint> ScanCandidates(KaiMapTactics map)
    {
        var list = new List<KaiPoint>(_lurkSpots);

        if (_bombPos == null)
        {
            return list;
        }

        foreach (var spot in map.PreAim)
        {
            if (spot.Trigger.DistanceXY(_bombPos.X, _bombPos.Y) <= HoldAngleMaxFromBomb)
            {
                list.Add(spot.Trigger);
            }
        }

        return list;
    }

    // Give a covering CT an angle to hold: far, distinct, and never the bomb.
    //
    // Three rules, all of which the previous version broke.
    //
    // Never the bomb. A bot watching the defuser is a bot watching the one
    // thing on the site that cannot hurt anybody, with its back to everything
    // that can. The defuser is covered by the angles being held, not by being
    // looked at.
    //
    // Never another bot's arc. Two CTs on the same doorway is one doorway
    // covered and one uncovered, and both die to the same player. Every angle
    // handed out has to sit clear of the ones already given.
    //
    // Far in preference to near. A distant corridor or doorway is where a
    // lurker has to come from; a spot ten feet away is one somebody would have
    // walked past already. Distance is the tie-break once separation is met.
    private KaiPoint? AssignHoldAngle(CCSPlayerController bot, Vector origin, KaiMapTactics map)
    {
        var pawn = bot.PlayerPawn?.Value;

        if (pawn == null || !pawn.IsValid || _bombPos == null)
        {
            return null;
        }

        // Sticky: an angle once taken is held, or the whole side reshuffles
        // its arcs every tick and nobody actually watches anything.
        //
        // One sanctioned exception. When the tracked human is known and the
        // held angle no longer covers where they actually are, holding it is
        // covering a theory while the real threat sets up somewhere else:
        // measured against a human on the T side, three clearers held angles
        // frozen at plant time while the contact list knew the human's exact
        // coordinates, and the defuser died to an approach nobody was
        // watching. So the angle is dropped and reselected below, where the
        // tracked bias picks the doorway they are behind. Rate limited so
        // the cover rotates on the human's moves, not on their strafing.
        if (_holdAngle.TryGetValue(bot.Slot, out var held))
        {
            var humanNow = TrackedHumanTarget();

            if (humanNow == null
                || held.DistanceXY(humanNow.X, humanNow.Y) <= TrackedWatchRadius)
            {
                return held;
            }

            float nowTime = Server.CurrentTime;
            float lastCheck = _holdRepointAt.GetValueOrDefault(bot.Slot, 0.0f);

            if (nowTime - lastCheck < TrackedRepointSeconds)
            {
                return held;
            }

            _holdRepointAt[bot.Slot] = nowTime;
            _holdAngle.Remove(bot.Slot);

            KaiLog.Event(nameof(AssignHoldAngle),
                $"slot {bot.Slot} drops its held angle at ({held.X:F0},{held.Y:F0}): the " +
                $"tracked human is {held.DistanceXY(humanNow.X, humanNow.Y):F0} units from " +
                $"it, outside the {TrackedWatchRadius:F0} covering radius. Reselecting.");
        }

        var eye = new Vector(origin.X, origin.Y, origin.Z + pawn.ViewOffset.Z);

        var takenBearings = new List<float>();

        foreach (var kv in _holdAngle)
        {
            if (kv.Key == bot.Slot)
            {
                continue;
            }

            var other = Utilities.GetPlayerFromSlot(kv.Key);
            var otherOrigin = other?.PlayerPawn?.Value?.AbsOrigin;

            if (otherOrigin == null)
            {
                continue;
            }

            takenBearings.Add(KaiFormation.Bearing(
                otherOrigin.X, otherOrigin.Y, kv.Value.X, kv.Value.Y));
        }

        KaiPoint? best = null;
        float bestDistance = -1.0f;

        // When the human's position is known, a candidate inside the
        // covering radius of it beats every merely-distant one, and the
        // nearest such candidate wins. The clash test still applies, so the
        // first clearer to pick claims the human's doorway and the rest
        // spread across the other approaches exactly as before; a known
        // threat gets one dedicated pair of eyes, not the whole rotation.
        var tracked = TrackedHumanTarget();
        KaiPoint? bestTracked = null;
        float bestTrackedGap = TrackedWatchRadius;

        // Candidates are the known lurk spots plus every learned duel angle
        // near the site: doorways, corridor ends, the places somebody has to
        // appear from.
        var candidates = new List<KaiPoint>(_lurkSpots);

        foreach (var spot in map.PreAim)
        {
            if (spot.Trigger.DistanceXY(_bombPos.X, _bombPos.Y) <= HoldAngleMaxFromBomb)
            {
                candidates.Add(spot.Trigger);
            }
        }

        foreach (var candidate in candidates)
        {
            float distance = candidate.DistanceXY(origin.X, origin.Y);

            // Too close to be an approach worth watching.
            if (distance < HoldAngleMinDistance)
            {
                continue;
            }

            var target = new Vector(
                candidate.X, candidate.Y, candidate.Z + KaiHeights.Head);

            if (!KaiRayTraceBridge.CanSee(eye, target))
            {
                continue;
            }

            float bearing = KaiFormation.Bearing(
                origin.X, origin.Y, candidate.X, candidate.Y);

            bool clashes = false;

            foreach (float taken in takenBearings)
            {
                if (KaiFormation.AngleGap(bearing, taken) < HoldAngleSeparationDeg)
                {
                    clashes = true;
                    break;
                }
            }

            if (clashes)
            {
                continue;
            }

            // Passed every test a normal candidate has to. Now score it both
            // ways: as an ordinary far angle, and as cover on the human.
            if (tracked != null)
            {
                float gap = candidate.DistanceXY(tracked.X, tracked.Y);

                if (gap < bestTrackedGap)
                {
                    bestTrackedGap = gap;
                    bestTracked = candidate;
                }
            }

            if (distance > bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        // The human-covering candidate outranks the merely-far one.
        bool humanBiased = false;

        if (bestTracked != null)
        {
            best = bestTracked;
            humanBiased = true;
        }

        if (best == null)
        {
            return null;
        }

        _holdAngle[bot.Slot] = best;

        KaiComms.Call((int)CsTeam.CounterTerrorist, bot.Slot, $"cover:{bot.Slot}",
            $"covering {KaiCallouts.Describe(best, _bombPos)}", 6.0f);

        KaiLog.Event(nameof(AssignHoldAngle),
            $"slot {bot.Slot} takes the angle at ({best.X:F0},{best.Y:F0}), " +
            $"{best.DistanceXY(origin.X, origin.Y):F0} units out on bearing " +
            $"{KaiFormation.Bearing(origin.X, origin.Y, best.X, best.Y):F0}, " +
            $"clear of {takenBearings.Count} team mate arc(s)" +
            (humanBiased
                ? $", chosen because it sits {bestTrackedGap:F0} units from the tracked " +
                  $"human. It watches their doorway, not the bomb."
                : ". It watches that, not the bomb."));

        return best;
    }

    // Drive one non-defusing CT.
    //
    // During Inspect every bot sweeps the known lurk spots, whether or not it
    // has an authored clearing angle, because checking for lurkers is the job
    // at that point and it is a job for the whole team. From Bait onwards a bot
    // with an authored angle settles onto it and holds it through the defuse,
    // which is the reason for clearing first.
    //
    // USE stays suppressed in every phase, so a covering bot never wanders off
    // its angle to go and help defuse.
    private void DriveClearer(
        float now,
        CCSPlayerController bot,
        Vector origin,
        Func<int, KaiBotIntent> intentFor,
        KaiMapTactics map)
    {
        var intent = intentFor(bot.Slot);
        intent.SuppressUse = true;

        _clearAssignments.TryGetValue(bot.Slot, out var spot);

        // Gathering. Everything else about this bot's retake waits until the
        // whole side goes in together.
        if (Phase == KaiRetakePhase.Rally)
        {
            DriveRally(now, bot, origin, intent, spot);
            return;
        }

        if (Phase == KaiRetakePhase.Inspect)
        {
            if (DriveInspection(now, bot, origin, intent))
            {
                // Pin only once it has arrived somewhere sensible, otherwise
                // let native pathing keep bringing it onto the site while it
                // sweeps with its eyes.
                if (spot != null
                    && spot.Anchor.DistanceSqr(origin.X, origin.Y, origin.Z)
                       <= ArriveRadius * ArriveRadius)
                {
                    intent.Anchored = true;
                }

                return;
            }

            // Nothing learned to sweep yet. Fall through to the angle.
        }

        // Demote a spot the ring cannot reach. Rally has returned above and
        // Inspect keeps its beats, so only Bait and Commit arrive here with a
        // spot still set; a post further out than RingRadiusMax at that point
        // is a clearer walking away from the defuse it is meant to be
        // guarding. Nulling the stored assignment as well means this fires
        // once, and the very next block fabricates a ring post in its place.
        //
        // AssignClearers now refuses to hand out spots beyond
        // ClearSpotMaxFromBomb, so this mostly exists as a belt for whatever
        // slips past that: the Retry path, hand-authored data added later, or
        // a tunable someone widens without reading this far.
        if (spot != null
            && Phase != KaiRetakePhase.Inspect
            && spot.Anchor.DistanceXY(_bombPos.X, _bombPos.Y) > RingRadiusMax)
        {
            KaiLog.Event(nameof(DriveClearer),
                $"slot {bot.Slot} demoted from '{spot.Name}': " +
                $"{spot.Anchor.DistanceXY(_bombPos.X, _bombPos.Y):F0} units from the bomb " +
                $"is outside the {RingRadiusMax:F0} ring. Taking a computed post instead.");

            _clearAssignments[bot.Slot] = null;
            spot = null;
        }

        if (spot == null)
        {
            // A spotless clearer used to be left entirely stock from here,
            // which is the pile-on-the-bomb bug wearing a new phase: with
            // USE suppressed, native post-plant logic walked these bots to
            // the bomb to defuse, the suppression blocked the defuse, and
            // they simply stood on it, stacked together, covering nothing.
            // AssignClearers fixed exactly this for the Inspect sweep and
            // the fix stopped one phase short.
            //
            // During Inspect the beats are the job and this stays as it was.
            // From Bait onward the bot is given a computed ring post: a real
            // recorded position in the donut around the bomb, spread by
            // bearing from every post already taken, facing outwards. The
            // circle of cover the defuser was supposed to have.
            if (Phase == KaiRetakePhase.Inspect)
            {
                return;
            }

            spot = AssignRingPost(bot, origin, map);

            if (spot == null)
            {
                return;
            }
        }

        float distSqr = spot.Anchor.DistanceSqr(origin.X, origin.Y, origin.Z);

        if (distSqr > ArriveRadius * ArriveRadius)
        {
            // Explicitly clear the anchor rather than merely not setting it.
            // An earlier stage in the same tick can have set it, and silently
            // inheriting that froze bots mid-approach while the log claimed
            // they were still walking.
            intent.Anchored = false;
            intent.SourceName = $"clear:{spot.Name}:enroute";

            // Tell it to go there.
            //
            // Nothing here ever did. The anchor was cleared, a source name
            // was written, a line was logged saying the bot was en route, and
            // then the function returned having issued no movement command of
            // any kind, on the assumption that native pathing would carry the
            // bot onto the site by itself. It did not: of 23 measured approach
            // runs, 18 finished further from the assigned spot than they
            // started, several by more than 1000 units, while the log
            // cheerfully reported them en route the whole time. That is why
            // inspection ended with most of the site never swept in 17 rounds,
            // and why the defuse watchdog fired in 9.
            var destination = new KaiPoint(spot.Anchor.X, spot.Anchor.Y, spot.Anchor.Z);
            var here = new KaiPoint(origin.X, origin.Y, origin.Z);

            bool steered = false;

            if (Pathing != null)
            {
                steered = Pathing.Steer(
                    bot.Slot, here, destination, now, intent, $"clear:{spot.Name}");
            }

            if (!steered)
            {
                // Either there is no follower, or the follower counts the bot
                // as arrived while this function does not. The two measure
                // differently on purpose: the follower works horizontally,
                // this works in three dimensions, so on stairs they can
                // disagree by the height of the step. Whichever it is, the
                // bot still needs telling to move, and the last stretch is
                // short enough for a straight line to be safe.
                intent.SteerTowards = destination;

                KaiLog.Throttled($"clearnopath:{bot.Slot}", nameof(DriveClearer),
                    $"slot {bot.Slot} closing the last of the way to '{spot.Name}' " +
                    $"directly", 5.0f);
            }

            KaiLog.Throttled($"clearwalk:{bot.Slot}", nameof(DriveClearer),
                $"slot {bot.Slot} en route to '{spot.Name}', {MathF.Sqrt(distSqr):F0} units out", 2.0f);

            return;
        }

        // Standing on it. Drop the path so the next assignment solves fresh.
        Pathing?.Forget(bot.Slot);

        intent.Anchored = true;
        intent.Crouch = spot.Crouch;

        // The human is close. Watch the fact, not the theory.
        //
        // A pinned bot's view cone is wherever its assigned angle points,
        // and native vision only acquires what falls inside the cone: with
        // the human's exact position in hand, bots were still stared at
        // 'for quite some time' before reacting, because their forced watch
        // faced somewhere else entirely and native eyes never got the
        // chance. Inside TrackedThreatRange the assigned angle yields and
        // the cone goes to the human's actual position, so the moment they
        // peek they are already in view. Attention only, as everywhere
        // else: the trigger stays native.
        var threat = TrackedHumanTarget();

        if (threat != null
            && bot.Slot != _defuserSlot
            && threat.DistanceXY(origin.X, origin.Y) <= TrackedThreatRange)
        {
            intent.Watch = new KaiPoint(threat.X, threat.Y, threat.Z + KaiHeights.Chest);
            intent.SourceName = "cover:threat";

            KaiLog.Throttled($"coverthreat:{bot.Slot}", nameof(DriveClearer),
                $"slot {bot.Slot} watching the tracked human directly, " +
                $"{threat.DistanceXY(origin.X, origin.Y):F0} units away, assigned angle " +
                $"yields to the live threat", 3.0f);

            return;
        }

        // While the site is being inspected, scan rather than lock on.
        //
        // A bot holding one angle through the whole clear looks like, and is,
        // a bot doing nothing. Sweeping between the angles it can see from
        // where it stands is what clearing a site actually looks like from the
        // outside, and it announces each one so the sequence is followable.
        if (Phase == KaiRetakePhase.Inspect
            && ScanFromPosition(now, bot, origin, intent, map))
        {
            return;
        }

        // Hold a distinct distant angle in preference to the authored one.
        //
        // The authored watch point is whatever the learner recorded for this
        // spot, with no regard for what anybody else is covering. Two bots on
        // adjacent authored spots will happily stare down the same doorway
        // while another goes unwatched. AssignHoldAngle picks something far
        // out, visible, and clear of every team mate's arc instead, and never
        // the bomb.
        var angle = AssignHoldAngle(bot, origin, map);

        if (angle != null)
        {
            intent.Watch = new KaiPoint(angle.X, angle.Y, angle.Z + KaiHeights.Head);
            intent.SourceName = $"cover:{spot.Name}";
        }
        else
        {
            intent.Watch = spot.Watch;
            intent.SourceName = $"clear:{spot.Name}";
        }

        KaiLog.Throttled($"clearhold:{bot.Slot}", nameof(DriveClearer),
            $"slot {bot.Slot} covering from '{spot.Name}' through phase {Phase}, " +
            $"source={intent.SourceName}", 3.0f);
    }

    // Fabricate a defensive ring post for a clearer with no authored spot.
    //
    // The post is a real recorded position (a lurk spot or a learned duel
    // angle near the site, the same pool the scans use), not free geometry,
    // so it is somewhere a player has actually stood rather than a point
    // inside a crate. Selection maximises the smallest bearing gap, around
    // the bomb, to every position already taken by the defence, which is
    // what spreads the side around the clock face; linear spacing and the
    // donut bounds are hard filters. The watch point faces outwards, away
    // from the bomb, because the threat arrives from outside the ring and
    // the one direction guaranteed to hold nothing dangerous is inward.
    //
    // The result goes into _clearAssignments, so from the next tick this
    // bot is driven by exactly the same enroute, arrive, and cover code as
    // a bot with an authored spot, hold-angle bias and threat override
    // included. One assignment path, two sources of spots.
    private KaiHoldSpot? AssignRingPost(CCSPlayerController bot, Vector origin, KaiMapTactics map)
    {
        if (_ringPostFailed.Contains(bot.Slot))
        {
            return null;
        }

        // Everything the defence already occupies, for spacing and spread.
        var takenPositions = new List<KaiPoint>();

        foreach (var assigned in _clearAssignments.Values)
        {
            if (assigned != null)
            {
                takenPositions.Add(assigned.Anchor);
            }
        }

        if (_defuserStage != null)
        {
            takenPositions.Add(_defuserStage.Anchor);
        }

        KaiPoint? best = null;
        float bestGap = -1.0f;
        float bestBotDist = float.MaxValue;
        bool bestMeetsGap = false;

        foreach (var candidate in ScanCandidates(map))
        {
            float fromBomb = candidate.DistanceXY(_bombPos.X, _bombPos.Y);

            if (fromBomb < RingRadiusMin || fromBomb > RingRadiusMax)
            {
                continue;
            }

            if (!KaiFormation.FarEnoughFrom(candidate, takenPositions, MinPostSpacing))
            {
                continue;
            }

            // Smallest bearing gap, around the bomb, to anything taken. An
            // empty defence means every candidate is equally spread; call
            // that a full half circle.
            float candidateBearing = KaiFormation.Bearing(
                _bombPos.X, _bombPos.Y, candidate.X, candidate.Y);

            float minGap = 180.0f;

            foreach (var takenPos in takenPositions)
            {
                float takenBearing = KaiFormation.Bearing(
                    _bombPos.X, _bombPos.Y, takenPos.X, takenPos.Y);

                float gap = MathF.Abs(candidateBearing - takenBearing);

                if (gap > 180.0f)
                {
                    gap = 360.0f - gap;
                }

                if (gap < minGap)
                {
                    minGap = gap;
                }
            }

            float botDist = candidate.DistanceXY(origin.X, origin.Y);

            // Spread first, walk second. A candidate clearing the angular
            // floor always beats one that does not, whatever their gaps;
            // within the same tier the widest slice of unowned clock face
            // wins, and the nearer candidate breaks ties within a few
            // degrees of each other. The floor only bends when nothing on
            // the map can meet it, because a cramped ring still beats a
            // pile.
            bool meetsGap = minGap >= RingMinAngularGap;
            bool better;

            if (meetsGap && !bestMeetsGap)
            {
                better = true;
            }
            else if (!meetsGap && bestMeetsGap)
            {
                better = false;
            }
            else if (minGap > bestGap + 5.0f)
            {
                better = true;
            }
            else if (minGap > bestGap - 5.0f && botDist < bestBotDist)
            {
                better = true;
            }
            else
            {
                better = false;
            }

            if (better)
            {
                bestGap = minGap;
                bestBotDist = botDist;
                bestMeetsGap = meetsGap;
                best = candidate;
            }
        }

        if (best == null)
        {
            _ringPostFailed.Add(bot.Slot);

            KaiLog.Event(nameof(AssignRingPost),
                $"no ring post available for slot {bot.Slot}: nothing recorded in the " +
                $"{RingRadiusMin:F0}-{RingRadiusMax:F0} donut clears the spacing tests. " +
                $"The bot stays stock for this plant.",
                KaiLogLevel.Error);

            return null;
        }

        // Face outwards: from the bomb, through the post, and beyond.
        float outBearing = KaiFormation.Bearing(_bombPos.X, _bombPos.Y, best.X, best.Y);
        float radians = outBearing * MathF.PI / 180.0f;

        var watch = new KaiPoint(
            best.X + (MathF.Cos(radians) * 500.0f),
            best.Y + (MathF.Sin(radians) * 500.0f),
            best.Z + KaiHeights.Chest);

        _ringPostCount++;

        var post = new KaiHoldSpot
        {
            Name = $"ring_{_ringPostCount:D2}",
            Team = (int)CsTeam.CounterTerrorist,
            Anchor = new KaiPoint(best.X, best.Y, best.Z),
            Watch = watch,
            Stage = false,
        };

        _clearAssignments[bot.Slot] = post;

        KaiLog.Event(nameof(AssignRingPost),
            $"slot {bot.Slot} takes computed post '{post.Name}' at " +
            $"({best.X:F0},{best.Y:F0}), {best.DistanceXY(_bombPos.X, _bombPos.Y):F0} units " +
            $"from the bomb on bearing {outBearing:F0}, {bestGap:F0} degrees clear of the " +
            $"nearest taken post, facing outwards. The ring closes around the defuse.");

        return post;
    }

    // Bring one CT to the rally ring, fast.    //
    // Two states. Off the ring: run flat out at the destination, knife out
    // for the movement speed while the leg is long and nothing is shooting,
    // eyes down the direction of travel. On the ring: stop, gun out, face
    // the site, and wait for the release. The release itself is a phase
    // change, so every bot leaves the ring on the same tick, which is the
    // entire point: the first T seen after that is looked at by several
    // guns at once instead of meeting the side one bot at a time.
    //
    // The knife rule mirrors the rotation sprint's: contact ends it
    // immediately, because a knife is not an answer to somebody shooting,
    // and the last stretch inside RallyKnifeMinDistance is run with the gun
    // up because that is where somebody might actually be waiting.
    private void DriveRally(
        float now,
        CCSPlayerController bot,
        Vector origin,
        KaiBotIntent intent,
        KaiHoldSpot? spot)
    {
        intent.SuppressUse = true;

        float toBomb = _bombPos.DistanceXY(origin.X, origin.Y);

        if (toBomb <= RallyHoldDistance)
        {
            // Set. Gun out, hold the ring, watch the site.
            RallyKnife(bot, false);
            Pathing?.Forget(bot.Slot);

            intent.Anchored = true;
            intent.Walk = false;
            intent.Crouch = false;
            intent.Watch = new KaiPoint(
                _bombPos.X, _bombPos.Y, _bombPos.Z + KaiHeights.Chest);
            intent.SourceName = "rally:set";

            KaiLog.Throttled($"rallyhold:{bot.Slot}", nameof(DriveRally),
                $"slot {bot.Slot} set on the ring, {toBomb:F0} units from the bomb, " +
                $"waiting for the release", 3.0f);

            return;
        }

        // Still closing. Sprint, and knife when the leg justifies it.
        var pawn = bot.PlayerPawn?.Value;
        var nativeBot = pawn?.Bot;
        bool contact = nativeBot != null
                       && (nativeBot.IsEnemyVisible || nativeBot.IsAttacking);

        RallyKnife(bot, !contact && toBomb > RallyKnifeMinDistance);

        intent.Anchored = false;
        intent.Walk = false;
        intent.Crouch = false;
        intent.SourceName = "rally:run";

        var here = new KaiPoint(origin.X, origin.Y, origin.Z);

        KaiPoint destination;

        if (spot != null)
        {
            destination = new KaiPoint(spot.Anchor.X, spot.Anchor.Y, spot.Anchor.Z);
        }
        else
        {
            destination = new KaiPoint(_bombPos.X, _bombPos.Y, _bombPos.Z);
        }

        bool steered = false;

        if (Pathing != null)
        {
            steered = Pathing.Steer(bot.Slot, here, destination, now, intent, "rally");
        }

        if (!steered)
        {
            intent.SteerTowards = destination;
        }

        // Eyes forward while running: at the immediate steer target rather
        // than at corners it is passing, same choice the rotation sprint
        // makes. The ring hold above is where careful looking resumes.
        var ahead = intent.SteerTowards;

        if (ahead != null)
        {
            intent.Watch = new KaiPoint(ahead.X, ahead.Y, ahead.Z + KaiHeights.Chest);
        }

        KaiLog.Throttled($"rallyrun:{bot.Slot}", nameof(DriveRally),
            $"slot {bot.Slot} running to the ring, {toBomb:F0} units from the bomb, " +
            $"knife={_rallyKnife.Contains(bot.Slot)}", 2.0f);
    }

    // Put a knife in this bot's hands, or take it back out, for the rally.
    //
    // A dry bot already knifing under the arsenal's management is left
    // alone in both directions: its weapon state is the arsenal's business
    // and the rally must not fight it over which slot is selected.
    private void RallyKnife(CCSPlayerController bot, bool wantKnife)
    {
        if (wantKnife)
        {
            if (IsKnifing != null && IsKnifing(bot.Slot))
            {
                return;
            }

            bool first = _rallyKnife.Add(bot.Slot);

            try
            {
                bot.ExecuteClientCommand("slot3");
            }
            catch (Exception ex)
            {
                KaiLog.Throttled($"rallyknife:{bot.Slot}", nameof(RallyKnife),
                    $"could not switch slot {bot.Slot} to the knife: {ex.Message}",
                    30.0f, KaiLogLevel.Error);
            }

            if (first)
            {
                KaiLog.Event(nameof(RallyKnife),
                    $"slot {bot.Slot} has the knife out for the rally run");
            }

            return;
        }

        if (!_rallyKnife.Remove(bot.Slot))
        {
            return;
        }

        RestoreGun(bot);

        KaiLog.Event(nameof(RallyKnife),
            $"slot {bot.Slot} run over, gun back out");
    }

    // Give this bot its gun back, preferring the plugin's inventory-aware
    // restore over a blind slot switch.
    private void RestoreGun(CCSPlayerController bot)
    {
        if (IsKnifing != null && IsKnifing(bot.Slot))
        {
            // The arsenal wants this bot knifing (it is dry). Leave it.
            return;
        }

        try
        {
            if (RestoreWeapon != null)
            {
                RestoreWeapon(bot);
            }
            else
            {
                bot.ExecuteClientCommand("slot1");
            }
        }
        catch (Exception ex)
        {
            KaiLog.Throttled($"rallygun:{bot.Slot}", nameof(RestoreGun),
                $"could not restore slot {bot.Slot}'s gun: {ex.Message}",
                30.0f, KaiLogLevel.Error);
        }
    }

    // Every rally knife goes away. Called on the release, so the side
    // enters shooting, and on reset, so a round end never leaves a bot
    // knifing into the next engagement.
    private void EndRallyKnives(string why)
    {
        if (_rallyKnife.Count == 0)
        {
            return;
        }

        int restored = 0;

        foreach (int slot in _rallyKnife)
        {
            var p = Utilities.GetPlayerFromSlot(slot);

            if (p == null || !p.IsValid || !p.PawnIsAlive)
            {
                continue;
            }

            RestoreGun(p);
            restored++;
        }

        _rallyKnife.Clear();

        KaiLog.Event(nameof(EndRallyKnives),
            $"{restored} rally knife(s) put away ({why})");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    // Where the tracked human is, if the CT side is entitled to know.
    //
    // Thin wrapper over the plugin's TrackedTargetFor, which owns the three
    // gate checks: handicap on, position fresh, asking team is the enemy of
    // the human. This class is CT-only machinery, so the asking team is
    // always the CTs, mirroring the T-sector fan hardcoding its own side on
    // the other half of the plugin.
    private KaiPoint? TrackedHumanTarget()
    {
        if (TrackedEnemy == null)
        {
            return null;
        }

        return TrackedEnemy((int)CsTeam.CounterTerrorist);
    }

    private float RemainingBombSeconds(float now)
    {
        try
        {
            var c4 = Utilities
                .FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4")
                .FirstOrDefault(e => e.IsValid);

            if (c4 == null)
            {
                return -1.0f;
            }

            float remaining = c4.C4Blow - now;

            KaiLog.Throttled(
                "bombtime",
                nameof(RemainingBombSeconds),
                $"bomb has {remaining:F1}s left, beingDefused={c4.BeingDefused}",
                2.0f);

            return remaining;
        }
        catch (Exception ex)
        {
            KaiLog.Throttled("bombtimeerr", nameof(RemainingBombSeconds),
                $"could not read planted_c4: {ex.Message}", 10.0f, KaiLogLevel.Error);
            return -1.0f;
        }
    }

    private static List<CCSPlayerController> AliveBots(CsTeam team)
    {
        return KaiPlayers.All()
            .Where(p => p != null && p.IsValid && p.IsBot && !p.IsHLTV
                        && p.PawnIsAlive && !p.HasBeenControlledByPlayerThisRound
                        && (int)p.TeamNum == (int)team)
            .ToList();
    }

    // Everyone alive on a team, humans included. The "site is clear" test has
    // to count you as well as the bots.
    private static int AliveCount(CsTeam team)
    {
        return KaiPlayers.All()
            .Count(p => p != null && p.IsValid && !p.IsHLTV && p.PawnIsAlive
                        && (int)p.TeamNum == (int)team);
    }

    private static bool HasDefuser(CCSPlayerPawn pawn)
    {
        try
        {
            if (pawn.ItemServices == null || pawn.ItemServices.Handle == nint.Zero)
            {
                return false;
            }

            return new CCSPlayer_ItemServices(pawn.ItemServices.Handle).HasDefuser;
        }
        catch (Exception ex)
        {
            KaiLog.Event(nameof(HasDefuser), $"could not read defuser flag: {ex.Message}",
                KaiLogLevel.Error);
            return false;
        }
    }
}

// Questions about the planted bomb that more than one class needs to ask.
//
// Kept in one place rather than answered separately in the director and the
// plugin: two implementations of "is it being defused" is two things to get
// wrong, and they are read on the same tick by code that has to agree.
internal static class KaiBombState
{
    public static bool IsBeingDefused()
    {
        try
        {
            var c4 = Utilities
                .FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4")
                .FirstOrDefault(e => e.IsValid);

            if (c4 == null)
            {
                return false;
            }

            return c4.BeingDefused;
        }
        catch (Exception ex)
        {
            KaiLog.Throttled("defusecheck", nameof(IsBeingDefused),
                $"could not read planted_c4: {ex.Message}", 10.0f, KaiLogLevel.Error);
            return false;
        }
    }
}

// Geometry for spreading bots out and putting their backs to walls.
//
// TWO PROBLEMS THIS SOLVES
//
// First, every learned spot is a place somebody DIED. That is exactly why the
// angle is worth watching, and exactly why the position itself is often bad:
// people die standing in the open. Backing away from the watch direction until
// a wall stops you converts "where someone got shot" into "where someone could
// have held that angle from", which is what was wanted in the first place.
//
// Second, bots given the same selection rule all make the same choice, so five
// defenders converge on one spot and cover one angle between them. Handing
// each bot an evenly spaced bearing around the objective and letting it choose
// only within its own arc forces the coverage to fan out. The spacing is
// literally 360/N, so the formation is symmetric by construction rather than
// by luck.
internal static class KaiFormation
{
    // Compass bearing in degrees from one point to another, in the same
    // convention as eye yaw.
    public static float Bearing(float fromX, float fromY, float toX, float toY)
    {
        return MathF.Atan2(toY - fromY, toX - fromX) * 180.0f / MathF.PI;
    }

    // Smallest angle between two bearings, 0..180.
    public static float AngleGap(float a, float b)
    {
        float d = MathF.Abs((a - b) % 360.0f);

        if (d > 180.0f)
        {
            d = 360.0f - d;
        }

        return d;
    }

    public static float Normalize(float degrees)
    {
        float a = degrees % 360.0f;

        if (a > 180.0f)
        {
            a -= 360.0f;
        }
        else if (a < -180.0f)
        {
            a += 360.0f;
        }

        return a;
    }

    // Give every bot an evenly spaced bearing around a centre point.
    //
    // Sorted by slot so the assignment is stable from tick to tick and from
    // round to round: a bot keeps its arc rather than swapping with a
    // neighbour every time the dictionary happens to enumerate differently.
    //
    // baseBearing anchors the whole fan. Passing the bearing of the most
    // dangerous approach means one bot always faces it squarely and the rest
    // spread symmetrically either side, rather than the fan landing at an
    // arbitrary rotation.
    public static Dictionary<int, float> AssignSectors(List<int> slots, float baseBearing)
    {
        var result = new Dictionary<int, float>();

        if (slots.Count == 0)
        {
            return result;
        }

        var ordered = new List<int>(slots);
        ordered.Sort();

        float step = 360.0f / ordered.Count;

        for (int i = 0; i < ordered.Count; i++)
        {
            result[ordered[i]] = Normalize(baseBearing + (i * step));
        }

        return result;
    }

    // Minimum gap between two bots' standing positions.
    //
    // This is not tidiness. Two bots inside one another's spacing occupy the
    // same piece of cover, are killed by the same spray transfer, and cover
    // one angle between them instead of two. 200 units is roughly three
    // player widths, far enough that a burst aimed at one does not walk onto
    // the other.
    public const float MinBotSpacing = 200.0f;

    // Is this candidate position far enough from every position already
    // handed out? Horizontal only: two bots on different floors of Nuke are
    // not crowding each other however close they look from above.
    public static bool FarEnoughFrom(
        KaiPoint candidate, IEnumerable<KaiPoint> taken, float minSpacing)
    {
        foreach (var other in taken)
        {
            if (other == null)
            {
                continue;
            }

            if (candidate.DistanceXY(other.X, other.Y) < minSpacing
                && MathF.Abs(candidate.Z - other.Z) < 100.0f)
            {
                return false;
            }
        }

        return true;
    }

    // Step back from a point along a bearing, used to find somewhere to stand
    // that can see a spot without standing on top of it.
    public static KaiPoint StepBack(KaiPoint from, float bearingDegrees, float distance)
    {
        float rad = bearingDegrees * MathF.PI / 180.0f;

        return new KaiPoint(
            from.X + (MathF.Cos(rad) * distance),
            from.Y + (MathF.Sin(rad) * distance),
            from.Z);
    }

    // Walk backwards from a position, away from what it is watching, until a
    // wall stops you, and return a standing spot just in front of that wall.
    //
    // Returns the original position unchanged when there is nothing to back
    // into within maxBack, or when backing up would lose sight of the very
    // thing being watched, which would trade a bad position for a useless one.
    public static KaiPoint BackToCover(
        KaiPoint anchor,
        float eyeHeight,
        KaiPoint watch,
        float maxBack,
        float wallStandoff)
    {
        float dx = anchor.X - watch.X;
        float dy = anchor.Y - watch.Y;
        float len = MathF.Sqrt((dx * dx) + (dy * dy));

        if (len < 1.0f)
        {
            return anchor;
        }

        dx /= len;
        dy /= len;

        var eye = new Vector(anchor.X, anchor.Y, anchor.Z + eyeHeight);
        var back = new Vector(
            anchor.X + (dx * maxBack),
            anchor.Y + (dy * maxBack),
            anchor.Z + eyeHeight);

        float fraction = KaiRayTraceBridge.TraceFraction(eye, back);

        // Nothing behind within range. Leave the position alone rather than
        // shoving the bot an arbitrary distance into open ground.
        if (fraction >= 0.999f)
        {
            return anchor;
        }

        float travel = (fraction * maxBack) - wallStandoff;

        if (travel <= 16.0f)
        {
            // Already against something.
            return anchor;
        }

        var candidate = new KaiPoint(
            anchor.X + (dx * travel),
            anchor.Y + (dy * travel),
            anchor.Z);

        // Backing off must not cost the angle.
        var candidateEye = new Vector(candidate.X, candidate.Y, candidate.Z + eyeHeight);
        var target = new Vector(watch.X, watch.Y, watch.Z);

        if (!KaiRayTraceBridge.CanSee(candidateEye, target))
        {
            return anchor;
        }

        return candidate;
    }
}

// How this plugin enumerates players.
//
// Deliberately NOT KaiPlayers.All(). That helper filters on
// controller.Connected != PlayerConnectedState.Connected, and bots are fake
// clients that never report that state, so it silently returns humans only.
// On a 5v5 against bots it returned exactly one controller, which meant every
// bot-driving path in this plugin was iterating an empty list and doing
// nothing, all session, without a single error.
//
// FindAllEntitiesByDesignerName walks the entity list instead and has no
// connection filter. It is what ed0ard's NadeSystem and BotBuy use, and those
// are the two plugins in his stack that successfully drive bots.
//
// The learner was unaffected and kept working throughout, because it takes its
// controllers straight off the death event rather than enumerating.
internal static class KaiPlayers
{
    public static List<CCSPlayerController> All()
    {
        try
        {
            return Utilities
                .FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
                .Where(p => p != null && p.IsValid)
                .ToList();
        }
        catch (Exception ex)
        {
            KaiLog.Throttled("playerscan", nameof(All),
                $"could not enumerate controllers: {ex.Message}", 10.0f, KaiLogLevel.Error);
            return new List<CCSPlayerController>();
        }
    }
}

// Dumps the exact state of every connected player.
//
// Added because "no alive CT bots" kept firing during a 5v5, which is a
// contradiction: either the filters disagree with reality or one of the
// properties they read is not what it appears to be. Rather than guess which,
// this prints every field the filters test, for every player, at the moment
// the decision is made.
internal static class KaiCensus
{
    public static void Dump(string context)
    {
        try
        {
            var players = KaiPlayers.All();

            int bots = 0;
            int aliveBots = 0;

            foreach (var p in players)
            {
                if (p == null || !p.IsValid)
                {
                    KaiLog.Event(nameof(Dump), $"[{context}] <invalid controller>");
                    continue;
                }

                bool isBot = p.IsBot;
                bool alive = p.PawnIsAlive;

                if (isBot)
                {
                    bots++;
                }

                if (isBot && alive)
                {
                    aliveBots++;
                }

                KaiLog.Event(nameof(Dump),
                    $"[{context}] slot={p.Slot} '{p.PlayerName}' team={(int)p.TeamNum} " +
                    $"isBot={isBot} alive={alive} hltv={p.IsHLTV} " +
                    $"controlledThisRound={p.HasBeenControlledByPlayerThisRound} " +
                    $"pawnValid={p.PlayerPawn?.Value?.IsValid == true}");
            }

            KaiLog.Event(nameof(Dump),
                $"[{context}] totals: {players.Count} controllers, {bots} bots, {aliveBots} alive bots");
        }
        catch (Exception ex)
        {
            KaiLog.Event(nameof(Dump), $"[{context}] census failed: {ex.Message}", KaiLogLevel.Error);
        }
    }
}

// Isolates the optional BotControllerApi types so a missing
// BotControllerApi.dll produces a null capability rather than a type load
// failure at JIT time. Same pattern ed0ard uses in BotState.
internal static class KaiBotControllerBridge
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object? TryGet()
    {
        var capability =
            new CounterStrikeSharp.API.Core.Capabilities.PluginCapability<
                BotControllerApi.IBotControllerApi>("botcontroller:api");

        return capability.Get();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long InjectUsercmd(object api, int slot, ulong buttonMask, int durationMs)
    {
        return ((BotControllerApi.IBotControllerApi)api).InjectUsercmd(slot, buttonMask, durationMs);
    }
}

// Line-of-sight tests through the RayTrace metamod plugin that ships with
// CS2-Bot-Improver. Isolated behind NoInlining for the same reason as the
// BotController bridge: a missing RayTraceApi.dll then produces a null
// capability rather than a type load failure at JIT time.
//
// This is the same capability, the same trace call and the same clear-fraction
// threshold that ed0ard's BotAimImprover uses to decide whether it can see a
// body part, so behaviour is consistent with the rest of the stack.
internal static class KaiRayTraceBridge
{
    private static readonly CounterStrikeSharp.API.Core.Capabilities.PluginCapability<
        RayTraceAPI.CRayTraceInterface> Capability = new("raytrace:craytraceinterface");

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool Available()
    {
        try
        {
            return Capability.Get() != null;
        }
        catch
        {
            return false;
        }
    }

    // True if nothing solid sits between the two points.
    //
    // MASK_WORLD_ONLY ignores players deliberately: a teammate standing in the
    // way is not a reason to reject a holding position, and will move.
    //
    // Returns true when RayTrace is unavailable, so a missing plugin degrades
    // to the previous behaviour rather than silently switching every check off.
    // How far along the ray the first solid thing sits, 0..1. 1 means clear.
    // CanSee only answers yes or no, which is useless for finding a wall to
    // put your back against.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static float TraceFraction(Vector from, Vector to)
    {
        try
        {
            var rt = Capability.Get();

            if (rt == null)
            {
                return 1.0f;
            }

            var options = new RayTraceAPI.TraceOptions(
                RayTraceAPI.InteractionLayers.MASK_WORLD_ONLY);

            rt.TraceEndShape(from, to, null, options, out RayTraceAPI.TraceResult result);

            return result.Fraction;
        }
        catch (Exception ex)
        {
            KaiLog.Throttled("tracefrac_err", nameof(TraceFraction),
                $"trace failed: {ex.Message}", 10.0f, KaiLogLevel.Error);
            return 1.0f;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool CanSee(Vector from, Vector to)
    {
        try
        {
            var rt = Capability.Get();

            if (rt == null)
            {
                return true;
            }

            var options = new RayTraceAPI.TraceOptions(
                RayTraceAPI.InteractionLayers.MASK_WORLD_ONLY);

            rt.TraceEndShape(from, to, null, options, out RayTraceAPI.TraceResult result);

            return result.Fraction >= 0.999f;
        }
        catch (Exception ex)
        {
            KaiLog.Throttled("raytrace_err", nameof(CanSee),
                $"trace failed: {ex.Message}", 10.0f, KaiLogLevel.Error);
            return true;
        }
    }
}
