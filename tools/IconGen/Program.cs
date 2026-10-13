using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Svg;

// Generates every raster icon asset from the single source-of-truth SVG (flamingle.svg).
//
//   src/Flamingle.App/Assets/flamingle.png              256x256 PNG — the in-app logo
//   src/Flamingle.App/Assets/flamingle.ico              multi-resolution ICO (16..256) — windows + .exe ApplicationIcon
//   src/Flamingle.App/Assets/tray/flamingle-<n>.png     one exact-size tray frame per DPI scale (16 = 100% ... 48 = 300%)
//   src/Flamingle.App/Assets/tray/flamingle-dev-<n>.png the same with the pink recoloured green — marks a dev instance
//   landing-icon.png                                     512x512 PNG — the README header logo
//
// Re-run after editing flamingle.svg:  dotnet run --project tools/IconGen   (or tools/gen-icons.ps1),
// then commit the regenerated assets.
//
// Adapted from emuwren's IconGen (../emuwren/tools/IconGen). The tray frames are the addition: the
// notification area draws its icon at SM_CXSMICON for the taskbar's DPI, and any resampling there (by Avalonia
// or the shell) is what makes a tray icon blurry. Rendering the vector straight to each of those pixel sizes means
// the app can hand over a bitmap that is shown 1:1.

// Resolve the repo root from this tool's location so it works regardless of CWD.
string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
string svgPath = Path.Combine(repoRoot, "flamingle.svg");
if (!File.Exists(svgPath))
{
    Console.Error.WriteLine($"Source SVG not found: {svgPath}");
    return 1;
}

Console.WriteLine($"Source: {svgPath}");
var doc = Load(svgPath);
var devDoc = Load(svgPath);
// Dev instance: the flamingo is pink, so a dev build turns it green (the greens already in the SVG) to stand
// out. Keys are the source fills as lower-case #rrggbb.
Recolour(devDoc, new()
{
    ["#ff4fa3"] = Color.FromArgb(0x17, 0x97, 0x69),
    ["#ff9ccb"] = Color.FromArgb(0x88, 0xE1, 0xC5),
});

// The artwork may not fill its viewBox — there can be transparent margin. Crop to the actual drawn
// content, re-center it in a square, and scale that to fill the frame. PAD keeps a sliver of breathing
// room so anti-aliased edges don't clip.
const float PAD = 0.02f;
var fit = ComputeFit(doc, PAD);
float vbSide = doc.ViewBox.Width > 0 ? doc.ViewBox.Width : fit.Side;
Console.WriteLine($"Fit: content {fit.Box.Width:0.0}x{fit.Box.Height:0.0} cropped from {vbSide:0}x{vbSide:0} viewBox ({vbSide / fit.Side:0.00}x larger)");

// The .ico ships a true frame at each size so Windows never downscales at runtime (title bars and the
// taskbar ask for small frames at every DPI scale; large surfaces ask for 256).
int[] icoSizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

// SM_CXSMICON at 100%, 125%, 150%, 175%, 200%, 225%, 250% and 300%.
int[] traySizes = { 16, 20, 24, 28, 32, 36, 40, 48 };

string assetsDir = Path.Combine(repoRoot, "src", "Flamingle.App", "Assets");
WritePng(doc, fit, Path.Combine(assetsDir, "flamingle.png"), 256);
WritePng(doc, fit, Path.Combine(repoRoot, "landing-icon.png"), 512);
WriteIco(doc, fit, Path.Combine(assetsDir, "flamingle.ico"), icoSizes);

string trayDir = Path.Combine(assetsDir, "tray");
foreach (var size in traySizes)
{
    WritePng(doc, fit, Path.Combine(trayDir, $"flamingle-{size}.png"), size);
    WritePng(devDoc, fit, Path.Combine(trayDir, $"flamingle-dev-{size}.png"), size);
}

Console.WriteLine("Done.");
return 0;

static SvgDocument Load(string path)
{
    var doc = SvgDocument.Open(path);
    // Normalize the viewport to the viewBox in user units. Inkscape exports set width/height in real-world
    // units (flamingle.svg is "256mm"), which makes Svg.NET apply an extra viewBox->viewport scale that throws
    // off doc.Bounds and the crop transform. Pinning width/height to the viewBox makes that transform
    // identity, so content bounds line up with user coordinates.
    if (doc.ViewBox.Width > 0 && doc.ViewBox.Height > 0)
    {
        doc.Width = new SvgUnit(SvgUnitType.User, doc.ViewBox.Width);
        doc.Height = new SvgUnit(SvgUnitType.User, doc.ViewBox.Height);
    }
    return doc;
}

