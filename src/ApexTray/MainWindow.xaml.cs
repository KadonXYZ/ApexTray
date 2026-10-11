using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ApexTray.Core;
using ApexTray.Core.Firmware;
using ApexTray.Core.Imaging;
using ApexTray.Core.Localization;
using ApexTray.Core.Protocol;
using Microsoft.Win32;

namespace ApexTray;

public partial class MainWindow : Window
{
    private const string ImageFilter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*";
    private const int SlotCount = 5;

    private readonly ObservableCollection<ButtonRow> _rows;
    private readonly ObservableCollection<DpiRow> _dpiRows;
    private readonly List<RadioButton> _rateChips = [];
    private readonly List<RadioButton> _slotChips = [];
    private readonly DispatcherTimer _animation = new();
    private readonly DispatcherTimer _dpiPoll = new() { Interval = TimeSpan.FromSeconds(2) };
    private DeviceSnapshot _state = new();
    private int? _shownActiveStage;
    private string? _imagePath;
    private DockImage? _image;
    private BitmapSource[] _previewFrames = [];
    private int _previewIndex;
    private CancellationTokenSource? _uploadCts;
    private bool _busy, _exiting, _dpiDirty, _populating;

    public MainWindow()
    {
        InitializeComponent();
        Icon = AppIcon.ForWindow;
        Logo.Source = AppIcon.Largest;

        _rows = new(Aj179Apex.Buttons.Select(b => new ButtonRow(b)));
        ButtonList.ItemsSource = _rows;
        _dpiRows = new(Enumerable.Range(0, MouseCommands.DpiStageCount).Select(i => new DpiRow(i)));
        foreach (var row in _dpiRows) row.Edited += OnDpiEdited;
        DpiList.ItemsSource = _dpiRows;

        foreach (var hz in MouseCommands.PollingRates)
        {
            var chip = new RadioButton { Style = (Style)FindResource("Chip"), GroupName = "Rate", Content = $"{hz} Hz", Tag = hz };
            chip.Click += RateChip_Click;
            _rateChips.Add(chip);
            RatePanel.Children.Add(chip);
        }
        for (var i = 0; i < SlotCount; i++)
        {
            var chip = new RadioButton { Style = (Style)FindResource("Chip"), GroupName = "Slot", Tag = i, IsChecked = i == 0 };
            _slotChips.Add(chip);
            SlotChips.Children.Add(chip);
        }

        StartupCheck.IsChecked = StartupRegistration.IsEnabled;
        LoadLowBatterySettings();
        PopulatePickers();
        _animation.Tick += (_, _) => AdvancePreview();
        _dpiPoll.Tick += (_, _) => { if (!_busy) _ = App.Device.PollDpiAsync(); };
        IsVisibleChanged += (_, _) => _dpiPoll.IsEnabled = IsVisible;
        BatteryTrack.SizeChanged += (_, _) => Render(_state);
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        ThemeManager.Changed += () => Render(_state);
        Loc.Changed += OnLanguageChanged;
        OnLanguageChanged();
        Render(App.Device.State);
    }

