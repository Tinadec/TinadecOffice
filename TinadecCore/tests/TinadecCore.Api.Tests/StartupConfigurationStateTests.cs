using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Startup must record a configuration failure instead of aborting, so the API can report it.
/// These assertions pin the two halves of that contract: what counts as recoverable, and what a
/// recorded failure looks like once /api/v1/readiness renders it.
/// </summary>
public sealed class StartupConfigurationStateTests
{
    [Fact]
    public void OnlyConfigurationDocumentFailuresAreRecoverable()
    {
        Assert.True(StartupConfigurationState.IsRecoverable(new ConfigurationDocumentException("configuration_invalid", "broken", [])));
        Assert.False(StartupConfigurationState.IsRecoverable(new InvalidOperationException("database is gone")));
        Assert.False(StartupConfigurationState.IsRecoverable(new IOException("disk full")));
    }

    [Fact]
    public void RecordingKeepsTheCodeMessageAndDiagnostics()
    {
        var state = new StartupConfigurationState();
        Assert.False(state.IsBroken);

        state.Record("projection", new ConfigurationDocumentException("configuration_invalid",
            "Configuration document 'agents' is invalid.",
            [new("configuration_schema", "agent_versions.snapshot_json: value exceeds the supported length.")]));

        Assert.True(state.IsBroken);
        Assert.Equal("configuration_invalid", state.Diagnostics[0].Code);
        Assert.Contains("[projection]", state.Diagnostics[0].Message);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Message.Contains("snapshot_json"));

        state.Record("storage", new ConfigurationDocumentException("configuration_missing", "Configuration file 'tools.toml' is missing.", []));
        // Each stage contributes its own marker row; the document diagnostics follow it.
        Assert.Equal(3, state.Diagnostics.Count);
        Assert.Equal("configuration_missing", state.Diagnostics[^1].Code);
    }
}
