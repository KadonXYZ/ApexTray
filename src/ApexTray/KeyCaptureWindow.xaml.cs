using System.Windows;
using System.Windows.Input;
using ApexTray.Core.Protocol;

namespace ApexTray;

// Records a shortcut from real key presses. Buttons aren't focusable, so Enter, Esc and Space
// can be captured as keys too.
public partial class KeyCaptureWindow : Window
{
    private const int MaxKeys = 3;
    private readonly List<byte> _keys = [];

    public KeyCaptureWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    public ButtonAction? Result { get; private set; }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };
        if (HidUsages.FromVirtualKey(KeyInterop.VirtualKeyFromKey(key)) is not { } usage) return;

        var held = new List<byte>();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) held.Add(HidUsages.LeftCtrl);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) held.Add(HidUsages.LeftShift);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) held.Add(HidUsages.LeftAlt);
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) held.Add(HidUsages.LeftWin);

        _keys.Clear();
        _keys.AddRange(held.Take(HidUsages.IsModifier(usage) ? MaxKeys : MaxKeys - 1));
        if (!_keys.Contains(usage) && _keys.Count < MaxKeys) _keys.Add(usage);

        Result = ButtonAction.Keys(_keys);
        ComboText.Text = Result.Value.Describe();
        OkButton.IsEnabled = true;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _keys.Clear();
        Result = null;
        ComboText.Text = Loc.T("Waiting for keys...");
        OkButton.IsEnabled = false;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = Result != null;
}
