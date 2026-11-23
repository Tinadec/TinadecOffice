using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Persistence;

/// <summary>
/// Decides which bootstrap failures the desktop host can explain, so <c>storage.toml</c> /
/// <c>logging.toml</c> failures still leave a receipt naming the file.
///
/// The receipt exists because these documents are read before the HTTP host exists: a failure
/// there can never reach <c>/api/v1/readiness</c>, and the only thing the window used to say was
/// "the local services are temporarily unavailable". The filter must therefore cover what a broken
/// document actually throws — a malformed file surfaces as <c>Tomlyn.TomlException</c> straight out
/// of <c>TomlSerializer.Deserialize</c>, not as <c>ConfigurationDocumentException</c>, and a value
/// the schema cannot use surfaces as <c>FormatException</c> / <c>ArgumentException</c> /
/// <c>OverflowException</c> / <c>JsonException</c>.
///
/// Matching is only ever used to annotate the failure: every caller rethrows, so a list that is too
/// wide costs a misleading receipt, never a swallowed startup error.
/// </summary>
public static class BootstrapFailure
{
    public static bool IsConfigurationFailure(Exception error) =>
        error is ConfigurationDocumentException
            or InvalidDataException
            or FormatException
            or OverflowException
            or ArgumentException
            or Tomlyn.TomlException
            or System.Text.Json.JsonException;

    /// <summary>The receipt's reason code: the document's own code when it has one.</summary>
    public static string DocumentCode(Exception error) =>
        error is ConfigurationDocumentException document ? document.Code : "configuration_invalid";
}
