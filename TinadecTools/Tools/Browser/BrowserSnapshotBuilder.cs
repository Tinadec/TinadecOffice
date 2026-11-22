using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TinadecTools.Tools.Browser;

internal readonly record struct FrameNodeKey(string FrameKey, int BackendNodeId)
{
    public static FrameNodeKey For(string? frameId, int backendNodeId) =>
        new(string.IsNullOrEmpty(frameId) ? string.Empty : frameId, backendNodeId);
}

internal sealed record VerifiedDomLocator(
    string Locator,
    int BackendNodeId,
    string? TagName = null,
    string? InputType = null,
    string? ExplicitRole = null);

internal sealed record BrowserFrameAxInput(
    string? FrameId,
    string? CdpSessionId,
    JsonElement AxTree,
    string? ParentFrameId = null,
    int? OwnerBackendNodeId = null,
    string? FrameUrl = null,
    string? FrameTitle = null);

internal sealed class BrowserSnapshotOptions
{
    public int MaxChars { get; init; } = BrowserSnapshotBuilder.DefaultMaxChars;
    public string Scope { get; init; } = "full_page";
    public FrameNodeKey? SubtreeRoot { get; init; }
    public string? SubtreeFrameId { get; init; }
    public IReadOnlySet<FrameNodeKey>? ViewportNodes { get; init; }
    public bool HasBelowViewportContent { get; init; }
    public IReadOnlyDictionary<FrameNodeKey, VerifiedDomLocator>? VerifiedCssLocators { get; init; }
    public IReadOnlySet<(string Role, string Name)>? InvalidRoleNamePairs { get; init; }
    public bool IncludeLocators { get; init; } = true;
}

internal sealed record BrowserRef(
    int Ref,
    int BackendNodeId,
    string Role,
    string Name,
    string? FrameId = null,
    string? CdpSessionId = null,
    string? Locator = null,
    string? ChildFrameId = null);

internal sealed class SnapshotBuildResult
{
    public string Text { get; init; } = string.Empty;
    public bool Truncated { get; init; }
    public int RefCount { get; init; }
    public Dictionary<int, BrowserRef> Refs { get; init; } = new();
    public int NextRef { get; init; } = 1;
    public bool SubtreeRootFound { get; init; } = true;
}

/// <summary>
/// Turns Chromium's accessibility tree(s) into a compact, deterministic text tree with inline
/// <c>[ref=N]</c> markers, cross-iframe/OOPIF provenance, and verified unique <c>loc=...</c>
/// fallbacks per <c>native-snapshot-ref-requirement.md</c>.
/// </summary>
internal static class BrowserSnapshotBuilder
{
    public const int DefaultMaxChars = 24_000;
    public const int MinimumMaxChars = 2_048;
    public const int MaximumMaxChars = 64_000;

