using System.Text.Json;
using System.Text.Json.Serialization;
using TinadecCore.Companion;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// Companion conversation surface (Task 2): conversations, membership, messages, runs and the
/// activity feed. Routes live beside the identity/profile surface but keep the same response shape
/// rules: snake_case JSON, <c>{code,message}</c> refusals, and a tenant predicate on every read.
/// </summary>
public static class CompanionConversationEndpoints
{
    public static IEndpointRouteBuilder MapCompanionConversationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/companions/{companionId:guid}/conversations", ListConversations);
        app.MapPost("/api/v1/companions/{companionId:guid}/conversations", CreateConversation);

        app.MapGet("/api/v1/companion-conversations/{conversationId:guid}", GetConversation);
        app.MapGet("/api/v1/companion-conversations", ListAllConversations);
        app.MapPost("/api/v1/companion-conversations/{conversationId:guid}/members", InviteMember);
        app.MapDelete("/api/v1/companion-conversations/{conversationId:guid}/members/{companionId:guid}", LeaveMember);

        app.MapGet("/api/v1/companion-conversations/{conversationId:guid}/messages", ListMessages);
        app.MapPost("/api/v1/companion-conversations/{conversationId:guid}/messages", PostMessage);
        app.MapPost("/api/v1/companion-conversations/{conversationId:guid}/pause", PauseConversation);

        app.MapGet("/api/v1/companion-runs/{runId:guid}", GetRun);
        app.MapGet("/api/v1/companions/activity", ListActivity);
        app.MapGet("/api/v1/companion-connections", ListConnectionsOverview);

        return app;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    static IResult Ok(object body) => Results.Json(body, JsonOptions);

    static IResult ConversationNotFound() =>
        Results.NotFound(new { code = "companion_conversation_not_found", message = "Conversation not found." });

    static IResult CompanionNotFound() =>
        Results.NotFound(new { code = "companion_not_found", message = "Companion not found." });

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

    static Task<IResult> ListConversations(
        Guid companionId,
        CompanionConversationService service,
        bool? include_groups = null,
        CancellationToken cancellationToken = default) =>
        Run(async () => Ok(await service.ListAsync(companionId, include_groups ?? true, cancellationToken).ConfigureAwait(false)));

    static Task<IResult> CreateConversation(
        Guid companionId,
        CompanionConversationCreateRequest? input,
        CompanionConversationService service,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            var kind = input?.Kind?.Trim().ToLowerInvariant();
            return kind switch
            {
                null or "" or "private" => Ok(await service.EnsurePrivateAsync(companionId, cancellationToken).ConfigureAwait(false)),
                "group" => Ok(await service.CreateGroupAsync(
                    input!.Title,
                    (input.CompanionIds ?? []).Append(companionId).Distinct().ToList(),
                    cancellationToken).ConfigureAwait(false)),
                _ => Error(new CompanionException("invalid_request", "kind must be private or group.", 400))
            };
        });

    static Task<IResult> GetConversation(
        Guid conversationId,
        CompanionConversationService service,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            var body = await service.GetAsync(conversationId, cancellationToken).ConfigureAwait(false);
            return body is null ? ConversationNotFound() : Ok(body);
        });

    /// <summary>
    /// Cross-companion list. `companion_id`, `kind` and `origin` narrow it; the companion centre
    /// reads one page instead of one request per companion.
    /// </summary>
    static Task<IResult> ListAllConversations(
        CompanionConversationService service,
        Guid? companion_id = null,
        string? kind = null,
        string? origin = null,
        int? limit = null,
        CancellationToken cancellationToken = default) =>
        Run(async () => Ok(await service.ListAllAsync(
            companion_id, kind, origin, limit ?? 50, cancellationToken).ConfigureAwait(false)));

    static Task<IResult> InviteMember(
        Guid conversationId,
        CompanionInviteRequest? input,
        CompanionConversationService service,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            if (input?.CompanionId is not { } companionId || companionId == Guid.Empty)
                throw new CompanionException("invalid_request", "companion_id is required.", 400);
            var body = await service.InviteAsync(conversationId, companionId, input.ShareHistory ?? false, cancellationToken)
                .ConfigureAwait(false);
            return Ok(body);
        });

    static Task<IResult> LeaveMember(
        Guid conversationId,
        Guid companionId,
        CompanionConversationService service,
        CancellationToken cancellationToken = default) =>
        Run(async () => Ok(await service.LeaveAsync(conversationId, companionId, cancellationToken).ConfigureAwait(false)));

    static Task<IResult> ListMessages(
        Guid conversationId,
        CompanionConversationService service,
        long? after_seq = null,
        int? limit = null,
        CancellationToken cancellationToken = default) =>
        Run(async () => Ok(await service.ListMessagesAsync(conversationId, after_seq, limit, cancellationToken).ConfigureAwait(false)));

    static Task<IResult> PostMessage(
        Guid conversationId,
        CompanionMessagePostRequest? input,
        HttpRequest request,
        CompanionConversationService service,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            if (input is null) throw new CompanionException("invalid_request", "A message body is required.", 400);
            var headerKey = request.Headers["Idempotency-Key"].FirstOrDefault();
            return Ok(await service.PostMessageAsync(conversationId, input, headerKey, cancellationToken).ConfigureAwait(false));
        });

    static Task<IResult> PauseConversation(
        Guid conversationId,
        CompanionPauseRequest? input,
        CompanionConversationService service,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            if (input?.Paused is not { } paused)
                throw new CompanionException("invalid_request", "paused is required.", 400);
            return Ok(await service.SetPausedAsync(conversationId, paused, cancellationToken).ConfigureAwait(false));
        });

    static Task<IResult> GetRun(
        Guid runId,
        CompanionConversationService service,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            var body = await service.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
            return body is null ? CompanionNotFound() : Ok(body);
        });

    static Task<IResult> ListActivity(
        CompanionConversationService service,
        int? limit = null,
        CancellationToken cancellationToken = default) =>
        Run(async () => Ok(await service.ListActivityAsync(limit, cancellationToken).ConfigureAwait(false)));

    static Task<IResult> ListConnectionsOverview(
        CompanionConversationService service,
        CancellationToken cancellationToken = default) =>
        Run(async () => Ok(await service.ListConnectionsOverviewAsync(cancellationToken).ConfigureAwait(false)));
}
