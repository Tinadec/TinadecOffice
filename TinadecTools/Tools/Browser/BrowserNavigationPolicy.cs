using System.Net;
using System.Net.Sockets;

namespace TinadecTools.Tools.Browser;

/// <summary>
/// Navigation policy for the agent browser. The bridge also rejects non-http(s) and metadata
/// literals; this layer is the one that has the richer rules and runs before anything is sent.
///
/// Loopback is allowed on purpose: the built-in browser's original job is previewing the user's
/// local dev server, and the Agent browser has its own isolated profile (no user login state).
/// Private LAN ranges stay blocked by default; metadata/link-local addresses are always blocked.
/// A hostname is not resolved here — Chromium resolves it later, so a DNS name that points at a
/// private address is a documented gap, not a guarantee.
/// </summary>
internal sealed class BrowserNavigationPolicy
{
    private static readonly HashSet<string> BlockedHostNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "metadata.google.internal",
        "metadata.goog",
        "metadata",
    };

    private readonly bool _allowPrivate;

    public BrowserNavigationPolicy(bool allowPrivate = false)
    {
        _allowPrivate = allowPrivate;
    }

    public bool TryNormalize(string? raw, out Uri url, out string error)
    {
        url = null!;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "A URL is required.";
            return false;
        }
        if (raw.Length > 4096)
        {
            error = "The URL is longer than 4096 characters.";
            return false;
        }
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var parsed))
        {
            error = "The URL is not absolute.";
            return false;
        }
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            error = "Only http and https URLs are allowed.";
            return false;
        }
        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "URLs with embedded credentials are not allowed.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(parsed.Host))
        {
            error = "The URL has no host.";
            return false;
        }

        var host = parsed.DnsSafeHost.TrimEnd('.').ToLowerInvariant();
        if (BlockedHostNames.Contains(host))
        {
            error = "The host is a cloud metadata endpoint.";
            return false;
        }
        if (host == "localhost")
        {
            url = parsed;
            return true;
        }
        if (IPAddress.TryParse(host, out var address))
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = address.GetAddressBytes();
                if (address.Equals(IPAddress.Loopback))
                {
                    url = parsed;
                    return true;
                }
                if (address.Equals(IPAddress.Any) || bytes[0] == 0)
                {
                    error = "The address is not routable.";
                    return false;
                }
                if (bytes[0] == 169 && bytes[1] == 254)
                {
                    error = "The address is link-local (cloud metadata range).";
                    return false;
                }
                if (IsPrivateV4(bytes))
                {
                    if (!_allowPrivate)
                    {
                        error = "The address is on a private network; enable private hosts to browse it.";
                        return false;
                    }
                }
                else if (bytes[0] >= 224)
                {
                    error = "The address is multicast or reserved.";
                    return false;
                }
                url = parsed;
                return true;
            }

            if (address.Equals(IPAddress.IPv6Loopback))
            {
                url = parsed;
                return true;
            }
            var v6 = address.GetAddressBytes();
            var uniqueLocal = (v6[0] & 0xFE) == 0xFC; // fc00::/7
            var siteLocal = v6[0] == 0xFE && (v6[1] & 0xC0) == 0xC0; // fec0::/10
            if (address.IsIPv6LinkLocal || siteLocal || address.IsIPv6Multicast || uniqueLocal)
            {
                if (uniqueLocal && _allowPrivate)
                {
                    url = parsed;
                    return true;
                }
                error = "The address is link-local, private, or multicast.";
                return false;
            }
            url = parsed;
            return true;
        }

        url = parsed;
        return true;
    }

    private static bool IsPrivateV4(byte[] bytes)
    {
        if (bytes[0] == 10) return true;
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
        if (bytes[0] == 192 && bytes[1] == 168) return true;
        // CGNAT and benchmarking ranges are not "public" for this purpose either.
        if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;
        if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19)) return true;
        return false;
    }
}
