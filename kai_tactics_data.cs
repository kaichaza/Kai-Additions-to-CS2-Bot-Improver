// kai_tactics_data.cs
//
// KAI ADDITION. Not part of CS2-Bot-Improver.
// Schema version 2. Not backward compatible with version 1 banks.
//
// WHAT CHANGED FROM V1
//
//   - Every sample carries a UTC timestamp and the round number it came from,
//     so a bank can be audited after the fact.
//   - Every sample carries an engagement id. One death now produces samples in
//     more than one category; they share an id so counting can be done per
//     engagement rather than per sample, which stops one duel inflating two
//     different spot priorities.
//   - Generated tactics files carry a generation timestamp, the generator
//     version, and the sample and engagement counts they were built from.
//   - Hold spots carry their sample count and bomb distance as real fields
//     rather than smuggling them through the name and the site string.
//   - Every write makes a .backup copy of the previous file first.
//   - Pre-aim triggers have a separate vertical half-height, so a trigger on
//     the upper floor of Nuke does not fire for a bot walking underneath it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KaiBotTactics;

// A plain serialisable point. CounterStrikeSharp's Vector is native-backed and
// cannot be deserialised directly, so JSON uses this.
public sealed class KaiPoint
{
    [JsonPropertyName("x")]
    public float X { get; set; }

    [JsonPropertyName("y")]
    public float Y { get; set; }

    [JsonPropertyName("z")]
    public float Z { get; set; }

    public KaiPoint()
    {
    }

    public KaiPoint(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    // Squared 3D distance. Squared because this runs per bot per tick and
    // there is no reason to pay for a square root to compare against a radius.
    public float DistanceSqr(float x, float y, float z)
    {
        float dx = X - x;
        float dy = Y - y;
        float dz = Z - z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    // Horizontal distance only. Used wherever height must be judged
    // separately, which on Nuke and Vertigo is everywhere that matters.
    public float DistanceXY(float x, float y)
    {
        float dx = X - x;
        float dy = Y - y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }
}

// Somewhere a bot can stand, and what it should point at from there.
public sealed class KaiHoldSpot
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "unnamed";

    // Free text. Generated spots leave this empty; hand-authored spots can put
    // a callout name here.
    [JsonPropertyName("site")]
    public string Site { get; set; } = "";

    // 2 Terrorist, 3 CounterTerrorist, 0 either.
    [JsonPropertyName("team")]
    public int Team { get; set; } = 0;

    [JsonPropertyName("anchor")]
    public KaiPoint Anchor { get; set; } = new();

    [JsonPropertyName("watch")]
    public KaiPoint Watch { get; set; } = new();

    [JsonPropertyName("crouch")]
    public bool Crouch { get; set; } = false;

    // CT only. Where the designated defuser waits out the clear phase, rather
    // than a clearing angle.
    [JsonPropertyName("stage")]
    public bool Stage { get; set; } = false;

    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 0;

    // How many samples produced this spot. Generated spots only.
    [JsonPropertyName("samples")]
    public int Samples { get; set; } = 0;

    // Mean distance from the planted bomb across those samples. Tells you at a
    // glance whether a hold is really near the site or was a long range pick
    // at the edge of the search radius.
    [JsonPropertyName("bombDist")]
    public float BombDist { get; set; } = -1.0f;

    [JsonPropertyName("recorded")]
    public string Recorded { get; set; } = "";
}

// A corner to pre-aim while walking past. Never moves the bot.
public sealed class KaiPreAimSpot
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "unnamed";

    [JsonPropertyName("team")]
    public int Team { get; set; } = 0;

    [JsonPropertyName("trigger")]
    public KaiPoint Trigger { get; set; } = new();

    [JsonPropertyName("triggerRadius")]
    public float TriggerRadius { get; set; } = 160.0f;

    // Vertical half-height of the trigger volume, checked separately from the
    // horizontal radius. Without this a trigger on Nuke upper fires for a bot
    // in lower directly beneath it.
    [JsonPropertyName("triggerHeight")]
    public float TriggerHeight { get; set; } = 70.0f;

    [JsonPropertyName("watch")]
    public KaiPoint Watch { get; set; } = new();

    // Only apply while the bot already roughly faces the corner. Without this
    // a bot retreating through the trigger whips its view round to a corner it
    // is walking away from. 180 disables the check.
    [JsonPropertyName("facingToleranceDeg")]
    public float FacingToleranceDeg { get; set; } = 100.0f;

    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 0;

