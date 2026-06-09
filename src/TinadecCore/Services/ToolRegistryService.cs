using Tinadec.Contracts.Models;
using TinadecCore.Abstractions;

namespace TinadecCore.Services;

public sealed class CodexCapabilityProvider : ICapabilityProvider
{
    public string Id => "codex-rust";

    private static readonly IReadOnlyList<ToolDescriptorDto> CodexTools =
    [
        new(
            "search_files",
            "Search Files",
            "programming",
            "codex-rust",
            "read-only",
            false,
            "/api/v1/code/tools/search_files/execute",
            ["file.search", "workspace.read", "codex-rust.future"]),
        new(
            "glob_search",
            "Glob Search",
            "programming",
            "codex-rust",
            "read-only",
            false,
            "/api/v1/code/tools/glob_search/execute",
            ["file.glob", "workspace.read", "codex-rust.future"]),
        new(
            "read_file",
            "Read File",
            "programming",
            "codex-rust",
            "read-only",
            false,
            "/api/v1/code/tools/read_file/execute",
            ["file.read", "workspace.read", "codex-rust.active"]),
        new(
            "list_directory",
            "List Directory",
            "programming",
            "codex-rust",
            "read-only",
            false,
            "/api/v1/code/tools/list_directory/execute",
            ["directory.list", "workspace.read", "codex-rust.active"]),
        new(
            "grep_content",
            "Grep Content",
            "programming",
            "codex-rust",
            "read-only",
            false,
            "/api/v1/code/tools/grep_content/execute",
            ["file.grep", "workspace.read", "codex-rust.active"]),
        new(
            "sandbox_exec",
            "Sandbox Exec",
            "programming",
            "codex-rust",
            "shell",
            true,
            "/api/v1/code/tools/sandbox_exec/execute",
            ["shell.approved", "test.run", "codex-rust.future"]),
        new(
            "apply_patch",
            "Apply Patch",
            "programming",
            "codex-rust",
            "workspace-write",
            true,
            "/api/v1/code/tools/apply_patch/execute",
            ["file.write.approved", "patch.apply", "codex-rust.active"]),
        new(
            "review_format",
            "Review Format",
            "programming",
            "codex-rust",
            "read-only",
            false,
            "/api/v1/code/tools/review_format/execute",
            ["review.format", "workspace.read", "codex-rust.active"])
    ];

    public IReadOnlyList<ToolDescriptorDto> ListCapabilities() => CodexTools;
}

public sealed class CodeCapabilityProvider : ICapabilityProvider
{
    public string Id => "code";

    private static readonly string[] RuntimeCapabilities =
    [
        "runtime.nodejs",
        "runtime.bun",
        "runtime.golang",
        "runtime.flutter",
        "runtime.python",
        "runtime.rust",
        "runtime.zig",
        "runtime.nim",
        "runtime.csharp",
        "runtime.java"
    ];

    private static readonly IReadOnlyList<ToolDescriptorDto> CodeTools =
    [
        new(
            "project_templates",
            "Project Templates",
            "programming",
            "code",
            "read-only",
            false,
            "/api/v1/code/tools/project_templates/execute",
            ["project.template", "project.preview", "tool-layer.code-suite", .. RuntimeCapabilities]),
        new(
            "project_template_scaffold",
            "Project Template Scaffold",
            "programming",
            "code",
            "workspace-write",
            true,
            "/api/v1/code/tools/project_template_scaffold/execute",
            ["project.scaffold", "file.write.approved", "tool-layer.code-suite", .. RuntimeCapabilities]),
        new(
            "language_runtime_probe",
            "Language Runtime Probe",
            "programming",
            "code",
            "read-only",
            false,
            "/api/v1/code/tools/language_runtime_probe/execute",
            ["runtime.probe", "tool-layer.code-suite", .. RuntimeCapabilities]),
        new(
            "bash_environment",
            "Bash-like Environment",
            "programming",
            "code",
            "shell",
            true,
            "/api/v1/code/tools/bash_environment/execute",
            ["shell.approved", "process.exec", "env.vars", "tool-layer.code-suite"]),
        new(
            "debug_session",
            "Built-in Debug Session",
            "programming",
            "code",
            "shell",
            true,
            "/api/v1/code/tools/debug_session/execute",
            ["debug.run", "debug.breakpoint", "trace.capture", "tool-layer.code-suite"]),
        new(
            "code_editor",
            "Built-in Code Editor",
            "programming",
            "code",
            "workspace-write",
            true,
            "/api/v1/code/tools/code_editor/execute",
            ["editor.open", "editor.diff", "file.write.approved", "tool-layer.code-suite"]),
        new(
            "git_worktree_manager",
            "Git Worktree Manager",
            "programming",
            "code",
            "git-write",
            true,
            "/api/v1/code/tools/git_worktree_manager/execute",
            ["git.status", "git.diff", "git.stage", "git.unstage", "git.worktree", "git.branch", "git.commit", "git.push", "workspace.isolation", "tool-layer.code-suite"])
    ];

