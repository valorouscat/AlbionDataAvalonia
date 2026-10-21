using System;
using System.Collections.Generic;
using System.Linq;

namespace AlbionDataAvalonia.Maps;

/// <summary>
/// The camp slots of the Mists layouts, which AlbionWorld's map features leave out. Each slot gets a section the game
/// picks when the instance opens; when that is a PvE camp, its chest stands within the slot's radius of its centre.
/// </summary>
internal static partial class MistsCampSlots
{
    public static IReadOnlyList<MapChestSpawn> For(string layout) =>
        Slots.TryGetValue(layout, out var slots)
            ? slots.Select(slot => new MapChestSpawn(slot.Medium ? "Medium camp chest" : "Small camp chest", slot.Medium ? 2 : 1,
                new MapPoint(slot.X, slot.Z), slot.Medium ? MediumChestRadius : SmallChestRadius)).ToArray()
            : [];
}
