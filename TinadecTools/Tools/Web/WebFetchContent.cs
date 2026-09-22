using System.Globalization;
using System.Text;

namespace TinadecTools.Tools.Web;

/// <summary>
/// What to do with a response body once it has been fetched: which media types
/// can become model-readable text, how to decode them, and how to shrink an HTML
/// page to its prose without spending the whole context window on markup.
/// </summary>
internal static class WebFetchContent
{
    private static readonly string[] TextualPrefixes = ["text/"];

    private static readonly string[] TextualTypes =
    [
        "application/json",
        "application/ld+json",
        "application/geo+json",
        "application/xhtml+xml",
        "application/xml",
        "application/javascript",
        "application/x-javascript",
        "application/manifest+json",
        "application/yaml",
        "application/x-yaml",
        "application/toml",
        "application/sql",
        "application/graphql",
    ];

    private static readonly string[] HtmlTypes = ["text/html", "application/xhtml+xml"];

    private static readonly string[] BlockTags =
    [
        "address", "article", "aside", "blockquote", "br", "dd", "div", "dl", "dt",
        "fieldset", "figcaption", "footer", "form", "h1", "h2", "h3", "h4", "h5",
        "h6", "header", "hr", "li", "main", "nav", "ol", "option", "p", "pre",
        "section", "summary", "table", "tbody", "td", "tfoot", "th", "thead", "tr", "ul",
    ];

    /// <summary>The media type without its parameters, lowercased; empty when absent.</summary>
    public static string MediaTypeOf(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return string.Empty;

        var semicolon = contentType.IndexOf(';');
        var media = semicolon >= 0 ? contentType[..semicolon] : contentType;
        return media.Trim().ToLowerInvariant();
    }

    public static bool IsHtml(string mediaType) => HtmlTypes.Contains(mediaType);

    /// <summary>
    /// Binary media (pdf, images, archives, streams) are refused rather than
    /// returned as bytes: this tool has no way to make them readable, and a
    /// base64 blob in the transcript is neither evidence nor a cost the model can
    /// reason about.
    /// </summary>
    public static bool IsTextual(string mediaType)
    {
        if (mediaType.Length == 0)
            return false;

        if (mediaType.StartsWith('+'))
            return false;

        if (TextualPrefixes.Any(prefix => mediaType.StartsWith(prefix, StringComparison.Ordinal)))
            return true;

        if (mediaType.EndsWith("+json", StringComparison.Ordinal) || mediaType.EndsWith("+xml", StringComparison.Ordinal))
            return true;

        return TextualTypes.Contains(mediaType);
    }

    /// <summary>
    /// Only the decoders the in-box encodings provide are offered; an exotic
    /// declared charset falls back to UTF-8 and says so, because silently
    /// mojibake-ing a page reads to the model like a hostile source. The fallback
    /// is replacement-tolerant on purpose: one bad byte on a page the model asked
    /// for must not turn the whole fetch into an exception.
    /// </summary>
    public static Encoding ResolveEncoding(string? contentType, out string? note)
    {
        note = null;
        var declared = string.Empty;
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            foreach (var part in contentType.Split(';', StringSplitOptions.TrimEntries))
            {
                if (part.StartsWith("charset=", StringComparison.OrdinalIgnoreCase))
                    declared = part["charset=".Length..].Trim('"', ' ');
            }
        }

        if (declared.Length == 0)
            return Encoding.UTF8;

        if (declared.Equals("utf-8", StringComparison.OrdinalIgnoreCase) || declared.Equals("utf8", StringComparison.OrdinalIgnoreCase))
            return Encoding.UTF8;

        if (declared.Equals("utf-16le", StringComparison.OrdinalIgnoreCase) || declared.Equals("utf-16", StringComparison.OrdinalIgnoreCase))
            return Encoding.Unicode;

        if (declared.Equals("utf-16be", StringComparison.OrdinalIgnoreCase))
            return Encoding.BigEndianUnicode;

        if (declared.Equals("iso-8859-1", StringComparison.OrdinalIgnoreCase) || declared.Equals("latin1", StringComparison.OrdinalIgnoreCase))
            return Encoding.Latin1;

