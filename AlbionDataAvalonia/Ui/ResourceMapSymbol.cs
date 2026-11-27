using Avalonia;
using Avalonia.Media;

namespace AlbionDataAvalonia.Ui;

/// <summary>Small resource silhouettes with a few solid facets, cached independently of map redraws.</summary>
internal static class ResourceMapSymbol
{
    private sealed record Layer(string Token, Geometry Shape);
    private static Layer Shape(string token, string path) => new(token, Geometry.Parse(path));
    private static Layer Ellipse(string token, double x, double y, double width, double height) =>
        new(token, new EllipseGeometry(new Rect(x, y, width, height)));

    private static readonly Layer[] Wood =
    [
        Shape("UiResourceWoodColor", "M2,9 L10,1 C13,0 17,4 15,7 L7,15 Z"),
        Ellipse("UiMapWoodCutColor", 1.2, 8.2, 7, 7),
        Ellipse("UiMapWoodGrainColor", 3.1, 10.1, 3.2, 3.2),
        Shape("UiMapWoodGrainColor", "M8,9 L13,4 L14,5 L9,10 Z")
    ];
    private static readonly Layer[] Stone =
    [
        Shape("UiResourceStoneColor", "M1,5 L10,1 L15,5 L15,12 L6,15 L1,11 Z"),
        Shape("UiMapStoneShadeColor", "M1,5 L6,8 L6,15 L1,11 Z"),
        Shape("UiMapStoneFaceColor", "M6,8 L15,5 L15,12 L6,15 Z")
    ];
    private static readonly Layer[] Fiber =
    [
        Shape("UiResourceFiberColor", "M7,15 L7,10 C1,11 0,7 0,3 C5,3 7,5 7,8 L7,5 C4,2 6,0 8,0 C11,2 10,4 9,6 L9,10 C10,6 13,5 16,5 C16,10 13,12 9,12 L9,15 Z"),
        Shape("UiMapFiberVeinColor", "M2.6,6.4 L3.4,5.6 L7.4,9.6 L6.6,10.4 Z M8.6,11.6 L12.6,7.6 L13.4,8.4 L9.4,12.4 Z")
    ];
    private static readonly Layer[] Hide =
    [
        Shape("UiResourceHideColor", "M3,0 L6,3 L10,3 L13,0 L16,3 L13,6 L15,8 L12,9 L15,13 L12,16 L9,13 L7,14 L4,16 L1,13 L4,9 L1,8 L3,6 L0,3 Z"),
        Shape("UiMapHideFoldColor", "M7,4 L9,5 L8,9 L10,12 L7,11 L6,8 Z")
    ];
    private static readonly Layer[] Ore =
    [
        Shape("UiResourceOreColor", "M0,11 L2,5 L7,2 L12,3 L16,10 L13,15 L4,15 Z"),
        Shape("UiMapOreFacetColor", "M4,8 L7,5 L9,8 L6,11 Z M10,9 L12,6 L14,10 L12,12 Z"),
        Shape("UiMapOreGlintColor", "M4,8 L7,5 L6,8 L6,11 Z M10,9 L12,6 L12,10 Z")
    ];

    public static void Draw(DrawingContext context, Rect box, string family)
    {
        var layers = family switch
        {
            "Wood" => Wood,
            "Stone" => Stone,
            "Fiber" => Fiber,
            "Hide" => Hide,
            "Ore" => Ore,
            _ => null
        };
        if (layers is null) return;
        using var transform = context.PushTransform(Matrix.CreateScale(box.Width / 16, box.Height / 16) *
            Matrix.CreateTranslation(box.X, box.Y));
        foreach (var layer in layers) context.DrawGeometry(UiPalette.Brush(layer.Token), null, layer.Shape);
    }
}
