namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// One owner for "can these attachment bytes be shown to a model as text".
///
/// Both readers have to agree: the context builder quotes an attachment inline when it can, and the
/// <c>read_attachment</c> tool refuses when it cannot. A private copy in each is how one of them
/// could start claiming a file the other said was unreadable.
///
/// The rule is deliberately about the media type, not about sniffing the bytes: a caller supplies
/// that type at upload and Core keeps it as display metadata, so it can be wrong. The consequence is
/// bounded both ways - a text file mislabelled as binary reads as "not text" and the model says so,
/// while a binary mislabelled as text is decoded into replacement characters rather than executed
/// anywhere, because these bytes never reach a shell, a browser or a filesystem path.
/// </summary>
public static class AttachmentContentPolicy
{
    /// <summary>
    /// Non-<c>text/*</c> types whose bytes are the content rather than an encoding of something the
    /// model cannot see.
    /// </summary>
    public static readonly IReadOnlyList<string> InlineMediaTypes =
    [
        "application/json",
        "application/xml",
        "application/sql",
        "application/x-sh",
        "application/yaml",
        "application/x-yaml",
        "application/javascript",
        "application/typescript",
        "image/svg+xml",
    ];

    public static bool InlineAsText(string? mediaType) =>
        !string.IsNullOrWhiteSpace(mediaType)
        && (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || InlineMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase));
}
