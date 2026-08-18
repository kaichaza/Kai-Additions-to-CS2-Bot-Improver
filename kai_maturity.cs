// kai_maturity.cs
//
// KAI ADDITION. Not part of CS2-Bot-Improver.
//
// Decides when a map is finished being learned.
//
// WHY THIS EXISTS
//
// Every learning system in this plugin grows a file: the sample bank, the
// breadcrumb graph, the playbook. Left alone they grow forever, and long
// before that they stop learning anything, because a map has a finite number
// of angles and a finite number of ways to walk between them. Past that point
// every extra sample is disk space bought for nothing.
//
// WHY NOT COUNT MATCHES
//
// An earlier version counted completed matches, which was wrong twice over. A
// match abandoned after three rounds counts for nothing despite having taught
// something, and ten matches of one-sided blowouts teach far less than ten
// close ones. Worse, a match count says nothing about whether anything was
// actually learned: it measures how long you played, not what came of it.
//
// So readiness is measured against the evidence itself. The map stops being
// recorded when the recorders have stopped finding anything new, and the plays
// stop being learned when every play has been tried enough times for its
// record to mean something. Rounds are counted too, but only as a floor to
// stop a quiet start being mistaken for a finished map.
//
// WHAT THE THRESHOLDS MEAN
//
// The post-plant sample counts are the bottleneck: pre-plant samples come from
// every death, while a post-plant one needs a round to reach a plant and then
// produce a kill near the bomb. Measuring readiness against the scarce
// category rather than the total is the difference between a map that is
// learned and one that merely has a large file.
//
// For plays, what matters is the LEAST tried play, not the total. A book where
// one play has been called sixty times and another twice is not a book that
// has been learned; it is one strong opinion and a guess.

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KaiBotTactics;

public enum KaiMapStage
{
    // Still discovering the map itself.
    Mapping = 0,

    // Map known, learning which plays win on it.
    Learning = 1,

    // Finished. Nothing further is recorded.
    Mature = 2,
}

// A snapshot of how much has actually been learned, assembled by the plugin
// from the recorders themselves rather than from a clock.
public sealed class KaiLearningEvidence
{
    public int Rounds;

    // Distinct deaths in the bank. Used once, to give a map that was played
    // before this counter existed a fair starting position.
    public int Engagements;

    public int PostPlantSamples;
    public int ClearSamples;
    public int PreAimSamples;

    public bool GraphSaturated;
    public int GraphNodes;
    public int NewNodesThisSession;

    // The least-called play in the book, and how many plays there are.
    public int MinPlayCalls;
    public int PlayCount;
    public int TotalPlayCalls;
}

public sealed class KaiMaturityRecord
{
    [JsonPropertyName("mapName")]
    public string MapName { get; set; } = "";

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 2;

    // Rounds, not matches. A round is the unit that actually produces
    // evidence, and it counts whether or not the match it belonged to was
    // played out.
    [JsonPropertyName("rounds")]
    public int Rounds { get; set; }

    // Kept for interest only. Nothing depends on it.
    [JsonPropertyName("matches")]
    public int Matches { get; set; }

    [JsonPropertyName("stage")]
    public KaiMapStage Stage { get; set; } = KaiMapStage.Mapping;

    [JsonPropertyName("firstSeenUtc")]
    public string FirstSeenUtc { get; set; } = "";

    // Whether the round count was seeded from a sample bank that predated
    // this counter. Recorded so a seeded figure is never mistaken for one
    // that was actually counted, and so the seeding only ever happens once.
    [JsonPropertyName("seededFromHistory")]
    public bool SeededFromHistory { get; set; }

    [JsonPropertyName("seededBecause")]
    public string SeededBecause { get; set; } = "";

    // When each stage was reached, and on what evidence, so a decision made
    // weeks ago can still be understood.
    [JsonPropertyName("mappedUtc")]
    public string MappedUtc { get; set; } = "";

    [JsonPropertyName("mappedBecause")]
    public string MappedBecause { get; set; } = "";

    [JsonPropertyName("maturedUtc")]
    public string MaturedUtc { get; set; } = "";

    [JsonPropertyName("maturedBecause")]
    public string MaturedBecause { get; set; } = "";
}

