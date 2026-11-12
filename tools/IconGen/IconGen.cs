#:package Avalonia.Skia@12.0.4
#:package Avalonia.Headless@12.0.4

// Generates Hymnal's application icon: an open hymnal with a single note silhouetted against a synthwave sun.
// The drawing below is the design's source of truth. It writes hymnal.ico (16–256 px), hymnal-256.png and
// hymnal.svg (a preview of the same shapes) into src/Hymnal/Assets.
//
// Run from the repo root:  dotnet run tools/IconGen/IconGen.cs

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ShapePath = Avalonia.Controls.Shapes.Path;

AppBuilder.Configure<IconApp>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

var assets = Path.GetFullPath(args.Length > 0 ? args[0] : "src/Hymnal/Assets");
if (!Directory.Exists(assets))
    throw new DirectoryNotFoundException($"Run from the Hymnal repo root (missing {assets}).");

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var pngs = new List<byte[]>();
foreach (var size in sizes)
{
    var canvas = Design.Build(detailed: size >= 32);
    var scale = size / Design.Grid;
    using var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96 * scale, 96 * scale));
    canvas.Measure(new Size(Design.Grid, Design.Grid));
    canvas.Arrange(new Rect(0, 0, Design.Grid, Design.Grid));
    bitmap.Render(canvas);

    using var stream = new MemoryStream();
    bitmap.Save(stream);
    pngs.Add(stream.ToArray());
    if (size == 256)
        File.WriteAllBytes(Path.Combine(assets, "hymnal-256.png"), stream.ToArray());
}

File.WriteAllBytes(Path.Combine(assets, "hymnal.ico"), Ico.Pack(sizes, pngs));
File.WriteAllText(Path.Combine(assets, "hymnal.svg"), Design.Svg());
Console.WriteLine($"Wrote hymnal.ico ({string.Join(", ", sizes)} px), hymnal-256.png and hymnal.svg to {assets}");

internal sealed class IconApp : Application;

internal static class Design
{
    public const double Grid = 256;

    // Palette: the app's synthwave theme.
    private const string Night = "#0C0A20";
    private const string Dusk = "#2B1466";
    private const string Edge = "#4A2596";
    private const string SunTop = "#F5C842";
    private const string SunMid = "#FF6B35";
    private const string SunLow = "#E91E8C";
    private const string Silhouette = "#160A36";
    private const string PageLight = "#F6F0FF";
    private const string PageShade = "#C9B4F2";
    private const string CoverLight = "#B469F0";
    private const string CoverDark = "#6B2FB3";
    private const string Spine = "#7A3BC8";

    private const string Tile = "M64,16 H192 A48,48 0 0 1 240,64 V192 A48,48 0 0 1 192,240 H64 A48,48 0 0 1 16,192 V64 A48,48 0 0 1 64,16 Z";
    private const string Sun = "M128,44 A66,66 0 1 1 127.99,44 Z";
    private static readonly (double Y, double Height)[] SunStripes = [(116, 5), (130, 7), (145, 9)];
    private const string NoteHead = "M97,106 C97,96 109,89 120,91 C129,93 132,101 128,108 C123,117 110,121 102,118 C98,116 97,111 97,106 Z";
    private const string NoteStem = "M122,100 L122,50 L130,50 L130,100 Z";
    private const string NoteFlag = "M130,50 C133,66 156,70 158,90 C159,100 154,108 149,113 C152,99 149,86 130,79 Z";
    private const string Covers = "M128,160 C105,146 74,143 42,150 L42,212 C74,205 105,208 128,224 C151,208 182,205 214,212 L214,150 C182,143 151,146 128,160 Z";
    private const string LeftPage = "M126,158 C106,145 79,142 52,147 L52,203 C79,198 106,201 126,214 Z";
    private const string RightPage = "M130,158 C150,145 177,142 204,147 L204,203 C177,198 150,201 130,214 Z";
    private const string Lines = "M66,162 C82,159 98,160 112,166 M66,176 C82,173 98,174 112,180 M66,190 C82,187 98,188 112,194 M144,166 C158,160 174,159 190,162 M144,180 C158,174 174,173 190,176 M144,194 C158,188 174,187 190,190";

