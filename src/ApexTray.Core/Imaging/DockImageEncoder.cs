using System.Windows.Media;
using System.Windows.Media.Imaging;
using ApexTray.Core.Protocol;

namespace ApexTray.Core.Imaging;

public enum FitMode
{
    // Crop the centre to a square, filling the whole screen.
    Fill,
    // Show the whole image, padding with black.
    Fit,
}

// Frames already scaled to the dock: 128×128 BGRA, opaque over black.
public sealed record DockImage(IReadOnlyList<byte[]> Frames, byte DelayMs)
{
    public const int Side = DockCommands.ScreenSize;
    public bool IsAnimated => Frames.Count > 1;
}

public sealed record EncodedDockImage(PixelRect Box, IReadOnlyList<byte[]> FramePixels);

public static class DockImageEncoder
{
    // The vendor's limit for 16-bit frames: (8 MiB − 4 KiB) / 36 KiB per frame, minus a margin of 5.
    public const int MaxFrames = 222;

    private const int MinDelayMs = 20, MaxDelayMs = 255;

    public static DockImage Load(string path, FitMode mode)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        var (canvases, width, height, delaysMs) = decoder is GifBitmapDecoder && decoder.Frames.Count > 1
            ? CompositeGif(decoder)
            : (new List<byte[]> { ToBgra(decoder.Frames[0]) }, decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight, new List<int> { 0 });

        var step = (canvases.Count + MaxFrames - 1) / MaxFrames;
        var frames = new List<byte[]>();
        for (var i = 0; i < canvases.Count; i += step)
            frames.Add(Resample(canvases[i], width, height, mode));

