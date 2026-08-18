namespace TinadecTools.Abstractions;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
internal sealed class ToolFunctionAttribute : Attribute
{
    public ToolFunctionAttribute(string toolId)
    {
        ToolId = toolId;
    }

    public string ToolId { get; }

    public bool RequiresApproval { get; set; }

    public string? Description { get; set; }

    public string? Risk { get; set; }

    public bool MutatesWorkspace { get; set; }

    public string? RetrySafety { get; set; }

    public string[] ConfirmationFields { get; set; } = [];

    /// <summary>JSON Schema object used for manifest v2. Defaults to a permissive object schema.</summary>
    public string InputSchema { get; set; } = "{\"type\":\"object\",\"additionalProperties\":true}";
}
