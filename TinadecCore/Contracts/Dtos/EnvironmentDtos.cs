namespace TinadecCore.Contracts.Dtos;

/// <summary>A slot of an environment and the run holding it (todo E1).</summary>
public sealed record EnvironmentHolderDto(int Slot, Guid LeaseId, Guid? SessionId, Guid? RunId, Guid? TaskId, string Reason);

/// <summary>
/// One registered environment and its occupancy. <c>connection</c> describes how to reach it and never
/// carries a credential (a credential is a <c>secret_ref</c> the secret store resolves).
/// </summary>
public sealed record EnvironmentDto(
    Guid Id,
    string Key,
    string Kind,
    string DisplayName,
    string? Description,
    System.Text.Json.JsonElement Connection,
    int Capacity,
    int FreeSlots,
    string Status,
    EnvironmentHolderDto[] Holders,
    DateTimeOffset UpdatedAt);

public sealed record EnvironmentRegisterRequest(string Key, string Kind, string DisplayName, string? Description = null, System.Text.Json.JsonElement? Connection = null, int? Capacity = null);

/// <summary>Omitted fields are left as they are; <c>status</c> is available or disabled.</summary>
public sealed record EnvironmentUpdateRequest(string? DisplayName = null, string? Description = null, System.Text.Json.JsonElement? Connection = null, int? Capacity = null, string? Status = null);
