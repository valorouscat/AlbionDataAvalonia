using Avalonia;
using Avalonia.Media;
using System.Globalization;

namespace AlbionDataAvalonia.Ui;

/// <summary>A colored resource silhouette and explicit tier/enchantment on an opaque, compact plate.</summary>
internal static class ResourceMapGlyph
{
    public const double Width = 46;
    public const double Height = 22;
    public const double HalfWidth = Width / 2;
    public const double HalfHeight = Height / 2;
    public const double OuterHalfSize = HalfWidth + 2;

    public static void Draw(DrawingContext context, Point at, string family, int tier, int? enchantment,
        bool mob, bool possible)
    {
        var box = new Rect(at.X - HalfWidth, at.Y - HalfHeight, Width, Height);
        var ink = UiPalette.Color("UiMapResourceInkColor");
        context.DrawRectangle(UiPalette.Brush("UiMapResourcePlateColor"),
            UiPalette.Pen(UiPalette.Color("UiMapResourceEdgeColor"), 2), box, 4, 4);
        // A full colored rim reads at a glance; only observed enchantments may claim a color.
        if (!possible && enchantment is >= 1 and <= 4)
            context.DrawRectangle(null, UiPalette.Pen(ActivityPalette.Enchantment(enchantment.Value), 2), box, 4, 4);
        context.DrawRectangle(UiPalette.Brush(ActivityPalette.ResourceTier(tier)), null,
            new Rect(box.X + 1, box.Y + 4, 2, 14), 1, 1);
        ResourceMapSymbol.Draw(context, new Rect(box.X + 5, box.Y + 3, 16, 16), family);

        // A candidate never claims an observed enchantment; a mixed-tier spot also leaves its tier unknown.
        var tierText = ChartDrawing.Text(tier > 0 ? tier.ToString(CultureInfo.InvariantCulture) : "?", 11, ink, FontWeight.SemiBold);
        tierText.Draw(context, new Point(box.X + 24, at.Y - tierText.Height / 2));
        var enchantText = ChartDrawing.Text(possible || enchantment is null ? ".?" : $".{enchantment.Value}", 11,
            possible || enchantment is null || enchantment == 0
                ? UiPalette.Color("UiMapResourceMutedColor") : ActivityPalette.Enchantment(enchantment.Value), FontWeight.SemiBold);
        enchantText.Draw(context, new Point(box.X + 31, at.Y - enchantText.Height / 2));

        if (!mob) return;
        // An outlined diamond distinguishes living resource mobs without replacing their resource silhouette.
        var centre = new Point(box.Left + 4, box.Bottom - 3);
        var diamond = new StreamGeometry();
        using (var path = diamond.Open())
        {
            path.BeginFigure(new Point(centre.X, centre.Y - 2.5), true);
            path.LineTo(new Point(centre.X + 2.5, centre.Y));
            path.LineTo(new Point(centre.X, centre.Y + 2.5));
            path.LineTo(new Point(centre.X - 2.5, centre.Y));
            path.EndFigure(true);
        }
        context.DrawGeometry(UiPalette.Brush("UiMapResourcePlateColor"), UiPalette.Pen(ink, 1), diamond);
    }
}
