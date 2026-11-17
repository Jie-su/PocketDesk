using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PocketDeskIconGenerator;

internal static class Program
{
    // One small vector scene generates both the editable SVG and every raster.
    // Coordinates use a 256-pixel viewBox; transparent outer corners are retained.
    private static readonly Shape[] Artwork =
    [
        new(8, 8, 240, 240, 54, "#0A78F5"),
        new(42, 93, 172, 94, 18, null, "#FFFFFF", 10, 0.58),
        new(84, 42, 88, 172, 20, "#FFFFFF"),
        new(94, 65, 68, 121, 8, "#0A78F5"),
        new(116, 51, 24, 5, 2.5, "#0A78F5"),
        new(114, 199, 28, 5, 2.5, "#0A78F5")
    ];
    private static readonly int[] Sizes = [16, 24, 32, 48, 64, 128, 256];

    [STAThread]
    private static void Main(string[] args)
    {
        var output = Path.GetFullPath(args.Length > 0 ? args[0] : "src/PocketDesk/Assets");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "PocketDesk.svg"), CreateSvg(), new UTF8Encoding(false));
        using (var preview = File.Create(Path.Combine(output, "preview.png")))
            preview.Write(EncodePng(Render(512)));

        var frames = Sizes.Select(size =>
        {
            var bitmap = Render(size);
            return (Size: size, Bytes: size == 256 ? EncodePng(bitmap) : EncodeDib(bitmap));
        }).ToArray();
        using (var outputFile = File.Create(Path.Combine(output, "PocketDesk.ico")))
        using (var writer = new BinaryWriter(outputFile))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)frames.Length);
            var offset = 6 + frames.Length * 16;
            foreach (var frame in frames)
            {
                writer.Write((byte)(frame.Size == 256 ? 0 : frame.Size));
                writer.Write((byte)(frame.Size == 256 ? 0 : frame.Size));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write((uint)frame.Bytes.Length);
                writer.Write((uint)offset);
                offset += frame.Bytes.Length;
            }
            foreach (var frame in frames) writer.Write(frame.Bytes);
        }
        // Decode the finished asset using the same Windows WPF codec as the app.
        using var icon = File.OpenRead(Path.Combine(output, "PocketDesk.ico"));
        var decoded = new IconBitmapDecoder(icon, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var decodedSizes = decoded.Frames.Select(frame => frame.PixelWidth).OrderBy(size => size).ToArray();
        if (!decodedSizes.SequenceEqual(Sizes))
            throw new InvalidDataException("The generated ICO is missing a required size.");
        Console.WriteLine($"Generated SVG, 512 px preview and verified ICO ({string.Join(", ", Sizes)}) in {output}");
    }

    private static string CreateSvg()
    {
        var svg = new StringBuilder("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"256\" height=\"256\" viewBox=\"0 0 256 256\" role=\"img\" aria-labelledby=\"title desc\">\n");
        svg.AppendLine("  <title id=\"title\">PocketDesk</title>");
        svg.AppendLine("  <desc id=\"desc\">A white portrait phone overlaps a landscape outline on a blue rounded square.</desc>");
        foreach (var shape in Artwork)
        {
            svg.Append($"  <rect x=\"{Number(shape.X)}\" y=\"{Number(shape.Y)}\" width=\"{Number(shape.Width)}\" height=\"{Number(shape.Height)}\" rx=\"{Number(shape.Radius)}\" fill=\"{shape.Fill ?? "none"}\"");
            if (shape.Stroke is not null)
                svg.Append($" stroke=\"{shape.Stroke}\" stroke-width=\"{Number(shape.StrokeWidth)}\" stroke-opacity=\"{Number(shape.StrokeOpacity)}\"");
            svg.AppendLine("/>");
        }
        svg.AppendLine("</svg>");
        return svg.ToString();
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static BitmapSource Render(int size)
    {
        // Supersampling keeps the same geometry smooth at 16/24 px without
        // introducing a second, divergent small-icon design.
        const int samples = 4;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new ScaleTransform(size * samples / 256.0, size * samples / 256.0));
            foreach (var shape in Artwork)
            {
                var fill = Brush(shape.Fill);
                var stroke = Brush(shape.Stroke);
                if (stroke is not null) stroke.Opacity = shape.StrokeOpacity;
                context.DrawRoundedRectangle(fill, stroke is null ? null : new Pen(stroke, shape.StrokeWidth),
                    new Rect(shape.X, shape.Y, shape.Width, shape.Height), shape.Radius, shape.Radius);
            }
            context.Pop();
        }
        var high = new RenderTargetBitmap(size * samples, size * samples, 96, 96, PixelFormats.Pbgra32);
        high.Render(visual);
        var reduced = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(reduced, BitmapScalingMode.HighQuality);
        using (var context = reduced.RenderOpen()) context.DrawImage(high, new Rect(0, 0, size, size));
        var output = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        output.Render(reduced);
        return output;
    }

    private static SolidColorBrush? Brush(string? color) => color is null
        ? null : new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

    private static byte[] EncodePng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] EncodeDib(BitmapSource bitmap)
    {
        var size = bitmap.PixelWidth;
        var stride = size * 4;
        var maskStride = ((size + 31) / 32) * 4;
        var source = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[stride * size];
        source.CopyPixels(pixels, stride, 0);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((uint)40); // BITMAPINFOHEADER, uncompressed BGRA plus AND mask.
        writer.Write(size);
        writer.Write(size * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write((uint)0);
        writer.Write((uint)(stride * size + maskStride * size));
        writer.Write(0); writer.Write(0); writer.Write((uint)0); writer.Write((uint)0);
        for (var y = size - 1; y >= 0; y--) writer.Write(pixels, y * stride, stride);
        for (var y = size - 1; y >= 0; y--)
        {
            var mask = new byte[maskStride];
            for (var x = 0; x < size; x++)
                if (pixels[y * stride + x * 4 + 3] == 0) mask[x / 8] |= (byte)(0x80 >> (x % 8));
            writer.Write(mask);
        }
        return stream.ToArray();
    }

    private sealed record Shape(double X, double Y, double Width, double Height, double Radius,
        string? Fill, string? Stroke = null, double StrokeWidth = 0, double StrokeOpacity = 1);
}
