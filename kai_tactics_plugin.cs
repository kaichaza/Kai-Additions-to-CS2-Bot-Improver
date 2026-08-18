// kai_tactics_plugin.cs
//
// KAI ADDITION. Not part of CS2-Bot-Improver.
// Version 0.3.0, schema version 2.
//
// Layers four behaviours on top of ed0ard's CS2-Bot-Improver stack without
// forking or replacing any of it:
//
//   1. T post-plant holds. Authored anchors near the bomb with authored watch
//      angles, instead of the native "hide somewhere nearby" behaviour.
//
//   2. T rotation under fire. A T that takes damage on its hold, or whose
//      neighbour dies, abandons the spot. If a safer authored spot is free it
//      is reassigned there; otherwise the native AI takes the fight. New in
//      v2. Without this a pinned bot stands still while being shot, which is
//      worse than the stock behaviour it replaced.
//
//   3. CT retake discipline. See kai_retake_director.cs. Clear the site as a
//      team, fake defuse to make the Ts show themselves, then commit, with
//      the covering bots holding their angles right through the defuse.
//
//   4. Pre-aim. While a bot has no visible enemy, its crosshair is pulled onto
//      an authored world point as it walks through an authored trigger volume.
//
// WHICH EXTENSION SEAM AND WHY
//
//   Seam A  per-tick writes to CCSBot schema fields, as his BotState does.
//           Cheap and version independent, but a race: whichever plugin's
//           OnTick listener runs last wins. Worse for aiming, since
//           CCSBot::UpdateLookAround recomputes m_lookYaw and m_lookPitch
//           inside Upkeep AFTER tick listeners run, so anything written there
//           is discarded before use.
//
//   Seam B  signature hooks on native bot functions, as his BotAimImprover
//           does with PickNewAimSpot. Correctly ordered by construction and
//           immune to listener ordering. Both overrides here use Seam B. The
//           tick listener only decides intent; it never writes bot state.
//
//   Seam C  the BotController native API. LockKind.All freezes CCSBot::Update,
//           where vision and target acquisition live, so a bot locked that way
//           is blind and will not shoot. Only InjectUsercmd is used here, for
//           the fake defuse taps.
//
// SIGNATURES
//
// CCSBot::UpdateLookAngles and CCSBot::Update are read out of the gamedata
// shipped with his BotController metamod plugin rather than hardcoded here, so
// when he updates for a new CS2 build this follows automatically.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace KaiBotTactics;

[MinimumApiVersion(305)]
public class KaiBotTacticsPlugin : BasePlugin
{
    public override string ModuleName => "KaiBotTactics";
    public override string ModuleVersion => "1.33.0";
    public override string ModuleAuthor => "kai";
    public override string ModuleDescription =>
        "Learned pre-aim, T post-plant holds with rotation, and CT retake discipline";

    // ------------------------------------------------------------------
    // Tunables
    // ------------------------------------------------------------------

    // How close a bot must get to its anchor before it counts as arrived.
    private const float AnchorArriveRadius = 90.0f;

    // Spots further than this from the planted bomb are ignored, so one file
    // covers both sites with no site-name matching anywhere.
    private const float MaxSpotDistanceFromBomb = 2200.0f;

    // An intent older than this is treated as stale by the native hooks.
    private const float IntentStaleSeconds = 0.25f;

    // How long a T stays off its hold after being shot or losing a neighbour.
    private float _pressureSeconds = 6.0f;

    // A teammate dying within this range counts as pressure on your position.
    private float _pressureRadius = 750.0f;

    // How long after a noise, a shot taken, or a lost enemy the plugin keeps
    // its hands off the bot's view, and how long it actively turns towards
    // a threat. See TryThreatAim.
    private float _yieldSeconds = 3.0f;

    // Only noises that travelled less than this are treated as local enough to
    // turn towards. Gunfire across the map should not drag a holding bot off
    // its angle; footsteps in the next room should.
    private float _noiseRange = 1500.0f;

    // CT bots before the plant hold the learned angle they walk into rather
    // than passing through it. This is what makes a defence sedentary: the
    // pre-aim data already says where the duels happen, so a CT standing in
    // one of those spots facing the right way is doing its job.
    private bool _pinCtOnPreAim = true;

    // Post-plant, a T with no authored spot still stops rather than wandering,
    // as long as it is this close to the bomb. Its aim is left to the native
    // approach-point watching, which ed0ard's Vision_AlwaysWatchApproachPoints
    // patch already makes decent.
    private float _tHoldNearBombRadius = 1600.0f;

    // Where the enemy is expected to come from, derived at plant time. Every
    // defending T is given a different one of these to watch.
    private readonly List<KaiPoint> _threatPoints = new();

    // slot -> index into _threatPoints. Sticky for the round so a bot does not
    // flip between angles tick to tick, and exclusive so no two defenders
    // cover the same approach.
    private readonly Dictionary<int, int> _tWatchClaims = new();

    // slot -> the compass bearing from the bomb that this defender owns.
    // Evenly spaced, so five defenders cover five arcs 72 degrees apart
    // instead of all picking whichever approach happens to be nearest.
    private readonly Dictionary<int, float> _tSectors = new();

    // slot -> the position a defender has settled on after backing into cover.
    // Cached because the trace only needs doing once per bot per round, and
    // because a position that keeps being recomputed is a position the bot
    // never actually reaches.
    private readonly Dictionary<int, KaiPoint> _tCover = new();

    // How far a bot will back away from a learned spot looking for a wall.
    private float _coverBackDistance = 320.0f;

    // How far in front of the wall to stop.
    private float _coverWallStandoff = 44.0f;

    // Whether to back learned positions into cover at all.
    private bool _coverSeeking = true;

    // Whether a planter with cover nearby is pinned through the plant.
    private bool _stickThePlant = true;

    // How near a team mate has to be to count as covering the plant.
    private float _siteMateRadius = 1200.0f;

    // slot -> when it started arming, for the log.
    private readonly Dictionary<int, float> _plantCommittedSince = new();

    // Minimum angle between two defenders' watch directions.
    //
    // Worth being straight about the geometry: 120 degrees between EVERY pair
    // is only satisfiable for three defenders, because three is the most
    // directions that fit around a circle at that spacing. With more than
    // three the best achievable minimum separation is 360/N, so the
    // requirement is applied as min(this, 360/N) and degrades to an even fan
    // rather than silently failing to place anybody.
    private float _watchSeparationDeg = 120.0f;

    // How far away a pre-aim spot still counts towards a position's coverage
    // score. Beyond this a bot could technically trace to it but would not
    // reliably pick a player out of it.
    private float _coverageRange = 1600.0f;

    // How long the crosshair rests on each covered spot during a glance
    // sweep. Short: the point is to be looking at every angle often, not to
    // stare at any one of them.
    private float _glanceDwell = 0.4f;

    // slot -> the pre-aim spot indices visible from its held position, and
    // where it is in the cycle.
    private readonly Dictionary<int, List<int>> _glanceSet = new();

    // The moving equivalent of the glance sweep: which angles a walking bot
    // can currently see, and where it is in the cycle. Rescanned as it moves,
    // because unlike a held position the view changes with every step.
    private readonly Dictionary<int, List<int>> _transitSet = new();
    private readonly Dictionary<int, int> _transitIndex = new();
    private readonly Dictionary<int, float> _transitNext = new();
    private readonly Dictionary<int, float> _transitFlick = new();

    // slot -> the last named place it reported being at, so a position report
    // goes out when the place changes rather than on a timer.
    private readonly Dictionary<int, string> _lastReported = new();

    // Faster than the stationary sweep: a bot crossing a site has less time on
    // each angle and more of them to get through before it arrives.
    private float _transitRescanSeconds = 0.6f;
    private float _transitFlickSeconds = 0.28f;

    // Half-angle of the forward arc a moving bot clears. Seventy degrees each
    // side is a wide but forward-facing cone: enough to check a doorway coming
    // up on the flank, not enough to end up looking at ground already walked
    // through. Wider than about ninety and a bot can be aiming behind itself
    // while moving forwards, which is where this started.
    private float _transitArcDeg = 70.0f;

    // How far off its direction of travel a moving bot may aim before it is
    // overruled. Eighty degrees keeps a flank doorway in play while making it
    // impossible to be looking behind while running forwards.
    private const float MaxLookBehindDeg = 80.0f;

    // How far down the route to look when working out which way the journey is
    // going, in waypoints and in units. Whichever is reached first.
    private int _headingLookahead = 4;
    private float _headingLookaheadUnits = 320.0f;

    // Speed squared above which a bot counts as moving rather than shuffling.
    // 40 units a second, well under walking pace.
    private float _movingSpeedSqr = 1600.0f;

    // Slow to a walk when a known duel angle is this close.
    //
    // Running is loud and it outruns reaction time: a bot sprinting past a
    // corner is three steps beyond it before it can act on what was there.
    // Clearing an angle properly means being able to stop on it.
    private bool _walkNearAngles = true;
    private float _walkNearDistance = 500.0f;
    private readonly Dictionary<int, int> _glanceIndex = new();
    private readonly Dictionary<int, float> _glanceNext = new();

    // slot -> the compass bearing this defender is watching along, so the
    // separation rule has something to compare against.
    private readonly Dictionary<int, float> _tWatchBearings = new();

    // slot -> arc around the loose bomb, and the position that bot has taken.
    // Same fan as the post-plant defence: without it every CT solves the same
    // "find any sightline" problem and they all solve it the same way.
    private readonly Dictionary<int, float> _guardSectors = new();
    private readonly Dictionary<int, KaiPoint> _guardPositions = new();

    // Which approaches a guarding bot cycles, and where it is in that cycle.
    // Index zero of the rotation is always the bomb itself.
    private readonly Dictionary<int, List<int>> _guardSet = new();
    private readonly Dictionary<int, int> _guardIndex = new();
    private readonly Dictionary<int, float> _guardFlick = new();

    private bool _guardSweepAngles = true;

    // A guard flicking between fifteen angles is watching none of them long
    // enough to react to anything.
    private int _guardSweepMax = 4;

    // slot -> zone bearing for pre-plant CT defence, measured from the centre
    // of the learned play area. On a normal bomb map the geography does the
    // rest: evenly spaced bearings from the middle land on the separate sites
    // and the routes between them.
    private readonly Dictionary<int, float> _ctZones = new();
    private KaiPoint? _mapCentre;
    private float _nextZoneRefresh;

    // ------------------------------------------------------------------
    // Crossfire support
    //
    // Pre-aiming exists to spot an enemy early. Spotting one and then leaving
    // the team mate next to you to fight it alone throws that away. When a bot
    // opens up on somebody, every friendly nearby that is not already in a
    // fight of its own swings onto the same target, so the enemy is taking
    // fire from several directions at once instead of duelling each bot in
    // turn.
    //
    // The support fire only points bots at the contact. The moment a
    // supporting bot can see the enemy itself, the aim hook hands straight
    // back to the native aim and BotAimImprover, so the duel is fought by the
    // code that is good at duels.
    // ------------------------------------------------------------------

    private sealed class KaiContact
    {
        public KaiPoint Position = new();

        // The team the contact belongs to, so friends of the shooter respond
        // and the contact itself does not.
        public int EnemyTeam;

        // Which bot reported it, for the log.
        public int ReportedBy;

        public float Stamp;
    }

    private readonly List<KaiContact> _contacts = new();

    // How far from a contact a bot will still swing onto it. Beyond this it is
    // somebody else's fight and abandoning an angle to look at it just opens a
    // hole in the defence.
    private float _supportRadius = 1100.0f;

    // How long a contact stays worth swinging at after it was last seen.
    private float _supportSeconds = 2.5f;

    private bool _supportFire = true;

    // ------------------------------------------------------------------
    // Loose bomb guard
    //
    // Everything else in this plugin keys off the plant, which left the whole
    // pre-plant round untouched. That is a real gap: once the carrier dies and
    // the bomb is lying on the ground, the Ts have to come back to it, so it
    // is the single most valuable thing on the map for a CT to be covering.
    // Stock bots ignore that completely and wander off taking map control that
    // no longer matters.
    //
    // So while the bomb is loose, any CT bot already near it stops wandering
    // and holds. Deliberately scoped to CTs already in the area: bots
    // elsewhere carry on as normal, because this plugin cannot path anyone
    // and dragging distant bots into a hold they can never walk to would just
    // freeze them where they stand.
    // ------------------------------------------------------------------

    private bool _guardLooseBomb = true;

    // A CT inside this range of the loose bomb switches to guarding it.
    private float _guardRadius = 1400.0f;

    // Once inside this range the bot stops moving entirely. Between this and
    // the guard radius it keeps walking in under native pathing.
    private float _guardHoldRadius = 900.0f;

    // Require an unobstructed line from the bot's eye to the bomb before it is
    // allowed to hold. This is what turns guarding from a guess into a
    // guarantee: a CT that can see the bomb will see anyone who comes to pick
    // it up, and a CT that cannot see it is guarding nothing.
    private bool _guardRequireLineOfSight = true;

    // How far a bot will shuffle sideways looking for a sightline to the bomb.
    // Short on purpose: this is steering, not pathfinding, so it has no
    // obstacle avoidance and will walk into a corner if allowed to go far.
    private float _guardSeekRange = 350.0f;

    // Give up seeking after this long and just hold position. Prevents a bot
    // grinding against geometry for the whole round.
    private float _guardSeekSeconds = 5.0f;

    // slot -> server time its current seek attempt started.
    private readonly Dictionary<int, float> _guardSeekStart = new();

    // Height above the bomb to aim at, so the crosshair sits at the chest of
    // whoever crouches or stands over it rather than on the floor.
    private const float GuardAimHeight = KaiHeights.BombWatch;

    // Where the loose bomb is, or null when carried, planted or gone.
    private KaiPoint? _looseBombPos;

    // Throttle for the entity scan, which does not need to run every tick.
    private float _nextLooseBombScan;

    // ------------------------------------------------------------------
    // State
    // ------------------------------------------------------------------

    private readonly Dictionary<int, KaiBotIntent> _intents = new();

    // slot -> server time until which this T is considered under fire.
    private readonly Dictionary<int, float> _tPressureUntil = new();

    private readonly KaiRetakeDirector _retake = new();
    private readonly KaiSpotLearner _learner = new();

    // Records where bots walk, building a navigation graph out of it. See
    // kai_breadcrumbs.cs. Independent of everything else: it only observes.
    private readonly KaiBreadcrumbs _crumbs = new();

    // Pre-solves the best holding positions on a map. See kai_solver.cs.
    private readonly KaiSolver _solver = new();

    // Static routes, loaded once per map. See kai_routes.cs.
    private KaiRouteBook _routes = new();

    // A live A* graph over the breadcrumbs, for paths the static route book
    // does not contain. Routes are precomputed between fixed endpoints;
    // converging on a dropped bomb needs a path from wherever each bot happens
    // to be to wherever the bomb happens to have landed, which is a different
    // problem and cannot be answered from a fixed book.
    private KaiRouteGraph? _liveGraph;
    private int _liveGraphNodes;

    // Where the bomb was when the side was last sent to it, so a bomb that
    // moves can be noticed and the ring re-formed around the new position.
    private KaiPoint? _convergeAnchor;
    private float _nextConvergeCheck;

    // slot -> the route it is currently running and how far along it is.
    private readonly Dictionary<int, KaiRoute> _routeOf = new();
    private readonly Dictionary<int, int> _routeLeg = new();

    // slot -> true if this bot is faking: it will turn round partway and go
    // back the way it came.
    private readonly Dictionary<int, bool> _routeFaking = new();
    private readonly Dictionary<int, bool> _routeReversing = new();

    // Spawn positions, averaged from where bots stand when a round goes live.
    private readonly Dictionary<string, KaiPoint> _spawns = new();
    private bool _spawnsSampledThisRound;

    // ------------------------------------------------------------------
    // Fakes
    //
    // Two different deceptions, previously conflated into one per-bot coin
    // flip that was neither.
    //
    // A FAKE ROTATION is a whole-team action. The side rotates together, is
    // heard rotating, and then turns round together and comes back. One bot
    // doing that on its own is not a fake, it is a bot changing its mind, and
    // the enemy has no reason to commit to anything on the strength of it.
    // The turn can come at any point, including after arriving at the far
    // site, which is the most convincing version because the enemy has had
    // time to see them there.
    //
    // A FAKE EXECUTE is the opposite shape: one or two bots WITHOUT the bomb
    // hit a site the team is not taking, make contact, fire, and then leave to
    // join the real execute elsewhere. The point is not to win that fight but
    // to be seen and heard losing it in the wrong place.
    // ------------------------------------------------------------------

    private sealed class KaiTeamRotation
    {
        public int Team;
        public int ToSite;
        public bool IsFake;

        // How far along the route the team turns round. Drawn per rotation, up
        // to and including 1.0, which means arriving before turning back.
        public float ReverseAt;

        public bool Reversing;
        public readonly HashSet<int> Members = new();
    }

    private KaiTeamRotation? _rotation;

    // Chance that a team rotation is a fake.
    private float _fakeRotateChance = 0.35f;

    // slot -> the site this bot is faking an execute on, and when it must
    // leave for the real one.
    private readonly Dictionary<int, int> _decoySite = new();
    private readonly Dictionary<int, float> _decoyUntil = new();
    private readonly HashSet<int> _decoyEngaged = new();

    // The site the team is actually taking this round.
    private int _realTargetSite = -1;

    // How many bots are sent to fake, and how long they stay once they have
    // made contact.
    private int _decoyCount = 2;
    private float _decoyLingerSeconds = 3.0f;

    // Longest a decoy will wait for contact before giving up and leaving.
    private float _decoyPatienceSeconds = 18.0f;

    private bool _useRoutes = true;
    private readonly Random _routeRandom = new();

    // Calls the plays and the audibles. See kai_playbook.cs.
    private readonly KaiTacticalController _tactics = new();

    // Leaders, carrier reading and the synchronised execute. See
    // kai_command.cs.
    private readonly KaiCommand _command = new();

    // How far through learning this map is. See kai_maturity.cs. Everything
    // that records anything checks this before writing.
    private readonly KaiMapMaturity _maturity = new();

    // Ammunition awareness and the floor. See kai_arsenal.cs.
    private readonly KaiArsenal _arsenal = new();

    // ------------------------------------------------------------------
    // Automatic config
    //
    // The game mode config is executed on every map load, so a session never
    // starts on default settings because somebody forgot to type it.
    //
    // Deliberately once per map rather than once per round. These configs
    // normally end with mp_restartgame, so running one mid-match would restart
    // the match, and running one every round would make the match unplayable.
    // ------------------------------------------------------------------

    // Name only, no path and no extension: CS2 resolves exec against csgo/cfg.
    private string _autoExecConfig = "gungame_pro";

    private bool _autoExecEnabled = true;

    // Delay before the exec fires. The map start listener runs while the map
    // is still coming up, and a config executed then is applied to a server
    // that is not ready to receive some of it.
    private float _autoExecDelay = 3.0f;

    // ------------------------------------------------------------------
    // Defuse watchdog
    //
    // A planted bomb ticking down with living CTs, no defuse running and
    // nobody in the act of starting one is not a tactical situation. It is a
    // symptom: some branch of this plugin has taken control of a bot and left
    // it holding an angle instead of doing the only thing that matters.
    //
    // No amount of reasoning about which branch is at fault helps in the
    // moment, and every second spent on it is a second of bomb timer. So the
    // watchdog does the only reliably correct thing: it takes this plugin off
    // the CT side entirely for the rest of the round and lets the native AI,
    // which has always known how to defuse a bomb, get on with it.
    // ------------------------------------------------------------------

    private bool _defuseWatchdogTripped;
    private float _defuseWatchdogArmed;

    // Seconds of a planted bomb with nobody defusing before the plugin gives
    // up and hands the round back. Long enough that a genuine approach is not
    // interrupted, short enough to leave time to actually defuse afterwards.
    private float _defuseWatchdogSeconds = 12.0f;

    // ------------------------------------------------------------------
    // Ghost mode
    //
    // For unattended mapping. The learner records every death, including the
    // human's, and a human parked in spawn while a server runs itself for
    // hours produces a dense cluster of samples at the spawn point with
    // whatever killed them as the angle to watch. That is not a duel spot; it
    // is a stationary target, and it poisons the very data the map is being
    // built from.
    //
    // Breadcrumbs were never affected, because the recorder already filters to
    // bots. The learner is the leak, and it is the only one.
    //
    // Ghost mode discards any engagement involving a human, so the map is
    // learned purely from bots fighting bots.
    // ------------------------------------------------------------------

    private bool _ghostHumans;

    // Also move humans to spectator and top the teams back up, so the match
    // is a genuine five on five rather than four bots against five.
    private bool _ghostSpectate;

    private float _nextGhostSweep;

    // Discarded engagements, so the effect is visible rather than assumed.
    private int _ghostDiscarded;

    // Enemy contacts reported near each bombsite this round. The controller's
    // only real information about where the opposition actually is, so it is
    // accumulated over the round rather than sampled, because a contact seen
    // twenty seconds ago still says something about where they went.
    private int[] _contactsBySite = Array.Empty<int>();

    private int _friendlyDeaths;
    private int _enemyDeaths;
    private float _roundStartedAt;

    // The queue of solves still to run, as (team, siteIndex) pairs. Worked
    // through one at a time because each is expensive.
    private readonly List<(int Team, int Site)> _solveQueue = new();
    private CCSPlayerController? _solveCaller;

    // Run the solve on its own once the prerequisites are met, so the only
    // manual step in the whole pipeline stays kai_learn build.
    //
    // Unlike the build, there is no judgement in solving: it either has a
    // usable graph, some known angles and a recorded bombsite, or it does not.
    // Nothing is gained by making somebody ask for it.
    private bool _autoSolve = true;

    // Throttle for the precondition check. Cheap, but no reason to run it
    // every tick when it can only change between rounds.
    private float _nextAutoSolveCheck;

    private KaiMapTactics _map = new();
    private string _currentMap = "";
    private bool _bombPlanted;
    private KaiPoint? _bombPos;
    private bool _enabled = true;

    private MemoryFunctionVoid<IntPtr>? _updateLookAngles;
    private MemoryFunctionVoid<IntPtr>? _botUpdate;
    private object? _botController;

    // ------------------------------------------------------------------
    // Round-win celebration
    //
    // When a round ends, every surviving bot on the winning team spins on the
    // spot and fires whatever it is already holding into the air until the
    // round restarts. Any win condition counts: defuse, explosion,
    // elimination or time. Nothing is switched or equipped.
    //
    // This is also the plugin's heartbeat. Two of the three write mechanisms
    // here cannot be verified by reading the game's headers, only by being
    // seen working, and this exercises both at once: the sweep comes from
    // writing m_lookYaw and m_lookPitch in the CCSBot::UpdateLookAngles pre
    // hook, which is what T holds, CT clear angles and pre-aim all depend on,
    // and the shooting comes from InjectUsercmd, which is what the fake defuse
    // depends on. A silent round end means something upstream is broken.
    // ------------------------------------------------------------------

    // Server time the celebration ends. Zero when not celebrating.
    private float _celebrateUntil;

    // Which team won, and is therefore allowed to celebrate.
    private int _celebrateTeam = -1;

    // How long it runs. Deliberately shorter than the default
    // mp_round_restart_delay so it cannot bleed into the next round's
    // freezetime, where bots are frozen and it would look broken.
    private const float CelebrateSeconds = 4.0f;

    // Degrees per second for the view sweep. Fast enough to read as
    // deliberate, slow enough that the native look spring can follow it.
    private const float CelebrateSpinDegreesPerSecond = 220.0f;

    // Look this far up, so the shots go into the sky rather than into a wall
    // or a teammate.
    private const float CelebrateSkyPitch = -70.0f;

    // Per-bot re-injection schedule, so the attack button stays held for
    // the whole celebration rather than lapsing between injections.
    private readonly Dictionary<int, float> _nextCelebrateShot = new();

    private bool IsCelebrating()
    {
        if (_celebrateUntil <= 0.0f)
        {
            return false;
        }

        if (Server.CurrentTime >= _celebrateUntil)
        {
            _celebrateUntil = 0.0f;
            _celebrateTeam = -1;
            _nextCelebrateShot.Clear();
            KaiLog.Event(nameof(IsCelebrating), "celebration finished");
            return false;
        }

        return true;
    }

    // Begin the celebration for one team. Nothing is switched or equipped;
    // whatever the bot happens to be holding is what it fires.
    private void StartCelebration(int winningTeam, string reason)
    {
        _celebrateUntil = Server.CurrentTime + CelebrateSeconds;
        _celebrateTeam = winningTeam;
        _nextCelebrateShot.Clear();

        int survivors = KaiPlayers.All()
            .Count(p => p != null && p.IsValid && p.IsBot && !p.IsHLTV && p.PawnIsAlive
                        && (int)p.TeamNum == winningTeam);

        KaiLog.Event(nameof(StartCelebration),
            $"round won by team {winningTeam} ({reason}), {survivors} surviving bots " +
            $"celebrating for {CelebrateSeconds:F1}s");

        // Zero survivors on the winning team every single round is not
        // plausible in a 5v5, so when it happens the full player state is
        // dumped rather than left to guesswork.
        if (survivors == 0)
        {
            KaiCensus.Dump("StartCelebration/noSurvivors");
        }
    }

    // ------------------------------------------------------------------
    // Paths
    // ------------------------------------------------------------------

    // Where the per-map tactics files and the learned sample banks live.
    // ModuleDirectory is the folder CounterStrikeSharp loaded the DLL from,
    // so this resolves to
    //   addons/counterstrikesharp/plugins/KaiBotTactics/kai_tactics
    private string DataDir => Path.Combine(ModuleDirectory, "kai_tactics");

    // Where the per-map, per-session log files are written. Kept alongside
    // the tactics data rather than mixed into it, so kai_tactics can be
    // synced or backed up without dragging a growing pile of log files along.
    private string LogDir => Path.Combine(DataDir, "logs");

    public override void Load(bool hotReload)
    {
        Directory.CreateDirectory(DataDir);

        // Open the log file first, so signature resolution and hook setup
        // below land in it. OnMapStart does not fire on a css_plugins reload,
        // so opening only there would mean a hot reload produced no file
        // until the next map change, which is when a log is most wanted.
        //
        // Named "startup" rather than after the current map, deliberately.
        // Plugins load during engine start, before any map exists, and
        // Server.MapName calls straight through to native code that is not
        // ready at that point. OnMapStart rolls over to a properly named file
        // as soon as there is a map, so nothing is lost by not asking here.
        KaiLog.OpenForMap(LogDir, "startup");
        KaiLog.Note($"plugin={ModuleName} v{ModuleVersion} hotReload={hotReload}");

        // A fingerprint of what this build actually contains. Comparing a
        // version number against a changelog is easy to get wrong; listing the
        // behaviours means the startup log alone answers "is my fix running".
        KaiLog.Note("features: objective-commitment-hardened, auto-exec-config, contact-callouts, "
                    + "trade-and-loss-calls, "
                    + "retake-roles, "
                    + "team-scoped-comms, inferno-callouts, weapon-pickup, knife-rush, "
                    + "dropped-weapon-memory, "
                    + "waypoint-overshoot-skip, watchdog-phase-aware, stick-the-plant, "
                    + "stick-the-defuse, aim-hook-forward-guard, "
                    + "stationary-only-glance, "
                    + "waypoint-spacing-floor, journey-heading, "
                    + "no-jump-while-steering, "
                    + "retake-scanning, named-position-reports, "
                    + "map-callout-table, real-bombsite-letters, named-squad-comms, "
                    + "round-briefing, "
                    + "distinct-cover-arcs, "
                    + "waypoint-ledger, "
                    + "deliberate-lurk-sweeps, staging-follows-route, "
                    + "route-detour-cap, forward-arc-clearing, "
                    + "rear-guard, walk-near-angles, "
                    + "defuse-watchdog, "
                    + "mapping-ceiling, variety-first-playcalling, bomb-converge, "
                    + "live-astar-paths, ghost-mode, "
                    + "bombsites-survive-rebuild, history-seeded-maturity, "
                    + "hands-off-while-mapping, "
                    + "auto-bootstrap-new-maps, "
                    + "evidence-based-maturity, team-leaders, "
                    + "carrier-driven-site, "
                    + "synchronised-execute, guard-bomb-play, transit-angle-sweep, "
                    + "guard-angle-sweep, "
                    + "tactical-controller, play-outcome-learning, audibles, static-routes, "
                    + "team-fake-rotations, fake-executes, spawn-learning, "
                    + "graph-connectivity, "
                    + "committed-defuse, whole-team-sweep, presolved-posts, "
                    + "plant-site-learning, graph-saturation, "
                    + "ground-snapped-posts, coverage-scored-holds, glance-sweep, "
                    + "transit-clearing, defuse-released-to-native, solo-defuser-routine, "
                    + "crossfire-support, breadcrumbs");

        KaiLog.Event(nameof(Load), $"loading v{ModuleVersion}, hotReload={hotReload}, {KaiTime.NowUtc()}");

        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        string lookSig = ResolveSignature("CCSBot::UpdateLookAngles", isWindows);
        string updateSig = ResolveSignature("CCSBot::Update", isWindows);

        if (!string.IsNullOrEmpty(lookSig))
        {
            try
            {
                _updateLookAngles = new MemoryFunctionVoid<IntPtr>(lookSig);
                _updateLookAngles.Hook(OnUpdateLookAnglesPre, HookMode.Pre);
                KaiLog.Event(nameof(Load), "hooked CCSBot::UpdateLookAngles, aim overrides are live");
            }
            catch (Exception ex)
            {
                _updateLookAngles = null;
                KaiLog.Event(nameof(Load),
                    $"could not hook CCSBot::UpdateLookAngles, all aim overrides disabled: {ex.Message}",
                    KaiLogLevel.Error);
            }
        }

        if (!string.IsNullOrEmpty(updateSig))
        {
            try
            {
                _botUpdate = new MemoryFunctionVoid<IntPtr>(updateSig);
                _botUpdate.Hook(OnBotUpdatePost, HookMode.Post);
                KaiLog.Event(nameof(Load),
                    "hooked CCSBot::Update, movement pin and USE suppression are live");
            }
            catch (Exception ex)
            {
                _botUpdate = null;
                KaiLog.Event(nameof(Load),
                    $"could not hook CCSBot::Update, anchoring and USE suppression disabled: {ex.Message}",
                    KaiLogLevel.Error);
            }
        }

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventBombPlanted>(OnBombPlanted);
        RegisterEventHandler<EventBombDefused>(OnBombEnded);
        RegisterEventHandler<EventBombExploded>(OnBombEnded);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        RegisterEventHandler<EventCsWinPanelMatch>(OnMatchEnd);

        KaiLog.Event(nameof(Load), "load complete");
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        try
        {
            _botController = KaiBotControllerBridge.TryGet();
        }
        catch (Exception ex)
        {
            _botController = null;
            KaiLog.Event(nameof(OnAllPluginsLoaded),
                $"BotController capability not resolvable: {ex.Message}", KaiLogLevel.Error);
        }

        if (_botController == null)
        {
            KaiLog.Event(nameof(OnAllPluginsLoaded),
                "BotController API unavailable. Everything still works except the fake defuse, " +
                "which needs InjectUsercmd to force the USE button.", KaiLogLevel.Error);
        }
        else
        {
            KaiLog.Event(nameof(OnAllPluginsLoaded), "BotController API resolved, fake defuse available");
        }

        // Pinned near the top of the log, because whether these resolved
        // changes how every line below it should be read.
        KaiLog.Note($"botController={_botController != null} rayTrace={KaiRayTraceBridge.Available()}");
    }

    public override void Unload(bool hotReload)
    {
        KaiLog.Event(nameof(Unload), "unloading, releasing hooks");

        // Persist before anything else. A hot reload that loses the session's
        // samples is the one unrecoverable failure in this plugin.
        _learner.SaveBank("unload");
        _crumbs.Save("unload");

        try
        {
            _updateLookAngles?.Unhook(OnUpdateLookAnglesPre, HookMode.Pre);
        }
        catch (Exception ex)
        {
            KaiLog.Event(nameof(Unload), $"unhook look angles failed: {ex.Message}", KaiLogLevel.Error);
        }

        try
        {
            _botUpdate?.Unhook(OnBotUpdatePost, HookMode.Post);
        }
        catch (Exception ex)
        {
            KaiLog.Event(nameof(Unload), $"unhook bot update failed: {ex.Message}", KaiLogLevel.Error);
        }

        _intents.Clear();
        _tPressureUntil.Clear();
        _retake.Reset("plugin unload");

        KaiLog.Event(nameof(Unload), "unload complete");

        // Last, so the "unload complete" line above still lands in the file.
        KaiLog.CloseCurrent();
    }

    // ------------------------------------------------------------------
    // Gamedata
    // ------------------------------------------------------------------

