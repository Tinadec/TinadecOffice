using System.Text.Json;

namespace TinadecCore.Models.Harness.Acp;

/// <summary>
/// Answers the requests an agent sends to the client. Lifetime is per session, not per process: a
/// router that parks a run on a pending approval has to know which session it parked, and closing one
/// session's interactions must not answer another's.
/// </summary>
internal interface IAcpInteractionRouter
{
    /// <summary>
    /// Handles one agent→client request and returns the result to send back. Throwing
    /// <see cref="AcpMethodNotFoundException"/> declines it as -32601, which lets the agent's turn
    /// continue instead of losing it.
    /// </summary>
    Task<JsonElement> RouteAsync(string method, JsonElement @params, IAcpTurnObserver observer, CancellationToken cancellationToken);

    /// <summary>Resolves everything still outstanding, each with its own cancel value. Idempotent.</summary>
    void CloseAll();

    /// <summary>
    /// Requests waiting on a human. The turn's drain window must stay open while this is above zero:
    /// an agent that is blocked on a permission question has not gone quiet, and closing the window
    /// would truncate the answer that follows the decision.
    /// </summary>
    int PendingCount { get; }
}

/// <summary>Session and teammate metadata supplied when constructing a per-session ACP interaction router.</summary>
internal sealed record AcpInteractionContext(
    string SessionId,
    string? RunId = null,
    string? TeammateHandle = null,
    string? WorkingDirectory = null,
    IReadOnlyList<string>? WriteScope = null);

/// <summary>Creates the per-session router. Round 2 swaps what this returns and nothing else changes.</summary>
internal interface IAcpInteractionRouterFactory
{
    IAcpInteractionRouter Create(string sessionId);

    IAcpInteractionRouter Create(AcpInteractionContext context) => Create(context.SessionId);
}

/// <summary>
/// Round 1's router: it refuses permission requests on the user's behalf and declines vendor-specific
/// questions it has no bridge for.
/// <para>
/// Refusing rather than throwing for <c>session/request_permission</c> is a deliberate behaviour
/// change against the client it replaces. Abandoning a turn because the agent asked a question throws
/// away everything it had already produced and leaves the user with a bare exception; refusing lets
/// the agent finish and report. The refusal is not silent — it is handed to the turn observer so it
/// can be journaled.
/// </para>
/// <para>
/// Declining <c>fs</c>/<c>terminal</c> capability does not stop the harness from writing files with
/// its own tools, so refusing here is not what makes a connected harness safe. The governed scratch
/// <c>cwd</c> is what makes it safe; this class only makes the refusal honest.
/// </para>
/// </summary>
internal sealed class RefusingAcpInteractionRouter(string sessionId) : IAcpInteractionRouter
{
    /// <summary>Stable reason string so a run journal can tell "nobody answered" from "an agent said no".</summary>
    public const string NoApprovalBridge = "round1_no_approval_bridge";

    private static readonly IReadOnlyList<string> RejectFirst = ["reject_once", "reject_always"];

    private const int MaxLoggedMethods = 64;

    /// <summary>
    /// Vendor methods that were actually requested. Populated as they arrive, because the honest list
    /// of what real harnesses ask for cannot be written from a specification — batch 2 needs the
    /// observed names to decide which of them deserve a bridge.
    /// </summary>
    public static IReadOnlyCollection<string> ObservedUnsupportedMethods => Logged;

    private static readonly HashSet<string> Logged = new(StringComparer.Ordinal);
    private static readonly object LoggedGate = new();

    public string SessionId { get; } = sessionId;

    public int PendingCount => 0;

    public void CloseAll()
    {
    }

    internal static void RecordUnsupportedMethod(string method)
    {
        lock (LoggedGate)
        {
            // Bounded: an agent in a loop could otherwise grow this without limit inside one process.
            if (Logged.Count < MaxLoggedMethods) Logged.Add(method);
        }
    }

    public Task<JsonElement> RouteAsync(string method, JsonElement @params, IAcpTurnObserver observer, CancellationToken cancellationToken)
    {
        if (method == "session/request_permission") return Task.FromResult(Refuse(@params, observer));

        RecordUnsupportedMethod(method);
        throw new AcpMethodNotFoundException(method);
    }

    private JsonElement Refuse(JsonElement @params, IAcpTurnObserver observer)
    {
        var options = ReadOptions(@params);
        var toolCallId = ReadString(@params, "toolCall", "toolCallId");
        var title = ReadString(@params, "toolCall", "title");

        var reject = RejectFirst
            .Select(kind => options.FirstOrDefault(option => option.Kind == kind))
            .FirstOrDefault(option => option is not null);

        if (reject is null)
        {
            observer.PermissionRefused(new AcpPermissionRefusal(toolCallId, title, null, NoApprovalBridge));
            return AcpWire.ToParams(new AcpPermissionResult(new AcpPermissionOutcome("cancelled")));
        }

        observer.PermissionRefused(new AcpPermissionRefusal(toolCallId, title, reject.OptionId, NoApprovalBridge));
        return AcpWire.ToParams(new AcpPermissionResult(new AcpPermissionOutcome("selected", reject.OptionId)));
    }

    internal static IReadOnlyList<AcpPermissionOption> ReadOptions(JsonElement @params)
    {
        if (!@params.TryGetProperty("options", out var element) || element.ValueKind != JsonValueKind.Array) return [];
        return element.EnumerateArray()
            .Where(candidate => candidate.ValueKind == JsonValueKind.Object)
            .Select(candidate => new AcpPermissionOption(
                ReadRequiredString(candidate, "optionId"),
                candidate.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null,
                ReadRequiredString(candidate, "kind")))
            .ToList();
    }

    private static string ReadRequiredString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    internal static string? ReadString(JsonElement root, string objectProperty, string valueProperty)
    {
        if (!root.TryGetProperty(objectProperty, out var nested) || nested.ValueKind != JsonValueKind.Object) return null;
        return nested.TryGetProperty(valueProperty, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}

/// <summary>Supplies <see cref="RefusingAcpInteractionRouter"/> for every session.</summary>
internal sealed class RefusingAcpInteractionRouterFactory : IAcpInteractionRouterFactory
{
    public IAcpInteractionRouter Create(string sessionId) => new RefusingAcpInteractionRouter(sessionId);
}
