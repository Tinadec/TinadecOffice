using System.Security.AccessControl;
using System.Security.Principal;

namespace TinadecTools.Runtime.Sandbox.Windows;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class AclManager : IDisposable
{
    private readonly string _accountName;
    private readonly List<(string Path, FileSystemAccessRule Rule)> _rules = new();
    private readonly List<string> _readExceptions = new();
    private static readonly object Gate = new();
    private static readonly Dictionary<string, int> References = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (string Sddl, int Count)> ExceptionReferences = new(StringComparer.OrdinalIgnoreCase);

    internal AclManager(string accountName)
    {
        _accountName = accountName;
    }

    internal void GrantRead(string path) => Add(path, FileSystemRights.ReadAndExecute, AccessControlType.Allow);

    internal void GrantWrite(string path) => Add(path, FileSystemRights.Modify, AccessControlType.Allow);

    internal void DenyAll(string path) => Add(path, FileSystemRights.FullControl, AccessControlType.Deny);
    internal void DenyWrite(string path) => Add(path,
        FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Deny);

    internal void AllowReadException(string path)
    {
        lock (Gate)
        {
            try
            {
                var directory = new DirectoryInfo(path);
                var security = directory.GetAccessControl();
                if (ExceptionReferences.TryGetValue(path, out var existing)) ExceptionReferences[path] = (existing.Sddl, existing.Count + 1);
                else
                {
                    ExceptionReferences[path] = (security.GetSecurityDescriptorSddlForm(AccessControlSections.Access), 1);
                }
                foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(NTAccount)))
                    if (!rule.IsInherited
                        && rule.AccessControlType == AccessControlType.Deny
                        && rule.IdentityReference.Value.EndsWith("\\" + _accountName, StringComparison.OrdinalIgnoreCase))
                        security.RemoveAccessRuleSpecific(rule);
                directory.SetAccessControl(security);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or SystemException)
            {
                throw new InvalidOperationException(
                    $"Failed to allow the sandbox account to read '{path}': {ex.Message}", ex);
            }
            _readExceptions.Add(path);
        }
        GrantRead(path);
    }

    internal void AllowWriteException(string path)
    {
        AllowReadException(path);
        GrantWrite(path);
    }

    private void Add(string path, FileSystemRights rights, AccessControlType type)
    {
        var identity = new NTAccount(Environment.MachineName, _accountName);
        var rule = new FileSystemAccessRule(
            identity,
            rights,
            Directory.Exists(path) ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None,
            PropagationFlags.None,
            type);

        lock (Gate)
        {
            var key = RuleKey(path, rule);
            if (References.TryGetValue(key, out var count)) References[key] = count + 1;
            else
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        var directory = new DirectoryInfo(path);
                        var security = directory.GetAccessControl();
                        security.ModifyAccessRule(AccessControlModification.Add, rule, out _);
                        directory.SetAccessControl(security);
                    }
                    else
                    {
                        var file = new FileInfo(path);
                        var security = file.GetAccessControl();
                        security.ModifyAccessRule(AccessControlModification.Add, rule, out _);
                        file.SetAccessControl(security);
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or SystemException)
                {
                    throw new InvalidOperationException(
                        $"Failed to apply sandbox ACL '{rights}' ({type}) to '{path}': {ex.Message}", ex);
                }
                References[key] = 1;
            }
            _rules.Add((path, rule));
        }
    }

    internal void RevokeAll()
    {
        RestoreReadExceptions();
        for (var index = _rules.Count - 1; index >= 0; index--)
        {
            var (path, rule) = _rules[index];
            try
            {
                lock (Gate)
                {
                    var key = RuleKey(path, rule);
                    if (References[key] > 1) { References[key]--; continue; }
                    References.Remove(key);
                    if (Directory.Exists(path))
                    {
                        var directory = new DirectoryInfo(path);
                        var security = directory.GetAccessControl();
                        security.ModifyAccessRule(AccessControlModification.RemoveSpecific, rule, out _);
                        directory.SetAccessControl(security);
                    }
                    else if (File.Exists(path))
                    {
                        var file = new FileInfo(path);
                        var security = file.GetAccessControl();
                        security.ModifyAccessRule(AccessControlModification.RemoveSpecific, rule, out _);
                        file.SetAccessControl(security);
                    }
                }
            }
            catch
            {
                // Cleanup must not hide the command result. The next reset can retry it.
            }
        }
        _rules.Clear();
    }

    /// <summary>
    /// Revokes the temporary grants off the command's critical path. The static
    /// reference counts keep a rule alive if the next command re-applies it
    /// before this timer fires.
    /// </summary>
    internal void RevokeAllAfter(TimeSpan delay)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
                RevokeAll();
            }
            catch
            {
                // Best effort: the next sandbox reset retries cleanup.
            }
        });
    }

    private void RestoreReadExceptions()
    {
        lock (Gate)
        {
            foreach (var path in _readExceptions.AsEnumerable().Reverse())
            {
                var entry = ExceptionReferences[path];
                if (entry.Count > 1) { ExceptionReferences[path] = (entry.Sddl, entry.Count - 1); continue; }
                ExceptionReferences.Remove(path);
                if (!Directory.Exists(path)) continue;
                var directory = new DirectoryInfo(path);
                var security = directory.GetAccessControl();
                security.SetSecurityDescriptorSddlForm(entry.Sddl, AccessControlSections.Access);
                directory.SetAccessControl(security);
            }
            _readExceptions.Clear();
        }
    }

    private static string RuleKey(string path, FileSystemAccessRule rule) =>
        path + "|" + rule.IdentityReference.Value + "|" + rule.FileSystemRights + "|" + rule.AccessControlType;

    public void Dispose() => RevokeAll();
}
