// kai_arsenal.cs
//
// KAI ADDITION. Not part of CS2-Bot-Improver.
//
// Knowing when a bot is out of ammo, and what to do about it.
//
// THE PROBLEM
//
// Bots keep pulling the trigger on empty guns with loaded rifles lying on the
// floor beside them. The native AI has no concept of resupplying mid-round: it
// picks a weapon at buy time and lives with it, and once the ammo is gone the
// bot is a spectator that still walks around.
//
// WHAT THIS DOES
//
// Three things, in the order a person would do them.
//
// Dry and safe: go and pick something up. A rifle twenty metres away is worth
// more than any angle a bot with no bullets can hold.
//
// Dry and in a fight: draw the knife and commit to it. Not because a knife
// beats a rifle, but because a bot standing still holding an empty gun loses
// with certainty, and one moving erratically at close range sometimes does
// not. Then take the gun off whoever it killed.
//
// Told about it: a weapon seen by anybody is remembered by everybody. "There
// is an AK on the ground at Mid Doors" is a real callout, and it stays true
// after the bot that saw it has moved on or died. The memory lasts the round,
// which is exactly as long as the weapon does.

using System;
using System.Collections.Generic;
using System.Linq;

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace KaiBotTactics;

// A weapon somebody has seen on the floor.
public sealed class KaiDroppedWeapon
{
    public string DesignerName = "";

    // What a person would call it: "AK", "AWP", "Deagle".
    public string ShortName = "";

    public KaiPoint Position = new();

    // Where it is, in map terms, for the callout.
    public string Callout = "";

    // Who first saw it, and when. Kept so a stale report can be aged out if
    // the weapon is picked up by somebody else.
    public int SeenBy;
    public float SeenAt;

    // Rifles are worth crossing a site for; a pistol usually is not.
    public bool IsPrimary;

    public int EntityIndex;
}

public sealed class KaiArsenal
{
    // ------------------------------------------------------------------
    // Tunables
    // ------------------------------------------------------------------

    public bool Enabled = true;

    // Rounds left in the magazine plus reserve, at or below which a bot counts
    // as dry. Not zero: a bot with two rounds left is already in trouble and
    // should be moving towards a resupply before it is empty rather than
    // after.
    public int DryThreshold = 5;

    // How far a bot will travel for a weapon it has been told about. Beyond
    // this it is a different part of the map and the round will be decided
    // before it arrives.
    public float PickupRange = 1600.0f;

    // Furthest a bot will charge an armed enemy holding nothing but a knife.
    //
    // The knife is the only answer at close range and a hopeless one at any
    // other. Measured across three sessions: the median knife charge covered
    // 481 units, but 20 of 111 were over 800 and the longest was 1516, which
    // is a bot sprinting most of the length of the map at somebody with a
    // rifle. Beyond this the bot breaks off and goes for a gun on the floor
    // instead, which is the same decision a person would make.
    //
    // 600 units is roughly two seconds of running, which is about as long as
    // anybody survives crossing open ground at a loaded weapon.
    public float KnifeRushRange = 600.0f;

    // How near a weapon has to be before a bot has to be able to see it. Very
    // close, so a bot walks onto something in the open rather than needing a
    // clear line from wherever it started.
    public float PickupArriveRadius = 70.0f;

    // How often the floor is swept for dropped weapons.
    public float ScanInterval = 1.0f;

    // How far a bot can be from a weapon and still be counted as having seen
    // it. Sight is checked as well; this only bounds the trace count.
    public float SpotRange = 1800.0f;

    // ------------------------------------------------------------------
    // State
    // ------------------------------------------------------------------

    // Entity index -> what is known about it. Keyed on the entity so the same
    // gun is not remembered twice, and cleared each round because the map
    // resets its weapons anyway.
    private readonly Dictionary<int, KaiDroppedWeapon> _known = new();

    private float _nextScan;

    // slot -> the weapon it is currently going to collect.
    private readonly Dictionary<int, int> _collecting = new();

    // slot -> when it last drew a knife out of desperation, so the log and the
    // comms do not repeat every tick.
    private readonly Dictionary<int, float> _knifeSince = new();

    public int KnownCount => _known.Count;

    public string Summary()
    {
        int primaries = _known.Values.Count(w => w.IsPrimary);

        return $"enabled={Enabled} known={_known.Count} ({primaries} primary) " +
               $"collecting={_collecting.Count} knifing={_knifeSince.Count} " +
               $"dryAt={DryThreshold} knifeRange={KnifeRushRange:F0}";
    }

    public void OnRoundStart()
    {
        _known.Clear();
        _collecting.Clear();
        _knifeSince.Clear();
        _nextScan = 0.0f;
    }

    // ------------------------------------------------------------------
    // Ammunition
    // ------------------------------------------------------------------

