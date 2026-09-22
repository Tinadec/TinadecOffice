using System.Runtime.InteropServices;

namespace TinadecTools.Tests;

/// <summary>
/// Makes "this machine refuses to make a symlink" a precondition instead of a failure.
/// Windows reports <c>ERROR_PRIVILEGE_NOT_HELD</c> as an <see cref="IOException"/>, not the
/// <see cref="UnauthorizedAccessException"/> a link-traversal guard naturally catches, so on an
/// ordinary (non-elevated, no Developer Mode) dev box those guards went red while never touching
/// the code they exist to protect. Register the consequence honestly: a skipped return here means
/// the link guard itself is untested on that machine, not that it passed.
/// </summary>
internal static class LinkPrerequisite
{
    private const int ErrorPrivilegeNotHeld = 1314;

    public static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException ex) when ((Marshal.GetHRForException(ex) & 0xFFFF) == ErrorPrivilegeNotHeld)
        {
            return false;
        }
    }
}
