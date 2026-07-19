using System.Diagnostics;
using TinadecTools.Tools.FileRW;

namespace TinadecTools.Tests;

// ponytail: 4 个旧 git 测试文件重复了同样的 init/config/RunGit/Cleanup；抽一份共采.
// 升级路径: 如果未来出现真实 bare-remote fixture 需求，可在这层加 AddRemote/seed-branch.
internal sealed class TempGitRepo : IDisposable
{
    public string Path { get; }

    // ponytail: 让 fixture 能直接当 cwd 字符串用，省 callsite 全部加 .Path；隐式转换编译期无运行时开销.
    public static implicit operator string(TempGitRepo repo) => repo.Path;

    public TempGitRepo(string idPrefix)
    {
        Path = System.IO.Path.Combine(FileToolRuntime.WorkspaceRoot, ".tinadec-tools-tests", $"{idPrefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
        RunGit("init", "--initial-branch=main");
        RunGit("config", "user.name", "Test");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "commit.gpgSign", "false");
    }

    /// <summary>初始化仓库内已有一个 commit 的 README.md；许多工具测试依赖至少一个 HEAD 提交.</summary>
    public void SeedInitialCommit(string file = "README.md", string content = "# hello\n", string message = "initial")
    {
        File.WriteAllText(System.IO.Path.Combine(Path, file), content);
        RunGit("add", file);
        RunGit("commit", "-m", message);
    }

    public void CommitFile(string relativePath, string content, string message)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        RunGit("add", "--", relativePath);
        RunGit("commit", "-m", message);
    }

    public void RunGit(params string[] args)
    {
        using var process = Process.Start(StartInfo(args))!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
    }

    public string CaptureGit(params string[] args)
    {
        var start = StartInfo(args);
        start.RedirectStandardOutput = true;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
        return output;
    }

    private ProcessStartInfo StartInfo(string[] args)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = Path,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
        catch { }
    }
}