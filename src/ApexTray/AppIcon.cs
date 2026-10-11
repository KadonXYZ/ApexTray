using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ApexTray;

public static class AppIcon
{
    private static readonly Uri Source = new("pack://application:,,,/Assets/ApexTray.ico");

    // All sizes, so the window and taskbar can pick the one they need.
    public static ImageSource ForWindow { get; } = BitmapFrame.Create(Source);

    public static ImageSource Largest { get; } = new IconBitmapDecoder(Source, BitmapCreateOptions.None, BitmapCacheOption.OnLoad)
        .Frames.OrderByDescending(f => f.PixelWidth).First();

    public static System.Drawing.Icon ForTray(int size)
    {
        using var stream = Application.GetResourceStream(Source)!.Stream;
        return new System.Drawing.Icon(stream, size, size);
    }
}
