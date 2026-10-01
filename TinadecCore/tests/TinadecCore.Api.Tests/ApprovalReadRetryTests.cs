using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Lifecycle;
using TinadecCore.Runtime;

namespace TinadecCore.Api.Tests;

public sealed class ApprovalReadRetryTests : IAsyncLifetime
{
    private readonly ApiEndpointFactory _factory = new();
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public async Task TransientFailure_RecreatesContextAndReturnsTheStoredApproval(int code)
    {
        var inner = _factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>();
        var scope = _factory.Services.GetRequiredService<TinadecCore.Abstractions.Ports.ITenantContextAccessor>().Current;
        var id = Guid.NewGuid();
        await using (var db = await inner.CreateDbContextAsync())
        {
            db.ApprovalRequests.Add(new ApprovalRequestRecord { Id = id, TenantId = scope.TenantId,
                WorkspaceId = scope.WorkspaceId, Status = "pending", Kind = "tool", ToolId = "write_file",
                CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
            await db.SaveChangesAsync();
        }
        var faults = new FaultingFactory(inner, code, 1);
        var service = ActivatorUtilities.CreateInstance<ControlPlaneService>(_factory.Services, faults);
        var result = await service.ListApprovals("pending", null, null, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var items = Assert.IsAssignableFrom<IEnumerable<object>>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        Assert.Contains(items, item => item is TinadecCore.Contracts.Dtos.ApprovalResponseDto row && row.Id == id);
        Assert.Equal(2, faults.Attempts);
    }

    [Theory]
    [InlineData(5, 3)]
    [InlineData(6, 3)]
    [InlineData(1, 1)]
    public async Task PersistentOrNonTransientFailures_RemainErrors(int code, int attempts)
    {
        var inner = _factory.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>();
        var faults = new FaultingFactory(inner, code, int.MaxValue);
        var service = ActivatorUtilities.CreateInstance<ControlPlaneService>(_factory.Services, faults);
        var error = await Assert.ThrowsAsync<SqliteException>(() => service.ListApprovals(null, null, null, CancellationToken.None));
        Assert.Equal(code, error.SqliteErrorCode);
        Assert.Equal(attempts, faults.Attempts);
    }

    private sealed class FaultingFactory(IDbContextFactory<LifecycleDbContext> inner, int code, int failures)
        : IDbContextFactory<LifecycleDbContext>
    {
        public int Attempts { get; private set; }
        public LifecycleDbContext CreateDbContext()
        {
            if (++Attempts <= failures) throw new SqliteException("Injected read connection failure", code);
            return inner.CreateDbContext();
        }
        public Task<LifecycleDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
}
