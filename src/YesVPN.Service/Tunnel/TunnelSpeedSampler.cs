using System.Net.Http;
using System.Text.Json;

namespace YesVPN.Service.Tunnel;

public sealed record SpeedSnapshot(
    long DownloadBytesPerSec,
    long UploadBytesPerSec,
    long TotalDownloadBytes,
    long TotalUploadBytes);

/// <summary>
/// Streams real-time per-direction upload/download throughput from sing-box's Clash API (/traffic)
/// so upload and download counters are measured independently inside the proxy core.
/// </summary>
public sealed class TunnelSpeedSampler : IDisposable
{
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _streamTask;

    private long _downPerSec;
    private long _upPerSec;
    private long _totalDown;
    private long _totalUp;

    public void Start(int clashApiPort)
    {
        Stop();

        var cts = new CancellationTokenSource();
        _cts = cts;
        _streamTask = Task.Run(() => StreamTrafficAsync(clashApiPort, cts.Token), cts.Token);
    }

    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
        _streamTask = null;

        lock (_lock)
        {
            _downPerSec = 0;
            _upPerSec = 0;
            _totalDown = 0;
            _totalUp = 0;
        }
    }

    public SpeedSnapshot Sample()
    {
        lock (_lock)
        {
            return new SpeedSnapshot(_downPerSec, _upPerSec, _totalDown, _totalUp);
        }
    }

    public static bool TryParseTrafficLine(string? line, out long upBytesPerSec, out long downBytesPerSec)
    {
        upBytesPerSec = 0;
        downBytesPerSec = 0;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("up", out var upEl) && upEl.TryGetInt64(out var up) &&
                root.TryGetProperty("down", out var downEl) && downEl.TryGetInt64(out var down))
            {
                upBytesPerSec = Math.Max(0, up);
                downBytesPerSec = Math.Max(0, down);
                return true;
            }
        }
        catch (JsonException)
        {
            // Ignore malformed line
        }
        return false;
    }

    private async Task StreamTrafficAsync(int port, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var url = $"http://127.0.0.1:{port}/traffic";

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(stream);

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    if (TryParseTrafficLine(line, out var up, out var down))
                    {
                        lock (_lock)
                        {
                            _upPerSec = up;
                            _downPerSec = down;
                            _totalUp += up;
                            _totalDown += down;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                lock (_lock)
                {
                    _upPerSec = 0;
                    _downPerSec = 0;
                }
                try { await Task.Delay(400, ct).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    public void Dispose() => Stop();
}
