using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;

namespace TinadecModel.Services;

public sealed class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TinadecCode.Core.v1");
    private static readonly byte[] LegacyEntropy = Convert.FromBase64String("VGluYWRlY0NvZGUuQWdlbnRDb3JlLnYx");

    public string Protect(string secret)
    {
        if (string.IsNullOrEmpty(secret))
            return string.Empty;

        var bytes = Encoding.UTF8.GetBytes(secret);
        var protectedBytes = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser)
            : bytes;

        var prefix = OperatingSystem.IsWindows() ? "dpapi:" : "plain:";
        return prefix + Convert.ToBase64String(protectedBytes);
    }

    public string Unprotect(string protectedSecret)
    {
        if (string.IsNullOrWhiteSpace(protectedSecret))
            return string.Empty;

        if (protectedSecret.StartsWith("dpapi:", StringComparison.OrdinalIgnoreCase))
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("DPAPI-protected TinadecCode secrets can only be opened on Windows.");

            var bytes = Convert.FromBase64String(protectedSecret["dpapi:".Length..]);
            var unprotected = UnprotectDpapi(bytes);
            return Encoding.UTF8.GetString(unprotected);
        }

        if (protectedSecret.StartsWith("plain:", StringComparison.OrdinalIgnoreCase))
            return Encoding.UTF8.GetString(Convert.FromBase64String(protectedSecret["plain:".Length..]));

        return protectedSecret;
    }

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectDpapi(byte[] bytes)
    {
        try
        {
            return ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            return ProtectedData.Unprotect(bytes, LegacyEntropy, DataProtectionScope.CurrentUser);
        }
    }
}
