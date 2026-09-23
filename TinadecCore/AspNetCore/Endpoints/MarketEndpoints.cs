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
/// The install routes below are where that boundary is crossed, and they are shaped so that
/// crossing it takes two calls and a human: a preview freezes a proposal, and an apply hands the
/// frozen bytes to the governed user-tool-action path. Nothing in this file writes a file or
/// starts a process itself.
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

            try
            {
                return await market.DeleteSourceAsync(id, ct).ConfigureAwait(false)
                    ? Results.NoContent()
                    : NotFound(MarketErrorCodes.SourceNotFound, "No market source has that id.");
            }
            catch (MarketCatalogException ex)
            {
                // Deleting a source that still backs an installation refuses with its own code.
                // Without this catch the guard surfaces as a 500 and the caller has no idea the
                // row they tried to forget is what an installed server is traceable to.
                return Problem(ex);
            }
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

        group.MapPost("/catalog/{catalogId}/install-preview", async (
            string catalogId,
            MarketInstallRequestDto request,
            IMarketInstallService install,
            CancellationToken ct) =>
        {
            if (!TryId(catalogId, out var id))
                return InvalidId("catalogId", catalogId);

            if (!TryGuidId(request.ProjectId, out var projectId))
                return InvalidId("project_id", request.ProjectId);

            try
            {
                return Results.Ok(await install.PreviewInstallAsync(id, projectId, ct).ConfigureAwait(false));
            }
            catch (MarketCatalogException ex)
            {
                return Problem(ex);
            }
        })
            .Produces<MarketInstallProposalDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/installations/{installationId}/uninstall-preview", async (
            string installationId,
            IMarketInstallService install,
            CancellationToken ct) =>
        {
            if (!TryId(installationId, out var id))
                return InvalidId("installationId", installationId);

            try
            {
                return Results.Ok(await install.PreviewUninstallAsync(id, ct).ConfigureAwait(false));
            }
            catch (MarketCatalogException ex)
            {
                return Problem(ex);
            }
        })
            .Produces<MarketInstallProposalDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/install-proposals/{proposalId}/apply", async (
            string proposalId,
            IMarketInstallService install,
            CancellationToken ct) =>
        {
            if (!TryId(proposalId, out var id))
                return InvalidId("proposalId", proposalId);

            try
            {
                // 200, not 201: this answers "what did you queue", and the queued thing is an
                // approval awaiting a human rather than a file that now exists.
                return Results.Ok(await install.ApplyAsync(id, ct).ConfigureAwait(false));
            }
            catch (MarketCatalogException ex)
            {
                return Problem(ex);
            }
        })
            .Produces<MarketInstallationDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status412PreconditionFailed);

        group.MapGet("/installations", async (IMarketInstallService install, CancellationToken ct) =>
        {
            var installations = await install.ListInstallationsAsync(ct).ConfigureAwait(false);
            return Results.Ok(new MarketInstallationListDto { Installations = installations });
        })
            .Produces<MarketInstallationListDto>(StatusCodes.Status200OK);

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

    /// <summary>Same rule for an id that arrives in a body rather than the route.</summary>
    private static bool TryGuidId(string? value, out Guid id) => TryId(value, out id);

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
        // Both of these are properties of the world, not of the request: the entry publishes no
        // command this layer could start, or the provider keeps its config where Core may not
        // write. Retrying with the same body answers the same way, so this is a conflict (409)
        // rather than a malformed request (400).
        MarketErrorCodes.SourceDisabled or MarketErrorCodes.DuplicateSource
            or MarketErrorCodes.SourceInUse or MarketErrorCodes.TargetUnresolved
            or MarketErrorCodes.NotExpressible => Results.Json(
            new { code = ex.Code, message = ex.Message }, statusCode: StatusCodes.Status409Conflict),
        // A proposal that no longer describes the market is not a malformed request and not a
        // missing thing: the precondition it was built on stopped holding, which is 412 and is
        // what tells a client to re-preview rather than to fix its input or give up.
        MarketErrorCodes.ProposalStale => Results.Json(
            new { code = ex.Code, message = ex.Message }, statusCode: StatusCodes.Status412PreconditionFailed),
        MarketErrorCodes.ProposalNotFound or MarketErrorCodes.InstallationNotFound
            or MarketErrorCodes.ProjectNotFound => Results.Json(
            new { code = ex.Code, message = ex.Message }, statusCode: StatusCodes.Status404NotFound),
        _ => Results.Json(new { code = ex.Code, message = ex.Message },
            statusCode: StatusCodes.Status400BadRequest),
    };
}
