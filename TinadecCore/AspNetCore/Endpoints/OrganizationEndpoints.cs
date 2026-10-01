using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// A session's organization, its graph and its evidence archive, for the session's owner
/// (architecture §9, todo O1–O4, D4, R4).
/// Read-mostly: the owner may post as the organization's human member and decide reports; every
/// other change happens through the agents' own tools. Reports carry the live state of what they are
/// about, resolved at read time, so a stale sentence is never shown as the current fact.
/// </summary>
public static class OrganizationEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder app)
    {
        var sessions = app.MapGroup("/api/v1/sessions/{sessionId:guid}").WithTags("Organization");

        sessions.MapGet("/topology", async (Guid sessionId, Guid? run_id, bool? include_finished, int? max_runs, int? max_tasks,
            ISessionTopology topology, CancellationToken ct) =>
        {
            var view = await topology.GetAsync(sessionId, new SessionTopologyQuery(
                MaxRuns: max_runs ?? 10, MaxTasksPerRun: max_tasks ?? 200, MaxInstancesPerRun: 200, MaxLeases: 200, MaxMembers: 200,
                IncludeFinishedRuns: include_finished ?? true, RunId: run_id), ct);
            return view is null ? Results.NotFound(new { code = "NOT_FOUND", message = "Session was not found." }) : Results.Ok(view);
        }).Produces<SessionTopologyDto>();

        // The session's evidence archive, for the owner: the same recall governance roles use, scoped to
        // the caller's tenant (another tenant's session simply has no evidence here).
        sessions.MapGet("/evidence", async (Guid sessionId, string? q, string? kinds, Guid? run_id, int? limit,
            IServiceProvider services, ITenantContextAccessor tenant, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { code = "INVALID_REQUEST", message = "q (what to look for) is required." });
            if (services.GetService<IEvidenceArchive>() is not { } archive)
                return Results.NotFound(new { code = "NOT_FOUND", message = "This host keeps no evidence archive." });
            var requested = (kinds ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (requested.FirstOrDefault(kind => !EvidenceKinds.All.Contains(kind, StringComparer.Ordinal)) is { } unknown)
                return Results.BadRequest(new { code = "INVALID_REQUEST", message = $"'{unknown}' is not an evidence kind; use {string.Join(", ", EvidenceKinds.All)}." });
            var scope = tenant.Current;
            var result = await archive.RecallAsync(new EvidenceRecallQuery(scope.TenantId, scope.WorkspaceId, sessionId, q.Trim(),
                requested.Length == 0 ? null : requested, run_id, Math.Clamp(limit ?? 20, 1, 20)), ct);
            return Results.Ok(new EvidenceRecallDto(result.Mode, result.Note, result.Hits.Select(hit => new EvidenceHitDto(
                hit.EvidenceId, hit.Kind, hit.Title, hit.Author, hit.RunId, hit.TaskId, hit.Snippet, hit.Score, hit.MatchedBy, hit.CreatedAt)).ToArray()));
        }).Produces<EvidenceRecallDto>();

        var organization = sessions.MapGroup("/organization");
        organization.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (TinaChatException ex)
            {
                return Results.Problem(statusCode: ex.StatusCode, title: ex.Code, detail: ex.Message,
                    extensions: new Dictionary<string, object?> { ["code"] = ex.Code, ["trace_id"] = context.HttpContext.TraceIdentifier });
            }
        });

        organization.MapGet("", async (Guid sessionId, ISessionOrganizationView view, CancellationToken ct) =>
        {
            var result = await view.GetAsync(sessionId, ct);
            return result is null
                ? Results.NotFound(new { code = "organization_not_started", message = "This session has no organization yet: it is created when the first run starts." })
                : Results.Ok(result);
        }).Produces<OrganizationDto>();

        organization.MapGet("/rooms/{roomId:guid}/messages", async (Guid sessionId, Guid roomId, long? after_sequence, int? limit,
            ISessionOrganizationView view, CancellationToken ct) =>
            Results.Ok(await view.ReadRoomAsync(sessionId, roomId, after_sequence ?? 0, limit ?? 50, ct))).Produces<OrganizationMessagePage>();

        organization.MapPost("/rooms/{roomId:guid}/messages", async (Guid sessionId, Guid roomId, OrganizationPostRequest request,
            ISessionOrganizationView view, CancellationToken ct) =>
            Results.Ok(await view.PostAsync(sessionId, roomId, request, ct))).Produces<OrganizationMessageDto>();

        organization.MapGet("/reports", async (Guid sessionId, string? status, int? limit, ISessionOrganizationView view,
            ISessionTopology topology, IServiceProvider services, CancellationToken ct) =>
        {
            var page = await view.ListReportsAsync(sessionId, status, limit ?? 50, ct);
            return Results.Ok(page with { Items = await ReportSubjects.ResolveAsync(sessionId, page.Items, topology, services.GetService<IResourceLeaseService>(), ct) });
        }).Produces<OrganizationReportPage>();

        organization.MapPost("/reports/{reportId:guid}/decision", async (Guid sessionId, Guid reportId, OrganizationReportDecisionRequest request,
            ISessionOrganizationView view, ISessionTopology topology, IServiceProvider services, CancellationToken ct) =>
        {
            var decided = await view.DecideReportAsync(sessionId, reportId, request, ct);
            return Results.Ok((await ReportSubjects.ResolveAsync(sessionId, [decided], topology, services.GetService<IResourceLeaseService>(), ct))[0]);
        }).Produces<OrganizationReportDto>();

        // Per-member visibility into run internals (todo E5): "down" is the default for governance
        // roles ("they watch the execution layer"), "own" narrows a member to its own run.
        organization.MapPatch("/members/{participantId:guid}", async (Guid sessionId, Guid participantId, OrganizationMemberVisibilityRequest request,
            ISessionOrganizationView view, CancellationToken ct) =>
            Results.Ok(await view.SetMemberVisibilityAsync(sessionId, participantId, request, ct))).Produces<OrganizationMemberDto>();

        return app;
    }
}

