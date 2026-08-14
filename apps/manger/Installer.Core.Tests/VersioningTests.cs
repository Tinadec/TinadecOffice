using Installer.Core.Services;
using Xunit;

namespace Tinadec.Installer.Core.Tests;

public class SemanticVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.2", 1, 2, 0)]
    [InlineData("1", 1, 0, 0)]
    [InlineData("2.0.0-beta.1", 2, 0, 0)]
    public void Parse_extracts_core_segments(string input, int major, int minor, int patch)
    {
        var version = SemanticVersion.Parse(input);

        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
    }

    [Fact]
    public void Parse_keeps_pre_release_label()
    {
        var version = SemanticVersion.Parse("1.0.0-rc.2+build.5");

        Assert.Equal("rc.2", version.PreRelease);
        Assert.Equal("1.0.0-rc.2", version.ToString());
    }

    [Fact]
    public void Release_beats_pre_release()
    {
        var release = SemanticVersion.Parse("1.0.0");
        var pre = SemanticVersion.Parse("1.0.0-beta");

        Assert.True(release > pre);
        Assert.True(pre < release);
    }

    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0-beta")]      // alphabetical
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.2")] // numeric
    [InlineData("1.0.0-2", "1.0.0-10")]            // numeric not lexical
    public void Pre_release_follows_semver_ordering(string lower, string higher)
    {
        Assert.True(SemanticVersion.Parse(lower) < SemanticVersion.Parse(higher));
    }

    [Fact]
    public void Comparison_checks_major_then_minor_then_patch()
    {
        Assert.True(SemanticVersion.Parse("2.0.0") > SemanticVersion.Parse("1.9.9"));
        Assert.True(SemanticVersion.Parse("1.10.0") > SemanticVersion.Parse("1.9.0"));
        Assert.True(SemanticVersion.Parse("1.0.10") > SemanticVersion.Parse("1.0.9"));
        Assert.True(SemanticVersion.Parse("1.0.0") == SemanticVersion.Parse("1.0.0"));
    }
}

public class VersionRangeMatcherTests
{
    [Theory]
    [InlineData("1.5.0", ">=1.0.0", true)]
    [InlineData("0.9.0", ">=1.0.0", false)]
    [InlineData("1.0.0", "<=1.0.0", true)]
    [InlineData("1.0.1", "<=1.0.0", false)]
    [InlineData("1.2.3", "=1.2.3", true)]
    [InlineData("1.2.4", "=1.2.3", false)]
    [InlineData("1.0.0", "latest", true)]
    [InlineData("0.1.0", "*", true)]
    [InlineData("1.5.0", "1.0.0..2.0.0", true)]
    [InlineData("2.5.0", "1.0.0..2.0.0", false)]
    [InlineData("1.5.0", ">=1.0.0,<2.0.0", true)]
    [InlineData("2.5.0", ">=1.0.0,<2.0.0", false)]
    public void Satisfies_handles_supported_constraints(string version, string range, bool expected)
    {
        Assert.Equal(expected, VersionRangeMatcher.Satisfies(version, range));
    }

    [Fact]
    public void FindLatestCompatible_picks_highest_matching()
    {
        var available = new[] { "1.0.0", "1.4.2", "1.9.0", "2.0.0-rc.1" };

        var latest = VersionRangeMatcher.FindLatestCompatible(available, ">=1.0.0,<2.0.0");

        Assert.Equal("1.9.0", latest);
    }

    [Fact]
    public void FindLatestCompatible_returns_null_when_nothing_matches()
    {
        Assert.Null(VersionRangeMatcher.FindLatestCompatible(new[] { "1.0.0" }, ">=2.0.0"));
    }
}

public class AssetMapperTests
{
    [Theory]
    [InlineData("tinadec-core-1.0.0-win-x64.zip", "win", "x64", ".zip")]
    [InlineData("tinadec-core-1.0.0-linux-arm64.tar.gz", "linux", "arm64", ".tar.gz")]
    [InlineData("tinadec-core-1.0.0-osx-x64.tar.gz", "macos", "x64", ".tar.gz")]
    [InlineData("installer-1.0.0-win-arm64.zip", "win", "arm64", ".zip")]
    [InlineData("any-1.0.0.tar.gz", "any", "any", ".tar.gz")]
    public void ParseFileName_infers_platform_arch_extension(
        string fileName, string platform, string arch, string ext)
    {
        var (parsedPlatform, parsedArch, parsedExt) = AssetMapper.ParseFileName(fileName);

        Assert.Equal(platform, parsedPlatform);
        Assert.Equal(arch, parsedArch);
        Assert.Equal(ext, parsedExt);
    }
}
