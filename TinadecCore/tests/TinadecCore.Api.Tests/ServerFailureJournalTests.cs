using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TinadecCore.AspNetCore;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The handler's promise: a 5xx keeps its cause in the process and off the wire.
/// <para>
/// Both halves are load-bearing. Without the journal, the load-sensitive reds in this repository
/// (interactions, the approvals poll) arrive as a status code and the fixed sentence "An unexpected
/// error occurred", so a flake cannot be diagnosed even after it is caught. Without the assertion on
/// the response body, the journal would be a way to leak exception types to any caller who can make a
/// request — which is exactly what the wire format refuses to do.
/// </para>
/// <para>
/// Self-hosted rather than <c>WebApplicationFactory</c> because the fact under test lives in one
/// middleware, and booting the whole Core host to reach it would tie the result to unrelated
/// startup state. Same shape as <see cref="FakeAcpServer"/>.
/// </para>
/// </summary>
public sealed class ServerFailureJournalTests
{
    [Fact]
    public async Task AnUnmappedFailureRecordsItsCauseAndStillSaysNothingOnTheWire()
    {
        await using var host = await ThrowingHost.StartAsync();

        var response = await host.Client.GetAsync("/boom");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("internal_error", body);
        // The whole reason the journal exists: the caller must not learn the driver, the type, or the
        // SQL state from a response body.
        Assert.DoesNotContain("InvalidCastException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("user-function", body, StringComparison.Ordinal);

        var failure = Assert.Single(host.Journal.Recent());
        Assert.Equal("GET", failure.Method);
        Assert.Equal("/boom", failure.Path);
        Assert.Equal(500, failure.Status);
        Assert.Equal("internal_error", failure.Code);
        Assert.Contains("InvalidCastException", failure.ExceptionType, StringComparison.Ordinal);
        // The outer type is the wrapper; the innermost one is the sentence a fixer searches for.
        Assert.NotNull(failure.RootMessage);
        Assert.Contains("unable to delete/modify user-function", failure.RootMessage, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(failure.TraceId));
    }

    [Fact]
    public async Task AFourxxAlreadyCarriesItsReasonSoItDoesNotCrowdTheRing()
    {
        await using var host = await ThrowingHost.StartAsync();

        var response = await host.Client.GetAsync("/missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("run_not_found", await response.Content.ReadAsStringAsync());
        Assert.Empty(host.Journal.Recent());
    }

    [Fact]
    public async Task AComposedHostWithoutTheHttpServicesStillAnswersItsOwnFailure()
    {
        // The handler resolves the journal optionally. A host that composes
        // UseTinadecCoreExceptionHandler without AddTinadecCoreHttp must still answer 500 rather than
        // trade one failure for a second one inside the diagnostic.
        await using var bare = await BareThrowingHost.StartAsync();

        var response = await bare.Client.GetAsync("/boom");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("internal_error", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void TheRingKeepsTheRecentFailuresAndDropsTheOldest()
    {
        var journal = new ServerFailureJournal();
        for (var index = 0; index < ServerFailureJournal.MaxEntries + 5; index++)
        {
            journal.Add(new ServerFailure("GET", $"/path-{index}", 500, "internal_error", "System.Exception", "m", null, null, "trace", DateTimeOffset.UtcNow));
        }

        var recent = journal.Recent();
        Assert.Equal(ServerFailureJournal.MaxEntries, recent.Count);
        Assert.Equal("/path-5", recent[0].Path);
        Assert.Equal($"/path-{ServerFailureJournal.MaxEntries + 4}", recent[^1].Path);
    }

    [Fact]
    public void TheReportLineCarriesTheWrapperAndTheCauseItConceals()
    {
        // Every red in the load-flake family enters through this line, so the parts that make it
        // searchable — route, machine code, thrown type, innermost message — are worth pinning.
        var line = new ServerFailure(
            "POST", "/api/v1/sessions/x/interactions", 500, "internal_error",
            "System.IO.IOException", "the read failed",
            "Microsoft.Data.Sqlite.SqliteException", "SQLite Error 5",
            "0HN", DateTimeOffset.UtcNow).Describe();

        Assert.Equal(
            "POST /api/v1/sessions/x/interactions [internal_error] System.IO.IOException: the read failed <- Microsoft.Data.Sqlite.SqliteException: SQLite Error 5",
            line);
    }

    [Fact]
    public void TheReportLineStopsNamingLayersOnceThereIsOnlyOne()
    {
        var line = new ServerFailure(
            "GET", "/api/v1/runs/x", 500, "internal_error",
            "System.InvalidOperationException", "lease already held",
            "System.InvalidOperationException", "lease already held",
            "0HN", DateTimeOffset.UtcNow).Describe();

        Assert.DoesNotContain(" <- ", line, StringComparison.Ordinal);
    }

    private sealed class ThrowingHost : IAsyncDisposable
    {
        private ThrowingHost(WebApplication app, HttpClient client, ServerFailureJournal journal)
        {
            App = app;
            Client = client;
            Journal = journal;
        }

        private WebApplication App { get; }
        public HttpClient Client { get; }
        public ServerFailureJournal Journal { get; }

        public static async Task<ThrowingHost> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
            // Registered through the production composition, so this also proves the line that adds it.
            builder.Services.AddTinadecCoreHttp();
            var app = builder.Build();
            app.UseTinadecCoreExceptionHandler();
            // Typed as Action: a bare `() => throw ...` lambda has no inferable signature and binds to
            // RequestDelegate, which wants a HttpContext.
            Action boom = () => throw new InvalidCastException("the read failed",
                new InvalidOperationException("SQLite Error 5: 'unable to delete/modify user-function due to active statements.'"));
            Action missing = () => throw new KeyNotFoundException("no such run");
            app.MapGet("/boom", boom);
            app.MapGet("/missing", missing);
            await app.StartAsync();
            return new ThrowingHost(app, ClientFor(app), app.Services.GetRequiredService<ServerFailureJournal>());
        }

        private static HttpClient ClientFor(WebApplication app)
        {
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.First(uri => uri.StartsWith("http://127.0.0.1:"));
            return new HttpClient { BaseAddress = new Uri(address) };
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private sealed class BareThrowingHost : IAsyncDisposable
    {
        private BareThrowingHost(WebApplication app, HttpClient client)
        {
            App = app;
            Client = client;
        }

        private WebApplication App { get; }
        public HttpClient Client { get; }

        public static async Task<BareThrowingHost> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            app.UseTinadecCoreExceptionHandler();
            Action boom = () => throw new Exception("an unmapped failure");
            app.MapGet("/boom", boom);
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.First(uri => uri.StartsWith("http://127.0.0.1:"));
            return new BareThrowingHost(app, new HttpClient { BaseAddress = new Uri(address) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}
