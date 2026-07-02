using TinadecTools.Tools.FileRW;

namespace TinadecTools.Tests;

public sealed class FileHashingTests
{
    [Fact]
    public void ComputeLineHash_UsesContentSeedForSignificantLines()
    {
        var first = FileHashing.ComputeLineHash("function hello() {", 1);
        var second = FileHashing.ComputeLineHash("function hello() {", 42);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ComputeLineHash_UsesLineSeedForNonSignificantLines()
    {
        var first = FileHashing.ComputeLineHash("{}", 1);
        var second = FileHashing.ComputeLineHash("{}", 2);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ComputeLineHash_IgnoresTrailingWhitespaceAndCarriageReturns()
    {
        var first = FileHashing.ComputeLineHash("return value", 1);
        var second = FileHashing.ComputeLineHash("return value  \r", 1);

        Assert.Equal(first, second);
    }
}
