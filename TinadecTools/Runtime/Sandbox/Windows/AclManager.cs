using System.Security.AccessControl;
using System.Security.Principal;

namespace TinadecTools.Runtime.Sandbox.Windows;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class AclManager : IDisposable
{
    private readonly string _accountName;
    private readonly List<(string Path, FileSystemAccessRule Rule)> _rules = new();

    internal AclManager(string accountName)
    {
        _accountName = accountName;
    }

    internal void GrantRead(string path) => Add(path, FileSystemRights.ReadAndExecute, AccessControlType.Allow);

    internal void GrantWrite(string path) => Add(path, FileSystemRights.Modify, AccessControlType.Allow);

    internal void DenyAll(string path) => Add(path, FileSystemRights.FullControl, AccessControlType.Deny);

    private void Add(string path, FileSystemRights rights, AccessControlType type)
    {
        var identity = new NTAccount(Environment.MachineName, _accountName);
        var rule = new FileSystemAccessRule(
            identity,
            rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            type);

        var directory = new DirectoryInfo(path);
        var security = directory.GetAccessControl();
        security.ModifyAccessRule(AccessControlModification.Add, rule, out _);
        directory.SetAccessControl(security);
        _rules.Add((path, rule));
    }

    internal void RevokeAll()
    {
        for (var index = _rules.Count - 1; index >= 0; index--)
        {
            var (path, rule) = _rules[index];
            try
            {
                var directory = new DirectoryInfo(path);
                var security = directory.GetAccessControl();
                security.ModifyAccessRule(AccessControlModification.RemoveSpecific, rule, out _);
                directory.SetAccessControl(security);
            }
            catch
            {
                // Cleanup must not hide the command result. The next reset can retry it.
            }
        }
        _rules.Clear();
    }

    public void Dispose() => RevokeAll();
}
