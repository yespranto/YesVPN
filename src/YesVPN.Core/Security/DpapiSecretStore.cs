using System.Security.Cryptography;
using System.Text;

namespace YesVPN.Core.Security;

/// <summary>
/// Encrypts and decrypts sensitive configuration blobs using Windows DPAPI.
/// </summary>
public static class DpapiSecretStore
{
    private static readonly byte[] AppEntropy = Encoding.UTF8.GetBytes("YesVPN.DPAPI.v1");

    public static byte[] ProtectBytes(byte[] plainBytes, DataProtectionScope scope = DataProtectionScope.CurrentUser)
    {
        ArgumentNullException.ThrowIfNull(plainBytes);
        return ProtectedData.Protect(plainBytes, AppEntropy, scope);
    }

    public static byte[] UnprotectBytes(byte[] protectedBytes, DataProtectionScope scope = DataProtectionScope.CurrentUser)
    {
        ArgumentNullException.ThrowIfNull(protectedBytes);
        return ProtectedData.Unprotect(protectedBytes, AppEntropy, scope);
    }

    public static string ProtectString(string plainText, DataProtectionScope scope = DataProtectionScope.CurrentUser)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        var bytes = Encoding.UTF8.GetBytes(plainText);
        return Convert.ToBase64String(ProtectBytes(bytes, scope));
    }

    public static string UnprotectString(string protectedBase64, DataProtectionScope scope = DataProtectionScope.CurrentUser)
    {
        ArgumentNullException.ThrowIfNull(protectedBase64);
        var protectedBytes = Convert.FromBase64String(protectedBase64);
        var plainBytes = UnprotectBytes(protectedBytes, scope);
        return Encoding.UTF8.GetString(plainBytes);
    }

    public static void SaveProtectedText(string filePath, string plainText, DataProtectionScope scope = DataProtectionScope.CurrentUser)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var encrypted = ProtectBytes(Encoding.UTF8.GetBytes(plainText), scope);
        var tempPath = filePath + ".tmp";
        File.WriteAllBytes(tempPath, encrypted);
        File.Move(tempPath, filePath, overwrite: true);
    }

    public static string? LoadProtectedText(string filePath, DataProtectionScope scope = DataProtectionScope.CurrentUser)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        var encrypted = File.ReadAllBytes(filePath);
        var plain = UnprotectBytes(encrypted, scope);
        return Encoding.UTF8.GetString(plain);
    }
}
