using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Governance;

internal static class CapabilityRuleMatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool Matches(CapabilityRule rule, CapabilityClaim claim) =>
        WildcardMatch(rule.Capability, claim.Capability, StringComparison.OrdinalIgnoreCase)
        && WildcardMatch(rule.Action, claim.Action, StringComparison.OrdinalIgnoreCase)
        && WildcardMatch(rule.ResourcePattern, claim.Resource, StringComparison.OrdinalIgnoreCase);

    public static bool Covers(CapabilityClaim ceiling, CapabilityClaim requested) =>
        WildcardMatch(ceiling.Capability, requested.Capability, StringComparison.OrdinalIgnoreCase)
        && WildcardMatch(ceiling.Action, requested.Action, StringComparison.OrdinalIgnoreCase)
        && WildcardMatch(ceiling.Resource, requested.Resource, StringComparison.OrdinalIgnoreCase);

    public static bool HasDeny(IEnumerable<CapabilityRule> rules, CapabilityClaim claim) =>
        rules.Any(rule => string.Equals(rule.Effect, "deny", StringComparison.OrdinalIgnoreCase) && Matches(rule, claim));

    public static bool HasAllow(IEnumerable<CapabilityRule> rules, CapabilityClaim claim) =>
        rules.Any(rule => string.Equals(rule.Effect, "allow", StringComparison.OrdinalIgnoreCase) && Matches(rule, claim));

    public static IReadOnlyList<CapabilityRule> NormalizeRules(IEnumerable<CapabilityRule> rules)
    {
        var normalized = NormalizeRulesAllowEmpty(rules);
        if (normalized.Count == 0) throw new ArgumentException("At least one capability rule is required.", nameof(rules));
        return normalized;
    }

    public static IReadOnlyList<CapabilityRule> NormalizeRulesAllowEmpty(IEnumerable<CapabilityRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var normalized = rules.Select(rule => new CapabilityRule(
                NormalizeEffect(rule.Effect),
                NormalizePattern(rule.Capability, nameof(rule.Capability), 256),
                NormalizePattern(rule.Action, nameof(rule.Action), 256),
                NormalizeResource(rule.ResourcePattern, pattern: true)))
            .Distinct()
            .OrderBy(rule => rule.Effect, StringComparer.Ordinal)
            .ThenBy(rule => rule.Capability, StringComparer.Ordinal)
            .ThenBy(rule => rule.Action, StringComparer.Ordinal)
            .ThenBy(rule => rule.ResourcePattern, StringComparer.Ordinal)
            .ToArray();
        return normalized;
    }

    public static CapabilityClaim NormalizeClaim(CapabilityClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        return new CapabilityClaim(
            NormalizeIdentifier(claim.Capability, nameof(claim.Capability), 256),
            NormalizeIdentifier(claim.Action, nameof(claim.Action), 256),
            NormalizeResource(claim.Resource, pattern: false));
    }

    public static CapabilityClaim NormalizeScopeClaim(CapabilityClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        return new CapabilityClaim(
            NormalizePattern(claim.Capability, nameof(claim.Capability), 256),
            NormalizePattern(claim.Action, nameof(claim.Action), 256),
            NormalizeResource(claim.Resource, pattern: true));
    }

    public static string SerializeRules(IReadOnlyList<CapabilityRule> rules) => JsonSerializer.Serialize(rules, JsonOptions);

    public static IReadOnlyList<CapabilityRule> DeserializeRules(string json) =>
        JsonSerializer.Deserialize<CapabilityRule[]>(json, JsonOptions) ?? [];

    public static string SerializeBoundaries(IReadOnlyList<AuthorizationBoundary> boundaries) =>
        JsonSerializer.Serialize(boundaries, JsonOptions);

    public static IReadOnlyList<AuthorizationBoundary> DeserializeBoundaries(string json) =>
        JsonSerializer.Deserialize<AuthorizationBoundary[]>(json, JsonOptions) ?? [];

    public static string HashRules(IReadOnlyList<CapabilityRule> rules) => Sha256(SerializeRules(rules));

    public static string HashBoundaries(IEnumerable<AuthorizationBoundary> boundaries)
    {
        var canonical = boundaries
            .Select(boundary => new AuthorizationBoundary(
                Required(boundary.Name, nameof(boundary.Name), 256),
                NormalizeRulesAllowEmpty(boundary.Rules)))
            .OrderBy(boundary => boundary.Name, StringComparer.Ordinal)
            .ToArray();
        return Sha256(JsonSerializer.Serialize(canonical, JsonOptions));
    }

    public static string HashNonce(string nonce) => Sha256(Required(nonce, nameof(nonce), 512));

    public static string HashText(string value) => Sha256(value);

    public static string Required(string? value, string name, int maxLength)
    {
        var result = value?.Trim() ?? string.Empty;
        if (result.Length == 0) throw new ArgumentException($"{name} is required.", name);
        if (result.Length > maxLength) throw new ArgumentException($"{name} cannot exceed {maxLength} characters.", name);
        return result;
    }

    private static string NormalizeEffect(string effect)
    {
        var normalized = Required(effect, nameof(effect), 16).ToLowerInvariant();
        return normalized is "allow" or "deny"
            ? normalized
            : throw new ArgumentException("Rule effect must be 'allow' or 'deny'.", nameof(effect));
    }

    private static string NormalizeIdentifier(string value, string name, int maxLength)
    {
        var normalized = Required(value, name, maxLength).ToLowerInvariant();
        if (normalized.Contains('*', StringComparison.Ordinal))
            throw new ArgumentException($"{name} cannot contain a wildcard.", name);
        if (normalized.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
            throw new ArgumentException($"{name} contains an unsupported character.", name);
        return normalized;
    }

    private static string NormalizePattern(string value, string name, int maxLength)
    {
        var normalized = Required(value, name, maxLength).ToLowerInvariant();
        var wildcardIndex = normalized.IndexOf('*', StringComparison.Ordinal);
        if (wildcardIndex >= 0 && (wildcardIndex != normalized.Length - 1 || normalized.LastIndexOf('*') != wildcardIndex))
            throw new ArgumentException($"{name} supports only one trailing wildcard.", name);
        var literal = wildcardIndex < 0 ? normalized : normalized[..wildcardIndex];
        if (literal.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
            throw new ArgumentException($"{name} contains an unsupported character.", name);
        return normalized;
    }

    private static string NormalizeResource(string value, bool pattern)
    {
        var normalized = Required(value, nameof(value), 2048).Replace('\\', '/');
        if (normalized == "*" && pattern) return normalized;
        var wildcardIndex = normalized.IndexOf('*', StringComparison.Ordinal);
        if (!pattern && wildcardIndex >= 0)
            throw new ArgumentException("A concrete resource cannot contain a wildcard.", nameof(value));
        if (pattern && wildcardIndex >= 0 && (wildcardIndex != normalized.Length - 1 || normalized.LastIndexOf('*') != wildcardIndex))
            throw new ArgumentException("A resource rule supports only one trailing wildcard.", nameof(value));
        var schemeEnd = normalized.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0 || normalized[..schemeEnd].Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
            throw new ArgumentException("Resource must use a typed '<kind>://<identifier>' form.", nameof(value));
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
            throw new ArgumentException("Resource traversal segments are not allowed.", nameof(value));
        if (normalized.Any(char.IsControl)) throw new ArgumentException("Resource contains a control character.", nameof(value));
        return normalized;
    }

    private static bool WildcardMatch(string pattern, string value, StringComparison comparison)
    {
        var patternIndex = 0;
        var valueIndex = 0;
        var starIndex = -1;
        var matchIndex = 0;
        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length
                && pattern[patternIndex] != '*'
                && string.Equals(pattern[patternIndex].ToString(), value[valueIndex].ToString(), comparison))
            {
                patternIndex++;
                valueIndex++;
                continue;
            }

            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                matchIndex = valueIndex;
                continue;
            }

            if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                valueIndex = ++matchIndex;
                continue;
            }

            return false;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*') patternIndex++;
        return patternIndex == pattern.Length;
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
