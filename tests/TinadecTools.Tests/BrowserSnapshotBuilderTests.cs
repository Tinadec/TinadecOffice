using System.Text;
using System.Text.Json;
using TinadecTools.Tools.Browser;

namespace TinadecTools.Tests;

public sealed class BrowserSnapshotBuilderTests
{
    private const string Tree = """
    {
      "nodes": [
        {"nodeId":"1","role":{"value":"RootWebArea"},"name":{"value":"Test"},"childIds":["2","3"]},
        {"nodeId":"2","role":{"value":"button"},"name":{"value":"Click me"},"backendDOMNodeId":6,
         "childIds":[],"properties":[{"name":"disabled","value":{"value":true}}]},
        {"nodeId":"3","role":{"value":"generic"},"name":{"value":""},"childIds":["4","5"]},
        {"nodeId":"4","role":{"value":"textbox"},"name":{"value":"Email"},"value":{"value":"a@b"},
         "backendDOMNodeId":7,"childIds":[]},
        {"nodeId":"5","role":{"value":"link"},"name":{"value":"Docs"},"backendDOMNodeId":8,"childIds":[]}
      ]
    }
    """;

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void Build_EmitsCompactTreeWithRefsAndFlattensAnonymousContainers()
    {
        var result = BrowserSnapshotBuilder.Build(Parse(Tree), new Dictionary<int, BrowserRef>(), 1, 24_000);

        Assert.False(result.Truncated);
        Assert.Equal(3, result.RefCount);
        Assert.Contains("RootWebArea \"Test\"", result.Text);
        Assert.Contains("button \"Click me\" [ref=1] disabled", result.Text);
        Assert.Contains("textbox \"Email\" [ref=2] value=\"a@b\"", result.Text);
        Assert.Contains("link \"Docs\" [ref=3]", result.Text);
        Assert.DoesNotContain("generic", result.Text);
    }

    [Fact]
    public void Build_ReusesRefsByBackendNodeAndAllocatesNewOnes()
    {
        var first = BrowserSnapshotBuilder.Build(Parse(Tree), new Dictionary<int, BrowserRef>(), 1, 24_000);

        var extended = """
        {
          "nodes": [
            {"nodeId":"1","role":{"value":"RootWebArea"},"name":{"value":"Test"},"childIds":["2","4","5","9"]},
            {"nodeId":"2","role":{"value":"button"},"name":{"value":"Click me"},"backendDOMNodeId":6,"childIds":[]},
            {"nodeId":"4","role":{"value":"textbox"},"name":{"value":"Email"},"value":{"value":"a@b"},"backendDOMNodeId":7,"childIds":[]},
            {"nodeId":"5","role":{"value":"link"},"name":{"value":"Docs"},"backendDOMNodeId":8,"childIds":[]},
            {"nodeId":"9","role":{"value":"button"},"name":{"value":"Next"},"backendDOMNodeId":9,"childIds":[]}
          ]
        }
        """;
        var second = BrowserSnapshotBuilder.Build(Parse(extended), first.Refs, first.NextRef, 24_000);

        Assert.Contains("button \"Click me\" [ref=1]", second.Text);
        Assert.Contains("button \"Next\" [ref=4]", second.Text);
        Assert.Equal(4, second.RefCount);
    }

    [Fact]
    public void Build_TruncatesAtTheBudgetAndStillFlagsIt()
    {
        var json = new StringBuilder("""{"nodes":[{"nodeId":"1","role":{"value":"RootWebArea"},"name":{"value":"Big"},"childIds":[""");
        for (var i = 0; i < 400; i++)
        {
            if (i > 0) json.Append(',');
            json.Append($"\"n{i}\"");
        }
        json.Append("]},");
        for (var i = 0; i < 400; i++)
        {
            if (i > 0) json.Append(',');
            json.Append($"{{\"nodeId\":\"n{i}\",\"role\":{{\"value\":\"text\"}},\"name\":{{\"value\":\"line {i} with enough text to consume the budget\"}},\"childIds\":[]}}");
        }
        json.Append("]}");

        var result = BrowserSnapshotBuilder.Build(Parse(json.ToString()), new Dictionary<int, BrowserRef>(), 1, 2048);

        Assert.True(result.Truncated);
        Assert.True(result.Text.Length <= 2048 + 96, $"snapshot length {result.Text.Length}");
        Assert.Contains("truncated", result.Text);
    }

    [Fact]
    public void Build_EscapesQuotesAndCollapsesWhitespace()
    {
        var tree = """
        {"nodes":[
          {"nodeId":"1","role":{"value":"RootWebArea"},"name":{"value":"T"},"childIds":["2"]},
          {"nodeId":"2","role":{"value":"button"},"name":{"value":"Say \"yes\"\n  now"},"backendDOMNodeId":3,"childIds":[]}
        ]}
        """;

        var result = BrowserSnapshotBuilder.Build(Parse(tree), new Dictionary<int, BrowserRef>(), 1, 24_000);

        Assert.Contains("button \"Say \\\"yes\\\" now\" [ref=1]", result.Text);
    }

    [Fact]
    public void Build_ReportsCheckedAndSelectedStates()
    {
        var tree = """
        {"nodes":[
          {"nodeId":"1","role":{"value":"RootWebArea"},"name":{"value":"T"},"childIds":["2","3"]},
          {"nodeId":"2","role":{"value":"checkbox"},"name":{"value":"Remember"},"backendDOMNodeId":4,"childIds":[],
           "properties":[{"name":"checked","value":{"value":true}}]},
          {"nodeId":"3","role":{"value":"tab"},"name":{"value":"Home"},"backendDOMNodeId":5,"childIds":[],
           "properties":[{"name":"selected","value":{"value":true}}]}
        ]}
        """;

        var result = BrowserSnapshotBuilder.Build(Parse(tree), new Dictionary<int, BrowserRef>(), 1, 24_000);

        Assert.Contains("checkbox \"Remember\" [ref=1] checked", result.Text);
        Assert.Contains("tab \"Home\" [ref=2] selected", result.Text);
    }
}
