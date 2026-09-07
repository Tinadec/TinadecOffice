using System.Text.RegularExpressions;

namespace TinadecCore.DmaEA;

/// <summary>
/// Reasoning-model-tolerant helpers for consuming model text output.
///
/// Models with thinking/reasoning (DeepSeek-R1/V4, QwQ, Qwen3, o-series behind
/// proxies, etc.) do not emit clean payloads: they wrap answers in
/// <c>&lt;think&gt;</c> / <c>&lt;thinking&gt;</c> blocks, leak orphan close tags,
/// embed the real JSON inside prose and markdown fences, or interleave reasoning
/// with the payload. The OpenAI-compatible <c>/chat/completions</c> connector also
/// drops the non-standard <c>reasoning_content</c> field, so reasoning arrives
/// INLINE in message content (the separate-channel case is already excluded from
/// <c>ChatResponse.Text</c> by Microsoft.Extensions.AI).
///
/// Every model-output consumption point in DmaEA (planning, supervision, lane
/// gating, capability advice, curation, context compression, the meeting answer,
/// and the worker turn) routes through here so a reasoning preamble never breaks
/// structured parsing or leaks thinking tags into user-facing text.
/// </summary>
internal static partial class ModelOutputText
{
    // A complete <think> / <thinking> block: DOTALL, non-greedy, all occurrences.
    // Source-generated (not RegexOptions.Compiled): faster to warm up and avoids a
    // runtime-compiled-regex matching anomaly observed with \b + alternation + a lazy
    // quantifier on single-line content under .NET 10.
    [GeneratedRegex(@"<(?:think|thinking)\b[^>]*>[\s\S]*?</(?:think|thinking)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();

    // Orphan open/close tags a provider leaves behind when it half-strips a block
    // (e.g. a leading open tag removed but the closing tag kept inline).
    [GeneratedRegex(@"</?(?:think|thinking)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex OrphanThinkTag();

    // Bound on how many balanced candidates we hand back, to cap pathological input.
    private const int MaxCandidates = 32;

    /// <summary>
    /// Removes inline reasoning (think/thinking blocks and orphan tags) from model
    /// text. Separate-channel reasoning (M.E.AI <c>ReasoningContent</c>) is already
    /// excluded from <c>ChatResponse.Text</c>, so only the inline case is handled.
    /// Untagged prose is deliberately preserved: it cannot be told apart from the
    /// real answer without semantic understanding.
    /// </summary>
    public static string StripReasoning(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var stripped = ThinkBlock().Replace(text, " ");
        stripped = OrphanThinkTag().Replace(stripped, " ");
        return stripped;
    }

    /// <summary>Reasoning-stripped, trimmed answer text for user-facing/worker output.</summary>
    public static string AnswerText(string? text) => StripReasoning(text).Trim();

    /// <summary>
    /// Returns every balanced top-level JSON array (<paramref name="array"/> = true)
    /// or object (false) candidate found in reasoning-stripped text, in document
    /// order. Callers try each candidate against their schema and keep the first
    /// that deserializes, which survives reasoning prose, markdown fences, payloads
    /// wrapped in surrounding text, and even schema-shape drift (an array nested in
    /// an object, or vice versa). Truncated (unbalanced) spans are skipped.
    /// </summary>
    public static IReadOnlyList<string> ExtractJsonCandidates(string? text, bool array)
    {
        var source = StripReasoning(text);
        if (string.IsNullOrWhiteSpace(source)) return [];
        var open = array ? '[' : '{';
        var close = array ? ']' : '}';
        var candidates = new List<string>();
        for (var i = 0; i < source.Length && candidates.Count < MaxCandidates; i++)
        {
            if (source[i] != open) continue;
            var end = ScanBalanced(source, i, open, close);
            if (end < 0) continue; // unbalanced/truncated — try the next opener
            candidates.Add(source[i..(end + 1)]);
            i = end; // resume scanning after this candidate
        }
        return candidates;
    }

    /// <summary>
    /// Scans from <paramref name="start"/> (an opener) to its matching closer,
    /// tracking nesting depth and ignoring bracket characters that appear inside
    /// JSON strings (with backslash-escape handling). Returns the closer index, or
    /// -1 when the span never balances (truncated output).
    /// </summary>
    private static int ScanBalanced(string source, int start, char open, char close)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < source.Length; i++)
        {
            var c = source[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == open) depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }
}
