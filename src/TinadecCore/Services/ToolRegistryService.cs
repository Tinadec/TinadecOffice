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

    public IReadOnlyList<ToolDescriptorDto> ListCapabilities()
    {
        return CodexTools;
    }
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

    private static readonly IReadOnlyList<ToolDescriptorDto> GitReadTools =
    [
        GitRead("git_status", "Git Status", "git.status", "git.conflict"),
        GitRead("git_log_list", "Git Log List", "git.log.read"),
        GitRead("git_log_detail", "Git Log Detail", "git.log.read", "git.diff"),
        GitRead("git_file_history", "Git File History", "git.history.read"),
        GitRead("git_push_readiness", "Git Push Readiness", "git.push.readiness"),
        GitRead("git_diff", "Git Diff", "git.diff"),
        GitRead("git_branch_list", "Git Branch List", "git.branch.read"),
        GitRead("git_worktree_list", "Git Worktree List", "git.worktree.read"),
        GitRead("git_ref_list", "Git Ref List", "git.ref.read"),
        GitRead("git_remote_list", "Git Remote List", "git.remote.read"),
        GitRead("git_blame", "Git Blame", "git.blame"),
        GitRead("git_file_at_revision", "Git File At Revision", "git.file.revision"),
        GitRead("git_conflict_preview", "Git Conflict Preview", "git.conflict.preview")
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
            [
                "git.status", "git.diff", "git.stage", "git.unstage", "git.worktree", "git.branch", "git.commit",
                "git.push", "workspace.isolation", "tool-layer.code-suite"
            ]),
        GitWrite("git_stage", "Git Stage", "git.stage", "git.index.write"),
        GitWrite("git_unstage", "Git Unstage", "git.unstage", "git.index.write"),
        GitWrite("git_commit", "Git Commit", "git.commit"),
        GitWrite("git_checkout", "Git Checkout", "git.checkout", "git.branch.write"),
        GitWrite("git_branch_create", "Git Branch Create", "git.branch.create", "git.branch.write"),
        GitWrite("git_branch_delete", "Git Branch Delete", "git.branch.delete", "git.branch.write"),
        GitWrite("git_branch_rename", "Git Branch Rename", "git.branch.rename", "git.branch.write"),
        GitWrite("git_worktree_create", "Git Worktree Create", "git.worktree.create", "git.worktree.write"),
        GitWrite("git_worktree_remove", "Git Worktree Remove", "git.worktree.remove", "git.worktree.write"),
        GitWrite("git_fetch", "Git Fetch", "git.fetch", "git.remote.write"),
        GitWrite("git_push", "Git Push", "git.push", "git.remote.write"),
        GitWrite("git_pull", "Git Pull", "git.pull", "git.remote.write"),
        GitWrite("git_merge", "Git Merge", "git.merge"),
        GitWrite("git_rebase", "Git Rebase", "git.rebase", "git.history.rewrite"),
        GitWrite("git_conflict_resolve", "Git Conflict Resolve", "git.conflict.resolve", "git.index.write"),
        .. GitReadTools
    ];

    private static ToolDescriptorDto GitRead(string id, string displayName, params string[] capabilities)
    {
        return new ToolDescriptorDto(
            id,
            displayName,
            "programming",
            "code",
            "git-read",
            true,
            $"/api/v1/code/tools/{id}/execute",
            [.. capabilities, "tool-layer.code-suite"]);
    }

    private static ToolDescriptorDto GitWrite(string id, string displayName, params string[] capabilities)
    {
        return new ToolDescriptorDto(
            id,
            displayName,
            "programming",
            "code",
            "git-write",
            true,
            $"/api/v1/code/tools/{id}/execute",
            [.. capabilities, "tool-layer.code-suite"]);
    }

    public IReadOnlyList<ToolDescriptorDto> ListCapabilities()
    {
        return CodeTools;
    }
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

    public IReadOnlyList<ToolDescriptorDto> ListCapabilities()
    {
        return PromptContextTools;
    }
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
        if (string.IsNullOrWhiteSpace(domain)) return tools;

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

    /// <summary>
    /// Build OpenAI-compatible function-calling tool specs for model invocation.
    /// Converts canonical tool descriptors into the format expected by chat completion APIs.
    /// </summary>
    public IReadOnlyList<ModelToolSpecDto> BuildOpenAiToolSpecs(string? domain = null)
    {
        return CanonicalTools(domain)
            .Where(tool => !string.Equals(tool.Source, "core", StringComparison.OrdinalIgnoreCase))
            .Select(tool => new ModelToolSpecDto(
                "function",
                new ModelToolFunctionDto(
                    tool.Id,
                    BuildToolDescription(tool),
                    BuildToolParameters(tool))))
            .ToArray();
    }

    private static string BuildToolDescription(ToolDescriptorDto tool)
    {
        var approval = tool.RequiresApproval ? " [requires user approval]" : "";
        var caps = tool.Capabilities.Count > 0 ? $" Capabilities: {string.Join(", ", tool.Capabilities.Take(5))}." : "";
        return $"{tool.DisplayName}.{approval}{caps}";
    }

    private static IReadOnlyDictionary<string, object?> BuildToolParameters(ToolDescriptorDto tool)
    {
        // Generic parameter schema — accepts an object with any properties.
        // Tool-specific schemas can be added later per tool ID.
        return tool.Id switch
        {
            "search_files" => new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["query"] = new Dictionary<string, object?>
                        { ["type"] = "string", ["description"] = "Search query for file names." },
                    ["limit"] = new Dictionary<string, object?>
                        { ["type"] = "integer", ["description"] = "Maximum results." },
                    ["exclude"] = new Dictionary<string, object?>
                    {
                        ["type"] = "array", ["items"] = new Dictionary<string, object?> { ["type"] = "string" },
                        ["description"] = "Patterns to exclude."
                    }
                },
                ["required"] = new[] { "query" }
            },
            "glob_search" => new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["pattern"] = new Dictionary<string, object?>
                        { ["type"] = "string", ["description"] = "Glob pattern (e.g. **/*.ts, src/**/*.rs)." },
                    ["limit"] = new Dictionary<string, object?>
                        { ["type"] = "integer", ["description"] = "Maximum results." }
                },
                ["required"] = new[] { "pattern" }
            },
            "read_file" => new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["path"] = new Dictionary<string, object?>
                        { ["type"] = "string", ["description"] = "File path to read." },
                    ["start_line"] = new Dictionary<string, object?>
                        { ["type"] = "integer", ["description"] = "Start line (1-based)." },
                    ["end_line"] = new Dictionary<string, object?>
                        { ["type"] = "integer", ["description"] = "End line (inclusive)." }
                },
                ["required"] = new[] { "path" }
            },
            "list_directory" => new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["path"] = new Dictionary<string, object?>
                        { ["type"] = "string", ["description"] = "Directory path." },
                    ["show_hidden"] = new Dictionary<string, object?>
                        { ["type"] = "boolean", ["description"] = "Include hidden files." },
                    ["limit"] = new Dictionary<string, object?>
                        { ["type"] = "integer", ["description"] = "Maximum entries." }
                },
                ["required"] = new[] { "path" }
            },
            "grep_content" => new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["pattern"] = new Dictionary<string, object?>
                        { ["type"] = "string", ["description"] = "Text pattern to search for." },
                    ["glob"] = new Dictionary<string, object?>
                        { ["type"] = "string", ["description"] = "Optional glob filter for files." },
                    ["context"] = new Dictionary<string, object?>
                        { ["type"] = "integer", ["description"] = "Context lines around matches." },
                    ["case_insensitive"] = new Dictionary<string, object?>
                        { ["type"] = "boolean", ["description"] = "Case-insensitive search." },
                    ["limit"] = new Dictionary<string, object?>
                        { ["type"] = "integer", ["description"] = "Maximum matches." }
                },
                ["required"] = new[] { "pattern" }
            },
            "apply_patch" => new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["patch"] = new Dictionary<string, object?>
                        { ["type"] = "string", ["description"] = "The patch content to apply." }
                },
                ["required"] = new[] { "patch" }
            },
            "sandbox_exec" => new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["command"] = new Dictionary<string, object?>
                    {
                        ["type"] = "array", ["items"] = new Dictionary<string, object?> { ["type"] = "string" },
                        ["description"] = "Command and arguments to execute."
                    }
                },
                ["required"] = new[] { "command" }
            },
            "review_format" => new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["findings"] = new Dictionary<string, object?>
                    {
                        ["type"] = "array", ["items"] = new Dictionary<string, object?> { ["type"] = "object" },
                        ["description"] = "Review findings with title, severity, location, description."
                    },
                    ["title"] = new Dictionary<string, object?>
                        { ["type"] = "string", ["description"] = "Review title." }
                },
                ["required"] = new[] { "findings" }
            },
            "git_worktree_manager" => new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["action"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string", ["description"] = "Action: status, diff, branch, commit, push, worktree."
                    },
                    ["args"] = new Dictionary<string, object?>
                        { ["type"] = "object", ["description"] = "Action-specific arguments." }
                },
                ["required"] = new[] { "action" }
            },
            "git_stage" or "git_unstage" => new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["paths"] = new Dictionary<string, object?> { ["type"] = "array", ["items"] = new Dictionary<string, object?> { ["type"] = "string" } },
                    ["patch"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "Validated unified text patch for a partial Git index update." },
                    ["max_patch_bytes"] = new Dictionary<string, object?> { ["type"] = "integer" }
                }
            },
            "git_commit" => new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["message"] = new Dictionary<string, object?> { ["type"] = "string" },
                    ["paths"] = new Dictionary<string, object?> { ["type"] = "array", ["items"] = new Dictionary<string, object?> { ["type"] = "string" } },
                    ["include_all"] = new Dictionary<string, object?> { ["type"] = "boolean" },
                    ["commit_staged_only"] = new Dictionary<string, object?> { ["type"] = "boolean" },
                    ["confirm_commit"] = new Dictionary<string, object?> { ["type"] = "boolean" }
                },
                ["required"] = new[] { "message", "confirm_commit" }
            },
            "git_checkout" or "git_branch_create" or "git_branch_delete" or "git_branch_rename" => new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["branch"] = new Dictionary<string, object?> { ["type"] = "string" },
                    ["new_name"] = new Dictionary<string, object?> { ["type"] = "string" },
                    ["force"] = new Dictionary<string, object?> { ["type"] = "boolean" },
                    ["confirm_checkout"] = new Dictionary<string, object?> { ["type"] = "boolean" },
                    ["confirm_create_branch"] = new Dictionary<string, object?> { ["type"] = "boolean" },
                    ["confirm_delete_branch"] = new Dictionary<string, object?> { ["type"] = "boolean" },
                    ["confirm_rename_branch"] = new Dictionary<string, object?> { ["type"] = "boolean" }
                }
            },
            "git_worktree_create" or "git_worktree_remove" => new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["path"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "Managed path inside .tinadec/worktrees." },
                    ["branch"] = new Dictionary<string, object?> { ["type"] = "string" },
                    ["start_ref"] = new Dictionary<string, object?> { ["type"] = "string" },
                    ["force"] = new Dictionary<string, object?> { ["type"] = "boolean" },
                    ["confirm_create_worktree"] = new Dictionary<string, object?> { ["type"] = "boolean" },
                    ["confirm_remove_worktree"] = new Dictionary<string, object?> { ["type"] = "boolean" }
                }
            },
            "git_fetch" or "git_push" or "git_pull" => new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["remote"] = new Dictionary<string, object?> { ["type"] = "string" },
                    ["branch"] = new Dictionary<string, object?> { ["type"] = "string" },
                    ["prune"] = new Dictionary<string, object?> { ["type"] = "boolean" },
                    ["set_upstream"] = new Dictionary<string, object?> { ["type"] = "boolean" },
                    ["confirm_fetch"] = new Dictionary<string, object?> { ["type"] = "boolean" },
                    ["confirm_push"] = new Dictionary<string, object?> { ["type"] = "boolean" },
                    ["confirm_pull"] = new Dictionary<string, object?> { ["type"] = "boolean" }
                }
            },
            "git_merge" or "git_rebase" => new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["operation"] = new Dictionary<string, object?> { ["type"] = "string" },
                    ["branch"] = new Dictionary<string, object?> { ["type"] = "string" },
                    ["strategy"] = new Dictionary<string, object?> { ["type"] = "string" },
                    ["confirm_merge"] = new Dictionary<string, object?> { ["type"] = "boolean" },
                    ["confirm_rebase"] = new Dictionary<string, object?> { ["type"] = "boolean" }
                }
            },
            "git_conflict_resolve" => new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["path"] = new Dictionary<string, object?> { ["type"] = "string" },
                    ["strategy"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "auto, ours, theirs, or both." },
                    ["confirm_resolve"] = new Dictionary<string, object?> { ["type"] = "boolean" }
                },
                ["required"] = new[] { "path", "strategy", "confirm_resolve" }
            },
            _ => new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>(),
                ["required"] = Array.Empty<string>()
            }
        };
    }

    private ToolDescriptorDto[] DeclaredTools(string? domain = null)
    {
        var tools = _providers
            .SelectMany(provider => provider.ListCapabilities())
            .ToArray();

        if (string.IsNullOrWhiteSpace(domain)) return tools;

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
