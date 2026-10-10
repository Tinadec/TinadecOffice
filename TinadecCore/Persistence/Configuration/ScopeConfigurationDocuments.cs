using System.Security.Cryptography;
using System.Text;
using TinadecCore.Abstractions.Ports;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;

namespace TinadecCore.Persistence;

public sealed class ScopeConfigurationDocuments(
    IScopeStorageLocations locations,
    IEnumerable<IConfigurationDocumentValidator> validators) : IScopeConfigurationDocuments
{
    private static readonly string[] Ids = ["runtime", "agents", "models", "tools", "mcp", "prompts", "skills", "storage", "logging"];
    private readonly SemaphoreSlim _writer = new(1, 1);
    public IReadOnlyList<string> DocumentIds => Ids;
    internal bool RequiresRuntimeDocument => validators.Any(v => v.DocumentId == "runtime");

    public Task<ScopeConfigurationDocument> ReadAsync(string id, CancellationToken cancellationToken = default) =>
        ReadCoreAsync(id, null, cancellationToken);

    // Only the coordinator may supply a successfully validated and applied
    // projection. Re-read the source bytes and verify their hash on every
    // attach; unchanged files need no repeated SQL/history/TOML validation.
    internal Task<ScopeConfigurationDocument> ReadForProjectionAsync(string id, ScopeConfigurationDocument? applied,
        CancellationToken cancellationToken) => ReadCoreAsync(id, applied, cancellationToken);

    private async Task<ScopeConfigurationDocument> ReadCoreAsync(string id, ScopeConfigurationDocument? applied, CancellationToken cancellationToken)
    {
        var path = ResolvePath(id);
        var exists = File.Exists(path);
        var text = exists ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) : "";
        cancellationToken.ThrowIfCancellationRequested();
        var hash = exists ? Hash(text) : "";
        if (exists && applied is not null && applied.Id == id && applied.Path == path && applied.ContentHash == hash)
            return applied;
        var diagnostics = exists ? await ValidateAsync(id, text, cancellationToken).ConfigureAwait(false)
            : File.Exists(EstablishedPath(id)) || ScopeConfigurationInitialization.IsPublishedProject(locations)
                ? new ConfigurationDiagnostic[] { new("configuration_missing", "An established configuration file is missing. Restore it or save a valid replacement.") } : [];
        return new(id, path, text, hash, diagnostics, DocumentVersion(text));
    }

    public Task<IReadOnlyList<ConfigurationDiagnostic>> ValidateAsync(string id, string text, CancellationToken cancellationToken = default)
    {
        _ = ResolvePath(id);
        cancellationToken.ThrowIfCancellationRequested();
        if (Encoding.UTF8.GetByteCount(text) > 16 * 1024 * 1024)
            return Task.FromResult<IReadOnlyList<ConfigurationDiagnostic>>([new("document_too_large", "Configuration documents may not exceed 16 MiB.")]);
        var syntax = SyntaxParser.Parse(text, sourceName: id + ".toml");
        var errors = syntax.Diagnostics.Select(d => new ConfigurationDiagnostic(
            "toml_syntax", d.Message, d.Kind.ToString().ToLowerInvariant(), d.Span.Start.Line + 1, d.Span.Start.Column + 1)).ToList();
        if (!syntax.HasErrors)
        {
            foreach (var validator in validators.Where(v => v.DocumentId == id))
                errors.AddRange(validator.Validate(text));
        }
        return Task.FromResult<IReadOnlyList<ConfigurationDiagnostic>>(errors);
    }

    public async Task<ScopeConfigurationDocument> SaveIfMatchAsync(string id, string text, string expectedContentHash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedContentHash);
        var path = ResolvePath(id);
        var diagnostics = await ValidateAsync(id, text, cancellationToken).ConfigureAwait(false);
        if (diagnostics.Any(d => d.Severity == "error"))
            throw new ConfigurationDocumentException("configuration_invalid", "Configuration validation failed.", diagnostics);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(locations.Config);
            AssertNoLinks(path);
            // Exclusive OS lock also serializes separate Core processes using the same scope.
            await using var lease = await AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
            var actual = File.Exists(path) ? Hash(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)) : "";
            if (!string.Equals(actual, expectedContentHash.Trim('"'), StringComparison.OrdinalIgnoreCase))
                throw new ConfigurationDocumentException("configuration_conflict", "Configuration changed since it was read. Reload before saving.");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
                AssertNoLinks(path);
                File.Move(temporary, path, overwrite: true);
                await MarkEstablishedAsync(id, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return new(id, path, text, Hash(text), diagnostics, DocumentVersion(text));
        }
        finally { _writer.Release(); }
    }

    internal string ResolvePath(string id)
    {
        if (!Ids.Contains(id, StringComparer.Ordinal))
            throw new ConfigurationDocumentException("configuration_not_found", "Unknown configuration document.");
        var path = Path.GetFullPath(Path.Combine(locations.Config, id + ".toml"));
        AssertNoLinks(path);
        return path;
    }

    internal async Task MarkEstablishedAsync(string id, CancellationToken cancellationToken)
    {
        var path = EstablishedPath(id);
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AssertNoLinks(path);
        try
        {
            await using var marker = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await marker.WriteAsync(Encoding.UTF8.GetBytes(id + "\n"), cancellationToken).ConfigureAwait(false);
            await marker.FlushAsync(cancellationToken).ConfigureAwait(false);
            marker.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(path)) { }
    }

    private string EstablishedPath(string id)
    {
        var path = Path.GetFullPath(Path.Combine(locations.State, ".configuration-documents", id + ".initialized"));
        AssertNoLinks(path);
        return path;
    }

    private void AssertNoLinks(string path)
    {
        var current = new FileInfo(path) as FileSystemInfo;
        var boundary = Path.GetFullPath(locations.Root).TrimEnd(Path.DirectorySeparatorChar);
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ConfigurationDocumentException("configuration_link_rejected", "Configuration storage cannot follow a symbolic link or reparse point.");
            if (string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar), boundary,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
            current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent;
        }
    }

    private async Task<FileStream> AcquireLeaseAsync(CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(locations.State, ".configuration-write.lock");
        AssertNoLinks(lockPath);
        Directory.CreateDirectory(locations.State);
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 200) { await Task.Delay(25, cancellationToken).ConfigureAwait(false); }
        }
    }

    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static long DocumentVersion(string text)
    {
        try
        {
            var table = TomlSerializer.Deserialize<TomlTable>(text);
            return table is not null && table.TryGetValue("version", out var value) && value is long version ? version : 1;
        }
        catch (TomlException) { return 1; }
    }
}
