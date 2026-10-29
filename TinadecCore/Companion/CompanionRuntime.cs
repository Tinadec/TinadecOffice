using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Companion;

/// <summary>Hand-off between the message endpoint and the background run loop.</summary>
public interface ICompanionRuntime
{
    ValueTask EnqueueAsync(Guid runId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes the runs a user message owes, one at a time, off the request thread. The run row carries
/// its own tenant id, so nothing here reads a request-scoped tenant: the hosted instance runs in the
/// host container while endpoints only enqueue. A run freezes the companion's configuration before
/// the model is called, never executes a tool, and writes at most one reply message, so a companion
/// reply can never cascade into another companion's turn.
/// </summary>
public sealed class CompanionRuntime : BackgroundService, ICompanionRuntime
{
    private const int MaxMemories = 6;
    private const int MaxContextMessages = 30;
    private const int MaxErrorMessage = 2048;

    private static readonly JsonSerializerOptions SnakeCase = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private const string DefaultPersona = "你是一个乐于助人的伙伴。";

    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly IDbContextFactory<CompanionDbContext> _dbFactory;
    private readonly IAgentChatClientFactory _chatClients;
    private readonly ILogger<CompanionRuntime> _logger;

    public CompanionRuntime(
        IDbContextFactory<CompanionDbContext> dbFactory,
        IAgentChatClientFactory chatClients,
        ILogger<CompanionRuntime> logger)
    {
        _dbFactory = dbFactory;
        _chatClients = chatClients;
        _logger = logger;
    }

    public ValueTask EnqueueAsync(Guid runId, CancellationToken cancellationToken = default) =>
        _queue.Writer.WriteAsync(runId, cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var runId in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ExecuteAsync(runId, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // A run interrupted by shutdown must not stay "running": a live row reads as
                // "thinking" to every client that asks.
                await FailAsync(runId, "companion_run_failed", "The run was interrupted by host shutdown.", CancellationToken.None)
                    .ConfigureAwait(false);
                break;
            }
            catch (Exception exception)
            {
                // A run that fell over must not take the loop with it; the run row already carries
                // the failure it was given, and this is only a diagnostic.
                _logger.LogError(exception, "Companion run {RunId} failed outside its own failure path.", runId);
            }
        }
    }

    /// <summary>Public so a test or an operator can settle one run without the background loop.</summary>
    public async Task ExecuteAsync(Guid runId, CancellationToken cancellationToken)
    {
        CompanionRunRecord run;
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = await db.Runs.SingleOrDefaultAsync(x => x.Id == runId && x.Status == "queued", cancellationToken).ConfigureAwait(false);
            // Another executor already took it (or it is finished): nothing to do.
            if (row is null) return;
            row.Status = "running";
            row.StartedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            run = row;
        }

        var prompt = await FreezeAsync(run, cancellationToken).ConfigureAwait(false);
        if (prompt is null) return;

        var resolution = prompt.ProviderId is { } providerId
            ? await _chatClients.ResolveModelChatAsync(providerId, prompt.ModelName, cancellationToken).ConfigureAwait(false)
            : await _chatClients.ResolveChatAsync(prompt.ModelPurpose, cancellationToken).ConfigureAwait(false);
        if (!resolution.IsAvailable)
        {
            await FailAsync(run.Id, "companion_model_unavailable",
                resolution.Error ?? "No chat route is available.", cancellationToken).ConfigureAwait(false);
            return;
        }

        string? reply;
        try
        {
            using var client = await _chatClients.CreateAsync(resolution, cancellationToken).ConfigureAwait(false);
            var options = new ChatOptions
            {
                Instructions = prompt.Instructions,
                ToolMode = ChatToolMode.None,
                AllowMultipleToolCalls = false
            };
            var response = await client.GetResponseAsync(prompt.Messages, options, cancellationToken).ConfigureAwait(false);
            reply = response.Text?.Trim();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FailAsync(run.Id, "companion_run_failed", "The run was interrupted.", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await FailAsync(run.Id, "companion_run_failed", exception.Message, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrEmpty(reply))
        {
            await FailAsync(run.Id, "companion_run_failed", "The companion produced no reply.", cancellationToken).ConfigureAwait(false);
            return;
        }

        await CompleteAsync(run, reply, cancellationToken).ConfigureAwait(false);
    }

    // ── freeze ──

    private sealed record FrozenPrompt(Guid? ProviderId, string? ModelName, string ModelPurpose, string Instructions, List<ChatMessage> Messages);

    /// <summary>
    /// Reads the companion, its private memories, enabled skills and the recent transcript, then
    /// writes the frozen configuration onto the run. Returns null (after marking the run failed)
    /// when the companion is gone, archived or belongs to another tenant.
    /// </summary>
    private async Task<FrozenPrompt?> FreezeAsync(CompanionRunRecord run, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Runs.SingleOrDefaultAsync(x => x.Id == run.Id && x.Status == "running", cancellationToken).ConfigureAwait(false);
        if (row is null) return null;

        var companion = await db.Companions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == row.CompanionId && x.TenantId == row.TenantId && x.Status != "archived", cancellationToken)
            .ConfigureAwait(false);
        if (companion is null)
        {
            await FailAsync(row.Id, "companion_not_found", "Companion not found.", cancellationToken).ConfigureAwait(false);
            return null;
        }

        var triggerBody = await db.Messages.AsNoTracking()
            .Where(x => x.Id == row.TriggerMessageId && x.ConversationId == row.ConversationId && x.TenantId == row.TenantId)
            .Select(x => x.Body)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;

        // Only this companion's indexed memories take part: group content is never merged into a
        // member's private memory, and another companion's rows are not even a candidate.
        var allMemories = await db.Memories.AsNoTracking()
            .Where(x => x.TenantId == row.TenantId && x.CompanionId == companion.Id && x.IndexStatus == "indexed")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var memories = SelectMemories(allMemories, triggerBody);

        var skills = (await db.SkillBindings
                .Where(x => x.TenantId == row.TenantId && x.CompanionId == companion.Id && x.Enabled)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToList();

        var now = DateTimeOffset.UtcNow;
        row.ConfigSnapshotJson = JsonSerializer.Serialize(new
        {
            companion_id = companion.Id,
            revision = companion.Revision,
            name = companion.Name,
            persona_document = companion.PersonaDocument,
            model_purpose = companion.ModelPurpose,
            model_provider_id = companion.ModelProviderId,
            model = companion.ModelName,
            memories = memories.Select(x => new { id = x.Id, content = x.Content }),
            skills = skills.Select(x => new { id = x.Id, name = x.Name, instructions = x.Instructions })
        }, SnakeCase);
        row.ConfigRevision = companion.Revision;
        row.LoadedMemoryIdsJson = JsonSerializer.Serialize(memories.Select(x => x.Id));
        row.LoadedSkillBindingIdsJson = JsonSerializer.Serialize(skills.Select(x => x.Id));
        foreach (var skill in skills)
        {
            skill.LastLoadedRunId = row.Id;
            skill.LastLoadedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var messages = await TranscriptAsync(db, row, cancellationToken).ConfigureAwait(false);
        return new FrozenPrompt(companion.ModelProviderId, companion.ModelName, companion.ModelPurpose, Instructions(companion, memories, skills), messages);
    }

    private static string Instructions(
        CompanionAgentRecord companion,
        IReadOnlyList<CompanionMemoryRecord> memories,
        IReadOnlyList<CompanionSkillBindingRecord> skills)
    {
        var persona = string.IsNullOrWhiteSpace(companion.PersonaDocument) ? DefaultPersona : companion.PersonaDocument.Trim();
        var text = new System.Text.StringBuilder(persona);
        text.AppendLine().AppendLine();
        text.AppendLine($"你是伙伴域中的伙伴「{companion.Name}」。你只与本会话的成员交流，不调用任何工具、MCP 或外部设备；"
            + "不泄露其他成员的私有信息，群聊内容也不会自动成为任何成员的私有记忆。");
        if (memories.Count > 0)
        {
            text.AppendLine().AppendLine("你记得的事实（只有你自己可见）：");
            foreach (var memory in memories) text.AppendLine($"- {memory.Content}");
        }
        var withInstructions = skills.Where(x => !string.IsNullOrWhiteSpace(x.Instructions)).ToList();
        if (withInstructions.Count > 0)
        {
            text.AppendLine().AppendLine("可参考的工作手册（仅作说明，不执行工具）：");
            foreach (var skill in withInstructions) text.AppendLine($"- {skill.Name}：{skill.Instructions!.Trim()}");
        }
        return text.ToString().TrimEnd();
    }

    private static async Task<List<ChatMessage>> TranscriptAsync(
        CompanionDbContext db, CompanionRunRecord run, CancellationToken cancellationToken)
    {
        var rows = (await db.Messages.AsNoTracking()
                .Where(x => x.TenantId == run.TenantId && x.ConversationId == run.ConversationId)
                .OrderByDescending(x => x.Seq)
                .Take(MaxContextMessages)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(x => x.Seq)
            .ToList();
        var names = await db.Companions.AsNoTracking()
            .Where(x => x.TenantId == run.TenantId)
            .Select(x => new { x.Id, x.Name })
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken).ConfigureAwait(false);

        var messages = new List<ChatMessage>();
        foreach (var row in rows)
        {
            messages.Add(row.AuthorKind switch
            {
                "system" => new ChatMessage(ChatRole.System, $"系统：{row.Body}"),
                // The companion's own past turns stay assistant turns; another companion speaks as a
                // named participant, never as this companion.
                "companion" when row.AuthorCompanionId == run.CompanionId => new ChatMessage(ChatRole.Assistant, row.Body),
                "companion" => new ChatMessage(ChatRole.User,
                    $"{(row.AuthorCompanionId is { } id ? names.GetValueOrDefault(id, "伙伴") : "伙伴")}：{row.Body}"),
                _ => new ChatMessage(ChatRole.User, $"用户：{row.Body}")
            });
        }
        if (messages.Count == 0) messages.Add(new ChatMessage(ChatRole.User, "（本会话还没有消息）"));
        return messages;
    }

    /// <summary>
    /// Keyword/containment match against the trigger message, then the most recently updated rows
    /// fill the remaining slots so the budget is always used deliberately rather than randomly.
    /// </summary>
    private static List<CompanionMemoryRecord> SelectMemories(IReadOnlyList<CompanionMemoryRecord> memories, string body)
    {
        if (memories.Count == 0) return [];
        var terms = Terms(body);
        var scored = memories
            .Select(memory => new
            {
                Row = memory,
                Score = (body.Contains(memory.Content, StringComparison.OrdinalIgnoreCase) ? 100 : 0)
                    + terms.Count(term => memory.Content.Contains(term, StringComparison.OrdinalIgnoreCase))
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Row.UpdatedAt)
            .Take(MaxMemories)
            .Select(x => x.Row)
            .ToList();
        if (scored.Count < MaxMemories)
        {
            scored.AddRange(memories
                .Where(memory => !scored.Contains(memory))
                .OrderByDescending(x => x.UpdatedAt)
                .Take(MaxMemories - scored.Count));
        }
        return scored;
    }

    private static string[] Terms(string body)
    {
        char[] separators = [' ', '\n', '\r', '\t', ',', '，', '.', '。', '?', '？', '!', '！', '、', ';', '；', ':', '：', '(', ')', '（', '）', '"', '“', '”'];
        return body.Split(separators, StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // ── settle ──

    private async Task CompleteAsync(CompanionRunRecord run, string reply, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Runs.SingleOrDefaultAsync(x => x.Id == run.Id && x.Status == "running", cancellationToken).ConfigureAwait(false);
        if (row is null) return;
        var conversation = await db.Conversations
            .SingleOrDefaultAsync(x => x.Id == row.ConversationId && x.TenantId == row.TenantId, cancellationToken).ConfigureAwait(false);
        if (conversation is null)
        {
            await FailAsync(row.Id, "companion_run_failed", "Conversation not found.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var message = await CompanionMessages.AppendAsync(
            db, row.TenantId, row.ConversationId, "companion", row.CompanionId, reply, [], null, now, cancellationToken).ConfigureAwait(false);
        row.Status = "completed";
        row.ReplyMessageId = message.Id;
        row.Rounds = 1;
        row.FinishedAt = now;
        conversation.LastMessageAt = now;
        var name = await db.Companions.AsNoTracking()
            .Where(x => x.Id == row.CompanionId && x.TenantId == row.TenantId)
            .Select(x => x.Name)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? "伙伴";
        db.Activities.Add(new CompanionActivityRecord
        {
            Id = Guid.NewGuid(),
            TenantId = row.TenantId,
            Kind = "run_completed",
            CompanionId = row.CompanionId,
            ConversationId = row.ConversationId,
            Summary = $"伙伴 {name} 完成了回复",
            Detail = Truncate(reply, 500),
            PayloadJson = JsonSerializer.Serialize(new { run_id = row.Id, reply_message_id = message.Id }),
            CreatedAt = now
        });
        try
        {
            await CompanionMessages.SaveAsync(db, message, row.TenantId, row.ConversationId, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
        {
            _logger.LogWarning(exception, "Companion reply for run {RunId} could not allocate a message sequence.", row.Id);
            await FailAsync(row.Id, "companion_run_failed", "The reply could not be stored.", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FailAsync(Guid runId, string code, string message, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var row = await db.Runs.SingleOrDefaultAsync(x => x.Id == runId && x.Status == "running", cancellationToken).ConfigureAwait(false);
            if (row is null) return;
            row.Status = "failed";
            row.ErrorCode = code;
            row.ErrorMessage = Truncate(message, MaxErrorMessage);
            row.FinishedAt = DateTimeOffset.UtcNow;
            var name = await db.Companions.AsNoTracking()
                .Where(x => x.Id == row.CompanionId && x.TenantId == row.TenantId)
                .Select(x => x.Name)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            db.Activities.Add(new CompanionActivityRecord
            {
                Id = Guid.NewGuid(),
                TenantId = row.TenantId,
                Kind = "run_failed",
                CompanionId = row.CompanionId,
                ConversationId = row.ConversationId,
                Summary = name is null ? $"伙伴运行失败：{code}" : $"伙伴 {name} 运行失败：{code}",
                Detail = Truncate(message, 500),
                PayloadJson = JsonSerializer.Serialize(new { run_id = row.Id, error_code = code }),
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Nothing else to do: the host is stopping and the row keeps its last visible state.
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not record the failure of companion run {RunId}.", runId);
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
