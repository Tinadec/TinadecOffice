using System.Text.Json;
using System.Text.Json.Serialization;
using TinadecCore.Companion;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// Companion domain HTTP surface: identity/profile, mandates, recruitments, memories, skills,
/// connections, schedules/plans/todos, settings, BYO Live2D Cubism Core SDK &amp; pure-data model assets.
/// </summary>
public static class CompanionEndpoints
{
    public static IEndpointRouteBuilder MapCompanionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/companions", ListCompanions);
        app.MapPost("/api/v1/companions", CreateCompanion);
        app.MapGet("/api/v1/companions/browser-profiles", ListBrowserProfiles);
        app.MapGet("/api/v1/companions/{companionId:guid}", GetCompanion);
        app.MapPatch("/api/v1/companions/{companionId:guid}", PatchCompanion);
        app.MapPost("/api/v1/companions/{companionId:guid}/archive", ArchiveCompanion);
        app.MapPost("/api/v1/companions/{companionId:guid}/autonomy/tick", TickAutonomy);
        app.MapGet("/api/v1/companions/{companionId:guid}/work-sessions", ListWorkSessions);

        app.MapGet("/api/v1/companions/{companionId:guid}/mandates", ListMandates);
        app.MapPost("/api/v1/companions/{companionId:guid}/mandates/{mandateId:guid}/decision", DecideMandate);

        app.MapPost("/api/v1/companions/{companionId:guid}/emote", RecordEmote);

        app.MapGet("/api/v1/companion-recruitments", ListRecruitments);
        app.MapPost("/api/v1/companion-recruitments/{recruitmentId:guid}/decision", DecideRecruitment);

        app.MapGet("/api/v1/companions/{companionId:guid}/memories", ListMemories);
        app.MapPost("/api/v1/companions/{companionId:guid}/memories", CreateMemory);
        app.MapPatch("/api/v1/companions/{companionId:guid}/memories/{memoryId:guid}", PatchMemory);
        app.MapDelete("/api/v1/companions/{companionId:guid}/memories/{memoryId:guid}", DeleteMemory);

        app.MapGet("/api/v1/companions/{companionId:guid}/skills", ListSkills);
        app.MapPost("/api/v1/companions/{companionId:guid}/skills", CreateSkill);
        app.MapPatch("/api/v1/companions/{companionId:guid}/skills/{bindingId:guid}", PatchSkill);
        app.MapDelete("/api/v1/companions/{companionId:guid}/skills/{bindingId:guid}", DeleteSkill);

        app.MapGet("/api/v1/companions/{companionId:guid}/connections", ListConnections);
        app.MapPost("/api/v1/companions/{companionId:guid}/connections", CreateConnection);
        app.MapPatch("/api/v1/companions/{companionId:guid}/connections/{bindingId:guid}", PatchConnection);
        app.MapDelete("/api/v1/companions/{companionId:guid}/connections/{bindingId:guid}", DeleteConnection);

        app.MapGet("/api/v1/companions/{companionId:guid}/schedules", ListSchedules);
        app.MapPost("/api/v1/companions/{companionId:guid}/schedules", CreateSchedule);
        app.MapPatch("/api/v1/companions/{companionId:guid}/schedules/{scheduleId:guid}", PatchSchedule);
        app.MapDelete("/api/v1/companions/{companionId:guid}/schedules/{scheduleId:guid}", DeleteSchedule);
        app.MapPost("/api/v1/companions/{companionId:guid}/schedules/{scheduleId:guid}/trigger", TriggerSchedule);

        app.MapGet("/api/v1/companions/{companionId:guid}/plans", ListPlans);
        app.MapPost("/api/v1/companions/{companionId:guid}/plans", CreatePlan);
        app.MapPatch("/api/v1/companions/{companionId:guid}/plans/{planId:guid}", PatchPlan);
        app.MapDelete("/api/v1/companions/{companionId:guid}/plans/{planId:guid}", DeletePlan);

        app.MapGet("/api/v1/companions/{companionId:guid}/todos", ListTodos);
        app.MapPost("/api/v1/companions/{companionId:guid}/todos", CreateTodo);
        app.MapPatch("/api/v1/companions/{companionId:guid}/todos/{todoId:guid}", PatchTodo);
        app.MapDelete("/api/v1/companions/{companionId:guid}/todos/{todoId:guid}", DeleteTodo);

