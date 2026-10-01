using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Api.Tests;

public sealed partial class FullDuplexEndpointTests
{
    [Fact]
    public async Task Supervision_DecisionEvidenceIsDurableBeforeTheWaitingStatusBecomesVisible()
    {
        var script = new ScriptedChatClient()
            .WhenPlanner("[{\"title\":\"Review evidence\",\"success_criteria\":[\"complete\"],\"dependencies\":[],\"risk\":\"low\"}]")
            .WhenWorker("Done")
            .WhenSupervisor("{\"decision\":\"escalate\",\"reasons\":[\"Needs confirmation\"],\"revise_task_indexes\":[]}")
            .WhenMeeting("Confirmed");
        var factory = CreateFactory(script);
        WaitingStatusBarrier? barrier = null;
        factory.ConfigureTestServices = services =>
        {
            var descriptor = services.Last(item => item.ServiceType == typeof(ILifecycleManager));
            services.Remove(descriptor);
            services.AddSingleton<ILifecycleManager>(provider =>
            {
                var inner = (ILifecycleManager)(descriptor.ImplementationInstance
                    ?? descriptor.ImplementationFactory?.Invoke(provider)
                    ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!));
                var proxy = DispatchProxy.Create<ILifecycleManager, WaitingStatusBarrier>();
                barrier = (WaitingStatusBarrier)(object)proxy;
                barrier.Inner = inner;
                return proxy;
            });
        };
        var client = factory.CreateClient();
        var sessionId = await CreateSessionAsync(client);
        var active = StartStreamingInvoke(client, sessionId, new { content = "Review" });
        var runId = RunIdOf(await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.NotNull(barrier);
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var events = await barrier.Inner.ReplayEventsAsync(sessionId, 0);
            var review = Assert.Single(events, item => item.EventType == "supervision.user_review.requested");
            Assert.Contains("Needs confirmation", review.Payload["payload"]!.ToString());
            Assert.Equal(RunStatus.Reviewing, (await barrier.Inner.GetRunStateAsync(runId.ToString())).Status);
        }
        finally { barrier.Release.TrySetResult(); }
        await barrier.Written.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RunStatus.AwaitingUser, (await barrier.Inner.GetRunStateAsync(runId.ToString())).Status);
        var resumed = await client.PostAsJsonAsync($"/api/v1/runs/{runId}/control", new { action = "resume" });
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        Assert.Contains(await active.Completion.WaitAsync(TimeSpan.FromSeconds(15)), chunk => KindOf(chunk) == "done");
    }

    // Decorates the real lifecycle writer; only the publication boundary is held.
    // Old status-before-event code deterministically fails the evidence assertion.
    public class WaitingStatusBarrier : DispatchProxy
    {
        public ILifecycleManager Inner { get; set; } = null!;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(ILifecycleManager.SetRunStatusUnderLeaseAsync)
                && args?[1] is "awaiting_user") return WaitAndWriteAsync(method, args);
            return method.Invoke(Inner, args);
        }

        private async Task WaitAndWriteAsync(MethodInfo method, object?[] args)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync((CancellationToken)args[^1]!);
            await (Task)method.Invoke(Inner, args)!;
            Written.TrySetResult();
        }
    }
}
