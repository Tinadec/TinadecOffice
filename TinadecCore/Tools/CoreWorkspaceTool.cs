using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Tools;

/// <summary>
/// Core-owned virtual tool for projectless (free-conversation) sessions. It lets a
/// worker propose creating a project workspace; the call never reaches a TinadecTools
/// child process. After the standard approval gate passes, Core itself creates the
/// directory, registers the project, and migrates the session onto it. A run whose
/// frozen manifest contains this tool keeps it as its tool ceiling: subsequent
/// interactions run with a fresh manifest frozen from the new project root.
/// </summary>
internal static class CoreWorkspaceTool
{
    public const string ToolId = CoreVirtualToolPolicy.CreateWorkspaceToolId;

    private const string InputSchemaJson =
        "{\"type\":\"object\",\"properties\":{" +
        "\"name\":{\"type\":\"string\",\"description\":\"Name of the project workspace.\"}," +
        "\"path\":{\"type\":\"string\",\"description\":\"Absolute filesystem path of the workspace directory to create.\"}}," +
        "\"required\":[\"name\",\"path\"],\"additionalProperties\":false}";

    public static ToolManifestEntryDto ManifestEntry() => new()
    {
        Id = ToolId,
        Description = "Create a new project workspace directory and bind this conversation to it. Requires explicit user approval.",
        RequiresApproval = true,
        InputSchema = JsonDocument.Parse(InputSchemaJson).RootElement.Clone(),
        Risk = "high",
        MutatesWorkspace = true,
        RetrySafety = "unsafe",
        ConfirmationFields = []
    };

    public static bool IsCoreTool(string toolId) => CoreVirtualToolPolicy.IsCreateWorkspace(toolId);
}
