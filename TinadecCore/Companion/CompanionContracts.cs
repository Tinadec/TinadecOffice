using System.Text.Json;

namespace TinadecCore.Companion;

/// <summary>
/// HTTP request bodies and JSON projections for the companion surface.
/// </summary>
public sealed record CompanionAutonomyRequest(
    bool? Enabled = null,
    bool? Paused = null,
    int? IntervalMinutes = null,
    int? DailyActionBudget = null);

public sealed record CompanionMandateDecisionRequest(string? Decision = null);

public sealed record CompanionEmoteRequest(string? Emote = null);

public sealed record CompanionCreateRequest(
    string? Name = null,
    string? Tagline = null,
    string? Avatar = null,
    string? PersonaDocument = null,
    string? ModelPurpose = null,
    string? Model = null,
    string? Role = null,
    CompanionAutonomyRequest? Autonomy = null);

public sealed record CompanionPatchRequest(
    string? Name = null,
    string? Tagline = null,
    string? Avatar = null,
    string? PersonaDocument = null,
    string? ModelPurpose = null,
    string? Model = null,
    string? Role = null,
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
    string? Authorization = null,
    string? Driver = null,
    string? ProfileId = null);

public sealed record CompanionConnectionPatchRequest(
    string? Authorization = null,
    bool? Enabled = null,
    string? Endpoint = null,
    string? Driver = null,
    string? ProfileId = null);

public sealed record CompanionConversationCreateRequest(
    string? Kind = null,
    string? Title = null,
    IReadOnlyList<Guid>? CompanionIds = null);

public sealed record CompanionInviteRequest(Guid? CompanionId = null, bool? ShareHistory = null);

public sealed record CompanionMessagePostRequest(
    string? Body = null,
    IReadOnlyList<Guid>? AddressedTo = null,
    string? ChannelKind = null,
    string? ThreadKey = null,
    string? IdempotencyKey = null);

public sealed record CompanionPauseRequest(bool? Paused = null);

public sealed record CompanionAvatarImportRequest(string? Path = null);

public sealed record CompanionSdkImportRequest(
    string? Path = null,
    string? FileName = null,
    string? ContentBase64 = null,
    string? WasmBase64 = null);

public sealed record CompanionScheduleCreateRequest(
    string? Title = null,
    string? Prompt = null,
    string? CronExpression = null,
    DateTimeOffset? RunAt = null,
    bool? OneShot = null,
    Guid? TargetConversationId = null,
    bool? Enabled = null);

public sealed record CompanionSchedulePatchRequest(
    string? Title = null,
    string? Prompt = null,
    string? CronExpression = null,
    DateTimeOffset? RunAt = null,
    bool? OneShot = null,
    Guid? TargetConversationId = null,
    bool? Enabled = null,
    string? Status = null);

public sealed record CompanionPlanStepDto(
    string? Id = null,
    string? Title = null,
    string? Status = null,
    string? Note = null);

public sealed record CompanionPlanCreateRequest(
    string? Title = null,
    string? Summary = null,
    string? Status = null,
    IReadOnlyList<CompanionPlanStepDto>? Steps = null);

public sealed record CompanionPlanPatchRequest(
    string? Title = null,
    string? Summary = null,
    string? Status = null,
    IReadOnlyList<CompanionPlanStepDto>? Steps = null);

public sealed record CompanionTodoCreateRequest(
    string? Title = null,
    string? Note = null,
    string? Priority = null,
    string? Status = null,
    DateTimeOffset? DueAt = null,
    Guid? PlanId = null);

