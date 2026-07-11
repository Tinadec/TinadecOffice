namespace TinadecTools.Runtime.Sandbox;

internal sealed class UnsupportedSandboxBackend : ISandboxBackend
{
    public bool IsSupported => false;
    public bool IsInitialized => false;

    public Task EnsureSetupAsync(CancellationToken ct) =>
        throw new PlatformNotSupportedException("Sandbox is not supported on this platform.");

    public Task<SandboxRunnerResponse> ExecuteAsync(
        SandboxRunnerRequest request,
        SandboxPermissions permissions,
        bool persistGrants,
        CancellationToken ct) =>
        throw new PlatformNotSupportedException("Sandbox is not supported on this platform.");

    public Task ResetAsync(SandboxResetScope scope, CancellationToken ct) => Task.CompletedTask;
}