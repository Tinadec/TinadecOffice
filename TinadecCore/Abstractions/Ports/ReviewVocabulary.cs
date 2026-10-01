using System.Globalization;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// The closed vocabularies shared by the human review queues (memory candidates,
/// promoted memory, generated agent candidates). A filter over a closed set can be
/// rejected when it names something outside the set; a filter Core stays silent about
/// turns a typo into "nothing to review", which is the one answer a reviewer cannot act
/// on. Both the write path and the read path check against these sets, so the queue and
/// the review surface cannot drift apart.
/// </summary>
public static class ReviewVocabulary
{
    /// <summary>Canonical spellings. Stored rows hold exactly these; a filter is lowered onto them.</summary>
    public static readonly IReadOnlySet<string> CandidateStatuses = new HashSet<string>(StringComparer.Ordinal) { "proposed", "promoted", "rejected" };
    public static readonly IReadOnlySet<string> ItemStatuses = new HashSet<string>(StringComparer.Ordinal) { "active", "revoked" };
    public static readonly IReadOnlySet<string> Scopes = new HashSet<string>(StringComparer.Ordinal) { "principal", "workspace", "project", "agent" };

    /// <summary>
    /// Bounds one page of review. Listing reads a content blob per row, so a caller must
    /// not be able to ask for the whole store in one call.
    /// </summary>
    public const int MaxLimit = 500;

    /// <summary>
    /// Normalizes a filter value, or throws when a closed vocabulary was named wrongly.
    /// An open vocabulary (memory kind) is trimmed but never validated — there is no
    /// complete list to reject against — and it keeps its spelling, because a kind is
    /// stored exactly as it was proposed and a retyped filter must match that spelling,
    /// not a guess at it.
    /// </summary>
    public static string? Normalize(string? value, IReadOnlySet<string>? closedSet, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (closedSet is null) return trimmed;
        var canonical = trimmed.ToLowerInvariant();
        if (!closedSet.Contains(canonical))
        {
            throw new ReviewFilterValueException(
                fieldName,
                "INVALID_REQUEST",
                $"Unknown {fieldName} '{value}'. Expected one of: {string.Join(", ", closedSet.OrderBy(x => x, StringComparer.Ordinal))}.");
        }
        return canonical;
    }

    /// <summary>
    /// Parses a wire-side page size. A value that cannot be a count is rejected with the
    /// field it got wrong instead of being dropped: a client that sends limit=abc and gets
    /// an unbounded list has no way to learn its filter never applied.
    /// </summary>
    public static int? ParseLimit(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            throw new ReviewFilterValueException("limit", "INVALID_REQUEST", $"limit must be a non-negative integer, but '{raw}' is not.");
        }
        return parsed;
    }

    /// <summary>
    /// Bounds a page size for storage work. Zero means "no rows", which is taken literally;
    /// null means the caller did not ask for a page and gets the whole queue, as before.
    /// </summary>
    public static int? ClampLimit(int? limit)
    {
        if (limit is null) return null;
        return Math.Min(Math.Max(limit.Value, 0), MaxLimit);
    }

    /// <summary>
    /// Parses an id filter. A filter that cannot name any row is rejected with the code of
    /// the field it got wrong rather than left to match nothing: an unparseable id is the
    /// caller's mistake, while "no rows" is a fact about the workspace, and a client that
    /// receives the second answer for the first mistake will keep asking.
    /// </summary>
    public static Guid? ParseId(string? raw, string fieldName, string code)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!Guid.TryParse(raw.Trim(), out var parsed) || parsed == Guid.Empty)
        {
            throw new ReviewFilterValueException(fieldName, code, $"{fieldName} must be an identifier, but '{raw}' is not.");
        }
        return parsed;
    }
}

/// <summary>
/// A filter value the review queue cannot use. Its own type because "you named this field
/// wrongly" and "this row does not exist" both arrive as bad requests and mean different
/// things to the caller; the carried code names which knob broke.
/// </summary>
public sealed class ReviewFilterValueException : ArgumentException
{
    public ReviewFilterValueException(string fieldName, string code, string message) : base(message)
    {
        FieldName = fieldName;
        Code = code;
    }

    public string FieldName { get; }

    /// <summary>Core machine code for this failure, for example INVALID_RUN_ID.</summary>
    public string Code { get; }
}
