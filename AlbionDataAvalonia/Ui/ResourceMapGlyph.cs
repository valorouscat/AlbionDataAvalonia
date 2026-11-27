using AlbionDataAvalonia.Activity;
using AlbionDataAvalonia.Items;
using Avalonia;
using Avalonia.Media;
using System;
using System.Globalization;

namespace AlbionDataAvalonia.Ui;

/// <summary>The game's item tile at the actual resource position, with its own tier and enchantment artwork.</summary>
internal static class ResourceMapGlyph
{
    public const double HalfSize = 13;

    public static void Draw(DrawingContext context, Point at, string family, int tier, int? enchantment,
        bool mob, bool possible, Action loaded)
    {
        var box = new Rect(at.X - HalfSize, at.Y - HalfSize, HalfSize * 2, HalfSize * 2);
        var image = tier > 0
            ? LazyItemImage.ForDrawing(ActivityResourceNodePackets.ItemUniqueName(family, tier, enchantment ?? 0), 1, loaded)
            : null;
        if (image is not null) context.DrawImage(image, new Rect(image.Size), box);
        else
        {
            context.DrawRectangle(UiPalette.Brush("UiMapCountColor"),
                UiPalette.Pen(UiPalette.Color("UiMapMarkerOutlineColor"), 1), box, 3, 3);
            var label = ChartDrawing.Text(tier > 0 ? $"{tier}.{enchantment ?? 0}" : "?", 9,
                UiPalette.Color("UiTextColor"), FontWeight.SemiBold);
            label.Draw(context, new Point(at.X - label.Width / 2, at.Y - label.Height / 2));
        }

        // Static candidates disclose no current enchantment. A question badge covers the tier when it is unknown.
        if (possible)
        {
            var badge = new Rect(box.Left - 1, box.Top - 1, 10, 11);
            context.DrawRectangle(UiPalette.Brush("UiMapCountColor"), null, badge, 2, 2);
            var text = ChartDrawing.Text("?", 9, UiPalette.Color("UiTextColor"), FontWeight.Bold);
            text.Draw(context, new Point(badge.Center.X - text.Width / 2, badge.Center.Y - text.Height / 2));
        }
        if (!mob) return;
        var centre = new Point(box.Left + 2, box.Bottom - 2);
        var diamond = new StreamGeometry();
        using (var path = diamond.Open())
        {
            path.BeginFigure(new Point(centre.X, centre.Y - 4), true);
            path.LineTo(new Point(centre.X + 4, centre.Y));
            path.LineTo(new Point(centre.X, centre.Y + 4));
            path.LineTo(new Point(centre.X - 4, centre.Y));
            path.EndFigure(true);
        }
        context.DrawGeometry(UiPalette.Brush("UiMapCountColor"),
            UiPalette.Pen(UiPalette.Color("UiMapMarkerEdgeColor"), 1), diamond);
    }
}
