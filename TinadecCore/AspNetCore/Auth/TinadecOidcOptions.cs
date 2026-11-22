namespace TinadecCore.AspNetCore.Auth;

/// <summary>
/// Logto OIDC login configuration for the collaborator identity source. The
/// Desktop never calls this directly; the Electron main process opens a small
/// login window against the Gateway, which forwards the OIDC flow to Core.
/// </summary>
public sealed class TinadecOidcOptions
{
    public const string SectionName = "TinadecOidc";

    /// <summary>Issuer root, e.g. <c>https://auth.lincube.top/oidc</c>.</summary>
    public string Authority { get; set; } = "";

    /// <summary>Logto application id (Traditional Web client).</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Client secret exchanged server-side for authorization codes.</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>Post-login callback URL, always the Gateway origin, e.g. <c>http://127.0.0.1:48730/auth/callback</c>.</summary>
    public string RedirectUri { get; set; } = "";

    /// <summary>
    /// Optional OIDC subject allowlist for Web owner binding. When both allowlists are empty,
    /// any authenticated subject is accepted (preserving Desktop Logto sign-in).
    /// </summary>
    public string[] AllowedSubjects { get; set; } = [];

    /// <summary>Optional OIDC verified-email allowlist (case-insensitive) for Web owner binding.</summary>
    public string[] AllowedEmails { get; set; } = [];

    public bool Enabled =>
        !string.IsNullOrWhiteSpace(Authority) &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret) &&
        !string.IsNullOrWhiteSpace(RedirectUri);

    internal bool IsAllowedIdentity(
        OidcIdentity identity,
        IReadOnlyList<string>? extraAllowedSubjects = null,
        IReadOnlyList<string>? extraAllowedEmails = null)
    {
        var subjects = (AllowedSubjects ?? [])
            .Concat(extraAllowedSubjects ?? [])
            .Select(s => s?.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();
        var emails = (AllowedEmails ?? [])
            .Concat(extraAllowedEmails ?? [])
            .Select(e => e?.Trim())
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .ToArray();

        if (subjects.Length == 0 && emails.Length == 0) return true;
        if (subjects.Any(s => string.Equals(s, identity.Subject, StringComparison.Ordinal))) return true;
        if (!string.IsNullOrWhiteSpace(identity.Email)
            && emails.Any(e => string.Equals(e, identity.Email.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        return false;
    }

    /// <summary>The Gateway origin derived from <see cref="RedirectUri"/>; the login window lives there.</summary>
    public string PublicOrigin
    {
        get
        {
            if (string.IsNullOrWhiteSpace(RedirectUri)) return "";
            return Uri.TryCreate(RedirectUri, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : "";
        }
    }

    /// <summary>
    /// Logto's own endpoint (the issuer without its <c>/oidc</c> suffix), where the
    /// Account API lives. Derived rather than configured so the pair cannot drift.
    /// </summary>
    public string Endpoint
    {
        get
        {
            var authority = Authority.TrimEnd('/');
            const string suffix = "/oidc";
            return authority.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? authority[..^suffix.Length]
                : authority;
        }
    }

    /// <summary>Account API endpoint the user's own token writes their profile through.</summary>
    public string AccountApiUrl => Endpoint + "/api/my-account";
}