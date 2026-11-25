using System.IO;
using System.Text.Json;
using YesVPN.Core.Models;
using YesVPN.Core.Security;

namespace YesVPN.App.Services;

public sealed class AppPersistedState
{
    public List<ProxyEntry> Proxies { get; set; } = new();
    public TunnelOptions Options { get; set; } = new();
    public bool StartWithWindows { get; set; }
    public bool FirstRunNoticeDismissed { get; set; }
}

/// <summary>
/// Persists proxy credentials and user settings encrypted via Windows DPAPI (CurrentUser scope).
/// </summary>
public sealed class AppStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;

    public AppStateStore(string? filePath = null)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            _filePath = filePath;
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _filePath = Path.Combine(appData, "YesVPN", "state.dpapi");
        }
    }

    public AppPersistedState Load()
    {
        try
        {
            var json = DpapiSecretStore.LoadProtectedText(_filePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AppPersistedState();
            }

            return JsonSerializer.Deserialize<AppPersistedState>(json, JsonOptions)
                ?? new AppPersistedState();
        }
        catch
        {
            return new AppPersistedState();
        }
    }

    public void Save(AppPersistedState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var json = JsonSerializer.Serialize(state, JsonOptions);
        DpapiSecretStore.SaveProtectedText(_filePath, json);
    }
}
