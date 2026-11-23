using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Context;

/// <summary>
/// One Winnowing fingerprint selected inside a sliding window (<paramref name="Hash"/> at normalized index <paramref name="Pos"/>).
/// </summary>
public readonly record struct WinnowFingerprint(long Hash, int Pos);

/// <summary>
/// 64-character rolling-hash Winnowing context overlap auditor (Schleimer, Wilkerson &amp; Aiken 2003).
/// Exact 1:1 port of <c>contextOverlapWinnowing.ts</c> with server-side corpus gathering, sandbox path
/// containment checks, CJK-aware token estimation, and secret redaction.
/// </summary>
public sealed class ContextOverlapAuditor : IContextOverlapAuditor
{
    public const long Base1 = 131L;
    public const long Mod1 = 67_108_859L;
    public const long Base2 = 137L;
    public const long Mod2 = 67_108_879L;

    public const int DefaultKGramSize = 64;
    public const int DefaultWindowSize = 16;
    public const int DefaultMaxExcerptLength = 180;
    public const int DefaultMaxMatchesPerPair = 12;

    private const int MaxBucketPostings = 64;
    private const int MaxServerSkills = 48;
    private const int MaxSkillBodyChars = 32_000;

    private static readonly Regex WhitespaceCollapseRegex = new(@"\s+", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex BearerSecretRegex = new(@"(?i)(bearer\s+)[A-Za-z0-9._\-]+", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex TokenPrefixSecretRegex = new(@"(?i)\b(?:sk-[A-Za-z0-9_\-]{8,}|ghp_[A-Za-z0-9]{8,}|xox[baprs]-[A-Za-z0-9\-]{8,})\b", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex KeyValueSecretRegex = new(@"(?i)((?:api[_-]?key|token|secret|password|passwd|pwd|authorization)\s*[=:]\s*)([^\s,;""']+)", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private readonly ILifecycleManager? _lifecycle;
    private readonly ISessionLocator? _sessionLocator;
    private readonly IPromptFragmentService? _promptFragments;
    private readonly IToolSkillResourceService? _skillResources;
    private readonly IToolRegistry? _toolRegistry;
    private readonly IToolProvider? _toolProvider;
    private readonly IMcpServerService? _mcpServers;
    private readonly IContextAuditCatalogReader? _catalogReader;

    public ContextOverlapAuditor(
        ILifecycleManager? lifecycle = null,
        ISessionLocator? sessionLocator = null,
        IPromptFragmentService? promptFragments = null,
        IToolSkillResourceService? skillResources = null,
        IToolRegistry? toolRegistry = null,
        IToolProvider? toolProvider = null,
        IMcpServerService? mcpServers = null,
        IContextAuditCatalogReader? catalogReader = null)
    {
        _lifecycle = lifecycle;
        _sessionLocator = sessionLocator;
        _promptFragments = promptFragments;
        _skillResources = skillResources;
        _toolRegistry = toolRegistry;
        _toolProvider = toolProvider;
        _mcpServers = mcpServers;
        _catalogReader = catalogReader;
    }

    /// <summary>
    /// CJK-aware lightweight token estimator (~1.5 CJK chars/token, ~4 Latin chars/token, 0.25 whitespace weight).
    /// Matches <c>estimateTokens</c> in <c>contextOverlapWinnowing.ts</c> 1:1.
    /// </summary>
    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var cjk = 0;
        var nonCjk = 0.0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (IsCjkChar(ch))
            {
                cjk++;
            }
            else if (!IsJsWhitespace(ch))
            {
                nonCjk += 1.0;
            }
            else
            {
                nonCjk += 0.25;
            }
        }

        var tokens = (cjk / 1.5) + (nonCjk / 4.0);
        return Math.Max(1, (int)Math.Ceiling(tokens));
    }

    /// <summary>
    /// Computes dual-prime Rabin-Karp rolling-hash Winnowing fingerprints (<paramref name="kGramSize"/>-grams
    /// over sliding window <paramref name="windowSize"/>) in <c>O(n)</c> time with monotonic deque rightmost tie-breaking.
    /// </summary>
    public static IReadOnlyList<WinnowFingerprint> WinnowFingerprints(
        string normalizedText,
        int kGramSize = DefaultKGramSize,
        int windowSize = DefaultWindowSize)
    {
        if (string.IsNullOrEmpty(normalizedText))
        {
            return [];
        }

        var k = Math.Max(1, kGramSize);
        var w = Math.Max(1, windowSize);
        var n = normalizedText.Length;
        if (n < k)
        {
            return [];
        }

        var hashCount = n - k + 1;
        var hashes = new long[hashCount];

        long pow1 = 1;
        long pow2 = 1;
        long h1 = 0;
        long h2 = 0;

        for (var i = 0; i < k; i++)
        {
            long c = normalizedText[i] + 1L;
            h1 = (h1 * Base1 + c) % Mod1;
            h2 = (h2 * Base2 + c) % Mod2;
            if (i > 0)
            {
                pow1 = (pow1 * Base1) % Mod1;
                pow2 = (pow2 * Base2) % Mod2;
            }
        }

        hashes[0] = h1 * Mod2 + h2;

        for (var i = k; i < n; i++)
        {
            long outCode = normalizedText[i - k] + 1L;
            long inCode = normalizedText[i] + 1L;

            h1 = (h1 - ((outCode * pow1) % Mod1) + Mod1) % Mod1;
            h1 = (h1 * Base1 + inCode) % Mod1;

            h2 = (h2 - ((outCode * pow2) % Mod2) + Mod2) % Mod2;
            h2 = (h2 * Base2 + inCode) % Mod2;

            hashes[i - k + 1] = h1 * Mod2 + h2;
        }

        var effectiveWindow = Math.Min(w, hashCount);
        var fingerprints = new List<WinnowFingerprint>(Math.Max(4, hashCount / Math.Max(1, effectiveWindow)));

        // Monotonic queue storing indices with non-decreasing hash values.
        // Uses `>=` on back pop so equal hashes favor the rightmost position.
        var deque = new int[hashCount];
        var head = 0;
        var tail = 0;
        var lastRecordedPos = -1;

        for (var i = 0; i < hashCount; i++)
        {
            var curHash = hashes[i];
            while (tail > head && hashes[deque[tail - 1]] >= curHash)
            {
                tail--;
            }

            deque[tail++] = i;

            var winStart = i - effectiveWindow + 1;
            if (deque[head] < winStart)
            {
                head++;
            }

            if (i >= effectiveWindow - 1)
            {
                var minPos = deque[head];
                if (minPos != lastRecordedPos)
                {
                    fingerprints.Add(new WinnowFingerprint(hashes[minPos], minPos));
                    lastRecordedPos = minPos;
                }
            }
        }

        return fingerprints;
    }

    public static (string Code, string Summary) ResolveOverlapRemediation(string kindA, string kindB)
    {
        var normA = ContextAuditSourceKinds.Normalize(kindA);
        var normB = ContextAuditSourceKinds.Normalize(kindB);

        bool Has(string target) =>
            string.Equals(normA, target, StringComparison.Ordinal) ||
            string.Equals(normB, target, StringComparison.Ordinal);

        if (Has(ContextAuditSourceKinds.Skill)
            && (Has(ContextAuditSourceKinds.SystemPrompt)
                || Has(ContextAuditSourceKinds.PipelineTemplate)
                || Has(ContextAuditSourceKinds.PromptFragment)))
        {
            return (
                OverlapRemediationCodes.MoveToSkillReferences,
                "Move repeated procedural rules into on-demand SKILL.md or references/ and keep only a one-line trigger pointer in the always-on prompt.");
        }

        if (Has(ContextAuditSourceKinds.WorkspaceInstructions))
        {
            return (
                OverlapRemediationCodes.DedupeWorkspaceInstructions,
                "Keep repository conventions in a single AGENTS.md or .apm/instructions file and remove duplicated policy prose from prompts or skills.");
        }

        if (Has(ContextAuditSourceKinds.ToolSchema))
        {
            return (
                OverlapRemediationCodes.DedupeToolSchemaProse,
                "Keep parameter and usage constraints inside tool schema descriptions instead of repeating them across system prompts or skills.");
        }

        if (string.Equals(normA, ContextAuditSourceKinds.Skill, StringComparison.Ordinal)
            && string.Equals(normB, ContextAuditSourceKinds.Skill, StringComparison.Ordinal))
        {
            return (
                OverlapRemediationCodes.ConsolidateSharedSkill,
                "Extract duplicated workflow checklists into a shared references/ document or consolidate overlapping skills.");
        }

        return (
            OverlapRemediationCodes.ExtractSharedFragment,
            "Consolidate duplicated instructions into a single reusable prompt fragment or on-demand skill reference to stop paying twice every turn.");
    }

    public static string RedactSensitiveText(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var redacted = BearerSecretRegex.Replace(input, "$1********");
        redacted = TokenPrefixSecretRegex.Replace(redacted, "********");
        redacted = KeyValueSecretRegex.Replace(redacted, "$1********");
        return redacted;
    }

    public ContextOverlapAuditDto AuditSources(
        IReadOnlyList<ContextAuditSourceInputDto> sources,
        WinnowingAuditOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var opts = options ?? new WinnowingAuditOptions();
        var k = Math.Clamp(opts.KGramSize, 1, 512);
        var w = Math.Clamp(opts.WindowSize, 1, 256);
        var maxExcerpt = Math.Clamp(opts.MaxExcerptLength, 16, 1000);
        var maxMatchesPerPair = Math.Clamp(opts.MaxMatchesPerPair, 1, 50);

        var docs = new NormalizedDocument[sources.Count];
        for (var i = 0; i < sources.Count; i++)
        {
            docs[i] = NormalizeWithOffsets(sources[i]);
        }

        var index = new Dictionary<long, List<(int DocIdx, int Pos)>>();
        for (var docIdx = 0; docIdx < docs.Length; docIdx++)
        {
            var fps = WinnowFingerprints(docs[docIdx].Normalized, k, w);
            for (var f = 0; f < fps.Count; f++)
            {
                var fp = fps[f];
                if (!index.TryGetValue(fp.Hash, out var bucket))
                {
                    bucket = new List<(int DocIdx, int Pos)>(2);
                    index[fp.Hash] = bucket;
                }

                bucket.Add((docIdx, fp.Pos));
            }
        }

        var pairSpans = new Dictionary<(int IdxA, int IdxB), List<SpanPair>>();
        foreach (var postings in index.Values)
        {
            if (postings.Count < 2)
            {
                continue;
            }

            var cappedCount = Math.Min(postings.Count, MaxBucketPostings);
            for (var i = 0; i < cappedCount; i++)
            {
                var p1 = postings[i];
                for (var j = i + 1; j < cappedCount; j++)
                {
                    var p2 = postings[j];
                    if (p1.DocIdx == p2.DocIdx)
                    {
                        continue;
                    }

                    var idxA = p1.DocIdx < p2.DocIdx ? p1.DocIdx : p2.DocIdx;
                    var posA = p1.DocIdx < p2.DocIdx ? p1.Pos : p2.Pos;
                    var idxB = p1.DocIdx < p2.DocIdx ? p2.DocIdx : p1.DocIdx;
                    var posB = p1.DocIdx < p2.DocIdx ? p2.Pos : p1.Pos;

                    var textA = docs[idxA].Normalized;
                    var textB = docs[idxB].Normalized;

                    // Exact collision verification for the k-gram before extending.
                    if (!textA.AsSpan(posA, k).SequenceEqual(textB.AsSpan(posB, k)))
                    {
                        continue;
                    }

                    var startA = posA;
                    var startB = posB;
                    while (startA > 0 && startB > 0 && textA[startA - 1] == textB[startB - 1])
                    {
                        startA--;
                        startB--;
                    }

                    var endA = posA + k;
                    var endB = posB + k;
                    while (endA < textA.Length && endB < textB.Length && textA[endA] == textB[endB])
                    {
                        endA++;
                        endB++;
                    }

                    // Trim boundary whitespace so 1-based line ranges point strictly to the matched content lines.
                    while (startA < endA && startB < endB && textA[startA] == ' ' && textB[startB] == ' ')
                    {
                        startA++;
                        startB++;
                    }

                    while (endA > startA && endB > startB && textA[endA - 1] == ' ' && textB[endB - 1] == ' ')
                    {
                        endA--;
                        endB--;
                    }

                    if (endA - startA < k || endB - startB < k)
                    {
                        continue;
                    }

                    var pairKey = (idxA, idxB);
                    if (!pairSpans.TryGetValue(pairKey, out var spanList))
                    {
                        spanList = new List<SpanPair>(4);
                        pairSpans[pairKey] = spanList;
                    }

                    spanList.Add(new SpanPair(startA, endA, startB, endB));
                }
            }
        }

        var perSourceDupIntervals = new List<(int Start, int End)>[docs.Length];
        for (var i = 0; i < docs.Length; i++)
        {
            perSourceDupIntervals[i] = [];
        }

        var pairs = new List<ContextOverlapPairSummaryDto>();
        var allMatches = new List<OverlapPassageMatchDto>();

        foreach (var kvp in pairSpans)
        {
            var (idxA, idxB) = kvp.Key;
            var docA = docs[idxA];
            var docB = docs[idxB];
            var merged = MergeSpanPairs(kvp.Value)
                .Where(s => s.EndA - s.StartA >= k && s.EndB - s.StartB >= k)
                .OrderByDescending(s => s.EndA - s.StartA)
                .ThenBy(s => s.StartA)
                .ToList();

            if (merged.Count == 0)
            {
                continue;
            }

            var intervalsA = new List<(int Start, int End)>(merged.Count);
            var intervalsB = new List<(int Start, int End)>(merged.Count);
            foreach (var s in merged)
            {
                intervalsA.Add((s.StartA, s.EndA));
                intervalsB.Add((s.StartB, s.EndB));
                perSourceDupIntervals[idxA].Add((s.StartA, s.EndA));
                perSourceDupIntervals[idxB].Add((s.StartB, s.EndB));
            }

            var sharedCharsA = MergedIntervalLength(intervalsA);
            var sharedCharsB = MergedIntervalLength(intervalsB);
            var sharedChars = Math.Min(sharedCharsA, sharedCharsB);
            var minLen = Math.Max(1, Math.Min(docA.Normalized.Length, docB.Normalized.Length));
            var overlapPercent = Math.Min(100.0, JsRound((sharedChars / (double)minLen) * 1000.0) / 10.0);
            var overlapPercentA = docA.Normalized.Length > 0
                ? Math.Min(100.0, JsRound((sharedCharsA / (double)docA.Normalized.Length) * 1000.0) / 10.0)
                : 0.0;
            var overlapPercentB = docB.Normalized.Length > 0
                ? Math.Min(100.0, JsRound((sharedCharsB / (double)docB.Normalized.Length) * 1000.0) / 10.0)
                : 0.0;

            var remediation = ResolveOverlapRemediation(docA.Kind, docB.Kind);
            var pairId = $"{docA.Source.Id}__{docB.Source.Id}";

            var takeCount = Math.Min(merged.Count, maxMatchesPerPair);
            var pairMatches = new List<OverlapPassageMatchDto>(takeCount);
            for (var idx = 0; idx < takeCount; idx++)
            {
                var span = merged[idx];
                var sliceText = docA.Normalized.Substring(span.StartA, span.EndA - span.StartA);
                var dupTokens = EstimateTokens(sliceText);
                var linesA = NormSpanToLines(docA, span.StartA, span.EndA);
                var linesB = NormSpanToLines(docB, span.StartB, span.EndB);

                pairMatches.Add(new OverlapPassageMatchDto
                {
                    Id = $"{pairId}#{idx + 1}",
                    SourceAId = docA.Source.Id,
                    SourceALabel = docA.Source.Label,
                    SourceAKind = docA.Kind,
                    SourceBId = docB.Source.Id,
                    SourceBLabel = docB.Source.Label,
                    SourceBKind = docB.Kind,
                    SourceALines = linesA,
                    SourceBLines = linesB,
                    LineRangePair = LineRangeDto.FormatPair(linesA, linesB),
                    SharedChars = span.EndA - span.StartA,
                    EstimatedDuplicateTokens = dupTokens,
                    Excerpt = BuildExcerpt(docA, span.StartA, span.EndA, maxExcerpt),
                    RemediationCode = remediation.Code,
                    RemediationSummary = remediation.Summary
                });
            }

            // Estimate pair duplicate tokens from unique matched text (never double-counted).
            var pairDuplicateTokens = Math.Max(
                1,
                (int)JsRound((sharedChars / (double)Math.Max(1, docA.Normalized.Length)) * Math.Max(1, docA.EstimatedTokens)));

            pairs.Add(new ContextOverlapPairSummaryDto
            {
                Id = pairId,
                SourceAId = docA.Source.Id,
                SourceALabel = docA.Source.Label,
                SourceAKind = docA.Kind,
                SourceBId = docB.Source.Id,
                SourceBLabel = docB.Source.Label,
                SourceBKind = docB.Kind,
                SharedChars = sharedChars,
                OverlapPercent = overlapPercent,
                OverlapPercentA = overlapPercentA,
                OverlapPercentB = overlapPercentB,
                EstimatedDuplicateTokens = pairDuplicateTokens,
                MatchCount = merged.Count,
                Matches = pairMatches,
                RemediationCode = remediation.Code,
                RemediationSummary = remediation.Summary
            });

            allMatches.AddRange(pairMatches);
        }

        pairs.Sort(static (a, b) =>
        {
            var cmp = b.EstimatedDuplicateTokens.CompareTo(a.EstimatedDuplicateTokens);
            if (cmp != 0) return cmp;
            cmp = b.SharedChars.CompareTo(a.SharedChars);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.Id, b.Id);
        });

        allMatches.Sort(static (a, b) =>
        {
            var cmp = b.SharedChars.CompareTo(a.SharedChars);
            if (cmp != 0) return cmp;
            cmp = b.EstimatedDuplicateTokens.CompareTo(a.EstimatedDuplicateTokens);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.Id, b.Id);
        });

        var totalEstimatedTokens = 0;
        var totalChars = 0;
        var totalDuplicatedChars = 0;

        var sourceStats = new List<ContextOverlapSourceStatsDto>(docs.Length);
        for (var idx = 0; idx < docs.Length; idx++)
        {
            var doc = docs[idx];
            var dupChars = MergedIntervalLength(perSourceDupIntervals[idx]);
            var normLen = doc.Normalized.Length;
            totalEstimatedTokens += doc.EstimatedTokens;
            totalChars += normLen;
            totalDuplicatedChars += dupChars;

            sourceStats.Add(new ContextOverlapSourceStatsDto
            {
                Id = doc.Source.Id,
                Label = doc.Source.Label,
                Kind = doc.Kind,
                Path = doc.Source.Path,
                CharCount = (doc.Source.Content ?? string.Empty).Length,
                LineCount = doc.LineCount,
                EstimatedTokens = doc.EstimatedTokens,
                DuplicatedChars = dupChars,
                OverlapPercent = normLen > 0
                    ? Math.Min(100.0, JsRound((dupChars / (double)normLen) * 1000.0) / 10.0)
                    : 0.0
            });
        }

        var redundantChars = (int)JsRound(totalDuplicatedChars / 2.0);
        var duplicateEstimatedTokens = totalChars > 0 && redundantChars > 0
            ? Math.Max(1, (int)JsRound((redundantChars / (double)totalChars) * totalEstimatedTokens))
            : 0;
        var overallOverlapPercent = totalEstimatedTokens > 0
            ? Math.Min(100.0, JsRound((duplicateEstimatedTokens / (double)totalEstimatedTokens) * 1000.0) / 10.0)
            : 0.0;

        return new ContextOverlapAuditDto
        {
            Scope = "manual_sources",
            KGramSize = k,
            WindowSize = w,
            SourceCount = docs.Length,
            TotalEstimatedTokens = totalEstimatedTokens,
            DuplicateEstimatedTokens = duplicateEstimatedTokens,
            OverallOverlapPercent = overallOverlapPercent,
            Pairs = pairs,
            Matches = allMatches,
            Sources = sourceStats,
            GeneratedAt = DateTimeOffset.UtcNow
        };
    }

