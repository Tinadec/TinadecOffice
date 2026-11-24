using System.Text;

namespace TinadecCore.Runtime.ProjectBoard;

/// <summary>
/// Deterministic Base62 Fractional Indexing utilities preserving strict <see cref="StringComparer.Ordinal"/>
/// ordering without trailing <c>'0'</c> characters so midpoint insertions remain unbounded.
/// </summary>
public static class ProjectBoardFractionalIndex
{
    private const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const int Base = 62;
    private const char MidDigit = 'V'; // index 31

    public static bool IsValid(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256)
            return false;

        foreach (var ch in key)
        {
            if (Digits.IndexOf(ch) < 0)
                return false;
        }

        return key[^1] != '0';
    }

    public static string Normalize(string? candidate, IEnumerable<string>? existingKeys = null)
    {
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            var trimmed = candidate.Trim();
            if (trimmed.Length <= 250 && trimmed.All(ch => Digits.IndexOf(ch) >= 0))
            {
                return trimmed[^1] == '0' ? trimmed + MidDigit : trimmed;
            }
        }

        string? maxKey = null;
        if (existingKeys is not null)
        {
            foreach (var key in existingKeys)
            {
                if (string.IsNullOrWhiteSpace(key)) continue;
                var clean = key.Trim();
                if (maxKey is null || string.CompareOrdinal(clean, maxKey) > 0)
                    maxKey = clean;
            }
        }

        return Between(maxKey, null);
    }

    /// <summary>
    /// Computes a Base62 fractional key strictly between <paramref name="before"/> and <paramref name="after"/>
    /// under <see cref="StringComparer.Ordinal"/>.
    /// </summary>
    public static string Between(string? before, string? after)
    {
        var a = string.IsNullOrWhiteSpace(before) ? string.Empty : before.Trim();
        var b = string.IsNullOrWhiteSpace(after) ? string.Empty : after.Trim();

        if (a.Length > 0 && b.Length > 0 && string.CompareOrdinal(a, b) >= 0)
            throw new ArgumentException($"Invalid fractional bounds: '{a}' must be strictly less than '{b}'.");

        if (a.Length == 0 && b.Length == 0)
            return "V";

        if (b.Length == 0)
        {
            for (var i = 0; i < a.Length; i++)
            {
                var idx = DigitIndex(a[i]);
                if (idx < Base - 1)
                {
                    var mid = (idx + Base) / 2;
                    return string.Concat(a.AsSpan(0, i), Digits.AsSpan(mid, 1));
                }
            }

            return a + MidDigit;
        }

        if (a.Length == 0)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < b.Length; i++)
            {
                var idx = DigitIndex(b[i]);
                if (idx > 1)
                {
                    sb.Append(Digits[idx / 2]);
                    return sb.ToString();
                }

                sb.Append('0');
                if (idx == 1 && i == b.Length - 1)
                {
                    sb.Append(MidDigit);
                    return sb.ToString();
                }
            }

            sb.Append(MidDigit);
            return sb.ToString();
        }

        var result = new StringBuilder();
        var pos = 0;
        while (true)
        {
            var digitA = pos < a.Length ? DigitIndex(a[pos]) : 0;
            var digitB = pos < b.Length ? DigitIndex(b[pos]) : Base;

            if (digitA == digitB)
            {
                result.Append(Digits[digitA]);
                pos++;
                continue;
            }

            if (digitB - digitA > 1)
            {
                var mid = (digitA + digitB) / 2;
                result.Append(Digits[mid]);
                return result.ToString();
            }

            // digitB == digitA + 1: carry into next position on the lower bound branch
            result.Append(Digits[digitA]);
            var suffixA = pos + 1 < a.Length ? a[(pos + 1)..] : string.Empty;
            result.Append(Between(suffixA, null));
            return result.ToString();
        }
    }

    private static int DigitIndex(char ch)
    {
        var idx = Digits.IndexOf(ch);
        return idx < 0 ? 0 : idx;
    }
}
