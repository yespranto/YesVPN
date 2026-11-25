using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using YesVPN.Core.Security;
using YesVPN.Service.Logging;

namespace YesVPN.Service.Engine;

public interface ISingBoxRunner : IDisposable
{
    int? ProcessId { get; }
    bool IsRunning { get; }
    string? SingBoxPath { get; }
    string? SingBoxVersion { get; }

    event Action<int>? UnexpectedExit;

    Task StartAsync(string configJson, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task CleanupOrphansAsync(CancellationToken ct = default);
}

/// <summary>
/// Manages the bundled sing-box process lifecycle, locked-down runtime config file, and log capture.
/// </summary>
public sealed class SingBoxProcessRunner : ISingBoxRunner
{
    private readonly LogRingBuffer _logs;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _runtimeDir;
    private Process? _process;
    private string? _activeConfigPath;
    private bool _intentionalStop;
    private bool _startupVerified;

    public int? ProcessId => _process is { HasExited: false } p ? p.Id : null;
    public bool IsRunning => _process is { HasExited: false };
    public string? SingBoxPath { get; }
    public string? SingBoxVersion { get; }

    public event Action<int>? UnexpectedExit;

    public SingBoxProcessRunner(LogRingBuffer logs, string? runtimeDir = null)
    {
        _logs = logs;
        _runtimeDir = runtimeDir ?? ResolveDefaultRuntimeDir();
        SingBoxPath = ResolveSingBoxPath();
        SingBoxVersion = ResolveSingBoxVersion(SingBoxPath);
    }

    public async Task StartAsync(string configJson, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configJson);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopInternalAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrEmpty(SingBoxPath) || !File.Exists(SingBoxPath))
            {
                throw new FileNotFoundException(
                    "Bundled sing-box.exe could not be located. Ensure third_party/sing-box/sing-box.exe is installed.");
            }

            Directory.CreateDirectory(_runtimeDir);
            ApplyRestrictedDirectoryAcl(_runtimeDir);

