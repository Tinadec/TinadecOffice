using Microsoft.AspNetCore.Http.Features;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

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

            try
            {
                var stored = await store.StoreAsync(
                    new StoreAttachmentRequest(sessionId, fileName, mediaType, request.Body), ct).ConfigureAwait(false);
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

        var group = app.MapGroup("/api/v1/attachments").WithTags("Attachments");

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
        CreatedAt = stored.CreatedAt,
        BoundAt = stored.BoundAt,
    };
}
