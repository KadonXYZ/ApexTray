using System.Diagnostics;
using ApexTray.Core.Imaging;
using ApexTray.Core.Protocol;
using ApexTray.Core.Transport;

namespace ApexTray.Core;

public sealed record DeviceSnapshot
{
    public bool DriverReady { get; init; }
    public bool DownloadingDriver { get; init; }
    public string? DonglePath { get; init; }
    public bool MouseOnline { get; init; }
    public int? Battery { get; init; }
    public FirmwareVersion? MouseFirmware { get; init; }
    public string? DockVersion { get; init; }
    public SensorSettings? Sensor { get; init; }
    public DpiSettings? Dpi { get; init; }
    public ButtonAction[]? Matrix { get; init; }
    public string? Error { get; init; }

    public bool DongleConnected => DonglePath != null;
}

// The AJ179 APEX and its dock, driven through iot_driver. All device I/O is serialised because
// the dongle has a single request/response feature-report channel.
public sealed class ApexDevice : IAsyncDisposable
{
    private static readonly TimeSpan BatteryInterval = TimeSpan.FromSeconds(30);

    private readonly IotDriverHost _host = new();
    private readonly IotDriverClient _client = new();
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _watchLoop, _batteryLoop, _venderLoop;
    private byte _profile;
    private int _streamBattery;
    private int? _dockBattery;
    private bool _streamOnline;
    // A freshly started iot_driver reports the mouse offline until its own loop catches up,
    // so whether the mouse actually answers is the better signal.
    private bool _mouseResponds;

    public DeviceSnapshot State { get; private set; } = new();

    public event Action<DeviceSnapshot>? StateChanged;

    public void Start()
    {
        _watchLoop = Task.Run(() => WatchLoopAsync(_stop.Token));
        _batteryLoop = Task.Run(() => BatteryLoopAsync(_stop.Token));
        _venderLoop = Task.Run(() => VenderLoopAsync(_stop.Token));
    }

    public async Task RefreshDetailsAsync(CancellationToken ct = default)
    {
        var path = RequirePath();
        await _io.WaitAsync(ct);
        try
        {
            var fw = MouseCommands.ParseFirmware(await MouseQueryAsync(path, MouseCommands.Simple(MouseCommands.GetFirmware), ct));
            _profile = (await MouseQueryAsync(path, MouseCommands.Simple(MouseCommands.GetProfile), ct))[1];
            var sensor = MouseCommands.ParseOption0(await MouseQueryAsync(path, MouseCommands.Simple(MouseCommands.GetOption0), ct));
            var dpi = MouseCommands.ParseOption1(await MouseQueryAsync(path, MouseCommands.Simple(MouseCommands.GetOption1, _profile), ct));
            var matrix = MouseCommands.ParseMatrix(await MouseQueryAsync(path, MouseCommands.Simple(MouseCommands.GetKeyMatrix, _profile), ct, expectEcho: false));
            var dock = DockCommands.ParseVersion(await DockQueryAsync(path, DockCommands.Simple(DockCommands.Version), 20, ct));
            Update(s => s with { MouseFirmware = fw, Sensor = sensor, Dpi = dpi, Matrix = matrix, DockVersion = dock, Error = null });
        }
        finally
        {
            _io.Release();
        }
    }

    public async Task SetButtonAsync(int slot, ButtonAction action, CancellationToken ct = default)
    {
        var path = RequirePath();
        await _io.WaitAsync(ct);
        try
        {
            await _client.SendMsgAsync(path, MouseCommands.SetButton(_profile, slot, action), ChecksumType.Bit7, DangleDevType.Mouse, ct);
            await Task.Delay(30, ct);
            var matrix = MouseCommands.ParseMatrix(await MouseQueryAsync(path, MouseCommands.Simple(MouseCommands.GetKeyMatrix, _profile), ct, expectEcho: false));
            Update(s => s with { Matrix = matrix });
            if (matrix[slot] != action)
                throw new IOException($"The mouse kept \"{matrix[slot].Describe()}\" instead of \"{action.Describe()}\".");
        }
        finally
        {
            _io.Release();
        }
    }

