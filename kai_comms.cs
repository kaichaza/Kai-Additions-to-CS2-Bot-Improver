// kai_comms.cs
//
// KAI ADDITION. Not part of CS2-Bot-Improver.
//
// The squad talks in team chat.
//
// WHY
//
// Everything this plugin decides has so far been visible only in a log file
// read afterwards. That is fine for finding bugs and useless while playing:
// from inside the game a coordinated execute and five bots wandering look
// identical until somebody dies. A line in team chat when the play is called,
// when a corner is cleared, when the side rotates, turns the whole thing from
// something you audit later into something you can follow and act on.
//
// THE SQUAD
//
// Bot names are assigned by the game and change between rounds, which makes
// them useless as identities: "Bot Zane" means nothing on round two. So four
// fixed names are handed out instead, sticky by slot, and they keep them for
// as long as they live.
//
// The prefix follows whichever side the human is on, because these are the
// human's team mates and nobody else. Counter-Terrorist makes them Operators;
// Terrorist makes them Comrades.
//
// TEAM ONLY, ALWAYS
//
// Every message goes to one team. Not etiquette: a call of "taking B through
// apartments" broadcast to the server hands the defence the round. Messages
// reach the human's own team, and spectators, who are watching rather than
// playing.
//
// RESTRAINT
//
// Every line competes with the game for the same few rows of screen. So this
// is throttled per subject, the calls worth reading are sent and the rest are
// not. A radio that never stops talking is one nobody listens to.

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

public enum KaiCommsLevel
{
    // Nothing.
    Off = 0,

    // Play calls, audibles, the defuse. The things that decide a round.
    Calls = 1,

    // The above plus sweeps, clears, cover and significant waypoints.
    Detail = 2,
}

// Fixed identities for the human's team mates.
public static class KaiSquad
{
    // Four names, in the order they are handed out. Deliberately short so a
    // callout reads quickly at a glance mid-round.
    private static readonly string[] Names = { "Wei", "Bullseye", "Tank", "Private" };

    // slot -> which of the names above that bot holds.
    private static readonly Dictionary<int, string> _assigned = new();

    private static readonly Random _random = new();

    // The side the human is on, which is the side the squad belongs to.
    public static int SquadTeam { get; private set; } = -1;

    public static string HumanName { get; private set; } = "";

    public static int HumanSlot { get; private set; } = -1;

    // Rank prefix, following the human's side.
    private static string Prefix
    {
        get
        {
            if (SquadTeam == (int)CsTeam.CounterTerrorist)
            {
                return "Op";
            }

            if (SquadTeam == (int)CsTeam.Terrorist)
            {
                return "Cde";
            }

            return "";
        }
    }

    // Work out who is on the squad. Called at round start.
    //
    // Assignments are kept across rounds for bots that are still on the same
    // side, so a name means the same bot for as long as it is around. Only
    // vacated names are handed out again.
    public static void Refresh()
    {
        HumanSlot = -1;
        HumanName = "";
        SquadTeam = -1;

        var teamMates = new List<CCSPlayerController>();

        foreach (var p in KaiPlayers.All())
        {
            if (p == null || !p.IsValid || p.IsHLTV)
            {
                continue;
            }

            if (!p.IsBot)
            {
                int team = (int)p.TeamNum;

                if (team == (int)CsTeam.Terrorist || team == (int)CsTeam.CounterTerrorist)
                {
                    HumanSlot = p.Slot;
                    HumanName = p.PlayerName;
                    SquadTeam = team;
                }
            }
        }

        if (SquadTeam < 0)
        {
            // Nobody playing, so nobody to talk to. Common on an unattended
            // mapping run.
            _assigned.Clear();
            return;
        }

        foreach (var p in KaiPlayers.All())
        {
            if (p == null || !p.IsValid || !p.IsBot || p.IsHLTV)
            {
                continue;
            }

            if ((int)p.TeamNum == SquadTeam)
            {
                teamMates.Add(p);
            }
        }

        // Drop anybody no longer on the squad's side, freeing their name.
        var stale = _assigned.Keys
            .Where(slot => !teamMates.Any(t => t.Slot == slot))
            .ToList();

        foreach (int slot in stale)
        {
            _assigned.Remove(slot);
        }

        // Hand out whatever names are free, lowest slot first so the
        // assignment is stable rather than dependent on enumeration order.
        var free = Names.Where(n => !_assigned.ContainsValue(n)).ToList();
        int next = 0;

        foreach (var bot in teamMates.OrderBy(t => t.Slot))
        {
            if (_assigned.ContainsKey(bot.Slot))
            {
                continue;
            }

            if (next >= free.Count)
            {
                // More team mates than names. The extras simply do not speak,
                // which is better than two bots sharing an identity.
                break;
            }

            _assigned[bot.Slot] = free[next];
            next++;
        }

        KaiLog.Event(nameof(Refresh),
            $"squad for '{HumanName}' on team {SquadTeam}: " +
            string.Join(", ", _assigned.Select(kv => $"{NameOf(kv.Key)} (slot {kv.Key})")));
    }