    // Total rounds a weapon has available, magazine plus reserve.
    public static int RoundsLeft(CBasePlayerWeapon? weapon)
    {
        if (weapon == null || !weapon.IsValid)
        {
            return 0;
        }

        try
        {
            int total = weapon.Clip1 < 0 ? 0 : weapon.Clip1;

            var reserve = weapon.ReserveAmmo;

            if (reserve.Length > 0 && reserve[0] > 0)
            {
                total += reserve[0];
            }

            return total;
        }
        catch
        {
            return 0;
        }
    }

    // Is this bot out of anything worth shooting?
    //
    // Checked across every weapon it carries rather than only the active one,
    // because a bot with an empty rifle and a full pistol is not dry, it is
    // holding the wrong gun.
    public bool IsDry(CCSPlayerController player, out bool hasAnyGun)
    {
        hasAnyGun = false;

        try
        {
            var weapons = player.PlayerPawn?.Value?.WeaponServices?.MyWeapons;

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

                string name = weapon.DesignerName;

                // The knife and the bomb are not answers to being out of ammo.
                if (name == "weapon_knife" || name.StartsWith("weapon_knife")
                    || name == "weapon_c4")
                {
                    continue;
                }

                hasAnyGun = true;

                if (RoundsLeft(weapon) > DryThreshold)
                {
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            KaiLog.Throttled("dry", nameof(IsDry),
                $"could not read ammunition: {ex.Message}", 30.0f, KaiLogLevel.Error);
            return false;
        }

        return hasAnyGun;
    }

    // ------------------------------------------------------------------
    // Remembering what is on the floor
    // ------------------------------------------------------------------

    // Sweep the floor and record anything a living bot can see.
    //
    // Seen by one is known to all, and stays known. That is the whole point:
    // a weapon does not stop existing because the bot that saw it walked away,
    // and a team that has to rediscover the same rifle three times is not a
    // team that communicates.
    public void Scan(float now)
    {
        if (!Enabled || now < _nextScan)
        {
            return;
        }

        _nextScan = now + ScanInterval;

        try
        {
            var found = 0;

            foreach (var weapon in Utilities
                         .FindAllEntitiesByDesignerName<CBasePlayerWeapon>("weapon_"))
            {
                if (weapon == null || !weapon.IsValid)
                {
                    continue;
                }

                // Held by somebody, so not on the floor.
                if (weapon.OwnerEntity?.Value != null)
                {
                    continue;
                }

                string name = weapon.DesignerName;

                if (name == "weapon_c4" || name.StartsWith("weapon_knife"))
                {
                    continue;
                }

                // Empty guns are litter, not a resupply.
                if (RoundsLeft(weapon) <= DryThreshold)
                {
                    continue;
                }

                var origin = weapon.AbsOrigin;

                if (origin == null)
                {
                    continue;
                }

                int index = (int)weapon.Index;

                if (_known.ContainsKey(index))
                {
                    // Already known. Position is not updated: a weapon on the
                    // floor does not move, and if it has been picked up the
                    // owner check above will stop it being re-reported.
                    continue;
                }

                var spot = new KaiPoint(origin.X, origin.Y, origin.Z);

                int spotter = FirstToSee(spot);

                if (spotter < 0)
                {
                    // On the floor but nobody has laid eyes on it. Not known
                    // yet, and it will be picked up on a later sweep once
                    // somebody walks past.
                    continue;
                }

                var record = new KaiDroppedWeapon
                {
                    DesignerName = name,
                    ShortName = ShortNameOf(name),
                    Position = spot,
                    Callout = KaiCallouts.Nearest(spot),
                    SeenBy = spotter,
                    SeenAt = now,
                    IsPrimary = IsPrimaryWeapon(name),
                    EntityIndex = index,
                };

                _known[index] = record;
                found++;

                string where = record.Callout.Length > 0 ? record.Callout : "in the open";

                KaiLog.Event(nameof(Scan),
                    $"slot {spotter} spotted a dropped {record.ShortName} at {where} " +
                    $"({spot.X:F0},{spot.Y:F0}), {_known.Count} weapon(s) known this round");

                // Only worth saying out loud for a primary. Nobody calls out a
                // dropped pistol.
                if (record.IsPrimary)
                {
                    // Team taken from the spotter, because a weapon on the
                    // floor is only news to the side that saw it.
                    KaiComms.DetailBy(spotter, $"drop:{index}",
                        $"{record.ShortName} on the ground at {where}", 6.0f);
                }
            }

            if (found > 0)
            {
                KaiLog.Event(nameof(Scan), $"{found} new weapon(s) added to the team's memory");
            }
        }
        catch (Exception ex)
        {
            KaiLog.Throttled("wepscan", nameof(Scan),
                $"weapon sweep failed: {ex.Message}", 30.0f, KaiLogLevel.Error);
        }
    }

    // The first living bot with a clear line to this spot, or -1.
    private int FirstToSee(KaiPoint spot)
    {
        foreach (var p in KaiPlayers.All())
        {
            if (p == null || !p.IsValid || !p.IsBot || p.IsHLTV || !p.PawnIsAlive)
            {
                continue;
            }

            var pawn = p.PlayerPawn?.Value;
            var origin = pawn?.AbsOrigin;

            if (pawn == null || origin == null)
            {
                continue;
            }

            if (spot.DistanceXY(origin.X, origin.Y) > SpotRange)
            {
                continue;
            }

            var eye = new Vector(origin.X, origin.Y, origin.Z + pawn.ViewOffset.Z);
            var target = new Vector(spot.X, spot.Y, spot.Z + 12.0f);

            if (KaiRayTraceBridge.CanSee(eye, target))
            {
                return p.Slot;
            }
        }

        return -1;
    }

    // The nearest remembered weapon worth collecting, or null.
    //
    // Primaries are preferred outright rather than by distance: a rifle across
    // the site beats a pistol at your feet, because the pistol will leave the
    // bot dry again within one fight.
    public KaiDroppedWeapon? NearestUseful(Vector origin, int forSlot)
    {
        KaiDroppedWeapon? bestPrimary = null;
        KaiDroppedWeapon? bestAny = null;
        float primaryDist = PickupRange;
        float anyDist = PickupRange;

        foreach (var weapon in _known.Values)
        {
            // Somebody else is already on their way to it.
            if (_collecting.Any(kv => kv.Key != forSlot && kv.Value == weapon.EntityIndex))
            {
                continue;
            }

            float d = weapon.Position.DistanceXY(origin.X, origin.Y);

            if (weapon.IsPrimary && d < primaryDist)
            {
                primaryDist = d;
                bestPrimary = weapon;
            }

            if (d < anyDist)
            {
                anyDist = d;
                bestAny = weapon;
            }
        }

        return bestPrimary ?? bestAny;
    }

    // Forget a weapon that is no longer there.
    public void Forget(int entityIndex, string why)
    {
        if (!_known.TryGetValue(entityIndex, out var weapon))
        {
            return;
        }

        _known.Remove(entityIndex);

        foreach (int slot in _collecting.Where(kv => kv.Value == entityIndex)
                     .Select(kv => kv.Key).ToList())
        {
            _collecting.Remove(slot);
        }

        KaiLog.Event(nameof(Forget),
            $"the {weapon.ShortName} at " +
            $"{(weapon.Callout.Length > 0 ? weapon.Callout : "open ground")} is gone: {why}");
    }

    public void Claim(int slot, int entityIndex)
    {
        _collecting[slot] = entityIndex;
    }

    public void Release(int slot)
    {
        _collecting.Remove(slot);
    }

    public int ClaimOf(int slot)
    {
        return _collecting.GetValueOrDefault(slot, -1);
    }

    public bool StillThere(int entityIndex)
    {
        return _known.ContainsKey(entityIndex);
    }

    public KaiDroppedWeapon? Get(int entityIndex)
    {
        return _known.GetValueOrDefault(entityIndex);
    }

    // ------------------------------------------------------------------
    // The knife
    // ------------------------------------------------------------------

    // Note that this bot has gone to the knife, and whether it is the first
    // time this round.
    public bool BeginKnifing(int slot, float now)
    {
        if (_knifeSince.ContainsKey(slot))
        {
            return false;
        }

        _knifeSince[slot] = now;
        return true;
    }

    public void StopKnifing(int slot)
    {
        _knifeSince.Remove(slot);
    }

    public bool IsKnifing(int slot)
    {
        return _knifeSince.ContainsKey(slot);
    }

    // ------------------------------------------------------------------
    // Naming
    // ------------------------------------------------------------------

    // How far out this weapon is worth holding a line from.
    //
    // Used by the post-plant overwatch: a defender that arrives too late to be
    // part of the ring holds a line onto the bomb instead, and how far back it
    // can usefully sit is entirely a question of what it is carrying.
    //
    // The numbers are holding distances, not maximum ranges. A rifle can hit
    // at 3000 units; it is worth SITTING at around 1400, far enough to see a
    // defuser start and be outside the fight on the site, near enough that the
    // shots land. A shotgun at 1400 is a spectator.
    //
    // There are no AWPs in this game mode, so no entry here assumes one. The
    // sniper rifles are still listed because the game can hand one out
    // through a dropped weapon, and a bot holding an SSG is better off far
    // back than guessing.
    public static float HoldingRangeOf(string designerName)
    {
        return designerName switch
        {
            // Machine guns. Heaviest calibre, worst mobility, best suppression:
            // exactly the weapon you want sitting still a long way back.
            "weapon_m249" or "weapon_negev" => 1800.0f,

            // Rifles. The default long hold.
            "weapon_ak47" or "weapon_m4a1" or "weapon_m4a1_silencer"
                or "weapon_sg556" or "weapon_aug" or "weapon_galilar"
                or "weapon_famas" => 1400.0f,

            // Sniper rifles, if one is ever picked up off the ground.
            "weapon_ssg08" or "weapon_scar20" or "weapon_g3sg1"
                or "weapon_awp" => 2000.0f,

            // Submachine guns. Accurate enough at mid range, useless past it.
            "weapon_mp9" or "weapon_mac10" or "weapon_mp7" or "weapon_mp5sd"
                or "weapon_ump45" or "weapon_p90" or "weapon_bizon" => 800.0f,

            // Shotguns. Have to be close or they are doing nothing at all.
            "weapon_nova" or "weapon_xm1014" or "weapon_mag7"
                or "weapon_sawedoff" => 400.0f,

            // Pistols. Closer than a rifle, further than a shotgun.
            "weapon_deagle" or "weapon_revolver" => 900.0f,

            "weapon_knife" or "weapon_bayonet" => 250.0f,

            // Everything else is a pistol of some description.
            _ => 650.0f,
        };
    }

    // The holding range for whatever this player currently has out, falling
    // back to the best weapon they own if the active one cannot be read.
    public static float HoldingRangeFor(CCSPlayerController player)
    {
        try
        {
            var active = player.PlayerPawn?.Value?.WeaponServices?.ActiveWeapon?.Value;

            if (active != null && active.IsValid)
            {
                return HoldingRangeOf(active.DesignerName);
            }

            // No active weapon readable. Take the longest range among what it
            // is carrying, which is the weapon it would sensibly hold with.
            var weapons = player.PlayerPawn?.Value?.WeaponServices?.MyWeapons;

            if (weapons != null)
            {
                float best = 0.0f;

                foreach (var handle in weapons)
                {
                    var weapon = handle?.Value;

                    if (weapon == null || !weapon.IsValid)
                    {
                        continue;
                    }

                    if (RoundsLeft(weapon) <= 0)
                    {
                        continue;
                    }

                    float range = HoldingRangeOf(weapon.DesignerName);

                    if (range > best)
                    {
                        best = range;
                    }
                }

                if (best > 0.0f)
                {
                    return best;
                }
            }
        }
        catch (Exception ex)
        {
            KaiLog.Throttled($"holdrange:{player.Slot}", nameof(HoldingRangeFor),
                $"could not read the active weapon: {ex.Message}", 30.0f);
        }

        // Rifle by default. It is what most bots are holding most of the time.
        return 1400.0f;
    }

    private static bool IsPrimaryWeapon(string designerName)
    {
        return designerName switch
        {
            "weapon_ak47" or "weapon_m4a1" or "weapon_m4a1_silencer" or "weapon_galilar"
                or "weapon_famas" or "weapon_sg556" or "weapon_aug" or "weapon_awp"
                or "weapon_ssg08" or "weapon_scar20" or "weapon_g3sg1" or "weapon_mp9"
                or "weapon_mac10" or "weapon_mp7" or "weapon_mp5sd" or "weapon_ump45"
                or "weapon_p90" or "weapon_bizon" or "weapon_nova" or "weapon_xm1014"
                or "weapon_mag7" or "weapon_sawedoff" or "weapon_m249" or "weapon_negev"
                => true,
            _ => false,
        };
    }

    // What a person would call it on the radio.
    private static string ShortNameOf(string designerName)
    {
        return designerName switch
        {
            "weapon_ak47" => "AK",
            "weapon_m4a1" => "M4",
            "weapon_m4a1_silencer" => "M4 silenced",
            "weapon_awp" => "AWP",
            "weapon_ssg08" => "Scout",
            "weapon_scar20" or "weapon_g3sg1" => "auto sniper",
            "weapon_galilar" => "Galil",
            "weapon_famas" => "Famas",
            "weapon_sg556" => "SG",
            "weapon_aug" => "AUG",
            "weapon_deagle" => "Deagle",
            "weapon_revolver" => "R8",
            "weapon_usp_silencer" => "USP",
            "weapon_glock" => "Glock",
            "weapon_p250" => "P250",
            "weapon_fiveseven" or "weapon_tec9" or "weapon_cz75a" => "pistol",
            "weapon_mp9" or "weapon_mac10" or "weapon_mp7" or "weapon_mp5sd"
                or "weapon_ump45" or "weapon_p90" or "weapon_bizon" => "SMG",
            "weapon_nova" or "weapon_xm1014" or "weapon_mag7" or "weapon_sawedoff"
                => "shotgun",
            "weapon_m249" or "weapon_negev" => "LMG",
            _ => designerName.Replace("weapon_", ""),
        };
    }
}
