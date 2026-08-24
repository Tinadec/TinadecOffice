using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Resolves the trusted TinadecTools manifest for a run admission.  The resolver
/// owns project-root lookup and process access so callers cannot substitute a
/// workspace path or a caller-visible tool catalog.
/// </summary>
public interface IToolManifestSnapshotResolver
{
    Task<ToolManifestSnapshot> ResolveAsync(
        ToolManifestSnapshotRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The only authorization input accepted while freezing a manifest.  An explicit
/// wildcard is expanded against the v2 process manifest; an empty list authorizes
/// no tools.
/// </summary>
public sealed record ToolManifestSnapshotRequest(
    Guid SessionId,
    IReadOnlyList<string> AllowedToolIds,
    bool AllowAllTools);

/// <summary>
/// Immutable v2 tool metadata retained in the run's frozen configuration.  It is
/// deliberately independent from the mutable process DTO and contains no secrets.
/// </summary>
public sealed record FrozenToolManifestEntry(
    string Id,
    string Description,
    JsonElement InputSchema,
    string Risk,
    bool MutatesWorkspace,
    bool RequiresApproval,
    string RetrySafety,
    IReadOnlyList<string> ConfirmationFields);

public sealed record ToolManifestSnapshot(
    int ProtocolVersion,
    string ManifestHash,
    IReadOnlyList<FrozenToolManifestEntry> AuthorizedTools);

/// <summary>Safe, machine-readable failure from trusted manifest admission.</summary>
public sealed class ToolManifestSnapshotException : InvalidOperationException
{
    public ToolManifestSnapshotException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}

/// <summary>
/// Canonicalizes the v2 hash exactly as the TinadecTools process does.  Keeping
/// this algorithm in the Core port assembly lets admission and dispatch verify a
/// live process without trusting a mutable tool id-only catalog.
/// </summary>
public static class ToolManifestHasher
{
    public static string Compute(IReadOnlyList<ToolManifestEntryDto> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var builder = new StringBuilder();
        foreach (var tool in tools.OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase))
        {
            Append(builder, tool.Id, tool.Description, tool.RequiresApproval, tool.InputSchema,
                tool.Risk, tool.MutatesWorkspace, tool.RetrySafety, tool.ConfirmationFields);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    public static string Compute(IReadOnlyList<FrozenToolManifestEntry> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var builder = new StringBuilder();
        foreach (var tool in tools.OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase))
        {
            Append(builder, tool.Id, tool.Description, tool.RequiresApproval, tool.InputSchema,
                tool.Risk, tool.MutatesWorkspace, tool.RetrySafety, tool.ConfirmationFields);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    public static bool Equivalent(ToolManifestEntryDto live, FrozenToolManifestEntry frozen) =>
        string.Equals(live.Id, frozen.Id, StringComparison.OrdinalIgnoreCase)
        && string.Equals(live.Description, frozen.Description, StringComparison.Ordinal)
        && live.RequiresApproval == frozen.RequiresApproval
        && live.MutatesWorkspace == frozen.MutatesWorkspace
        && string.Equals(live.Risk, frozen.Risk, StringComparison.Ordinal)
        && string.Equals(live.RetrySafety, frozen.RetrySafety, StringComparison.Ordinal)
        && string.Equals(GetRawSchema(live.InputSchema), GetRawSchema(frozen.InputSchema), StringComparison.Ordinal)
        && live.ConfirmationFields.SequenceEqual(frozen.ConfirmationFields, StringComparer.Ordinal);

    public static string GetRawSchema(JsonElement schema) =>
        schema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? string.Empty : schema.GetRawText();

    private static void Append(
        StringBuilder builder,
        string id,
        string description,
        bool requiresApproval,
        JsonElement inputSchema,
        string risk,
        bool mutatesWorkspace,
        string retrySafety,
        IReadOnlyList<string> confirmationFields)
    {
        builder.Append(id).Append('\0')
            .Append(description).Append('\0')
            .Append(requiresApproval).Append('\0')
            .Append(GetRawSchema(inputSchema)).Append('\0')
            .Append(risk).Append('\0')
            .Append(mutatesWorkspace).Append('\0')
            .Append(retrySafety).Append('\0')
            .AppendJoin('\u001f', confirmationFields).Append('\n');
    }
}