    // The full callsign for a slot, or empty if that bot is not on the squad.
    public static string NameOf(int slot)
    {
        if (!_assigned.TryGetValue(slot, out string? name))
        {
            return "";
        }

        return $"{Prefix} {name}";
    }

    public static bool IsSquad(int slot)
    {
        return _assigned.ContainsKey(slot);
    }

    // A living squad member to put a call in the mouth of.
    //
    // Prefers the bot that actually did the thing, when it is on the squad.
    // Otherwise any living member, so calls still get made when the bot
    // concerned is unnamed or dead.
    public static int SpeakerFor(int preferredSlot)
    {
        if (preferredSlot >= 0 && _assigned.ContainsKey(preferredSlot))
        {
            var p = Utilities.GetPlayerFromSlot(preferredSlot);

            if (p != null && p.IsValid && p.PawnIsAlive)
            {
                return preferredSlot;
            }
        }

        var alive = _assigned.Keys
            .Where(slot =>
            {
                var p = Utilities.GetPlayerFromSlot(slot);
                return p != null && p.IsValid && p.PawnIsAlive;
            })
            .ToList();

        if (alive.Count == 0)
        {
            return -1;
        }

        return alive[_random.Next(alive.Count)];
    }
}

// Real place names, so a callout says something a person can act on.
//
// WHY A TABLE
//
// This started out describing positions by compass bearing relative to the
// bomb, which produced "north-east mid" and similar. That is not a callout. It
// is a coordinate read aloud, and it tells a listener nothing they can act on
// because nobody thinks about a map in compass terms while playing it.
//
// There is no way to derive "Triple" or "Palace" from geometry. The names are
// a human convention layered on top of the map and have to be supplied. So
// each map gets a table of named anchor points, a position is described by
// whichever anchor it is nearest, and anything too far from every anchor is
// described as unmapped rather than guessed at.
//
// The table ships for de_mirage and is written to disk on first use, so it can
// be corrected and extended by hand without touching code. Any map without one
// falls back to plain distance from the bomb, which is at least honest.

public sealed class KaiCalloutAnchor
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("x")]
    public float X { get; set; }

    [JsonPropertyName("y")]
    public float Y { get; set; }

    // Optional. Set on maps where two callouts sit above each other, so that
    // an anchor only matches within a height band.
    [JsonPropertyName("z")]
    public float Z { get; set; }

    [JsonPropertyName("hasHeight")]
    public bool HasHeight { get; set; }
}

public sealed class KaiCalloutTable
{
    [JsonPropertyName("mapName")]
    public string MapName { get; set; } = "";

    [JsonPropertyName("maxDistance")]
    public float MaxDistance { get; set; } = 750.0f;

    [JsonPropertyName("anchors")]
    public List<KaiCalloutAnchor> Anchors { get; set; } = new();
}

