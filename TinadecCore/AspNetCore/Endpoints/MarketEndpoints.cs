using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// The market surface: which sources Core may read, and what those sources said the last time
/// somebody refreshed them.
///
/// Until now these five routes were stubs — <c>GET /market/sources</c> and <c>GET
/// /market/catalog</c> answered <c>[]</c>, the rest 501 — while the desktop and the gateway both
/// had code that called them and rendered the empty array as "no extensions in the market". That
/// is the same phantom this file's sibling, <see cref="McpEndpoints"/>, closed for MCP; here the
/// underlying facts genuinely existed nowhere, so the fix is a store and an adapter, not a
/// projection of something already computed.
///
/// What is deliberately absent: installation. A catalog row here is an external party's claim
/// about a thing, and nothing on this surface can turn it into a file, a config entry, or a tool
/// a model can call. That boundary is the point of keeping the read half shippable on its own.
/// </summary>
public static class MarketEndpoints
{
    public static IEndpointRouteBuilder MapMarketEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/market").WithTags("Market");

        group.MapGet("/sources", async (IMarketCatalogService market, CancellationToken ct) =>
        {
            var sources = await market.ListSourcesAsync(ct).ConfigureAwait(false);
            return Results.Ok(new MarketSourceListDto
            {
                Sources = sources,
                SupportedKinds = market.SupportedKinds,
            });
        })
            .Produces<MarketSourceListDto>(StatusCodes.Status200OK);

