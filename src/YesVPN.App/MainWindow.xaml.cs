using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using YesVPN.App.Services;
using YesVPN.Core.Config;
using YesVPN.Core.Diagnostics;
using YesVPN.Core.Health;
using YesVPN.Core.Ipc;
using YesVPN.Core.Models;
using YesVPN.Core.Parsing;

namespace YesVPN.App;

public sealed class ProxyRowItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ProxyEntry Entry { get; }
    public Guid Id => Entry.Id;
    public string DisplayLabel => Entry.DisplayLabel;
    public string Endpoint => Entry.HasCredentials ? $"{Entry.DisplayLabel} ({Entry.Username}:***)" : Entry.DisplayLabel;
    public string HealthText => Entry.HealthStatus switch
    {
        ProxyHealthStatus.Healthy => "Healthy",
        ProxyHealthStatus.Degraded => "Slow",
        ProxyHealthStatus.Dead => "Dead",
        _ => "Untested"
    };
    public string LatencyText => Entry.LatencyMs.HasValue ? $"{Entry.LatencyMs} ms" : "—";
    public string UdpText => Entry.SupportsUdp switch
    {
        true => "UDP OK",
        false => "TCP only",
        null => "?"
    };

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    public bool IsEnabled
    {
        get => Entry.IsEnabled;
        set
        {
            if (Entry.IsEnabled != value)
            {
                Entry.IsEnabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
            }
        }
    }

    public ProxyRowItem(ProxyEntry entry) => Entry = entry;

    public void NotifyTelemetryChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HealthText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LatencyText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UdpText)));
    }
}

public partial class MainWindow : Window
{
    private readonly AppStateStore _stateStore;
    private readonly IpcClient _ipcClient;
    private readonly TrayIconManager _tray;
    private readonly ProxyHealthStateMachine _healthStateMachine;
    private readonly ProxyHealthChecker _healthChecker;
    private readonly DispatcherTimer _statusPollTimer;
    private readonly DispatcherTimer _healthCheckTimer;
    private readonly HttpClient _ipHttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    private AppPersistedState _state;
    private ServiceStatus? _latestStatus;
    private TunnelState _lastVisualState = TunnelState.Disconnected;
    private bool _allowClose;
    private bool _isLoadingUi;
    private bool _isCheckingHealth;
    private bool _serviceReachable;
    private bool _isOrbitSpinning;

    public ObservableCollection<ProxyRowItem> ProxyRows { get; } = new();