/// <summary>
/// Resolves what reports are about against the session's current graph: one bounded topology read per
/// request, however many reports reference it.
/// </summary>
internal static class ReportSubjects
{
    public static async Task<OrganizationReportDto[]> ResolveAsync(Guid sessionId, IReadOnlyList<OrganizationReportDto> reports,
        ISessionTopology topology, IResourceLeaseService? ledger, CancellationToken ct)
    {
        if (!reports.Any(report => report.SubjectKind is not null)) return reports.ToArray();
        var view = await topology.GetAsync(sessionId, new SessionTopologyQuery(MaxRuns: 20, MaxTasksPerRun: 500, MaxInstancesPerRun: 1,
            MaxLeases: 500, MaxMembers: 500), ct);
        if (view is null) return reports.ToArray();
        // Resources are not session-scoped (the host or another session may hold them), so leases and
        // paths are asked of the ledger itself rather than of this session's slice of it.
        var active = ledger is null ? null : await ledger.ListActiveAsync(null, ct);
        var result = new List<OrganizationReportDto>();
        foreach (var report in reports)
        {
            var state = Resolve(report, view);
            if (ledger is not null && report.SubjectId is { } subject)
            {
                if (report.SubjectKind == "lease" && Guid.TryParse(subject, out var leaseId))
                    state = active!.FirstOrDefault(lease => lease.Id == leaseId) is { } lease
                        ? new OrganizationSubjectStateDto("lease", subject, "active", $"{lease.Kind} {lease.ResourceKey}")
                        : new OrganizationSubjectStateDto("lease", subject, "released", null);
                else if (report.SubjectKind == "path")
                {
                    // A fresh run id matches no lease, so every holder — the host included — is reported.
                    var holders = await ledger.ProbeAsync(new ResourceClaim(ResourceLeaseKinds.Path, subject, Exclusive: true), Guid.NewGuid(), ct);
                    state = holders.Count > 0
                        ? new OrganizationSubjectStateDto("path", subject, "held", holders[0].Purpose)
                        : new OrganizationSubjectStateDto("path", subject, "free", null);
                }
            }
            result.Add(report with { SubjectState = state });
        }
        return result.ToArray();
    }

    private static OrganizationSubjectStateDto? Resolve(OrganizationReportDto report, SessionTopologyDto view)
    {
        if (report.SubjectKind is not { } kind || report.SubjectId is not { } id) return null;
        var guid = Guid.TryParse(id, out var parsed) ? parsed : (Guid?)null;
        return kind switch
        {
            "run" => view.Runs.FirstOrDefault(run => run.RunId == guid) is { } run
                ? new OrganizationSubjectStateDto(kind, id, run.Status, run.Phase)
                : new OrganizationSubjectStateDto(kind, id, null, null),
            "task" => view.Runs.SelectMany(run => run.Tasks).FirstOrDefault(task => task.TaskId == guid) is { } task
                ? new OrganizationSubjectStateDto(kind, id, task.Status, task.Handle is null ? task.Title : $"{task.Title} · {task.Handle}")
                : new OrganizationSubjectStateDto(kind, id, null, null),
            "lease" => view.Leases.FirstOrDefault(lease => lease.LeaseId == guid) is { } lease
                ? new OrganizationSubjectStateDto(kind, id, "active", $"{lease.Kind} {lease.ResourceKey}")
                : new OrganizationSubjectStateDto(kind, id, view.LeasesTruncated ? null : "released", null),
            "member" => view.Members.FirstOrDefault(member => member.ParticipantId == guid
                    || string.Equals(member.DisplayName, id, StringComparison.OrdinalIgnoreCase)) is { } member
                ? new OrganizationSubjectStateDto(kind, id, member.Presence, member.DisplayName)
                : new OrganizationSubjectStateDto(kind, id, null, null),
            "path" => view.Leases.FirstOrDefault(lease => string.Equals(lease.ResourceKey, id, StringComparison.OrdinalIgnoreCase)) is { } held
                ? new OrganizationSubjectStateDto(kind, id, "held", held.Purpose)
                : new OrganizationSubjectStateDto(kind, id, "free", null),
            _ => new OrganizationSubjectStateDto(kind, id, null, null)
        };
    }
}
