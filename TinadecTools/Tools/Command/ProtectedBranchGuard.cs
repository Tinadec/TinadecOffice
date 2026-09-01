using System.Text;

namespace TinadecTools.Tools.Command;

internal sealed record ProtectedBranchVerdict(bool Allowed, string? ReasonCode = null, string? Detail = null)
{
    public static readonly ProtectedBranchVerdict Allow = new(true);
}

// Deterministic tool-layer guard against unattended pushes to trunk branches
// (the residual risk accepted in the M5 approval-bypass reversal record).
// It closes the two paths agents have: the structured `git_push` tool and
// `git push` inside `shell` command text. It is a text-level parser — it does
// not stop push via aliases, scripts, or wrapper executables, and it applies
// to agents only; humans push from their own terminal (node-pty), which is
// not governed here. Override the protected set with TINADEC_PROTECTED_BRANCHES
// (comma-separated); unset falls back to main/master.
internal static class ProtectedBranchGuard
{
    private const string EnvVar = "TINADEC_PROTECTED_BRANCHES";
    private const int MaxRecursionDepth = 3;

    private static readonly HashSet<string> Protected = BuildProtected();

    private static HashSet<string> BuildProtected()
    {
        var raw = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(raw))
            return new HashSet<string>(["main", "master"], StringComparer.OrdinalIgnoreCase);
        var values = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return values.Length == 0
            ? new HashSet<string>(["main", "master"], StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsProtected(string? branch)
    {
        var name = NormalizeRef(branch);
        return name is not null && Protected.Contains(name);
    }

    // Verdict for the structured git_push tool, which pushes exactly one branch.
    public static ProtectedBranchVerdict EvaluatePushBranch(string? branch) =>
        IsProtected(branch)
            ? new(false, "protected_branch_push",
                $"Refusing to push protected branch '{NormalizeRef(branch)}'. Agents must push feature branches; a human can push '{NormalizeRef(branch)}' from their own terminal.")
            : ProtectedBranchVerdict.Allow;

    // Quote-aware scan of shell command text for `git push` invocations.
    public static ProtectedBranchVerdict EvaluateShellCommand(string command, int depth = 0)
    {
        foreach (var segment in SplitSegments(command))
        {
            var verdict = EvaluateSegment(segment, depth);
            if (!verdict.Allowed) return verdict;
        }
        return ProtectedBranchVerdict.Allow;
    }

    private static ProtectedBranchVerdict EvaluateSegment(List<string> tokens, int depth)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            var bare = tokens[i].Trim('"', '\'');
            if (bare.Contains(' ') && depth < MaxRecursionDepth)
            {
                // A quoted multi-word token is a nested command (e.g. cmd /c "git push ...").
                var nested = EvaluateShellCommand(bare, depth + 1);
                if (!nested.Allowed) return nested;
            }

            var fileName = Path.GetFileName(bare);
            if (!fileName.Equals("git", StringComparison.OrdinalIgnoreCase)
                && !fileName.StartsWith("git.", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!IsInvocation(tokens, i)) continue;

            var j = i + 1;
            while (j < tokens.Count && tokens[j].StartsWith('-'))
            {
                // Global options that consume a separate value token.
                var option = tokens[j].Trim('"', '\'');
                if (option is "-C" or "-c") j++;
                j++;
            }
            if (j >= tokens.Count) continue;
            if (!tokens[j].Trim('"', '\'').Equals("push", StringComparison.OrdinalIgnoreCase)) continue;

            return EvaluatePushArguments(tokens.Skip(j + 1), depth);
        }
        return ProtectedBranchVerdict.Allow;
    }

    // A `git` token is an invocation only at segment start or when led by
    // launcher words/flags — otherwise `echo git push ...` would false-positive.
    private static bool IsInvocation(List<string> tokens, int gitIndex)
    {
        for (var k = gitIndex - 1; k >= 0; k--)
        {
            var prev = tokens[k].Trim('"', '\'');
            if (prev.StartsWith('-')) continue;
            if (prev is "sudo" or "time" or "nohup" or "exec" or "command" or "env") continue;
            return false;
        }
        return true;
    }