    // Read one signature out of ed0ard's BotController gamedata.json.
    //
    //   ModuleDirectory is <csgo>/addons/counterstrikesharp/plugins/KaiBotTactics
    //   his gamedata is   <csgo>/addons/BotController/gamedata.json
    //
    // A kai_gamedata.json in this plugin's own folder takes precedence, so a
    // signature can be pinned without touching his files.
    private string ResolveSignature(string key, bool isWindows)
    {
        string platform;

        if (isWindows)
        {
            platform = "windows";
        }
        else
        {
            platform = "linux";
        }

        var candidates = new List<string>
        {
            Path.Combine(ModuleDirectory, "kai_gamedata.json"),
            Path.GetFullPath(Path.Combine(
                ModuleDirectory, "..", "..", "..", "BotController", "gamedata.json")),
        };

        foreach (string path in candidates)
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(File.ReadAllText(path));

                if (!doc.RootElement.TryGetProperty(key, out var entry)) continue;
                if (!entry.TryGetProperty("signatures", out var sigs)) continue;
                if (!sigs.TryGetProperty(platform, out var sig)) continue;

                KaiLog.Event(nameof(ResolveSignature), $"'{key}' [{platform}] resolved from '{path}'");
                return sig.GetString() ?? "";
            }
            catch (Exception ex)
            {
                KaiLog.Event(nameof(ResolveSignature), $"error reading '{path}': {ex.Message}",
                    KaiLogLevel.Error);
            }
        }

        KaiLog.Event(nameof(ResolveSignature),
            $"'{key}' [{platform}] not found in any gamedata candidate", KaiLogLevel.Error);
        return "";
    }

    // ------------------------------------------------------------------
    // Game rules access
    // ------------------------------------------------------------------

    private static CCSGameRules? GameRules()
    {
        try
        {
            return Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;
        }
        catch (Exception ex)
        {
            KaiLog.Event(nameof(GameRules), $"could not resolve game rules: {ex.Message}",
                KaiLogLevel.Error);
            return null;
        }
    }

    private static int CurrentRound()
    {
        var gr = GameRules();

        if (gr == null)
        {
            return -1;
        }

        return gr.TotalRoundsPlayed;
    }

    // Is it safe to rebuild right now?
    //
    // A rebuild clears every assignment and resets the retake director, so
    // running it mid-round throws away that round's post-plant behaviour. It
    // also rewrites both JSON files, which is not something to do while a
    // round is generating new samples into the bank.
    private bool IsSafeBuildPhase(out string reason)
    {
        var gr = GameRules();

        if (gr == null)
        {
            reason = "game rules unavailable, cannot confirm the phase";
            return false;
        }

        if (gr.WarmupPeriod)
        {
            reason = "warmup";
            return true;
        }

        if (gr.FreezePeriod)
        {
            reason = "freezetime";
            return true;
        }

        if (gr.BombPlanted)
        {
            reason = "live round, bomb is planted";
            return false;
        }

        reason = "live round";
        return false;
    }

    // ------------------------------------------------------------------
    // Map and round lifecycle
    // ------------------------------------------------------------------

    private void OnMapStart(string mapName)
    {
        // Save the outgoing map's bank before switching, or a map change loses
        // everything learned since the last write. Deliberately before the
        // file sink is reopened, so this line still lands in the OUTGOING
        // map's log rather than the new one.
        _learner.SaveBank("map change");
        _crumbs.Save("map change");

        // New log file per map. kai_tactics/logs/<map>_<timestamp>.log
        KaiLog.OpenForMap(LogDir, mapName);

        _currentMap = mapName;
        _map = KaiTacticsLoader.Load(DataDir, mapName);
        _learner.OnMapStart(DataDir, mapName);
        _crumbs.OnMapStart(DataDir, mapName);
        _routes = KaiRouteStore.Load(DataDir, mapName);

        ScheduleAutoExec(mapName);
        KaiCallouts.OnMapStart(DataDir, mapName);
        _tactics.OnMapStart(DataDir, mapName, _map.PlantSites.Count);
        _maturity.OnMapStart(DataDir, mapName);

        foreach (var kv in _routes.Spawns)
        {
            _spawns[kv.Key] = kv.Value;
        }

        // A map with banked samples but no generated spots is a specific and
        // recoverable state: the tactics file is missing or was emptied, and
        // every behaviour that depends on it will silently do nothing. Said
        // plainly at load rather than left to be inferred from bots acting
        // stock three rounds later.
        int spots = _map.PostPlant.Count + _map.CtClear.Count + _map.PreAim.Count;

        // A map with post-plant samples but no recorded bombsites has lost
        // them. Sites are observed from planted_c4 and cannot be rebuilt from
        // the sample bank, so the only cure is a plant on each site, and
        // without one the playbook, the solver and the router all stay empty.
        if (_map.PlantSites.Count == 0 && _learner.PostPlantSamples > 0)
        {
            KaiLog.Event(nameof(OnMapStart),
                $"'{mapName}' has {_learner.PostPlantSamples} post-plant samples but no recorded " +
                $"bombsites. Sites are learned by watching where the bomb is planted and cannot " +
                $"be recovered from samples. Plays, solved posts and routes are all generated " +
                $"per site, so they will stay empty until a round ends with a plant on each one.",
                KaiLogLevel.Error);
        }

        if (spots == 0 && _learner.SampleCount > 0)
        {
            KaiLog.Event(nameof(OnMapStart),
                $"'{mapName}' has {_learner.SampleCount} banked samples but no generated spots. " +
                $"Every position behaviour will do nothing until this is rebuilt. " +
                $"Run kai_learn build during freezetime to regenerate it from the samples, " +
                $"or restore {mapName}.json.backup if one exists.",
                KaiLogLevel.Error);
        }

        _intents.Clear();
        _tPressureUntil.Clear();
        _threatPoints.Clear();
        _tWatchClaims.Clear();
        _tSectors.Clear();
        _tCover.Clear();
        _tWatchBearings.Clear();
        _glanceSet.Clear();
        _glanceIndex.Clear();
        _glanceNext.Clear();
        _transitSet.Clear();
        _lastReported.Clear();
        _transitIndex.Clear();
        _transitNext.Clear();
        _transitFlick.Clear();
        _retake.Reset("map start");
        _bombPlanted = false;
        _bombPos = null;
        _ctZones.Clear();
        _mapCentre = null;
        _nextZoneRefresh = 0.0f;
        _solver.Reset();
        _solveQueue.Clear();
        _solveCaller = null;
        _nextAutoSolveCheck = 0.0f;

        KaiLog.Event(nameof(OnMapStart), $"map='{mapName}' at {KaiTime.NowUtc()}");
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        _intents.Clear();
        _tPressureUntil.Clear();
        _threatPoints.Clear();
        _tWatchClaims.Clear();
        _tSectors.Clear();
        _tCover.Clear();
        _tWatchBearings.Clear();
        _glanceSet.Clear();
        _glanceIndex.Clear();
        _glanceNext.Clear();
        _transitSet.Clear();
        _transitIndex.Clear();
        _transitNext.Clear();
        _transitFlick.Clear();
        _retake.Reset("round start");
        _crumbs.OnRoundStart();
        _arsenal.OnRoundStart();

        _routeOf.Clear();
        _routeLeg.Clear();
        _routeFaking.Clear();
        _routeReversing.Clear();
        _decoySite.Clear();
        _decoyUntil.Clear();
        _decoyEngaged.Clear();
        _rotation = null;
        _realTargetSite = -1;
        _spawnsSampledThisRound = false;

        _contactsBySite = new int[Math.Max(1, _map.PlantSites.Count)];
        _friendlyDeaths = 0;
        _enemyDeaths = 0;
        _roundStartedAt = Server.CurrentTime;

        // The controller decides what each side is doing before anybody is
        // given a route, because the routes are how the play gets executed
        // rather than being the plan themselves.
        _maturity.AnnounceIfMature();

        // Leaders and plays only exist once the map does.
        if (_maturity.BehavioursActive)
        {
            _command.EnsureLeaders();
            CallPlays();
        }
        _bombPlanted = false;
        _bombPos = null;
        _looseBombPos = null;
        _nextLooseBombScan = 0.0f;
        _guardSeekStart.Clear();
        _guardSectors.Clear();
        _guardPositions.Clear();
        _guardSet.Clear();
        _guardIndex.Clear();
        _guardFlick.Clear();
        KaiLog.ResetThrottles();
        KaiComms.Reset();
        KaiSquad.Refresh();

        KaiLog.Event(nameof(OnRoundStart), $"round {CurrentRound()} start, all state cleared");
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        // Tell the controller how it went before anything is cleared. This is
        // the only feedback the play selection ever gets.
        if (_maturity.LearningPlays)
        {
            _tactics.RecordOutcome(@event.Winner);
        }
        else
        {
            KaiLog.Throttled("maturelearn", nameof(OnRoundEnd),
                "map is mature, the play record is final and this round was not counted", 60.0f);
        }
        _maturity.OnRoundEnd(BuildLearningEvidence());

        // The moment the sample bank stops growing is the moment to build
        // from it, so the tactics file is generated once from the complete
        // set rather than left as whatever partial build happened earliest.
        // Doing it here also means a new map needs no manual step at all:
        // an early kai_learn build only brings the behaviour forward.
        if (_maturity.JustFinishedMapping)
        {
            KaiLog.Event(nameof(OnRoundEnd),
                "mapping is complete, rebuilding the tactics file from the final sample bank");

            _learner.SaveBank("mapping complete");

            _map = _learner.Build(_map);
            _map.MapName = _currentMap;

            KaiTacticsLoader.Save(DataDir, _map, "mapping complete");

            // Everything downstream was derived from the old spots.
            _tCover.Clear();
            _glanceSet.Clear();
            _transitSet.Clear();

            // First time the book is built. Every bombsite has been seen by
            // now, so the plays generated here cover the whole map rather
            // than whichever site happened to be planted on first.
            _tactics.EnsurePlays(_map.PlantSites.Count);

            KaiLog.Event(nameof(OnRoundEnd),
                $"'{_currentMap}' is now LEARNING: the playbook has been built for " +
                $"{_map.PlantSites.Count} bombsite(s) and the bots start running plays from the " +
                $"next round. Everything before this was recording only.");
        }
        _command.Reset();

        // Celebrate first. Every win condition counts, so this reads the
        // winner off the event rather than inferring it from the bomb.
        // Winner is a team number: 2 Terrorist, 3 CounterTerrorist. Anything
        // else means a draw or a round that ended without a winner, and
        // nobody celebrates.
        int winner = @event.Winner;

        if (winner == (int)CsTeam.Terrorist || winner == (int)CsTeam.CounterTerrorist)
        {
            StartCelebration(winner, $"reason {@event.Reason}");
        }
        else
        {
            KaiLog.Event(nameof(OnRoundEnd), $"round ended with winner={winner}, no celebration");
        }

        _intents.Clear();
        _tPressureUntil.Clear();
        _threatPoints.Clear();
        _tWatchClaims.Clear();
        _tSectors.Clear();
        _tCover.Clear();
        _tWatchBearings.Clear();
        _glanceSet.Clear();
        _glanceIndex.Clear();
        _glanceNext.Clear();
        _transitSet.Clear();
        _transitIndex.Clear();
        _transitNext.Clear();
        _transitFlick.Clear();
        _retake.Reset("round end");
        _bombPlanted = false;
        _bombPos = null;
        _looseBombPos = null;
        _nextLooseBombScan = 0.0f;
        _guardSeekStart.Clear();
        _guardSectors.Clear();
        _guardPositions.Clear();
        _guardSet.Clear();
        _guardIndex.Clear();
        _guardFlick.Clear();

        KaiLog.Event(nameof(OnRoundEnd), "round end, overrides released");
        return HookResult.Continue;
    }

    private HookResult OnBombEnded(GameEvent @event, GameEventInfo info)
    {
        _bombPlanted = false;
        _bombPos = null;
        _tPressureUntil.Clear();
        _threatPoints.Clear();
        _tWatchClaims.Clear();
        _tSectors.Clear();
        _tCover.Clear();
        _tWatchBearings.Clear();
        _glanceSet.Clear();
        _glanceIndex.Clear();
        _glanceNext.Clear();
        _transitSet.Clear();
        _transitIndex.Clear();
        _transitNext.Clear();
        _transitFlick.Clear();
        _retake.Reset($"'{@event.EventName}'");

        KaiLog.Event(nameof(OnBombEnded), $"'{@event.EventName}', post-plant assignments released");
        return HookResult.Continue;
    }

    private HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        _bombPlanted = true;
        _looseBombPos = null;
        _tPressureUntil.Clear();

        if (!_enabled)
        {
            KaiLog.Event(nameof(OnBombPlanted), "plugin disabled, no assignments made");
            return HookResult.Continue;
        }

        if (!TryGetBombPosition(out var bomb))
        {
            KaiLog.Event(nameof(OnBombPlanted), "could not locate planted_c4, no assignments made",
                KaiLogLevel.Error);
            return HookResult.Continue;
        }

        _bombPos = bomb;

        RecordPlantSite(bomb);

        // Baseline census at every plant. Cheap, once a round, and it gives a
        // known-good reference to compare against whenever a filter later
        // disagrees about who is on the server.
        KaiCensus.Dump("OnBombPlanted");

        BuildThreatPoints(bomb);
        AssignTerroristSectors(bomb);
        if (_maturity.BehavioursActive)
        {
            _retake.OnBombPlanted(bomb, _map, MaxSpotDistanceFromBomb);
        }

        return HookResult.Continue;
    }

    // Lower a stored watch point back to feet level.
    //
    // Learned watch points are recorded at chest height, because that is where
    // the enemy's chest was. Pooling one with feet-level anchors and triggers
    // requires putting it back on the floor first, so that the single chest
    // offset applied at the point of use lands correctly for every entry.
    private static KaiPoint ToFeet(KaiPoint chestLevelPoint)
    {
        return new KaiPoint(
            chestLevelPoint.X,
            chestLevelPoint.Y,
            chestLevelPoint.Z - KaiHeights.Chest);
    }

    // Build the set of places a defender should be watching.
    //
    // The insight is that the learner already knows where the enemy comes
    // from, just filed under other names. A ctClear anchor is a position a CT
    // stood in near this bomb. A ctClear watch point is where a T was when it
    // killed one. A team-3 pre-aim trigger is a position a CT died in. All
    // three are CT positions, which is exactly what a defending T wants its
    // crosshair on.
    //
    // Points closer together than ThreatPointSpacing are merged, because two
    // defenders watching spots forty units apart are watching the same thing.
    private void BuildThreatPoints(KaiPoint bomb)
    {
        _threatPoints.Clear();
        _tWatchClaims.Clear();
        _tSectors.Clear();
        _tCover.Clear();
        _tWatchBearings.Clear();
        _glanceSet.Clear();
        _glanceIndex.Clear();
        _glanceNext.Clear();
        _transitSet.Clear();
        _transitIndex.Clear();
        _transitNext.Clear();
        _transitFlick.Clear();

        const float threatPointSpacing = 250.0f;

        // Everything pooled here has to mean the same thing vertically, because
        // the chest offset is added once at the point of use. Anchors and
        // triggers are stored at feet level, but a learned watch point already
        // has the chest offset baked in from when the sample was recorded, so
        // it is lowered back to feet before being pooled. Without this the
        // watch-derived entries end up aimed a whole extra chest height too
        // high, which on a long angle is a miss over the target's head.
        var raw = new List<KaiPoint>();

        foreach (var spot in _map.CtClear)
        {
            raw.Add(spot.Anchor);
            raw.Add(ToFeet(spot.Watch));
        }

        foreach (var spot in _map.PreAim)
        {
            if (spot.Team == (int)CsTeam.CounterTerrorist || spot.Team == 0)
            {
                raw.Add(spot.Trigger);
            }
        }

        foreach (var candidate in raw)
        {
            if (candidate.DistanceXY(bomb.X, bomb.Y) > MaxSpotDistanceFromBomb)
            {
                continue;
            }

            bool duplicate = false;

            foreach (var kept in _threatPoints)
            {
                if (kept.DistanceXY(candidate.X, candidate.Y) < threatPointSpacing
                    && MathF.Abs(kept.Z - candidate.Z) < 100.0f)
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
            {
                _threatPoints.Add(candidate);
            }
        }

        // Nearest approaches first, so the closest threats get covered even
        // when there are fewer defenders than approaches.
        _threatPoints.Sort((a, b) =>
            a.DistanceXY(bomb.X, bomb.Y).CompareTo(b.DistanceXY(bomb.X, bomb.Y)));

        KaiLog.Event(nameof(BuildThreatPoints),
            $"{raw.Count} raw candidates reduced to {_threatPoints.Count} distinct approaches " +
            $"within {MaxSpotDistanceFromBomb:F0} units of the bomb");
    }

    // Hand every defending T an evenly spaced arc around the bomb.
    //
    // The fan is anchored on the nearest known approach, so one bot always
    // faces the most immediate threat squarely and the rest spread
    // symmetrically either side. Without this every defender applies the same
    // "nearest visible approach" rule, reaches the same answer, and the whole
    // team ends up watching one corridor.
    private void AssignTerroristSectors(KaiPoint bomb)
    {
        _tSectors.Clear();

        var slots = new List<int>();

        foreach (var p in KaiPlayers.All())
        {
            if (p == null || !p.IsValid || !p.IsBot || p.IsHLTV)
            {
                continue;
            }

            if (!p.PawnIsAlive || (int)p.TeamNum != (int)CsTeam.Terrorist)
            {
                continue;
            }

            slots.Add(p.Slot);
        }

        if (slots.Count == 0)
        {
            return;
        }

        float baseBearing = 0.0f;

        if (_threatPoints.Count > 0)
        {
            var first = _threatPoints[0];
            baseBearing = KaiFormation.Bearing(bomb.X, bomb.Y, first.X, first.Y);
        }

        var assigned = KaiFormation.AssignSectors(slots, baseBearing);

        foreach (var kv in assigned)
        {
            _tSectors[kv.Key] = kv.Value;
        }

        KaiLog.Event(nameof(AssignTerroristSectors),
            $"{slots.Count} defenders given {360.0f / slots.Count:F0} degree arcs, " +
            $"fan anchored on bearing {baseBearing:F0}");
    }

    // Claim an approach for this defender.
    //
    // Selection is by ARC, not by distance. Among the approaches nobody else
    // has taken and this bot can actually see, it takes the one whose bearing
    // from the bomb sits closest to its own assigned sector. Distance is only
    // the tie-break. That is what stops five defenders converging: each is
    // pulled towards a different part of the circle by construction.
    //
    // Sticky once claimed, so a defender covers the same approach all round
    // rather than swapping every time another bot dies and frees something up.
    private bool TryClaimThreatPoint(
        CCSPlayerController player, CCSPlayerPawn pawn, Vector origin, out KaiPoint watch)
    {
        watch = new KaiPoint();

        if (_threatPoints.Count == 0 || _bombPos == null)
        {
            return false;
        }

        if (_tWatchClaims.TryGetValue(player.Slot, out int held)
            && held >= 0 && held < _threatPoints.Count)
        {
            watch = _threatPoints[held];
            return true;
        }

        float sector;

        if (!_tSectors.TryGetValue(player.Slot, out sector))
        {
            // No arc assigned, most likely because this bot spawned in or
            // switched teams after the plant. Fall back to its own bearing so
            // it at least holds where it already is rather than crossing the
            // site to somewhere arbitrary.
            sector = KaiFormation.Bearing(_bombPos.X, _bombPos.Y, origin.X, origin.Y);
        }

        var eye = new Vector(origin.X, origin.Y, origin.Z + pawn.ViewOffset.Z);

        // What separation is actually achievable. Asking for 120 degrees
        // between five defenders is asking for something that does not exist
        // on a circle, so the requirement is capped at an even share.
        int defenders = Math.Max(1, _tSectors.Count);
        float required = MathF.Min(_watchSeparationDeg, 360.0f / defenders);

        int bestIndex = -1;
        float bestGap = float.MaxValue;
        float bestOutward = -1.0f;

        for (int i = 0; i < _threatPoints.Count; i++)
        {
            if (_tWatchClaims.ContainsValue(i))
            {
                continue;
            }

            var candidate = _threatPoints[i];

            var target = new Vector(
                candidate.X, candidate.Y, candidate.Z + KaiHeights.Chest);

            if (!KaiRayTraceBridge.CanSee(eye, target))
            {
                continue;
            }

            // The direction this bot would be LOOKING, which is what has to be
            // separated from the others. Separating the approaches by their
            // bearing from the bomb is not the same thing: two defenders on
            // opposite sides of the site can end up looking down the same lane.
            float watchBearing = KaiFormation.Bearing(
                origin.X, origin.Y, candidate.X, candidate.Y);

            bool clear = true;

            foreach (var kv in _tWatchBearings)
            {
                if (kv.Key == player.Slot)
                {
                    continue;
                }

                if (KaiFormation.AngleGap(watchBearing, kv.Value) < required)
                {
                    clear = false;
                    break;
                }
            }

            if (!clear)
            {
                continue;
            }

            float bearing = KaiFormation.Bearing(
                _bombPos.X, _bombPos.Y, candidate.X, candidate.Y);

            float gap = KaiFormation.AngleGap(bearing, sector);

            // How far out this puts the defender. Further from the bomb is
            // better: a defender pushed to the edge of the site sees an
            // attacker crossing open ground, while one stood on the bomb only
            // sees them once they are already inside it.
            float outward = candidate.DistanceXY(_bombPos.X, _bombPos.Y);

            if (gap < bestGap - 1.0f
                || (MathF.Abs(gap - bestGap) <= 1.0f && outward > bestOutward))
            {
                bestGap = gap;
                bestOutward = outward;
                bestIndex = i;
            }
        }

        if (bestIndex < 0 && _tWatchBearings.Count > 0)
        {
            // Nothing satisfied the separation rule. Rather than leave this
            // bot with no angle at all, drop the requirement for it: a
            // defender watching a duplicated lane still beats one watching
            // nothing.
            KaiLog.Event(nameof(TryClaimThreatPoint),
                $"slot {player.Slot} found no approach {required:F0} degrees clear of the " +
                $"others, relaxing the separation rule for this bot");

            for (int i = 0; i < _threatPoints.Count; i++)
            {
                if (_tWatchClaims.ContainsValue(i))
                {
                    continue;
                }

                var candidate = _threatPoints[i];
                var target = new Vector(
                    candidate.X, candidate.Y, candidate.Z + KaiHeights.Chest);

                if (!KaiRayTraceBridge.CanSee(eye, target))
                {
                    continue;
                }

                float outward = candidate.DistanceXY(_bombPos.X, _bombPos.Y);

                if (outward > bestOutward)
                {
                    bestOutward = outward;
                    bestIndex = i;
                }
            }
        }

        if (bestIndex < 0)
        {
            return false;
        }

        _tWatchClaims[player.Slot] = bestIndex;
        watch = _threatPoints[bestIndex];

        _tWatchBearings[player.Slot] = KaiFormation.Bearing(
            origin.X, origin.Y, watch.X, watch.Y);

        KaiLog.Event(nameof(TryClaimThreatPoint),
            $"slot {player.Slot} ('{player.PlayerName}') sector={sector:F0} claimed approach " +
            $"{bestIndex} at ({watch.X:F0},{watch.Y:F0},{watch.Z:F0}), " +
            $"arc gap {bestGap:F0} deg, watching bearing " +
            $"{_tWatchBearings[player.Slot]:F0}, {bestOutward:F0} units out from the bomb " +
            $"(separation required {required:F0} deg across {defenders} defenders)");

        return true;
    }

    // Pull an arbitrary computed point onto ground somebody has actually
    // stood on.
    //
    // Trigonometry will happily produce a position outside the map, inside
    // geometry, or on a ledge no bot can reach, and a bot sent to one walks
    // towards it until the round ends. The breadcrumb graph is the only thing
    // in this plugin that knows where bots can physically be, so every
    // computed destination is snapped to it before anybody is sent there.
    //
    // Falls back to the learned spot data when the graph is still empty, and
    // to rejecting the point outright when neither knows anything about it.
    private KaiPoint? SnapToGround(KaiPoint candidate, float tolerance)
    {
        var snapped = _crumbs.NearestStandable(
            candidate.X, candidate.Y, candidate.Z, tolerance);

        if (snapped != null)
        {
            return snapped;
        }

        if (_crumbs.IsUsable)
        {
            // The graph is trusted and has nothing near this point, which is a
            // positive statement that bots do not go there.
            return null;
        }

        // No breadcrumbs yet. Learned spots are the next best evidence: every
        // one came from a position somebody was killed at or killed from.
        KaiPoint? best = null;
        float bestDist = tolerance;

        foreach (var spot in _map.PreAim)
        {
            float d = spot.Trigger.DistanceXY(candidate.X, candidate.Y);

            if (d < bestDist && MathF.Abs(spot.Trigger.Z - candidate.Z) < 120.0f)
            {
                bestDist = d;
                best = spot.Trigger;
            }
        }

        foreach (var spot in _map.PostPlant)
        {
            float d = spot.Anchor.DistanceXY(candidate.X, candidate.Y);

            if (d < bestDist && MathF.Abs(spot.Anchor.Z - candidate.Z) < 120.0f)
            {
                bestDist = d;
                best = spot.Anchor;
            }
        }

        return best;
    }

    // How many pre-aim spots can be seen from here.
    //
    // The score a holding position is chosen on. A bot that can see five known
    // duel spots from one piece of cover is worth more than one that can see
    // a single spot, because it can cover all five by glancing between them
    // and only has to win one fight at a time.
    private int CoverageScore(KaiPoint position, float eyeHeight, int team, List<int>? visible)
    {
        visible?.Clear();

        var eye = new Vector(position.X, position.Y, position.Z + eyeHeight);
        int score = 0;

        for (int i = 0; i < _map.PreAim.Count; i++)
        {
            var spot = _map.PreAim[i];

            if (spot.Team != 0 && spot.Team != team)
            {
                continue;
            }

            // Only spots close enough to make out a player standing there.
            if (spot.Trigger.DistanceXY(position.X, position.Y) > _coverageRange)
            {
                continue;
            }

            var target = new Vector(
                spot.Trigger.X, spot.Trigger.Y, spot.Trigger.Z + KaiHeights.Chest);

            if (!KaiRayTraceBridge.CanSee(eye, target))
            {
                continue;
            }

            score++;
            visible?.Add(i);
        }

        return score;
    }

    // Hand this defender one of the pre-solved posts for the site the bomb is
    // on, closest in bearing to the arc it was given, and not already taken.
    //
    // Bearing rather than distance, so the fan is preserved: five defenders
    // each take the best post in their own arc rather than the five best posts
    // overall, which would put them all on one side of the site.
    private KaiPoint? ClaimSolvedPost(CCSPlayerController player, KaiPoint bomb, float bearing)
    {
        if (_map.SolvedTPosts.Count == 0 || _map.PlantSites.Count == 0)
        {
            return null;
        }

        // Which recorded site is this plant on?
        int site = -1;
        float bestSiteDist = 900.0f;

        for (int i = 0; i < _map.PlantSites.Count; i++)
        {
            float d = _map.PlantSites[i].DistanceXY(bomb.X, bomb.Y);

            if (d < bestSiteDist)
            {
                bestSiteDist = d;
                site = i;
            }
        }

        if (site < 0)
        {
            KaiLog.Throttled("nosite", nameof(ClaimSolvedPost),
                $"plant at ({bomb.X:F0},{bomb.Y:F0}) is not near any of the " +
                $"{_map.PlantSites.Count} recorded sites, falling back to a live search", 10.0f);

            return null;
        }

        var taken = new List<KaiPoint>(_tCover.Values);

        KaiSolvedPost? best = null;
        float bestGap = float.MaxValue;

        foreach (var candidate in _map.SolvedTPosts)
        {
            if (candidate.SiteIndex != site)
            {
                continue;
            }

            if (!KaiFormation.FarEnoughFrom(
                    candidate.Position, taken, KaiFormation.MinBotSpacing))
            {
                continue;
            }

            float gap = KaiFormation.AngleGap(candidate.Bearing, bearing);

            if (gap < bestGap)
            {
                bestGap = gap;
                best = candidate;
            }
        }

        if (best == null)
        {
            return null;
        }

        KaiLog.Event(nameof(ClaimSolvedPost),
            $"slot {player.Slot} takes pre-solved post '{best.Name}' on site {site}, " +
            $"bearing {best.Bearing:F0} against its arc {bearing:F0} (gap {bestGap:F0}), " +
            $"covering {best.Coverage} angle(s) from {best.Distance:F0} units out");

        return best.Position;
    }

    // Find this defender a place on the ring around the bomb.
    //
    // In priority order: on ground a bot has actually stood on, with an
    // unobstructed view of the bomb, as far out as possible, with cover
    // behind, spread evenly around the circle, and not crowding anybody.
    //
    // Every candidate is snapped to the breadcrumb graph before it is
    // considered. Trigonometry alone was producing points a thousand units
    // outside the map, and a bot sent to one walks towards it until the round
    // ends, which is exactly what "the supporting Ts wander around post
    // plant" looked like from the outside.
    private KaiPoint? ResolveRingPosition(
        CCSPlayerController player, CCSPlayerPawn pawn, Vector origin, KaiPoint bomb, float bearing)
    {
        float eyeHeight = pawn.ViewOffset.Z;
        var bombTarget = new Vector(bomb.X, bomb.Y, bomb.Z + KaiHeights.BombWatch);

        var taken = new List<KaiPoint>();

        foreach (var kv in _tCover)
        {
            if (kv.Key != player.Slot)
            {
                taken.Add(kv.Value);
            }
        }

        // Far to near: the furthest position that still sees the bomb sees an
        // attacker earliest.
        float[] radii = { 1400.0f, 1150.0f, 900.0f, 700.0f, 550.0f, 420.0f, 320.0f };
        float[] offsets = { 0.0f, 15.0f, -15.0f, 30.0f, -30.0f, 45.0f, -45.0f };

        KaiPoint? best = null;
        int bestScore = -1;
        float bestRadius = 0.0f;

        foreach (float radius in radii)
        {
            if (radius > _tHoldNearBombRadius)
            {
                continue;
            }

            foreach (float offset in offsets)
            {
                var raw = KaiFormation.StepBack(
                    bomb, KaiFormation.Normalize(bearing + offset), radius);

                raw = new KaiPoint(raw.X, raw.Y, origin.Z);

                // Must be somewhere a bot can actually be.
                var candidate = SnapToGround(raw, 220.0f);

                if (candidate == null)
                {
                    continue;
                }

                var candidateEye = new Vector(
                    candidate.X, candidate.Y, candidate.Z + eyeHeight);

                if (!KaiRayTraceBridge.CanSee(candidateEye, bombTarget))
                {
                    continue;
                }

                if (!KaiFormation.FarEnoughFrom(candidate, taken, KaiFormation.MinBotSpacing))
                {
                    continue;
                }

                // Back into cover, away from the bomb.
                var covered = KaiFormation.BackToCover(
                    candidate,
                    eyeHeight,
                    new KaiPoint(bomb.X, bomb.Y, bomb.Z + KaiHeights.BombWatch),
                    _coverBackDistance,
                    _coverWallStandoff);

                var snappedCover = SnapToGround(covered, 120.0f);

                if (snappedCover != null)
                {
                    var coveredEye = new Vector(
                        snappedCover.X, snappedCover.Y, snappedCover.Z + eyeHeight);

                    if (KaiRayTraceBridge.CanSee(coveredEye, bombTarget))
                    {
                        candidate = snappedCover;
                    }
                }

                // Among everything valid at this radius, prefer the position
                // that also covers the most known duel spots.
                int score = CoverageScore(
                    candidate, eyeHeight, (int)player.TeamNum, null);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                    bestRadius = radius;
                }
            }

            // Stop at the first radius that produced anything. Further out is
            // better, so there is no reason to keep searching inwards.
            if (best != null)
            {
                break;
            }
        }

        if (best != null)
        {
            KaiLog.Event(nameof(ResolveRingPosition),
                $"slot {player.Slot} ring post on bearing {bearing:F0} at {bestRadius:F0} units, " +
                $"({best.X:F0},{best.Y:F0},{best.Z:F0}), covers {bestScore} pre-aim spot(s)");

            return best;
        }

        // Nothing on the ring works. Returning the bot's current position was
        // wrong: for whoever just planted, that position is standing on the
        // bomb, which is the worst place on the site to be. Return null and
        // let the caller fall back to simply getting away from it.
        KaiLog.Event(nameof(ResolveRingPosition),
            $"slot {player.Slot} found no ring post on bearing {bearing:F0} with sight of the bomb");

        return null;
    }

    // Anywhere that is not stood on the bomb.
    //
    // The fallback when the ring search finds nothing. Walks outward along the
    // assigned bearing looking for standable ground at any distance, because a
    // defender in a mediocre spot forty metres out still beats one silhouetted
    // on top of the objective.
    private KaiPoint? ResolveFallbackPost(
        CCSPlayerController player, CCSPlayerPawn pawn, Vector origin, KaiPoint bomb, float bearing)
    {
        float eyeHeight = pawn.ViewOffset.Z;

        float[] radii = { 800.0f, 600.0f, 450.0f, 350.0f, 260.0f };
        float[] offsets = { 0.0f, 30.0f, -30.0f, 60.0f, -60.0f, 90.0f, -90.0f };

        foreach (float radius in radii)
        {
            foreach (float offset in offsets)
            {
                var raw = KaiFormation.StepBack(
                    bomb, KaiFormation.Normalize(bearing + offset), radius);

                raw = new KaiPoint(raw.X, raw.Y, origin.Z);

                var candidate = SnapToGround(raw, 250.0f);

                if (candidate == null)
                {
                    continue;
                }

                // Deliberately no line-of-sight requirement here. This runs
                // only when nothing with a view was available, and the aim is
                // simply to get off the bomb and behind something.
                var covered = KaiFormation.BackToCover(
                    candidate, eyeHeight,
                    new KaiPoint(bomb.X, bomb.Y, bomb.Z + KaiHeights.BombWatch),
                    _coverBackDistance, _coverWallStandoff);

                var snapped = SnapToGround(covered, 120.0f);

                var chosen = snapped ?? candidate;

                KaiLog.Event(nameof(ResolveFallbackPost),
                    $"slot {player.Slot} has no ring post, falling back to " +
                    $"({chosen.X:F0},{chosen.Y:F0}) {radius:F0} units off the bomb");

                return chosen;
            }
        }

        return null;
    }

    // Work out where this defender should actually stand.
    //
    // The learned approach is a place somebody died, which is frequently open
    // ground. Backing away from the watch direction until a wall stops it
    // turns that into a position with something solid behind it. Cached per
    // round: a destination that keeps moving is one the bot never reaches.
    private KaiPoint ResolveCoverPosition(
        CCSPlayerController player, CCSPlayerPawn pawn, Vector origin, KaiPoint watch)
    {
        if (!_coverSeeking)
        {
            return new KaiPoint(origin.X, origin.Y, origin.Z);
        }

        if (_tCover.TryGetValue(player.Slot, out var cached))
        {
            return cached;
        }

        var here = new KaiPoint(origin.X, origin.Y, origin.Z);

        var cover = KaiFormation.BackToCover(
            here,
            pawn.ViewOffset.Z,
            new KaiPoint(watch.X, watch.Y, watch.Z + KaiHeights.Chest),
            _coverBackDistance,
            _coverWallStandoff);

        // A defender that has backed out of sight of the bomb is no longer
        // defending it. BackToCover only checks the angle it was given, so
        // the bomb has to be verified separately or a bot can reverse round a
        // corner and lose the only thing it is there to protect.
        if (_bombPos != null)
        {
            var coverEye = new Vector(cover.X, cover.Y, cover.Z + pawn.ViewOffset.Z);
            var bombTarget = new Vector(
                _bombPos.X, _bombPos.Y, _bombPos.Z + KaiHeights.BombWatch);

            if (!KaiRayTraceBridge.CanSee(coverEye, bombTarget))
            {
                KaiLog.Event(nameof(ResolveCoverPosition),
                    $"slot {player.Slot} cover spot loses sight of the bomb, staying put");

                cover = here;
            }
        }

        // Never take cover on top of a team mate. Two defenders sharing a
        // piece of cover die to the same burst and cover one angle between
        // them, which defeats the whole point of the fan.
        if (!KaiFormation.FarEnoughFrom(cover, _tCover.Values, KaiFormation.MinBotSpacing))
        {
            KaiLog.Event(nameof(ResolveCoverPosition),
                $"slot {player.Slot} cover spot is inside another defender's spacing, staying put");

            cover = here;
        }

        _tCover[player.Slot] = cover;

        float moved = cover.DistanceXY(origin.X, origin.Y);

        KaiLog.Event(nameof(ResolveCoverPosition),
            $"slot {player.Slot} backing {moved:F0} units into cover, " +
            $"from ({origin.X:F0},{origin.Y:F0}) to ({cover.X:F0},{cover.Y:F0})");

        return cover;
    }

    // Can this bot see the bomb from where it is standing?
    private static bool CanSeeBomb(CCSPlayerPawn pawn, Vector origin, KaiPoint bomb)
    {
        var eye = new Vector(origin.X, origin.Y, origin.Z + pawn.ViewOffset.Z);
        var target = new Vector(bomb.X, bomb.Y, bomb.Z + KaiHeights.BombWatch);
        return KaiRayTraceBridge.CanSee(eye, target);
    }
    // Remember where the bomb gets planted, merged into sites, and what the
    // game calls each one.
    //
    // The solver needs to know where a bombsite is, and a bombsite is only
    // meaningfully "where the bomb actually ends up", which is not the same as
    // the map's own site volume: plants cluster on a few favoured spots inside
    // it. Merging at 500 units keeps A and B distinct while folding the
    // scatter within each one into a single centre.
    //
    // The letter is read from the game rather than inferred from the index.
    // Indices here are assigned in the order sites are first planted on, so
    // index zero is whichever site somebody happened to take first, and
    // calling that one "A" was wrong exactly as often as it was right.
    private void RecordPlantSite(KaiPoint bomb)
    {
        const float mergeRadius = 500.0f;

        string letter = ReadBombSiteLetter();

        for (int i = 0; i < _map.PlantSites.Count; i++)
        {
            var site = _map.PlantSites[i];

            if (site.DistanceXY(bomb.X, bomb.Y) < mergeRadius
                && MathF.Abs(site.Z - bomb.Z) < 200.0f)
            {
                // Nudge the centre towards this plant rather than replacing
                // it, so the site converges on where plants actually happen.
                _map.PlantSites[i] = new KaiPoint(
                    site.X + ((bomb.X - site.X) * 0.2f),
                    site.Y + ((bomb.Y - site.Y) * 0.2f),
                    site.Z + ((bomb.Z - site.Z) * 0.2f));

                // Fill the letter in if it was missing, in case an early plant
                // could not be read.
                while (_map.PlantSiteNames.Count <= i)
                {
                    _map.PlantSiteNames.Add("");
                }

                if (_map.PlantSiteNames[i].Length == 0 && letter.Length > 0)
                {
                    _map.PlantSiteNames[i] = letter;

                    KaiLog.Event(nameof(RecordPlantSite),
                        $"site {i} is bombsite {letter} according to the game");
                }

                return;
            }
        }

        _map.PlantSites.Add(new KaiPoint(bomb.X, bomb.Y, bomb.Z));

        while (_map.PlantSiteNames.Count < _map.PlantSites.Count)
        {
            _map.PlantSiteNames.Add("");
        }

        _map.PlantSiteNames[_map.PlantSites.Count - 1] = letter;

        KaiLog.Event(nameof(RecordPlantSite),
            $"new plant site {_map.PlantSites.Count - 1} recorded at " +
            $"({bomb.X:F0},{bomb.Y:F0},{bomb.Z:F0}), the game calls it " +
            $"{(letter.Length > 0 ? "bombsite " + letter : "unnamed")}, " +
            $"{_map.PlantSites.Count} known");

        // Extend the playbook now that a site exists, but only once the map
        // has been learned. Adding plays during mapping would create a book
        // nobody is allowed to call from.
        if (_maturity.BehavioursActive)
        {
            _tactics.EnsurePlays(_map.PlantSites.Count);
        }
    }

    // What the game calls the site the bomb is currently on.
    //
    // m_nBombSite is the engine's own designation, so it is right regardless
    // of what order this plugin happened to discover the sites in.
    private static string ReadBombSiteLetter()
    {
        try
        {
            var c4 = Utilities
                .FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4")
                .FirstOrDefault(e => e.IsValid);

            if (c4 == null)
            {
                return "";
            }

            return c4.BombSite switch
            {
                0 => "A",
                1 => "B",
                _ => "",
            };
        }
        catch (Exception ex)
        {
            KaiLog.Throttled("sitename", nameof(ReadBombSiteLetter),
                $"could not read the bombsite: {ex.Message}", 30.0f, KaiLogLevel.Error);
            return "";
        }
    }


    private bool TryGetBombPosition(out KaiPoint bomb)
    {
        bomb = new KaiPoint();

        try
        {
            var c4 = Utilities
                .FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4")
                .FirstOrDefault(e => e.IsValid);

            var origin = c4?.AbsOrigin;

            if (origin == null)
            {
                return false;
            }

            bomb = new KaiPoint(origin.X, origin.Y, origin.Z);
            KaiLog.Event(nameof(TryGetBombPosition),
                $"planted_c4 at ({bomb.X:F0},{bomb.Y:F0},{bomb.Z:F0})");
            return true;
        }
        catch (Exception ex)
        {
            KaiLog.Event(nameof(TryGetBombPosition), $"failed: {ex.Message}", KaiLogLevel.Error);
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Pressure: Ts abandon a hold when the fight comes to them
    // ------------------------------------------------------------------

    // Watch for a bomb nobody is defusing, and hand the round back if so.
    //
    // Armed when the bomb is planted and CTs are alive. Disarmed the moment a
    // defuse actually starts. If it runs out first, the plugin stops touching
    // the CT side for the rest of the round.
    //
    // This is a safety valve, not a tactic. It exists because a bot standing
    // on a live bomb doing nothing has been observed more than once, and every
    // cause found so far has been a different one: a stale intent, a pin that
    // outlived its purpose, a branch that returned without writing. Rather
    // than keep chasing individual causes, this catches the whole class by its
    // one shared symptom.
    private void DriveDefuseWatchdog(float now)
    {
        if (!_bombPlanted)
        {
            _defuseWatchdogArmed = 0.0f;
            _defuseWatchdogTripped = false;
            return;
        }

        if (_defuseWatchdogTripped)
        {
            return;
        }

        // Do not count time the retake is deliberately spending elsewhere.
        //
        // The inspection and bait phases exist precisely so that nobody
        // touches the bomb yet, and the inspection runs for the same twelve
        // seconds this watchdog allows. So it was tripping at the exact moment
        // a normal retake finished clearing and would have moved to the
        // defuse, then disabling the director for the rest of the round. The
        // logs showed eleven plants reaching Inspect and not one reaching
        // Commit: the safety net was the thing breaking it.
        //
        // Rearmed rather than paused, so the full window is available from the
        // moment the team actually intends to defuse.
        if (_retake.Phase == KaiRetakePhase.Inspect
            || _retake.Phase == KaiRetakePhase.Bait)
        {
            _defuseWatchdogArmed = now;

            KaiLog.Throttled("watchdoghold", nameof(DriveDefuseWatchdog),
                $"retake is in {_retake.Phase}, which is meant to have nobody on the bomb. " +
                $"Watchdog held off.", 10.0f);

            return;
        }

        // A defuse in progress is the system working. Re-arm from now, so a
        // defuse that gets interrupted still gets its own full window.
        if (KaiBombState.IsBeingDefused())
        {
            _defuseWatchdogArmed = now;
            return;
        }

        int ctsAlive = 0;

        foreach (var p in KaiPlayers.All())
        {
            if (p == null || !p.IsValid || p.IsHLTV || !p.PawnIsAlive)
            {
                continue;
            }

            if ((int)p.TeamNum == (int)CsTeam.CounterTerrorist)
            {
                ctsAlive++;
            }
        }

        if (ctsAlive == 0)
        {
            // Nobody left to defuse. Not a fault.
            _defuseWatchdogArmed = 0.0f;
            return;
        }

        if (_defuseWatchdogArmed <= 0.0f)
        {
            _defuseWatchdogArmed = now;
            return;
        }

        if (now - _defuseWatchdogArmed < _defuseWatchdogSeconds)
        {
            return;
        }

        _defuseWatchdogTripped = true;

        // Strip everything this plugin has told the CT side to do.
        foreach (var p in KaiPlayers.All())
        {
            if (p == null || !p.IsValid || (int)p.TeamNum != (int)CsTeam.CounterTerrorist)
            {
                continue;
            }

            _intents.Remove(p.Slot);
            _routeOf.Remove(p.Slot);
            _routeLeg.Remove(p.Slot);
            _routeReversing.Remove(p.Slot);
            _glanceSet.Remove(p.Slot);
            _transitSet.Remove(p.Slot);
        }

        _retake.Reset("defuse watchdog");

        KaiComms.Call((int)CsTeam.CounterTerrorist, -1, "watchdog",
            "nobody is on the bomb, playing it straight from here", 20.0f);

        KaiLog.Event(nameof(DriveDefuseWatchdog),
            $"WATCHDOG: the bomb has been planted for {_defuseWatchdogSeconds:F0}s with " +
            $"{ctsAlive} CT(s) alive and no defuse started. Something in this plugin is holding " +
            $"them off the objective. All CT overrides are dropped for the rest of the round and " +
            $"the native AI has the side back.",
            KaiLogLevel.Error);
    }

    // Tell the human what the side is doing, and what they should do.
    //
    // Two lines at most. The first is the plan, the second is an instruction
    // aimed at the person, because a plan nobody is told their part in is just
    // noise. Only ever sent for the human's own team, and only when there is a
    // human to tell.
    private void BriefTheSquad(int team, KaiPlay play, int site, List<int> mainGroup)
    {
        if (team != KaiSquad.SquadTeam || KaiSquad.HumanName.Length == 0)
        {
            return;
        }

        int leader = _command.LeaderOf(team);

        // Where the side is actually going, counted off the routes they were
        // just handed. Derived rather than described, so it cannot drift out
        // of step with what the bots do.
        string spread = ApproachSpread(site);

        string plan = play.Kind switch
        {
            KaiPlayKind.Execute => spread.Length > 0
                ? $"hitting {SiteName(site)}: {spread}"
                : $"hitting {SiteName(site)} together",

            KaiPlayKind.SplitFake => spread.Length > 0
                ? $"split {SiteName(site)}: {spread}, two faking the other site"
                : $"split on {SiteName(site)}, two faking the other site",

            KaiPlayKind.Default => spread.Length > 0
                ? $"default, taking map control first: {spread}"
                : "default, taking map control before we commit",

            KaiPlayKind.Aggro => spread.Length > 0
                ? $"pushing early for info: {spread}"
                : "pushing early for info",

            KaiPlayKind.GuardBomb => "bomb is on the floor, we sit on it and hold the angles",

            _ => $"holding {SiteName(site)}, spread across the angles",
        };

        KaiComms.Call(team, leader, $"play:{team}", plan, 10.0f);

        // The human's own job. Whoever carries the bomb decides the shape of
        // it: with the bomb they are told where to take it, without it they
        // are told where to be.
        int carrier = BombCarrierSlot();
        bool humanHasBomb = carrier >= 0 && carrier == KaiSquad.HumanSlot;

        string instruction;

        // Name a place to be, not a direction to face. "go go go" tells
        // nobody anything; "hold the doorway into A" is an instruction.
        string busiest = BusiestApproach(site);

        if (team == (int)CsTeam.Terrorist && humanHasBomb)
        {
            instruction = busiest.Length > 0
                ? $"take the bomb to {SiteName(site)} through {busiest}, we clear ahead of you"
                : $"take the bomb to {SiteName(site)}, we clear ahead of you";
        }
        else if (team == (int)CsTeam.Terrorist)
        {
            instruction = play.Kind == KaiPlayKind.Default
                ? (busiest.Length > 0
                    ? $"hold {busiest} with us, do not commit yet"
                    : "hold with us for map control, do not commit yet")
                : (busiest.Length > 0
                    ? $"come {SiteName(site)} through {busiest}"
                    : $"come {SiteName(site)} with us");
        }
        else if (play.Kind == KaiPlayKind.Aggro)
        {
            instruction = busiest.Length > 0
                ? $"push {busiest} with us, we need the info early"
                : "push with us, we need the info early";
        }
        else
        {
            string angle = OpenAngleNear(site);
            string covered = CoveredAngles(3);

            if (angle.Length > 0 && covered.Length > 0)
            {
                instruction = $"hold {angle} on {SiteName(site)}, we have {covered}";
            }
            else if (angle.Length > 0)
            {
                instruction = $"hold {angle} on {SiteName(site)}, we cover the rest";
            }
            else if (covered.Length > 0)
            {
                instruction = $"anywhere on {SiteName(site)}, we have {covered}";
            }
            else
            {
                instruction = $"hold {SiteName(site)} with us";
            }
        }

        // Sent by a different mouth to the plan, so it reads as two people
        // talking rather than one bot monologuing.
        KaiComms.ToHuman(team, -1, $"brief:{team}", instruction, 10.0f);
    }

    // The approach most of the side is taking, for telling the human where to
    // be. Named place or empty.
    private string BusiestApproach(int site)
    {
        var tally = new Dictionary<string, int>();

        foreach (var kv in _routeOf)
        {
            var route = kv.Value;

            if (route.Waypoints.Count < 2)
            {
                continue;
            }

            string where = KaiCallouts.ApproachName(
                route.Waypoints[route.Waypoints.Count / 2]);

            if (where.Length > 0)
            {
                tally[where] = tally.GetValueOrDefault(where) + 1;
            }
        }

        if (tally.Count == 0)
        {
            return "";
        }

        return tally.OrderByDescending(kv => kv.Value).First().Key;
    }

    // The named angles the bots are already covering, so the human is told what
    // is taken rather than only what is left. Naming them is the difference
    // between "we cover the rest" and knowing nobody is on Palace.
    private string CoveredAngles(int limit)
    {
        var names = new List<string>();

        foreach (var kv in _intents)
        {
            if (kv.Value.Watch == null || kv.Key == KaiSquad.HumanSlot)
            {
                continue;
            }

            string name = KaiCallouts.Nearest(kv.Value.Watch);

            if (name.Length > 0 && !names.Contains(name))
            {
                names.Add(name);
            }

            if (names.Count >= limit)
            {
                break;
            }
        }

        return string.Join(", ", names);
    }

    // A named angle near a site that nobody has been given, so the human can
    // be handed a real job rather than told to stand somewhere vague.
    private string OpenAngleNear(int site)
    {
        if (site < 0 || site >= _map.PlantSites.Count)
        {
            return "";
        }

        var centre = _map.PlantSites[site];

        // Places the bots are already covering, so the human is not sent to
        // double up on one of them.
        var taken = new HashSet<string>();

        foreach (var kv in _intents)
        {
            if (kv.Value.Watch == null)
            {
                continue;
            }

            string name = KaiCallouts.Nearest(kv.Value.Watch);

            if (name.Length > 0)
            {
                taken.Add(name);
            }
        }

        string best = "";
        float bestDist = float.MaxValue;

        foreach (var spot in _map.PreAim)
        {
            float d = spot.Trigger.DistanceXY(centre.X, centre.Y);

            if (d > 1400.0f || d < 250.0f)
            {
                continue;
            }

            string name = KaiCallouts.Nearest(spot.Trigger);

            if (name.Length == 0 || taken.Contains(name))
            {
                continue;
            }

            if (d < bestDist)
            {
                bestDist = d;
                best = name;
            }
        }

        return best;
    }

    // Count the side off by the direction each route approaches from.
    //
    // Reads as "two north, two east, one south". Derived from the routes
    // actually assigned, using the midpoint of each so it describes the
    // approach rather than the destination they all share.
    private string ApproachSpread(int site)
    {
        if (site < 0 || site >= _map.PlantSites.Count)
        {
            return "";
        }

        var tally = new Dictionary<string, int>();

        foreach (var kv in _routeOf)
        {
            var route = kv.Value;

            if (route.Waypoints.Count < 2)
            {
                continue;
            }

            // The midpoint names the approach rather than the destination,
            // which every route on the same play shares.
            var mid = route.Waypoints[route.Waypoints.Count / 2];
            string where = KaiCallouts.ApproachName(mid);

            if (where.Length == 0)
            {
                continue;
            }

            tally[where] = tally.GetValueOrDefault(where) + 1;
        }

        if (tally.Count == 0)
        {
            return "";
        }

        var parts = tally
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"{kv.Value} {kv.Key}")
            .ToList();

        return string.Join(", ", parts);
    }
    // A site index as something worth saying.
    //
    // Uses the letter the game reported at the plant, not the index. Indices
    // are assigned in the order sites were first planted on this map, so index
    // zero is whichever one somebody happened to take first. Mapping it to "A"
    // was a coin flip, and when it came up wrong the squad told the human to
    // take the bomb to B while the whole side executed A.
    //
    // Falls back to a compass bearing between the sites when no letter is
    // known, which at least points in the right direction.
    private string SiteName(int site)
    {
        if (site < 0)
        {
            return "the site";
        }

        if (site < _map.PlantSiteNames.Count && _map.PlantSiteNames[site].Length > 0)
        {
            return _map.PlantSiteNames[site];
        }

        // No letter recorded yet. Use the callout table if it names the place,
        // since "the site by Palace" is still something a person can act on.
        if (site < _map.PlantSites.Count)
        {
            string named = KaiCallouts.Nearest(_map.PlantSites[site]);

            if (named.Length > 0)
            {
                return named;
            }
        }

        return "the site";
    }


    // Run the game mode config, shortly after the map has settled.
    //
    // STOP_ON_MAPCHANGE matters here: without it a pending exec from a map
    // that was left early would fire onto whatever loaded next, applying one
    // mode's settings to another map's session.
    private void ScheduleAutoExec(string mapName)
    {
        if (!_autoExecEnabled || _autoExecConfig.Length == 0)
        {
            return;
        }

        AddTimer(_autoExecDelay, () =>
        {
            try
            {
                Server.ExecuteCommand($"exec {_autoExecConfig}");

                KaiLog.Event(nameof(ScheduleAutoExec),
                    $"executed '{_autoExecConfig}.cfg' on '{mapName}'. This runs once per map " +
                    $"load, not per round: these configs usually end with mp_restartgame and " +
                    $"running one mid-match would restart it.");
            }
            catch (Exception ex)
            {
                KaiLog.Event(nameof(ScheduleAutoExec),
                    $"could not execute '{_autoExecConfig}.cfg': {ex.Message}",
                    KaiLogLevel.Error);
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);

        KaiLog.Event(nameof(ScheduleAutoExec),
            $"'{_autoExecConfig}.cfg' will run in {_autoExecDelay:F1}s, once the map has settled");
    }

    // Gather what has actually been learned, for the maturity test.
    //
    // Read from the recorders themselves rather than from any clock, because
    // the question is not how long the map has been played but whether it has
    // stopped teaching anything.
    private KaiLearningEvidence BuildLearningEvidence()
    {
        var evidence = new KaiLearningEvidence
        {
            Engagements = _learner.EngagementCount,
            PostPlantSamples = _learner.PostPlantSamples,
            ClearSamples = _learner.ClearSamples,
            PreAimSamples = _learner.PreAimSamples,
            GraphSaturated = _crumbs.Saturated,
            GraphNodes = _crumbs.NodeCount,
            NewNodesThisSession = _crumbs.NewNodesThisSession,
        };

        // The least-tried play is what decides whether the book is learned.
        // One play called sixty times and another twice is a strong opinion
        // and a guess, not a record.
        var plays = _tactics.AllPlays();

        evidence.PlayCount = plays.Count;
        evidence.MinPlayCalls = int.MaxValue;

        foreach (var play in plays)
        {
            evidence.TotalPlayCalls += play.Called;

            if (play.Called < evidence.MinPlayCalls)
            {
                evidence.MinPlayCalls = play.Called;
            }
        }

        if (plays.Count == 0)
        {
            evidence.MinPlayCalls = 0;
        }

        return evidence;
    }

    // A completed match is what moves a map along its life cycle.
    //
    // Matches rather than rounds, because a match covers both sides, both
    // halves and every site, which is the unit over which "have we seen this
    // map" is actually meaningful. Ten of them stops the map recording and ten
    // more stops the play learning.
    private HookResult OnMatchEnd(EventCsWinPanelMatch @event, GameEventInfo info)
    {
        // Recorded for interest only. Nothing in the life cycle depends on it:
        // a match abandoned after three rounds still taught three rounds
        // worth, and counting matches would throw that away.
        _maturity.OnMatchEnd();
        _command.OnMatchEnd();

        // Persist everything at a natural boundary, whether or not it is
        // still being added to.
        _learner.SaveBank("match end");
        _crumbs.Save("match end");
        _tactics.Save();
        _maturity.Save();

        return HookResult.Continue;
    }

    // Being shot is the most direct evidence that a hold has been found. A bot
    // pinned in place while taking damage is worse than the stock behaviour
    // this replaced, so the pin comes off immediately.
    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        try
        {
            var victim = @event.Userid;

            if (victim == null || !victim.IsValid || !victim.IsBot)
            {
                return HookResult.Continue;
            }

            if (!_bombPlanted || (int)victim.TeamNum != (int)CsTeam.Terrorist)
            {
                return HookResult.Continue;
            }

            if (!_tWatchClaims.ContainsKey(victim.Slot))
            {
                return HookResult.Continue;
            }

            float until = Server.CurrentTime + _pressureSeconds;
            _tPressureUntil[victim.Slot] = until;

            KaiLog.Event(nameof(OnPlayerHurt),
                $"slot {victim.Slot} ('{victim.PlayerName}') took fire on its hold, " +
                $"pin released for {_pressureSeconds:F1}s");
        }
        catch (Exception ex)
        {
            KaiLog.Event(nameof(OnPlayerHurt), $"exception: {ex.Message}", KaiLogLevel.Error);
        }

        return HookResult.Continue;
    }

    // Every death is a free measurement for the learner, and a T death near
    // the bomb is also a signal to the Ts still holding nearby.
    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var victim = @event.Userid;
        var attacker = @event.Attacker;

        if (_maturity.RecordingMapData)
        {
            // A human in the engagement makes it unrepresentative while ghost
            // mode is on. Either side of the duel is enough to spoil it: a
            // human standing still is not a duel spot, and a human doing the
            // killing was not standing where a bot would.
            bool humanInvolved =
                (victim != null && victim.IsValid && !victim.IsBot)
                || (attacker != null && attacker.IsValid && !attacker.IsBot);

            if (_ghostHumans && humanInvolved)
            {
                _ghostDiscarded++;

                KaiLog.Throttled("ghostdrop", nameof(OnPlayerDeath),
                    $"ghost mode discarded an engagement involving a human " +
                    $"({_ghostDiscarded} so far this session)", 10.0f);
            }
            else
            {
                _learner.OnPlayerDeath(
                    victim, attacker, _bombPlanted, _bombPos,
                    MaxSpotDistanceFromBomb, CurrentRound());
            }
        }

        // Trades and losses, called out by position.
        //
        // Both matter to somebody playing alongside these bots. A kill tells
        // the side an angle just opened; a death tells it an angle just
        // closed, and where the shot came from. Neither was being said.
        ReportDeath(victim, attacker);

        // Running tallies for the play caller. It reads these to know when a
        // side no longer has the bodies for what it was doing.
        if (victim != null && victim.IsValid)
        {
            if ((int)victim.TeamNum == (int)CsTeam.Terrorist)
            {
                _friendlyDeaths++;
            }
            else
            {
                _enemyDeaths++;
            }
        }

        try
        {
            if (!_bombPlanted || victim == null || !victim.IsValid)
            {
                return HookResult.Continue;
            }

            if ((int)victim.TeamNum != (int)CsTeam.Terrorist)
            {
                return HookResult.Continue;
            }

            var deathOrigin = victim.PlayerPawn?.Value?.AbsOrigin;

            if (deathOrigin == null)
            {
                return HookResult.Continue;
            }

            var deathPoint = new KaiPoint(deathOrigin.X, deathOrigin.Y, deathOrigin.Z);

            // The dead bot's approach is free for someone else to claim.
            _tCover.Remove(victim.Slot);
            _glanceSet.Remove(victim.Slot);
            _tWatchBearings.Remove(victim.Slot);

            if (_tWatchClaims.Remove(victim.Slot))
            {
                KaiLog.Event(nameof(OnPlayerDeath),
                    $"slot {victim.Slot} died, its approach is free to be reclaimed");
            }

            _tPressureUntil.Remove(victim.Slot);

            RotateNeighbours(deathPoint, victim.Slot);
        }
        catch (Exception ex)
        {
            KaiLog.Event(nameof(OnPlayerDeath), $"exception: {ex.Message}", KaiLogLevel.Error);
        }

        return HookResult.Continue;
    }

    // A teammate dying nearby means the fight has reached this part of the
    // site. Everyone close enough drops their hold; those that had one get put
    // back into the assignment pool preferring spots away from the threat.
    private void RotateNeighbours(KaiPoint deathPoint, int deadSlot)
    {
        if (_bombPos == null)
        {
            return;
        }

        float now = Server.CurrentTime;
        var displaced = new List<int>();

        foreach (var bot in KaiPlayers.All())
        {
            if (bot == null || !bot.IsValid || !bot.IsBot || !bot.PawnIsAlive)
            {
                continue;
            }

            if (bot.Slot == deadSlot || (int)bot.TeamNum != (int)CsTeam.Terrorist)
            {
                continue;
            }

            var origin = bot.PlayerPawn?.Value?.AbsOrigin;

            if (origin == null)
            {
                continue;
            }

            float dist = deathPoint.DistanceXY(origin.X, origin.Y);

            if (dist > _pressureRadius)
            {
                continue;
            }

            _tPressureUntil[bot.Slot] = now + _pressureSeconds;

            // Drop the claim so the bot re-picks an approach once it settles
            // somewhere new, rather than trying to hold an angle it has walked
            // away from.
            if (_tWatchClaims.Remove(bot.Slot))
            {
                // Drop the cached cover spot too: it was solved for an angle
                // this bot no longer holds and a position it has left.
                _tCover.Remove(bot.Slot);
                _glanceSet.Remove(bot.Slot);
                _tWatchBearings.Remove(bot.Slot);
                displaced.Add(bot.Slot);
            }

            KaiLog.Event(nameof(RotateNeighbours),
                $"slot {bot.Slot} ('{bot.PlayerName}') is {dist:F0} units from a teammate death, " +
                $"abandoning its hold for {_pressureSeconds:F1}s");
        }

        if (displaced.Count > 0)
        {
            KaiLog.Event(nameof(RotateNeighbours),
                $"{displaced.Count} defender(s) released their approach and will reclaim " +
                $"once they settle somewhere with sight of the bomb");
        }
    }

    private bool IsUnderPressure(int slot, float now)
    {
        if (!_tPressureUntil.TryGetValue(slot, out float until))
        {
            return false;
        }

        if (now >= until)
        {
            _tPressureUntil.Remove(slot);
            KaiLog.Event(nameof(IsUnderPressure), $"slot {slot} pressure expired, hold may resume",
                KaiLogLevel.Verbose);
            return false;
        }

        return true;
    }

    // ------------------------------------------------------------------
    // Per-tick intent building
    //
    // Writes nothing to bot state. It only decides what each bot SHOULD be
    // doing; the actual writes happen inside the native hooks, which is what
    // makes this immune to tick listener ordering against BotState.
    // ------------------------------------------------------------------

    private void OnTick()
    {
        if (!_enabled)
        {
            return;
        }

        float now = Server.CurrentTime;

        ScanForLooseBomb(now);

        // Breadcrumbs are recorded whatever else is going on. They describe
        // where bots can walk, which is true regardless of what the plugin is
        // asking them to do, and the recorder skips anything that is not a
        // live round on its own.
        var rules = GameRules();
        bool roundLive = rules != null && !rules.WarmupPeriod && !rules.FreezePeriod;
        // Map recording stops once the map is known. Everything else carries
        // on: the bots keep using what was learned, the files just stop
        // growing.
        _crumbs.Tick(now, roundLive && _maturity.RecordingMapData);
        SampleSpawns(now, roundLive);
        DriveGhostMode(now);
        MaintainBombConverge(now);

        // Hands off entirely while the map is still being mapped.
        //
        // Everything above this line only observes. Everything below it steers
        // bots, and during MAPPING none of it should: there is nothing to
        // steer with, and steering would poison the very samples being
        // collected. The bots run on stock CS2 and ed0ard's stack until the
        // map is known.
        if (!_maturity.BehavioursActive)
        {
            if (_intents.Count > 0)
            {
                _intents.Clear();
            }

            KaiLog.Throttled("mappinghandsoff", nameof(OnTick),
                $"mapping '{_currentMap}': recording only, bots left on stock behaviour", 60.0f);

            return;
        }

        DriveTeamRotation(now);
        DriveDecoys(now);
        DriveExecute(now);
        ConsiderAudibles(now);

        // Who is fighting whom, before anybody is told what to look at.
        RefreshContacts(now);
        _arsenal.Scan(now);
        DriveDefuseWatchdog(now);

        PumpSolver(now);
        ConsiderAutoSolve(now);

        // A defuse in progress overrides every authored angle on the T side.
        // Holding a pre-aim while the bomb is being taken away is the exact
        // opposite of what a T should be doing, so they are handed back to the
        // native AI wholesale rather than gated frame by frame.
        bool defuseInProgress = _bombPlanted && KaiBombState.IsBeingDefused();

        // Clear every intent first so a bot that no longer qualifies for
        // anything falls back to stock behaviour on the same tick.
        foreach (var kv in _intents)
        {
            kv.Value.Reset(now);
        }

        foreach (var player in KaiPlayers.All())
        {
            if (player == null || !player.IsValid || !player.IsBot || player.IsHLTV)
            {
                continue;
            }

            if (!player.PawnIsAlive || player.HasBeenControlledByPlayerThisRound)
            {
                _intents.Remove(player.Slot);
                continue;
            }

            var pawn = player.PlayerPawn?.Value;

            if (pawn == null || !pawn.IsValid)
            {
                continue;
            }

            var origin = pawn.AbsOrigin;

            if (origin == null)
            {
                continue;
            }

            bool isTerrorist = (int)player.TeamNum == (int)CsTeam.Terrorist;

            if (defuseInProgress && isTerrorist)
            {
                // Nothing written at all. The bot goes and does something
                // about the defuse instead of holding an angle.
                _intents.Remove(player.Slot);

                KaiLog.Throttled($"tdefuse:{player.Slot}", nameof(OnTick),
                    $"slot {player.Slot} released, bomb is being defused", 2.0f);

                continue;
            }

            // Watchdog has given the CT side back to the native AI.
            if (_defuseWatchdogTripped
                && (int)player.TeamNum == (int)CsTeam.CounterTerrorist)
            {
                continue;
            }

            // Sticking the plant outranks everything, including a team mate's
            // fight. Nothing this plugin does is worth more than the bomb
            // going down, and a planter that gets pulled off to help with a
            // duel has thrown away the entire approach.
            bool handled = ApplyPlantCommitment(player, origin);

            // An empty gun beats every angle in the book, because a bot
            // holding one is not covering anything. Below the plant only:
            // finishing the bomb matters more than being armed afterwards.
            if (!handled)
            {
                handled = ApplyResupply(player, pawn, origin);
            }

            // A team mate's fight outranks any angle. Holding a corner while
            // the bot beside you duels an enemy in plain view is the exact
            // failure pre-aiming was meant to prevent.
            if (!handled)
            {
                handled = ApplyContactSupport(player, pawn, origin);
            }

            if (!handled)
            {
                handled = ApplyTerroristHold(player, pawn, origin, now);
            }

            if (!handled)
            {
                handled = ApplyLooseBombGuard(player, pawn, origin);
            }

            // A route outranks a pre-aim angle: a bot walking a route is
            // going somewhere, and the transit clearing inside ApplyRoute is
            // what keeps it clearing angles while it does.
            if (!handled)
            {
                handled = ApplyRoute(player, pawn, origin, GetOrCreateIntent(player.Slot));
            }

            if (!handled)
            {
                ApplyPreAim(player, pawn, origin);
            }
        }

        // Keep the pre-plant CT zoning in step with who is alive.
        if (!_bombPlanted)
        {
            RefreshCtZones(now);
        }

        // If the director gave up at plant time because no CT bots were alive
        // then, give it another chance now that one might be.
        if (_bombPlanted && _bombPos != null && !_defuseWatchdogTripped)
        {
            _retake.Retry(now, _bombPos, _map, MaxSpotDistanceFromBomb);
        }

        // CT side last, so it overrides any pre-aim applied above to a CT bot
        // that is now under retake direction. Bots already swinging onto a
        // team mate's fight are passed through untouched: a sweep that
        // continues while somebody is being shot at is worse than no sweep.
        var supporting = new HashSet<int>();

        foreach (var kv in _intents)
        {
            if (kv.Value.SourceName == "support")
            {
                supporting.Add(kv.Key);
            }
        }

        if (!_defuseWatchdogTripped)
        {
            _retake.Tick(now, GetOrCreateIntent, _botController, supporting);
        }

        DriveCelebrationFire(now);
    }

    // Surviving bots on the winning team empty a burst into the air.
    //
    // This is the only mechanism in the plugin that reaches a bot through
    // BotController's native usercmd injection rather than through a schema
    // write. If the CTs sweep their view but never shoot, the aim hook is
    // working and the injection path is not, which is the same path the fake
    // defuse uses.
    private void DriveCelebrationFire(float now)
    {
        if (!IsCelebrating())
        {
            return;
        }

        if (_botController == null)
        {
            KaiLog.Throttled("celebrate_noapi", nameof(DriveCelebrationFire),
                "BotController API unavailable, cannot inject the attack button. " +
                "The fake defuse will not work either.", 3.0f, KaiLogLevel.Error);
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
                continue;
            }

            if ((int)player.TeamNum != _celebrateTeam)
            {
                continue;
            }

            if (_nextCelebrateShot.TryGetValue(player.Slot, out float next) && now < next)
            {
                continue;
            }

            // Re-injected before the previous injection lapses, so the trigger
            // is effectively held for the whole celebration: an automatic
            // weapon runs continuously and a semi-automatic fires as fast as
            // it can cycle.
            _nextCelebrateShot[player.Slot] = now + 0.20f;

            long token = KaiBotControllerBridge.InjectUsercmd(
                _botController, player.Slot, (ulong)PlayerButtons.Attack, 300);

            if (token > 0)
            {
                KaiLog.Throttled($"celebrate_fire:{player.Slot}", nameof(DriveCelebrationFire),
                    $"slot {player.Slot} celebration shot injected, token {token}", 2.0f);
            }
            else
            {
                KaiLog.Throttled($"celebrate_fail:{player.Slot}", nameof(DriveCelebrationFire),
                    $"slot {player.Slot} celebration shot injection FAILED (token={token})",
                    2.0f, KaiLogLevel.Error);
            }
        }
    }

    private KaiBotIntent GetOrCreateIntent(int slot)
    {
        if (!_intents.TryGetValue(slot, out var intent))
        {
            intent = new KaiBotIntent();
            intent.Reset(Server.CurrentTime);
            _intents[slot] = intent;
        }

        intent.Stamp = Server.CurrentTime;
        return intent;
    }

    // Sweep the crosshair across every pre-aim spot visible from here.
    //
    // A bot holding one angle covers one angle. A bot that flicks between the
    // four or five known duel spots it can see covers all of them, because it
    // is looking at each of them often enough that anybody stepping into one
    // is seen within a fraction of a second. That is what a human does holding
    // a site, and it is why choosing positions by coverage score matters: the
    // score is how many angles this glance cycle will be able to include.
    //
    // The set is recomputed only when the bot settles somewhere new, since the
    // traces are the expensive part and a stationary bot's view does not
    // change.
    private bool ApplyGlanceSweep(
        CCSPlayerController player, CCSPlayerPawn pawn, KaiPoint position, float now,
        KaiBotIntent intent, string source)
    {
        // Stationary bots only.
        //
        // This sweep cycles every angle visible from a position, with no
        // regard for direction, because a bot holding a spot has no direction
        // of travel to respect. Applied to a moving bot it does exactly the
        // wrong thing, and every backwards-facing bot in a measured session
        // traced to this: the source on all of them was preaim:glance.
        //
        // A bot can be moving while its intent says anchored, because the pin
        // releases on contact and native pathing takes over while the watch
        // target stays behind.
        var moving = pawn.AbsVelocity;

        if (moving != null)
        {
            float speedSqr = (moving.X * moving.X) + (moving.Y * moving.Y);

            if (speedSqr > 2500.0f)
            {
                _glanceSet.Remove(player.Slot);

                KaiLog.Throttled($"glancemove:{player.Slot}", nameof(ApplyGlanceSweep),
                    $"slot {player.Slot} is moving, so it sweeps the way it is going rather " +
                    $"than the angles around where it was standing", 5.0f);

                // Its real position, not the post it was assigned. The post
                // is where it was told to stand; if it is moving then that is
                // no longer where it is.
                var actual = pawn.AbsOrigin;

                if (actual == null)
                {
                    return false;
                }

                return ApplyTransitClearing(player, pawn, actual, intent);
            }
        }

        if (!_glanceSet.TryGetValue(player.Slot, out var visible))
        {
            visible = new List<int>();

            CoverageScore(position, pawn.ViewOffset.Z, (int)player.TeamNum, visible);

            _glanceSet[player.Slot] = visible;
            _glanceIndex[player.Slot] = 0;
            _glanceNext[player.Slot] = now + _glanceDwell;

            KaiLog.Event(nameof(ApplyGlanceSweep),
                $"slot {player.Slot} holding a position that covers {visible.Count} " +
                $"pre-aim spot(s), sweeping between them every {_glanceDwell:F2}s");
        }

        if (visible.Count == 0)
        {
            return false;
        }

        if (!_glanceIndex.TryGetValue(player.Slot, out int cursor))
        {
            cursor = 0;
        }

        if (_glanceNext.TryGetValue(player.Slot, out float due) && now >= due)
        {
            cursor = (cursor + 1) % visible.Count;
            _glanceIndex[player.Slot] = cursor;
            _glanceNext[player.Slot] = now + _glanceDwell;
        }

        int spotIndex = visible[cursor % visible.Count];

        if (spotIndex < 0 || spotIndex >= _map.PreAim.Count)
        {
            return false;
        }

        var spot = _map.PreAim[spotIndex];

        intent.Watch = new KaiPoint(
            spot.Trigger.X, spot.Trigger.Y, spot.Trigger.Z + KaiHeights.Chest);
        intent.SourceName = $"{source}:glance:{spotIndex}";

        KaiLog.Throttled($"glance:{player.Slot}", nameof(ApplyGlanceSweep),
            $"slot {player.Slot} glancing at pre-aim spot {spotIndex} " +
            $"({cursor + 1} of {visible.Count} it can see)", 3.0f);

        return true;
    }
    // Which way this bot is actually travelling.
    //
    // Three sources, in order of how much they can be trusted.
    //
    // A route lookahead is best. A route is a piece of string and the next
    // waypoint on it can sit sideways or even behind the overall journey while
    // the string doubles back round a corner, so aiming at the next waypoint
    // alone points a bot backwards on exactly the winding sections where
    // seeing forwards matters most. Looking several waypoints down the path
    // gives the direction of the JOURNEY rather than of the next step.
    //
    // Velocity is next, and it is the one that was missing. A bot moving under
    // native pathing has no waypoints of ours at all, so nothing knew which
    // way it was going and the aim override was free to point it backwards.
    // Its own velocity always knows.
    //
    // Current facing is the last resort, used only when genuinely stationary.
    private float TravelHeading(CCSPlayerController player, CCSPlayerPawn pawn, Vector origin)
    {
        // 1. Look down the route, not at the next step.
        if (_routeOf.TryGetValue(player.Slot, out var route)
            && _routeLeg.TryGetValue(player.Slot, out int leg)
            && route.Waypoints.Count > 0)
        {
            bool reversing = _routeReversing.GetValueOrDefault(player.Slot);

            int target = leg;
            float walked = 0.0f;
            var from = new KaiPoint(origin.X, origin.Y, origin.Z);

            // Walk forward along the path until far enough away that local
            // wiggles cannot dominate the bearing.
            for (int step = 0; step < _headingLookahead; step++)
            {
                int index = reversing ? leg - step : leg + step;

                if (index < 0 || index >= route.Waypoints.Count)
                {
                    break;
                }

                var point = route.Waypoints[index];
                walked += point.DistanceXY(from.X, from.Y);
                from = point;
                target = index;

                if (walked >= _headingLookaheadUnits)
                {
                    break;
                }
            }

            if (target >= 0 && target < route.Waypoints.Count)
            {
                var aim = route.Waypoints[target];

                if (aim.DistanceXY(origin.X, origin.Y) > 40.0f)
                {
                    return KaiFormation.Bearing(origin.X, origin.Y, aim.X, aim.Y);
                }
            }
        }

        // 2. Where it is actually moving, which covers native pathing.
        var velocity = pawn.AbsVelocity;

        if (velocity != null)
        {
            float speedSqr = (velocity.X * velocity.X) + (velocity.Y * velocity.Y);

            if (speedSqr > _movingSpeedSqr)
            {
                return KaiFormation.Bearing(0.0f, 0.0f, velocity.X, velocity.Y);
            }
        }

        // 3. Standing still. Where it is looking is as good as it gets.
        return pawn.EyeAngles.Y;
    }

    // Sweep the angles ahead while moving.
    //
    // Previously this swept every visible angle regardless of direction, so
    // bots walking a route kept turning round to look at corners they had
    // already walked through. That is not clearing, it is post-aiming: the
    // danger from a spot you have already passed and survived is far lower
    // than from the one you are about to walk into, and a bot facing backwards
    // while moving forwards loses every duel it walks into.
    //
    // The sweep is therefore limited to a forward arc taken from the DIRECTION
    // OF TRAVEL rather than from where the bot is currently looking. Using the
    // current facing would let the arc drift round with the crosshair and
    // defeat itself within a couple of flicks.
    //
    // One bot per group is exempt and sweeps the full circle. Somebody has to
    // watch the way back, and the natural choice is whoever is furthest from
    // the shared destination, because they are already at the rear.
    private bool ApplyTransitClearing(
        CCSPlayerController player, CCSPlayerPawn pawn, Vector origin, KaiBotIntent intent)
    {
        if (_map.PreAim.Count == 0)
        {
            return false;
        }

        float now = Server.CurrentTime;
        var eye = new Vector(origin.X, origin.Y, origin.Z + pawn.ViewOffset.Z);
        int team = (int)player.TeamNum;

        float heading = TravelHeading(player, pawn, origin);

        if (!_transitNext.TryGetValue(player.Slot, out float due) || now >= due)
        {
            var found = new List<(int Index, float Distance, float OffHeading)>();

            for (int i = 0; i < _map.PreAim.Count; i++)
            {
                var spot = _map.PreAim[i];

                if (spot.Team != 0 && spot.Team != team)
                {
                    continue;
                }

                float dist = spot.Trigger.DistanceXY(origin.X, origin.Y);

                if (dist > _coverageRange)
                {
                    continue;
                }

                // Forward arc only, no exceptions.
                //
                // There used to be a rear guard exempt from this, and it was a
                // mistake twice over. It returned true whenever it could not
                // find somebody further back, which on a thinned-out side is
                // everybody, so bots walked entire routes facing the way they
                // had come. And even when it picked one correctly, a bot
                // moving forwards while looking backwards loses the duel it is
                // walking into to save one it has already survived.
                float bearing = KaiFormation.Bearing(
                    origin.X, origin.Y, spot.Trigger.X, spot.Trigger.Y);

                float offHeading = KaiFormation.AngleGap(bearing, heading);

                if (offHeading > _transitArcDeg)
                {
                    continue;
                }

                var target = new Vector(
                    spot.Trigger.X, spot.Trigger.Y, spot.Trigger.Z + KaiHeights.Chest);

                if (!KaiRayTraceBridge.CanSee(eye, target))
                {
                    continue;
                }

                found.Add((i, dist, offHeading));
            }

            // Most directly ahead first, then nearest.
            //
            // Sorting on distance alone put the crosshair on whatever was
            // closest, which while moving is frequently a spot beside or just
            // behind the bot. What matters walking into a site is what is in
            // front of it.
            found.Sort((a, b) =>
            {
                int byAngle = a.OffHeading.CompareTo(b.OffHeading);
                return byAngle != 0 ? byAngle : a.Distance.CompareTo(b.Distance);
            });

            _transitSet[player.Slot] = found.Select(f => f.Index).ToList();
            _transitNext[player.Slot] = now + _transitRescanSeconds;

            KaiLog.Throttled($"transitscan:{player.Slot}", nameof(ApplyTransitClearing),
                $"slot {player.Slot} sweeping {_transitSet[player.Slot].Count} angle(s) " +
                $"ahead of it, within {_transitArcDeg:F0} degrees of heading " +
                $"{heading:F0}", 3.0f);
        }

        if (!_transitSet.TryGetValue(player.Slot, out var visible) || visible.Count == 0)
        {
            return false;
        }

        if (!_transitIndex.TryGetValue(player.Slot, out int cursor))
        {
            cursor = 0;
        }

        if (!_transitFlick.TryGetValue(player.Slot, out float flick) || now >= flick)
        {
            cursor = (cursor + 1) % visible.Count;
            _transitIndex[player.Slot] = cursor;
            _transitFlick[player.Slot] = now + _transitFlickSeconds;
        }

        int spotIndex = visible[cursor % visible.Count];

        if (spotIndex < 0 || spotIndex >= _map.PreAim.Count)
        {
            return false;
        }

        var pick = _map.PreAim[spotIndex];

        intent.Watch = new KaiPoint(
            pick.Trigger.X, pick.Trigger.Y, pick.Trigger.Z + KaiHeights.Chest);

        // Slow to a walk near a known angle, but never during the final push
        // of a synchronised execute: that one is meant to be fast and
        // simultaneous, and walking it in hands the defence the staggered
        // arrival the staging exists to prevent.
        if (_walkNearAngles && _command.Phase != KaiExecutePhase.Committed)
        {
            float nearest = float.MaxValue;

            foreach (int index in visible)
            {
                float d = _map.PreAim[index].Trigger.DistanceXY(origin.X, origin.Y);

                if (d < nearest)
                {
                    nearest = d;
                }
            }

            if (nearest <= _walkNearDistance)
            {
                intent.Walk = true;

                KaiLog.Throttled($"walkangle:{player.Slot}", nameof(ApplyTransitClearing),
                    $"slot {player.Slot} slowing to a walk, a known angle is {nearest:F0} " +
                    $"units ahead", 3.0f);
            }
        }

        KaiLog.Throttled($"transit:{player.Slot}", nameof(ApplyTransitClearing),
            $"slot {player.Slot} clearing pre-aim spot {spotIndex} ahead " +
            $"({cursor + 1} of {visible.Count} in the arc)", 2.0f);

        return true;
    }

    // Stick the plant.
    //
    // The mirror of the defuse commitment, and it did not exist at all. A T
    // that starts arming and then breaks off to fight has spent the whole
    // approach for nothing: the plant is the round, and a team mate on the
    // same site is far better placed to take that duel than somebody standing
    // still holding a bomb.
    //
    // So while the bar is running and somebody friendly is on the site, the
    // planter is pinned and left alone. Being shot at is not a reason to stop.
    // It is the reason team mates are there.
    //
    // Alone is the exception, matching the defuse rule, and it has the same
    // exception of its own: within a second of finishing, stopping is the only
    // way left to lose it.
    private bool ApplyPlantCommitment(CCSPlayerController player, Vector origin)
    {
        if (!_stickThePlant || _bombPlanted)
        {
            return false;
        }

        if ((int)player.TeamNum != (int)CsTeam.Terrorist)
        {
            return false;
        }

        bool arming = IsArming(player, out float armingLeft);

        if (!arming)
        {
            _plantCommittedSince.Remove(player.Slot);
            return false;
        }

        float now = Server.CurrentTime;

        if (!_plantCommittedSince.ContainsKey(player.Slot))
        {
            _plantCommittedSince[player.Slot] = now;
        }

        int covering = CountLivingSiteMates(player, origin);
        bool nearlyDone = armingLeft >= 0.0f && armingLeft <= 1.0f;

        if (covering == 0 && !nearlyDone)
        {
            KaiLog.Throttled($"plantalone:{player.Slot}", nameof(ApplyPlantCommitment),
                $"slot {player.Slot} is planting alone with {armingLeft:F1}s to go and nobody " +
                $"on the site, leaving it free to break off", 3.0f);

            return false;
        }

        var intent = GetOrCreateIntent(player.Slot);

        intent.Anchored = true;
        intent.SuppressUse = false;
        intent.SourceName = "planting:committed";

        // Same reasoning as the defuse: the steering block in the movement
        // hook runs before the pin, so a destination left over from an earlier
        // branch this tick moves the bot whatever the pin says.
        intent.SteerTowards = null;
        intent.Walk = false;
        intent.Erratic = false;

        // The view is left alone on purpose. Dragging it around mid-plant
        // achieves nothing and only makes the bot worse at shooting back if
        // the plant does get interrupted.
        KaiComms.CallBy(player.Slot, "planting",
            covering > 0 ? "planting, cover me" : "planting, almost there", 8.0f);

        KaiLog.Throttled($"planting:{player.Slot}", nameof(ApplyPlantCommitment),
            $"slot {player.Slot} is committed to the plant: {armingLeft:F1}s to go, " +
            $"{covering} team mate(s) on the site" +
            (nearlyDone && covering == 0 ? " and alone but too close to stop" : ""), 2.0f);

        return true;
    }

    // Is this bot mid-plant, and how long is left on it.
    private bool IsArming(CCSPlayerController player, out float secondsRemaining)
    {
        secondsRemaining = -1.0f;

        try
        {
            var pawn = player.PlayerPawn?.Value;
            var weapons = pawn?.WeaponServices?.MyWeapons;

            if (weapons == null)
            {
                return false;
            }

            foreach (var handle in weapons)
            {
                var weapon = handle?.Value;

                if (weapon == null || !weapon.IsValid)
                {
                    continue;
                }

                if (weapon.DesignerName != "weapon_c4")
                {
                    continue;
                }

                var c4 = new CC4(weapon.Handle);

                if (!c4.StartedArming)
                {
                    return false;
                }

                // m_fArmedTime is the absolute time the plant completes.
                float remaining = c4.ArmedTime - Server.CurrentTime;
                secondsRemaining = remaining < 0.0f ? 0.0f : remaining;

                return true;
            }
        }
        catch (Exception ex)
        {
            KaiLog.Throttled("arming", nameof(IsArming),
                $"could not read the arming state: {ex.Message}", 30.0f, KaiLogLevel.Error);
        }

        return false;
    }

    // Living team mates near enough to be taking the fights that let this bot
    // stand still. Site-local rather than team-wide: somebody across the map is
    // not covering a plant.
    private int CountLivingSiteMates(CCSPlayerController player, Vector origin)
    {
        int team = (int)player.TeamNum;
        int count = 0;

        foreach (var p in KaiPlayers.All())
        {
            if (p == null || !p.IsValid || p.Slot == player.Slot || p.IsHLTV)
            {
                continue;
            }

            if (!p.PawnIsAlive || (int)p.TeamNum != team)
            {
                continue;
            }

            var mateOrigin = p.PlayerPawn?.Value?.AbsOrigin;

            if (mateOrigin == null)
            {
                continue;
            }

            float dx = mateOrigin.X - origin.X;
            float dy = mateOrigin.Y - origin.Y;

            if ((dx * dx) + (dy * dy) <= _siteMateRadius * _siteMateRadius)
            {
                count++;
            }
        }

        return count;
    }

    // Is this bot mid-plant or mid-defuse?
    //
    // Read from the game rather than from our own intent, so it is true even
    // on the tick before the commitment has been written and regardless of
    // which branch happens to run first.
    private bool IsOnTheObjective(CCSPlayerController player)
    {
        // Defusing: the bar is running and this is the bot on it.
        if (_bombPlanted && KaiBombState.IsBeingDefused())
        {
            var pawn = player.PlayerPawn?.Value;
            var origin = pawn?.AbsOrigin;

            if (origin != null && _bombPos != null
                && (int)player.TeamNum == (int)CsTeam.CounterTerrorist
                && _bombPos.DistanceXY(origin.X, origin.Y) <= 100.0f)
            {
                return true;
            }
        }

        // Planting.
        if (!_bombPlanted
            && (int)player.TeamNum == (int)CsTeam.Terrorist
            && IsArming(player, out _))
        {
            return true;
        }

        return false;
    }

    // A bot with nothing left to shoot.
    //
    // Ranked above everything except the plant, because a bot out of ammo
    // contributes nothing to any of it. Holding an angle with an empty rifle
    // is not holding an angle.
    //
    // Two answers depending on whether it is safe. Clear, and it goes and
    // collects something. In a fight, and it draws the knife and commits,
    // because standing still holding an empty gun loses with certainty while
    // moving erratically at close range sometimes does not.
    private bool ApplyResupply(CCSPlayerController player, CCSPlayerPawn pawn, Vector origin)
    {
        if (!_arsenal.Enabled)
        {
            return false;
        }

        // Never pull somebody off the objective for a gun.
        //
        // The plant and the defuse both outrank being armed: a bot that walks
        // away from a running bar to collect a rifle has lost the round to win
        // a gunfight it no longer needs to take. Checked here rather than
        // relying on ordering, because the resupply sets a destination and the
        // commitment further down only sets a pin.
        if (IsOnTheObjective(player))
        {
            KaiLog.Throttled($"nofetch:{player.Slot}", nameof(ApplyResupply),
                $"slot {player.Slot} is on the objective, so it is not going anywhere for a gun",
                5.0f);

            return false;
        }

        if (!_arsenal.IsDry(player, out bool hasAnyGun))
        {
            // Re-armed since last tick, most likely by walking over something.
            if (_arsenal.ClaimOf(player.Slot) >= 0)
            {
                int had = _arsenal.ClaimOf(player.Slot);
                _arsenal.Release(player.Slot);
                _arsenal.Forget(had, "somebody picked it up");

                KaiComms.DetailBy(player.Slot, $"rearmed:{player.Slot}", "got a gun", 6.0f);
            }

            if (_arsenal.IsKnifing(player.Slot))
            {
                _arsenal.StopKnifing(player.Slot);
                RestoreBestWeapon(player);
            }

            return false;
        }

        if (!hasAnyGun)
        {
            // Nothing but a knife to begin with, which is a pistol round or a
            // bot that has already thrown everything away. Not a resupply
            // problem.
            return false;
        }

        var bot = pawn.Bot;
        bool inFight = bot != null && (bot.IsEnemyVisible || bot.IsAttacking);

        float now = Server.CurrentTime;

        if (inFight)
        {
            return KnifeRush(player, pawn, origin, now);
        }

        // Clear. Go and get something.
        if (_arsenal.IsKnifing(player.Slot))
        {
            _arsenal.StopKnifing(player.Slot);
        }

        int claimed = _arsenal.ClaimOf(player.Slot);
        KaiDroppedWeapon? target = claimed >= 0 ? _arsenal.Get(claimed) : null;

        if (target == null)
        {
            target = _arsenal.NearestUseful(origin, player.Slot);

            if (target != null)
            {
                _arsenal.Claim(player.Slot, target.EntityIndex);

                string where = target.Callout.Length > 0 ? target.Callout : "the open";

                KaiComms.CallBy(player.Slot, $"fetch:{player.Slot}",
                    $"dry, going for the {target.ShortName} at {where}", 8.0f);

                KaiLog.Event(nameof(ApplyResupply),
                    $"slot {player.Slot} is dry and heading for the {target.ShortName} at " +
                    $"{where}, {target.Position.DistanceXY(origin.X, origin.Y):F0} units away");
            }
        }

        if (target == null)
        {
            KaiLog.Throttled($"nogun:{player.Slot}", nameof(ApplyResupply),
                $"slot {player.Slot} is dry and nothing is known to be on the floor within " +
                $"{_arsenal.PickupRange:F0} units", 10.0f);

            return false;
        }

        float distance = target.Position.DistanceXY(origin.X, origin.Y);

        if (distance <= _arsenal.PickupArriveRadius)
        {
            // Standing on it. CS2 picks weapons up on contact, so arriving is
            // the whole action; if it is still there next tick the sweep will
            // notice and this repeats.
            return false;
        }

        var intent = GetOrCreateIntent(player.Slot);

        intent.SteerTowards = target.Position;
        intent.SourceName = $"resupply:{target.ShortName}";

        // Still clear the angles on the way. A dry bot walking into a fight it
        // did not see is worse off than one that stayed put.
        if (!ApplyTransitClearing(player, pawn, origin, intent))
        {
            intent.Watch = new KaiPoint(
                target.Position.X, target.Position.Y, target.Position.Z + KaiHeights.Chest);
        }

        KaiLog.Throttled($"resupply:{player.Slot}", nameof(ApplyResupply),
            $"slot {player.Slot} collecting the {target.ShortName}, {distance:F0} units to go",
            3.0f);

        return true;
    }

    // Out of ammo with an enemy in view.
    //
    // The knife is not a good answer. It is the only one: an empty gun cannot
    // win and running away in a straight line from somebody who can shoot is
    // the same as standing still. So the bot closes, moves unpredictably, and
    // takes the chance. A knife kill also solves the ammunition problem, since
    // whatever the victim was holding lands on the floor.
    private bool KnifeRush(CCSPlayerController player, CCSPlayerPawn pawn, Vector origin, float now)
    {
        if (_arsenal.BeginKnifing(player.Slot, now))
        {
            KaiComms.CallBy(player.Slot, $"knife:{player.Slot}",
                "out of ammo, going in with the knife", 10.0f);

            KaiLog.Event(nameof(KnifeRush),
                $"slot {player.Slot} is dry in a fight and has drawn the knife");
        }

        // Force the knife out. Native weapon selection will keep picking the
        // empty rifle otherwise, because as far as it is concerned that is
        // still the best gun it owns.
        try
        {
            player.ExecuteClientCommand("slot3");
        }
        catch (Exception ex)
        {
            KaiLog.Throttled($"knifeswitch:{player.Slot}", nameof(KnifeRush),
                $"could not switch to the knife: {ex.Message}", 30.0f, KaiLogLevel.Error);
        }

        var bot = pawn.Bot;
        var enemy = bot?.Enemy?.Value;
        var enemyOrigin = enemy?.AbsOrigin;

        if (enemy == null || enemyOrigin == null)
        {
            return false;
        }

        var intent = GetOrCreateIntent(player.Slot);

        // Straight at them, looking at them. Everything else is the native
        // AI's business.
        intent.SteerTowards = new KaiPoint(enemyOrigin.X, enemyOrigin.Y, enemyOrigin.Z);
        intent.Watch = new KaiPoint(
            enemyOrigin.X, enemyOrigin.Y, enemyOrigin.Z + KaiHeights.Chest);
        intent.ForceAim = true;
        intent.Erratic = true;
        intent.SourceName = "knife_rush";

        // DistanceXY belongs to KaiPoint, not to the engine's Vector, so the
        // gap is measured here rather than borrowed from a type that has no
        // such method.
        float dx = enemyOrigin.X - origin.X;
        float dy = enemyOrigin.Y - origin.Y;
        float gap = MathF.Sqrt((dx * dx) + (dy * dy));

        KaiLog.Throttled($"knifing:{player.Slot}", nameof(KnifeRush),
            $"slot {player.Slot} closing with the knife, {gap:F0} units to the enemy", 2.0f);

        return true;
    }

    // Put the best available gun back in its hands.
    private static void RestoreBestWeapon(CCSPlayerController player)
    {
        try
        {
            // slot1 is the primary, slot2 the pistol. Asking for the primary
            // first and letting the game refuse is simpler than working out
            // which one has ammunition.
            player.ExecuteClientCommand("slot1");
            player.ExecuteClientCommand("slot2");
        }
        catch (Exception ex)
        {
            KaiLog.Throttled($"restore:{player.Slot}", nameof(RestoreBestWeapon),
                $"could not switch back off the knife: {ex.Message}", 30.0f, KaiLogLevel.Error);
        }
    }

    // Returns true if this bot is under T post-plant direction.
    //
    // The defence is a ring. Each defender owns an evenly spaced bearing from
    // the bomb, walks out along it to the furthest position that still has an
    // unobstructed view of the bomb, backs into cover there, and watches the
    // bomb itself.
    //
    // Watching the bomb rather than an approach is the deliberate part. A
    // defender covering a corridor sees whoever comes down that corridor; a
    // ring of defenders all watching the bomb sees whoever reaches it, from
    // whichever direction they came, and between them they cover every
    // approach without needing to know what the approaches are.
    private bool ApplyTerroristHold(
        CCSPlayerController player, CCSPlayerPawn pawn, Vector origin, float now)
    {
        if (!_bombPlanted || _bombPos == null)
        {
            return false;
        }

        if ((int)player.TeamNum != (int)CsTeam.Terrorist)
        {
            return false;
        }

        if (IsUnderPressure(player.Slot, now))
        {
            var pressured = GetOrCreateIntent(player.Slot);
            pressured.SourceName = "t_hold:underfire";

            KaiLog.Throttled($"tpressure:{player.Slot}", nameof(ApplyTerroristHold),
                $"slot {player.Slot} under fire, hold suspended", 2.0f);

            return true;
        }

        float bombDist = _bombPos.DistanceXY(origin.X, origin.Y);

        if (bombDist > _tHoldNearBombRadius)
        {
            return false;
        }

        if (!_tSectors.TryGetValue(player.Slot, out float bearing))
        {
            // No arc assigned, most likely because this bot spawned in or
            // changed team after the plant. Use where it already is, so it
            // holds rather than crossing the site to an arbitrary bearing.
            bearing = KaiFormation.Bearing(_bombPos.X, _bombPos.Y, origin.X, origin.Y);
            _tSectors[player.Slot] = bearing;
        }

        // Solved once per round per bot. A destination that keeps being
        // recomputed is one the bot never arrives at.
        if (!_tCover.TryGetValue(player.Slot, out var post))
        {
            // A pre-solved post beats anything derived live. The live search
            // starts from wherever this bot happens to be standing, so its
            // answer depends on that accident; the solver considered every
            // standable position on the site against every known angle.
            var resolved = ClaimSolvedPost(player, _bombPos, bearing);

            if (resolved == null)
            {
                resolved = ResolveRingPosition(player, pawn, origin, _bombPos, bearing)
                           ?? ResolveFallbackPost(player, pawn, origin, _bombPos, bearing);
            }

            if (resolved == null)
            {
                // Nothing standable anywhere on this bearing. Rather than pin
                // the bot where it is, which for the planter means standing on
                // the bomb, leave it entirely to the native AI.
                KaiLog.Throttled($"tnopost:{player.Slot}", nameof(ApplyTerroristHold),
                    $"slot {player.Slot} has nowhere to post on bearing {bearing:F0}, " +
                    $"leaving it to the native AI", 5.0f);

                return false;
            }

            post = resolved;
            _tCover[player.Slot] = post;
        }

        var intent = GetOrCreateIntent(player.Slot);

        float toPost = post.DistanceXY(origin.X, origin.Y);

        if (toPost > 60.0f)
        {
            intent.SteerTowards = post;
            intent.SourceName = "t_hold:to_post";

            // Clear the angles crossed on the way rather than staring at the
            // destination. Falls back to watching the bomb when there is no
            // known angle in view.
            if (!ApplyTransitClearing(player, pawn, origin, intent))
            {
                intent.Watch = new KaiPoint(
                    _bombPos.X, _bombPos.Y, _bombPos.Z + KaiHeights.BombWatch);
            }

            KaiLog.Throttled($"tpost:{player.Slot}", nameof(ApplyTerroristHold),
                $"slot {player.Slot} moving to its ring post on bearing {bearing:F0}, " +
                $"{toPost:F0} units to go", 2.0f);

            return true;
        }

        intent.Anchored = true;
        intent.SourceName = "t_hold:ring";

        // Sweep between every angle this post covers. Watching only the bomb
        // covers one line; sweeping covers everything the position was chosen
        // for in the first place.
        if (!ApplyGlanceSweep(player, pawn, post, now, intent, "t_hold"))
        {
            intent.Watch = new KaiPoint(
                _bombPos.X, _bombPos.Y, _bombPos.Z + KaiHeights.BombWatch);
        }

        string watching = intent.Watch != null
            ? KaiCallouts.Describe(intent.Watch, _bombPos)
            : KaiCallouts.Describe(post, _bombPos);

        // Promoted from Detail to a proper call. Which entry each defender has
        // is the whole picture of a post-plant hold, and it is the thing a
        // human joining that hold most needs: it says which way in is covered
        // and, by omission, which is not.
        KaiComms.Call((int)CsTeam.Terrorist, player.Slot, $"tring:{player.Slot}",
            $"holding {KaiCallouts.Describe(post, _bombPos)}, watching {watching}", 8.0f);

        KaiLog.Throttled($"thold:{player.Slot}", nameof(ApplyTerroristHold),
            $"slot {player.Slot} holding its ring post on bearing {bearing:F0}, " +
            $"{bombDist:F0} units from the bomb", 3.0f);

        return true;
    }

    // Start a solve by itself when it is both possible and needed.
    //
    // Needed means one of two things: nothing has ever been solved for this
    // map, or the tactics file has been rebuilt since the last solve. That
    // second case matters because the solve scores positions against the
    // pre-aim set, so a kai_learn build silently invalidates every solved post
    // and leaving them in place would have bots holding positions chosen for
    // angles that no longer exist.
    private void ConsiderAutoSolve(float now)
    {
        if (!_autoSolve || now < _nextAutoSolveCheck)
        {
            return;
        }

        _nextAutoSolveCheck = now + 5.0f;

        // Already working, or already queued.
        if (_solver.Stage != KaiSolveStage.Idle || _solveQueue.Count > 0)
        {
            return;
        }

        // Nothing is solved while the map is still being mapped: the
        // positions would be scored against a graph and an angle set that are
        // both still moving.
        if (!_maturity.BehavioursActive)
        {
            return;
        }

        // Only during freezetime or warmup. The solve spends a trace budget
        // every tick and clears live assignments when it lands, neither of
        // which belongs in the middle of a round.
        if (!IsSafeBuildPhase(out string phase))
        {
            return;
        }

        // Prerequisites. Logged at most once per condition change rather than
        // every five seconds, because on a fresh map none of them are met for
        // a long time and that is expected rather than a fault.
        if (!_crumbs.IsUsable)
        {
            KaiLog.Throttled("autosolve_crumbs", nameof(ConsiderAutoSolve),
                "auto solve waiting: the breadcrumb graph is not usable yet", 120.0f);
            return;
        }

        if (_map.PreAim.Count == 0)
        {
            KaiLog.Throttled("autosolve_angles", nameof(ConsiderAutoSolve),
                "auto solve waiting: no pre-aim data yet, run kai_learn build", 120.0f);
            return;
        }

        if (_map.PlantSites.Count == 0)
        {
            KaiLog.Throttled("autosolve_sites", nameof(ConsiderAutoSolve),
                "auto solve waiting: no bombsite has been recorded yet, which needs at least " +
                "one plant to happen", 120.0f);
            return;
        }

        bool neverSolved = string.IsNullOrEmpty(_map.SolvedUtc);
        bool staleAgainstBuild =
            !neverSolved
            && !string.IsNullOrEmpty(_map.GeneratedUtc)
            && string.CompareOrdinal(_map.SolvedUtc, _map.GeneratedUtc) < 0;

        // A site recorded after the last solve has no posts of its own.
        bool missingSite = false;

        if (!neverSolved)
        {
            for (int i = 0; i < _map.PlantSites.Count; i++)
            {
                bool covered = false;

                foreach (var post in _map.SolvedTPosts)
                {
                    if (post.SiteIndex == i)
                    {
                        covered = true;
                        break;
                    }
                }

                if (!covered)
                {
                    missingSite = true;
                    break;
                }
            }
        }

        if (!neverSolved && !staleAgainstBuild && !missingSite)
        {
            return;
        }

        string why;

        if (neverSolved)
        {
            why = "this map has never been solved";
        }
        else if (staleAgainstBuild)
        {
            why = $"the tactics file was rebuilt at {_map.GeneratedUtc}, after the last solve " +
                  $"at {_map.SolvedUtc}, so the solved posts were chosen against angles that " +
                  $"may no longer exist";
        }
        else
        {
            why = "a bombsite has been recorded that has no solved posts";
        }

        KaiLog.Event(nameof(ConsiderAutoSolve),
            $"starting an automatic solve during {phase}: {why}");

        BeginSolveQueue(null);
    }

    // Build and start the queue of solves: one pass per bombsite for the Ts,
    // then one for the CT early round.
    private bool BeginSolveQueue(CCSPlayerController? caller)
    {
        _map.SolvedTPosts.Clear();
        _map.SolvedCtPosts.Clear();
        _solveQueue.Clear();
        _solveCaller = caller;

        for (int i = 0; i < _map.PlantSites.Count; i++)
        {
            _solveQueue.Add(((int)CsTeam.Terrorist, i));
        }

        _solveQueue.Add(((int)CsTeam.CounterTerrorist, -1));

        var first = _solveQueue[0];
        _solveQueue.RemoveAt(0);

        return StartSolve(first.Team, first.Site);
    }

    // Advance the pre-solve, if one is running.
    //
    // Spread across ticks rather than done in one go: a few hundred candidate
    // positions against thirty-odd angles is tens of thousands of traces, and
    // doing that in a single frame would hitch the server badly enough to be
    // worse than not having the feature.
    private void PumpSolver(float now)
    {
        if (_solver.Stage == KaiSolveStage.Idle)
        {
            return;
        }

        if (_solver.Stage == KaiSolveStage.Scoring)
        {
            _solver.Pump(now);
            return;
        }

        if (_solver.Stage != KaiSolveStage.Selecting)
        {
            return;
        }

        string stamp = KaiTime.NowUtc();
        var posts = _solver.Select(stamp);

        if (posts.Count > 0)
        {
            if (posts[0].Team == (int)CsTeam.Terrorist)
            {
                _map.SolvedTPosts.AddRange(posts);
            }
            else
            {
                _map.SolvedCtPosts.AddRange(posts);
            }
        }

        _solver.Reset();

        if (_solveQueue.Count > 0)
        {
            var next = _solveQueue[0];
            _solveQueue.RemoveAt(0);
            StartSolve(next.Team, next.Site);
            return;
        }

        // Queue exhausted. Routes come next, because they are generated
        // between the solved posts and the plant sites and so need the solve
        // to have finished first.
        GenerateRoutes();

        _map.SolvedUtc = stamp;
        _map.MapName = _currentMap;

        bool saved = KaiTacticsLoader.Save(DataDir, _map, "kai_solve");

        // Any live assignments were computed under the old data.
        _tCover.Clear();
        _glanceSet.Clear();

        string message =
            $"solve complete: {_map.SolvedTPosts.Count} T post(s) across " +
            $"{_map.PlantSites.Count} site(s), {_map.SolvedCtPosts.Count} CT post(s), " +
            $"written to disk ({saved})";

        KaiLog.Event(nameof(PumpSolver), message);

        _solveCaller?.PrintToConsole($"[KaiTactics] {message}");
        _solveCaller = null;
    }

    // Build the route book from the graph, once the posts are solved.
    private void GenerateRoutes()
    {
        if (_spawns.Count == 0)
        {
            KaiLog.Event(nameof(GenerateRoutes),
                "no spawn positions learned yet, so routes cannot be anchored. They will be " +
                "generated on the next solve after a round has gone live.",
                KaiLogLevel.Error);
            return;
        }

        if (_map.PlantSites.Count == 0)
        {
            KaiLog.Event(nameof(GenerateRoutes), "no bombsites recorded, cannot route to them",
                KaiLogLevel.Error);
            return;
        }

        var book = KaiRouteGenerator.Generate(_currentMap, _crumbs, _map, _spawns);

        if (book.Routes.Count == 0)
        {
            KaiLog.Event(nameof(GenerateRoutes), "generation produced no routes",
                KaiLogLevel.Error);
            return;
        }

        _routes = book;
        KaiRouteStore.Save(DataDir, book);

        // Anything mid-route was following the old book.
        _routeOf.Clear();
        _routeLeg.Clear();
        _routeFaking.Clear();
        _routeReversing.Clear();
    }

    private bool StartSolve(int team, int siteIndex)
    {
        // Eye height of a standing player. Solved positions are judged from a
        // standing view because that is how a bot will hold them.
        const float standingEye = 64.0f;

        if (_solver.Begin(_map, _crumbs, team, siteIndex, standingEye))
        {
            return true;
        }

        // This one could not start. Try the next rather than stalling the
        // whole queue on one bad site.
        if (_solveQueue.Count > 0)
        {
            var next = _solveQueue[0];
            _solveQueue.RemoveAt(0);
            return StartSolve(next.Team, next.Site);
        }

        return false;
    }

    // Ask the controller what each side is running, then execute it.
    //
    // The play says what; the routes and decoys are how. Keeping those
    // separate is what lets an audible change the plan mid-round without
    // having to unpick the route machinery: the controller changes its mind,
    // and the same execution path runs the new answer.
    private void CallPlays()
    {
        var tState = BuildGameState((int)CsTeam.Terrorist);
        var ctState = BuildGameState((int)CsTeam.CounterTerrorist);

        var tPlay = _tactics.CallPlay((int)CsTeam.Terrorist, tState);
        var ctPlay = _tactics.CallPlay((int)CsTeam.CounterTerrorist, ctState);

        ExecutePlay((int)CsTeam.Terrorist, tPlay);
        ExecutePlay((int)CsTeam.CounterTerrorist, ctPlay);
    }
    // Turn a called play into route and decoy assignments.
    //
    // For the Ts the site is not simply whatever the play named: it is
    // wherever the bomb is going, because a site take without the bomb is just
    // a fight. When a bot carries it the play's choice stands and the carrier
    // is routed there. When the human carries it the team reads their movement
    // and follows, which is the difference between bots that play with you and
    // bots that execute somewhere else while you plant alone.
    private void ExecutePlay(int team, KaiPlay? play)
    {
        if (play == null || !_useRoutes || _routes.Routes.Count == 0)
        {
            return;
        }

        int site = play.Site;

        if (team == (int)CsTeam.Terrorist && _map.PlantSites.Count > 0)
        {
            int carrier = BombCarrierSlot();
            bool carrierIsHuman = false;

            if (carrier >= 0)
            {
                var holder = Utilities.GetPlayerFromSlot(carrier);
                carrierIsHuman = holder != null && holder.IsValid && !holder.IsBot;
            }

            site = _command.ReadCarrierSite(
                carrier, carrierIsHuman, _map.PlantSites, play.Site);

            if (site != play.Site)
            {
                KaiLog.Event(nameof(ExecutePlay),
                    $"the play called site {play.Site} but the bomb is going to site {site}, " +
                    $"so the team goes with the bomb");
            }
        }

        int assigned = 0;
        var mainGroup = new List<int>();

        foreach (var player in KaiPlayers.All())
        {
            if (player == null || !player.IsValid || !player.IsBot || player.IsHLTV)
            {
                continue;
            }

            if (!player.PawnIsAlive || (int)player.TeamNum != team)
            {
                continue;
            }

            KaiRoute? route = play.Kind switch
            {
                KaiPlayKind.Execute => PickRoute(player, KaiRouteKind.Execute, site),

                KaiPlayKind.SplitFake => PickRoute(player, KaiRouteKind.Execute, site),

                // Take ground first. Patrol routes lead through contested
                // space rather than at an objective, which is what a default
                // is: information now, commitment later.
                KaiPlayKind.Default => PickRoute(player, KaiRouteKind.Patrol, -1)
                                       ?? PickRoute(player, KaiRouteKind.Execute, site),

                KaiPlayKind.Aggro => PickRoute(player, KaiRouteKind.Patrol, -1),

                // Guarding the bomb and holding are position problems rather
                // than movement ones. The loose bomb guard and the hold logic
                // already own where these bots go, and handing them a route as
                // well would only fight it.
                _ => null,
            };

            if (route != null)
            {
                assigned++;
                mainGroup.Add(player.Slot);
            }
        }

        if (team == (int)CsTeam.Terrorist)
        {
            _realTargetSite = site;

            bool split = play.Kind == KaiPlayKind.SplitFake;

            if (split)
            {
                // Decoys are chosen before the group is finalised, so whoever
                // peels off is removed from the main group rather than being
                // waited for at the staging point.
                AssignDecoys(site);

                foreach (int slot in _decoySite.Keys)
                {
                    mainGroup.Remove(slot);
                }
            }
            else
            {
                _decoySite.Clear();
                _decoyUntil.Clear();
                _decoyEngaged.Clear();
            }

            // Only a real site take gets synchronised. A default is meant to
            // trickle: it is map control, not a hit.
            if (play.Kind == KaiPlayKind.Execute || split)
            {
                _command.BeginExecute(site, mainGroup, split && _decoySite.Count > 0);
            }
        }

        // Brief the human, on the radio, before anything happens.
        BriefTheSquad(team, play, site, mainGroup);

        KaiLog.Event(nameof(ExecutePlay),
            $"team {team} executing '{play.Name}' on site {site}: {assigned} bot(s) routed, " +
            $"{mainGroup.Count} in the main group" +
            (play.Kind == KaiPlayKind.SplitFake ? $", {_decoySite.Count} faking" : "") +
            $", leader is slot {_command.LeaderOf(team)}");
    }

    // Assemble what the controller is allowed to know about the round.
    private KaiGameState BuildGameState(int team)
    {
        int friendlies = 0;
        int enemies = 0;

        foreach (var p in KaiPlayers.All())
        {
            if (p == null || !p.IsValid || p.IsHLTV || !p.PawnIsAlive)
            {
                continue;
            }

            if ((int)p.TeamNum == team)
            {
                friendlies++;
            }
            else if ((int)p.TeamNum == (int)CsTeam.Terrorist
                     || (int)p.TeamNum == (int)CsTeam.CounterTerrorist)
            {
                enemies++;
            }
        }

        float remaining = -1.0f;

        if (_bombPlanted)
        {
            try
            {
                var c4 = Utilities
                    .FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4")
                    .FirstOrDefault(e => e.IsValid);

                if (c4 != null)
                {
                    remaining = c4.C4Blow - Server.CurrentTime;
                }
            }
            catch
            {
                remaining = -1.0f;
            }
        }

        return new KaiGameState
        {
            Team = team,
            FriendliesAlive = friendlies,
            EnemiesAlive = enemies,
            BombPlanted = _bombPlanted,
            BombDropped = _looseBombPos != null,
            BombCarried = BombCarrierSlot() >= 0,
            RoundElapsed = Server.CurrentTime - _roundStartedAt,
            BombRemaining = remaining,
            ContactsBySite = _contactsBySite,
            FriendlyDeaths = _friendlyDeaths,
            EnemyDeaths = _enemyDeaths,
        };
    }

    // Ask the controller whether the plan still fits, and act on the answer.
    private void ConsiderAudibles(float now)
    {
        if (!_useRoutes)
        {
            return;
        }

        foreach (int team in new[] { (int)CsTeam.Terrorist, (int)CsTeam.CounterTerrorist })
        {
            var state = BuildGameState(team);

            var call = _tactics.Consider(team, state, out int newSite, out string why);

            if (call == KaiAudibleKind.None)
            {
                continue;
            }

            KaiLog.Event(nameof(ConsiderAudibles),
                $"AUDIBLE for team {team}: {call}. {why}");

            // The site being abandoned, for the calls that mention both. Read
            // from the controller rather than from a local, because the play
            // is owned there and this method never had one of its own.
            var running = _tactics.CurrentPlay(team);
            int leaving = running?.Site ?? -1;

            string shout = call switch
            {
                KaiAudibleKind.SwitchSite =>
                    $"{SiteName(leaving)} is stacked, swinging to {SiteName(newSite)}",
                KaiAudibleKind.RotateDefence =>
                    $"they are {SiteName(newSite)}, rotating over",
                KaiAudibleKind.FakeRotate =>
                    $"showing a rotate to {SiteName(newSite)}, then coming back",
                KaiAudibleKind.CommitNow =>
                    $"clock is going, committing {SiteName(newSite)} now",
                KaiAudibleKind.PullBack =>
                    "we are down bodies, hold what we have and play for picks",
                KaiAudibleKind.GuardBomb =>
                    "bomb is on the floor, everyone keep eyes on it",
                KaiAudibleKind.BombRecovered =>
                    "they picked it up, spread back out and find it",
                _ => "",
            };

            // Any living squad member can call an audible, not just the
            // leader: the point is that somebody says it, and the leader may
            // well be dead by the time it matters.
            if (shout.Length > 0 && team == KaiSquad.SquadTeam)
            {
                KaiComms.Call(team, -1, $"audible:{team}", shout, 6.0f);
            }

            switch (call)
            {
                case KaiAudibleKind.SwitchSite:
                case KaiAudibleKind.CommitNow:
                    RerouteTeam(team, KaiRouteKind.Execute, newSite);

                    if (team == (int)CsTeam.Terrorist)
                    {
                        _realTargetSite = newSite;
                    }

                    break;

                case KaiAudibleKind.RotateDefence:
                    BeginTeamRotation(team, newSite);
                    RerouteTeam(team, KaiRouteKind.Rotate, newSite);
                    break;

                case KaiAudibleKind.FakeRotate:
                    BeginTeamRotation(team, newSite);
                    RerouteTeam(team, KaiRouteKind.Rotate, newSite);
                    break;

                case KaiAudibleKind.BombRecovered:
                    // Tear down the ring and spread out again.
                    //
                    // Cancelling the converge routes is the whole job: with
                    // them gone the zone and hold logic reclaims these bots
                    // and re-spreads them on their own bearings. The audible
                    // cooldown is also cleared, so a contact read arriving a
                    // second later can rotate the side immediately rather than
                    // waiting out a timer that this event just consumed.
                    CancelBombConverge();
                    ClearTeamRoutes(team);
                    _guardLooseBomb = true;

                    KaiLog.Event(nameof(ConsiderAudibles),
                        $"team {team} abandoning the bomb guard: the carrier has it and the " +
                        $"ring is now around nothing. Spreading back out and playing the " +
                        $"contacts instead.");
                    break;

                case KaiAudibleKind.GuardBomb:
                    // Everybody goes. Clearing the routes alone was not enough:
                    // the guard only engages inside its own radius, so a CT
                    // across the map kept holding a corner while the objective
                    // sat unattended.
                    ClearTeamRoutes(team);
                    _guardLooseBomb = true;
                    ConvergeOnLooseBomb(team);
                    break;

                case KaiAudibleKind.PullBack:
                    // Drop the routes and let the position logic hold what is
                    // left. There is no route for "stop attacking".
                    ClearTeamRoutes(team);
                    break;
            }
        }
    }

    private void RerouteTeam(int team, KaiRouteKind kind, int toSite)
    {
        int moved = 0;

        foreach (var player in KaiPlayers.All())
        {
            if (player == null || !player.IsValid || !player.IsBot || player.IsHLTV)
            {
                continue;
            }

            if (!player.PawnIsAlive || (int)player.TeamNum != team)
            {
                continue;
            }

            // A decoy is mid-job. Leave it: pulling it back early wastes the
            // noise it has already made.
            if (_decoySite.ContainsKey(player.Slot))
            {
                continue;
            }

            _routeOf.Remove(player.Slot);
            _routeLeg.Remove(player.Slot);
            _routeReversing.Remove(player.Slot);

            if (PickRoute(player, kind, toSite) != null)
            {
                moved++;
            }
        }

        KaiLog.Event(nameof(RerouteTeam),
            $"team {team} rerouted: {moved} bot(s) now running {kind} to site {toSite}");
    }

    private void ClearTeamRoutes(int team)
    {
        int cleared = 0;

        foreach (var player in KaiPlayers.All())
        {
            if (player == null || !player.IsValid || (int)player.TeamNum != team)
            {
                continue;
            }

            if (_routeOf.Remove(player.Slot))
            {
                cleared++;
            }

            _routeLeg.Remove(player.Slot);
            _routeReversing.Remove(player.Slot);
        }

        KaiLog.Event(nameof(ClearTeamRoutes),
            $"team {team} pulled off its routes, {cleared} bot(s) holding what they have");
    }

    // Send one or two Ts to fake a site the team is not taking.
    //
    // Never the bomb carrier, and never so many that the real execute is
    // weakened: with a five man side, two faking leaves three plus the carrier
    // hitting the site for real. Decoys take an execute route like everybody
    // else, so they arrive on a real approach and are heard doing it, and are
    // pulled back to the real site by DriveDecoys once they have been seen.
    private void AssignDecoys(int realSite)
    {
        _decoySite.Clear();
        _decoyUntil.Clear();
        _decoyEngaged.Clear();

        _realTargetSite = realSite;

        if (realSite < 0 || _map.PlantSites.Count < 2 || _decoyCount <= 0)
        {
            return;
        }

        // Somewhere other than the real target.
        var alternatives = new List<int>();

        for (int i = 0; i < _map.PlantSites.Count; i++)
        {
            if (i != realSite)
            {
                alternatives.Add(i);
            }
        }

        if (alternatives.Count == 0)
        {
            return;
        }

        int fakeSite = alternatives[_routeRandom.Next(alternatives.Count)];
        int carrier = BombCarrierSlot();

        var eligible = new List<CCSPlayerController>();

        foreach (var player in KaiPlayers.All())
        {
            if (player == null || !player.IsValid || !player.IsBot || player.IsHLTV)
            {
                continue;
            }

            if (!player.PawnIsAlive || (int)player.TeamNum != (int)CsTeam.Terrorist)
            {
                continue;
            }

            // The carrier plants; it does not fake.
            if (player.Slot == carrier)
            {
                continue;
            }

            // Nor does the leader. The bot calling the play should be with
            // the play, not making noise on the other side of the map.
            if (_command.IsLeader(player.Slot, (int)CsTeam.Terrorist))
            {
                continue;
            }

            eligible.Add(player);
        }

        // Never send so many that the real execute is outnumbered by its own
        // decoys. Half the side, minus the carrier, is the ceiling.
        int allowed = Math.Min(_decoyCount, Math.Max(0, (eligible.Count - 1) / 2));

        float now = Server.CurrentTime;
        int sent = 0;

        while (sent < allowed && eligible.Count > 0)
        {
            int pick = _routeRandom.Next(eligible.Count);
            var chosen = eligible[pick];
            eligible.RemoveAt(pick);

            _routeOf.Remove(chosen.Slot);
            _routeLeg.Remove(chosen.Slot);

            var route = PickRoute(chosen, KaiRouteKind.Execute, fakeSite);

            if (route == null)
            {
                continue;
            }

            _decoySite[chosen.Slot] = fakeSite;
            _decoyUntil[chosen.Slot] = now + _decoyPatienceSeconds;
            sent++;

            KaiLog.Event(nameof(AssignDecoys),
                $"slot {chosen.Slot} ('{chosen.PlayerName}') is faking site {fakeSite} while the " +
                $"team takes site {realSite}, and will rejoin them once seen or after " +
                $"{_decoyPatienceSeconds:F0}s");
        }

        if (sent > 0)
        {
            KaiLog.Event(nameof(AssignDecoys),
                $"{sent} decoy(s) faking site {fakeSite}, carrier is slot {carrier}");
        }
    }

    // Keep humans out of the way while a map is being mapped unattended.
    //
    // Moves any human to spectator and tops the sides back up with bots, so
    // what gets recorded is a full five on five of bots rather than a
    // four on five with somebody idle in spawn.
    //
    // Swept periodically rather than once, because a human who reconnects, is
    // moved by the game, or joins mid-session would otherwise slip back in
    // and quietly start contaminating again.
    private void DriveGhostMode(float now)
    {
        if (!_ghostHumans || !_ghostSpectate || now < _nextGhostSweep)
        {
            return;
        }

        _nextGhostSweep = now + 5.0f;

        foreach (var player in KaiPlayers.All())
        {
            if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
            {
                continue;
            }

            int team = (int)player.TeamNum;

            if (team != (int)CsTeam.Terrorist && team != (int)CsTeam.CounterTerrorist)
            {
                continue;
            }

            // ChangeTeam rather than SwitchTeam: this should take effect now
            // rather than at the end of the round, since every round it waits
            // is another round of contaminated samples.
            player.ChangeTeam(CsTeam.Spectator);

            // Replace them, so the side is not a body down. Which side they
            // left decides which bot is added.
            string add = team == (int)CsTeam.Terrorist ? "bot_add_t" : "bot_add_ct";
            Server.ExecuteCommand(add);

            KaiLog.Event(nameof(DriveGhostMode),
                $"'{player.PlayerName}' moved to spectator for unattended mapping, and " +
                $"{add} issued to keep the sides even");
        }
    }

    // Learn where each side spawns, once per round, shortly after it goes live.
    //
    // Averaged over the whole side rather than taken from one bot, and merged
    // across rounds, so the answer converges on the middle of the spawn area
    // rather than wherever the first bot sampled happened to be standing.
    private void SampleSpawns(float now, bool roundLive)
    {
        if (_spawnsSampledThisRound || !roundLive)
        {
            return;
        }

        _spawnsSampledThisRound = true;

        SampleSpawnFor("t", CsTeam.Terrorist);
        SampleSpawnFor("ct", CsTeam.CounterTerrorist);
    }

    private void SampleSpawnFor(string key, CsTeam team)
    {
        float sx = 0.0f;
        float sy = 0.0f;
        float sz = 0.0f;
        int n = 0;

        foreach (var p in KaiPlayers.All())
        {
            if (p == null || !p.IsValid || !p.IsBot || p.IsHLTV || !p.PawnIsAlive)
            {
                continue;
            }

            if ((int)p.TeamNum != (int)team)
            {
                continue;
            }

            var origin = p.PlayerPawn?.Value?.AbsOrigin;

            if (origin == null)
            {
                continue;
            }

            sx += origin.X;
            sy += origin.Y;
            sz += origin.Z;
            n++;
        }

        if (n == 0)
        {
            return;
        }

        var here = new KaiPoint(sx / n, sy / n, sz / n);

        if (_spawns.TryGetValue(key, out var known))
        {
            _spawns[key] = new KaiPoint(
                known.X + ((here.X - known.X) * 0.25f),
                known.Y + ((here.Y - known.Y) * 0.25f),
                known.Z + ((here.Z - known.Z) * 0.25f));
        }
        else
        {
            _spawns[key] = here;

            KaiLog.Event(nameof(SampleSpawnFor),
                $"{key} spawn learned at ({here.X:F0},{here.Y:F0},{here.Z:F0}) from {n} bot(s)");
        }
    }

    // Give this bot a route, chosen at random from those that fit.
    //
    // Random on purpose. A team that always takes the best route is a team the
    // enemy only has to learn once; a team drawing from a set of routes that
    // are each known to be walkable and known to differ from each other is not
    // predictable even in principle. Bots already running a route are excluded
    // from a route's candidacy so the side spreads rather than stacking.
    private KaiRoute? PickRoute(CCSPlayerController player, KaiRouteKind kind, int toSite)
    {
        if (!_useRoutes || _routes.Routes.Count == 0)
        {
            return null;
        }

        int team = (int)player.TeamNum;

        var candidates = new List<KaiRoute>();

        foreach (var route in _routes.Routes)
        {
            if (route.Kind != kind)
            {
                continue;
            }

            if (route.Team != 0 && route.Team != team)
            {
                continue;
            }

            if (toSite >= 0 && route.ToSite != toSite)
            {
                continue;
            }

            candidates.Add(route);
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // Prefer routes nobody else is on, so a five man side takes five
        // different ways in rather than all drawing the same number.
        var free = candidates
            .Where(r => !_routeOf.Values.Any(taken => taken.Name == r.Name))
            .ToList();

        var pool = free.Count > 0 ? free : candidates;

        var chosen = pool[_routeRandom.Next(pool.Count)];

        _routeOf[player.Slot] = chosen;
        _routeLeg[player.Slot] = 0;
        _routeReversing[player.Slot] = false;

        // Whether a rotation is a fake is decided for the team, not per bot,
        // and handled in DriveTeamRotation.
        _routeFaking[player.Slot] = false;

        if (kind == KaiRouteKind.Rotate && _rotation != null)
        {
            _rotation.Members.Add(player.Slot);
        }

        KaiLog.Event(nameof(PickRoute),
            $"slot {player.Slot} takes route '{chosen.Name}' ({kind}, {chosen.Waypoints.Count} " +
            $"waypoints, {chosen.Length:F0} units, covers {chosen.Coverage} angle(s)), " +
            $"{pool.Count} candidate(s) considered");

        return chosen;
    }

    // Order a whole team to rotate, deciding up front whether it is real.
    //
    // The fake is committed to at the start, including how far along it turns
    // round, so every bot in the rotation reverses on the same signal rather
    // than each deciding for itself. A rotation where three bots turn back and
    // two carry on is not a fake, it is a mess.
    private void BeginTeamRotation(int team, int toSite)
    {
        bool fake = _routeRandom.NextDouble() < _fakeRotateChance;

        // Anywhere from 40% along to fully arrived. Turning back at 1.0 is the
        // most convincing version: the enemy has seen them arrive and has
        // committed to answering it before they leave again.
        float reverseAt = fake ? 0.4f + (float)(_routeRandom.NextDouble() * 0.6f) : 1.0f;

        _rotation = new KaiTeamRotation
        {
            Team = team,
            ToSite = toSite,
            IsFake = fake,
            ReverseAt = reverseAt,
        };

        KaiLog.Event(nameof(BeginTeamRotation),
            $"team {team} rotating to site {toSite}" +
            (fake
                ? $" as a TEAM FAKE, turning back together at {reverseAt * 100.0f:F0}% along" +
                  (reverseAt >= 0.95f ? " (after arriving, so it is seen)" : "")
                : " for real"));
    }

    // Watch the team's progress along the rotation and turn everybody at once.
    //
    // Progress is the furthest member rather than the average, so the reversal
    // fires when the lead bot has been seen at the far end rather than waiting
    // for stragglers. The whole side then reverses on the same tick.
    private void DriveTeamRotation(float now)
    {
        if (_rotation == null || !_rotation.IsFake || _rotation.Reversing)
        {
            return;
        }

        float furthest = 0.0f;
        int live = 0;

        foreach (int slot in _rotation.Members)
        {
            if (!_routeOf.TryGetValue(slot, out var route) || route.Waypoints.Count == 0)
            {
                continue;
            }

            var member = Utilities.GetPlayerFromSlot(slot);

            if (member == null || !member.IsValid || !member.PawnIsAlive)
            {
                continue;
            }

            live++;

            float progress = _routeLeg.GetValueOrDefault(slot) / (float)route.Waypoints.Count;

            if (progress > furthest)
            {
                furthest = progress;
            }
        }

        if (live == 0 || furthest < _rotation.ReverseAt)
        {
            return;
        }

        _rotation.Reversing = true;

        foreach (int slot in _rotation.Members)
        {
            _routeReversing[slot] = true;
        }

        KaiLog.Event(nameof(DriveTeamRotation),
            $"team {_rotation.Team} turning back together at {furthest * 100.0f:F0}% along the " +
            $"rotation to site {_rotation.ToSite}: {live} bot(s) reversing, the rotation was a fake");
    }

    // Who is carrying the bomb, if anybody.
    //
    // A decoy must never be the carrier. Sending the bomb to fake a site is
    // not a fake, it is just going to the wrong site.
    private int BombCarrierSlot()
    {
        try
        {
            foreach (var c4 in Utilities.FindAllEntitiesByDesignerName<CC4>("weapon_c4"))
            {
                if (c4 == null || !c4.IsValid)
                {
                    continue;
                }

                var owner = c4.OwnerEntity?.Value;

                if (owner == null || !owner.IsValid)
                {
                    continue;
                }

                foreach (var p in KaiPlayers.All())
                {
                    if (p?.PlayerPawn?.Value?.Handle == owner.Handle)
                    {
                        return p.Slot;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            KaiLog.Throttled("carrier", nameof(BombCarrierSlot),
                $"could not resolve the bomb carrier: {ex.Message}", 10.0f, KaiLogLevel.Error);
        }

        return -1;
    }

    // Run the decoys: make contact somewhere the team is not going, then leave.
    //
    // A decoy that dies has still done its job. One that never finds anybody
    // gives up on a timer rather than standing in an empty site for the round,
    // because a fake nobody witnessed is just a bot in the wrong place.
    private void DriveDecoys(float now)
    {
        if (_decoySite.Count == 0)
        {
            return;
        }

        var finished = new List<int>();

        foreach (var kv in _decoySite)
        {
            int slot = kv.Key;

            var player = Utilities.GetPlayerFromSlot(slot);

            if (player == null || !player.IsValid || !player.PawnIsAlive)
            {
                finished.Add(slot);
                continue;
            }

            var bot = player.PlayerPawn?.Value?.Bot;

            // Contact made. Fire for a moment longer so it is unmistakably a
            // fight, then leave.
            if (bot != null && (bot.IsEnemyVisible || bot.IsAttacking))
            {
                if (_decoyEngaged.Add(slot))
                {
                    _decoyUntil[slot] = now + _decoyLingerSeconds;

                    KaiLog.Event(nameof(DriveDecoys),
                        $"slot {slot} has made contact faking site {kv.Value}, holding the fight " +
                        $"for {_decoyLingerSeconds:F1}s before leaving for site {_realTargetSite}");
                }
            }

            if (_decoyUntil.TryGetValue(slot, out float until) && now >= until)
            {
                finished.Add(slot);
            }
        }

        foreach (int slot in finished)
        {
            int fakedSite = _decoySite.GetValueOrDefault(slot, -1);
            bool engaged = _decoyEngaged.Contains(slot);

            _decoySite.Remove(slot);
            _decoyUntil.Remove(slot);
            _decoyEngaged.Remove(slot);

            var player = Utilities.GetPlayerFromSlot(slot);

            if (player == null || !player.IsValid || !player.PawnIsAlive)
            {
                continue;
            }

            // Drop the fake route and take a real one to the real site.
            _routeOf.Remove(slot);
            _routeLeg.Remove(slot);
            _routeReversing.Remove(slot);

            var rejoin = PickRoute(player, KaiRouteKind.Execute, _realTargetSite);

            KaiLog.Event(nameof(DriveDecoys),
                $"slot {slot} finished faking site {fakedSite} " +
                $"({(engaged ? "made contact" : "found nobody, gave up")}) and is " +
                $"{(rejoin != null ? $"rejoining the execute on site {_realTargetSite}" : "returning to native pathing")}");
        }
    }

    // Drop every converge route and forget where the ring was.
    //
    // Called when the bomb stops being loose, whether that is a pickup or a
    // plant. Leaving the routes in place would march the side to a ring around
    // wherever the bomb used to be, which after a pickup is the one place on
    // the map guaranteed not to have it.
    private void CancelBombConverge()
    {
        int cancelled = 0;

        foreach (int slot in new List<int>(_routeOf.Keys))
        {
            if (!_routeOf[slot].Name.StartsWith("converge_bomb_"))
            {
                continue;
            }

            _routeOf.Remove(slot);
            _routeLeg.Remove(slot);
            _routeReversing.Remove(slot);
            cancelled++;
        }

        _convergeAnchor = null;

        if (cancelled > 0)
        {
            KaiLog.Event(nameof(CancelBombConverge),
                $"cancelled {cancelled} converge route(s), the bomb is no longer on the ground");
        }
    }

    // Keep the converge order standing while the bomb is loose.
    //
    // The audible that starts it fires once and then sits behind a cooldown,
    // so on its own the order would not survive a CT respawning, a bot dying
    // mid-approach and being replaced, or the bomb being picked up and dropped
    // somewhere else entirely. Any of those would leave part of the side
    // quietly back on corner-holding while the objective stayed unattended,
    // which is the exact failure the audible was added to fix.
    private void MaintainBombConverge(float now)
    {
        if (now < _nextConvergeCheck)
        {
            return;
        }

        _nextConvergeCheck = now + 3.0f;

        if (_looseBombPos == null || _bombPlanted)
        {
            _convergeAnchor = null;
            return;
        }

        var play = _tactics.CurrentPlay((int)CsTeam.CounterTerrorist);

        if (play == null || play.Kind != KaiPlayKind.GuardBomb)
        {
            return;
        }

        // The bomb has moved far enough that the ring computed for the old
        // position is somewhere else. Everything is recomputed rather than
        // patched, because the sector fan only makes sense as a whole.
        bool moved = _convergeAnchor != null
                     && _convergeAnchor.DistanceXY(_looseBombPos.X, _looseBombPos.Y) > 400.0f;

        if (moved)
        {
            KaiLog.Event(nameof(MaintainBombConverge),
                $"the bomb has moved {_convergeAnchor!.DistanceXY(_looseBombPos.X, _looseBombPos.Y):F0} " +
                $"units since the side was sent to it, re-forming the ring");

            foreach (var kv in new List<int>(_routeOf.Keys))
            {
                if (_routeOf[kv].Name.StartsWith("converge_bomb_"))
                {
                    _routeOf.Remove(kv);
                    _routeLeg.Remove(kv);
                }
            }
        }

        _convergeAnchor = new KaiPoint(_looseBombPos.X, _looseBombPos.Y, _looseBombPos.Z);

        ConvergeOnLooseBomb((int)CsTeam.CounterTerrorist);
    }

    // The A* graph, built on demand and rebuilt if the breadcrumbs have grown.
    //
    // Kept lazily rather than built at map start because on a new map there is
    // nothing to build from, and rebuilt on growth so a graph that filled out
    // mid-session is actually used.
    private KaiRouteGraph? LiveGraph()
    {
        if (!_crumbs.IsUsable)
        {
            return null;
        }

        if (_liveGraph != null && _liveGraphNodes == _crumbs.NodeCount)
        {
            return _liveGraph;
        }

        var graph = new KaiRouteGraph();

        if (!graph.Build(_crumbs))
        {
            return null;
        }

        _liveGraph = graph;
        _liveGraphNodes = _crumbs.NodeCount;

        KaiLog.Event(nameof(LiveGraph),
            $"live path graph rebuilt over {_crumbs.NodeCount} breadcrumb nodes");

        return _liveGraph;
    }

    // Send the whole CT side to the dropped bomb.
    //
    // The guard only engages within its radius, so before this a CT across the
    // map simply carried on holding a corner while the objective sat
    // unattended. That is the wrong answer to the one moment a CT side gets
    // hard information: the Ts have to come back for it, so that is where the
    // round will be decided.
    //
    // They converge on the RING around the bomb rather than on the bomb
    // itself. Sending five bots to one coordinate would stack them on the
    // objective, throw away the spacing and the sector fan, and hand a T a
    // free multi-kill. Each takes its own bearing, and the ring sits at the
    // guard's hold radius so arriving bots are already where the guard wants
    // them.
    //
    // Paths come from A* over the breadcrumbs. Steering alone cannot cross a
    // map, and the static route book only holds spawn-to-site and
    // site-to-site paths, neither of which reaches an arbitrary point where a
    // bomb happened to land.
    private void ConvergeOnLooseBomb(int team)
    {
        if (_looseBombPos == null)
        {
            return;
        }

        var graph = LiveGraph();

        var cts = new List<CCSPlayerController>();

        foreach (var p in KaiPlayers.All())
        {
            if (p == null || !p.IsValid || !p.IsBot || p.IsHLTV)
            {
                continue;
            }

            if (!p.PawnIsAlive || (int)p.TeamNum != team)
            {
                continue;
            }

            cts.Add(p);
        }

        if (cts.Count == 0)
        {
            return;
        }

        var slots = cts.Select(p => p.Slot).ToList();
        var sectors = KaiFormation.AssignSectors(slots, 0.0f);

        int routed = 0;
        int already = 0;
        int stranded = 0;

        foreach (var bot in cts)
        {
            var origin = bot.PlayerPawn?.Value?.AbsOrigin;

            if (origin == null)
            {
                continue;
            }

            // Already inside the guard's reach; it will pick them up.
            if (_looseBombPos.DistanceXY(origin.X, origin.Y) <= _guardRadius)
            {
                already++;
                continue;
            }

            // Already on its way. Reassigning would reset its progress to leg
            // zero every time this runs, which for a bot halfway across the
            // map means never arriving.
            if (_routeOf.TryGetValue(bot.Slot, out var existing)
                && existing.Name.StartsWith("converge_bomb_"))
            {
                already++;
                continue;
            }

            float bearing = sectors.GetValueOrDefault(bot.Slot, 0.0f);

            var ringPoint = KaiFormation.StepBack(_looseBombPos, bearing, _guardHoldRadius);
            var destination = SnapToGround(ringPoint, 400.0f) ?? ringPoint;

            var waypoints = new List<KaiPoint>();

            if (graph != null)
            {
                string? from = graph.NearestKey(
                    new KaiPoint(origin.X, origin.Y, origin.Z), 400.0f);
                string? to = graph.NearestKey(destination, 400.0f);

                if (from != null && to != null)
                {
                    var path = graph.FindPath(from, to);

                    if (path != null && path.Count >= 2)
                    {
                        waypoints = graph.Simplify(path, 25.0f);
                    }
                }
            }

            if (waypoints.Count == 0)
            {
                // No graph, or no path. A single waypoint still beats holding
                // a corner across the map: the bot steers straight at the ring
                // and the guard takes over once it arrives.
                waypoints.Add(destination);
                stranded++;
            }

            var route = new KaiRoute
            {
                Name = $"converge_bomb_{bot.Slot}",
                Kind = KaiRouteKind.Rotate,
                Team = team,
                ToSite = -1,
                Waypoints = waypoints,
                GeneratedUtc = KaiTime.NowUtc(),
            };

            _routeOf[bot.Slot] = route;
            _routeLeg[bot.Slot] = 0;
            _routeReversing[bot.Slot] = false;
            _routeFaking[bot.Slot] = false;

            routed++;
        }

        if (routed > 0)
        {
            KaiComms.Call((int)CsTeam.CounterTerrorist, -1, "converge",
                $"bomb is on the floor, keep eyes on it, {routed} of us moving in", 10.0f);
        }

        KaiLog.Event(nameof(ConvergeOnLooseBomb),
            $"the bomb is loose and every CT is converging on it: {routed} routed onto the ring " +
            $"at {_guardHoldRadius:F0} units, {already} already in guard range, " +
            $"{stranded} steering directly for want of a path" +
            (graph == null ? " (no usable breadcrumb graph, all direct)" : ""));
    }

    // Advance the synchronised execute.
    //
    // Decoys peel, the main group gathers short of the site, and the whole
    // group goes on one tick. The alternative is what the bots did before:
    // arrive one at a time and lose five separate duels to a defence that only
    // ever has to win one at a time.
    private void DriveExecute(float now)
    {
        if (_command.Phase == KaiExecutePhase.Idle || _realTargetSite < 0)
        {
            return;
        }

        if (_realTargetSite >= _map.PlantSites.Count)
        {
            return;
        }

        var wasPhase = _command.Phase;

        _command.Update(now, _map.PlantSites[_realTargetSite], _decoySite.Count);

        if (wasPhase != KaiExecutePhase.Committed
            && _command.Phase == KaiExecutePhase.Committed)
        {
            string through = BusiestApproach(_realTargetSite);

            KaiComms.Call((int)CsTeam.Terrorist, _command.LeaderOf((int)CsTeam.Terrorist), "commit",
                through.Length > 0
                    ? $"in now, {SiteName(_realTargetSite)} through {through}"
                    : $"in now on {SiteName(_realTargetSite)}",
                6.0f);
        }
    }
    // Should this bot stop where it is and wait for the rest of the group?
    //
    // Judged on distance to the site along nothing more than a straight line,
    // because the question is only "am I close enough to be part of the hit
    // yet", and the bot has already walked a real path to get here.
    private bool ShouldHoldShort(CCSPlayerController player, Vector origin, out float toSite)
    {
        toSite = float.MaxValue;

        if (_command.Phase == KaiExecutePhase.Committed
            || _command.Phase == KaiExecutePhase.Idle)
        {
            return false;
        }

        if (_realTargetSite < 0 || _realTargetSite >= _map.PlantSites.Count)
        {
            return false;
        }

        if (!_command.IsInMainGroup(player.Slot))
        {
            return false;
        }

        var site = _map.PlantSites[_realTargetSite];
        toSite = site.DistanceXY(origin.X, origin.Y);

        // Still far out, so carry on down the route.
        return toSite <= _command.StagingDistance;
    }


    // Advance along the assigned route. Returns true while still running one.
    //
    // Waypoints are followed in order and dropped once reached. A faking bot
    // reverses at the halfway mark and walks its own route backwards, which is
    // what sells the deception: the footsteps go the right way for long enough
    // to be believed, then come back.
    private bool ApplyRoute(
        CCSPlayerController player, CCSPlayerPawn pawn, Vector origin, KaiBotIntent intent)
    {
        if (!_routeOf.TryGetValue(player.Slot, out var route))
        {
            return false;
        }

        if (!_routeLeg.TryGetValue(player.Slot, out int leg))
        {
            leg = 0;
        }

        bool reversing = _routeReversing.GetValueOrDefault(player.Slot);

        if (leg < 0 || leg >= route.Waypoints.Count)
        {
            // Route finished. Drop it so something else can take over.
            _routeOf.Remove(player.Slot);
            _routeLeg.Remove(player.Slot);

            KaiLog.Event(nameof(ApplyRoute),
                $"slot {player.Slot} COMPLETED route '{route.Name}', all " +
                $"{route.Waypoints.Count} waypoint(s) reached" +
                (reversing ? " back at where it started, the rotation having been a fake" : ""));

            return false;
        }

        // Hold short if the group has not gone yet. The route is still the
        // way in; this only stops the bot arriving alone before the rest of
        // the side is ready to arrive with it.
        // Hold short of the site while the group forms up.
        //
        // The bot keeps its route and stops advancing along it. An earlier
        // version pointed it at a computed staging coordinate instead, which
        // abandoned the route entirely and tried to walk a straight line from
        // spawn to a point beside the bombsite. Steering has no obstacle
        // avoidance, so every bot walked into the first wall it met and no
        // execute ever staged: ten commits in a row reported nought of five in
        // position and timed out.
        //
        // Stopping on the path the bot was already walking needs no new
        // pathing, cannot walk into anything the route did not already cross,
        // and puts the group short of the site on their own approaches, which
        // is where a fanned-out hit wants to start from.
        if (ShouldHoldShort(player, origin, out float toSite))
        {
            intent.Anchored = true;
            intent.SourceName = $"route:{route.Name}:ready";

            if (!ApplyTransitClearing(player, pawn, origin, intent))
            {
                var site = _map.PlantSites[_realTargetSite];
                intent.Watch = new KaiPoint(site.X, site.Y, site.Z + KaiHeights.Chest);
            }

            KaiLog.Throttled($"staging:{player.Slot}", nameof(ApplyRoute),
                $"slot {player.Slot} holding {toSite:F0} units short of site " +
                $"{_realTargetSite}, waiting for the group", 2.0f);

            return true;
        }

        var target = route.Waypoints[leg];
        float distance = target.DistanceXY(origin.X, origin.Y);

        // Skip a waypoint that has been overshot.
        //
        // Arrival is a 90-unit radius, so a bot that passes wide of a waypoint
        // never registers reaching it and turns round to go back for it. That
        // is a bot walking backwards on purpose, and it was the largest single
        // source of backwards aiming in a measured session: every one of the
        // top ten offenders was a route leg.
        //
        // Passed is judged against the NEXT waypoint rather than by distance:
        // if the bot is already closer to where it is going next than to the
        // point it is supposed to touch first, that point is behind it and
        // going back for it achieves nothing.
        int following = reversing ? leg - 1 : leg + 1;

        if (distance > 90.0f
            && following >= 0
            && following < route.Waypoints.Count)
        {
            var next = route.Waypoints[following];

            float toNext = next.DistanceXY(origin.X, origin.Y);
            float legLength = next.DistanceXY(target.X, target.Y);

            // Closer to the next one than this one is to it, which means the
            // bot is past it rather than short of it.
            if (toNext < legLength && toNext < distance)
            {
                KaiLog.Event(nameof(ApplyRoute),
                    $"slot {player.Slot} overshot waypoint {leg + 1}/{route.Waypoints.Count} " +
                    $"on '{route.Name}' by {distance:F0} units and is already " +
                    $"{toNext:F0} from the next. Skipping it rather than walking back.");

                leg = following;
                _routeLeg[player.Slot] = leg;

                target = route.Waypoints[leg];
                distance = target.DistanceXY(origin.X, origin.Y);
            }
        }

        if (distance <= 90.0f)
        {
            // Sign the waypoint off.
            //
            // This is the whole reason the breadcrumb idea existed: a route is
            // a sequence of places the bot is supposed to reach, and without a
            // line per arrival there is no way to tell a bot that walked its
            // route from one that got stuck on waypoint three and stood there.
            // The count and what was being watched on arrival make the round
            // auditable after the fact rather than guessed at.
            int reached = reversing ? leg : leg + 1;

            string doing = "no known angle in view";

            if (_transitSet.TryGetValue(player.Slot, out var inView) && inView.Count > 0)
            {
                doing = $"sweeping {inView.Count} angle(s), currently spot " +
                        $"{inView[_transitIndex.GetValueOrDefault(player.Slot) % inView.Count]}";
            }

            // Position reports, named rather than numbered.
            //
            // "waypoint 12 of 40" tells a listener nothing; "at Connector,
            // heading Catwalk" tells them where the bot is and where it is
            // about to be, which is what a position report is for. Sent
            // whenever the named place changes rather than on a fixed
            // interval, so a long run down one corridor stays quiet and a
            // push through several areas reports each one.
            if (KaiSquad.IsSquad(player.Slot))
            {
                string atNow = KaiCallouts.Nearest(target);

                if (atNow.Length > 0
                    && _lastReported.GetValueOrDefault(player.Slot, "") != atNow)
                {
                    _lastReported[player.Slot] = atNow;

                    string nextUp = "";

                    int ahead = reversing ? leg - 1 : leg + 1;

                    if (ahead >= 0 && ahead < route.Waypoints.Count)
                    {
                        nextUp = KaiCallouts.Nearest(route.Waypoints[ahead]);
                    }

                    string report = nextUp.Length > 0 && nextUp != atNow
                        ? $"at {atNow}, moving {nextUp}"
                        : $"at {atNow}";

                    KaiComms.DetailBy(player.Slot, $"wp:{player.Slot}",
                        $"{report} ({reached} of {route.Waypoints.Count})", 3.0f);
                }
            }

            KaiLog.Event(nameof(ApplyRoute),
                $"slot {player.Slot} waypoint {reached}/{route.Waypoints.Count} on " +
                $"'{route.Name}'{(reversing ? " (returning)" : "")} at " +
                $"({target.X:F0},{target.Y:F0}) | {doing}" +
                $"{(intent.Walk ? " | walking" : "")}");

            if (reversing)
            {
                leg--;
            }
            else
            {
                leg++;
            }

            // Turning back is no longer decided here. A fake rotation is a
            // team action, so DriveTeamRotation watches the whole side's
            // progress and flips every member's reversing flag on the same
            // tick. A bot turning round on its own timer was the bug that made
            // the old version read as indecision rather than deception.
            _routeLeg[player.Slot] = leg;
            return true;
        }

        intent.SteerTowards = target;
        intent.SourceName = $"route:{route.Name}:{leg}" + (reversing ? ":back" : "");

        // Clear the angles crossed on the way rather than staring at the next
        // waypoint. This is what makes a route a push rather than a march.
        if (!ApplyTransitClearing(player, pawn, origin, intent))
        {
            intent.Watch = new KaiPoint(target.X, target.Y, target.Z + KaiHeights.Chest);
        }

        KaiLog.Throttled($"route:{player.Slot}", nameof(ApplyRoute),
            $"slot {player.Slot} on '{route.Name}' leg {leg}/{route.Waypoints.Count}, " +
            $"{distance:F0} units to the next waypoint" + (reversing ? " (returning)" : ""), 2.0f);

        return true;
    }

    // Collect every fight currently in progress.
    //
    // A bot with a visible enemy and an attack in progress is a fight. The
    // enemy pawn is read straight off CCSBot::m_enemy, so the position is
    // exact rather than inferred from where the shooter is facing.
    private void RefreshContacts(float now)
    {
        _contacts.RemoveAll(c => now - c.Stamp > _supportSeconds);

        if (!_supportFire)
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
                continue;
            }

            var pawn = player.PlayerPawn?.Value;
            var bot = pawn?.Bot;

            if (pawn == null || bot == null)
            {
                continue;
            }

            if (!bot.IsEnemyVisible && !bot.IsAttacking)
            {
                continue;
            }

            var enemy = bot.Enemy?.Value;
            var enemyOrigin = enemy?.AbsOrigin;

            if (enemy == null || !enemy.IsValid || enemyOrigin == null)
            {
                continue;
            }

            var position = new KaiPoint(
                enemyOrigin.X, enemyOrigin.Y, enemyOrigin.Z + KaiHeights.Chest);

            int enemyTeam = (int)enemy.TeamNum;

            // Merge with an existing report of the same fight rather than
            // stacking five reports of one enemy.
            KaiContact? existing = null;

            foreach (var c in _contacts)
            {
                if (c.EnemyTeam == enemyTeam
                    && c.Position.DistanceXY(position.X, position.Y) < 200.0f)
                {
                    existing = c;
                    break;
                }
            }

            if (existing != null)
            {
                existing.Position = position;
                existing.Stamp = now;
                continue;
            }

            _contacts.Add(new KaiContact
            {
                Position = position,
                EnemyTeam = enemyTeam,
                ReportedBy = player.Slot,
                Stamp = now,
            });

            AttributeContactToSite(position);

            // Call it. This is the single most useful thing a team mate can
            // say and nothing was saying it: where the enemy is, by name, the
            // moment somebody sees one.
            string spotted = KaiCallouts.Describe(position, _bombPlanted ? _bombPos : null);

            int enemiesHere = CountContactsNear(position, 600.0f);

            KaiComms.CallBy(player.Slot, $"contact:{spotted}",
                enemiesHere > 1
                    ? $"{enemiesHere} {spotted}"
                    : $"one {spotted}",
                2.5f);

            KaiLog.Event(nameof(RefreshContacts),
                $"slot {player.Slot} is fighting an enemy of team {enemyTeam} at " +
                $"({position.X:F0},{position.Y:F0},{position.Z:F0}), calling it in as {spotted}");
        }
    }

    // How many separate contacts have been reported near a point, so a call
    // can say "two Banana" rather than reporting the same enemy twice.
    private int CountContactsNear(KaiPoint position, float radius)
    {
        int count = 0;

        foreach (var contact in _contacts)
        {
            if (contact.Position.DistanceXY(position.X, position.Y) <= radius)
            {
                count++;
            }
        }

        return count;
    }

    // Say who died, where, and who did it.
    //
    // Two messages from opposite sides, each sent only to the side that would
    // actually hear it. The killer's team gets a trade call; the victim's team
    // gets a warning naming ground that is now hostile and an angle that is
    // now uncovered, which is the more useful of the two.
    private void ReportDeath(CCSPlayerController? victim, CCSPlayerController? attacker)
    {
        if (victim == null || !victim.IsValid)
        {
            return;
        }

        var victimOrigin = victim.PlayerPawn?.Value?.AbsOrigin;

        if (victimOrigin == null)
        {
            return;
        }

        var where = new KaiPoint(victimOrigin.X, victimOrigin.Y, victimOrigin.Z);
        string place = KaiCallouts.Describe(where, _bombPlanted ? _bombPos : null);

        int victimTeam = (int)victim.TeamNum;

        // The killer's side: a body is down and that angle is open.
        if (attacker != null && attacker.IsValid && (int)attacker.TeamNum != victimTeam)
        {
            int left = CountAlive(victimTeam);

            KaiComms.CallBy(attacker.Slot, $"traded:{place}",
                left > 0 ? $"one down {place}, {left} left" : $"that is all of them, {place}",
                2.0f);
        }

        // The victim's side: somebody just died there, so that ground is
        // dangerous and whatever they were covering is not covered now.
        if (victim.IsBot)
        {
            KaiComms.Call(victimTeam, -1, $"lost:{place}",
                $"lost one at {place}, nobody on it now", 3.0f);
        }
    }

    private static int CountAlive(int team)
    {
        int count = 0;

        foreach (var p in KaiPlayers.All())
        {
            if (p != null && p.IsValid && !p.IsHLTV && p.PawnIsAlive
                && (int)p.TeamNum == team)
            {
                count++;
            }
        }

        return count;
    }

    // Chalk a contact up against whichever bombsite it happened nearest.
    //
    // This is the entire information channel the play caller has. It is
    // deliberately crude: a running tally per site over the whole round rather
    // than a live picture, because a contact twenty seconds old still tells
    // you where they chose to go, and that is what a read is.
    private void AttributeContactToSite(KaiPoint position)
    {
        if (_map.PlantSites.Count == 0 || _contactsBySite.Length == 0)
        {
            return;
        }

        int nearest = -1;
        float bestDist = 2400.0f;

        for (int i = 0; i < _map.PlantSites.Count && i < _contactsBySite.Length; i++)
        {
            float d = _map.PlantSites[i].DistanceXY(position.X, position.Y);

            if (d < bestDist)
            {
                bestDist = d;
                nearest = i;
            }
        }

        if (nearest < 0)
        {
            // Out in the middle somewhere. Says nothing about either site.
            return;
        }

        _contactsBySite[nearest]++;

        KaiLog.Throttled($"contactsite:{nearest}", nameof(AttributeContactToSite),
            $"site {nearest} now has {_contactsBySite[nearest]} reported contact(s) this round",
            3.0f);
    }

    // Swing this bot onto a team mate's fight.
    //
    // Only for bots not already fighting: one that can see an enemy is handled
    // by the native aim, which is better at duels than anything here. Requires
    // line of sight to the contact, because pointing a bot at a wall with an
    // enemy behind it helps nobody and costs the angle it was holding.
    private bool ApplyContactSupport(
        CCSPlayerController player, CCSPlayerPawn pawn, Vector origin)
    {
        if (!_supportFire || _contacts.Count == 0)
        {
            return false;
        }

        int myTeam = (int)player.TeamNum;

        var eye = new Vector(origin.X, origin.Y, origin.Z + pawn.ViewOffset.Z);

        KaiContact? best = null;
        float bestDist = float.MaxValue;

        foreach (var contact in _contacts)
        {
            // Only respond to fights against the other side.
            if (contact.EnemyTeam == myTeam)
            {
                continue;
            }

            float dist = contact.Position.DistanceXY(origin.X, origin.Y);

            if (dist > _supportRadius || dist >= bestDist)
            {
                continue;
            }

            var target = new Vector(
                contact.Position.X, contact.Position.Y, contact.Position.Z);

            if (!KaiRayTraceBridge.CanSee(eye, target))
            {
                continue;
            }

            bestDist = dist;
            best = contact;
        }

        if (best == null)
        {
            return false;
        }

        var intent = GetOrCreateIntent(player.Slot);
        intent.Watch = best.Position;
        intent.ForceAim = true;
        intent.SourceName = "support";

        KaiComms.DetailBy(player.Slot, $"support:{player.Slot}",
            $"swinging {KaiCallouts.Describe(best.Position, _bombPlanted ? _bombPos : null)}, " +
            $"with you", 3.0f);

        KaiLog.Throttled($"support:{player.Slot}", nameof(ApplyContactSupport),
            $"slot {player.Slot} swinging onto slot {best.ReportedBy}'s fight, " +
            $"{bestDist:F0} units away", 1.5f);

        return true;
    }

    // Split the CT side into zones before the plant.
    //
    // "A, B and mid" is a callout, not a coordinate, and hardcoding callouts
    // per map does not scale. But the geography does the work on its own: take
    // the centre of everywhere the learner has seen people fight, give each CT
    // an evenly spaced bearing from it, and on a normal two-site map those
    // bearings land on the separate sites and the routes between them. No map
    // knowledge required, and it generalises to Nuke and Vertigo unchanged.
    private void RefreshCtZones(float now)
    {
        if (now < _nextZoneRefresh)
        {
            return;
        }

        _nextZoneRefresh = now + 2.0f;

        if (_mapCentre == null)
        {
            _mapCentre = ComputeMapCentre();
        }

        if (_mapCentre == null)
        {
            return;
        }

        var slots = new List<int>();

        foreach (var p in KaiPlayers.All())
        {
            if (p == null || !p.IsValid || !p.IsBot || p.IsHLTV)
            {
                continue;
            }

            if (!p.PawnIsAlive || (int)p.TeamNum != (int)CsTeam.CounterTerrorist)
            {
                continue;
            }

            slots.Add(p.Slot);
        }

        if (slots.Count == 0)
        {
            _ctZones.Clear();
            return;
        }

        // Only rebuild when the roster actually changed, so a bot is not
        // shuffled to a different zone every two seconds as team mates die.
        bool same = slots.Count == _ctZones.Count;

        if (same)
        {
            foreach (int slot in slots)
            {
                if (!_ctZones.ContainsKey(slot))
                {
                    same = false;
                    break;
                }
            }
        }

        if (same)
        {
            return;
        }

        _ctZones.Clear();

        var assigned = KaiFormation.AssignSectors(slots, 0.0f);

        foreach (var kv in assigned)
        {
            _ctZones[kv.Key] = kv.Value;
        }

        KaiLog.Event(nameof(RefreshCtZones),
            $"{slots.Count} CT(s) split into {360.0f / slots.Count:F0} degree zones " +
            $"around ({_mapCentre.X:F0},{_mapCentre.Y:F0})");
    }

    // The middle of everywhere the learner has recorded a fight. A crude but
    // effective stand-in for the centre of the playable area, and it costs one
    // pass over data already in memory.
    private KaiPoint? ComputeMapCentre()
    {
        float sx = 0.0f;
        float sy = 0.0f;
        float sz = 0.0f;
        int n = 0;

        foreach (var spot in _map.PreAim)
        {
            sx += spot.Trigger.X;
            sy += spot.Trigger.Y;
            sz += spot.Trigger.Z;
            n++;
        }

        if (n == 0)
        {
            KaiLog.Event(nameof(ComputeMapCentre),
                "no pre-aim data, CT zoning disabled until the map is learned");
            return null;
        }

        var centre = new KaiPoint(sx / n, sy / n, sz / n);

        KaiLog.Event(nameof(ComputeMapCentre),
            $"map centre from {n} pre-aim spots is ({centre.X:F0},{centre.Y:F0},{centre.Z:F0})");

        return centre;
    }

    // Pull the crosshair onto an authored corner while inside its trigger.
    // Never touches movement.
    private void ApplyPreAim(CCSPlayerController player, CCSPlayerPawn pawn, Vector origin)
    {
        if (_map.PreAim.Count == 0)
        {
            return;
        }

        int team = (int)player.TeamNum;

        // Once the bomb is down the retake director owns every CT. Leaving
        // pre-aim running alongside it meant the defuser had a horizontal
        // watch target written before the director ran, so a bot standing on
        // the bomb kept its crosshair on a corner and never looked down far
        // enough for the game to let it defuse.
        if (_bombPlanted && team == (int)CsTeam.CounterTerrorist)
        {
            return;
        }

        float eyeYaw = pawn.EyeAngles.Y;

        KaiPreAimSpot? chosen = null;
        int chosenPriority = int.MinValue;

        foreach (var spot in _map.PreAim)
        {
            if (spot.Team != 0 && spot.Team != team)
            {
                continue;
            }

            // Horizontal and vertical checked separately. On Nuke and Vertigo
            // a single 3D radius would fire an upper-floor trigger for a bot
            // standing directly below it.
            if (spot.Trigger.DistanceXY(origin.X, origin.Y) > spot.TriggerRadius)
            {
                continue;
            }

            if (MathF.Abs(spot.Trigger.Z - origin.Z) > spot.TriggerHeight)
            {
                continue;
            }

            // Facing gate. Without it a bot retreating through the trigger
            // whips its view round to a corner it is walking away from.
            if (spot.FacingToleranceDeg < 180.0f)
            {
                float wantYaw = YawTo(origin.X, origin.Y, spot.Watch.X, spot.Watch.Y);

                if (MathF.Abs(NormalizeYaw(wantYaw - eyeYaw)) > spot.FacingToleranceDeg)
                {
                    continue;
                }
            }

            if (spot.Priority > chosenPriority)
            {
                chosenPriority = spot.Priority;
                chosen = spot;
            }
        }

        if (chosen == null)
        {
            return;
        }

        var intent = GetOrCreateIntent(player.Slot);
        intent.Watch = chosen.Watch;
        intent.SourceName = $"preaim:{chosen.Name}";

        // A CT on defence before the plant holds the angle rather than walking
        // through it. The pre-aim data already says where duels happen, so a
        // CT standing on one of those spots facing the right way is doing its
        // job, and this is what stops a defence wandering the map. Ts are
        // excluded because a pinned T never plants.
        if (_pinCtOnPreAim
            && !_bombPlanted
            && (int)player.TeamNum == (int)CsTeam.CounterTerrorist)
        {
            // Only hold if this trigger is in the zone this bot was given, and
            // only if no team mate is already holding within spacing. Without
            // both checks the whole CT side converges on whichever couple of
            // triggers happen to be nearest to spawn.
            bool ownZone = true;

            if (_mapCentre != null && _ctZones.TryGetValue(player.Slot, out float zone))
            {
                float bearing = KaiFormation.Bearing(
                    _mapCentre.X, _mapCentre.Y, origin.X, origin.Y);

                ownZone = KaiFormation.AngleGap(bearing, zone) <= 90.0f;
            }

            var held = new List<KaiPoint>();

            foreach (var kv in _intents)
            {
                if (kv.Key == player.Slot || !kv.Value.Anchored)
                {
                    continue;
                }

                var mate = Utilities.GetPlayerFromSlot(kv.Key);
                var mateOrigin = mate?.PlayerPawn?.Value?.AbsOrigin;

                if (mateOrigin != null)
                {
                    held.Add(new KaiPoint(mateOrigin.X, mateOrigin.Y, mateOrigin.Z));
                }
            }

            var here = new KaiPoint(origin.X, origin.Y, origin.Z);
            bool spaced = KaiFormation.FarEnoughFrom(here, held, KaiFormation.MinBotSpacing);

            if (ownZone && spaced)
            {
                intent.Anchored = true;

                // Sweep between every angle this position covers rather than
                // staring down one of them.
                ApplyGlanceSweep(player, pawn, here, Server.CurrentTime, intent, "preaim");

                KaiLog.Throttled($"preaimhold:{player.Slot}", nameof(ApplyPreAim),
                    $"slot {player.Slot} holding '{chosen.Name}' in its zone on defence", 3.0f);
            }
            else
            {
                KaiLog.Throttled($"preaimskip:{player.Slot}", nameof(ApplyPreAim),
                    $"slot {player.Slot} not holding '{chosen.Name}': " +
                    $"ownZone={ownZone} spaced={spaced}", 3.0f);
            }
        }

        KaiLog.Throttled($"preaim:{player.Slot}", nameof(ApplyPreAim),
            $"slot {player.Slot} pre-aiming '{chosen.Name}'", 2.0f);
    }

    // ------------------------------------------------------------------
    // Loose bomb tracking and CT guarding
    // ------------------------------------------------------------------

    // Find the bomb if it is lying on the ground.
    //
    // A CC4 with no owner entity is loose. When a player is carrying it the
    // owner is that player's pawn, and once it is planted the entity becomes a
    // CPlantedC4 instead, so neither case matches here.
    private void ScanForLooseBomb(float now)
    {
        if (now < _nextLooseBombScan)
        {
            return;
        }

        // Twice a second is plenty. A bomb on the floor does not move.
        _nextLooseBombScan = now + 0.5f;

        if (_bombPlanted)
        {
            _looseBombPos = null;
            return;
        }

        try
        {
            KaiPoint? found = null;

            foreach (var c4 in Utilities.FindAllEntitiesByDesignerName<CC4>("weapon_c4"))
            {
                if (c4 == null || !c4.IsValid)
                {
                    continue;
                }

                var owner = c4.OwnerEntity;

                if (owner != null && owner.Value != null && owner.Value.IsValid)
                {
                    // Somebody is carrying it.
                    continue;
                }

                var origin = c4.AbsOrigin;

                if (origin == null)
                {
                    continue;
                }

                found = new KaiPoint(origin.X, origin.Y, origin.Z);
                break;
            }

            bool wasLoose = _looseBombPos != null;
            _looseBombPos = found;

            if (found != null && !wasLoose)
            {
                // Both sides want to know and both are told, separately. For
                // the CTs it is where the fight will be; for the Ts it is what
                // they have to go back for.
                string dropped = KaiCallouts.Describe(found, null);

                KaiComms.Call((int)CsTeam.CounterTerrorist, -1, "bombdown",
                    $"bomb is on the floor at {dropped}, hold the angles on it", 12.0f);

                KaiComms.Call((int)CsTeam.Terrorist, -1, "bombdown",
                    $"bomb is down at {dropped}, somebody get it", 12.0f);

                KaiLog.Event(nameof(ScanForLooseBomb),
                    $"bomb is loose at ({found.X:F0},{found.Y:F0},{found.Z:F0}) near {dropped}, " +
                    $"CTs will guard it");
            }
            else if (found == null && wasLoose)
            {
                KaiLog.Event(nameof(ScanForLooseBomb), "bomb picked up or gone, guard released");

                // Cancel here as well as on the audible. The scanner notices
                // within half a second, which is faster than waiting for the
                // controller's next pass, and a side marching towards a ring
                // that no longer means anything should stop as soon as that is
                // known rather than as soon as it is decided.
                CancelBombConverge();
            }
        }
        catch (Exception ex)
        {
            KaiLog.Throttled("loosescan", nameof(ScanForLooseBomb),
                $"scan failed: {ex.Message}", 10.0f, KaiLogLevel.Error);
        }
    }

    // Look for somewhere nearby that can see the bomb.
    //
    // Samples a ring of candidate positions around the bot and traces from
    // each to the bomb, returning the nearest that has a clear view. The bot
    // is then steered towards it.
    //
    // Two traces per candidate, deliberately: one from the candidate to the
    // bomb to check the sightline is worth having, and one from the bot to the
    // candidate to check there is not a wall in between. The second is a poor
    // substitute for pathfinding, because a clear straight line does not mean
    // a walkable one, but over a few hundred units it rejects most of the
    // obviously impossible moves.
    private bool TryFindSightline(
        CCSPlayerPawn pawn, Vector origin, KaiPoint bomb, out KaiPoint destination)
    {
        destination = new KaiPoint();

        float eyeOffset = pawn.ViewOffset.Z;
        var eye = new Vector(origin.X, origin.Y, origin.Z + eyeOffset);
        var bombTarget = new Vector(bomb.X, bomb.Y, bomb.Z + GuardAimHeight);

        const int rings = 2;
        const int spokes = 8;

        float best = float.MaxValue;
        bool found = false;

        for (int ring = 1; ring <= rings; ring++)
        {
            float radius = _guardSeekRange * ring / rings;

            for (int spoke = 0; spoke < spokes; spoke++)
            {
                float angle = spoke * (2.0f * MathF.PI / spokes);
                float cx = origin.X + (MathF.Cos(angle) * radius);
                float cy = origin.Y + (MathF.Sin(angle) * radius);
                float cz = origin.Z;

                var candidateEye = new Vector(cx, cy, cz + eyeOffset);

                // Does this spot see the bomb?
                if (!KaiRayTraceBridge.CanSee(candidateEye, bombTarget))
                {
                    continue;
                }

                // Can the bot get there without a wall in the way?
                if (!KaiRayTraceBridge.CanSee(eye, candidateEye))
                {
                    continue;
                }

                if (radius < best)
                {
                    best = radius;
                    destination = new KaiPoint(cx, cy, cz);
                    found = true;
                }
            }

            if (found)
            {
                // Nearest ring wins; no reason to look further out.
                break;
            }
        }

        return found;
    }

    // Hand every guarding CT an arc around the loose bomb.
    //
    // Recomputed whenever the set of guards changes, because a dropped bomb
    // is a pre-plant situation and CTs are dying and rotating around it all
    // the time. Same fan as the post-plant defence: evenly spaced bearings so
    // the bomb is covered from several sides rather than several bots
    // covering one side of it.
    private void AssignGuardSectors(KaiPoint bomb, List<int> slots)
    {
        _guardSectors.Clear();

        if (slots.Count == 0)
        {
            return;
        }

        var assigned = KaiFormation.AssignSectors(slots, 0.0f);

        foreach (var kv in assigned)
        {
            _guardSectors[kv.Key] = kv.Value;
        }

        KaiLog.Event(nameof(AssignGuardSectors),
            $"{slots.Count} CT(s) guarding the loose bomb in {360.0f / slots.Count:F0} degree arcs");
    }

    // Cycle a guarding bot between the bomb and the approaches to it.
    //
    // Watching the bomb guarantees seeing whoever reaches it, but only once
    // they are already on it. The Ts have to cross known ground to get there,
    // and a guard that cycles those approaches sees them coming instead. The
    // bomb stays as index zero of the rotation, so the guarantee is kept
    // rather than traded away.
    private bool ApplyGuardSweep(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        KaiPoint here,
        KaiPoint bombAim,
        float now,
        KaiBotIntent intent)
    {
        if (!_guardSweepAngles)
        {
            return false;
        }

        if (!_guardSet.TryGetValue(player.Slot, out var visible))
        {
            visible = new List<int>();
            CoverageScore(here, pawn.ViewOffset.Z, (int)player.TeamNum, visible);

            // Keep only the few nearest the bomb. A guard cycling fifteen
            // angles is on none of them long enough to react.
            if (visible.Count > _guardSweepMax)
            {
                visible = visible
                    .OrderBy(i => _map.PreAim[i].Trigger.DistanceXY(bombAim.X, bombAim.Y))
                    .Take(_guardSweepMax)
                    .ToList();
            }

            _guardSet[player.Slot] = visible;
            _guardIndex[player.Slot] = 0;
            _guardFlick[player.Slot] = now + _glanceDwell;

            KaiLog.Event(nameof(ApplyGuardSweep),
                $"slot {player.Slot} guarding the loose bomb, sweeping it plus " +
                $"{visible.Count} approach angle(s) visible from here");
        }

        if (visible.Count == 0)
        {
            return false;
        }

        if (!_guardIndex.TryGetValue(player.Slot, out int cursor))
        {
            cursor = 0;
        }

        if (_guardFlick.TryGetValue(player.Slot, out float due) && now >= due)
        {
            // The rotation is the bomb followed by every angle, so index zero
            // always brings it back to the bomb.
            cursor = (cursor + 1) % (visible.Count + 1);
            _guardIndex[player.Slot] = cursor;
            _guardFlick[player.Slot] = now + _glanceDwell;
        }

        if (cursor == 0)
        {
            intent.Watch = bombAim;
            intent.SourceName = "guard:sighted:bomb";
            return true;
        }

        int spotIndex = visible[(cursor - 1) % visible.Count];

        if (spotIndex < 0 || spotIndex >= _map.PreAim.Count)
        {
            return false;
        }

        var spot = _map.PreAim[spotIndex];

        intent.Watch = new KaiPoint(
            spot.Trigger.X, spot.Trigger.Y, spot.Trigger.Z + KaiHeights.Chest);
        intent.SourceName = $"guard:sighted:angle{spotIndex}";

        KaiLog.Throttled($"guardsweep:{player.Slot}", nameof(ApplyGuardSweep),
            $"slot {player.Slot} checking approach {spotIndex} " +
            $"({cursor} of {visible.Count}) while guarding the bomb", 3.0f);

        return true;
    }

    // Hold a CT on a bomb lying on the ground.
    //
    // Four tiers, in order of preference:
    //
    //   1. Already in its own arc with a clear view. Pin and watch.
    //   2. Can see the bomb but is in somebody else's arc, or crowding a team
    //      mate. Slide round the circle to its own bearing.
    //   3. No view. Steer to somewhere that has one.
    //   4. Nothing works. Hold position rather than wander off, because a CT
    //      near the bomb is still worth more than a CT taking map control that
    //      no longer matters.
    private bool ApplyLooseBombGuard(CCSPlayerController player, CCSPlayerPawn pawn, Vector origin)
    {
        if (!_guardLooseBomb || _looseBombPos == null)
        {
            return false;
        }

        // Guard only when guarding is what the side is doing. Before the play
        // caller existed this fired as a reflex the moment a bomb hit the
        // floor, silently overriding whatever else had been decided. Now it is
        // a play: it runs when called and defers otherwise, which is also what
        // lets the controller find out whether camping the bomb actually wins.
        var called = _tactics.CurrentPlay((int)player.TeamNum);

        if (called != null && called.Kind != KaiPlayKind.GuardBomb)
        {
            return false;
        }

        if ((int)player.TeamNum != (int)CsTeam.CounterTerrorist)
        {
            _guardSectors.Remove(player.Slot);
            _guardPositions.Remove(player.Slot);
            return false;
        }

        float dist = _looseBombPos.DistanceXY(origin.X, origin.Y);

        if (dist > _guardRadius)
        {
            _guardSeekStart.Remove(player.Slot);
            _guardSectors.Remove(player.Slot);
            _guardPositions.Remove(player.Slot);
            return false;
        }

        // Keep the fan in step with whoever is actually in range.
        if (!_guardSectors.ContainsKey(player.Slot))
        {
            var slots = new List<int>(_guardSectors.Keys) { player.Slot };
            AssignGuardSectors(_looseBombPos, slots);
        }

        var aimPoint = new KaiPoint(
            _looseBombPos.X, _looseBombPos.Y, _looseBombPos.Z + GuardAimHeight);

        bool canSee = true;

        if (_guardRequireLineOfSight)
        {
            canSee = CanSeeBomb(pawn, origin, _looseBombPos);
        }

        var intent = GetOrCreateIntent(player.Slot);
        var here = new KaiPoint(origin.X, origin.Y, origin.Z);

        float myBearing = KaiFormation.Bearing(
            _looseBombPos.X, _looseBombPos.Y, origin.X, origin.Y);

        _guardSectors.TryGetValue(player.Slot, out float sector);
        float gap = KaiFormation.AngleGap(myBearing, sector);

        var others = new List<KaiPoint>();

        foreach (var kv in _guardPositions)
        {
            if (kv.Key != player.Slot)
            {
                others.Add(kv.Value);
            }
        }

        bool spaced = KaiFormation.FarEnoughFrom(here, others, KaiFormation.MinBotSpacing);

        // Tier 1: right arc, clear view, not crowding anyone.
        if (canSee && gap <= 45.0f && spaced)
        {
            _guardSeekStart.Remove(player.Slot);
            _guardPositions[player.Slot] = here;

            intent.SourceName = "guard:sighted";

            if (dist <= _guardHoldRadius)
            {
                intent.Anchored = true;
            }

            if (!ApplyGuardSweep(player, pawn, here, aimPoint, Server.CurrentTime, intent))
            {
                intent.Watch = aimPoint;
            }

            KaiLog.Throttled($"guard:{player.Slot}", nameof(ApplyLooseBombGuard),
                $"slot {player.Slot} guarding from {dist:F0} units on bearing {myBearing:F0} " +
                $"(arc {sector:F0}), anchored={intent.Anchored}", 3.0f);

            return true;
        }

        // Tier 2: it can see the bomb but is in the wrong place. Walk round
        // the circle to its own bearing at the same radius.
        if (canSee && (gap > 45.0f || !spaced))
        {
            float radius = MathF.Max(dist, 250.0f);
            var slot = KaiFormation.StepBack(_looseBombPos, sector, radius);

            intent.SteerTowards = slot;
            intent.SourceName = "guard:spreading";

            // Clear the angles crossed on the way round. Sliding round the
            // ring is a walk across open site past every angle a T could be
            // holding, and the bomb is not going anywhere and cannot shoot.
            if (!ApplyTransitClearing(player, pawn, origin, intent))
            {
                intent.Watch = aimPoint;
            }

            KaiLog.Throttled($"guardspread:{player.Slot}", nameof(ApplyLooseBombGuard),
                $"slot {player.Slot} sliding from bearing {myBearing:F0} to its arc " +
                $"{sector:F0} (gap {gap:F0}, spaced={spaced})", 2.0f);

            return true;
        }

        float now = Server.CurrentTime;

        if (!_guardSeekStart.TryGetValue(player.Slot, out float seekStart))
        {
            seekStart = now;
            _guardSeekStart[player.Slot] = seekStart;
        }

        // Tier 3: no view, go and find one.
        if (now - seekStart < _guardSeekSeconds
            && TryFindSightline(pawn, origin, _looseBombPos, out KaiPoint destination))
        {
            intent.SteerTowards = destination;
            intent.SourceName = "guard:seeking";

            // Same again, and more so: a bot seeking a sightline has no view
            // of the bomb by definition, so watching it means staring through
            // a wall while walking past things it can actually see.
            if (!ApplyTransitClearing(player, pawn, origin, intent))
            {
                intent.Watch = aimPoint;
            }

            KaiLog.Throttled($"guardseek:{player.Slot}", nameof(ApplyLooseBombGuard),
                $"slot {player.Slot} cannot see the bomb, steering " +
                $"{destination.DistanceXY(origin.X, origin.Y):F0} units to a spot that can", 2.0f);

            return true;
        }

        // Tier 4: stay put rather than wander off.
        _guardPositions[player.Slot] = here;
        intent.Anchored = true;
        intent.SourceName = "guard:blind_hold";

        KaiLog.Throttled($"guardblind:{player.Slot}", nameof(ApplyLooseBombGuard),
            $"slot {player.Slot} has no sightline and nowhere nearby to get one, " +
            $"holding at {dist:F0} units", 3.0f);

        return true;
    }

    private bool TryThreatAim(CCSBot bot, float now, out KaiPoint target, out string reason)
    {
        target = new KaiPoint();
        reason = "";

        // Being shot at. The attacker's position is known exactly, so there is
        // no reason for a bot to stand there taking fire without turning.
        if (bot.AttackedTimestamp > 0.0f && now - bot.AttackedTimestamp < _yieldSeconds)
        {
            var attacker = bot.Attacker?.Value;
            var origin = attacker?.AbsOrigin;

            if (attacker != null && attacker.IsValid && origin != null)
            {
                target = new KaiPoint(origin.X, origin.Y, origin.Z + KaiHeights.Chest);
                reason = $"attacked {now - bot.AttackedTimestamp:F1}s ago";
                return true;
            }
        }

        // Heard something close. m_noiseTravelDistance is how far the sound
        // travelled to reach this bot, which is a better filter than straight
        // line distance because it follows the map rather than passing through
        // walls. Gunfire across the map should not drag a holding bot off its
        // angle; footsteps in the next room should.
        if (bot.NoiseTimestamp > 0.0f
            && now - bot.NoiseTimestamp < _yieldSeconds
            && bot.NoiseTravelDistance > 0.0f
            && bot.NoiseTravelDistance <= _noiseRange)
        {
            var noise = bot.NoisePosition;

            if (noise != null)
            {
                target = new KaiPoint(noise.X, noise.Y, noise.Z + KaiHeights.Chest);
                reason = $"noise {now - bot.NoiseTimestamp:F1}s ago at " +
                         $"{bot.NoiseTravelDistance:F0} units travel";
                return true;
            }
        }

        return false;
    }

    // When the movement pin comes off.
    //
    // Deliberately much narrower than the aim response. An earlier version
    // released the pin on every condition the aim reacted to, which meant a
    // single distant noise unpinned a bot and let it wander off. Hearing
    // something is a reason to turn and look, not a reason to abandon a held
    // position, so only actual contact releases the pin.
    private static bool ShouldReleasePin(CCSBot bot)
    {
        return bot.IsEnemyVisible || bot.IsAttacking || bot.IsAimingAtEnemy;
    }

    // ------------------------------------------------------------------
    // Native hook: aim
    //
    // CCSBot::Upkeep calls UpdateLookAround, which writes the bot's desired
    // view into m_lookYaw and m_lookPitch, then calls UpdateLookAngles, which
    // spring-smooths the real eye angles towards those two values using the
    // profile's stiffness, damping and max acceleration.
    //
    // Writing those two fields in a PRE hook on UpdateLookAngles overrides the
    // decision UpdateLookAround just made while leaving all the native
    // smoothing intact, so the bot turns to check an angle rather than
    // snapping to it. It is also exactly why these fields cannot be written
    // from a tick listener: UpdateLookAround would overwrite them first.
    // ------------------------------------------------------------------

    private HookResult OnUpdateLookAnglesPre(DynamicHook hook)
    {
        try
        {
            IntPtr pBot = hook.GetParam<IntPtr>(0);

            if (pBot == IntPtr.Zero)
            {
                return HookResult.Continue;
            }

            var bot = new CCSBot(pBot);
            var controller = bot.Controller;

            if (controller == null || !controller.IsValid)
            {
                return HookResult.Continue;
            }

            // Round-win celebration. Checked BEFORE the enemy gate below,
            // because the round is already decided and a surviving loser
            // wandering into view should not cut it short. This is the only
            // place in the plugin that bypasses that gate.
            if (IsCelebrating() && (int)controller.TeamNum == _celebrateTeam)
            {
                // A free-running yaw rather than an offset from each bot's own
                // angle, so the whole team sweeps together and it reads as
                // deliberate rather than as ordinary look jitter.
                float spun = (Server.CurrentTime * CelebrateSpinDegreesPerSecond) % 360.0f;

                if (spun > 180.0f)
                {
                    spun -= 360.0f;
                }

                ref float spinYaw = ref bot.LookYaw;
                spinYaw = spun;

                ref float spinPitch = ref bot.LookPitch;
                spinPitch = CelebrateSkyPitch;

                KaiLog.Throttled("celebrate_spin", nameof(OnUpdateLookAnglesPre),
                    $"celebration driving winning-team look angles, yaw={spun:F0}", 1.0f);

                return HookResult.Continue;
            }

            // Hand straight back to the native AI whenever the bot's own
            // systems have something more urgent to look at: an enemy, a
            // noise, an AI look-at, incoming fire, or a defuse starting. An
            // authored angle is the default for when nothing is happening, not
            // a command that outranks the bot's senses.
            //
            // This is also the seam that keeps BotAimImprover's PickNewAimSpot
            // override and the native reaction timing entirely intact during
            // actual fights.
            float nowAim = Server.CurrentTime;

            // 1. Real contact. Hand back so the native reaction timing and
            //    BotAimImprover's PickNewAimSpot override run untouched.
            if (bot.IsEnemyVisible || bot.IsAttacking || bot.IsAimingAtEnemy)
            {
                return HookResult.Continue;
            }

            // 2. Something threatened it. Drive the view at the threat rather
            //    than merely getting out of the way, so the turn starts on the
            //    same tick instead of waiting on the AI's own look decision.
            if (TryThreatAim(bot, nowAim, out KaiPoint threat, out string threatReason))
            {
                var tPawn = controller.PlayerPawn?.Value;
                var tOrigin = tPawn?.AbsOrigin;

                if (tPawn != null && tOrigin != null)
                {
                    float tEyeZ = tOrigin.Z + tPawn.ViewOffset.Z;

                    ref float threatYaw = ref bot.LookYaw;
                    threatYaw = YawTo(tOrigin.X, tOrigin.Y, threat.X, threat.Y);

                    ref float threatPitch = ref bot.LookPitch;
                    threatPitch = PitchTo(tOrigin.X, tOrigin.Y, tEyeZ, threat.X, threat.Y, threat.Z);

                    KaiLog.Throttled($"threat:{controller.Slot}", nameof(OnUpdateLookAnglesPre),
                        $"slot {controller.Slot} turning to threat ({threatReason})", 1.0f);

                    return HookResult.Continue;
                }
            }

            // 3. The AI has explicitly told this bot to look somewhere, via
            //    SetLookAt. Leave it alone, unless the plugin has something it
            //    must point at: defusing requires the bot to physically look
            //    at the bomb, and deferring to the AI there means standing on
            //    the bomb until the round is lost.
            bool forced = _intents.TryGetValue(controller.Slot, out var forceIntent)
                          && forceIntent.ForceAim
                          && forceIntent.Watch != null
                          && nowAim - forceIntent.Stamp <= IntentStaleSeconds;

            if (!forced
                && bot.LookAtSpotDuration > 0.0f
                && nowAim - bot.LookAtSpotTimestamp < bot.LookAtSpotDuration)
            {
                return HookResult.Continue;
            }

            // 4. Nothing happening. Fall through to the authored angle below.

            if (!_intents.TryGetValue(controller.Slot, out var intent) || intent.Watch == null)
            {
                return HookResult.Continue;
            }

            if (Server.CurrentTime - intent.Stamp > IntentStaleSeconds)
            {
                return HookResult.Continue;
            }

            var pawn = controller.PlayerPawn?.Value;
            var origin = pawn?.AbsOrigin;

            if (pawn == null || origin == null)
            {
                return HookResult.Continue;
            }

            float eyeZ = pawn.ViewOffset.Z;
            float ex = origin.X;
            float ey = origin.Y;
            float ez = origin.Z + eyeZ;

            // Nothing aims behind a moving bot. Ever. Enforced here.
            //
            // This check used to live in the movement hook, which was the
            // wrong place: that is a different hook on a different function,
            // and if look angles run first in a tick the correction lands a
            // tick late, every tick, forever. The logs showed it firing over
            // and over for the same bot while the bot carried on backwards.
            //
            // Here it cannot be late and it cannot be bypassed, because every
            // aim this plugin writes passes through these two lines.
            var watch = intent.Watch;
            var travel = pawn.AbsVelocity;

            if (travel != null)
            {
                float speedSqr = (travel.X * travel.X) + (travel.Y * travel.Y);

                // Moving, rather than shuffling on the spot. A bot holding a
                // position may look wherever its angle is.
                if (speedSqr > 2500.0f)
                {
                    float travelBearing = KaiFormation.Bearing(0.0f, 0.0f, travel.X, travel.Y);
                    float watchBearing = KaiFormation.Bearing(ex, ey, watch.X, watch.Y);
                    float off = KaiFormation.AngleGap(travelBearing, watchBearing);

                    if (off > MaxLookBehindDeg)
                    {
                        // Look along the direction of travel, well ahead so
                        // the pitch stays level rather than at its own feet.
                        watch = new KaiPoint(
                            ex + (travel.X * 6.0f),
                            ey + (travel.Y * 6.0f),
                            origin.Z + KaiHeights.Chest);

                        KaiLog.Throttled($"lookback:{controller.Slot}",
                            nameof(OnUpdateLookAnglesPre),
                            $"slot {controller.Slot} '{intent.SourceName}' wanted to aim " +
                            $"{off:F0} degrees off a heading of {travelBearing:F0}. " +
                            $"Aiming where it is running instead.", 3.0f);
                    }
                }
            }

            float yaw = YawTo(ex, ey, watch.X, watch.Y);
            float pitch = PitchTo(ex, ey, ez, watch.X, watch.Y, watch.Z);

            // m_lookYawVel and m_lookPitchVel are left alone on purpose so the
            // native spring keeps its momentum and the turn stays smooth.
            ref float lookYaw = ref bot.LookYaw;
            lookYaw = yaw;

            ref float lookPitch = ref bot.LookPitch;
            lookPitch = pitch;

            KaiLog.Throttled($"aim:{controller.Slot}", nameof(OnUpdateLookAnglesPre),
                $"slot {controller.Slot} '{intent.SourceName}' -> yaw={yaw:F1} pitch={pitch:F1}", 2.0f);
        }
        catch (Exception ex)
        {
            KaiLog.Throttled("aimerr", nameof(OnUpdateLookAnglesPre),
                $"exception: {ex.Message}", 5.0f, KaiLogLevel.Error);
        }

        return HookResult.Continue;
    }

    // ------------------------------------------------------------------
    // Native hook: movement and buttons
    //
    // CCSBot::Update runs the state machine including path following, and
    // leaves its movement decision in CBot::m_forwardSpeed and m_leftSpeed and
    // its button decision in CBot::m_buttonFlags. Zeroing the speeds in a POST
    // hook pins the bot while leaving vision, target acquisition and firing
    // untouched, which is the difference between a bot holding an angle and a
    // bot that has been switched off.
    //
    // The speed write is the mechanism I am confident about. The button write
    // is not verified against a live build, which is why the CT defuser is
    // also physically pinned at a standoff radius. Run kai_log 2 and read the
    // before and after values to see which one is doing the work.
    // ------------------------------------------------------------------

    private HookResult OnBotUpdatePost(DynamicHook hook)
    {
        try
        {
            IntPtr pBot = hook.GetParam<IntPtr>(0);

            if (pBot == IntPtr.Zero)
            {
                return HookResult.Continue;
            }

            var bot = new CCSBot(pBot);
            var controller = bot.Controller;

            if (controller == null || !controller.IsValid)
            {
                return HookResult.Continue;
            }

            // Round-win celebration: hold the attack button down.
            //
            // This runs alongside the InjectUsercmd call in
            // DriveCelebrationFire rather than replacing it, because the two
            // take different routes and only one of them may be effective on a
            // given build. Injection goes in at the usercmd layer; this write
            // happens after CCSBot::Update has set the bot's own buttons for
            // the frame, so anything the bot decided cannot overwrite it.
            //
            // A bot does not need a target to fire. The weapon discharges
            // because IN_ATTACK is in the usercmd, nothing more.
            if (IsCelebrating() && (int)controller.TeamNum == _celebrateTeam)
            {
                ulong beforeButtons = bot.ButtonFlags;

                ref ulong celebrateButtons = ref bot.ButtonFlags;
                celebrateButtons = beforeButtons | (ulong)PlayerButtons.Attack;

                KaiLog.Throttled($"celebrate_btn:{controller.Slot}", nameof(OnBotUpdatePost),
                    $"slot {controller.Slot} attack bit set, flags 0x{beforeButtons:X} -> " +
                    $"0x{bot.ButtonFlags:X} (unchanged here means the write is not sticking)",
                    1.0f);

                return HookResult.Continue;
            }

            if (!_intents.TryGetValue(controller.Slot, out var intent))
            {
                return HookResult.Continue;
            }

            if (Server.CurrentTime - intent.Stamp > IntentStaleSeconds)
            {
                return HookResult.Continue;
            }

            // USE suppression applies regardless of contact. A CT that has
            // just spotted a T should still not be defusing.
            if (intent.SuppressUse)
            {
                ulong before = bot.ButtonFlags;

                ref ulong buttons = ref bot.ButtonFlags;
                buttons = before & ~(ulong)PlayerButtons.Use;

                if ((before & (ulong)PlayerButtons.Use) != 0)
                {
                    KaiLog.Throttled($"use:{controller.Slot}", nameof(OnBotUpdatePost),
                        $"slot {controller.Slot} USE cleared, flags 0x{before:X} -> 0x{bot.ButtonFlags:X}",
                        2.0f);
                }
            }

            if (!intent.Anchored)
            {
                return HookResult.Continue;
            }

            // Release the movement pin on the same signals that release the
            // aim. A bot pinned in place while something is happening behind
            // it is worse than the stock behaviour this replaced.
            // Nothing aims behind a moving bot. Ever.
            //
            // Judged on the bot's own VELOCITY, not on whether this plugin
            // happens to be steering it. The previous version only checked
            // bots with a SteerTowards set, which meant a bot walking under
            // native pathing with its aim overridden by pre-aim was never
            // checked at all: it had no waypoint of ours to compare against.
            // That is the case that produced a bot travelling to mid facing
            // backwards the entire way, and the guard logged nothing because
            // it never ran.
            //
            // Velocity is true regardless of who is doing the steering, so
            // this now covers every moving bot however it came to be moving.
            if (intent.Watch != null)
            {
                var movingPawn = controller.PlayerPawn?.Value;
                var movingOrigin = movingPawn?.AbsOrigin;
                var movingVelocity = movingPawn?.AbsVelocity;

                if (movingOrigin != null && movingVelocity != null)
                {
                    float speedSqr = (movingVelocity.X * movingVelocity.X)
                                     + (movingVelocity.Y * movingVelocity.Y);

                    // Only while actually moving. A bot holding a position is
                    // entitled to look wherever its angle is.
                    if (speedSqr > 1600.0f)
                    {
                        float travelBearing = KaiFormation.Bearing(
                            0.0f, 0.0f, movingVelocity.X, movingVelocity.Y);

                        float watchBearing = KaiFormation.Bearing(
                            movingOrigin.X, movingOrigin.Y,
                            intent.Watch.X, intent.Watch.Y);

                        float off = KaiFormation.AngleGap(travelBearing, watchBearing);

                        if (off > 95.0f)
                        {
                            KaiLog.Throttled($"backwards:{controller.Slot}",
                                nameof(OnBotUpdatePost),
                                $"slot {controller.Slot} was about to travel backwards on " +
                                $"'{intent.SourceName}': watch {off:F0} degrees off a heading of " +
                                $"{travelBearing:F0}. Pointing it along its own velocity instead.",
                                5.0f);

                            // Aim down the direction of travel, a good way out
                            // so the pitch is level rather than at its feet.
                            intent.Watch = new KaiPoint(
                                movingOrigin.X + (movingVelocity.X * 4.0f),
                                movingOrigin.Y + (movingVelocity.Y * 4.0f),
                                movingOrigin.Z + KaiHeights.Chest);
                        }
                    }
                }
            }

            // Steering. Written before the pin check, and skipped entirely on
            // contact, so a bot that finds a fight on the way stops shuffling
            // and deals with it.
            //
            // m_forwardSpeed and m_leftSpeed are relative to the bot's view
            // yaw, not to the world, so a world direction has to be projected
            // onto the bot's own forward and left vectors. That decoupling is
            // what lets a bot walk sideways towards a sightline while its
            // crosshair stays on the bomb.
            if (intent.SteerTowards != null && !ShouldReleasePin(bot))
            {
                var steerPawn = controller.PlayerPawn?.Value;
                var steerOrigin = steerPawn?.AbsOrigin;

                if (steerPawn != null && steerOrigin != null)
                {
                    float dx = intent.SteerTowards.X - steerOrigin.X;
                    float dy = intent.SteerTowards.Y - steerOrigin.Y;
                    float len = MathF.Sqrt((dx * dx) + (dy * dy));

                    if (len > 24.0f)
                    {
                        dx /= len;
                        dy /= len;

                        float yawRad = steerPawn.EyeAngles.Y * MathF.PI / 180.0f;

                        // Source basis: forward is (cos, sin), left is (-sin, cos).
                        float fwd = (dx * MathF.Cos(yawRad)) + (dy * MathF.Sin(yawRad));
                        float lft = (dx * -MathF.Sin(yawRad)) + (dy * MathF.Cos(yawRad));

                        const float walkSpeed = 210.0f;

                        ref float steerForward = ref bot.ForwardSpeed;
                        steerForward = fwd * walkSpeed;

                        ref float steerLeft = ref bot.LeftSpeed;
                        steerLeft = lft * walkSpeed;

                        // A knife rush moves unpredictably on purpose.
                        //
                        // Straight-line movement into somebody holding a gun
                        // is a free kill for them. Strafing across the line of
                        // travel and jumping does not make the knife good, but
                        // it makes the bot harder to track, which is the only
                        // variable still available to it.
                        if (intent.Erratic)
                        {
                            float wobble = MathF.Sin(Server.CurrentTime * 7.0f);

                            steerLeft += wobble * 180.0f;

                            ref bool erraticRunning = ref bot.IsRunning;
                            erraticRunning = true;

                            // Jump on the upswing, which with the strafe gives
                            // the classic hard-to-hit approach rather than a
                            // predictable hop.
                            if (wobble > 0.85f)
                            {
                                ulong before = bot.ButtonFlags;

                                ref ulong jumpButtons = ref bot.ButtonFlags;
                                jumpButtons = before | (ulong)PlayerButtons.Jump;
                            }

                            KaiLog.Throttled($"erratic:{controller.Slot}",
                                nameof(OnBotUpdatePost),
                                $"slot {controller.Slot} moving erratically on " +
                                $"'{intent.SourceName}'", 2.0f);
                        }

                        // No jumping while being steered.
                        //
                        // The same anti-stuck reflex that made pinned bots hop
                        // on the spot fires while steering too, because
                        // overriding the movement command looks to the state
                        // machine like being unable to move. A bot bunny
                        // hopping down a corridor is slower, louder and
                        // cannot shoot, which is three ways worse than
                        // walking.
                        ulong beforeSteerJump = bot.ButtonFlags;

                        if (!intent.Erratic
                            && (beforeSteerJump & (ulong)PlayerButtons.Jump) != 0)
                        {
                            ref ulong steerButtons = ref bot.ButtonFlags;
                            steerButtons = beforeSteerJump & ~(ulong)PlayerButtons.Jump;

                            KaiLog.Throttled($"steerjump:{controller.Slot}",
                                nameof(OnBotUpdatePost),
                                $"slot {controller.Slot} jump suppressed while steering on " +
                                $"'{intent.SourceName}'", 3.0f);
                        }

                        // Walking is silent; running is not. When the intent
                        // asks for a quiet move, the run flag is cleared and
                        // the walk button held, so the bot repositions without
                        // masking the sound it is listening for.
                        ref bool steerRunning = ref bot.IsRunning;
                        steerRunning = !intent.Walk;

                        if (intent.Walk)
                        {
                            ulong beforeWalk = bot.ButtonFlags;

                            ref ulong walkButtons = ref bot.ButtonFlags;
                            walkButtons = beforeWalk | (ulong)PlayerButtons.Speed;

                            // Walk speed also scales the movement command, so
                            // the steering does not fight the button.
                            steerForward *= 0.5f;
                            steerLeft *= 0.5f;
                        }

                        KaiLog.Throttled($"steer:{controller.Slot}", nameof(OnBotUpdatePost),
                            $"slot {controller.Slot} steering '{intent.SourceName}', " +
                            $"{len:F0} units to go, fwd={steerForward:F0} left={steerLeft:F0}",
                            1.0f);

                        return HookResult.Continue;
                    }
                }
            }

            // A committed plant or defuse is never released by contact.
            //
            // This is the whole rule: being shot at while planting is not a
            // reason to stop planting, it is the reason team mates are on the
            // site. Without this exception the pin comes straight off the
            // moment an enemy appears, which is exactly when it matters most.
            bool committed = intent.SourceName == "planting:committed"
                             || intent.SourceName == "defusing:committed";

            if (committed)
            {
                KaiLog.Throttled($"committed:{controller.Slot}", nameof(OnBotUpdatePost),
                    $"slot {controller.Slot} holding '{intent.SourceName}' through contact",
                    3.0f);
            }

            // Only real contact releases the pin. A noise makes the bot turn,
            // handled in the aim hook, but does not make it leave its position.
            if (!committed && ShouldReleasePin(bot))
            {
                KaiLog.Throttled($"release:{controller.Slot}", nameof(OnBotUpdatePost),
                    $"slot {controller.Slot} in contact, movement pin released", 2.0f);
                return HookResult.Continue;
            }

            float beforeSpeed = bot.ForwardSpeed;

            ref float forward = ref bot.ForwardSpeed;
            forward = 0.0f;

            ref float left = ref bot.LeftSpeed;
            left = 0.0f;

            ref bool running = ref bot.IsRunning;
            running = false;

            // Hold the jump button down and a pinned bot bunny hops on the
            // spot. Zeroing its speed while its own state machine still wants
            // to move looks exactly like being stuck, and BotState's
            // anti-stuck handling responds by jumping. Clearing the bit is
            // cheaper than trying to convince the state machine it has
            // arrived somewhere.
            ulong beforeJump = bot.ButtonFlags;

            if ((beforeJump & (ulong)PlayerButtons.Jump) != 0)
            {
                ref ulong jumpButtons = ref bot.ButtonFlags;
                jumpButtons = beforeJump & ~(ulong)PlayerButtons.Jump;

                KaiLog.Throttled($"nojump:{controller.Slot}", nameof(OnBotUpdatePost),
                    $"slot {controller.Slot} jump suppressed while pinned " +
                    $"(anti-stuck fighting the pin)", 2.0f);
            }

            if (intent.Crouch)
            {
                ref bool crouching = ref bot.IsCrouching;
                crouching = true;
            }

            KaiLog.Throttled($"pin:{controller.Slot}", nameof(OnBotUpdatePost),
                $"slot {controller.Slot} pinned on '{intent.SourceName}', " +
                $"forwardSpeed {beforeSpeed:F1} -> {bot.ForwardSpeed:F1}", 3.0f);
        }
        catch (Exception ex)
        {
            KaiLog.Throttled("moveerr", nameof(OnBotUpdatePost),
                $"exception: {ex.Message}", 5.0f, KaiLogLevel.Error);
        }

        return HookResult.Continue;
    }

    // ------------------------------------------------------------------
    // Geometry
    // ------------------------------------------------------------------

    // Yaw in degrees, same convention as CCSPlayerPawn.EyeAngles.Y.
    private static float YawTo(float fromX, float fromY, float toX, float toY)
    {
        return MathF.Atan2(toY - fromY, toX - fromX) * 180.0f / MathF.PI;
    }

    // Pitch in degrees. Source convention, so positive means looking down.
    private static float PitchTo(
        float fromX, float fromY, float fromZ, float toX, float toY, float toZ)
    {
        float dx = toX - fromX;
        float dy = toY - fromY;
        float dz = toZ - fromZ;

        float horizontal = MathF.Sqrt((dx * dx) + (dy * dy));

        if (horizontal < 0.001f)
        {
            if (dz > 0.0f)
            {
                return -89.0f;
            }
            else
            {
                return 89.0f;
            }
        }

        return -MathF.Atan2(dz, horizontal) * 180.0f / MathF.PI;
    }

    private static float NormalizeYaw(float degrees)
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

    // ------------------------------------------------------------------
    // Console commands
    // ------------------------------------------------------------------

    [ConsoleCommand("kai_log", "Set KaiTactics log verbosity: 0 errors, 1 info, 2 verbose")]
    [CommandHelper(minArgs: 0, usage: "[0|1|2]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdLog(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (cmd.ArgCount > 1 && int.TryParse(cmd.GetArg(1), out int level))
        {
            KaiLog.Level = (KaiLogLevel)Math.Clamp(level, 0, 2);
        }

        cmd.ReplyToCommand($"[KaiTactics] log level = {KaiLog.Level}");
    }

    [ConsoleCommand("kai_logfile", "Control the per-map log file under kai_tactics/logs")]
    [CommandHelper(minArgs: 0, usage: "[on|off] [keepFiles]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdLogFile(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (cmd.ArgCount > 1)
        {
            bool wantOn = cmd.GetArg(1) != "0" && cmd.GetArg(1).ToLowerInvariant() != "off";
            KaiLog.FileEnabled = wantOn;

            if (wantOn)
            {
                KaiLog.OpenForMap(LogDir, _currentMap);
            }
            else
            {
                KaiLog.CloseCurrent();
            }
        }

        if (cmd.ArgCount > 2 && int.TryParse(cmd.GetArg(2), out int keep))
        {
            KaiLog.KeepFiles = Math.Max(0, keep);
        }

        string where = "none";

        if (KaiLog.CurrentLogPath != null)
        {
            where = KaiLog.CurrentLogPath;
        }

        cmd.ReplyToCommand(
            $"[KaiTactics] logfile enabled={KaiLog.FileEnabled} keep={KaiLog.KeepFiles} path={where}");
    }

    [ConsoleCommand("kai_enable", "Enable or disable all KaiTactics overrides at runtime")]
    [CommandHelper(minArgs: 0, usage: "[0|1]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdEnable(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (cmd.ArgCount > 1)
        {
            _enabled = cmd.GetArg(1) != "0";

            if (!_enabled)
            {
                _intents.Clear();
                _tPressureUntil.Clear();
                _retake.Reset("disabled by command");
            }
        }

        cmd.ReplyToCommand($"[KaiTactics] enabled = {_enabled}");
    }

    [ConsoleCommand("kai_retake", "Tune the CT retake director")]
    [CommandHelper(minArgs: 0,
        usage: "[on|off|fake on|fake off|inspect <sec>|dwell <sec>|bait <sec>|standoff <u>]",
        whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdRetake(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (cmd.ArgCount > 1)
        {
            string key = cmd.GetArg(1).ToLowerInvariant();
            string value = "";

            if (cmd.ArgCount > 2)
            {
                value = cmd.GetArg(2).ToLowerInvariant();
            }

            if (key == "on")
            {
                _retake.Enabled = true;
            }
            else if (key == "off")
            {
                _retake.Enabled = false;
                _retake.Reset("disabled by command");
            }
            else if (key == "fake")
            {
                _retake.FakeDefuseEnabled = value != "off";
            }
            else if ((key == "inspect" || key == "clear")
                     && float.TryParse(value, out float c))
            {
                _retake.InspectSeconds = c;
            }
            else if (key == "dwell" && float.TryParse(value, out float dw))
            {
                _retake.InspectDwellSeconds = dw;
            }
            else if (key == "bait" && float.TryParse(value, out float b))
            {
                _retake.BaitSeconds = b;
            }
            else if (key == "standoff" && float.TryParse(value, out float so))
            {
                _retake.DefuserStandoff = so;
            }
        }

        cmd.ReplyToCommand($"[KaiTactics] retake {_retake.StatusLine()}");
    }

    [ConsoleCommand("kai_rotate", "Tune how Ts abandon a hold when under fire")]
    [CommandHelper(minArgs: 0, usage: "[seconds <s>|radius <u>|yield <s>|noise <u>|ctpin 0|1|thold <u>|cover 0|1|coverback <u>|stickplant 0|1|sitemate <u>|support 0|1|supportrange <u>|sep <deg>|glance <s>|coverage <u>|arc <deg>|walk 0|1|walknear <u>]",
        whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdRotate(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (cmd.ArgCount > 2)
        {
            string key = cmd.GetArg(1).ToLowerInvariant();

            if (key == "seconds" && float.TryParse(cmd.GetArg(2), out float s))
            {
                _pressureSeconds = s;
            }
            else if (key == "radius" && float.TryParse(cmd.GetArg(2), out float r))
            {
                _pressureRadius = r;
            }
            else if (key == "yield" && float.TryParse(cmd.GetArg(2), out float y))
            {
                _yieldSeconds = y;
            }
            else if (key == "noise" && float.TryParse(cmd.GetArg(2), out float n))
            {
                _noiseRange = n;
            }
            else if (key == "ctpin")
            {
                _pinCtOnPreAim = cmd.GetArg(2) != "0";
            }
            else if (key == "thold" && float.TryParse(cmd.GetArg(2), out float th))
            {
                _tHoldNearBombRadius = th;
            }
            else if (key == "glance" && float.TryParse(cmd.GetArg(2), out float gd))
            {
                _glanceDwell = gd;
                _glanceSet.Clear();
            }
            else if (key == "arc" && float.TryParse(cmd.GetArg(2), out float ar))
            {
                _transitArcDeg = Math.Clamp(ar, 30.0f, 180.0f);
                _transitSet.Clear();
            }
            else if (key == "walk" && cmd.ArgCount > 2)
            {
                _walkNearAngles = cmd.GetArg(2) != "0";
            }
            else if (key == "walknear" && float.TryParse(cmd.GetArg(2), out float wn))
            {
                _walkNearDistance = wn;
            }
            else if (key == "coverage" && float.TryParse(cmd.GetArg(2), out float cr))
            {
                _coverageRange = cr;
                _glanceSet.Clear();
            }
            else if (key == "sep" && float.TryParse(cmd.GetArg(2), out float sep))
            {
                _watchSeparationDeg = sep;
                _tWatchClaims.Clear();
                _tWatchBearings.Clear();
            }
            else if (key == "support")
            {
                _supportFire = cmd.GetArg(2) != "0";
            }
            else if (key == "supportrange" && float.TryParse(cmd.GetArg(2), out float sr))
            {
                _supportRadius = sr;
            }
            else if (key == "stickplant" && cmd.ArgCount > 2)
            {
                _stickThePlant = cmd.GetArg(2) != "0";
            }
            else if (key == "sitemate" && float.TryParse(cmd.GetArg(2), out float sm))
            {
                _siteMateRadius = sm;
            }
            else if (key == "cover")
            {
                _coverSeeking = cmd.GetArg(2) != "0";
                _tCover.Clear();
            }
            else if (key == "coverback" && float.TryParse(cmd.GetArg(2), out float cb))
            {
                _coverBackDistance = cb;
                _tCover.Clear();
            }
        }

        cmd.ReplyToCommand(
            $"[KaiTactics] rotate seconds={_pressureSeconds:F1} radius={_pressureRadius:F0}u " +
            $"aimYield={_yieldSeconds:F1}s noiseRange={_noiseRange:F0}u ctPin={_pinCtOnPreAim} " +
            $"tHoldNearBomb={_tHoldNearBombRadius:F0}u cover={_coverSeeking} " +
            $"coverBack={_coverBackDistance:F0}u support={_supportFire}/{_supportRadius:F0}u " +
            $"contacts={_contacts.Count} pressured={_tPressureUntil.Count}");
    }

    [ConsoleCommand("kai_guard", "Tune how CTs hold a bomb lying on the ground")]
    [CommandHelper(minArgs: 0, usage: "[on|off|radius <u>|hold <u>|los 0|1|sweep 0|1|seek <u>|seektime <s>]",
        whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdGuard(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (cmd.ArgCount > 1)
        {
            string key = cmd.GetArg(1).ToLowerInvariant();

            if (key == "on")
            {
                _guardLooseBomb = true;
            }
            else if (key == "off")
            {
                _guardLooseBomb = false;
            }
            else if (key == "radius" && cmd.ArgCount > 2
                     && float.TryParse(cmd.GetArg(2), out float r))
            {
                _guardRadius = r;
            }
            else if (key == "hold" && cmd.ArgCount > 2
                     && float.TryParse(cmd.GetArg(2), out float h))
            {
                _guardHoldRadius = h;
            }
            else if (key == "sweep" && cmd.ArgCount > 2)
            {
                _guardSweepAngles = cmd.GetArg(2) != "0";
                _guardSet.Clear();
            }
            else if (key == "los" && cmd.ArgCount > 2)
            {
                _guardRequireLineOfSight = cmd.GetArg(2) != "0";
            }
            else if (key == "seek" && cmd.ArgCount > 2
                     && float.TryParse(cmd.GetArg(2), out float sk))
            {
                _guardSeekRange = sk;
            }
            else if (key == "seektime" && cmd.ArgCount > 2
                     && float.TryParse(cmd.GetArg(2), out float st))
            {
                _guardSeekSeconds = st;
            }
        }

        string where = "not loose";

        if (_looseBombPos != null)
        {
            where = $"({_looseBombPos.X:F0},{_looseBombPos.Y:F0},{_looseBombPos.Z:F0})";
        }

        cmd.ReplyToCommand(
            $"[KaiTactics] guard enabled={_guardLooseBomb} radius={_guardRadius:F0}u " +
            $"hold={_guardHoldRadius:F0}u los={_guardRequireLineOfSight} " +
            $"seek={_guardSeekRange:F0}u/{_guardSeekSeconds:F1}s " +
            $"rayTrace={KaiRayTraceBridge.Available()} bomb={where}");
    }

    [ConsoleCommand("kai_ghost", "Exclude humans from map learning, for unattended mapping")]
    [CommandHelper(minArgs: 0, usage: "[on|off|spectate]",
        whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdGhost(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (cmd.ArgCount > 1)
        {
            string key = cmd.GetArg(1).ToLowerInvariant();

            if (key == "off")
            {
                _ghostHumans = false;
                _ghostSpectate = false;

                cmd.ReplyToCommand(
                    "[KaiTactics] ghost mode off, humans count towards map learning again");
                return;
            }

            if (key == "on")
            {
                _ghostHumans = true;
                _ghostSpectate = false;

                cmd.ReplyToCommand(
                    "[KaiTactics] ghost mode on: engagements involving a human are discarded, " +
                    "but you stay on your team and can play normally");
                return;
            }

            if (key == "spectate")
            {
                _ghostHumans = true;
                _ghostSpectate = true;
                _nextGhostSweep = 0.0f;

                cmd.ReplyToCommand(
                    "[KaiTactics] ghost mode on with spectate: humans are moved to spectator " +
                    "and replaced with a bot, so the map is learned from a full bot match. " +
                    "Use kai_ghost off to rejoin.");
                return;
            }
        }

        cmd.ReplyToCommand(
            $"[KaiTactics] ghost={_ghostHumans} spectate={_ghostSpectate} " +
            $"discarded={_ghostDiscarded} engagement(s) this session");
        cmd.ReplyToCommand(
            "[KaiTactics] breadcrumbs already ignore humans; this only affects the death " +
            "sampling that builds pre-aim and hold spots");
    }

    [ConsoleCommand("kai_autoexec", "Config executed automatically on every map load")]
    [CommandHelper(minArgs: 0, usage: "[on|off|<config name>|delay <s>|now]",
        whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdAutoExec(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (cmd.ArgCount > 1)
        {
            string key = cmd.GetArg(1);
            string lower = key.ToLowerInvariant();

            if (lower == "off")
            {
                _autoExecEnabled = false;
            }
            else if (lower == "on")
            {
                _autoExecEnabled = true;
            }
            else if (lower == "now")
            {
                Server.ExecuteCommand($"exec {_autoExecConfig}");

                cmd.ReplyToCommand($"[KaiTactics] executed '{_autoExecConfig}.cfg' now");
                return;
            }
            else if (lower == "delay" && cmd.ArgCount > 2
                     && float.TryParse(cmd.GetArg(2), out float delay))
            {
                _autoExecDelay = Math.Clamp(delay, 0.0f, 60.0f);
            }
            else
            {
                // Anything else is taken as the config name. The .cfg suffix
                // is stripped because exec does not want it.
                if (lower.EndsWith(".cfg"))
                {
                    key = key.Substring(0, key.Length - 4);
                }

                _autoExecConfig = key;
                _autoExecEnabled = true;
            }
        }

        cmd.ReplyToCommand(
            $"[KaiTactics] autoexec enabled={_autoExecEnabled} config='{_autoExecConfig}.cfg' " +
            $"delay={_autoExecDelay:F1}s");
        cmd.ReplyToCommand(
            "[KaiTactics] runs once per map load, not per round, because these configs " +
            "usually end with mp_restartgame");
    }

    [ConsoleCommand("kai_arsenal", "Weapon awareness: dropped guns, dry bots and knife rushes")]
    [CommandHelper(minArgs: 0, usage: "[on|off|list|dry <n>|range <u>]",
        whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdArsenal(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (cmd.ArgCount > 1)
        {
            string key = cmd.GetArg(1).ToLowerInvariant();

            if (key == "on")
            {
                _arsenal.Enabled = true;
            }
            else if (key == "off")
            {
                _arsenal.Enabled = false;
            }
            else if (key == "dry" && cmd.ArgCount > 2
                     && int.TryParse(cmd.GetArg(2), out int dry))
            {
                _arsenal.DryThreshold = Math.Max(0, dry);
            }
            else if (key == "range" && cmd.ArgCount > 2
                     && float.TryParse(cmd.GetArg(2), out float range))
            {
                _arsenal.PickupRange = range;
            }
            else if (key == "list")
            {
                foreach (var p in KaiPlayers.All())
                {
                    if (p == null || !p.IsValid || !p.IsBot || !p.PawnIsAlive)
                    {
                        continue;
                    }

                    bool dryNow = _arsenal.IsDry(p, out bool armed);

                    if (dryNow)
                    {
                        cmd.ReplyToCommand(
                            $"  slot {p.Slot} is DRY (armed={armed}, " +
                            $"knifing={_arsenal.IsKnifing(p.Slot)})");
                    }
                }

                return;
            }
        }

        cmd.ReplyToCommand($"[KaiTactics] arsenal {_arsenal.Summary()}");
    }

    [ConsoleCommand("kai_comms", "Control what the bots say in team chat")]
    [CommandHelper(minArgs: 0, usage: "[off|calls|detail]",
        whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdComms(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (cmd.ArgCount > 1)
        {
            string key = cmd.GetArg(1).ToLowerInvariant();

            if (key == "off")
            {
                KaiComms.Level = KaiCommsLevel.Off;
            }
            else if (key == "calls")
            {
                KaiComms.Level = KaiCommsLevel.Calls;
            }
            else if (key == "detail")
            {
                KaiComms.Level = KaiCommsLevel.Detail;
            }
        }

        cmd.ReplyToCommand(
            $"[KaiTactics] comms {KaiComms.Summary()} | " +
            $"{KaiCallouts.AnchorCount} callout(s) loaded for {_currentMap}");
        cmd.ReplyToCommand(
            $"[KaiTactics] squad: " +
            string.Join(", ",
                KaiPlayers.All()
                    .Where(p => p != null && p.IsValid && KaiSquad.IsSquad(p.Slot))
                    .Select(p => $"{KaiSquad.NameOf(p.Slot)}{(p.PawnIsAlive ? "" : " (down)")}")));
        cmd.ReplyToCommand(
            "[KaiTactics] calls = plays, audibles, the defuse. " +
            "detail = clears, cover and formation as well. " +
            "Everything is team only; the other side never sees it.");
    }

    [ConsoleCommand("kai_maturity", "How far through learning this map is")]
    [CommandHelper(minArgs: 0, usage: "[reset|samples <n>|rounds <n>|calls <n>]",
        whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdMaturity(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (cmd.ArgCount > 1)
        {
            string key = cmd.GetArg(1).ToLowerInvariant();

            if (key == "reset")
            {
                _maturity.Reset();
                cmd.ReplyToCommand("[KaiTactics] maturity reset, all recorders live again");
                return;
            }

            if (key == "samples" && cmd.ArgCount > 2
                && int.TryParse(cmd.GetArg(2), out int samples))
            {
                _maturity.RequiredPostPlantSamples = Math.Max(1, samples);
                _maturity.RequiredClearSamples = Math.Max(1, samples);
            }
            else if (key == "rounds" && cmd.ArgCount > 2
                     && int.TryParse(cmd.GetArg(2), out int rounds))
            {
                _maturity.MinRoundsToMap = Math.Max(1, rounds);
            }
            else if (key == "calls" && cmd.ArgCount > 2
                     && int.TryParse(cmd.GetArg(2), out int calls))
            {
                _maturity.RequiredCallsPerPlay = Math.Max(1, calls);
            }
        }

        var e = BuildLearningEvidence();

        cmd.ReplyToCommand($"[KaiTactics] {_currentMap}: {_maturity.Describe()}");
        cmd.ReplyToCommand(
            $"[KaiTactics] rounds={_maturity.Rounds} (matches={_maturity.Matches}, not used) " +
            $"recordingMap={_maturity.RecordingMapData} learningPlays={_maturity.LearningPlays}");
        cmd.ReplyToCommand(
            $"[KaiTactics] map evidence: postPlant={e.PostPlantSamples}/" +
            $"{_maturity.RequiredPostPlantSamples} clear={e.ClearSamples}/" +
            $"{_maturity.RequiredClearSamples} rounds={_maturity.Rounds}/" +
            $"{_maturity.MinRoundsToMap} graph={e.GraphNodes} nodes " +
            $"(saturated={e.GraphSaturated}, {e.NewNodesThisSession} new this session)");
        cmd.ReplyToCommand(
            $"[KaiTactics] play evidence: least-tried play has {e.MinPlayCalls}/" +
            $"{_maturity.RequiredCallsPerPlay} calls across {e.PlayCount} plays " +
            $"({e.TotalPlayCalls} total)");

        // Bombsites gate the playbook, the solver and the router all at once,
        // so the count belongs on the dashboard rather than buried in the
        // solver's own status where it was easy to miss.
        string sites = _map.PlantSites.Count == 0
            ? "NONE RECORDED - plays, posts and routes cannot be generated until a round " +
              "ends with a plant"
            : string.Join(", ", _map.PlantSites.Select(
                (p, i) => $"{i}={SiteName(i)} ({p.X:F0},{p.Y:F0})"));

        cmd.ReplyToCommand(
            $"[KaiTactics] bombsites={_map.PlantSites.Count}: {sites}");

        cmd.ReplyToCommand(
            $"[KaiTactics] generated: {_map.PreAim.Count} pre-aim, {_map.PostPlant.Count} " +
            $"post-plant, {_map.CtClear.Count} clear | solved {_map.SolvedTPosts.Count} T / " +
            $"{_map.SolvedCtPosts.Count} CT posts | {_routes.Routes.Count} route(s)");
        cmd.ReplyToCommand($"[KaiTactics] command: {_command.Summary()}");
    }

    [ConsoleCommand("kai_plays", "Inspect the tactical controller and its win record")]
    [CommandHelper(minArgs: 0, usage: "[list|reset|bias <0-1>]",
        whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdPlays(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (cmd.ArgCount > 1)
        {
            string key = cmd.GetArg(1).ToLowerInvariant();

            if (key == "reset")
            {
                _tactics.ClearRecord();
                cmd.ReplyToCommand("[KaiTactics] play record cleared");
                return;
            }

            if ((key == "bias" || key == "explore") && cmd.ArgCount > 2
                && float.TryParse(cmd.GetArg(2), out float bias))
            {
                _tactics.OutcomeBias = Math.Clamp(bias, 0.0f, 1.0f);

                cmd.ReplyToCommand(
                    $"[KaiTactics] outcome bias = {_tactics.OutcomeBias:F2} " +
                    $"(0 is pure variety, 1 always takes the best record)");
                return;
            }

            if (key == "list")
            {
                foreach (var play in _tactics.AllPlays()
                             .OrderByDescending(p => p.Called == 0 ? -1.0f : p.WinRate))
                {
                    string rate = play.Called == 0
                        ? "untried"
                        : $"{play.WinRate * 100.0f:F0}%";

                    cmd.ReplyToCommand(
                        $"  {play.Name,-18} team={play.Team} {play.Kind} site={play.Site} " +
                        $"{play.Won}/{play.Called} {rate} abandoned={play.Abandoned}");
                }

                return;
            }
        }

        cmd.ReplyToCommand(
            $"[KaiTactics] selection: shuffled bag, outcomeBias={_tactics.OutcomeBias:F2}. " +
            $"T bag has {_tactics.BagRemaining((int)CsTeam.Terrorist)} play(s) left, " +
            $"CT bag {_tactics.BagRemaining((int)CsTeam.CounterTerrorist)}");
        cmd.ReplyToCommand($"[KaiTactics] T: {_tactics.Summary((int)CsTeam.Terrorist)}");
        cmd.ReplyToCommand($"[KaiTactics] CT: {_tactics.Summary((int)CsTeam.CounterTerrorist)}");

        var contacts = string.Join(", ",
            _contactsBySite.Select((c, i) => $"site{i}={c}"));

        cmd.ReplyToCommand(
            $"[KaiTactics] contacts this round: {contacts} | " +
            $"deaths T={_friendlyDeaths} CT={_enemyDeaths}");
    }

    [ConsoleCommand("kai_routes", "Inspect and control the static route book")]
    [CommandHelper(minArgs: 0, usage: "[on|off|list|regen|fake <0-1>|decoys <n>]",
        whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdRoutes(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (cmd.ArgCount > 1)
        {
            string key = cmd.GetArg(1).ToLowerInvariant();

            if (key == "on")
            {
                _useRoutes = true;
            }
            else if (key == "off")
            {
                _useRoutes = false;
                _routeOf.Clear();
                _routeLeg.Clear();
            }
            else if (key == "regen")
            {
                if (!IsSafeBuildPhase(out string phase))
                {
                    cmd.ReplyToCommand($"[KaiTactics] refusing to regenerate routes: {phase}");
                    return;
                }

                GenerateRoutes();
                cmd.ReplyToCommand($"[KaiTactics] regenerated: {_routes.Routes.Count} route(s)");
                return;
            }
            else if (key == "fake" && cmd.ArgCount > 2
                     && float.TryParse(cmd.GetArg(2), out float chance))
            {
                _fakeRotateChance = Math.Clamp(chance, 0.0f, 1.0f);
            }
            else if (key == "decoys" && cmd.ArgCount > 2
                     && int.TryParse(cmd.GetArg(2), out int decoys))
            {
                _decoyCount = Math.Max(0, decoys);
            }

            else if (key == "list")
            {
                foreach (var route in _routes.Routes)
                {
                    cmd.ReplyToCommand(
                        $"  {route.Name} {route.Kind} team={route.Team} " +
                        $"to={route.ToSite} wp={route.Waypoints.Count} " +
                        $"len={route.Length:F0} cover={route.Coverage}");
                }

                return;
            }
        }

        string rotation = "none";

        if (_rotation != null)
        {
            rotation = $"team{_rotation.Team}->site{_rotation.ToSite} " +
                       $"fake={_rotation.IsFake} at={_rotation.ReverseAt:F2} " +
                       $"reversing={_rotation.Reversing} members={_rotation.Members.Count}";
        }

        cmd.ReplyToCommand(
            $"[KaiTactics] routes enabled={_useRoutes} book={_routes.Routes.Count} " +
            $"generated={_routes.GeneratedUtc} spawns={_spawns.Count} " +
            $"running={_routeOf.Count} fakeChance={_fakeRotateChance:F2}");
        cmd.ReplyToCommand(
            $"[KaiTactics] rotation: {rotation} | decoys={_decoySite.Count}/{_decoyCount} " +
            $"realSite={_realTargetSite}");
    }

    [ConsoleCommand("kai_solve", "Pre-compute the best holding positions for this map")]
    [CommandHelper(minArgs: 0, usage: "[run|clear|status|force|auto 0|1]",
        whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdSolve(CCSPlayerController? caller, CommandInfo cmd)
    {
        string key = "";

        if (cmd.ArgCount > 1)
        {
            key = cmd.GetArg(1).ToLowerInvariant();
        }

        if (key == "auto")
        {
            if (cmd.ArgCount > 2)
            {
                _autoSolve = cmd.GetArg(2) != "0";
                _nextAutoSolveCheck = 0.0f;
            }

            cmd.ReplyToCommand($"[KaiTactics] auto solve = {_autoSolve}");
            return;
        }

        if (key == "clear")
        {
            _map.SolvedTPosts.Clear();
            _map.SolvedCtPosts.Clear();
            _map.SolvedUtc = "";
            _solver.Reset();
            _solveQueue.Clear();
            _tCover.Clear();
            _glanceSet.Clear();

            cmd.ReplyToCommand("[KaiTactics] solved posts cleared");
            return;
        }

        if (key == "run" || key == "force")
        {
            bool safe = IsSafeBuildPhase(out string phase);

            if (!safe && key != "force")
            {
                cmd.ReplyToCommand(
                    $"[KaiTactics] refusing to solve: {phase}. The solve spends a trace budget " +
                    $"every tick until it finishes and clears live assignments when it lands.");
                cmd.ReplyToCommand("[KaiTactics] wait for freezetime, or use: kai_solve force");
                return;
            }

            if (!_crumbs.IsUsable)
            {
                cmd.ReplyToCommand(
                    "[KaiTactics] cannot solve: the breadcrumb graph is not usable yet. " +
                    "Play more rounds so bots map out where they can walk, then try again.");
                return;
            }

            if (_map.PreAim.Count == 0)
            {
                cmd.ReplyToCommand(
                    "[KaiTactics] cannot solve: no pre-aim data to score positions against. " +
                    "Run kai_learn build first.");
                return;
            }

            if (!BeginSolveQueue(caller))
            {
                cmd.ReplyToCommand("[KaiTactics] solve could not start, see the log");
                return;
            }

            cmd.ReplyToCommand(
                $"[KaiTactics] solving {_map.PlantSites.Count} bombsite(s) plus the CT early " +
                $"round against {_map.PreAim.Count} known angles. This runs over the next few " +
                $"seconds; the console will report when it lands.");

            return;
        }

        cmd.ReplyToCommand(
            $"[KaiTactics] solve auto={_autoSolve} {_solver.Summary()} queued={_solveQueue.Count} " +
            $"sites={_map.PlantSites.Count} tPosts={_map.SolvedTPosts.Count} " +
            $"ctPosts={_map.SolvedCtPosts.Count} solvedUtc={_map.SolvedUtc}");
    }

    [ConsoleCommand("kai_crumbs", "Control the breadcrumb navigation graph recorder")]
    [CommandHelper(minArgs: 0, usage: "[on|off|save|clear|coverage|resume|max <n>|minusable <n>|cell <xy> <z>|rate <hz>]",
        whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdCrumbs(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (cmd.ArgCount > 1)
        {
            string key = cmd.GetArg(1).ToLowerInvariant();

            if (key == "on")
            {
                _crumbs.Enabled = true;
            }
            else if (key == "off")
            {
                _crumbs.Enabled = false;
            }
            else if (key == "save")
            {
                bool ok = _crumbs.Save("kai_crumbs save");
                cmd.ReplyToCommand($"[KaiTactics] breadcrumb save {(ok ? "succeeded" : "failed")}");
                return;
            }
            else if (key == "clear")
            {
                _crumbs.Clear();
                _crumbs.Save("kai_crumbs clear");
            }
            else if (key == "coverage")
            {
                cmd.ReplyToCommand($"[KaiTactics] {_crumbs.CoverageReport()}");
                return;
            }
            else if (key == "resume")
            {
                _crumbs.Resume();
            }
            else if (key == "max" && cmd.ArgCount > 2
                     && int.TryParse(cmd.GetArg(2), out int maxNodes))
            {
                _crumbs.MaxNodes = Math.Max(100, maxNodes);
            }
            else if (key == "minusable" && cmd.ArgCount > 2
                     && int.TryParse(cmd.GetArg(2), out int minUsable))
            {
                _crumbs.MinUsableNodes = Math.Max(1, minUsable);
            }
            else if (key == "cell" && cmd.ArgCount > 3
                     && float.TryParse(cmd.GetArg(2), out float xy)
                     && float.TryParse(cmd.GetArg(3), out float z))
            {
                // Changing resolution invalidates every existing cell key, so
                // the graph on disk stops being readable by this build. Said
                // out loud rather than discovered later.
                _crumbs.CellSizeXY = xy;
                _crumbs.CellSizeZ = z;

                cmd.ReplyToCommand(
                    "[KaiTactics] cell size changed. The existing graph was recorded at the " +
                    "old size and will be ignored on next load. Use kai_crumbs clear to start over.");
            }
            else if (key == "rate" && cmd.ArgCount > 2
                     && float.TryParse(cmd.GetArg(2), out float hz) && hz > 0.0f)
            {
                _crumbs.SampleInterval = 1.0f / hz;
            }
        }

        cmd.ReplyToCommand($"[KaiTactics] crumbs {_crumbs.Summary()}");
    }

    [ConsoleCommand("kai_learn", "Control the automatic spot learner")]
    [CommandHelper(minArgs: 0,
        usage: "[on|off|status|build|build force|clear|min <n>|radius <u>|zt <u>|yaw <deg>|maxpre <n>|maxpost <n>|maxclear <n>]",
        whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdLearn(CCSPlayerController? caller, CommandInfo cmd)
    {
        string key = "";
        string value = "";

        if (cmd.ArgCount > 1)
        {
            key = cmd.GetArg(1).ToLowerInvariant();
        }

        if (cmd.ArgCount > 2)
        {
            value = cmd.GetArg(2).ToLowerInvariant();
        }

        if (key == "on")
        {
            _learner.Enabled = true;
        }
        else if (key == "off")
        {
            _learner.Enabled = false;
        }
        else if (key == "clear")
        {
            _learner.ClearBank();
            _learner.SaveBank("kai_learn clear");
        }
        else if (key == "min" && int.TryParse(value, out int minSamples))
        {
            _learner.MinSamples = Math.Max(1, minSamples);
        }
        else if (key == "radius" && float.TryParse(value, out float radius))
        {
            _learner.XyRadius = radius;
        }
        else if (key == "zt" && float.TryParse(value, out float zt))
        {
            _learner.ZTolerance = zt;
        }
        else if (key == "yaw" && float.TryParse(value, out float yawTol))
        {
            _learner.YawTolerance = yawTol;
        }
        else if (key == "maxpre" && int.TryParse(value, out int maxPre))
        {
            _learner.MaxPreAim = Math.Max(1, maxPre);
        }
        else if (key == "maxpost" && int.TryParse(value, out int maxPost))
        {
            _learner.MaxPostPlant = Math.Max(1, maxPost);
        }
        else if (key == "maxclear" && int.TryParse(value, out int maxClear))
        {
            _learner.MaxCtClear = Math.Max(1, maxClear);
        }
        else if (key == "build")
        {
            RunBuild(cmd, value == "force");
            return;
        }

        cmd.ReplyToCommand(
            $"[KaiTactics] learn enabled={_learner.Enabled} min={_learner.MinSamples} " +
            $"radius={_learner.XyRadius:F0}u zTol={_learner.ZTolerance:F0}u " +
            $"yawTol={_learner.YawTolerance:F0}deg");
        cmd.ReplyToCommand(
            $"[KaiTactics] caps preAim={_learner.MaxPreAim} postPlant={_learner.MaxPostPlant} " +
            $"ctClear={_learner.MaxCtClear}");
        cmd.ReplyToCommand($"[KaiTactics] {_learner.Summary()}");
    }

    // Rebuild the tactics file from the sample bank.
    //
    // Guarded on the game phase because a rebuild clears every assignment and
    // resets the retake director. Run mid-round it silently throws away that
    // round's post-plant behaviour, and rewrites both JSON files while the
    // round is still generating samples into one of them.
    private void RunBuild(CommandInfo cmd, bool force)
    {
        bool safe = IsSafeBuildPhase(out string phase);

        if (!safe && !force)
        {
            cmd.ReplyToCommand(
                $"[KaiTactics] refusing to build: {phase}. A rebuild clears live assignments " +
                $"and resets the retake director.");
            cmd.ReplyToCommand(
                "[KaiTactics] wait for freezetime or warmup, or use: kai_learn build force");

            KaiLog.Event(nameof(RunBuild), $"build refused, phase='{phase}'");
            return;
        }

        if (!safe && force)
        {
            cmd.ReplyToCommand($"[KaiTactics] forcing a build during '{phase}'");
            KaiLog.Event(nameof(RunBuild), $"build FORCED during '{phase}'", KaiLogLevel.Error);
        }

        // Bank first. If anything below throws, the irreplaceable file is
        // already safely on disk with a backup beside it.
        _learner.SaveBank("kai_learn build");

        _map = _learner.Build(_map);
        _map.MapName = _currentMap;

        bool saved = KaiTacticsLoader.Save(DataDir, _map, "kai_learn build");

        _intents.Clear();
        _tPressureUntil.Clear();
        _retake.Reset("tactics rebuilt");

        cmd.ReplyToCommand(
            $"[KaiTactics] built '{_currentMap}' at {_map.GeneratedUtc} during '{phase}': " +
            $"{_map.PostPlant.Count} T post-plant, {_map.CtClear.Count} CT clear, " +
            $"{_map.PreAim.Count} pre-aim from {_map.SourceSamples} samples / " +
            $"{_map.SourceEngagements} engagements (disk write {saved})");
    }

    [ConsoleCommand("kai_thold", "Record your position as a T post-plant hold spot")]
    [CommandHelper(minArgs: 1, usage: "<name> [crouch]", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void CmdTHold(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (!TryGetAuthoringPose(caller, out var stand, out var watch))
        {
            cmd.ReplyToCommand("[KaiTactics] could not read your position");
            return;
        }

        var spot = new KaiHoldSpot
        {
            Name = cmd.GetArg(1),
            Team = (int)CsTeam.Terrorist,
            Anchor = stand,
            Watch = watch,
            Crouch = cmd.ArgCount > 2 && cmd.GetArg(2) == "crouch",
            Priority = _map.PostPlant.Count,
            Recorded = KaiTime.NowUtc(),
        };

        _map.PostPlant.Add(spot);
        _map.MapName = _currentMap;

        KaiLog.ToHumans(nameof(CmdTHold),
            $"T post-plant spot '{spot.Name}' recorded at {spot.Recorded}. Run kai_save.");
    }

    [ConsoleCommand("kai_ctclear", "Record your position as a CT clearing angle")]
    [CommandHelper(minArgs: 1, usage: "<name> [crouch|stage]",
        whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void CmdCtClear(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (!TryGetAuthoringPose(caller, out var stand, out var watch))
        {
            cmd.ReplyToCommand("[KaiTactics] could not read your position");
            return;
        }

        bool crouch = false;
        bool stage = false;

        if (cmd.ArgCount > 2)
        {
            string flag = cmd.GetArg(2).ToLowerInvariant();

            if (flag == "crouch")
            {
                crouch = true;
            }
            else if (flag == "stage")
            {
                stage = true;
            }
        }

        var spot = new KaiHoldSpot
        {
            Name = cmd.GetArg(1),
            Team = (int)CsTeam.CounterTerrorist,
            Anchor = stand,
            Watch = watch,
            Crouch = crouch,
            Stage = stage,
            Priority = _map.CtClear.Count,
            Recorded = KaiTime.NowUtc(),
        };

        _map.CtClear.Add(spot);
        _map.MapName = _currentMap;

        KaiLog.ToHumans(nameof(CmdCtClear),
            $"CT clear spot '{spot.Name}' recorded at {spot.Recorded} stage={stage}. Run kai_save.");
    }

    [ConsoleCommand("kai_preaim", "Record your position as a pre-aim trigger")]
    [CommandHelper(minArgs: 1, usage: "<name> [radius]", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void CmdPreAim(CCSPlayerController? caller, CommandInfo cmd)
    {
        if (!TryGetAuthoringPose(caller, out var stand, out var watch))
        {
            cmd.ReplyToCommand("[KaiTactics] could not read your position");
            return;
        }

        float radius = 160.0f;

        if (cmd.ArgCount > 2 && float.TryParse(cmd.GetArg(2), out float parsed))
        {
            radius = parsed;
        }

        var spot = new KaiPreAimSpot
        {
            Name = cmd.GetArg(1),
            Team = 0,
            Trigger = stand,
            TriggerRadius = radius,
            TriggerHeight = 70.0f,
            Watch = watch,
            Priority = _map.PreAim.Count,
            Recorded = KaiTime.NowUtc(),
        };

        _map.PreAim.Add(spot);
        _map.MapName = _currentMap;

        KaiLog.ToHumans(nameof(CmdPreAim),
            $"pre-aim spot '{spot.Name}' recorded at {spot.Recorded}. Run kai_save.");
    }

    [ConsoleCommand("kai_save", "Write the current map's tactics file to disk")]
    [CommandHelper(minArgs: 0, whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdSave(CCSPlayerController? caller, CommandInfo cmd)
    {
        _map.MapName = _currentMap;
        _map.GeneratedUtc = KaiTime.NowUtc();
        _map.GeneratorVersion = ModuleVersion + "-manual";

        bool ok = KaiTacticsLoader.Save(DataDir, _map, "kai_save");

        cmd.ReplyToCommand(
            $"[KaiTactics] save {(ok ? "succeeded" : "failed")} for '{_currentMap}' at {_map.GeneratedUtc}");
    }

    [ConsoleCommand("kai_reload", "Reload the current map's tactics file from disk")]
    [CommandHelper(minArgs: 0, whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdReload(CCSPlayerController? caller, CommandInfo cmd)
    {
        _map = KaiTacticsLoader.Load(DataDir, _currentMap);
        _intents.Clear();
        _tPressureUntil.Clear();
        _retake.Reset("tactics reloaded");

        cmd.ReplyToCommand(
            $"[KaiTactics] reloaded '{_currentMap}' generated {_map.GeneratedUtc}: " +
            $"{_map.PostPlant.Count} T post-plant, {_map.CtClear.Count} CT clear, " +
            $"{_map.PreAim.Count} pre-aim");
    }

    [ConsoleCommand("kai_list", "List the loaded spots for the current map")]
    [CommandHelper(minArgs: 0, whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdList(CCSPlayerController? caller, CommandInfo cmd)
    {
        cmd.ReplyToCommand(
            $"[KaiTactics] '{_currentMap}' generated {_map.GeneratedUtc} by v{_map.GeneratorVersion} " +
            $"from {_map.SourceSamples} samples / {_map.SourceEngagements} engagements");

        cmd.ReplyToCommand("[KaiTactics] T post-plant:");

        foreach (var s in _map.PostPlant)
        {
            cmd.ReplyToCommand(
                $"  {s.Name} anchor=({s.Anchor.X:F0},{s.Anchor.Y:F0},{s.Anchor.Z:F0}) " +
                $"n={s.Samples} pri={s.Priority} bombDist={s.BombDist:F0}");
        }

        cmd.ReplyToCommand("[KaiTactics] CT clear:");

        foreach (var s in _map.CtClear)
        {
            cmd.ReplyToCommand(
                $"  {s.Name} anchor=({s.Anchor.X:F0},{s.Anchor.Y:F0},{s.Anchor.Z:F0}) " +
                $"n={s.Samples} pri={s.Priority} bombDist={s.BombDist:F0} stage={s.Stage}");
        }

        cmd.ReplyToCommand("[KaiTactics] pre-aim:");

        foreach (var s in _map.PreAim)
        {
            cmd.ReplyToCommand(
                $"  {s.Name} team={s.Team} trigger=({s.Trigger.X:F0},{s.Trigger.Y:F0},{s.Trigger.Z:F0}) " +
                $"r={s.TriggerRadius:F0} h={s.TriggerHeight:F0} n={s.Samples}");
        }
    }

    // Read the calling human's stance and view direction. The watch point is
    // projected forward from the eye rather than traced, because all the plugin
    // needs is a direction: a point in mid air along the correct line produces
    // the same yaw and pitch as a point on the wall behind it.
    private bool TryGetAuthoringPose(
        CCSPlayerController? caller, out KaiPoint stand, out KaiPoint watch)
    {
        stand = new KaiPoint();
        watch = new KaiPoint();

        if (caller == null || !caller.IsValid)
        {
            return false;
        }

        var pawn = caller.PlayerPawn?.Value;
        var origin = pawn?.AbsOrigin;

        if (pawn == null || origin == null)
        {
            return false;
        }

        var angles = pawn.EyeAngles;
        float eyeZ = pawn.ViewOffset.Z;

        stand = new KaiPoint(origin.X, origin.Y, origin.Z);

        float yawRad = angles.Y * MathF.PI / 180.0f;
        float pitchRad = angles.X * MathF.PI / 180.0f;

        // Source forward vector. Pitch is positive downwards, hence the
        // negative sign on the Z component.
        float fx = MathF.Cos(yawRad) * MathF.Cos(pitchRad);
        float fy = MathF.Sin(yawRad) * MathF.Cos(pitchRad);
        float fz = -MathF.Sin(pitchRad);

        const float projection = 700.0f;

        watch = new KaiPoint(
            origin.X + (fx * projection),
            origin.Y + (fy * projection),
            origin.Z + eyeZ + (fz * projection));

        KaiLog.Event(nameof(TryGetAuthoringPose),
            $"caller at ({stand.X:F0},{stand.Y:F0},{stand.Z:F0}) " +
            $"pitch={angles.X:F1} yaw={angles.Y:F1} -> watch " +
            $"({watch.X:F0},{watch.Y:F0},{watch.Z:F0})");

        return true;
    }
}