        group.MapPost("/sources", async (
            CreateMarketSourceRequestDto request,
            IMarketCatalogService market,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await market.CreateSourceAsync(request, ct).ConfigureAwait(false));
            }
            catch (MarketCatalogException ex)
            {
                return Problem(ex);
            }
        })
            .Produces<MarketSourceDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPatch("/sources/{sourceId}", async (
            string sourceId,
            MarketSourceStateRequestDto request,
            IMarketCatalogService market,
            CancellationToken ct) =>
        {
            if (!TryId(sourceId, out var id))
                return InvalidId("sourceId", sourceId);

            // Absent is not false. A body that names no field would otherwise switch a source off
            // for a client that meant to ask about something else.
            if (request.Enabled is not { } enabled)
                return Results.Json(
                    new { code = "invalid_request", message = "enabled: true or false is required." },
                    statusCode: StatusCodes.Status400BadRequest);

            try
            {
                return Results.Ok(await market.SetSourceEnabledAsync(id, enabled, ct).ConfigureAwait(false));
            }
            catch (MarketCatalogException ex)
            {
                return Problem(ex);
            }
        })
            .Produces<MarketSourceDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/sources/{sourceId}", async (
            string sourceId,
            IMarketCatalogService market,
            CancellationToken ct) =>
        {
            if (!TryId(sourceId, out var id))
                return InvalidId("sourceId", sourceId);

            return await market.DeleteSourceAsync(id, ct).ConfigureAwait(false)
                ? Results.NoContent()
                : NotFound(MarketErrorCodes.SourceNotFound, "No market source has that id.");
        })
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/sources/{sourceId}/refresh", async (
            string sourceId,
            IMarketCatalogService market,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            if (!TryId(sourceId, out var id))
                return InvalidId("sourceId", sourceId);

            // Same convention as the MCP inventory: the configured default root, else this
            // process's working directory. It selects which provider process performs the fetch;
            // it grants no filesystem access.
            var workspaceRoot = configuration["TinadecTools:DefaultWorkspaceRoot"];
            if (string.IsNullOrWhiteSpace(workspaceRoot))
                workspaceRoot = Directory.GetCurrentDirectory();

            try
            {
                // A refresh always answers 200 with its own outcome; the codes below are for the
                // requests that never got as far as the network.
                return Results.Ok(await market.RefreshSourceAsync(id, workspaceRoot, ct).ConfigureAwait(false));
            }
            catch (MarketCatalogException ex)
            {
                return Problem(ex);
            }
        })
            .Produces<MarketRefreshDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/catalog", async (
            HttpContext http,
            IMarketCatalogService market,
            CancellationToken ct) =>
        {
            var query = http.Request.Query;
            Guid? sourceId = null;
            string? rawSource = query["source_id"];
            if (!string.IsNullOrWhiteSpace(rawSource))
            {
                if (!Guid.TryParse(rawSource, out var parsed))
                {
                    return Results.Json(
                        new { code = "invalid_request", message = $"source_id is not a guid: '{rawSource}'" },
                        statusCode: StatusCodes.Status400BadRequest);
                }

                sourceId = parsed;
            }

            // Absent means the default; present-but-unusable is refused rather than quietly
            // reinterpreted, because a misspelled paging value that answers "page 1" makes a
            // truncated catalog look like a complete one.
            var limit = MarketCatalogQuery.DefaultLimit;
            string? rawLimit = query["limit"];
            if (!string.IsNullOrWhiteSpace(rawLimit) && (!int.TryParse(rawLimit, out limit) || limit < 1))
                return InvalidQuery("limit", rawLimit);

            var offset = 0;
            string? rawOffset = query["offset"];
            if (!string.IsNullOrWhiteSpace(rawOffset) && (!int.TryParse(rawOffset, out offset) || offset < 0))
                return InvalidQuery("offset", rawOffset);

            try
            {
                var page = await market.ListCatalogAsync(
                    new MarketCatalogQuery(
                        Kind: query["kind"],
                        Search: query["q"],
                        SourceId: sourceId,
                        Limit: limit,
                        Offset: offset),
                    ct).ConfigureAwait(false);

                return Results.Ok(page);
            }
            catch (MarketCatalogException ex)
            {
                return Problem(ex);
            }
        })
            .Produces<MarketCatalogPageDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/catalog/{catalogId}", async (
            string catalogId,
            IMarketCatalogService market,
            CancellationToken ct) =>
        {
            if (!TryId(catalogId, out var id))
                return InvalidId("catalogId", catalogId);

            var entry = await market.FindCatalogEntryAsync(id, ct).ConfigureAwait(false);
            return entry is null
                ? NotFound(MarketErrorCodes.EntryNotFound, "No catalog entry has that id.")
                : Results.Ok(entry);
        })
            .Produces<MarketCatalogEntryDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>
    /// Ids bind as text and parse here. Letting minimal API bind a <c>Guid</c> parameter would put
    /// the rejection in the framework, which reports that a route requirement failed but not which
    /// value offended — and every code below is chosen so a client can branch on it without reading
    /// a sentence.
    /// </summary>
    private static bool TryId(string? value, out Guid id) =>
        Guid.TryParse(value, out id) && id != Guid.Empty;

    private static IResult InvalidId(string field, string? value) => Results.Json(
        new { code = "invalid_request", message = $"{field} is not a guid: '{value}'" },
        statusCode: StatusCodes.Status400BadRequest);

    private static IResult InvalidQuery(string field, string? value) => Results.Json(
        new { code = "invalid_request", message = $"{field} is out of range: '{value}'" },
        statusCode: StatusCodes.Status400BadRequest);

    private static IResult NotFound(string code, string message) =>
        Results.Json(new { code, message }, statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// One place decides which market failures are "not there", "cannot now", and "you sent the
    /// wrong thing". A code that fell through to 500 would look like a Core bug to every client,
    /// which is how a bad id becomes an outage in someone's dashboard.
    /// </summary>
    private static IResult Problem(MarketCatalogException ex) => ex.Code switch
    {
        MarketErrorCodes.SourceNotFound or MarketErrorCodes.EntryNotFound => NotFound(ex.Code, ex.Message),
        MarketErrorCodes.SourceDisabled or MarketErrorCodes.DuplicateSource => Results.Json(
            new { code = ex.Code, message = ex.Message }, statusCode: StatusCodes.Status409Conflict),
        _ => Results.Json(new { code = ex.Code, message = ex.Message },
            statusCode: StatusCodes.Status400BadRequest),
    };
}
