using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace ApexTray;

// Swaps the palette brushes (all XAML uses them as DynamicResource) and switches WPF's Fluent
// control theme so combo boxes, check boxes and scroll bars follow along.
public static partial class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const int DwmUseImmersiveDarkMode = 20;

    private static readonly Dictionary<string, Color> Light = new()
    {
        ["Bg"] = Rgb(0xF4F5F7),
        ["Card"] = Rgb(0xFFFFFF),
        ["Line"] = Rgb(0xE2E4E9),
        ["Text"] = Rgb(0x1C1E23),
        ["MutedBrush"] = Rgb(0x6B7180),
        ["Accent"] = Rgb(0x3B5BDB),
        ["AccentSoft"] = Rgb(0xE7ECFD),
        ["Good"] = Rgb(0x2F9E44),
        ["Warn"] = Rgb(0xE8590C),
        ["Bad"] = Rgb(0xE03131),
    };

    private static readonly Dictionary<string, Color> Dark = new()
    {
        ["Bg"] = Rgb(0x16181D),
        ["Card"] = Rgb(0x21242B),
        ["Line"] = Rgb(0x343843),
        ["Text"] = Rgb(0xE8EAEE),
        ["MutedBrush"] = Rgb(0x9AA1AE),
        ["Accent"] = Rgb(0x6F8CFF),
        ["AccentSoft"] = Rgb(0x2A3252),
        ["Good"] = Rgb(0x51CF66),
        ["Warn"] = Rgb(0xFF922B),
        ["Bad"] = Rgb(0xFF6B6B),
    };

    private static AppTheme _theme;

    public static bool IsDark { get; private set; }

    public static event Action? Changed;

    public static void Initialize(AppTheme theme)
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && _theme == AppTheme.System)
                Application.Current.Dispatcher.BeginInvoke(() => Apply(AppTheme.System));
        };
        Apply(theme);
    }

    public static void Apply(AppTheme theme)
    {
        _theme = theme;
        IsDark = theme == AppTheme.Dark || (theme == AppTheme.System && SystemUsesDarkTheme());

        var app = Application.Current;
#pragma warning disable WPF0001 // Fluent theming is marked experimental but ships in the framework.
        app.ThemeMode = IsDark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
        foreach (var (key, color) in IsDark ? Dark : Light)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            app.Resources[key] = brush;
        }

        foreach (Window w in app.Windows) ApplyTitleBar(w);
        Changed?.Invoke();
    }

    // Call from SourceInitialized; the caption bar is drawn by DWM, not WPF.
    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;
        var dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
    }

    private static bool SystemUsesDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    private static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
