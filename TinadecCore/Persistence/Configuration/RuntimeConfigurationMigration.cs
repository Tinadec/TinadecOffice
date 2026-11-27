using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TinadecCore.Abstractions.Ports;
using Tomlyn;
using Tomlyn.Model;

namespace TinadecCore.Persistence;

/// <summary>
/// Brings an established <c>runtime.toml</c> up to the policy baseline this build ships.
///
/// <para>
/// Why it exists: <see cref="ScopeConfigurationInitialization.Ensure"/> seeds a document only
/// when it does not exist yet, so every storage scope created before a baseline change keeps
/// the old numbers forever — including the context budgets that used to cut a normal long task
/// off. New scopes get the new file, old scopes silently keep the old limits.
/// </para>
///
/// <para>Contract (all three rules are load-bearing):</para>
/// <list type="bullet">
/// <item>Only a value that is STILL the previous shipped default is rewritten. A number the
/// operator changed is never touched, so "upgrade my baseline" can never overwrite tuning.</item>
/// <item>The decision is per key, not per file: a document that customized one budget still
/// receives the other upgrades.</item>
/// <item>The rewrite is textual — the matching assignment keeps its line, indentation and
/// trailing comment — instead of re-serializing the parsed model, which would erase the
/// commentary the shipped baseline is written in.</item>
/// </list>
///
/// <para>
/// A state marker records that this revision was evaluated for the scope, so restoring an old
/// value on purpose after the upgrade ran does not get "fixed" again on the next start.
/// </para>
/// </summary>
public static class RuntimeConfigurationMigration
{
    /// <summary>Applied-revision marker name. Bump it together with <see cref="Upgrades"/>.</summary>
    public const string Revision = "context-budget-2026-10";

    /// <summary>One key this revision can move, with the previous shipped default it replaces.</summary>
    /// <param name="Table">TOML table the key lives in.</param>
    /// <param name="Key">Key name inside that table.</param>
    /// <param name="Previous">
    /// The old shipped value. <c>null</c> means "the key did not exist in the old baseline";
    /// such an upgrade only applies while the document is still on the old context window,
    /// because a document that already retuned its budgets must not silently gain a new key.
    /// </param>
    /// <param name="Current">The value this build ships.</param>
    private sealed record Upgrade(string Table, string Key, long? Previous, long Current);

    private static readonly Upgrade[] Upgrades =
    [
        // 65536 was the context window before the 2026-10 baseline; it is a window, not a
        // spend cap, so the shipped value is the larger one.
        new("context", "default_token_budget", 65_536, 131_072),
        // The run-wide fuse used to be 1 MiB. It is a fuse for unattended runs, not a budget;
        // it ships disabled so normal long investigations are stopped by the loop guard instead.
        new("context", "run_token_budget", 1_048_576, 0),
        // The key did not exist in the old baseline: absent meant "effective per-task fuse =
        // default_token_budget", which the larger window would silently tighten.
        new("context", "task_token_budget", null, 0),
    ];

