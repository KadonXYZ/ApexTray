using System.Windows.Media;
using System.Windows.Media.Imaging;
using ApexTray.Core.Imaging;
using ApexTray.Core.Protocol;
using ApexTray.Core.Transport;

namespace ApexTray.Core.Tests;

public class ImagingAndTransportTests
{
    private const int Side = DockImage.Side;

    private static byte[] Solid(byte r, byte g, byte b)
    {
        var f = new byte[Side * Side * 4];
        for (var i = 0; i < f.Length; i += 4)
        {
            f[i] = b;
            f[i + 1] = g;
            f[i + 2] = r;
            f[i + 3] = 255;
        }
        return f;
    }

    private static void SetPixel(byte[] f, int x, int y, byte r, byte g, byte b)
    {
        var p = (y * Side + x) * 4;
        f[p] = b;
        f[p + 1] = g;
        f[p + 2] = r;
    }

    [Fact]
    public void Rgb565_Packing()
    {
        Assert.Equal(0xF800, DockImageEncoder.ToRgb565(255, 0, 0));
        Assert.Equal(0x07E0, DockImageEncoder.ToRgb565(0, 255, 0));
        Assert.Equal(0x001F, DockImageEncoder.ToRgb565(0, 0, 255));
        Assert.Equal(0xFFFF, DockImageEncoder.ToRgb565(255, 255, 255));
    }

    [Fact]
    public void Encode_CropsToContentAndWritesColumnMajorBigEndian()
    {
        var f = Solid(0, 0, 0);
        SetPixel(f, 10, 20, 255, 0, 0);
        SetPixel(f, 11, 20, 0, 0, 255);
        SetPixel(f, 10, 21, 0, 255, 0);

        var encoded = DockImageEncoder.Encode(new DockImage([f], 0));

        Assert.Equal(new PixelRect(10, 20, 12, 22), encoded.Box);
        Assert.Equal(new byte[]
        {
            0xF8, 0x00, // (10,20) red
            0x07, 0xE0, // (10,21) green - column-major, so y advances first
            0x00, 0x1F, // (11,20) blue
            0x00, 0x00, // (11,21) black
        }, encoded.FramePixels[0]);
    }

    [Fact]
    public void Encode_AllBlackUsesFullScreen()
    {
        var encoded = DockImageEncoder.Encode(new DockImage([Solid(0, 0, 0)], 0));
        Assert.Equal(new PixelRect(0, 0, Side, Side), encoded.Box);
        Assert.Equal(Side * Side * 2, encoded.FramePixels[0].Length);
    }

    [Fact]
    public void Encode_SharesOneBoxAcrossFrames()
    {
        var a = Solid(0, 0, 0);
        var b = Solid(0, 0, 0);
        SetPixel(a, 5, 5, 255, 255, 255);
        SetPixel(b, 50, 60, 255, 255, 255);

        var encoded = DockImageEncoder.Encode(new DockImage([a, b], 100));

        Assert.Equal(new PixelRect(5, 5, 51, 61), encoded.Box);
        Assert.All(encoded.FramePixels, p => Assert.Equal(46 * 56 * 2, p.Length));
    }

    [Fact]
    public void Resample_FillCropsCentreFitPads()
    {
        const int w = 256, h = 128;
        var src = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var p = (y * w + x) * 4;
            src[p + 2] = x < 64 || x >= 192 ? (byte)255 : (byte)0;
            src[p + 1] = x is >= 64 and < 192 ? (byte)255 : (byte)0;
            src[p + 3] = 255;
        }

        var fill = DockImageEncoder.Resample(src, w, h, FitMode.Fill);
        Assert.Equal(255, fill[1]);
        Assert.Equal(0, fill[2]);

        var fit = DockImageEncoder.Resample(src, w, h, FitMode.Fit);
        Assert.Equal(0, fit[1]);
        Assert.Equal(0, fit[2]);
        var mid = (64 * Side + 0) * 4;
        Assert.Equal(255, fit[mid + 2]);
    }

    [Fact]
    public void Load_AnimatedGif()
    {
        var path = Path.Combine(Path.GetTempPath(), $"apextray-test-{Guid.NewGuid():N}.gif");
        try
        {
            var encoder = new GifBitmapEncoder();
            foreach (var color in new[] { Colors.Red, Colors.Lime, Colors.Blue })
            {
                var px = new byte[64 * 64 * 4];
                for (var i = 0; i < px.Length; i += 4)
                {
                    px[i] = color.B;
                    px[i + 1] = color.G;
                    px[i + 2] = color.R;
                    px[i + 3] = 255;
                }
                encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, px, 64 * 4)));
            }
            using (var fs = File.Create(path)) encoder.Save(fs);

            var image = DockImageEncoder.Load(path, FitMode.Fill);

            Assert.True(image.IsAnimated);
            Assert.Equal(3, image.Frames.Count);
            Assert.InRange(image.DelayMs, (byte)20, (byte)255);
            Assert.True(image.Frames[1][(64 * Side + 64) * 4 + 1] > 200, "second frame should be green");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Protobuf_RoundTrip()
    {
        var bytes = new ProtoWriter().String(1, "\\\\?\\HID#x").Bytes(2, [0x50, 0x00]).UInt(3, 0).UInt(4, 2).ToArray();
        var fields = ProtoReader.Parse(bytes);

        Assert.Equal(3, fields.Count);
        Assert.Equal("\\\\?\\HID#x", fields[0].AsString());
        Assert.Equal(new byte[] { 0x50, 0x00 }, fields[1].Data.ToArray());
        Assert.Equal(4, fields[2].Number);
        Assert.Equal(2UL, fields[2].Value);
    }

    [Fact]
    public void Protobuf_LargeVarint()
    {
        var fields = ProtoReader.Parse(new ProtoWriter().UInt(6, 2147485500).ToArray());
        Assert.Equal(2147485500UL, fields[0].Value);
    }

    [Fact]
    public void IotVersion_FilePath()
    {
        const string live = """{"code":0,"err_message":null,"data":{"iot_driver_version_str":"v218","description":"","file_path":"iot_upgrade_file/win/iot_v218","sys_type":"win"}}""";
        Assert.Equal("iot_upgrade_file/win/iot_v218", IotDriverDownload.ParseFilePath(live));
        Assert.Null(IotDriverDownload.ParseFilePath("""{"code":1,"data":null}"""));
        Assert.Null(IotDriverDownload.ParseFilePath("""{"code":0,"data":{"file_path":"../admin"}}"""));
        Assert.Null(IotDriverDownload.ParseFilePath("""{"code":0,"data":{"file_path":"https://elsewhere.example/x"}}"""));
    }

    [Fact]
    public void Signature_RequiresRongYuan()
    {
        // Validly signed, but by Microsoft.
        Assert.False(IotDriverDownload.IsSignedByRongYuan(typeof(object).Assembly.Location));
        Assert.False(IotDriverDownload.IsSignedByRongYuan(typeof(ImagingAndTransportTests).Assembly.Location));
        if (IotDriverHost.FindExecutable() is { } installed)
            Assert.True(IotDriverDownload.IsSignedByRongYuan(installed));
    }
}