public static class KaiCallouts
{
    private static KaiCalloutTable _table = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static int AnchorCount => _table.Anchors.Count;

    public static void OnMapStart(string dataDir, string mapName)
    {
        _table = new KaiCalloutTable { MapName = mapName };

        try
        {
            string dir = Path.Combine(dataDir, "callouts");
            string path = Path.Combine(dir, $"{mapName}.callouts.json");

            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<KaiCalloutTable>(
                    File.ReadAllText(path), Options);

                if (loaded != null && loaded.Anchors.Count > 0)
                {
                    loaded.MapName = mapName;
                    _table = loaded;

                    KaiLog.Event(nameof(OnMapStart),
                        $"loaded {_table.Anchors.Count} callout(s) for '{mapName}'");
                    return;
                }
            }

            // No table on disk. Ship one if this is a map we know, and write it
            // out so it can be corrected by hand.
            var builtIn = BuiltIn(mapName);

            if (builtIn.Count == 0)
            {
                KaiLog.Event(nameof(OnMapStart),
                    $"no callout table for '{mapName}'. Positions will be described by " +
                    $"distance from the bomb until one is written to " +
                    $"callouts/{mapName}.callouts.json.");
                return;
            }

            _table.Anchors = builtIn;

            Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(_table, Options));

            KaiLog.Event(nameof(OnMapStart),
                $"wrote a starting callout table for '{mapName}' with {builtIn.Count} name(s) " +
                $"to '{path}'. Edit it freely; it is read back on the next map load.");
        }
        catch (Exception ex)
        {
            KaiLog.Event(nameof(OnMapStart), $"callout load failed: {ex.Message}",
                KaiLogLevel.Error);
        }
    }

    // The nearest named place, or empty if nothing is close enough.
    public static string Nearest(KaiPoint spot)
    {
        string best = "";
        float bestDist = _table.MaxDistance;

        foreach (var anchor in _table.Anchors)
        {
            float dx = anchor.X - spot.X;
            float dy = anchor.Y - spot.Y;
            float dist = MathF.Sqrt((dx * dx) + (dy * dy));

            if (anchor.HasHeight && MathF.Abs(anchor.Z - spot.Z) > 180.0f)
            {
                continue;
            }

            if (dist < bestDist)
            {
                bestDist = dist;
                best = anchor.Name;
            }
        }

        return best;
    }

    // How a bot refers to a place out loud.
    //
    // Falls back to a distance from the bomb when the position is not near any
    // named place, because "unmapped, 900 out" is at least true, where a
    // compass bearing dressed up as a callout is not.
    public static string Describe(KaiPoint spot, KaiPoint? bomb)
    {
        string name = Nearest(spot);

        if (name.Length > 0)
        {
            return name;
        }

        if (bomb == null)
        {
            return "off the callouts";
        }

        return $"open ground {spot.DistanceXY(bomb.X, bomb.Y):F0} out";
    }

    // The named place a route passes through at its midpoint, used to say how
    // a side is approaching rather than where it ends up.
    public static string ApproachName(KaiPoint mid)
    {
        string name = Nearest(mid);
        return name.Length > 0 ? name : "";
    }

    // de_inferno.
    //
    // Derived from the community callout overview by anchoring the image to
    // world space on the two bombsites, which were already known from watching
    // where the bomb actually gets planted. The scale came out at 6.15 units
    // per pixel across and 6.26 down, near enough uniform to confirm the image
    // is a true overhead projection rather than a stylised drawing.
    //
    // Checked before being trusted: the transform puts T Spawn at world X
    // -1707, and the westernmost node in the recorded navigation graph sits at
    // -1702. A five unit agreement on a point that was not used to build the
    // transform. Against the learned data all 215 pre-aim spots resolve to a
    // name, and both plant sites come back as A Site and B Site.
    private static void AddInferno(Action<string, float, float> Add)
    {
        Add("Coffins", 58, 3158);
        Add("Garden", 353, 3346);
        Add("Sandbags Construction", 1620, 3384);
        Add("Dark", -40, 3121);
        Add("Construction", 1067, 3090);
        Add("Fountain", -261, 2871);
        Add("B Site", 292, 2683);
        Add("Grill", 661, 2858);
        Add("Truck", 1251, 2883);
        Add("Quad", -77, 2545);
        Add("CT", 833, 2595);
        Add("Tree", 1251, 2545);
        Add("Terrace", 2198, 2452);
        Add("Well", 1829, 2477);
        Add("Second Oranges", 144, 2389);
        Add("First Oranges", 427, 2389);
        Add("CT Boost", 1214, 2339);
        Add("CT Spawn", 2321, 2139);
        Add("Boost", -3, 2201);
        Add("Speedway", 1534, 2064);
        Add("Car", 329, 1970);
        Add("Sandbags Banana", 784, 1820);
        Add("Banana", 181, 1657);
        Add("Logs", -261, 1532);
        Add("Long Corner", 1030, 1344);
        Add("Arch", 1682, 1350);
        Add("Kitchen", 2333, 1394);
        Add("A Long", 1596, 1056);
        Add("Library", 2395, 1075);
        Add("Ledge", -680, 950);
        Add("Underpass", 489, 950);
        Add("T Ramp", -274, 787);
        Add("Bench", 1005, 881);
        Add("Graveyard", 2518, 737);
        Add("Boiler", 1153, 650);
        Add("Back Site", 1780, 719);
        Add("Bottom Mid", -126, 537);
        Add("Mid", 476, 525);
        Add("Top Mid", 1337, 443);
        Add("A Site", 1940, 387);
        Add("T Spawn", -1706, 450);
        Add("Close Left", 1706, 174);
        Add("Mid Stairs", 821, 143);
        Add("Living Room", -397, 168);
        Add("A Short", 1497, 55);
        Add("Balcony", -102, -7);
        Add("Patio", 1411, -70);
        Add("Second Mid", 181, -151);
        Add("Window", 895, -151);
        Add("Truck Cemetery", 2137, -138);
        Add("CT Apps", 1091, -307);
        Add("Pit", 2420, -464);
        Add("T Apps", -225, -514);
        Add("Apps Stairs", 1227, -545);
        Add("Back Alley", 489, -651);
        Add("Close Apps", 1596, -651);
        Add("Apps Balcony", 2137, -670);
        Add("Bridge", -680, -733);
        Add("Second Mid Door", -102, -870);
    }

    // de_dust2.
    //
    // Same method as de_inferno, but anchored differently, because Dust2's
    // two bombsites sit within a hundred units of each other north to south
    // and Cache's sit within a hundred and fifty east to west, so a
    // transform fitted to the sites alone is badly conditioned on both maps.
    //
    // Fitted instead on the one thing the recorded data states without
    // ambiguity: bots do not walk through walls. On the overview a wall is
    // drawn black and a floor grey, so a candidate scale and offset can be
    // scored by how many of the 3136 recorded positions land on grey. That
    // was then sharpened on a test that does not saturate: a player's hull
    // is 32 units across, so an origin cannot sit within 16 units of a wall
    // face, and the count of positions that do is a clean minimum. It came
    // out at 5.700 units per pixel, with pixel (0,0) at world
    // (-2505.3, 3248.3).
    //
    // Checked before being trusted, against things not used to fit it: both
    // recorded plant positions land inside the drawn orange bombsite
    // rectangles, and both recorded spawn averages land inside the drawn
    // green spawn boxes. Against the learned data, 334 of 337 recorded
    // pre-aim, post-plant and CT-clear positions resolve to a name, and the
    // plant sites come back as A Site and B Site.
    //
    // The three that do not resolve sit in the eastern end of T spawn,
    // which the overview leaves unlabelled; they are 770 to 880 units from
    // T Spawn, just outside MaxDistance. Nothing is invented to cover them.
    //
    // Not included: A Default Plant and B Default Plant. They name a bomb
    // position rather than a place, they sit a couple of hundred units from
    // the site anchors, and the plugin names a bombsite by asking which
    // callout is nearest the recorded plant position. Keeping them would
    // make that answer "A Default Plant" where "A Site" is wanted.
    private static void AddDust2(Action<string, float, float> Add)
    {
        // Counted here rather than written down beside the list, so the
        // log line cannot drift out of step with what actually went in
        // the next time a callout is added or dropped.
        int placed = 0;

        void Place(string label, float x, float y)
        {
            Add(label, x, y);
            placed++;
        }

        Place("Back Plat", -2004, 3094);
        Place("Goose", 1017, 3017);
        Place("B Back Site", -1656, 2809);
        Place("Ninja", 561, 2781);
        Place("Barrels", 1336, 2767);
        Place("Scaffolding", -1075, 2673);
        Place("B Window", -1325, 2661);
        Place("B Plat", -1978, 2650);
        Place("B Site", -1559, 2644);
        Place("A Ramp", 1519, 2627);
        Place("A Plat", 781, 2604);
        Place("Double Stack", -1679, 2593);
        Place("A Site", 1108, 2482);
        Place("CT Spawn", 257, 2415);
        Place("Big Box", -1824, 2370);
        Place("Elevator", 966, 2359);
        Place("Short Boost", 624, 2262);
        Place("CT Mid", -610, 2245);
        Place("A Short", 422, 2211);
        Place("B Doors", -1317, 2205);
        Place("Fence", -2220, 2188);
        Place("A Cross", 1411, 2160);
        Place("A Car", 1687, 2054);
        Place("B Boxes", -1080, 2051);
        Place("B Car", -1613, 1869);
        Place("Close Mid Doors", -328, 1812);
        Place("Close", -2198, 1789);
        Place("Mid Doors", -402, 1692);
        Place("Stairs", 396, 1661);
        Place("B Closet", -1596, 1561);
        Place("A Long", 1388, 1476);
        Place("Lower Tunnels", -909, 1436);
        Place("Xbox", -336, 1416);
        Place("Long Corner", 1291, 1293);
        Place("Upper Tunnels", -1818, 1151);
        Place("Blue", 804, 1148);
        Place("Mid", -459, 1002);
        Place("Catwalk", -202, 946);
        Place("Palm", -294, 774);
        Place("Pit Plat", 1781, 621);
        Place("Long Doors", 618, 604);
        Place("Side Pit", 1071, 481);
        Place("Pit", 1436, 461);
        Place("Top Mid", -165, 395);
        Place("Right Side Mid", -713, 381);
        Place("Outside", -1693, 364);
        Place("Tunnels", -1696, 136);
        Place("Outside Long", 573, 136);
        Place("Suicide", -465, -177);
        Place("T Ramp", -2095, -331);
        Place("T Plat", -1365, -383);
        Place("T Spawn", -741, -783);

        KaiLog.Event(nameof(AddDust2),
            $"placed {placed} built-in callout anchor(s) for de_dust2");
    }

    // de_cache.
    //
    // Fitted the same way as de_dust2 above: scored on how many recorded
    // positions land on drawn floor rather than drawn wall, then sharpened
    // on how many sit within a player half width of a wall face. It came
    // out at 7.125 units per pixel, with pixel (0,0) at world
    // (-2025.1, 3280.4).
    //
    // Checked before being trusted: the recorded B plant position lands 21
    // units from the centre of the drawn B bombsite rectangle and the A
    // plant lands inside the A rectangle, and both spawn averages land
    // inside their drawn spawn boxes. None of those four were used to fit
    // the transform. Against the learned data all 303 recorded pre-aim,
    // post-plant and CT-clear positions resolve to a name, and the plant
    // sites come back as B Site and A Site, which also settles the site
    // order: the recorded file has no site letters for this map.
    //
    // Not included: A Default and B Default, for the same reason as Dust2.
    // Default Box is kept, because it names a physical object and sits far
    // enough from the site centre not to shadow it.
    //
    // Heights are left unset, as on de_mirage and de_inferno. Cache does
    // stack geometry, but the recorded samples do not show two clear height
    // bands at any anchor, and a wrong Z would silently hide that anchor
    // from the lookup rather than merely being imprecise.
    private static void AddCache(Action<string, float, float> Add)
    {
        // Counted here rather than written down beside the list, so the
        // log line cannot drift out of step with what actually went in
        // the next time a callout is added or dropped.
        int placed = 0;

        void Place(string label, float x, float y)
        {
            Add(label, x, y);
            placed++;
        }

        Place("NBK", 41, 2254);
        Place("Squeaky", 604, 2201);
        Place("Shroud", 469, 2183);
        Place("Quad", -297, 2172);
        Place("A Site", -219, 1773);
        Place("Default Box", -272, 1592);
        Place("Lockers", 975, 1485);
        Place("A Main", 654, 1464);
        Place("Forklift", 141, 1460);
        Place("Elektro", -646, 1317);
        Place("Balcony", 169, 1268);
        Place("Truck", -903, 1036);
        Place("A Long", 1260, 1029);
        Place("Highway", -212, 901);
        Place("Cubby", 511, 815);
        Place("CT Spawn", -1470, 803);
        Place("T Truck", 2300, 701);
        Place("Boost", 821, 651);
        Place("White Box", -44, 473);
        Place("Mid", 429, 306);
        Place("Red", 1163, 302);
        Place("CT Halls", -899, 231);
        Place("Connector", -443, 92);
        Place("Sand Bags", 91, 67);
        Place("T Spawn", 3030, 51);
        Place("Garage", 1238, -15);
        Place("Roof", 301, -43);
        Place("Vents", 536, -186);
        Place("Dumpster", 1206, -325);
        Place("Checkers", 166, -343);
        Place("Heaven", -561, -535);
        Place("Rafters", -383, -549);
        Place("Boxes", 640, -553);
        Place("T Boxes", 2043, -667);
        Place("Hell", -853, -724);
        Place("B Main", 444, -781);
        Place("Tree", -999, -824);
        Place("B Ramp", -59, -909);
        Place("B Halls", 1014, -909);
        Place("Pit", 162, -1066);
        Place("B Site", -51, -1251);
        Place("Toxic", 982, -1251);
        Place("Headshot", -301, -1258);
        Place("Sun Room", 885, -1315);
        Place("New Boxes", 155, -1386);
        Place("Spray", 20, -1415);

        KaiLog.Event(nameof(AddCache),
            $"placed {placed} built-in callout anchor(s) for de_cache");
    }

    // Starting tables. Only maps whose coordinates have actually been checked
    // appear here; a guessed table is worse than none, because a wrong callout
    // sends somebody to the wrong place with confidence.
    private static List<KaiCalloutAnchor> BuiltIn(string mapName)
    {
        var list = new List<KaiCalloutAnchor>();

        void Add(string name, float x, float y)
        {
            list.Add(new KaiCalloutAnchor { Name = name, X = x, Y = y });
        }

        if (mapName == "de_inferno")
        {
            AddInferno(Add);
            return list;
        }

        if (mapName == "de_dust2")
        {
            AddDust2(Add);
            return list;
        }

        if (mapName == "de_cache")
        {
            AddCache(Add);
            return list;
        }

        if (mapName != "de_mirage")
        {
            return list;
        }

        Add("T Spawn", 1150, 50);
        Add("Side Alley", 1000, 520);
        Add("Cart", 780, 430);
        Add("House", 560, 700);
        Add("T Ramp", 700, 250);
        Add("Kitchen", -780, 800);
        Add("Back Alley", -560, 620);
        Add("B Apartments", -1080, 520);
        Add("Arches", -1380, 300);
        Add("Van", -1880, 420);
        Add("Bench", -2120, 190);
        Add("Boost Boxes", -2300, 430);
        Add("B Site", -2043, 306);
        Add("B Platform", -1900, 640);
        Add("Market", -1820, -480);
        Add("Window", -2150, -420);
        Add("Door", -2350, -560);
        Add("Sneaky", -2050, -900);
        Add("E Box", -1500, -620);
        Add("Snipers Nest", -1450, -600);
        Add("B Short", -380, 120);
        Add("Underpass", -520, -90);
        Add("Vent", -700, -380);
        Add("Ladder Room", -620, -250);
        Add("Catwalk", -260, -700);
        Add("Mid", -120, -420);
        Add("Top Mid", 380, -300);
        Add("Mid Boxes", 560, -560);
        Add("Chair", 180, -620);
        Add("Connector", -620, -880);
        Add("Jungle", -720, -1280);
        Add("Sandwich", 0, -1000);
        Add("Stairs", 180, -1180);
        Add("Tetris", 330, -1120);
        Add("Shadows", 600, -1320);
        Add("A Ramp", 480, -980);
        Add("Palace", 760, -1560);
        Add("Pillars", 520, -1720);
        Add("T Roof", 880, -1180);
        Add("Triple Box", -780, -1900);
        Add("Trash", -900, -1750);
        Add("CT Spawn", -1720, -1700);
        Add("CT", -1150, -2050);
        Add("Ticket Booth", -980, -2380);
        Add("A Site", -471, -2136);
        Add("Ninja", -620, -2480);
        Add("Firebox", -300, -2520);
        Add("Balcony", -90, -2380);

        return list;
    }
}