    [JsonPropertyName("samples")]
    public int Samples { get; set; } = 0;

    [JsonPropertyName("recorded")]
    public string Recorded { get; set; } = "";
}

// A position solved offline as a good place to hold, rather than derived on
// the fly each round.
//
// The live solvers work from where a bot happens to be standing when the bomb
// lands, which means the answer depends on the accident of where it was at
// that moment. These are chosen from every standable position on the map
// against the full set of known duel angles, so they are the best available
// answer rather than the best answer reachable from wherever the bot started.
public sealed class KaiSolvedPost
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    // Which plant site this post covers, as an index into PlantSites. Minus
    // one for CT early-round posts, which are not tied to a site.
    [JsonPropertyName("site")]
    public int SiteIndex { get; set; } = -1;

    [JsonPropertyName("team")]
    public int Team { get; set; }

    [JsonPropertyName("position")]
    public KaiPoint Position { get; set; } = new();

    // Compass bearing from the site centre, so posts can be handed out in an
    // even fan without recomputing the geometry.
    [JsonPropertyName("bearing")]
    public float Bearing { get; set; }

    [JsonPropertyName("distance")]
    public float Distance { get; set; }

    // How many known duel angles are visible from here. The primary score.
    [JsonPropertyName("coverage")]
    public int Coverage { get; set; }

    // Indices into PreAim of exactly which ones, so the glance sweep does not
    // have to re-trace them at runtime.
    [JsonPropertyName("covers")]
    public List<int> Covers { get; set; } = new();

    // How far back the nearest wall is. Small is good: it means cover.
    [JsonPropertyName("backWall")]
    public float BackWall { get; set; } = -1.0f;

    [JsonPropertyName("score")]
    public float Score { get; set; }

    [JsonPropertyName("solvedUtc")]
    public string SolvedUtc { get; set; } = "";
}

// Everything known about one map.
public sealed class KaiMapTactics
{
    [JsonPropertyName("mapName")]
    public string MapName { get; set; } = "";

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 2;

    // When kai_learn build last ran, or when kai_save last wrote this.
    [JsonPropertyName("generatedUtc")]
    public string GeneratedUtc { get; set; } = "";

    [JsonPropertyName("generatorVersion")]
    public string GeneratorVersion { get; set; } = "";

    // What the build was made from, so the file can be judged without opening
    // the sample bank alongside it.
    [JsonPropertyName("sourceSamples")]
    public int SourceSamples { get; set; } = 0;

    [JsonPropertyName("sourceEngagements")]
    public int SourceEngagements { get; set; } = 0;

    [JsonPropertyName("postPlant")]
    public List<KaiHoldSpot> PostPlant { get; set; } = new();

    [JsonPropertyName("ctClear")]
    public List<KaiHoldSpot> CtClear { get; set; } = new();

    [JsonPropertyName("preAim")]
    public List<KaiPreAimSpot> PreAim { get; set; } = new();

    // Observed plant positions, merged into sites. Recorded rather than
    // configured, because a bombsite is only meaningfully "where the bomb
    // actually gets planted" and that differs from the map's own site volume.
    [JsonPropertyName("plantSites")]
    public List<KaiPoint> PlantSites { get; set; } = new();

    // The game's own letter for each recorded site, read from
    // CPlantedC4::m_nBombSite at the moment of the plant.
    //
    // Sites are numbered here in the order they were first planted on, which
    // has nothing to do with what they are called. Assuming index zero meant
    // A was wrong the moment a map's first plant of a session happened to land
    // on B, and produced the squad telling the human to take the bomb to B
    // while the whole side executed A.
    //
    // Parallel to PlantSites by index. Empty entries mean the letter was not
    // readable and the site is described positionally instead.
    [JsonPropertyName("plantSiteNames")]
    public List<string> PlantSiteNames { get; set; } = new();

    // Pre-solved holding positions. Empty until kai_solve has run.
    [JsonPropertyName("solvedTPosts")]
    public List<KaiSolvedPost> SolvedTPosts { get; set; } = new();

    [JsonPropertyName("solvedCtPosts")]
    public List<KaiSolvedPost> SolvedCtPosts { get; set; } = new();

    [JsonPropertyName("solvedUtc")]
    public string SolvedUtc { get; set; } = "";
}

