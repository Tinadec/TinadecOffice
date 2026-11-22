using System.Text.RegularExpressions;

namespace TinadecCore.Architecture.Tests;

/// <summary>
/// Structural guard preventing EF Core queries against SQLite from translating
/// <see cref="DateTimeOffset"/> <c>OrderBy</c> / <c>ThenBy</c> or range comparisons
/// server-side (which EF Core SQLite rejects with <see cref="InvalidOperationException"/>).
/// </summary>
public sealed class SqliteQueryCompatibilityTests
{
    private static readonly string[] DefaultDbContextNames =
    [
        "db", "ctx", "context", "dbContext", "_db", "_context"
    ];

    private static readonly Regex DbContextDeclarationRegex = new(
        @"\b(?:[A-Za-z0-9_]+DbContext\s+([a-zA-Z_][a-zA-Z0-9_]*)|([a-zA-Z_][a-zA-Z0-9_]*)\s*=\s*(?:await\s+)?[A-Za-z0-9_.]*CreateDbContext(?:Async)?\s*\()",
        RegexOptions.Compiled);

    private static readonly Regex DbSetMemberAccessRegex = new(
        @"\b([a-zA-Z_][a-zA-Z0-9_]*)\.[A-Z][A-Za-z0-9_]*\b",
        RegexOptions.Compiled);