            var configPath = Path.Combine(_runtimeDir, $"yesvpn-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(configPath, configJson, new UTF8Encoding(false), ct).ConfigureAwait(false);
            ApplyRestrictedFileAcl(configPath);
            _activeConfigPath = configPath;

            // Validate config before launching
            await RunCheckAsync(SingBoxPath, configPath, ct).ConfigureAwait(false);

            _intentionalStop = false;
            _startupVerified = false;
            var startupOutput = new List<string>();

            var singBoxDir = Path.GetDirectoryName(SingBoxPath)!;
            var psi = new ProcessStartInfo
            {
                FileName = SingBoxPath,
                Arguments = $"run -c \"{configPath}\"",
                WorkingDirectory = singBoxDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            // Ensure wintun.dll is discoverable even when running from repo third_party layout
            var wintunDir = ResolveWintunDirectory(singBoxDir);
            if (!string.IsNullOrEmpty(wintunDir) && !string.Equals(wintunDir, singBoxDir, StringComparison.OrdinalIgnoreCase))
            {
                var currentPath = psi.Environment.TryGetValue("PATH", out var p) ? p : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                psi.Environment["PATH"] = $"{wintunDir};{currentPath}";
            }

            var process = new Process
            {
                StartInfo = psi,
                EnableRaisingEvents = true
            };

            void CaptureLine(string? data)
            {
                if (string.IsNullOrWhiteSpace(data))
                {
                    return;
                }

                _logs.Append("sing-box", data);
                if (!_startupVerified)
                {
                    lock (startupOutput)
                    {
                        if (startupOutput.Count < 25)
                        {
                            startupOutput.Add(CredentialMasker.Mask(data));
                        }
                    }
                }
            }

            process.OutputDataReceived += (_, e) => CaptureLine(e.Data);
            process.ErrorDataReceived += (_, e) => CaptureLine(e.Data);
            process.Exited += (_, _) =>
            {
                var exitCode = -1;
                try { exitCode = process.ExitCode; } catch { }
                DeleteActiveConfigSafe();

                if (!_intentionalStop && _startupVerified)
                {
                    _logs.Append("Service", $"sing-box exited unexpectedly with code {exitCode}.");
                    UnexpectedExit?.Invoke(exitCode);
                }
            };

            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start sing-box process.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;

            // Give sing-box a brief startup window to catch immediate config/TUN failures
            await Task.Delay(450, ct).ConfigureAwait(false);
            if (process.HasExited)
            {
                // Flush async output streams briefly
                try { process.WaitForExit(300); } catch { }
                var exitCode = process.ExitCode;
                _process = null;
                process.Dispose();
                DeleteActiveConfigSafe();

                string captured;
                lock (startupOutput)
                {
                    captured = string.Join(" | ", startupOutput);
                }

                if (captured.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException(
                        "Administrator privileges are required to create the Wintun TUN adapter. Click 'Elevate' or run YesVPN.Service as Administrator.");
                }

                var detail = string.IsNullOrWhiteSpace(captured)
                    ? $"sing-box terminated immediately after launch (exit code {exitCode})."
                    : $"sing-box failed to start (exit {exitCode}): {captured}";

                throw new InvalidOperationException(detail);
            }

            _startupVerified = true;
            _logs.Append("Service", $"Started sing-box (PID {process.Id}).");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopInternalAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CleanupOrphansAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Directory.Exists(_runtimeDir))
            {
                foreach (var file in Directory.GetFiles(_runtimeDir, "yesvpn-*.json"))
                {
                    try { File.Delete(file); } catch { }
                }
            }

            foreach (var proc in Process.GetProcessesByName("sing-box"))
            {
                try
                {
                    var path = proc.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(SingBoxPath) &&
                        string.Equals(path, SingBoxPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _logs.Append("Service", $"Terminating orphan sing-box process (PID {proc.Id}).");
                        proc.Kill(entireProcessTree: true);
                        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Ignore processes we cannot inspect or already exited
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopInternalAsync(CancellationToken ct)
    {
        _intentionalStop = true;
        var proc = _process;
        _process = null;

        if (proc is not null)
        {
            try
            {
                if (!proc.HasExited)
                {
                    _logs.Append("Service", $"Stopping sing-box (PID {proc.Id})...");

                    // Send graceful CTRL_C_EVENT first so sing-box cleanly closes the Wintun adapter
                    var exitedGracefully = await TryGracefulCtrlCStopAsync(proc, ct).ConfigureAwait(false);
                    if (!exitedGracefully && !proc.HasExited)
                    {
                        _logs.Append("Service", $"sing-box (PID {proc.Id}) did not exit gracefully; forcing termination.");
                        proc.Kill(entireProcessTree: true);
                        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        waitCts.CancelAfter(TimeSpan.FromSeconds(5));
                        await proc.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
                        await Task.Delay(500, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await Task.Delay(150, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _logs.Append("Service", $"Warning while stopping sing-box: {ex.Message}");
            }
            finally
            {
                proc.Dispose();
            }
        }

        DeleteActiveConfigSafe();
    }

    private static async Task RunCheckAsync(string singBoxPath, string configPath, CancellationToken ct)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = singBoxPath,
                Arguments = $"check -c \"{configPath}\"",
                WorkingDirectory = Path.GetDirectoryName(singBoxPath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        proc.Start();
        var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"sing-box config check failed: {stderr.Trim()}");
        }
    }

    private void DeleteActiveConfigSafe()
    {
        var path = Interlocked.Exchange(ref _activeConfigPath, null);
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static string ResolveDefaultRuntimeDir()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrEmpty(baseDir))
        {
            baseDir = Path.GetTempPath();
        }

        var candidate = Path.Combine(baseDir, "YesVPN", "runtime");
        try
        {
            Directory.CreateDirectory(candidate);
            return candidate;
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "YesVPN", "runtime");
        }
    }

    public static string? ResolveSingBoxPath()
    {
        var appDir = AppContext.BaseDirectory;
        string[] directCandidates =
        [
            Path.Combine(appDir, "sing-box.exe"),
            Path.Combine(appDir, "sing-box", "sing-box.exe"),
            Path.Combine(appDir, "third_party", "sing-box", "sing-box.exe")
        ];

        foreach (var c in directCandidates)
        {
            if (File.Exists(c))
            {
                return Path.GetFullPath(c);
            }
        }

        var dir = new DirectoryInfo(appDir);
        while (dir is not null)
        {
            var devPath = Path.Combine(dir.FullName, "third_party", "sing-box", "sing-box.exe");
            if (File.Exists(devPath))
            {
                return devPath;
            }
            dir = dir.Parent;
        }

        return null;
    }

    private static string? ResolveWintunDirectory(string singBoxDir)
    {
        if (File.Exists(Path.Combine(singBoxDir, "wintun.dll")))
        {
            return singBoxDir;
        }

        var sibling = Path.GetFullPath(Path.Combine(singBoxDir, "..", "wintun"));
        if (File.Exists(Path.Combine(sibling, "wintun.dll")))
        {
            return sibling;
        }

        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "wintun.dll")))
        {
            return AppContext.BaseDirectory;
        }

        return null;
    }

    private static string? ResolveSingBoxVersion(string? singBoxPath)
    {
        if (string.IsNullOrEmpty(singBoxPath))
        {
            return null;
        }

        var versionTxt = Path.Combine(Path.GetDirectoryName(singBoxPath)!, "version.txt");
        if (File.Exists(versionTxt))
        {
            return File.ReadAllText(versionTxt).Trim();
        }

        return null;
    }

    private static void ApplyRestrictedDirectoryAcl(string directoryPath)
    {
        try
        {
            var info = new DirectoryInfo(directoryPath);
            var security = info.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is not null)
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    currentUser,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            info.SetAccessControl(security);
        }
        catch
        {
            // Best-effort ACL tightening
        }
    }

    private static void ApplyRestrictedFileAcl(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            var security = info.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is not null)
            {
                security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
            }
            info.SetAccessControl(security);
        }
        catch
        {
            // Best-effort
        }
    }

    private static readonly object ConsoleCtrlLock = new();

    private static async Task<bool> TryGracefulCtrlCStopAsync(Process proc, CancellationToken ct)
    {
        try
        {
            var sent = false;
            lock (ConsoleCtrlLock)
            {
                FreeConsole();
                if (AttachConsole((uint)proc.Id))
                {
                    SetConsoleCtrlHandler(IntPtr.Zero, true);
                    try
                    {
                        sent = GenerateConsoleCtrlEvent(0, 0);
                    }
                    finally
                    {
                        FreeConsole();
                        SetConsoleCtrlHandler(IntPtr.Zero, false);
                    }
                }
            }

            if (!sent)
            {
                return false;
            }

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(TimeSpan.FromMilliseconds(2500));
            await proc.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
            return proc.HasExited;
        }
        catch
        {
            return proc.HasExited;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handlerRoutine, bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    public void Dispose()
    {
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
        _gate.Dispose();
    }
}
