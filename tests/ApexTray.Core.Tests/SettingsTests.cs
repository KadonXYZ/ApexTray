using System.Text.RegularExpressions;
using ApexTray.Core.Firmware;
using ApexTray.Core.Localization;
using ApexTray.Core.Protocol;

namespace ApexTray.Core.Tests;

public class SettingsTests
{
    // Live AJ179 APEX replies: 1000 Hz, lift-off middle, angle snapping on, ripple off; DPI 400-3200 with stage 6 active.
    private static readonly byte[] Option0Reply = Convert.FromHexString(
        "D30000000000002C0001020000000C01010000000000000003030407000000000000000000000000140000001400000000006464010100000000000000000000");
    private static readonly byte[] Option1Reply = Convert.FromHexString(
        "D40005060000002B90012003B00440066009800C0000000090012003B00440066009800C00000000FF000000FF000000FF00FFFFFFFF00FF00FFFF00FFFFFFFF");

    [Fact]
    public void ParseOption0_FromLiveDevice()
        => Assert.Equal(new SensorSettings(1000, LiftOffDistance.Middle, AngleSnapping: true, RippleControl: false), MouseCommands.ParseOption0(Option0Reply));

    [Fact]
    public void BuildOption0_UnchangedSettingsReproduceVendorPacket()
    {
        var p = MouseCommands.BuildOption0(Option0Reply, MouseCommands.ParseOption0(Option0Reply));

        var expected = (byte[])Option0Reply.Clone();
        expected[0] = MouseCommands.SetOption0;
        expected[7] = 0;
        Assert.Equal(expected, p);
    }

    [Fact]
    public void BuildOption0_ChangesOnlySensorFields()
    {
        var wanted = new SensorSettings(8000, LiftOffDistance.High, AngleSnapping: false, RippleControl: true);
        var p = MouseCommands.BuildOption0(Option0Reply, wanted);

        Assert.Equal(0x81, p[9]);
        Assert.Equal(0x04, p[12]);
        Assert.Equal(2, p[52]);
        Assert.Equal(0, p[53]);
        Assert.Equal(Option0Reply[24..48], p[24..48]);
        Assert.Equal(Option0Reply[50..52], p[50..52]);
        Assert.Equal(wanted, MouseCommands.ParseOption0(p));
    }

    [Fact]
    public void BuildOption0_ZeroesBytesTheVendorNeverSends()
    {
        var noisy = Enumerable.Repeat((byte)0xEE, 64).ToArray();
        var p = MouseCommands.BuildOption0(noisy, new SensorSettings(1000, LiftOffDistance.Low, false, false));

        foreach (var i in new[] { 1, 2, 3, 4, 5, 6, 7, 11, 17, 23, 48, 49, 61, 62, 63 })
            Assert.Equal(0, p[i]);
    }

    [Fact]
    public void ParseOption1_FromLiveDevice()
    {
        var dpi = MouseCommands.ParseOption1(Option1Reply);

        Assert.Equal(new[] { 400, 800, 1200, 1600, 2400, 3200, 0, 0 }, dpi.Stages);
        Assert.Equal(5, dpi.ActiveIndex);
    }

    [Fact]
    public void BuildOption1_WritesXAndYAndKeepsColours()
    {
        var p = MouseCommands.BuildOption1(0, new DpiSettings([400, 800, 1600, 26000, 0, 0, 0, 0], 2), Option1Reply);

        Assert.Equal(new byte[] { 0x54, 0, 2, 8, 0, 0, 0, 0 }, p[..8]);
        Assert.Equal(new byte[] { 0x90, 0x01, 0x20, 0x03, 0x40, 0x06, 0x90, 0x65, 0, 0, 0, 0, 0, 0, 0, 0 }, p[8..24]);
        Assert.Equal(p[8..24], p[24..40]);
        Assert.Equal(Option1Reply[40..], p[40..]);
    }

