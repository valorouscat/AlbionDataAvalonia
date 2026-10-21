using AlbionDataAvalonia.Activity.Models;
using AlbionDataAvalonia.Network.Events;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AlbionDataAvalonia.Activity;

// The live map can show the roaming bosses and the other players around the character where they stand now, like the
// resource mobs: they come with NewMob and NewCharacter, follow Move packets, and go when the game says they left or
// died. Neither is remembered between visits.
public sealed partial class ActivityTrackerService
{
    private const int MaxLiveActors = 500;
    // A move shorter than this doesn't redraw the map.
    private const double LiveActorStep = 0.5;
    private readonly Dictionary<long, ActivityLiveActor> roamingBosses = new();
    private readonly Dictionary<long, ActivityLiveActor> players = new();
    private long liveActorsEpoch = -1;
    // The first player of each map logs its packet, so the layout can be checked in game.
    private long playerLayoutEpoch = -1;

    /// <summary>A NewCharacter event the live map reads raw for the player's position.</summary>
    public void ObservePlayers(DebugEventProbeEvent packet)
    {
        lock (sync)
        {
            if (disposed || map is null || captureSuspended) return;
            var at = Utc(packet.CapturedAt);
            if (at < mapEnteredAt || !SameConnection(packet.ConnectionId, packet.MessageIdentity)) return;
            if (ActivityLiveActors.ReadCharacter(packet.Parameters) is not { } sighting) return;
            SyncLiveActors();
            var actor = actors.Resolve(sighting.Id, sighting.Name, sighting.Guild);
            if (actor.Relationship == ActorRelationship.Self) return;
            if (playerLayoutEpoch != mapEpoch)
            {
                playerLayoutEpoch = mapEpoch;
                Log.Information("Player at {X:0.#}, {Y:0.#}; NewCharacter parameters: {Parameters}",
                    sighting.X, sighting.Y, ActivityResourceMobs.Describe(packet.Parameters));
            }

            Keep(players, new ActivityLiveActor(sighting.Id, ActivityLiveActorKind.Player, sighting.X, sighting.Y, at,
                string.IsNullOrWhiteSpace(sighting.Name) ? actor.Name : sighting.Name, sighting.Guild, sighting.Alliance,
                Party: actor.Relationship == ActorRelationship.Party));
            liveMapVersion++;
        }
    }

    private bool PlaceRoamingBoss(ResourceMobSighting sighting, DateTime at)
    {
        SyncLiveActors();
        var descriptor = actors.DescribeMob(sighting.MobIndex);
        if (ActivityLiveActors.RoamingBoss(descriptor?.UniqueName) is not { } miniboss) return roamingBosses.Remove(sighting.Id);
        Keep(roamingBosses, new ActivityLiveActor(sighting.Id, miniboss ? ActivityLiveActorKind.Miniboss : ActivityLiveActorKind.Boss,
            sighting.X, sighting.Y, at, descriptor!.DisplayName, MobUniqueName: descriptor.UniqueName ?? "",
            Tier: ActivityLiveActors.Tier(descriptor.UniqueName)));
        return true;
    }

    private static void Keep(Dictionary<long, ActivityLiveActor> known, ActivityLiveActor actor)
    {
        if (known.Count >= MaxLiveActors && !known.ContainsKey(actor.Id)) known.Remove(known.Values.MinBy(x => x.SeenAtUtc)!.Id);
        known[actor.Id] = actor;
    }

    private bool MoveLiveActor(long id, double x, double y, DateTime at)
    {
        SyncLiveActors();
        var known = roamingBosses.ContainsKey(id) ? roamingBosses : players.ContainsKey(id) ? players : null;
        if (known is null) return false;
        var actor = known[id];
        var far = Math.Abs(actor.X - x) >= LiveActorStep || Math.Abs(actor.Y - y) >= LiveActorStep;
        known[id] = far ? actor with { X = x, Y = y, SeenAtUtc = at } : actor with { SeenAtUtc = at };
        return far;
    }

    // A boss or player that left the character's view or died.
    private void ForgetLiveActor(long? id)
    {
        SyncLiveActors();
        if (id is { } value && (roamingBosses.Remove(value) | players.Remove(value))) liveMapVersion++;
    }

    // Object ids belong to one map, so a new map starts with none.
    private void SyncLiveActors()
    {
        if (liveActorsEpoch == mapEpoch) return;
        liveActorsEpoch = mapEpoch;
        roamingBosses.Clear();
        players.Clear();
    }

    private ActivityLiveActor[] CurrentRoamingBosses()
    {
        SyncLiveActors();
        return roamingBosses.Values.ToArray();
    }

    private ActivityLiveActor[] CurrentPlayers()
    {
        SyncLiveActors();
        return players.Values.ToArray();
    }
}
