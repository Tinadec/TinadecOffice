using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Memory;

namespace TinadecCore.Runtime.ProjectBoard;

public interface IProjectCoordinatorPlanner
{
    Task<CoordinatorPlanResultDto> PlanAsync(
        Guid projectId,
        CoordinatorPlanRequest request,
        CancellationToken cancellationToken = default);

    Task<CoordinatorApplyPlanResultDto> ApplyPlanAsync(
        Guid projectId,
        CoordinatorApplyPlanRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Coordinator Smart Task Planner:
/// - Reads project roots/files, <c>project_instructions</c>, <c>notes.md</c>, and current board tasks;
/// - Attempts configured <see cref="IChatClient"/> structured JSON decomposition with a short bounded timeout,
///   falling back cleanly to a deterministic milestone/markdown heuristic planner (<c>planner_source = "heuristic"</c>)
///   when no live LLM provider is configured;
/// - Validates strict DAG acyclicity across <c>depends_on_draft_ids</c> and <c>depends_on_task_ids</c>
///   (<c>409 cycle_detected</c>) and materializes tasks in topological order on <c>apply-plan</c>.
/// </summary>
public sealed class DefaultProjectCoordinatorPlanner : IProjectCoordinatorPlanner
{
    private static readonly TimeSpan LlmPlanTimeout = TimeSpan.FromSeconds(10);

    private static readonly Regex HeadingLineRegex = new(
        @"^(?<hashes>#{1,4})\s+(?<title>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TopListItemRegex = new(
        @"^(?<indent>\s*)(?:(?<num>\d+)[\.\)]\s+|[-*+]\s+(?:\[[ xX]\]\s+)?)(?<body>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TaskKeyMentionRegex = new(
        @"\b([A-Z][A-Z0-9]{1,9}-\d+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DraftDepMentionRegex = new(
        @"(?:depends\s+on|after|requires|依赖|前置)[:\s]+(?:draft-|#|step\s*|phase\s*)(?<idx>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly ProjectBoardCoordinator _coordinator;
    private readonly IDbContextFactory<ProjectBoardDbContext> _dbFactory;
    private readonly IServiceProvider _services;
    private readonly ILogger<DefaultProjectCoordinatorPlanner>? _logger;

    public DefaultProjectCoordinatorPlanner(
        ProjectBoardCoordinator coordinator,
        IDbContextFactory<ProjectBoardDbContext> dbFactory,
        IServiceProvider services,
        ILogger<DefaultProjectCoordinatorPlanner>? logger = null)
    {
        _coordinator = coordinator;
        _dbFactory = dbFactory;
        _services = services;
        _logger = logger;
    }

    public async Task<CoordinatorPlanResultDto> PlanAsync(
        Guid projectId,
        CoordinatorPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var goal = request.Goal?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(goal))
            throw new ArgumentException("goal is required and cannot be blank.");

        var defaultStatus = string.IsNullOrWhiteSpace(request.DefaultStatus)
            ? ProjectBoardTaskStatuses.Ready
            : ProjectBoardCoordinator.NormalizeStatus(request.DefaultStatus);
        if (defaultStatus == ProjectBoardTaskStatuses.Done)
            throw new ArgumentException("default_status must be a non-terminal status (backlog, ready, in_progress, or in_review).");

        var defaultAssignee = ProjectBoardCoordinator.NormalizeAssignee(request.DefaultAssignee);
        var maxTasks = Math.Clamp(request.MaxTasks ?? 8, 1, 20);

        await _coordinator.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var project = await _coordinator.RequireActiveProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        var settings = await _coordinator.GetSettingsAsync(projectId, cancellationToken).ConfigureAwait(false);
        var notes = await _coordinator.GetProjectNotesAsync(projectId, cancellationToken).ConfigureAwait(false);
        var existingTasks = await _coordinator.ListProjectTasksAsync(projectId, filter: null, cancellationToken).ConfigureAwait(false);

        string plannerSource = CoordinatorPlannerSources.Heuristic;
        string summary;
        List<CoordinatorTaskDraftDto> drafts;

        var llmOutcome = await TryGeneratePlanWithLlmAsync(
            project,
            settings,
            notes,
            existingTasks,
            goal,
            defaultStatus,
            defaultAssignee,
            maxTasks,
            cancellationToken).ConfigureAwait(false);

        if (llmOutcome is not null)
        {
            plannerSource = CoordinatorPlannerSources.Llm;
            summary = llmOutcome.Value.Summary;
            drafts = llmOutcome.Value.Drafts;
        }
        else
        {
            (summary, drafts) = BuildHeuristicPlan(
                goal,
                existingTasks,
                defaultStatus,
                defaultAssignee,
                maxTasks);
        }

        var createdTasks = new List<ProjectBoardTaskDto>();
        var autoApplied = false;
        if (request.AutoApply)
        {
            var applied = await ApplyPlanAsync(projectId, new CoordinatorApplyPlanRequest
            {
                Goal = goal,
                UpdateNotes = true,
                Drafts = drafts
            }, cancellationToken).ConfigureAwait(false);
            createdTasks = applied.CreatedTasks;
            autoApplied = true;
        }

        return new CoordinatorPlanResultDto
        {
            ProjectId = projectId,
            Goal = goal,
            Summary = summary,
            PlannerSource = plannerSource,
            AutoApplied = autoApplied,
            Drafts = drafts,
            CreatedTasks = createdTasks
        };
    }

    public async Task<CoordinatorApplyPlanResultDto> ApplyPlanAsync(
        Guid projectId,
        CoordinatorApplyPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Drafts is null || request.Drafts.Count == 0)
            throw new ArgumentException("drafts must contain at least one task draft.");

        await _coordinator.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var project = await _coordinator.RequireActiveProjectAsync(projectId, cancellationToken).ConfigureAwait(false);

        // Normalize and validate draft payloads + intra-draft DAG topological order first.
        var normalizedDrafts = ValidateAndNormalizeDrafts(request.Drafts);
        var orderedDrafts = TopologicalSortDraftsOrThrow(normalizedDrafts);

        var gate = _coordinator.GetProjectGate(projectId);
        var createdDtos = new List<ProjectBoardTaskDto>(orderedDrafts.Count);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var settings = await _coordinator.GetOrCreateSettingsUnderLockAsync(db, project, cancellationToken).ConfigureAwait(false);
            var existingRows = await db.BoardTasks
                .Where(x => x.ProjectId == projectId && !x.Deleted)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var existingGraph = ProjectBoardCoordinator.BuildGraphMap(existingRows);

            // Pre-allocate task IDs for all drafts in topological order so depends_on_draft_ids resolve deterministically.
            var draftToTaskId = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var draft in orderedDrafts)
                draftToTaskId[draft.DraftId] = Guid.NewGuid();

            var resolvedDependsOnByDraft = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
            var combinedGraph = new Dictionary<Guid, (Guid? ParentId, List<Guid> DependsOn)>(existingGraph);

            foreach (var draft in orderedDrafts)
            {
                var deps = new List<Guid>();
                foreach (var depDraftId in draft.DependsOnDraftIds)
                {
                    var mappedId = draftToTaskId[depDraftId];
                    if (!deps.Contains(mappedId))
                        deps.Add(mappedId);
                }

                foreach (var existingTaskId in draft.DependsOnTaskIds)
                {
                    if (!deps.Contains(existingTaskId))
                        deps.Add(existingTaskId);
                }

                resolvedDependsOnByDraft[draft.DraftId] = deps;
                combinedGraph[draftToTaskId[draft.DraftId]] = (ParentId: null, DependsOn: deps);
            }

            // Validate full graph (both existing tasks and all newly planned tasks) before mutating DB.
            foreach (var draft in orderedDrafts)
            {
                var plannedTaskId = draftToTaskId[draft.DraftId];
                ProjectBoardCoordinator.ValidateNoCyclesOrThrow(
                    plannedTaskId,
                    candidateParentId: null,
                    resolvedDependsOnByDraft[draft.DraftId],
                    combinedGraph,
                    requireExistingReferences: true);
            }

            var allRows = new List<ProjectBoardTaskRecord>(existingRows);
            var sortKeysByStatus = existingRows
                .GroupBy(r => r.Status, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(r => r.SortKey).ToList(), StringComparer.Ordinal);
            var nowMs = _coordinator.NowUnixMs();

            foreach (var draft in orderedDrafts)
            {
                var taskId = draftToTaskId[draft.DraftId];
                var taskKey = ProjectBoardCoordinator.AllocateNextTaskKey(settings, allRows);
                if (!sortKeysByStatus.TryGetValue(draft.Status, out var statusSortKeys))
                {
                    statusSortKeys = [];
                    sortKeysByStatus[draft.Status] = statusSortKeys;
                }

                var sortKey = ProjectBoardFractionalIndex.Normalize(null, statusSortKeys);
                statusSortKeys.Add(sortKey);

                var deps = resolvedDependsOnByDraft[draft.DraftId];
                var record = new ProjectBoardTaskRecord
                {
                    TaskId = taskId,
                    ProjectId = projectId,
                    TaskKey = taskKey,
                    Title = draft.Title,
                    DescriptionMd = ProjectBoardMarkdownCodec.NormalizeLineEndings(draft.DescriptionMd).Trim(),
                    ChecklistJson = JsonSerializer.Serialize(draft.Checklist, ProjectBoardMarkdownCodec.CanonicalJson),
                    Status = draft.Status,
                    Priority = draft.Priority,
                    Blocked = false,
                    BlockedReason = null,
                    AssigneeJson = draft.Assignee is null ? null : JsonSerializer.Serialize(draft.Assignee, ProjectBoardMarkdownCodec.CanonicalJson),
                    ParentTaskId = null,
                    DependsOnJson = JsonSerializer.Serialize(deps, ProjectBoardMarkdownCodec.CanonicalJson),
                    LabelsJson = JsonSerializer.Serialize(draft.Labels, ProjectBoardMarkdownCodec.CanonicalJson),
                    SpatialRefJson = null,
                    SortKey = sortKey,
                    Revision = 1,
                    CreatedAtUnixMs = nowMs,
                    UpdatedAtUnixMs = nowMs,
                    Deleted = false
                };

                db.BoardTasks.Add(record);
                allRows.Add(record);
                createdDtos.Add(ProjectBoardCoordinator.ToTaskDto(record, project.Name, syncRow: null));
            }

            settings.UpdatedAtUnixMs = nowMs;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        var syncHook = _coordinator.GetSyncHook();
        var finalDtos = new List<ProjectBoardTaskDto>(createdDtos.Count);
        foreach (var dto in createdDtos)
        {
            var current = dto;
            if (syncHook is not null)
            {
                await syncHook.ExportTaskToMarkdownAsync(projectId, dto.TaskId, cancellationToken).ConfigureAwait(false);
                current = (await _coordinator.GetTaskAsync(projectId, dto.TaskId, cancellationToken).ConfigureAwait(false)) ?? dto;
            }

            finalDtos.Add(current);

            await _coordinator.PublishEventAsync(new ProjectBoardEventDto
            {
                EventType = "task_upserted",
                ProjectId = projectId,
                TaskId = current.TaskId,
                Task = current,
                OccurredAtUnixMs = current.UpdatedAtUnixMs
            }, cancellationToken).ConfigureAwait(false);
        }

        if (request.UpdateNotes != false)
        {
            var goalLabel = string.IsNullOrWhiteSpace(request.Goal)
                ? "Applied Coordinator task plan"
                : TruncateSingleLine(request.Goal, 100);
            var keysText = string.Join(", ", finalDtos.Select(t => t.TaskKey));
            await _coordinator.AppendProjectNoteEntryAsync(
                projectId,
                $"- [Coordinator Plan] {goalLabel} -> Created {finalDtos.Count} task(s): {keysText}",
                cancellationToken).ConfigureAwait(false);
        }

        _coordinator.NotifyAutopilot(projectId);

        return new CoordinatorApplyPlanResultDto
        {
            ProjectId = projectId,
            CreatedTasks = finalDtos
        };
    }

