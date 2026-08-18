// kai_playbook.cs
//
// KAI ADDITION. Not part of CS2-Bot-Improver.
//
// The play caller.
//
// Everything else in this plugin answers "where should this bot stand" or
// "what should this bot look at". Nothing decided what the TEAM was trying to
// do, so the answer was hardcoded: Ts always execute a random site, CTs always
// patrol, and a rotation only happened if somebody typed a command. That is a
// team with no plan running the same play every round.
//
// This holds the plan. It calls a play at the start of a round, watches how
// the round actually develops, and calls an audible when what it is seeing
// stops matching what it planned for. Afterwards it records whether the play
// won, and that record weights what gets called next time.
//
// VARIETY, NOT OPTIMISATION
//
// This originally scored plays by win rate and called the best. That was the
// wrong objective. A round in this game turns on aim, timing, one lucky spray
// and a dozen things no play controls, so the outcome carries far more noise
// than signal, and selection that chases it converges on whatever happened to
// win early. A side that converges is a side you can read after three rounds,
// which defeats the entire purpose of having a playbook.
//
// So selection is a shuffled bag: every play for a side goes in, they are
// drawn without replacement, and the bag is reshuffled when empty. That gives
// the strongest variety guarantee there is. Every play runs once before any
// runs twice, and the order within each bag is unpredictable, which pure
// random cannot manage because pure random deals the same play three rounds
// running often enough to be noticed.
//
// The win record is still kept and still worth reading. It simply does not
// decide anything, unless OutcomeBias is deliberately raised.
//
// WHAT IT CANNOT DO
//
// It has no model of the opponent, and with variety-first selection it is not
// trying to build one. What it does instead is guarantee that the opposition
// never faces the same call twice in a row, and never faces one call more
// often than any other. The record is there to be read by a person, not to be
// optimised against by the machine.

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

public enum KaiPlayKind
{
    // Everybody hits one site together.
    Execute = 0,

    // Hit one site, with a decoy making noise at another.
    SplitFake = 1,

    // Take map control first, commit late.
    Default = 2,

    // CT: hold assigned positions and wait.
    Hold = 3,

    // CT: take map control aggressively for information.
    Aggro = 4,

    // CT: the bomb is on the ground. Sit on it and watch the ways back to it.
    //
    // A dropped bomb is the one piece of hard information a CT side gets for
    // free. The Ts must come back for it, so the CTs know where the fight will
    // be without having to go and find out. Camping it is a real play rather
    // than a fallback, which is why it sits in the book and earns a win record
    // like everything else.
    GuardBomb = 5,
}

public enum KaiAudibleKind
{
    None = 0,

    // The site we are going to is stacked. Go to the other one.
    SwitchSite = 1,

    // Rotate the defence towards where the enemy actually is.
    RotateDefence = 2,

    // Pretend to rotate, then come back.
    FakeRotate = 3,

    // Stop taking map control and commit now, the clock is going.
    CommitNow = 4,

    // We are down bodies. Stop pushing and hold what we have.
    PullBack = 5,

    // The bomb is loose. Go and sit on it.
    GuardBomb = 6,

    // The bomb has been picked up. Guarding it has failed and the ring around
    // where it used to be is now the worst formation on the map.
    BombRecovered = 7,
}

// One callable play, with its record.
public sealed class KaiPlay
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("team")]
    public int Team { get; set; }

    [JsonPropertyName("kind")]
    public KaiPlayKind Kind { get; set; }

    // Which bombsite the play is aimed at, or -1 for site-agnostic.
    [JsonPropertyName("site")]
    public int Site { get; set; } = -1;

    [JsonPropertyName("called")]
    public int Called { get; set; }

    [JsonPropertyName("won")]
    public int Won { get; set; }

    // Rounds where an audible pulled the team off this play. Tracked
    // separately from losses: a play that keeps getting abandoned is telling
    // you something different from one that gets run and loses.
    [JsonPropertyName("abandoned")]
    public int Abandoned { get; set; }

    [JsonPropertyName("lastCalledUtc")]
    public string LastCalledUtc { get; set; } = "";

    [JsonIgnore]
    public float WinRate
    {
        get
        {
            if (Called == 0)
            {
                return 0.0f;
            }

            return (float)Won / Called;
        }
    }
}