    private static readonly Regex DbSetGenericRegex = new(
        @"\bSet<[A-Z][A-Za-z0-9_]*>\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex MaterializationRegex = new(
        @"\.(?:ToListAsync|ToArrayAsync|ToDictionaryAsync|ToHashSetAsync|ToLookupAsync|FirstOrDefaultAsync|FirstAsync|SingleOrDefaultAsync|SingleAsync|LastOrDefaultAsync|LastAsync|ElementAtOrDefaultAsync|AnyAsync|AllAsync|CountAsync|LongCountAsync|MaxAsync|MinAsync|SumAsync|AverageAsync|ExecuteUpdateAsync|ExecuteDeleteAsync|ForEachAsync|AsAsyncEnumerable|AsEnumerable|ToList|ToArray|ToDictionary|ToHashSet)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex VarAssignmentRegex = new(
        @"^\s*(?:(?:await\s+using\s+|using\s+)?(?:var|IQueryable<[^>]+>|IEnumerable<[^>]+>)\s+)?([a-zA-Z_][a-zA-Z0-9_]*)\s*=(?![>=])",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex DateTimeOffsetOrderRegex = new(
        @"\.(?:OrderBy|OrderByDescending|ThenBy|ThenByDescending)\s*\([^;{}]*?\b(?:CreatedAt|UpdatedAt|StartedAt|CompletedAt|ArchivedAt|Timestamp|LastMessageAt|CoolingUntil|ExpiresAt|AcquiredAt|PublishedAt)\b",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex DateTimeOffsetWhereComparisonRegex = new(
        @"\.Where\s*\([^;{}]*?\b(?:CreatedAt|UpdatedAt|StartedAt|CompletedAt|ArchivedAt|Timestamp|LastMessageAt|CoolingUntil|ExpiresAt|AcquiredAt|PublishedAt)\b\s*(?:<=|>=|<|>)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex CommentAndLiteralStripRegex = new(
        @"//[^\r\n]*|/\*[\s\S]*?\*/|""""""[\s\S]*?""""""|@""(?:""""|[^""])*""|""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'",
        RegexOptions.Compiled);

    [Fact]
    public void ProductionEfQueries_DoNotTranslateDateTimeOffsetOrderingOrRangeComparisons()
    {
        var tinadecCoreRoot = FindTinadecCoreRoot();
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(tinadecCoreRoot, "*.cs", SearchOption.AllDirectories))
        {
            var normalized = file.Replace('\\', '/');
            if (normalized.Contains("/tests/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var source = File.ReadAllText(file);
            foreach (var violation in FindViolations(source))
            {
                var relPath = Path.GetRelativePath(tinadecCoreRoot, file).Replace('\\', '/');
                violations.Add($"{relPath}: {violation}");
            }
        }

        Assert.True(violations.Count == 0,
            "Found untranslated DateTimeOffset EF query operations (materialize via ToListAsync() first):\n" + string.Join("\n", violations));
    }

    [Fact]
    public void GuardDetectsUntranslatedQuery_AndAllowsPostMaterializationSort()
    {
        const string bad = """
            public class Repo {
                public async Task Run(MyDbContext db) {
                    var rows = await db.AgentModes.Where(x => x.UpdatedAt > cutoff).OrderByDescending(x => x.UpdatedAt).ToListAsync(ct);
                }
            }
            """;
        const string good = """
            public class Repo {
                public async Task MethodA(MyDbContext db) {
                    var runs = db.Runs.AsNoTracking();
                    var list = await runs.ToListAsync(ct);
                }
                public async Task MethodB(MyDbContext db, IReadOnlyList<MemoryItem> memories) {
                    var runs = await db.Runs.AsNoTracking().ToListAsync(ct);
                    var sorted = runs.OrderByDescending(x => x.CreatedAt).ToList();
                    var authors = await db.Participants.ToDictionaryAsync(x => x.Id, x => x.Name, ct);
                    var mapped = sorted.OrderBy(x => x.UpdatedAt).Select(x => authors.GetValueOrDefault(x.Id)).ToList();
                    var mems = memories.OrderByDescending(m => m.UpdatedAt).Where(m => m.Content.Length > 0).ToList();
                }
            }
            """;

        Assert.NotEmpty(FindViolations(bad));
        Assert.Empty(FindViolations(good));
    }

    internal static IReadOnlyList<string> FindViolations(string source)
    {
        var stripped = StripCommentsAndStrings(source);
        var dbVars = CollectDbContextVariables(stripped);
        var findings = new List<string>();
        var queryVars = new Dictionary<string, int>(StringComparer.Ordinal);

        var depth = 0;
        var stmtStart = 0;
        var minDepthInStmt = 0;

        for (var i = 0; i < stripped.Length; i++)
        {
            var ch = stripped[i];
            if (ch == '{')
            {
                depth++;
                continue;
            }

            if (ch == '}')
            {
                if (depth > 0) depth--;
                if (depth < minDepthInStmt) minDepthInStmt = depth;
                continue;
            }

            if (ch != ';') continue;

            // Evict any IQueryable variables whose declaring block/method has exited.
            if (queryVars.Count > 0)
            {
                foreach (var key in queryVars.Where(kv => kv.Value > minDepthInStmt).Select(kv => kv.Key).ToList())
                {
                    queryVars.Remove(key);
                }
            }

            var rawStmt = stripped[stmtStart..i];
            var stmt = rawStmt.Trim();
            stmtStart = i + 1;
            minDepthInStmt = depth;

            if (stmt.Length == 0) continue;

            var startsFromDbSet = IsDbSetQueryStart(stmt, dbVars);
            var usesQueryVar = queryVars.Keys.Any(v => Regex.IsMatch(stmt, $@"\b{Regex.Escape(v)}\s*\."));
            var varAssign = VarAssignmentRegex.Match(stmt);
            var isMaterialized = MaterializationRegex.IsMatch(stmt);

            if (varAssign.Success)
            {
                var assignedName = varAssign.Groups[1].Value;
                if (isMaterialized || (!startsFromDbSet && !usesQueryVar))
                {
                    queryVars.Remove(assignedName);
                }
                else
                {
                    var assignDepth = ComputeDepthAtOffset(rawStmt, varAssign.Index, depth);
                    queryVars[assignedName] = assignDepth;
                }
            }

            if (!startsFromDbSet && !usesQueryVar) continue;

            // Inspect only the portion of the statement before the first materialization call.
            var matMatch = MaterializationRegex.Match(stmt);
            var serverSidePortion = matMatch.Success ? stmt[..matMatch.Index] : stmt;

            if (DateTimeOffsetOrderRegex.IsMatch(serverSidePortion))
            {
                findings.Add($"Server-side DateTimeOffset OrderBy/ThenBy before materialization: {Compact(serverSidePortion)}");
            }

            if (DateTimeOffsetWhereComparisonRegex.IsMatch(serverSidePortion))
            {
                findings.Add($"Server-side DateTimeOffset Where comparison before materialization: {Compact(serverSidePortion)}");
            }
        }

        return findings;
    }

    private static HashSet<string> CollectDbContextVariables(string strippedSource)
    {
        var vars = new HashSet<string>(DefaultDbContextNames, StringComparer.Ordinal);
        foreach (Match match in DbContextDeclarationRegex.Matches(strippedSource))
        {
            if (match.Groups[1].Success) vars.Add(match.Groups[1].Value);
            if (match.Groups[2].Success) vars.Add(match.Groups[2].Value);
        }
        return vars;
    }

    private static bool IsDbSetQueryStart(string stmt, HashSet<string> dbVars)
    {
        if (DbSetGenericRegex.IsMatch(stmt)) return true;
        foreach (Match match in DbSetMemberAccessRegex.Matches(stmt))
        {
            if (dbVars.Contains(match.Groups[1].Value))
                return true;
        }
        return false;
    }

    private static int ComputeDepthAtOffset(string rawStmt, int offsetInTrimmed, int depthAtEndOfRaw)
    {
        // Reconstruct depth at the end of the raw statement's leading whitespace + offset
        var leadingTrim = rawStmt.Length - rawStmt.TrimStart().Length;
        var targetIndex = Math.Min(rawStmt.Length, leadingTrim + offsetInTrimmed);
        var deltaAfterTarget = 0;
        for (var idx = targetIndex; idx < rawStmt.Length; idx++)
        {
            if (rawStmt[idx] == '{') deltaAfterTarget++;
            else if (rawStmt[idx] == '}') deltaAfterTarget--;
        }
        return Math.Max(0, depthAtEndOfRaw - deltaAfterTarget);
    }

    private static string StripCommentsAndStrings(string input) =>
        CommentAndLiteralStripRegex.Replace(input, static match =>
        {
            var val = match.Value;
            if (val.StartsWith("//", StringComparison.Ordinal) || val.StartsWith("/*", StringComparison.Ordinal))
                return " ";
            if (val.StartsWith('\''))
                return "''";
            return "\"\"";
        });

    private static string Compact(string text)
    {
        var collapsed = Regex.Replace(text, @"\s+", " ").Trim();
        return collapsed.Length <= 140 ? collapsed : collapsed[..140] + "...";
    }

    private static string FindTinadecCoreRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TinadecCore.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate TinadecCore.slnx above test binary directory.");
    }
}