    // -------------------------------------------------------------------------
    // Validation & Topological Sort
    // -------------------------------------------------------------------------

    internal static List<CoordinatorTaskDraftDto> ValidateAndNormalizeDrafts(IReadOnlyList<CoordinatorTaskDraftDto> rawDrafts)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<CoordinatorTaskDraftDto>(rawDrafts.Count);

        for (var i = 0; i < rawDrafts.Count; i++)
        {
            var raw = rawDrafts[i] ?? throw new ArgumentException($"Draft at index {i} cannot be null.");
            var draftId = string.IsNullOrWhiteSpace(raw.DraftId) ? $"draft-{i + 1}" : raw.DraftId.Trim();
            if (!seenIds.Add(draftId))
                throw new ArgumentException($"Duplicate draft_id '{draftId}' in coordinator plan.");

            var title = raw.Title?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(title) || title.Length > 512)
                throw new ArgumentException($"Draft '{draftId}' requires a non-empty title of at most 512 characters.");

            var status = string.IsNullOrWhiteSpace(raw.Status)
                ? ProjectBoardTaskStatuses.Ready
                : ProjectBoardCoordinator.NormalizeStatus(raw.Status);
            var priority = string.IsNullOrWhiteSpace(raw.Priority)
                ? ProjectBoardPriorities.Medium
                : ProjectBoardCoordinator.NormalizePriority(raw.Priority);
            var assignee = ProjectBoardCoordinator.NormalizeAssignee(raw.Assignee);
            var checklist = ProjectBoardCoordinator.NormalizeChecklist(raw.Checklist);
            var labels = ProjectBoardCoordinator.NormalizeLabels(raw.Labels);
            var depDraftIds = (raw.DependsOnDraftIds ?? [])
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => d.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var depTaskIds = ProjectBoardCoordinator.NormalizeGuidList(raw.DependsOnTaskIds);