public sealed class KaiPlayBook
{
    [JsonPropertyName("mapName")]
    public string MapName { get; set; } = "";

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("lastWrittenUtc")]
    public string LastWrittenUtc { get; set; } = "";

    [JsonPropertyName("plays")]
    public List<KaiPlay> Plays { get; set; } = new();
}

// Everything the controller knows about the round right now.
//
// Assembled fresh each tick by the plugin. Kept as a plain snapshot so the
// controller can be reasoned about without knowing anything about hooks,
// entities or schema fields.
public sealed class KaiGameState
{
    public int FriendliesAlive;
    public int EnemiesAlive;
    public int Team;

    public bool BombPlanted;
    public bool BombDropped;
    public bool BombCarried;

    public float RoundElapsed;
    public float BombRemaining = -1.0f;

    // How many enemy contacts have been reported near each bombsite this
    // round. The controller's only real information about where the opposition
    // actually is.
    public int[] ContactsBySite = Array.Empty<int>();

    // Friendly deaths this round, and how many of those were trades.
    public int FriendlyDeaths;
    public int EnemyDeaths;
}

public sealed class KaiTacticalController
{
    // ------------------------------------------------------------------
    // Tunables
    // ------------------------------------------------------------------

    // How often to abandon variety and take the best-performing play instead.
    //
    // Zero by default, which means never. Variety is the objective: a side
    // that always runs its best play is one the opposition solves in three
    // rounds, and a round outcome in this game is too noisy to identify a best
    // play from anyway. Raise it only if you want the record acted on.
    public float OutcomeBias = 0.0f;

    // A site with at least this many reported contacts counts as stacked.
    public int StackedContacts = 2;

    // Do not audible in the first few seconds: the information that early is
    // one bot seeing one enemy, which is not a read.
    public float AudibleAfterSeconds = 8.0f;

    // Nor more than this often, so the team commits to something rather than
    // oscillating between two sites all round.
    public float AudibleCooldownSeconds = 12.0f;

    // Below this fraction of the side remaining, stop pushing.
    public float PullBackFraction = 0.4f;

    // Chance that a rotation the controller calls is a fake.
    public float FakeRotateChance = 0.35f;

    // ------------------------------------------------------------------
    // State
    // ------------------------------------------------------------------

    private KaiPlayBook _book = new();
    private string _dataDir = "";
    private string _mapName = "";

    private readonly Dictionary<int, KaiPlay> _current = new();
    private readonly Dictionary<int, float> _lastAudible = new();
    private readonly Dictionary<int, KaiAudibleKind> _lastAudibleKind = new();

    // team -> the plays not yet drawn from the current bag, and the last one
    // drawn, so a reshuffle cannot immediately repeat it.
    private readonly Dictionary<int, List<string>> _bag = new();
    private readonly Dictionary<int, string> _lastCalled = new();

    private readonly Random _random = new();

    public KaiPlay? CurrentPlay(int team)
    {
        return _current.GetValueOrDefault(team);
    }

    public string Summary(int team)
    {
        var play = CurrentPlay(team);

        string current = play == null
            ? "none"
            : $"{play.Name} ({play.Kind} site {play.Site}, {play.Won}/{play.Called} won)";

        return $"plays={_book.Plays.Count} current[{team}]={current} " +
               $"lastAudible={_lastAudibleKind.GetValueOrDefault(team)}";
    }

    public List<KaiPlay> AllPlays()
    {
        return _book.Plays;
    }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    public void OnMapStart(string dataDir, string mapName, int siteCount)
    {
        _dataDir = dataDir;
        _mapName = mapName;
        _current.Clear();
        _lastAudible.Clear();
        _lastAudibleKind.Clear();
        _bag.Clear();
        _lastCalled.Clear();

        Load();
        EnsurePlays(siteCount);
    }

