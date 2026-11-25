using YesVPN.Core.Security;

namespace YesVPN.Service.Logging;

/// <summary>
/// Thread-safe bounded ring buffer for service and sing-box logs with automatic credential masking.
/// </summary>
public sealed class LogRingBuffer
{
    private readonly string[] _buffer;
    private readonly object _lock = new();
    private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);
    private int _nextIndex;
    private int _count;

    public LogRingBuffer(int capacity = 1000)
    {
        if (capacity < 10)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be at least 10.");
        }

        _buffer = new string[capacity];
    }

    public void RegisterSecrets(IEnumerable<string?> secrets)
    {
        lock (_lock)
        {
            foreach (var secret in secrets)
            {
                if (!string.IsNullOrEmpty(secret) && secret.Length >= 2)
                {
                    _secrets.Add(secret);
                }
            }
        }
    }

    public void Append(string source, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
        lock (_lock)
        {
            var masked = CredentialMasker.Mask(message.TrimEnd(), _secrets);
            _buffer[_nextIndex] = $"[{timestamp}] [{source}] {masked}";
            _nextIndex = (_nextIndex + 1) % _buffer.Length;
            if (_count < _buffer.Length)
            {
                _count++;
            }
        }
    }

    public IReadOnlyList<string> GetTail(int tail = 200)
    {
        if (tail <= 0)
        {
            return Array.Empty<string>();
        }

        lock (_lock)
        {
            var toTake = Math.Min(tail, _count);
            var result = new List<string>(toTake);
            var start = (_nextIndex - toTake + _buffer.Length) % _buffer.Length;
            for (var i = 0; i < toTake; i++)
            {
                result.Add(_buffer[(start + i) % _buffer.Length]);
            }
            return result;
        }
    }
}
