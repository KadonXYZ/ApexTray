using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using ApexTray.Core.Localization;

namespace ApexTray;

// UI text is written in English in XAML and code and looked up by that English text,
// so a missing translation simply shows the English.
public sealed class Loc : INotifyPropertyChanged
{
    private IReadOnlyDictionary<string, string> _table = Translations.For("en");

    public static Loc Instance { get; } = new();

    // Bumped on every language change so bindings to it re-run their converter.
    public int Version { get; private set; }

    public string Language { get; private set; } = "en";

    public event PropertyChangedEventHandler? PropertyChanged;

    public static event Action? Changed;

    public static string T(string english) => Instance._table.TryGetValue(english, out var text) ? text : english;

    public static string F(string english, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(english), args);

    // code is a language code from Translations.Languages, or empty to follow Windows.
    public void SetLanguage(string code)
    {
        Language = Translations.Resolve(string.IsNullOrEmpty(code) ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : code);
        _table = Translations.For(Language);
        Version++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version)));
        Changed?.Invoke();
    }
}

// {l:T 'English text'}: a live-updating translation of the given text.
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension(string text) : MarkupExtension
{
    private static readonly IValueConverter Translate = new TranslateConverter();

    public TExtension() : this("") { }

    [ConstructorArgument("text")]
    public string Text { get; set; } = text;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(Loc.Version)) { Source = Loc.Instance, Mode = BindingMode.OneWay, Converter = Translate, ConverterParameter = Text }
            .ProvideValue(serviceProvider);

    private sealed class TranslateConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Loc.T((string)parameter);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
