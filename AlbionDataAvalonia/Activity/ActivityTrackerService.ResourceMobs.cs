using AlbionDataAvalonia.Activity.Models;
using AlbionDataAvalonia.Network.Events;
using AlbionDataAvalonia.Shared;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AlbionDataAvalonia.Activity;

// The game tells the client about the mobs around the character. The live map shows the ones that yield gathering
// resources (hide animals and the wood, stone, fiber and ore critters) at their last observed positions.
// Leaving visibility retains the sighting for this map visit; confirmed death removes it.
public sealed partial class ActivityTrackerService
{
    private const int MaxResourceMobs = 1000;
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
                    && (PlaceResourceMob(mob, packet, at) | PlaceRoamingBoss(mob, at) | PlaceWisp(mob, at)),
                EventCodes.MobChangeState => ActivityResourceMobs.ReadChange(packet.Parameters) is { } change && EnchantResourceMob(change.Id, change.Enchantment, at),
                // Dynamic mob POIs and portal wisps move with the same packet.
                EventCodes.Move => ActivityResourceMobs.ReadMove(packet.Parameters) is { } move
                    && (MoveResourceMob(move.Id, move.X, move.Y, at) | MoveLiveActor(move.Id, move.X, move.Y, at) | MoveWisp(move.Id, move.X, move.Y, at)),
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

        resourceMobs.TryGetValue(sighting.Id, out var previous);
        if (resourceMobs.Count >= MaxResourceMobs && !resourceMobs.ContainsKey(sighting.Id))
            resourceMobs.Remove(resourceMobs.Values.MinBy(x => x.SeenAtUtc)!.Id);
        resourceMobs[sighting.Id] = new ActivityResourceMob(sighting.Id, resource.Family, resource.Tier, sighting.Enchantment, sighting.X,
            sighting.Y, at, descriptor!.UniqueName ?? "", descriptor.DisplayName,
            ActivityResourceNodePackets.ItemUniqueName(resource.Family, resource.Tier, sighting.Enchantment));
        NotifyResourceAppearance(at, new(resource.Family, resource.Tier, sighting.Enchantment, true),
            previous is null ? null : new(previous.Family, previous.Tier, previous.Enchantment, true));
        return true;
    }

    private bool EnchantResourceMob(long id, int enchantment, DateTime at)
    {
        if (!resourceMobs.TryGetValue(id, out var mob) || mob.Enchantment == enchantment) return false;
        resourceMobs[id] = mob with
        {
            Enchantment = enchantment,
            ItemUniqueName = ActivityResourceNodePackets.ItemUniqueName(mob.Family, mob.Tier, enchantment)
        };
        NotifyResourceAppearance(at, new(mob.Family, mob.Tier, enchantment, true),
            new(mob.Family, mob.Tier, mob.Enchantment, true));
        return true;
    }

    private bool MoveResourceMob(long id, double x, double y, DateTime at)
    {
        if (!resourceMobs.TryGetValue(id, out var mob)) return false;
        var moved = mob.X != x || mob.Y != y;
        resourceMobs[id] = mob with { X = x, Y = y, SeenAtUtc = at, IsInView = true };
        return moved || !mob.IsInView;
    }

    private void LeaveResourceMob(long id)
    {
        SyncResourceMobs();
        if (!resourceMobs.TryGetValue(id, out var mob) || !mob.IsInView) return;
        resourceMobs[id] = mob with { IsInView = false };
        liveMapVersion++;
    }

    // Confirmed death; a killed hide animal comes back as a harvestable node to skin.
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