public static class KaiComms
{
    // Detail by default.
    //
    // The quieter setting was the wrong default. The moments worth listening
    // to are a retake and a firefight, and both are made of exactly the
    // traffic that Calls leaves out: who is clearing what, who is covering
    // which entry, where the contact is. kai_comms calls quietens it again.
    public static KaiCommsLevel Level { get; set; } = KaiCommsLevel.Detail;

    // One colour throughout. Changing colour per message type turns the chat
    // into a light show and makes none of it easier to read.
    private static readonly char Speech = ChatColors.Green;

    // Shorter than it was. A fight lasts a couple of seconds and a call that
    // arrives after it is over is noise rather than information.
    private const float DefaultThrottleSeconds = 2.5f;

    private static readonly Dictionary<string, float> _lastSent = new();

    // Send one line, spoken by a squad member, to the squad's own team.
    //
    //   actingTeam    the side that actually did the thing. Required, and
    //                 checked, because getting it wrong is not a cosmetic
    //                 error: it puts the other side's intentions in your team
    //                 chat.
    //   speakerSlot   the bot that did it. Falls back to another living squad
    //                 member when that bot is unnamed or dead.
    //   key           throttle key. The same key inside the window is dropped.
    //
    // WHY actingTeam IS A PARAMETER
    //
    // It used to be inferred from the speaker, and the inference was wrong.
    // SpeakerFor falls back to any living squad member when the acting bot is
    // not on the squad, and the squad is always the human's team, so a CT
    // action with no CT in the squad was handed to a T bot and announced to
    // the T team. A Terrorist would announce that he was going in to defuse.
    //
    // Passing it explicitly makes that impossible: a message whose acting team
    // is not the squad's team is dropped, whoever ends up speaking it.
    public static void Say(
        int actingTeam,
        int speakerSlot,
        string key,
        string message,
        KaiCommsLevel level = KaiCommsLevel.Calls,
        float throttleSeconds = DefaultThrottleSeconds)
    {
        if (Level == KaiCommsLevel.Off || level > Level)
        {
            return;
        }

        if (KaiSquad.SquadTeam < 0)
        {
            // Nobody is playing, so there is nobody to tell.
            return;
        }

        // The other side's business. Not ours to overhear and certainly not
        // ours to announce.
        if (actingTeam != KaiSquad.SquadTeam)
        {
            return;
        }

        try
        {
            float now = Server.CurrentTime;

            if (_lastSent.TryGetValue(key, out float last))
            {
                // Guarded both ways: the server clock restarts on a map
                // change, and a negative gap must not read as "long enough
                // ago" or every key unblocks at once.
                if (now >= last && now - last < throttleSeconds)
                {
                    return;
                }
            }

            int slot = KaiSquad.SpeakerFor(speakerSlot);

            if (slot < 0)
            {
                // The whole squad is dead. Nothing to say and nobody to say
                // it, which is itself information.
                return;
            }

            _lastSent[key] = now;

            string line = $" {Speech}{KaiSquad.NameOf(slot)}: {message}";

            foreach (var p in KaiPlayers.All())
            {
                if (p == null || !p.IsValid || p.IsBot || p.IsHLTV)
                {
                    continue;
                }

                int watching = (int)p.TeamNum;

                bool sameTeam = watching == KaiSquad.SquadTeam;
                bool spectating = watching != (int)CsTeam.Terrorist
                                  && watching != (int)CsTeam.CounterTerrorist;

                if (!sameTeam && !spectating)
                {
                    continue;
                }

                p.PrintToChat(line);
            }
        }
        catch (Exception ex)
        {
            KaiLog.Throttled("commsfail", nameof(Say),
                $"could not send team chat: {ex.Message}", 30.0f, KaiLogLevel.Error);
        }
    }