public sealed class KaiMapMaturity
{
    // ------------------------------------------------------------------
    // Thresholds
    //
    // Set against measured data rather than guessed. A de_mirage bank sitting
    // at roughly two thousand samples held 121 post-plant and 168 clear
    // samples, and its breadcrumb graph had stopped finding new ground well
    // before that. The numbers below sit a little above those, so a map is
    // called finished once it has comfortably passed the point where a real
    // one stopped teaching anything.
    // ------------------------------------------------------------------

    // Post-plant evidence needed before the map counts as learned. The scarce
    // category, and the one every post-plant behaviour depends on.
    public int RequiredPostPlantSamples = 150;

    public int RequiredClearSamples = 150;

    // A floor on rounds, so a lucky run of bomb-heavy rounds cannot declare a
    // map finished after twenty minutes.
    public int MinRoundsToMap = 60;

    // A ceiling, past which the map is called mapped whatever the sample
    // counts say.
    //
    // The sample thresholds were calibrated on de_mirage. On a map where the
    // bots reach a plant less often they are simply too strict: de_inferno sat
    // at 116 post-plant samples after 180 rounds with every other condition
    // met for over a hundred of them, which is not a map that needs more
    // mapping, it is a threshold that does not fit it.
    //
    // Past this point the answer is that you have what you are going to get.
    // Proceeding on slightly thin data beats never proceeding at all, and it
    // is logged plainly so the reason is on record.
    public int MaxRoundsToMap = 150;

    // The graph must also say it has stopped growing, either through its own
    // saturation test or by adding nothing this session.
    public int MaxNewNodesToCallItDone = 30;

    // Every play must have been tried this many times before the win record
    // is taken as final. With around eleven plays and two called per round,
    // eight apiece works out at roughly ninety rounds, which is the "eighty to
    // a hundred" this was meant to land on.
    public int RequiredCallsPerPlay = 8;

    // ------------------------------------------------------------------
    // State
    // ------------------------------------------------------------------

    private KaiMaturityRecord _record = new();
    private string _dataDir = "";
    private string _mapName = "";
    private bool _announcedThisSession;
    private string _lastWaitingReason = "";

    // Set for exactly one round when the map finishes MAPPING, so the caller
    // can do the one thing that has to happen at that moment and nothing
    // else: rebuild the tactics file from the now-final sample bank.
    public bool JustFinishedMapping { get; private set; }

    public int Rounds => _record.Rounds;
    public int Matches => _record.Matches;
    public KaiMapStage Stage => _record.Stage;

    // Should the map recorders still be taking new data?
    public bool RecordingMapData => _record.Stage == KaiMapStage.Mapping;

    // Should the bots be doing anything at all beyond being watched?
    //
    // No, during MAPPING. That phase is deliberately hands-off: the plugin
    // records and nothing else, so the bots run on stock CS2 plus ed0ard's
    // stack exactly as they would without this installed.
    //
    // Two reasons, and the second is the important one. There is nothing to
    // act on yet, since holds, pre-aim and routes all derive from data that
    // does not exist. And every sample taken during this phase then describes
    // unaltered behaviour, which is the clean baseline the whole map model is
    // built from. Let the plugin steer bots while it is still learning where
    // people die, and it starts learning where it put them.
    public bool BehavioursActive => _record.Stage != KaiMapStage.Mapping;

    // Should the playbook still be updating its win record?
    //
    // Only during LEARNING. No plays are called during mapping, so there is
    // nothing to record, and none are recorded once mature.
    public bool LearningPlays => _record.Stage == KaiMapStage.Learning;

    public string Describe()
    {
        return _record.Stage switch
        {
            KaiMapStage.Mapping =>
                $"MAPPING after {_record.Rounds} round(s): still finding kill spots and walkable " +
                $"ground",

            KaiMapStage.Learning =>
                $"LEARNING after {_record.Rounds} round(s): the map is known, working out which " +
                $"plays win on it",

            _ => $"MATURE after {_record.Rounds} round(s): nothing further is being recorded",
        };
    }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    public void OnMapStart(string dataDir, string mapName)
    {
        _dataDir = dataDir;
        _mapName = mapName;
        _announcedThisSession = false;
        _lastWaitingReason = "";

        Load();

        if (string.IsNullOrEmpty(_record.FirstSeenUtc))
        {
            _record.FirstSeenUtc = KaiTime.NowUtc();
            Save();
        }

        KaiLog.Event(nameof(OnMapStart), $"'{mapName}' is {Describe()}");
    }

