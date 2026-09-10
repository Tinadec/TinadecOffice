namespace TinadecCore.Tools;

/// <summary>
/// Resource-grant evaluation shared by the invocation-scope resolver and the
/// Core-owned virtual tools. A declared list containing the coarse tokens
/// <c>workspace</c>/<c>project</c> allows anything; otherwise the candidate path
/// must equal, or be nested under, a granted path. An empty list means no
/// restriction was declared and allows everything, matching the resolver's
/// historical behaviour.
/// </summary>
internal static class ToolResourceAllowList
{
    public static bool IsAllowed(IReadOnlyList<string> resources, string path)
    {
        if (resources.Count == 0) return true;
        var normalizedPath = TryNormalize(path);
        foreach (var resource in resources)
        {
            if (string.Equals(resource, "workspace", StringComparison.OrdinalIgnoreCase)
                || string.Equals(resource, "project", StringComparison.OrdinalIgnoreCase)) return true;
            var root = TryNormalize(resource);
            if (root is null || normalizedPath is null) continue;
            if (string.Equals(normalizedPath, root, StringComparison.OrdinalIgnoreCase)
                || normalizedPath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? TryNormalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            // A malformed grant/path is simply not an authorization match.
            return Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