// What a single bot has been told to do right now.
public sealed class KaiBotIntent
{
    // World point the crosshair should sit on. Null leaves native aim alone.
    public KaiPoint? Watch;

    // Pin movement. Only set once the bot has actually reached its anchor.
    public bool Anchored;

    public bool Crouch;

    // Clear the USE bit, so a CT cannot touch the bomb.
    public bool SuppressUse;

    // Apply the watch target even when the bot's own AI has a look-at of its
    // own running. Used only for defusing, where the bot must physically point
    // at the bomb for the game to let it start, and where deferring to the AI
    // means it stands on the bomb looking at a wall until the round is lost.
    // Never bypasses actual enemy contact.
    public bool ForceAim;

    // Move unpredictably: strafe across the line of travel and jump. Used
    // only for a knife rush, where being hard to hit is worth more than
    // arriving quickly or quietly.
    public bool Erratic;

    // Move at walking pace rather than running. Walking is silent in CS2, so
    // this is what lets a bot reposition without drowning out the footsteps it
    // is trying to hear.
    public bool Walk;

    // Steer the bot in this world direction instead of pinning it. Null means
    // no steering. Set only when the plugin wants a bot to shuffle a short
    // distance under its own power, which is the closest thing available to
    // movement control without a nav mesh.
    public KaiPoint? SteerTowards;

    public string SourceName = "";

    // Server time this intent was last refreshed. The native hooks ignore
    // anything older than a tick's slack, so a stale intent can never keep
    // driving a bot after the tick listener stops renewing it.
    public float Stamp;

    public void Reset(float now)
    {
        Watch = null;
        Anchored = false;
        Crouch = false;
        SuppressUse = false;
        ForceAim = false;
        Walk = false;
        Erratic = false;
        SteerTowards = null;
        SourceName = "";
        Stamp = now;
    }
}

// Vertical reference points, shared so that every file agrees on what a
// stored coordinate means.
//
// The distinction matters more than it looks. Every position the plugin
// records is CBaseEntity::AbsOrigin, which is at the FEET. A crosshair aimed
// at a feet-level point stares at the floor, so anything used as a watch
// target has the chest offset added at the moment it is used.
//
// The rule, applied everywhere: points are stored at FEET level, and the
// offset is added once, at the point of use. Anything that arrives already
// raised, such as the watch field of a learned spot, is lowered back to feet
// before being pooled with feet-level points.
public static class KaiHeights
{
    // Roughly chest height on a standing player. Aiming here rather than at
    // the head gives a hit on a crouching target as well, and is where the
    // native aim system puts its default body shot.
    public const float Chest = 52.0f;

    // Height to aim at for a bomb on the ground: the chest of whoever is
    // stood over it, which is slightly lower than Chest because the bomb model
    // sits a little above the floor itself.
    public const float BombWatch = 45.0f;

    // Head height on a standing player.
    //
    // Used when deliberately clearing a position rather than watching a lane.
    // The two are different jobs. Watching a lane wants the chest, because it
    // is the biggest target and works against a crouching player too.
    // Clearing a specific spot where somebody might be standing right now
    // wants the head, because the crosshair is already on the position and the
    // only question is whether the first shot kills.
    public const float Head = 64.0f;
}

public static class KaiTime
{
    // One timestamp format everywhere: sortable, unambiguous, no locale.
    public static string NowUtc()
    {
        return DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + "Z";
    }

    public static long NowUnix()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }
}

