using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// User-level UI preferences endpoints (<c>/api/v1/preferences</c> and SSE <c>/api/v1/preferences/events</c>).
/// </summary>
public static class UiPreferencesEndpoints
{
    private static readonly JsonSerializerOptions StrictWireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly TimeSpan SseHeartbeatInterval = TimeSpan.FromSeconds(15);

    public static IEndpointRouteBuilder MapUiPreferencesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/preferences")
            .WithTags("Preferences");

        group.MapGet("", GetPreferencesAsync)
            .Produces<UiPreferencesDocumentDto>(StatusCodes.Status200OK);

        group.MapPut("", PutPreferencesAsync)
            .Accepts<UiPreferencesPutRequestDto>("application/json")
            .Produces<UiPreferencesDocumentDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed);

        group.MapPatch("", PatchPreferencesAsync)
            .Accepts<UiPreferencesPatchRequestDto>("application/json")
            .Produces<UiPreferencesDocumentDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed);

        group.MapGet("/events", StreamPreferencesEventsAsync);

        return app;
    }

    private static async Task<IResult> GetPreferencesAsync(
        HttpResponse response,
        IUiPreferencesService preferences,
        CancellationToken ct)
    {
        var doc = await preferences.GetAsync(ct).ConfigureAwait(false);
        response.Headers.ETag = $"\"{doc.ContentHash}\"";
        return Results.Ok(doc);
    }

    private static async Task<IResult> PutPreferencesAsync(
        HttpContext http,
        IUiPreferencesService preferences,
        CancellationToken ct)
    {
        var (request, parseError) = await ReadStrictJsonAsync<UiPreferencesPutRequestDto>(http, ct).ConfigureAwait(false);
        if (parseError is not null)
            return parseError;

        var ifMatch = ExtractIfMatch(http.Request);
        var effective = string.IsNullOrWhiteSpace(request!.ExpectedContentHash) && !string.IsNullOrWhiteSpace(ifMatch)
            ? request with { ExpectedContentHash = ifMatch }
            : request;

        try
        {
            var doc = await preferences.PutAsync(effective, ct).ConfigureAwait(false);
            http.Response.Headers.ETag = $"\"{doc.ContentHash}\"";
            return Results.Ok(doc);
        }
        catch (ConfigurationDocumentException ex)
        {
            return ToProblemResult(http, ex);
        }
    }

    private static async Task<IResult> PatchPreferencesAsync(
        HttpContext http,
        IUiPreferencesService preferences,
        CancellationToken ct)
    {
        var (request, parseError) = await ReadStrictJsonAsync<UiPreferencesPatchRequestDto>(http, ct).ConfigureAwait(false);
        if (parseError is not null)
            return parseError;

        var ifMatch = ExtractIfMatch(http.Request);
        var effective = string.IsNullOrWhiteSpace(request!.ExpectedContentHash) && !string.IsNullOrWhiteSpace(ifMatch)
            ? request with { ExpectedContentHash = ifMatch }
            : request;

        try
        {
            var doc = await preferences.PatchAsync(effective, ct).ConfigureAwait(false);
            http.Response.Headers.ETag = $"\"{doc.ContentHash}\"";
            return Results.Ok(doc);
        }
        catch (ConfigurationDocumentException ex)
        {
            return ToProblemResult(http, ex);
        }
    }

    private static async Task StreamPreferencesEventsAsync(
        HttpContext http,
        IUiPreferencesService preferences,
        CancellationToken ct)
    {
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";

        using var linkedAbort = CancellationTokenSource.CreateLinkedTokenSource(ct, http.RequestAborted);
        var token = linkedAbort.Token;

        try
        {
            await using var enumerator = preferences.Subscribe(token).GetAsyncEnumerator(token);
            // Start MoveNextAsync before reading initial snapshot so the subscriber channel is
            // registered synchronously and cannot miss a concurrent write during GetAsync.
            var pendingMoveNext = enumerator.MoveNextAsync().AsTask();

            var initial = await preferences.GetAsync(token).ConfigureAwait(false);
            var lastSentHash = initial.ContentHash;
            await WriteSseEventAsync(http.Response, initial, token).ConfigureAwait(false);

            while (!token.IsCancellationRequested)
            {
                using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                heartbeatCts.CancelAfter(SseHeartbeatInterval);
                var delayTask = Task.Delay(Timeout.InfiniteTimeSpan, heartbeatCts.Token);

                var completed = await Task.WhenAny(pendingMoveNext, delayTask).ConfigureAwait(false);
                if (completed == pendingMoveNext)
                {
                    if (!await pendingMoveNext.ConfigureAwait(false))
                        break;

                    var nextDoc = enumerator.Current;
                    if (!string.Equals(nextDoc.ContentHash, lastSentHash, StringComparison.Ordinal))
                    {
                        lastSentHash = nextDoc.ContentHash;
                        await WriteSseEventAsync(http.Response, nextDoc, token).ConfigureAwait(false);
                    }

                    pendingMoveNext = enumerator.MoveNextAsync().AsTask();
                }
                else if (!token.IsCancellationRequested)
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

    private static async Task WriteSseEventAsync(HttpResponse response, UiPreferencesDocumentDto doc, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(doc, StrictWireJson);
        await response.WriteAsync($"event: preferences.updated\ndata: {json}\n\n", ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<(T? Value, IResult? Error)> ReadStrictJsonAsync<T>(HttpContext http, CancellationToken ct)
        where T : class
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, Problem(http, StatusCodes.Status400BadRequest, "preferences_invalid", "Request body must be a JSON object."));
            }

            var parsed = doc.RootElement.Deserialize<T>(StrictWireJson);
            if (parsed is null)
            {
                return (null, Problem(http, StatusCodes.Status400BadRequest, "preferences_invalid", "Request body is required."));
            }

            return (parsed, null);
        }
        catch (JsonException ex)
        {
            return (null, Problem(http, StatusCodes.Status400BadRequest, "preferences_invalid", ex.Message));
        }
    }

    private static string? ExtractIfMatch(HttpRequest request)
    {
        var raw = request.Headers.IfMatch.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[2..].Trim();
        return trimmed.Trim('"');
    }

    private static IResult ToProblemResult(HttpContext http, ConfigurationDocumentException ex)
    {
        var status = ex.Code.Contains("conflict", StringComparison.OrdinalIgnoreCase)
            ? StatusCodes.Status412PreconditionFailed
            : StatusCodes.Status400BadRequest;
        return Problem(http, status, ex.Code, ex.Message, ex.Diagnostics);
    }

    private static IResult Problem(
        HttpContext http,
        int status,
        string code,
        string detail,
        IReadOnlyList<ConfigurationDiagnostic>? diagnostics = null)
    {
        var classification = ErrorClassification.Classify(code, status);
        var extensions = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["trace_id"] = http.TraceIdentifier,
            ["category"] = classification.Category,
            ["retryable"] = classification.Retryable,
            ["actions"] = classification.Actions,
        };
        if (diagnostics is { Count: > 0 })
            extensions["diagnostics"] = diagnostics;

        return Results.Problem(
            type: $"https://tinadec.dev/errors/{code}",
            title: code,
            detail: detail,
            statusCode: status,
            instance: http.Request.Path,
            extensions: extensions);
    }
}
