using AlbionDataAvalonia.Ui;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AlbionDataAvalonia.Views.Controls;

/// <summary>The production glyphs in the UI kit, including unobserved spawn and living-mob states.</summary>
public sealed class ResourceMapSamples : Control
{
    public override void Render(DrawingContext context)
    {
        string[] families = ["Wood", "Stone", "Fiber", "Hide", "Ore"];
        for (var index = 0; index < families.Length; index++)
        {
            var x = 40 + index * 100;
            ResourceMapGlyph.Draw(context, new Point(x, 19), families[index], 6, 2, false, false);
            ChartDrawing.DrawText(context, families[index], new Point(x, 40), 11, UiPalette.Text, TextAlign.Center);
        }
        for (var tier = 1; tier <= 8; tier++)
            ResourceMapGlyph.Draw(context, new Point(40 + (tier - 1) * 64, 81), families[(tier - 1) % families.Length], tier, tier < 4 ? 0 : (tier - 1) % 5, false, false);
        ResourceMapGlyph.Draw(context, new Point(40, 126), "Hide", 6, 2, true, false);
        ResourceMapGlyph.Draw(context, new Point(104, 126), "Wood", 6, null, false, true);
        ResourceMapGlyph.Draw(context, new Point(168, 126), "Ore", 0, null, false, true);
        ChartDrawing.DrawText(context, "Mob · possible spawn · unknown tier/enchantment", new Point(208, 120), 11, UiPalette.Muted);
    }
}