public static class KaiTacticsLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // Copy a file to <path>.backup before it is overwritten.
    //
    // One rolling backup rather than a timestamped series, deliberately: the
    // tactics file is always regenerable from the sample bank, and the sample
    // bank only ever grows, so a single generation of history is enough to
    // recover from a bad build without filling the folder with clutter.
    //
    // The zero-length guard matters. Without it, a failed write that leaves an
    // empty file would, on the next build, copy that empty file over a good
    // backup and destroy both copies.
    public static void Backup(string path, string caller)
    {
        try
        {
            if (!File.Exists(path))
            {
                KaiLog.Event(
                    nameof(Backup),
                    $"[{caller}] nothing to back up at '{path}'",
                    KaiLogLevel.Verbose);
                return;
            }

            var info = new FileInfo(path);

            if (info.Length == 0)
            {
                KaiLog.Event(
                    nameof(Backup),
                    $"[{caller}] '{path}' is zero bytes, refusing to overwrite the existing backup",
                    KaiLogLevel.Error);
                return;
            }

            string backupPath = path + ".backup";
            File.Copy(path, backupPath, true);

            KaiLog.Event(
                nameof(Backup),
                $"[{caller}] backed up {info.Length} bytes to '{backupPath}'");
        }
        catch (Exception ex)
        {
            KaiLog.Event(
                nameof(Backup),
                $"[{caller}] backup of '{path}' failed: {ex.Message}",
                KaiLogLevel.Error);
        }
    }

    // Load the tactics file for one map. Returns an empty record rather than
    // null when missing, so an unauthored map behaves exactly like stock.
    public static KaiMapTactics Load(string dataDir, string mapName)
    {
        var empty = new KaiMapTactics { MapName = mapName };

        try
        {
            string path = Path.Combine(dataDir, $"{mapName}.json");

            if (!File.Exists(path))
            {
                KaiLog.Event(
                    nameof(Load),
                    $"no tactics file for '{mapName}' at '{path}', running stock behaviour");
                return empty;
            }

            var loaded = JsonSerializer.Deserialize<KaiMapTactics>(File.ReadAllText(path), Options);

            if (loaded == null)
            {
                KaiLog.Event(nameof(Load), $"'{path}' deserialised to null", KaiLogLevel.Error);
                return empty;
            }

            loaded.MapName = mapName;

            if (loaded.SchemaVersion < 2)
            {
                KaiLog.Event(
                    nameof(Load),
                    $"'{path}' is schema v{loaded.SchemaVersion}, this build expects v2. " +
                    $"Fields added in v2 read as defaults. Rebuild with kai_learn build.",
                    KaiLogLevel.Error);
            }

            KaiLog.Event(
                nameof(Load),
                $"loaded '{path}' generated {loaded.GeneratedUtc} by v{loaded.GeneratorVersion} " +
                $"from {loaded.SourceSamples} samples / {loaded.SourceEngagements} engagements: " +
                $"{loaded.PostPlant.Count} T post-plant, {loaded.CtClear.Count} CT clear, " +
                $"{loaded.PreAim.Count} pre-aim");

            return loaded;
        }
        catch (Exception ex)
        {
            KaiLog.Event(nameof(Load), $"failed to load '{mapName}': {ex.Message}", KaiLogLevel.Error);
            return empty;
        }
    }

    // Write a map's tactics, backing up the previous file first.
    //
    // Refuses to replace a file that has spots in it with one that has none.
    // The tactics file is the output of hours of play, and every path that
    // writes it is guarded against running on empty data, but a guard that
    // depends on every caller getting it right is not a guard. This one sits
    // at the point of writing, where it cannot be bypassed by accident.
    public static bool Save(string dataDir, KaiMapTactics data, string caller)
    {
        try
        {
            Directory.CreateDirectory(dataDir);

            string path = Path.Combine(dataDir, $"{data.MapName}.json");

            int spots = data.PostPlant.Count + data.CtClear.Count + data.PreAim.Count;

            if (spots == 0 && File.Exists(path))
            {
                var existing = Load(dataDir, data.MapName);
                int existingSpots =
                    existing.PostPlant.Count + existing.CtClear.Count + existing.PreAim.Count;

                if (existingSpots > 0)
                {
                    KaiLog.Event(
                        nameof(Save),
                        $"[{caller}] REFUSED to overwrite '{path}': the file on disk holds " +
                        $"{existingSpots} spot(s) and this write holds none. Something asked to " +
                        $"save an empty map over a populated one, which would throw away every " +
                        $"generated position. Nothing was written.",
                        KaiLogLevel.Error);

                    return false;
                }
            }

            Backup(path, caller);

            File.WriteAllText(path, JsonSerializer.Serialize(data, Options));

            KaiLog.Event(
                nameof(Save),
                $"[{caller}] wrote '{path}' stamped {data.GeneratedUtc}: " +
                $"{data.PostPlant.Count} T post-plant, {data.CtClear.Count} CT clear, " +
                $"{data.PreAim.Count} pre-aim");

            return true;
        }
        catch (Exception ex)
        {
            KaiLog.Event(nameof(Save), $"[{caller}] save failed: {ex.Message}", KaiLogLevel.Error);
            return false;
        }
    }
}