        app.MapGet("/api/v1/companion-settings", GetSettings);
        app.MapPatch("/api/v1/companion-settings", PatchSettings);

        // BYO Live2D Cubism Core SDK & model registry (static routes registered before {avatarId:guid}).
        app.MapGet("/api/v1/companion-avatars/sdk", GetCompanionAvatarSdk);
        app.MapPost("/api/v1/companion-avatars/sdk", ImportCompanionAvatarSdk);
        app.MapPost("/api/v1/companion-avatars/sdk/import", ImportCompanionAvatarSdk);
        app.MapDelete("/api/v1/companion-avatars/sdk", DeleteCompanionAvatarSdk);
        app.MapGet("/api/v1/companion-avatars/sdk/{asset}", ServeCompanionAvatarSdkAsset);

        app.MapGet("/api/v1/companion-avatars", ListCompanionAvatars);
        app.MapPost("/api/v1/companion-avatars/import", ImportCompanionAvatar);
        app.MapGet("/api/v1/companion-avatars/{avatarId:guid}/assets/{**relativePath}", ServeCompanionAvatarAsset);
        app.MapDelete("/api/v1/companion-avatars/{avatarId:guid}", DeleteCompanionAvatar);

        app.MapCompanionConversationEndpoints();
        app.MapCompanionEventEndpoints();

        return app;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    static long IfMatch(HttpRequest request) =>
        long.TryParse(request.Headers.IfMatch.FirstOrDefault()?.Trim('"', 'W', '/', ' '), out var value) ? value : -1;

    static IResult Ok(object body) => Results.Json(body, JsonOptions);

    static IResult NotFound(string code = "companion_not_found", string message = "Companion not found.") =>
        Results.NotFound(new { code, message });

    static IResult Error(CompanionException error) =>
        Results.Json(new { code = error.Code, message = error.Message }, statusCode: error.StatusCode);

    static async Task<IResult> Run(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (CompanionException error)
        {
            return Error(error);
        }
    }

    static Task<IResult> ListCompanions(
        CompanionService service,
        bool? include_archived = null,
        CancellationToken cancellationToken = default) =>
        Run(async () => Ok(await service.ListAsync(include_archived ?? false, cancellationToken).ConfigureAwait(false)));

