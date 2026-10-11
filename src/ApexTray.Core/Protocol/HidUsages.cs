namespace ApexTray.Core.Protocol;

// HID keyboard usages (page 0x07) and the Windows virtual-key mapping used for hotkey capture.
public static class HidUsages
{
    public const byte LeftCtrl = 0xE0, LeftShift = 0xE1, LeftAlt = 0xE2, LeftWin = 0xE3;

    private static readonly Dictionary<byte, string> Names = BuildNames();
    private static readonly Dictionary<int, byte> FromVk = BuildVkMap();

    public static bool IsModifier(byte usage) => usage is >= 0xE0 and <= 0xE7;

    public static string Name(byte usage) => Names.TryGetValue(usage, out var n) ? n : $"Key 0x{usage:X2}";

    public static byte? FromVirtualKey(int vk) => FromVk.TryGetValue(vk, out var u) ? u : null;

    private static Dictionary<byte, string> BuildNames()
    {
        var n = new Dictionary<byte, string>();
        for (var i = 0; i < 26; i++) n[(byte)(0x04 + i)] = ((char)('A' + i)).ToString();
        for (var i = 0; i < 9; i++) n[(byte)(0x1E + i)] = ((char)('1' + i)).ToString();
        n[0x27] = "0";
        string[] misc = ["Enter", "Esc", "Backspace", "Tab", "Space", "-", "=", "[", "]", "\\", "#", ";", "'", "`", ",", ".", "/", "Caps Lock"];
        for (var i = 0; i < misc.Length; i++) n[(byte)(0x28 + i)] = misc[i];
        for (var i = 0; i < 12; i++) n[(byte)(0x3A + i)] = $"F{i + 1}";
        string[] nav = ["Print Screen", "Scroll Lock", "Pause", "Insert", "Home", "Page Up", "Delete", "End", "Page Down", "Right", "Left", "Down", "Up", "Num Lock"];
        for (var i = 0; i < nav.Length; i++) n[(byte)(0x46 + i)] = nav[i];
        string[] pad = ["Num /", "Num *", "Num -", "Num +", "Num Enter", "Num 1", "Num 2", "Num 3", "Num 4", "Num 5", "Num 6", "Num 7", "Num 8", "Num 9", "Num 0", "Num ."];
        for (var i = 0; i < pad.Length; i++) n[(byte)(0x54 + i)] = pad[i];
        n[0x64] = "\\ (ISO)";
        n[0x65] = "Menu";
        for (var i = 0; i < 12; i++) n[(byte)(0x68 + i)] = $"F{i + 13}";
        string[] mods = ["Ctrl", "Shift", "Alt", "Win", "Right Ctrl", "Right Shift", "Right Alt", "Right Win"];
        for (var i = 0; i < mods.Length; i++) n[(byte)(0xE0 + i)] = mods[i];
        return n;
    }

    private static Dictionary<int, byte> BuildVkMap()
    {
        var m = new Dictionary<int, byte>();
        for (var i = 0; i < 26; i++) m['A' + i] = (byte)(0x04 + i);
        for (var i = 1; i <= 9; i++) m['0' + i] = (byte)(0x1E + i - 1);
        m['0'] = 0x27;
        for (var i = 0; i < 12; i++) m[0x70 + i] = (byte)(0x3A + i);
        for (var i = 0; i < 12; i++) m[0x7C + i] = (byte)(0x68 + i);
        for (var i = 1; i <= 9; i++) m[0x60 + i] = (byte)(0x59 + i - 1);
        m[0x60] = 0x62;

        (int vk, byte usage)[] rest =
        [
            (0x0D, 0x28), (0x1B, 0x29), (0x08, 0x2A), (0x09, 0x2B), (0x20, 0x2C),
            (0xBD, 0x2D), (0xBB, 0x2E), (0xDB, 0x2F), (0xDD, 0x30), (0xDC, 0x31),
            (0xBA, 0x33), (0xDE, 0x34), (0xC0, 0x35), (0xBC, 0x36), (0xBE, 0x37), (0xBF, 0x38),
            (0x14, 0x39), (0x2C, 0x46), (0x91, 0x47), (0x13, 0x48), (0x2D, 0x49), (0x24, 0x4A),
            (0x21, 0x4B), (0x2E, 0x4C), (0x23, 0x4D), (0x22, 0x4E), (0x27, 0x4F), (0x25, 0x50),
            (0x28, 0x51), (0x26, 0x52), (0x90, 0x53), (0x6F, 0x54), (0x6A, 0x55), (0x6D, 0x56),
            (0x6B, 0x57), (0x6E, 0x63), (0xE2, 0x64), (0x5D, 0x65),
            (0xA2, LeftCtrl), (0xA0, LeftShift), (0xA4, LeftAlt), (0x5B, LeftWin),
            (0xA3, 0xE4), (0xA1, 0xE5), (0xA5, 0xE6), (0x5C, 0xE7),
            (0x11, LeftCtrl), (0x10, LeftShift), (0x12, LeftAlt),
        ];
        foreach (var (vk, usage) in rest) m[vk] = usage;
        return m;
    }
}