    public MainWindow(AppStateStore stateStore, IpcClient ipcClient, TrayIconManager tray)
    {
        _stateStore = stateStore;
        _ipcClient = ipcClient;
        _tray = tray;
        _healthStateMachine = new ProxyHealthStateMachine();
        _healthChecker = new ProxyHealthChecker(_healthStateMachine);
        _state = _stateStore.Load();

        InitializeComponent();

        ProxyListView.ItemsSource = ProxyRows;
        LoadStateIntoUi();

        _statusPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusPollTimer.Tick += async (_, _) => await PollServiceStatusAsync();
        _statusPollTimer.Start();

        _healthCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _healthCheckTimer.Tick += async (_, _) => await RunHealthChecksAsync(force: false);
        _healthCheckTimer.Start();

        Loaded += async (_, _) =>
        {
            await PollServiceStatusAsync();
            await RefreshPublicIpAsync();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        EnableDarkTitleBar();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private void EnableDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var useDark = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref useDark, sizeof(int)) != 0)
            {
                _ = DwmSetWindowAttribute(hwnd, 19, ref useDark, sizeof(int));
            }
        }
        catch
        {
            // Ignore on unsupported OS versions
        }
    }

    public void AllowClose() => _allowClose = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _statusPollTimer.Stop();
        _healthCheckTimer.Stop();
        _ipHttpClient.Dispose();
        base.OnClosing(e);
    }

    public async Task PrepareForExitAsync()
    {
        if (_latestStatus?.State is TunnelState.Connected or TunnelState.Connecting or TunnelState.Reconnecting)
        {
            try
            {
                await _ipcClient.DisconnectAsync();
            }
            catch
            {
                // Best effort on shutdown
            }
        }
        SaveState();
    }

    public async Task ToggleConnectionFromTrayAsync()
    {
        await Dispatcher.InvokeAsync(async () => await ToggleConnectionAsync());
    }

    private void LoadStateIntoUi()
    {
        _isLoadingUi = true;
        try
        {
            PrivacyNoticeBanner.Visibility = _state.FirstRunNoticeDismissed ? Visibility.Collapsed : Visibility.Visible;

            ModeComboBox.SelectedIndex = _state.Options.Mode switch
            {
                ProxyMode.LoadBalance => 1,
                ProxyMode.Manual => 2,
                _ => 0
            };
            UpdateModeVisibility();

            KillSwitchCheckBox.IsChecked = _state.Options.KillSwitchEnabled;
            BlockUdpCheckBox.IsChecked = _state.Options.BlockNonDnsUdp;
            StartWithWindowsCheckBox.IsChecked = _state.StartWithWindows || StartupManager.IsEnabled();
            UpdateKillSwitchPill();

            BypassDomainsTextBox.Text = string.Join(Environment.NewLine, _state.Options.SplitTunnel.BypassDomains);
            BypassIpsTextBox.Text = string.Join(Environment.NewLine, _state.Options.SplitTunnel.BypassIps);
            BypassAppsTextBox.Text = string.Join(Environment.NewLine, _state.Options.SplitTunnel.BypassApps);

            RefreshProxyListBindings();

            if (_state.Proxies.Count == 0)
            {
                AddProxyPanel.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _isLoadingUi = false;
        }
    }

    private void UpdateModeVisibility()
    {
        ManualProxyPanel.Visibility = _state.Options.Mode == ProxyMode.Manual ? Visibility.Visible : Visibility.Collapsed;
        LoadBalanceHint.Visibility = _state.Options.Mode == ProxyMode.LoadBalance ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateKillSwitchPill()
    {
        if (_state.Options.KillSwitchEnabled)
        {
            KillSwitchPillText.Text = "Kill Switch: ON";
            KillSwitchPillText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
        }
        else
        {
            KillSwitchPillText.Text = "Kill Switch: OFF";
            KillSwitchPillText.Foreground = (System.Windows.Media.Brush)FindResource("TextMuted");
        }
    }

    private void RefreshProxyListBindings()
    {
        var prevChecked = ProxyRows.Where(r => r.IsSelected).Select(r => r.Id).ToHashSet();
        ProxyRows.Clear();
        foreach (var p in _state.Proxies)
        {
            ProxyRows.Add(new ProxyRowItem(p) { IsSelected = prevChecked.Contains(p.Id) });
        }

        var prevLoading = _isLoadingUi;
        _isLoadingUi = true;
        try
        {
            ManualProxyComboBox.ItemsSource = null;
            ManualProxyComboBox.ItemsSource = _state.Proxies;
            if (_state.Options.ManualProxyId.HasValue)
            {
                ManualProxyComboBox.SelectedItem = _state.Proxies.FirstOrDefault(p => p.Id == _state.Options.ManualProxyId.Value);
            }
            if (ManualProxyComboBox.SelectedItem is null && _state.Proxies.Count > 0)
            {
                ManualProxyComboBox.SelectedIndex = 0;
            }
        }
        finally
        {
            _isLoadingUi = prevLoading;
        }

        UpdateProxySummaryAndSelectionUi();
    }

    private void UpdateProxySummaryAndSelectionUi()
    {
        var healthyCount = _state.Proxies.Count(p => p.IsEnabled && p.HealthStatus != ProxyHealthStatus.Dead);
        ProxySummaryText.Text = $"{_state.Proxies.Count} total ({healthyCount} available)";

        var hasTcpOnly = _state.Proxies.Any(p => p.IsEnabled && p.SupportsUdp == false);
        UdpWarningBanner.Visibility = hasTcpOnly ? Visibility.Visible : Visibility.Collapsed;

        var markedCount = GetTargetedProxyIds().Count;
        DeleteSelectedButton.Content = markedCount > 0 ? $"Delete ({markedCount})" : "Delete Selected";
        DeleteSelectedButton.IsEnabled = markedCount > 0;

        if (ProxyRows.Count == 0)
        {
            SelectAllProxiesCheckBox.IsChecked = false;
        }
        else if (ProxyRows.All(r => r.IsSelected))
        {
            SelectAllProxiesCheckBox.IsChecked = true;
        }
        else if (ProxyRows.Any(r => r.IsSelected))
        {
            SelectAllProxiesCheckBox.IsChecked = null;
        }
        else
        {
            SelectAllProxiesCheckBox.IsChecked = false;
        }
    }

    private HashSet<Guid> GetTargetedProxyIds()
    {
        var ids = ProxyRows.Where(r => r.IsSelected).Select(r => r.Id).ToHashSet();
        foreach (ProxyRowItem row in ProxyListView.SelectedItems)
        {
            ids.Add(row.Id);
        }
        return ids;
    }

    private void SaveState()
    {
        try
        {
            _stateStore.Save(_state);
        }
        catch (Exception ex)
        {
            ShowError($"Failed to save encrypted settings: {ex.Message}");
        }
    }

    private async Task PollServiceStatusAsync()
    {
        try
        {
            var resp = await _ipcClient.GetStatusAsync();
            _serviceReachable = resp.Ok;
            _latestStatus = resp.Status;

            if (_latestStatus?.IsElevated == true)
            {
                ServiceStatusBadge.Text = "Service ready (Admin)";
                ServiceStatusBadge.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
                ElevateServiceButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                ServiceStatusBadge.Text = "Service needs Admin";
                ServiceStatusBadge.Foreground = (System.Windows.Media.Brush)FindResource("WarningBrush");
                ElevateServiceButton.Content = "Elevate";
                ElevateServiceButton.Visibility = FindServiceExecutablePath() is not null ? Visibility.Visible : Visibility.Collapsed;
            }

            UpdateConnectionUi(_latestStatus);
        }
        catch
        {
            _serviceReachable = false;
            _latestStatus = null;
            ServiceStatusBadge.Text = "Service offline";
            ServiceStatusBadge.Foreground = (System.Windows.Media.Brush)FindResource("WarningBrush");
            ElevateServiceButton.Content = "Start Service";
            ElevateServiceButton.Visibility = FindServiceExecutablePath() is not null ? Visibility.Visible : Visibility.Collapsed;
            UpdateConnectionUi(null);
        }
    }

    private void UpdateConnectionUi(ServiceStatus? status)
    {
        var state = status?.State ?? TunnelState.Disconnected;
        _tray.UpdateState(state, status?.ActiveProxyLabel);
        UpdateSpeedMeter(status);

        var transitionChanged = state != _lastVisualState;
        _lastVisualState = state;

        switch (state)
        {
            case TunnelState.Connected:
                ConnectButton.Content = "Disconnect";
                ConnectButton.Tag = "Traffic protected";
                ConnectionStatusText.Text = string.IsNullOrWhiteSpace(status?.ActiveProxyLabel)
                    ? "Connected"
                    : $"Connected via {status!.ActiveProxyLabel}";
                AnimateRingColor(System.Windows.Media.Color.FromRgb(34, 197, 94));
                StopBusyOrbitAnimation();
                if (transitionChanged)
                {
                    PlayPulseBurst(System.Windows.Media.Color.FromRgb(34, 197, 94));
                    PlayButtonBounce();
                    AnimateStatusTextIn();
                }
                HideError();
                break;

            case TunnelState.Connecting:
                ConnectButton.Content = "Cancel";
                ConnectButton.Tag = "Starting tunnel...";
                ConnectionStatusText.Text = "Connecting...";
                AnimateRingColor(System.Windows.Media.Color.FromRgb(245, 158, 11));
                StartBusyOrbitAnimation(System.Windows.Media.Color.FromRgb(245, 158, 11));
                if (transitionChanged)
                {
                    AnimateStatusTextIn();
                }
                break;

            case TunnelState.Reconnecting:
                ConnectButton.Content = "Disconnect";
                ConnectButton.Tag = "Recovering...";
                ConnectionStatusText.Text = status?.KillSwitchActive == true
                    ? "Reconnecting (Kill switch active)..."
                    : "Reconnecting...";
                AnimateRingColor(System.Windows.Media.Color.FromRgb(245, 158, 11));
                StartBusyOrbitAnimation(System.Windows.Media.Color.FromRgb(245, 158, 11));
                if (transitionChanged)
                {
                    AnimateStatusTextIn();
                }
                break;

            case TunnelState.Error:
                ConnectButton.Content = "Connect";
                ConnectButton.Tag = "Tap to retry";
                ConnectionStatusText.Text = "Disconnected";
                AnimateRingColor(System.Windows.Media.Color.FromRgb(239, 68, 68));
                StopBusyOrbitAnimation();
                if (transitionChanged)
                {
                    PlayButtonBounce();
                    AnimateStatusTextIn();
                }
                if (!string.IsNullOrWhiteSpace(status?.LastError))
                {
                    ShowError(status.LastError);
                }
                break;

            default:
                ConnectButton.Content = "Connect";
                ConnectButton.Tag = "Tap to route traffic";
                ConnectionStatusText.Text = "Disconnected";
                AnimateRingColor(System.Windows.Media.Color.FromRgb(59, 130, 246), System.Windows.Media.Color.FromRgb(100, 116, 139));
                StopBusyOrbitAnimation();
                if (transitionChanged)
                {
                    PlayPulseBurst(System.Windows.Media.Color.FromRgb(59, 130, 246));
                    PlayButtonBounce();
                    AnimateStatusTextIn();
                }
                break;
        }
    }

    #region Connect/Disconnect Animations

    private void AnimateRingColor(System.Windows.Media.Color ringTarget, System.Windows.Media.Color? dotTarget = null)
    {
        var duration = TimeSpan.FromMilliseconds(260);
        if (ConnectButton.Template.FindName("ConnectRingColor", ConnectButton) is SolidColorBrush ringBrush)
        {
            ringBrush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation(ringTarget, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        HeaderDotBrush.BeginAnimation(
            SolidColorBrush.ColorProperty,
            new ColorAnimation(dotTarget ?? ringTarget, duration));
    }

    private void PlayButtonClickPress()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var downX = new DoubleAnimation(1.0, 0.94, TimeSpan.FromMilliseconds(90)) { AutoReverse = true, EasingFunction = ease };
        var downY = new DoubleAnimation(1.0, 0.94, TimeSpan.FromMilliseconds(90)) { AutoReverse = true, EasingFunction = ease };
        ButtonScale.BeginAnimation(ScaleTransform.ScaleXProperty, downX);
        ButtonScale.BeginAnimation(ScaleTransform.ScaleYProperty, downY);
    }

    private void PlayButtonBounce()
    {
        var bounce = new DoubleAnimationUsingKeyFrames();
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1.05, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)), new CubicEase { EasingMode = EasingMode.EaseInOut }));
        ButtonScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
        ButtonScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
    }

    private void PlayPulseBurst(System.Windows.Media.Color color)
    {
        PulseRingBrush.Color = color;
        var scaleAnim = new DoubleAnimation(0.96, 1.24, TimeSpan.FromMilliseconds(550))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var opacityAnim = new DoubleAnimation(0.65, 0.0, TimeSpan.FromMilliseconds(550))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        PulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        PulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        PulseRing.BeginAnimation(OpacityProperty, opacityAnim);
    }

    private void StartBusyOrbitAnimation(System.Windows.Media.Color color)
    {
        OrbitArcBrush.Color = color;
        if (_isOrbitSpinning)
        {
            return;
        }
        _isOrbitSpinning = true;

        OrbitArc.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(200)));
        var spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(1250))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        OrbitRotate.BeginAnimation(RotateTransform.AngleProperty, spin);

        PulseRingBrush.Color = color;
        var pulseScale = new DoubleAnimation(1.0, 1.16, TimeSpan.FromMilliseconds(1100))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var pulseOpacity = new DoubleAnimation(0.45, 0.0, TimeSpan.FromMilliseconds(1100))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        PulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulseScale);
        PulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulseScale);
        PulseRing.BeginAnimation(OpacityProperty, pulseOpacity);
    }

    private void StopBusyOrbitAnimation()
    {
        if (!_isOrbitSpinning)
        {
            return;
        }
        _isOrbitSpinning = false;

        OrbitArc.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(200)));
        OrbitRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        PulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PulseRing.BeginAnimation(OpacityProperty, null);
        PulseRing.Opacity = 0;
    }

    private void AnimateStatusTextIn()
    {
        var slide = new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var fade = new DoubleAnimation(0.25, 1.0, TimeSpan.FromMilliseconds(220));
        StatusTextSlide.BeginAnimation(TranslateTransform.YProperty, slide);
        ConnectionStatusText.BeginAnimation(OpacityProperty, fade);
    }

    private void UpdateSpeedMeter(ServiceStatus? status)
    {
        if (status?.State != TunnelState.Connected)
        {
            DownloadSpeedText.Text = "0 B/s";
            UploadSpeedText.Text = "0 B/s";
            TotalDownloadText.Text = "Total: 0 B";
            TotalUploadText.Text = "Total: 0 B";
            AnimateMeterBar(DownloadMeterBar, 0);
            AnimateMeterBar(UploadMeterBar, 0);
            return;
        }

        DownloadSpeedText.Text = FormatSpeed(status.DownloadBytesPerSec);
        UploadSpeedText.Text = FormatSpeed(status.UploadBytesPerSec);
        TotalDownloadText.Text = $"Total: {FormatBytes(status.TotalDownloadBytes)}";
        TotalUploadText.Text = $"Total: {FormatBytes(status.TotalUploadBytes)}";

        AnimateMeterBar(DownloadMeterBar, ToLogMeterPercent(status.DownloadBytesPerSec));
        AnimateMeterBar(UploadMeterBar, ToLogMeterPercent(status.UploadBytesPerSec));
    }

    private static void AnimateMeterBar(System.Windows.Controls.ProgressBar bar, double target)
    {
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(350))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, anim);
    }

    private static double ToLogMeterPercent(long bytesPerSec) =>
        bytesPerSec <= 0 ? 0 : Math.Min(100.0, Math.Log10(bytesPerSec + 1) * 13.0);

    public static string FormatSpeed(long bytesPerSec) => $"{FormatBytes(bytesPerSec)}/s";

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0.0} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:0.00} MB";
        double gb = mb / 1024.0;
        return $"{gb:0.00} GB";
    }

    #endregion

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        PlayButtonClickPress();
        await ToggleConnectionAsync();
    }

    private async void ElevateService_Click(object sender, RoutedEventArgs e)
    {
        ElevateServiceButton.IsEnabled = false;
        try
        {
            await EnsureElevatedServiceAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            ElevateServiceButton.IsEnabled = true;
        }
    }

    private async Task<bool> EnsureElevatedServiceAsync()
    {
        if (_serviceReachable && _latestStatus?.IsElevated == true)
        {
            return true;
        }

        var svcExe = FindServiceExecutablePath();
        if (svcExe is null)
        {
            ShowError("YesVPN.Service.exe could not be located. Install or start YesVPN.Service as Administrator.");
            return false;
        }

        if (_serviceReachable && _latestStatus?.IsElevated == false)
        {
            try
            {
                await _ipcClient.ShutdownIfUnprivilegedAsync();
                await Task.Delay(350);
            }
            catch
            {
                // Ignore if already exited
            }
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = svcExe,
                WorkingDirectory = Path.GetDirectoryName(svcExe)!,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ShowError("Administrator approval is required to start the tunnel adapter.");
            return false;
        }

        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(300);
            await PollServiceStatusAsync();
            if (_serviceReachable && _latestStatus?.IsElevated == true)
            {
                HideError();
                return true;
            }
        }

        ShowError("Elevated service did not become ready in time.");
        return false;
    }

    private static string? FindServiceExecutablePath()
    {
        var baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "YesVPN.Service.exe"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "Service", "YesVPN.Service.exe")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "service", "YesVPN.Service.exe"))
        ];

        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                return c;
            }
        }

        var dir = new DirectoryInfo(baseDir);
        while (dir is not null)
        {
            var devSvc = Path.Combine(dir.FullName, "src", "YesVPN.Service", "bin", "Release", "net8.0-windows", "YesVPN.Service.exe");
            if (File.Exists(devSvc))
            {
                return devSvc;
            }
            dir = dir.Parent;
        }

        return null;
    }

    private async Task ToggleConnectionAsync()
    {
        var currentState = _latestStatus?.State ?? TunnelState.Disconnected;
        if (currentState is TunnelState.Connected or TunnelState.Connecting or TunnelState.Reconnecting)
        {
            ConnectButton.IsEnabled = false;
            StartBusyOrbitAnimation(System.Windows.Media.Color.FromRgb(245, 158, 11));
            ConnectionStatusText.Text = "Disconnecting...";
            try
            {
                var resp = await _ipcClient.DisconnectAsync();
                _latestStatus = resp.Status;
                UpdateConnectionUi(_latestStatus);
                await RefreshPublicIpAsync();
            }
            catch (Exception ex)
            {
                StopBusyOrbitAnimation();
                ShowError($"Disconnect failed: {ex.Message}");
            }
            finally
            {
                ConnectButton.IsEnabled = true;
            }
            return;
        }

        if (_state.Proxies.Count == 0)
        {
            ShowError("Add at least one SOCKS5 proxy in the Proxies tab first.");
            MainTabs.SelectedIndex = 1;
            return;
        }

        ConnectButton.IsEnabled = false;
        HideError();
        UpdateConnectionUi(new ServiceStatus { State = TunnelState.Connecting });
        try
        {
            if (!_serviceReachable || _latestStatus?.IsElevated != true)
            {
                var elevated = await EnsureElevatedServiceAsync();
                if (!elevated)
                {
                    UpdateConnectionUi(new ServiceStatus { State = TunnelState.Disconnected });
                    return;
                }
            }

            await ConnectWithCurrentStateAsync();
            await RefreshPublicIpAsync();
        }
        catch (NoHealthyProxiesException ex)
        {
            UpdateConnectionUi(new ServiceStatus { State = TunnelState.Error, LastError = $"{ex.Message} Run 'Test All' in the Proxies tab to check proxy recovery." });
        }
        catch (Exception ex)
        {
            UpdateConnectionUi(new ServiceStatus { State = TunnelState.Error, LastError = $"Connect failed: {ex.Message}" });
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private async Task ConnectWithCurrentStateAsync()
    {
        var usable = ProxyHealthStateMachine.GetUsableProxiesOrdered(_state.Proxies);
        if (usable.Count == 0)
        {
            throw new NoHealthyProxiesException("All configured proxies are currently unreachable (Dead) or disabled.");
        }

        var configJson = SingBoxConfigBuilder.BuildJson(usable, _state.Options);
        var activeLabel = _state.Options.Mode switch
        {
            ProxyMode.Auto => $"Auto ({usable.Count} proxies)",
            ProxyMode.LoadBalance => $"Load Balance ({usable.Count} proxies)",
            _ => usable.FirstOrDefault(p => p.Id == _state.Options.ManualProxyId)?.DisplayLabel ?? usable[0].DisplayLabel
        };

        var hosts = usable.Select(p => p.Host).ToList();
        var lbProxies = _state.Options.Mode == ProxyMode.LoadBalance ? usable : null;

        var resp = await _ipcClient.ConnectAsync(
            configJson,
            activeLabel,
            _state.Options.Mode.ToString(),
            _state.Options.KillSwitchEnabled,
            _state.Options.BlockNonDnsUdp,
            hosts,
            lbProxies);

        if (!resp.Ok)
        {
            throw new InvalidOperationException(resp.Error ?? "Service rejected connect request.");
        }

        _latestStatus = resp.Status;
        UpdateConnectionUi(_latestStatus);
    }

    private async Task RefreshPublicIpAsync()
    {
        try
        {
            PublicIpText.Text = "Public IP: checking...";
            var ip = await _ipHttpClient.GetStringAsync("https://api.ipify.org");
            PublicIpText.Text = $"Public IP: {ip.Trim()}";
        }
        catch
        {
            PublicIpText.Text = "Public IP: unavailable";
        }
    }

    private async void RefreshIp_Click(object sender, RoutedEventArgs e) => await RefreshPublicIpAsync();

    private void DismissPrivacyNotice_Click(object sender, RoutedEventArgs e)
    {
        _state.FirstRunNoticeDismissed = true;
        PrivacyNoticeBanner.Visibility = Visibility.Collapsed;
        SaveState();
    }

    private async void ModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingUi)
        {
            return;
        }

        _state.Options.Mode = ModeComboBox.SelectedIndex switch
        {
            1 => ProxyMode.LoadBalance,
            2 => ProxyMode.Manual,
            _ => ProxyMode.Auto
        };
        UpdateModeVisibility();
        SaveState();

        if (_latestStatus?.State == TunnelState.Connected)
        {
            try
            {
                await ConnectWithCurrentStateAsync();
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
        }
    }

    private async void ManualProxyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingUi)
        {
            return;
        }

        if (ManualProxyComboBox.SelectedItem is ProxyEntry selected)
        {
            if (_state.Options.ManualProxyId == selected.Id)
            {
                return;
            }

            _state.Options.ManualProxyId = selected.Id;
            SaveState();

            if (_latestStatus?.State == TunnelState.Connected && _state.Options.Mode == ProxyMode.Manual)
            {
                try
                {
                    await ConnectWithCurrentStateAsync();
                }
                catch (Exception ex)
                {
                    ShowError(ex.Message);
                }
            }
        }
    }

    private void ToggleAddPanel_Click(object sender, RoutedEventArgs e)
    {
        AddProxyPanel.Visibility = AddProxyPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        PasteErrorsText.Visibility = Visibility.Collapsed;
    }

    private void CancelAdd_Click(object sender, RoutedEventArgs e)
    {
        AddProxyPanel.Visibility = Visibility.Collapsed;
        PasteProxiesTextBox.Clear();
        PasteErrorsText.Visibility = Visibility.Collapsed;
    }

    private async void ConfirmAddProxies_Click(object sender, RoutedEventArgs e)
    {
        var result = ProxyParser.Parse(PasteProxiesTextBox.Text);
        if (result.HasErrors)
        {
            PasteErrorsText.Text = string.Join(
                Environment.NewLine,
                result.Errors.Select(err => $"Line {err.LineNumber} ({err.MaskedLine}): {err.Message}"));
            PasteErrorsText.Visibility = Visibility.Visible;
        }
        else
        {
            PasteErrorsText.Visibility = Visibility.Collapsed;
        }

        if (result.Entries.Count > 0)
        {
            _state.Proxies.AddRange(result.Entries);
            SaveState();
            RefreshProxyListBindings();

            if (!result.HasErrors)
            {
                PasteProxiesTextBox.Clear();
                AddProxyPanel.Visibility = Visibility.Collapsed;
            }

            await RunHealthChecksAsync(force: true, quickMode: true);
        }
        else if (!result.HasErrors)
        {
            PasteErrorsText.Text = "No proxy entries found.";
            PasteErrorsText.Visibility = Visibility.Visible;
        }
    }

    private async void QuickTest_Click(object sender, RoutedEventArgs e) => await RunHealthChecksAsync(force: true, quickMode: true);

    private async void TestAll_Click(object sender, RoutedEventArgs e) => await RunHealthChecksAsync(force: true, quickMode: false);

    private async Task RunHealthChecksAsync(bool force, bool quickMode = false)
    {
        if (_isCheckingHealth || _state.Proxies.Count == 0)
        {
            return;
        }

        _isCheckingHealth = true;
        TestAllButton.IsEnabled = false;
        QuickTestButton.IsEnabled = false;
        QuickTestButton.Content = quickMode ? "Quick Testing..." : "Quick Test";
        TestAllButton.Content = quickMode ? "Full Test" : "Testing...";

        try
        {
            var snapshot = _state.Proxies.ToList();
            var completed = 0;
            var total = snapshot.Count;
            var rowById = ProxyRows.ToDictionary(r => r.Id);

            await Task.Run(() => _healthChecker.CheckAllAsync(
                snapshot,
                force: force,
                maxConcurrency: quickMode ? 24 : 12,
                checkUdp: !quickMode,
                timeoutOverride: quickMode ? TimeSpan.FromMilliseconds(2500) : null,
                onProxyChecked: proxy =>
                {
                    var done = Interlocked.Increment(ref completed);
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (rowById.TryGetValue(proxy.Id, out var row))
                        {
                            row.NotifyTelemetryChanged();
                        }
                        if (quickMode)
                        {
                            QuickTestButton.Content = $"Quick ({done}/{total})";
                        }
                        else
                        {
                            TestAllButton.Content = $"Full ({done}/{total})";
                        }
                        UpdateProxySummaryAndSelectionUi();
                    });
                }));

            SaveState();
            UpdateProxySummaryAndSelectionUi();

            if (_latestStatus?.State == TunnelState.Connected &&
                _state.Options.Mode is ProxyMode.Auto or ProxyMode.LoadBalance)
            {
                var usable = ProxyHealthStateMachine.GetUsableProxiesOrdered(_state.Proxies);
                if (usable.Count == 0)
                {
                    ShowError("All proxies are unreachable. Kill switch is holding traffic.");
                }
            }
        }
        finally
        {
            _isCheckingHealth = false;
            TestAllButton.IsEnabled = true;
            QuickTestButton.IsEnabled = true;
            QuickTestButton.Content = "Quick Test";
            TestAllButton.Content = "Full Test";
        }
    }

    private void SelectAllProxies_Click(object sender, RoutedEventArgs e)
    {
        var selectAll = SelectAllProxiesCheckBox.IsChecked == true;
        foreach (var row in ProxyRows)
        {
            row.IsSelected = selectAll;
        }
        UpdateProxySummaryAndSelectionUi();
    }

    private void MarkDeadProxies_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in ProxyRows)
        {
            row.IsSelected = row.Entry.HealthStatus == ProxyHealthStatus.Dead;
        }
        UpdateProxySummaryAndSelectionUi();
    }

    private void ProxySelectionCheck_Click(object sender, RoutedEventArgs e) => UpdateProxySummaryAndSelectionUi();

    private void ProxyListView_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateProxySummaryAndSelectionUi();

    private void ProxyListView_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.A &&
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control)
        {
            foreach (var row in ProxyRows)
            {
                row.IsSelected = true;
            }
            ProxyListView.SelectAll();
            UpdateProxySummaryAndSelectionUi();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Delete)
        {
            DeleteSelectedProxies();
            e.Handled = true;
        }
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e) => DeleteSelectedProxies();

    private void DeleteSelectedProxies()
    {
        var selected = GetTargetedProxyIds();
        if (selected.Count == 0)
        {
            return;
        }

        _state.Proxies.RemoveAll(p => selected.Contains(p.Id));
        SaveState();
        RefreshProxyListBindings();
    }

    private void ProxyEnabled_Click(object sender, RoutedEventArgs e)
    {
        SaveState();
        RefreshProxyListBindings();
    }

    private void SettingToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi)
        {
            return;
        }

        _state.Options.KillSwitchEnabled = KillSwitchCheckBox.IsChecked == true;
        _state.Options.BlockNonDnsUdp = BlockUdpCheckBox.IsChecked == true;
        UpdateKillSwitchPill();
        SaveState();
    }

    private void StartWithWindows_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi)
        {
            return;
        }

        var enabled = StartWithWindowsCheckBox.IsChecked == true;
        try
        {
            StartupManager.SetEnabled(enabled);
            _state.StartWithWindows = enabled;
            SaveState();
        }
        catch (Exception ex)
        {
            ShowError($"Failed to update Windows startup setting: {ex.Message}");
        }
    }

    private void SaveSplitTunnel_Click(object sender, RoutedEventArgs e)
    {
        _state.Options.SplitTunnel.BypassDomains = ParseLines(BypassDomainsTextBox.Text);
        _state.Options.SplitTunnel.BypassIps = ParseLines(BypassIpsTextBox.Text);
        _state.Options.SplitTunnel.BypassApps = ParseLines(BypassAppsTextBox.Text);
        SaveState();
        System.Windows.MessageBox.Show(this, "Split-tunnel rules saved. Reconnect to apply changes.", "YesVPN", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void ViewLogs_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> logs;
        try
        {
            var resp = await _ipcClient.GetLogsAsync(300);
            logs = resp.Logs ?? Array.Empty<string>();
        }
        catch
        {
            logs = ["YesVPN.Service is not running or unreachable."];
        }

        var logWin = new Window
        {
            Title = "YesVPN Service Logs (Credentials Masked)",
            Owner = this,
            Width = 700,
            Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)FindResource("BgPrimary"),
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary"),
            Icon = Icon,
            Content = new System.Windows.Controls.TextBox
            {
                Text = logs.Count == 0 ? "(No logs yet)" : string.Join(Environment.NewLine, logs),
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 12,
                Margin = new Thickness(12)
            }
        };
        logWin.ShowDialog();
    }

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> logs = Array.Empty<string>();
        try
        {
            var resp = await _ipcClient.GetLogsAsync(300);
            logs = resp.Logs ?? Array.Empty<string>();
        }
        catch
        {
            logs = ["(YesVPN.Service unreachable)"];
        }

        var report = DiagnosticsExporter.BuildReport(_latestStatus, _state.Proxies, _state.Options, logs);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export YesVPN Diagnostics",
            Filter = "Text files (*.txt)|*.txt",
            FileName = $"YesVPN-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt"
        };

        if (dialog.ShowDialog(this) == true)
        {
            await File.WriteAllTextAsync(dialog.FileName, report);
            System.Windows.MessageBox.Show(this, "Diagnostics exported (all proxy credentials masked).", "YesVPN", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void AboutLicenses_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.MessageBox.Show(
            this,
            "YesVPN v1.0.0\n" +
            "Author: Pranto Biswas\n" +
            "Website: https://www.yespranto.com\n" +
            "Contact: hello@yespranto.com\n\n" +
            "Routes Windows system traffic through multiple SOCKS5 proxies with automatic failover and load balancing.\n\n" +
            "Third-Party Components:\n" +
            "• sing-box — GNU General Public License v3.0 (GPL-3.0)\n" +
            "• Wintun — WireGuard / Jason A. Donenfeld License\n\n" +
            "Full LICENSE files are included in the installation directory.",
            "About YesVPN",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static List<string> ParseLines(string text) =>
        text.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith('#'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void ShowError(string message)
    {
        ErrorBannerText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorBanner.Visibility = Visibility.Collapsed;
}
