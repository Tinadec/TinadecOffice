using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Runtime.ProjectBoard;

/// <summary>
/// Pure deterministic planner that extracts titles, markdown bodies, checklists, section child hierarchies,
/// and acyclic connector-derived <c>depends_on</c> edges from a <see cref="SpatialBoardSnapshotDto"/>.
/// </summary>
internal static partial class ProjectBoardSpatialPromoter
{
    internal sealed class PlannedBoardTask
    {
        public Guid TaskId { get; init; }
        public string PrimaryObjectId { get; init; } = string.Empty;
        public List<string> LinkedObjectIds { get; init; } = [];
        public Guid? ParentTaskId { get; init; }
        public string Title { get; init; } = string.Empty;
        public string DescriptionMd { get; init; } = string.Empty;
        public List<ProjectBoardChecklistItemDto> Checklist { get; init; } = [];
        public List<Guid> DependsOn { get; } = [];
    }

    internal sealed record PromotionPlan(
        Guid? ParentTaskId,
        List<PlannedBoardTask> Tasks);

    private sealed record ExtractedObjectContent(
        string Title,
        string DescriptionMd,
        string FullRawText,
        List<ProjectBoardChecklistItemDto> ExplicitChecklist,
        List<string> BulletOrBodyLines);

    public static PromotionPlan BuildPlan(
        string mode,
        IReadOnlyList<SpatialBoardObjectDto> selectedObjects,
        SpatialBoardSnapshotDto snapshot,
        IReadOnlyDictionary<Guid, (Guid? ParentId, List<Guid> DependsOn)> existingBoardGraph)
    {
        var promotable = selectedObjects
            .Where(o => o.Kind is not ("connector" or "drawing"))
            .ToList();
        if (promotable.Count == 0)
            promotable = [.. selectedObjects];

        return mode switch
        {
            SpatialPromotionModes.MergeSingle => BuildMergeSinglePlan(promotable, selectedObjects, snapshot),
            SpatialPromotionModes.ParentWithChildren => BuildParentWithChildrenPlan(promotable, snapshot, existingBoardGraph),
            _ => BuildSplitManyPlan(promotable, snapshot, existingBoardGraph)
        };
    }

    private static PromotionPlan BuildMergeSinglePlan(
        IReadOnlyList<SpatialBoardObjectDto> promotable,
        IReadOnlyList<SpatialBoardObjectDto> allSelected,
        SpatialBoardSnapshotDto snapshot)
    {
        var expanded = new List<SpatialBoardObjectDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var obj in promotable)
        {
            if (seen.Add(obj.Id))
                expanded.Add(obj);

            if (string.Equals(obj.Kind, "section", StringComparison.Ordinal))
            {
                foreach (var child in GetSectionChildNodes(obj.Id, snapshot))
                {
                    if (seen.Add(child.Id))
                        expanded.Add(child);
                }
            }
        }

        var firstObj = expanded[0];
        var firstContent = ExtractContent(firstObj);
        var title = firstContent.Title;

        string descriptionMd;
        var checklist = new List<ProjectBoardChecklistItemDto>();

