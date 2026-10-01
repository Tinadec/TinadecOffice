using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Runtime;

/// <summary>
/// Serves <c>tina_chat_execute_intent</c> — the one chat tool that has to reach above the
/// communication module, because a handoff needs the mode catalog and the run coordinator. Every
/// other tool is forwarded to the module's own gateway unchanged, so identity, audience and
/// membership rules stay defined in exactly one place, and the module never takes a dependency on
/// <see cref="ITinaChatRunService"/> (which depends back on it) and close a construction cycle.
///
/// The acting identity is resolved by the inner gateway, not re-derived here: a handoff is made as
/// the participant this session bound, under the same active/ownership checks every other call runs.
/// </summary>
internal sealed class TinaChatHandoffGateway(
    ITinaChatToolGateway inner,
    ITinaChatRunService runs,
    ITenantContextAccessor tenant,
    IDbContextFactory<AgentConfigurationDbContext> configuration) : ITinaChatToolGateway
{
    public const string HandoffToolId = "tina_chat_execute_intent";

    public Task<Guid> RequireActorAsync(TinaChatToolCall call, CancellationToken ct = default) =>
        inner.RequireActorAsync(call, ct);

    public async Task<TinaChatToolOutcome> ExecuteAsync(TinaChatToolCall call, CancellationToken ct = default)
    {
        if (!string.Equals(call.ToolId, HandoffToolId, StringComparison.OrdinalIgnoreCase))
            return await inner.ExecuteAsync(call, ct);
        try
        {
            var actor = await inner.RequireActorAsync(call, ct);
            var conversationId = Identifier(call.Arguments, "conversation_id");
            var intentId = Identifier(call.Arguments, "intent_id");
            var modeVersionId = await ResolveModeAsync(call, OptionalIdentifier(call.Arguments, "mode_version_id"), ct);
            var execution = await runs.ExecuteAsync(conversationId, intentId,
                new TinaChatExecuteIntentRequest(actor, modeVersionId, OptionalIdentifier(call.Arguments, "project_id")), ct);
            return new TinaChatToolOutcome(true, JsonSerializer.Serialize(new
            {
                status = execution.Status,
                execution_id = execution.Id.ToString("N"),
                session_id = execution.SessionId.ToString("N"),
                run_id = execution.RunId?.ToString("N"),
                mode_version_id = modeVersionId.ToString("N"),
                note = "Detached. The run reads only this accepted brief; its outcome is posted back to the conversation when it terminates. Do not report results you have not read from the inbox.",
            }));
        }
        catch (TinaChatException ex)
        {
            return TinaChatToolOutcome.Failed(ex.Message);
        }
    }

    /// <summary>
    /// An omitted mode means "the workspace's published default", which is the same authority a new
    /// ordinary session gets. Refusing rather than guessing keeps an agent from being run under a mode
    /// nobody chose.
    /// </summary>
    private async Task<Guid> ResolveModeAsync(TinaChatToolCall call, Guid? supplied, CancellationToken ct)
    {
        if (supplied is { } chosen && chosen != Guid.Empty) return chosen;
        var current = tenant.Current;
        if (current.TenantId != call.TenantId || current.WorkspaceId != call.WorkspaceId)
            throw new TinaChatException(403, "tina_chat_scope_mismatch",
                "This run's workspace is not the one the handoff path is authorised for, so no mode can be resolved for it.");
        await using var db = await configuration.CreateDbContextAsync(ct);
        var defaults = await db.WorkspaceDefaults.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == call.TenantId && x.WorkspaceId == call.WorkspaceId && x.Status == "active", ct);
        return defaults?.DefaultModeVersionId ?? throw new TinaChatException(409, "agent_mode_not_configured",
            "This workspace has no published default mode, so tina_chat_execute_intent needs an explicit mode_version_id.");
    }

    private static Guid? OptionalIdentifier(JsonElement? args, string field)
    {
        if (args is not { ValueKind: JsonValueKind.Object } element || !element.TryGetProperty(field, out var value)) return null;
        var raw = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return Parse(raw);
    }

    private static Guid Identifier(JsonElement? args, string field)
    {
        if (args is not { ValueKind: JsonValueKind.Object } element || !element.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
            throw new TinaChatException(400, "invalid_request", $"{field} is required. Read it from tina_chat_list_rooms and tina_chat_list_intents.");
        return Parse(value.GetString() ?? "");
    }

    /// <summary>Accepts both spellings an earlier tool result may have handed the model.</summary>
    private static Guid Parse(string raw)
    {
        var value = raw.Trim();
        if (value.Length == 32 && value.All(Uri.IsHexDigit))
            value = value.Insert(20, "-").Insert(16, "-").Insert(12, "-").Insert(8, "-");
        if (Guid.TryParse(value, out var id) && id != Guid.Empty) return id;
        throw new TinaChatException(400, "invalid_request", $"'{raw}' is not an identifier from an earlier tool result.");
    }
}
