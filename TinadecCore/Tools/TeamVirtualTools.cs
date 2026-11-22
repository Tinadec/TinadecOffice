using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Tools;

/// <summary>
/// Manifest entries for the 5 orthogonal <c>team_*</c> orchestration primitives (#33).
/// Core executes them inside the run engine against checkpoint/board/worktree state, so they are
/// injected into the frozen manifest when a mode declares them.
/// </summary>
internal static class TeamVirtualTools
{
    public static ToolManifestEntryDto? ManifestEntry(string? toolId)
    {
        var entry = TeamToolCatalog.Find(toolId);
        if (entry is null) return null;
        var isIntegrate = string.Equals(entry.Id, CoreVirtualToolPolicy.TeamIntegrateToolId, StringComparison.OrdinalIgnoreCase);
        return new ToolManifestEntryDto
        {
            Id = entry.Id,
            Description = entry.Description,
            RequiresApproval = false,
            InputSchema = JsonDocument.Parse(entry.SchemaJson).RootElement.Clone(),
            Risk = isIntegrate ? "medium" : "low",
            MutatesWorkspace = false,
            RetrySafety = "safe",
            ConfirmationFields = [],
        };
    }
}