    [Theory]
    [InlineData(new[] { 400, 0, 0, 0, 0, 0, 0, 0 }, 1)]
    [InlineData(new[] { 0, 0, 0, 0, 0, 0, 0, 0 }, 0)]
    [InlineData(new[] { 425, 0, 0, 0, 0, 0, 0, 0 }, 0)]
    [InlineData(new[] { 21050, 0, 0, 0, 0, 0, 0, 0 }, 0)]
    [InlineData(new[] { 50000, 0, 0, 0, 0, 0, 0, 0 }, 0)]
    public void BuildOption1_RejectsInvalidSettings(int[] stages, int active)
        => Assert.Throws<ArgumentException>(() => MouseCommands.BuildOption1(0, new DpiSettings(stages, active), Option1Reply));

    [Theory]
    [InlineData(1234, 1250)]
    [InlineData(10, 50)]
    [InlineData(21000, 21000)]
    [InlineData(21040, 21000)]
    [InlineData(21060, 21100)]
    [InlineData(99999, 42000)]
    public void SnapDpi(int input, int expected) => Assert.Equal(expected, Aj179Apex.SnapDpi(input));

    [Fact]
    public void FirmwareVersion_FormatsLikeVendor() => Assert.Equal("3.0A", new FirmwareVersion(0x030A).ToString());

    [Theory]
    [InlineData("050C030000", 3)]
    [InlineData("050C000000", 0)]
    [InlineData("050C030100", null)]
    [InlineData("050C080000", null)]
    [InlineData("0502010000", null)]
    [InlineData("050C", null)]
    public void DpiSwitch_Parse(string hex, int? stage) => Assert.Equal(stage, MouseCommands.ParseDpiSwitch(Convert.FromHexString(hex)));

    [Fact]
    public void PollingRateSwitch_Recognised()
    {
        Assert.True(MouseCommands.IsPollingRateSwitch(Convert.FromHexString("0502010000")));
        Assert.False(MouseCommands.IsPollingRateSwitch(Convert.FromHexString("050C010000")));
    }

    [Fact]
    public void FirmwareRelease_Parse()
    {
        var release = FirmwareUpdates.Parse("""
            {"code":0,"data":{"version_str":"v0302_rfv0105","description":"<p>Fixes &amp; tweaks</p><br>Better sleep","file_path":"fw/aj179.zip"}}
            """);

        Assert.NotNull(release);
        Assert.Equal(0x0302, release.Version.Raw);
        Assert.Equal("Fixes & tweaks\n\nBetter sleep", release.Notes);
        Assert.Equal("fw/aj179.zip", release.FilePath);
    }

    [Theory]
    [InlineData("""{"code":1,"data":null}""")]
    [InlineData("""{"code":0,"data":{"version_str":"latest"}}""")]
    public void FirmwareRelease_ParseRejectsUnusable(string json) => Assert.Null(FirmwareUpdates.Parse(json));

    [Fact]
    public void Translations_EveryLanguageHasTheSameKeys()
    {
        var reference = Translations.For("de").Keys.ToHashSet();
        foreach (var language in Translations.Languages.Where(l => l.Code is not ("en" or "de")))
        {
            var keys = Translations.For(language.Code).Keys.ToHashSet();
            Assert.True(reference.SetEquals(keys),
                $"{language.Code}: missing [{string.Join(" | ", reference.Except(keys))}], extra [{string.Join(" | ", keys.Except(reference))}]");
        }
    }

    [Fact]
    public void Translations_CoverEveryUiString()
    {
        var used = UiStrings()
            .Concat(ActionCatalog.All.SelectMany(a => new[] { a.Category, a.Name }))
            .Concat(Aj179Apex.Buttons.Select(b => b.Name))
            .ToHashSet();
        var table = Translations.For("de");

        var missing = used.Where(k => !table.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Untranslated: " + string.Join(" | ", missing));
    }

    private static IEnumerable<string> UiStrings()
    {
        var app = Path.Combine(RepoRoot(), "src", "ApexTray");
        foreach (var file in Directory.GetFiles(app, "*.xaml"))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\{l:T '((?:[^'\\]|\\.)*)'\}"))
                yield return m.Groups[1].Value;
        foreach (var file in Directory.GetFiles(app, "*.cs"))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"Loc\.[TF]\(""((?:[^""\\]|\\.)*)"""))
                yield return Regex.Unescape(m.Groups[1].Value);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ApexTray.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("ApexTray.sln not found above the test output folder.");
    }
}
