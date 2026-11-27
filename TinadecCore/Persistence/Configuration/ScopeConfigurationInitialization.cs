using System.Text;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Persistence;

/// <summary>First-install seeding may create a file once; an established file must be restored explicitly.</summary>
public static class ScopeConfigurationInitialization
{
    public static void Ensure(IScopeStorageLocations scope, string id, Func<string> initialText)
    {
        var path = Path.Combine(scope.Config, id + ".toml");
        var marker = Path.Combine(scope.State, ".configuration-documents", id + ".initialized");
        StorageScopePaths.RejectLinks(scope.Root, path); StorageScopePaths.RejectLinks(scope.Root, marker);
        if (!File.Exists(path))
        {
            if (File.Exists(marker) || IsPublishedProject(scope))
                throw new ConfigurationDocumentException("configuration_missing", "Established configuration file '" + id + ".toml' is missing. Restore it before starting this storage scope.");
            var bytes = Encoding.UTF8.GetBytes(initialText());
            Directory.CreateDirectory(scope.Config);
            try
            {
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.None);
                output.Write(bytes); output.Flush();
            }
            catch (IOException) when (File.Exists(path)) { }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        StorageScopePaths.RejectLinks(scope.Root, marker);
        try
        {
            using var output = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.None);
            output.Write(Encoding.UTF8.GetBytes(id + "\n")); output.Flush();
        }
        catch (IOException) when (File.Exists(marker)) { }
    }

    internal static bool IsPublishedProject(IScopeStorageLocations scope)
    {
        if (scope is not StorageScopeDescriptor { ScopeKind: "project" }) return false;
        var manifest = Path.Combine(scope.Root, "project.toml");
        StorageScopePaths.RejectLinks(scope.Root, manifest);
        return File.Exists(manifest);
    }
}
