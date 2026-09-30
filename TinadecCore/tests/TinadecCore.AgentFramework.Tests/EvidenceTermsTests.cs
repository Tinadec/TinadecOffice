using TinadecCore.Abstractions.Ports;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>The keyword half of evidence recall: what counts as a term in text a model or a person wrote.</summary>
public sealed class EvidenceTermsTests
{
    [Fact]
    public void LatinWordsAreLowerCased_AndPathsAndHandlesStayWhole()
    {
        Assert.Equal(["search#1", "wrote", "docs/a.md"], EvidenceTerms.Extract("search#1 wrote docs/a.md."));
        Assert.Equal(["run", "the", "migration"], EvidenceTerms.Extract("Run THE migration!"));
    }

    [Fact]
    public void ChineseBecomesBigrams_AndASingleCharacterStandsAlone()
    {
        Assert.Equal(["主机", "机占", "占用"], EvidenceTerms.Extract("主机占用"));
        Assert.Equal(["改", "docs"], EvidenceTerms.Extract("改 docs"));
        Assert.Equal(["docs", "被占", "占用"], EvidenceTerms.Extract("docs被占用"));
    }

    [Fact]
    public void NoiseIsDropped_AndTermsAreBounded()
    {
        Assert.Empty(EvidenceTerms.Extract("  , . ! a "));
        var many = string.Join(' ', Enumerable.Range(0, 40).Select(index => $"word{index}"));
        Assert.Equal(EvidenceTerms.MaxTerms, EvidenceTerms.Extract(many).Count);
        Assert.Single(EvidenceTerms.Extract("again again AGAIN"));
    }
}
