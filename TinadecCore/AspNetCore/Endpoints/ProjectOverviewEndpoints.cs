using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Memory;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// The project commander's desk (todo E4, first slice): one read that rolls a project's sessions,
/// their unfinished runs, their open reports and the resources being held right now up to the
/// project. Visibility before authority — a future project-level chairman acts on this shape.
/// </summary>
public static class ProjectOverviewEndpoints
{
    private const int MaxReports = 50;

    public static IEndpointRouteBuilder MapProjectOverviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/projects/{projectId:guid}/overview", async (
            Guid projectId,
            ProjectSessionStore sessions,
            ILifecycleManager lifecycle,
            IServiceProvider services,
            Microsoft.EntityFrameworkCore.IDbContextFactory<MemoryDbContext> memoryDbFactory,
            CancellationToken ct) =>
        {
            var store = sessions;
            var project = await store.FindProjectAsync(projectId, ct).ConfigureAwait(false);
            if (project is null) return Results.NotFound(new { code = "NOT_FOUND", message = "Project was not found." });
            await using var memory = await memoryDbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var name = await memory.Projects.Where(x => x.Id == projectId).Select(x => x.Name).FirstOrDefaultAsync(ct).ConfigureAwait(false) ?? string.Empty;

            var sessionRows = await store.ListSessionsAsync(projectId, LifecycleStatuses.Active, ct).ConfigureAwait(false);
            var sessionIds = sessionRows.Select(row => row.Id).ToArray();
            var activeRuns = new List<ProjectOverviewRunDto>();
            foreach (var sessionId in sessionIds)
            {
                var runs = await lifecycle.ListActiveRunsAsync(sessionId, ct).ConfigureAwait(false);
                foreach (var run in runs)
                {
                    activeRuns.Add(new ProjectOverviewRunDto(
                        Guid.TryParse(run.RunId, out var runId) ? runId : Guid.Empty,
                        sessionId,
                        Wire(run.Status),
                        Phase: null,
                        run.StartedAt));
                }
            }

            var openReports = new List<ProjectOverviewReportDto>();
            var reportsTruncated = false;
            if (services.GetService(typeof(ISessionOrganization)) is ISessionOrganization organization)
            {
                foreach (var sessionId in sessionIds)
                {
                    if (openReports.Count >= MaxReports) { reportsTruncated = true; break; }
                    var remaining = MaxReports - openReports.Count;
                    var reports = await organization.ListOpenReportsAsync(sessionId, remaining + 1, ct).ConfigureAwait(false);
                    if (reports.Count > remaining) reportsTruncated = true;
                    foreach (var report in reports.Take(remaining))
                    {
                        openReports.Add(new ProjectOverviewReportDto(sessionId, report.ReportId, report.AuthorDisplayName,
                            report.ReportKind, report.Severity, report.Finding, report.CreatedAt));
                    }
                }
            }

            IReadOnlyList<ResourceLeaseInfo> leases = [];
            if (services.GetService(typeof(IResourceLeaseService)) is IResourceLeaseService ledger)
                leases = await ledger.ListActiveAsync(null, ct).ConfigureAwait(false);
            var held = leases
                .Where(lease => lease.SessionId is { } leaseSession && sessionIds.Contains(leaseSession))
                .Select(lease => new ProjectOverviewLeaseDto(lease.Id, lease.Kind, lease.ResourceKey, lease.Purpose, lease.RunId, lease.Reason))
                .ToArray();

            return Results.Ok(new ProjectOverviewDto(project.ProjectId, name, sessionRows.Count,
                activeRuns.OrderBy(run => run.StartedAt).ToArray(),
                openReports.OrderBy(r => r.Severity == "blocking" ? 0 : 1).ThenByDescending(r => r.CreatedAt).ToArray(),
                held, reportsTruncated));
        }).Produces<ProjectOverviewDto>();

        return app;
    }

    private static string Wire(RunStatus status) => status switch
    {
        RunStatus.Planning => "planning",
        RunStatus.Understanding => "understanding",
        RunStatus.Executing => "executing",
        RunStatus.Replanning => "replanning",
        RunStatus.AwaitingApproval => "awaiting_approval",
        RunStatus.AwaitingDelegate => "awaiting_delegate",
        RunStatus.AwaitingUser => "awaiting_user",
        RunStatus.Paused => "paused",
        RunStatus.Reviewing => "reviewing",
        RunStatus.Completed => "completed",
        RunStatus.Failed => "failed",
        RunStatus.Cancelled => "cancelled",
        _ => "unknown"
    };
}
