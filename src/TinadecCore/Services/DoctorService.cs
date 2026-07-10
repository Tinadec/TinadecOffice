using System.Diagnostics;
using System.Reflection;
using Tinadec.Contracts.Models;

namespace TinadecCore.Services;

public sealed class DoctorService
{
    public DoctorReportDto Check()
    {
        var checks = new List<DoctorCheckDto>
        {
            Probe("git", "--version", "Git is needed for diff and repository workflows."),
            Probe("dotnet", "--version", ".NET is needed to run Core."),
            Probe("node", "--version", "Node is needed for the gateway and desktop shell.")
        };

        return new DoctorReportDto(
            $"{Environment.OSVersion.Platform} {Environment.OSVersion.Version}",
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "dev",
            checks);
    }

    private static DoctorCheckDto Probe(string fileName, string arguments, string missingMessage)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null) return new DoctorCheckDto(fileName, "missing", missingMessage);

            process.WaitForExit(3000);
            var output = process.StandardOutput.ReadToEnd().Trim();

            return process.ExitCode == 0
                ? new DoctorCheckDto(fileName, "ok", output)
                : new DoctorCheckDto(fileName, "error", process.StandardError.ReadToEnd().Trim());
        }
        catch (Exception ex)
        {
            return new DoctorCheckDto(fileName, "missing", $"{missingMessage} ({ex.Message})");
        }
    }
}
