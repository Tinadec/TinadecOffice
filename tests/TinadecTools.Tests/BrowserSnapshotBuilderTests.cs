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

    [Fact]
    public void Build_CompactsBlankTextAndNestedContainersIdempotentlyWhileKeepingSemanticNodes()
    {
        // Acceptance fixture from native-snapshot-ref-requirement.md:
        // nested containers, blank/NBSP/zero-width text, two sibling groups, iframe, image, SVG, canvas, empty table cell.
        var fixture = """
        {
          "nodes": [
            {"nodeId":"1","role":{"value":"RootWebArea"},"name":{"value":"Fixture"},"childIds":["emptyWrap","singleWrap","groupA","groupB","media"]},
            {"nodeId":"emptyWrap","role":{"value":"generic"},"name":{"value":""},"childIds":["blank1","nbsp","zwsp"]},
            {"nodeId":"blank1","role":{"value":"StaticText"},"name":{"value":"   \t\n "},"childIds":[]},
            {"nodeId":"nbsp","role":{"value":"StaticText"},"name":{"value":"\u00A0\u00A0"},"childIds":[]},
            {"nodeId":"zwsp","role":{"value":"StaticText"},"name":{"value":"\u200B\u200D\uFEFF"},"childIds":[]},
            {"nodeId":"singleWrap","role":{"value":"generic"},"name":{"value":""},"childIds":["innerWrap"]},
            {"nodeId":"innerWrap","role":{"value":"container"},"name":{"value":""},"childIds":["keptText"]},
            {"nodeId":"keptText","role":{"value":"StaticText"},"name":{"value":"Non-interactive summary"},"childIds":[]},
            {"nodeId":"groupA","role":{"value":"generic"},"name":{"value":""},"childIds":["btnA","txtA"]},
            {"nodeId":"btnA","role":{"value":"button"},"name":{"value":"Alpha"},"backendDOMNodeId":11,"childIds":[]},
            {"nodeId":"txtA","role":{"value":"StaticText"},"name":{"value":"Group A note"},"childIds":[]},
            {"nodeId":"groupB","role":{"value":"generic"},"name":{"value":""},"childIds":["btnB","txtB"]},
            {"nodeId":"btnB","role":{"value":"button"},"name":{"value":"Beta"},"backendDOMNodeId":12,"childIds":[]},
            {"nodeId":"txtB","role":{"value":"StaticText"},"name":{"value":"Group B note"},"childIds":[]},
            {"nodeId":"media","role":{"value":"generic"},"name":{"value":""},"childIds":["img1","svg1","canvas1","iframe1","cell1"]},
            {"nodeId":"img1","role":{"value":"image"},"name":{"value":""},"childIds":[]},
            {"nodeId":"svg1","role":{"value":"SVGRoot"},"name":{"value":""},"childIds":[]},
            {"nodeId":"canvas1","role":{"value":"canvas"},"name":{"value":""},"childIds":[]},
            {"nodeId":"iframe1","role":{"value":"Iframe"},"name":{"value":"FrameBox"},"backendDOMNodeId":15,"childIds":[]},
            {"nodeId":"cell1","role":{"value":"cell"},"name":{"value":""},"childIds":[]}
          ]
        }
        """;

        var first = BrowserSnapshotBuilder.Build(Parse(fixture), new Dictionary<int, BrowserRef>(), 1, 24_000);
        var second = BrowserSnapshotBuilder.Build(Parse(fixture), first.Refs, first.NextRef, 24_000);

        Assert.Equal(first.Text, second.Text);
        Assert.Contains("text \"Non-interactive summary\"", first.Text);
        Assert.Contains("image", first.Text);
        Assert.Contains("svg_root", first.Text);
        Assert.Contains("canvas", first.Text);
        Assert.Contains("iframe \"FrameBox\" [ref=3]", first.Text);
        Assert.Contains("cell", first.Text);
        Assert.DoesNotContain("\u200B", first.Text, StringComparison.Ordinal);
        // Two sibling groups remain distinct under separate `container` nodes.
        var containerLines = first.Text.Split('\n').Count(line => line.Trim() == "container");
        Assert.Equal(3, containerLines); // groupA, groupB, media
    }

    [Fact]
    public void BuildMultiFrame_AllocatesDistinctRefsWhenBackendIdsRepeatAcrossSameOriginAndOopif()
    {
        var mainAx = Parse("""
        {
          "nodes": [
            {"nodeId":"1","role":{"value":"RootWebArea"},"name":{"value":"Top"},"childIds":["2","3","4"]},
            {"nodeId":"2","role":{"value":"button"},"name":{"value":"Top Action"},"backendDOMNodeId":6,"childIds":[]},
            {"nodeId":"3","role":{"value":"Iframe"},"name":{"value":"Same Origin"},"backendDOMNodeId":20,"childIds":[]},
            {"nodeId":"4","role":{"value":"Iframe"},"name":{"value":"Cross Origin"},"backendDOMNodeId":21,"childIds":[]}
          ]
        }
        """);

        // Same backendDOMNodeId = 6 inside both child frames!
        var sameOriginAx = Parse("""
        {
          "nodes": [
            {"nodeId":"1","role":{"value":"RootWebArea"},"name":{"value":"Inner Same"},"childIds":["2"]},
            {"nodeId":"2","role":{"value":"button"},"name":{"value":"Run same-origin"},"backendDOMNodeId":6,"childIds":[]}
          ]
        }
        """);

        var oopifAx = Parse("""
        {
          "nodes": [
            {"nodeId":"1","role":{"value":"RootWebArea"},"name":{"value":"Inner OOPIF"},"childIds":["2"]},
            {"nodeId":"2","role":{"value":"textbox"},"name":{"value":"Card Number"},"backendDOMNodeId":6,"childIds":[]}
          ]
        }
        """);

        var frames = new[]
        {
            new BrowserFrameAxInput(null, null, mainAx),
            new BrowserFrameAxInput("frame-same", null, sameOriginAx, OwnerBackendNodeId: 20),
            new BrowserFrameAxInput("frame-oopif", "cdp-oopif-1", oopifAx, OwnerBackendNodeId: 21),
        };

        var built = BrowserSnapshotBuilder.BuildMultiFrame(frames, new Dictionary<int, BrowserRef>(), 1);

        Assert.Equal(5, built.RefCount); // Top button + 2 iframes + 1 same-origin button + 1 OOPIF textbox
        var topBtn = built.Refs.Values.Single(r => r.Name == "Top Action");
        var sameBtn = built.Refs.Values.Single(r => r.Name == "Run same-origin");
        var oopifBox = built.Refs.Values.Single(r => r.Name == "Card Number");

        Assert.NotEqual(topBtn.Ref, sameBtn.Ref);
        Assert.NotEqual(topBtn.Ref, oopifBox.Ref);
        Assert.NotEqual(sameBtn.Ref, oopifBox.Ref);

        Assert.Null(topBtn.FrameId);
        Assert.Equal("frame-same", sameBtn.FrameId);
        Assert.Equal("frame-oopif", oopifBox.FrameId);
        Assert.Equal("cdp-oopif-1", oopifBox.CdpSessionId);
    }

    [Fact]
    public void BuildMultiFrame_OmitsAmbiguousHiddenCrossFrameAndTypeMismatchedLocators()
    {
        // Covers all 4 locator cases in native-snapshot-ref-requirement.md + sentinel omission:
        // 1. Repeated controls in one document ("Delete" x2)
        // 2. Hidden/ignored copy sharing accessible name with visible editor ("Secret Notes")
        // 3. Same role + name in top-level document and an iframe ("Shared Save")
        // 4. Locator whose element tag/type does not match source role (div on button without explicit role)
        // 5. Never serializes loc=unstable or loc=ambiguous
        var topAx = Parse("""
        {
          "nodes": [
            {"nodeId":"1","role":{"value":"RootWebArea"},"name":{"value":"Top"},"childIds":["dup1","dup2","hiddenEditor","visibleEditor","sharedTop","badTag","sentinelNode","uniqueBtn","frameOwner"]},
            {"nodeId":"dup1","role":{"value":"button"},"name":{"value":"Delete"},"backendDOMNodeId":10,"childIds":[]},
            {"nodeId":"dup2","role":{"value":"button"},"name":{"value":"Delete"},"backendDOMNodeId":11,"childIds":[]},
            {"nodeId":"hiddenEditor","ignored":true,"role":{"value":"textbox"},"name":{"value":"Secret Notes"},"backendDOMNodeId":12,"childIds":[]},
            {"nodeId":"visibleEditor","role":{"value":"textbox"},"name":{"value":"Secret Notes"},"backendDOMNodeId":13,"childIds":[]},
            {"nodeId":"sharedTop","role":{"value":"button"},"name":{"value":"Shared Save"},"backendDOMNodeId":14,"childIds":[]},
            {"nodeId":"badTag","role":{"value":"button"},"name":{"value":"Fake Div Button"},"tagName":"div","backendDOMNodeId":15,"childIds":[]},
            {"nodeId":"sentinelNode","role":{"value":"button"},"name":{"value":""},"loc":"ambiguous","backendDOMNodeId":16,"childIds":[]},
            {"nodeId":"uniqueBtn","role":{"value":"button"},"name":{"value":"Unique Submit"},"loc":"unstable","backendDOMNodeId":17,"childIds":[]},
            {"nodeId":"frameOwner","role":{"value":"Iframe"},"name":{"value":"Sub"},"backendDOMNodeId":18,"childIds":[]}
          ]
        }
        """);

        var iframeAx = Parse("""
        {
          "nodes": [
            {"nodeId":"1","role":{"value":"RootWebArea"},"name":{"value":"Child"},"childIds":["sharedChild"]},
            {"nodeId":"sharedChild","role":{"value":"button"},"name":{"value":"Shared Save"},"backendDOMNodeId":30,"childIds":[]}
          ]
        }
        """);

        var verifiedCss = new Dictionary<FrameNodeKey, VerifiedDomLocator>
        {
            // dup1 gets a verified unique CSS fallback (#delete-first) matching backendId=10 and tag=button:
            [FrameNodeKey.For(null, 10)] = new("loc=css:#delete-first", BackendNodeId: 10, TagName: "button"),
            // dup2 has a mismatched backendNodeId (999 != 11), so it must be rejected!
            [FrameNodeKey.For(null, 11)] = new("loc=css:#wrong-node", BackendNodeId: 999, TagName: "button"),
        };

        var built = BrowserSnapshotBuilder.BuildMultiFrame(
            [
                new BrowserFrameAxInput(null, null, topAx),
                new BrowserFrameAxInput("child-1", null, iframeAx, OwnerBackendNodeId: 18),
            ],
            new Dictionary<int, BrowserRef>(),
            1,
            new BrowserSnapshotOptions { VerifiedCssLocators = verifiedCss });

        Assert.DoesNotContain("loc=unstable", built.Text);
        Assert.DoesNotContain("loc=ambiguous", built.Text);
        Assert.DoesNotContain("loc=role:button[name=\"Delete\"]", built.Text);
        Assert.DoesNotContain("loc=role:textbox[name=\"Secret Notes\"]", built.Text);
        Assert.DoesNotContain("loc=role:button[name=\"Shared Save\"]", built.Text);
        Assert.DoesNotContain("Fake Div Button\" [ref=5] loc=", built.Text);
        Assert.DoesNotContain("loc=css:#wrong-node", built.Text);

        // Verified unique CSS fallback for dup1 and verified unique role locator for uniqueBtn are emitted:
        Assert.Contains("loc=css:#delete-first", built.Text);
        Assert.Contains("loc=role:button[name=\"Unique Submit\"]", built.Text);
    }
}
