using System.Runtime.InteropServices;

namespace TinadecTools.Runtime.Sandbox.Windows;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class DpapiCredentialStore
{
    private static readonly string StoreDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TinadecTools", "Sandbox");

    private static string CredentialPath => Path.Combine(StoreDir, "sandbox-account.dat");

    internal static void SavePassword(string password)
    {
        Directory.CreateDirectory(StoreDir);
        var bytes = System.Text.Encoding.UTF8.GetBytes(password);
        var input = CreateBlob(bytes);
        try
        {
            if (!Win32DpapiApi.CryptProtectData(ref input, "TinadecSandbox", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var output))
                throw new InvalidOperationException("DPAPI CryptProtectData failed.");
            try
            {
                var cipher = new byte[output.cbData];
                Marshal.Copy(output.pbData, cipher, 0, (int)output.cbData);
                File.WriteAllBytes(CredentialPath, cipher);
            }
            finally { FreeDpapiBlob(ref output); }
        }
        finally { FreeBlob(ref input); }
    }

    internal static string LoadPassword()
    {
        var path = CredentialPath;
        if (!File.Exists(path)) throw new FileNotFoundException("Sandbox credential not found.", path);
        var cipher = File.ReadAllBytes(path);
        var input = CreateBlob(cipher);
        try
        {
            if (!Win32DpapiApi.CryptUnprotectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var output))
                throw new InvalidOperationException("DPAPI CryptUnprotectData failed.");
            try
            {
                var plain = new byte[output.cbData];
                Marshal.Copy(output.pbData, plain, 0, (int)output.cbData);
                return System.Text.Encoding.UTF8.GetString(plain);
            }
            finally { FreeDpapiBlob(ref output); }
        }
        finally { FreeBlob(ref input); }
    }

    internal static void Delete()
    {
        try { if (File.Exists(CredentialPath)) File.Delete(CredentialPath); }
        catch { }
    }

    internal static bool Exists() => File.Exists(CredentialPath);

    private static DATA_BLOB CreateBlob(byte[] data)
    {
        var blob = new DATA_BLOB { cbData = (uint)data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        Marshal.Copy(data, 0, blob.pbData, data.Length);
        return blob;
    }

    private static void FreeBlob(ref DATA_BLOB blob)
    {
        if (blob.pbData != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(blob.pbData);
            blob.pbData = IntPtr.Zero;
            blob.cbData = 0;
        }
    }

    private static void FreeDpapiBlob(ref DATA_BLOB blob)
    {
        if (blob.pbData != IntPtr.Zero)
        {
            Win32ProcessApi.LocalFree(blob.pbData);
            blob.pbData = IntPtr.Zero;
            blob.cbData = 0;
        }
    }
}
