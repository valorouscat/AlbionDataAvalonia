using Avalonia;
using Avalonia.Media;
using System.Globalization;

namespace AlbionDataAvalonia.Ui;

/// <summary>A square resource plate: silhouette above separate tier and enchantment badges.</summary>
internal static class ResourceMapGlyph
{
    public const double Width = 34;
    public const double Height = 34;
    public const double HalfWidth = Width / 2;
    public const double HalfHeight = Height / 2;
    public const double OuterHalfSize = HalfWidth + 2;

    public static void Draw(DrawingContext context, Point at, string family, int tier, int? enchantment,
        bool mob, bool possible)
    {
        var box = new Rect(at.X - HalfWidth, at.Y - HalfHeight, Width, Height);
        var digit = UiPalette.Color("UiMapResourceEdgeColor");
        var muted = UiPalette.Color("UiMapResourceMutedColor");
        var plate = UiPalette.Color("UiMapResourcePlateColor");
        var neutral = UiPalette.Blend(plate, muted, 0.22);
        context.DrawRectangle(UiPalette.Brush(plate), UiPalette.Pen(digit, 2), box, 4, 4);
        ResourceMapSymbol.Draw(context, new Rect(at.X - 8, box.Y + 2, 16, 16), family);

        var tierBox = new Rect(at.X - 12, box.Y + 20, 10, 12);
        var enchantBox = new Rect(at.X - 1, box.Y + 20, 13, 12);
        var level = enchantment.GetValueOrDefault();
        var knownEnchantment = !possible && enchantment is >= 0 and <= 4;
        context.DrawRectangle(UiPalette.Brush(tier > 0 ? ActivityPalette.ResourceTier(tier) : neutral), null, tierBox, 2, 2);
        context.DrawRectangle(UiPalette.Brush(knownEnchantment && level > 0
            ? ActivityPalette.Enchantment(level) : neutral), null, enchantBox, 2, 2);

        var tierText = ChartDrawing.Text(tier > 0 ? tier.ToString(CultureInfo.InvariantCulture) : "?", 11,
            tier > 0 ? digit : muted, FontWeight.SemiBold);
        tierText.Draw(context, new Point(tierBox.Center.X - tierText.Width / 2, tierBox.Center.Y - tierText.Height / 2));
        // Static candidates never claim a current enchantment, even when their possible tier is known.
        var enchantText = ChartDrawing.Text(knownEnchantment ? $".{level}" : ".?", 11,
            knownEnchantment && level > 0 ? digit : muted, FontWeight.SemiBold);
        enchantText.Draw(context, new Point(enchantBox.Center.X - enchantText.Width / 2, enchantBox.Center.Y - enchantText.Height / 2));

        if (!mob) return;
        // The mob diamond sits beside the silhouette, leaving both numeric badges unobstructed.
        var centre = new Point(box.Left + 4, box.Top + 14);
        var diamond = new StreamGeometry();
        using (var path = diamond.Open())
        {
            path.BeginFigure(new Point(centre.X, centre.Y - 2.5), true);
            path.LineTo(new Point(centre.X + 2.5, centre.Y));
            path.LineTo(new Point(centre.X, centre.Y + 2.5));
            path.LineTo(new Point(centre.X - 2.5, centre.Y));
            path.EndFigure(true);
        }
        context.DrawGeometry(UiPalette.Brush(plate), UiPalette.Pen(UiPalette.Color("UiMapResourceInkColor"), 1), diamond);
    }
}
