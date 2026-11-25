using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TinadecCore.AcpServer;

/// <summary>
/// CLI entrypoint for <c>TinadecCore.Api --acp</c>.
/// <para>
/// Enforces the 100% pure <c>stdout</c> invariant: raw <c>stdin</c> and <c>stdout</c> streams are captured
/// first, <see cref="Console.Out"/> is immediately redirected to <see cref="Console.Error"/>, no Kestrel
/// HTTP listener or startup banner is started, and <c>stdin</c> EOF / <c>SIGTERM</c> / <c>SIGINT</c>
/// gracefully cancels active turns and exits with code <c>0</c>.
/// </para>
/// </summary>
public static class AcpHostRunner
{
    public static async Task<int> RunFromCliAsync(string[] args)
    {
        await using var rawStdin = Console.OpenStandardInput();
        await using var rawStdout = Console.OpenStandardOutput();
        var originalOut = Console.Out;
        Console.SetOut(Console.Error);

        try
        {
            return await RunWithStreamsAsync(args, rawStdin, rawStdout, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await rawStdout.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException)
            {
            }

            Console.SetOut(originalOut);
        }
    }

    internal static async Task<int> RunWithStreamsAsync(
        string[] args,
        Stream rawStdin,
        Stream rawStdout,
        CancellationToken externalCancellationToken,
        Action<Microsoft.Extensions.Hosting.HostApplicationBuilder>? configureEmbeddedBuilder = null)
    {
        AcpCliOptions options;
        try
        {
            options = AcpCliOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync($"[tinadec-acp] Invalid CLI arguments: {ex.Message}").ConfigureAwait(false);
            return 2;
        }

        using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken);
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            try { shutdownCts.Cancel(); } catch (ObjectDisposedException) { }
        };
        Console.CancelKeyPress += cancelHandler;

        using var sigtermReg = TryRegisterPosixSignal(PosixSignal.SIGTERM, shutdownCts);
        using var sigintReg = TryRegisterPosixSignal(PosixSignal.SIGINT, shutdownCts);

        IAcpCoreBackend? backend = null;
        try
        {
            if (options.IsRemoteAttachMode)
            {
                backend = new RemoteHttpAcpCoreBackend(options.CoreUrl!, options.HostToken);
            }
            else
            {
                // Fail fast (< 5ms) if another standalone TinadecCore / --acp host holds host.lock,
                // while allowing `initialize` to respond immediately in parallel with SQLite migrations.
                EmbeddedAcpCoreBackend.VerifyUserStorageLockAvailable(options);
                backend = new LazyEmbeddedAcpCoreBackend(options, configureEmbeddedBuilder, shutdownCts.Token);
            }

            await using var transport = new StdioServerAcpTransport(rawStdin, rawStdout, leaveOpen: true);
            var server = new AcpAgentServer(
                transport,
                backend,
                logger: null,
                onDiagnostic: line => Console.Error.WriteLine($"[tinadec-acp] Ignored non-JSON stdin frame ({line.Length} chars)."));

            try
            {
                await server.RunAsync(shutdownCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdownCts.IsCancellationRequested)
            {
                // Graceful SIGTERM / CancelKeyPress / external cancellation.
            }

            await server.CancelAllActiveTurnsAsync(CancellationToken.None).ConfigureAwait(false);
            await rawStdout.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (shutdownCts.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[tinadec-acp] Fatal error: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            if (backend is not null)
            {
                try
                {
                    await backend.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync($"[tinadec-acp] Shutdown cleanup warning: {ex.Message}").ConfigureAwait(false);
                }
            }
        }
    }

    private static IDisposable? TryRegisterPosixSignal(PosixSignal signal, CancellationTokenSource cts)
    {
        try
        {
            return PosixSignalRegistration.Create(signal, context =>
            {
                context.Cancel = true;
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            });
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Starts <see cref="EmbeddedAcpCoreBackend"/> asynchronously in the background immediately upon process
    /// launch so <c>initialize</c> answers in milliseconds while SQLite migrations finish concurrently before
    /// <c>session/new</c> completes.
    /// </summary>
    private sealed class LazyEmbeddedAcpCoreBackend : IAcpCoreBackend
    {
        private readonly CancellationTokenSource _initCts;
        private readonly Task<EmbeddedAcpCoreBackend> _startupTask;
        private int _disposed;

        public LazyEmbeddedAcpCoreBackend(
            AcpCliOptions options,
            Action<Microsoft.Extensions.Hosting.HostApplicationBuilder>? configureBuilder,
            CancellationToken shutdownToken)
        {
            _initCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
            _startupTask = Task.Run(() => EmbeddedAcpCoreBackend.StartAsync(options, configureBuilder, _initCts.Token));
        }

        private Task<EmbeddedAcpCoreBackend> GetReadyAsync(CancellationToken ct) =>
            _startupTask.WaitAsync(ct);

        public async Task<AcpSessionSnapshot> CreateSessionAsync(string cwd, CancellationToken cancellationToken) =>
            await (await GetReadyAsync(cancellationToken).ConfigureAwait(false)).CreateSessionAsync(cwd, cancellationToken).ConfigureAwait(false);

        public async Task<AcpSessionSnapshot> LoadSessionAsync(Guid sessionId, string cwd, CancellationToken cancellationToken) =>
            await (await GetReadyAsync(cancellationToken).ConfigureAwait(false)).LoadSessionAsync(sessionId, cwd, cancellationToken).ConfigureAwait(false);

        public async Task<IReadOnlyList<AcpHistoryMessage>> ListHistoryAsync(Guid sessionId, CancellationToken cancellationToken) =>
            await (await GetReadyAsync(cancellationToken).ConfigureAwait(false)).ListHistoryAsync(sessionId, cancellationToken).ConfigureAwait(false);

        public async Task<AcpSessionSnapshot> SetModeAsync(Guid sessionId, string modeId, CancellationToken cancellationToken) =>
            await (await GetReadyAsync(cancellationToken).ConfigureAwait(false)).SetModeAsync(sessionId, modeId, cancellationToken).ConfigureAwait(false);

        public async Task<AcpSessionSnapshot> SetModelAsync(Guid sessionId, string modelId, CancellationToken cancellationToken) =>
            await (await GetReadyAsync(cancellationToken).ConfigureAwait(false)).SetModelAsync(sessionId, modelId, cancellationToken).ConfigureAwait(false);

        public async Task<Guid> StoreAttachmentAsync(Guid sessionId, string fileName, string mediaType, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
            await (await GetReadyAsync(cancellationToken).ConfigureAwait(false)).StoreAttachmentAsync(sessionId, fileName, mediaType, bytes, cancellationToken).ConfigureAwait(false);

        public async Task<AcpPromptSubmission> SubmitPromptAsync(Guid sessionId, string content, IReadOnlyList<Guid> attachmentIds, CancellationToken cancellationToken) =>
            await (await GetReadyAsync(cancellationToken).ConfigureAwait(false)).SubmitPromptAsync(sessionId, content, attachmentIds, cancellationToken).ConfigureAwait(false);

        public async IAsyncEnumerable<AcpRunStreamFrame> FollowRunStreamAsync(
            Guid sessionId,
            Guid runId,
            Guid? turnId,
            long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var inner = await GetReadyAsync(cancellationToken).ConfigureAwait(false);
            await foreach (var item in inner.FollowRunStreamAsync(sessionId, runId, turnId, afterSequence, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }

        public async IAsyncEnumerable<AcpBackendEvent> SubscribeSessionEventsAsync(
            Guid sessionId,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var inner = await GetReadyAsync(cancellationToken).ConfigureAwait(false);
            await foreach (var ev in inner.SubscribeSessionEventsAsync(sessionId, cancellationToken).ConfigureAwait(false))
            {
                yield return ev;
            }
        }

        public async Task<AcpRunStatusSnapshot?> GetRunStatusAsync(Guid sessionId, Guid runId, CancellationToken cancellationToken) =>
            await (await GetReadyAsync(cancellationToken).ConfigureAwait(false)).GetRunStatusAsync(sessionId, runId, cancellationToken).ConfigureAwait(false);

        public async Task<AcpToolExecutionDetail?> GetToolExecutionDetailAsync(Guid sessionId, Guid runId, Guid executionId, CancellationToken cancellationToken) =>
            await (await GetReadyAsync(cancellationToken).ConfigureAwait(false)).GetToolExecutionDetailAsync(sessionId, runId, executionId, cancellationToken).ConfigureAwait(false);

        public async Task<IReadOnlyList<AcpPendingApproval>> ListPendingApprovalsAsync(Guid sessionId, Guid runId, CancellationToken cancellationToken) =>
            await (await GetReadyAsync(cancellationToken).ConfigureAwait(false)).ListPendingApprovalsAsync(sessionId, runId, cancellationToken).ConfigureAwait(false);

        public async Task<AcpPendingApproval?> GetApprovalAsync(Guid sessionId, Guid approvalId, CancellationToken cancellationToken) =>
            await (await GetReadyAsync(cancellationToken).ConfigureAwait(false)).GetApprovalAsync(sessionId, approvalId, cancellationToken).ConfigureAwait(false);

        public async Task<AcpApprovalDecisionOutcome> DecideApprovalAsync(Guid sessionId, Guid approvalId, AcpApprovalDecisionInput input, CancellationToken cancellationToken) =>
            await (await GetReadyAsync(cancellationToken).ConfigureAwait(false)).DecideApprovalAsync(sessionId, approvalId, input, cancellationToken).ConfigureAwait(false);

        public async Task ResumeAwaitingUserSupervisionAsync(Guid sessionId, Guid runId, CancellationToken cancellationToken) =>
            await (await GetReadyAsync(cancellationToken).ConfigureAwait(false)).ResumeAwaitingUserSupervisionAsync(sessionId, runId, cancellationToken).ConfigureAwait(false);

        public async Task CancelRunAsync(Guid sessionId, Guid runId, CancellationToken cancellationToken) =>
            await (await GetReadyAsync(cancellationToken).ConfigureAwait(false)).CancelRunAsync(sessionId, runId, cancellationToken).ConfigureAwait(false);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _initCts.Cancel(); } catch { }
            try
            {
                var inner = await _startupTask.ConfigureAwait(false);
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Startup was cancelled or failed before completion.
            }
            finally
            {
                _initCts.Dispose();
            }
        }
    }
}
