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
            var methodsByToolId = new List<(string ToolId, string ContainingType, string MethodName)>();

            foreach (var item in items)
            {
                if (item.TargetSymbol is not IMethodSymbol method)
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

                methodsByToolId.Add((
                    toolId!,
                    method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    method.Name));
            }

            var source = generateRegistry(methodsByToolId);
            spc.AddSource("ToolFunctionRegistry.g.cs", SourceText.From(source, Encoding.UTF8));
        });
    }

    private static string generateRegistry(IReadOnlyList<(string ToolId, string ContainingType, string MethodName)> methods)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Text.Json;");
        sb.AppendLine("using TinadecTools.Abstractions;");
        sb.AppendLine();
        sb.AppendLine("namespace TinadecTools.Abstractions;");
        sb.AppendLine();
        sb.AppendLine("internal static partial class GeneratedToolRegistry");
        sb.AppendLine("{");
        sb.AppendLine("    public static void RegisterAll()");
        sb.AppendLine("    {");

        foreach (var (toolId, containingType, methodName) in methods)
        {
            sb.AppendLine($"""        ToolRegistry.Register("{escape(toolId)}", {containingType}.{methodName});""");
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