    public Task SetSensorAsync(SensorSettings wanted, CancellationToken ct = default) =>
        WriteOptionAsync(
            MouseCommands.Simple(MouseCommands.GetOption0),
            current => MouseCommands.BuildOption0(current, wanted),
            reply =>
            {
                var sensor = MouseCommands.ParseOption0(reply);
                Update(s => s with { Sensor = sensor });
                return sensor == wanted;
            },
            ct);

    public Task SetDpiAsync(DpiSettings wanted, CancellationToken ct = default) =>
        WriteOptionAsync(
            MouseCommands.Simple(MouseCommands.GetOption1, _profile),
            current => MouseCommands.BuildOption1(_profile, wanted, current),
            reply =>
            {
                var dpi = MouseCommands.ParseOption1(reply);
                Update(s => s with { Dpi = dpi });
                return dpi.SameAs(wanted);
            },
            ct);

    // Switches the stage the mouse uses now, keeping whatever stage values it has stored.
    public Task SetActiveDpiStageAsync(int index, CancellationToken ct = default) =>
        WriteOptionAsync(
            MouseCommands.Simple(MouseCommands.GetOption1, _profile),
            current => MouseCommands.BuildOption1(_profile, MouseCommands.ParseOption1(current) with { ActiveIndex = index }, current),
            reply =>
            {
                var dpi = MouseCommands.ParseOption1(reply);
                Update(s => s with { Dpi = dpi });
                return dpi.ActiveIndex == index;
            },
            ct);

    // Re-reads the DPI block unless other I/O is in progress. Backs up the DPI-button notification,
    // which a mouse can only send while the dock relays it.
    public async Task PollDpiAsync(CancellationToken ct = default)
    {
        if (State.DonglePath is not { } path || !State.MouseOnline || !await _io.WaitAsync(0, ct)) return;
        try
        {
            var dpi = MouseCommands.ParseOption1(await MouseQueryAsync(path, MouseCommands.Simple(MouseCommands.GetOption1, _profile), ct));
            Update(s => dpi.SameAs(s.Dpi) ? s : s with { Dpi = dpi });
        }
        catch (IOException)
        {
            // Asleep; MouseQueryAsync already marked it offline.
        }
        finally
        {
            _io.Release();
        }
    }

    public async Task ResetButtonsAsync(CancellationToken ct = default)
    {
        foreach (var b in Aj179Apex.Buttons)
        {
            if (State.Matrix?[b.Slot] != b.Default)
                await SetButtonAsync(b.Slot, b.Default, ct);
        }
    }

    public async Task SyncClockAsync(CancellationToken ct = default)
    {
        var path = RequirePath();
        await _io.WaitAsync(ct);
        try
        {
            await _client.SendRawFeatureAsync(path, DockCommands.SetClock(DateTime.Now), ChecksumType.Bit7, DangleDevType.None, ct);
        }
        finally
        {
            _io.Release();
        }
    }

    // Wipes all stored dock images. The dock signals completion asynchronously and needs ~55 s.
    public async Task EraseDockImagesAsync(CancellationToken ct = default)
    {
        var path = RequirePath();
        await _io.WaitAsync(ct);
        try
        {
            var reply = await DockQueryAsync(path, DockCommands.Simple(DockCommands.FlashErase), 150, ct);
            if (!DockCommands.IsEraseAck(reply)) throw new IOException("The dock did not accept the erase command.");
        }
        finally
        {
            _io.Release();
        }
    }

