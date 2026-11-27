using TinadecCore.DmaEA;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// The live stream projection sits between a run's durable writes and its SSE reader. These
/// cases pin the two properties that make it safe to put on that path: a reader never sees a
/// frame it would have to guess about, and anything the buffer cannot answer falls back to
/// replaying storage.
/// </summary>
public sealed class RunStreamProjectionTests
{
    private static RunStreamChunk Frame(Guid runId, long seq, string kind = "answer.delta", string? delta = "x") =>
        new(runId, Guid.Parse("11111111-1111-1111-1111-111111111111"), null, seq, kind, delta,
            OccurredAt: DateTimeOffset.UnixEpoch.AddSeconds(seq));

    [Fact]
    public void Since_ReturnsOnlyFramesPastTheCursor_InOrder()
    {
        var projection = new RunStreamProjection();
        var run = Guid.NewGuid();
        projection.Track(run);
        foreach (var seq in new long[] { 1, 2, 3 }) projection.Publish(Frame(run, seq));

        Assert.Equal([2L, 3L], projection.Since(run, 1).Select(chunk => chunk.Seq));
        Assert.Empty(projection.Since(run, 3));
    }

    [Fact]
    public void Publish_IsIdempotentForARetriedWrite()
    {
        // A retried append returns the row its idempotency key already created, so the same
        // sequence can reach the projection twice. A reader must not render it twice.
        var projection = new RunStreamProjection();
        var run = Guid.NewGuid();
        projection.Track(run);
        projection.Publish(Frame(run, 1));
        projection.Publish(Frame(run, 1));

        Assert.Single(projection.Since(run, 0));
    }

    [Fact]
    public void Covers_IsTrueOnlyWhileTheRetainedWindowReachesTheCursor()
    {
        var projection = new RunStreamProjection();
        var run = Guid.NewGuid();
        Assert.False(projection.Covers(run, 0), "an unknown run has no live window");

        projection.Track(run);
        // A tracked run with no frames must NOT claim to cover: it cannot prove storage holds
        // nothing earlier, and a follower trusting it would silently skip that history.
        Assert.False(projection.Covers(run, 0), "an empty buffer cannot answer a cursor by itself");

        projection.Publish(Frame(run, 5));
        Assert.True(projection.Covers(run, 4), "a cursor just before the first retained frame is covered");
        Assert.False(projection.Covers(run, 3), "a cursor before the retained window must replay storage");
    }

    [Fact]
    public void HasFramesPast_LetsAFollowerSwitchFromReplayToLive()
    {
        var projection = new RunStreamProjection();
        var run = Guid.NewGuid();
        projection.Track(run);
        Assert.False(projection.HasFramesPast(run, 0), "nothing published yet");

        projection.Publish(Frame(run, 1));
        Assert.True(projection.HasFramesPast(run, 0));
        Assert.False(projection.HasFramesPast(run, 1), "the cursor is already past that frame");
    }

    [Fact]
    public void Since_WithholdsEverythingWhenTheCursorPredatesTheWindow()
    {
        // Returning a partial tail would silently drop frames from the middle of an answer;
        // the caller replays storage instead. This is the assertion that guards that choice.
        var projection = new RunStreamProjection();
        var run = Guid.NewGuid();
        projection.Track(run);
        for (var seq = 10L; seq <= 12; seq++) projection.Publish(Frame(run, seq));

        Assert.Empty(projection.Since(run, 0));
        Assert.Equal([12L], projection.Since(run, 11).Select(chunk => chunk.Seq));
    }

    [Fact]
    public async Task WaitForFramesAsync_ReturnsAsSoonAsAFrameArrives()
    {
        var projection = new RunStreamProjection();
        var run = Guid.NewGuid();
        projection.Track(run);

        var waiting = projection.WaitForFramesAsync(run, 0, CancellationToken.None);
        projection.Publish(Frame(run, 1));

        Assert.True(await waiting);
    }

    [Fact]
    public async Task WaitForFramesAsync_ReturnsOnSeal_SoATerminalRunDoesNotWaitOutTheTimeout()
    {
        var projection = new RunStreamProjection();
        var run = Guid.NewGuid();
        projection.Track(run);

        var waiting = projection.WaitForFramesAsync(run, 0, CancellationToken.None);
        projection.Seal(run);

        Assert.True(await waiting);
    }

    [Fact]
    public async Task WaitForFramesAsync_GivesUpSoTheReaderCanFallBackToStorage()
    {
        // A run that stops producing without sealing (a stuck provider) must not park the HTTP
        // response: the wait is bounded and the caller replays the durable stream instead.
        var projection = new RunStreamProjection();
        var run = Guid.NewGuid();
        projection.Track(run);

        Assert.False(await projection.WaitForFramesAsync(run, 0, CancellationToken.None));
    }

    [Fact]
    public async Task WaitForFramesAsync_HonoursTheReadersCancellation()
    {
        var projection = new RunStreamProjection();
        var run = Guid.NewGuid();
        projection.Track(run);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => projection.WaitForFramesAsync(run, 0, cancelled.Token));
    }

    [Fact]
    public void Forget_DropsTheRun()
    {
        var projection = new RunStreamProjection();
        var run = Guid.NewGuid();
        projection.Track(run);
        projection.Publish(Frame(run, 1));
        projection.Forget(run);

        Assert.False(projection.Covers(run, 0));
        Assert.Empty(projection.Since(run, 0));
    }

    [Fact]
    public async Task WaitForFramesAsync_DoesNotSpinThroughPreDrainedPublishCounts()
    {
        var projection = new RunStreamProjection();
        var run = Guid.NewGuid();
        projection.Track(run);

        for (var seq = 1L; seq <= 25; seq++)
            projection.Publish(Frame(run, seq));

        var drained = projection.Since(run, 0);
        Assert.Equal(25, drained.Count);

        // If semaphore counts had accumulated across the 25 publishes, WaitForFramesAsync
        // would spin 25 times before observing no frames past 25. With broadcast TCS
        // signaling it must genuinely suspend until the next frame or timeout.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ready = await projection.WaitForFramesAsync(run, 25, CancellationToken.None);
        sw.Stop();

        Assert.False(ready);
        Assert.True(sw.ElapsedMilliseconds >= 150, $"Expected bounded wait ~250ms, finished too fast in {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Seal_WakesAllConcurrentWaitersSimultaneously()
    {
        var projection = new RunStreamProjection();
        var run = Guid.NewGuid();
        projection.Track(run);

        var wait1 = projection.WaitForFramesAsync(run, 0, CancellationToken.None);
        var wait2 = projection.WaitForFramesAsync(run, 0, CancellationToken.None);

        await Task.Delay(15);
        projection.Seal(run);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var results = await Task.WhenAll(wait1, wait2).WaitAsync(cts.Token);
        Assert.Equal([true, true], results);
        Assert.True(projection.IsSealed(run));
    }
}
