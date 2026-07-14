namespace TinadecTools.Tools.Git;

internal enum TextConflictResolution { Markers, Ours, Theirs, Both }

internal sealed record TextMergeConflict(int StartLine, int EndLine, string Base, string Ours, string Theirs);
internal sealed record TextMergeResult(string Text, IReadOnlyList<TextMergeConflict> Conflicts)
{
    public bool IsClean => Conflicts.Count == 0;
}

internal static class ThreeWayTextMerge
{
    private const long MaxLcsCells = 4_000_000;
    private sealed record Change(int Start, int End, string[] Replacement);

    public static TextMergeResult Merge(string baseText, string oursText, string theirsText, TextConflictResolution resolution = TextConflictResolution.Markers)
    {
        var baseLines = Lines(baseText);
        var oursLines = Lines(oursText);
        var theirsLines = Lines(theirsText);
        var oursChanges = Changes(baseLines, oursLines);
        var theirsChanges = Changes(baseLines, theirsLines);
        var output = new List<string>();
        var conflicts = new List<TextMergeConflict>();
        var left = 0;
        var right = 0;
        var position = 0;

        while (left < oursChanges.Count || right < theirsChanges.Count)
        {
            var ours = left < oursChanges.Count ? oursChanges[left] : null;
            var theirs = right < theirsChanges.Count ? theirsChanges[right] : null;
            if (ours is not null && (theirs is null || (!Overlaps(ours, theirs) && ours.Start <= theirs.Start)))
            {
                AppendBase(output, baseLines, position, ours.Start);
                output.AddRange(ours.Replacement);
                position = ours.End;
                left++;
                continue;
            }
            if (theirs is not null && (ours is null || !Overlaps(ours, theirs)))
            {
                AppendBase(output, baseLines, position, theirs.Start);
                output.AddRange(theirs.Replacement);
                position = theirs.End;
                right++;
                continue;
            }

            var start = Math.Min(ours!.Start, theirs!.Start);
            var end = Math.Max(ours.End, theirs.End);
            var leftStart = left;
            var rightStart = right;
            left++;
            right++;
            var expanded = true;
            while (expanded)
            {
                expanded = false;
                while (left < oursChanges.Count && Joins(oursChanges[left], start, end)) { end = Math.Max(end, oursChanges[left].End); left++; expanded = true; }
                while (right < theirsChanges.Count && Joins(theirsChanges[right], start, end)) { end = Math.Max(end, theirsChanges[right].End); right++; expanded = true; }
            }
            AppendBase(output, baseLines, position, start);
            var basePart = baseLines[start..end];
            var oursPart = Apply(baseLines, start, end, oursChanges.GetRange(leftStart, left - leftStart));
            var theirsPart = Apply(baseLines, start, end, theirsChanges.GetRange(rightStart, right - rightStart));
            if (oursPart.SequenceEqual(theirsPart)) output.AddRange(oursPart);
            else if (oursPart.SequenceEqual(basePart)) output.AddRange(theirsPart);
            else if (theirsPart.SequenceEqual(basePart)) output.AddRange(oursPart);
            else
            {
                var startLine = output.Count + 1;
                var selected = resolution switch
                {
                    TextConflictResolution.Ours => oursPart,
                    TextConflictResolution.Theirs => theirsPart,
                    TextConflictResolution.Both => [.. oursPart, .. theirsPart],
                    _ => ["<<<<<<< ours", .. oursPart, "||||||| base", .. basePart, "=======", .. theirsPart, ">>>>>>> theirs"]
                };
                output.AddRange(selected);
                conflicts.Add(new TextMergeConflict(startLine, Math.Max(startLine, output.Count), Join(basePart), Join(oursPart), Join(theirsPart)));
            }
            position = end;
        }
        AppendBase(output, baseLines, position, baseLines.Length);
        var trailingNewline = baseText.EndsWith('\n') || oursText.EndsWith('\n') || theirsText.EndsWith('\n');
        var text = Join(output.ToArray()) + (trailingNewline && output.Count > 0 ? "\n" : string.Empty);
        return new TextMergeResult(text, conflicts);
    }

    private static List<Change> Changes(string[] source, string[] target)
    {
        if (source.SequenceEqual(target)) return [];
        if ((long)(source.Length + 1) * (target.Length + 1) > MaxLcsCells) return [new Change(0, source.Length, target)];
        var lcs = new int[source.Length + 1, target.Length + 1];
        for (var i = source.Length - 1; i >= 0; i--)
            for (var j = target.Length - 1; j >= 0; j--)
                lcs[i, j] = source[i] == target[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var result = new List<Change>();
        var si = 0; var ti = 0; var start = -1; var replacement = new List<string>();
        void Flush()
        {
            if (start < 0) return;
            result.Add(new Change(start, si, replacement.ToArray()));
            start = -1;
            replacement = [];
        }
        while (si < source.Length || ti < target.Length)
        {
            if (si < source.Length && ti < target.Length && source[si] == target[ti]) { Flush(); si++; ti++; }
            else if (ti < target.Length && (si == source.Length || lcs[si, ti + 1] >= lcs[si + 1, ti])) { if (start < 0) start = si; replacement.Add(target[ti++]); }
            else { if (start < 0) start = si; si++; }
        }
        Flush();
        return result;
    }

    private static bool Overlaps(Change a, Change b) =>
        a.Start == a.End || b.Start == b.End
            ? (a.Start == b.Start || (a.Start >= b.Start && a.Start <= b.End) || (b.Start >= a.Start && b.Start <= a.End))
            : a.Start < b.End && b.Start < a.End;

    private static bool Joins(Change change, int start, int end) =>
        change.Start == change.End ? change.Start >= start && change.Start <= end : change.Start < end && change.End > start;

    private static string[] Apply(string[] source, int start, int end, List<Change> changes)
    {
        var output = new List<string>(); var position = start;
        foreach (var change in changes) { AppendBase(output, source, position, change.Start); output.AddRange(change.Replacement); position = change.End; }
        AppendBase(output, source, position, end);
        return output.ToArray();
    }

    private static void AppendBase(List<string> output, string[] source, int start, int end) { if (end > start) output.AddRange(source[start..end]); }
    private static string[] Lines(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        return normalized.EndsWith('\n') ? lines[..^1] : lines;
    }
    private static string Join(string[] lines) => string.Join('\n', lines);
}
