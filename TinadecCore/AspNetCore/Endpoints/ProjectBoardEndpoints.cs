using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;
using TinadecCore.Memory;
using TinadecCore.Persistence;
using TinadecCore.Runtime;
using TinadecCore.Runtime.ProjectBoard;
using TinadecCore.Runtime.SpatialBoard;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// HTTP + SSE endpoints for single-project task boards (<c>/api/v1/projects/{projectId}/board/*</c>)
/// and the cross-project global board &amp; saved views (<c>/api/v1/board/*</c>).
/// </summary>
public static class ProjectBoardEndpoints
{
    private static readonly JsonSerializerOptions WireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly TimeSpan SseHeartbeatInterval = TimeSpan.FromSeconds(15);

    public static IEndpointRouteBuilder MapProjectBoardEndpoints(this IEndpointRouteBuilder app)
    {
        var projectGroup = app.MapGroup("/api/v1/projects/{projectId:guid}/board")
            .WithTags("ProjectBoard");

        projectGroup.MapGet("/settings", GetSettingsAsync)
            .Produces<ProjectBoardSettingsDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        projectGroup.MapPut("/settings", PutSettingsAsync)
            .Accepts<UpdateProjectBoardSettingsRequest>("application/json")
            .Produces<ProjectBoardSettingsDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        projectGroup.MapPost("/sync", SyncProjectBoardAsync)
            .Produces<ProjectBoardSyncResultDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        projectGroup.MapGet("/tasks", ListProjectTasksAsync)
            .Produces<IReadOnlyList<ProjectBoardTaskDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        projectGroup.MapPost("/tasks", CreateProjectTaskAsync)
            .Accepts<CreateProjectBoardTaskRequest>("application/json")
            .Produces<ProjectBoardTaskDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        projectGroup.MapPost("/tasks/promote-from-spatial", PromoteFromSpatialAsync)
            .Accepts<PromoteFromSpatialRequest>("application/json")
            .Produces<PromoteFromSpatialResultDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        projectGroup.MapGet("/tasks/{taskId:guid}", GetProjectTaskAsync)
            .Produces<ProjectBoardTaskDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        projectGroup.MapPatch("/tasks/{taskId:guid}", PatchProjectTaskAsync)
            .Accepts<UpdateProjectBoardTaskRequest>("application/json")
            .Produces<ProjectBoardTaskDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        projectGroup.MapDelete("/tasks/{taskId:guid}", DeleteProjectTaskAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        projectGroup.MapPost("/tasks/{taskId:guid}/resolve-conflict", ResolveProjectTaskConflictAsync)
            .Accepts<ResolveProjectBoardConflictRequest>("application/json")
            .Produces<ProjectBoardTaskDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        projectGroup.MapPost("/tasks/{taskId:guid}/dispatch", DispatchProjectTaskAsync)
            .Accepts<DispatchProjectBoardTaskRequest>(isOptional: true, contentType: "application/json")
            .Produces<ProjectBoardTaskDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        projectGroup.MapGet("/tasks/{taskId:guid}/diff", GetProjectTaskDiffAsync)
            .Produces<ProjectBoardTaskDiffDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        projectGroup.MapPost("/tasks/{taskId:guid}/approve", ApproveProjectTaskAsync)
            .Accepts<ApproveProjectBoardTaskRequest>(isOptional: true, contentType: "application/json")
            .Produces<ProjectBoardTaskDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        projectGroup.MapPost("/tasks/{taskId:guid}/request-changes", RequestProjectTaskChangesAsync)
            .Accepts<RequestTaskChangesRequest>("application/json")
            .Produces<ProjectBoardTaskDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        projectGroup.MapPost("/tasks/{taskId:guid}/cancel", CancelProjectTaskAsync)
            .Produces<ProjectBoardTaskDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        projectGroup.MapGet("/events", StreamProjectBoardEventsAsync);

        // Phase 3 (#56): Coordinator Smart Task Planner & Autopilot Concurrent Scheduler
        projectGroup.MapPost("/coordinator/plan", PlanCoordinatorTasksAsync)
            .Accepts<CoordinatorPlanRequest>("application/json")
            .Produces<CoordinatorPlanResultDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        projectGroup.MapPost("/coordinator/apply-plan", ApplyCoordinatorPlanAsync)
            .Accepts<CoordinatorApplyPlanRequest>("application/json")
            .Produces<CoordinatorApplyPlanResultDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        projectGroup.MapGet("/coordinator/autopilot", GetProjectAutopilotAsync)
            .Produces<ProjectAutopilotStateDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        projectGroup.MapPut("/coordinator/autopilot", PutProjectAutopilotAsync)
            .Accepts<UpdateProjectAutopilotRequest>("application/json")
            .Produces<ProjectAutopilotStateDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Phase 3 (#56): Project Living Document (notes.md)
        var projectNotesGroup = app.MapGroup("/api/v1/projects/{projectId:guid}")
            .WithTags("ProjectBoard");

        projectNotesGroup.MapGet("/notes", GetProjectNotesAsync)
            .Produces<ProjectNotesDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        projectNotesGroup.MapPut("/notes", PutProjectNotesAsync)
            .Accepts<UpdateProjectNotesRequest>("application/json")
            .Produces<ProjectNotesDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // Cross-project Global Board & Saved Views
        var globalGroup = app.MapGroup("/api/v1/board")
            .WithTags("ProjectBoard");

        globalGroup.MapGet("/tasks", ListGlobalBoardTasksAsync)
            .Produces<IReadOnlyList<ProjectBoardTaskDto>>(StatusCodes.Status200OK);

        globalGroup.MapGet("/summary", GetGlobalBoardSummaryAsync)
            .Produces<ProjectBoardSummaryDto>(StatusCodes.Status200OK);

        globalGroup.MapGet("/events", StreamGlobalBoardEventsAsync);

        globalGroup.MapGet("/views", ListBoardViewsAsync)
            .Produces<IReadOnlyList<ProjectBoardViewDto>>(StatusCodes.Status200OK);

        globalGroup.MapPost("/views", CreateBoardViewAsync)
            .Accepts<CreateProjectBoardViewRequest>("application/json")
            .Produces<ProjectBoardViewDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        globalGroup.MapPatch("/views/{viewId:guid}", PatchBoardViewAsync)
            .Accepts<UpdateProjectBoardViewRequest>("application/json")
            .Produces<ProjectBoardViewDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        globalGroup.MapDelete("/views/{viewId:guid}", DeleteBoardViewAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> GetSettingsAsync(Guid projectId, HttpContext http, CancellationToken ct)
    {
        try
        {
            await using var scope = await ResolveProjectServicesAsync(http, projectId, ct).ConfigureAwait(false);
            if (scope is null) return ProjectNotFound();
            var coordinator = scope.Services.GetRequiredService<ProjectBoardCoordinator>();
            var settings = await coordinator.GetSettingsAsync(projectId, ct).ConfigureAwait(false);
            return Results.Ok(settings);
        }
        catch (KeyNotFoundException) { return ProjectNotFound(); }
    }

    private static async Task<IResult> PutSettingsAsync(Guid projectId, HttpContext http, CancellationToken ct)
    {
        var (request, parseErr) = await ReadRequiredJsonAsync<UpdateProjectBoardSettingsRequest>(http.Request, ct).ConfigureAwait(false);
        if (parseErr is not null) return parseErr;

        try
        {
            await using var scope = await ResolveProjectServicesAsync(http, projectId, ct).ConfigureAwait(false);
            if (scope is null) return ProjectNotFound();
            var coordinator = scope.Services.GetRequiredService<ProjectBoardCoordinator>();
            var updated = await coordinator.UpdateSettingsAsync(projectId, request!, ct).ConfigureAwait(false);
            return Results.Ok(updated);
        }
        catch (KeyNotFoundException) { return ProjectNotFound(); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    private static async Task<IResult> SyncProjectBoardAsync(Guid projectId, HttpContext http, CancellationToken ct)
    {
        try
        {
            await using var scope = await ResolveProjectServicesAsync(http, projectId, ct).ConfigureAwait(false);
            if (scope is null) return ProjectNotFound();
            var syncService = scope.Services.GetRequiredService<ProjectBoardFileSyncService>();
            var result = await syncService.SyncProjectAsync(projectId, ct).ConfigureAwait(false);
            return Results.Ok(result);
        }
        catch (KeyNotFoundException) { return ProjectNotFound(); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    private static async Task<IResult> ListProjectTasksAsync(Guid projectId, HttpContext http, CancellationToken ct)
    {
        try
        {
            await using var scope = await ResolveProjectServicesAsync(http, projectId, ct).ConfigureAwait(false);
            if (scope is null) return ProjectNotFound();

            var coordinator = scope.Services.GetRequiredService<ProjectBoardCoordinator>();
            var execution = scope.Services.GetRequiredService<ProjectTaskExecutionService>();
            var filter = ParseTaskFilter(http.Request.Query);

            var tasks = await coordinator.ListProjectTasksAsync(projectId, filter: null, ct).ConfigureAwait(false);
            tasks = await execution.RefreshTasksRunStateAsync(projectId, tasks, ct).ConfigureAwait(false);
            if (filter is not null)
                tasks = tasks.Where(t => ProjectBoardCoordinator.MatchesFilter(t, filter)).ToList();

            return Results.Ok(tasks);
        }
        catch (KeyNotFoundException) { return ProjectNotFound(); }
    }

    private static async Task<IResult> CreateProjectTaskAsync(Guid projectId, HttpContext http, CancellationToken ct)
    {
        var (request, parseErr) = await ReadRequiredJsonAsync<CreateProjectBoardTaskRequest>(http.Request, ct).ConfigureAwait(false);
        if (parseErr is not null) return parseErr;

        try
        {
            await using var scope = await ResolveProjectServicesAsync(http, projectId, ct).ConfigureAwait(false);
            if (scope is null) return ProjectNotFound();
            var coordinator = scope.Services.GetRequiredService<ProjectBoardCoordinator>();
            var created = await coordinator.CreateTaskAsync(projectId, request!, ct).ConfigureAwait(false);
            return Results.Created($"/api/v1/projects/{projectId}/board/tasks/{created.TaskId}", created);
        }
        catch (KeyNotFoundException) { return ProjectNotFound(); }
        catch (ProjectBoardCycleException ex) { return Results.Conflict(new { code = ex.Code, message = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    private static async Task<IResult> PromoteFromSpatialAsync(Guid projectId, HttpContext http, CancellationToken ct)
    {
        var (request, parseErr) = await ReadRequiredJsonAsync<PromoteFromSpatialRequest>(http.Request, ct).ConfigureAwait(false);
        if (parseErr is not null) return parseErr;

        try
        {
            await using var projectScope = await ResolveProjectServicesAsync(http, projectId, ct).ConfigureAwait(false);
            if (projectScope is null) return ProjectNotFound();

            await using var spatialScope = await ResolveSpatialServicesForSessionAsync(http, projectScope.Services, request!.SessionId, ct).ConfigureAwait(false);
            var boardCoordinator = projectScope.Services.GetRequiredService<ProjectBoardCoordinator>();
            var spatialCoordinator = spatialScope?.Services.GetRequiredService<SpatialBoardCoordinator>()
                ?? projectScope.Services.GetRequiredService<SpatialBoardCoordinator>();

            var result = await boardCoordinator.PromoteFromSpatialAsync(projectId, request, spatialCoordinator, ct).ConfigureAwait(false);
            return Results.Ok(result);
        }
        catch (KeyNotFoundException ex) { return Results.NotFound(new { code = "not_found", message = ex.Message }); }
        catch (ProjectBoardCycleException ex) { return Results.Conflict(new { code = ex.Code, message = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    private static async Task<IResult> GetProjectTaskAsync(Guid projectId, Guid taskId, HttpContext http, CancellationToken ct)
    {
        await using var scope = await ResolveProjectServicesAsync(http, projectId, ct).ConfigureAwait(false);
        if (scope is null) return ProjectNotFound();

        var coordinator = scope.Services.GetRequiredService<ProjectBoardCoordinator>();
        var execution = scope.Services.GetRequiredService<ProjectTaskExecutionService>();

        var task = await coordinator.GetTaskAsync(projectId, taskId, ct).ConfigureAwait(false);
        if (task is null) return TaskNotFound();

        task = await execution.RefreshSingleTaskRunStateAsync(projectId, task, ct).ConfigureAwait(false);
        return Results.Ok(task);
    }

    private static async Task<IResult> PatchProjectTaskAsync(Guid projectId, Guid taskId, HttpContext http, CancellationToken ct)
    {
        var (request, parseErr) = await ReadRequiredJsonAsync<UpdateProjectBoardTaskRequest>(http.Request, ct).ConfigureAwait(false);
        if (parseErr is not null) return parseErr;

        try
        {
            await using var scope = await ResolveProjectServicesAsync(http, projectId, ct).ConfigureAwait(false);
            if (scope is null) return ProjectNotFound();
            var coordinator = scope.Services.GetRequiredService<ProjectBoardCoordinator>();
            var updated = await coordinator.UpdateTaskAsync(projectId, taskId, request!, ct).ConfigureAwait(false);
            if (updated is null) return TaskNotFound();
            return Results.Ok(updated);
        }
        catch (KeyNotFoundException) { return ProjectNotFound(); }
        catch (ProjectBoardCycleException ex) { return Results.Conflict(new { code = ex.Code, message = ex.Message }); }
        catch (ProjectBoardRevisionConflictException ex)
        {
            return Results.Conflict(new { code = ex.Code, message = ex.Message, current_revision = ex.CurrentRevision });
        }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    private static async Task<IResult> DeleteProjectTaskAsync(Guid projectId, Guid taskId, HttpContext http, CancellationToken ct)
    {
        try
        {
            await using var scope = await ResolveProjectServicesAsync(http, projectId, ct).ConfigureAwait(false);
            if (scope is null) return ProjectNotFound();
            var coordinator = scope.Services.GetRequiredService<ProjectBoardCoordinator>();
            var deleted = await coordinator.DeleteTaskAsync(projectId, taskId, ct).ConfigureAwait(false);
            return deleted ? Results.NoContent() : TaskNotFound();
        }
        catch (KeyNotFoundException) { return ProjectNotFound(); }
    }

    private static async Task<IResult> ResolveProjectTaskConflictAsync(Guid projectId, Guid taskId, HttpContext http, CancellationToken ct)
    {
        var (request, parseErr) = await ReadRequiredJsonAsync<ResolveProjectBoardConflictRequest>(http.Request, ct).ConfigureAwait(false);
        if (parseErr is not null) return parseErr;

        try
        {
            await using var scope = await ResolveProjectServicesAsync(http, projectId, ct).ConfigureAwait(false);
            if (scope is null) return ProjectNotFound();
            var syncService = scope.Services.GetRequiredService<ProjectBoardFileSyncService>();
            var resolved = await syncService.ResolveConflictAsync(projectId, taskId, request!, ct).ConfigureAwait(false);
            if (resolved is null) return TaskNotFound();
            return Results.Ok(resolved);
        }
        catch (KeyNotFoundException) { return ProjectNotFound(); }
        catch (ProjectBoardCycleException ex) { return Results.Conflict(new { code = ex.Code, message = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { code = "conflict", message = ex.Message }); }
    }

    private static async Task<IResult> DispatchProjectTaskAsync(Guid projectId, Guid taskId, HttpContext http, CancellationToken ct)
    {
        var (request, parseErr) = await ReadOptionalJsonAsync<DispatchProjectBoardTaskRequest>(http.Request, ct).ConfigureAwait(false);
        if (parseErr is not null) return parseErr;

        try
        {
            await using var scope = await ResolveProjectServicesAsync(http, projectId, ct).ConfigureAwait(false);
            if (scope is null) return ProjectNotFound();
            var execution = scope.Services.GetRequiredService<ProjectTaskExecutionService>();
            var dispatched = await execution.DispatchTaskAsync(projectId, taskId, request, ct).ConfigureAwait(false);
            if (dispatched is null) return TaskNotFound();
            return Results.Ok(dispatched);
        }
        catch (KeyNotFoundException) { return ProjectNotFound(); }
        catch (ProjectBoardConflictStateException ex) { return Results.Conflict(new { code = ex.Code, message = ex.Message }); }
        catch (RunAdmissionException ex) { return Results.Conflict(new { code = ex.Code, message = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    private static async Task<IResult> GetProjectTaskDiffAsync(Guid projectId, Guid taskId, HttpContext http, CancellationToken ct)
    {
        await using var scope = await ResolveProjectServicesAsync(http, projectId, ct).ConfigureAwait(false);
        if (scope is null) return ProjectNotFound();
        var execution = scope.Services.GetRequiredService<ProjectTaskExecutionService>();
        var diff = await execution.GetTaskDiffAsync(projectId, taskId, ct).ConfigureAwait(false);
        return diff is null ? TaskNotFound() : Results.Ok(diff);
    }

    private static async Task<IResult> ApproveProjectTaskAsync(Guid projectId, Guid taskId, HttpContext http, CancellationToken ct)
    {
        var (request, parseErr) = await ReadOptionalJsonAsync<ApproveProjectBoardTaskRequest>(http.Request, ct).ConfigureAwait(false);
        if (parseErr is not null) return parseErr;

        try
        {
            await using var scope = await ResolveProjectServicesAsync(http, projectId, ct).ConfigureAwait(false);
            if (scope is null) return ProjectNotFound();
            var execution = scope.Services.GetRequiredService<ProjectTaskExecutionService>();
            var approved = await execution.ApproveTaskAsync(projectId, taskId, request, ct).ConfigureAwait(false);
            return approved is null ? TaskNotFound() : Results.Ok(approved);
        }
        catch (KeyNotFoundException) { return ProjectNotFound(); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    private static async Task<IResult> RequestProjectTaskChangesAsync(Guid projectId, Guid taskId, HttpContext http, CancellationToken ct)
    {
        var (request, parseErr) = await ReadRequiredJsonAsync<RequestTaskChangesRequest>(http.Request, ct).ConfigureAwait(false);
        if (parseErr is not null) return parseErr;

        try
        {
            await using var scope = await ResolveProjectServicesAsync(http, projectId, ct).ConfigureAwait(false);
            if (scope is null) return ProjectNotFound();
            var execution = scope.Services.GetRequiredService<ProjectTaskExecutionService>();
            var updated = await execution.RequestTaskChangesAsync(projectId, taskId, request!, ct).ConfigureAwait(false);
            return updated is null ? TaskNotFound() : Results.Ok(updated);
        }
        catch (KeyNotFoundException) { return ProjectNotFound(); }
        catch (ProjectBoardConflictStateException ex) { return Results.Conflict(new { code = ex.Code, message = ex.Message }); }
        catch (RunAdmissionException ex) { return Results.Conflict(new { code = ex.Code, message = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    private static async Task<IResult> CancelProjectTaskAsync(Guid projectId, Guid taskId, HttpContext http, CancellationToken ct)
    {
        await using var scope = await ResolveProjectServicesAsync(http, projectId, ct).ConfigureAwait(false);
        if (scope is null) return ProjectNotFound();
        var execution = scope.Services.GetRequiredService<ProjectTaskExecutionService>();
        var cancelled = await execution.CancelTaskExecutionAsync(projectId, taskId, ct).ConfigureAwait(false);
        return cancelled is null ? TaskNotFound() : Results.Ok(cancelled);
    }

    private static async Task StreamProjectBoardEventsAsync(Guid projectId, HttpContext http, CancellationToken ct)
    {
        await using var scope = await ResolveProjectServicesAsync(http, projectId, ct).ConfigureAwait(false);
        if (scope is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            await http.Response.WriteAsJsonAsync(new { code = "not_found", message = "Project was not found." }, ct).ConfigureAwait(false);
            return;
        }

        var coordinator = scope.Services.GetRequiredService<ProjectBoardCoordinator>();
        try
        {
            _ = await coordinator.GetSettingsAsync(projectId, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            await http.Response.WriteAsJsonAsync(new { code = "not_found", message = "Project was not found." }, ct).ConfigureAwait(false);
            return;
        }

        using var sub = coordinator.SubscribeProjectEvents(projectId, out var reader);
        await PumpSseStreamAsync(http, reader, projectId, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Phase 3 (#56): Project Notes, Coordinator Planner & Autopilot Handlers
    // -------------------------------------------------------------------------

    private static async Task<IResult> GetProjectNotesAsync(Guid projectId, HttpContext http, CancellationToken ct)
    {
        try
        {
            await using var scope = await ResolveProjectServicesAsync(http, projectId, ct).ConfigureAwait(false);
            if (scope is null) return ProjectNotFound();
            var syncService = scope.Services.GetRequiredService<ProjectBoardFileSyncService>();
            var notes = await syncService.GetProjectNotesAsync(projectId, ct).ConfigureAwait(false);
            return Results.Ok(notes);
        }
        catch (KeyNotFoundException) { return ProjectNotFound(); }
    }

    private static async Task<IResult> PutProjectNotesAsync(Guid projectId, HttpContext http, CancellationToken ct)
    {
        var (request, parseErr) = await ReadRequiredJsonAsync<UpdateProjectNotesRequest>(http.Request, ct).ConfigureAwait(false);
        if (parseErr is not null) return parseErr;

        try
        {
            await using var scope = await ResolveProjectServicesAsync(http, projectId, ct).ConfigureAwait(false);
            if (scope is null) return ProjectNotFound();
            var syncService = scope.Services.GetRequiredService<ProjectBoardFileSyncService>();
            var updated = await syncService.UpdateProjectNotesAsync(projectId, request!, ct).ConfigureAwait(false);
            return Results.Ok(updated);
        }
        catch (KeyNotFoundException) { return ProjectNotFound(); }
        catch (ProjectBoardRevisionConflictException ex)
        {
            return Results.Conflict(new { code = ex.Code, message = ex.Message, current_revision = ex.CurrentRevision });
        }
        catch (ProjectBoardConflictException ex)
        {
            return Results.Conflict(new { code = ex.Code, message = ex.Message, current_revision = ex.CurrentRevision });
        }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    private static async Task<IResult> PlanCoordinatorTasksAsync(Guid projectId, HttpContext http, CancellationToken ct)
    {
        var (request, parseErr) = await ReadRequiredJsonAsync<CoordinatorPlanRequest>(http.Request, ct).ConfigureAwait(false);
        if (parseErr is not null) return parseErr;

        try
        {
            await using var scope = await ResolveProjectServicesAsync(http, projectId, ct).ConfigureAwait(false);
            if (scope is null) return ProjectNotFound();
            var planner = scope.Services.GetRequiredService<IProjectCoordinatorPlanner>();
            var result = await planner.PlanAsync(projectId, request!, ct).ConfigureAwait(false);
            return Results.Ok(result);
        }
        catch (KeyNotFoundException) { return ProjectNotFound(); }
        catch (ProjectBoardCycleException ex) { return Results.Conflict(new { code = ex.Code, message = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    private static async Task<IResult> ApplyCoordinatorPlanAsync(Guid projectId, HttpContext http, CancellationToken ct)
    {
        var (request, parseErr) = await ReadRequiredJsonAsync<CoordinatorApplyPlanRequest>(http.Request, ct).ConfigureAwait(false);
        if (parseErr is not null) return parseErr;

        try
        {
            await using var scope = await ResolveProjectServicesAsync(http, projectId, ct).ConfigureAwait(false);
            if (scope is null) return ProjectNotFound();
            var planner = scope.Services.GetRequiredService<IProjectCoordinatorPlanner>();
            var result = await planner.ApplyPlanAsync(projectId, request!, ct).ConfigureAwait(false);
            return Results.Ok(result);
        }
        catch (KeyNotFoundException) { return ProjectNotFound(); }
        catch (ProjectBoardCycleException ex) { return Results.Conflict(new { code = ex.Code, message = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    private static async Task<IResult> GetProjectAutopilotAsync(Guid projectId, HttpContext http, CancellationToken ct)
    {
        try
        {
            await using var scope = await ResolveProjectServicesAsync(http, projectId, ct).ConfigureAwait(false);
            if (scope is null) return ProjectNotFound();
            var autopilot = scope.Services.GetRequiredService<ProjectAutopilotService>();
            var state = await autopilot.GetAutopilotStateAsync(projectId, ct).ConfigureAwait(false);
            return Results.Ok(state);
        }
        catch (KeyNotFoundException) { return ProjectNotFound(); }
    }

    private static async Task<IResult> PutProjectAutopilotAsync(Guid projectId, HttpContext http, CancellationToken ct)
    {
        var (request, parseErr) = await ReadRequiredJsonAsync<UpdateProjectAutopilotRequest>(http.Request, ct).ConfigureAwait(false);
        if (parseErr is not null) return parseErr;

        try
        {
            await using var scope = await ResolveProjectServicesAsync(http, projectId, ct).ConfigureAwait(false);
            if (scope is null) return ProjectNotFound();
            var autopilot = scope.Services.GetRequiredService<ProjectAutopilotService>();
            var state = await autopilot.UpdateAutopilotAsync(projectId, request!, ct).ConfigureAwait(false);
            return Results.Ok(state);
        }
        catch (KeyNotFoundException) { return ProjectNotFound(); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    // -------------------------------------------------------------------------
    // Global Board & Saved Views Handlers
    // -------------------------------------------------------------------------

    private static async Task<IResult> ListGlobalBoardTasksAsync(HttpContext http, CancellationToken ct)
    {
        var filter = ParseTaskFilter(http.Request.Query);
        var (_, allTasks) = await CollectGlobalProjectsAndTasksAsync(http, filter, ct).ConfigureAwait(false);
        return Results.Ok(allTasks);
    }

    private static async Task<IResult> GetGlobalBoardSummaryAsync(HttpContext http, CancellationToken ct)
    {
        var (projects, allTasks) = await CollectGlobalProjectsAndTasksAsync(http, filter: null, ct).ConfigureAwait(false);
        await using var hostScope = await ResolveHostServicesAsync(http, ct).ConfigureAwait(false);
        var coordinator = hostScope.Services.GetRequiredService<ProjectBoardCoordinator>();
        var summary = coordinator.BuildSummary(projects, allTasks);
        return Results.Ok(summary);
    }

    private static async Task StreamGlobalBoardEventsAsync(HttpContext http, CancellationToken ct)
    {
        await using var hostScope = await ResolveHostServicesAsync(http, ct).ConfigureAwait(false);
        var coordinator = hostScope.Services.GetRequiredService<ProjectBoardCoordinator>();
        using var sub = coordinator.SubscribeGlobalEvents(out var reader);
        await PumpSseStreamAsync(http, reader, projectId: null, ct).ConfigureAwait(false);
    }

    private static async Task<IResult> ListBoardViewsAsync(
        string? scope,
        Guid? project_id,
        Guid? projectId,
        HttpContext http,
        CancellationToken ct)
    {
        await using var hostScope = await ResolveHostServicesAsync(http, ct).ConfigureAwait(false);
        var coordinator = hostScope.Services.GetRequiredService<ProjectBoardCoordinator>();
        var views = await coordinator.ListViewsAsync(scope, project_id ?? projectId, ct).ConfigureAwait(false);
        return Results.Ok(views);
    }

    private static async Task<IResult> CreateBoardViewAsync(HttpContext http, CancellationToken ct)
    {
        var (request, parseErr) = await ReadRequiredJsonAsync<CreateProjectBoardViewRequest>(http.Request, ct).ConfigureAwait(false);
        if (parseErr is not null) return parseErr;

        try
        {
            await using var hostScope = await ResolveHostServicesAsync(http, ct).ConfigureAwait(false);
            var coordinator = hostScope.Services.GetRequiredService<ProjectBoardCoordinator>();
            var created = await coordinator.CreateViewAsync(request!, ct).ConfigureAwait(false);
            return Results.Created($"/api/v1/board/views/{created.ViewId}", created);
        }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    private static async Task<IResult> PatchBoardViewAsync(Guid viewId, HttpContext http, CancellationToken ct)
    {
        var (request, parseErr) = await ReadRequiredJsonAsync<UpdateProjectBoardViewRequest>(http.Request, ct).ConfigureAwait(false);
        if (parseErr is not null) return parseErr;

        try
        {
            await using var hostScope = await ResolveHostServicesAsync(http, ct).ConfigureAwait(false);
            var coordinator = hostScope.Services.GetRequiredService<ProjectBoardCoordinator>();
            var updated = await coordinator.UpdateViewAsync(viewId, request!, ct).ConfigureAwait(false);
            return updated is null
                ? Results.NotFound(new { code = "not_found", message = "Board view was not found." })
                : Results.Ok(updated);
        }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    private static async Task<IResult> DeleteBoardViewAsync(Guid viewId, HttpContext http, CancellationToken ct)
    {
        await using var hostScope = await ResolveHostServicesAsync(http, ct).ConfigureAwait(false);
        var coordinator = hostScope.Services.GetRequiredService<ProjectBoardCoordinator>();
        var deleted = await coordinator.DeleteViewAsync(viewId, ct).ConfigureAwait(false);
        return deleted
            ? Results.NoContent()
            : Results.NotFound(new { code = "not_found", message = "Board view was not found." });
    }

    // -------------------------------------------------------------------------
    // Scope Resolution, SSE Pump & Query Parsing Helpers
    // -------------------------------------------------------------------------

    private sealed class ResolvedServicesHandle(IServiceProvider services, IAsyncDisposable? lease = null) : IAsyncDisposable
    {
        public IServiceProvider Services { get; } = services;
        public ValueTask DisposeAsync() => lease?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private static async Task<ResolvedServicesHandle?> ResolveProjectServicesAsync(
        HttpContext http,
        Guid projectId,
        CancellationToken ct)
    {
        var currentLocations = http.RequestServices.GetService<IScopeStorageLocations>();
        if (currentLocations is StorageScopeDescriptor { ProjectId: { } currentProjId } && currentProjId == projectId)
            return new ResolvedServicesHandle(http.RequestServices);

        if (http.RequestServices.GetService<IStorageScopeRegistry>() is { } registry)
        {
            var matchedScope = registry.List().FirstOrDefault(s => s.ScopeKind == "project" && s.ProjectId == projectId);
            if (matchedScope is not null)
            {
                var lease = await registry.AcquireAsync(matchedScope.StorageId, ct).ConfigureAwait(false);
                return new ResolvedServicesHandle(lease.Services, lease);
            }
        }

        var localStore = http.RequestServices.GetRequiredService<ProjectSessionStore>();
        var localProject = await localStore.GetProjectAnyStatusAsync(projectId, ct).ConfigureAwait(false);
        if (localProject is null || localProject.LifecycleStatus != LifecycleStatuses.Active)
            return null;

        return new ResolvedServicesHandle(http.RequestServices);
    }

    private static async Task<ResolvedServicesHandle?> ResolveSpatialServicesForSessionAsync(
        HttpContext http,
        IServiceProvider projectServices,
        Guid sessionId,
        CancellationToken ct)
    {
        if (sessionId == Guid.Empty)
            return null;

        static async Task<bool> HasSessionOrObjectsAsync(IServiceProvider sp, Guid sid, CancellationToken token)
        {
            if (sp.GetService<ISessionLocator>() is { } locator
                && await locator.FindAsync(sid, token).ConfigureAwait(false) is not null)
            {
                return true;
            }

            if (sp.GetService<SpatialBoardCoordinator>() is { } spatial)
            {
                var snap = await spatial.GetSnapshotAsync(sid, includeDeleted: false, token).ConfigureAwait(false);
                if (snap.Objects.Count > 0)
                    return true;
            }

            return false;
        }

        if (await HasSessionOrObjectsAsync(projectServices, sessionId, ct).ConfigureAwait(false))
            return new ResolvedServicesHandle(projectServices);

        if (!ReferenceEquals(http.RequestServices, projectServices)
            && await HasSessionOrObjectsAsync(http.RequestServices, sessionId, ct).ConfigureAwait(false))
        {
            return new ResolvedServicesHandle(http.RequestServices);
        }

        if (http.RequestServices.GetService<IStorageScopeRegistry>() is { } registry)
        {
            foreach (var scope in registry.List())
            {
                try
                {
                    var lease = await registry.AcquireAsync(scope.StorageId, ct).ConfigureAwait(false);
                    try
                    {
                        if (await HasSessionOrObjectsAsync(lease.Services, sessionId, ct).ConfigureAwait(false))
                        {
                            return new ResolvedServicesHandle(lease.Services, lease);
                        }
                    }
                    catch
                    {
                        await lease.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }

                    await lease.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && StorageScopeRowError.CanRepresent(ex))
                {
                    // Skip unavailable scopes during cross-scope session lookup.
                }
            }
        }

        return null;
    }

    private static async Task<ResolvedServicesHandle> ResolveHostServicesAsync(HttpContext http, CancellationToken ct)
    {
        var currentLocations = http.RequestServices.GetService<IScopeStorageLocations>();
        if (http.RequestServices.GetService<IStorageScopeRegistry>() is { } registry
            && currentLocations is not null
            && currentLocations.StorageId != "user")
        {
            var lease = await registry.AcquireAsync("user", ct).ConfigureAwait(false);
            return new ResolvedServicesHandle(lease.Services, lease);
        }

        return new ResolvedServicesHandle(http.RequestServices);
    }

    private static async Task<(List<(Guid ProjectId, string ProjectName)> Projects, List<ProjectBoardTaskDto> Tasks)> CollectGlobalProjectsAndTasksAsync(
        HttpContext http,
        ProjectBoardCoordinator.TaskQueryFilter? filter,
        CancellationToken ct)
    {
        var seenProjects = new HashSet<Guid>();
        var activeProjects = new List<(Guid ProjectId, string ProjectName)>();
        var allTasks = new List<ProjectBoardTaskDto>();

        async Task CollectFromProviderAsync(IServiceProvider sp)
        {
            var store = sp.GetRequiredService<ProjectSessionStore>();
            var coordinator = sp.GetRequiredService<ProjectBoardCoordinator>();
            var execution = sp.GetRequiredService<ProjectTaskExecutionService>();
            var projects = await store.ListProjectsAsync(LifecycleStatuses.Active, ct).ConfigureAwait(false);

            foreach (var proj in projects)
            {
                if (!seenProjects.Add(proj.Id)) continue;
                activeProjects.Add((proj.Id, proj.Name));

                if (filter?.ProjectIds is { Count: > 0 } && !filter.ProjectIds.Contains(proj.Id))
                    continue;

                var projTasks = await coordinator.ListProjectTasksAsync(proj.Id, filter: null, ct).ConfigureAwait(false);
                projTasks = await execution.RefreshTasksRunStateAsync(proj.Id, projTasks, ct).ConfigureAwait(false);
                if (filter is not null)
                    projTasks = projTasks.Where(t => ProjectBoardCoordinator.MatchesFilter(t, filter)).ToList();

                allTasks.AddRange(projTasks);
            }
        }

        if (http.RequestServices.GetService<IStorageScopeRegistry>() is { } registry)
        {
            foreach (var scope in registry.List().Where(s => s.ScopeKind == "project"))
            {
                try
                {
                    await using var lease = await registry.AcquireAsync(scope.StorageId, ct).ConfigureAwait(false);
                    await CollectFromProviderAsync(lease.Services).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && StorageScopeRowError.CanRepresent(ex))
                {
                    // Skip unavailable scopes so global board stays responsive.
                }
            }

            await using var userLease = await registry.AcquireAsync("user", ct).ConfigureAwait(false);
            await CollectFromProviderAsync(userLease.Services).ConfigureAwait(false);
        }
        else
        {
            await CollectFromProviderAsync(http.RequestServices).ConfigureAwait(false);
        }

        var orderedTasks = allTasks
            .OrderBy(t => t.SortKey, StringComparer.Ordinal)
            .ThenByDescending(t => t.UpdatedAtUnixMs)
            .ToList();

        return (activeProjects, orderedTasks);
    }

    private static async Task PumpSseStreamAsync(
        HttpContext http,
        System.Threading.Channels.ChannelReader<ProjectBoardEventDto> reader,
        Guid? projectId,
        CancellationToken ct)
    {
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";

        var linkedAbort = CancellationTokenSource.CreateLinkedTokenSource(ct, http.RequestAborted);
        using (linkedAbort)
        {
            var token = linkedAbort.Token;
            try
            {
                var initFrame = new ProjectBoardEventDto
                {
                    Seq = 0,
                    EventType = "snapshot",
                    ProjectId = projectId,
                    OccurredAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                var initJson = JsonSerializer.Serialize(initFrame, WireJson);
                await http.Response.WriteAsync($"id: 0\nevent: snapshot\ndata: {initJson}\n\n", token).ConfigureAwait(false);
                await http.Response.Body.FlushAsync(token).ConfigureAwait(false);

                while (!token.IsCancellationRequested)
                {
                    using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    heartbeatCts.CancelAfter(SseHeartbeatInterval);

                    try
                    {
                        if (await reader.WaitToReadAsync(heartbeatCts.Token).ConfigureAwait(false))
                        {
                            while (reader.TryRead(out var evt))
                            {
                                var json = JsonSerializer.Serialize(evt, WireJson);
                                await http.Response.WriteAsync($"id: {evt.Seq}\nevent: {evt.EventType}\ndata: {json}\n\n", token).ConfigureAwait(false);
                            }
                            await http.Response.Body.FlushAsync(token).ConfigureAwait(false);
                        }
                        else
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        await http.Response.WriteAsync(": heartbeat\n\n", token).ConfigureAwait(false);
                        await http.Response.Body.FlushAsync(token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Normal SSE client disconnect.
            }
        }
    }

    private static ProjectBoardCoordinator.TaskQueryFilter? ParseTaskFilter(IQueryCollection query)
    {
        if (query.Count == 0) return null;

        var projectIds = ParseGuidValues(query, "project_id", "project_ids", "projectId", "projectIds");
        var statuses = ParseStringValues(query, "status", "statuses");
        var priorities = ParseStringValues(query, "priority", "priorities");
        var assigneeKinds = ParseStringValues(query, "assignee_kind", "assignee_kinds", "assigneeKind", "assigneeKinds");
        var labels = ParseStringValues(query, "label", "labels");
        var needsAction = ParseBoolValue(query, "needs_my_action", "needsMyAction");
        var hasConflict = ParseBoolValue(query, "has_conflict", "hasConflict");
        var search = FirstNonEmpty(query, "query", "q");

        if (projectIds is null && statuses is null && priorities is null && assigneeKinds is null
            && labels is null && needsAction is null && hasConflict is null && search is null)
        {
            return null;
        }

        return new ProjectBoardCoordinator.TaskQueryFilter
        {
            ProjectIds = projectIds,
            Statuses = statuses,
            Priorities = priorities,
            AssigneeKinds = assigneeKinds,
            Labels = labels,
            NeedsMyAction = needsAction,
            HasConflict = hasConflict,
            Query = search
        };
    }

    private static List<Guid>? ParseGuidValues(IQueryCollection query, params string[] keys)
    {
        var list = new List<Guid>();
        foreach (var key in keys)
        {
            if (!query.TryGetValue(key, out var values)) continue;
            foreach (var raw in values)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (Guid.TryParse(token, out var g) && g != Guid.Empty && !list.Contains(g))
                        list.Add(g);
                }
            }
        }
        return list.Count > 0 ? list : null;
    }

    private static List<string>? ParseStringValues(IQueryCollection query, params string[] keys)
    {
        var list = new List<string>();
        foreach (var key in keys)
        {
            if (!query.TryGetValue(key, out var values)) continue;
            foreach (var raw in values)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!list.Contains(token, StringComparer.OrdinalIgnoreCase))
                        list.Add(token);
                }
            }
        }
        return list.Count > 0 ? list : null;
    }

    private static bool? ParseBoolValue(IQueryCollection query, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (query.TryGetValue(key, out var val) && bool.TryParse(val.ToString(), out var parsed))
                return parsed;
        }
        return null;
    }

    private static string? FirstNonEmpty(IQueryCollection query, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (query.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val.ToString()))
                return val.ToString().Trim();
        }
        return null;
    }

    private static async Task<(T? Value, IResult? Error)> ReadRequiredJsonAsync<T>(HttpRequest request, CancellationToken ct) where T : class
    {
        try
        {
            var value = await JsonSerializer.DeserializeAsync<T>(request.Body, WireJson, ct).ConfigureAwait(false);
            if (value is null)
                return (null, BadRequest("Request body must be a JSON object."));
            return (value, null);
        }
        catch (JsonException)
        {
            return (null, Results.BadRequest(new { code = "invalid_json", message = "Request body must be valid JSON." }));
        }
    }

    private static async Task<(T Value, IResult? Error)> ReadOptionalJsonAsync<T>(HttpRequest request, CancellationToken ct) where T : class, new()
    {
        if (request.ContentLength == 0)
            return (new T(), null);

        using var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var raw = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
            return (new T(), null);

        try
        {
            var parsed = JsonSerializer.Deserialize<T>(raw, WireJson);
            return (parsed ?? new T(), null);
        }
        catch (JsonException)
        {
            return (new T(), Results.BadRequest(new { code = "invalid_json", message = "Request body must be valid JSON." }));
        }
    }

    private static IResult ProjectNotFound() =>
        Results.NotFound(new { code = "not_found", message = "Project was not found." });

    private static IResult TaskNotFound() =>
        Results.NotFound(new { code = "not_found", message = "Board task was not found." });

    private static IResult BadRequest(string message) =>
        Results.BadRequest(new { code = "invalid_request", message });
}
