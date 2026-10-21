using AlbionDataAvalonia.Activity.Models;
using AlbionDataAvalonia.Network.Events;
using AlbionDataAvalonia.Shared;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AlbionDataAvalonia.Activity;

// The game tells the client about the mobs around the character. The live map shows the ones that yield gathering
// resources (hide animals and the wood, stone, fiber and ore critters) where they stand now: they follow Move packets,
// and go when the game says they left or died. Mobs roam and respawn elsewhere, so none are remembered between visits.
public sealed partial class ActivityTrackerService
{
    private const int MaxResourceMobs = 1000;
    // A mob's move shorter than this doesn't redraw the map.
    private const double ResourceMobStep = 0.5;
    private readonly Dictionary<long, ActivityResourceMob> resourceMobs = new();
    private long resourceMobsEpoch = -1;
    // The first resource mob of each map logs its packet, so the layout can be checked in game.
    private long resourceMobLayoutEpoch = -1;

    /// <summary>A mob event the live map reads raw (NewMob, MobChangeState or Move).</summary>
    public void ObserveResourceMobs(DebugEventProbeEvent packet, EventCodes code)
    {
        lock (sync)
        {
            if (disposed || map is null || captureSuspended) return;
            var at = Utc(packet.CapturedAt);
            if (at < mapEnteredAt || !SameConnection(packet.ConnectionId, packet.MessageIdentity)) return;
            SyncResourceMobs();
            var changed = code switch
            {
                EventCodes.NewMob => ActivityResourceMobs.ReadNew(packet.Parameters) is { } mob
                    && (PlaceResourceMob(mob, packet, at) | PlaceRoamingBoss(mob, at)),
                EventCodes.MobChangeState => ActivityResourceMobs.ReadChange(packet.Parameters) is { } change && EnchantResourceMob(change.Id, change.Enchantment),
                // Roaming bosses move with the same packet.
                EventCodes.Move => ActivityResourceMobs.ReadMove(packet.Parameters) is { } move
                    && (MoveResourceMob(move.Id, move.X, move.Y, at) | MoveLiveActor(move.Id, move.X, move.Y, at)),
                _ => false
            };
            if (changed) liveMapVersion++;
        }
    }

    private bool PlaceResourceMob(ResourceMobSighting sighting, DebugEventProbeEvent packet, DateTime at)
    {
        var descriptor = actors.DescribeMob(sighting.MobIndex);
        if (ActivityResourceMobs.Resource(descriptor?.UniqueName) is not { } resource) return resourceMobs.Remove(sighting.Id);
        if (resourceMobLayoutEpoch != mapEpoch)
        {
            resourceMobLayoutEpoch = mapEpoch;
            Log.Information("Resource mob {Mob} at {X:0.#}, {Y:0.#}, enchantment {Enchantment}; NewMob parameters: {Parameters}",
                descriptor!.UniqueName, sighting.X, sighting.Y, sighting.Enchantment, ActivityResourceMobs.Describe(packet.Parameters));
        }

        if (resourceMobs.Count >= MaxResourceMobs && !resourceMobs.ContainsKey(sighting.Id))
            resourceMobs.Remove(resourceMobs.Values.MinBy(x => x.SeenAtUtc)!.Id);
        resourceMobs[sighting.Id] = new ActivityResourceMob(sighting.Id, resource.Family, resource.Tier, sighting.Enchantment, sighting.X,
            sighting.Y, at, descriptor!.UniqueName ?? "", descriptor.DisplayName,
            ActivityResourceNodePackets.ItemUniqueName(resource.Family, resource.Tier, sighting.Enchantment));
        return true;
    }

    private bool EnchantResourceMob(long id, int enchantment)
    {
        if (!resourceMobs.TryGetValue(id, out var mob) || mob.Enchantment == enchantment) return false;
        resourceMobs[id] = mob with
        {
            Enchantment = enchantment,
            ItemUniqueName = ActivityResourceNodePackets.ItemUniqueName(mob.Family, mob.Tier, enchantment)
        };
        return true;
    }

    private bool MoveResourceMob(long id, double x, double y, DateTime at)
    {
        if (!resourceMobs.TryGetValue(id, out var mob)) return false;
        var far = Math.Abs(mob.X - x) >= ResourceMobStep || Math.Abs(mob.Y - y) >= ResourceMobStep;
        resourceMobs[id] = far ? mob with { X = x, Y = y, SeenAtUtc = at } : mob with { SeenAtUtc = at };
        return far;
    }

    // A mob that left the character's view or died; a killed hide animal comes back as a harvestable node to skin.
    private void ForgetResourceMob(long? id)
    {
        SyncResourceMobs();
        if (id is { } value && resourceMobs.Remove(value)) liveMapVersion++;
    }

    // Object ids belong to one map, so a new map starts with none.
    private void SyncResourceMobs()
    {
        if (resourceMobsEpoch == mapEpoch) return;
        resourceMobsEpoch = mapEpoch;
        resourceMobs.Clear();
    }

    private ActivityResourceMob[] CurrentResourceMobs()
    {
        SyncResourceMobs();
        return resourceMobs.Values.ToArray();
    }
}
