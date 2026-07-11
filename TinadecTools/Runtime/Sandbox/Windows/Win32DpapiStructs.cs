using System.Runtime.InteropServices;

namespace TinadecTools.Runtime.Sandbox.Windows;

[StructLayout(LayoutKind.Sequential)]
internal struct DATA_BLOB
{
    public uint cbData;
    public IntPtr pbData;
}