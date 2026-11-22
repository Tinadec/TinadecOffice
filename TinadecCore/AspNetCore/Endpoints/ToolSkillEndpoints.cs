using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Skills;

namespace TinadecCore.AspNetCore.Endpoints;

public static class ToolSkillEndpoints
{
    public static IEndpointRouteBuilder MapToolSkillEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tools/skills").WithTags("Tool Skills");
        group.MapGet("", async (Guid? project_id, IToolSkillResourceService skills, CancellationToken ct) =>
        {
            try { return Results.Ok(await skills.ListAsync(project_id, includeBuiltins: true, ct)); } catch (ToolSettingsException ex) { return Problem(ex); }
        }).Produces<ToolSkillListDto>();
        group.MapGet("/diagnostics", async (Guid? project_id, IToolSkillResourceService skills, CancellationToken ct) =>
        {
            try
            {
                var list = await skills.ListAsync(project_id, includeBuiltins: true, ct);
                return Results.Ok(new
                {
                    source = list.Source,
                    skills = list.Skills.Select(x => new
                    {
                        x.ResourceId, x.Name, x.SourceKind, x.Scope, x.Rank, x.Enabled, x.Valid,
                        x.ShadowedBy, x.ReadOnly, x.ReasonCode, x.Path,
                    }).ToArray(),
                    diagnostics = list.Diagnostics,
                });
            }
            catch (ToolSettingsException ex) { return Problem(ex); }
        });
        group.MapGet("/{resourceId:guid}", async (Guid resourceId, Guid? project_id, HttpResponse response, IToolSkillResourceService skills, CancellationToken ct) =>
        {
            try
            {
                var resource = await skills.GetAsync(resourceId, project_id, ct);
                if (resource is null) return Results.NotFound();
                response.Headers.ETag = $"\"{resource.Revision}\"";
                return Results.Ok(resource);
            }
            catch (ToolSettingsException ex) { return Problem(ex); }
        }).Produces<ToolSkillResourceDto>().Produces(StatusCodes.Status404NotFound);
        group.MapPost("/{resourceId:guid}/copy-to-project", async (Guid resourceId, Guid project_id, IToolSkillResourceService skills, CancellationToken ct) =>
        {
            try { return Results.Ok(await skills.RequestCopyToProjectAsync(resourceId, project_id, ct)); }
            catch (ToolSettingsException ex) { return Problem(ex); }
        }).Produces<ToolSkillResourceDto>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        group.MapGet("/{resourceId:guid}/files", async (Guid resourceId, Guid? project_id, string path, IToolSkillResourceService skills, CancellationToken ct) =>
        {
            try
            {
                var file = await skills.GetFileAsync(resourceId, project_id, path, ct);
                return file is null ? Results.NotFound() : Results.Ok(file);
            }
            catch (ToolSettingsException ex) { return Problem(ex); }
        }).Produces<ToolSkillFileDto>().Produces(StatusCodes.Status404NotFound);
        group.MapPost("/import", async (ToolSkillWriteDto input, HttpRequest request, HttpResponse response, IToolSkillResourceService skills, CancellationToken ct) =>
        {
            if (!Revision(request, out var revision)) return Results.Problem("If-Match with revision zero is required for a new resource.", statusCode: 428);
            if (revision != 0) return Results.Problem("A new resource requires revision zero.", statusCode: 412);
            try
            {
                var resource = await skills.RequestSaveAsync(null, input.ProjectId, input, 0, ct);
                response.Headers.ETag = $"\"{resource.Revision}\"";
                return Results.Ok(resource);
            }
            catch (ToolSettingsException ex) { return Problem(ex); }
        }).Produces<ToolSkillResourceDto>().ProducesProblem(400).ProducesProblem(409).ProducesProblem(412).ProducesProblem(428);
        group.MapPut("/{resourceId:guid}", async (Guid resourceId, Guid? project_id, ToolSkillWriteDto input, HttpRequest request, HttpResponse response, IToolSkillResourceService skills, CancellationToken ct) =>
        {
            if (!Revision(request, out var revision)) return Results.Problem("If-Match with the loaded revision is required.", statusCode: 428);
            try
            {
                var resource = await skills.RequestSaveAsync(resourceId, project_id, input, revision, ct);
                response.Headers.ETag = $"\"{resource.Revision}\"";
                return Results.Ok(resource);
            }
            catch (ToolSettingsException ex) { return Problem(ex); }
        }).Produces<ToolSkillResourceDto>().ProducesProblem(400).ProducesProblem(412).ProducesProblem(428);
        group.MapDelete("/{resourceId:guid}", async (Guid resourceId, Guid? project_id, HttpRequest request, IToolSkillResourceService skills, CancellationToken ct) =>
        {
            if (!Revision(request, out var revision)) return Results.Problem("If-Match with the loaded revision is required.", statusCode: 428);
            try { return Results.Ok(await skills.RequestDeleteAsync(resourceId, project_id, revision, ct)); } catch (ToolSettingsException ex) { return Problem(ex); }
        }).Produces<ToolSkillResourceDto>().ProducesProblem(412).ProducesProblem(428);

