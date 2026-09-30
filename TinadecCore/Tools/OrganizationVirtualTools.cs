using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Tools;

/// <summary>
/// Manifest entries for the organization tools and <c>graph_view</c>. Core executes them against its
/// own state, so like the TinaChat tools they are injected into the frozen manifest when a mode
/// declares them.
///
/// No approval gate: the writes are messages and reports, bounded by the organization's own
/// membership, contact and board rules, and the actor is the instance's own member — gating a chat
/// message like a workspace write would make an agent that cannot wait simply stop talking.
/// </summary>
internal static class OrganizationVirtualTools
{
    public static ToolManifestEntryDto? ManifestEntry(string? toolId)
    {
        var entry = OrganizationToolCatalog.Find(toolId);
        if (entry is null) return null;
        return new ToolManifestEntryDto
        {
            Id = entry.Id,
            Description = entry.Description,
            RequiresApproval = false,
            InputSchema = JsonDocument.Parse(entry.SchemaJson).RootElement.Clone(),
            Risk = "low",
            MutatesWorkspace = false,
            // Every write keys on the model's own tool-call id, so replaying a call returns the
            // original post instead of a second one.
            RetrySafety = "safe",
            ConfirmationFields = [],
        };
    }
}