    private static readonly HashSet<string> ActionableRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "button", "link", "textbox", "searchbox", "checkbox", "radio", "combobox", "listbox",
        "option", "menuitem", "menuitemcheckbox", "menuitemradio", "tab", "switch", "slider",
        "spinbutton", "treeitem", "gridcell", "columnheader", "rowheader", "iframe",
    };

    private static readonly HashSet<string> UnnamedContainerRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "generic", "genericcontainer", "container", "none", "presentation", "group", "div", "section", "paragraph",
    };

    private static readonly HashSet<string> SemanticPreservedRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "image", "svg_root", "canvas", "iframe", "root", "rootwebarea", "webarea",
        "listitem", "cell", "gridcell", "columnheader", "rowheader", "row", "table", "list",
        "heading", "separator", "figure", "math", "video", "audio", "meter", "progressbar",
    };

    private static readonly HashSet<string> ValueRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "textbox", "searchbox", "combobox", "listbox", "slider", "spinbutton",
    };

    private static readonly HashSet<string> TextLeafRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "statictext", "inlinetextbox",
    };

    public static SnapshotBuildResult Build(
        JsonElement axResult,
        IReadOnlyDictionary<int, BrowserRef> previousRefs,
        int nextRef,
        int maxChars)
    {
        return BuildMultiFrame(
            [new BrowserFrameAxInput(null, null, axResult)],
            previousRefs,
            nextRef,
            new BrowserSnapshotOptions { MaxChars = maxChars });
    }

    public static SnapshotBuildResult BuildMultiFrame(
        IReadOnlyList<BrowserFrameAxInput> frames,
        IReadOnlyDictionary<int, BrowserRef> previousRefs,
        int nextRef,
        BrowserSnapshotOptions? options = null)
    {
        options ??= new BrowserSnapshotOptions();
        var maxChars = Math.Clamp(options.MaxChars, MinimumMaxChars, MaximumMaxChars);
        if (frames.Count == 0)
        {
            return new SnapshotBuildResult { Text = "(no accessibility nodes)", NextRef = nextRef };
        }

        // Index previous refs by (FrameKey, BackendNodeId) so repeated backendNodeIds across
        // different renderer processes stay completely isolated while same-document refs persist.
        var previousByFrameNode = new Dictionary<FrameNodeKey, BrowserRef>();
        var reservedRefIds = new HashSet<int>();
        foreach (var existing in previousRefs.Values)
        {
            if (existing.BackendNodeId > 0 && existing.Ref > 0)
            {
                previousByFrameNode[FrameNodeKey.For(existing.FrameId, existing.BackendNodeId)] = existing;
            }
        }

        // Parse each frame's AX nodes and simultaneously count (canonicalRole, normalizedName)
        // across ALL frames (including ignored/hidden nodes!) for global locator uniqueness.
        var parsedFrames = new List<ParsedFrame>(frames.Count);
        var roleNameOccurrences = new Dictionary<(string Role, string Name), HashSet<FrameNodeKey>>();
        foreach (var frame in frames)
        {
            var parsed = ParseFrame(frame, roleNameOccurrences);
            if (parsed is not null) parsedFrames.Add(parsed);
        }

        if (parsedFrames.Count == 0)
        {
            var firstTree = frames[0].AxTree;
            var emptyText = !firstTree.TryGetProperty("nodes", out var n) || n.ValueKind != JsonValueKind.Array
                ? "(no accessibility nodes)"
                : "(empty accessibility tree)";
            return new SnapshotBuildResult { Text = emptyText, NextRef = nextRef };
        }

        // Map owner (parentFrameKey, ownerBackendNodeId) -> child frames, plus unattached child frames.
        var childFramesByOwner = new Dictionary<FrameNodeKey, List<ParsedFrame>>();
        var unownedChildFrames = new List<ParsedFrame>();
        for (var i = 1; i < parsedFrames.Count; i++)
        {
            var childFrame = parsedFrames[i];
            if (childFrame.Input.OwnerBackendNodeId is > 0 and var ownerBackendId)
            {
                var ownerKey = FrameNodeKey.For(childFrame.Input.ParentFrameId, ownerBackendId);
                if (!childFramesByOwner.TryGetValue(ownerKey, out var list))
                {
                    list = new List<ParsedFrame>();
                    childFramesByOwner[ownerKey] = list;
                }
                list.Add(childFrame);
            }
            else
            {
                unownedChildFrames.Add(childFrame);
            }
        }

        // Build raw AST across frames (without mutating/focusing anything).
        var unownedIndex = 0;
        var astRoots = new List<AstNode>();
        var isSubtree = string.Equals(options.Scope, "subtree", StringComparison.OrdinalIgnoreCase);
        var subtreeFound = !isSubtree;

        if (isSubtree && !string.IsNullOrEmpty(options.SubtreeFrameId) && options.SubtreeRoot is null)
        {
            var targetFrame = parsedFrames.FirstOrDefault(f =>
                string.Equals(f.Input.FrameId, options.SubtreeFrameId, StringComparison.Ordinal));
            if (targetFrame is null)
            {
                return new SnapshotBuildResult
                {
                    Text = "(subtree root not found)",
                    NextRef = nextRef,
                    SubtreeRootFound = false,
                };
            }
            subtreeFound = true;
            foreach (var rootId in targetFrame.RootIds)
            {
                astRoots.AddRange(BuildAst(
                    targetFrame,
                    rootId,
                    0,
                    new HashSet<string>(StringComparer.Ordinal),
                    childFramesByOwner,
                    unownedChildFrames,
                    ref unownedIndex,
                    isChildFrameRoot: false));
            }
        }
        else
        {
            var primary = parsedFrames[0];
            foreach (var rootId in primary.RootIds)
            {
                astRoots.AddRange(BuildAst(
                    primary,
                    rootId,
                    0,
                    new HashSet<string>(StringComparer.Ordinal),
                    childFramesByOwner,
                    unownedChildFrames,
                    ref unownedIndex,
                    isChildFrameRoot: false));
            }

            // Attach any remaining unowned child frames (e.g., OOPIFs when DOM.getFrameOwner was unavailable)
            // under explicit iframe boundary nodes so their actionable elements are never lost.
            while (unownedIndex < unownedChildFrames.Count)
            {
                var remaining = unownedChildFrames[unownedIndex++];
                astRoots.Add(WrapChildFrame(
                    remaining,
                    childFramesByOwner,
                    unownedChildFrames,
                    ref unownedIndex));
            }

            if (isSubtree && options.SubtreeRoot is { } wantedRoot)
            {
                var matched = FindSubtreeNodes(astRoots, wantedRoot);
                if (matched.Count != 1)
                {
                    return new SnapshotBuildResult
                    {
                        Text = matched.Count == 0 ? "(subtree root not found)" : "(subtree root is ambiguous)",
                        NextRef = nextRef,
                        SubtreeRootFound = false,
                    };
                }
                subtreeFound = true;
                astRoots = [matched[0]];
            }
        }

        // Apply viewport filter if requested (`scope == "viewport"` or `"only_within_viewport"`).
        var isViewport = string.Equals(options.Scope, "viewport", StringComparison.OrdinalIgnoreCase)
            || string.Equals(options.Scope, "only_within_viewport", StringComparison.OrdinalIgnoreCase);
        if (isViewport && options.ViewportNodes is not null)
        {
            astRoots = FilterViewport(astRoots, options.ViewportNodes);
        }

        // Apply compact snapshot simplification (`native-snapshot-ref-requirement.md`).
        var compacted = new List<AstNode>();
        foreach (var root in astRoots)
        {
            compacted.AddRange(CompactNode(root));
        }

        var text = new StringBuilder();
        var newRefs = new Dictionary<int, BrowserRef>();
        var currentNext = Math.Max(1, nextRef);
        var truncated = false;

        int AllocateRefId(FrameNodeKey key)
        {
            if (previousByFrameNode.TryGetValue(key, out var existing) && reservedRefIds.Add(existing.Ref))
            {
                if (existing.Ref >= currentNext) currentNext = existing.Ref + 1;
                return existing.Ref;
            }
            while (reservedRefIds.Contains(currentNext)) currentNext++;
            var allocated = currentNext++;
            reservedRefIds.Add(allocated);
            return allocated;
        }

        bool AppendLine(string line)
        {
            if (text.Length + line.Length + 1 > maxChars)
            {
                truncated = true;
                return false;
            }
            text.Append(line).Append('\n');
            return true;
        }

        void Emit(AstNode node, int depth)
        {
            if (truncated || depth > 80) return;

            var line = new StringBuilder();
            line.Append(' ', depth * 2);
            line.Append(node.DisplayRole);

            if (node.EscapedName.Length > 0)
            {
                line.Append(" \"").Append(node.EscapedName).Append('"');
            }

            if (node.IsRefCandidate && node.BackendNodeId > 0)
            {
                var key = FrameNodeKey.For(node.FrameId, node.BackendNodeId);
                var refId = AllocateRefId(key);
                var verifiedLoc = options.IncludeLocators
                    ? ResolveVerifiedLocator(node, key, roleNameOccurrences, options)
                    : null;

                var browserRef = new BrowserRef(
                    Ref: refId,
                    BackendNodeId: node.BackendNodeId,
                    Role: node.DisplayRole,
                    Name: node.RawName,
                    FrameId: string.IsNullOrEmpty(node.FrameId) ? null : node.FrameId,
                    CdpSessionId: string.IsNullOrEmpty(node.CdpSessionId) ? null : node.CdpSessionId,
                    Locator: verifiedLoc,
                    ChildFrameId: node.ChildFrameId);

                newRefs[refId] = browserRef;
                line.Append(" [ref=").Append(refId).Append(']');
                if (node.StateSuffix.Length > 0) line.Append(node.StateSuffix);
                if (node.ValueSuffix.Length > 0) line.Append(node.ValueSuffix);
                if (!string.IsNullOrEmpty(verifiedLoc))
                {
                    line.Append(' ').Append(verifiedLoc);
                }
            }
            else
            {
                if (node.StateSuffix.Length > 0) line.Append(node.StateSuffix);
                if (node.ValueSuffix.Length > 0) line.Append(node.ValueSuffix);
            }

            if (!AppendLine(line.ToString())) return;

            foreach (var child in node.Children)
            {
                if (truncated) return;
                Emit(child, depth + 1);
            }
        }

        foreach (var root in compacted)
        {
            if (truncated) break;
            Emit(root, 0);
        }

        if (isViewport && options.HasBelowViewportContent && !truncated)
        {
            const string belowFold = "[more content below the viewport: scroll or use scope=\"full_page\" to inspect]";
            if (text.Length + belowFold.Length + 1 <= maxChars + 96)
            {
                text.Append(belowFold).Append('\n');
            }
        }

        if (truncated)
        {
            const string note = "\n… snapshot truncated; raise max_chars or narrow the page with wait/scroll …";
            if (text.Length + note.Length <= maxChars + 96) text.Append(note);
        }

        if (text.Length == 0) text.Append("(empty accessibility tree)");

        return new SnapshotBuildResult
        {
            Text = text.ToString().TrimEnd('\n'),
            Truncated = truncated,
            RefCount = newRefs.Count,
            Refs = newRefs,
            NextRef = currentNext,
            SubtreeRootFound = subtreeFound,
        };
    }

    private sealed class ParsedFrame
    {
        public required BrowserFrameAxInput Input { get; init; }
        public required Dictionary<string, JsonElement> ById { get; init; }
        public required List<string> RootIds { get; init; }
    }

    private sealed class AstNode
    {
        public required string RawRole { get; init; }
        public required string DisplayRole { get; set; }
        public required string RawName { get; init; }
        public required string EscapedName { get; init; }
        public string? FrameId { get; init; }
        public string? CdpSessionId { get; init; }
        public int BackendNodeId { get; init; }
        public bool IsRefCandidate { get; init; }
        public string StateSuffix { get; init; } = string.Empty;
        public string ValueSuffix { get; init; } = string.Empty;
        public string? AdvertisedLocator { get; init; }
        public string? DomTagName { get; init; }
        public string? ChildFrameId { get; set; }
        public List<AstNode> Children { get; set; } = new();
    }

    private static ParsedFrame? ParseFrame(
        BrowserFrameAxInput input,
        Dictionary<(string Role, string Name), HashSet<FrameNodeKey>> roleNameOccurrences)
    {
        if (!input.AxTree.TryGetProperty("nodes", out var nodesElement) || nodesElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var byId = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var childIds = new HashSet<string>(StringComparer.Ordinal);
        var syntheticCounter = 0;

        foreach (var node in nodesElement.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            var id = StringValue(node, "nodeId");
            if (string.IsNullOrEmpty(id)) continue;
            byId[id] = node.Clone();

            if (node.TryGetProperty("childIds", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in children.EnumerateArray())
                {
                    if (child.ValueKind == JsonValueKind.String && child.GetString() is { Length: > 0 } childId)
                    {
                        childIds.Add(childId);
                    }
                }
            }

            // Record every node (including ignored/hidden copies!) in roleNameOccurrences so a hidden
            // duplicate sharing role + accessible name prevents emitting an ambiguous role locator.
            var rawRole = RoleOf(node);
            var canonicalRole = CanonicalDisplayRole(rawRole, isChildFrameRoot: false);
            var normalizedName = NormalizeRoleNameForKey(NameOf(node));
            if (!string.IsNullOrEmpty(canonicalRole) && !string.IsNullOrEmpty(normalizedName))
            {
                var backendId = node.TryGetProperty("backendDOMNodeId", out var bEl) && bEl.TryGetInt32(out var bId) && bId > 0
                    ? bId
                    : --syntheticCounter;
                var key = (canonicalRole.ToLowerInvariant(), normalizedName);
                if (!roleNameOccurrences.TryGetValue(key, out var set))
                {
                    set = new HashSet<FrameNodeKey>();
                    roleNameOccurrences[key] = set;
                }
                set.Add(FrameNodeKey.For(input.FrameId, backendId));
            }
        }

        if (byId.Count == 0) return null;

        var roots = new List<string>();
        foreach (var id in byId.Keys)
        {
            if (!childIds.Contains(id)) roots.Add(id);
        }

        if (roots.Count == 0)
        {
            roots.Add(byId.Keys.First());
        }
        else
        {
            var rootWebArea = roots.FirstOrDefault(id =>
                string.Equals(RoleOf(byId[id]), "RootWebArea", StringComparison.OrdinalIgnoreCase)
                || string.Equals(RoleOf(byId[id]), "WebArea", StringComparison.OrdinalIgnoreCase));
            if (rootWebArea is not null)
            {
                roots.Remove(rootWebArea);
                roots.Insert(0, rootWebArea);
            }
        }

        return new ParsedFrame
        {
            Input = input,
            ById = byId,
            RootIds = roots,
        };
    }

    private static List<AstNode> BuildAst(
        ParsedFrame frame,
        string nodeId,
        int depth,
        HashSet<string> visited,
        Dictionary<FrameNodeKey, List<ParsedFrame>> childFramesByOwner,
        List<ParsedFrame> unownedChildFrames,
        ref int unownedIndex,
        bool isChildFrameRoot)
    {
        var output = new List<AstNode>();
        if (depth > 80 || visited.Count > 20_000) return output;
        if (!frame.ById.TryGetValue(nodeId, out var node)) return output;
        if (!visited.Add(nodeId)) return output;

        try
        {
            var ignored = node.TryGetProperty("ignored", out var ignoredEl) && ignoredEl.ValueKind == JsonValueKind.True;
            var rawRole = RoleOf(node);
            var displayRole = CanonicalDisplayRole(rawRole, isChildFrameRoot);
            var backendId = node.TryGetProperty("backendDOMNodeId", out var backendEl) && backendEl.TryGetInt32(out var bId) && bId > 0
                ? bId
                : 0;

            var isIframe = string.Equals(displayRole, "iframe", StringComparison.OrdinalIgnoreCase);

            // Never drop an iframe boundary even if AX marked IframePresentational as ignored.
            if (ignored && !isIframe)
            {
                return BuildAstChildren(frame, node, depth, visited, childFramesByOwner, unownedChildFrames, ref unownedIndex);
            }

            var rawName = NameOf(node);
            if (TextLeafRoles.Contains(rawRole) && string.IsNullOrEmpty(rawName))
            {
                rawName = RawValueOf(node) ?? string.Empty;
            }

            // Rule 1: Omit text nodes that are empty, whitespace-only (including NBSP), or
            // contain only zero-width formatting characters.
            if (TextLeafRoles.Contains(rawRole) && IsBlankOrZeroWidth(rawName))
            {
                var textChildren = BuildAstChildren(frame, node, depth, visited, childFramesByOwner, unownedChildFrames, ref unownedIndex);
                return textChildren;
            }

            var escapedName = IsBlankOrZeroWidth(rawName) ? string.Empty : Escape(rawName);
            var children = BuildAstChildren(frame, node, depth + 1, visited, childFramesByOwner, unownedChildFrames, ref unownedIndex);

            string? attachedChildFrameId = null;
            if (isIframe)
            {
                if (backendId > 0 && childFramesByOwner.TryGetValue(FrameNodeKey.For(frame.Input.FrameId, backendId), out var ownedFrames))
                {
                    foreach (var childFrame in ownedFrames)
                    {
                        attachedChildFrameId ??= childFrame.Input.FrameId;
                        children.AddRange(BuildChildFrameRootNodes(
                            childFrame,
                            depth + 1,
                            childFramesByOwner,
                            unownedChildFrames,
                            ref unownedIndex));
                    }
                }
                else if (children.Count == 0 && unownedIndex < unownedChildFrames.Count)
                {
                    var nextUnowned = unownedChildFrames[unownedIndex++];
                    attachedChildFrameId = nextUnowned.Input.FrameId;
                    children.AddRange(BuildChildFrameRootNodes(
                        nextUnowned,
                        depth + 1,
                        childFramesByOwner,
                        unownedChildFrames,
                        ref unownedIndex));
                }
            }

            var stateBuilder = new StringBuilder();
            AppendStateProperties(node, stateBuilder);

            var valueBuilder = new StringBuilder();
            if (ValueRoles.Contains(displayRole))
            {
                var rawVal = RawValueOf(node);
                if (!string.IsNullOrEmpty(rawVal))
                {
                    valueBuilder.Append(" value=\"").Append(Escape(rawVal)).Append('"');
                }
            }

            var isActionable = ActionableRoles.Contains(displayRole) && backendId > 0 && !ignored;
            var advertisedLoc = StringValue(node, "loc") ?? AxValue(node, "loc");
            var domTag = StringValue(node, "tagName") ?? AxValue(node, "htmlTag");

            output.Add(new AstNode
            {
                RawRole = rawRole,
                DisplayRole = string.IsNullOrEmpty(displayRole) ? "container" : displayRole,
                RawName = rawName,
                EscapedName = escapedName,
                FrameId = frame.Input.FrameId,
                CdpSessionId = frame.Input.CdpSessionId,
                BackendNodeId = backendId,
                IsRefCandidate = isActionable,
                StateSuffix = stateBuilder.ToString(),
                ValueSuffix = valueBuilder.ToString(),
                AdvertisedLocator = advertisedLoc,
                DomTagName = domTag,
                ChildFrameId = attachedChildFrameId,
                Children = children,
            });
            return output;
        }
        finally
        {
            visited.Remove(nodeId);
        }
    }

    private static List<AstNode> BuildAstChildren(
        ParsedFrame frame,
        JsonElement node,
        int depth,
        HashSet<string> visited,
        Dictionary<FrameNodeKey, List<ParsedFrame>> childFramesByOwner,
        List<ParsedFrame> unownedChildFrames,
        ref int unownedIndex)
    {
        var list = new List<AstNode>();
        if (!node.TryGetProperty("childIds", out var children) || children.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var child in children.EnumerateArray())
        {
            if (child.ValueKind == JsonValueKind.String && child.GetString() is { Length: > 0 } childId)
            {
                list.AddRange(BuildAst(
                    frame,
                    childId,
                    depth,
                    visited,
                    childFramesByOwner,
                    unownedChildFrames,
                    ref unownedIndex,
                    isChildFrameRoot: false));
            }
        }
        return list;
    }

    private static List<AstNode> BuildChildFrameRootNodes(
        ParsedFrame childFrame,
        int depth,
        Dictionary<FrameNodeKey, List<ParsedFrame>> childFramesByOwner,
        List<ParsedFrame> unownedChildFrames,
        ref int unownedIndex)
    {
        var nodes = new List<AstNode>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < childFrame.RootIds.Count; i++)
        {
            nodes.AddRange(BuildAst(
                childFrame,
                childFrame.RootIds[i],
                depth,
                visited,
                childFramesByOwner,
                unownedChildFrames,
                ref unownedIndex,
                isChildFrameRoot: i == 0));
        }
        return nodes;
    }

    private static AstNode WrapChildFrame(
        ParsedFrame childFrame,
        Dictionary<FrameNodeKey, List<ParsedFrame>> childFramesByOwner,
        List<ParsedFrame> unownedChildFrames,
        ref int unownedIndex)
    {
        var label = !string.IsNullOrWhiteSpace(childFrame.Input.FrameTitle)
            ? childFrame.Input.FrameTitle!
            : childFrame.Input.FrameUrl ?? string.Empty;
        var children = BuildChildFrameRootNodes(childFrame, 1, childFramesByOwner, unownedChildFrames, ref unownedIndex);
        return new AstNode
        {
            RawRole = "Iframe",
            DisplayRole = "iframe",
            RawName = label,
            EscapedName = IsBlankOrZeroWidth(label) ? string.Empty : Escape(label),
            FrameId = childFrame.Input.ParentFrameId,
            BackendNodeId = childFrame.Input.OwnerBackendNodeId ?? 0,
            IsRefCandidate = (childFrame.Input.OwnerBackendNodeId ?? 0) > 0,
            ChildFrameId = childFrame.Input.FrameId,
            Children = children,
        };
    }

    /// <summary>
    /// Implements exact tree compaction from <c>native-snapshot-ref-requirement.md</c>:
    /// - Omit blank/whitespace/NBSP/zero-width text leaves.
    /// - Omit unnamed containers with 0 remaining children.
    /// - Replace an unnamed container with its 1 remaining child.
    /// - Keep unnamed containers with >= 2 remaining children (normalized to <c>container</c>).
    /// - Keep named or ref-bearing containers and semantic nodes (<c>image</c>, <c>svg_root</c>,
    ///   <c>canvas</c>, <c>iframe</c>, <c>root</c>, list items, table cells).
    /// </summary>
    private static List<AstNode> CompactNode(AstNode node)
    {
        var compactedChildren = new List<AstNode>(node.Children.Count);
        foreach (var child in node.Children)
        {
            compactedChildren.AddRange(CompactNode(child));
        }
        node.Children = compactedChildren;

        // Deduplicate a single InlineTextBox child that merely repeats its parent StaticText/text node verbatim.
        if (TextLeafRoles.Contains(node.RawRole)
            && node.Children.Count == 1
            && string.Equals(node.Children[0].RawRole, "InlineTextBox", StringComparison.OrdinalIgnoreCase)
            && string.Equals(node.Children[0].EscapedName, node.EscapedName, StringComparison.Ordinal))
        {
            node.Children.Clear();
        }

        if (TextLeafRoles.Contains(node.RawRole) && node.EscapedName.Length == 0 && node.Children.Count == 0)
        {
            return [];
        }

        if (IsSimplifiableUnnamedContainer(node))
        {
            if (node.Children.Count == 0) return [];
            if (node.Children.Count == 1) return [node.Children[0]];
            node.DisplayRole = "container";
            return [node];
        }

        return [node];
    }

    private static bool IsSimplifiableUnnamedContainer(AstNode node)
    {
        if (node.EscapedName.Length > 0) return false;
        if (node.IsRefCandidate && node.BackendNodeId > 0) return false;
        if (node.StateSuffix.Length > 0 || node.ValueSuffix.Length > 0) return false;
        if (SemanticPreservedRoles.Contains(node.DisplayRole) || SemanticPreservedRoles.Contains(node.RawRole))
        {
            return false;
        }
        return UnnamedContainerRoles.Contains(node.RawRole) || UnnamedContainerRoles.Contains(node.DisplayRole);
    }

    private static List<AstNode> FindSubtreeNodes(IEnumerable<AstNode> nodes, FrameNodeKey target)
    {
        var matches = new List<AstNode>();
        void Walk(AstNode node)
        {
            if (node.BackendNodeId > 0 && FrameNodeKey.For(node.FrameId, node.BackendNodeId) == target)
            {
                matches.Add(node);
                return;
            }
            foreach (var child in node.Children) Walk(child);
        }
        foreach (var root in nodes) Walk(root);
        return matches;
    }

    private static List<AstNode> FilterViewport(List<AstNode> nodes, IReadOnlySet<FrameNodeKey> visibleSet)
    {
        var result = new List<AstNode>();
        foreach (var node in nodes)
        {
            var filtered = FilterViewportNode(node, visibleSet);
            if (filtered is not null) result.Add(filtered);
        }
        return result;
    }

    private static AstNode? FilterViewportNode(AstNode node, IReadOnlySet<FrameNodeKey> visibleSet)
    {
        var isRoot = string.Equals(node.DisplayRole, "RootWebArea", StringComparison.OrdinalIgnoreCase)
            || string.Equals(node.DisplayRole, "root", StringComparison.OrdinalIgnoreCase);
        var selfVisible = isRoot
            || (node.BackendNodeId > 0 && visibleSet.Contains(FrameNodeKey.For(node.FrameId, node.BackendNodeId)));

        var keptChildren = new List<AstNode>();
        foreach (var child in node.Children)
        {
            var keptChild = FilterViewportNode(child, visibleSet);
            if (keptChild is not null) keptChildren.Add(keptChild);
        }

        // Text leaves in AXTree often omit backendDOMNodeId; retain them when their parent element is in viewport.
        if (!selfVisible && keptChildren.Count == 0 && node.BackendNodeId > 0)
        {
            return null;
        }

        node.Children = keptChildren;
        return node;
    }

    private static string? ResolveVerifiedLocator(
        AstNode node,
        FrameNodeKey nodeKey,
        Dictionary<(string Role, string Name), HashSet<FrameNodeKey>> roleNameOccurrences,
        BrowserSnapshotOptions options)
    {
        // Never accept or emit "unstable" or "ambiguous" locator sentinels.
        if (IsInvalidLocatorSentinel(node.AdvertisedLocator))
        {
            // Explicitly ignored.
        }
        else if (!string.IsNullOrWhiteSpace(node.AdvertisedLocator))
        {
            var normalizedAdvertised = NormalizeLocatorPrefix(node.AdvertisedLocator!);
            if (normalizedAdvertised.StartsWith("loc=css:", StringComparison.Ordinal))
            {
                if (options.VerifiedCssLocators is not null
                    && options.VerifiedCssLocators.TryGetValue(nodeKey, out var verifiedAdv)
                    && verifiedAdv.BackendNodeId == node.BackendNodeId
                    && string.Equals(NormalizeLocatorPrefix(verifiedAdv.Locator), normalizedAdvertised, StringComparison.Ordinal)
                    && IsElementCompatibleWithRole(node.DisplayRole, verifiedAdv.TagName, verifiedAdv.InputType, verifiedAdv.ExplicitRole))
                {
                    return normalizedAdvertised;
                }
            }
        }

        // Check DOM tag/type mismatch if node itself or verified DOM metadata reports an incompatible element.
        if (!string.IsNullOrEmpty(node.DomTagName)
            && !IsElementCompatibleWithRole(node.DisplayRole, node.DomTagName, null, null))
        {
            return null;
        }

        if (options.VerifiedCssLocators is not null
            && options.VerifiedCssLocators.TryGetValue(nodeKey, out var domInfo))
        {
            if (domInfo.BackendNodeId != node.BackendNodeId
                || !IsElementCompatibleWithRole(node.DisplayRole, domInfo.TagName, domInfo.InputType, domInfo.ExplicitRole))
            {
                return null;
            }
        }

        // 1. Prefer unique `loc=role:<role>[name="..."]` when role + accessible name uniquely identify
        //    this exact (FrameId, BackendNodeId) across all frames (including ignored/hidden copies!).
        var normalizedName = NormalizeRoleNameForKey(node.RawName);
        if (!string.IsNullOrEmpty(normalizedName)
            && normalizedName.Length <= 120
            && !string.Equals(node.DisplayRole, "iframe", StringComparison.OrdinalIgnoreCase))
        {
            var roleKey = (node.DisplayRole.ToLowerInvariant(), normalizedName);
            var blockedByDomDuplicate = options.InvalidRoleNamePairs is not null
                && options.InvalidRoleNamePairs.Contains(roleKey);
            if (!blockedByDomDuplicate
                && roleNameOccurrences.TryGetValue(roleKey, out var matches)
                && matches.Count == 1
                && matches.Contains(nodeKey))
            {
                var escapedForLoc = FormatRoleLocatorName(normalizedName);
                return $"loc=role:{node.DisplayRole.ToLowerInvariant()}[name={escapedForLoc}]";
            }
        }

        // 2. Fallback to verified unique `loc=css:...` when proven unique across all frames to this exact backendNodeId.
        if (options.VerifiedCssLocators is not null
            && options.VerifiedCssLocators.TryGetValue(nodeKey, out var verifiedCss)
            && verifiedCss.BackendNodeId == node.BackendNodeId
            && !IsInvalidLocatorSentinel(verifiedCss.Locator)
            && !string.IsNullOrWhiteSpace(verifiedCss.Locator)
            && IsElementCompatibleWithRole(node.DisplayRole, verifiedCss.TagName, verifiedCss.InputType, verifiedCss.ExplicitRole))
        {
            return NormalizeLocatorPrefix(verifiedCss.Locator);
        }

        return null;
    }

    internal static bool IsInvalidLocatorSentinel(string? locator)
    {
        if (string.IsNullOrWhiteSpace(locator)) return true;
        var trimmed = locator.Trim();
        return string.Equals(trimmed, "unstable", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "ambiguous", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "loc=unstable", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "loc=ambiguous", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsElementCompatibleWithRole(
        string axRole,
        string? tagName,
        string? inputType,
        string? explicitRole)
    {
        if (string.IsNullOrWhiteSpace(tagName) && string.IsNullOrWhiteSpace(explicitRole)) return true;
        var role = axRole.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(explicitRole))
        {
            return string.Equals(CanonicalDisplayRole(explicitRole, false), role, StringComparison.OrdinalIgnoreCase);
        }

        var tag = tagName?.Trim().ToLowerInvariant() ?? string.Empty;
        var type = inputType?.Trim().ToLowerInvariant() ?? string.Empty;
        return role switch
        {
            "button" => tag is "button" or "summary" || (tag == "input" && type is "button" or "submit" or "reset" or "image"),
            "link" => tag is "a" or "area",
            "textbox" or "searchbox" => tag is "textarea"
                || (tag == "input" && type is "" or "text" or "search" or "email" or "url" or "tel" or "password" or "number"),
            "checkbox" or "switch" => tag == "input" && type == "checkbox",
            "radio" => tag == "input" && type == "radio",
            "combobox" or "listbox" => tag is "select" or "input",
            "option" => tag == "option",
            "slider" => tag == "input" && type == "range",
            "spinbutton" => tag == "input" && type == "number",
            "iframe" => tag is "iframe" or "frame",
            _ => true,
        };
    }

    private static string NormalizeLocatorPrefix(string locator)
    {
        var trimmed = locator.Trim();
        return trimmed.StartsWith("loc=", StringComparison.Ordinal) ? trimmed : "loc=" + trimmed;
    }

    private static string FormatRoleLocatorName(string name)
    {
        var escaped = name.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    private static string CanonicalDisplayRole(string rawRole, bool isChildFrameRoot)
    {
        if (string.IsNullOrWhiteSpace(rawRole)) return string.Empty;
        if (isChildFrameRoot
            && (string.Equals(rawRole, "RootWebArea", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rawRole, "WebArea", StringComparison.OrdinalIgnoreCase)))
        {
            return "root";
        }

        return rawRole switch
        {
            var r when string.Equals(r, "SVGRoot", StringComparison.OrdinalIgnoreCase) => "svg_root",
            var r when string.Equals(r, "Iframe", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(r, "IframePresentational", StringComparison.OrdinalIgnoreCase) => "iframe",
            var r when string.Equals(r, "Image", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(r, "img", StringComparison.OrdinalIgnoreCase) => "image",
            var r when string.Equals(r, "StaticText", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(r, "InlineTextBox", StringComparison.OrdinalIgnoreCase) => "text",
            _ => rawRole,
        };
    }

    internal static bool IsBlankOrZeroWidth(string? value)
    {
        if (string.IsNullOrEmpty(value)) return true;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch)) continue;
            if (ch is '\u00A0' or '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF' or '\u00AD') continue;
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Format) continue;
            return false;
        }
        return true;
    }

    internal static string NormalizeRoleNameForKey(string? value)
    {
        if (IsBlankOrZeroWidth(value)) return string.Empty;
        var builder = new StringBuilder(value!.Length);
        var prevSpace = false;
        foreach (var ch in value)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Format
                && ch is '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF' or '\u00AD')
            {
                continue;
            }
            if (char.IsWhiteSpace(ch) || ch == '\u00A0')
            {
                if (prevSpace || builder.Length == 0) continue;
                prevSpace = true;
                builder.Append(' ');
            }
            else
            {
                prevSpace = false;
                builder.Append(ch);
            }
        }
        while (builder.Length > 0 && builder[^1] == ' ') builder.Length--;
        return builder.ToString();
    }

    private static void AppendStateProperties(JsonElement node, StringBuilder line)
    {
        if (!node.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Array) return;
        foreach (var property in properties.EnumerateArray())
        {
            if (property.ValueKind != JsonValueKind.Object) continue;
            var name = StringValue(property, "name");
            if (string.IsNullOrEmpty(name)) continue;
            if (!property.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object) continue;
            if (!value.TryGetProperty("value", out var actual)) continue;

            switch (name.ToLowerInvariant())
            {
                case "disabled" when actual.ValueKind == JsonValueKind.True:
                    line.Append(" disabled");
                    break;
                case "checked":
                    AppendChecked(line, actual);
                    break;
                case "expanded" when actual.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    line.Append(actual.GetBoolean() ? " expanded" : " collapsed");
                    break;
                case "selected" when actual.ValueKind == JsonValueKind.True:
                    line.Append(" selected");
                    break;
                case "required" when actual.ValueKind == JsonValueKind.True:
                    line.Append(" required");
                    break;
                case "readonly" when actual.ValueKind == JsonValueKind.True:
                    line.Append(" readonly");
                    break;
                case "pressed":
                    AppendPressed(line, actual);
                    break;
                case "invalid" when actual.ValueKind == JsonValueKind.True:
                    line.Append(" invalid");
                    break;
                case "invalid" when actual.ValueKind == JsonValueKind.String
                                   && !string.Equals(actual.GetString(), "false", StringComparison.OrdinalIgnoreCase):
                    line.Append(" invalid");
                    break;
            }
        }
    }

    private static void AppendChecked(StringBuilder line, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.True) line.Append(" checked");
        else if (value.ValueKind == JsonValueKind.False) line.Append(" unchecked");
        else if (value.ValueKind == JsonValueKind.String
                 && string.Equals(value.GetString(), "mixed", StringComparison.OrdinalIgnoreCase)) line.Append(" mixed");
    }

    private static void AppendPressed(StringBuilder line, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.True) line.Append(" pressed");
        else if (value.ValueKind == JsonValueKind.String
                 && string.Equals(value.GetString(), "mixed", StringComparison.OrdinalIgnoreCase)) line.Append(" mixed");
    }

    private static string RoleOf(JsonElement node) => AxValue(node, "role") ?? string.Empty;

    private static string NameOf(JsonElement node) => AxValue(node, "name") ?? string.Empty;

    private static string? RawValueOf(JsonElement node)
    {
        if (!node.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object) return null;
        if (!value.TryGetProperty("value", out var actual)) return null;
        return actual.ValueKind switch
        {
            JsonValueKind.String => actual.GetString(),
            JsonValueKind.Number => actual.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static string? AxValue(JsonElement node, string property)
    {
        if (!node.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty("value", out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static string? StringValue(JsonElement node, string property)
    {
        if (!node.TryGetProperty(property, out var element)) return null;
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        var previousSpace = false;
        foreach (var character in value)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format
                && character is '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF' or '\u00AD')
            {
                continue;
            }
            var current = character is '\r' or '\n' or '\t' or '\u00A0' ? ' ' : character;
            if (current == ' ')
            {
                if (previousSpace || builder.Length == 0) continue;
                previousSpace = true;
            }
            else
            {
                previousSpace = false;
            }
            builder.Append(current);
        }
        while (builder.Length > 0 && builder[^1] == ' ') builder.Length--;
        var collapsed = builder.ToString();
        if (collapsed.Length > 160) collapsed = collapsed[..157] + "...";
        return collapsed.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
