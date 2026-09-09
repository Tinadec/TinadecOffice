using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TinadecTools.Abstractions;

internal delegate ValueTask<ToolCallResponse<JsonElement>> ToolHandlerDelegate(
    ToolCallRequest<JsonElement> request,
    CancellationToken cancellationToken);

/// <summary>A registered tool's manifest descriptor exposed via the <c>#manifest</c> protocol call.</summary>
internal sealed record ToolDescriptor
{
    public string Id { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool RequiresApproval { get; init; }
    public string InputSchemaJson { get; init; } = "{\"type\":\"object\",\"additionalProperties\":true}";
    public string Risk { get; init; } = "low";
    public bool MutatesWorkspace { get; init; }
    public string RetrySafety { get; init; } = "safe";
    public IReadOnlyList<string> ConfirmationFields { get; init; } = [];
}

internal static class ToolRegistry
{
    internal const string ManifestToolId = "#manifest";

    private static readonly Dictionary<string, ToolHandlerDelegate> Handlers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ToolDescriptor> Descriptors = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object RegistrationLock = new();

    public static void Register(string toolId, ToolHandlerDelegate handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentNullException.ThrowIfNull(handler);

        lock (RegistrationLock)
        {
            Handlers[toolId] = handler;
            if (!Descriptors.ContainsKey(toolId))
            {
                Descriptors[toolId] = new ToolDescriptor { Id = toolId };
            }
        }
    }

    /// <summary>
    /// Registers a delegate handler with full manifest metadata. Used for tools
    /// that need the raw request (e.g. streaming terminal events correlated by
    /// call id) rather than the source-generated typed wrapper.
    /// </summary>
    public static void Register(
        string toolId,
        ToolHandlerDelegate handler,
        bool requiresApproval,
        string? description = null,
        string? inputSchemaJson = null,
        string? risk = null,
        bool? mutatesWorkspace = null,
        string? retrySafety = null,
        IReadOnlyList<string>? confirmationFields = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentNullException.ThrowIfNull(handler);

        lock (RegistrationLock)
        {
            Handlers[toolId] = handler;
            var mutates = mutatesWorkspace ?? requiresApproval;
            Descriptors[toolId] = new ToolDescriptor
            {
                Id = toolId,
                Description = description ?? string.Empty,
                RequiresApproval = requiresApproval,
                InputSchemaJson = inputSchemaJson ?? "{\"type\":\"object\",\"additionalProperties\":true}",
                Risk = risk ?? (requiresApproval ? "high" : "low"),
                MutatesWorkspace = mutates,
                RetrySafety = retrySafety ?? (mutates ? "unsafe" : "safe"),
                ConfirmationFields = confirmationFields ?? []
            };
        }
    }

    public static void Register<TArgs, TResult>(ToolHandlerBase<TArgs, TResult> handler)
        where TArgs : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        Register(handler.ToolId, handler.HandleAsync);
    }

    public static void Register<TArgs, TResult>(
        string toolId,
        Func<TArgs, CancellationToken, ValueTask<TResult>> handler,
        JsonTypeInfo<TArgs> argsTypeInfo,
        JsonTypeInfo<TResult> resultTypeInfo,
        bool requiresApproval = false,
        string? description = null,
        string? inputSchemaJson = null,
        string? risk = null,
        bool? mutatesWorkspace = null,
        string? retrySafety = null,
        IReadOnlyList<string>? confirmationFields = null)
        where TArgs : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(argsTypeInfo);
        ArgumentNullException.ThrowIfNull(resultTypeInfo);

        ToolHandlerDelegate wrapper = async (request, cancellationToken) =>
        {
            if (requiresApproval && !request.Approved)
            {
                return new ToolCallResponse<JsonElement>
                {
                    CallId = request.ToolCallId,
                    IsSuccess = false,
                    Response = JsonSerializer.SerializeToElement(NotApprovedResponse.MESSAGE, ToolCallJsonContext.Default.String)
                };
            }

            if (request.Params.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                throw new InvalidOperationException($"Tool '{request.ToolId}' requires params.");

            var args = JsonSerializer.Deserialize(request.Params, argsTypeInfo)
                       ?? throw new InvalidOperationException($"Tool '{request.ToolId}' params could not be parsed.");

            var result = await handler(args, cancellationToken).ConfigureAwait(false);

            return new ToolCallResponse<JsonElement>
            {
                CallId = request.ToolCallId,
                IsSuccess = true,
                Response = JsonSerializer.SerializeToElement(result, resultTypeInfo)
            };
        };
        lock (RegistrationLock)
        {
            Handlers[toolId] = wrapper;
            var mutates = mutatesWorkspace ?? requiresApproval;
            Descriptors[toolId] = new ToolDescriptor
            {
                Id = toolId,
                Description = description ?? string.Empty,
                RequiresApproval = requiresApproval,
                InputSchemaJson = inputSchemaJson ?? "{\"type\":\"object\",\"additionalProperties\":true}",
                Risk = risk ?? (requiresApproval ? "high" : "low"),
                MutatesWorkspace = mutates,
                RetrySafety = retrySafety ?? (mutates ? "unsafe" : "safe"),
                ConfirmationFields = confirmationFields ?? []
            };
        }
    }

    /// <summary>Returns every registered tool's descriptor, ordered by id, for manifest reporting.
    /// Reserved control tools (ids prefixed with '#') are transport-internal and excluded.</summary>
    public static IReadOnlyList<ToolDescriptor> ListTools()
    {
        lock (RegistrationLock)
        {
            return Descriptors.Values
                .Where(x => !x.Id.StartsWith('#'))
                .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public static bool TryResolve(string toolId, out ToolHandlerDelegate handler)
    {
        return Handlers.TryGetValue(toolId, out handler!);
    }

    /// <summary>Looks up a registered tool's descriptor. Used by the dispatch loop to gate concurrency.</summary>
    public static bool TryGetDescriptor(string toolId, out ToolDescriptor descriptor)
    {
        lock (RegistrationLock)
        {
            return Descriptors.TryGetValue(toolId, out descriptor!);
        }
    }

    public static ValueTask<ToolCallResponse<JsonElement>> DispatchAsync(
        ToolCallRequest<JsonElement> request,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(request.ToolId, ManifestToolId, StringComparison.OrdinalIgnoreCase))
        {
            var tools = ListTools()
                .Select(x => new ToolManifestEntry
                {
                    Id = x.Id,
                    Description = x.Description,
                    RequiresApproval = x.RequiresApproval,
                    InputSchema = ParseInputSchema(x.InputSchemaJson, x.Id),
                    Risk = x.Risk,
                    MutatesWorkspace = x.MutatesWorkspace,
                    RetrySafety = x.RetrySafety,
                    ConfirmationFields = x.ConfirmationFields.ToList()
                })
                .ToList();
            var manifest = new ToolManifest
            {
                ProtocolVersion = 2,
                Tools = tools,
                ManifestHash = ToolManifestHash.Compute(tools)
            };
            return ValueTask.FromResult(new ToolCallResponse<JsonElement>
            {
                CallId = request.ToolCallId,
                IsSuccess = true,
                Response = JsonSerializer.SerializeToElement(manifest, ToolCallJsonContext.Default.ToolManifest)
            });
        }

        if (!TryResolve(request.ToolId, out var handler))
            throw new InvalidOperationException($"Unknown tool '{request.ToolId}'.");

        return handler(request, cancellationToken);
    }

    private static JsonElement ParseInputSchema(string json, string toolId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Input schema must be a JSON object.");
            }
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Tool '{toolId}' has an invalid manifest input schema.", ex);
        }
    }
}
