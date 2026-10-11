using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ApexTray.Core.Transport;

// Reads the dock's status feature report directly. Some dock firmwares mirror the mouse charge
// there after a 0xF7 status poll (AJAZZ-Control-Center, aj_series_battery.md); iot_driver can
// only read report 0, so this goes around it. The HID handle is opened shared, alongside iot_driver.
public static partial class DockStatusReader
{
    private const int ReportLength = 65;
    private const byte StatusPoll = 0xF7;
    private const byte StatusReportId = 0x05;

    // Returns charge 1-100, or null when the dock doesn't report one.
    public static int? TryReadBattery(string hidPath)
    {
        using var h = CreateFile(hidPath, GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
        if (h.IsInvalid) return null;

        var poll = new byte[ReportLength];
        poll[1] = StatusPoll;
        if (!HidD_SetFeature(h, poll, poll.Length)) return null;
        Thread.Sleep(40);

        var report = new byte[ReportLength];
        report[0] = StatusReportId;
        if (!HidD_GetFeature(h, report, report.Length)) return null;
        return ParseCharge(report);
    }

    // Frame layout on Windows: [reportId, 0, 0, charge, linkFlags...]. Non-zero bytes before the charge
    // are reconnect garbage, and a zero charge means the link hasn't reported one.
    internal static int? ParseCharge(ReadOnlySpan<byte> report)
    {
        if (report.Length < 4 || report[1] != 0 || report[2] != 0) return null;
        var charge = report[3];
        return charge is >= 1 and <= 100 ? charge : null;
    }

    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, int length);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);
}
