using System.Text;
using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Tools;

/// <summary>
/// Core-owned virtual tool that reads one page of a message attachment as text.
///
/// Why it exists: the context builder quotes at most <c>MaxInlineCharsPerAttachment</c> characters of
/// an attachment and a fixed total per turn, so the rest of a log, a CSV or a config was simply not
/// there - the model saw a filename and a byte count and had to ask the user to paste lines by hand.
/// This is the way to ask for the rest: Core serves pages out of the bytes it already stores, and the
/// model decides how deep to read and where to start.
///
/// The boundary is stated in the answer rather than hidden in a failure: an attachment whose media
/// type is not text is refused, because this runtime sends no image or audio part to any provider (a
/// conversation message carries one string, and the CLI/ACP clients flatten it). Returning base64
/// would read to a model as content it cannot decode, and guessing at a PNG is the exact failure the
/// context section warns it against.
///
/// Scope: only a row belonging to the CALLING run's session can be read. The attachment store is
/// scoped to (tenant, workspace), which is wider than a conversation, so the id is resolved against
/// the session's own listing here instead of trusting the store's answer.
///
/// Approval: not required. It reads bytes the user uploaded into this session, writes nothing, and
/// the declared tool surface plus the per-instance grant is what authorizes it.
/// </summary>
internal static class CoreAttachmentReadTool
{
    public const string ToolId = CoreVirtualToolPolicy.ReadAttachmentToolId;

    /// <summary>
    /// Characters one call returns, and the ceiling on a requested <c>limit</c>. Sized against the
    /// default context budget (<c>Configuration/default-agent-runtime.toml</c>:
    /// default_token_budget = 65536): the estimator the context builder uses is length/4, so a page is
    /// roughly 4k tokens - big enough to read a real file in a few calls, small enough that a model
    /// paging a 30 MB log cannot spend its whole budget on one result.
    /// </summary>
    public const int MaxCharsPerPage = 16 * 1024;

    private const string InputSchemaJson =
        "{\"type\":\"object\",\"properties\":{" +
        "\"attachment_id\":{\"type\":\"string\",\"description\":\"The id: value shown for the attachment in the session's file list.\"}," +
        "\"offset\":{\"type\":\"integer\",\"minimum\":0,\"description\":\"Character offset to start at, 0 for the beginning. Defaults to 0.\"}," +
        "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"Characters to return; defaults to the maximum page and is capped at it.\"}}" +
        ",\"required\":[\"attachment_id\"],\"additionalProperties\":false}";

    public static ToolManifestEntryDto ManifestEntry() => new()
    {
        Id = ToolId,
        Description =
            "Read a page of a file the user attached to a message in this conversation, as text. The "
            + "context only quotes the beginning of each attachment, so use this for the rest: pass the "
            + "attachment id and an offset, and the result tells you where the next page starts. "
            + "Offsets and limits count CHARACTERS, not bytes. Files that are not text (images, audio, "
            + "opaque binaries) cannot be read by anything here - say you cannot see them instead of guessing.",
        RequiresApproval = false,
        InputSchema = JsonDocument.Parse(InputSchemaJson).RootElement.Clone(),
        Risk = "low",
        MutatesWorkspace = false,
        // A pure read of stored bytes: the same call returns the same page, so a retry is safe.
        RetrySafety = "safe",
        ConfirmationFields = []
    };

    public static bool IsCoreTool(string toolId) => CoreVirtualToolPolicy.IsReadAttachment(toolId);

    public static async Task<ToolWireResponseDto> ExecuteAsync(
        IMessageAttachmentStore store,
        ToolInvocationScope scope,
        ToolWireRequestDto wire,
        CancellationToken cancellationToken = default)
    {
        string? rawId = null;
        var offset = 0;
        var limit = MaxCharsPerPage;
        if (wire.Params is { ValueKind: JsonValueKind.Object } parameters)
        {
            if (parameters.TryGetProperty("attachment_id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
                rawId = idElement.GetString()?.Trim();
            offset = ReadNonNegative(parameters, "offset", 0);
            limit = ReadNonNegative(parameters, "limit", MaxCharsPerPage);
        }
        if (limit > MaxCharsPerPage) limit = MaxCharsPerPage;

        if (string.IsNullOrWhiteSpace(rawId) || !Guid.TryParse(rawId, out var attachmentId))
        {
            return Failure(wire,
                "read_attachment needs an 'attachment_id' exactly as shown in the session's file list "
                + "(a guid string). Only files the user attached to this conversation can be read.");
        }

        var rows = await store.ListAsync(scope.SessionId, cancellationToken).ConfigureAwait(false);
        var row = rows.FirstOrDefault(item => item.Id == attachmentId);
        if (row is null)
        {
            return Failure(wire,
                $"No attachment in this session has the id {attachmentId}. "
                + "Attachments are listed with an id: in the context's file section; nothing from another "
                + "conversation is readable here.");
        }

        if (!AttachmentContentPolicy.InlineAsText(row.MediaType))
        {
            return Failure(wire,
                $"{row.FileName} is {row.MediaType} ({row.ContentLength} bytes), and no binary content reaches a "
                + "model in this runtime - there is nothing to read. Tell the user what you cannot see rather than "
                + "describing the file from its name.");
        }

        var content = await store.OpenContentAsync(row, cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            return Failure(wire,
                $"{row.FileName}'s stored bytes are gone (the row outlived its content). Report that; do not reconstruct the file.");
        }

        string page;
        bool moreToRead;
        await using (content)
        {
            using var reader = new StreamReader(content, Encoding.UTF8);
            var consumed = 0;
            var skip = new char[4096];
            while (consumed < offset)
            {
                var want = Math.Min(skip.Length, offset - consumed);
                var skipped = await reader.ReadAsync(skip.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
                if (skipped <= 0) break;
                consumed += skipped;
            }

            // One character more than the page answers "is there more" without reading the file out.
            var pageBuffer = new char[limit + 1];
            var read = await reader.ReadAsync(pageBuffer.AsMemory(0, pageBuffer.Length), cancellationToken).ConfigureAwait(false);
            moreToRead = read > limit;
            page = new string(pageBuffer, 0, Math.Min(read, limit));
        }

        var pageStart = offset;
        var nextOffset = moreToRead ? offset + page.Length : (int?)null;
        return new ToolWireResponseDto
        {
            CallId = wire.ToolCallId,
            IsSuccess = true,
            Result = JsonSerializer.SerializeToElement(new
            {
                attachment_id = row.Id,
                file_name = row.FileName,
                media_type = row.MediaType,
                content_length_bytes = row.ContentLength,
                offset = pageStart,
                chars_returned = page.Length,
                content = page,
                has_more = moreToRead,
                next_offset = nextOffset,
            }),
        };
    }

    private static int ReadNonNegative(JsonElement parameters, string name, int fallback) =>
        parameters.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) && value >= 0
            ? value
            : fallback;

    private static ToolWireResponseDto Failure(ToolWireRequestDto wire, string error) =>
        new() { CallId = wire.ToolCallId, IsSuccess = false, Error = error };
}
