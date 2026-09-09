using NLog;
using NLog.Config;
using NLog.Targets;

namespace TinadecTools.Tests;

public sealed class NlogConfigTests
{
    [Fact]
    public void FileTarget_WritesToTempDirectory_NotWorkspace()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Nlog.config");
        Assert.True(File.Exists(path), "Nlog.config must be copied to the build output.");

        // throwConfigExceptions="true" in the file makes an unknown layout
        // renderer (e.g. a misspelled ${tempdir}) fail the load here.
        var config = new XmlLoggingConfiguration(path, new LogFactory());
        var fileTarget = config.AllTargets.OfType<FileTarget>().Single();

        var rendered = fileTarget.FileName.Render(LogEventInfo.CreateNullEvent());
        var temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // The child process CWD is the user's project root; a relative log path
        // would pollute the workspace (dirty git status). Temp must be the root.
        Assert.StartsWith(temp, rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"[\\/]tinadec-tools[\\/]", rendered);
    }
}
