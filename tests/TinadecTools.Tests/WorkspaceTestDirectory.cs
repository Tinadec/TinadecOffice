using TinadecTools.Tools.FileRW;

namespace TinadecTools.Tests;

internal sealed class WorkspaceTestDirectory : IDisposable
{
    public WorkspaceTestDirectory()
    {
        Path = System.IO.Path.Combine(FileToolRuntime.WorkspaceRoot, ".tinadec-tools-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string CreateFile(string name, string content)
    {
        var path = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string RelativePath => System.IO.Path.GetRelativePath(FileToolRuntime.WorkspaceRoot, Path);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // FileToolRuntime intentionally retains shared file handles for process lifetime.
        }
    }
}