    // Build the callable set for however many bombsites this map has.
    //
    // Generated rather than configured, so a map with three sites or one gets
    // a sensible book without anybody writing it out. Existing plays keep
    // their record; only missing ones are added.
    public void EnsurePlays(int siteCount)
    {
        if (siteCount <= 0)
        {
            return;
        }

        int added = 0;

        for (int site = 0; site < siteCount; site++)
        {
            added += AddIfMissing($"t_exec_s{site}", (int)CsTeam.Terrorist, KaiPlayKind.Execute, site);
            added += AddIfMissing($"t_split_s{site}", (int)CsTeam.Terrorist, KaiPlayKind.SplitFake, site);
            added += AddIfMissing($"t_default_s{site}", (int)CsTeam.Terrorist, KaiPlayKind.Default, site);

            added += AddIfMissing($"ct_hold_s{site}", (int)CsTeam.CounterTerrorist, KaiPlayKind.Hold, site);
        }

        added += AddIfMissing("ct_aggro", (int)CsTeam.CounterTerrorist, KaiPlayKind.Aggro, -1);
        added += AddIfMissing("ct_hold_spread", (int)CsTeam.CounterTerrorist, KaiPlayKind.Hold, -1);
        added += AddIfMissing("ct_guard_bomb", (int)CsTeam.CounterTerrorist, KaiPlayKind.GuardBomb, -1);

        if (added > 0)
        {
            // Anything mid-bag was shuffled from a smaller book and would
            // never deal the new plays. Dropped so the next draw reshuffles
            // across everything.
            _bag.Clear();

            KaiLog.Event(nameof(EnsurePlays),
                $"playbook for '{_mapName}' extended with {added} play(s) for {siteCount} " +
                $"bombsite(s), {_book.Plays.Count} total");

            Save();
        }
    }

    private int AddIfMissing(string name, int team, KaiPlayKind kind, int site)
    {
        foreach (var play in _book.Plays)
        {
            if (play.Name == name)
            {
                return 0;
            }
        }

        _book.Plays.Add(new KaiPlay
        {
            Name = name,
            Team = team,
            Kind = kind,
            Site = site,
        });

        return 1;
    }
    // Pick what this side is doing this round.
    //
    // A shuffled bag, not an outcome ranking.
    //
    // This started as upper confidence bound over the win record, and that was
    // the wrong tool. A round in this game is decided by aim, timing, one
    // lucky spray and half a dozen things the play never touched, so the
    // outcome is mostly noise with a faint signal in it. Selection that chases
    // that noise converges anyway, on whatever happened to get lucky in the
    // first few attempts, and a side that converges is a side you can read.
    // The point of a playbook is that you cannot.
    //
    // So every play for this side goes into a bag, the bag is shuffled, and
    // plays are drawn without replacement until it is empty. That gives the
    // strongest variety guarantee available: every play runs once before any
    // runs twice, and the order inside each bag is unpredictable. Pure random
    // would not manage that, because pure random happily deals the same play
    // three rounds running.
    //
    // The win record is still kept. It is worth looking at, and OutcomeBias
    // can put weight back on it if you ever want to, but it is off by default
    // and nothing depends on it.
    public KaiPlay? CallPlay(int team, KaiGameState state)
    {
        var options = _book.Plays.Where(p => p.Team == team).ToList();

        if (options.Count == 0)
        {
            return null;
        }

        KaiPlay? chosen = null;
        string why;

        // Optional, and zero by default. Kept so the record can be acted on
        // deliberately rather than the machinery having to be rebuilt.
        if (OutcomeBias > 0.0f && _random.NextDouble() < OutcomeBias)
        {
            chosen = options
                .OrderByDescending(p => p.Called == 0 ? 1.0f : p.WinRate)
                .First();

            why = $"outcome bias fired at {OutcomeBias:F2}, taking the best record";
        }
        else
        {
            chosen = DrawFromBag(team, options);
            why = $"drawn from the bag, {BagRemaining(team)} play(s) left before it reshuffles";
        }

        if (chosen == null)
        {
            return null;
        }

        chosen.Called++;
        chosen.LastCalledUtc = KaiTime.NowUtc();

        _current[team] = chosen;
        _lastAudible[team] = 0.0f;
        _lastAudibleKind[team] = KaiAudibleKind.None;
        _lastCalled[team] = chosen.Name;

        KaiLog.Event(nameof(CallPlay),
            $"team {team} runs '{chosen.Name}': {chosen.Kind} on site {chosen.Site}. " +
            $"{why}. Record so far {chosen.Won}/{chosen.Called - 1}, " +
            $"{chosen.Abandoned} abandoned (kept for interest, not used to choose).");

        return chosen;
    }

