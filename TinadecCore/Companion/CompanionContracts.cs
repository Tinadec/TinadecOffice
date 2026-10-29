using System.Text.Json;

namespace TinadecCore.Companion;

/// <summary>
/// HTTP request bodies for the companion surface. Minimal API binds these with the host's
/// snake_case JSON policy (model_purpose, persona_document, ...); every field is optional on
/// PATCH so "absent" and "set to null" stay distinguishable where the contract needs it.
/// </summary>
public sealed record CompanionAutonomyRequest(bool? Enabled = null, bool? Paused = null, int? IntervalMinutes = null, int? DailyActionBudget = null);

public sealed record CompanionCreateRequest(
    string? Name = null,
    string? Tagline = null,
    string? Avatar = null,
    string? PersonaDocument = null,
    string? ModelPurpose = null,
    string? Model = null,
    CompanionAutonomyRequest? Autonomy = null);

public sealed record CompanionPatchRequest(
    string? Name = null,
    string? Tagline = null,
    string? Avatar = null,
    string? PersonaDocument = null,
    string? ModelPurpose = null,
    // Absent leaves the model untouched; an empty string clears it back to the route purpose;
    // otherwise "<providerInstanceId>|<model>" pins one model, exactly what the detail returns.
    // The separator is a pipe because model names themselves contain slashes.
    string? Model = null,
    CompanionAutonomyRequest? Autonomy = null);

public sealed record CompanionMemoryCreateRequest(
    string? Content = null,
    string? SourceKind = null,
    Guid? SourceConversationId = null,
    Guid? SourceMessageId = null);

public sealed record CompanionMemoryPatchRequest(string? Content = null);

public sealed record CompanionSkillCreateRequest(
    string? Name = null,
    string? SourceKind = null,
    string? PackId = null,
    string? PackVersion = null,
    string? Instructions = null,
    bool? Enabled = null);

public sealed record CompanionSkillPatchRequest(bool? Enabled = null, string? Instructions = null);

public sealed record CompanionConnectionCreateRequest(
    string? Kind = null,
    string? Name = null,
    string? Endpoint = null,
    string? CredentialRef = null,
    string? Authorization = null);

public sealed record CompanionConnectionPatchRequest(string? Authorization = null, bool? Enabled = null);

public sealed record CompanionConversationCreateRequest(string? Kind = null, string? Title = null, IReadOnlyList<Guid>? CompanionIds = null);

public sealed record CompanionInviteRequest(Guid? CompanionId = null, bool? ShareHistory = null);

public sealed record CompanionMessagePostRequest(string? Body = null, IReadOnlyList<Guid>? AddressedTo = null, string? IdempotencyKey = null);

public sealed record CompanionPauseRequest(bool? Paused = null);

