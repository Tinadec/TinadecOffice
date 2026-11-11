using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Tools;

/// <summary>
/// The progressive-disclosure half of the skill index: the model sees a name and one description,
/// then asks for the body by name. It resolves through the same catalog the index and settings use,
/// so a built-in skill (no readable file on disk) and a project skill are loaded the same way.
/// </summary>
internal static class CoreSkillLoadTool
{
    public const string ToolId = CoreVirtualToolPolicy.LoadSkillToolId;
    private const int DefaultPageChars = 32 * 1024;
    private const int MaxPageChars = 64 * 1024;
    private const int MaxListedNames = 40;

    private const string InputSchemaJson =
        "{\"type\":\"object\",\"properties\":{" +
        "\"name\":{\"type\":\"string\",\"description\":\"Exact skill name from the available skills list. Kebab-case; not a path.\"}," +
        "\"offset\":{\"type\":\"integer\",\"minimum\":0,\"description\":\"Character offset in the body. Defaults to 0.\"}," +
        "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"description\":\"Characters to return; defaults to 32768 and is capped at 65536.\"}}" +
        ",\"required\":[\"name\"],\"additionalProperties\":false}";

    public static ToolManifestEntryDto ManifestEntry() => new()
    {
        Id = ToolId,
        Description =
            "Load the full instructions of a skill by its exact name. The skill index shows only a "
            + "name and a description; call this before acting on a task that names or clearly matches "
            + "a skill. The result includes the body, the package's file list, and the base relative "
            + "resources resolve from. Built-in skills are loaded from Core and have no file path.",
        RequiresApproval = false,
        InputSchema = JsonDocument.Parse(InputSchemaJson).RootElement.Clone(),
        Risk = "low",
        MutatesWorkspace = false,
        RetrySafety = "safe",
        ConfirmationFields = []
    };

    public static bool IsCoreTool(string toolId) => CoreVirtualToolPolicy.IsLoadSkill(toolId);

    public static async Task<ToolWireResponseDto> ExecuteAsync(
        IToolSkillResourceService? skills,
        ToolInvocationScope scope,
        ToolWireRequestDto wire,
        CancellationToken cancellationToken = default)
    {
        if (skills is null)
            return Failure(wire, "Skill loading is unavailable in this host: no skill catalog is registered.");

        string? name = null;
        var offset = 0;
        var limit = DefaultPageChars;
        if (wire.Params is { ValueKind: JsonValueKind.Object } parameters)
        {
            if (parameters.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
                name = nameElement.GetString()?.Trim();
            offset = ReadNonNegative(parameters, "offset", 0);
            limit = ReadNonNegative(parameters, "limit", DefaultPageChars);
        }
        if (limit > MaxPageChars) limit = MaxPageChars;
        if (string.IsNullOrWhiteSpace(name))
            return Failure(wire, "load_skill needs a 'name' copied from the available skills list.");

        var catalog = await skills.ListAsync(scope.ProjectId, includeBuiltins: true, cancellationToken).ConfigureAwait(false);
        var resource = catalog.Skills.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal)
            && x.Valid && x.Enabled && x.ShadowedBy is null);
        if (resource is null)
        {
            var names = catalog.Skills.Where(x => x.Valid && x.Enabled)
                .Select(x => x.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Take(MaxListedNames).ToArray();
            var known = names.Length == 0 ? "no skills are available in this run" : $"available: {string.Join(", ", names)}";
            return Failure(wire, $"No enabled skill named '{name}' in this run; {known}.");
        }

        var body = (await skills.GetAsync(resource.ResourceId, scope.ProjectId, cancellationToken).ConfigureAwait(false))?.Content;
        if (body is null)
            return Failure(wire, $"Skill '{name}' is listed but its body could not be read. Reload the skill catalog and try again.");

        var page = body.Length <= offset ? "" : body[offset..Math.Min(body.Length, offset + limit)];
        var next = offset + page.Length < body.Length ? offset + page.Length : (int?)null;
        var resourceBase = resource.SourceKind == "builtin"
            ? $"tinadec://builtin/{resource.Name}"
            : Path.GetDirectoryName(resource.Path)?.Replace('\\', '/') ?? "";

        return new ToolWireResponseDto
        {
            CallId = wire.ToolCallId,
            IsSuccess = true,
            Result = JsonSerializer.SerializeToElement(new
            {
                name = resource.Name,
                description = resource.Description,
                source = resource.SourceKind,
                resource_base = resourceBase,
                offset,
                chars_returned = page.Length,
                body = page,
                has_more = next is not null,
                next_offset = next,
                files = resource.PackageFiles.Select(x => new { path = x.Path, size_bytes = x.SizeBytes }).ToArray(),
            }),
        };
    }

    private static int ReadNonNegative(JsonElement parameters, string name, int fallback) =>
        parameters.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number
        && element.TryGetInt32(out var value) && value >= 0 ? value : fallback;

    private static ToolWireResponseDto Failure(ToolWireRequestDto wire, string error) =>
        new() { CallId = wire.ToolCallId, IsSuccess = false, Error = error };
}
