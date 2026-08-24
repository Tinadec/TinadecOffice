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

    /// <summary>Creates a new HttpClient already branded.</summary>
    public static HttpClient CreateClient()
    {
        var client = new HttpClient();
        Apply(client);
        return client;
    }

    public static HttpClient CreateClient(Uri baseAddress)
    {
        var client = new HttpClient { BaseAddress = baseAddress };
        Apply(client);
        return client;
    }
}
