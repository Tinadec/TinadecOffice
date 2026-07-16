using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.Memory;

public sealed class ProjectSessionStore : ISessionLocator, IStorageMigrationParticipant
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> SessionLocks = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly IDbContextFactory<MemoryDbContext> _dbFactory;
    private readonly StoragePaths _paths;

    public ProjectSessionStore(IDbContextFactory<MemoryDbContext> dbFactory, StoragePaths paths)
    {
        _dbFactory = dbFactory;
        _paths = paths;
    }

    public async Task<ProjectRecord> CreateProjectAsync(string name, string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Project name and path are required.");
        }

        var rootPath = Path.GetFullPath(path.Trim());
        if (!Path.IsPathRooted(rootPath))
        {
            throw new ArgumentException("Project path must be absolute.");
        }

        var now = DateTimeOffset.UtcNow;
        var project = new ProjectRecord
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            RootPath = Path.TrimEndingDirectorySeparator(rootPath),
            NormalizedRootPath = NormalizeRootPath(rootPath),
            CreatedAt = now,
            UpdatedAt = now
        };

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await db.Projects.AnyAsync(x => x.NormalizedRootPath == project.NormalizedRootPath, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A project already exists for this root path.");
        }

        db.Projects.Add(project);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return project;
    }

    public async Task<IReadOnlyList<ProjectRecord>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var projects = await db.Projects.AsNoTracking().Where(x => !x.Archived).ToListAsync(cancellationToken).ConfigureAwait(false);
        return projects.OrderByDescending(x => x.UpdatedAt).ToList();
    }

    public async Task<SessionRecord> CreateSessionAsync(Guid projectId, string? title, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (!await db.Projects.AnyAsync(x => x.Id == projectId && !x.Archived, cancellationToken).ConfigureAwait(false))
        {
            throw new KeyNotFoundException("Project was not found.");
        }

        var now = DateTimeOffset.UtcNow;
        var session = new SessionRecord { Id = Guid.NewGuid(), ProjectId = projectId, Title = string.IsNullOrWhiteSpace(title) ? "New session" : title.Trim(), CreatedAt = now, UpdatedAt = now };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await WriteHistoryAsync(session.Id, new SessionHistoryFile { SessionId = session.Id }, cancellationToken).ConfigureAwait(false);
        return session;
    }

    public async Task<IReadOnlyList<SessionRecord>> ListSessionsAsync(Guid? projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.Sessions.AsNoTracking().Where(x => !x.Archived);
        if (projectId is { } id) query = query.Where(x => x.ProjectId == id);
        var sessions = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        return sessions.OrderByDescending(x => x.UpdatedAt).ToList();
    }

    public async Task<SessionRecord?> UpdateTitleAsync(Guid sessionId, string title, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Session title is required.");
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var session = await db.Sessions.SingleOrDefaultAsync(x => x.Id == sessionId && !x.Archived, cancellationToken).ConfigureAwait(false);
        if (session is null) return null;
        session.Title = title.Trim();
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    public async Task<IReadOnlyList<StoredMessage>> ListMessagesAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await EnsureSessionExistsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var history = await ReadHistoryAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return history.Messages;
    }

    public async Task<StoredMessage> AddMessageAsync(Guid sessionId, string content, string role = "user", Guid? runId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("Message content is required.");
        await EnsureSessionExistsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var gate = SessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var history = await ReadHistoryAsync(sessionId, cancellationToken).ConfigureAwait(false);
            var message = new StoredMessage { Id = Guid.NewGuid(), SessionId = sessionId, RunId = runId, Role = role, Content = content, CreatedAt = DateTimeOffset.UtcNow };
            history.Messages.Add(message);
            history.Revision++;
            await WriteHistoryAsync(sessionId, history, cancellationToken).ConfigureAwait(false);

            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var session = await db.Sessions.SingleAsync(x => x.Id == sessionId, cancellationToken).ConfigureAwait(false);
            session.HistoryRevision = history.Revision;
            session.UpdatedAt = message.CreatedAt;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return message;
        }
        finally { gate.Release(); }
    }

    public async Task<SessionReference?> FindAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Sessions.AsNoTracking().Where(x => x.Id == sessionId && !x.Archived)
            .Select(x => new SessionReference(x.Id, x.ProjectId)).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureSessionExistsAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (await FindAsync(sessionId, cancellationToken).ConfigureAwait(false) is null) throw new KeyNotFoundException("Session was not found.");
    }

    private async Task<SessionHistoryFile> ReadHistoryAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var path = _paths.SessionHistory(sessionId);
        if (!File.Exists(path)) return new SessionHistoryFile { SessionId = sessionId };
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SessionHistoryFile>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Session history is invalid.");
    }

    private async Task WriteHistoryAsync(Guid sessionId, SessionHistoryFile history, CancellationToken cancellationToken)
    {
        var path = _paths.SessionHistory(sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, history, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path)) File.Replace(temporary, path, destinationBackupFileName: null);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string NormalizeRootPath(string rootPath) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)).ToUpperInvariant();
}

public sealed class SessionHistoryFile
{
    public int Version { get; set; } = 1;
    public Guid SessionId { get; set; }
    public long Revision { get; set; }
    public List<StoredMessage> Messages { get; set; } = [];
}

public sealed class StoredMessage
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid? RunId { get; set; }
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
