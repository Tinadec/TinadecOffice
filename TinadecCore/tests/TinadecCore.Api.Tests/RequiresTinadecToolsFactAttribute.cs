namespace TinadecCore.Api.Tests;

/// <summary>
/// Marks a test that drives the real TinadecTools apphost child process.
/// The apphost is copied next to the test output by an optional project
/// reference that only exists in the monorepo layout; when the binary is
/// absent (e.g. a Core-only showcase mirror) the test skips itself instead
/// of failing.
/// </summary>
public sealed class RequiresTinadecToolsFactAttribute : FactAttribute
{
    public RequiresTinadecToolsFactAttribute()
    {
        var executable = Path.Combine(
            AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "TinadecTools.exe" : "TinadecTools");

        if (!File.Exists(executable))
            Skip = "Requires the TinadecTools apphost binary, which is not part of a Core-only checkout.";
    }
}
