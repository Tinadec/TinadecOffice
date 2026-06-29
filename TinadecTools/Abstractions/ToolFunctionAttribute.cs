namespace TinadecTools.Abstractions;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
internal sealed class ToolFunctionAttribute : Attribute
{
    public ToolFunctionAttribute(string toolId)
    {
        ToolId = toolId;
    }

    public string ToolId { get; }
}
