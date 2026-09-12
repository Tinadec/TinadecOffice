using TinadecCore.Abstractions.Ports;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// Projectless sentinel round-trip contract: the wire level (Guid.Empty) and the
/// durable level (NULL) must translate through CoreVirtualToolPolicy's helper pair
/// without loss, for every Guid. A regression here silently breaks projectless
/// admission (comparison never matches) or idempotency (mismatched stored ids), so
/// the round-trip is pinned as a property, not as spot examples.
/// </summary>
public sealed class ProjectlessSentinelRoundTripTests
{
    [Fact]
    public void WireSentinel_NormalizesToDurableNull_AndRealIdsPassThrough()
    {
        Assert.Null(CoreVirtualToolPolicy.FromWireSentinel(Guid.Empty));
        var real = Guid.NewGuid();
        Assert.Equal(real, CoreVirtualToolPolicy.FromWireSentinel(real));
    }

    [Fact]
    public void WireSentinel_NullDurableRoundTripsToSentinel_AndRealIdsPassThrough()
    {
        Assert.Equal(Guid.Empty, CoreVirtualToolPolicy.ToWireSentinel(null));
        var real = Guid.NewGuid();
        Assert.Equal(real, CoreVirtualToolPolicy.ToWireSentinel(real));
    }

    [Fact]
    public void RoundTrip_PreservesEveryGuid()
    {
        // deterministic pseudo-random sweep: 1_000 generated ids + the sentinel itself
        var random = new Random(20260912);
        for (var i = 0; i < 1_000; i++)
        {
            var bytes = new byte[16];
            random.NextBytes(bytes);
            var value = new Guid(bytes);

            // wire → durable → wire must be the identity for every input
            var durable = CoreVirtualToolPolicy.FromWireSentinel(value);
            Assert.Equal(CoreVirtualToolPolicy.IsProjectlessScope(value) ? null : value, durable);
            Assert.Equal(value, CoreVirtualToolPolicy.ToWireSentinel(durable));

            // durable → wire → durable must be the identity for every stored row
            var wire = CoreVirtualToolPolicy.ToWireSentinel(durable);
            Assert.Equal(durable, CoreVirtualToolPolicy.FromWireSentinel(wire));
        }

        // the sentinel itself, explicitly
        Assert.Null(CoreVirtualToolPolicy.FromWireSentinel(CoreVirtualToolPolicy.ToWireSentinel(null)));
        Assert.Equal(Guid.Empty, CoreVirtualToolPolicy.ToWireSentinel(CoreVirtualToolPolicy.FromWireSentinel(Guid.Empty)));
    }
}
