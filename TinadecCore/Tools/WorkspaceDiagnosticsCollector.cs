using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TinadecCore.Tools;

public sealed record WorkspaceDiagnosticItem(
    string FilePath,
    int Line,
    int? Column,
    string Severity,
    string Code,
    string Message)
{
    public string FormatConcise(string? workspaceRoot = null)
    {
        var displayPath = FilePath;
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            try
            {
                var rel = Path.GetRelativePath(workspaceRoot, FilePath);
                if (!rel.StartsWith("..", StringComparison.Ordinal))
                {
                    displayPath = rel.Replace('\\', '/');
                }
            }
            catch
            {
                // Keep original path on invalid path chars.
            }
        }
        var loc = Column is > 0 ? $"{displayPath}:{Line}:{Column}" : $"{displayPath}:{Line}";
        var codePart = string.IsNullOrWhiteSpace(Code) ? string.Empty : $" [{Code}]";
        return $"{loc}{codePart} {Severity}: {Message}";
    }
}

public sealed record WorkspaceDiagnosticsRequest(
    string WorkspaceRoot,
    string ToolId,
    string TargetFilePath);

/// <summary>
/// Lightweight, bounded-timeout post-edit diagnostics collector invoked after file-modifying
/// tools (<c>write_file</c>, <c>edit_file</c>, <c>apply_patch</c>, etc.).
/// <para>
/// Designed to be a fast 0ms no-op on non-code files, clean edits, or synthetic unit-test
/// temp workspaces without an initialized compiler target (<c>obj/project.assets.json</c> for
/// C# or local <c>node_modules/.bin/tsc</c> for TypeScript).
/// </para>
/// </summary>
public sealed class WorkspaceDiagnosticsCollector
{
    public const int MaxDiagnosticsReturned = 10;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(4);

