using Albion.Network;
using AlbionDataAvalonia.Activity.Models;
using AlbionDataAvalonia.Network.Events;
using AlbionDataAvalonia.Network.Responses;
using AlbionDataAvalonia.Shared;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AlbionDataAvalonia.Activity;

public sealed partial class ActivityTrackerService
{
    public CombatMeter CombatMeter { get; } = new();
    private readonly Dictionary<long, (Guid Lifetime, int Flag, DateTime At)> combatFlags = new();
    private readonly Dictionary<PendingActorKey, (int Flag, DateTime At)> pendingCombatFlags = new();
    private string combatOwner = "";
    private int? localCombatFlag;

    public int? CombatFactionFlag { get { lock (sync) return localCombatFlag; } }
    public bool CombatCaptureReady { get { lock (sync) return HasConfirmedContext && actorJoinStartedAt is null; } }

    private void JoinCombatMeter(JoinResponse packet, DateTime at)
    {
        var owner = $"{actors.ServerId}:{actors.CharacterId}:{actors.CharacterName}";
        if (combatOwner != owner) localCombatFlag = null;
        combatOwner = owner;
        combatFlags.Clear();
        CombatMeter.SetOwner(owner, at);
        var connection = ActorConnection(packet.ConnectionId, packet.MessageIdentity);
        foreach (var entry in pendingCombatFlags)
            if (entry.Key.Connection == connection && entry.Value.At <= at && at - entry.Value.At <= PendingActorLifetime)
                BindCombatFlag(entry.Key.ObjectId, entry.Value.Flag, entry.Value.At);
        pendingCombatFlags.Clear();
    }

    // Desktop-only faction metadata adapter over the shared decoded envelope. There is no second
    // Photon parser, no inferred guild affiliation, and no change to Activity's persisted schema.
    // NewCharacter[53]: flag; ChangeFlaggingFinished[0,1]: object id, flag.
    // Missing fields remain unknown. A neutral/hostile flag is never a city faction.
    public void ObserveCombatFaction(EventPacket packet)
    {
        lock (sync)
        {
            if (disposed || packet.EventCode is not ((int)EventCodes.NewCharacter or (int)EventCodes.ChangeFlaggingFinished)
                || !Accept(packet.MessageIdentity?.OriginKey, "CombatFaction", packet.CapturedAt)) return;
            var flagKey = packet.EventCode == (int)EventCodes.NewCharacter ? (byte)53 : (byte)1;
            if (!TryCombatInteger(packet.Parameters, 0, out var id) || id <= 0
                || !TryCombatInteger(packet.Parameters, flagKey, out var flag)
                || flag is not (>= 0 and <= 6 or 255)) return;
            var at = Utc(packet.CapturedAt);
            if (!HasConfirmedContext || actorJoinStartedAt is not null)
            {
                if (packet.EventCode != (int)EventCodes.NewCharacter
                    || actorJoinStartedAt is { } started && at < started
                    || ActorConnection(packet.ConnectionId, packet.MessageIdentity) is not { } connection) return;
                foreach (var key in pendingCombatFlags.Where(x => at - x.Value.At > PendingActorLifetime).Select(x => x.Key).ToArray())
                    pendingCombatFlags.Remove(key);
                var keyForActor = new PendingActorKey(connection, id);
                if (pendingCombatFlags.Count < MaximumPendingActors
                    && (!pendingCombatFlags.TryGetValue(keyForActor, out var previous) || at >= previous.At))
                    pendingCombatFlags[keyForActor] = ((int)flag, at);
                return;
            }
            if (at < mapEnteredAt || !SameConnection(packet.ConnectionId, packet.MessageIdentity)) return;
            BindCombatFlag(id, (int)flag, at);
        }
    }

    private void BindCombatFlag(long id, int flag, DateTime at)
    {
        var actor = actors.Resolve(id);
        if (actor.Kind != ActivityActorKind.Player) return;
        if (combatFlags.TryGetValue(id, out var previous) && previous.Lifetime == actor.LifetimeId && previous.At > at) return;
        if (actor.Relationship == ActorRelationship.Self) localCombatFlag = flag;
        if (combatFlags.Count >= 4096 && !combatFlags.ContainsKey(id)) return;
        combatFlags[id] = (actor.LifetimeId, flag, at);
    }

    private void SuspendCombatFaction()
    {
        localCombatFlag = null;
        combatFlags.Clear();
        pendingCombatFlags.Clear();
    }

    private static bool TryCombatInteger(IReadOnlyDictionary<byte, object> values, byte key, out long number)
    {
        number = 0;
        if (!values.TryGetValue(key, out var value)) return false;
        switch (value)
        {
            case byte v: number = v; return true;
            case short v: number = v; return true;
            case int v: number = v; return true;
            case long v: number = v; return true;
            default: return false;
        }
    }

    private CombatMeterActor CombatActor(ActivityActor actor)
    {
        var flag = actor.Relationship == ActorRelationship.Self ? localCombatFlag
            : actor.ObjectId is { } id && combatFlags.TryGetValue(id, out var binding)
                && binding.Lifetime == actor.LifetimeId ? binding.Flag : (int?)null;
        return new(actor.Id, actor.Name, actor.Kind, actor.Relationship,
            localCombatFlag is >= 1 and <= 6 && flag == localCombatFlag);
    }

    private void ObserveCombatHealth(long? source, long target, double delta, DateTime at)
    {
        if (!HasConfirmedContext || actorJoinStartedAt is not null) return;
        CombatMeter.Observe(at, delta, CombatActor(actors.Resolve(source)), CombatActor(actors.Resolve(target)));
    }
}
