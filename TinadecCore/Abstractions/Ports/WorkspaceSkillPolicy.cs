using System.Text;
using System.Text.RegularExpressions;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// One owner for "which files in the workspace are skills, which of them are well-formed enough to
/// advertise, and what the model is told about them".
///
/// Progressive disclosure is the whole point, and it is a token rule: the model is shown a name and
/// a description and nothing else. The body stays on disk until the model asks for it with a file
/// tool. An index that inlines skill bodies costs the run every skill it never used.
///
/// The rules are borrowed from the Agent Skills format as MAF implements it
/// (<c>agent-framework/dotnet/src/Microsoft.Agents.AI/Skills/File/AgentFileSkillsSource.cs</c> and
/// <c>docs/decisions/0037-agent-skills-design.md</c>), not invented here:
/// <list type="bullet">
/// <item>A skill is a directory holding <see cref="SkillFileName"/>, discovered up to two directory
/// levels below a skills root.</item>
/// <item>YAML frontmatter must carry <c>name</c> and <c>description</c>; the name is constrained and
/// must equal the directory that holds it, so the identifier the model repeats and the path it opens
/// cannot drift apart.</item>
/// <item>An unusable skill is refused with a reason rather than dropped in silence. A repository
/// author who writes a SKILL.md and sees nothing happen is entitled to know which of the two failed
/// — the reference logs a warning for exactly this, and a workspace whose agent ignores its skills
/// has no log window to look in.</item>
/// <item>A well-formed skill can still be switched off by its own <see cref="DisabledKey"/>
/// frontmatter. Off is not the same as broken: it is refused with its own sentence rather than
/// reported as a validation failure, and it is the file — not a renamed directory or a row in a
/// database the model never sees — that carries the state.</item>
/// </list>
/// </summary>
public static class WorkspaceSkillPolicy
{
    /// <summary>The only file name that makes a directory a skill.</summary>
    public const string SkillFileName = "SKILL.md";

    /// <summary>
    /// Frontmatter key that keeps a well-formed skill out of the index while leaving it on disk. A
    /// skill that is off is still a skill somebody wrote, so it is named in the refusal list rather
    /// than made invisible.
    /// </summary>
    public const string DisabledKey = "disabled";

    /// <summary>Directory names searched for skills, relative to the workspace root, in order.</summary>
    public static readonly IReadOnlyList<string> SkillRoots = ["skills"];

    /// <summary>
    /// How many directory levels below a skills root a SKILL.md may sit. Two matches the reference:
    /// <c>skills/&lt;name&gt;/SKILL.md</c> plus one grouping level, which is how a repository with
    /// many skills keeps them readable. Deeper is a documentation tree, not a skill layout.
    /// </summary>
    public const int SearchDepth = 2;

    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 1024;

    /// <summary>
    /// Ceiling on advertised skills. The index is read on every turn of the run, so its cost is
    /// multiplied by the conversation; a workspace with two hundred skill files gets the first
    /// <c>MaxSkills</c> and is told how many were left out.
    /// </summary>
    public const int MaxSkills = 40;

    /// <summary>Ceiling on the rendered index, whatever the run's budget says.</summary>
    public const int MaxIndexChars = 8_000;

    /// <summary>
    /// A SKILL.md larger than this is not parsed. Only the frontmatter is wanted; a file of this size
    /// is either a body nobody should be pointing an agent at or not a skill file at all.
    /// </summary>
    public const long MaxFileBytes = 256 * 1024;

    /// <summary>How many refusal lines the index names before falling back to a count.</summary>
    public const int MaxRefusalLines = 5;