public sealed record CompanionTodoPatchRequest(
    string? Title = null,
    string? Note = null,
    string? Priority = null,
    string? Status = null,
    DateTimeOffset? DueAt = null,
    Guid? PlanId = null);

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
    private static readonly JsonSerializerOptions SnakeCase = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    internal static object ToJson(CompanionAgentRecord row, CompanionCounts counts) => new
    {
        id = row.Id,
        name = row.Name,
        tagline = row.Tagline,
        avatar = row.AvatarKey,
        status = row.Status,
        role = NormalizeRole(row.Role),
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
        role = NormalizeRole(row.Role),
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

    private static string CurrentActivity(CompanionAgentRecord row, CompanionCounts counts) =>
        counts.HasRunningRun ? "thinking"
        : row.Status == "paused" || row.AutonomyPaused || counts.AutonomyPausedGlobally ? "paused"
        : "idle";

    /// <summary>
    /// Normalizes stored role strings onto the two supported Companion roles:
    /// <c>autonomous</c> (Companion Bot) and <c>session_partner</c>. Legacy <c>buddy</c> or empty
    /// values read cleanly as <c>autonomous</c>.
    /// </summary>
    internal static string NormalizeRole(string? role) =>
        string.Equals(role, "session_partner", StringComparison.OrdinalIgnoreCase)
            ? "session_partner"
            : "autonomous";

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

    internal static object ToMemoryJson(
        TinadecCore.Contracts.Dtos.MemoryItemLedgerDto item,
        Guid companionId,
        string? sourceKindOverride = null,
        Guid? sourceConversationId = null,
        Guid? sourceMessageId = null) => new
    {
        id = item.Id,
        companion_id = companionId,
        content = item.Content,
        kind = item.Kind,
        section = item.Section,
        in_hot_markdown = item.InHotMarkdown,
        source_kind = sourceKindOverride ?? InferMemorySourceKind(item),
        source_conversation_id = sourceConversationId ?? item.SourceSessionId,
        source_message_id = sourceMessageId,
        index_status = item.Status == "active" ? "indexed" : "deleted",
        revision = (long)Math.Max(1, item.CurrentVersion),
        created_at = item.CreatedAt,
        updated_at = item.UpdatedAt
    };

    private static string InferMemorySourceKind(TinadecCore.Contracts.Dtos.MemoryItemLedgerDto item)
    {
        var latestReason = item.Versions.Count > 0 ? item.Versions[^1].Reason : null;
        if (latestReason is not null)
        {
            if (latestReason.Contains("group_message", StringComparison.OrdinalIgnoreCase)) return "group_message";
            if (latestReason.Contains("companion_run", StringComparison.OrdinalIgnoreCase)
                || latestReason.Contains("distill", StringComparison.OrdinalIgnoreCase)
                || latestReason.Contains("tool", StringComparison.OrdinalIgnoreCase))
                return "companion_run";
        }
        return item.SourceRunId is { } rid && rid != Guid.Empty ? "companion_run" : "user";
    }

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

    internal static object ToJson(CompanionConnectionBindingRecord row)
    {
        var (driver, profileId) = ParseBrowserBinding(row);
        return new
        {
            id = row.Id,
            companion_id = row.CompanionId,
            kind = row.Kind,
            name = row.Name,
            endpoint = row.Endpoint,
            driver,
            profile_id = profileId,
            authorization = row.Authorization,
            runtime_status = EffectiveRuntimeStatus(row),
            enabled = row.Enabled,
            last_checked_at = row.LastCheckedAt,
            revision = row.Revision
        };
    }

    internal static Dictionary<string, object?> ToJson(CompanionSettingsRecord? row) => new()
    {
        ["main_companion_id"] = row?.MainCompanionId,
        ["recruit_limit"] = row?.RecruitLimit ?? 3,
        ["max_bot_hop_depth"] = Math.Clamp(row?.MaxBotHopDepth ?? 3, 0, 10),
        ["autonomy_paused"] = row?.AutonomyPaused ?? false,
        ["revision"] = row?.Revision ?? 0
    };

    internal static object ConversationJson(
        CompanionConversationRecord row, int messageCount, string? lastMessagePreview, IReadOnlyList<object> members,
        string origin, bool canOpen) => new
    {
        id = row.Id,
        kind = row.Kind,
        origin,
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
        channel_kind = row.ChannelKind,
        thread_key = row.ThreadKey,
        depth = row.Depth,
        hop_depth = row.Depth,
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
        seq = row.Seq,
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

    internal static object RecruitmentJson(CompanionRecruitmentRecord row) => new
    {
        id = row.Id,
        proposer_id = row.ProposerId,
        name = row.Name,
        persona_document = row.PersonaDocument,
        rationale = row.Rationale,
        status = row.Status,
        created_companion_id = row.CreatedCompanionId,
        revision = row.Revision,
        created_at = row.CreatedAt,
        updated_at = row.UpdatedAt,
        decided_at = row.DecidedAt
    };

    internal static object MandateJson(CompanionMandateRecord row) => new
    {
        id = row.Id,
        companion_id = row.CompanionId,
        action = row.Action,
        status = row.Status,
        summary = row.Summary,
        proposal = ParseJson(row.ProposalJson),
        revision = row.Revision,
        created_at = row.CreatedAt,
        updated_at = row.UpdatedAt,
        decided_at = row.DecidedAt
    };

    internal static object ConnectionOverviewJson(CompanionConnectionBindingRecord row, string? companionName)
    {
        var (driver, profileId) = ParseBrowserBinding(row);
        return new
        {
            id = row.Id,
            companion_id = row.CompanionId,
            companion_name = companionName,
            kind = row.Kind,
            name = row.Name,
            endpoint = row.Endpoint,
            driver,
            profile_id = profileId,
            authorization = row.Authorization,
            runtime_status = EffectiveRuntimeStatus(row),
            enabled = row.Enabled,
            last_checked_at = row.LastCheckedAt,
            revision = row.Revision
        };
    }

    internal static string EffectiveRuntimeStatus(CompanionConnectionBindingRecord row)
    {
        if (string.Equals(row.Kind, "work_session", StringComparison.OrdinalIgnoreCase))
        {
            if (!row.Enabled || string.Equals(row.Authorization, "revoked", StringComparison.OrdinalIgnoreCase))
                return "disconnected";
            return string.Equals(row.Authorization, "authorized", StringComparison.OrdinalIgnoreCase)
                ? "connected"
                : "configured";
        }

        if (string.Equals(row.Kind, "browser", StringComparison.OrdinalIgnoreCase))
        {
            if (!row.Enabled || string.Equals(row.Authorization, "revoked", StringComparison.OrdinalIgnoreCase))
                return "disconnected";
            if (!string.Equals(row.Authorization, "authorized", StringComparison.OrdinalIgnoreCase))
                return "configured";
            return row.RuntimeStatus is "connected" or "active" or "unavailable"
                ? row.RuntimeStatus
                : "connected";
        }

        return row.RuntimeStatus;
    }

    internal static (string? Driver, string? ProfileId) ParseBrowserBinding(CompanionConnectionBindingRecord row)
    {
        if (!string.Equals(row.Kind, "browser", StringComparison.OrdinalIgnoreCase))
            return (null, null);

        var raw = row.Endpoint?.Trim();
        string driver = "builtin_electron";
        string? profileId = string.IsNullOrWhiteSpace(row.CredentialRef) ? null : row.CredentialRef.Trim();

        if (!string.IsNullOrEmpty(raw))
        {
            var colon = raw.IndexOf(':');
            if (colon > 0)
            {
                var head = raw[..colon].Trim().ToLowerInvariant();
                var tail = raw[(colon + 1)..].Trim();
                if (head is "builtin_electron" or "ego_lite")
                {
                    driver = head;
                    if (!string.IsNullOrEmpty(tail)) profileId ??= tail;
                }
            }
            else if (raw.Equals("ego_lite", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("builtin_electron", StringComparison.OrdinalIgnoreCase))
            {
                driver = raw.ToLowerInvariant();
            }
        }

        return (driver, profileId);
    }

    internal static object ScheduleJson(CompanionScheduleRecord row) => new
    {
        id = row.Id,
        companion_id = row.CompanionId,
        title = row.Title,
        prompt = row.Prompt,
        cron_expression = row.CronExpression,
        run_at = row.RunAt,
        next_run_at = row.NextRunAt,
        last_run_at = row.LastRunAt,
        target_conversation_id = row.TargetConversationId,
        one_shot = row.OneShot,
        enabled = row.Enabled,
        status = row.Status,
        revision = row.Revision,
        created_at = row.CreatedAt,
        updated_at = row.UpdatedAt
    };

    internal static object PlanJson(CompanionPlanRecord row) => new
    {
        id = row.Id,
        companion_id = row.CompanionId,
        title = row.Title,
        summary = row.Summary,
        status = row.Status,
        steps = ParsePlanSteps(row.StepsJson),
        revision = row.Revision,
        created_at = row.CreatedAt,
        updated_at = row.UpdatedAt
    };

    internal static object TodoJson(CompanionTodoRecord row) => new
    {
        id = row.Id,
        companion_id = row.CompanionId,
        title = row.Title,
        note = row.Note,
        priority = row.Priority,
        status = row.Status,
        due_at = row.DueAt,
        plan_id = row.PlanId,
        revision = row.Revision,
        created_at = row.CreatedAt,
        updated_at = row.UpdatedAt
    };

    internal static IReadOnlyList<object> ParsePlanSteps(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var items = JsonSerializer.Deserialize<CompanionPlanStepDto[]>(json, SnakeCase) ?? [];
            return items
                .Select((step, idx) => (object)new
                {
                    id = string.IsNullOrWhiteSpace(step.Id) ? $"step-{idx + 1}" : step.Id.Trim(),
                    title = step.Title ?? string.Empty,
                    status = string.IsNullOrWhiteSpace(step.Status) ? "pending" : step.Status.Trim().ToLowerInvariant(),
                    note = step.Note
                })
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

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