/// <summary>
/// A companion-domain refusal. Endpoints translate it verbatim: the code is part of the
/// contract (companion_not_found / companion_revision_conflict / ...), never a generic
/// conflict, and the status is the one the caller can branch on.
/// </summary>
public sealed class CompanionException(string code, string message, int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

internal sealed record CompanionCounts(
    int MemoryCount,
    int SkillCount,
    int ConnectionCount,
    DateTimeOffset? LastActivityAt,
    bool HasRunningRun = false,
    bool AutonomyPausedGlobally = false);

internal static class CompanionContracts
{
    internal static object ToJson(CompanionAgentRecord row, CompanionCounts counts) => new
    {
        id = row.Id,
        name = row.Name,
        tagline = row.Tagline,
        avatar = row.AvatarKey,
        status = row.Status,
        model_purpose = row.ModelPurpose,
        model_provider_id = row.ModelProviderId,
        model = row.ModelName,
        autonomy_enabled = row.AutonomyEnabled,
        autonomy_paused = row.AutonomyPaused,
        revision = row.Revision,
        created_at = row.CreatedAt,
        updated_at = row.UpdatedAt,
        memory_count = counts.MemoryCount,
        skill_count = counts.SkillCount,
        connection_count = counts.ConnectionCount,
        last_activity_at = counts.LastActivityAt,
        current_activity = CurrentActivity(row, counts)
    };

    internal static object ToDetailJson(CompanionAgentRecord row, CompanionCounts counts) => new
    {
        id = row.Id,
        name = row.Name,
        tagline = row.Tagline,
        avatar = row.AvatarKey,
        status = row.Status,
        model_purpose = row.ModelPurpose,
        model_provider_id = row.ModelProviderId,
        model = row.ModelName,
        autonomy_enabled = row.AutonomyEnabled,
        autonomy_paused = row.AutonomyPaused,
        revision = row.Revision,
        created_at = row.CreatedAt,
        updated_at = row.UpdatedAt,
        memory_count = counts.MemoryCount,
        skill_count = counts.SkillCount,
        connection_count = counts.ConnectionCount,
        last_activity_at = counts.LastActivityAt,
        current_activity = CurrentActivity(row, counts),
        persona_document = row.PersonaDocument,
        autonomy = new
        {
            enabled = row.AutonomyEnabled,
            paused = row.AutonomyPaused,
            interval_minutes = row.AutonomyIntervalMinutes,
            daily_action_budget = row.AutonomyDailyBudget
        }
    };

    // Only facts this piece owns: a live run is thinking; a paused identity or a paused tenant is
    // paused; otherwise there is nothing happening. The vocabulary is fixed by the contract.
    private static string CurrentActivity(CompanionAgentRecord row, CompanionCounts counts) =>
        counts.HasRunningRun ? "thinking"
        : row.Status == "paused" || row.AutonomyPaused || counts.AutonomyPausedGlobally ? "paused"
        : "idle";

    internal static object ToJson(CompanionMemoryRecord row) => new
    {
        id = row.Id,
        companion_id = row.CompanionId,
        content = row.Content,
        source_kind = row.SourceKind,
        source_conversation_id = row.SourceConversationId,
        source_message_id = row.SourceMessageId,
        index_status = row.IndexStatus,
        revision = row.Revision,
        created_at = row.CreatedAt,
        updated_at = row.UpdatedAt
    };

    internal static object ToJson(CompanionSkillBindingRecord row) => new
    {
        id = row.Id,
        companion_id = row.CompanionId,
        name = row.Name,
        source_kind = row.SourceKind,
        pack_id = row.PackId,
        pack_version = row.PackVersion,
        instructions = row.Instructions,
        enabled = row.Enabled,
        last_loaded_run_id = row.LastLoadedRunId,
        last_loaded_at = row.LastLoadedAt,
        revision = row.Revision
    };

    internal static object ToJson(CompanionConnectionBindingRecord row) => new
    {
        id = row.Id,
        companion_id = row.CompanionId,
        kind = row.Kind,
        name = row.Name,
        endpoint = row.Endpoint,
        authorization = row.Authorization,
        runtime_status = row.RuntimeStatus,
        enabled = row.Enabled,
        last_checked_at = row.LastCheckedAt,
        revision = row.Revision
    };

    // The host JSON policy omits null properties. main_companion_id is explicitly nullable in
    // the contract ("no row" answers null, revision 0), so it travels as a dictionary entry.
    internal static Dictionary<string, object?> ToJson(CompanionSettingsRecord? row) => new()
    {
        ["main_companion_id"] = row?.MainCompanionId,
        ["autonomy_paused"] = row?.AutonomyPaused ?? false,
        ["revision"] = row?.Revision ?? 0
    };

    // ── conversations / messages / runs / activity (Task 2) ──

    internal static object ConversationJson(
        CompanionConversationRecord row, int messageCount, string? lastMessagePreview, IReadOnlyList<object> members,
        string origin, bool canOpen) => new
    {
        id = row.Id,
        kind = row.Kind,
        // user | companion: who opened this conversation. The member list cannot answer this —
        // every group carries a user member regardless of who created it.
        origin,
        // Whether this user may read the transcript. Always true today (the user is a member of
        // every conversation this endpoint returns); it travels so the UI never has to assume.
        can_open = canOpen,
        title = row.Title,
        parent_conversation_id = row.ParentConversationId,
        autonomy_paused = row.AutonomyPaused,
        turn_budget = row.TurnBudget,
        created_at = row.CreatedAt,
        last_message_at = row.LastMessageAt,
        last_message_preview = lastMessagePreview,
        message_count = messageCount,
        members
    };

    internal static object MemberJson(CompanionConversationMemberRecord row, string displayName) => new
    {
        member_kind = row.MemberKind,
        companion_id = row.CompanionId,
        display_name = displayName,
        joined_at = row.JoinedAt,
        status = row.Status
    };

    internal static object MessageJson(CompanionMessageRecord row, string authorName, IReadOnlyList<Guid> addressedTo) => new
    {
        id = row.Id,
        conversation_id = row.ConversationId,
        seq = row.Seq,
        author_kind = row.AuthorKind,
        author_companion_id = row.AuthorCompanionId,
        author_name = authorName,
        body = row.Body,
        addressed_to = addressedTo,
        created_at = row.CreatedAt
    };

    internal static object RunJson(CompanionRunRecord row) => new
    {
        id = row.Id,
        companion_id = row.CompanionId,
        conversation_id = row.ConversationId,
        trigger_message_id = row.TriggerMessageId,
        status = row.Status,
        depth = row.Depth,
        config_revision = row.ConfigRevision,
        loaded_memory_ids = ParseGuids(row.LoadedMemoryIdsJson),
        loaded_skill_binding_ids = ParseGuids(row.LoadedSkillBindingIdsJson),
        rounds = row.Rounds,
        error_code = row.ErrorCode,
        error_message = row.ErrorMessage,
        reply_message_id = row.ReplyMessageId,
        created_at = row.CreatedAt,
        started_at = row.StartedAt,
        finished_at = row.FinishedAt
    };

    internal static object ActivityJson(CompanionActivityRecord row, string? companionName, string? conversationTitle) => new
    {
        id = row.Id,
        kind = row.Kind,
        companion_id = row.CompanionId,
        companion_name = companionName,
        conversation_id = row.ConversationId,
        conversation_title = conversationTitle,
        summary = row.Summary,
        detail = row.Detail,
        payload = ParseJson(row.PayloadJson),
        created_at = row.CreatedAt
    };

    internal static object ConnectionOverviewJson(CompanionConnectionBindingRecord row, string? companionName) => new
    {
        id = row.Id,
        companion_id = row.CompanionId,
        companion_name = companionName,
        kind = row.Kind,
        name = row.Name,
        endpoint = row.Endpoint,
        authorization = row.Authorization,
        runtime_status = row.RuntimeStatus,
        enabled = row.Enabled,
        last_checked_at = row.LastCheckedAt,
        revision = row.Revision
    };

    private static IReadOnlyList<Guid> ParseGuids(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Guid[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
