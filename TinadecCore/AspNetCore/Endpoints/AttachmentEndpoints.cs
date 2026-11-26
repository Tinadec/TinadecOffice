using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Memory;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// Upload, list, read and discard session attachments.
///
/// Transport is a raw request body plus metadata in the query string, not multipart.
/// That is a deliberate fit to the layering: the Gateway is a byte-for-byte proxy, and
/// every extra encoding it would have to parse and re-emit is a place where a proxy can
/// silently disagree with the client. A body Core never has to unwrap cannot be
/// mis-unwrapped.
/// </summary>
public static class AttachmentEndpoints
{
    public const long MaxAttachmentBytes = 32L * 1024 * 1024;
    public const int MaxFileNameLength = 255;
    private const int MaxMediaTypeLength = 128;

    /// <summary>
    /// The only media types ever served inline. Everything else is forced to
    /// <c>application/octet-stream</c> with an attachment disposition, because these bytes
    /// are user-supplied and are served from the same origin the Desktop renderer trusts:
    /// an uploaded <c>text/html</c> or <c>image/svg+xml</c> opened inline is a script
    /// execution path on that origin (SVG carries script), so a browser-previewable type
    /// must be a type that cannot carry one.
    /// </summary>
    private static readonly HashSet<string> InlineSafeMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp", "text/plain",
    };

    public static IEndpointRouteBuilder MapAttachmentEndpoints(this IEndpointRouteBuilder app)
    {
        var sessionGroup = app.MapGroup("/api/v1/sessions/{sessionId:guid}/attachments").WithTags("Attachments");

        sessionGroup.MapPost(string.Empty, async (
            Guid sessionId,
            HttpRequest request,
            IMessageAttachmentStore store,
            [FromServices] ProjectSessionStore sessions,
            [FromServices] IServiceProvider services,
            CancellationToken ct) =>
        {
            var fileName = SanitizeFileName(request.Query["filename"].FirstOrDefault());
            if (fileName is null)
                return Results.BadRequest(new { code = "attachment_filename_invalid", message = "filename must be a bare name of at most 255 characters." });

            var mediaType = SanitizeMediaType(request.Query["media_type"].FirstOrDefault());
            if (mediaType is null)
                return Results.BadRequest(new { code = "attachment_media_type_invalid", message = $"media_type must be printable ASCII of at most {MaxMediaTypeLength} characters." });

            if (request.ContentLength is > MaxAttachmentBytes)
                return PayloadTooLarge();

            // The declared length is the client's claim; this is the enforced ceiling.
            // Without it a chunked upload could write an unbounded file to the store.
            var bodyLimit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = MaxAttachmentBytes;

            if (request.ContentLength == 0)
                return Results.BadRequest(new { code = "attachment_empty", message = "The request body carried no bytes." });

            var session = await sessions.GetSessionAsync(sessionId, ct).ConfigureAwait(false);
            if (session is null)
                return Results.NotFound(new { code = "session_not_found", message = "No session in the current workspace has that id." });
            var origin = request.Query["origin"].FirstOrDefault()?.Trim() ?? AttachmentOrigins.Composer;
            if (!AttachmentOrigins.IsKnown(origin))
                return Results.BadRequest(new { code = "attachment_origin_invalid", message = "origin must be composer or workspace." });
            Guid? projectId = session.ProjectId;
            if (Guid.TryParse(request.Query["project_id"].FirstOrDefault(), out var requestedProjectId))
            {
                if (session.ProjectId != requestedProjectId)
                    return Results.BadRequest(new { code = "attachment_project_mismatch", message = "project_id must match the session's project." });
                projectId = requestedProjectId;
            }
            else if (!string.IsNullOrWhiteSpace(request.Query["project_id"].FirstOrDefault()))
            {
                return Results.BadRequest(new { code = "attachment_project_invalid", message = "project_id must be a guid." });
            }

            string? sourcePath = null;
            if (origin == AttachmentOrigins.Workspace)
            {
                if (projectId is null)
                    return Results.BadRequest(new { code = "attachment_workspace_project_required", message = "A workspace exposure requires a project-bound session." });
                sourcePath = request.Query["source_path"].FirstOrDefault()?.Trim();
                var sourceProblem = await ValidateWorkspaceSourceAsync(
                    sourcePath,
                    projectId.Value,
                    sessions,
                    services.GetService<IWorkspaceDefinitionProvider>(),
                    ct).ConfigureAwait(false);
                if (sourceProblem is not null) return sourceProblem;
                sourcePath = Path.GetFullPath(sourcePath!);
            }
            else if (!string.IsNullOrWhiteSpace(request.Query["source_path"].FirstOrDefault()))
            {
                return Results.BadRequest(new { code = "attachment_source_not_allowed", message = "source_path is only valid for origin=workspace." });
            }

            try
            {
                var stored = await store.StoreAsync(
                    new StoreAttachmentRequest(sessionId, fileName, mediaType, request.Body)
                    {
                        Origin = origin,
                        ProjectId = projectId,
                        SourcePath = sourcePath
                    }, ct).ConfigureAwait(false);
                return stored is null
                    ? Results.NotFound(new { code = "session_not_found", message = "No session in the current workspace has that id." })
                    : Results.Created($"/api/v1/attachments/{stored.Id}", ToDto(stored));
            }
            catch (BadHttpRequestException)
            {
                // Kestrel aborts the body once the limit trips, after the handler started.
                return PayloadTooLarge();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return PayloadTooLarge();
            }
        })
        // No Consumes metadata: the MVC attribute is the only built-in one and dragging
        // Mvc into this project for a label is not worth it. The request body is therefore
        // absent from openapi.core.json (same as /interactions), so this route's real
        // regressions are pinned by AttachmentApiTests, not by the contract generator.
        .Produces<MessageAttachmentDto>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        sessionGroup.MapGet(string.Empty, async (Guid sessionId, IMessageAttachmentStore store, CancellationToken ct) =>
        {
            var rows = await store.ListAsync(sessionId, ct).ConfigureAwait(false);
            return Results.Ok(rows.Select(ToDto).ToArray());
        })
        .Produces<MessageAttachmentDto[]>(StatusCodes.Status200OK);

        app.MapGet("/api/v1/projects/{projectId:guid}/exposures", async (
            Guid projectId,
            IMessageAttachmentStore store,
            [FromServices] ProjectSessionStore sessions,
            CancellationToken ct) =>
        {
            var project = await sessions.FindProjectAsync(projectId, ct).ConfigureAwait(false);
            if (project is null)
                return Results.NotFound(new { code = "project_not_found", message = "No project in the current workspace has that id." });
            var rows = await store.ListByProjectAsync(projectId, ct).ConfigureAwait(false);
            return Results.Ok(rows.Select(ToDto).ToArray());
        })
        .WithTags("Attachments")
        .Produces<MessageAttachmentDto[]>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound);

        var group = app.MapGroup("/api/v1/attachments").WithTags("Attachments");

        group.MapPost("/stage", async (
            HttpRequest request,
            IPeerAttachmentStageService stageService,
            [FromServices] ProjectSessionStore sessions,
            [FromServices] IServiceProvider services,
            CancellationToken ct) =>
        {
            if (PeerDeviceEndpoints.IsObserverPeerRequest(request.HttpContext))
            {
                return PeerDeviceEndpoints.Problem(
                    request.HttpContext,
                    StatusCodes.Status403Forbidden,
                    PeerErrorCodes.PolicyDenied,
                    "Observer peers cannot stage attachments.");
            }

            string? rawStageId;
            string? rawSessionId;
            string? rawProjectId;
            string? rawFileName;
            string? rawMediaType;
            long? declaredTotalBytes = null;
            string? expectedSha256 = null;
            int chunkIndex = 0;
            int totalChunks = 1;
            string? rawOrigin;
            string? rawSourcePath;
            byte[]? chunkBytes = null;

            var isJson = request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true;
            if (isJson)
            {
                PeerAttachmentStageJsonRequestDto? body;
                try
                {
                    body = await request.ReadFromJsonAsync<PeerAttachmentStageJsonRequestDto>(ct).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { code = "invalid_request", message = "Stage request body must be valid JSON." });
                }

                if (body is null)
                {
                    return Results.BadRequest(new { code = "invalid_request", message = "Stage request body is required." });
                }

                rawStageId = body.StageId;
                rawSessionId = body.SessionId;
                rawProjectId = body.ProjectId;
                rawFileName = body.Filename;
                rawMediaType = body.MediaType;
                declaredTotalBytes = body.TotalBytes;
                expectedSha256 = body.Sha256Hex;
                chunkIndex = body.ChunkIndex ?? 0;
                totalChunks = body.TotalChunks ?? 1;
                rawOrigin = body.Origin;
                rawSourcePath = body.SourcePath;

                if (!string.IsNullOrEmpty(body.ChunkBase64))
                {
                    try
                    {
                        chunkBytes = Convert.FromBase64String(body.ChunkBase64);
                    }
                    catch (FormatException)
                    {
                        return Results.BadRequest(new { code = "attachment_chunk_invalid", message = "chunk_base64 must be valid base64." });
                    }
                }
            }
            else
            {
                if (request.ContentLength is > MaxAttachmentBytes)
                    return PayloadTooLarge();

                var bodyLimit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = MaxAttachmentBytes;

                rawStageId = request.Query["stage_id"].FirstOrDefault();
                rawSessionId = request.Query["session_id"].FirstOrDefault();
                rawProjectId = request.Query["project_id"].FirstOrDefault();
                rawFileName = request.Query["filename"].FirstOrDefault();
                rawMediaType = request.Query["media_type"].FirstOrDefault();
                if (long.TryParse(request.Query["total_bytes"].FirstOrDefault(), out var parsedTotalBytes))
                    declaredTotalBytes = parsedTotalBytes;
                expectedSha256 = request.Query["sha256_hex"].FirstOrDefault();
                if (int.TryParse(request.Query["chunk_index"].FirstOrDefault(), out var parsedChunkIndex))
                    chunkIndex = parsedChunkIndex;
                if (int.TryParse(request.Query["total_chunks"].FirstOrDefault(), out var parsedTotalChunks))
                    totalChunks = parsedTotalChunks;
                rawOrigin = request.Query["origin"].FirstOrDefault();
                rawSourcePath = request.Query["source_path"].FirstOrDefault();

                var normalizedOriginPreview = rawOrigin?.Trim() ?? AttachmentOrigins.Composer;
                if (!string.Equals(normalizedOriginPreview, AttachmentOrigins.Workspace, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var ms = new MemoryStream();
                        var buffer = new byte[64 * 1024];
                        long readTotal = 0;
                        int read;
                        while ((read = await request.Body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            readTotal += read;
                            if (readTotal > MaxAttachmentBytes)
                                return PayloadTooLarge();
                            ms.Write(buffer, 0, read);
                        }
                        chunkBytes = ms.ToArray();
                    }
                    catch (BadHttpRequestException)
                    {
                        return PayloadTooLarge();
                    }
                }
            }

            var origin = string.IsNullOrWhiteSpace(rawOrigin) ? AttachmentOrigins.Composer : rawOrigin.Trim().ToLowerInvariant();
            if (!AttachmentOrigins.IsKnown(origin))
            {
                return Results.BadRequest(new { code = "attachment_origin_invalid", message = "origin must be composer or workspace." });
            }

            var fileNameCandidate = rawFileName;
            if (string.IsNullOrWhiteSpace(fileNameCandidate) && origin == AttachmentOrigins.Workspace && !string.IsNullOrWhiteSpace(rawSourcePath))
            {
                fileNameCandidate = Path.GetFileName(rawSourcePath.Trim());
            }

            var fileName = SanitizeFileName(fileNameCandidate);
            if (fileName is null)
            {
                return Results.BadRequest(new { code = "attachment_filename_invalid", message = "filename must be a bare name of at most 255 characters." });
            }

            var mediaType = SanitizeMediaType(rawMediaType);

            Guid? sessionId = null;
            SessionRecord? session = null;
            if (!string.IsNullOrWhiteSpace(rawSessionId))
            {
                if (!Guid.TryParse(rawSessionId.Trim(), out var parsedSessionId))
                    return Results.BadRequest(new { code = "attachment_session_invalid", message = "session_id must be a guid." });
                session = await sessions.GetSessionAsync(parsedSessionId, ct).ConfigureAwait(false);
                if (session is null)
                    return Results.NotFound(new { code = "session_not_found", message = "No session in the current workspace has that id." });
                sessionId = parsedSessionId;
            }

            Guid? projectId = session?.ProjectId;
            if (!string.IsNullOrWhiteSpace(rawProjectId))
            {
                if (!Guid.TryParse(rawProjectId.Trim(), out var parsedProjectId))
                    return Results.BadRequest(new { code = "attachment_project_invalid", message = "project_id must be a guid." });
                if (session is not null && session.ProjectId != parsedProjectId)
                    return Results.BadRequest(new { code = "attachment_project_mismatch", message = "project_id must match the session's project." });
                projectId = parsedProjectId;
            }

            string? validatedSourcePath = null;
            long? workspaceSourceBytes = null;
            if (origin == AttachmentOrigins.Workspace)
            {
                if (projectId is null)
                    return Results.BadRequest(new { code = "attachment_workspace_project_required", message = "A workspace exposure requires a project-bound session or project_id." });

                var trimmedSource = rawSourcePath?.Trim();
                var sourceProblem = await ValidateStagedWorkspaceSourceAsync(
                    trimmedSource,
                    projectId.Value,
                    sessions,
                    services.GetService<IWorkspaceDefinitionProvider>(),
                    ct).ConfigureAwait(false);
                if (sourceProblem is not null)
                    return sourceProblem;

                validatedSourcePath = Path.GetFullPath(WorkspacePathSpelling.Canonical(trimmedSource!));
                workspaceSourceBytes = new FileInfo(validatedSourcePath).Length;
            }
            else if (!string.IsNullOrWhiteSpace(rawSourcePath))
            {
                return Results.BadRequest(new { code = "attachment_source_not_allowed", message = "source_path is only valid for origin=workspace." });
            }

            try
            {
                var staged = await stageService.StageChunkOrReferenceAsync(
                    new PeerAttachmentStageCommand(
                        StageId: rawStageId,
                        SessionId: sessionId,
                        ProjectId: projectId,
                        FileName: fileName,
                        MediaType: mediaType,
                        Origin: origin,
                        ChunkIndex: chunkIndex,
                        TotalChunks: totalChunks,
                        DeclaredTotalBytes: declaredTotalBytes,
                        ExpectedSha256Hex: expectedSha256,
                        ValidatedWorkspaceSourcePath: validatedSourcePath,
                        WorkspaceSourceFileBytes: workspaceSourceBytes,
                        ChunkBytes: chunkBytes),
                    ct).ConfigureAwait(false);

                return Results.Ok(staged);
            }
            catch (PeerException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return PayloadTooLarge();
            }
            catch (PeerException ex) when (ex.StatusCode == StatusCodes.Status404NotFound)
            {
                return Results.NotFound(new { code = ex.Code, message = ex.Message });
            }
            catch (PeerException ex) when (ex.StatusCode == StatusCodes.Status403Forbidden)
            {
                return PeerDeviceEndpoints.Problem(request.HttpContext, StatusCodes.Status403Forbidden, ex.Code, ex.Message);
            }
            catch (PeerException ex)
            {
                return Results.BadRequest(new { code = ex.Code, message = ex.Message });
            }
        })
        .Produces<PeerAttachmentStageResultDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        group.MapGet("/{attachmentId:guid}", async (Guid attachmentId, IMessageAttachmentStore store, CancellationToken ct) =>
        {
            var stored = await store.FindAsync(attachmentId, ct).ConfigureAwait(false);
            return stored is null
                ? Results.NotFound(new { code = "attachment_not_found", message = "No attachment in the current workspace has that id." })
                : Results.Ok(ToDto(stored));
        })
        .Produces<MessageAttachmentDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{attachmentId:guid}/content", async (HttpContext context, Guid attachmentId, IMessageAttachmentStore store, CancellationToken ct) =>
        {
            var stored = await store.FindAsync(attachmentId, ct).ConfigureAwait(false);
            if (stored is null)
                return Results.NotFound(new { code = "attachment_not_found", message = "No attachment in the current workspace has that id." });

            var stream = await store.OpenContentAsync(stored, ct).ConfigureAwait(false);
            if (stream is null)
                return Results.NotFound(new { code = "attachment_content_missing", message = "The attachment row outlived its stored content." });

            var inline = InlineSafeMediaTypes.Contains(stored.MediaType);
            context.Response.Headers.ContentDisposition =
                $"{(inline ? "inline" : "attachment")}; filename*=UTF-8''{Uri.EscapeDataString(stored.FileName)}";
            return Results.Stream(stream, inline ? stored.MediaType : "application/octet-stream");
        })
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{attachmentId:guid}", async (Guid attachmentId, IMessageAttachmentStore store, CancellationToken ct) =>
        {
            var removed = await store.DeleteAsync(attachmentId, ct).ConfigureAwait(false);
            return removed ? Results.NoContent() : Results.NotFound(new { code = "attachment_not_found", message = "No attachment in the current workspace has that id." });
        })
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static IResult PayloadTooLarge() =>
        Results.Json(new { code = "attachment_too_large", message = $"An attachment may not exceed {MaxAttachmentBytes} bytes.", max_bytes = MaxAttachmentBytes },
            statusCode: StatusCodes.Status413PayloadTooLarge);

    /// <summary>
    /// Takes only the leaf of whatever path the client sent, and rejects anything that
    /// still carries a separator or a control character. A filename here is display
    /// metadata that later becomes a response header, so it must not be able to carry a
    /// traversal segment or a header break. Returns null when nothing usable is left.
    /// </summary>
    internal static string? SanitizeFileName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var leaf = raw.Replace('\\', '/').Trim();
        var slash = leaf.LastIndexOf('/');
        if (slash >= 0) leaf = leaf[(slash + 1)..];
        leaf = leaf.Trim().TrimStart('.');
        if (leaf.Length is 0 || leaf.Length > MaxFileNameLength) return null;
        if (leaf.Any(c => char.IsControl(c) || c is '"' or ':')) return null;
        return leaf;
    }

    internal static string SanitizeMediaType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "application/octet-stream";
        var value = raw.Trim();
        if (value.Length > MaxMediaTypeLength) return "application/octet-stream";
        if (value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return "application/octet-stream";
        return value;
    }

    private static MessageAttachmentDto ToDto(StoredAttachment stored) => new()
    {
        Id = stored.Id.ToString(),
        SessionId = stored.SessionId.ToString(),
        MessageId = stored.MessageId?.ToString(),
        FileName = stored.FileName,
        MediaType = stored.MediaType,
        ContentHash = stored.ContentHash,
        ContentLength = stored.ContentLength,
        Origin = stored.Origin,
        ProjectId = stored.ProjectId,
        SourcePath = stored.SourcePath,
        DeliveredToModel = stored.DeliveredToModel,
        DeliveredRunId = stored.DeliveredRunId,
        DeliveredAt = stored.DeliveredAt,
        CreatedAt = stored.CreatedAt,
        BoundAt = stored.BoundAt,
    };

    private static async Task<IResult?> ValidateWorkspaceSourceAsync(
        string? rawPath,
        Guid projectId,
        ProjectSessionStore sessions,
        IWorkspaceDefinitionProvider? workspace,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return Results.BadRequest(new { code = "attachment_source_required", message = "origin=workspace requires source_path." });
        if (workspace is null)
            return Results.BadRequest(new { code = "attachment_workspace_unavailable", message = "Workspace file authorization is not available in this host." });
        if (!Path.IsPathFullyQualified(rawPath))
            return Results.BadRequest(new { code = "attachment_source_invalid", message = "source_path must be absolute." });
        var project = await sessions.FindProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project is null)
            return Results.NotFound(new { code = "project_not_found", message = "No project in the current workspace has that id." });
        var path = WorkspacePathSpelling.Canonical(rawPath);
        if (!File.Exists(path))
            return Results.BadRequest(new { code = "attachment_source_missing", message = "source_path must name an existing file." });
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            return Results.BadRequest(new { code = "attachment_source_link", message = "source_path cannot be a symbolic link." });
        var definition = workspace.Read();
        var authorized = definition.Files.Any(file => IsWithin(file.Path, path))
            || definition.Roots.Any(root => IsWithin(root.Path, path));
        return authorized
            ? null
            : Results.BadRequest(new { code = "attachment_source_unauthorized", message = "source_path is not in the workspace's authorized folders or exact-file grants." });
    }

    private static async Task<IResult?> ValidateStagedWorkspaceSourceAsync(
        string? rawPath,
        Guid projectId,
        ProjectSessionStore sessions,
        IWorkspaceDefinitionProvider? workspace,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return Results.BadRequest(new { code = "attachment_source_required", message = "origin=workspace requires source_path." });
        if (!Path.IsPathFullyQualified(rawPath))
            return Results.BadRequest(new { code = "attachment_source_invalid", message = "source_path must be absolute." });
        var project = await sessions.FindProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project is null)
            return Results.NotFound(new { code = "project_not_found", message = "No project in the current workspace has that id." });
        var path = WorkspacePathSpelling.Canonical(rawPath);
        if (!File.Exists(path))
            return Results.BadRequest(new { code = "attachment_source_missing", message = "source_path must name an existing file." });
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            return Results.BadRequest(new { code = "attachment_source_link", message = "source_path cannot be a symbolic link." });

        bool authorized;
        if (workspace is not null)
        {
            var definition = workspace.Read();
            authorized = definition.Files.Any(file => IsWithin(file.Path, path))
                || definition.Roots.Any(root => IsWithin(root.Path, path));
        }
        else if (!string.IsNullOrWhiteSpace(project.RootPath))
        {
            authorized = IsWithin(project.RootPath, path);
        }
        else
        {
            return Results.BadRequest(new { code = "attachment_workspace_unavailable", message = "Workspace file authorization is not available in this host." });
        }

        return authorized
            ? null
            : Results.BadRequest(new { code = "attachment_source_unauthorized", message = "source_path is not in the workspace's authorized folders or exact-file grants." });
    }

    private static bool IsWithin(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(fullRoot, fullPath, comparison)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }
}
