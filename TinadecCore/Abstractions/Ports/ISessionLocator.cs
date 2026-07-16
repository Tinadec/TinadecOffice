namespace TinadecCore.Abstractions.Ports;

/// <summary>Minimal cross-module lookup used to validate lifecycle ownership boundaries.</summary>
public interface ISessionLocator
{
    Task<SessionReference?> FindAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

public sealed record SessionReference(Guid SessionId, Guid ProjectId);
