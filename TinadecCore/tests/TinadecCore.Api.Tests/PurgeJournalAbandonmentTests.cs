using TinadecCore.Runtime;

namespace TinadecCore.Api.Tests;

/// <summary>
/// A purge that dies on a configuration error must stop looking like "still pending": the journal is
/// renamed out of the "*.toml" enumeration, so the state honestly says the cleanup did not finish.
/// </summary>
public sealed class PurgeJournalAbandonmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-purge-journal-" + Guid.NewGuid().ToString("N"));

    public PurgeJournalAbandonmentTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void MarkJournalAbandoned_MovesTheJournalOutOfThePendingEnumeration()
    {
        var journal = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".toml");
        File.WriteAllText(journal, "[session]\n");

        var abandoned = ProjectSessionLifecycleService.MarkJournalAbandoned(journal);

        Assert.Equal(journal + ".abandoned", abandoned);
        Assert.False(File.Exists(journal));
        Assert.True(File.Exists(abandoned));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.toml"));
    }

    [Fact]
    public void MarkJournalAbandoned_KeepsTheEarlierEvidenceInsteadOfOverwritingIt()
    {
        var journal = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".toml");
        File.WriteAllText(journal, "[session]\n");
        var first = ProjectSessionLifecycleService.MarkJournalAbandoned(journal);

        File.WriteAllText(journal, "[session]\n");
        var second = ProjectSessionLifecycleService.MarkJournalAbandoned(journal);

        Assert.Equal(first + "2", second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }
}
