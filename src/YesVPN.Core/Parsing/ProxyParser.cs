using System.Net;
using System.Text;
using YesVPN.Core.Models;

namespace YesVPN.Core.Parsing;

public sealed record ProxyParseError(int LineNumber, string MaskedLine, string Message);

public sealed record ProxyParseResult(
    IReadOnlyList<ProxyEntry> Entries,
    IReadOnlyList<ProxyParseError> Errors)
{
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// Parses multi-line SOCKS5 proxy lists in socks5://, host:port, and host:port:user:pass formats.
/// </summary>
public static class ProxyParser
{
    public static ProxyParseResult Parse(string? input)
    {
        var entries = new List<ProxyEntry>();
        var errors = new List<ProxyParseError>();

        if (string.IsNullOrWhiteSpace(input))
        {
            return new ProxyParseResult(entries, errors);
        }

        var lines = input.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            var rawLine = lines[i];
            var trimmed = rawLine.Trim();

            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (TryParseLine(trimmed, out var entry, out var errorMessage))
            {
                entries.Add(entry!);
            }
            else
            {
                errors.Add(new ProxyParseError(lineNumber, MaskLineForError(trimmed), errorMessage!));
            }
        }

        return new ProxyParseResult(entries, errors);
    }

    public static bool TryParseLine(string line, out ProxyEntry? entry, out string? errorMessage)
    {
        entry = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            errorMessage = "Line is empty.";
            return false;
        }

        var trimmed = line.Trim();

        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            return TryParseUriFormat(trimmed, out entry, out errorMessage);
        }

        return TryParseColonFormat(trimmed, out entry, out errorMessage);
    }

    private static bool TryParseUriFormat(string line, out ProxyEntry? entry, out string? errorMessage)
    {
        entry = null;
        errorMessage = null;

        const string scheme = "socks5://";
        if (!line.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = "Unsupported proxy scheme. Only socks5:// is supported.";
            return false;
        }

        var remainder = line[scheme.Length..];
        if (string.IsNullOrWhiteSpace(remainder))
        {
            errorMessage = "Missing host and port after socks5://.";
            return false;
        }

        string? label = null;
        string? username = null;
        string? password = null;

        // Extract optional trailing #label only when # appears after the host/authority section
        var atIndex = remainder.LastIndexOf('@');
        var hostStartSearch = atIndex >= 0 ? atIndex + 1 : 0;
        var hashAfterHost = remainder.IndexOf('#', hostStartSearch);
        if (hashAfterHost >= 0)
        {
            var rawLabel = remainder[(hashAfterHost + 1)..].Trim();
            if (!string.IsNullOrEmpty(rawLabel))
            {
                label = SafeUnescape(rawLabel);
            }
            remainder = remainder[..hashAfterHost];
        }

        // Strip trailing slash if someone pasted socks5://host:port/
        remainder = remainder.TrimEnd('/');

        atIndex = remainder.LastIndexOf('@');
        string hostPortPart;
        if (atIndex >= 0)
        {
            var userInfo = remainder[..atIndex];
            hostPortPart = remainder[(atIndex + 1)..];

            var colonIndex = userInfo.IndexOf(':');
            if (colonIndex < 0)
            {
                username = SafeUnescape(userInfo);
            }
            else
            {
                username = SafeUnescape(userInfo[..colonIndex]);
                password = SafeUnescape(userInfo[(colonIndex + 1)..]);
            }
        }
        else
        {
            hostPortPart = remainder;
        }

        if (!TryParseHostAndPort(hostPortPart, out var host, out var port, out errorMessage))
        {
            return false;
        }

        entry = new ProxyEntry
        {
            Host = host,
            Port = port,
            Username = string.IsNullOrEmpty(username) ? null : username,
            Password = string.IsNullOrEmpty(password) ? null : password,
            Label = label
        };
        return true;
    }

    private static bool TryParseColonFormat(string line, out ProxyEntry? entry, out string? errorMessage)
    {
        entry = null;
        errorMessage = null;

        string hostPart;
        string rest;

        if (line.StartsWith('['))
        {
            var closeBracket = line.IndexOf(']');
            if (closeBracket < 0)
            {
                errorMessage = "Malformed IPv6 address: missing closing ']'.";
                return false;
            }

            hostPart = line[1..closeBracket];
            if (!IPAddress.TryParse(hostPart, out var ipv6) || ipv6.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                errorMessage = $"Invalid IPv6 address '{hostPart}'.";
                return false;
            }

            if (closeBracket + 1 >= line.Length || line[closeBracket + 1] != ':')
            {
                errorMessage = "Missing port after IPv6 address. Expected [ipv6]:port.";
                return false;
            }

            rest = line[(closeBracket + 2)..];
        }
        else
        {
            var firstColon = line.IndexOf(':');
            if (firstColon < 0)
            {
                errorMessage = "Missing port. Expected host:port or host:port:user:pass.";
                return false;
            }

            hostPart = line[..firstColon].Trim();
            rest = line[(firstColon + 1)..];
        }

        if (!IsValidHost(hostPart))
        {
            errorMessage = $"Invalid host '{hostPart}'.";
            return false;
        }

        // rest is either "<port>" or "<port>:<user>:<pass>" (password may itself contain ':')
        var portColon = rest.IndexOf(':');
        if (portColon < 0)
        {
            if (!TryParsePort(rest, out var port, out errorMessage))
            {
                return false;
            }

            entry = new ProxyEntry
            {
                Host = NormalizeHost(hostPart),
                Port = port
            };
            return true;
        }

        var portToken = rest[..portColon];
        if (!TryParsePort(portToken, out var parsedPort, out errorMessage))
        {
            return false;
        }

        var credsPart = rest[(portColon + 1)..];
        var userColon = credsPart.IndexOf(':');
        if (userColon < 0)
        {
            errorMessage = "Expected host:port:user:pass format with both username and password.";
            return false;
        }

        var username = credsPart[..userColon];
        var password = credsPart[(userColon + 1)..];
        if (string.IsNullOrEmpty(username))
        {
            errorMessage = "Username cannot be empty in host:port:user:pass format.";
            return false;
        }

        entry = new ProxyEntry
        {
            Host = NormalizeHost(hostPart),
            Port = parsedPort,
            Username = username,
            Password = password
        };
        return true;
    }

    private static bool TryParseHostAndPort(string hostPort, out string host, out int port, out string? errorMessage)
    {
        host = string.Empty;
        port = 0;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(hostPort))
        {
            errorMessage = "Missing host and port.";
            return false;
        }

        if (hostPort.StartsWith('['))
        {
            var closeBracket = hostPort.IndexOf(']');
            if (closeBracket < 0)
            {
                errorMessage = "Malformed IPv6 host: missing closing ']'.";
                return false;
            }

            var ipText = hostPort[1..closeBracket];
            if (!IPAddress.TryParse(ipText, out var ipv6) || ipv6.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                errorMessage = $"Invalid IPv6 host '{ipText}'.";
                return false;
            }

            if (closeBracket + 1 >= hostPort.Length || hostPort[closeBracket + 1] != ':')
            {
                errorMessage = "Missing port after IPv6 host.";
                return false;
            }

            var portText = hostPort[(closeBracket + 2)..];
            if (!TryParsePort(portText, out port, out errorMessage))
            {
                return false;
            }

            host = ipv6.ToString();
            return true;
        }

        var lastColon = hostPort.LastIndexOf(':');
        if (lastColon <= 0)
        {
            errorMessage = "Missing port. Expected host:port.";
            return false;
        }

        var rawHost = hostPort[..lastColon].Trim();
        var rawPort = hostPort[(lastColon + 1)..];

        if (!IsValidHost(rawHost))
        {
            errorMessage = $"Invalid host '{rawHost}'.";
            return false;
        }

        if (!TryParsePort(rawPort, out port, out errorMessage))
        {
            return false;
        }

        host = NormalizeHost(rawHost);
        return true;
    }

    private static bool TryParsePort(string text, out int port, out string? errorMessage)
    {
        errorMessage = null;
        if (!int.TryParse(text.Trim(), out port) || port < 1 || port > 65535)
        {
            errorMessage = $"Invalid port '{text.Trim()}'. Must be between 1 and 65535.";
            return false;
        }
        return true;
    }

    private static bool IsValidHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253)
        {
            return false;
        }

        if (IPAddress.TryParse(host, out _))
        {
            return true;
        }

        if (host.Any(char.IsWhiteSpace) || host.Contains('/') || host.Contains('@') || host.Contains(':'))
        {
            return false;
        }

        return Uri.CheckHostName(host) == UriHostNameType.Dns;
    }

    private static string NormalizeHost(string host) =>
        IPAddress.TryParse(host, out var ip) ? ip.ToString() : host.Trim();

    private static string SafeUnescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    private static string MaskLineForError(string line)
    {
        if (line.Contains("://", StringComparison.Ordinal))
        {
            var at = line.LastIndexOf('@');
            var schemeEnd = line.IndexOf("://", StringComparison.Ordinal);
            if (at > schemeEnd + 3)
            {
                return string.Concat(line.AsSpan(0, schemeEnd + 3), "***@", line.AsSpan(at + 1));
            }
            return line;
        }

        // Mask host:port:user:pass
        var parts = line.Split(':');
        if (parts.Length >= 4 && !line.StartsWith('['))
        {
            return $"{parts[0]}:{parts[1]}:{parts[2]}:***";
        }

        return line;
    }

    public static string FormatMasked(ProxyEntry entry)
    {
        var hostPort = ProxyEntry.FormatHostPort(entry.Host, entry.Port);
        return string.IsNullOrEmpty(entry.Username)
            ? $"socks5://{hostPort}"
            : $"socks5://{entry.Username}:***@{hostPort}";
    }
}