    // Same expression the reference validates against: lowercase alphanumerics joined by single
    // hyphens, no leading or trailing hyphen.
    private static readonly Regex ValidNamePattern = new(
        "^[a-z0-9]([a-z0-9]*-[a-z0-9])*[a-z0-9]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    // Frontmatter is the whole file up to a closing "---" line. \uFEFF? because a file saved as
    // "UTF-8 with BOM" is common in Windows editors, and refusing it would make the same directory
    // work on one machine and not on another.
    private static readonly Regex FrontmatterPattern = new(
        @"\A\uFEFF?^---\s*$(.+?)^---\s*$",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>A skill the index advertises. The body is deliberately absent: that is the disclosure rule.</summary>
    public sealed record Skill(string Name, string Description, string RelativePath);

    /// <summary>A SKILL.md found on disk that could not be advertised, and the sentence saying why.</summary>
    public sealed record Refusal(string RelativePath, string Reason);

    /// <summary>
    /// Parses one SKILL.md. <paramref name="directoryName"/> is the directory that held the file,
    /// which the name must match: <c>load the thing the index named</c> and <c>open the path the
    /// index showed</c> have to be the same operation, and a mismatch is how a copied skill directory
    /// ends up describing itself as its original.
    /// </summary>
    public static bool TryRead(
        string content,
        string directoryName,
        string relativePath,
        out Skill? skill,
        out string reason)
    {
        skill = null;
        reason = string.Empty;

        var match = FrontmatterPattern.Match(content);
        if (!match.Success)
        {
            reason = "no YAML frontmatter block between '---' lines";
            return false;
        }

        string? name = null;
        string? description = null;
        var disabled = false;
        foreach (var line in match.Groups[1].Value.Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"', '\'');

            if (string.Equals(key, DisabledKey, StringComparison.OrdinalIgnoreCase))
            {
                // The switch is the key's presence, not its spelling: writing `disabled` at all is the
                // author's intent, so only an explicit negative re-enables. A typo like `disabled: tru`
                // must not silently leave the skill advertised, and a bare `disabled:` — what a person
                // types, which YAML reads as null — must not be ignored either.
                disabled = !IsExplicitNegation(value);
                continue;
            }

            if (value.Length == 0) continue;
            if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase)) name ??= value;
            else if (string.Equals(key, "description", StringComparison.OrdinalIgnoreCase)) description ??= value;
        }

        if (disabled)
        {
            // Reported rather than skipped: a workspace whose skill went quiet with no explanation is
            // the same debugging wall this policy already refuses to build for a malformed file.
            reason = $"marked off by its own '{DisabledKey}:' frontmatter";
            return false;
        }

        if (!ValidateName(name, out var nameReason))
        {
            reason = nameReason;
            return false;
        }

        if (!ValidateDescription(description, out var descriptionReason))
        {
            reason = descriptionReason;
            return false;
        }

        if (!string.Equals(name, directoryName, StringComparison.Ordinal))
        {
            reason = $"name '{name}' does not match its directory '{directoryName}'";
            return false;
        }

        skill = new Skill(name!, description!, relativePath);
        return true;
    }