    private static ProtectedBranchVerdict EvaluatePushArguments(IEnumerable<string> args, int depth)
    {
        // Options that consume a separate value token (everything else is boolean or `=`-attached).
        var valueOptions = new HashSet<string>(["-o", "--push-option", "--repo", "--exec", "--receive-pack"], StringComparer.Ordinal);
        var positionals = new List<string>();
        var expectingValue = false;
        foreach (var raw in args)
        {
            if (raw.Trim('"', '\'').Length != raw.Length && depth < MaxRecursionDepth && raw.Contains(' '))
            {
                var nested = EvaluateShellCommand(raw, depth + 1);
                if (!nested.Allowed) return nested;
                continue;
            }
            if (expectingValue) { expectingValue = false; continue; }
            if (raw.StartsWith('-'))
            {
                var option = raw.Trim('"', '\'');
                if (option.Equals("--all", StringComparison.OrdinalIgnoreCase) || option.Equals("--mirror", StringComparison.OrdinalIgnoreCase))
                    return new(false, "push_target_all", "--all/--mirror pushes every branch including protected ones.");
                if (valueOptions.Contains(option)) expectingValue = true;
                continue;
            }
            positionals.Add(raw);
        }

        if (positionals.Count < 2)
            return new(false, "push_target_unknown",
                "Push target is not determined (bare push or repository-only push follows the current branch's upstream). Name an explicit non-protected refspec, e.g. `git push origin <feature-branch>`.");

        foreach (var refspec in positionals.Skip(1))
        {
            var target = NormalizeRef(ExtractTargetRef(refspec));
            if (target is not null && Protected.Contains(target))
                return new(false, "protected_branch_push",
                    $"Refspec '{refspec}' targets protected branch '{target}'. Agents must push feature branches; a human can push '{target}' from their own terminal.");
        }
        return ProtectedBranchVerdict.Allow;
    }

    private static string? ExtractTargetRef(string refspec)
    {
        var spec = refspec.Trim().Trim('"', '\'');
        if (spec.StartsWith('+')) spec = spec[1..];
        var colon = spec.IndexOf(':');
        var target = colon >= 0 ? spec[(colon + 1)..] : spec;
        return target.Length == 0 ? null : target;
    }

    private static string? NormalizeRef(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var name = reference.Trim().Trim('"', '\'');
        if (name.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)) name = name["refs/heads/".Length..];
        if (name.StartsWith("refs/tags/", StringComparison.OrdinalIgnoreCase)) return null;
        if (name.Length == 0) return null;
        return name;
    }

    private static List<List<string>> SplitSegments(string command)
    {
        var segments = new List<List<string>>();
        var tokens = new List<string>();
        var token = new StringBuilder();
        var quote = '\0';

        void FlushToken()
        {
            if (quote == '\0' && token.Length > 0)
            {
                tokens.Add(token.ToString());
                token.Clear();
            }
        }
        void FlushSegment()
        {
            FlushToken();
            if (tokens.Count > 0)
            {
                segments.Add(tokens);
                tokens = new List<string>();
            }
        }

        for (var i = 0; i < command.Length; i++)
        {
            var ch = command[i];
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
                else token.Append(ch);
                continue;
            }
            if (ch is '\'' or '"') { quote = ch; continue; }
            if (ch == '&' && i + 1 < command.Length && command[i + 1] == '&') { FlushSegment(); i++; continue; }
            if (ch == '|' && i + 1 < command.Length && command[i + 1] == '|') { FlushSegment(); i++; continue; }
            if (ch is ';' or '|' or '&' or '\n' or '\r') { FlushSegment(); continue; }
            if (char.IsWhiteSpace(ch)) { FlushToken(); continue; }
            token.Append(ch);
        }
        FlushSegment();
        return segments;
    }
}
