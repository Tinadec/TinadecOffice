using Microsoft.AspNetCore.Mvc;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.AspNetCore.Endpoints;

public static class TinaChatEndpoints
{
    public static IEndpointRouteBuilder MapTinaChatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tina-chat").WithTags("TinaChat");
        var observer = group.MapGroup("/observer");
        observer.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        observer.MapGet("/access", async (ITinaChatObserver chat, CancellationToken ct) =>
            await chat.GetObserverAccessAsync(ct)).Produces<TinaChatObserverAccessDto>();
        observer.MapGet("/conversations", async (string? query, string? kind, Guid? workspace_id, int? offset, int? limit,
            ITinaChatObserver chat, CancellationToken ct) =>
            await chat.ObserveConversationsAsync(query, kind, workspace_id, offset ?? 0, limit ?? 50, ct))
            .Produces<TinaChatObservedConversationPage>();
        observer.MapGet("/conversations/{id:guid}", async (Guid id, ITinaChatObserver chat, CancellationToken ct) =>
            await chat.ObserveConversationAsync(id, ct)).Produces<TinaChatObservedConversationDetail>();
        observer.MapGet("/conversations/{id:guid}/messages", async (Guid id, long? before_sequence, long? after_sequence, int? limit,
            ITinaChatObserver chat, CancellationToken ct) =>
            await chat.ObserveMessagesAsync(id, before_sequence, after_sequence, limit ?? 50, ct))
            .Produces<TinaChatObservedMessagePage>();

        group.MapGet("/participants", async (string? query, ITinaChatService chat, CancellationToken ct) =>
            await chat.DiscoverAsync(query, ct)).Produces<TinaChatParticipantDto[]>();
        group.MapPost("/participants", async (TinaChatRegisterParticipantRequest request, ITinaChatService chat, CancellationToken ct) =>
        {
            var participant = await chat.RegisterAsync(request, ct);
            return Results.Created($"/api/v1/tina-chat/participants/{participant.Id}", participant);
        }).Produces<TinaChatParticipantDto>(StatusCodes.Status201Created);
        group.MapGet("/participants/{id:guid}", async (Guid id, ITinaChatService chat, CancellationToken ct) =>
            await chat.GetParticipantAsync(id, ct)).Produces<TinaChatParticipantDto>();
        group.MapPatch("/participants/{id:guid}", async (Guid id, TinaChatUpdateParticipantRequest request, ITinaChatService chat, CancellationToken ct) =>
            await chat.UpdateParticipantAsync(id, request, ct)).Produces<TinaChatParticipantDto>();

        group.MapGet("/conversations", async (Guid actor_id, ITinaChatService chat, CancellationToken ct) =>
            await chat.ListConversationsAsync(actor_id, ct)).Produces<TinaChatConversationDto[]>();
        group.MapPost("/conversations", async (TinaChatCreateConversationRequest request, ITinaChatService chat, CancellationToken ct) =>
        {
            var conversation = await chat.CreateConversationAsync(request, ct);
            return Results.Created($"/api/v1/tina-chat/conversations/{conversation.Id}", conversation);
        }).Produces<TinaChatConversationDto>(StatusCodes.Status201Created);
        group.MapGet("/conversations/{id:guid}", async (Guid id, Guid actor_id, ITinaChatService chat, CancellationToken ct) =>
            await chat.GetConversationAsync(id, actor_id, ct)).Produces<TinaChatConversationDto>();
        group.MapGet("/conversations/{id:guid}/members", async (Guid id, Guid actor_id, ITinaChatService chat, CancellationToken ct) =>
            await chat.ListMembersAsync(id, actor_id, ct)).Produces<TinaChatMemberDto[]>();
        group.MapPut("/conversations/{id:guid}/members", async (Guid id, TinaChatMemberRequest request, ITinaChatService chat, CancellationToken ct) =>
            await chat.ChangeMemberAsync(id, request, ct)).Produces<TinaChatConversationDto>();

        group.MapGet("/conversations/{id:guid}/messages", async (Guid id, Guid actor_id, long? after_sequence, int? limit, ITinaChatService chat, CancellationToken ct) =>
            await chat.ReadMessagesAsync(id, actor_id, after_sequence ?? 0, limit ?? 50, ct)).Produces<TinaChatMessagePage>();
        group.MapPost("/conversations/{id:guid}/messages", async (Guid id, TinaChatSendMessageRequest request, ITinaChatService chat, CancellationToken ct) =>
            await chat.SendAsync(id, request, ct)).Produces<TinaChatMessageDto>();
        group.MapGet("/participants/{id:guid}/inbox", async (Guid id, long? after_sequence, int? limit, ITinaChatService chat, CancellationToken ct) =>
            await chat.ReadInboxAsync(id, after_sequence ?? 0, limit ?? 50, ct)).Produces<TinaChatInboxPage>();
        group.MapPost("/participants/{id:guid}/inbox/{messageId:guid}/ack", async (Guid id, Guid messageId, ITinaChatService chat, CancellationToken ct) =>
        {
            await chat.AcknowledgeAsync(id, messageId, ct);
            return Results.NoContent();
        }).Produces(StatusCodes.Status204NoContent);

        group.MapGet("/workspace-policy", async (ITinaChatService chat, CancellationToken ct) =>
            await chat.GetPolicyAsync(ct)).Produces<TinaChatWorkspacePolicyDto>();
        group.MapPut("/workspace-policy", async (TinaChatWorkspacePolicyRequest request, ITinaChatService chat, CancellationToken ct) =>
            await chat.SetPolicyAsync(request, ct)).Produces<TinaChatWorkspacePolicyDto>();

        group.MapGet("/conversations/{id:guid}/intents", async (Guid id, Guid actor_id, ITinaChatService chat, CancellationToken ct) =>
            await chat.ListIntentsAsync(id, actor_id, ct)).Produces<TinaChatIntentDto[]>();
        group.MapPost("/conversations/{id:guid}/intents", async (Guid id, TinaChatProposeIntentRequest request, ITinaChatService chat, CancellationToken ct) =>
            await chat.ProposeIntentAsync(id, request, ct)).Produces<TinaChatIntentDto>();
        group.MapPost("/conversations/{id:guid}/intents/generate", async (Guid id, TinaChatGenerateIntentRequest request, ITinaChatService chat, CancellationToken ct) =>
            await chat.GenerateIntentAsync(id, request, ct)).Produces<TinaChatIntentDto>();
        group.MapPost("/conversations/{id:guid}/intents/{intentId:guid}/decision", async (Guid id, Guid intentId, TinaChatIntentDecisionRequest request, ITinaChatService chat, CancellationToken ct) =>
            await chat.DecideIntentAsync(id, intentId, request, ct)).Produces<TinaChatIntentDto>();
        group.MapPost("/conversations/{id:guid}/intents/{intentId:guid}/execute", async (Guid id, Guid intentId, TinaChatExecuteIntentRequest request, ITinaChatRunService runs, CancellationToken ct) =>
            await runs.ExecuteAsync(id, intentId, request, ct)).Produces<TinaChatExecutionDto>();

        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (TinaChatException ex)
            {
                return Results.Problem(statusCode: ex.StatusCode, title: ex.Code, detail: ex.Message,
                    extensions: new Dictionary<string, object?> { ["code"] = ex.Code, ["trace_id"] = context.HttpContext.TraceIdentifier });
            }
        });
        return app;
    }
}
