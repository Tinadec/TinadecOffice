using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Architecture.Tests;

public sealed class AbstractionsCompatibilityTests
{
    [Fact]
    public void LeaseAndExplicitModeExtensions_DoNotMakeLegacyImplementersAbstract()
    {
        Assert.False(Method(typeof(ILifecycleManager), nameof(ILifecycleManager.TryClaimRunCompletionAsync)).IsAbstract);
        Assert.False(Method(typeof(ILifecycleManager), nameof(ILifecycleManager.SetRunStatusUnderLeaseAsync)).IsAbstract);
        Assert.False(Method(typeof(ILifecycleManager), nameof(ILifecycleManager.SetRunFailedUnderLeaseAsync)).IsAbstract);
        Assert.False(Method(typeof(IFormalModeResolver), nameof(IFormalModeResolver.GetEffectiveToolsForModeAsync)).IsAbstract);
        Assert.False(Method(typeof(IFormalModeResolver), nameof(IFormalModeResolver.ResolveRosterForModeAsync)).IsAbstract);
    }

    [Fact]
    public void ExtendedRecords_PreserveTheirLegacyConstructorSignatures()
    {
        Assert.NotNull(typeof(ToolManifestSnapshotRequest).GetConstructor(
        [
            typeof(Guid),
            typeof(IReadOnlyList<string>),
            typeof(bool),
            typeof(IReadOnlyList<string>)
        ]));

        Assert.NotNull(typeof(RunCheckpointWrite).GetConstructor(
        [
            typeof(long),
            typeof(string),
            typeof(string),
            typeof(long),
            typeof(string)
        ]));
    }

    private static System.Reflection.MethodInfo Method(Type type, string name) =>
        type.GetMethod(name) ?? throw new InvalidOperationException($"Expected {type.Name}.{name} to exist.");
}