        if (expanded.Count == 1)
        {
            descriptionMd = firstContent.DescriptionMd;
            if (firstContent.ExplicitChecklist.Count > 0)
            {
                checklist.AddRange(firstContent.ExplicitChecklist);
            }
            else if (firstContent.BulletOrBodyLines.Count > 0)
            {
                foreach (var line in firstContent.BulletOrBodyLines)
                {
                    checklist.Add(new ProjectBoardChecklistItemDto
                    {
                        Text = ClampText(line, 240),
                        Done = false
                    });
                }
            }
        }
        else
        {
            var bodyParagraphs = new List<string>();
            var contentNodes = expanded.Where(o => !string.Equals(o.Kind, "section", StringComparison.Ordinal)).ToList();
            if (contentNodes.Count == 0)
                contentNodes = expanded;

            if (string.Equals(firstObj.Kind, "section", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(firstContent.DescriptionMd))
            {
                bodyParagraphs.Add(firstContent.DescriptionMd);
            }

            foreach (var item in contentNodes)
            {
                var extracted = ExtractContent(item);
                if (!string.IsNullOrWhiteSpace(extracted.FullRawText))
                    bodyParagraphs.Add(extracted.FullRawText);

                if (extracted.ExplicitChecklist.Count > 0)
                {
                    checklist.AddRange(extracted.ExplicitChecklist);
                }
                else
                {
                    checklist.Add(new ProjectBoardChecklistItemDto
                    {
                        Text = ClampText(extracted.Title, 240),
                        Done = false
                    });
                }
            }

            descriptionMd = string.Join("\n\n", bodyParagraphs.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        var linkedIds = new List<string>();
        var linkedSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var obj in expanded)
        {
            if (linkedSeen.Add(obj.Id))
                linkedIds.Add(obj.Id);
        }
        foreach (var sel in allSelected)
        {
            if (!string.Equals(sel.Kind, "connector", StringComparison.Ordinal) && linkedSeen.Add(sel.Id))
                linkedIds.Add(sel.Id);
        }

        var singleTask = new PlannedBoardTask
        {
            TaskId = Guid.NewGuid(),
            PrimaryObjectId = firstObj.Id,
            LinkedObjectIds = linkedIds,
            ParentTaskId = null,
            Title = title,
            DescriptionMd = descriptionMd,
            Checklist = checklist
        };

        return new PromotionPlan(ParentTaskId: null, Tasks: [singleTask]);
    }

    private static PromotionPlan BuildSplitManyPlan(
        IReadOnlyList<SpatialBoardObjectDto> promotable,
        SpatialBoardSnapshotDto snapshot,
        IReadOnlyDictionary<Guid, (Guid? ParentId, List<Guid> DependsOn)> existingBoardGraph)
    {
        var targetObjects = new List<SpatialBoardObjectDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sectionExpansion = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var obj in promotable)
        {
            if (string.Equals(obj.Kind, "section", StringComparison.Ordinal))
            {
                var children = GetSectionChildNodes(obj.Id, snapshot);
                if (children.Count > 0)
                {
                    sectionExpansion[obj.Id] = children.Select(c => c.Id).ToList();
                    foreach (var child in children)
                    {
                        if (seen.Add(child.Id))
                            targetObjects.Add(child);
                    }
                    continue;
                }
            }

            if (seen.Add(obj.Id))
                targetObjects.Add(obj);
        }

        var planned = new List<PlannedBoardTask>(targetObjects.Count);
        var objectToTaskIds = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);

        foreach (var obj in targetObjects)
        {
            var extracted = ExtractContent(obj);
            var task = new PlannedBoardTask
            {
                TaskId = Guid.NewGuid(),
                PrimaryObjectId = obj.Id,
                LinkedObjectIds = [obj.Id],
                ParentTaskId = null,
                Title = extracted.Title,
                DescriptionMd = extracted.DescriptionMd,
                Checklist = extracted.ExplicitChecklist
            };
            planned.Add(task);
            objectToTaskIds[obj.Id] = [task.TaskId];
        }

        foreach (var (sectionId, childIds) in sectionExpansion)
        {
            var mappedIds = childIds
                .Where(objectToTaskIds.ContainsKey)
                .SelectMany(id => objectToTaskIds[id])
                .Distinct()
                .ToList();
            if (mappedIds.Count > 0)
                objectToTaskIds[sectionId] = mappedIds;
        }