// Replaces solid fills by colour (keyed by lower-case #rrggbb).
static void Recolour(SvgDocument doc, Dictionary<string, Color> map)
{
    foreach (var el in doc.Descendants().OfType<SvgVisualElement>())
    {
        if (el.Fill is not SvgColourServer { Colour: var c }) continue;
        var key = $"#{c.R:x2}{c.G:x2}{c.B:x2}";
        if (map.TryGetValue(key, out var to))
            el.Fill = new SvgColourServer(to);
    }
}

// Renders the cropped, re-centered content square at the given pixel size with high-quality
// anti-aliasing and a transparent background.
static Bitmap Render(SvgDocument doc, Fit fit, int size)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    bmp.SetResolution(96, 96);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.Clear(Color.Transparent);
    // Map the padded content square onto the pixel canvas: shift its top-left to 0,0 then scale up to
    // fill. Prepend order means the translate is applied first.
    float scale = size / fit.Side;
    g.ScaleTransform(scale, scale);
    g.TranslateTransform(-fit.OriginX, -fit.OriginY);
    doc.Draw(g);
    return bmp;
}

static byte[] RenderPng(SvgDocument doc, Fit fit, int size)
{
    using var bmp = Render(doc, fit, size);
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

static void WritePng(SvgDocument doc, Fit fit, string path, int size)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, RenderPng(doc, fit, size));
    Console.WriteLine($"  {Path.GetFileName(path)}  {size}x{size}");
}

// Writes a Vista+ ICO whose frames are PNG-compressed (keeps the file small and supports 256px).
static void WriteIco(SvgDocument doc, Fit fit, string path, int[] sizes)
{
    var frames = sizes.Select(s => RenderPng(doc, fit, s)).ToArray();

    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);

    // ICONDIR header
    w.Write((ushort)0);             // reserved
    w.Write((ushort)1);             // type = icon
    w.Write((ushort)sizes.Length);  // image count

    // Each ICONDIRENTRY is 16 bytes; image data follows the full directory.
    int offset = 6 + sizes.Length * 16;
    for (int i = 0; i < sizes.Length; i++)
    {
        int size = sizes[i];
        w.Write((byte)(size >= 256 ? 0 : size)); // width  (0 = 256)
        w.Write((byte)(size >= 256 ? 0 : size)); // height (0 = 256)
        w.Write((byte)0);                        // palette count
        w.Write((byte)0);                        // reserved
        w.Write((ushort)1);                      // colour planes
        w.Write((ushort)32);                     // bits per pixel
        w.Write(frames[i].Length);               // bytes of image data
        w.Write(offset);                         // offset of image data
        offset += frames[i].Length;
    }

    foreach (var frame in frames)
        w.Write(frame);

    Console.WriteLine($"  {Path.GetFileName(path)}  [{string.Join(", ", sizes)}]");
}

// Walks up from the tool's binary location to the directory that holds flamingle.svg.
static string FindRepoRoot(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "flamingle.svg")))
            return dir.FullName;
        dir = dir.Parent;
    }
    // Fall back to five-up from bin/<cfg>/<tfm> if the marker walk fails.
    return Path.GetFullPath(Path.Combine(start, "..", "..", "..", "..", ".."));
}

// Computes the square crop that tightly frames the SVG's drawn content (so the raster uses the whole
// canvas instead of inheriting the viewBox's transparent margin). The square is the larger content
// dimension, centered on the content, grown by `pad` on every side.
static Fit ComputeFit(SvgDocument doc, float pad)
{
    var box = doc.Bounds;
    if (box.Width <= 0 || box.Height <= 0)
    {
        float w = doc.ViewBox.Width > 0 ? doc.ViewBox.Width : 1f;
        float h = doc.ViewBox.Height > 0 ? doc.ViewBox.Height : 1f;
        box = new RectangleF(0, 0, w, h);
    }
    float side = Math.Max(box.Width, box.Height);
    side += side * pad * 2f;
    float cx = box.X + box.Width / 2f;
    float cy = box.Y + box.Height / 2f;
    return new Fit(box, side, cx - side / 2f, cy - side / 2f);
}

// The crop geometry: the content's own bounds, the side of the padded square that frames it, and that
// square's top-left corner in SVG user units.
readonly record struct Fit(RectangleF Box, float Side, float OriginX, float OriginY);