    /// <summary>Same-line assignment: keeps the identity prefix and any trailing comment.</summary>
    private static readonly Regex Assignment = new(
        @"^(?<head>\s*(?<key>[A-Za-z0-9_\-]+)\s*=\s*)(?<value>[^#]*?)(?<tail>\s*#.*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Any table header — the boundary of the table a key lives in.</summary>
    private static readonly Regex Header = new(
        @"^\s*\[\[?[A-Za-z0-9_.\-""'\s]+\]\]?\s*(#.*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Evaluates and, when needed, rewrites this scope's <c>runtime.toml</c>.
    /// Returns <c>true</c> only when the file content actually changed.
    /// </summary>
    public static bool Apply(IScopeStorageLocations scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var path = Path.Combine(scope.Config, "runtime.toml");
        var marker = Path.Combine(scope.State, ".configuration-migrations", Revision + ".applied");
        StorageScopePaths.RejectLinks(scope.Root, path);
        StorageScopePaths.RejectLinks(scope.Root, marker);
        // Nothing to upgrade before the document exists: seeding already writes the new baseline.
        // The marker short-circuits every later start, including one after a deliberate revert.
        if (!File.Exists(path) || File.Exists(marker)) return false;
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            var changed = TryUpgrade(text, out var upgraded);
            if (changed) WriteAtomically(path, upgraded);
            WriteMarker(marker);
            return changed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TomlException)
        {
            // Booting must not depend on this maintenance write: a read-only or momentarily
            // locked document keeps the numbers it has (the runtime policy surface reports the
            // effective values), and leaving the marker absent makes the next start retry.
            return false;
        }
    }

    /// <summary>Pure half of <see cref="Apply"/>: decides what to change and produces the new text.</summary>
    internal static bool TryUpgrade(string text, out string upgraded)
    {
        ArgumentNullException.ThrowIfNull(text);
        upgraded = text;
        var model = TomlSerializer.Deserialize<TomlTable>(text);
        if (model is null) return false;
        var applies = new List<Upgrade>();
        foreach (var upgrade in Upgrades)
        {
            if (Table(model, upgrade.Table) is not { } table) continue;
            var present = table.TryGetValue(upgrade.Key, out var value);
            if (upgrade.Previous is { } previous)
            {
                if (present && value is long number && number == previous) applies.Add(upgrade);
                continue;
            }
            // Absent-key upgrade: only while the table still carries the old context window.
            if (!present && table.TryGetValue("default_token_budget", out var window) && window is long oldWindow && oldWindow == 65_536)
                applies.Add(upgrade);
        }
        if (applies.Count == 0) return false;
        upgraded = Rewrite(text, applies);
        return true;
    }

    private static string Rewrite(string text, IReadOnlyList<Upgrade> upgrades)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var trailingNewline = text.EndsWith('\n');
        var lines = new List<string>(text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
        if (trailingNewline) lines.RemoveAt(lines.Count - 1);
        foreach (var upgrade in upgrades) ApplyToLines(lines, upgrade);
        var result = string.Join(newline, lines);
        return trailingNewline ? result + newline : result;
    }

    private static void ApplyToLines(List<string> lines, Upgrade upgrade)
    {
        var (start, end) = Section(lines, upgrade.Table);
        if (start < 0) return;
        for (var index = start + 1; index < end; index++)
        {
            var match = Assignment.Match(lines[index]);
            if (!match.Success || !string.Equals(match.Groups["key"].Value, upgrade.Key, StringComparison.Ordinal)) continue;
            lines[index] = match.Groups["head"].Value + upgrade.Current.ToString(CultureInfo.InvariantCulture) + match.Groups["tail"].Value;
            return;
        }
        if (upgrade.Previous is not null) return;
        // The key is absent by construction; append it after the table's last assignment so the
        // document keeps its grouping instead of gaining a line somewhere in the middle.
        var insertAfter = start;
        for (var index = start + 1; index < end; index++)
            if (Assignment.IsMatch(lines[index])) insertAfter = index;
        var indent = insertAfter == start ? string.Empty : LeadingWhitespace(lines[insertAfter]);
        lines.Insert(insertAfter + 1, indent + upgrade.Key + " = " + upgrade.Current.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Bounds of one table: the header line plus everything up to the next header.</summary>
    private static (int Start, int End) Section(List<string> lines, string table)
    {
        var header = "[" + table + "]";
        var start = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            if (!string.Equals(lines[index].Split('#')[0].Trim(), header, StringComparison.Ordinal)) continue;
            start = index;
            break;
        }
        if (start < 0) return (-1, -1);
        for (var index = start + 1; index < lines.Count; index++)
            if (Header.IsMatch(lines[index])) return (start, index);
        return (start, lines.Count);
    }

    private static string LeadingWhitespace(string line)
    {
        var length = 0;
        while (length < line.Length && char.IsWhiteSpace(line[length])) length++;
        return line[..length];
    }

    private static TomlTable? Table(TomlTable model, string name) =>
        model.TryGetValue(name, out var value) ? value as TomlTable : null;

    private static void WriteAtomically(string path, string text)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.None))
            {
                output.Write(Encoding.UTF8.GetBytes(text));
                output.Flush();
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void WriteMarker(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.None);
            output.Write(Encoding.UTF8.GetBytes(Revision + "\n"));
        }
        catch (IOException) when (File.Exists(path)) { }
    }
}