        PopulateAcyclicConnectorDependencies(planned, objectToTaskIds, snapshot, existingBoardGraph);
        return new PromotionPlan(ParentTaskId: null, Tasks: planned);
    }

    private static PromotionPlan BuildParentWithChildrenPlan(
        IReadOnlyList<SpatialBoardObjectDto> promotable,
        SpatialBoardSnapshotDto snapshot,
        IReadOnlyDictionary<Guid, (Guid? ParentId, List<Guid> DependsOn)> existingBoardGraph)
    {
        var parentObj = promotable.FirstOrDefault(o => string.Equals(o.Kind, "section", StringComparison.Ordinal))
            ?? promotable[0];

        var childObjects = new List<SpatialBoardObjectDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { parentObj.Id };

        if (string.Equals(parentObj.Kind, "section", StringComparison.Ordinal))
        {
            foreach (var child in GetSectionChildNodes(parentObj.Id, snapshot))
            {
                if (seen.Add(child.Id))
                    childObjects.Add(child);
            }
        }

        foreach (var obj in promotable)
        {
            if (string.Equals(obj.Id, parentObj.Id, StringComparison.Ordinal))
                continue;

            if (string.Equals(obj.Kind, "section", StringComparison.Ordinal))
            {
                var nested = GetSectionChildNodes(obj.Id, snapshot);
                if (nested.Count > 0)
                {
                    foreach (var child in nested)
                    {
                        if (seen.Add(child.Id))
                            childObjects.Add(child);
                    }
                    continue;
                }
            }

            if (seen.Add(obj.Id))
                childObjects.Add(obj);
        }

        var parentId = Guid.NewGuid();
        var parentExtracted = ExtractContent(parentObj);
        var parentTask = new PlannedBoardTask
        {
            TaskId = parentId,
            PrimaryObjectId = parentObj.Id,
            LinkedObjectIds = [parentObj.Id],
            ParentTaskId = null,
            Title = parentExtracted.Title,
            DescriptionMd = parentExtracted.DescriptionMd,
            Checklist = parentExtracted.ExplicitChecklist
        };

        var planned = new List<PlannedBoardTask>(1 + childObjects.Count) { parentTask };
        var objectToTaskIds = new Dictionary<string, List<Guid>>(StringComparer.Ordinal)
        {
            [parentObj.Id] = [parentId]
        };

        foreach (var childObj in childObjects)
        {
            var extracted = ExtractContent(childObj);
            var childTask = new PlannedBoardTask
            {
                TaskId = Guid.NewGuid(),
                PrimaryObjectId = childObj.Id,
                LinkedObjectIds = [childObj.Id],
                ParentTaskId = parentId,
                Title = extracted.Title,
                DescriptionMd = extracted.DescriptionMd,
                Checklist = extracted.ExplicitChecklist
            };
            planned.Add(childTask);
            objectToTaskIds[childObj.Id] = [childTask.TaskId];
        }

        PopulateAcyclicConnectorDependencies(planned, objectToTaskIds, snapshot, existingBoardGraph);
        return new PromotionPlan(ParentTaskId: parentId, Tasks: planned);
    }

    private static void PopulateAcyclicConnectorDependencies(
        IReadOnlyList<PlannedBoardTask> planned,
        IReadOnlyDictionary<string, List<Guid>> objectToTaskIds,
        SpatialBoardSnapshotDto snapshot,
        IReadOnlyDictionary<Guid, (Guid? ParentId, List<Guid> DependsOn)> existingBoardGraph)
    {
        if (planned.Count <= 1)
            return;

        var taskById = planned.ToDictionary(t => t.TaskId);
        var graph = new Dictionary<Guid, (Guid? ParentId, List<Guid> DependsOn)>(existingBoardGraph.Count + planned.Count);
        foreach (var (id, node) in existingBoardGraph)
            graph[id] = (node.ParentId, [.. node.DependsOn]);
        foreach (var task in planned)
            graph[task.TaskId] = (task.ParentTaskId, []);

        var connectors = snapshot.Objects.Values
            .Where(o => !o.Deleted && string.Equals(o.Kind, "connector", StringComparison.Ordinal))
            .OrderBy(o => o.SortKey, StringComparer.Ordinal)
            .ThenBy(o => o.Id, StringComparer.Ordinal);

        foreach (var conn in connectors)
        {
            var sourceObjId = ReadPropString(conn.Props, "sourceId", "source_id", "fromId", "from_id", "from")?.Trim();
            var targetObjId = ReadPropString(conn.Props, "targetId", "target_id", "toId", "to_id", "to")?.Trim();
            if (string.IsNullOrEmpty(sourceObjId) || string.IsNullOrEmpty(targetObjId))
                continue;

            if (!objectToTaskIds.TryGetValue(sourceObjId, out var prereqIds)
                || !objectToTaskIds.TryGetValue(targetObjId, out var dependentIds))
            {
                continue;
            }

            foreach (var dependentTaskId in dependentIds)
            {
                var dependentTask = taskById[dependentTaskId];
                foreach (var prereqTaskId in prereqIds)
                {
                    if (prereqTaskId == dependentTaskId || dependentTask.DependsOn.Contains(prereqTaskId))
                        continue;

                    var candidateDeps = new List<Guid>(dependentTask.DependsOn) { prereqTaskId };
                    try
                    {
                        ProjectBoardCoordinator.ValidateNoCyclesOrThrow(
                            dependentTaskId,
                            dependentTask.ParentTaskId,
                            candidateDeps,
                            graph,
                            requireExistingReferences: true);

                        dependentTask.DependsOn.Add(prereqTaskId);
                        graph[dependentTaskId] = (dependentTask.ParentTaskId, candidateDeps);
                    }
                    catch (ProjectBoardCycleException)
                    {
                        // Ignore cyclic or ancestor-referencing canvas arrows so promotion always yields a valid DAG.
                    }
                }
            }
        }
    }

    private static List<SpatialBoardObjectDto> GetSectionChildNodes(string sectionId, SpatialBoardSnapshotDto snapshot) =>
        snapshot.Objects.Values
            .Where(o => !o.Deleted
                && string.Equals(o.ParentId, sectionId, StringComparison.Ordinal)
                && o.Kind is "sticky" or "text")
            .OrderBy(o => o.Y)
            .ThenBy(o => o.X)
            .ThenBy(o => o.SortKey, StringComparer.Ordinal)
            .ThenBy(o => o.Id, StringComparer.Ordinal)
            .ToList();

    private static ExtractedObjectContent ExtractContent(SpatialBoardObjectDto obj)
    {
        var fallbackLabel = $"[{obj.Kind}] {obj.Id}";

        if (obj.Kind is "section" or "work" or "media")
        {
            var rawTitle = ReadPropString(obj.Props, "title", "name", "text", "path") ?? string.Empty;
            var rawBody = ReadPropString(obj.Props, "description", "summary", "snippet", "body") ?? string.Empty;
            var cleanTitle = CleanHeadingOrBulletPrefix(FirstNonEmptyLine(rawTitle));
            if (string.IsNullOrWhiteSpace(cleanTitle))
                cleanTitle = fallbackLabel;

            var normalizedBody = ProjectBoardMarkdownCodec.NormalizeLineEndings(rawBody).Trim();
            var fullText = string.IsNullOrWhiteSpace(normalizedBody)
                ? cleanTitle
                : $"{cleanTitle}\n{normalizedBody}";

            return new ExtractedObjectContent(
                Title: ClampText(cleanTitle, 512),
                DescriptionMd: normalizedBody,
                FullRawText: fullText,
                ExplicitChecklist: [],
                BulletOrBodyLines: []);
        }

        var rawText = ProjectBoardMarkdownCodec.NormalizeLineEndings(
            ReadPropString(obj.Props, "text", "title", "body", "label") ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new ExtractedObjectContent(
                Title: ClampText(fallbackLabel, 512),
                DescriptionMd: string.Empty,
                FullRawText: string.Empty,
                ExplicitChecklist: [],
                BulletOrBodyLines: []);
        }

        var allLines = rawText.Split('\n');
        var nonEmptyLines = allLines
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        var titleLine = CleanHeadingOrBulletPrefix(nonEmptyLines[0]);
        if (string.IsNullOrWhiteSpace(titleLine))
            titleLine = fallbackLabel;

        var explicitChecklist = new List<ProjectBoardChecklistItemDto>();
        var proseLines = new List<string>();
        var bulletOrBodyLines = new List<string>();

        for (var i = 1; i < nonEmptyLines.Count; i++)
        {
            var line = nonEmptyLines[i];
            var checkboxMatch = CheckboxLineRegex().Match(line);
            if (checkboxMatch.Success)
            {
                var itemText = checkboxMatch.Groups[2].Value.Trim();
                if (itemText.Length > 0)
                {
                    explicitChecklist.Add(new ProjectBoardChecklistItemDto
                    {
                        Text = ClampText(itemText, 240),
                        Done = string.Equals(checkboxMatch.Groups[1].Value, "x", StringComparison.OrdinalIgnoreCase)
                    });
                }
                continue;
            }

            proseLines.Add(line);
            var cleanedBullet = CleanHeadingOrBulletPrefix(line);
            if (cleanedBullet.Length > 0)
                bulletOrBodyLines.Add(cleanedBullet);
        }

        return new ExtractedObjectContent(
            Title: ClampText(titleLine, 512),
            DescriptionMd: string.Join("\n", proseLines).Trim(),
            FullRawText: rawText,
            ExplicitChecklist: explicitChecklist,
            BulletOrBodyLines: bulletOrBodyLines);
    }

    private static string? ReadPropString(IReadOnlyDictionary<string, JsonElement> props, params ReadOnlySpan<string> keys)
    {
        foreach (var key in keys)
        {
            if (props.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String)
            {
                var val = el.GetString();
                if (!string.IsNullOrWhiteSpace(val))
                    return val;
            }
        }
        return null;
    }

    private static string FirstNonEmptyLine(string text)
    {
        var norm = ProjectBoardMarkdownCodec.NormalizeLineEndings(text);
        foreach (var line in norm.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                return trimmed;
        }
        return string.Empty;
    }

    private static string CleanHeadingOrBulletPrefix(string line)
    {
        var trimmed = line.Trim();
        var checkbox = CheckboxLineRegex().Match(trimmed);
        if (checkbox.Success)
            return checkbox.Groups[2].Value.Trim();

        var bullet = LeadingMarkerRegex().Match(trimmed);
        if (bullet.Success)
            return bullet.Groups[1].Value.Trim();

        return trimmed;
    }

    private static string ClampText(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..maxLen].TrimEnd();

    [GeneratedRegex(@"^(?:[-*•]|\d+\.)\s+\[([ xX])\]\s+(.*)$")]
    private static partial Regex CheckboxLineRegex();

    [GeneratedRegex(@"^(?:#{1,6}|[-*•]|\d+[.)])\s+(.*)$")]
    private static partial Regex LeadingMarkerRegex();
}
