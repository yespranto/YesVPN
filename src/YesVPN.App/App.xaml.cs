using System.Windows;
using YesVPN.App.Services;
using YesVPN.Core.Ipc;

namespace YesVPN.App;

public partial class App : System.Windows.Application
{
    private TrayIconManager? _tray;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var startMinimized = e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
        var stateStore = new AppStateStore();
        var ipcClient = new IpcClient();

        _tray = new TrayIconManager();
        _mainWindow = new MainWindow(stateStore, ipcClient, _tray);

        _tray.OpenRequested += ShowMainWindow;
        _tray.ToggleConnectRequested += async () => await _mainWindow.ToggleConnectionFromTrayAsync();
        _tray.ExitRequested += async () => await ExitCleanlyAsync();

        if (!startMinimized)
        {
            ShowMainWindow();
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    private async Task ExitCleanlyAsync()
    {
        if (_mainWindow is not null)
        {
            await _mainWindow.PrepareForExitAsync();
            _mainWindow.AllowClose();
            _mainWindow.Close();
        }

        _tray?.Dispose();
        Shutdown();
    }
}
