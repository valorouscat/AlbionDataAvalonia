using AlbionDataAvalonia.Activity.Models;
using AlbionDataAvalonia.Network.Events;
using AlbionDataAvalonia.Shared;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AlbionDataAvalonia.Activity;

// The game tells the client about the resource nodes around the character. The live map keeps the ones seen on the
// current map, so a node passed a while ago still shows, until it is gathered out or the character leaves the map.
// Nothing here is stored.
public sealed partial class ActivityTrackerService
{
    private const int MaxResourceNodes = 4000;
    private readonly Dictionary<long, ActivityResourceNode> resourceNodes = new();
    private long resourceNodesEpoch = -1;
    // One note per map when a harvest shows a node's packet was read wrongly.
    private long resourceMismatchEpoch = -1;

    /// <summary>A harvestable node event (NewHarvestableObject, NewSimpleHarvestableObjectList or HarvestableChangeState).</summary>
    public void ObserveResourceNodes(DebugEventProbeEvent packet, EventCodes code)
    {
        lock (sync)
        {
            if (disposed || map is null || captureSuspended) return;
            var at = Utc(packet.CapturedAt);
            if (at < mapEnteredAt || !SameConnection(packet.ConnectionId, packet.MessageIdentity)) return;
            SyncResourceNodes();
            var changed = false;
            switch (code)
            {
                case EventCodes.NewHarvestableObject:
                    if (ActivityResourceNodePackets.ReadNode(packet.Parameters) is { } node) changed = PlaceResourceNode(node, at);
                    break;
                case EventCodes.NewSimpleHarvestableObjectList:
                    foreach (var listed in ActivityResourceNodePackets.ReadNodeList(packet.Parameters)) changed |= PlaceResourceNode(listed, at);
                    break;
                case EventCodes.HarvestableChangeState:
                    if (ActivityResourceNodePackets.ReadChange(packet.Parameters) is { } change) changed = ChangeResourceNode(change, at);
                    break;
            }

            if (changed) liveMapVersion++;
        }
    }

    // A harvest names the resource it gave, which is certain; the node's packet is only read by its layout. The node
    // takes the harvest's kind, tier and enchantment, and a difference is noted once a map for the diagnostics.
    internal void NoteHarvestedNode(long? nodeId, ActivityResourceIdentity identity, string? itemUniqueName)
    {
        if (nodeId is not { } id || identity is not { Family: { } family, Tier: { } tier } || family is "Fish" or "Seaweed") return;
        SyncResourceNodes();
        if (!resourceNodes.TryGetValue(id, out var node)) return;
        var enchantment = identity.Enchantment ?? 0;
        if (node.Family == family && node.Tier == tier && node.Enchantment == enchantment) return;
        if (resourceMismatchEpoch != mapEpoch)
        {
            resourceMismatchEpoch = mapEpoch;
            Log.Information("A resource node read as {NodeFamily} T{NodeTier}.{NodeEnchantment} gave {Item}", node.Family, node.Tier,
                node.Enchantment, itemUniqueName);
            DiagnosticNote?.Invoke($"Resource node read as {node.Family} T{node.Tier}.{node.Enchantment} gave {itemUniqueName}");
        }

        resourceNodes[id] = node with
        {
            Family = family,
            Tier = tier,
            Enchantment = enchantment,
            ItemUniqueName = ActivityResourceNodePackets.ItemUniqueName(family, tier, enchantment)
        };
        liveMapVersion++;
    }

    private bool PlaceResourceNode(ResourceNodeSighting node, DateTime at)
    {
        if (resourceNodes.Count >= MaxResourceNodes && !resourceNodes.ContainsKey(node.Id))
            resourceNodes.Remove(resourceNodes.Values.MinBy(x => x.SeenAtUtc)!.Id);
        resourceNodes[node.Id] = new ActivityResourceNode(node.Id, node.Family, node.Tier, node.Enchantment, node.X, node.Y, node.Charges, at,
            ActivityResourceNodePackets.ItemUniqueName(node.Family, node.Tier, node.Enchantment));
        return true;
    }

    // A node gathered out is gone until it grows back, when the game sends it again.
    private bool ChangeResourceNode(ResourceNodeChange change, DateTime at)
    {
        if (!resourceNodes.TryGetValue(change.Id, out var node)) return false;
        if (change.Charges <= 0) return resourceNodes.Remove(change.Id);
        var enchantment = change.Enchantment ?? node.Enchantment;
        resourceNodes[change.Id] = node with
        {
            Charges = change.Charges,
            Enchantment = enchantment,
            SeenAtUtc = at,
            ItemUniqueName = ActivityResourceNodePackets.ItemUniqueName(node.Family, node.Tier, enchantment)
        };
        return true;
    }

    // Object ids belong to one map, so a new map starts with none.
    private void SyncResourceNodes()
    {
        if (resourceNodesEpoch == mapEpoch) return;
        resourceNodesEpoch = mapEpoch;
        resourceNodes.Clear();
    }

    private ActivityResourceNode[] CurrentResourceNodes()
    {
        SyncResourceNodes();
        return resourceNodes.Values.ToArray();
    }
}
