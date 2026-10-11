namespace ApexTray.Core.Localization;

public sealed record LanguageInfo(string Code, string NativeName);

// UI translations keyed by the English text. English needs no table: a missing key falls back
// to the key itself.
public static partial class Translations
{
    public static readonly IReadOnlyList<LanguageInfo> Languages =
    [
        new("en", "English"),
        new("de", "Deutsch"),
        new("fr", "Français"),
        new("es", "Español"),
        new("zh", "简体中文"),
    ];

    private static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>();

    // Maps a two-letter code to a supported language, English otherwise.
    public static string Resolve(string code) =>
        Languages.Any(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase)) ? code.ToLowerInvariant() : "en";

    public static IReadOnlyDictionary<string, string> For(string code) => Resolve(code) switch
    {
        "de" => German,
        "fr" => French,
        "es" => Spanish,
        "zh" => Chinese,
        _ => English,
    };
}
