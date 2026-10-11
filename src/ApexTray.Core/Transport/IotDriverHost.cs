using System.Diagnostics;
using System.Net.Sockets;

namespace ApexTray.Core.Transport;

// Makes sure the vendor's headless iot_driver is listening, starting it from the AJAZZ install
// when needed. Only a process we started ourselves is stopped on dispose, so a running
// official app keeps its own helper.
public sealed class IotDriverHost : IDisposable
{
    private const string AppFolderName = "AJAZZ Driver（R）";
    private Process? _owned;

    public static bool IsListening()
    {
        try
        {
            using var tcp = new TcpClient();
            return tcp.ConnectAsync("127.0.0.1", IotDriverClient.Port).Wait(300) && tcp.Connected;
        }
        catch (Exception e) when (e is SocketException or AggregateException)
        {
            return false;
        }
    }

    public static string? FindExecutable()
    {
        var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);
        if (Directory.Exists(roaming))
        {
            var newest = Directory.GetFiles(roaming, "iot_driver_v*.exe")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (newest != null) return newest;
        }

        var bundled = Path.Combine(InstallFolder, "resources", "app", "iot_driver.exe");
        return File.Exists(bundled) ? bundled : null;
    }

    // The official AJAZZ app, used for the one job ApexTray hands back to it: flashing firmware.
    public static string? FindVendorApp()
    {
        var exe = Path.Combine(InstallFolder, AppFolderName + ".exe");
        return File.Exists(exe) ? exe : null;
    }

    private static string InstallFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppFolderName);

    public async Task EnsureRunningAsync(CancellationToken ct = default)
    {
        if (IsListening()) return;
        if (_owned is { HasExited: false }) return;

        var exe = FindExecutable() ?? throw new FileNotFoundException(
            "iot_driver.exe not found. It ships with the official AJAZZ Driver(R); keep that installed.");

        // iot_driver keeps its database next to itself, matching how the official app runs it.
        _owned = Process.Start(new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!IsListening())
        {
            if (_owned is null || _owned.HasExited) throw new InvalidOperationException("iot_driver exited during startup.");
            if (DateTime.UtcNow > deadline) throw new TimeoutException("iot_driver did not start listening on port 3814.");
            await Task.Delay(200, ct);
        }
    }

    public void Dispose()
    {
        if (_owned is { HasExited: false })
        {
            try { _owned.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
        _owned?.Dispose();
        _owned = null;
    }
}