    public IReadOnlyList<ToolDescriptorDto> ListCapabilities() => CodeTools;
}

public sealed class PromptContextCapabilityProvider : ICapabilityProvider
{
    public string Id => "core";

    private static readonly IReadOnlyList<ToolDescriptorDto> PromptContextTools =
    [
        new(
            "prompt_context_resolve",
            "Prompt Context Resolve",
            "agent-context",
            "core",
            "read-only",
            false,
            "/api/v1/prompt-context/preview",
            ["prompt.context.resolve", "prompt.fragment.select", "context_pack.rank"])
    ];

    public IReadOnlyList<ToolDescriptorDto> ListCapabilities() => PromptContextTools;
}

public sealed class ToolRegistryService : IToolRegistry
{
    private readonly IReadOnlyList<ICapabilityProvider> _providers;
    private static readonly string[] SourcePrecedence = ["core", "code", "codex-rust", "extension"];

    public ToolRegistryService()
        : this([new CodexCapabilityProvider(), new CodeCapabilityProvider(), new PromptContextCapabilityProvider()])
    {
    }

    public ToolRegistryService(IEnumerable<ICapabilityProvider> providers)
    {
        _providers = providers.ToArray();
    }

    public IReadOnlyList<ToolDescriptorDto> ListTools(string? domain = null)
    {
        var tools = CanonicalTools(domain);
        if (string.IsNullOrWhiteSpace(domain))
        {
            return tools;
        }

        return tools;
    }

    public ToolDescriptorDto? Resolve(string toolId)
    {
        return ListTools().FirstOrDefault(tool =>
            string.Equals(tool.Id, toolId, StringComparison.OrdinalIgnoreCase));
    }

    public ToolRegistrySummaryDto Describe(string? domain = null)
    {
        var declaredTools = DeclaredTools(domain);
        var duplicateToolIds = declaredTools
            .GroupBy(tool => tool.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ToolRegistrySummaryDto(
            declaredTools.Length,
            CanonicalTools(domain).Length,
            duplicateToolIds.Length,
            duplicateToolIds,
            SourcePrecedence,
            "Core canonicalizes duplicate tool ids by source precedence, then keeps the more approval-gated and higher-risk descriptor for same-precedence declarations.");
    }

    private ToolDescriptorDto[] DeclaredTools(string? domain = null)
    {
        var tools = _providers
            .SelectMany(provider => provider.ListCapabilities())
            .ToArray();

        if (string.IsNullOrWhiteSpace(domain))
        {
            return tools;
        }

        return tools
            .Where(tool => string.Equals(tool.Domain, domain, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private ToolDescriptorDto[] CanonicalTools(string? domain = null)
    {
        return DeclaredTools(domain)
            .GroupBy(tool => tool.Id, StringComparer.OrdinalIgnoreCase)
            .Select(SelectCanonicalTool)
            .ToArray();
    }

    private static ToolDescriptorDto SelectCanonicalTool(IGrouping<string, ToolDescriptorDto> duplicates)
    {
        return duplicates
            .OrderBy(tool => SourceSortKey(tool.Source))
            .ThenByDescending(tool => tool.RequiresApproval)
            .ThenByDescending(tool => RiskSortKey(tool.Risk))
            .ThenBy(tool => tool.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(tool => tool.DisplayName, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static int SourceSortKey(string source)
    {
        return source.ToLowerInvariant() switch
        {
            "core" => 0,
            "code" => 1,
            "codex-rust" => 2,
            _ => 3
        };
    }

    private static int RiskSortKey(string risk)
    {
        return risk.ToLowerInvariant() switch
        {
            "read-only" => 0,
            "workspace-write" => 1,
            "shell" => 2,
            "git-write" => 3,
            "external-url" => 4,
            _ => 5
        };
    }
}