    // Counted per round, because a round is what produces evidence and it
    // counts whether or not the match around it was finished.
    public void OnRoundEnd(KaiLearningEvidence evidence)
    {
        if (_record.Stage == KaiMapStage.Mature)
        {
            AnnounceIfMature();
            return;
        }

        SeedFromHistory(evidence);

        _record.Rounds++;
        evidence.Rounds = _record.Rounds;

        JustFinishedMapping = false;

        var before = _record.Stage;
        Evaluate(evidence);

        if (before == KaiMapStage.Mapping && _record.Stage == KaiMapStage.Learning)
        {
            JustFinishedMapping = true;
        }

        // Written every round rather than every match, so stopping mid-match
        // loses nothing.
        Save();
    }

    public void OnMatchEnd()
    {
        _record.Matches++;
        Save();
    }

    // Give a map that was played before this counter existed a fair start.
    //
    // The round count was added long after some maps had been played for days,
    // so on those it begins at zero against a sample bank holding thousands of
    // engagements. The floor then blocks a map that is demonstrably finished,
    // which is the floor doing the opposite of its job: it exists to stop a
    // short run being mistaken for a complete one, not to discard history.
    //
    // The estimate divides banked deaths by a typical number per round. It is
    // deliberately conservative, and it only ever runs once, on a bank that
    // already existed before the first round was counted. A genuinely new map
    // has no bank, so nothing is seeded and the floor applies in full.
    private void SeedFromHistory(KaiLearningEvidence evidence)
    {
        if (_record.SeededFromHistory || _record.Rounds > 0)
        {
            return;
        }

        _record.SeededFromHistory = true;

        // Roughly how many deaths a round of ten players produces. Erring
        // high means erring towards under-counting the history, which is the
        // safe direction.
        const int deathsPerRound = 9;

        if (evidence.Engagements < deathsPerRound * 2)
        {
            _record.SeededBecause = "no meaningful history to seed from";
            return;
        }

        int estimated = evidence.Engagements / deathsPerRound;

        _record.Rounds = estimated;
        _record.SeededBecause =
            $"{evidence.Engagements} banked engagements predate this counter, " +
            $"estimated at {estimated} rounds";

        KaiLog.Event(nameof(SeedFromHistory),
            $"'{_mapName}' was played before round counting existed. {_record.SeededBecause}. " +
            $"Counting continues from there rather than from zero, so history already recorded " +
            $"is not thrown away.");
    }

