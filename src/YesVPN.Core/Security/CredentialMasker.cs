using System.Text.RegularExpressions;

namespace YesVPN.Core.Security;

/// <summary>
/// Redacts proxy passwords and credentials from logs, diagnostics, and error messages.
/// </summary>
public static partial class CredentialMasker
{
    [GeneratedRegex(@"(socks5://[^:\s/@]+:)([^@\s]+)(@)", RegexOptions.IgnoreCase)]
    private static partial Regex SocksUriPasswordRegex();

    [GeneratedRegex(@"(""password""\s*:\s*"")([^""\\]*(?:\\.[^""\\]*)*)("")", RegexOptions.IgnoreCase)]
    private static partial Regex JsonPasswordRegex();

    [GeneratedRegex(@"(?im)^([^\s:#\[\]]+:\d{1,5}:[^\s:]+:)(.+)$")]
    private static partial Regex ColonCredsRegex();

    public static string Mask(string? input, IEnumerable<string>? additionalSecrets = null)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var result = SocksUriPasswordRegex().Replace(input, "$1***$3");
        result = JsonPasswordRegex().Replace(result, "$1***$3");
        result = ColonCredsRegex().Replace(result, "$1***");

        if (additionalSecrets is not null)
        {
            foreach (var secret in additionalSecrets)
            {
                if (!string.IsNullOrEmpty(secret) && secret.Length >= 2)
                {
                    result = result.Replace(secret, "***", StringComparison.Ordinal);
                }
            }
        }

        return result;
    }
}