        group.MapPost("/install", async (ToolSkillInstallRequestDto input, IToolSkillResourceService skills, IServiceProvider sp, CancellationToken ct) =>
        {
            try
            {
                var prepared = AttachAgentImporter(input, sp);
                return Results.Ok(await skills.InstallAsync(prepared, ct));
            }
            catch (ToolSettingsException ex) { return Problem(ex); }
        }).Produces<ToolSkillInstallResultDto>().ProducesProblem(400).ProducesProblem(409);

        group.MapPatch("/{resourceId:guid}/enabled", async (Guid resourceId, ToolSkillEnabledRequestDto input, HttpRequest request, HttpResponse response, IToolSkillResourceService skills, CancellationToken ct) =>
        {
            if (input.Enabled is not { } enabled)
                return Results.Problem("enabled (true/false) is required.", statusCode: 400);
            long? expected = request.Headers.IfMatch.Count > 0 && Revision(request, out var rev) ? rev : null;
            if (request.Headers.IfMatch.Count > 0 && expected is null)
                return Results.Problem("If-Match must contain a valid non-negative integer.", statusCode: 400);
            try
            {
                var updated = await skills.SetEnabledAsync(resourceId, enabled, input.ProjectId, expected, ct);
                response.Headers.ETag = $"\"{updated.Revision}\"";
                return Results.Ok(updated);
            }
            catch (ToolSettingsException ex) { return Problem(ex); }
        }).Produces<ToolSkillResourceDto>().ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(412);

        group.MapPost("/reload", async (Guid? project_id, IToolSkillResourceService skills, CancellationToken ct) =>
        {
            try { return Results.Ok(await skills.ReloadAsync(project_id, ct)); }
            catch (ToolSettingsException ex) { return Problem(ex); }
        }).Produces<ToolSkillListDto>();

        app.MapGet("/api/v1/tools/external-cli/discover", async (
            Guid? project_id,
            string? ecosystems,
            IExternalCliDiscoveryService discovery,
            IServiceProvider sp,
            CancellationToken ct) =>
        {
            var ecoList = string.IsNullOrWhiteSpace(ecosystems)
                ? null
                : ecosystems.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var result = await discovery.DiscoverAsync(project_id, ecoList, ct);
            result = await EnrichAgentAlreadyImportedAsync(result, sp, ct);
            return Results.Ok(result);
        }).WithTags("Tool Skills").Produces<ExternalCliDiscoveryDto>();

        app.MapPost("/api/v1/tools/external-cli/import", async (
            ExternalCliImportRequestDto input,
            IExternalCliDiscoveryService discovery,
            IServiceProvider sp,
            CancellationToken ct) =>
        {
            try
            {
                var prepared = new ExternalCliImportRequestDto
                {
                    ItemIds = input.ItemIds,
                    ProjectId = input.ProjectId,
                    Scope = input.Scope,
                    EnableStdioMcp = input.EnableStdioMcp,
                    Overwrite = input.Overwrite,
                    SecretValues = input.SecretValues,
                    CustomAgentImporter = (dto, overwrite, token) => ImportCustomAgentAsync(dto, overwrite, sp, token),
                };
                return Results.Ok(await discovery.ImportAsync(prepared, ct));
            }
            catch (ToolSettingsException ex) { return Problem(ex); }
        }).WithTags("Tool Skills").Produces<ExternalCliImportResultDto>().ProducesProblem(400).ProducesProblem(409);

