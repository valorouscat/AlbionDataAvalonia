using AlbionDataAvalonia.Activity;
using AlbionDataAvalonia.Items;
using AlbionDataAvalonia.Maps;
using Avalonia;
using Avalonia.Media;
using System;
using System.Collections.Generic;

namespace AlbionDataAvalonia.Ui;

/// <summary>Family pictograms beside the map's existing tier/enchantment marks. Symbols are original vector art.</summary>
internal static class ResourceMapGlyph
{
    private static readonly Dictionary<string, Geometry> Symbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Wood"] = Geometry.Parse("M2,4 L11,1 L15,5 L6,8 Z M1,10 L10,7 L15,11 L6,15 Z"),
        ["Stone"] = Geometry.Parse("M1,10 L3,5 L8,4 L11,8 L9,13 L3,14 Z M9,3 L12,1 L15,5 L14,10 L12,9 Z"),
        ["Ore"] = Geometry.Parse("M1,10 L4,4 L7,7 L10,1 L14,5 L15,12 L9,15 L3,14 Z"),
        ["Fiber"] = Geometry.Parse("M7,15 L7,10 C1,10 1,6 1,4 C5,4 7,5 7,8 L7,5 C4,2 6,0 8,0 C11,2 10,4 9,6 L9,10 C10,6 13,5 15,5 C15,10 13,12 9,12 L9,15 Z"),
        ["Hide"] = Geometry.Parse("M3,1 L6,3 L10,3 L13,1 L15,4 L12,6 L12,10 L15,12 L13,15 L10,13 L6,13 L3,15 L1,12 L4,10 L4,6 L1,4 Z")
    };

    public static void Draw(DrawingContext context, Point at, string family, int tier, MapResourceIconStyle style, Action loaded)
    {
        if (style == MapResourceIconStyle.Classic) return;
        var size = style == MapResourceIconStyle.ItemArt ? 22 : 16;
        var box = new Rect(at.X - size - 10, at.Y - size / 2d, size, size);
        if (style == MapResourceIconStyle.ItemArt && tier > 0
            && LazyItemImage.ForDrawing(ActivityResourceNodePackets.ItemUniqueName(family, tier, 0), 1, loaded) is { } image)
        {
            context.DrawImage(image, new Rect(image.Size), box);
            return;
        }

        if (!Symbols.TryGetValue(family, out var shape)) return;
        context.DrawRectangle(UiPalette.Brush("UiMapCountColor"), null, box.Inflate(1), 3, 3);
        using var transform = context.PushTransform(Matrix.CreateScale(size / 16d, size / 16d) * Matrix.CreateTranslation(box.X, box.Y));
        context.DrawGeometry(UiPalette.Brush(ActivityPalette.Resource(family)),
            UiPalette.Pen(UiPalette.Color("UiMapMarkerEdgeColor"), 0.6, join: PenLineJoin.Round), shape);
    }
}