    static Task<IResult> CreateCompanion(
        CompanionCreateRequest? input,
        CompanionService service,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            if (input is null) throw new CompanionException("invalid_request", "A companion body is required.", 400);
            return Ok(await service.CreateAsync(input, cancellationToken).ConfigureAwait(false));
        });

    static Task<IResult> GetCompanion(
        Guid companionId,
        CompanionService service,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            var body = await service.GetAsync(companionId, cancellationToken).ConfigureAwait(false);
            return body is null ? NotFound() : Ok(body);
        });

    static Task<IResult> PatchCompanion(
        Guid companionId,
        CompanionPatchRequest? input,
        HttpRequest request,
        CompanionService service,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            if (input is null) throw new CompanionException("invalid_request", "A companion body is required.", 400);
            var body = await service.PatchAsync(companionId, IfMatch(request), input, cancellationToken).ConfigureAwait(false);
            return body is null ? NotFound() : Ok(body);
        });

    static Task<IResult> ArchiveCompanion(
        Guid companionId,
        HttpRequest request,
        CompanionService service,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            var body = await service.ArchiveAsync(companionId, IfMatch(request), cancellationToken).ConfigureAwait(false);
            return body is null ? NotFound() : Ok(body);
        });

    static Task<IResult> TickAutonomy(
        Guid companionId,
        CompanionAutonomyService service,
        CancellationToken cancellationToken = default) =>
        Run(async () => Ok(await service.TickAsync(companionId, cancellationToken).ConfigureAwait(false)));

    static IResult ListBrowserProfiles(CompanionService service) =>
        Ok(service.DiscoverBrowserProfiles());

    static Task<IResult> ListWorkSessions(
        Guid companionId,
        CompanionService service,
        Guid? project_id = null,
        CancellationToken cancellationToken = default) =>
        Run(async () => Ok(await service.ListWorkSessionsAsync(companionId, project_id, cancellationToken).ConfigureAwait(false)));

    static Task<IResult> RecordEmote(
        Guid companionId,
        CompanionEmoteRequest? input,
        CompanionService service,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            if (input is null) throw new CompanionException("invalid_request", "An emote body is required.", 400);
            return Ok(await service.RecordEmoteAsync(companionId, input.Emote, cancellationToken).ConfigureAwait(false));
        });

    static Task<IResult> ListRecruitments(
        CompanionService service,
        CancellationToken cancellationToken = default) =>
        Run(async () => Ok(await service.ListRecruitmentsAsync(cancellationToken).ConfigureAwait(false)));

    static Task<IResult> DecideRecruitment(
        Guid recruitmentId,
        CompanionMandateDecisionRequest? input,
        HttpRequest request,
        CompanionService service,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            if (input is null) throw new CompanionException("invalid_request", "A decision body is required.", 400);
            var body = await service.DecideRecruitmentAsync(
                recruitmentId, IfMatch(request), input.Decision, cancellationToken).ConfigureAwait(false);
            return body is null ? NotFound() : Ok(body);
        });

    static Task<IResult> ListMandates(
        Guid companionId,
        CompanionService service,
        CancellationToken cancellationToken = default) =>
        Run(async () => Ok(await service.ListMandatesAsync(companionId, cancellationToken).ConfigureAwait(false)));

    static Task<IResult> DecideMandate(
        Guid companionId,
        Guid mandateId,
        CompanionMandateDecisionRequest? input,
        HttpRequest request,
        CompanionService service,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            if (input is null) throw new CompanionException("invalid_request", "A decision body is required.", 400);
            var body = await service.DecideMandateAsync(
                companionId, mandateId, IfMatch(request), input.Decision, cancellationToken).ConfigureAwait(false);
            return body is null ? NotFound() : Ok(body);
        });

    static Task<IResult> ListMemories(
        Guid companionId,
        CompanionService service,
        string? q = null,
        int? limit = null,
        CancellationToken cancellationToken = default) =>
        Run(async () => Ok(await service.ListMemoriesAsync(companionId, q, limit, cancellationToken).ConfigureAwait(false)));

    static Task<IResult> CreateMemory(
        Guid companionId,
        CompanionMemoryCreateRequest? input,
        CompanionService service,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            if (input is null) throw new CompanionException("invalid_request", "A memory body is required.", 400);
            return Ok(await service.CreateMemoryAsync(companionId, input, cancellationToken).ConfigureAwait(false));
        });

    static Task<IResult> PatchMemory(
        Guid companionId,
        Guid memoryId,
        CompanionMemoryPatchRequest? input,
        HttpRequest request,
        CompanionService service,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            if (input is null) throw new CompanionException("invalid_request", "A memory body is required.", 400);
            var body = await service.PatchMemoryAsync(companionId, memoryId, IfMatch(request), input, cancellationToken).ConfigureAwait(false);
            return Ok(body);
        });

    static Task<IResult> DeleteMemory(
        Guid companionId,
        Guid memoryId,
        HttpRequest request,
        CompanionService service,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            var deleted = await service.DeleteMemoryAsync(companionId, memoryId, IfMatch(request), cancellationToken).ConfigureAwait(false);
            return deleted ? Results.NoContent() : NotFound();
        });

    static Task<IResult> ListSkills(
        Guid companionId,
        CompanionService service,
        CancellationToken cancellationToken = default) =>
        Run(async () => Ok(await service.ListSkillsAsync(companionId, cancellationToken).ConfigureAwait(false)));

    static Task<IResult> CreateSkill(
        Guid companionId,
        CompanionSkillCreateRequest? input,
        CompanionService service,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            if (input is null) throw new CompanionException("invalid_request", "A skill body is required.", 400);
            return Ok(await service.CreateSkillAsync(companionId, input, cancellationToken).ConfigureAwait(false));
        });

    static Task<IResult> PatchSkill(
        Guid companionId,
        Guid bindingId,
        CompanionSkillPatchRequest? input,
        HttpRequest request,
        CompanionService service,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            if (input is null) throw new CompanionException("invalid_request", "A skill body is required.", 400);
            var body = await service.PatchSkillAsync(companionId, bindingId, IfMatch(request), input, cancellationToken).ConfigureAwait(false);
            return Ok(body);
        });

    static Task<IResult> DeleteSkill(
        Guid companionId,
        Guid bindingId,
        HttpRequest request,
        CompanionService service,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            var deleted = await service.DeleteSkillAsync(companionId, bindingId, IfMatch(request), cancellationToken).ConfigureAwait(false);
            return deleted ? Results.NoContent() : NotFound();
        });

    static Task<IResult> ListConnections(
        Guid companionId,
        CompanionService service,
        CancellationToken cancellationToken = default) =>
        Run(async () => Ok(await service.ListConnectionsAsync(companionId, cancellationToken).ConfigureAwait(false)));

    static Task<IResult> CreateConnection(
        Guid companionId,
        CompanionConnectionCreateRequest? input,
        CompanionService service,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            if (input is null) throw new CompanionException("invalid_request", "A connection body is required.", 400);
            return Ok(await service.CreateConnectionAsync(companionId, input, cancellationToken).ConfigureAwait(false));
        });

    static Task<IResult> PatchConnection(
        Guid companionId,
        Guid bindingId,
        CompanionConnectionPatchRequest? input,
        HttpRequest request,
        CompanionService service,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            if (input is null) throw new CompanionException("invalid_request", "A connection body is required.", 400);
            var body = await service.PatchConnectionAsync(companionId, bindingId, IfMatch(request), input, cancellationToken).ConfigureAwait(false);
            return Ok(body);
        });

    static Task<IResult> DeleteConnection(
        Guid companionId,
        Guid bindingId,
        HttpRequest request,
        CompanionService service,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            var deleted = await service.DeleteConnectionAsync(companionId, bindingId, IfMatch(request), cancellationToken).ConfigureAwait(false);
            return deleted ? Results.NoContent() : NotFound();
        });

    // ── schedules / plans / todos ──

    static Task<IResult> ListSchedules(
        Guid companionId,
        CompanionPlanningService planning,
        CancellationToken cancellationToken = default) =>
        Run(async () => Ok(await planning.ListSchedulesAsync(companionId, cancellationToken).ConfigureAwait(false)));

    static Task<IResult> CreateSchedule(
        Guid companionId,
        CompanionScheduleCreateRequest? input,
        CompanionPlanningService planning,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            if (input is null) throw new CompanionException("invalid_request", "A schedule body is required.", 400);
            return Ok(await planning.CreateScheduleAsync(companionId, input, cancellationToken).ConfigureAwait(false));
        });

    static Task<IResult> PatchSchedule(
        Guid companionId,
        Guid scheduleId,
        CompanionSchedulePatchRequest? input,
        HttpRequest request,
        CompanionPlanningService planning,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            if (input is null) throw new CompanionException("invalid_request", "A schedule body is required.", 400);
            var body = await planning.PatchScheduleAsync(companionId, scheduleId, IfMatch(request), input, cancellationToken).ConfigureAwait(false);
            return body is null ? NotFound("companion_schedule_not_found", "Schedule not found.") : Ok(body);
        });

    static Task<IResult> DeleteSchedule(
        Guid companionId,
        Guid scheduleId,
        HttpRequest request,
        CompanionPlanningService planning,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            var deleted = await planning.DeleteScheduleAsync(companionId, scheduleId, IfMatch(request), cancellationToken).ConfigureAwait(false);
            return deleted ? Results.NoContent() : NotFound("companion_schedule_not_found", "Schedule not found.");
        });

    static Task<IResult> TriggerSchedule(
        Guid companionId,
        Guid scheduleId,
        CompanionPlanningService planning,
        CancellationToken cancellationToken = default) =>
        Run(async () => Ok(await planning.TriggerScheduleAsync(companionId, scheduleId, cancellationToken).ConfigureAwait(false)));

    static Task<IResult> ListPlans(
        Guid companionId,
        CompanionPlanningService planning,
        CancellationToken cancellationToken = default) =>
        Run(async () => Ok(await planning.ListPlansAsync(companionId, cancellationToken).ConfigureAwait(false)));

    static Task<IResult> CreatePlan(
        Guid companionId,
        CompanionPlanCreateRequest? input,
        CompanionPlanningService planning,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            if (input is null) throw new CompanionException("invalid_request", "A plan body is required.", 400);
            return Ok(await planning.CreatePlanAsync(companionId, input, cancellationToken).ConfigureAwait(false));
        });

    static Task<IResult> PatchPlan(
        Guid companionId,
        Guid planId,
        CompanionPlanPatchRequest? input,
        HttpRequest request,
        CompanionPlanningService planning,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            if (input is null) throw new CompanionException("invalid_request", "A plan body is required.", 400);
            var body = await planning.PatchPlanAsync(companionId, planId, IfMatch(request), input, cancellationToken).ConfigureAwait(false);
            return body is null ? NotFound("companion_plan_not_found", "Plan not found.") : Ok(body);
        });

    static Task<IResult> DeletePlan(
        Guid companionId,
        Guid planId,
        HttpRequest request,
        CompanionPlanningService planning,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            var deleted = await planning.DeletePlanAsync(companionId, planId, IfMatch(request), cancellationToken).ConfigureAwait(false);
            return deleted ? Results.NoContent() : NotFound("companion_plan_not_found", "Plan not found.");
        });

    static Task<IResult> ListTodos(
        Guid companionId,
        CompanionPlanningService planning,
        CancellationToken cancellationToken = default) =>
        Run(async () => Ok(await planning.ListTodosAsync(companionId, cancellationToken).ConfigureAwait(false)));

    static Task<IResult> CreateTodo(
        Guid companionId,
        CompanionTodoCreateRequest? input,
        CompanionPlanningService planning,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            if (input is null) throw new CompanionException("invalid_request", "A todo body is required.", 400);
            return Ok(await planning.CreateTodoAsync(companionId, input, cancellationToken).ConfigureAwait(false));
        });

    static Task<IResult> PatchTodo(
        Guid companionId,
        Guid todoId,
        CompanionTodoPatchRequest? input,
        HttpRequest request,
        CompanionPlanningService planning,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            if (input is null) throw new CompanionException("invalid_request", "A todo body is required.", 400);
            var body = await planning.PatchTodoAsync(companionId, todoId, IfMatch(request), input, cancellationToken).ConfigureAwait(false);
            return body is null ? NotFound("companion_todo_not_found", "Todo not found.") : Ok(body);
        });

    static Task<IResult> DeleteTodo(
        Guid companionId,
        Guid todoId,
        HttpRequest request,
        CompanionPlanningService planning,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            var deleted = await planning.DeleteTodoAsync(companionId, todoId, IfMatch(request), cancellationToken).ConfigureAwait(false);
            return deleted ? Results.NoContent() : NotFound("companion_todo_not_found", "Todo not found.");
        });

    // ── settings & BYO Live2D SDK/avatars ──

    static Task<IResult> GetSettings(
        CompanionService service,
        CancellationToken cancellationToken = default) =>
        Run(async () => Ok(await service.GetSettingsAsync(cancellationToken).ConfigureAwait(false)));

    static Task<IResult> PatchSettings(
        JsonElement input,
        HttpRequest request,
        CompanionService service,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            if (input.ValueKind != JsonValueKind.Object)
                throw new CompanionException("invalid_request", "A settings object is required.", 400);

            if (input.TryGetProperty("buddy_companion_id", out _))
                throw new CompanionException("invalid_request", "buddy_companion_id has been removed; Buddy role is no longer supported.", 400);

            var mainSpecified = input.TryGetProperty("main_companion_id", out var mainElement);
            Guid? mainCompanionId = null;
            if (mainSpecified && mainElement.ValueKind != JsonValueKind.Null)
            {
                if (!mainElement.TryGetGuid(out var parsed))
                    throw new CompanionException("invalid_request", "main_companion_id must be a uuid.", 400);
                mainCompanionId = parsed;
            }

            int? recruitLimit = null;
            if (input.TryGetProperty("recruit_limit", out var limitElement) && limitElement.ValueKind != JsonValueKind.Null)
            {
                if (!limitElement.TryGetInt32(out var parsedLimit))
                    throw new CompanionException("invalid_request", "recruit_limit must be an integer.", 400);
                recruitLimit = parsedLimit;
            }

            int? maxBotHopDepth = null;
            if (input.TryGetProperty("max_bot_hop_depth", out var hopElement) && hopElement.ValueKind != JsonValueKind.Null)
            {
                if (!hopElement.TryGetInt32(out var parsedHop))
                    throw new CompanionException("invalid_request", "max_bot_hop_depth must be an integer between 0 and 10.", 400);
                maxBotHopDepth = parsedHop;
            }

            bool? autonomyPaused = null;
            if (input.TryGetProperty("autonomy_paused", out var pausedElement) && pausedElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                autonomyPaused = pausedElement.GetBoolean();

            var body = await service.PatchSettingsAsync(
                IfMatch(request), mainSpecified, mainCompanionId, recruitLimit, maxBotHopDepth, autonomyPaused, cancellationToken)
                .ConfigureAwait(false);
            return Ok(body);
        });

    static Task<IResult> GetCompanionAvatarSdk(
        CompanionAvatarStore store,
        CancellationToken cancellationToken = default) =>
        Run(async () => Ok(await store.GetSdkStatusAsync(cancellationToken).ConfigureAwait(false)));

    static Task<IResult> ImportCompanionAvatarSdk(
        CompanionSdkImportRequest? input,
        CompanionAvatarStore store,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            if (input is null)
                throw new CompanionException("sdk_input_required", "A Cubism Core SDK import body is required.", 400);
            return Ok(await store.ImportSdkAsync(input, cancellationToken).ConfigureAwait(false));
        });

    static Task<IResult> DeleteCompanionAvatarSdk(
        CompanionAvatarStore store,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            await store.DeleteSdkAsync(cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        });

    static Task<IResult> ServeCompanionAvatarSdkAsset(
        string asset,
        HttpContext context,
        CompanionAvatarStore store,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            var served = await store.OpenSdkAssetAsync(asset, cancellationToken).ConfigureAwait(false);
            if (served is null)
                return Results.NotFound(new { code = "sdk_asset_not_found", message = "Cubism Core SDK asset is not installed." });
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers.CacheControl = "private, no-cache";
            context.Response.Headers.ETag = served.ETag;
            return Results.Stream(served.Stream, contentType: served.ContentType);
        });

    static Task<IResult> ListCompanionAvatars(
        CompanionAvatarStore store,
        CancellationToken cancellationToken = default) =>
        Run(async () => Ok(await store.ListAsync(cancellationToken).ConfigureAwait(false)));

    static Task<IResult> ImportCompanionAvatar(
        CompanionAvatarImportRequest? input,
        CompanionAvatarStore store,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            if (input is null || string.IsNullOrWhiteSpace(input.Path))
                throw new CompanionException("avatar_path_required", "A model directory path is required.", 400);
            return Ok(await store.ImportAsync(input.Path, cancellationToken).ConfigureAwait(false));
        });

    static Task<IResult> ServeCompanionAvatarAsset(
        Guid avatarId,
        string? relativePath,
        HttpContext context,
        CompanionAvatarStore store,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            var served = await store.OpenAssetAsync(avatarId, relativePath, cancellationToken).ConfigureAwait(false);
            if (served is null)
                return Results.NotFound(new { code = "companion_avatar_asset_not_found", message = "Avatar asset not found." });
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers.CacheControl = "private, max-age=3600";
            context.Response.Headers.ETag = served.ETag;
            return Results.Stream(served.Stream, contentType: served.ContentType);
        });

    static Task<IResult> DeleteCompanionAvatar(
        Guid avatarId,
        CompanionAvatarStore store,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            var deleted = await store.DeleteAsync(avatarId, cancellationToken).ConfigureAwait(false);
            return deleted
                ? Results.NoContent()
                : Results.Json(new { code = "companion_avatar_not_found", message = "Companion avatar not found." }, statusCode: 404);
        });
}