    // A call worth reading: plays, audibles, the defuse.
    public static void Call(
        int actingTeam, int speakerSlot, string key, string message,
        float throttleSeconds = DefaultThrottleSeconds)
    {
        Say(actingTeam, speakerSlot, key, message, KaiCommsLevel.Calls, throttleSeconds);
    }

    // Routine traffic: clears, cover, waypoints. Only at Detail.
    public static void Detail(
        int actingTeam, int speakerSlot, string key, string message,
        float throttleSeconds = DefaultThrottleSeconds)
    {
        Say(actingTeam, speakerSlot, key, message, KaiCommsLevel.Detail, throttleSeconds);
    }

    // As above, but the acting team is taken from the bot itself. For call
    // sites where the actor is known but its side is not to hand.
    public static void CallBy(
        int speakerSlot, string key, string message,
        float throttleSeconds = DefaultThrottleSeconds)
    {
        Say(TeamOf(speakerSlot), speakerSlot, key, message,
            KaiCommsLevel.Calls, throttleSeconds);
    }

    public static void DetailBy(
        int speakerSlot, string key, string message,
        float throttleSeconds = DefaultThrottleSeconds)
    {
        Say(TeamOf(speakerSlot), speakerSlot, key, message,
            KaiCommsLevel.Detail, throttleSeconds);
    }

    private static int TeamOf(int slot)
    {
        var p = Utilities.GetPlayerFromSlot(slot);

        return p != null && p.IsValid ? (int)p.TeamNum : -1;
    }

    // Addressed to the human by name. Used for the round-start instruction,
    // which is the one message meant for a person rather than about the team.
    public static void ToHuman(
        int actingTeam, int speakerSlot, string key, string message,
        float throttleSeconds = DefaultThrottleSeconds)
    {
        if (KaiSquad.HumanName.Length == 0)
        {
            return;
        }

        Say(actingTeam, speakerSlot, key, $"{KaiSquad.HumanName} {message}",
            KaiCommsLevel.Calls, throttleSeconds);
    }

    public static void Reset()
    {
        _lastSent.Clear();
    }

    public static string Summary()
    {
        return $"level={Level} squadTeam={KaiSquad.SquadTeam} human='{KaiSquad.HumanName}' " +
               $"throttled={_lastSent.Count} key(s)";
    }
}
