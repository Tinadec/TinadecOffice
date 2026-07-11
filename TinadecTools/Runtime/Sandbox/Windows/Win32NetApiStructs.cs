using System.Runtime.InteropServices;

namespace TinadecTools.Runtime.Sandbox.Windows;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct USER_INFO_1
{
    public string usri1_name;
    public string usri1_password;
    public uint usri1_password_age;
    public uint usri1_priv;
    public string? usri1_home_dir;
    public string? usri1_comment;
    public uint usri1_flags;
    public string? usri1_script_path;
}