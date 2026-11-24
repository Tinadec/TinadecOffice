using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Runtime.ProjectBoard;

/// <summary>
/// Deterministic Markdown + sorted YAML frontmatter codec and 3-way field diff/merge helper
/// for <c>.tinadec/board/tasks/&lt;KEY&gt;.md</c>.
/// Runtime-only fields (<c>active_session_id</c>, <c>active_run_id</c>, <c>worktree_path</c>,
/// <c>lease_id</c>, <c>conflict</c>) are strictly excluded from serialized Markdown.
/// </summary>
public static partial class ProjectBoardMarkdownCodec
{
    public static readonly JsonSerializerOptions CanonicalJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    public static string NormalizeLineEndings(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    public static string ComputeNormalizedSha256(string content)
    {
        var normalized = NormalizeLineEndings(content).TrimEnd('\n') + "\n";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    public static string FormatTaskMarkdown(Guid taskId, string taskKey, ProjectBoardTaskSnapshotDto snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var sb = new StringBuilder(1024);
        sb.Append("---\n");

        // Deterministically sorted YAML frontmatter keys:
        // assignee, blocked, blocked_reason, depends_on, id, key, labels, parent_task_id,
        // priority, revision, sort_key, spatial_ref, status, title, updated_at
        WriteAssigneeYaml(sb, snapshot.Assignee);
        sb.Append("blocked: ").Append(snapshot.Blocked ? "true" : "false").Append('\n');
        sb.Append("blocked_reason: ").Append(FormatNullableScalar(snapshot.BlockedReason)).Append('\n');
        WriteStringListYaml(sb, "depends_on", snapshot.DependsOn.Select(id => id.ToString("D")).ToList());
        sb.Append("id: ").Append(FormatScalar(taskId.ToString("D"))).Append('\n');
        sb.Append("key: ").Append(FormatScalar(taskKey)).Append('\n');
        WriteStringListYaml(sb, "labels", snapshot.Labels);
        sb.Append("parent_task_id: ").Append(snapshot.ParentTaskId.HasValue ? FormatScalar(snapshot.ParentTaskId.Value.ToString("D")) : "null").Append('\n');
        sb.Append("priority: ").Append(FormatScalar(snapshot.Priority)).Append('\n');
        sb.Append("revision: ").Append(snapshot.Revision.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("sort_key: ").Append(FormatScalar(snapshot.SortKey)).Append('\n');
        WriteSpatialRefYaml(sb, snapshot.SpatialRef);
        sb.Append("status: ").Append(FormatScalar(snapshot.Status)).Append('\n');
        sb.Append("title: ").Append(FormatScalar(snapshot.Title)).Append('\n');
        var updatedIso = DateTimeOffset.FromUnixTimeMilliseconds(snapshot.UpdatedAtUnixMs).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        sb.Append("updated_at: ").Append(FormatScalar(updatedIso)).Append('\n');
        sb.Append("---\n\n");

        sb.Append("## 目标描述\n\n");
        var desc = NormalizeLineEndings(snapshot.DescriptionMd).Trim();
        if (desc.Length > 0)
        {
            sb.Append(desc).Append("\n\n");
        }

        sb.Append("## 验收标准\n");
        if (snapshot.Checklist.Count > 0)
        {
            sb.Append('\n');
            foreach (var item in snapshot.Checklist)
            {
                var box = item.Done ? "[x]" : "[ ]";
                var cleanText = NormalizeLineEndings(item.Text).Replace('\n', ' ').Trim();
                var idPart = string.IsNullOrWhiteSpace(item.Id) ? string.Empty : $" <!-- id: {item.Id.Trim()} -->";
                sb.Append("- ").Append(box).Append(' ').Append(cleanText).Append(idPart).Append('\n');
            }
        }

        return sb.ToString();
    }

    public sealed record ParsedMarkdownTask(
        Guid? TaskId,
        string? TaskKey,
        ProjectBoardTaskSnapshotDto Snapshot);

    public static ParsedMarkdownTask ParseTaskMarkdown(string rawContent, string fallbackKey, long fallbackNowUnixMs)
    {
        var text = NormalizeLineEndings(rawContent ?? string.Empty);
        if (text.StartsWith('\uFEFF'))
            text = text[1..];

        var frontmatterLines = new List<string>();
        var body = text;

        if (text.StartsWith("---\n", StringComparison.Ordinal))
        {
            var endIdx = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            if (endIdx < 0 && text.EndsWith("\n---", StringComparison.Ordinal))
                endIdx = text.Length - 4;

            if (endIdx >= 4)
            {
                var fmBlock = text[4..endIdx];
                frontmatterLines.AddRange(fmBlock.Split('\n'));
                var afterMarker = endIdx + 5;
                body = afterMarker <= text.Length ? text[afterMarker..] : string.Empty;
            }
        }

        var scalars = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var lists = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var maps = new Dictionary<string, Dictionary<string, string?>>(StringComparer.OrdinalIgnoreCase);

        ParseFrontmatter(frontmatterLines, scalars, lists, maps);

        Guid? parsedId = scalars.TryGetValue("id", out var rawId) && Guid.TryParse(rawId, out var gId) ? gId : null;
        var parsedKey = scalars.TryGetValue("key", out var rawKey) && !string.IsNullOrWhiteSpace(rawKey)
            ? rawKey.Trim()
            : fallbackKey;

        var title = scalars.TryGetValue("title", out var rawTitle) && !string.IsNullOrWhiteSpace(rawTitle)
            ? rawTitle.Trim()
            : parsedKey;

        var rawStatus = scalars.TryGetValue("status", out var st) ? st?.Trim().ToLowerInvariant() : null;
        var status = ProjectBoardTaskStatuses.IsValid(rawStatus) ? rawStatus! : ProjectBoardTaskStatuses.Backlog;

        var rawPriority = scalars.TryGetValue("priority", out var pr) ? pr?.Trim().ToLowerInvariant() : null;
        var priority = ProjectBoardPriorities.IsValid(rawPriority) ? rawPriority! : ProjectBoardPriorities.None;

        var blocked = scalars.TryGetValue("blocked", out var bl)
            && (string.Equals(bl, "true", StringComparison.OrdinalIgnoreCase) || bl == "1" || string.Equals(bl, "yes", StringComparison.OrdinalIgnoreCase));

        var blockedReason = scalars.TryGetValue("blocked_reason", out var br) && !string.IsNullOrWhiteSpace(br)
            ? br.Trim()
            : null;

        ProjectBoardAssigneeDto? assignee = ParseAssignee(scalars, maps);

        Guid? parentTaskId = scalars.TryGetValue("parent_task_id", out var rawParent) && Guid.TryParse(rawParent, out var pGuid) && pGuid != Guid.Empty
            ? pGuid
            : null;

        var dependsOn = new List<Guid>();
        if (lists.TryGetValue("depends_on", out var depList))
        {
            foreach (var item in depList)
            {
                if (Guid.TryParse(item, out var depId) && depId != Guid.Empty && !dependsOn.Contains(depId))
                    dependsOn.Add(depId);
            }
        }

        var labels = new List<string>();
        if (lists.TryGetValue("labels", out var labelList))
        {
            foreach (var item in labelList)
            {
                var trimmed = item.Trim();
                if (trimmed.Length > 0 && !labels.Contains(trimmed, StringComparer.Ordinal))
                    labels.Add(trimmed);
            }
        }

        var spatialRef = ParseSpatialRef(maps);

        var sortKey = scalars.TryGetValue("sort_key", out var sk) && !string.IsNullOrWhiteSpace(sk)
            ? sk.Trim()
            : "V";

        var revision = scalars.TryGetValue("revision", out var revStr) && long.TryParse(revStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRev) && parsedRev > 0
            ? parsedRev
            : 1L;

        var updatedAtUnixMs = fallbackNowUnixMs;
        if (scalars.TryGetValue("updated_at", out var updStr) && !string.IsNullOrWhiteSpace(updStr))
        {
            if (long.TryParse(updStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixMs))
                updatedAtUnixMs = unixMs;
            else if (DateTimeOffset.TryParse(updStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                updatedAtUnixMs = dto.ToUnixTimeMilliseconds();
        }

        var (descriptionMd, checklist) = ParseMarkdownBody(body);

        var snapshot = new ProjectBoardTaskSnapshotDto
        {
            Title = title,
            DescriptionMd = descriptionMd,
            Checklist = checklist,
            Status = status,
            Priority = priority,
            Blocked = blocked,
            BlockedReason = blockedReason,
            Assignee = assignee,
            ParentTaskId = parentTaskId,
            DependsOn = dependsOn,
            Labels = labels,
            SpatialRef = spatialRef,
            SortKey = sortKey,
            Revision = revision,
            UpdatedAtUnixMs = updatedAtUnixMs
        };

        return new ParsedMarkdownTask(parsedId, parsedKey, snapshot);
    }

    public static ProjectBoardTaskSnapshotDto ToSnapshot(ProjectBoardTaskRecord row) => new()
    {
        Title = row.Title,
        DescriptionMd = row.DescriptionMd,
        Checklist = DeserializeList<ProjectBoardChecklistItemDto>(row.ChecklistJson),
        Status = row.Status,
        Priority = row.Priority,
        Blocked = row.Blocked,
        BlockedReason = row.BlockedReason,
        Assignee = DeserializeNullable<ProjectBoardAssigneeDto>(row.AssigneeJson),
        ParentTaskId = row.ParentTaskId,
        DependsOn = DeserializeList<Guid>(row.DependsOnJson),
        Labels = DeserializeList<string>(row.LabelsJson),
        SpatialRef = DeserializeNullable<ProjectBoardSpatialRefDto>(row.SpatialRefJson),
        SortKey = row.SortKey,
        Revision = row.Revision,
        UpdatedAtUnixMs = row.UpdatedAtUnixMs
    };

    /// <summary>
    /// Performs a deterministic 3-way field merge between <paramref name="baseSnapshot"/>,
    /// <paramref name="fileSnapshot"/>, and <paramref name="boardSnapshot"/>.
    /// Returns conflicting field names when the exact same declarative field diverged on both sides.
    /// </summary>
    public static (ProjectBoardTaskSnapshotDto Merged, List<string> ConflictingFields, bool ChangedFromBoard) MergeThreeWay(
        ProjectBoardTaskSnapshotDto baseSnapshot,
        ProjectBoardTaskSnapshotDto fileSnapshot,
        ProjectBoardTaskSnapshotDto boardSnapshot,
        long nowUnixMs)
    {
        var conflicts = new List<string>();

        T MergeField<T>(string fieldName, T baseVal, T fileVal, T boardVal, Func<T, T, bool> equals)
        {
            var fileChanged = !equals(fileVal, baseVal);
            var boardChanged = !equals(boardVal, baseVal);

            if (!fileChanged) return boardVal;
            if (!boardChanged) return fileVal;
            if (equals(fileVal, boardVal)) return boardVal;

            conflicts.Add(fieldName);
            return boardVal;
        }

        var mergedTitle = MergeField("title", baseSnapshot.Title, fileSnapshot.Title, boardSnapshot.Title, StringEquals);
        var mergedDesc = MergeField("description_md", NormalizeLineEndings(baseSnapshot.DescriptionMd).Trim(), NormalizeLineEndings(fileSnapshot.DescriptionMd).Trim(), NormalizeLineEndings(boardSnapshot.DescriptionMd).Trim(), StringEquals);
        var mergedChecklist = MergeField("checklist", baseSnapshot.Checklist, fileSnapshot.Checklist, boardSnapshot.Checklist, JsonEquals);
        var mergedStatus = MergeField("status", baseSnapshot.Status, fileSnapshot.Status, boardSnapshot.Status, StringEquals);
        var mergedPriority = MergeField("priority", baseSnapshot.Priority, fileSnapshot.Priority, boardSnapshot.Priority, StringEquals);
        var mergedBlocked = MergeField("blocked", baseSnapshot.Blocked, fileSnapshot.Blocked, boardSnapshot.Blocked, static (a, b) => a == b);
        var mergedBlockedReason = MergeField("blocked_reason", baseSnapshot.BlockedReason, fileSnapshot.BlockedReason, boardSnapshot.BlockedReason, NullableStringEquals);
        var mergedAssignee = MergeField("assignee", baseSnapshot.Assignee, fileSnapshot.Assignee, boardSnapshot.Assignee, JsonEquals);
        var mergedParent = MergeField("parent_task_id", baseSnapshot.ParentTaskId, fileSnapshot.ParentTaskId, boardSnapshot.ParentTaskId, static (a, b) => a == b);
        var mergedDepends = MergeField("depends_on", baseSnapshot.DependsOn, fileSnapshot.DependsOn, boardSnapshot.DependsOn, GuidSetEquals);
        var mergedLabels = MergeField("labels", baseSnapshot.Labels, fileSnapshot.Labels, boardSnapshot.Labels, StringListEquals);
        var mergedSpatial = MergeField("spatial_ref", baseSnapshot.SpatialRef, fileSnapshot.SpatialRef, boardSnapshot.SpatialRef, JsonEquals);
        var mergedSortKey = MergeField("sort_key", baseSnapshot.SortKey, fileSnapshot.SortKey, boardSnapshot.SortKey, StringEquals);

        var merged = new ProjectBoardTaskSnapshotDto
        {
            Title = mergedTitle,
            DescriptionMd = mergedDesc,
            Checklist = mergedChecklist,
            Status = mergedStatus,
            Priority = mergedPriority,
            Blocked = mergedBlocked,
            BlockedReason = mergedBlockedReason,
            Assignee = mergedAssignee,
            ParentTaskId = mergedParent,
            DependsOn = mergedDepends,
            Labels = mergedLabels,
            SpatialRef = mergedSpatial,
            SortKey = mergedSortKey,
            Revision = boardSnapshot.Revision + 1,
            UpdatedAtUnixMs = nowUnixMs
        };

        var changedFromBoard = !DeclarativeEquals(merged, boardSnapshot);
        return (merged, conflicts, changedFromBoard);
    }

    public static bool DeclarativeEquals(ProjectBoardTaskSnapshotDto a, ProjectBoardTaskSnapshotDto b) =>
        StringEquals(a.Title, b.Title)
        && StringEquals(NormalizeLineEndings(a.DescriptionMd).Trim(), NormalizeLineEndings(b.DescriptionMd).Trim())
        && JsonEquals(a.Checklist, b.Checklist)
        && StringEquals(a.Status, b.Status)
        && StringEquals(a.Priority, b.Priority)
        && a.Blocked == b.Blocked
        && NullableStringEquals(a.BlockedReason, b.BlockedReason)
        && JsonEquals(a.Assignee, b.Assignee)
        && a.ParentTaskId == b.ParentTaskId
        && GuidSetEquals(a.DependsOn, b.DependsOn)
        && StringListEquals(a.Labels, b.Labels)
        && JsonEquals(a.SpatialRef, b.SpatialRef)
        && StringEquals(a.SortKey, b.SortKey);

    private static bool StringEquals(string? a, string? b) =>
        string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.Ordinal);

    private static bool NullableStringEquals(string? a, string? b)
    {
        var na = string.IsNullOrWhiteSpace(a) ? null : a.Trim();
        var nb = string.IsNullOrWhiteSpace(b) ? null : b.Trim();
        return string.Equals(na, nb, StringComparison.Ordinal);
    }

    private static bool GuidSetEquals(List<Guid>? a, List<Guid>? b)
    {
        var la = a ?? [];
        var lb = b ?? [];
        return la.Count == lb.Count && la.SequenceEqual(lb);
    }

    private static bool StringListEquals(List<string>? a, List<string>? b)
    {
        var la = a ?? [];
        var lb = b ?? [];
        return la.Count == lb.Count && la.SequenceEqual(lb, StringComparer.Ordinal);
    }

    private static bool JsonEquals<T>(T? a, T? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return string.Equals(
            JsonSerializer.Serialize(a, CanonicalJson),
            JsonSerializer.Serialize(b, CanonicalJson),
            StringComparison.Ordinal);
    }

    private static void WriteAssigneeYaml(StringBuilder sb, ProjectBoardAssigneeDto? assignee)
    {
        if (assignee is null)
        {
            sb.Append("assignee: null\n");
            return;
        }

        sb.Append("assignee:\n");
        sb.Append("  display_name: ").Append(FormatNullableScalar(assignee.DisplayName)).Append('\n');
        sb.Append("  harness_id: ").Append(FormatNullableScalar(assignee.HarnessId)).Append('\n');
        sb.Append("  id: ").Append(FormatNullableScalar(assignee.Id)).Append('\n');
        sb.Append("  kind: ").Append(FormatScalar(assignee.Kind)).Append('\n');
        sb.Append("  mode_version_id: ").Append(assignee.ModeVersionId.HasValue ? FormatScalar(assignee.ModeVersionId.Value.ToString("D")) : "null").Append('\n');
    }

    private static void WriteSpatialRefYaml(StringBuilder sb, ProjectBoardSpatialRefDto? spatialRef)
    {
        if (spatialRef is null)
        {
            sb.Append("spatial_ref: null\n");
            return;
        }

        sb.Append("spatial_ref:\n");
        sb.Append("  object_id: ").Append(FormatScalar(spatialRef.ObjectId)).Append('\n');
        sb.Append("  session_id: ").Append(FormatScalar(spatialRef.SessionId.ToString("D"))).Append('\n');
        var hintJson = spatialRef.ViewportHint is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } vh
            ? FormatScalar(vh.GetRawText())
            : "null";
        sb.Append("  viewport_hint: ").Append(hintJson).Append('\n');
    }

    private static void WriteStringListYaml(StringBuilder sb, string key, List<string> values)
    {
        if (values.Count == 0)
        {
            sb.Append(key).Append(": []\n");
            return;
        }

        sb.Append(key).Append(":\n");
        foreach (var val in values)
        {
            sb.Append("  - ").Append(FormatScalar(val)).Append('\n');
        }
    }

    private static string FormatNullableScalar(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "null" : FormatScalar(value);

    private static string FormatScalar(string value) =>
        JsonSerializer.Serialize(value ?? string.Empty);

    private static void ParseFrontmatter(
        List<string> lines,
        Dictionary<string, string?> scalars,
        Dictionary<string, List<string>> lists,
        Dictionary<string, Dictionary<string, string?>> maps)
    {
        string? currentSection = null;

        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
                continue;

            var trimmedStart = rawLine.TrimStart(' ');
            if (trimmedStart.StartsWith('#'))
                continue;

            var indent = rawLine.Length - trimmedStart.Length;
            if (indent == 0)
            {
                currentSection = null;
                var colonIdx = rawLine.IndexOf(':');
                if (colonIdx <= 0) continue;

                var key = rawLine[..colonIdx].Trim();
                var rest = rawLine[(colonIdx + 1)..].Trim();

                if (rest.Length == 0)
                {
                    currentSection = key;
                    continue;
                }

                if (rest == "[]")
                {
                    lists[key] = [];
                    continue;
                }

                if (rest.StartsWith('[') && rest.EndsWith(']'))
                {
                    lists[key] = ParseInlineArray(rest);
                    continue;
                }

                scalars[key] = ParseYamlScalar(rest);
            }
            else if (currentSection is not null)
            {
                var line = trimmedStart.TrimEnd();
                if (line.StartsWith("- ", StringComparison.Ordinal) || line == "-")
                {
                    if (!lists.TryGetValue(currentSection, out var list))
                        lists[currentSection] = list = [];

                    var itemRaw = line.Length > 2 ? line[2..].Trim() : string.Empty;
                    var parsedItem = ParseYamlScalar(itemRaw);
                    if (!string.IsNullOrEmpty(parsedItem))
                        list.Add(parsedItem);
                }
                else
                {
                    var colonIdx = line.IndexOf(':');
                    if (colonIdx > 0)
                    {
                        if (!maps.TryGetValue(currentSection, out var map))
                            maps[currentSection] = map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

                        var subKey = line[..colonIdx].Trim();
                        var subVal = line[(colonIdx + 1)..].Trim();
                        map[subKey] = ParseYamlScalar(subVal);
                    }
                }
            }
        }
    }

    private static List<string> ParseInlineArray(string raw)
    {
        var inner = raw[1..^1].Trim();
        if (inner.Length == 0) return [];
        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(raw);
            if (parsed is not null) return parsed;
        }
        catch (JsonException)
        {
        }

        return inner.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => ParseYamlScalar(s.Trim()))
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .ToList();
    }

    private static string? ParseYamlScalar(string raw)
    {
        if (raw.Length == 0 || string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase) || raw == "~")
            return null;

        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(raw);
            }
            catch (JsonException)
            {
                return raw[1..^1];
            }
        }

        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
        {
            return raw[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        return raw;
    }

    private static ProjectBoardAssigneeDto? ParseAssignee(
        Dictionary<string, string?> scalars,
        Dictionary<string, Dictionary<string, string?>> maps)
    {
        if (maps.TryGetValue("assignee", out var map) && map.Count > 0)
        {
            var rawKind = map.TryGetValue("kind", out var k) ? k?.Trim().ToLowerInvariant() : null;
            if (!ProjectBoardAssigneeKinds.IsValid(rawKind))
                return null;

            Guid? modeVer = map.TryGetValue("mode_version_id", out var mv) && Guid.TryParse(mv, out var mvGuid) && mvGuid != Guid.Empty
                ? mvGuid
                : null;

            return new ProjectBoardAssigneeDto
            {
                Kind = rawKind!,
                Id = EmptyToNull(map.GetValueOrDefault("id")),
                DisplayName = EmptyToNull(map.GetValueOrDefault("display_name")),
                ModeVersionId = modeVer,
                HarnessId = EmptyToNull(map.GetValueOrDefault("harness_id"))
            };
        }

        if (scalars.TryGetValue("assignee", out var shorthand) && !string.IsNullOrWhiteSpace(shorthand))
        {
            var normalized = shorthand.Trim().ToLowerInvariant();
            if (ProjectBoardAssigneeKinds.IsValid(normalized))
                return new ProjectBoardAssigneeDto { Kind = normalized };
        }

        return null;
    }

    private static ProjectBoardSpatialRefDto? ParseSpatialRef(Dictionary<string, Dictionary<string, string?>> maps)
    {
        if (!maps.TryGetValue("spatial_ref", out var map) || map.Count == 0)
            return null;

        if (!map.TryGetValue("session_id", out var rawSession) || !Guid.TryParse(rawSession, out var sessionId) || sessionId == Guid.Empty)
            return null;

        var objectId = map.GetValueOrDefault("object_id")?.Trim();
        if (string.IsNullOrEmpty(objectId))
            return null;

        JsonElement? viewportHint = null;
        if (map.TryGetValue("viewport_hint", out var vhRaw) && !string.IsNullOrWhiteSpace(vhRaw))
        {
            try
            {
                using var doc = JsonDocument.Parse(vhRaw);
                viewportHint = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                viewportHint = JsonSerializer.SerializeToElement(vhRaw);
            }
        }

        return new ProjectBoardSpatialRefDto
        {
            SessionId = sessionId,
            ObjectId = objectId,
            ViewportHint = viewportHint
        };
    }

    private static (string DescriptionMd, List<ProjectBoardChecklistItemDto> Checklist) ParseMarkdownBody(string body)
    {
        const string descHeading = "## 目标描述";
        const string checkHeading = "## 验收标准";

        var normalized = NormalizeLineEndings(body);
        var checkHeadingIdx = FindHeadingIndex(normalized, checkHeading);
        string descSection;
        string? checkSection = null;

        if (checkHeadingIdx >= 0)
        {
            descSection = normalized[..checkHeadingIdx];
            var afterCheckHeading = normalized.IndexOf('\n', checkHeadingIdx);
            checkSection = afterCheckHeading >= 0 ? normalized[(afterCheckHeading + 1)..] : string.Empty;
        }
        else
        {
            descSection = normalized;
        }

        var descHeadingIdx = FindHeadingIndex(descSection, descHeading);
        if (descHeadingIdx >= 0)
        {
            var afterDescHeading = descSection.IndexOf('\n', descHeadingIdx);
            descSection = afterDescHeading >= 0 ? descSection[(afterDescHeading + 1)..] : string.Empty;
        }

        var checklist = new List<ProjectBoardChecklistItemDto>();
        if (checkSection is not null)
        {
            var idx = 0;
            foreach (var rawLine in checkSection.Split('\n'))
            {
                var trimmed = rawLine.Trim();
                var match = ChecklistLineRegex().Match(trimmed);
                if (!match.Success) continue;

                var isDone = string.Equals(match.Groups[1].Value, "x", StringComparison.OrdinalIgnoreCase);
                var itemBody = match.Groups[2].Value.Trim();
                string itemId;

                var idMatch = TrailingChecklistIdRegex().Match(itemBody);
                if (idMatch.Success)
                {
                    itemId = idMatch.Groups[1].Value.Trim();
                    itemBody = itemBody[..idMatch.Index].TrimEnd();
                }
                else
                {
                    idx++;
                    itemId = $"chk-{idx}";
                }

                checklist.Add(new ProjectBoardChecklistItemDto
                {
                    Id = itemId,
                    Text = itemBody,
                    Done = isDone
                });
            }
        }

        return (descSection.Trim(), checklist);
    }

    private static int FindHeadingIndex(string content, string heading)
    {
        if (content.StartsWith(heading + "\n", StringComparison.Ordinal) || string.Equals(content, heading, StringComparison.Ordinal))
            return 0;

        var needle = "\n" + heading;
        var pos = content.IndexOf(needle, StringComparison.Ordinal);
        while (pos >= 0)
        {
            var after = pos + needle.Length;
            if (after == content.Length || content[after] == '\n' || content[after] == ' ')
                return pos + 1;
            pos = content.IndexOf(needle, pos + 1, StringComparison.Ordinal);
        }

        return -1;
    }

    private static string? EmptyToNull(string? val) =>
        string.IsNullOrWhiteSpace(val) ? null : val.Trim();

    internal static List<T> DeserializeList<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, CanonicalJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static T? DeserializeNullable<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, CanonicalJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^[-*]\s+\[([ xX])\]\s*(.*)$")]
    private static partial Regex ChecklistLineRegex();

    [GeneratedRegex(@"<!--\s*id:\s*([a-zA-Z0-9_.:-]+)\s*-->$")]
    private static partial Regex TrailingChecklistIdRegex();
}