        return app;
    }

    private static ToolSkillInstallRequestDto AttachAgentImporter(ToolSkillInstallRequestDto input, IServiceProvider sp) =>
        new()
        {
            SourceType = input.SourceType,
            Scope = input.Scope,
            ProjectId = input.ProjectId,
            Name = input.Name,
            Content = input.Content,
            Files = input.Files,
            LocalPath = input.LocalPath,
            GitUrl = input.GitUrl,
            Subpath = input.Subpath,
            ItemIds = input.ItemIds,
            Ecosystems = input.Ecosystems,
            Enabled = input.Enabled,
            EnableStdioMcp = input.EnableStdioMcp,
            Overwrite = input.Overwrite,
            Governed = input.Governed,
            SecretValues = input.SecretValues,
            CustomAgentImporter = (dto, overwrite, token) => ImportCustomAgentAsync(dto, overwrite, sp, token),
        };

    private static async Task<ExternalCliDiscoveryDto> EnrichAgentAlreadyImportedAsync(
        ExternalCliDiscoveryDto discovered,
        IServiceProvider sp,
        CancellationToken ct)
    {
        if (discovered.Items.All(i => !string.Equals(i.Kind, "agent", StringComparison.OrdinalIgnoreCase)))
            return discovered;

        var agentDbFactory = sp.GetService<IDbContextFactory<AgentConfigurationDbContext>>();
        var tenant = sp.GetService<ITenantContextAccessor>();
        if (agentDbFactory is null || tenant is null)
            return discovered;

        var scope = tenant.Current;
        await using var db = await agentDbFactory.CreateDbContextAsync(ct);
        var existingSlugs = (await db.AgentDefinitions.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Status != "archived")
            .Select(x => x.Slug)
            .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (existingSlugs.Count == 0)
            return discovered;

        var updatedItems = discovered.Items.Select(item =>
        {
            if (!string.Equals(item.Kind, "agent", StringComparison.OrdinalIgnoreCase))
                return item;
            var slug = ExternalCliDiscoveryService.NormalizeSkillName(item.Name);
            if (!existingSlugs.Contains(slug))
                return item;
            return new ExternalCliDiscoveredItemDto
            {
                ItemId = item.ItemId,
                Ecosystem = item.Ecosystem,
                Kind = item.Kind,
                Name = item.Name,
                DisplayName = item.DisplayName,
                Description = item.Description,
                SourcePath = item.SourcePath,
                Transport = item.Transport,
                Command = item.Command,
                Args = item.Args,
                Url = item.Url,
                EnvKeys = item.EnvKeys,
                HeaderKeys = item.HeaderKeys,
                SecretPlaceholders = item.SecretPlaceholders,
                HasSecrets = item.HasSecrets,
                DefaultEnabled = item.DefaultEnabled,
                AlreadyImported = true,
                ContentPreview = item.ContentPreview,
                Commit = item.Commit,
                Version = item.Version,
                ContentHash = item.ContentHash,
                PackageHash = item.PackageHash,
                HasHiddenUnicode = item.HasHiddenUnicode,
                SecurityWarnings = item.SecurityWarnings,
                Role = item.Role,
                SystemPrompt = item.SystemPrompt,
            };
        }).ToList();

        return new ExternalCliDiscoveryDto
        {
            UserHome = discovered.UserHome,
            WorkspaceRoot = discovered.WorkspaceRoot,
            Ecosystems = discovered.Ecosystems,
            Items = updatedItems,
        };
    }

    private static async Task<ExternalCliImportedAgentDto> ImportCustomAgentAsync(
        ExternalCliDiscoveredItemDto item,
        bool overwrite,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var agentConfig = sp.GetService<IAgentConfigurationService>()
            ?? throw new ToolSettingsException("agent_config_unavailable", "Agent Configuration service is not registered.");
        var agentDbFactory = sp.GetService<IDbContextFactory<AgentConfigurationDbContext>>()
            ?? throw new ToolSettingsException("agent_config_unavailable", "Agent Configuration database is not registered.");
        var tenant = sp.GetService<ITenantContextAccessor>()
            ?? throw new ToolSettingsException("tenant_unavailable", "Tenant context is not registered.");
        var packs = sp.GetService<IAgentPackService>();

        var slug = ExternalCliDiscoveryService.NormalizeSkillName(item.Name);
        var displayName = string.IsNullOrWhiteSpace(item.DisplayName) ? slug : item.DisplayName.Trim();
        var role = string.IsNullOrWhiteSpace(item.Role) ? slug : item.Role.Trim();
        var systemPrompt = item.SystemPrompt ?? string.Empty;
        var provenanceObj = new
        {
            ecosystem = item.Ecosystem,
            source_path = item.SourcePath,
            commit = item.Commit,
            content_hash = item.ContentHash,
            has_hidden_unicode = item.HasHiddenUnicode,
        };

        var scope = tenant.Current;
        await using var db = await agentDbFactory.CreateDbContextAsync(ct);
        var existing = await db.AgentDefinitions.FirstOrDefaultAsync(
            x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Slug == slug && x.Status != "archived",
            ct);

        if (existing is not null)
        {
            if (existing.Managed || (packs is not null && await packs.FindManagedResourceAsync("agent", existing.Id, ct) is not null))
            {
                throw new ToolSettingsException("managed_agent_protected", $"Agent '{slug}' is managed by an AgentPack and cannot be overwritten.", 409);
            }

            if (!overwrite)
            {
                throw new ToolSettingsException("agent_already_exists", $"Custom agent '{slug}' already exists.", 409);
            }

            var provJson = JsonSerializer.Serialize(provenanceObj);
            if (provJson.Length > 4096) provJson = provJson[..4096];

            existing.DisplayName = displayName;
            existing.Description = item.Description;
            existing.Role = role;
            existing.SystemPrompt = systemPrompt;
            existing.SourceKind = "custom";
            existing.SourceKey = $"{item.Ecosystem}:{slug}";
            existing.ProvenanceJson = provJson;
            existing.Status = "draft";
            existing.Revision++;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            existing.UpdatedByPrincipalId = scope.PrincipalId;
            await db.SaveChangesAsync(ct);

            var pubExisting = JsonSerializer.SerializeToElement(await agentConfig.PublishAsync(existing.Id, existing.Revision, ct));
            var nextVer = pubExisting.TryGetProperty("version", out var vEl) && vEl.TryGetInt32(out var vInt) ? vInt : existing.Version + 1;

            return new ExternalCliImportedAgentDto
            {
                Id = existing.Id,
                Slug = slug,
                Name = slug,
                DisplayName = displayName,
                Description = item.Description,
                SourceKind = "custom",
                Status = "published",
                Version = nextVer,
                SourcePath = item.SourcePath,
                Ecosystem = item.Ecosystem,
                Commit = item.Commit,
                ContentHash = item.ContentHash,
                HasHiddenUnicode = item.HasHiddenUnicode,
                SecurityWarnings = item.SecurityWarnings,
            };
        }

        var draftPayload = new
        {
            slug,
            display_name = displayName,
            layer = "execution",
            role,
            source_kind = "custom",
            source_key = $"{item.Ecosystem}:{slug}",
            description = item.Description,
            system_prompt = systemPrompt,
            provenance = provenanceObj,
        };

        var createdDraft = JsonSerializer.SerializeToElement(await agentConfig.CreateDraftAsync("agent", draftPayload, ct));
        var createdId = createdDraft.GetProperty("id").GetGuid();
        var createdRev = createdDraft.GetProperty("revision").GetInt64();
        var published = JsonSerializer.SerializeToElement(await agentConfig.PublishAsync(createdId, createdRev, ct));
        var publishedVersion = published.TryGetProperty("version", out var pubVerEl) && pubVerEl.TryGetInt32(out var pubVer) ? pubVer : 1;

        return new ExternalCliImportedAgentDto
        {
            Id = createdId,
            Slug = slug,
            Name = slug,
            DisplayName = displayName,
            Description = item.Description,
            SourceKind = "custom",
            Status = "published",
            Version = publishedVersion,
            SourcePath = item.SourcePath,
            Ecosystem = item.Ecosystem,
            Commit = item.Commit,
            ContentHash = item.ContentHash,
            HasHiddenUnicode = item.HasHiddenUnicode,
            SecurityWarnings = item.SecurityWarnings,
        };
    }
    private static bool Revision(HttpRequest request, out long revision) => long.TryParse(request.Headers.IfMatch.ToString().Trim('"'), out revision) && revision >= 0;
    private static IResult Problem(ToolSettingsException ex) => Results.Problem(ex.Message, statusCode: ex.StatusCode, extensions: new Dictionary<string, object?> { ["error_code"] = ex.Code });
}

