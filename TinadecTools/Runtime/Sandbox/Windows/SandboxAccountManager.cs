using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TinadecTools.Runtime.Sandbox.Windows;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class SandboxAccountManager
{
    internal const string AccountName = "TinadecSandbox";
    internal const string AccountDomain = ".";

    internal static bool AccountExists() =>
        DpapiCredentialStore.Exists() && Win32NetUserApi.UserExists(AccountName);

    internal static (string username, string password) CreateSandboxAccount()
    {
        var password = GenerateRandomPassword(32);

        var userInfo = new USER_INFO_1
        {
            usri1_name = AccountName,
            usri1_password = password,
            usri1_priv = Win32Constants.USER_PRIV_USER,
            usri1_flags = Win32Constants.UF_DONT_EXPIRE_PASSWD | Win32Constants.UF_NORMAL_ACCOUNT | Win32Constants.UF_SCRIPT,
            usri1_home_dir = null,
            usri1_comment = "TinadecTools low-privilege sandbox account",
            usri1_script_path = null
        };

        var result = Win32NetUserApi.NetUserAdd(null!, 1, ref userInfo, out _);
        if (result == Win32Constants.NERR_UserExists)
        {
            if (!DpapiCredentialStore.Exists())
                throw new InvalidOperationException("A Tinadec sandbox account exists without a matching local credential. Run sandbox_reset with scope=machine before retrying setup.");
            return (AccountName, DpapiCredentialStore.LoadPassword());
        }

        if (result != Win32Constants.NERR_Success && result != Win32Constants.ERROR_SUCCESS)
            throw new InvalidOperationException($"Failed to create sandbox account (NetUserAdd returned {result}).");

        DpapiCredentialStore.SavePassword(password);
        return (AccountName, password);
    }

    internal static void DeleteSandboxAccount()
    {
        Win32NetUserApi.NetUserDel(null!, AccountName);
        DpapiCredentialStore.Delete();
    }

    internal static IntPtr LogonSandboxUser(string password)
    {
        if (!Win32TokenApi.LogonUserW(
            AccountName, AccountDomain, password,
            Win32Constants.LOGON32_LOGON_BATCH,
            Win32Constants.LOGON32_PROVIDER_DEFAULT,
            out var token))
        {
            throw new InvalidOperationException($"Failed to logon sandbox user (error {Marshal.GetLastWin32Error()}).");
        }
        return token;
    }

    internal static string GetSandboxProfileDir()
    {
        var usersRoot = Directory.GetParent(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))?.FullName
            ?? @"C:\\Users";
        return Path.Combine(usersRoot, AccountName);
    }

    internal static string GetSandboxCacheDir()
    {
        return Path.Combine(GetSandboxProfileDir(), "AppData", "Local", "TinadecTools", "Cache");
    }

    private static string GenerateRandomPassword(int length)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789!@#$%^&*";
        var bytes = RandomNumberGenerator.GetBytes(length);
        var sb = new StringBuilder(length);
        foreach (var b in bytes)
            sb.Append(chars[b % chars.Length]);
        return sb.ToString();
    }
}