    // Draw one play, refilling and reshuffling when the bag runs dry.
    //
    // Fisher-Yates on refill, which is the shuffle that actually produces a
    // uniform permutation rather than the sort-by-random-key that usually
    // stands in for it.
    private KaiPlay? DrawFromBag(int team, List<KaiPlay> options)
    {
        if (!_bag.TryGetValue(team, out var remaining) || remaining.Count == 0)
        {
            remaining = options.Select(p => p.Name).ToList();

            for (int i = remaining.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (remaining[i], remaining[j]) = (remaining[j], remaining[i]);
            }

            // Do not open a fresh bag with the play that closed the last one.
            // Without this the one back-to-back repeat a bag cannot otherwise
            // produce is exactly the one it produces most often.
            if (remaining.Count > 1
                && _lastCalled.TryGetValue(team, out string? previous)
                && remaining[0] == previous)
            {
                (remaining[0], remaining[1]) = (remaining[1], remaining[0]);
            }

            _bag[team] = remaining;

            KaiLog.Event(nameof(DrawFromBag),
                $"team {team} reshuffled its bag: {remaining.Count} play(s), every one of them " +
                $"will run before any runs again");
        }

        string name = remaining[0];
        remaining.RemoveAt(0);

        foreach (var play in options)
        {
            if (play.Name == name)
            {
                return play;
            }
        }

        // The book changed underneath the bag, most likely because a new
        // bombsite was discovered. Start again rather than return nothing.
        _bag.Remove(team);
        return options.Count > 0 ? options[_random.Next(options.Count)] : null;
    }

    public int BagRemaining(int team)
    {
        return _bag.TryGetValue(team, out var remaining) ? remaining.Count : 0;
    }

    // ------------------------------------------------------------------
    // Audibles
    // ------------------------------------------------------------------

