namespace TinadecCore.AcpServer;

/// <summary>
/// Command-line options for <c>--acp</c> stdio Agent Server mode.
/// Credentials such as <see cref="HostToken"/> are redacted from <see cref="ToString"/> so diagnostic
/// formatting can never leak <c>TINADEC_HOST_CONTROL_TOKEN</c> to <c>stderr</c>.
/// </summary>
public sealed class AcpCliOptions
{
    public const string CoreUrlEnvVar = "TINADEC_CORE_URL";
    public const string HostTokenEnvVar = "TINADEC_HOST_CONTROL_TOKEN";

    public bool IsAcp { get; init; }

    public Uri? CoreUrl { get; init; }

    public string? HostToken { get; init; }

    public string? StorageRoot { get; init; }

    public IReadOnlyList<string> ForwardedHostArgs { get; init; } = [];

    public bool IsRemoteAttachMode => CoreUrl is not null;

    public static bool ContainsAcpFlag(string[]? args) =>
        args is { Length: > 0 } && args.Any(arg => string.Equals(arg, "--acp", StringComparison.OrdinalIgnoreCase));

    public static AcpCliOptions Parse(string[] args, Func<string, string?>? environmentReader = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var readEnv = environmentReader ?? Environment.GetEnvironmentVariable;

        var isAcp = false;
        string? rawCoreUrl = null;
        string? rawHostToken = null;
        string? rawStorageRoot = null;
        var forwarded = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--acp", StringComparison.OrdinalIgnoreCase))
            {
                isAcp = true;
                continue;
            }

            if (TryMatchOption(args, ref i, "--core-url", out var coreUrlValue))
            {
                rawCoreUrl = coreUrlValue;
                continue;
            }

            if (TryMatchOption(args, ref i, "--host-token", out var tokenValue))
            {
                rawHostToken = tokenValue;
                continue;
            }

            if (TryMatchOption(args, ref i, "--storage-root", out var storageRootValue))
            {
                rawStorageRoot = storageRootValue;
                continue;
            }

            forwarded.Add(arg);
        }

        if (string.IsNullOrWhiteSpace(rawCoreUrl))
        {
            rawCoreUrl = readEnv(CoreUrlEnvVar);
        }

        if (string.IsNullOrWhiteSpace(rawHostToken))
        {
            rawHostToken = readEnv(HostTokenEnvVar);
        }

        Uri? parsedCoreUrl = null;
        if (!string.IsNullOrWhiteSpace(rawCoreUrl))
        {
            var trimmedUrl = rawCoreUrl.Trim();
            if (!Uri.TryCreate(trimmedUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException($"--core-url must be an absolute http:// or https:// URL (received '{trimmedUrl}').");
            }

            parsedCoreUrl = uri;
        }

        string? normalizedStorageRoot = null;
        if (!string.IsNullOrWhiteSpace(rawStorageRoot))
        {
            var trimmedRoot = rawStorageRoot.Trim();
            if (!Path.IsPathFullyQualified(trimmedRoot))
            {
                throw new ArgumentException($"--storage-root must be an absolute path (received '{trimmedRoot}').");
            }

            normalizedStorageRoot = Path.GetFullPath(trimmedRoot);
        }

        return new AcpCliOptions
        {
            IsAcp = isAcp,
            CoreUrl = parsedCoreUrl,
            HostToken = string.IsNullOrEmpty(rawHostToken) ? null : rawHostToken,
            StorageRoot = normalizedStorageRoot,
            ForwardedHostArgs = forwarded
        };
    }

    public override string ToString() =>
        $"AcpCliOptions(IsAcp={IsAcp}, Mode={(IsRemoteAttachMode ? "RemoteHttp" : "Embedded")}, CoreUrl={CoreUrl?.ToString() ?? "<none>"}, StorageRoot={StorageRoot ?? "<default>"}, HasHostToken={HostToken is not null})";

    private static bool TryMatchOption(string[] args, ref int index, string optionName, out string value)
    {
        var current = args[index];
        if (string.Equals(current, optionName, StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            {
                throw new ArgumentException($"CLI option '{optionName}' requires a non-empty value.");
            }

            value = args[++index];
            return true;
        }

        var prefix = optionName + "=";
        if (current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var inline = current[prefix.Length..];
            if (string.IsNullOrWhiteSpace(inline))
            {
                throw new ArgumentException($"CLI option '{optionName}' requires a non-empty value.");
            }

            value = inline;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
