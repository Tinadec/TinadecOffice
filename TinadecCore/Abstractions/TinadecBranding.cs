using System.Net;
using System.Net.Http.Headers;

namespace TinadecCore.Abstractions;

/// <summary>
/// Single source of truth for the Tinadec brand that is sent to upstream model providers.
/// All outbound HTTP calls to model APIs must carry <c>User-Agent: Tinadec/...</c> and
/// <c>X-Tinadec-Client: Tinadec</c> so gateways (e.g. OpenCode) aggregate by the brand.
/// </summary>
public static class TinadecBranding
{
    public const string Name = "Tinadec";

    private static readonly SocketsHttpHandler SharedExternalHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        MaxConnectionsPerServer = 64,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    };

    private static readonly SocketsHttpHandler SharedLoopbackHandler = new()
    {
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
        ConnectTimeout = TimeSpan.FromSeconds(10),
        MaxConnectionsPerServer = 64,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    };

    /// <summary>e.g. Tinadec/1.0.0</summary>
    public static string UserAgent => $"{Name}/{Version}";

    private static string Version
    {
        get
        {
            var v = typeof(TinadecBranding).Assembly.GetName().Version;
            return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>Applies Tinadec headers to an existing HttpClient.</summary>
    public static void Apply(HttpClient client)
    {
        // ProductInfoHeaderValue ensures correct formatting: Tinadec/1.0.0
        var version = typeof(TinadecBranding).Assembly.GetName().Version;
        var versionString = version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        try { client.DefaultRequestHeaders.UserAgent.ParseAdd(new ProductInfoHeaderValue(Name, versionString).ToString()); } catch { /* best effort */ }
        if (!client.DefaultRequestHeaders.Contains("X-Tinadec-Client"))
        {
            try { client.DefaultRequestHeaders.Add("X-Tinadec-Client", Name); } catch { }
        }
    }

    public static SocketsHttpHandler HandlerFor(Uri? uri) =>
        uri is not null && uri.IsLoopback ? SharedLoopbackHandler : SharedExternalHandler;

    /// <summary>Creates a new HttpClient already branded backed by the shared pooled handler.</summary>
    public static HttpClient CreateClient()
    {
        var client = new HttpClient(SharedExternalHandler, disposeHandler: false);
        Apply(client);
        return client;
    }

    public static HttpClient CreateClient(Uri baseAddress)
    {
        var client = new HttpClient(HandlerFor(baseAddress), disposeHandler: false) { BaseAddress = baseAddress };
        Apply(client);
        return client;
    }

    public static HttpClient CreateLongLivedClient(Uri? baseAddress = null, bool forceNoProxy = false)
    {
        var handler = forceNoProxy ? SharedLoopbackHandler : HandlerFor(baseAddress);
        var client = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        if (baseAddress is not null)
        {
            client.BaseAddress = baseAddress;
        }
        Apply(client);
        return client;
    }
}