    public void CloseForExit()
    {
        _exiting = true;
        _uploadCts?.Cancel();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    public void Render(DeviceSnapshot s)
    {
        _state = s;
        var (status, dot) = s switch
        {
            { DriverReady: false } when s.Error != null => (Loc.F("Driver unavailable: {0}", s.Error), "Bad"),
            { DriverReady: false } => (Loc.T("Starting driver..."), "MutedBrush"),
            { DongleConnected: false } => (Loc.T("Dock not connected. Plug in the AJAZZ 2.4G dock."), "Bad"),
            { MouseOnline: false } => (Loc.T("Dock connected · mouse asleep (move it to wake)"), "Warn"),
            _ => (Loc.T("Connected"), "Good"),
        };
        StatusText.Text = status;
        StatusDot.Fill = (Brush)FindResource(dot);

        var battery = s.Battery is { } b ? $"{b}%" : "-";
        HeaderBattery.Text = battery;
        BatteryBig.Text = battery;
        BatteryTrack.Visibility = s.Battery is null ? Visibility.Collapsed : Visibility.Visible;
        BatteryBar.Width = s.Battery is { } pct ? Math.Max(6, BatteryTrack.ActualWidth * pct / 100.0) : 0;
        BatteryBar.Background = (Brush)FindResource(s.Battery switch { <= 15 => "Bad", <= 40 => "Warn", _ => "Good" });
        BatteryNote.Text = s switch
        {
            { Battery: not null } => Loc.T("Reported by the dock. Updates every 30 seconds."),
            { MouseOnline: false } => Loc.T("The mouse is asleep, so there's no reading right now."),
            _ => Loc.T("The dock isn't reporting a charge for this mouse. The official AJAZZ app gets the same blank value. ApexTray keeps polling and will show it as soon as the dock reports one."),
        };

        ConnText.Text = s.MouseOnline ? Loc.T("Online (2.4 GHz via dock)") : s.DongleConnected ? Loc.T("Asleep") : Loc.T("Not connected");
        RateText.Text = s.Sensor?.PollingRate is { } r ? $"{r} Hz" : "-";
        FirmwareText.Text = s.MouseFirmware is { } fw
            ? Loc.F("Mouse {0}", fw) + (s.DockVersion is { } dv ? " · " + Loc.F("dock screen {0}", dv) : "")
            : "-";
        DockVersionText.Text = s.DockVersion is { } v ? Loc.F("Dock screen firmware {0}", v) : "";

        var mouseReady = s.DongleConnected && s.MouseOnline && !_busy;
        RenderDpiChips(s.Dpi, mouseReady);
        if (!_dpiDirty) LoadDpiRows(s.Dpi);
        else if (s.Dpi is { } live && _dpiRows[live.ActiveIndex].IsOn)
        {
            // Pending value edits stay, but the active stage always mirrors the mouse.
            foreach (var row in _dpiRows) row.SetActiveQuietly(row.Index == live.ActiveIndex);
        }
        if (s.Dpi is { } now && _shownActiveStage is { } before && before != now.ActiveIndex && !_busy)
            FooterText.Text = Loc.F("Now using stage {0} ({1} DPI).", now.ActiveIndex + 1, now.Stages[now.ActiveIndex]);
        _shownActiveStage = s.Dpi?.ActiveIndex;

        var sensor = s.Sensor;
        foreach (var chip in _rateChips)
        {
            chip.IsChecked = sensor?.PollingRate == (int)chip.Tag;
            chip.IsEnabled = mouseReady && sensor != null;
        }
        LiftLow.IsChecked = sensor?.LiftOff == LiftOffDistance.Low;
        LiftMiddle.IsChecked = sensor?.LiftOff == LiftOffDistance.Middle;
        LiftHigh.IsChecked = sensor?.LiftOff == LiftOffDistance.High;
        SnapOn.IsChecked = sensor?.AngleSnapping == true;
        SnapOff.IsChecked = sensor?.AngleSnapping == false;
        RippleOn.IsChecked = sensor?.RippleControl == true;
        RippleOff.IsChecked = sensor?.RippleControl == false;
        foreach (var chip in new[] { LiftLow, LiftMiddle, LiftHigh, SnapOn, SnapOff, RippleOn, RippleOff })
            chip.IsEnabled = mouseReady && sensor != null;

        foreach (var row in _rows)
        {
            row.Current = s.Matrix?[row.Button.Slot] is { } action ? Describe(action) : "-";
            row.Enabled = s.Matrix != null && mouseReady;
        }

        var ready = s.DongleConnected && !_busy;
        ResetButton.IsEnabled = mouseReady && s.Matrix != null;
        ReloadButton.IsEnabled = mouseReady;
        ClockButton.IsEnabled = ready;
        EraseButton.IsEnabled = ready;
        UploadButton.IsEnabled = ready && _image != null;
        UpdateDpiButtons();

        if (s.Error != null && s.DriverReady) FooterText.Text = s.Error;
    }

    internal static string Describe(ButtonAction action) => Loc.T(action.Describe());

    private void RenderDpiChips(DpiSettings? dpi, bool mouseReady)
    {
        DpiPanel.Children.Clear();
        if (dpi is null) return;
        for (var i = 0; i < dpi.Stages.Length; i++)
        {
            if (dpi.Stages[i] == 0) continue;
            var chip = new RadioButton
            {
                Style = (Style)FindResource("Chip"),
                GroupName = "OverviewDpi",
                Content = dpi.Stages[i].ToString(),
                Tag = i,
                IsChecked = i == dpi.ActiveIndex,
                IsEnabled = mouseReady,
                ToolTip = Loc.T("Make this the stage the mouse uses now"),
            };
            chip.Click += DpiStageChip_Click;
            DpiPanel.Children.Add(chip);
        }
    }

    private async void DpiStageChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: int index }) await SwitchDpiStageAsync(index);
    }

    private async Task SwitchDpiStageAsync(int index)
    {
        if (_state.Dpi is not { } dpi || dpi.ActiveIndex == index) return;
        await RunAsync(Loc.F("Switching to {0} DPI...", dpi.Stages[index]),
            () => App.Device.SetActiveDpiStageAsync(index),
            Loc.F("Now using stage {0} ({1} DPI).", index + 1, dpi.Stages[index]));
    }

    private void OnLanguageChanged()
    {
        foreach (var row in _rows) row.Relabel();
        foreach (var row in _dpiRows) row.Relabel();
        for (var i = 0; i < _slotChips.Count; i++) _slotChips[i].Content = Loc.F("Slot {0}", i + 1);
        PopulatePickers();
        if (_image != null && _imagePath != null) ImageInfo.Text = DescribeImage(_imagePath, _image);
        Render(_state);
    }

    private void PopulatePickers()
    {
        _populating = true;
        ThemeBox.ItemsSource = new[]
        {
            new PickerItem(AppTheme.System.ToString(), Loc.T("Match Windows")),
            new PickerItem(AppTheme.Light.ToString(), Loc.T("Light")),
            new PickerItem(AppTheme.Dark.ToString(), Loc.T("Dark")),
        };
        ThemeBox.DisplayMemberPath = nameof(PickerItem.Name);
        ThemeBox.SelectedIndex = (int)AppSettings.Theme;

        var languages = new List<PickerItem> { new("", Loc.T("Match Windows")) };
        languages.AddRange(Translations.Languages.Select(l => new PickerItem(l.Code, l.NativeName)));
        LanguageBox.ItemsSource = languages;
        LanguageBox.SelectedIndex = Math.Max(0, languages.FindIndex(l => l.Code == AppSettings.Language));
        _populating = false;
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populating || ThemeBox.SelectedItem is not PickerItem item) return;
        var theme = Enum.Parse<AppTheme>(item.Code);
        AppSettings.Theme = theme;
        ThemeManager.Apply(theme);
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populating || LanguageBox.SelectedItem is not PickerItem item) return;
        AppSettings.Language = item.Code;
        // Rebuilding the pickers from inside their own SelectionChanged confuses the ComboBox.
        Dispatcher.BeginInvoke(() => Loc.Instance.SetLanguage(item.Code));
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        if (message != null) FooterText.Text = message;
        Render(_state);
    }

    private async Task<bool> RunAsync(string working, Func<Task> action, string done)
    {
        SetBusy(true, working);
        try
        {
            await action();
            FooterText.Text = done;
            return true;
        }
        catch (OperationCanceledException)
        {
            FooterText.Text = Loc.T("Cancelled.");
        }
        catch (Exception ex)
        {
            FooterText.Text = Loc.T(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
        return false;
    }

    private async void RateChip_Click(object sender, RoutedEventArgs e)
    {
        if (_state.Sensor is not { } sensor || sender is not RadioButton { Tag: int hz }) return;
        if (sensor.PollingRate == hz) return;
        await RunAsync(Loc.F("Setting polling rate to {0} Hz...", hz),
            () => App.Device.SetSensorAsync(sensor with { PollingRate = hz }),
            Loc.F("Polling rate set to {0} Hz.", hz));
    }

    private async void Sensor_Click(object sender, RoutedEventArgs e)
    {
        if (_state.Sensor is not { } sensor) return;
        var wanted = sender switch
        {
            _ when sender == LiftLow => sensor with { LiftOff = LiftOffDistance.Low },
            _ when sender == LiftMiddle => sensor with { LiftOff = LiftOffDistance.Middle },
            _ when sender == LiftHigh => sensor with { LiftOff = LiftOffDistance.High },
            _ when sender == SnapOn => sensor with { AngleSnapping = true },
            _ when sender == SnapOff => sensor with { AngleSnapping = false },
            _ when sender == RippleOn => sensor with { RippleControl = true },
            _ when sender == RippleOff => sensor with { RippleControl = false },
            _ => sensor,
        };
        if (wanted == sensor) return;
        await RunAsync(Loc.T("Saving sensor setting..."), () => App.Device.SetSensorAsync(wanted), Loc.T("Sensor setting saved."));
    }

    private void LoadDpiRows(DpiSettings? dpi)
    {
        var source = dpi ?? Aj179Apex.DefaultDpi;
        for (var i = 0; i < _dpiRows.Count; i++)
            _dpiRows[i].Load(source.Stages[i], i == source.ActiveIndex && source.Stages[i] != 0);
    }

    private async void OnDpiEdited(DpiRow edited, bool activated)
    {
        if (activated)
        {
            foreach (var row in _dpiRows.Where(r => r != edited)) row.SetActiveQuietly(false);
            // A stage the mouse already has switches live; a newly enabled one waits for Apply with the rest.
            if (_state.Dpi is { } live && live.Stages[edited.Index] != 0)
            {
                await SwitchDpiStageAsync(edited.Index);
                return;
            }
        }
        else if (!_dpiRows.Any(r => r.IsActive && r.IsOn) && _dpiRows.FirstOrDefault(r => r.IsOn) is { } first)
        {
            first.SetActiveQuietly(true);
        }
        _dpiDirty = true;
        UpdateDpiButtons();
    }

    private void UpdateDpiButtons()
    {
        var mouseReady = _state.DongleConnected && _state.MouseOnline && !_busy && _state.Dpi != null;
        DpiApplyButton.IsEnabled = mouseReady && _dpiDirty;
        DpiUndoButton.IsEnabled = _dpiDirty && !_busy;
        DpiDefaultsButton.IsEnabled = mouseReady;
        DpiList.IsEnabled = mouseReady;
    }

    private void DpiText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    private async void DpiApply_Click(object sender, RoutedEventArgs e)
    {
        Keyboard.FocusedElement?.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
        if (!_dpiRows.Any(r => r.IsOn))
        {
            FooterText.Text = Loc.T("Keep at least one DPI stage switched on.");
            return;
        }
        var stages = _dpiRows.Select(r => r.IsOn ? r.Value : 0).ToArray();
        var active = _dpiRows.FirstOrDefault(r => r.IsActive && r.IsOn)?.Index ?? Array.FindIndex(stages, v => v != 0);
        var wanted = new DpiSettings(stages, active);

        if (await RunAsync(Loc.T("Saving DPI stages..."), () => App.Device.SetDpiAsync(wanted), Loc.T("DPI stages saved.")))
        {
            _dpiDirty = false;
            Render(_state);
        }
    }

    private void DpiUndo_Click(object sender, RoutedEventArgs e)
    {
        _dpiDirty = false;
        LoadDpiRows(_state.Dpi);
        UpdateDpiButtons();
    }

    private void DpiDefaults_Click(object sender, RoutedEventArgs e)
    {
        LoadDpiRows(Aj179Apex.DefaultDpi);
        _dpiDirty = true;
        UpdateDpiButtons();
        FooterText.Text = Loc.T("Default DPI stages loaded. Click \"Apply DPI\" to save them to the mouse.");
    }

    private async void CheckFirmware_Click(object sender, RoutedEventArgs e)
    {
        FirmwareButton.IsEnabled = false;
        FirmwareNote.Visibility = Visibility.Visible;
        FirmwareNote.Text = Loc.T("Asking the AJAZZ update server...");
        try
        {
            var release = await FirmwareUpdates.GetLatestAsync(Aj179Apex.FirmwareDeviceId);
            var current = _state.MouseFirmware;
            if (release is null)
            {
                FirmwareNote.Text = Loc.T("AJAZZ hasn't published any firmware updates for the AJ179 APEX, so you're on the latest version.");
            }
            else if (current is { } c && release.Version.Raw <= c.Raw)
            {
                FirmwareNote.Text = Loc.F("Firmware {0} is the latest version.", c);
            }
            else
            {
                FirmwareNote.Text = Loc.F("Firmware {0} is available.", release.Version);
                OfferFirmwareHandOff(release, current);
            }
        }
        catch (Exception ex)
        {
            FirmwareNote.Text = Loc.F("Couldn't reach the AJAZZ update server: {0}", ex.Message);
        }
        finally
        {
            FirmwareButton.IsEnabled = true;
        }
    }

    // Flashing reboots the mouse into its bootloader over the USB cable. The official app does that,
    // so ApexTray steps aside rather than running an unverified flasher.
    private void OfferFirmwareHandOff(FirmwareRelease release, FirmwareVersion? current)
    {
        var notes = string.IsNullOrWhiteSpace(release.Notes) ? "" : release.Notes + "\n\n";
        var text = Loc.F("Firmware {0} is available for the AJ179 APEX (installed: {1}).", release.Version, current?.ToString() ?? "?") + "\n\n" + notes
            + Loc.T("Firmware is installed by the official AJAZZ app over the USB cable: connect the mouse with its cable, then click \"Upgrade\" next to the firmware version. ApexTray closes while you do this so the two apps don't fight over the mouse.")
            + "\n\n" + Loc.T("Open the AJAZZ app now?");
        if (MessageBox.Show(this, text, Loc.T("Firmware update"), MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;

        if (!((App)Application.Current).HandOffToVendorApp())
            MessageBox.Show(this, Loc.T("The official AJAZZ Driver(R) app isn't installed, so the update can't be installed from here."),
                Loc.T("Firmware update"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void ButtonChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: ChoiceItem choice, Tag: ButtonRow row } combo) return;
        combo.SelectedIndex = -1;

        var action = choice.Action;
        if (action is null)
        {
            var capture = new KeyCaptureWindow { Owner = this };
            if (capture.ShowDialog() != true || capture.Result is not { } keys) return;
            action = keys;
        }

        var value = action.Value;
        await RunAsync(Loc.F("Setting {0} to {1}...", row.Name, Describe(value)),
            () => App.Device.SetButtonAsync(row.Button.Slot, value),
            $"{row.Name}: {Describe(value)}");
    }

    private async void ResetButtons_Click(object sender, RoutedEventArgs e)
        => await RunAsync(Loc.T("Restoring default buttons..."), () => App.Device.ResetButtonsAsync(), Loc.T("Buttons restored to defaults."));

    private async void Reload_Click(object sender, RoutedEventArgs e)
        => await RunAsync(Loc.T("Reading settings from the mouse..."), () => App.Device.RefreshDetailsAsync(), Loc.T("Settings reloaded."));

    private void StartupCheck_Changed(object sender, RoutedEventArgs e)
    {
        var want = StartupCheck.IsChecked == true;
        if (want != StartupRegistration.IsEnabled) StartupRegistration.Set(want);
    }

    private void LoadLowBatterySettings()
    {
        _populating = true;
        var levels = Enumerable.Range(1, 10).Select(i => i * 5).Append(AppSettings.LowBatteryLevel).Distinct().Order().ToList();
        LowBatteryLevelBox.ItemsSource = levels.Select(l => new PickerItem(l.ToString(), $"{l}%")).ToList();
        LowBatteryLevelBox.DisplayMemberPath = nameof(PickerItem.Name);
        LowBatteryLevelBox.SelectedIndex = levels.IndexOf(AppSettings.LowBatteryLevel);
        LowBatteryCheck.IsChecked = AppSettings.LowBatteryAlert;
        LowBatteryLevelBox.IsEnabled = AppSettings.LowBatteryAlert;
        _populating = false;
    }

    private void LowBattery_Changed(object sender, RoutedEventArgs e)
    {
        if (_populating) return;
        var on = LowBatteryCheck.IsChecked == true;
        AppSettings.LowBatteryAlert = on;
        LowBatteryLevelBox.IsEnabled = on;
        if (LowBatteryLevelBox.SelectedItem is PickerItem item) AppSettings.LowBatteryLevel = int.Parse(item.Code);
        ((App)Application.Current).LowBatterySettingsChanged();
    }

    private void TestLowBattery_Click(object sender, RoutedEventArgs e)
    {
        ((App)Application.Current).TestLowBatteryNotification();
        FooterText.Text = Loc.T("Test notification sent. If nothing appeared, turn on notifications for ApexTray in Windows Settings › System › Notifications.");
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            await LoadImageAsync(files[0]);
    }

    private async void DropZone_Click(object sender, MouseButtonEventArgs e) => await BrowseAsync();

    private async void Browse_Click(object sender, RoutedEventArgs e) => await BrowseAsync();

    private async Task BrowseAsync()
    {
        var dialog = new OpenFileDialog { Filter = ImageFilter, Title = Loc.T("Choose a picture for the dock") };
        if (dialog.ShowDialog(this) == true) await LoadImageAsync(dialog.FileName);
    }

    private async void Fit_Changed(object sender, RoutedEventArgs e)
    {
        if (_imagePath != null && IsLoaded) await LoadImageAsync(_imagePath);
    }

    private async Task LoadImageAsync(string path)
    {
        var mode = FitRadio.IsChecked == true ? FitMode.Fit : FitMode.Fill;
        FooterText.Text = Loc.T("Preparing image...");
        try
        {
            var image = await Task.Run(() => DockImageEncoder.Load(path, mode));
            _imagePath = path;
            _image = image;
            _previewFrames = image.Frames
                .Select(f =>
                {
                    var bmp = BitmapSource.Create(DockImage.Side, DockImage.Side, 96, 96, PixelFormats.Bgra32, null, f, DockImage.Side * 4);
                    bmp.Freeze();
                    return bmp;
                })
                .ToArray();
            _previewIndex = 0;
            Preview.Source = _previewFrames[0];
            DropHint.Visibility = Visibility.Collapsed;

            _animation.Stop();
            if (image.IsAnimated)
            {
                _animation.Interval = TimeSpan.FromMilliseconds(image.DelayMs);
                _animation.Start();
            }

            SlotPanel.Visibility = image.IsAnimated ? Visibility.Collapsed : Visibility.Visible;
            GifNote.Visibility = image.IsAnimated ? Visibility.Visible : Visibility.Collapsed;
            ImageInfo.Text = DescribeImage(path, image);
            FooterText.Text = Loc.T("Ready to upload.");
        }
        catch (Exception ex)
        {
            FooterText.Text = Loc.F("Couldn't open that image: {0}", ex.Message);
        }
        Render(_state);
    }

    private static string DescribeImage(string path, DockImage image)
    {
        var name = System.IO.Path.GetFileName(path);
        return image.IsAnimated ? name + " · " + Loc.F("{0} frames · {1} ms/frame", image.Frames.Count, image.DelayMs) : name;
    }

    private void AdvancePreview()
    {
        if (_previewFrames.Length == 0) return;
        _previewIndex = (_previewIndex + 1) % _previewFrames.Length;
        Preview.Source = _previewFrames[_previewIndex];
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (_image is null) return;
        var image = _image;
        var slot = _slotChips.FindIndex(c => c.IsChecked == true);
        if (slot < 0) slot = 0;
        _uploadCts = new CancellationTokenSource();
        UploadProgress.Value = 0;
        UploadProgress.Visibility = Visibility.Visible;
        CancelUploadButton.Visibility = Visibility.Visible;
        var progress = new Progress<double>(p => UploadProgress.Value = p);
        var token = _uploadCts.Token;

        await RunAsync(
            image.IsAnimated ? Loc.F("Uploading {0} frames to the dock...", image.Frames.Count) : Loc.F("Uploading to slot {0}...", slot + 1),
            () => Task.Run(() => App.Device.UploadImageAsync(image, slot, progress, token), token),
            Loc.T("Upload complete. Check the dock."));

        UploadProgress.Visibility = Visibility.Collapsed;
        CancelUploadButton.Visibility = Visibility.Collapsed;
        _uploadCts.Dispose();
        _uploadCts = null;
    }

    private void CancelUpload_Click(object sender, RoutedEventArgs e) => _uploadCts?.Cancel();

    private async void SyncClock_Click(object sender, RoutedEventArgs e)
        => await RunAsync(Loc.T("Syncing dock clock..."), () => App.Device.SyncClockAsync(), Loc.F("Dock clock set to {0:t}.", DateTime.Now));

    private async void Erase_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            Loc.T("This deletes every picture and animation stored on the dock. It takes about a minute.") + "\n\n" + Loc.T("Continue?"),
            Loc.T("Erase dock pictures"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        await RunAsync(Loc.T("Erasing dock pictures..."), async () =>
        {
            await App.Device.EraseDockImagesAsync();
            await Task.Delay(TimeSpan.FromSeconds(55));
        }, Loc.T("Dock pictures erased."));
    }
}

public sealed record PickerItem(string Code, string Name);

public sealed record ChoiceItem(string Label, ButtonAction? Action);

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ButtonRow(PhysicalButton button) : Observable
{
    private string _current = "-";
    private bool _enabled;

    public PhysicalButton Button { get; } = button;
    public string Name => Loc.T(Button.Name);

    public IReadOnlyList<ChoiceItem> Choices { get; private set; } = BuildChoices();

    public string Current
    {
        get => _current;
        set => Set(ref _current, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    public void Relabel()
    {
        Choices = BuildChoices();
        Raise(nameof(Choices));
        Raise(nameof(Name));
    }

    private static IReadOnlyList<ChoiceItem> BuildChoices() =>
    [
        new("⌨  " + Loc.T("Keyboard shortcut..."), null),
        .. ActionCatalog.All.Select(c => new ChoiceItem($"{Loc.T(c.Category)} · {Loc.T(c.Name)}", c.Action)),
    ];
}

// One editable DPI stage. A stage that's off is sent to the mouse as 0.
public sealed class DpiRow(int index) : Observable
{
    private bool _isOn, _isActive, _loading;
    private int _value = 800;

    public int Index { get; } = index;
    public string Label => Loc.F("Stage {0}", Index + 1);
    public string ActiveTip => Loc.T("Make this the stage the mouse uses now");

    // Raised for user edits only; the flag says whether this row just became the active one.
    public event Action<DpiRow, bool>? Edited;

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (!Set(ref _isOn, value)) return;
            if (!value && _isActive) SetActiveQuietly(false);
            OnEdited(false);
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (!Set(ref _isActive, value)) return;
            if (value) OnEdited(true);
        }
    }

    public int Value
    {
        get => _value;
        set
        {
            if (Set(ref _value, Aj179Apex.SnapDpi(value))) OnEdited(false);
            Raise(nameof(ValueText));
            Raise(nameof(SliderPosition));
        }
    }

    // 0-1000 on a squared scale: the range runs to 42,000 but most people live below 5,000,
    // which a linear slider would squeeze into its first tenth.
    public double SliderPosition
    {
        get => Math.Sqrt((_value - Aj179Apex.MinDpi) / (double)(Aj179Apex.MaxDpi - Aj179Apex.MinDpi)) * 1000;
        set => Value = Aj179Apex.MinDpi + (int)Math.Round(Math.Pow(value / 1000, 2) * (Aj179Apex.MaxDpi - Aj179Apex.MinDpi));
    }

    public string ValueText
    {
        get => _value.ToString();
        set
        {
            if (int.TryParse(value.Replace(".", "").Replace(",", "").Trim(), out var dpi)) Value = dpi;
            else Raise(nameof(ValueText));
        }
    }

    public void Load(int stage, bool active)
    {
        _loading = true;
        if (stage != 0) Value = stage;
        IsOn = stage != 0;
        SetActiveQuietly(active);
        _loading = false;
    }

    public void SetActiveQuietly(bool active)
    {
        _isActive = active;
        Raise(nameof(IsActive));
    }

    public void Relabel()
    {
        Raise(nameof(Label));
        Raise(nameof(ActiveTip));
    }

    private void OnEdited(bool activated)
    {
        if (!_loading) Edited?.Invoke(this, activated);
    }
}