        var delay = frames.Count > 1 ? (int)Math.Round(delaysMs.Average() * step) : 0;
        return new DockImage(frames, frames.Count > 1 ? (byte)Math.Clamp(delay, MinDelayMs, MaxDelayMs) : (byte)0);
    }

    // Matches the vendor encoder: one bounding box of non-black pixels shared by all frames,
    // then RGB565 big-endian in column-major order (x outer, y inner).
    public static EncodedDockImage Encode(DockImage image)
    {
        var box = BoundingBox(image.Frames) ?? new PixelRect(0, 0, DockImage.Side, DockImage.Side);
        var payloads = new List<byte[]>(image.Frames.Count);
        foreach (var frame in image.Frames)
        {
            var data = new byte[box.Width * box.Height * 2];
            var o = 0;
            for (var x = box.Left; x < box.Right; x++)
            for (var y = box.Top; y < box.Bottom; y++)
            {
                var p = (y * DockImage.Side + x) * 4;
                var rgb565 = ToRgb565(frame[p + 2], frame[p + 1], frame[p]);
                data[o++] = (byte)(rgb565 >> 8);
                data[o++] = (byte)rgb565;
            }
            payloads.Add(data);
        }
        return new EncodedDockImage(box, payloads);
    }

    internal static ushort ToRgb565(byte r, byte g, byte b) => (ushort)((r >> 3) << 11 | (g >> 2) << 5 | b >> 3);

    internal static PixelRect? BoundingBox(IEnumerable<byte[]> frames)
    {
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        foreach (var f in frames)
        for (var y = 0; y < DockImage.Side; y++)
        for (var x = 0; x < DockImage.Side; x++)
        {
            var p = (y * DockImage.Side + x) * 4;
            if (f[p] == 0 && f[p + 1] == 0 && f[p + 2] == 0) continue;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x);
            bottom = Math.Max(bottom, y);
        }
        return right < 0 ? null : new PixelRect(left, top, right + 1, bottom + 1);
    }

    private static byte[] ToBgra(BitmapSource src)
    {
        var bgra = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var buffer = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(buffer, bgra.PixelWidth * 4, 0);
        return buffer;
    }

    // GIF frames are deltas; replay them onto a logical-screen canvas honouring disposal.
    private static (List<byte[]>, int, int, List<int>) CompositeGif(BitmapDecoder decoder)
    {
        int width = 0, height = 0;
        try
        {
            width = Convert.ToInt32(decoder.Metadata?.GetQuery("/logscrdesc/Width"));
            height = Convert.ToInt32(decoder.Metadata?.GetQuery("/logscrdesc/Height"));
        }
        catch (NotSupportedException) { }

        var frames = decoder.Frames.Select(f => (frame: f, meta: f.Metadata as BitmapMetadata)).ToList();
        if (width == 0 || height == 0)
        {
            width = frames.Max(f => Query(f.meta, "/imgdesc/Left") + f.frame.PixelWidth);
            height = frames.Max(f => Query(f.meta, "/imgdesc/Top") + f.frame.PixelHeight);
        }

        var canvas = new byte[width * height * 4];
        var result = new List<byte[]>();
        var delays = new List<int>();
        foreach (var (frame, meta) in frames)
        {
            int fx = Query(meta, "/imgdesc/Left"), fy = Query(meta, "/imgdesc/Top");
            int fw = frame.PixelWidth, fh = frame.PixelHeight;
            var disposal = Query(meta, "/grctlext/Disposal");
            var previous = disposal == 3 ? (byte[])canvas.Clone() : null;

            var pixels = ToBgra(frame);
            for (var y = 0; y < fh && fy + y < height; y++)
            for (var x = 0; x < fw && fx + x < width; x++)
            {
                var s = (y * fw + x) * 4;
                if (pixels[s + 3] < 128) continue;
                Array.Copy(pixels, s, canvas, ((fy + y) * width + fx + x) * 4, 4);
            }

            result.Add((byte[])canvas.Clone());
            var centis = Query(meta, "/grctlext/Delay");
            delays.Add(centis > 0 ? centis * 10 : 100);

            if (disposal == 2)
            {
                for (var y = fy; y < Math.Min(fy + fh, height); y++)
                    Array.Clear(canvas, (y * width + fx) * 4, Math.Min(fw, width - fx) * 4);
            }
            else if (previous != null)
            {
                canvas = previous;
            }
        }
        return (result, width, height, delays);
    }

    private static int Query(BitmapMetadata? meta, string query)
    {
        try { return meta?.GetQuery(query) is { } v ? Convert.ToInt32(v) : 0; }
        catch (Exception e) when (e is NotSupportedException or ArgumentException or InvalidOperationException) { return 0; }
    }

    // Scales a BGRA image onto the 128×128 screen over black: box-filter when shrinking, bilinear when enlarging.
    internal static byte[] Resample(byte[] src, int sw, int sh, FitMode mode)
    {
        const int side = DockImage.Side;
        double size = mode == FitMode.Fill ? Math.Min(sw, sh) : Math.Max(sw, sh);
        double originX = (sw - size) / 2, originY = (sh - size) / 2;
        var scale = size / side;
        var dst = new byte[side * side * 4];

        for (var ty = 0; ty < side; ty++)
        for (var tx = 0; tx < side; tx++)
        {
            double b, g, r;
            if (scale > 1)
            {
                var x0 = originX + tx * scale;
                var y0 = originY + ty * scale;
                (b, g, r) = BoxAverage(src, sw, sh, x0, y0, x0 + scale, y0 + scale);
            }
            else
            {
                (b, g, r) = Bilinear(src, sw, sh, originX + (tx + 0.5) * scale - 0.5, originY + (ty + 0.5) * scale - 0.5);
            }
            var d = (ty * side + tx) * 4;
            dst[d] = (byte)Math.Round(b);
            dst[d + 1] = (byte)Math.Round(g);
            dst[d + 2] = (byte)Math.Round(r);
            dst[d + 3] = 255;
        }
        return dst;
    }

    private static (double, double, double) BoxAverage(byte[] src, int sw, int sh, double x0, double y0, double x1, double y1)
    {
        int ix0 = (int)Math.Floor(x0), iy0 = (int)Math.Floor(y0), ix1 = (int)Math.Ceiling(x1), iy1 = (int)Math.Ceiling(y1);
        double b = 0, g = 0, r = 0;
        var count = 0;
        for (var y = iy0; y < iy1; y++)
        for (var x = ix0; x < ix1; x++)
        {
            count++;
            if (x < 0 || y < 0 || x >= sw || y >= sh) continue;
            var (pb, pg, pr) = OverBlack(src, (y * sw + x) * 4);
            b += pb;
            g += pg;
            r += pr;
        }
        return count == 0 ? (0, 0, 0) : (b / count, g / count, r / count);
    }

    private static (double, double, double) Bilinear(byte[] src, int sw, int sh, double x, double y)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        double b = 0, g = 0, r = 0;
        for (var j = 0; j < 2; j++)
        for (var i = 0; i < 2; i++)
        {
            var w = (i == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy);
            int sx = x0 + i, sy = y0 + j;
            if (w == 0 || sx < 0 || sy < 0 || sx >= sw || sy >= sh) continue;
            var (pb, pg, pr) = OverBlack(src, (sy * sw + sx) * 4);
            b += pb * w;
            g += pg * w;
            r += pr * w;
        }
        return (b, g, r);
    }

    private static (double, double, double) OverBlack(byte[] src, int p)
    {
        var a = src[p + 3] / 255.0;
        return (src[p] * a, src[p + 1] * a, src[p + 2] * a);
    }
}