    // Still images go to slot (0-4); an animation replaces the slideshow.
    // Sequence mirrors the vendor app: 0xA5 header (acked), then 0x25 chunks >=3 ms apart per frame.
    public async Task UploadImageAsync(DockImage image, int slot, IProgress<double>? progress, CancellationToken ct = default)
    {
        var path = RequirePath();
        var encoded = DockImageEncoder.Encode(image);
        var count = (byte)encoded.FramePixels.Count;
        var animated = count > 1;

        await _io.WaitAsync(ct);
        await _client.SetWirelessLoopLockedAsync(true, ct);
        try
        {
            var header = DockCommands.Header(animated ? (byte)0 : (byte)slot, count, image.DelayMs, encoded.Box, encoded.FramePixels[0].Length);
            await SendHeaderAsync(path, header, ct);
            await Task.Delay(100, ct);

            var total = encoded.FramePixels.Sum(f => (f.Length + DockCommands.ChunkSize - 1) / DockCommands.ChunkSize);
            var sent = 0;
            var pace = Stopwatch.StartNew();
            for (var g = 0; g < count; g++)
            {
                var frame = animated ? (byte)g : (byte)slot;
                foreach (var packet in DockCommands.DataPackets(frame, count, image.DelayMs, encoded.FramePixels[g]))
                {
                    // Task.Delay can't do 3 ms (timer granularity is ~15 ms), which would make GIFs take minutes.
                    while (pace.Elapsed.TotalMilliseconds < 3) Thread.Yield();
                    await _client.SendRawFeatureAsync(path, packet, ChecksumType.Bit7, DangleDevType.None, ct);
                    pace.Restart();
                    progress?.Report(++sent / (double)total);
                }
            }
            await Task.Delay(500, ct);
        }
        finally
        {
            await _client.SetWirelessLoopLockedAsync(false, CancellationToken.None);
            _io.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        foreach (var t in new[] { _watchLoop, _batteryLoop, _venderLoop })
        {
            if (t == null) continue;
            try { await t; } catch (OperationCanceledException) { }
        }
        _client.Dispose();
        _host.Dispose();
        _stop.Dispose();
    }

    private async Task SendHeaderAsync(string path, byte[] header, CancellationToken ct)
    {
        for (var attempt = 0; attempt <= 10; attempt++)
        {
            if (attempt > 0) await Task.Delay(100, ct);
            var reply = await DockQueryAsync(path, header, 100, ct);
            if (reply.Length > 1 && reply[1] == 1) return;
            if (reply.Length > 1 && reply[1] != 0) break;
        }
        throw new IOException("The dock did not accept the image header.");
    }

    // Option blocks are rebuilt from a fresh read so changes made on the mouse itself (the DPI button)
    // aren't reverted, then read back to confirm the mouse kept them.
    private async Task WriteOptionAsync(byte[] query, Func<byte[], byte[]> build, Func<byte[], bool> applyReadBack, CancellationToken ct)
    {
        var path = RequirePath();
        await _io.WaitAsync(ct);
        try
        {
            var packet = build(await MouseQueryAsync(path, query, ct));
            await _client.SendMsgAsync(path, packet, ChecksumType.Bit7, DangleDevType.Mouse, ct);
            await Task.Delay(60, ct);
            if (!applyReadBack(await MouseQueryAsync(path, query, ct)))
                throw new IOException("The mouse didn't keep the new setting.");
        }
        finally
        {
            _io.Release();
        }
    }

    private async Task<byte[]> MouseQueryAsync(string path, byte[] packet, CancellationToken ct, bool expectEcho = true)
    {
        for (var attempt = 0; ; attempt++)
        {
            await _client.SendMsgAsync(path, packet, ChecksumType.Bit7, DangleDevType.Mouse, ct);
            await Task.Delay(15 + attempt * 20, ct);
            var reply = await _client.ReadMsgAsync(path, ct);
            if (reply.Length >= MouseCommands.PacketLength && (!expectEcho || reply[0] == packet[0]))
            {
                SetMouseResponds(true);
                return reply;
            }
            if (attempt == 3)
            {
                SetMouseResponds(false);
                throw new IOException($"No reply from the mouse to command 0x{packet[0]:X2}. Is it awake?");
            }
        }
    }

    private void SetMouseResponds(bool responds)
    {
        _mouseResponds = responds;
        Update(s => s with { MouseOnline = _streamOnline || _mouseResponds });
    }

    private async Task<byte[]> DockQueryAsync(string path, byte[] packet, int waitMs, CancellationToken ct)
    {
        await _client.SendRawFeatureAsync(path, packet, ChecksumType.Bit7, DangleDevType.None, ct);
        await Task.Delay(waitMs, ct);
        return await _client.ReadRawFeatureAsync(path, ct);
    }

    private async Task WatchLoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!IotDriverHost.IsListening() && IotDriverHost.FindExecutable() is null)
                {
                    Update(s => s with { DownloadingDriver = true, Error = null });
                    try { await IotDriverDownload.DownloadAsync(ct); }
                    finally { Update(s => s with { DownloadingDriver = false }); }
                }
                await _host.EnsureRunningAsync(ct);
                await _client.GetVersionAsync(ct);
                Update(s => s with { DriverReady = true, Error = null });
                backoff = TimeSpan.FromSeconds(1);

