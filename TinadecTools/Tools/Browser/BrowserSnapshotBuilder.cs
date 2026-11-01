using System.Text;
using System.Text.Json;

namespace TinadecTools.Tools.Browser;

internal sealed record BrowserRef(int Ref, int BackendNodeId, string Role, string Name);

internal sealed class SnapshotBuildResult
{
    public string Text { get; init; } = string.Empty;
    public bool Truncated { get; init; }
    public int RefCount { get; init; }
    public Dictionary<int, BrowserRef> Refs { get; init; } = new();
    public int NextRef { get; init; } = 1;
}

/// <summary>
/// Turns Chromium's accessibility tree into the compact text view the model reads.
///
/// The rules borrowed from ego-lite are the load-bearing ones: a text tree with inline
/// `[ref=N]`, stable ref ids for the same backend node, and a hard character budget. Ref ids are
/// only ever handed out to interactive nodes; a node that disappears loses its entry, and the
/// runtime invalidates the whole map when the document loader id changes.
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
        "spinbutton", "treeitem", "gridcell", "columnheader", "rowheader",
    };

    private static readonly HashSet<string> AnonymousRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "generic", "genericcontainer", "none", "presentation",
    };

    private static readonly HashSet<string> ValueRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "textbox", "searchbox", "combobox", "listbox", "slider", "spinbutton",
    };

    private static readonly HashSet<string> TextRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "statictext", "inlinetextbox", "paragraph",
    };

    public static SnapshotBuildResult Build(
        JsonElement axResult,
        IReadOnlyDictionary<int, BrowserRef> previousRefs,
        int nextRef,
        int maxChars)
    {
        maxChars = Math.Clamp(maxChars, MinimumMaxChars, MaximumMaxChars);
        if (!axResult.TryGetProperty("nodes", out var nodesElement) || nodesElement.ValueKind != JsonValueKind.Array)
        {
            return new SnapshotBuildResult { Text = "(no accessibility nodes)", NextRef = nextRef };
        }

        var byId = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var childIds = new HashSet<string>(StringComparer.Ordinal);
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
        }

        if (byId.Count == 0) return new SnapshotBuildResult { Text = "(empty accessibility tree)", NextRef = nextRef };

        var roots = new List<string>();
        foreach (var id in byId.Keys)
        {
            if (!childIds.Contains(id)) roots.Add(id);
        }
        if (roots.Count == 0)
        {
            // A cycle or a malformed tree: start at the first node so the model still sees something.
            roots.Add(byId.Keys.First());
        }
        else
        {
            var rootWebArea = roots.FirstOrDefault(id => string.Equals(RoleOf(byId[id]), "RootWebArea", StringComparison.OrdinalIgnoreCase));
            if (rootWebArea is not null)
            {
                roots.Remove(rootWebArea);
                roots.Insert(0, rootWebArea);
            }
        }

        var text = new StringBuilder();
        var newRefs = new Dictionary<int, BrowserRef>();
        var currentNext = nextRef;
        var truncated = false;

        bool Append(string line)
        {
            if (text.Length + line.Length + 1 > maxChars)
            {
                truncated = true;
                return false;
            }
            text.Append(line).Append('\n');
            return true;
        }

        void Render(string id, int depth, HashSet<string> visited)
        {
            if (truncated || depth > 80 || visited.Count > 20_000) return;
            if (!byId.TryGetValue(id, out var node)) return;
            if (!visited.Add(id)) return;

            var ignored = node.TryGetProperty("ignored", out var ignoredElement)
                && ignoredElement.ValueKind == JsonValueKind.True;
            var role = RoleOf(node);
            var name = NameOf(node);
            if (ignored)
            {
                RenderChildren(node, depth, visited);
                visited.Remove(id);
                return;
            }
            if (IsAnonymous(role, name))
            {
                RenderChildren(node, depth, visited);
                visited.Remove(id);
                return;
            }
            if (TextRoles.Contains(role) && string.IsNullOrEmpty(name))
            {
                name = RawValueOf(node);
            }
            if (string.IsNullOrEmpty(role)) role = "node";

            var line = new StringBuilder();
            line.Append(' ', depth * 2);
            line.Append(role);
            var escapedName = Escape(name);
            if (escapedName.Length > 0) line.Append(" \"").Append(escapedName).Append('"');

            int refId = 0;
            if (ActionableRoles.Contains(role)
                && node.TryGetProperty("backendDOMNodeId", out var backendElement)
                && backendElement.TryGetInt32(out var backendId)
                && backendId > 0)
            {
                if (previousRefs.TryGetValue(backendId, out var existing))
                {
                    refId = existing.Ref;
                }
                else
                {
                    refId = currentNext++;
                }
                newRefs[backendId] = new BrowserRef(refId, backendId, role, name ?? string.Empty);
                line.Append(" [ref=").Append(refId).Append(']');
            }

            AppendStateProperties(node, line);
            if (ValueRoles.Contains(role))
            {
                var value = RawValueOf(node);
                if (!string.IsNullOrEmpty(value))
                {
                    line.Append(" value=\"").Append(Escape(value)).Append('"');
                }
            }

            if (!Append(line.ToString()))
            {
                visited.Remove(id);
                return;
            }
            RenderChildren(node, depth + 1, visited);
            visited.Remove(id);
        }

        void RenderChildren(JsonElement node, int depth, HashSet<string> visited)
        {
            if (!node.TryGetProperty("childIds", out var children) || children.ValueKind != JsonValueKind.Array) return;
            foreach (var child in children.EnumerateArray())
            {
                if (truncated) return;
                if (child.ValueKind == JsonValueKind.String && child.GetString() is { Length: > 0 } childId)
                {
                    Render(childId, depth, visited);
                }
            }
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            if (truncated) break;
            Render(root, 0, visited);
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
        };
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

    private static bool IsAnonymous(string role, string name)
    {
        if (name.Length > 0) return false;
        return string.IsNullOrEmpty(role) || AnonymousRoles.Contains(role);
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
            var current = character is '\r' or '\n' or '\t' ? ' ' : character;
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
        var collapsed = builder.ToString();
        if (collapsed.Length > 160) collapsed = collapsed[..157] + "...";
        return collapsed.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
