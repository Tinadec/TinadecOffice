using System.Net;
using System.Net.Sockets;

namespace TinadecTools.Tools.Web;

/// <summary>
/// Refused by policy rather than by the network: the caller must be able to tell
/// "this target may not be reached" apart from "the target could not be reached".
/// </summary>
internal sealed class WebFetchRefusedException : Exception
{
    public WebFetchRefusedException(string reason)
        : base(reason)
    {
        Reason = reason;
    }

    public string Reason { get; }
}

/// <summary>
/// URL and address policy for <c>web_fetch</c>. Every host name is resolved and
/// every candidate address is checked before a socket opens, so a public name
/// that re-points at a private address (DNS rebinding) is refused at connect
/// time instead of at check time.
/// </summary>
internal static class WebFetchGuard
{
    public const int MaxRedirections = 3;

    public static bool TryBuildTarget(string? rawUrl, out Uri? target, out string? error)
    {
        target = null;
        error = null;
        var trimmed = (rawUrl ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            error = "url is required.";
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
        {
            error = $"'{trimmed}' is not an absolute http(s) URL. Pass the full URL, including the scheme.";
            return false;
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Scheme '{parsed.Scheme}' is not allowed: web_fetch speaks http and https only, so file://, gopher:// and custom schemes cannot be reached through it.";
            return false;
        }

        if (parsed.UserInfo.Length > 0)
        {
            error = "URL credentials are refused: web_fetch never forwards a user:password component, because it would be echoed into the run record.";
            return false;
        }

        if (parsed.Host.Length == 0)
        {
            error = $"'{trimmed}' carries no host.";
            return false;
        }

        // A fragment is client-side only; keeping it would put dead bytes on the
        // wire and make the reported URL differ from the one actually fetched.
        target = new UriBuilder(parsed) { Host = parsed.IdnHost, Fragment = string.Empty }.Uri;
        return true;
    }

    /// <summary>
    /// Resolves <paramref name="host"/> and keeps only the addresses this tool may
    /// connect to. An IP literal resolves without touching the network, so both
    /// shapes go through the one path.
    /// </summary>
    public static async Task<IReadOnlyList<IPAddress>> ResolveAllowedAsync(
        string host,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        CancellationToken cancellationToken)
    {
        var name = host.TrimStart('[').TrimEnd(']');
        if (name.Length == 0)
            throw new WebFetchRefusedException($"'{host}' carries no host.");

        IPAddress[] addresses;
        try
        {
            addresses = await resolve(name, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not WebFetchRefusedException)
        {
            throw new WebFetchRefusedException($"Host '{name}' could not be resolved: {ex.Message}");
        }

        var allowed = new List<IPAddress>();
        string? blocked = null;
        foreach (var address in addresses)
        {
            var reason = BlockReason(address);
            if (reason is null)
            {
                allowed.Add(address);
                continue;
            }

            blocked ??= reason;
        }

        if (allowed.Count > 0)
            return allowed;

        throw new WebFetchRefusedException(
            blocked is not null
                ? $"'{name}' resolves only to a {blocked} address ({string.Join(", ", addresses)}), which web_fetch refuses: it reaches public HTTP services, not the machine it runs on or the network around it."
                : $"'{name}' resolved to no address.");
    }

    /// <summary>Returns why an address may not be fetched, or null when it may.</summary>
    /// <remarks>
    /// The ranges are spelled out in bytes rather than through <c>IPAddress</c>
    /// helpers because the helper set differs between target frameworks, and a
    /// silently missing check here is an SSRF hole, not a compile error.
    /// </remarks>
    public static string? BlockReason(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // ::ffff:127.0.0.1 is loopback wearing an IPv6 coat.
        var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        var bytes = ip.GetAddressBytes();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            if (bytes.Length != 4)
                return "malformed IPv4 address";

            if (ip.Equals(IPAddress.Any))
                return "unspecified";

            if (ip.Equals(IPAddress.Broadcast) || bytes[0] >= 240)
                return "reserved";

            if (bytes[0] == 0)
                return "'this network'";

            if (ip.Equals(IPAddress.Loopback) || bytes[0] == 127)
                return "loopback";

            if (bytes[0] == 169 && bytes[1] == 254)
                return "link-local";

            if (IsPrivateV4(bytes))
                return "private";

            if (bytes[0] >= 224 && bytes[0] <= 239)
                return "multicast";

            // 100.64.0.0/10 (carrier-grade NAT) and 198.18.0.0/15 (benchmarking)
            // are neither private nor public in practice: they are plumbing.
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
                return "carrier-grade NAT";

            if (bytes[0] == 198 && bytes[1] == 18)
                return "benchmark";

            // TEST-NET ranges have no reachable host; accepting them would let a
            // prompt report a "successful" fetch of documentation space.
            if ((bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2)
                || (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
                || (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113))
                return "documentation TEST-NET";

            return null;
        }

        if (ip.AddressFamily != AddressFamily.InterNetworkV6 || bytes.Length != 16)
            return "not an IP address";

        if (ip.Equals(IPAddress.IPv6Any))
            return "unspecified";

        if (ip.Equals(IPAddress.IPv6Loopback))
            return "loopback";

        if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
            return "link-local";

        if (bytes[0] == 0xFF)
            return "multicast";

        // fc00::/7 unique-local.
        if ((bytes[0] & 0xFE) == 0xFC)
            return "unique-local";

        // fec0::/10 was site-local and is deprecated, but a stack may still route it.
        if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0xC0)
            return "deprecated site-local";

        // 2001::/32 Teredo encapsulates a v4 target that the v4 checks above never
        // saw; 2002::/16 (6to4) and ::ffff:0:0/96 (v4-mapped, handled earlier) are
        // the other two tunnels that can hide a private v4 inside a public v6 name.
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00)
            return "Teredo";

        if (bytes[0] == 0x20 && bytes[1] == 0x02)
            return "6to4";

        // 64:ff9b::/96 is the NAT64 well-known prefix: it translates to a v4
        // address that the v4 branch above never inspected.
        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9B)
            return "NAT64";

        // 2001:db8::/32 documentation prefix, and 100::/64 discard-only prefix.
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8)
            return "documentation";

        if (bytes[0] == 0x01 && bytes[1] == 0x00)
            return "discard-only";

        return null;
    }

    private static bool IsPrivateV4(byte[] bytes) =>
        bytes[0] == 10
        || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
        || (bytes[0] == 192 && bytes[1] == 168);
}
