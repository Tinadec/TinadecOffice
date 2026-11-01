using TinadecTools.Tools.Browser;

namespace TinadecTools.Tests;

public sealed class BrowserNavigationPolicyTests
{
    private static bool Allowed(string url, bool allowPrivate = false) =>
        new BrowserNavigationPolicy(allowPrivate).TryNormalize(url, out _, out _);

    [Theory]
    [InlineData("https://example.com/docs")]
    [InlineData("http://example.com/")]
    [InlineData("http://127.0.0.1:5173/")]
    [InlineData("http://localhost:5173/")]
    [InlineData("http://[::1]:5173/")]
    public void AllowsHttpAndHttpsIncludingLoopback(string url)
    {
        Assert.True(Allowed(url), url);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("data:text/html,<b>x</b>")]
    [InlineData("devtools://devtools/bundled/inspector.html")]
    [InlineData("http://user:pass@example.com/")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://metadata.google.internal/computeMetadata/v1/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://224.0.0.1/")]
    public void RefusesUnsafeOrMetadataTargets(string url)
    {
        Assert.False(Allowed(url), url);
    }

    [Theory]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://100.64.0.1/")]
    public void RefusesPrivateNetworksByDefault(string url)
    {
        Assert.False(Allowed(url), url);
        Assert.True(Allowed(url, allowPrivate: true), url);
    }

    [Fact]
    public void RefusesLinkLocalAndPrivateIpv6()
    {
        Assert.False(Allowed("http://[fe80::1]/"));
        Assert.False(Allowed("http://[fd00::1]/"));
        Assert.True(Allowed("http://[fd00::1]/", allowPrivate: true));
    }

    [Fact]
    public void ReportsAReason()
    {
        var policy = new BrowserNavigationPolicy();
        Assert.False(policy.TryNormalize("javascript:alert(1)", out _, out var scheme));
        Assert.Contains("http", scheme);
        Assert.False(policy.TryNormalize("http://10.0.0.1/", out _, out var privateError));
        Assert.Contains("private", privateError);
    }
}