    private static readonly HashSet<string> FileEditTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_file", "edit_file", "apply_patch", "replace_in_file", "multi_edit", "str_replace_editor"
    };

    // Matches MSBuild: path/File.cs(12,34): error CS1002: ; expected [proj]
    // and TypeScript: path/file.ts(12,34): error TS2322: Type 'number' is not assignable...
    private static readonly Regex ParenCompilerLine = new(
        @"^(?<file>.+?)\((?<line>\d+)(?:,(?<col>\d+))?\):\s*(?<sev>error)\s+(?<code>[A-Za-z]+\d+):\s*(?<msg>.+?)(?:\s*\[[^\]]+\])?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches colon compiler format: path/file.ts:12:34 - error TS2322: ... or file.py:12:5: E999 ...
    private static readonly Regex ColonCompilerLine = new(
        @"^(?<file>[^:\r\n]+(?:\.[A-Za-z0-9]+)):(?<line>\d+)(?::(?<col>\d+))?\s*(?:-|:)\s*(?<sev>error)\s*(?<code>[A-Za-z]+\d+)?\s*:?\s*(?<msg>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly TimeSpan _timeout;
    private readonly Func<WorkspaceDiagnosticsRequest, CancellationToken, Task<IReadOnlyList<WorkspaceDiagnosticItem>>>? _customProbe;

    public static WorkspaceDiagnosticsCollector Default { get; } = new();

    public WorkspaceDiagnosticsCollector(
        TimeSpan? timeout = null,
        Func<WorkspaceDiagnosticsRequest, CancellationToken, Task<IReadOnlyList<WorkspaceDiagnosticItem>>>? customProbe = null)
    {
        _timeout = timeout is { TotalMilliseconds: > 0 } value ? value : DefaultTimeout;
        _customProbe = customProbe;
    }

    public static bool IsFileEditTool(string toolId) =>
        !string.IsNullOrWhiteSpace(toolId) && FileEditTools.Contains(toolId);

    public async Task<IReadOnlyList<WorkspaceDiagnosticItem>> CollectAfterEditAsync(
        string? workspaceRoot,
        string toolId,
        bool mutatesWorkspace,
        JsonElement parameters,
        JsonElement? result,
        CancellationToken cancellationToken = default)
    {
        if (!mutatesWorkspace || string.IsNullOrWhiteSpace(workspaceRoot) || !IsFileEditTool(toolId))
            return [];

        var targetPath = ResolveEditedFilePath(workspaceRoot, parameters, result);
        if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
            return [];

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);

        try
        {
            if (_customProbe is not null)
            {
                var custom = await _customProbe(new WorkspaceDiagnosticsRequest(workspaceRoot, toolId, targetPath), cts.Token).ConfigureAwait(false);
                return custom.Take(MaxDiagnosticsReturned).ToArray();
            }

            var ext = Path.GetExtension(targetPath);
            if (string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase))
            {
                return CheckJsonSyntax(targetPath);
            }

            if (string.Equals(ext, ".cs", StringComparison.OrdinalIgnoreCase))
            {
                return await CheckCSharpProjectAsync(workspaceRoot, targetPath, cts.Token).ConfigureAwait(false);
            }

            if (ext is ".ts" or ".tsx" or ".mts" or ".cts")
            {
                return await CheckTypeScriptProjectAsync(workspaceRoot, targetPath, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Diagnostics are strictly advisory feedback; timeouts or missing toolchains never fail the edit.
        }

        return [];
    }

    public static IReadOnlyList<WorkspaceDiagnosticItem> ParseCompilerOutput(string output, string? baseDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];
        var items = new List<WorkspaceDiagnosticItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var match = ParenCompilerLine.Match(line);
            if (!match.Success) match = ColonCompilerLine.Match(line);
            if (!match.Success) continue;

            var rawFile = match.Groups["file"].Value.Trim();
            var fullFile = !string.IsNullOrWhiteSpace(baseDirectory) && !Path.IsPathRooted(rawFile)
                ? Path.GetFullPath(Path.Combine(baseDirectory, rawFile))
                : rawFile;
            if (!int.TryParse(match.Groups["line"].Value, out var lineNum)) continue;
            int? colNum = int.TryParse(match.Groups["col"].Value, out var c) ? c : null;
            var code = match.Groups["code"].Value.Trim();
            var message = match.Groups["msg"].Value.Trim();
            var dedupeKey = $"{fullFile}:{lineNum}:{colNum}:{code}:{message}";
            if (!seen.Add(dedupeKey)) continue;

            items.Add(new WorkspaceDiagnosticItem(fullFile, lineNum, colNum, "error", code, message));
            if (items.Count >= MaxDiagnosticsReturned) break;
        }

        return items;
    }

    private static IReadOnlyList<WorkspaceDiagnosticItem> CheckJsonSyntax(string targetPath)
    {
        var fileName = Path.GetFileName(targetPath);
        var allowCommentsAndTrailingCommas = fileName.StartsWith("tsconfig", StringComparison.OrdinalIgnoreCase);
        try
        {
            var bytes = File.ReadAllBytes(targetPath);
            using var _ = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = allowCommentsAndTrailingCommas,
                CommentHandling = allowCommentsAndTrailingCommas ? JsonCommentHandling.Skip : JsonCommentHandling.Disallow
            });
            return [];
        }
        catch (JsonException ex)
        {
            var line = (int)(ex.LineNumber ?? 0) + 1;
            var col = ex.BytePositionInLine.HasValue ? (int?)((int)ex.BytePositionInLine.Value + 1) : null;
            return [new WorkspaceDiagnosticItem(targetPath, line, col, "error", "JSON001", ex.Message)];
        }
    }

    private static async Task<IReadOnlyList<WorkspaceDiagnosticItem>> CheckCSharpProjectAsync(
        string workspaceRoot,
        string targetPath,
        CancellationToken cancellationToken)
    {
        var csproj = FindNearestAncestorFile(workspaceRoot, targetPath, "*.csproj");
        if (csproj is null) return [];
        var projDir = Path.GetDirectoryName(csproj)!;
        // Fast no-op when project has not been restored (e.g. synthetic test fixtures).
        if (!File.Exists(Path.Combine(projDir, "obj", "project.assets.json")))
            return [];

        var output = await RunBoundedProcessAsync(
            "dotnet",
            ["build", csproj, "--no-restore", "-nologo", "-v", "quiet"],
            projDir,
            cancellationToken).ConfigureAwait(false);
        return ParseCompilerOutput(output, projDir);
    }

    private static async Task<IReadOnlyList<WorkspaceDiagnosticItem>> CheckTypeScriptProjectAsync(
        string workspaceRoot,
        string targetPath,
        CancellationToken cancellationToken)
    {
        var tsconfig = FindNearestAncestorFile(workspaceRoot, targetPath, "tsconfig.json");
        if (tsconfig is null) return [];
        var projDir = Path.GetDirectoryName(tsconfig)!;
        var tscBinary = FindLocalTscBinary(workspaceRoot, projDir);
        if (tscBinary is null) return [];

        var output = await RunBoundedProcessAsync(
            tscBinary,
            ["--noEmit", "--pretty", "false", "-p", tsconfig],
            projDir,
            cancellationToken).ConfigureAwait(false);
        return ParseCompilerOutput(output, projDir);
    }

    private static string? FindNearestAncestorFile(string workspaceRoot, string filePath, string searchPattern)
    {
        var fullRoot = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = Directory.GetParent(Path.GetFullPath(filePath));
        while (current is not null)
        {
            var currentPath = current.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!currentPath.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                break;

            var match = Directory.EnumerateFiles(current.FullName, searchPattern, SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (match is not null) return match;
            if (string.Equals(currentPath, fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                break;
            current = current.Parent;
        }
        return null;
    }

    private static string? FindLocalTscBinary(string workspaceRoot, string startDir)
    {
        var fullRoot = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = new DirectoryInfo(Path.GetFullPath(startDir));
        var binName = OperatingSystem.IsWindows() ? "tsc.cmd" : "tsc";
        while (current is not null)
        {
            var currentPath = current.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!currentPath.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                break;
            var candidate = Path.Combine(current.FullName, "node_modules", ".bin", binName);
            if (File.Exists(candidate)) return candidate;
            if (string.Equals(currentPath, fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                break;
            current = current.Parent;
        }
        return null;
    }

    private static async Task<string> RunBoundedProcessAsync(
        string fileName,
        IReadOnlyList<string> args,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode == 0) return string.Empty;
            return string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}";
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }

    public static string? ResolveEditedFilePath(string workspaceRoot, JsonElement parameters, JsonElement? result)
    {
        string? raw = null;
        if (result is { ValueKind: JsonValueKind.Object } resObj)
        {
            raw = TryReadString(resObj, "path") ?? TryReadString(resObj, "file_path") ?? TryReadString(resObj, "target_file");
        }
        if (string.IsNullOrWhiteSpace(raw) && parameters.ValueKind == JsonValueKind.Object)
        {
            raw = TryReadString(parameters, "path") ?? TryReadString(parameters, "file_path") ?? TryReadString(parameters, "target_file");
        }
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            var fullRoot = Path.GetFullPath(workspaceRoot);
            var resolved = Path.IsPathRooted(raw) ? Path.GetFullPath(raw) : Path.GetFullPath(Path.Combine(fullRoot, raw));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var rootPrefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return string.Equals(resolved, fullRoot, comparison) || resolved.StartsWith(rootPrefix, comparison)
                ? resolved
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
}
