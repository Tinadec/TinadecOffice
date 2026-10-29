using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Companion;

/// <summary>
/// One bounded autonomy tick: read the companion's switches and today's usage, ask the model for
/// exactly one decision, then validate every id and membership on the server before writing anything.
/// The model never owns an id, a membership or a pause switch; a decision it cannot justify is
/// recorded as <c>autonomy_skipped</c> rather than executed. A disabled, paused or model-less tick
/// writes nothing at all, so a closed switch can never leave a trace; an exhausted budget is the one
/// exception, because the spec requires the reason in the activity feed.
///
/// A manual tick ignores the interval (see <see cref="TickAsync"/>); the background worker asks
/// <see cref="DueCompanionIdsAsync"/> first, which applies the interval and the daily budget.
/// </summary>
public sealed class CompanionAutonomyService(
    IDbContextFactory<CompanionDbContext> dbFactory,
    ITenantContextAccessor tenant,
    CompanionConversationService conversations,
    IAgentChatClientFactory chatClients,
    ILogger<CompanionAutonomyService> logger)
{
    private const string DefaultPersona = "你是一个乐于助人的伙伴。";
    private const int MaxBodyLength = 4000;
    private const int MaxPeersInPrompt = 50;
    private const int MaxGroupsInPrompt = 20;
    private const int MaxActivitySummary = 512;

    private static readonly JsonSerializerOptions SnakeCase = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private Guid TenantId => tenant.Current.TenantId;

    // ── tick ──

    /// <summary>
    /// Manual tick: the interval is deliberately bypassed, while pause, budget and model availability
    /// still apply. A missing or foreign companion is a 404; every other refusal is an answer.
    /// </summary>
    public async Task<object> TickAsync(Guid companionId, CancellationToken cancellationToken = default)
    {
        var state = await LoadStateAsync(companionId, cancellationToken).ConfigureAwait(false);
        var companion = state.Companion;
        if (state.Rejection is { } rejection)
        {
            // An exhausted budget is a refusal the activity feed must show; the other pre-refusals
            // stay silent so a disabled or paused switch leaves no rows behind.
            return rejection == "daily_budget_exhausted"
                ? await BudgetExhaustedAsync(companion, cancellationToken).ConfigureAwait(false)
                : Refused(rejection);
        }

        ChatResolution resolution;
        string? reply;
        try
        {
            resolution = companion.ModelProviderId is { } providerId
                ? await chatClients.ResolveModelChatAsync(providerId, companion.ModelName, cancellationToken).ConfigureAwait(false)
                : await chatClients.ResolveChatAsync(companion.ModelPurpose, cancellationToken).ConfigureAwait(false);
            if (!resolution.IsAvailable) return Refused("model_unavailable");

            var facts = await BuildFactsAsync(companion, state.UsedToday, cancellationToken).ConfigureAwait(false);
            using var client = await chatClients.CreateAsync(resolution, cancellationToken).ConfigureAwait(false);
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, facts)],
                new ChatOptions
                {
                    Instructions = BuildInstructions(companion),
                    ToolMode = ChatToolMode.None,
                    AllowMultipleToolCalls = false
                },
                cancellationToken).ConfigureAwait(false);
            reply = response.Text?.Trim();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // There was no usable decision, so there is nothing to validate and nothing to write.
            logger.LogWarning(exception, "Companion autonomy could not obtain a decision for companion {CompanionId}.", companion.Id);
            return Refused("model_unavailable");
        }

        var decision = ParseDecision(reply);
        if (decision is null)
            return await SkipAsync(companion, "none", "invalid_decision", null, cancellationToken).ConfigureAwait(false);

        return decision.Action switch
        {
            "none" => Refused(null),
            "create_group" => await CreateGroupAsync(companion, decision, cancellationToken).ConfigureAwait(false),
            "invite" => await InviteAsync(companion, decision, cancellationToken).ConfigureAwait(false),
            "send_message" => await SendMessageAsync(companion, decision, cancellationToken).ConfigureAwait(false),
            _ => await SkipAsync(companion, "none", "invalid_decision", null, cancellationToken).ConfigureAwait(false)
        };
    }

    /// <summary>
    /// The worker's gate: active, enabled, neither paused, interval elapsed, budget remaining.
    /// Returns only companion ids, so the worker decides nothing about companions itself.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> DueCompanionIdsAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var globallyPaused = await db.Settings.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .Select(x => (bool?)x.AutonomyPaused)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? false;
        if (globallyPaused) return [];

        var companions = await db.Companions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Status == "active" && x.AutonomyEnabled && !x.AutonomyPaused)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (companions.Count == 0) return [];

        // Skipped rows are not actions: they neither spend quota nor push the interval forward.
        var actions = await db.Activities.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.CompanionId != null
                && x.Kind.StartsWith("autonomy_") && x.Kind != "autonomy_skipped")
            .Select(x => new { CompanionId = x.CompanionId!.Value, x.CreatedAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var byCompanion = actions.GroupBy(x => x.CompanionId).ToDictionary(group => group.Key, group => group.ToList());
        var today = UtcToday();
        var now = DateTimeOffset.UtcNow;

        var due = new List<Guid>();
        foreach (var companion in companions)
        {
            var used = byCompanion.TryGetValue(companion.Id, out var rows) ? rows.Count(x => x.CreatedAt >= today) : 0;
            if (companion.AutonomyDailyBudget <= 0 || used >= companion.AutonomyDailyBudget) continue;

            // The later of the last real autonomous action and any configuration change starts the
            // interval, so editing the companion's autonomy rules cannot be undone by an old action.
            var lastAction = rows is { Count: > 0 } ? rows.Max(x => x.CreatedAt) : (DateTimeOffset?)null;
            var basis = lastAction is { } value && value > companion.UpdatedAt ? value : companion.UpdatedAt;
            var interval = TimeSpan.FromMinutes(Math.Max(companion.AutonomyIntervalMinutes, 1));
            if (now < basis.Add(interval)) continue;
            due.Add(companion.Id);
        }
        return due;
    }

    // ── preconditions ──

    private sealed record TickState(CompanionAgentRecord Companion, string? Rejection, int UsedToday);

    private async Task<TickState> LoadStateAsync(Guid companionId, CancellationToken cancellationToken)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var companion = await db.Companions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == companionId && x.TenantId == tenantId, cancellationToken).ConfigureAwait(false)
            ?? throw new CompanionException("companion_not_found", "Companion not found.", 404);

        if (companion.Status != "active") return new TickState(companion, "companion_not_active", 0);
        if (!companion.AutonomyEnabled) return new TickState(companion, "autonomy_disabled", 0);

        var globallyPaused = await db.Settings.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .Select(x => (bool?)x.AutonomyPaused)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? false;
        if (companion.AutonomyPaused || globallyPaused) return new TickState(companion, "autonomy_paused", 0);

        var usedToday = (await db.Activities.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.CompanionId == companionId
                    && x.Kind.StartsWith("autonomy_") && x.Kind != "autonomy_skipped")
                .Select(x => x.CreatedAt)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Count(x => x >= UtcToday());
        if (companion.AutonomyDailyBudget <= 0 || usedToday >= companion.AutonomyDailyBudget)
            return new TickState(companion, "daily_budget_exhausted", usedToday);

        return new TickState(companion, null, usedToday);
    }

    // ── actions ──

    private async Task<object> CreateGroupAsync(CompanionAgentRecord companion, Decision decision, CancellationToken cancellationToken)
    {
        var tenantId = TenantId;
        var requested = decision.CompanionIds.Distinct().Where(id => id != companion.Id).ToList();
        if (requested.Count == 0)
            return await SkipAsync(companion, "create_group", "invalid_target", null, cancellationToken).ConfigureAwait(false);

        var title = string.IsNullOrWhiteSpace(decision.Title) ? null : decision.Title.Trim();
        if (title is { Length: > 256 })
            return await SkipAsync(companion, "create_group", "invalid_decision", null, cancellationToken).ConfigureAwait(false);

        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var active = await db.Companions.AsNoTracking()
                .Where(x => x.TenantId == tenantId && requested.Contains(x.Id) && x.Status == "active")
                .Select(x => x.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (active.Count != requested.Count)
                return await SkipAsync(companion, "create_group", "invalid_target", null, cancellationToken).ConfigureAwait(false);
        }

        var wanted = requested.Append(companion.Id).ToHashSet();
        var duplicate = await FindSameMemberGroupAsync(tenantId, companion.Id, wanted, cancellationToken).ConfigureAwait(false);
        if (duplicate is { } existing)
            return await SkipAsync(companion, "create_group", "duplicate_group", existing, cancellationToken).ConfigureAwait(false);

        var outcome = await conversations
            .CreateGroupCoreAsync(title, [companion.Id, .. requested], cancellationToken, origin: "companion").ConfigureAwait(false);
        var activity = await RecordAsync(
            companion, "autonomy_group_created", outcome.ConversationId, outcome.Title,
            $"伙伴 {companion.Name} 自主建立了群聊「{outcome.Title ?? "群聊"}」",
            new { autonomous = true, action = "create_group", conversation_id = outcome.ConversationId, companion_ids = requested, title = outcome.Title },
            cancellationToken).ConfigureAwait(false);
        return Acted("create_group", activity, outcome.Json);
    }

    private async Task<object> InviteAsync(CompanionAgentRecord companion, Decision decision, CancellationToken cancellationToken)
    {
        var tenantId = TenantId;
        var conversationId = decision.ConversationId!.Value;
        var targetId = decision.CompanionId!.Value;
        if (targetId == companion.Id)
            return await SkipAsync(companion, "invite", "invalid_target", conversationId, cancellationToken).ConfigureAwait(false);

        CompanionAgentRecord? target;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = await db.Conversations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == conversationId && x.TenantId == tenantId, cancellationToken).ConfigureAwait(false);
            // Autonomy only joins groups. A private chat is not a meeting place, and inviting into
            // one would silently create a second group.
            if (row is null || row.Kind != "group")
                return await SkipAsync(companion, "invite", "invalid_target", conversationId, cancellationToken).ConfigureAwait(false);

            var isMember = await db.ConversationMembers.AsNoTracking().AnyAsync(
                x => x.TenantId == tenantId && x.ConversationId == conversationId && x.MemberKind == "companion"
                    && x.CompanionId == companion.Id && x.Status == "active",
                cancellationToken).ConfigureAwait(false);
            if (!isMember)
                return await SkipAsync(companion, "invite", "invalid_target", conversationId, cancellationToken).ConfigureAwait(false);

            target = await db.Companions.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == targetId && x.TenantId == tenantId && x.Status == "active", cancellationToken).ConfigureAwait(false);
            if (target is null)
                return await SkipAsync(companion, "invite", "invalid_target", conversationId, cancellationToken).ConfigureAwait(false);

            var alreadyMember = await db.ConversationMembers.AsNoTracking().AnyAsync(
                x => x.TenantId == tenantId && x.ConversationId == conversationId && x.MemberKind == "companion"
                    && x.CompanionId == targetId && x.Status == "active",
                cancellationToken).ConfigureAwait(false);
            if (alreadyMember)
                return await SkipAsync(companion, "invite", "duplicate_member", conversationId, cancellationToken).ConfigureAwait(false);
        }

        // Autonomy never copies private history into a shared group.
        var outcome = await conversations.InviteCoreAsync(conversationId, targetId, false, cancellationToken).ConfigureAwait(false);
        var activity = await RecordAsync(
            companion, "autonomy_member_invited", outcome.ConversationId, outcome.Title,
            $"伙伴 {companion.Name} 自主邀请伙伴 {target.Name} 加入会话「{outcome.Title ?? "群聊"}」",
            new { autonomous = true, action = "invite", conversation_id = outcome.ConversationId, companion_id = targetId },
            cancellationToken).ConfigureAwait(false);
        return Acted("invite", activity, outcome.Json);
    }

    private async Task<object> SendMessageAsync(CompanionAgentRecord companion, Decision decision, CancellationToken cancellationToken)
    {
        var tenantId = TenantId;
        var conversationId = decision.ConversationId!.Value;
        var body = decision.Body?.Trim();
        if (string.IsNullOrEmpty(body) || body.Length > MaxBodyLength)
            return await SkipAsync(companion, "send_message", "invalid_decision", conversationId, cancellationToken).ConfigureAwait(false);

        // A repeated decision is the same message: the key is derived from the fact, not from a
        // model-supplied id, so a re-run of the same decision cannot post twice.
        var idempotencyKey = IdempotencyKey(companion.Id, conversationId, body);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var conversation = await db.Conversations
            .SingleOrDefaultAsync(x => x.Id == conversationId && x.TenantId == tenantId, cancellationToken).ConfigureAwait(false);
        if (conversation is null)
            return await SkipAsync(companion, "send_message", "invalid_target", conversationId, cancellationToken).ConfigureAwait(false);
        if (conversation.AutonomyPaused)
            return await SkipAsync(companion, "send_message", "conversation_paused", conversationId, cancellationToken).ConfigureAwait(false);

        var isMember = await db.ConversationMembers.AsNoTracking().AnyAsync(
            x => x.TenantId == tenantId && x.ConversationId == conversationId && x.MemberKind == "companion"
                && x.CompanionId == companion.Id && x.Status == "active",
            cancellationToken).ConfigureAwait(false);
        if (!isMember)
            return await SkipAsync(companion, "send_message", "invalid_target", conversationId, cancellationToken).ConfigureAwait(false);

        var replayed = await db.Messages.AsNoTracking().AnyAsync(
            x => x.TenantId == tenantId && x.ConversationId == conversationId && x.IdempotencyKey == idempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (replayed)
            return await SkipAsync(companion, "send_message", "duplicate_message", conversationId, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        // Autonomy writes a message only: no run is created, so a companion's autonomous turn
        // cannot wake another companion or cascade.
        var message = await CompanionMessages.AppendAsync(
            db, tenantId, conversationId, "companion", companion.Id, body, [], idempotencyKey, now, cancellationToken)
            .ConfigureAwait(false);
        conversation.LastMessageAt = now;
        var activityRow = new CompanionActivityRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Kind = "autonomy_message_sent",
            CompanionId = companion.Id,
            ConversationId = conversationId,
            Summary = Truncate($"伙伴 {companion.Name} 自主发言：{Preview(body, 40)}", MaxActivitySummary),
            Detail = Truncate(body, 500),
            PayloadJson = JsonSerializer.Serialize(new
            {
                autonomous = true,
                action = "send_message",
                conversation_id = conversationId,
                message_id = message.Id,
                preview = Preview(body, 80)
            }, SnakeCase),
            CreatedAt = now
        };
        db.Activities.Add(activityRow);
        try
        {
            await CompanionMessages.SaveAsync(db, message, tenantId, conversationId, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // The unique idempotency index lost a race with another tick; the message exists once.
            return await SkipAsync(companion, "send_message", "duplicate_message", conversationId, cancellationToken).ConfigureAwait(false);
        }

        return Acted(
            "send_message",
            CompanionContracts.ActivityJson(activityRow, companion.Name, conversation.Title),
            null,
            CompanionContracts.MessageJson(message, companion.Name, []));
    }

    // ── guards ──

    private async Task<Guid?> FindSameMemberGroupAsync(
        Guid tenantId, Guid companionId, HashSet<Guid> wanted, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var groupIds = await (
            from conversation in db.Conversations.AsNoTracking()
            join member in db.ConversationMembers.AsNoTracking() on conversation.Id equals member.ConversationId
            where conversation.TenantId == tenantId && conversation.Kind == "group"
                && member.TenantId == tenantId && member.MemberKind == "companion"
                && member.CompanionId == companionId && member.Status == "active"
            select conversation.Id).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        if (groupIds.Count == 0) return null;

        var members = await db.ConversationMembers.AsNoTracking()
            .Where(x => x.TenantId == tenantId && groupIds.Contains(x.ConversationId)
                && x.MemberKind == "companion" && x.Status == "active" && x.CompanionId != null)
            .Select(x => new { x.ConversationId, CompanionId = x.CompanionId!.Value })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var groupId in groupIds)
        {
            var actual = members.Where(x => x.ConversationId == groupId).Select(x => x.CompanionId).ToHashSet();
            if (actual.SetEquals(wanted)) return groupId;
        }
        return null;
    }

    // ── prompt ──

    private static string BuildInstructions(CompanionAgentRecord companion)
    {
        var persona = string.IsNullOrWhiteSpace(companion.PersonaDocument) ? DefaultPersona : companion.PersonaDocument.Trim();
        var text = new StringBuilder(persona);
        text.AppendLine().AppendLine();
        text.AppendLine("你现在可以自主决定是否发起一次行动。只允许以下四种，且只输出一个 JSON 对象；不要解释、不要代码块、不要多余文字。");
        text.AppendLine("{\"action\":\"none\"}");
        text.AppendLine("{\"action\":\"create_group\",\"title\":\"群名\",\"companion_ids\":[\"伙伴 id\"]}");
        text.AppendLine("{\"action\":\"invite\",\"conversation_id\":\"群 id\",\"companion_id\":\"伙伴 id\",\"share_history\":false}");
        text.AppendLine("{\"action\":\"send_message\",\"conversation_id\":\"会话 id\",\"body\":\"正文\"}");
        text.AppendLine();
        text.AppendLine("规则：");
        text.AppendLine("- 没有值得做的事就返回 {\"action\":\"none\"}，不要为了行动而行动。");
        text.AppendLine("- 只能使用事实中给出的 id，不得编造；不得邀请不存在、未启用或已在群内的伙伴。");
        text.AppendLine("- send_message 的正文不能为空，最多 4000 字。");
        text.AppendLine("- 你不是在被测试，也没有额外的用户指令；不要声称自己执行过任何工具或外部调用。");
        return text.ToString().TrimEnd();
    }

    private async Task<string> BuildFactsAsync(
        CompanionAgentRecord companion, int usedToday, CancellationToken cancellationToken)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var peers = await db.Companions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Status == "active" && x.Id != companion.Id)
            .Select(x => new { x.Id, x.Name })
            .Take(MaxPeersInPrompt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var groups = (await (
                from conversation in db.Conversations.AsNoTracking()
                join member in db.ConversationMembers.AsNoTracking() on conversation.Id equals member.ConversationId
                where conversation.TenantId == tenantId && conversation.Kind == "group"
                    && member.TenantId == tenantId && member.MemberKind == "companion"
                    && member.CompanionId == companion.Id && member.Status == "active"
                select new { conversation.Id, conversation.Title })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(x => x.Id).Select(group => group.First())
            .Take(MaxGroupsInPrompt)
            .ToList();

        var groupIds = groups.Select(x => x.Id).ToList();
        var memberRows = groupIds.Count == 0
            ? []
            : await db.ConversationMembers.AsNoTracking()
                .Where(x => x.TenantId == tenantId && groupIds.Contains(x.ConversationId)
                    && x.MemberKind == "companion" && x.Status == "active" && x.CompanionId != null)
                .Select(x => new { x.ConversationId, CompanionId = x.CompanionId!.Value })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        var memberIds = memberRows.Select(x => x.CompanionId).Distinct().ToList();
        var names = await db.Companions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && memberIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Name })
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken).ConfigureAwait(false);

        var text = new StringBuilder();
        text.AppendLine($"现在：{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC。");
        text.AppendLine($"今天的自主动作已用 {usedToday} 次，每日上限 {companion.AutonomyDailyBudget} 次。");
        text.AppendLine();
        text.AppendLine("其他伙伴：");
        if (peers.Count == 0) text.AppendLine("- （没有其他伙伴）");
        foreach (var peer in peers) text.AppendLine($"- {peer.Name} (id={peer.Id})");
        text.AppendLine();
        text.AppendLine("你参与的群：");
        if (groups.Count == 0) text.AppendLine("- （你还没有参与任何群）");
        foreach (var group in groups)
        {
            var members = memberRows.Where(x => x.ConversationId == group.Id)
                .Select(x => names.GetValueOrDefault(x.CompanionId, "伙伴"))
                .Distinct()
                .ToList();
            text.AppendLine($"- 「{group.Title ?? "群聊"}」 (id={group.Id})，成员：{string.Join("、", members)}");
        }
        return text.ToString().TrimEnd();
    }

    // ── decision parsing ──

    private sealed record Decision(
        string Action,
        string? Title,
        IReadOnlyList<Guid> CompanionIds,
        Guid? ConversationId,
        Guid? CompanionId,
        string? Body);

    /// <summary>
    /// Strict about the shape, tolerant of a model that wraps its object in prose or a code fence.
    /// Anything that cannot be turned into one of the four actions is null (invalid_decision).
    /// </summary>
    private static Decision? ParseDecision(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var first = reply.IndexOf('{');
        var last = reply.LastIndexOf('}');
        if (first < 0 || last <= first) return null;
        try
        {
            using var document = JsonDocument.Parse(reply[first..(last + 1)]);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("action", out var actionElement)
                || actionElement.ValueKind != JsonValueKind.String)
                return null;

            return actionElement.GetString()?.Trim().ToLowerInvariant() switch
            {
                "none" => new Decision("none", null, [], null, null, null),
                "create_group" when root.TryGetProperty("companion_ids", out var idsElement)
                    && idsElement.ValueKind == JsonValueKind.Array
                    && ReadIds(idsElement) is { Count: > 0 } ids => new Decision(
                        "create_group",
                        root.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String
                            ? titleElement.GetString()
                            : null,
                        ids, null, null, null),
                "invite" when TryId(root, "conversation_id", out var conversationId)
                    && TryId(root, "companion_id", out var companionId) => new Decision(
                        "invite", null, [], conversationId, companionId, null),
                "send_message" when TryId(root, "conversation_id", out var messageConversation)
                    && root.TryGetProperty("body", out var bodyElement)
                    && bodyElement.ValueKind == JsonValueKind.String => new Decision(
                        "send_message", null, [], messageConversation, null, bodyElement.GetString()),
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<Guid>? ReadIds(JsonElement array)
    {
        var ids = new List<Guid>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !item.TryGetGuid(out var id)) return null;
            ids.Add(id);
        }
        return ids;
    }

    private static bool TryId(JsonElement root, string name, out Guid value)
    {
        value = Guid.Empty;
        return root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            && element.TryGetGuid(out value) && value != Guid.Empty;
    }

    // ── results ──

    private static object Refused(string? reason, string action = "none") => new
    {
        acted = false,
        action,
        reason,
        activity = (object?)null,
        conversation = (object?)null,
        message = (object?)null
    };

    private static object Acted(string action, object activity, object? conversation = null, object? message = null) => new
    {
        acted = true,
        action,
        reason = (string?)null,
        activity,
        conversation,
        message
    };

    /// <summary>
    /// The one pre-refusal that is a fact about the feed, not noise: the quota is spent, so the
    /// reason is recorded once per asking tick. A skipped row never spends quota itself.
    /// </summary>
    private async Task<object> BudgetExhaustedAsync(CompanionAgentRecord companion, CancellationToken cancellationToken)
    {
        var activity = await RecordAsync(
            companion, "autonomy_skipped", null, null,
            Truncate($"伙伴 {companion.Name} 的今日自主额度已用完", MaxActivitySummary),
            new { autonomous = true, reason = "daily_budget_exhausted" },
            cancellationToken).ConfigureAwait(false);
        return new
        {
            acted = false,
            action = "none",
            reason = "daily_budget_exhausted",
            activity,
            conversation = (object?)null,
            message = (object?)null
        };
    }

    private async Task<object> SkipAsync(
        CompanionAgentRecord companion, string action, string reason, Guid? conversationId, CancellationToken cancellationToken)
    {
        var activity = await RecordAsync(
            companion, "autonomy_skipped", conversationId, null,
            Truncate($"伙伴 {companion.Name} 自主活动未执行：{reason}", MaxActivitySummary),
            new { autonomous = true, action, reason },
            cancellationToken).ConfigureAwait(false);
        return new
        {
            acted = false,
            action,
            reason,
            activity,
            conversation = (object?)null,
            message = (object?)null
        };
    }

    private async Task<object> RecordAsync(
        CompanionAgentRecord companion,
        string kind,
        Guid? conversationId,
        string? conversationTitle,
        string summary,
        object payload,
        CancellationToken cancellationToken)
    {
        var row = new CompanionActivityRecord
        {
            Id = Guid.NewGuid(),
            TenantId = companion.TenantId,
            Kind = kind,
            CompanionId = companion.Id,
            ConversationId = conversationId,
            Summary = Truncate(summary, MaxActivitySummary),
            PayloadJson = JsonSerializer.Serialize(payload, SnakeCase),
            CreatedAt = DateTimeOffset.UtcNow
        };
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Activities.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return CompanionContracts.ActivityJson(row, companion.Name, conversationTitle);
    }

    // ── helpers ──

    private static string IdempotencyKey(Guid companionId, Guid conversationId, string body)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(conversationId.ToString("N") + "\n" + body));
        return $"autonomy:{companionId}:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static DateTimeOffset UtcToday() => new(DateTime.UtcNow.Date, TimeSpan.Zero);

    private static string Preview(string body, int max)
    {
        var oneLine = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
