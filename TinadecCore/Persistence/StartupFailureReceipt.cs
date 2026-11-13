using System.Text.Json;

namespace TinadecCore.Persistence;

/// <summary>
/// A small receipt written when the host cannot finish bootstrap.
///
/// <c>storage.toml</c> and <c>logging.toml</c> are read before the HTTP host exists, and without a
/// readable storage document there is no database location to fall back to — so that stage still
/// fails fast instead of guessing a path that may point at the wrong data. The consequence is that
/// <c>/api/v1/readiness</c> cannot explain the failure (the process never binds a port) and the only
/// thing a user saw was "the local services are temporarily unavailable".
///
/// The desktop host therefore reads this file after the child exits and names the broken document.
/// It carries codes, the document id and the message only: no document contents and no secrets.
/// </summary>
public static class StartupFailureReceipt
{
    public const string RelativePath = "state/startup-failure.json";

    public static string PathFor(string userRoot) => Path.Combine(userRoot, "state", "startup-failure.json");

    public static void Write(string userRoot, string code, string document, string message)
    {
        var path = PathFor(userRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = JsonSerializer.Serialize(new
        {
            code,
            document,
            message,
            at = DateTimeOffset.UtcNow.ToString("O")
        }, new JsonSerializerOptions { WriteIndented = true });
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, payload);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Removes a receipt left by an earlier failed run; a healthy start must not keep stale text.</summary>
    public static void Clear(string userRoot)
    {
        try
        {
            File.Delete(PathFor(userRoot));
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was ever written.
        }
    }
}