    // Decide whether the evidence is now sufficient to move on.
    private void Evaluate(KaiLearningEvidence e)
    {
        if (_record.Stage == KaiMapStage.Mapping)
        {
            bool graphDone = e.GraphSaturated || e.NewNodesThisSession <= MaxNewNodesToCallItDone;
            bool samplesDone = e.PostPlantSamples >= RequiredPostPlantSamples
                               && e.ClearSamples >= RequiredClearSamples;
            bool roundsDone = e.Rounds >= MinRoundsToMap;

            // Out of patience. Everything except the sample count has been
            // satisfied for a long time and the count is not moving fast
            // enough to matter.
            bool ceilingHit = e.Rounds >= MaxRoundsToMap && graphDone;

            if (ceilingHit && !samplesDone)
            {
                _record.Stage = KaiMapStage.Learning;
                _record.MappedUtc = KaiTime.NowUtc();
                _record.MappedBecause =
                    $"{e.Rounds} rounds reached the ceiling of {MaxRoundsToMap} with " +
                    $"{e.PostPlantSamples}/{RequiredPostPlantSamples} post-plant and " +
                    $"{e.ClearSamples}/{RequiredClearSamples} clear samples";

                KaiLog.Event(nameof(Evaluate),
                    $"'{_mapName}' has finished MAPPING on the round ceiling rather than on " +
                    $"sample count: {_record.MappedBecause}. This map yields post-plant samples " +
                    $"more slowly than the thresholds assume, and {e.Rounds} rounds is enough. " +
                    $"The spots built from it are thinner than ideal but usable.",
                    KaiLogLevel.Error);

                return;
            }

            if (graphDone && samplesDone && roundsDone)
            {
                _record.Stage = KaiMapStage.Learning;
                _record.MappedUtc = KaiTime.NowUtc();
                _record.MappedBecause =
                    $"{e.Rounds} rounds, {e.PostPlantSamples} post-plant and {e.ClearSamples} " +
                    $"clear samples, graph at {e.GraphNodes} nodes " +
                    $"({(e.GraphSaturated ? "saturated" : $"only {e.NewNodesThisSession} new this session")})";

                KaiLog.Event(nameof(Evaluate),
                    $"'{_mapName}' has finished MAPPING: {_record.MappedBecause}. The kill spots " +
                    $"and the navigation graph are complete and will stop growing. Play learning " +
                    $"continues until every play has been tried {RequiredCallsPerPlay} times.");

                return;
            }

            ReportWaiting(
                $"mapping: post-plant {e.PostPlantSamples}/{RequiredPostPlantSamples}, " +
                $"clear {e.ClearSamples}/{RequiredClearSamples}, " +
                $"rounds {e.Rounds}/{MinRoundsToMap} (ceiling {MaxRoundsToMap}), " +
                $"graph {(graphDone ? "settled" : $"still growing ({e.NewNodesThisSession} new)")}");

            return;
        }

        if (_record.Stage == KaiMapStage.Learning)
        {
            // The least-tried play is the measure. A book with one play called
            // sixty times and another twice has not been learned.
            if (e.PlayCount > 0 && e.MinPlayCalls >= RequiredCallsPerPlay)
            {
                _record.Stage = KaiMapStage.Mature;
                _record.MaturedUtc = KaiTime.NowUtc();
                _record.MaturedBecause =
                    $"{e.Rounds} rounds, all {e.PlayCount} plays tried at least " +
                    $"{e.MinPlayCalls} times ({e.TotalPlayCalls} calls in total)";

                KaiLog.Event(nameof(Evaluate),
                    $"'{_mapName}' is MATURE: {_record.MaturedBecause}. Nothing further is being " +
                    $"recorded. The sample bank, the navigation graph and the playbook are all " +
                    $"final and will not grow again. The bots keep using what they learned. " +
                    $"Run kai_maturity reset if the map or the config changes enough to want a " +
                    $"fresh start.");

                return;
            }

            ReportWaiting(
                $"learning: least-tried play has {e.MinPlayCalls}/{RequiredCallsPerPlay} calls " +
                $"across {e.PlayCount} plays ({e.TotalPlayCalls} total), {e.Rounds} rounds played");
        }
    }

    // Only speak when the picture changes, so a long map does not produce the
    // same line every round.
    private void ReportWaiting(string reason)
    {
        if (reason == _lastWaitingReason)
        {
            return;
        }

        _lastWaitingReason = reason;
        KaiLog.Event(nameof(ReportWaiting), $"'{_mapName}' {reason}");
    }

    public void AnnounceIfMature()
    {
        if (_record.Stage != KaiMapStage.Mature || _announcedThisSession)
        {
            return;
        }

        _announcedThisSession = true;

        KaiLog.Event(nameof(AnnounceIfMature),
            $"'{_mapName}' matured on {_record.MaturedUtc} after {_record.Rounds} rounds " +
            $"({_record.MaturedBecause}). Recording is off and the files are static.");
    }

    public void Reset()
    {
        string map = _mapName;

        _record = new KaiMaturityRecord
        {
            MapName = map,
            FirstSeenUtc = KaiTime.NowUtc(),
        };

        _announcedThisSession = false;
        _lastWaitingReason = "";
        Save();

        KaiLog.Event(nameof(Reset),
            $"'{map}' maturity reset: back to MAPPING, all recorders live again");
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

    private string PathFor()
    {
        return Path.Combine(_dataDir, "maturity", $"{_mapName}.maturity.json");
    }

    private void Load()
    {
        _record = new KaiMaturityRecord { MapName = _mapName };

        try
        {
            string path = PathFor();

            if (!File.Exists(path))
            {
                KaiLog.Event(nameof(Load), $"'{_mapName}' has never been played, starting fresh");
                return;
            }

            var loaded = JsonSerializer.Deserialize<KaiMaturityRecord>(
                File.ReadAllText(path), Options);

            if (loaded != null)
            {
                loaded.MapName = _mapName;
                _record = loaded;
            }
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

            Directory.CreateDirectory(Path.Combine(_dataDir, "maturity"));
            _record.MapName = _mapName;

            File.WriteAllText(PathFor(), JsonSerializer.Serialize(_record, Options));
        }
        catch (Exception ex)
        {
            KaiLog.Event(nameof(Save), $"failed: {ex.Message}", KaiLogLevel.Error);
        }
    }
}