    public static Canvas Build(bool detailed)
    {
        var canvas = new Canvas { Width = Grid, Height = Grid };

        canvas.Children.Add(Shape(Tile, Vertical(Dusk, Night), stroke: Edge, thickness: 3));

        // The sun, cut by horizon stripes (small sizes keep one stripe so it still reads as a sunset).
        Geometry sun = Geometry.Parse(Sun);
        foreach (var (y, height) in detailed ? SunStripes : SunStripes[1..2])
            sun = new CombinedGeometry(GeometryCombineMode.Exclude, sun, new RectangleGeometry(new Rect(40, y, 176, height)));
        canvas.Children.Add(new ShapePath { Data = sun, Fill = Vertical(SunTop, SunMid, SunLow) });

        // A single eighth note, silhouetted against the sun.
        foreach (var part in new[] { NoteHead, NoteStem, NoteFlag })
            canvas.Children.Add(Shape(part, Solid(Silhouette)));

        // The open hymnal.
        canvas.Children.Add(Shape(Covers, Vertical(CoverLight, CoverDark)));
        canvas.Children.Add(Shape(LeftPage, Horizontal(PageShade, PageLight)));
        canvas.Children.Add(Shape(RightPage, Horizontal(PageLight, PageShade)));
        if (detailed)
            canvas.Children.Add(new ShapePath { Data = Geometry.Parse(Lines), Stroke = Solid("#A58BD6"), StrokeThickness = 3, StrokeLineCap = PenLineCap.Round });
        canvas.Children.Add(new ShapePath { Data = Geometry.Parse("M128,158 L128,216"), Stroke = Solid(Spine), StrokeThickness = 4, StrokeLineCap = PenLineCap.Round });

        return canvas;
    }

    public static string Svg()
    {
        var stripes = string.Concat(SunStripes.Select(s => $"<rect x=\"40\" y=\"{s.Y}\" width=\"176\" height=\"{s.Height}\" fill=\"black\"/>"));
        return $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256">
              <!-- Generated by tools/IconGen/IconGen.cs; edit the generator, not this file. -->
              <defs>
                <linearGradient id="tile" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{Dusk}"/><stop offset="1" stop-color="{Night}"/></linearGradient>
                <linearGradient id="sun" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{SunTop}"/><stop offset="0.5" stop-color="{SunMid}"/><stop offset="1" stop-color="{SunLow}"/></linearGradient>
                <linearGradient id="cover" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{CoverLight}"/><stop offset="1" stop-color="{CoverDark}"/></linearGradient>
                <linearGradient id="pageL" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="{PageShade}"/><stop offset="1" stop-color="{PageLight}"/></linearGradient>
                <linearGradient id="pageR" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="{PageLight}"/><stop offset="1" stop-color="{PageShade}"/></linearGradient>
                <mask id="horizon"><rect width="256" height="256" fill="white"/>{stripes}</mask>
              </defs>
              <path d="{Tile}" fill="url(#tile)" stroke="{Edge}" stroke-width="3"/>
              <path d="{Sun}" fill="url(#sun)" mask="url(#horizon)"/>
              <path d="{NoteHead}" fill="{Silhouette}"/>
              <path d="{NoteStem}" fill="{Silhouette}"/>
              <path d="{NoteFlag}" fill="{Silhouette}"/>
              <path d="{Covers}" fill="url(#cover)"/>
              <path d="{LeftPage}" fill="url(#pageL)"/>
              <path d="{RightPage}" fill="url(#pageR)"/>
              <path d="{Lines}" fill="none" stroke="#A58BD6" stroke-width="3" stroke-linecap="round"/>
              <path d="M128,158 L128,216" stroke="{Spine}" stroke-width="4" stroke-linecap="round"/>
            </svg>

            """;
    }

    private static ShapePath Shape(string data, IBrush fill, string? stroke = null, double thickness = 0) => new()
    {
        Data = Geometry.Parse(data),
        Fill = fill,
        Stroke = stroke is null ? null : Solid(stroke),
        StrokeThickness = thickness,
    };

    private static IBrush Solid(string color) => new SolidColorBrush(Color.Parse(color));

    private static IBrush Vertical(params string[] colors) => Gradient(new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(0, 1, RelativeUnit.Relative), colors);

    private static IBrush Horizontal(params string[] colors) => Gradient(new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(1, 0, RelativeUnit.Relative), colors);

    private static IBrush Gradient(RelativePoint start, RelativePoint end, string[] colors)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        for (var k = 0; k < colors.Length; k++)
            brush.GradientStops.Add(new GradientStop(Color.Parse(colors[k]), colors.Length == 1 ? 0 : (double)k / (colors.Length - 1)));
        return brush;
    }
}

/// <summary>Packs PNG images into a Windows .ico (PNG-compressed entries, supported since Windows Vista).</summary>
internal static class Ico
{
    public static byte[] Pack(IReadOnlyList<int> sizes, IReadOnlyList<byte[]> pngs)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);           // reserved
        writer.Write((ushort)1);           // type: icon
        writer.Write((ushort)sizes.Count); // image count

        var offset = 6 + 16 * sizes.Count;
        for (var k = 0; k < sizes.Count; k++)
        {
            writer.Write((byte)(sizes[k] >= 256 ? 0 : sizes[k])); // width (0 = 256)
            writer.Write((byte)(sizes[k] >= 256 ? 0 : sizes[k])); // height
            writer.Write((byte)0);   // palette colours
            writer.Write((byte)0);   // reserved
            writer.Write((ushort)1); // colour planes
            writer.Write((ushort)32);// bits per pixel
            writer.Write(pngs[k].Length);
            writer.Write(offset);
            offset += pngs[k].Length;
        }

        foreach (var png in pngs)
            writer.Write(png);

        writer.Flush();
        return stream.ToArray();
    }
}