            list.Add(new CoordinatorTaskDraftDto
            {
                DraftId = draftId,
                Title = title,
                DescriptionMd = ProjectBoardMarkdownCodec.NormalizeLineEndings(raw.DescriptionMd ?? string.Empty).Trim(),
                Checklist = checklist,
                Priority = priority,
                Status = status,
                Assignee = assignee,
                Labels = labels,
                DependsOnDraftIds = depDraftIds,
                DependsOnTaskIds = depTaskIds
            });
        }

        return list;
    }

    internal static List<CoordinatorTaskDraftDto> TopologicalSortDraftsOrThrow(IReadOnlyList<CoordinatorTaskDraftDto> drafts)
    {
        var byId = new Dictionary<string, CoordinatorTaskDraftDto>(drafts.Count, StringComparer.Ordinal);
        var indexById = new Dictionary<string, int>(drafts.Count, StringComparer.Ordinal);
        for (var i = 0; i < drafts.Count; i++)
        {
            byId[drafts[i].DraftId] = drafts[i];
            indexById[drafts[i].DraftId] = i;
        }

        var inDegree = new Dictionary<string, int>(drafts.Count, StringComparer.Ordinal);
        var dependents = new Dictionary<string, List<string>>(drafts.Count, StringComparer.Ordinal);
        foreach (var d in drafts)
        {
            inDegree[d.DraftId] = 0;
            dependents[d.DraftId] = [];
        }

        foreach (var d in drafts)
        {
            foreach (var depId in d.DependsOnDraftIds)
            {
                if (string.Equals(depId, d.DraftId, StringComparison.Ordinal))
                    throw new ProjectBoardCycleException($"Draft '{d.DraftId}' cannot depend on itself.");

                if (!byId.ContainsKey(depId))
                    throw new ArgumentException($"Draft '{d.DraftId}' depends on unknown draft_id '{depId}'.");

                inDegree[d.DraftId] += 1;
                dependents[depId].Add(d.DraftId);
            }
        }

        var readyQueue = new PriorityQueue<string, int>();
        foreach (var d in drafts)
        {
            if (inDegree[d.DraftId] == 0)
                readyQueue.Enqueue(d.DraftId, indexById[d.DraftId]);
        }

        var sorted = new List<CoordinatorTaskDraftDto>(drafts.Count);
        while (readyQueue.Count > 0)
        {
            var nextId = readyQueue.Dequeue();
            sorted.Add(byId[nextId]);

            foreach (var childId in dependents[nextId])
            {
                inDegree[childId] -= 1;
                if (inDegree[childId] == 0)
                    readyQueue.Enqueue(childId, indexById[childId]);
            }
        }

        if (sorted.Count != drafts.Count)
        {
            throw new ProjectBoardCycleException("Dependency cycle detected across coordinator task drafts (depends_on_draft_ids).");
        }

        return sorted;
    }

    // -------------------------------------------------------------------------
    // Optional Live LLM Structured Planner (with Safe Fallback)
    // -------------------------------------------------------------------------

    private async Task<(string Summary, List<CoordinatorTaskDraftDto> Drafts)?> TryGeneratePlanWithLlmAsync(
        ProjectRecord project,
        ProjectBoardSettingsDto settings,
        ProjectNotesDto notes,
        IReadOnlyList<ProjectBoardTaskDto> existingTasks,
        string goal,
        string defaultStatus,
        ProjectBoardAssigneeDto? defaultAssignee,
        int maxTasks,
        CancellationToken outerCt)
    {
        using var llmCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        llmCts.CancelAfter(LlmPlanTimeout);

        try
        {
            IChatClient? chatClient = null;
            if (_services.GetService<IModelProvider>() is { } modelProvider)
            {
                var readiness = await modelProvider.CheckReadinessAsync(llmCts.Token).ConfigureAwait(false);
                if (readiness.IsReady)
                {
                    chatClient = await modelProvider.GetChatClientAsync(routeId: null, llmCts.Token).ConfigureAwait(false);
                }
            }

            if (chatClient is null && _services.GetService<IAgentChatClientFactory>() is { } chatFactory)
            {
                var resolution = await chatFactory.ResolveChatAsync("default", llmCts.Token).ConfigureAwait(false);
                if (resolution.IsAvailable
                    && resolution.Protocol is ChatProtocols.OpenAiChat or ChatProtocols.OpenAiResponses or ChatProtocols.AnthropicMessages)
                {
                    chatClient = await chatFactory.CreateAsync(resolution, llmCts.Token).ConfigureAwait(false);
                }
            }

            if (chatClient is null)
                return null;

            var prompt = BuildLlmPlannerPrompt(project, settings, notes, existingTasks, goal, maxTasks);
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System,
                    "You are a technical Project Coordinator. Decompose the user's goal into an acyclic DAG of concrete tasks. " +
                    "Respond ONLY with valid JSON matching: {\"summary\":\"...\",\"drafts\":[{\"draft_id\":\"draft-1\",\"title\":\"...\",\"description_md\":\"...\",\"checklist\":[\"...\"],\"priority\":\"high|medium|low|urgent\",\"labels\":[\"...\"],\"depends_on_draft_ids\":[],\"depends_on_task_keys\":[]}]}"),
                new(ChatRole.User, prompt)
            };

            var response = await chatClient.GetResponseAsync(messages, cancellationToken: llmCts.Token).ConfigureAwait(false);
            var text = response.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                return null;

            return TryParseLlmPlanJson(text, existingTasks, defaultStatus, defaultAssignee, maxTasks);
        }
        catch (Exception ex) when (!outerCt.IsCancellationRequested)
        {
            _logger?.LogDebug(ex, "LLM coordinator planner unavailable or timed out for project {ProjectId}; using deterministic heuristic planner.", project.Id);
            return null;
        }
    }

    private string BuildLlmPlannerPrompt(
        ProjectRecord project,
        ProjectBoardSettingsDto settings,
        ProjectNotesDto notes,
        IReadOnlyList<ProjectBoardTaskDto> existingTasks,
        string goal,
        int maxTasks)
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine($"Project: {project.Name} ({project.RootPath})");
        sb.AppendLine($"Max tasks: {maxTasks}");

        if (_services.GetService<IWorkspaceDefinitionProvider>()?.Read() is { } wsDef && wsDef.Roots.Count > 0)
        {
            sb.AppendLine("Workspace roots: " + string.Join(", ", wsDef.Roots.Select(r => r.Path)));
        }

        if (!string.IsNullOrWhiteSpace(settings.ProjectInstructions))
        {
            sb.AppendLine("\nProject Instructions:");
            sb.AppendLine(TruncateText(settings.ProjectInstructions.Trim(), 800));
        }

        var notesSummary = ProjectBoardFileSyncService.SummarizeProjectNotes(notes.ContentMd, 1200);
        if (!string.IsNullOrWhiteSpace(notesSummary))
        {
            sb.AppendLine("\nProject Living Notes:");
            sb.AppendLine(notesSummary);
        }

        if (existingTasks.Count > 0)
        {
            sb.AppendLine("\nExisting Board Tasks:");
            foreach (var t in existingTasks.Take(25))
            {
                sb.AppendLine($"- {t.TaskKey} [{t.Status}]: {t.Title}");
            }
        }

        sb.AppendLine("\nGoal to decompose:");
        sb.AppendLine(goal);
        return sb.ToString();
    }

    private static (string Summary, List<CoordinatorTaskDraftDto> Drafts)? TryParseLlmPlanJson(
        string rawResponse,
        IReadOnlyList<ProjectBoardTaskDto> existingTasks,
        string defaultStatus,
        ProjectBoardAssigneeDto? defaultAssignee,
        int maxTasks)
    {
        var json = ExtractJsonObject(rawResponse);
        if (json is null)
            return null;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("drafts", out var draftsEl) || draftsEl.ValueKind != JsonValueKind.Array)
            return null;

        var existingByKey = existingTasks.ToDictionary(t => t.TaskKey, t => t.TaskId, StringComparer.OrdinalIgnoreCase);
        var existingIds = existingTasks.Select(t => t.TaskId).ToHashSet();

        var rawItems = draftsEl.EnumerateArray().Take(maxTasks).ToList();
        if (rawItems.Count == 0)
            return null;

        var drafts = new List<CoordinatorTaskDraftDto>(rawItems.Count);
        var validDraftIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < rawItems.Count; i++)
        {
            var el = rawItems[i];
            if (el.ValueKind != JsonValueKind.Object) continue;
            var title = el.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String ? tEl.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(title)) continue;

            var draftId = el.TryGetProperty("draft_id", out var idEl) && idEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(idEl.GetString())
                ? idEl.GetString()!.Trim()
                : $"draft-{drafts.Count + 1}";
            while (!validDraftIds.Add(draftId))
                draftId = $"draft-{validDraftIds.Count + 1}";

            var desc = el.TryGetProperty("description_md", out var dEl) && dEl.ValueKind == JsonValueKind.String
                ? dEl.GetString() ?? string.Empty
                : string.Empty;

            var priorityRaw = el.TryGetProperty("priority", out var pEl) && pEl.ValueKind == JsonValueKind.String
                ? pEl.GetString()
                : null;
            var priority = ProjectBoardPriorities.IsValid(priorityRaw?.Trim().ToLowerInvariant())
                ? priorityRaw!.Trim().ToLowerInvariant()
                : InferPriority(title + " " + desc);

            var checklist = new List<ProjectBoardChecklistItemDto>();
            if (el.TryGetProperty("checklist", out var cEl) && cEl.ValueKind == JsonValueKind.Array)
            {
                var cIdx = 0;
                foreach (var item in cEl.EnumerateArray())
                {
                    var text = item.ValueKind switch
                    {
                        JsonValueKind.String => item.GetString(),
                        JsonValueKind.Object when item.TryGetProperty("text", out var ctEl) && ctEl.ValueKind == JsonValueKind.String => ctEl.GetString(),
                        _ => null
                    };
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        cIdx++;
                        checklist.Add(new ProjectBoardChecklistItemDto { Id = $"chk-{cIdx}", Text = text.Trim(), Done = false });
                    }
                }
            }
            if (checklist.Count == 0)
                checklist.Add(new ProjectBoardChecklistItemDto { Id = "chk-1", Text = $"Verify {title}", Done = false });

            var labels = new List<string>();
            if (el.TryGetProperty("labels", out var lEl) && lEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in lEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        labels.Add(item.GetString()!.Trim());
                }
            }

            var depDrafts = new List<string>();
            if (el.TryGetProperty("depends_on_draft_ids", out var ddEl) && ddEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ddEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        depDrafts.Add(item.GetString()!.Trim());
                }
            }

            var depTasks = new List<Guid>();
            if (el.TryGetProperty("depends_on_task_ids", out var dtEl) && dtEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dtEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var g) && existingIds.Contains(g))
                        depTasks.Add(g);
                }
            }
            if (el.TryGetProperty("depends_on_task_keys", out var dkEl) && dkEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dkEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString())
                        && existingByKey.TryGetValue(item.GetString()!.Trim(), out var mappedId)
                        && !depTasks.Contains(mappedId))
                    {
                        depTasks.Add(mappedId);
                    }
                }
            }

            drafts.Add(new CoordinatorTaskDraftDto
            {
                DraftId = draftId,
                Title = TruncateSingleLine(title, 240),
                DescriptionMd = desc.Trim(),
                Checklist = checklist,
                Priority = priority,
                Status = defaultStatus,
                Assignee = defaultAssignee,
                Labels = ProjectBoardCoordinator.NormalizeLabels(labels),
                DependsOnDraftIds = depDrafts,
                DependsOnTaskIds = depTasks
            });
        }

        if (drafts.Count == 0)
            return null;

        // Sanitize any unknown or cyclic draft references from LLM output so returned drafts are guaranteed acyclic.
        SanitizeDraftDependenciesInPlace(drafts);

        var summary = root.TryGetProperty("summary", out var sEl) && sEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(sEl.GetString())
            ? sEl.GetString()!.Trim()
            : $"Decomposed goal into {drafts.Count} task draft(s).";

        return (summary, drafts);
    }

    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private static void SanitizeDraftDependenciesInPlace(List<CoordinatorTaskDraftDto> drafts)
    {
        var knownIds = drafts.Select(d => d.DraftId).ToHashSet(StringComparer.Ordinal);
        var acceptedEdges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var d in drafts)
            acceptedEdges[d.DraftId] = [];

        for (var i = 0; i < drafts.Count; i++)
        {
            var d = drafts[i];
            var cleanDeps = new List<string>();
            foreach (var cand in d.DependsOnDraftIds.Distinct(StringComparer.Ordinal))
            {
                if (!knownIds.Contains(cand) || string.Equals(cand, d.DraftId, StringComparison.Ordinal))
                    continue;
                if (!WouldIntroduceDraftCycle(d.DraftId, cand, acceptedEdges))
                {
                    acceptedEdges[d.DraftId].Add(cand);
                    cleanDeps.Add(cand);
                }
            }

            drafts[i] = new CoordinatorTaskDraftDto
            {
                DraftId = d.DraftId,
                Title = d.Title,
                DescriptionMd = d.DescriptionMd,
                Checklist = d.Checklist,
                Priority = d.Priority,
                Status = d.Status,
                Assignee = d.Assignee,
                Labels = d.Labels,
                DependsOnDraftIds = cleanDeps,
                DependsOnTaskIds = d.DependsOnTaskIds
            };
        }
    }

    private static bool WouldIntroduceDraftCycle(string sourceId, string targetId, Dictionary<string, List<string>> edges)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(targetId);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (string.Equals(cur, sourceId, StringComparison.Ordinal))
                return true;
            if (!visited.Add(cur))
                continue;
            if (edges.TryGetValue(cur, out var nexts))
            {
                foreach (var n in nexts)
                    stack.Push(n);
            }
        }
        return false;
    }

    // -------------------------------------------------------------------------
    // Deterministic Heuristic Decomposition Planner
    // -------------------------------------------------------------------------

    internal static (string Summary, List<CoordinatorTaskDraftDto> Drafts) BuildHeuristicPlan(
        string goal,
        IReadOnlyList<ProjectBoardTaskDto> existingTasks,
        string defaultStatus,
        ProjectBoardAssigneeDto? defaultAssignee,
        int maxTasks)
    {
        var normalizedGoal = ProjectBoardMarkdownCodec.NormalizeLineEndings(goal).Trim();
        var existingByKey = existingTasks.ToDictionary(t => t.TaskKey, t => t.TaskId, StringComparer.OrdinalIgnoreCase);

        var units = ExtractHeuristicUnits(normalizedGoal);
        var drafts = new List<CoordinatorTaskDraftDto>();

        if (units.Count >= 2)
        {
            var boundedUnits = units.Take(maxTasks).ToList();
            var anyNumbered = boundedUnits.Any(u => u.IsSequential);

            for (var i = 0; i < boundedUnits.Count; i++)
            {
                var unit = boundedUnits[i];
                var draftId = $"draft-{i + 1}";
                var depDrafts = new List<string>();
                var combinedUnitText = string.Join('\n', new[] { unit.Title, unit.Description }.Concat(unit.ChecklistItems));

                foreach (Match m in DraftDepMentionRegex.Matches(combinedUnitText))
                {
                    if (int.TryParse(m.Groups["idx"].Value, out var depIdx) && depIdx >= 1 && depIdx <= i)
                    {
                        var targetDraftId = $"draft-{depIdx}";
                        if (!depDrafts.Contains(targetDraftId))
                            depDrafts.Add(targetDraftId);
                    }
                }

                if (depDrafts.Count == 0 && i > 0)
                {
                    if (anyNumbered || unit.IsSequential || IsVerificationOrIntegrationStage(unit.Title))
                    {
                        depDrafts.Add($"draft-{i}");
                    }
                }

                var depTaskIds = ExtractReferencedTaskIds(combinedUnitText, existingByKey);
                var checklist = unit.ChecklistItems.Count > 0
                    ? unit.ChecklistItems.Select((c, idx) => new ProjectBoardChecklistItemDto { Id = $"chk-{idx + 1}", Text = c, Done = false }).ToList()
                    :
                    [
                        new ProjectBoardChecklistItemDto { Id = "chk-1", Text = $"Implement {TruncateSingleLine(unit.Title, 96)}", Done = false },
                        new ProjectBoardChecklistItemDto { Id = "chk-2", Text = "Verify build and targeted tests pass", Done = false }
                    ];

                drafts.Add(new CoordinatorTaskDraftDto
                {
                    DraftId = draftId,
                    Title = TruncateSingleLine(unit.Title, 200),
                    DescriptionMd = string.IsNullOrWhiteSpace(unit.Description)
                        ? $"Deliver **{unit.Title}** as part of project goal:\n\n> {TruncateSingleLine(normalizedGoal, 180)}"
                        : unit.Description.Trim(),
                    Checklist = checklist,
                    Priority = InferPriority(combinedUnitText),
                    Status = defaultStatus,
                    Assignee = defaultAssignee,
                    Labels = InferLabels(combinedUnitText),
                    DependsOnDraftIds = depDrafts,
                    DependsOnTaskIds = depTaskIds
                });
            }
        }
        else
        {
            var headline = ExtractGoalHeadline(normalizedGoal);
            var referencedExisting = ExtractReferencedTaskIds(normalizedGoal, existingByKey);

            if (maxTasks == 1)
            {
                drafts.Add(new CoordinatorTaskDraftDto
                {
                    DraftId = "draft-1",
                    Title = TruncateSingleLine(headline, 200),
                    DescriptionMd = normalizedGoal,
                    Checklist =
                    [
                        new ProjectBoardChecklistItemDto { Id = "chk-1", Text = "Complete core implementation", Done = false },
                        new ProjectBoardChecklistItemDto { Id = "chk-2", Text = "Verify targeted automated tests pass", Done = false }
                    ],
                    Priority = InferPriority(normalizedGoal),
                    Status = defaultStatus,
                    Assignee = defaultAssignee,
                    Labels = InferLabels(normalizedGoal),
                    DependsOnDraftIds = [],
                    DependsOnTaskIds = referencedExisting
                });
            }
            else if (maxTasks == 2)
            {
                drafts.Add(new CoordinatorTaskDraftDto
                {
                    DraftId = "draft-1",
                    Title = TruncateSingleLine($"Implement {headline}", 200),
                    DescriptionMd = $"Design contracts and implement core logic for:\n\n{normalizedGoal}",
                    Checklist =
                    [
                        new ProjectBoardChecklistItemDto { Id = "chk-1", Text = "Define contracts and data structures", Done = false },
                        new ProjectBoardChecklistItemDto { Id = "chk-2", Text = "Implement core runtime and API/UI wiring", Done = false }
                    ],
                    Priority = InferPriority(normalizedGoal),
                    Status = defaultStatus,
                    Assignee = defaultAssignee,
                    Labels = InferLabels(normalizedGoal),
                    DependsOnDraftIds = [],
                    DependsOnTaskIds = referencedExisting
                });
                drafts.Add(new CoordinatorTaskDraftDto
                {
                    DraftId = "draft-2",
                    Title = TruncateSingleLine($"Verify & test {headline}", 200),
                    DescriptionMd = $"Write automated tests and validate end-to-end behavior for **{headline}**.",
                    Checklist =
                    [
                        new ProjectBoardChecklistItemDto { Id = "chk-1", Text = "Add unit and integration test coverage", Done = false },
                        new ProjectBoardChecklistItemDto { Id = "chk-2", Text = "Confirm zero regressions across build & test suite", Done = false }
                    ],
                    Priority = ProjectBoardPriorities.Medium,
                    Status = defaultStatus,
                    Assignee = defaultAssignee,
                    Labels = MergeLabel(InferLabels(normalizedGoal), "testing"),
                    DependsOnDraftIds = ["draft-1"],
                    DependsOnTaskIds = []
                });
            }
            else
            {
                drafts.Add(new CoordinatorTaskDraftDto
                {
                    DraftId = "draft-1",
                    Title = TruncateSingleLine($"Design contracts & data model: {headline}", 200),
                    DescriptionMd = $"Establish clean contracts, schema/state model, and boundaries for:\n\n{normalizedGoal}",
                    Checklist =
                    [
                        new ProjectBoardChecklistItemDto { Id = "chk-1", Text = "Define DTOs, interfaces, and persistence/state shape", Done = false },
                        new ProjectBoardChecklistItemDto { Id = "chk-2", Text = "Validate edge cases and error contracts", Done = false }
                    ],
                    Priority = ProjectBoardPriorities.High,
                    Status = defaultStatus,
                    Assignee = defaultAssignee,
                    Labels = MergeLabel(InferLabels(normalizedGoal), "architecture"),
                    DependsOnDraftIds = [],
                    DependsOnTaskIds = referencedExisting
                });
                drafts.Add(new CoordinatorTaskDraftDto
                {
                    DraftId = "draft-2",
                    Title = TruncateSingleLine($"Implement core logic & integration: {headline}", 200),
                    DescriptionMd = $"Implement the core service, endpoints, and UI/workflow integration for **{headline}**.",
                    Checklist =
                    [
                        new ProjectBoardChecklistItemDto { Id = "chk-1", Text = "Implement core service and business rules", Done = false },
                        new ProjectBoardChecklistItemDto { Id = "chk-2", Text = "Wire endpoints / client integration cleanly", Done = false }
                    ],
                    Priority = InferPriority(normalizedGoal),
                    Status = defaultStatus,
                    Assignee = defaultAssignee,
                    Labels = InferLabels(normalizedGoal),
                    DependsOnDraftIds = ["draft-1"],
                    DependsOnTaskIds = []
                });
                drafts.Add(new CoordinatorTaskDraftDto
                {
                    DraftId = "draft-3",
                    Title = TruncateSingleLine($"Automated tests & verification: {headline}", 200),
                    DescriptionMd = $"Add targeted unit/API tests and verify end-to-end acceptance criteria for **{headline}**.",
                    Checklist =
                    [
                        new ProjectBoardChecklistItemDto { Id = "chk-1", Text = "Cover happy path, concurrency, and failure cases", Done = false },
                        new ProjectBoardChecklistItemDto { Id = "chk-2", Text = "Run build and targeted test suites with 0 failures", Done = false }
                    ],
                    Priority = ProjectBoardPriorities.Medium,
                    Status = defaultStatus,
                    Assignee = defaultAssignee,
                    Labels = MergeLabel(InferLabels(normalizedGoal), "testing"),
                    DependsOnDraftIds = ["draft-2"],
                    DependsOnTaskIds = []
                });
            }
        }

        SanitizeDraftDependenciesInPlace(drafts);
        var depEdgeCount = drafts.Sum(d => d.DependsOnDraftIds.Count + d.DependsOnTaskIds.Count);
        var summary = $"Decomposed goal into {drafts.Count} structured task draft(s) with {depEdgeCount} dependency link(s).";
        return (summary, drafts);
    }

    private sealed class HeuristicUnit
    {
        public required string Title { get; init; }
        public string Description { get; set; } = string.Empty;
        public List<string> ChecklistItems { get; } = [];
        public bool IsSequential { get; init; }
    }

    private static List<HeuristicUnit> ExtractHeuristicUnits(string markdown)
    {
        var lines = markdown.Split('\n');

        // 1. Check if markdown has multiple ## / ### section headings
        var headingUnits = new List<HeuristicUnit>();
        HeuristicUnit? currentHeading = null;
        var descLines = new List<string>();

        void FlushHeading()
        {
            if (currentHeading is null) return;
            currentHeading.Description = string.Join('\n', descLines).Trim();
            headingUnits.Add(currentHeading);
            currentHeading = null;
            descLines.Clear();
        }

        foreach (var rawLine in lines)
        {
            var hMatch = HeadingLineRegex.Match(rawLine);
            if (hMatch.Success && hMatch.Groups["hashes"].Value.Length is 2 or 3)
            {
                FlushHeading();
                var cleanTitle = StripMarkdownFormatting(hMatch.Groups["title"].Value);
                if (!string.IsNullOrWhiteSpace(cleanTitle))
                {
                    currentHeading = new HeuristicUnit
                    {
                        Title = cleanTitle,
                        IsSequential = Regex.IsMatch(cleanTitle, @"^(?:phase|step|stage|milestone|\d+)[\s\.:\-]", RegexOptions.IgnoreCase)
                    };
                }
                continue;
            }

            if (currentHeading is null) continue;

            var listMatch = TopListItemRegex.Match(rawLine);
            if (listMatch.Success)
            {
                currentHeading.ChecklistItems.Add(StripMarkdownFormatting(listMatch.Groups["body"].Value));
            }
            else if (!string.IsNullOrWhiteSpace(rawLine))
            {
                descLines.Add(rawLine.Trim());
            }
        }
        FlushHeading();

        if (headingUnits.Count >= 2)
            return headingUnits;

        // 2. Otherwise check top-level numbered or bulleted list items
        var listUnits = new List<HeuristicUnit>();
        HeuristicUnit? currentListUnit = null;
        var baseIndent = -1;

        foreach (var rawLine in lines)
        {
            var m = TopListItemRegex.Match(rawLine);
            if (m.Success)
            {
                var indent = m.Groups["indent"].Value.Replace("\t", "  ", StringComparison.Ordinal).Length;
                if (baseIndent < 0 || indent <= baseIndent)
                {
                    baseIndent = indent;
                    var body = StripMarkdownFormatting(m.Groups["body"].Value);
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        currentListUnit = new HeuristicUnit
                        {
                            Title = SplitLeadClause(body, out var restDesc),
                            Description = restDesc,
                            IsSequential = m.Groups["num"].Success
                        };
                        listUnits.Add(currentListUnit);
                    }
                }
                else if (currentListUnit is not null)
                {
                    currentListUnit.ChecklistItems.Add(StripMarkdownFormatting(m.Groups["body"].Value));
                }
            }
            else if (currentListUnit is not null && !string.IsNullOrWhiteSpace(rawLine) && rawLine.StartsWith(' '))
            {
                currentListUnit.Description = string.IsNullOrEmpty(currentListUnit.Description)
                    ? rawLine.Trim()
                    : currentListUnit.Description + "\n" + rawLine.Trim();
            }
        }

        if (listUnits.Count >= 2)
            return listUnits;

        // 3. Semicolon / arrow pipeline ("Step A -> Step B -> Step C")
        if (markdown.Contains("->", StringComparison.Ordinal) && !markdown.Contains('\n'))
        {
            var parts = markdown.Split("->", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                return parts.Select(p => new HeuristicUnit
                {
                    Title = StripMarkdownFormatting(p),
                    IsSequential = true
                }).ToList();
            }
        }

        return [];
    }

    private static List<Guid> ExtractReferencedTaskIds(string text, IReadOnlyDictionary<string, Guid> existingByKey)
    {
        var list = new List<Guid>();
        foreach (Match m in TaskKeyMentionRegex.Matches(text))
        {
            if (existingByKey.TryGetValue(m.Groups[1].Value, out var id) && !list.Contains(id))
                list.Add(id);
        }
        return list;
    }

    private static string ExtractGoalHeadline(string goal)
    {
        var firstLine = goal.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? goal;
        firstLine = StripMarkdownFormatting(Regex.Replace(firstLine, @"^#+\s*", string.Empty));
        return TruncateSingleLine(firstLine, 90);
    }

    private static string SplitLeadClause(string text, out string remainder)
    {
        var colonIdx = text.IndexOf(": ", StringComparison.Ordinal);
        if (colonIdx is >= 4 and <= 90)
        {
            remainder = text[(colonIdx + 2)..].Trim();
            return text[..colonIdx].Trim();
        }
        remainder = string.Empty;
        return text;
    }

    private static string StripMarkdownFormatting(string text) =>
        text.Trim().Replace("**", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);

    private static bool IsVerificationOrIntegrationStage(string title) =>
        Regex.IsMatch(title, @"\b(test|tests|testing|verify|verification|qa|e2e|release|deploy|测试|验收|联调)\b", RegexOptions.IgnoreCase);

    private static string InferPriority(string text)
    {
        if (Regex.IsMatch(text, @"\b(urgent|critical|p0|blocker|security|紧急|高危)\b", RegexOptions.IgnoreCase))
            return ProjectBoardPriorities.Urgent;
        if (Regex.IsMatch(text, @"\b(high|important|p1|core|schema|migration|关键|核心)\b", RegexOptions.IgnoreCase))
            return ProjectBoardPriorities.High;
        if (Regex.IsMatch(text, @"\b(low|minor|cleanup|polish|docs|p3|次要|文档)\b", RegexOptions.IgnoreCase))
            return ProjectBoardPriorities.Low;
        return ProjectBoardPriorities.Medium;
    }

    private static List<string> InferLabels(string text)
    {
        var labels = new List<string>();
        if (Regex.IsMatch(text, @"\b(api|endpoint|backend|service|runtime|db|sqlite|postgres|migration|后端|接口|数据库)\b", RegexOptions.IgnoreCase))
            labels.Add("backend");
        if (Regex.IsMatch(text, @"\b(ui|ux|vue|frontend|modal|drawer|component|css|前端|界面|组件)\b", RegexOptions.IgnoreCase))
            labels.Add("frontend");
        if (Regex.IsMatch(text, @"\b(test|vitest|xunit|spec|qa|测试|单测)\b", RegexOptions.IgnoreCase))
            labels.Add("testing");
        if (Regex.IsMatch(text, @"\b(doc|notes|readme|文档)\b", RegexOptions.IgnoreCase))
            labels.Add("docs");
        return labels;
    }

    private static List<string> MergeLabel(List<string> labels, string extra)
    {
        if (!labels.Contains(extra, StringComparer.OrdinalIgnoreCase))
            labels.Add(extra);
        return labels;
    }

    private static string TruncateSingleLine(string text, int maxChars)
    {
        var single = Regex.Replace(text.Trim(), @"\s+", " ");
        return single.Length <= maxChars ? single : single[..Math.Max(1, maxChars - 1)].TrimEnd() + "…";
    }

    private static string TruncateText(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..Math.Max(1, maxChars - 1)].TrimEnd() + "…";
}
