namespace ApexTray.Core.Protocol;

// The 4-byte action stored per button slot in the mouse key matrix.
// Byte 0 selects the kind; the remaining bytes are kind-specific.
public readonly record struct ButtonAction(byte B0, byte B1, byte B2, byte B3)
{
    private const byte KindKey = 0x00, KindMouse = 0x01, KindSystem = 0x02, KindMedia = 0x03,
        KindProfile = 0x08, KindMacro = 0x09, KindFire = 0x0B, KindDpi = 0x14;

    public static readonly ButtonAction Disabled = new(0, 0, 0, 0);

    public static ButtonAction Mouse(byte code, byte param = 0) => new(KindMouse, 0, code, param);
    public static ButtonAction Media(ushort usage) => new(KindMedia, 0, (byte)usage, (byte)(usage >> 8));
    public static ButtonAction Dpi(byte mode) => new(KindDpi, 0, mode, 0);

    // Up to three HID usages, modifiers (0xE0-0xE7) included as plain usages.
    public static ButtonAction Keys(IReadOnlyList<byte> usages)
    {
        if (usages.Count is 0 or > 3) throw new ArgumentException("A key combo holds 1-3 keys including modifiers.", nameof(usages));
        return new(KindKey, usages[0], usages.Count > 1 ? usages[1] : (byte)0, usages.Count > 2 ? usages[2] : (byte)0);
    }

    public static ButtonAction FromBytes(ReadOnlySpan<byte> b) => new(b[0], b[1], b[2], b[3]);

    public void CopyTo(Span<byte> dest)
    {
        dest[0] = B0;
        dest[1] = B1;
        dest[2] = B2;
        dest[3] = B3;
    }

    public bool IsKeyCombo => B0 == KindKey && this != Disabled;

    public IReadOnlyList<byte> KeyUsages => new[] { B1, B2, B3 }.Where(u => u != 0).ToArray();

    public override string ToString() => Describe();

    public string Describe()
    {
        if (this == Disabled) return "Disabled";
        var self = this;
        var known = ActionCatalog.All.FirstOrDefault(c => c.Action == self);
        if (known != null) return known.Name;
        return B0 switch
        {
            KindKey => string.Join(" + ", KeyUsages.OrderBy(u => HidUsages.IsModifier(u) ? 0 : 1).Select(HidUsages.Name)),
            KindMacro => $"Macro {B2 + 1}",
            KindProfile => "Profile switch",
            KindFire => "Fire key",
            KindSystem => "System key",
            _ => $"Custom ({B0:X2} {B1:X2} {B2:X2} {B3:X2})",
        };
    }
}

public sealed record ActionChoice(string Category, string Name, ButtonAction Action);

public static class ActionCatalog
{
    public static readonly IReadOnlyList<ActionChoice> All =
    [
        new("Mouse", "Left click", ButtonAction.Mouse(0xF0)),
        new("Mouse", "Right click", ButtonAction.Mouse(0xF1)),
        new("Mouse", "Middle click", ButtonAction.Mouse(0xF2)),
        new("Mouse", "Back", ButtonAction.Mouse(0xF3)),
        new("Mouse", "Forward", ButtonAction.Mouse(0xF4)),
        new("Mouse", "Scroll up", ButtonAction.Mouse(0xF5, 0x01)),
        new("Mouse", "Scroll down", ButtonAction.Mouse(0xF5, 0xFF)),

        new("DPI", "DPI cycle", ButtonAction.Dpi(0x00)),
        new("DPI", "DPI up", ButtonAction.Dpi(0x01)),
        new("DPI", "DPI down", ButtonAction.Dpi(0x02)),
        new("DPI", "Sniper (hold)", ButtonAction.Dpi(0x04)),

        new("Media", "Play / Pause", ButtonAction.Media(0x00CD)),
        new("Media", "Next track", ButtonAction.Media(0x00B5)),
        new("Media", "Previous track", ButtonAction.Media(0x00B6)),
        new("Media", "Stop", ButtonAction.Media(0x00B7)),
        new("Media", "Mute", ButtonAction.Media(0x00E2)),
        new("Media", "Volume up", ButtonAction.Media(0x00E9)),
        new("Media", "Volume down", ButtonAction.Media(0x00EA)),
        new("Media", "Calculator", ButtonAction.Media(0x0192)),
        new("Media", "Mail", ButtonAction.Media(0x018A)),
        new("Media", "This PC", ButtonAction.Media(0x0194)),
        new("Media", "Browser search", ButtonAction.Media(0x0221)),
        new("Media", "Browser home", ButtonAction.Media(0x0223)),
        new("Media", "Browser back", ButtonAction.Media(0x0224)),
        new("Media", "Browser refresh", ButtonAction.Media(0x0227)),

        new("Other", "Disabled", ButtonAction.Disabled),
    ];
}

// A physical button and the matrix slot it occupies on the AJ179 APEX.
public sealed record PhysicalButton(string Name, int Slot, ButtonAction Default);

public static class Aj179Apex
{
    public const ushort DongleVid = 0x3151;
    public const ushort DonglePid = 0x5007;

    // Id the vendor update server knows the mouse by. The vendor app maps the 2.4 GHz config id
    // (−2147485500) to its wired twin, 1852, before asking.
    public const int FirmwareDeviceId = 1852;

    public const int MinDpi = 50;
    public const int MaxDpi = 42000;

    // Above half the maximum the vendor UI only offers every second step.
    private const int FineStepLimit = MaxDpi / 2;
    private const int DpiStep = 50;

    public static readonly DpiSettings DefaultDpi = new([400, 800, 1200, 1600, 2400, 3200, 0, 0], 1);

    public static bool IsValidDpi(int dpi) => dpi is >= MinDpi and <= MaxDpi && SnapDpi(dpi) == dpi;

    public static int SnapDpi(int dpi)
    {
        var step = dpi <= FineStepLimit ? DpiStep : DpiStep * 2;
        var snapped = (int)Math.Round(dpi / (double)step, MidpointRounding.AwayFromZero) * step;
        return Math.Clamp(snapped, MinDpi, MaxDpi);
    }

    public static readonly IReadOnlyList<PhysicalButton> Buttons =
    [
        new("Left button", 0, ButtonAction.Mouse(0xF0)),
        new("Right button", 1, ButtonAction.Mouse(0xF1)),
        new("Wheel click", 2, ButtonAction.Mouse(0xF2)),
        new("Side button (front)", 3, ButtonAction.Mouse(0xF4)),
        new("Side button (rear)", 4, ButtonAction.Mouse(0xF3)),
        new("DPI button", 5, ButtonAction.Dpi(0x00)),
        new("Wheel up", 14, ButtonAction.Mouse(0xF5, 0x01)),
        new("Wheel down", 15, ButtonAction.Mouse(0xF5, 0xFF)),
    ];
}
