using AlbionDataAvalonia.Activity.Models;
using AlbionDataAvalonia.Locations;
using AlbionDataAvalonia.Network.Events;
using AlbionDataAvalonia.Shared;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AlbionDataAvalonia.Activity;

// The game tells the client about the resource nodes around the character. The live map keeps the ones seen on the
// current map, so a node passed a while ago still shows, until it is gathered out or the character leaves the map.
// While the map's resources are turned on, open-world nodes are also remembered by map and position between visits,
// so the live map shows what stood where last time; a remembered node is what was there when last seen.
public sealed partial class ActivityTrackerService
{
    private const int MaxResourceNodes = 4000;
    // A remembered node this close to a live one is that node.
    private const double RememberedSameSpot = 1.5;
    private readonly Dictionary<long, ActivityResourceNode> resourceNodes = new();
    private readonly ActivityResourceNodeMemory resourceMemory = new(Path.Combine(AppData.DataDirectoryPath, "resource-nodes.json"));
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
        Remember(resourceNodes[id], gatheredOut: false);
        liveMapVersion++;
    }

    /// <summary>Forgets the resource nodes remembered on the current map, for when they no longer match the game.</summary>
    public void ForgetRememberedResources()
    {
        lock (sync)
        {
            if (RememberKey() is { } key && resourceMemory.Forget(key)) liveMapVersion++;
        }
    }

    private bool PlaceResourceNode(ResourceNodeSighting node, DateTime at)
    {
        if (resourceNodes.Count >= MaxResourceNodes && !resourceNodes.ContainsKey(node.Id))
            resourceNodes.Remove(resourceNodes.Values.MinBy(x => x.SeenAtUtc)!.Id);
        var placed = resourceNodes[node.Id] = new ActivityResourceNode(node.Id, node.Family, node.Tier, node.Enchantment, node.X, node.Y,
            node.Charges, at, ActivityResourceNodePackets.ItemUniqueName(node.Family, node.Tier, node.Enchantment));
        Remember(placed, gatheredOut: false);
        return true;
    }

    // A node gathered out is gone until it grows back, when the game sends it again.
    private bool ChangeResourceNode(ResourceNodeChange change, DateTime at)
    {
        if (!resourceNodes.TryGetValue(change.Id, out var node)) return false;
        if (change.Charges <= 0)
        {
            Remember(node with { Charges = 0, SeenAtUtc = at }, gatheredOut: true);
            return resourceNodes.Remove(change.Id);
        }

        var enchantment = change.Enchantment ?? node.Enchantment;
        var changed = resourceNodes[change.Id] = node with
        {
            Charges = change.Charges,
            Enchantment = enchantment,
            SeenAtUtc = at,
            ItemUniqueName = ActivityResourceNodePackets.ItemUniqueName(node.Family, node.Tier, enchantment)
        };
        Remember(changed, gatheredOut: false);
        return true;
    }

    // Object ids belong to one map, so a new map starts with none.
    private void SyncResourceNodes()
    {
        if (resourceNodesEpoch == mapEpoch) return;
        resourceNodesEpoch = mapEpoch;
        resourceNodes.Clear();
    }

    // The live nodes, then the remembered ones not standing where a live one does.
    private ActivityResourceNode[] CurrentResourceNodes()
    {
        SyncResourceNodes();
        if (RememberKey() is not { } key) return resourceNodes.Values.ToArray();
        var live = new Dictionary<(int, int), List<ActivityResourceNode>>();
        foreach (var node in resourceNodes.Values)
        {
            var cell = RememberedCell(node.X, node.Y);
            if (!live.TryGetValue(cell, out var list)) live[cell] = list = new List<ActivityResourceNode>();
            list.Add(node);
        }

        var shown = resourceNodes.Values.ToList();
        foreach (var node in resourceMemory.Remembered(key))
        {
            var (cellX, cellY) = RememberedCell(node.X, node.Y);
            var taken = false;
            for (var dx = -1; dx <= 1 && !taken; dx++)
                for (var dy = -1; dy <= 1 && !taken; dy++)
                    taken = live.TryGetValue((cellX + dx, cellY + dy), out var nearby)
                        && nearby.Any(x => Math.Abs(x.X - node.X) <= RememberedSameSpot && Math.Abs(x.Y - node.Y) <= RememberedSameSpot);
            if (!taken) shown.Add(node);
        }

        return shown.ToArray();
    }

    private static (int, int) RememberedCell(double x, double y) =>
        ((int)Math.Floor(x / RememberedSameSpot), (int)Math.Floor(y / RememberedSameSpot));

    private void Remember(ActivityResourceNode node, bool gatheredOut)
    {
        if (RememberKey() is { } key)
            resourceMemory.Note(key, node.Family, node.Tier, node.Enchantment, node.X, node.Y, node.Charges, gatheredOut, node.SeenAtUtc);
    }

    // Only open-world maps keep their nodes between visits: an instance is laid out afresh each time. The server is
    // part of the key, as each server's world is its own. Nothing is remembered while the map's resources are off.
    private string? RememberKey() =>
        map is { } current && current.Kind == ActivityMapKind.OpenWorld && !current.RawLocationId.Contains('@')
            && settings.UserSettings.MapShowResources
            ? $"{actors.ServerId?.ToString(CultureInfo.InvariantCulture) ?? "0"}:{current.RawLocationId}" : null;

    private void FlushRememberedResources(DateTime now, bool force = false) => resourceMemory.Flush(now, force);
}
