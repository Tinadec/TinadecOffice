using TinadecCore.Contracts.Events;
using TinadecCore.Lifecycle;

namespace TinadecCore.Api.Tests;

/// <summary>
/// A tool opts into its own HTML view by returning a <c>widget_html</c> key. That single rule is
/// what keeps the trust boundary legible: Core never decides which tools produce UI, it only
/// carries what a tool returned, and the desktop renders it inside a sandboxed frame.
/// </summary>
public sealed class ToolWidgetEnvelopeTests
{
    private static string? Extract(string? body) => StorageLifecycleService.ExtractWidgetHtml(body);

    [Fact]
    public void ReadsTheViewOutOfAnOrdinaryToolResult()
    {
        var body = """{"files":["a.ts"],"widget_html":"<div id=\"w\">x</div>"}""";
        Assert.Equal("<div id=\"w\">x</div>", Extract(body));
    }

    [Fact]
    public void LeavesTheBodyAlone()
    {
        // The extraction is a read, not a strip: a consumer of the plain result must still see
        // every field the tool returned, including the markup.
        const string body = """{"count":2,"widget_html":"<p>y</p>"}""";
        Assert.Equal("<p>y</p>", Extract(body));
        Assert.Contains("widget_html", body, StringComparison.Ordinal);
    }

    [Theory]
    // Not an object, so there is no envelope to find.
    [InlineData("""["a","b"]""")]
    [InlineData("plain text output")]
    // An object without the key is the ordinary case, not an error.
    [InlineData("""{"files":["a.ts"]}""")]
    // Wrong type, empty, or whitespace is not a view.
    [InlineData("""{"widget_html":42}""")]
    [InlineData("""{"widget_html":""}""")]
    [InlineData("""{"widget_html":"   "}""")]
    [InlineData("""{"widget_html":null}""")]
    // Unparseable JSON has no envelope rather than being an error.
    [InlineData("""{"widget_html":""")]
    [InlineData("")]
    [InlineData(null)]
    public void RefusesAnythingThatIsNotAViews(string? body)
    {
        Assert.Null(Extract(body));
    }

    [Fact]
    public void FindsTheKeyNestedOneLevelDeep()
    {
        // The key is read from the root object only. A nested one belongs to a nested payload and
        // is not this tool's view, so it must not be picked up.
        const string body = """{"result":{"widget_html":"<p>inner</p>"}}""";
        Assert.Null(Extract(body));
    }
}

/// <summary>
/// A truncated body is not valid JSON, so the envelope cannot come from the decoded text. The read
/// path streams the property out of the bytes it does have, which is what keeps a large result from
/// silently losing a view it did declare.
/// </summary>
public sealed class ToolWidgetTruncationTests
{
    private static readonly System.Reflection.MethodInfo PrefixReader =
        typeof(StorageLifecycleService).GetMethod("ReadWidgetHtmlFromPrefix",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

    private static string? FromPrefix(string body, int budget)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        var take = Math.Min(bytes.Length, budget);
        var truncated = bytes.Length > budget;
        var buffer = new byte[take];
        Array.Copy(bytes, buffer, take);
        return (string?)PrefixReader.Invoke(null, [buffer, take, truncated]);
    }

    [Fact]
    public void ReadsTheViewFromACompleteBody()
    {
        var body = "{\"files\":[\"a.ts\"],\"widget_html\":\"<div>x</div>\"}";
        Assert.Equal("<div>x</div>", FromPrefix(body, 1024));
    }

    [Fact]
    public void ReadsTheViewEvenWhenTheBodyWasCutOff()
    {
        // The view comes first and the cut lands in an unrelated later field.
        var body = "{\"widget_html\":\"<p>kept</p>\",\"tail\":\"" + new string('x', 500) + "\"}";
        Assert.Equal("<p>kept</p>", FromPrefix(body, 60));
    }

    [Fact]
    public void ReportsNoViewWhenTheCutLandedBeforeIt()
    {
        // The property is past the budget, so there is nothing to read. Either way the caller is
        // told "no view" rather than handed a partial parse.
        var body = "{\"filler\":\"" + new string('x', 500) + "\",\"widget_html\":\"<p>lost</p>\"}";
        Assert.Null(FromPrefix(body, 60));
    }

    [Fact]
    public void DoesNotInventAViewFromAnUnrelatedBody()
    {
        Assert.Null(FromPrefix("{\"files\":[\"a.ts\"]}", 1024));
        Assert.Null(FromPrefix("plain text", 1024));
        Assert.Null(FromPrefix("", 1024));
    }

    [Fact]
    public void IgnoresANestedViewThatBelongsToSomebodyElse()
    {
        Assert.Null(FromPrefix("{\"result\":{\"widget_html\":\"<p>inner</p>\"}}", 1024));
    }
}
