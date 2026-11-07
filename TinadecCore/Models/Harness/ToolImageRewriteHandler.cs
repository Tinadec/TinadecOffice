using System.Text;
using System.Text.Json;

namespace TinadecCore.Models.Harness;

/// <summary>
/// Turns the <c>image</c> object a capture tool left in its result JSON into the Responses API's
/// native tool-result image form. Wire-probed (see the computer-use design doc): the backend
/// accepts a <c>function_call_output.output</c> that is a content-part array
/// (<c>input_text</c> + <c>input_image</c>) and the model demonstrably reads the image there —
/// while every M.E.AI.OpenAI adapter up to 10.11 serializes a Tool-role message's
/// FunctionResultContent as a plain string <c>output</c> and drops any DataContent. This rewriter
/// runs at the HTTP layer, after the SDK serialized and before the request leaves: it rewrites
/// only <c>function_call_output</c> items whose string output is a JSON object carrying a
/// top-level <c>image</c> object with a readable file. Everything else — including every request
/// without tool images — passes through byte-for-byte unchanged.
/// <para>
/// A result JSON that declares an image whose file cannot be read makes the rewrite throw: in
/// native mode an undeliverable image is a hard failure, never a silent drop.
/// </para>
/// </summary>
internal static class ToolImageOutputRewriter
{
    /// <summary>
    /// Rewrites the Responses request body so tool results carry their images natively.
    /// Returns null when there is nothing to rewrite — callers must then send the original
    /// body unchanged.
    /// </summary>
    public static string? TryRewrite(string requestBody)
    {
        // Cheap gate first: parse only bodies that could contain a tool-result image. The markers
        // are deliberately unquoted — inside a serialized tool-result string the JSON field name
        // is escape-quoted (\"image\"), so a quoted search would miss exactly the bodies we want.
        // A false positive (e.g. an unrelated "images" field) only costs one parse that finds
        // nothing and returns null.
        if (!requestBody.Contains("function_call_output", StringComparison.Ordinal)
            || !requestBody.Contains("image", StringComparison.Ordinal))
            return null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(requestBody);
        }
        catch (JsonException)
        {
            return null;
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("input", out var input)
                || input.ValueKind != JsonValueKind.Array)
                return null;

            var rewritten = false;
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (!property.NameEquals("input"))
                    {
                        property.WriteTo(writer);
                        continue;
                    }
                    writer.WritePropertyName("input"u8);
                    writer.WriteStartArray();
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object
                            && item.TryGetProperty("type", out var type)
                            && type.ValueKind == JsonValueKind.String
                            && type.GetString() == "function_call_output"
                            && item.TryGetProperty("output", out var output)
                            && output.ValueKind == JsonValueKind.String
                            && TryBuildOutputParts(output.GetString()!, out var partsJson))
                        {
                            // the native form: same item, output as a content-part array
                            writer.WriteStartObject();
                            foreach (var field in item.EnumerateObject())
                            {
                                if (field.NameEquals("output")) continue;
                                field.WriteTo(writer);
                            }
                            writer.WritePropertyName("output"u8);
                            using (var parts = JsonDocument.Parse(partsJson))
                            {
                                parts.RootElement.WriteTo(writer);
                            }
                            writer.WriteEndObject();
                            rewritten = true;
                        }
                        else
                        {
                            item.WriteTo(writer);
                        }
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            return rewritten ? Encoding.UTF8.GetString(buffer.WrittenSpan) : null;
        }
    }

    /// <summary>
    /// Builds the content-part array for one tool output: the original result JSON as
    /// <c>input_text</c> plus the image file as a base64 <c>input_image</c>. False (and no
    /// rewrite) when the output is not a JSON object with a top-level image object.
    /// </summary>
    private static bool TryBuildOutputParts(string output, out string partsJson)
    {
        partsJson = string.Empty;
        JsonDocument outputDocument;
        try
        {
            outputDocument = JsonDocument.Parse(output);
        }
        catch (JsonException)
        {
            return false;
        }
        using (outputDocument)
        {
            var root = outputDocument.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("image", out var image)
                || image.ValueKind != JsonValueKind.Object
                || !image.TryGetProperty("path", out var path)
                || path.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(path.GetString()))
                return false;

            var mediaType = image.TryGetProperty("media_type", out var media) && media.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(media.GetString())
                ? media.GetString()!
                : "image/png";
            // Native mode contract: an image declared in the result must be deliverable. A
            // missing/unreadable file fails the request here instead of silently losing the
            // image on the adapter floor.
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path.GetString()!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                throw new IOException($"Tool-result image '{path.GetString()}' could not be read for native delivery: {ex.Message}", ex);
            }

            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WritePropertyName("type"u8);
                writer.WriteStringValue("input_text");
                writer.WritePropertyName("text"u8);
                writer.WriteStringValue(output);
                writer.WriteEndObject();
                writer.WriteStartObject();
                writer.WritePropertyName("type"u8);
                writer.WriteStringValue("input_image");
                writer.WritePropertyName("image_url"u8);
                writer.WriteStringValue($"data:{mediaType};base64,{Convert.ToBase64String(bytes)}");
                writer.WriteEndObject();
                writer.WriteEndArray();
            }
            partsJson = Encoding.UTF8.GetString(buffer.WrittenSpan);
            return true;
        }
    }
}

/// <summary>
/// The HTTP-layer hook: installed by <c>AgentChatClientFactory</c> on Responses clients so tool
/// results reach the provider in the protocol's native image form. Requests that need no
/// rewrite are re-sent byte-for-byte identical.
/// </summary>
internal sealed class ToolImageRewriteHandler : DelegatingHandler
{
    public ToolImageRewriteHandler() : base(new HttpClientHandler())
    {
    }

    public ToolImageRewriteHandler(HttpMessageHandler innerHandler) : base(innerHandler)
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null
            && request.Method == HttpMethod.Post
            && request.RequestUri is { } uri
            && uri.AbsolutePath.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var rewritten = ToolImageOutputRewriter.TryRewrite(body);
            if (rewritten is not null)
                request.Content = new StringContent(rewritten, Encoding.UTF8, "application/json");
        }
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
