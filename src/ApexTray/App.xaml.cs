using System.Diagnostics;
using System.Windows;
using ApexTray.Core;
using ApexTray.Core.Transport;

namespace ApexTray;

public partial class App : Application
{
    private const string InstanceName = "ApexTray.SingleInstance";
    private const string ShowSignalName = "ApexTray.ShowWindow";

    private Mutex? _instance;
    private EventWaitHandle? _showSignal;
    private ApexDevice? _device;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _lowBatteryNotified;

    public static ApexDevice Device => ((App)Current)._device!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = new Mutex(true, InstanceName, out var first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        if (!first)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }
        ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.BeginInvoke(ShowWindow), null, -1, false);

        Loc.Instance.SetLanguage(AppSettings.Language);
        ThemeManager.Initialize(AppSettings.Theme);

        _device = new ApexDevice();
        _tray = new TrayIcon(ShowWindow, () => _ = SyncClockAsync(), () => _ = QuitAsync());
        _device.StateChanged += s => Dispatcher.BeginInvoke(() => OnState(s));
        _device.Start();

        if (!e.Args.Contains(StartupRegistration.TrayArgument))
            ShowWindow();
    }

    public void ShowWindow()
    {
        _window ??= new MainWindow();
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    // Closes ApexTray and its iot_driver, then starts the official app, which can't share the dock with us.
    public bool HandOffToVendorApp()
    {
        if (IotDriverHost.FindVendorApp() is not { } exe) return false;
        _ = QuitAsync(() => Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WorkingDirectory = System.IO.Path.GetDirectoryName(exe)!,
        }));
        return true;
    }

    private void OnState(DeviceSnapshot s)
    {
        _tray?.Update(s);
        _window?.Render(s);

        // Re-arming only after a real charge stops readings that hover around the threshold from repeating the alert.
        const int rearmMargin = 10;
        var level = AppSettings.LowBatteryLevel;
        if (s.Battery is { } b && b <= level && !_lowBatteryNotified && AppSettings.LowBatteryAlert)
        {
            _lowBatteryNotified = true;
            NotifyLowBattery(b);
        }
        else if (s.Battery > level + rearmMargin)
        {
            _lowBatteryNotified = false;
        }
    }

    // Applies a changed alert setting at once, so a lower battery than the new level alerts now.
    public void LowBatterySettingsChanged()
    {
        _lowBatteryNotified = false;
        if (_device != null) OnState(_device.State);
    }

    public void TestLowBatteryNotification() => NotifyLowBattery(_device?.State.Battery ?? AppSettings.LowBatteryLevel);

    private void NotifyLowBattery(int battery) =>
        _tray?.Notify(Loc.T("Mouse battery low"), Loc.F("AJ179 APEX is at {0}%. Put it on the dock.", battery));

    private async Task SyncClockAsync()
    {
        try
        {
            await Device.SyncClockAsync();
            _tray?.Notify(Loc.T("Dock clock synced"), DateTime.Now.ToString("t"));
        }
        catch (Exception ex)
        {
            _tray?.Notify(Loc.T("Clock sync failed"), Loc.T(ex.Message));
        }
    }

    private async Task QuitAsync(Action? afterRelease = null)
    {
        _tray?.Dispose();
        _tray = null;
        _window?.CloseForExit();
        if (_device != null) await _device.DisposeAsync();
        _device = null;
        afterRelease?.Invoke();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _showSignal?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