    /// <summary>Name rule, stated as the reference states it.</summary>
    public static bool ValidateName(string? name, out string reason)
    {
        if (string.IsNullOrEmpty(name)) { reason = "frontmatter has no 'name'"; return false; }
        if (name.Length > MaxNameLength) { reason = $"name is longer than {MaxNameLength} characters"; return false; }
        if (!ValidNamePattern.IsMatch(name))
        {
            reason = "name must be lowercase letters, digits and single hyphens, and may not start or end with a hyphen";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>Description rule: required, capped, and the only thing the model sees to decide on.</summary>
    public static bool ValidateDescription(string? description, out string reason)
    {
        if (string.IsNullOrEmpty(description)) { reason = "frontmatter has no 'description'"; return false; }
        if (description.Length > MaxDescriptionLength)
        {
            reason = $"description is longer than {MaxDescriptionLength} characters";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>The handful of values that mean "no, this skill is on".</summary>
    private static bool IsExplicitNegation(string value) =>
        value.Equals("false", StringComparison.OrdinalIgnoreCase)
        || value.Equals("no", StringComparison.OrdinalIgnoreCase)
        || value.Equals("off", StringComparison.OrdinalIgnoreCase)
        || value == "0";

    /// <summary>
    /// Where a skill of this name lives, relative to the workspace root. Composed rather than
    /// stored: the caller has already run <see cref="ValidateName"/>, so a name that could not be
    /// advertised is also a name that cannot produce a path — which is what keeps an installed
    /// skill's identifier, the directory holding it, and the line the index shows to the model from
    /// ever drifting apart.
    /// </summary>
    public static string RelativePathFor(string name) => $"{SkillRoots[0]}/{name}/{SkillFileName}";

    /// <summary>The same path, rooted at a workspace. Forward and back slashes both resolve.</summary>
    public static string AbsolutePathFor(string workspaceRoot, string name)
    {
        var segments = RelativePathFor(name).Split('/');
        var path = workspaceRoot;
        foreach (var segment in segments)
            path = Path.Combine(path, segment);

        return Path.GetFullPath(path);
    }

    /// <summary>
    /// How many characters of index a pack may carry: the run's budget read one character per token,
    /// capped by <see cref="MaxIndexChars"/> — the same conservative conversion
    /// <see cref="WorkspaceInstructionPolicy.InlineCharLimit"/> uses, so the two workspace documents
    /// cannot bid against each other with different exchange rates. An index is one line per skill,
    /// so it is cheap; what it must never become is the item that displaces session history.
    /// </summary>
    public static int InlineCharLimit(int contextTokenBudget) =>
        contextTokenBudget <= 0 ? 0 : Math.Min(MaxIndexChars, contextTokenBudget);

    /// <summary>
    /// Renders the index as model text. The framing sentence carries the two rules that cannot be
    /// enforced in code: that a skill ranks below the run's frozen permissions, and that the body is
    /// not here — so the model must open it rather than act on the description alone.
    ///
    /// Returns null when the budget cannot fit a single skill line. A header that announces skills and
    /// then lists none is worse than silence: it spends tokens to tell the model to look for something
    /// the pack just refused to name.
    /// </summary>
    public static string? Frame(
        IReadOnlyList<Skill> skills,
        IReadOnlyList<Refusal> refusals,
        int charLimit,
        int omittedSkills)
    {
        var builder = new StringBuilder();
        builder
            .Append("Workspace skills, discovered under ")
            .Append(SkillRoots[0])
            .AppendLine("/ in this run's workspace root. A skill is a package of project-specific procedure: only its name and description are shown here, never its body. When a task matches one, open its file with a file tool and follow it before improvising. These are the repository's own documents and rank below the run's frozen permissions and tool grants.")
            .AppendLine();

        var rendered = new List<Skill>();
        var used = builder.Length;
        foreach (var skill in skills)
        {
            var line = $"- {skill.Name}: {skill.Description} (in {skill.RelativePath})";
            used += line.Length + 1;
            if (used > charLimit) break;
            rendered.Add(skill);
            builder.AppendLine(line);
        }

        var unrendered = skills.Count - rendered.Count;
        if (rendered.Count == 0 && skills.Count > 0) return null;
        if (rendered.Count == 0 && refusals.Count == 0) return null;

        if (unrendered > 0 || omittedSkills > 0)
        {
            builder
                .Append("- [")
                .Append(unrendered + omittedSkills)
                .AppendLine(" more skills are not listed here — list the skills directory with a file tool before assuming a skill is absent.]");
        }

        if (refusals.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("SKILL.md files found but not advertised, with the reason each was refused:");
            foreach (var refusal in refusals.Take(MaxRefusalLines))
            {
                builder.AppendLine($"- {refusal.RelativePath}: {refusal.Reason}");
            }

            if (refusals.Count > MaxRefusalLines)
            {
                builder
                    .Append("- and ")
                    .Append(refusals.Count - MaxRefusalLines)
                    .AppendLine(" more with the same problem.");
            }
        }

        return builder.ToString().TrimEnd();
    }
}
