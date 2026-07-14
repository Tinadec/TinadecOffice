using System.Runtime.InteropServices;

namespace TinadecTools.Runtime.Sandbox.Windows;

internal static class Win32NetUserApi
{
    [DllImport("netapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int NetUserAdd(
        [MarshalAs(UnmanagedType.LPWStr)] string servername,
        uint level, ref USER_INFO_1 buf, out uint parm_err);

    [DllImport("netapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int NetUserDel(
        [MarshalAs(UnmanagedType.LPWStr)] string servername,
        [MarshalAs(UnmanagedType.LPWStr)] string username);

    [DllImport("netapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int NetUserGetInfo(
        [MarshalAs(UnmanagedType.LPWStr)] string servername,
        [MarshalAs(UnmanagedType.LPWStr)] string username,
        uint level, out IntPtr bufptr);

    [DllImport("netapi32.dll")]
    internal static extern int NetApiBufferFree(IntPtr buffer);

    internal static bool UserExists(string username)
    {
        var result = NetUserGetInfo(null!, username, 0, out var buffer);
        if (buffer != IntPtr.Zero)
            NetApiBufferFree(buffer);
        return result == Win32Constants.NERR_Success;
    }
}