    // Decide whether what is happening has diverged enough from the plan to
    // change it. Returns None when the play still fits.
    //
    // Ordered by how strongly each signal overrides the others: running out of
    // bodies beats running out of clock, which beats a read on where the enemy
    // is, because the first two are facts and the third is an inference.
    public KaiAudibleKind Consider(int team, KaiGameState state, out int newSite, out string why)
    {
        newSite = -1;
        why = "";

        var play = CurrentPlay(team);

        if (play == null)
        {
            return KaiAudibleKind.None;
        }

        // Checked before the settle timer and before the cooldown, unlike
        // everything else in this method.
        //
        // GuardBomb is only sound because a dropped bomb is certain: it does
        // not move, and the Ts have no choice but to come to it or kill
        // everybody. The moment it is picked up both halves of that are gone.
        // The bomb is mobile, its position is unknown again, and the side is
        // clustered on a ring around bare floor having given up map control to
        // get there.
        //
        // The timers exist to stop the side thrashing on weak reads. This is
        // not a read, it is the premise of the current play disappearing, and
        // making it wait twelve seconds would leave the whole defence guarding
        // an empty patch of floor while the bomb walks somewhere else.
        // Latched on the last audible, because bypassing the cooldown means
        // the condition would otherwise stay true and fire on every tick for
        // the rest of the round. The ring only needs tearing down once; if the
        // bomb hits the floor again the guard audible re-arms this by moving
        // the side back onto GuardBomb.
        if (team == (int)CsTeam.CounterTerrorist
            && play.Kind == KaiPlayKind.GuardBomb
            && !state.BombDropped
            && !state.BombPlanted
            && _lastAudibleKind.GetValueOrDefault(team) != KaiAudibleKind.BombRecovered)
        {
            why = "the bomb has been picked up, so guarding where it was is guarding nothing";
            Note(team, KaiAudibleKind.BombRecovered, state);
            return KaiAudibleKind.BombRecovered;
        }

        if (state.RoundElapsed < AudibleAfterSeconds)
        {
            return KaiAudibleKind.None;
        }

        float since = state.RoundElapsed - _lastAudible.GetValueOrDefault(team);

        if (since < AudibleCooldownSeconds)
        {
            return KaiAudibleKind.None;
        }

        // Down bodies. Nothing else matters: a four man site take with two
        // players is not a site take.
        int started = MathF.Max(state.FriendliesAlive + state.FriendlyDeaths, 1) is float f
            ? (int)f
            : 1;

        float remaining = (float)state.FriendliesAlive / started;

        if (remaining <= PullBackFraction
            && state.FriendliesAlive > 0
            && !state.BombPlanted
            && team == (int)CsTeam.Terrorist)
        {
            why = $"down to {state.FriendliesAlive} of {started}, the execute is not on";
            Note(team, KaiAudibleKind.PullBack, state);
            return KaiAudibleKind.PullBack;
        }

        // Clock. A default that never commits is a lost round.
        if (team == (int)CsTeam.Terrorist
            && play.Kind == KaiPlayKind.Default
            && !state.BombPlanted
            && state.RoundElapsed > 55.0f)
        {
            newSite = play.Site;
            why = $"{state.RoundElapsed:F0}s gone on a default, time to commit";
            Note(team, KaiAudibleKind.CommitNow, state);
            return KaiAudibleKind.CommitNow;
        }

        // The bomb is on the ground and we are not already sitting on it.
        //
        // Above the contact read deliberately, because this is not an
        // inference. Contacts tell you where they were; a loose bomb tells you
        // where they have to go.
        if (team == (int)CsTeam.CounterTerrorist
            && state.BombDropped
            && !state.BombPlanted
            && play.Kind != KaiPlayKind.GuardBomb)
        {
            why = "the bomb is on the ground, which is the one thing the Ts must come back for";
            Note(team, KaiAudibleKind.GuardBomb, state);
            return KaiAudibleKind.GuardBomb;
        }

        // Where the enemy actually is.
        if (state.ContactsBySite.Length > 0)
        {
            int stacked = -1;
            int stackedCount = 0;

            for (int i = 0; i < state.ContactsBySite.Length; i++)
            {
                if (state.ContactsBySite[i] > stackedCount)
                {
                    stackedCount = state.ContactsBySite[i];
                    stacked = i;
                }
            }

            if (stacked >= 0 && stackedCount >= StackedContacts)
            {
                if (team == (int)CsTeam.Terrorist && !state.BombPlanted && play.Site == stacked)
                {
                    // Going where they are. Go somewhere else.
                    int alternative = -1;

                    for (int i = 0; i < state.ContactsBySite.Length; i++)
                    {
                        if (i != stacked
                            && (alternative < 0
                                || state.ContactsBySite[i] < state.ContactsBySite[alternative]))
                        {
                            alternative = i;
                        }
                    }

                    if (alternative >= 0)
                    {
                        newSite = alternative;
                        why = $"site {stacked} is stacked with {stackedCount} contact(s), " +
                              $"switching to site {alternative}";
                        Note(team, KaiAudibleKind.SwitchSite, state);
                        return KaiAudibleKind.SwitchSite;
                    }
                }

                if (team == (int)CsTeam.CounterTerrorist && !state.BombPlanted)
                {
                    // They are somewhere other than where we are set up.
                    if (play.Site >= 0 && play.Site != stacked)
                    {
                        newSite = stacked;

                        bool fake = new Random().NextDouble() < FakeRotateChance;

                        why = $"{stackedCount} contact(s) at site {stacked} while we hold " +
                              $"site {play.Site}";

                        if (fake)
                        {
                            why += ", rotating as a fake to pull them back";
                            Note(team, KaiAudibleKind.FakeRotate, state);
                            return KaiAudibleKind.FakeRotate;
                        }

                        Note(team, KaiAudibleKind.RotateDefence, state);
                        return KaiAudibleKind.RotateDefence;
                    }
                }
            }
        }

        return KaiAudibleKind.None;
    }

