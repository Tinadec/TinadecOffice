using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using Tomlyn;
using Tomlyn.Model;

namespace TinadecCore.Tools;

/// <summary>Owns explicit program installation, independently of MCP resource registration.</summary>
public sealed class ManagedMcpProgramService(IMcpResourceRegistry resources, IScopeStorageLocations locations,
    IToolProvider provider, IServiceProvider services) : IManagedMcpProgramService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Writers = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly Regex Npm = new(@"\A(?<package>@[a-z0-9._-]+/[a-z0-9._-]+|[a-z0-9._-]+)@(?<version>[0-9]+\.[0-9]+\.[0-9]+(?:-[a-zA-Z0-9.-]+)?(?:\+[a-zA-Z0-9.-]+)?)\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Python = new(@"\A(?<package>[a-zA-Z0-9][a-zA-Z0-9._-]*)==(?<version>[0-9][a-zA-Z0-9.!+_-]*)\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public async Task<ManagedMcpProgramPreviewDto> PreviewAsync(Guid resourceId, Guid? projectId, string action, CancellationToken cancellationToken = default)
    {
        if (action is not ("install" or "uninstall")) throw new ToolSettingsException("invalid_mcp_program_action", "Program action must be install or uninstall.");
        var resource = await resources.GetAsync(resourceId, cancellationToken) ?? throw new ToolSettingsException("mcp_resource_not_found", "MCP resource was not found.", 404);
        if (resource.ProjectId != projectId) throw new ToolSettingsException("mcp_program_scope", "The program action must use the resource's scope.", 409);
        var spec = Parse(resource.Command, resource.Args);
        var hash = PlanHash(resourceId, resource.Revision, spec.Manager, spec.Package, spec.Version, spec.Arguments);
        var installed = await ReadPointerAsync(resourceId, cancellationToken);
        var status = installed is null ? "not_installed" : installed.Value.Status == "needs_reinstall" ? "needs_reinstall"
            : installed.Value.Hash != hash ? "outdated"
            : await ValidManifestAsync(installed.Value.Root, resourceId, hash, spec.Manager, spec.Package, spec.Version, spec.Arguments, cancellationToken) is null ? "invalid" : "installed";
        var previewId = Guid.NewGuid();
        var root = status == "installed" ? installed!.Value.Root : PackageRoot(resourceId, hash);
        if (status is "needs_reinstall" or "invalid" || installed is null && Directory.Exists(root)
            && await ValidManifestAsync(root, resourceId, hash, spec.Manager, spec.Package, spec.Version, spec.Arguments, cancellationToken) is null)
            root = PackageRoot(resourceId, hash) + "-" + previewId.ToString("N");
        return new(resourceId, projectId, action, resource.Revision, spec.Manager, spec.Package, spec.Version, root, hash,
            status, spec.Arguments, previewId);
    }

    public async Task<UserToolActionResult> RequestApplyAsync(ManagedMcpProgramPreviewDto preview, CancellationToken cancellationToken = default)
    {
        await ValidatePreviewAsync(preview, cancellationToken);
        return await services.GetRequiredService<IUserToolActionService>().CreateAsync(new(preview.ProjectId ?? Guid.Empty,
            "mcp_program_update", JsonSerializer.Serialize(preview, Json),
            $"mcp-program:{locations.StorageId}:{preview.PreviewId:N}"), cancellationToken);
    }

    public async Task<ManagedMcpProgramResultDto> ApplyApprovedAsync(ManagedMcpProgramPreviewDto preview, CancellationToken cancellationToken = default)
    {
        var gate = Writers.GetOrAdd(locations.StorageId + ":" + preview.ResourceId.ToString("N"), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await ValidatePreviewAsync(preview, cancellationToken);
            AssertOwnedPath(preview.PackageRoot);
            if (preview.Action == "uninstall")
            {
                var previous = await ReadPointerAsync(preview.ResourceId, cancellationToken);
                File.Delete(PointerPath(preview.ResourceId));
                return new(preview.ResourceId, "not_installed", previous?.Root, previous?.Hash, previous is not null);
            }
            var manifest = Path.Combine(preview.PackageRoot, "installation.toml");
            if (File.Exists(manifest) && await ValidManifestAsync(preview.PackageRoot, preview.ResourceId, preview.PlanHash,
                preview.Manager, preview.Package, preview.Version, preview.ServerArguments, cancellationToken) is null)
                throw new ToolSettingsException("mcp_program_integrity", "The reviewed installation directory changed; review a fresh install preview.", 412);
            if (!File.Exists(manifest))
            {
                var context = new ToolExecutionContextDto { StorageId = locations.StorageId, StorageRoot = locations.Root,
                    ProjectRoot = locations.ProjectRoot, ProtectedStorageRoots = services.GetService<IProtectedStorageRoots>()?.Roots ?? [],
                    Settings = JsonSerializer.SerializeToElement(new { }), AllowedToolIds = ["#managed_mcp_program"], SettingsHash = preview.PlanHash };
                var response = await provider.CallAsync(locations.ProjectRoot ?? "", new ToolWireRequestDto { ToolId = "#managed_mcp_program",
                    SessionId = "mcp-program-install", Approved = true, ExecutionContext = context,
                    Params = JsonSerializer.SerializeToElement(new { manager = preview.Manager, package = preview.Package, version = preview.Version,
                        package_root = preview.PackageRoot, server_arguments = preview.ServerArguments, plan_hash = preview.PlanHash }) }, TimeSpan.FromMinutes(10), cancellationToken);
                if (!response.IsSuccess || response.Result is not { } result || !result.TryGetProperty("success", out var success) || !success.GetBoolean())
                    throw new ToolSettingsException("mcp_program_install_failed", "The approved program installer failed: " + (response.FailureMessage ?? response.Result?.ToString()), 409);
                var command = result.GetProperty("launch_command").GetString()!;
                var args = result.GetProperty("launch_arguments").EnumerateArray().Select(x => x.GetString()!).ToArray();
                var launchArguments = new TomlArray(); foreach (var arg in args) launchArguments.Add(arg);
                var record = new TomlTable { ["schema_version"] = 1L, ["resource_id"] = preview.ResourceId.ToString("N"),
                    ["plan_hash"] = preview.PlanHash, ["manager"] = preview.Manager, ["package"] = preview.Package,
                    ["version"] = preview.Version, ["launch_command"] = command, ["launch_arguments"] = launchArguments,
                    ["content_hash"] = await ContentHashAsync(preview.PackageRoot, cancellationToken) };
                await WriteTomlAsync(manifest, record, cancellationToken);
            }
            await WriteTomlAsync(PointerPath(preview.ResourceId), new TomlTable { ["schema_version"] = 1L,
                ["program_root"] = preview.PackageRoot, ["program_hash"] = preview.PlanHash, ["program_status"] = "installed" }, cancellationToken);
            return new(preview.ResourceId, "installed", preview.PackageRoot, preview.PlanHash);
        }
        finally { gate.Release(); }
    }

    public async Task<ToolMcpServerSnapshotDto> ResolveForExecutionAsync(ToolMcpServerSnapshotDto resource, CancellationToken cancellationToken = default)
    {
        if (!IsManagedLauncher(resource.Command)) return resource;
        var spec = Parse(resource.Command, resource.Args);
        var expected = PlanHash(resource.ResourceId, resource.Revision, spec.Manager, spec.Package, spec.Version, spec.Arguments);
        var installed = await ReadPointerAsync(resource.ResourceId, cancellationToken);
        if (installed is null || installed.Value.Status != "installed" || installed.Value.Hash != expected || !Directory.Exists(installed.Value.Root)) return Copy(resource, resource.Command, resource.Args, null, null, installed is null ? "not_installed" : installed.Value.Status == "needs_reinstall" ? "needs_reinstall" : "outdated");
        AssertOwnedPath(installed.Value.Root);
        var model = await ValidManifestAsync(installed.Value.Root, resource.ResourceId, expected, spec.Manager, spec.Package,
            spec.Version, spec.Arguments, cancellationToken);
        if (model is null) return Copy(resource, resource.Command, resource.Args, null, null, "invalid");
        var contentHash = (string)model["content_hash"];
        var command = (string)model["launch_command"];
        var args = ((TomlArray)model["launch_arguments"]).Select(x => (string)x!).ToArray();
        return Copy(resource, command, args, installed.Value.Root, contentHash, "installed");
    }

    private async Task<TomlTable?> ValidManifestAsync(string root, Guid id, string planHash, string manager, string package,
        string version, IReadOnlyList<string> serverArguments, CancellationToken ct)
    {
        AssertOwnedPath(root);
        var manifest = Path.Combine(root, "installation.toml"); AssertOwnedPath(manifest);
        if (!File.Exists(manifest)) return null;
        TomlTable? model;
        try { model = TomlSerializer.Deserialize<TomlTable>(await File.ReadAllTextAsync(manifest, ct)); }
        catch (TomlException) { return null; }
        if (model is null || Read(model, "resource_id") as string != id.ToString("N")
            || Read(model, "plan_hash") as string != planHash || Read(model, "manager") as string != manager
            || Read(model, "package") as string != package || Read(model, "version") as string != version
            || Read(model, "content_hash") is not string contentHash || contentHash != await ContentHashAsync(root, ct)
            || Read(model, "launch_command") is not string command || Read(model, "launch_arguments") is not TomlArray arguments
            || arguments.Any(x => x is not string)) return null;
        var args = arguments.Cast<string>().ToArray();
        if (manager == "npm")
        {
            if (Path.GetFileNameWithoutExtension(command) != "node" || args.Length == 0
                || !Path.IsPathFullyQualified(args[0]) || !IsContained(root, args[0]) || !File.Exists(args[0])
                || !args.Skip(1).SequenceEqual(serverArguments)) return null;
            AssertOwnedPath(args[0]);
        }
        else
        {
            if (!Path.IsPathFullyQualified(command) || !IsContained(root, command) || !File.Exists(command)
                || !args.SequenceEqual(serverArguments)) return null;
            AssertOwnedPath(command);
        }
        return model;
    }

    private async Task ValidatePreviewAsync(ManagedMcpProgramPreviewDto preview, CancellationToken ct)
    {
        if (preview.PreviewId == Guid.Empty) throw new ToolSettingsException("mcp_program_preview_required", "Review a fresh program installation preview before applying it.", 412);
        var current = await PreviewAsync(preview.ResourceId, preview.ProjectId, preview.Action, ct);
        if (current.PlanHash != preview.PlanHash || current.ExpectedRevision != preview.ExpectedRevision
            || !IsPackageRoot(preview.ResourceId, preview.PlanHash, preview.PackageRoot) || current.Manager != preview.Manager || current.Package != preview.Package || current.Version != preview.Version
            || !current.ServerArguments.SequenceEqual(preview.ServerArguments))
            throw new ToolSettingsException("mcp_program_plan_changed", "The MCP resource or installation plan changed since review.", 412);
    }

    internal static bool IsManagedLauncher(string command) => Path.GetFileNameWithoutExtension(command).ToLowerInvariant() is "npx" or "uvx";
    internal static (string Manager, string Package, string Version, IReadOnlyList<string> Arguments) Parse(string command, IReadOnlyList<string> args)
    {
        var launcher = Path.GetFileNameWithoutExtension(command).ToLowerInvariant();
        var offset = launcher == "npx" && args.Count > 0 && args[0] is "-y" or "--yes" ? 1 : 0;
        if (args.Count <= offset || launcher is not ("npx" or "uvx")) throw new ToolSettingsException("mcp_program_external", "Explicit installation supports pinned npx and uvx package registrations; other commands remain externally managed.");
        var match = (launcher == "npx" ? Npm : Python).Match(args[offset]);
        if (!match.Success) throw new ToolSettingsException("mcp_program_version_required", "Managed MCP programs require an exact npm version or PyPI == version.");
        return (launcher == "npx" ? "npm" : "uv", match.Groups["package"].Value, match.Groups["version"].Value, args.Skip(offset + 1).ToArray());
    }

    private static string PlanHash(Guid id, long revision, string manager, string package, string version, IReadOnlyList<string> arguments) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { id, revision, manager, package, version, arguments })));
    private string PackageRoot(Guid id, string hash) => Path.Combine(locations.Packages, "mcp", id.ToString("N"), hash);
    private string PointerPath(Guid id) => Path.Combine(locations.State, "mcp-programs", id.ToString("N") + ".toml");
    private bool IsPackageRoot(Guid id, string hash, string root)
    {
        var expected = PackageRoot(id, hash);
        return root == expected || root.StartsWith(expected + "-", StringComparison.Ordinal)
            && Guid.TryParseExact(root[(expected.Length + 1)..], "N", out _);
    }
    private async Task<(string Root, string Hash, string Status)?> ReadPointerAsync(Guid id, CancellationToken ct)
    {
        var path = PointerPath(id);
        AssertOwnedPath(path);
        if (!File.Exists(path)) return null;
        var table = TomlSerializer.Deserialize<TomlTable>(await File.ReadAllTextAsync(path, ct))!;
        var root = (string)table["program_root"]; var hash = (string)table["program_hash"];
        if (hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c)) || !IsPackageRoot(id, hash, root))
            throw new ToolSettingsException("mcp_program_pointer_invalid", "The managed program pointer leaves its scope.");
        return (root, hash, table.TryGetValue("program_status", out var status) && status is string text ? text : "installed");
    }
    private async Task WriteTomlAsync(string path, TomlTable table, CancellationToken ct)
    {
        AssertOwnedPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, TomlSerializer.Serialize(table), ct); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private void AssertOwnedPath(string path)
    {
        var relative = Path.GetRelativePath(locations.Root, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new ToolSettingsException("mcp_program_path_escape", "The managed program path leaves its scope.");
        var current = locations.Root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ToolSettingsException("mcp_program_path_escape", "Managed program control paths cannot traverse links.");
        }
    }
    private static async Task<string> ContentHashAsync(string root, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var pending = new Stack<string>(); pending.Push(root);
        var entries = new List<string>();
        while (pending.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0) pending.Push(entry);
                else entries.Add(entry);
            }
        foreach (var entry in entries.OrderBy(x => Path.GetRelativePath(root, x), StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
            if (relative == "installation.toml") continue;
            ct.ThrowIfCancellationRequested(); hash.AppendData(Encoding.UTF8.GetBytes(relative + "\0"));
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(entry) : new FileInfo(entry);
                hash.AppendData(Encoding.UTF8.GetBytes("link\0" + info.LinkTarget + "\0"));
                if ((attributes & FileAttributes.Directory) != 0) continue;
            }
            await using var input = File.OpenRead(entry);
            hash.AppendData(await SHA256.HashDataAsync(input, ct));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    /// <summary>Initializes receipts from an already copied package tree. It never invokes a package manager.</summary>
    public static async Task<IReadOnlyList<string>> InitializeCopiedResourcesAsync(string sourceScopeRoot,
        string targetScopeRoot, string filesRoot, CancellationToken cancellationToken = default)
    {
        sourceScopeRoot = Path.GetFullPath(sourceScopeRoot); targetScopeRoot = Path.GetFullPath(targetScopeRoot);
        filesRoot = Path.GetFullPath(filesRoot);
        var sourcePointers = Path.Combine(sourceScopeRoot, "state", "mcp-programs");
        if (!Directory.Exists(sourcePointers)) return [];
        var diagnostics = new List<string>();
        foreach (var sourcePointer in Directory.EnumerateFiles(sourcePointers, "*.toml"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(sourcePointer), "N", out var id)) continue;
            RejectLinks(sourceScopeRoot, sourcePointer);
            var pointer = TomlSerializer.Deserialize<TomlTable>(await File.ReadAllTextAsync(sourcePointer, cancellationToken))!;
            if (!pointer.TryGetValue("program_root", out var rootValue) || rootValue is not string sourceProgram
                || !pointer.TryGetValue("program_hash", out var hashValue) || hashValue is not string planHash
                || planHash.Length != 64 || planHash.Any(c => !char.IsAsciiHexDigit(c)))
            { diagnostics.Add($"invalid_mcp_receipt:{id:N}"); continue; }
            var expected = Path.Combine(sourceScopeRoot, "packages", "mcp", id.ToString("N"), planHash);
            if (sourceProgram != expected && !(sourceProgram.StartsWith(expected + "-", StringComparison.Ordinal)
                && Guid.TryParseExact(sourceProgram[(expected.Length + 1)..], "N", out _)))
            { diagnostics.Add($"invalid_mcp_receipt:{id:N}"); continue; }
            var relative = Path.GetRelativePath(sourceScopeRoot, sourceProgram);
            var stagedProgram = Path.Combine(filesRoot, relative);
            var finalProgram = Path.Combine(targetScopeRoot, relative);
            var stagedManifest = Path.Combine(stagedProgram, "installation.toml");
            if (!File.Exists(stagedManifest)) { diagnostics.Add($"missing_mcp_package:{id:N}"); continue; }
            RejectLinks(filesRoot, stagedManifest);
            var manifest = TomlSerializer.Deserialize<TomlTable>(await File.ReadAllTextAsync(stagedManifest, cancellationToken))!;
            var manager = Read(manifest, "manager") as string;
            var valid = Read(manifest, "resource_id") as string == id.ToString("N")
                && Read(manifest, "plan_hash") as string == planHash
                && Read(manifest, "content_hash") is string digest && digest == await ContentHashAsync(stagedProgram, cancellationToken);
            var status = valid && manager == "npm" ? "installed" : "needs_reinstall";
            if (status == "installed")
            {
                var arguments = (TomlArray)manifest["launch_arguments"];
                if (arguments.Count == 0 || arguments[0] is not string entry
                    || !IsContained(sourceProgram, entry) || Path.GetFileNameWithoutExtension((string)manifest["launch_command"]) != "node")
                    status = "needs_reinstall";
                else
                {
                    var rewritten = new TomlArray();
                    foreach (var argument in arguments.Cast<string>()) rewritten.Add(Path.IsPathFullyQualified(argument) && IsContained(sourceProgram, argument)
                        ? Path.Combine(finalProgram, Path.GetRelativePath(sourceProgram, argument)) : argument);
                    manifest["launch_arguments"] = rewritten;
                    await WriteCopiedTomlAsync(stagedManifest, manifest, cancellationToken);
                }
            }
            if (status != "installed") diagnostics.Add($"needs_reinstall:{id:N}");
            await WriteCopiedTomlAsync(Path.Combine(filesRoot, "state", "mcp-programs", id.ToString("N") + ".toml"),
                new TomlTable { ["schema_version"] = 1L, ["program_root"] = finalProgram,
                    ["program_hash"] = planHash, ["program_status"] = status }, cancellationToken);
        }
        return diagnostics;
    }
    private static object? Read(TomlTable table, string name) => table.TryGetValue(name, out var value) ? value : null;
    private static bool IsContained(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
    private static void RejectLinks(string root, string path)
    {
        if (!IsContained(root, path)) throw new ToolSettingsException("mcp_program_path_escape", "The copied receipt leaves its scope.");
        var current = root;
        foreach (var part in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if (!File.Exists(current) && !Directory.Exists(current)) break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ToolSettingsException("mcp_program_path_escape", "Copied program control paths cannot traverse links.");
        }
    }
    private static async Task WriteCopiedTomlAsync(string path, TomlTable table, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, TomlSerializer.Serialize(table), ct); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static ToolMcpServerSnapshotDto Copy(ToolMcpServerSnapshotDto original, string command, IReadOnlyList<string> args, string? root, string? hash, string status) => new()
    { ResourceId = original.ResourceId, Id = original.Id, Name = original.Name, Command = command, Args = args, Env = original.Env,
      SecretReferences = original.SecretReferences, Cwd = original.Cwd, Revision = original.Revision, ConfigurationHash = original.ConfigurationHash,
      ProgramRoot = root, ProgramHash = hash, ProgramStatus = status };
}
