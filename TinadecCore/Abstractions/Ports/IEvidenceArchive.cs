using System.Text;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// What the evidence archive keeps, verbatim (architecture §6, todo R4): the lower layers' own words,
/// so a summary can always be traced back to what was actually said. Summaries compress; the archive
/// never does.
/// </summary>
public static class EvidenceKinds
{
    /// <summary>A finished task's result: status, answer, evidence and criteria verdicts.</summary>
    public const string TaskResult = "task_result";
    /// <summary>A governance report filed in the organization.</summary>
    public const string Report = "report";
    /// <summary>What a standing member concluded in a turn.</summary>
    public const string MemberTurn = "member_turn";
    /// <summary>A context-compression summary: the upper layer, archived next to what it summarised.</summary>
    public const string Summary = "summary";

    public static readonly IReadOnlyList<string> All = [TaskResult, Report, MemberTurn, Summary];
}

/// <param name="SourceKey">Idempotency key within the session (e.g. <c>task:{id}:{attempt}</c>): appending the same source twice keeps one entry.</param>
public sealed record EvidenceEntry(
    Guid TenantId,
    Guid WorkspaceId,
    Guid SessionId,
    Guid? RunId,
    Guid? TaskId,
    string Kind,
    string Title,
    string? Author,
    string Content,
    string SourceKey);

/// <summary>A recall is always inside one session of one tenant: the caller's, never one it names.</summary>
public sealed record EvidenceRecallQuery(
    Guid TenantId,
    Guid WorkspaceId,
    Guid SessionId,
    string Query,
    IReadOnlyList<string>? Kinds = null,
    Guid? RunId = null,
    int Limit = 8);

/// <param name="MatchedBy"><c>semantic</c>, <c>keyword</c> or <c>both</c>.</param>
public sealed record EvidenceHit(
    Guid EvidenceId,
    string Kind,
    string Title,
    string? Author,
    Guid? RunId,
    Guid? TaskId,
    string Snippet,
    double Score,
    string MatchedBy,
    DateTimeOffset CreatedAt);

/// <param name="Mode"><c>hybrid</c> when the semantic index answered, <c>keyword</c> when it could not (no embedding model, or not indexed yet).</param>
/// <param name="Note">Why the mode is what it is, in a sentence the caller can repeat.</param>
public sealed record EvidenceRecallResult(string Mode, IReadOnlyList<EvidenceHit> Hits, string? Note);

/// <summary>
/// The session's evidence archive: appended to by the engine, the organization and the member runner;
/// read by governance roles through <c>recall_evidence</c>. Recall is semantic when an embedding model is
/// configured and keyword otherwise — it never degrades to "no results" merely because embeddings are
/// missing.
/// </summary>
public interface IEvidenceArchive
{
    /// <summary>Keeps one entry, verbatim (bounded). Returns its id; the existing id when the source was already archived.</summary>
    Task<Guid> AppendAsync(EvidenceEntry entry, CancellationToken cancellationToken = default);

    Task<EvidenceRecallResult> RecallAsync(EvidenceRecallQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Search terms out of free text, for the keyword half of recall. Latin words are lower-cased and
/// kept when at least two characters long; a run of CJK characters becomes its bigrams (and the
/// single character when the run is one long), because Chinese has no spaces to split on and a bigram
/// is the smallest unit that still means something.
/// </summary>
public static class EvidenceTerms
{
    public const int MaxTerms = 12;

    public static IReadOnlyList<string> Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var terms = new List<string>();
        var latin = new StringBuilder();
        var cjk = new StringBuilder();

        void FlushLatin()
        {
            // Path and handle punctuation stays inside a word (docs/a.md, search#1), never at its ends.
            var word = latin.ToString().Trim('.', '-', '/', '#', '_');
            if (word.Length >= 2) Add(word.ToLowerInvariant());
            latin.Clear();
        }
        void FlushCjk()
        {
            var run = cjk.ToString();
            if (run.Length == 1) Add(run);
            for (var index = 0; index + 1 < run.Length; index++) Add(run.Substring(index, 2));
            cjk.Clear();
        }
        void Add(string term)
        {
            if (terms.Count < MaxTerms && !terms.Contains(term, StringComparer.Ordinal)) terms.Add(term);
        }

        foreach (var character in text)
        {
            if (IsCjk(character))
            {
                FlushLatin();
                cjk.Append(character);
            }
            else if (char.IsLetterOrDigit(character) || character is '_' or '.' or '/' or '#' or '-')
            {
                FlushCjk();
                latin.Append(character);
            }
            else
            {
                FlushLatin();
                FlushCjk();
            }
        }
        FlushLatin();
        FlushCjk();
        return terms;
    }

    private static bool IsCjk(char character) =>
        character is >= '一' and <= '鿿' or >= '㐀' and <= '䶿' or >= '぀' and <= 'ヿ' or >= '가' and <= '힯';
}

/// <summary>
/// The <c>recall_evidence</c> call, parsed once for every place that serves it (the dispatcher for a
/// worker, the member runner for a standing member), and its model-facing answer.
/// </summary>
public static class EvidenceRecallArguments
{
    public sealed record Parsed(string? Query, IReadOnlyList<string>? Kinds, Guid? RunId, int Limit, string? Error);

    public static Parsed Parse(System.Text.Json.JsonElement? parameters)
    {
        if (parameters is not { ValueKind: System.Text.Json.JsonValueKind.Object } value)
            return new Parsed(null, null, null, 8, "recall_evidence needs a query.");
        var query = value.TryGetProperty("query", out var q) && q.ValueKind == System.Text.Json.JsonValueKind.String ? q.GetString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(query))
            return new Parsed(null, null, null, 8, "recall_evidence needs a query: keywords, a path, a handle, or a sentence.");
        List<string>? kinds = null;
        if (value.TryGetProperty("kinds", out var k) && k.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            kinds = [];
            foreach (var item in k.EnumerateArray())
            {
                var kind = item.ValueKind == System.Text.Json.JsonValueKind.String ? item.GetString() : null;
                if (kind is null || !EvidenceKinds.All.Contains(kind, StringComparer.Ordinal))
                    return new Parsed(null, null, null, 8, $"'{kind}' is not an evidence kind; use {string.Join(", ", EvidenceKinds.All)}.");
                kinds.Add(kind);
            }
        }
        Guid? runId = null;
        if (value.TryGetProperty("run_id", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            if (!Guid.TryParse(r.GetString(), out var parsedRun))
                return new Parsed(null, null, null, 8, $"'{r.GetString()}' is not a run id from graph_view.");
            runId = parsedRun;
        }
        var limit = value.TryGetProperty("limit", out var l) && l.ValueKind == System.Text.Json.JsonValueKind.Number && l.TryGetInt32(out var parsedLimit)
            ? Math.Clamp(parsedLimit, 1, 20)
            : 8;
        return new Parsed(query, kinds, runId, limit, null);
    }

    public static object ForModel(EvidenceRecallResult result) => new
    {
        mode = result.Mode,
        note = result.Note,
        hits = result.Hits.Select(hit => new
        {
            evidence_id = hit.EvidenceId,
            kind = hit.Kind,
            title = hit.Title,
            author = hit.Author,
            run_id = hit.RunId,
            task_id = hit.TaskId,
            matched_by = hit.MatchedBy,
            created_at = hit.CreatedAt,
            snippet = hit.Snippet,
        }).ToArray()
    };
}
