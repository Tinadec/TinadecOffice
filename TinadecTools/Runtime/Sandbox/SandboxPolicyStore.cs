using System.Text.Json;
using TinadecTools.Tools.FileRW;

namespace TinadecTools.Runtime.Sandbox;

internal static class SandboxPolicyStore
{
    internal const int CurrentVersion = 1;
    private const string SandboxDirName = ".tinadec";
    private const string SandboxFileName = "sandbox.json";

    private static string SandboxFilePath => Path.Combine(
        WorkspacePathResolver.WorkspaceRoot, SandboxDirName, SandboxFileName);

    private static string SandboxDir => Path.Combine(
        WorkspacePathResolver.WorkspaceRoot, SandboxDirName);

    internal static SandboxPolicyFile Load()
    {
        var path = SandboxFilePath;
        if (!File.Exists(path)) return new SandboxPolicyFile { Version = CurrentVersion };
        try
        {
            var json = File.ReadAllText(path);
            var policy = JsonSerializer.Deserialize(json, SandboxJsonContext.Default.SandboxPolicyFile);
            return policy ?? new SandboxPolicyFile { Version = CurrentVersion };
        }
        catch
        {
            return new SandboxPolicyFile { Version = CurrentVersion };
        }
    }

    internal static SandboxPolicyFile MergeGrants(
        SandboxPermissions permissions, SandboxPolicyFile? existing = null)
    {
        existing ??= Load();

        existing.ReadPaths = Union(existing.ReadPaths, permissions.ReadPaths);
        existing.WritePaths = Union(existing.WritePaths, permissions.WritePaths);
        existing.EnvironmentVariables = Union(existing.EnvironmentVariables, permissions.EnvironmentVariableNames);
        existing.Version = CurrentVersion;
        return existing;
    }

    internal static void Save(SandboxPolicyFile policy)
    {
        if (!Directory.Exists(SandboxDir))
            Directory.CreateDirectory(SandboxDir);
        var json = JsonSerializer.Serialize(policy, SandboxJsonContext.Default.SandboxPolicyFile);
        File.WriteAllText(SandboxFilePath, json);
    }

    internal static void Delete()
    {
        try
        {
            if (File.Exists(SandboxFilePath))
                File.Delete(SandboxFilePath);
        }
        catch { /* best-effort */ }
    }

    internal static SandboxPolicyFile MergeAndPersist(SandboxPermissions permissions)
    {
        var merged = MergeGrants(permissions);
        Save(merged);
        return merged;
    }

    private static List<string> Union(IEnumerable<string> existing, IEnumerable<string> additions)
    {
        var cmp = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        return [.. existing
            .Concat(additions)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.TrimEnd('\\', '/'))
            .Distinct(cmp)
            .OrderBy(s => s)];
    }
}