    private void Note(int team, KaiAudibleKind kind, KaiGameState state)
    {
        // Abandoning a dead play does not consume the cooldown. The cooldown
        // exists to stop the side thrashing between reads, and tearing down a
        // ring around a bomb that has gone is not a read: the side should be
        // free to rotate on the very next contact rather than standing
        // undirected for twelve seconds because its previous plan expired.
        if (kind == KaiAudibleKind.BombRecovered)
        {
            _lastAudibleKind[team] = kind;

            var voided = CurrentPlay(team);

            if (voided != null)
            {
                voided.Abandoned++;
            }

            return;
        }

        _lastAudible[team] = state.RoundElapsed;
        _lastAudibleKind[team] = kind;

        var play = CurrentPlay(team);

        if (play != null && kind != KaiAudibleKind.None)
        {
            play.Abandoned++;
        }
    }

    // ------------------------------------------------------------------
    // Learning from the result
    // ------------------------------------------------------------------

    public void RecordOutcome(int winningTeam)
    {
        foreach (var kv in _current)
        {
            var play = kv.Value;

            if (kv.Key == winningTeam)
            {
                play.Won++;
            }

            KaiLog.Event(nameof(RecordOutcome),
                $"team {kv.Key} played '{play.Name}' and " +
                $"{(kv.Key == winningTeam ? "won" : "lost")}: now {play.Won}/{play.Called} " +
                $"({play.WinRate * 100.0f:F0}%), {play.Abandoned} abandoned");
        }

        _current.Clear();
        Save();
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

    private string Dir()
    {
        return Path.Combine(_dataDir, "playbook");
    }

    private string PathFor()
    {
        return Path.Combine(Dir(), $"{_mapName}.plays.json");
    }

    private void Load()
    {
        _book = new KaiPlayBook { MapName = _mapName };

        try
        {
            string path = PathFor();

            if (!File.Exists(path))
            {
                KaiLog.Event(nameof(Load),
                    $"no playbook for '{_mapName}' yet, starting with an empty record");
                return;
            }

            var loaded = JsonSerializer.Deserialize<KaiPlayBook>(File.ReadAllText(path), Options);

            if (loaded == null)
            {
                return;
            }

            loaded.MapName = _mapName;
            _book = loaded;

            var ranked = _book.Plays
                .Where(p => p.Called > 0)
                .OrderByDescending(p => p.WinRate)
                .Take(3)
                .Select(p => $"{p.Name} {p.Won}/{p.Called}");

            KaiLog.Event(nameof(Load),
                $"loaded {_book.Plays.Count} play(s) for '{_mapName}'. " +
                $"Best so far: {string.Join(", ", ranked)}");
        }
        catch (Exception ex)
        {
            KaiLog.Event(nameof(Load), $"failed: {ex.Message}", KaiLogLevel.Error);
        }
    }

    public void Save()
    {
        try
        {
            if (string.IsNullOrEmpty(_dataDir) || string.IsNullOrEmpty(_mapName))
            {
                return;
            }

            Directory.CreateDirectory(Dir());

            _book.MapName = _mapName;
            _book.LastWrittenUtc = KaiTime.NowUtc();

            File.WriteAllText(PathFor(), JsonSerializer.Serialize(_book, Options));
        }
        catch (Exception ex)
        {
            KaiLog.Event(nameof(Save), $"failed: {ex.Message}", KaiLogLevel.Error);
        }
    }

    public void ClearRecord()
    {
        foreach (var play in _book.Plays)
        {
            play.Called = 0;
            play.Won = 0;
            play.Abandoned = 0;
        }

        Save();

        KaiLog.Event(nameof(ClearRecord), $"cleared the win record for {_book.Plays.Count} play(s)");
    }
}
