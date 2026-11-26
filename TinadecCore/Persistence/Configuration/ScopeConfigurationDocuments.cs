using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using TinadecCore.Abstractions.Ports;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;

namespace TinadecCore.Persistence;

public sealed class ScopeConfigurationDocuments(
    IScopeStorageLocations locations,
    IEnumerable<IConfigurationDocumentValidator> validators,
    IConfigurationDocumentRedirects? redirects = null) : IScopeConfigurationDocuments
{
    private sealed record CachedFileHash(long Length, long LastWriteTimeUtcTicks, string Hash);
    private static readonly ConcurrentDictionary<string, CachedFileHash> FileHashCache = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Where a document lives: this scope, or the scope it is redirected to.</summary>
    private IScopeStorageLocations LocationsFor(string id) => redirects?.For(id) ?? locations;

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

    internal async Task<(string Path, string Hash, bool Exists)> HashForProjectionAsync(string id, CancellationToken cancellationToken)
    {
        var path = ResolvePath(id);
        if (!File.Exists(path)) return (path, string.Empty, false);
        var streamHash = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
        return (path, streamHash, true);
    }

    internal async Task<bool> IsProjectionUnchangedAsync(string id, ScopeConfigurationDocument applied, CancellationToken cancellationToken)
    {
        if (applied.Id != id || applied.ContentHash.Length == 0 || applied.Diagnostics.Any(d => d.Severity == "error"))
            return false;
        var path = ResolvePath(id);
        if (applied.Path != path || !File.Exists(path))
            return false;
        var streamHash = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
        return streamHash == applied.ContentHash;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (info.Exists)
        {
            var length = info.Length;
            var mtimeTicks = info.LastWriteTimeUtc.Ticks;
            if (DateTime.UtcNow.Ticks - mtimeTicks >= TimeSpan.TicksPerSecond * 2
                && FileHashCache.TryGetValue(fullPath, out var cached)
                && cached.Length == length
                && cached.LastWriteTimeUtcTicks == mtimeTicks)
            {
                return cached.Hash;
            }
            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            var hash = Convert.ToHexStringLower(digest);
            if (FileHashCache.Count >= 512) FileHashCache.Clear();
            FileHashCache[fullPath] = new CachedFileHash(length, mtimeTicks, hash);
            return hash;
        }
        await using var fallback = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var fallbackDigest = await SHA256.HashDataAsync(fallback, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(fallbackDigest);
    }

    private async Task<ScopeConfigurationDocument> ReadCoreAsync(string id, ScopeConfigurationDocument? applied, CancellationToken cancellationToken)
    {
        var path = ResolvePath(id);
        if (!File.Exists(path))
        {
            var missingDiagnostics = File.Exists(EstablishedPath(id)) || ScopeConfigurationInitialization.IsPublishedProject(LocationsFor(id))
                ? new ConfigurationDiagnostic[] { new("configuration_missing", "An established configuration file is missing. Restore it or save a valid replacement.") } : [];
            return new(id, path, "", "", missingDiagnostics, 1);
        }
        if (applied is not null && applied.Id == id && applied.Path == path && applied.ContentHash.Length > 0)
        {
            var streamHash = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (streamHash == applied.ContentHash)
            {
                if (applied.Text.Length > 0)
                    return applied;
                var cachedText = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (Hash(cachedText) == applied.ContentHash)
                    return new(id, path, cachedText, applied.ContentHash, applied.Diagnostics, DocumentVersion(cachedText));
            }
        }
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var hash = Hash(text);
        if (applied is not null && applied.Id == id && applied.Path == path && applied.ContentHash == hash && applied.Text.Length > 0)
            return applied;
        var diagnostics = await ValidateAsync(id, text, cancellationToken).ConfigureAwait(false);
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
        var target = LocationsFor(id);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(target.Config);
            AssertNoLinks(target, path);
            // Exclusive OS lock also serializes separate Core processes using the same scope —
            // and, for a redirected document, every scope that shares it.
            await using var lease = await AcquireLeaseAsync(target, cancellationToken).ConfigureAwait(false);
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
                AssertNoLinks(target, path);
                File.Move(temporary, path, overwrite: true);
                FileHashCache.TryRemove(Path.GetFullPath(path), out _);
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
        var target = LocationsFor(id);
        var path = Path.GetFullPath(Path.Combine(target.Config, id + ".toml"));
        AssertNoLinks(target, path);
        return path;
    }

    internal async Task MarkEstablishedAsync(string id, CancellationToken cancellationToken)
    {
        var path = EstablishedPath(id);
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AssertNoLinks(LocationsFor(id), path);
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
        var target = LocationsFor(id);
        var path = Path.GetFullPath(Path.Combine(target.State, ".configuration-documents", id + ".initialized"));
        AssertNoLinks(target, path);
        return path;
    }

    private static void AssertNoLinks(IScopeStorageLocations owner, string path)
    {
        var current = new FileInfo(path) as FileSystemInfo;
        var boundary = Path.GetFullPath(owner.Root).TrimEnd(Path.DirectorySeparatorChar);
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ConfigurationDocumentException("configuration_link_rejected", "Configuration storage cannot follow a symbolic link or reparse point.");
            if (string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar), boundary,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
            current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent;
        }
    }

    private static async Task<FileStream> AcquireLeaseAsync(IScopeStorageLocations owner, CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(owner.State, ".configuration-write.lock");
        AssertNoLinks(owner, lockPath);
        Directory.CreateDirectory(owner.State);
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
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;
            if (trimmed[0] == '[') break;
            if (trimmed.StartsWith("version", StringComparison.Ordinal))
            {
                var eq = trimmed.IndexOf('=');
                if (eq > 0 && trimmed[..eq].Trim().Equals("version", StringComparison.Ordinal))
                {
                    var rhs = trimmed[(eq + 1)..];
                    var comment = rhs.IndexOf('#');
                    if (comment >= 0) rhs = rhs[..comment];
                    if (long.TryParse(rhs.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var fastVersion))
                        return fastVersion;
                }
            }
        }
        try
        {
            var table = TomlSerializer.Deserialize<TomlTable>(text);
            return table is not null && table.TryGetValue("version", out var value) && value is long version ? version : 1;
        }
        catch (TomlException) { return 1; }
    }
}
