using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace TinadecTools.Generators;

[Generator]
public sealed class ToolFunctionGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var toolFunctionAttributeSymbol = "TinadecTools.Abstractions.ToolFunctionAttribute";

        var methods = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                toolFunctionAttributeSymbol,
                static (node, _) => node is MethodDeclarationSyntax,
                static (ctx, _) => ctx)
            .Collect();

        context.RegisterSourceOutput(methods, static (spc, items) =>
        {
            var source = new StringBuilder();
            source.AppendLine("using System.Text.Json.Serialization.Metadata;");
            source.AppendLine("using TinadecTools.Abstractions;");
            source.AppendLine();
            source.AppendLine("namespace TinadecTools.Abstractions;");
            source.AppendLine();
            source.AppendLine("internal static partial class GeneratedToolRegistry");
            source.AppendLine("{");
            source.AppendLine("    public static void RegisterAll()");
            source.AppendLine("    {");

            foreach (var item in items)
            {
                if (item.TargetSymbol is not IMethodSymbol method)
                {
                    continue;
                }

                if (!method.IsStatic || method.Parameters.Length != 2)
                {
                    continue;
                }

                if (method.Parameters[1].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) != "global::System.Threading.CancellationToken")
                {
                    continue;
                }

                var attr = method.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "TinadecTools.Abstractions.ToolFunctionAttribute");

                if (attr?.ConstructorArguments.Length != 1)
                {
                    continue;
                }

                var toolId = attr.ConstructorArguments[0].Value as string;
                if (string.IsNullOrWhiteSpace(toolId))
                {
                    continue;
                }

                var requiresApproval = attr.NamedArguments.FirstOrDefault(a => a.Key == "RequiresApproval").Value.Value as bool? ?? false;
                var description = GetStringArgument(attr, "Description") ?? string.Empty;
                var risk = GetStringArgument(attr, "Risk") ?? (requiresApproval ? "high" : "low");
                var mutatesWorkspace = GetBoolArgument(attr, "MutatesWorkspace") ?? requiresApproval;
                var retrySafety = GetStringArgument(attr, "RetrySafety") ?? (mutatesWorkspace ? "unsafe" : "safe");
                var inputSchema = GetStringArgument(attr, "InputSchema") ?? BuildInputSchema(method.Parameters[0].Type);
                var confirmationFields = GetStringArrayArgument(attr, "ConfirmationFields");

                var argsType = method.Parameters[0].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var resultType = GetResultType(method.ReturnType);
                if (resultType is null)
                {
                    continue;
                }

                var containingType = method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var contextType = $"{method.ContainingType.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{method.ContainingType.Name}JsonContext";
                var contextVar = $"{method.ContainingType.Name.ToLowerInvariant()}_{method.Name.ToLowerInvariant()}Json";
                var confirmationFieldsSource = confirmationFields.Count == 0
                    ? "System.Array.Empty<string>()"
                    : "new string[] { " + string.Join(", ", confirmationFields.Select(value => $"\"{escape(value)}\"")) + " }";

                source.AppendLine($"""        var {contextVar} = new {contextType}();""");
                source.AppendLine($"""        ToolRegistry.Register<{argsType}, {resultType}>("{escape(toolId!)}", {containingType}.{method.Name}, (JsonTypeInfo<{argsType}>) {contextVar}.GetTypeInfo(typeof({argsType}))!, (JsonTypeInfo<{resultType}>) {contextVar}.GetTypeInfo(typeof({resultType}))!, requiresApproval: {requiresApproval.ToString().ToLowerInvariant()}, description: "{escape(description)}", inputSchemaJson: "{escape(inputSchema)}", risk: "{escape(risk)}", mutatesWorkspace: {mutatesWorkspace.ToString().ToLowerInvariant()}, retrySafety: "{escape(retrySafety)}", confirmationFields: {confirmationFieldsSource});""");
            }

            source.AppendLine("    }");
            source.AppendLine("}");
            spc.AddSource("ToolFunctionRegistry.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
        });
    }

    private static string? GetResultType(ITypeSymbol returnType)
    {
        if (returnType is not INamedTypeSymbol namedType || namedType.Name != "ValueTask" || namedType.TypeArguments.Length != 1)
        {
            return null;
        }

        return namedType.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    private static string? GetStringArgument(AttributeData attribute, string name) =>
        attribute.NamedArguments.FirstOrDefault(argument => argument.Key == name).Value.Value as string;

    private static bool? GetBoolArgument(AttributeData attribute, string name)
    {
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key == name && argument.Value.Value is bool value)
            {
                return value;
            }
        }
        return null;
    }

    private static IReadOnlyList<string> GetStringArrayArgument(AttributeData attribute, string name)
    {
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key != name) continue;
            return argument.Value.Values
                .Where(value => value.Value is string)
                .Select(value => (string)value.Value!)
                .ToArray();
        }
        return [];
    }

    private static string BuildInputSchema(ITypeSymbol type) => BuildObjectSchema(type, depth: 0, []);

    /// <summary>
    /// Object schema for a parameter record. Nested parameter types are expanded
    /// recursively (see <see cref="BuildSchemaForType"/>) so a structured argument
    /// such as a line anchor is described to the model instead of collapsing into a
    /// bare <c>{"type":"object"}</c> the model cannot act on.
    /// </summary>
    private static string BuildObjectSchema(ITypeSymbol type, int depth, HashSet<string> visited)
    {
        if (type is not INamedTypeSymbol namedType)
        {
            return "{\"type\":\"object\",\"additionalProperties\":true}";
        }

        var properties = namedType.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(property => !property.IsStatic && property.DeclaredAccessibility == Accessibility.Public && property.GetMethod is not null)
            .OrderBy(GetJsonPropertyName, StringComparer.Ordinal)
            .Select(property => $"\"{escape(GetJsonPropertyName(property))}\":"
                + WithDescription(BuildSchemaForType(property.Type, depth, visited), GetDescription(property)))
            .ToArray();
        return "{\"type\":\"object\",\"properties\":{" + string.Join(",", properties) + "},\"additionalProperties\":false}";
    }

    /// <summary>
    /// Merges a description INTO the property's own schema. Appending it as a sibling
    /// of the property inside <c>properties</c> would instead declare a parameter
    /// literally named "description" — the schema would advertise a field the tool
    /// does not accept, and the prose would never reach the parameter it describes.
    /// Every schema produced here is a JSON object, so the last brace is the insertion
    /// point.
    /// </summary>
    private static string WithDescription(string schema, string? description)
    {
        // netstandard2.0: no char-of-string overload and no range operators.
        if (string.IsNullOrWhiteSpace(description) || !schema.EndsWith("}", StringComparison.Ordinal)) return schema;
        return schema.Substring(0, schema.Length - 1) + ",\"description\":\"" + escape(description!) + "\"}";
    }

    private static string GetJsonPropertyName(IPropertySymbol property)
    {
        var attribute = property.GetAttributes().FirstOrDefault(candidate =>
            candidate.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonPropertyNameAttribute");
        return attribute?.ConstructorArguments.FirstOrDefault().Value as string ?? property.Name;
    }

    /// <summary>
    /// The human/model-facing description of a parameter, when the author supplied
    /// one. Without this the generator produced schemas with no prose at all, so a
    /// tool whose parameters need explaining (hash anchors, byte offsets) reached the
    /// model as a shape with no instructions.
    /// </summary>
    private static string? GetDescription(IPropertySymbol property)
    {
        foreach (var candidate in property.GetAttributes())
        {
            var name = candidate.AttributeClass?.ToDisplayString();
            if (name is not ("System.ComponentModel.DescriptionAttribute" or "TinadecTools.Abstractions.DescriptionAttribute"))
            {
                continue;
            }
            if (candidate.ConstructorArguments.FirstOrDefault().Value is string text && !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }
        return null;
    }

    /// <summary>Maximum nesting the generator will describe before falling back to a bare object.</summary>
    private const int MaxSchemaDepth = 3;

    private static string BuildSchemaForType(ITypeSymbol type, int depth = 0, HashSet<string>? visited = null)
    {
        visited ??= [];
        if (type is IArrayTypeSymbol array)
        {
            return "{\"type\":\"array\",\"items\":" + BuildSchemaForType(array.ElementType, depth, visited) + "}";
        }
        if (type is INamedTypeSymbol named && named.IsGenericType && named.TypeArguments.Length == 1
            && (named.Name is "List" or "IReadOnlyList" or "IEnumerable" or "ICollection" or "IList"))
        {
            return "{\"type\":\"array\",\"items\":" + BuildSchemaForType(named.TypeArguments[0], depth, visited) + "}";
        }

        var primitive = type.SpecialType switch
        {
            SpecialType.System_String => "{\"type\":\"string\"}",
            SpecialType.System_Boolean => "{\"type\":\"boolean\"}",
            SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16
                or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64
                => "{\"type\":\"integer\"}",
            SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal => "{\"type\":\"number\"}",
            _ => null
        };
        if (primitive is not null) return primitive;

        var name = type.ToDisplayString();
        if (name is "System.Guid") return "{\"type\":\"string\",\"format\":\"uuid\"}";
        if (name is "System.DateTime" or "System.DateTimeOffset") return "{\"type\":\"string\",\"format\":\"date-time\"}";
        if (name == "System.Text.Json.JsonElement") return "{\"type\":\"object\"}";

        // A nested parameter record is described, not collapsed: an anchor whose
        // content/hash pair is invisible to the model is an edit tool the model
        // cannot learn to call. Depth and a visited set keep a self-referencing type
        // from turning schema generation into an infinite walk.
        if (depth < MaxSchemaDepth && type is INamedTypeSymbol nested && visited.Add(name))
        {
            return BuildObjectSchema(nested, depth + 1, visited);
        }
        return "{\"type\":\"object\"}";
    }

    private static string escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