                await foreach (var dongle in _client.WatchDongleAsync(Aj179Apex.DongleVid, Aj179Apex.DonglePid, ct))
                {
                    var wasConnected = State.DongleConnected;
                    var wasOnline = _streamOnline;
                    _streamBattery = (int)(dongle?.MouseBattery ?? 0);
                    _streamOnline = dongle?.MouseOnline ?? false;
                    if (dongle is null) _mouseResponds = false;
                    Update(s => s with
                    {
                        DonglePath = dongle?.Path,
                        MouseOnline = _streamOnline || _mouseResponds,
                        Battery = CombinedBattery(),
                    });

                    if (dongle is not null && (!wasConnected || (!wasOnline && _streamOnline)))
                        _ = Task.Run(() => OnConnectedAsync(ct), ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                Update(s => s with { DriverReady = false, DonglePath = null, MouseOnline = false, Error = e.Message });
            }

            await Task.Delay(backoff, ct);
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 15));
        }
    }

    private async Task OnConnectedAsync(CancellationToken ct)
    {
        try
        {
            await SyncClockAsync(ct);
            await RefreshDetailsAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Update(s => s with { Error = e.Message });
        }
    }

    private async Task VenderLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (State.DriverReady)
                {
                    await foreach (var msg in _client.WatchVenderAsync(ct))
                    {
                        if (MouseCommands.ParseDpiSwitch(msg) is { } stage)
                        {
                            // Show the new stage immediately, then confirm it from the mouse's stored block.
                            Update(s => s.Dpi is { } d && d.Stages[stage] != 0 && d.ActiveIndex != stage ? s with { Dpi = d with { ActiveIndex = stage } } : s);
                            await PollDpiAsync(ct);
                        }
                        else if (MouseCommands.IsPollingRateSwitch(msg) && State.DonglePath is { } path)
                        {
                            await _io.WaitAsync(ct);
                            try
                            {
                                var sensor = MouseCommands.ParseOption0(await MouseQueryAsync(path, MouseCommands.Simple(MouseCommands.GetOption0), ct));
                                Update(s => s with { Sensor = sensor });
                            }
                            catch (IOException)
                            {
                                // Asleep again already; the next connect refreshes everything.
                            }
                            finally
                            {
                                _io.Release();
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // The driver restarted or isn't up yet; WatchLoopAsync reports that.
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private async Task BatteryLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(BatteryInterval);
        do
        {
            if (State.DonglePath is { } path)
            {
                var wasResponding = _mouseResponds;
                await _io.WaitAsync(ct);
                try
                {
                    _dockBattery = DockStatusReader.TryReadBattery(path);
                    await MouseQueryAsync(path, MouseCommands.Simple(MouseCommands.GetFirmware), ct);
                }
                catch (IOException)
                {
                    // Asleep or out of range; SetMouseResponds already recorded it.
                }
                finally
                {
                    _io.Release();
                }
                Update(s => s with { Battery = CombinedBattery() });

                if (!wasResponding && _mouseResponds && State.Matrix is null)
                    _ = Task.Run(() => OnConnectedAsync(ct), ct);
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private int? CombinedBattery() => _streamBattery is >= 1 and <= 100 ? _streamBattery : _dockBattery;

    private string RequirePath() => State.DonglePath ?? throw new InvalidOperationException("The AJAZZ 2.4G dock is not connected.");

    private void Update(Func<DeviceSnapshot, DeviceSnapshot> change)
    {
        DeviceSnapshot next;
        lock (_stop)
        {
            next = change(State);
            if (next == State) return;
            State = next;
        }
        StateChanged?.Invoke(next);
    }
}
