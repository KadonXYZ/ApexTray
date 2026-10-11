using Microsoft.Win32;

namespace ApexTray;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

// Per-user preferences, kept in the registry next to the startup entry rather than in a file.
public static class AppSettings
{
    private const string KeyPath = @"Software\ApexTray";

    public static AppTheme Theme
    {
        get => Enum.TryParse<AppTheme>(Read("Theme"), out var t) ? t : AppTheme.System;
        set => Write("Theme", value.ToString());
    }

    // Language code, or empty to follow Windows.
    public static string Language
    {
        get => Read("Language") ?? "";
        set => Write("Language", value);
    }

    public static bool LowBatteryAlert
    {
        get => Read("LowBatteryAlert") != "0";
        set => Write("LowBatteryAlert", value ? "1" : "0");
    }

    // Percentage at or below which the low-battery notification fires.
    public static int LowBatteryLevel
    {
        get => int.TryParse(Read("LowBatteryLevel"), out var level) && level is >= 1 and <= 100 ? level : 15;
        set => Write("LowBatteryLevel", value.ToString());
    }

    private static string? Read(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(name) as string;
    }

    private static void Write(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(name, value);
    }
}