    public async Task<ContextOverlapAuditDto> AuditAsync(
        ContextOverlapAuditRequestDto request,
        ContextAuditExecutionScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = new WinnowingAuditOptions(
            request.KGramSize ?? DefaultKGramSize,
            request.WindowSize ?? DefaultWindowSize,
            request.MaxExcerptLength ?? DefaultMaxExcerptLength,
            request.MaxMatchesPerPair ?? DefaultMaxMatchesPerPair);

        var includePrompts = request.IncludePrompts ?? true;
        var includeSkills = request.IncludeSkills ?? true;
        var includeTools = request.IncludeTools ?? true;

        var byId = new Dictionary<string, ContextAuditSourceInputDto>(StringComparer.Ordinal);
        var orderedIds = new List<string>();
        void AddSource(ContextAuditSourceInputDto item)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Content))
            {
                return;
            }

            var normalized = item with
            {
                Id = item.Id.Trim(),
                Label = string.IsNullOrWhiteSpace(item.Label) ? item.Id.Trim() : item.Label.Trim(),
                Kind = ContextAuditSourceKinds.Normalize(item.Kind),
                Content = item.Content
            };
            if (!byId.ContainsKey(normalized.Id))
            {
                orderedIds.Add(normalized.Id);
            }

            byId[normalized.Id] = normalized;
        }

        string auditScope;
        var effectiveProjectId = request.ProjectId ?? scope?.ProjectId;

        if (request.RunId is { } runId && runId != Guid.Empty)
        {
            auditScope = "run_snapshot";
            await GatherRunSnapshotSourcesAsync(runId, scope, includePrompts, includeSkills, includeTools, AddSource, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (!includePrompts && !includeSkills && !includeTools)
        {
            auditScope = "manual_sources";
        }
        else
        {
            auditScope = "static_config";
            var verifiedRoot = await ResolveAuthorizedWorkspaceRootAsync(request, scope, effectiveProjectId, cancellationToken)
                .ConfigureAwait(false);

            if (includePrompts && !string.IsNullOrWhiteSpace(verifiedRoot))
            {
                foreach (var wsDoc in ReadWorkspaceInstructionSources(verifiedRoot))
                {
                    AddSource(wsDoc);
                }
            }

            if (_catalogReader is not null && scope is not null)
            {
                var effectiveScope = scope with
                {
                    ProjectId = effectiveProjectId,
                    AuthorizedWorkspaceRoot = verifiedRoot ?? scope.AuthorizedWorkspaceRoot
                };
                var catalogSources = await _catalogReader
                    .GatherCatalogSourcesAsync(request, effectiveScope, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var src in catalogSources)
                {
                    AddSource(src);
                }
            }
            else
            {
                if (includePrompts)
                {
                    await GatherPromptFragmentSourcesAsync(request.AgentId, AddSource, cancellationToken).ConfigureAwait(false);
                }

                if (includeTools)
                {
                    await GatherToolAndMcpSourcesAsync(effectiveProjectId, verifiedRoot, AddSource, cancellationToken).ConfigureAwait(false);
                }
            }

            if (includeSkills)
            {
                await GatherSkillSourcesAsync(effectiveProjectId, verifiedRoot, AddSource, cancellationToken).ConfigureAwait(false);
            }
        }

        if (request.ExtraSources is { Count: > 0 })
        {
            foreach (var extra in request.ExtraSources)
            {
                if (extra is not null)
                {
                    AddSource(extra);
                }
            }
        }

        var gathered = orderedIds.Select(id => byId[id]).ToArray();
        var audited = AuditSources(gathered, options);
        return audited with
        {
            Scope = auditScope,
            ProjectId = effectiveProjectId,
            ModeId = request.ModeId,
            AgentId = request.AgentId,
            RunId = request.RunId
        };
    }

    private async Task GatherRunSnapshotSourcesAsync(
        Guid runId,
        ContextAuditExecutionScope? scope,
        bool includePrompts,
        bool includeSkills,
        bool includeTools,
        Action<ContextAuditSourceInputDto> addSource,
        CancellationToken cancellationToken)
    {
        if (_lifecycle is null)
        {
            throw new KeyNotFoundException($"Run '{runId}' was not found.");
        }

        var runIdStr = runId.ToString();
        var runState = await _lifecycle.GetRunStateAsync(runIdStr, cancellationToken).ConfigureAwait(false);
        if (scope is not null)
        {
            if (!Guid.TryParse(runState.TenantId, out var runTenant) || runTenant != scope.TenantId
                || !Guid.TryParse(runState.WorkspaceId, out var runWorkspace) || runWorkspace != scope.WorkspaceId)
            {
                throw new KeyNotFoundException($"Run '{runId}' was not found.");
            }
        }

        var frozen = await _lifecycle.GetFrozenRunConfigurationAsync(runIdStr, cancellationToken).ConfigureAwait(false);
        if (frozen is null && string.IsNullOrWhiteSpace(runState.SessionId))
        {
            throw new KeyNotFoundException($"Run '{runId}' was not found.");
        }

        string? frozenRootPath = null;
        Guid? frozenProjectId = scope?.ProjectId;

        if (frozen is not null && !string.IsNullOrWhiteSpace(frozen.Content))
        {
            try
            {
                using var doc = JsonDocument.Parse(frozen.Content);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (TryGetPropertyIgnoreCase(root, "workspace", out var wsEl) && wsEl.ValueKind == JsonValueKind.Object)
                    {
                        frozenRootPath = ReadJsonString(wsEl, "root_path") ?? ReadJsonString(wsEl, "rootPath");
                        if (Guid.TryParse(ReadJsonString(wsEl, "project_id") ?? ReadJsonString(wsEl, "projectId"), out var pid) && pid != Guid.Empty)
                        {
                            frozenProjectId = pid;
                        }
                    }

                    if (includePrompts)
                    {
                        var modeInstructions = ReadJsonString(root, "mode_instructions") ?? ReadJsonString(root, "modeInstructions");
                        if (!string.IsNullOrWhiteSpace(modeInstructions))
                        {
                            addSource(new ContextAuditSourceInputDto
                            {
                                Id = $"run:{runId:N}:mode_instructions",
                                Label = "Frozen Mode Instructions",
                                Kind = ContextAuditSourceKinds.PipelineTemplate,
                                Content = modeInstructions
                            });
                        }

                        ExtractFrozenRosterPrompts(root, "operation", addSource);
                        ExtractFrozenRosterPrompts(root, "execution", addSource);
                        ExtractFrozenRosterPrompts(root, "spawnable_templates", addSource);
                        ExtractFrozenRosterPrompts(root, "spawnableTemplates", addSource);
                    }

                    if (includeTools)
                    {
                        ExtractFrozenToolDescriptions(root, addSource);
                    }
                }
            }
            catch (JsonException)
            {
                // Ignore malformed frozen JSON; still gather session/workspace files if available.
            }
        }

        if (string.IsNullOrWhiteSpace(frozenRootPath)
            && _sessionLocator is not null
            && Guid.TryParse(runState.SessionId, out var sessionGuid))
        {
            var sessionRef = await _sessionLocator.FindAsync(sessionGuid, cancellationToken).ConfigureAwait(false);
            if (sessionRef?.ProjectId is { } sp && sp != Guid.Empty)
            {
                frozenProjectId ??= sp;
                var proj = await _sessionLocator.FindProjectAsync(sp, cancellationToken).ConfigureAwait(false);
                if (proj is not null && (scope is null || (proj.TenantId == scope.TenantId && proj.WorkspaceId == scope.WorkspaceId)))
                {
                    frozenRootPath = proj.RootPath;
                }
            }
        }

        if (includePrompts && !string.IsNullOrWhiteSpace(frozenRootPath) && Directory.Exists(frozenRootPath))
        {
            foreach (var wsDoc in ReadWorkspaceInstructionSources(frozenRootPath))
            {
                addSource(wsDoc);
            }
        }

        if (includeSkills)
        {
            await GatherSkillSourcesAsync(frozenProjectId, frozenRootPath, addSource, cancellationToken).ConfigureAwait(false);
        }

        if (includeTools)
        {
            await GatherToolAndMcpSourcesAsync(frozenProjectId, frozenRootPath, addSource, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ExtractFrozenRosterPrompts(
        JsonElement root,
        string arrayName,
        Action<ContextAuditSourceInputDto> addSource)
    {
        if (!TryGetPropertyIgnoreCase(root, arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var slug = ReadJsonString(item, "id") ?? ReadJsonString(item, "slug") ?? "agent";
            var role = ReadJsonString(item, "role") ?? slug;
            var sysPrompt = ReadJsonString(item, "system_prompt") ?? ReadJsonString(item, "systemPrompt");
            if (!string.IsNullOrWhiteSpace(sysPrompt))
            {
                addSource(new ContextAuditSourceInputDto
                {
                    Id = $"frozen:agent:{slug}",
                    Label = $"{slug} ({role})",
                    Kind = ContextAuditSourceKinds.SystemPrompt,
                    Content = sysPrompt
                });
            }

            var graphJson = ReadJsonString(item, "prompt_graph_json") ?? ReadJsonString(item, "promptGraphJson");
            if (!string.IsNullOrWhiteSpace(graphJson))
            {
                var pipelineText = ExtractPipelineGraphTemplates(graphJson);
                if (!string.IsNullOrWhiteSpace(pipelineText))
                {
                    addSource(new ContextAuditSourceInputDto
                    {
                        Id = $"frozen:pipeline:{slug}",
                        Label = $"{slug} Prompt Pipeline",
                        Kind = ContextAuditSourceKinds.PipelineTemplate,
                        Content = pipelineText
                    });
                }
            }
        }
    }

    private static void ExtractFrozenToolDescriptions(
        JsonElement root,
        Action<ContextAuditSourceInputDto> addSource)
    {
        if (!TryGetPropertyIgnoreCase(root, "tools_manifest", out var manifest)
            && !TryGetPropertyIgnoreCase(root, "toolsManifest", out manifest)
            && !TryGetPropertyIgnoreCase(root, "tool_manifest", out manifest))
        {
            return;
        }

        JsonElement toolsArray = default;
        if (manifest.ValueKind == JsonValueKind.Array)
        {
            toolsArray = manifest;
        }
        else if (manifest.ValueKind == JsonValueKind.Object
            && TryGetPropertyIgnoreCase(manifest, "tools", out var innerTools)
            && innerTools.ValueKind == JsonValueKind.Array)
        {
            toolsArray = innerTools;
        }

        if (toolsArray.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var lines = new List<string>();
        foreach (var tool in toolsArray.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object) continue;
            var id = ReadJsonString(tool, "id");
            var desc = ReadJsonString(tool, "description");
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(desc))
            {
                lines.Add($"{id}: {RedactSensitiveText(desc)}");
            }
        }

        if (lines.Count > 0)
        {
            addSource(new ContextAuditSourceInputDto
            {
                Id = "frozen:tools:manifest",
                Label = $"Frozen Tool Schemas ({lines.Count})",
                Kind = ContextAuditSourceKinds.ToolSchema,
                Content = string.Join('\n', lines)
            });
        }
    }

    public static string ExtractPipelineGraphTemplates(string? graphJson)
    {
        if (string.IsNullOrWhiteSpace(graphJson))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(graphJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            JsonElement nodes = default;
            if (TryGetPropertyIgnoreCase(root, "nodes", out var directNodes) && directNodes.ValueKind == JsonValueKind.Array)
            {
                nodes = directNodes;
            }
            else if (TryGetPropertyIgnoreCase(root, "graph", out var nestedGraph)
                && nestedGraph.ValueKind == JsonValueKind.Object
                && TryGetPropertyIgnoreCase(nestedGraph, "nodes", out var nestedNodes)
                && nestedNodes.ValueKind == JsonValueKind.Array)
            {
                nodes = nestedNodes;
            }

            if (nodes.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var chunks = new List<string>();
            foreach (var node in nodes.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object) continue;
                string? tpl = null;
                if (TryGetPropertyIgnoreCase(node, "config", out var cfg) && cfg.ValueKind == JsonValueKind.Object)
                {
                    tpl = ReadJsonString(cfg, "template") ?? ReadJsonString(cfg, "content");
                }

                if (string.IsNullOrWhiteSpace(tpl) && TryGetPropertyIgnoreCase(node, "data", out var data) && data.ValueKind == JsonValueKind.Object)
                {
                    tpl = ReadJsonString(data, "template") ?? ReadJsonString(data, "content");
                }

                tpl ??= ReadJsonString(node, "template") ?? ReadJsonString(node, "content");
                if (!string.IsNullOrWhiteSpace(tpl))
                {
                    chunks.Add(tpl.Trim());
                }
            }

            return chunks.Count == 0 ? string.Empty : string.Join("\n\n", chunks);
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private async Task<string?> ResolveAuthorizedWorkspaceRootAsync(
        ContextOverlapAuditRequestDto request,
        ContextAuditExecutionScope? scope,
        Guid? effectiveProjectId,
        CancellationToken cancellationToken)
    {
        string? projectRoot = null;
        if (effectiveProjectId is { } pid && pid != Guid.Empty && _sessionLocator is not null)
        {
            var project = await _sessionLocator.FindProjectAsync(pid, cancellationToken).ConfigureAwait(false);
            if (project is null || (scope is not null && (project.TenantId != scope.TenantId || project.WorkspaceId != scope.WorkspaceId)))
            {
                throw new KeyNotFoundException($"Project '{pid}' was not found.");
            }

            projectRoot = project.RootPath;
        }

        var baseAuthorizedRoot = !string.IsNullOrWhiteSpace(projectRoot)
            ? projectRoot
            : scope?.AuthorizedWorkspaceRoot;

        if (!string.IsNullOrWhiteSpace(request.WorkspaceRoot))
        {
            var requestedFull = Path.GetFullPath(request.WorkspaceRoot.Trim());
            if (scope is not null)
            {
                if (string.IsNullOrWhiteSpace(baseAuthorizedRoot)
                    || !WorkspaceSkillDiscovery.IsContained(baseAuthorizedRoot, requestedFull))
                {
                    throw new ArgumentException("workspace_root is outside the authorized project/workspace sandbox.");
                }
            }

            return Directory.Exists(requestedFull) ? requestedFull : null;
        }

        if (!string.IsNullOrWhiteSpace(baseAuthorizedRoot) && Directory.Exists(baseAuthorizedRoot))
        {
            return Path.GetFullPath(baseAuthorizedRoot);
        }

        return null;
    }

    /// <summary>
    /// Reads raw workspace instruction files (<c>AGENTS.override.md &gt; AGENTS.md &gt; CLAUDE.md &gt; CONTEXT.md</c>
    /// plus <c>.apm/instructions/*.instructions.md</c>) obeying <see cref="WorkspaceInstructionPolicy"/> containment,
    /// symlink, and <c>MaxFileBytes</c> rules while preserving 1-based line numbers of the underlying files.
    /// </summary>
    public static IReadOnlyList<ContextAuditSourceInputDto> ReadWorkspaceInstructionSources(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return [];
        }

        var results = new List<ContextAuditSourceInputDto>();
        IReadOnlyCollection<string> present;
        try
        {
            present = Directory.GetFiles(rootPath).Select(Path.GetFileName).OfType<string>().ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }

        if (WorkspaceInstructionPolicy.Select(present) is { } primaryName)
        {
            var primaryFull = Path.Combine(rootPath, primaryName);
            if (TryReadContainedFile(rootPath, primaryFull, WorkspaceInstructionPolicy.MaxFileBytes, out var primaryContent))
            {
                results.Add(new ContextAuditSourceInputDto
                {
                    Id = $"workspace:{primaryName}",
                    Label = primaryName,
                    Kind = ContextAuditSourceKinds.WorkspaceInstructions,
                    Path = primaryName,
                    Content = primaryContent
                });
            }
        }

        try
        {
            var apmDir = Path.Combine(rootPath, ".apm");
            var apmInstructionsDir = Path.Combine(apmDir, "instructions");
            if (Directory.Exists(apmInstructionsDir)
                && WorkspaceSkillDiscovery.IsContained(rootPath, apmDir)
                && WorkspaceSkillDiscovery.IsContained(rootPath, apmInstructionsDir))
            {
                var apmFiles = Directory.GetFiles(apmInstructionsDir)
                    .Where(f => Path.GetFileName(f).EndsWith(WorkspaceInstructionPolicy.ApmInstructionSuffix, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                    .ToArray();

                foreach (var apmFile in apmFiles)
                {
                    var relPath = $"{WorkspaceInstructionPolicy.ApmInstructionsDirectory}/{Path.GetFileName(apmFile)}";
                    if (TryReadContainedFile(rootPath, apmFile, WorkspaceInstructionPolicy.MaxFileBytes, out var apmContent))
                    {
                        results.Add(new ContextAuditSourceInputDto
                        {
                            Id = $"workspace:{relPath}",
                            Label = relPath,
                            Kind = ContextAuditSourceKinds.WorkspaceInstructions,
                            Path = relPath,
                            Content = apmContent
                        });
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Keep primary instruction file even if .apm/instructions cannot be enumerated.
        }

        return results;
    }

    private static bool TryReadContainedFile(string rootPath, string fullPath, long maxBytes, out string content)
    {
        content = string.Empty;
        try
        {
            if (!WorkspaceSkillDiscovery.IsContained(rootPath, fullPath))
            {
                return false;
            }

            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length <= 0 || info.Length > maxBytes)
            {
                return false;
            }

            var raw = File.ReadAllText(fullPath);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            content = raw;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private async Task GatherPromptFragmentSourcesAsync(
        string? agentFilter,
        Action<ContextAuditSourceInputDto> addSource,
        CancellationToken cancellationToken)
    {
        if (_promptFragments is null)
        {
            return;
        }

        try
        {
            var fragments = await _promptFragments.ListAsync(enabled: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var frag in fragments)
            {
                if (!frag.Enabled || string.IsNullOrWhiteSpace(frag.Content))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(agentFilter)
                    && string.Equals(frag.Scope, "agent", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(frag.TargetAgentSlug, agentFilter, StringComparison.OrdinalIgnoreCase)
                    && (!Guid.TryParse(agentFilter, out var aid) || frag.TargetAgentId != aid))
                {
                    continue;
                }

                addSource(new ContextAuditSourceInputDto
                {
                    Id = $"fragment:{frag.Id}",
                    Label = string.IsNullOrWhiteSpace(frag.Title) ? frag.Key : frag.Title,
                    Kind = ContextAuditSourceKinds.PromptFragment,
                    Content = frag.Content
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Non-fatal if prompt fragment store is unavailable.
        }
    }

    private async Task GatherSkillSourcesAsync(
        Guid? projectId,
        string? verifiedRoot,
        Action<ContextAuditSourceInputDto> addSource,
        CancellationToken cancellationToken)
    {
        var addedAnyFromService = false;
        if (_skillResources is not null)
        {
            try
            {
                var list = await _skillResources.ListAsync(projectId, includeBuiltins: true, cancellationToken).ConfigureAwait(false);
                var enabledSkills = list.Skills
                    .Where(s => s.Enabled && s.Valid && string.IsNullOrWhiteSpace(s.ShadowedBy))
                    .Take(MaxServerSkills)
                    .ToArray();

                foreach (var item in enabledSkills)
                {
                    var body = item.Content;
                    if (string.IsNullOrWhiteSpace(body))
                    {
                        try
                        {
                            var detail = await _skillResources.GetAsync(item.ResourceId, projectId, cancellationToken).ConfigureAwait(false);
                            body = detail?.Content;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // Fall back to metadata description when body cannot be fetched.
                        }
                    }

                    var combined = !string.IsNullOrWhiteSpace(body)
                        ? (body.Length > MaxSkillBodyChars ? body[..MaxSkillBodyChars] : body)
                        : string.Join('\n', new[] { item.Description, item.WhenToUse }.Where(s => !string.IsNullOrWhiteSpace(s)));

                    if (!string.IsNullOrWhiteSpace(combined))
                    {
                        addedAnyFromService = true;
                        addSource(new ContextAuditSourceInputDto
                        {
                            Id = $"skill:{item.ResourceId}",
                            Label = string.IsNullOrWhiteSpace(item.Name) ? item.ResourceId.ToString() : item.Name,
                            Kind = ContextAuditSourceKinds.Skill,
                            Path = string.IsNullOrWhiteSpace(item.Path) ? null : item.Path,
                            Content = combined
                        });
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fall back to direct workspace discovery below.
            }
        }

        if (!addedAnyFromService && !string.IsNullOrWhiteSpace(verifiedRoot) && Directory.Exists(verifiedRoot))
        {
            foreach (var entry in WorkspaceSkillDiscovery.Discover(verifiedRoot)
                         .Where(e => e.Enabled && e.Valid && !string.IsNullOrWhiteSpace(e.Content))
                         .Take(MaxServerSkills))
            {
                var content = entry.Content!;
                if (content.Length > MaxSkillBodyChars)
                {
                    content = content[..MaxSkillBodyChars];
                }

                addSource(new ContextAuditSourceInputDto
                {
                    Id = $"skill:{entry.Name}",
                    Label = entry.Name,
                    Kind = ContextAuditSourceKinds.Skill,
                    Path = entry.RelativePath,
                    Content = content
                });
            }
        }
    }

    private async Task GatherToolAndMcpSourcesAsync(
        Guid? projectId,
        string? verifiedRoot,
        Action<ContextAuditSourceInputDto> addSource,
        CancellationToken cancellationToken)
    {
        var toolLines = new List<string>();
        var seenTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_toolRegistry is not null)
        {
            try
            {
                var tools = await _toolRegistry.ListToolsAsync(verifiedRoot, cancellationToken).ConfigureAwait(false);
                foreach (var tool in tools)
                {
                    if (!string.IsNullOrWhiteSpace(tool.Id) && !string.IsNullOrWhiteSpace(tool.Description) && seenTools.Add(tool.Id))
                    {
                        toolLines.Add($"{tool.Id}: {RedactSensitiveText(tool.Description)}");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fallback to provider manifest below.
            }
        }
        else if (_toolProvider is not null && !string.IsNullOrWhiteSpace(verifiedRoot))
        {
            try
            {
                var manifest = await _toolProvider.GetManifestAsync(verifiedRoot, cancellationToken).ConfigureAwait(false);
                foreach (var tool in manifest.Tools)
                {
                    if (!string.IsNullOrWhiteSpace(tool.Id) && !string.IsNullOrWhiteSpace(tool.Description) && seenTools.Add(tool.Id))
                    {
                        toolLines.Add($"{tool.Id}: {RedactSensitiveText(tool.Description)}");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Non-fatal if tool provider process is not running.
            }
        }

        foreach (var teamEntry in TeamToolCatalog.Entries)
        {
            if (seenTools.Add(teamEntry.Id))
            {
                toolLines.Add($"{teamEntry.Id}: {teamEntry.Description}");
            }
        }

        if (toolLines.Count > 0)
        {
            addSource(new ContextAuditSourceInputDto
            {
                Id = "tools:catalog",
                Label = $"Tool Schemas ({toolLines.Count})",
                Kind = ContextAuditSourceKinds.ToolSchema,
                Content = string.Join('\n', toolLines)
            });
        }

        if (_mcpServers is not null)
        {
            try
            {
                // Strictly metadata only: id, name, transport — never env values or headers!
                var servers = await _mcpServers.ListConfiguredAsync(projectId, cancellationToken).ConfigureAwait(false);
                var mcpLines = servers
                    .Where(s => s.Enabled)
                    .Select(s => $"- {s.Name} ({s.Id}, transport={s.Transport})")
                    .ToArray();
                if (mcpLines.Length > 0)
                {
                    addSource(new ContextAuditSourceInputDto
                    {
                        Id = "mcp:configured_servers",
                        Label = $"MCP Servers ({mcpLines.Length})",
                        Kind = ContextAuditSourceKinds.ToolSchema,
                        Content = "Configured MCP servers:\n" + string.Join('\n', mcpLines)
                    });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Non-fatal.
            }
        }
    }

    private static NormalizedDocument NormalizeWithOffsets(ContextAuditSourceInputDto source)
    {
        var raw = source.Content ?? string.Empty;
        var sb = new StringBuilder(raw.Length);
        var origOffsets = new List<int>(raw.Length);
        var prevWasSpace = true;

        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];
            if (IsJsWhitespace(ch))
            {
                if (!prevWasSpace)
                {
                    sb.Append(' ');
                    origOffsets.Add(i);
                    prevWasSpace = true;
                }

                continue;
            }

            prevWasSpace = false;
            var lower = ch is >= 'A' and <= 'Z' ? (char)(ch + 32) : ch;
            sb.Append(lower);
            origOffsets.Add(i);
        }

        if (sb.Length > 0 && sb[^1] == ' ')
        {
            sb.Length -= 1;
            origOffsets.RemoveAt(origOffsets.Count - 1);
        }

        var lineStarts = BuildLineStarts(raw);
        return new NormalizedDocument(
            source,
            ContextAuditSourceKinds.Normalize(source.Kind),
            sb.ToString(),
            origOffsets.ToArray(),
            lineStarts,
            raw.Length == 0 ? 0 : lineStarts.Length,
            string.IsNullOrWhiteSpace(raw) ? 0 : EstimateTokens(raw));
    }

    private static int[] BuildLineStarts(string content)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts.ToArray();
    }

    private static int OffsetToLine(int[] lineStarts, int offset)
    {
        var lo = 0;
        var hi = lineStarts.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (lineStarts[mid] <= offset)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return Math.Max(1, hi + 1);
    }

    private static LineRangeDto NormSpanToLines(NormalizedDocument doc, int normStart, int normEnd)
    {
        if (doc.OrigOffsets.Length == 0 || doc.LineCount == 0)
        {
            return new LineRangeDto { Start = 1, End = 1 };
        }

        var clampedStart = Math.Clamp(normStart, 0, doc.OrigOffsets.Length - 1);
        var clampedEnd = Math.Max(clampedStart, Math.Min(normEnd - 1, doc.OrigOffsets.Length - 1));
        var rawStart = doc.OrigOffsets[clampedStart];
        var rawEnd = doc.OrigOffsets[clampedEnd];
        return new LineRangeDto
        {
            Start = OffsetToLine(doc.LineStarts, rawStart),
            End = OffsetToLine(doc.LineStarts, rawEnd)
        };
    }

    private static string BuildExcerpt(NormalizedDocument doc, int normStart, int normEnd, int maxLen)
    {
        if (doc.OrigOffsets.Length == 0 || normStart >= normEnd)
        {
            return string.Empty;
        }

        var rawStart = doc.OrigOffsets[Math.Clamp(normStart, 0, doc.OrigOffsets.Length - 1)];
        var rawEnd = doc.OrigOffsets[Math.Clamp(normEnd - 1, 0, doc.OrigOffsets.Length - 1)] + 1;
        var rawContent = doc.Source.Content ?? string.Empty;
        if (rawStart >= rawContent.Length)
        {
            return string.Empty;
        }

        var sliceLen = Math.Clamp(rawEnd - rawStart, 0, rawContent.Length - rawStart);
        var rawSlice = WhitespaceCollapseRegex.Replace(rawContent.Substring(rawStart, sliceLen), " ").Trim();
        rawSlice = RedactSensitiveText(rawSlice);
        if (rawSlice.Length <= maxLen)
        {
            return rawSlice;
        }

        var cutLen = Math.Max(16, maxLen - 1);
        return string.Concat(rawSlice.AsSpan(0, Math.Min(cutLen, rawSlice.Length)).TrimEnd(), "…");
    }

    private static List<SpanPair> MergeSpanPairs(List<SpanPair> spans)
    {
        if (spans.Count <= 1)
        {
            return spans;
        }

        var sorted = new List<SpanPair>(spans);
        sorted.Sort(static (x, y) =>
        {
            var cmp = x.StartA.CompareTo(y.StartA);
            if (cmp != 0) return cmp;
            cmp = x.StartB.CompareTo(y.StartB);
            if (cmp != 0) return cmp;
            return (y.EndA - y.StartA).CompareTo(x.EndA - x.StartA);
        });

        var merged = new List<SpanPair>(sorted.Count);
        foreach (var span in sorted)
        {
            var absorbed = false;
            for (var i = merged.Count - 1; i >= 0; i--)
            {
                var existing = merged[i];
                var overlapA = span.StartA <= existing.EndA && existing.StartA <= span.EndA;
                var overlapB = span.StartB <= existing.EndB && existing.StartB <= span.EndB;
                if (overlapA && overlapB)
                {
                    merged[i] = new SpanPair(
                        Math.Min(existing.StartA, span.StartA),
                        Math.Max(existing.EndA, span.EndA),
                        Math.Min(existing.StartB, span.StartB),
                        Math.Max(existing.EndB, span.EndB));
                    absorbed = true;
                    break;
                }
            }

            if (!absorbed)
            {
                merged.Add(span);
            }
        }

        return merged;
    }

    private static int MergedIntervalLength(List<(int Start, int End)> intervals)
    {
        if (intervals.Count == 0)
        {
            return 0;
        }

        intervals.Sort(static (a, b) =>
        {
            var cmp = a.Start.CompareTo(b.Start);
            return cmp != 0 ? cmp : a.End.CompareTo(b.End);
        });

        var total = 0;
        var curStart = intervals[0].Start;
        var curEnd = intervals[0].End;
        for (var i = 1; i < intervals.Count; i++)
        {
            var (s, e) = intervals[i];
            if (s <= curEnd)
            {
                if (e > curEnd) curEnd = e;
            }
            else
            {
                total += Math.Max(0, curEnd - curStart);
                curStart = s;
                curEnd = e;
            }
        }

        total += Math.Max(0, curEnd - curStart);
        return total;
    }

    private static bool IsCjkChar(char ch) =>
        (ch >= '\u3040' && ch <= '\u30ff') ||
        (ch >= '\u3400' && ch <= '\u4dbf') ||
        (ch >= '\u4e00' && ch <= '\u9fff') ||
        (ch >= '\uf900' && ch <= '\ufaff') ||
        (ch >= '\uac00' && ch <= '\ud7af');

    private static bool IsJsWhitespace(char ch) =>
        ch <= 32 || ch == '\uFEFF' || char.IsWhiteSpace(ch);

    private static double JsRound(double value) => Math.Floor(value + 0.5);

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ReadJsonString(JsonElement obj, string name) =>
        TryGetPropertyIgnoreCase(obj, name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private sealed record NormalizedDocument(
        ContextAuditSourceInputDto Source,
        string Kind,
        string Normalized,
        int[] OrigOffsets,
        int[] LineStarts,
        int LineCount,
        int EstimatedTokens);

    private readonly record struct SpanPair(int StartA, int EndA, int StartB, int EndB);
}
