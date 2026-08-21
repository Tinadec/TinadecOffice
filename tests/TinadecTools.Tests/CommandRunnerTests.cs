using TinadecTools.Runtime.Sandbox;
using TinadecTools.Tools.Command;

namespace TinadecTools.Tests;

[Collection("CommandSandbox")]
public sealed class CommandRunnerTests
{
    [Fact]
    public async Task HandleAsync_ForwardsApprovedCommandToSandbox()
    {
        var backend = new FakeSandboxBackend
        {
            Result = new SandboxRunnerResponse
            {
                Success = true,
                ExitCode = 0,
                Stdout = "sandbox output",
                DurationMs = 12
            }
        };
        using var _ = CommandSandboxRuntime.OverrideBackendForTests(backend);

        var response = await CommandRunner.HandleAsync(new CommandRunParams
        {
            Executable = "dotnet",
            Arguments = ["--version"],
            Stdin = "input",
            TimeoutMs = 1_000
        }, CancellationToken.None);

        Assert.True(backend.SetupRequested);
        Assert.Equal("dotnet", backend.Request?.Executable);
        Assert.Equal(["--version"], backend.Request?.Arguments);
        Assert.Equal("input", backend.Request?.Stdin);
        Assert.True(response.Success);
        Assert.Equal("sandbox output", response.Stdout);
    }

    [Fact]
    public async Task HandleAsync_InvalidTimeout_ReturnsSandboxErrorWithoutExecution()
    {
        var backend = new FakeSandboxBackend();
        using var _ = CommandSandboxRuntime.OverrideBackendForTests(backend);

        var response = await CommandRunner.HandleAsync(new CommandRunParams
        {
            Executable = "dotnet",
            TimeoutMs = 1_800_001
        }, CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("timeout_ms", response.SandboxError, StringComparison.OrdinalIgnoreCase);
        Assert.False(backend.SetupRequested);
    }

    [Fact]
    public async Task HandleAsync_ReportsSandboxOutputTruncation()
    {
        var backend = new FakeSandboxBackend
        {
            Result = new SandboxRunnerResponse
            {
                Success = true,
                StdoutTruncated = true,
                StderrTruncated = true
            }
        };
        using var _ = CommandSandboxRuntime.OverrideBackendForTests(backend);

        var response = await CommandRunner.HandleAsync(new CommandRunParams
        {
            Executable = "dotnet",
            TimeoutMs = 1_000
        }, CancellationToken.None);

        Assert.True(response.StdoutTruncated);
        Assert.True(response.StderrTruncated);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task HandleAsync_RejectsEmptyExecutableBeforeSandboxSetup(string executable)
    {
        var backend = new FakeSandboxBackend();
        using var _ = CommandSandboxRuntime.OverrideBackendForTests(backend);

        var response = await CommandRunner.HandleAsync(new CommandRunParams
        {
            Executable = executable,
            Arguments = []
        }, CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("executable", response.SandboxError, StringComparison.OrdinalIgnoreCase);
        Assert.False(backend.SetupRequested);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1_800_000)]
    public async Task HandleAsync_AcceptsTimeoutBoundaries(int timeoutMs)
    {
        var backend = new FakeSandboxBackend
        {
            Result = new SandboxRunnerResponse { Success = true }
        };
        using var _ = CommandSandboxRuntime.OverrideBackendForTests(backend);

        var response = await CommandRunner.HandleAsync(new CommandRunParams
        {
            Executable = "dotnet",
            TimeoutMs = timeoutMs
        }, CancellationToken.None);

        Assert.True(response.Success);
        Assert.Equal(timeoutMs, backend.Request?.TimeoutMs);
    }

    [Fact]
    public async Task HandleAsync_RejectsNullArgumentsBeforeSandboxSetup()
    {
        var backend = new FakeSandboxBackend();
        using var _ = CommandSandboxRuntime.OverrideBackendForTests(backend);

        var response = await CommandRunner.HandleAsync(new CommandRunParams
        {
            Executable = "dotnet",
            Arguments = null!
        }, CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("arguments", response.SandboxError, StringComparison.OrdinalIgnoreCase);
        Assert.False(backend.SetupRequested);
    }

    [Fact]
    public async Task HandleAsync_PreservesArgumentsAsIndependentValues()
    {
        var backend = new FakeSandboxBackend
        {
            Result = new SandboxRunnerResponse { Success = true }
        };
        using var _ = CommandSandboxRuntime.OverrideBackendForTests(backend);

        var response = await CommandRunner.HandleAsync(new CommandRunParams
        {
            Executable = "git",
            Arguments = ["value with spaces", "quote\"value", "semi;colon", "amp&value"]
        }, CancellationToken.None);

        Assert.True(response.Success);
        Assert.Equal(["value with spaces", "quote\"value", "semi;colon", "amp&value"], backend.Request?.Arguments);
    }

    [Fact]
    public async Task HandleAsync_RejectsInvalidEnvironmentNameBeforeSandboxSetup()
    {
        var backend = new FakeSandboxBackend();
        using var _ = CommandSandboxRuntime.OverrideBackendForTests(backend);

        var response = await CommandRunner.HandleAsync(new CommandRunParams
        {
            Executable = "dotnet",
            EnvironmentVariableNames = ["BAD=NAME"]
        }, CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("environment", response.SandboxError, StringComparison.OrdinalIgnoreCase);
        Assert.False(backend.SetupRequested);
    }

    private sealed class FakeSandboxBackend : ISandboxBackend
    {
        public bool IsSupported => true;
        public bool IsInitialized => true;
        public bool SetupRequested { get; private set; }
        public SandboxRunnerRequest? Request { get; private set; }
        public SandboxRunnerResponse Result { get; init; } = new();

        public Task EnsureSetupAsync(CancellationToken ct)
        {
            SetupRequested = true;
            return Task.CompletedTask;
        }

        public Task<SandboxRunnerResponse> ExecuteAsync(
            SandboxRunnerRequest request,
            SandboxPermissions permissions,
            bool persistGrants,
            CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(Result);
        }

        public Task ResetAsync(SandboxResetScope scope, CancellationToken ct) => Task.CompletedTask;
    }
}

[CollectionDefinition("CommandSandbox", DisableParallelization = true)]
public sealed class CommandSandboxCollection;
