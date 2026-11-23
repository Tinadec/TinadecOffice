using System.Text.Json;
using TinadecCore.Persistence;

namespace TinadecCore.Api.Tests;

/// <summary>
/// storage.toml / logging.toml are read before the HTTP host exists, so a failure there can never be
/// reported by /api/v1/readiness. These receipts are the only channel left for the desktop host.
/// </summary>
public sealed class StartupFailureReceiptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-startup-receipt-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Write_NamesTheDocumentAndCodeWithoutContent()
    {
        StartupFailureReceipt.Write(_root, "configuration_missing", "storage",
            "Configuration document 'storage' is missing; restore it to continue.");

        var path = StartupFailureReceipt.PathFor(_root);
        Assert.True(File.Exists(path));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("configuration_missing", document.RootElement.GetProperty("code").GetString());
        Assert.Equal("storage", document.RootElement.GetProperty("document").GetString());
        Assert.Contains("restore it", document.RootElement.GetProperty("message").GetString());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("at").GetString()));
        // One receipt, no sibling temp file left behind: the desktop host reads exactly this path.
        Assert.Single(Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void Write_ReplacesThePreviousReceipt()
    {
        StartupFailureReceipt.Write(_root, "configuration_invalid", "logging", "first");
        StartupFailureReceipt.Write(_root, "configuration_missing", "storage", "second");

        using var document = JsonDocument.Parse(File.ReadAllText(StartupFailureReceipt.PathFor(_root)));
        Assert.Equal("configuration_missing", document.RootElement.GetProperty("code").GetString());
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Clear_RemovesTheReceiptAndToleratesAnAbsentOne()
    {
        StartupFailureReceipt.Clear(_root); // nothing written yet must not throw
        StartupFailureReceipt.Write(_root, "configuration_invalid", "storage", "broken");
        StartupFailureReceipt.Clear(_root);
        Assert.False(File.Exists(StartupFailureReceipt.PathFor(_root)));
        StartupFailureReceipt.Clear(_root);
    }
}
