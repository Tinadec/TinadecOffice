using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;
using Tomlyn;
using Tomlyn.Model;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The receipt filter has to cover what a broken document actually throws.
///
/// <c>storage.toml</c> is read by <c>TomlSerializer.Deserialize</c> out of a plain file read, so a
/// malformed file surfaces as <c>Tomlyn.TomlException</c> — not as
/// <c>ConfigurationDocumentException</c>, which is what validated documents throw. A filter that
/// only names the latter leaves the most common corruption with no receipt, and the desktop is back
/// to "the local services are temporarily unavailable".
/// </summary>
public sealed class BootstrapFailureTests
{
    [Fact]
    public void AMalformedTomlDocumentIsExplainedByTheReceipt()
    {
        var error = ParseFailure("storage = [");

        Assert.IsType<TomlException>(error);
        Assert.True(BootstrapFailure.IsConfigurationFailure(error));
        Assert.Equal("configuration_invalid", BootstrapFailure.DocumentCode(error));
    }

    [Fact]
    public void AValidatedDocumentKeepsItsOwnReasonCode()
    {
        var error = new ConfigurationDocumentException("configuration_missing", "Establish the file again.");

        Assert.True(BootstrapFailure.IsConfigurationFailure(error));
        Assert.Equal("configuration_missing", BootstrapFailure.DocumentCode(error));
    }

    [Theory]
    [InlineData(typeof(InvalidDataException))]
    [InlineData(typeof(FormatException))]
    [InlineData(typeof(OverflowException))]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(JsonException))]
    public void ValuesTheSchemaCannotUseAreExplained(Type type)
    {
        var error = (Exception)Activator.CreateInstance(type, "unusable value")!;

        Assert.True(BootstrapFailure.IsConfigurationFailure(error));
        Assert.Equal("configuration_invalid", BootstrapFailure.DocumentCode(error));
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(OperationCanceledException))]
    public void EnvironmentFailuresStayOutsideTheReceipt(Type type)
    {
        var error = (Exception)Activator.CreateInstance(type, "disk or lifecycle failure")!;

        Assert.False(BootstrapFailure.IsConfigurationFailure(error));
    }

    private static Exception ParseFailure(string text)
    {
        try { TomlSerializer.Deserialize<TomlTable>(text); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("the fixture is supposed to fail to parse: " + text);
    }
}
