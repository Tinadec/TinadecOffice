using System.Collections.Generic;

namespace TinadecTools.Runtime.Sandbox;

internal static class SandboxEnvironment
{
    private static readonly HashSet<string> RetainFromHost = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "PATHEXT", "SystemRoot", "TEMP", "TMP",
        "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE",
        "OS", "ComSpec", "windir",
        "LANG", "LC_ALL", "LC_CTYPE",
        "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
    };

    internal static Dictionary<string, string> Build(
        IReadOnlyDictionary<string, string>? sandboxAccountDirs,
        IEnumerable<string>? extraEnvVarNames)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in RetainFromHost)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
                env[name] = value;
        }

        if (sandboxAccountDirs is not null)
        {
            if (sandboxAccountDirs.TryGetValue("Profile", out var profile) && !string.IsNullOrEmpty(profile))
            {
                env["USERPROFILE"] = profile;
                env["APPDATA"] = Path.Combine(profile, "AppData", "Roaming");
                env["LOCALAPPDATA"] = Path.Combine(profile, "AppData", "Local");
                env["TEMP"] = Path.Combine(profile, "AppData", "Local", "Temp");
                env["TMP"] = Path.Combine(profile, "AppData", "Local", "Temp");
            }

            if (sandboxAccountDirs.TryGetValue("Cache", out var cache) && !string.IsNullOrEmpty(cache))
            {
                env["NPM_CONFIG_CACHE"] = Path.Combine(cache, "npm");
                env["NUGET_PACKAGES"] = Path.Combine(cache, "nuget");
                env["PIP_CACHE_DIR"] = Path.Combine(cache, "pip");
                env["CARGO_HOME"] = Path.Combine(cache, "cargo");
            }
        }

        if (extraEnvVarNames is not null)
        {
            foreach (var name in extraEnvVarNames)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(name);
                var value = Environment.GetEnvironmentVariable(name);
                if (value is not null)
                    env[name] = value;
            }
        }

        return env;
    }

    internal static bool IsEnvironmentVariableNameValid(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.IndexOf('=') < 0 && name.IndexOf('\0') < 0;
    }
}
