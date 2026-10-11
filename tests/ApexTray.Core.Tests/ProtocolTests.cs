using ApexTray.Core.Protocol;
using ApexTray.Core.Transport;

namespace ApexTray.Core.Tests;

public class ProtocolTests
{
    // Captured from the live AJ179 APEX: a 0xD0 reply with the factory key matrix.
    private static readonly byte[] DefaultMatrixReply =
    [
        0x01, 0x00, 0xF0, 0x00, 0x01, 0x00, 0xF1, 0x00, 0x01, 0x00, 0xF2, 0x00, 0x01, 0x00, 0xF4, 0x00,
        0x01, 0x00, 0xF3, 0x00, 0x14, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0xF9, 0x01, 0x01, 0x00, 0xF9, 0xFF, 0x01, 0x00, 0xF5, 0x01, 0x01, 0x00, 0xF5, 0xFF,
    ];

    [Fact]
    public void SetButton_LayoutMatchesVendor()
    {
        var p = MouseCommands.SetButton(0, 4, ButtonAction.Keys([HidUsages.LeftCtrl, 0x06]));

        Assert.Equal(64, p.Length);
        Assert.Equal(new byte[] { 0x50, 0x00, 0x04, 0, 0, 0, 0, 0, 0x00, 0xE0, 0x06, 0x00 }, p[..12]);
        Assert.All(p[12..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void ParseMatrix_DefaultsMatchButtonTable()
    {
        var matrix = MouseCommands.ParseMatrix(DefaultMatrixReply);

        foreach (var button in Aj179Apex.Buttons)
            Assert.Equal(button.Default, matrix[button.Slot]);
    }

    [Theory]
    [InlineData(new byte[] { 0x01, 0x00, 0xF3, 0x00 }, "Back")]
    [InlineData(new byte[] { 0x14, 0x00, 0x00, 0x00 }, "DPI cycle")]
    [InlineData(new byte[] { 0x03, 0x00, 0xCD, 0x00 }, "Play / Pause")]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00 }, "Disabled")]
    [InlineData(new byte[] { 0x00, 0xE0, 0x06, 0x00 }, "Ctrl + C")]
    [InlineData(new byte[] { 0x00, 0x17, 0xE1, 0xE0 }, "Shift + Ctrl + T")]
    [InlineData(new byte[] { 0x09, 0x00, 0x02, 0x00 }, "Macro 3")]
    public void Describe(byte[] bytes, string expected)
        => Assert.Equal(expected, ButtonAction.FromBytes(bytes).Describe());

    [Fact]
    public void Keys_RejectsMoreThanThree()
        => Assert.Throws<ArgumentException>(() => ButtonAction.Keys([0xE0, 0xE1, 0xE2, 0x04]));

    [Fact]
    public void ParseResponses_FromLiveDevice()
    {
        byte[] fw = [0x80, 0x01, 0x03, 0, 0, 0, 0, 0x7F];
        byte[] option0 = new byte[64];
        option0[0] = 0xD3;
        option0[9] = 0x01;
        byte[] option1 = new byte[64];
        byte[] head = [0xD4, 0x00, 0x04, 0x06, 0, 0, 0, 0x2B, 0x90, 0x01, 0x20, 0x03, 0xB0, 0x04, 0x40, 0x06, 0x60, 0x09, 0x80, 0x0C];
        head.CopyTo(option1, 0);

        Assert.Equal("3.01", MouseCommands.ParseFirmware(fw).ToString());
        Assert.Equal(1000, MouseCommands.ParsePollingRate(option0));
        var dpi = MouseCommands.ParseOption1(option1);
        Assert.Equal(new[] { 400, 800, 1200, 1600, 2400, 3200, 0, 0 }, dpi.Stages);
        Assert.Equal(4, dpi.ActiveIndex);
    }

    [Fact]
    public void DockHeader_LayoutMatchesVendor()
    {
        var h = DockCommands.Header(2, 1, 0, new PixelRect(1, 2, 300, 128), 0x0102_0304);

        Assert.Equal(new byte[]
        {
            0xA5, 2, 1, 0, 0x04, 0x03, 0, 0,
            1, 2, 0x2C, 128, 0, 0, 0x01, 0,
            0x02, 0x01,
        }, h);
    }

    [Fact]
    public void DockData_ChunksRestartPerFrameAndPadLastChunk()
    {
        var pixels = Enumerable.Range(0, 120).Select(i => (byte)i).ToArray();
        var packets = DockCommands.DataPackets(3, 10, 50, pixels).ToList();

        Assert.Equal(3, packets.Count);
        Assert.All(packets, p => Assert.Equal(64, p.Length));
        Assert.Equal(new byte[] { 0x25, 3, 10, 50, 2, 0, 8, 0 }, packets[2][..8]);
        Assert.Equal(pixels[112..], packets[2][8..16]);
        Assert.All(packets[2][16..], b => Assert.Equal(0, b));
        Assert.Equal(56, packets[0][6]);
    }

    [Fact]
    public void DockClock_YearIsBigEndian()
    {
        var p = DockCommands.SetClock(new DateTime(2026, 10, 11, 14, 5, 9));
        Assert.Equal(new byte[] { 0x28, 0, 0, 0, 0, 0, 0, 0, 0x07, 0xEA, 10, 11, 14, 5, 9 }, p[..15]);
    }

    [Fact]
    public void EraseAck()
    {
        Assert.True(DockCommands.IsEraseAck([0xAC, 0xAA, 0xAA, 0x55, 0x55, 0]));
        Assert.False(DockCommands.IsEraseAck([0xAC, 0, 0, 0, 0]));
    }

    [Theory]
    [InlineData(new byte[] { 5, 0, 0, 0x64, 1, 1, 1, 2 }, 100)]
    [InlineData(new byte[] { 5, 0, 0, 0x2A, 1, 1, 1, 2 }, 42)]
    [InlineData(new byte[] { 5, 0, 0, 0, 1, 0, 1, 2 }, null)]
    [InlineData(new byte[] { 5, 0xAD, 4, 0x50, 0, 0, 0, 0 }, null)]
    public void DockBattery_Parse(byte[] report, int? expected)
        => Assert.Equal(expected, DockStatusReader.ParseCharge(report));

    [Theory]
    [InlineData(0x41, 0x04)]
    [InlineData(0x30, 0x27)]
    [InlineData(0x70, 0x3A)]
    [InlineData(0xA2, HidUsages.LeftCtrl)]
    [InlineData(0x11, HidUsages.LeftCtrl)]
    [InlineData(0x0D, 0x28)]
    public void VirtualKeyToUsage(int vk, byte usage) => Assert.Equal(usage, HidUsages.FromVirtualKey(vk));
}
