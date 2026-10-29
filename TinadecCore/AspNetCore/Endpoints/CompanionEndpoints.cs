using System.Text.Json;
using System.Text.Json.Serialization;
using TinadecCore.Companion;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// Companion domain HTTP surface (Task 1: identity/profile/memory/skill/connection/settings;
/// Task 2: conversations, messages, runs and activity, mapped by
/// <see cref="CompanionConversationEndpoints"/>; Task 3: a manual autonomy tick).
/// </summary>
public static class CompanionEndpoints
{
    public static IEndpointRouteBuilder MapCompanionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/companions", ListCompanions);
        app.MapPost("/api/v1/companions", CreateCompanion);
        app.MapGet("/api/v1/companions/{companionId:guid}", GetCompanion);
        app.MapPatch("/api/v1/companions/{companionId:guid}", PatchCompanion);
        app.MapPost("/api/v1/companions/{companionId:guid}/archive", ArchiveCompanion);
        // The guid constraint keeps this distinct from GET/PATCH /companions/{companionId}.
        app.MapPost("/api/v1/companions/{companionId:guid}/autonomy/tick", TickAutonomy);

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

        app.MapGet("/api/v1/companion-settings", GetSettings);
        app.MapPatch("/api/v1/companion-settings", PatchSettings);

        app.MapCompanionConversationEndpoints();

        return app;
    }

    // The host policy omits nulls; the companion contract keeps nullable fields present
    // (tagline, avatar, last_activity_at, ...) so a client can distinguish null from absent.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    static long IfMatch(HttpRequest request) =>
        long.TryParse(request.Headers.IfMatch.FirstOrDefault()?.Trim('"', 'W', '/', ' '), out var value) ? value : -1;

    static IResult Ok(object body) => Results.Json(body, JsonOptions);

    static IResult NotFound() => Results.NotFound(new { code = "companion_not_found", message = "Companion not found." });

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

            var mainSpecified = input.TryGetProperty("main_companion_id", out var mainElement);
            Guid? mainCompanionId = null;
            if (mainSpecified && mainElement.ValueKind != JsonValueKind.Null)
            {
                if (!mainElement.TryGetGuid(out var parsed))
                    throw new CompanionException("invalid_request", "main_companion_id must be a uuid.", 400);
                mainCompanionId = parsed;
            }

            bool? autonomyPaused = null;
            if (input.TryGetProperty("autonomy_paused", out var pausedElement) && pausedElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                autonomyPaused = pausedElement.GetBoolean();

            var body = await service.PatchSettingsAsync(IfMatch(request), mainSpecified, mainCompanionId, autonomyPaused, cancellationToken).ConfigureAwait(false);
            return Ok(body);
        });
}