        note = $"declared charset '{declared}' is not supported here; the body was decoded as UTF-8, so non-ASCII text may be wrong.";
        return Encoding.UTF8;
    }

    /// <summary>
    /// Markup-stripping reader: drops script/style/comment blocks, turns block
    /// tags into line breaks, resolves the handful of entities that carry
    /// meaning in prose. This is deliberately not an HTML parser — the goal is
    /// readable text, not a document tree.
    /// </summary>
    public static string ToText(string body)
    {
        var builder = new StringBuilder(body.Length / 2);
        var index = 0;
        while (index < body.Length)
        {
            var next = body.IndexOf('<', index);
            if (next < 0)
            {
                AppendText(builder, body[index..]);
                break;
            }

            if (next > index)
                AppendText(builder, body[index..next]);

            if (next + 4 <= body.Length && body.AsSpan(next, 4).SequenceEqual("<!--".AsSpan()))
            {
                var end = body.IndexOf("-->", next + 4, StringComparison.Ordinal);
                index = end < 0 ? body.Length : end + 3;
                continue;
            }

            var close = body.IndexOf('>', next + 1);
            if (close < 0)
                break;

            var tag = ReadTagName(body, next, close);
            if (tag.Equals("script", StringComparison.OrdinalIgnoreCase) || tag.Equals("style", StringComparison.OrdinalIgnoreCase))
            {
                var stop = body.IndexOf($"</{tag}>", close + 1, StringComparison.OrdinalIgnoreCase);
                index = stop < 0 ? body.Length : stop + tag.Length + 3;
                continue;
            }

            if (BlockTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                AppendBreak(builder);

            index = close + 1;
        }

        return Collapse(builder.ToString());
    }

    /// <summary>Cuts at a character ceiling on a code-unit-safe boundary.</summary>
    public static string CapChars(string text, int maxChars, out bool truncated)
    {
        truncated = text.Length > maxChars;
        if (!truncated)
            return text;

        var cut = maxChars;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
            cut--;

        return text[..cut];
    }

    private static string ReadTagName(string body, int open, int close)
    {
        var start = open + 1;
        if (start < close && (body[start] == '/' || body[start] == '!'))
            start++;

        var end = start;
        while (end < close && !char.IsWhiteSpace(body[end]) && body[end] != '/')
            end++;

        return body[start..end].ToLowerInvariant();
    }

    private static void AppendText(StringBuilder builder, string chunk)
    {
        var decoded = DecodeEntities(chunk);
        foreach (var character in decoded)
        {
            if (character == '\r' || character == '\t')
            {
                builder.Append(' ');
                continue;
            }

            if (character == '\n')
            {
                AppendBreak(builder);
                continue;
            }

            // Control characters from a hostile body would otherwise be forwarded
            // verbatim into the model's context and into the desktop transcript.
            if (char.IsControl(character))
                continue;

            builder.Append(character);
        }
    }

    private static void AppendBreak(StringBuilder builder)
    {
        if (builder.Length == 0 || builder[^1] == '\n')
            return;

        builder.Append('\n');
    }

    private static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        var spaces = 0;
        var newlines = 0;
        foreach (var character in text)
        {
            if (character == ' ')
            {
                spaces++;
                newlines = 0;
                continue;
            }

            if (character == '\n')
            {
                newlines++;
                spaces = 0;
                if (newlines <= 2)
                    builder.Append('\n');
                continue;
            }

            if (spaces > 0)
                builder.Append(' ');

            spaces = 0;
            newlines = 0;
            builder.Append(character);
        }

        return builder.ToString().Trim();
    }

    private static string DecodeEntities(string chunk)
    {
        if (!chunk.Contains('&'))
            return chunk;

        var builder = new StringBuilder(chunk.Length);
        var index = 0;
        while (index < chunk.Length)
        {
            var ampersand = chunk.IndexOf('&', index);
            if (ampersand < 0)
            {
                builder.Append(chunk, index, chunk.Length - index);
                break;
            }

            builder.Append(chunk, index, ampersand - index);
            var semicolon = chunk.IndexOf(';', ampersand + 1);
            if (semicolon < 0 || semicolon - ampersand > 12)
            {
                builder.Append('&');
                index = ampersand + 1;
                continue;
            }

            var entity = chunk[(ampersand + 1)..semicolon];
            if (TryDecodeEntity(entity, out var decoded))
            {
                builder.Append(decoded);
            }
            else
            {
                builder.Append('&').Append(entity).Append(';');
            }

            index = semicolon + 1;
        }

        return builder.ToString();
    }

    private static bool TryDecodeEntity(string entity, out string decoded)
    {
        decoded = string.Empty;
        switch (entity)
        {
            case "amp": decoded = "&"; return true;
            case "lt": decoded = "<"; return true;
            case "gt": decoded = ">"; return true;
            case "quot": decoded = "\""; return true;
            case "apos": decoded = "'"; return true;
            case "nbsp": decoded = " "; return true;
            case "ensp":
            case "emsp":
            case "thinsp": decoded = " "; return true;
            case "mdash": decoded = "—"; return true;
            case "ndash": decoded = "–"; return true;
            case "hellip": decoded = "…"; return true;
            case "middot": decoded = "·"; return true;
        }

        if (entity.Length > 2 && entity[0] == '#')
        {
            var hex = entity[1] is 'x' or 'X';
            var digits = hex ? entity[2..] : entity[1..];
            var value = hex
                ? int.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsedHex) ? parsedHex : -1
                : int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;

            // Lone surrogates would not survive a round-trip through the JSON line
            // protocol, so they stay undecoded instead of corrupting the payload.
            if (value is >= 0 and <= 0x10FFFF && (value < 0xD800 || value > 0xDFFF))
            {
                decoded = char.ConvertFromUtf32(value);
                return true;
            }
        }

        return false;
    }
}
