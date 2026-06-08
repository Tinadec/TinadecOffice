using System.Text.Json;
using System.Text.Json.Nodes;
using TinadecCore.Services;
using TinadecCore.Storage;
using Tinadec.Contracts.Events;
using Tinadec.Contracts.Models;
using TinadecCore.Abstractions;
using TinadecCore.Tracing;
using TinadecCore.Debug;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .WithOrigins("http://127.0.0.1:48730", "http://localhost:48730", "http://127.0.0.1:5173", "http://localhost:5173")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddSingleton<CoreStore>();
builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddSingleton<ICapabilityProvider, CodexCapabilityProvider>();
builder.Services.AddSingleton<ICapabilityProvider, CodeCapabilityProvider>();
builder.Services.AddSingleton<ICapabilityProvider, PromptContextCapabilityProvider>();
builder.Services.AddSingleton<IRuntimeKernelAdapter, CodexRuntimeKernelAdapter>();
builder.Services.AddSingleton<ICapabilityPolicy, CapabilityPolicyService>();
builder.Services.AddSingleton<IToolRegistry, ToolRegistryService>();
builder.Services.AddSingleton<HarnessManifestService>();
builder.Services.AddSingleton<RuntimeReadinessService>();
builder.Services.AddSingleton<ModelReadinessService>();
builder.Services.AddSingleton<ToolSearchService>();
builder.Services.AddSingleton<ToolExecutionTimelineService>();
builder.Services.AddSingleton<IAgentWorkflowRuntime, AgentWorkflowRuntime>();
builder.Services.AddSingleton<IModelRouteResolver, ModelRouteResolver>();
builder.Services.AddSingleton<IModelCredentialResolver, ModelCredentialResolver>();
builder.Services.AddSingleton<IModelInvocationRuntime, ModelInvocationRuntime>();
builder.Services.AddSingleton<IPromptContextPlannerRuntime, ModelPromptContextPlannerRuntime>();
builder.Services.AddSingleton<PromptContextService>();
builder.Services.AddSingleton<IModelManagementService, ModelManagementService>();
builder.Services.AddModelProviderModule<LocalHttpModule>();
builder.Services.AddModelProviderModule<AnthropicModule>();
builder.Services.AddModelProviderModule<OpenAiCompatibleModule>();
builder.Services.AddModelProviderModule<CliModule>();
builder.Services.AddHttpClient<ICodeToolClient, CodeToolClient>(client =>
{
    var gatewayUrl = Environment.GetEnvironmentVariable("TINADEC_GATEWAY_URL") ?? "http://127.0.0.1:48730";
    client.BaseAddress = new Uri(gatewayUrl.TrimEnd('/') + "/");
});
builder.Services.AddSingleton<IToolInvocationAdapter, CodexToolInvocationAdapter>();
builder.Services.AddSingleton<IToolInvocationAdapter, CoreToolInvocationAdapter>();
builder.Services.AddSingleton<DoctorService>();
builder.Services.AddSingleton<OrchestratorService>();
builder.Services.AddSingleton<ToolExecutionService>();

// --- Agent Debug Studio: Tracing & Debug Services ---
builder.Services.AddSingleton<AgentTracing>();
builder.Services.AddSingleton<TinadecMetrics>();
builder.Services.AddSingleton<TraceDiagnosticService>();
builder.Services.AddSingleton<ProcessDiagnosticsService>();
builder.Services.AddSingleton<BreakpointService>();
builder.Services.AddSingleton<SimulationService>();
builder.Services.AddSingleton<DebugWebSocketHandler>();

var app = builder.Build();
app.UseCors();

// --- Initialize Agent Debug Studio Tracing ---
var tracing = app.Services.GetRequiredService<AgentTracing>();
var metrics = app.Services.GetRequiredService<TinadecMetrics>();
tracing.Initialize(metrics);

// --- Enable WebSocket for Debug Studio ---
app.UseWebSockets();

var store = app.Services.GetRequiredService<CoreStore>();
store.Initialize();

app.MapGet("/", () => Results.Redirect("/api/v1/health"));

app.MapGet("/api/v1/health", () => Results.Ok(new
{
    name = "Tinadec Core",
    status = "ok",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev",
    time = DateTimeOffset.UtcNow
}));

app.MapGet("/api/v1/doctor", (DoctorService doctor) => Results.Ok(doctor.Check()));

app.MapGet("/api/v1/readiness", (RuntimeReadinessService readiness) => Results.Ok(readiness.Check()));

app.MapGet("/api/v1/model-readiness", (ModelReadinessService readiness) => Results.Ok(readiness.Check()));

app.MapGet("/api/v1/projects", (CoreStore coreStore) => Results.Ok(coreStore.ListProjects()));

app.MapPost("/api/v1/projects", (CreateProjectRequest request, CoreStore coreStore, EventHub events) =>
{
    if (string.IsNullOrWhiteSpace(request.Path) || !Directory.Exists(request.Path))
    {
        return Results.BadRequest(new TinadecError("PROJECT_PATH_INVALID", "The selected project path does not exist."));
    }

    var name = string.IsNullOrWhiteSpace(request.Name) ? Path.GetFileName(request.Path.TrimEnd(Path.DirectorySeparatorChar)) : request.Name.Trim();
    var project = coreStore.CreateProject(name, request.Path);
    Publish(events, coreStore.AppendNewEvent("project.created", null, new JsonObject
    {
        ["project_id"] = project.Id,
        ["path"] = project.Path
    }, ["workspace.project"]));

    return Results.Created($"/api/v1/projects/{project.Id}", project);
});

app.MapGet("/api/v1/sessions", (string? projectId, CoreStore coreStore) =>
{
    return Results.Ok(coreStore.ListSessions(projectId));
});

app.MapPost("/api/v1/sessions", (CreateSessionRequest request, CoreStore coreStore, EventHub events) =>
{
    var session = coreStore.CreateSession(request.ProjectId, request.Title);
    Publish(events, coreStore.AppendNewEvent("session.created", session.Id, new JsonObject
    {
        ["session_id"] = session.Id,
        ["project_id"] = session.ProjectId
    }, ["agent.session"]));

    return Results.Created($"/api/v1/sessions/{session.Id}", session);
});

app.MapGet("/api/v1/sessions/{sessionId}/messages", (string sessionId, CoreStore coreStore) =>
{
    return Results.Ok(coreStore.ListMessages(sessionId));
});

app.MapPost("/api/v1/sessions/{sessionId}/messages", async (
    string sessionId,
    PostMessageRequest request,
    CoreStore coreStore,
    EventHub events,
    OrchestratorService orchestrator,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Content))
    {
        return Results.BadRequest(new TinadecError("MESSAGE_EMPTY", "Message content is required."));
    }

    var userMessage = coreStore.AddMessage(sessionId, "user", request.Content.Trim());
    Publish(events, coreStore.AppendNewEvent("message.created", sessionId, new JsonObject
    {
        ["message_id"] = userMessage.Id,
        ["role"] = userMessage.Role
    }, ["agent.message"]));

    var orchestration = orchestrator.CreateRunForMessage(sessionId, userMessage.Id, userMessage.Content);
    await orchestrator.DispatchReadOnlyToolsAsync(orchestration, userMessage.Content, cancellationToken);

    var modelCompletion = await orchestrator.CompleteRunWithModelAsync(orchestration, cancellationToken);
    return modelCompletion.AssistantMessage is null
        ? Results.Json(new TinadecError("MODEL_INVOCATION_FAILED", "Model invocation failed."), statusCode: StatusCodes.Status502BadGateway)
        : Results.Ok(modelCompletion.AssistantMessage);
});

app.MapGet("/api/v1/sessions/{sessionId}/orchestration", (string sessionId, CoreStore coreStore) =>
{
    return Results.Ok(coreStore.GetOrchestrationSnapshot(sessionId));
});

app.MapGet("/api/v1/sessions/{sessionId}/tool-executions", (
    string sessionId,
    string? runId,
    int? limit,
    ToolExecutionTimelineService timeline) =>
{
    return Results.Ok(timeline.ListForSession(sessionId, runId, limit));
});

app.MapGet("/api/v1/sessions/{sessionId}/runs", (string sessionId, CoreStore coreStore) =>
{
    return Results.Ok(coreStore.ListRuns(sessionId));
});

app.MapGet("/api/v1/sessions/{sessionId}/task-nodes", (string sessionId, CoreStore coreStore) =>
{
    return Results.Ok(coreStore.ListTaskNodes(sessionId));
});

app.MapGet("/api/v1/sessions/{sessionId}/context-packs", (string sessionId, CoreStore coreStore) =>
{
    return Results.Ok(coreStore.ListContextPacks(sessionId));
});

app.MapGet("/api/v1/sessions/{sessionId}/supervision-findings", (string sessionId, CoreStore coreStore) =>
{
    return Results.Ok(coreStore.ListSupervisionFindings(sessionId));
});

app.MapPost("/api/v1/runs/{runId}/tools/{toolId}/execute", async (
    string runId,
    string toolId,
    CodeToolExecuteRequest request,
    ToolExecutionService toolExecution,
    CancellationToken cancellationToken) =>
{
    var response = await toolExecution.ExecuteAsync(runId, toolId, request, cancellationToken);
    if (response is null)
    {
        return Results.NotFound(new TinadecError("TOOL_EXECUTION_NOT_FOUND", "Run or tool was not found."));
    }

    return string.Equals(response.Status, "approval_required", StringComparison.OrdinalIgnoreCase)
        ? Results.Accepted($"/api/v1/approvals/{response.Approval?.Id}", response)
        : Results.Ok(response);
});

app.MapGet("/api/v1/events", async (HttpContext context, string? sessionId, CoreStore coreStore, EventHub events) =>
{
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream";

    foreach (var envelope in coreStore.ListEvents(sessionId).TakeLast(50))
    {
        await WriteSseAsync(context.Response, envelope, context.RequestAborted);
    }

    await foreach (var envelope in events.Subscribe(context.RequestAborted))
    {
        if (!string.IsNullOrWhiteSpace(sessionId) && envelope.SessionId != sessionId)
        {
            continue;
        }

        await WriteSseAsync(context.Response, envelope, context.RequestAborted);
    }
});

app.MapGet("/api/v1/approvals", (string? status, string? sessionId, CoreStore coreStore) =>
{
    return Results.Ok(coreStore.ListApprovals(status, sessionId));
});

app.MapPost("/api/v1/approvals", (CreateApprovalRequest request, CoreStore coreStore, EventHub events) =>
{
    var approval = coreStore.CreateApproval(request);
    Publish(events, coreStore.AppendNewEvent("approval.requested", approval.SessionId, new JsonObject
    {
        ["approval_id"] = approval.Id,
        ["kind"] = approval.Kind,
        ["summary"] = approval.Summary
    }, ["approval.ask"]));

    return Results.Created($"/api/v1/approvals/{approval.Id}", approval);
});

app.MapPost("/api/v1/approvals/{approvalId}/decision", (string approvalId, DecideApprovalRequest request, CoreStore coreStore, EventHub events) =>
{
    var normalized = request.Decision.Trim().ToLowerInvariant();
    if (normalized is not ("approved" or "rejected"))
    {
        return Results.BadRequest(new TinadecError("APPROVAL_DECISION_INVALID", "Decision must be approved or rejected."));
    }

    var approval = coreStore.DecideApproval(approvalId, normalized);
    if (approval is null)
    {
        return Results.NotFound(new TinadecError("APPROVAL_NOT_FOUND", "Approval request was not found."));
    }

    Publish(events, coreStore.AppendNewEvent($"approval.{normalized}", approval.SessionId, new JsonObject
    {
        ["approval_id"] = approval.Id,
        ["kind"] = approval.Kind
    }, ["approval.decide"]));

    return Results.Ok(approval);
});

app.MapPost("/api/v1/tools/shell", (CreateApprovalRequest request, CoreStore coreStore, EventHub events) =>
{
    var approval = coreStore.CreateApproval(request with
    {
        Kind = "shell",
        Summary = string.IsNullOrWhiteSpace(request.Summary) ? "Run shell command" : request.Summary
    });

    Publish(events, coreStore.AppendNewEvent("tool.shell.approval_required", approval.SessionId, new JsonObject
    {
        ["approval_id"] = approval.Id,
        ["command"] = approval.Command,
        ["cwd"] = approval.Cwd
    }, ["tool.shell", "approval.ask"]));

    return Results.Accepted($"/api/v1/approvals/{approval.Id}", approval);
});

app.MapGet("/api/v1/model-provider-templates", (IModelManagementService modelManagement) =>
{
    return Results.Ok(modelManagement.ListProviderTemplates());
});

app.MapGet("/api/v1/model-providers", (IModelManagementService modelManagement) =>
{
    return Results.Ok(modelManagement.ListProviders());
});

app.MapPost("/api/v1/model-providers", (SaveModelProviderInstanceRequest request, IModelManagementService modelManagement, CoreStore coreStore, EventHub events) =>
{
    var saved = modelManagement.CreateProvider(request);

    Publish(events, coreStore.AppendNewEvent("model.provider.saved", null, new JsonObject
    {
        ["provider_instance_id"] = saved.Id,
        ["driver"] = saved.Driver,
        ["connection_kind"] = saved.ConnectionKind
    }, ["model.provider"]));

    return Results.Created($"/api/v1/model-providers/{saved.Id}", saved);
});

app.MapPut("/api/v1/model-providers/{providerInstanceId}", (string providerInstanceId, SaveModelProviderInstanceRequest request, IModelManagementService modelManagement, CoreStore coreStore, EventHub events) =>
{
    var saved = modelManagement.UpdateProvider(providerInstanceId, request);
    if (saved is null)
    {
        return Results.NotFound(new TinadecError("MODEL_PROVIDER_NOT_FOUND", "Model provider instance was not found."));
    }

    Publish(events, coreStore.AppendNewEvent("model.provider.saved", null, new JsonObject
    {
        ["provider_instance_id"] = saved.Id,
        ["driver"] = saved.Driver,
        ["connection_kind"] = saved.ConnectionKind
    }, ["model.provider"]));

    return Results.Ok(saved);
});

app.MapDelete("/api/v1/model-providers/{providerInstanceId}", (string providerInstanceId, IModelManagementService modelManagement, CoreStore coreStore, EventHub events) =>
{
    var deleted = modelManagement.DeleteProvider(providerInstanceId);
    if (deleted is null)
    {
        return Results.NotFound(new TinadecError("MODEL_PROVIDER_NOT_FOUND", "Model provider instance was not found."));
    }

    Publish(events, coreStore.AppendNewEvent("model.provider.deleted", null, new JsonObject
    {
        ["provider_instance_id"] = providerInstanceId,
        ["driver"] = deleted.Driver,
        ["connection_kind"] = deleted.ConnectionKind
    }, ["model.provider"]));

    return Results.NoContent();
});

app.MapGet("/api/v1/model-routes", (IModelManagementService modelManagement) =>
{
    return Results.Ok(modelManagement.ListRoutes());
});

app.MapPut("/api/v1/model-routes/{purpose}", (string purpose, SaveModelRouteRequest request, IModelManagementService modelManagement, CoreStore coreStore, EventHub events) =>
{
    var saved = modelManagement.SaveRoute(purpose, request);
    if (saved is null)
    {
        return Results.NotFound(new TinadecError("MODEL_PROVIDER_NOT_FOUND", "Model provider instance was not found."));
    }

    Publish(events, coreStore.AppendNewEvent("model.route.updated", null, new JsonObject
    {
        ["purpose"] = saved.Purpose,
        ["provider_instance_id"] = saved.ProviderInstanceId,
        ["model"] = saved.Model
    }, ["model.route"]));

    return Results.Ok(saved);
});

app.MapGet("/api/v1/model-settings", (IModelManagementService modelManagement) =>
{
    return Results.Ok(modelManagement.GetSettings());
});

app.MapPut("/api/v1/model-settings", (SaveModelSettingsRequest request, IModelManagementService modelManagement) =>
{
    return Results.Ok(modelManagement.SaveSettings(request));
});

app.MapGet("/api/v1/market/sources", (CoreStore coreStore) => Results.Ok(coreStore.ListExtensionSources()));

app.MapPost("/api/v1/market/sources", (CreateExtensionSourceRequest request, CoreStore coreStore, EventHub events) =>
{
    var source = coreStore.CreateExtensionSource(request);
    Publish(events, coreStore.AppendNewEvent("market.source.created", null, new JsonObject
    {
        ["source_id"] = source.Id,
        ["kind"] = source.Kind
    }, ["market.source"]));

    return Results.Created($"/api/v1/market/sources/{source.Id}", source);
});

app.MapPost("/api/v1/market/sources/{sourceId}/refresh", (string sourceId, CoreStore coreStore, EventHub events) =>
{
    var source = coreStore.RefreshExtensionSource(sourceId);
    if (source is null)
    {
        return Results.NotFound(new TinadecError("EXTENSION_SOURCE_NOT_FOUND", "Extension source was not found."));
    }

    Publish(events, coreStore.AppendNewEvent("market.source.refreshed", null, new JsonObject
    {
        ["source_id"] = source.Id,
        ["kind"] = source.Kind
    }, ["market.source"]));

    return Results.Ok(source);
});

app.MapGet("/api/v1/market/catalog", (string? kind, string? query, string? sourceId, CoreStore coreStore) =>
{
    return Results.Ok(coreStore.ListMarketCatalog(kind, query, sourceId));
});

app.MapGet("/api/v1/market/catalog/{catalogId}", (string catalogId, CoreStore coreStore) =>
{
    var item = coreStore.GetMarketCatalogItem(catalogId);
    return item is null
        ? Results.NotFound(new TinadecError("MARKET_CATALOG_ITEM_NOT_FOUND", "Market catalog item was not found."))
        : Results.Ok(item);
});

app.MapPost("/api/v1/extensions/install-preview", (InstallExtensionPreviewRequest request, CoreStore coreStore) =>
{
    try
    {
        return Results.Ok(coreStore.PreviewExtensionInstall(request));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new TinadecError("EXTENSION_PREVIEW_INVALID", ex.Message));
    }
});

app.MapPost("/api/v1/extensions/install", (InstallExtensionRequest request, CoreStore coreStore, EventHub events) =>
{
    try
    {
        var result = coreStore.InstallExtension(request);
        if (result.ApprovalRequired && result.Approval is not null)
        {
            Publish(events, coreStore.AppendNewEvent("extension.install.approval_required", null, new JsonObject
            {
                ["approval_id"] = result.Approval.Id,
                ["extension_id"] = result.Preview.ExtensionId,
                ["kind"] = result.Preview.Kind
            }, ["extension.install", "approval.ask"]));
            return Results.Accepted($"/api/v1/approvals/{result.Approval.Id}", result);
        }

        Publish(events, coreStore.AppendNewEvent("extension.installed", null, new JsonObject
        {
            ["installed_extension_id"] = result.Extension?.Id,
            ["extension_id"] = result.Extension?.ExtensionId,
            ["kind"] = result.Extension?.Kind,
            ["enabled"] = false
        }, ["extension.install"]));
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new TinadecError("EXTENSION_INSTALL_INVALID", ex.Message));
    }
});

app.MapGet("/api/v1/extensions/installed", (CoreStore coreStore) => Results.Ok(coreStore.ListInstalledExtensions()));

app.MapPost("/api/v1/extensions/{extensionId}/enable", (string extensionId, CoreStore coreStore, EventHub events) =>
{
    var extension = coreStore.SetExtensionEnabled(extensionId, true);
    if (extension is null)
    {
        return Results.NotFound(new TinadecError("EXTENSION_NOT_FOUND", "Installed extension was not found."));
    }

    Publish(events, coreStore.AppendNewEvent("extension.enabled", null, new JsonObject
    {
        ["installed_extension_id"] = extension.Id,
        ["extension_id"] = extension.ExtensionId,
        ["kind"] = extension.Kind
    }, ["extension.enable"]));

    return Results.Ok(extension);
});

app.MapPost("/api/v1/extensions/{extensionId}/disable", (string extensionId, CoreStore coreStore, EventHub events) =>
{
    var extension = coreStore.SetExtensionEnabled(extensionId, false);
    if (extension is null)
    {
        return Results.NotFound(new TinadecError("EXTENSION_NOT_FOUND", "Installed extension was not found."));
    }

    Publish(events, coreStore.AppendNewEvent("extension.disabled", null, new JsonObject
    {
        ["installed_extension_id"] = extension.Id,
        ["extension_id"] = extension.ExtensionId,
        ["kind"] = extension.Kind
    }, ["extension.disable"]));

    return Results.Ok(extension);
});

app.MapPost("/api/v1/extensions/{extensionId}/update", (string extensionId, CoreStore coreStore) =>
{
    var extension = coreStore.SetExtensionEnabled(extensionId, false);
    return extension is null
        ? Results.NotFound(new TinadecError("EXTENSION_NOT_FOUND", "Installed extension was not found."))
        : Results.Ok(extension with { Status = "update_available", StatusMessage = "Update check completed. No newer version is bundled in this MVP slice." });
});

app.MapDelete("/api/v1/extensions/{extensionId}", (string extensionId, CoreStore coreStore, EventHub events) =>
{
    var deleted = coreStore.DeleteInstalledExtension(extensionId);
    if (!deleted)
    {
        return Results.NotFound(new TinadecError("EXTENSION_NOT_FOUND", "Installed extension was not found."));
    }

    Publish(events, coreStore.AppendNewEvent("extension.uninstalled", null, new JsonObject
    {
        ["installed_extension_id"] = extensionId
    }, ["extension.uninstall"]));

    return Results.NoContent();
});

app.MapGet("/api/v1/mcp/servers", (CoreStore coreStore) => Results.Ok(coreStore.ListMcpServers()));

app.MapGet("/api/v1/mcp/servers/{serverId}/tools", (string serverId, CoreStore coreStore) =>
{
    var server = coreStore.ListMcpServers().FirstOrDefault(item => item.Id == serverId);
    return server is null
        ? Results.NotFound(new TinadecError("MCP_SERVER_NOT_FOUND", "MCP server was not found."))
        : Results.Ok(server.Tools);
});

app.MapPost("/api/v1/mcp/servers/{serverId}/reload", (string serverId, CoreStore coreStore) =>
{
    var server = coreStore.ReloadMcpServer(serverId);
    return server is null
        ? Results.NotFound(new TinadecError("MCP_SERVER_NOT_FOUND", "MCP server was not found."))
        : Results.Ok(server);
});

app.MapGet("/api/v1/acp/adapters", (CoreStore coreStore) => Results.Ok(coreStore.ListAcpAdapters()));

app.MapPost("/api/v1/acp/adapters/{adapterId}/probe", (string adapterId, CoreStore coreStore) =>
{
    var adapter = coreStore.ProbeAcpAdapter(adapterId);
    return adapter is null
        ? Results.NotFound(new TinadecError("ACP_ADAPTER_NOT_FOUND", "ACP adapter was not found."))
        : Results.Ok(adapter);
});

app.MapGet("/api/v1/agent-modes", (CoreStore coreStore) => Results.Ok(coreStore.ListAgentModes()));

app.MapGet("/api/v1/tools/search", (
    string? query,
    string? domain,
    string? source,
    string? risk,
    int? limit,
    ToolSearchService search) =>
{
    return Results.Ok(search.Search(query, domain, source, risk, limit));
});

app.MapGet("/api/v1/tools", (IToolRegistry tools) => Results.Ok(tools.ListTools()));

app.MapGet("/api/v1/harness/manifest", (HarnessManifestService manifest) => Results.Ok(manifest.Build()));

app.MapGet("/api/v1/prompt-fragments", (
    string? scope,
    string? targetAgentId,
    string? category,
    bool? enabled,
    CoreStore coreStore) =>
{
    return Results.Ok(coreStore.ListPromptFragments(scope, targetAgentId, category, enabled));
});

app.MapPost("/api/v1/prompt-fragments", (SavePromptFragmentRequest request, CoreStore coreStore, EventHub events) =>
{
    try
    {
        var fragment = coreStore.CreatePromptFragment(request);
        Publish(events, coreStore.AppendNewEvent("prompt.fragment.created", null, new JsonObject
        {
            ["fragment_id"] = fragment.Id,
            ["scope"] = fragment.Scope,
            ["category"] = fragment.Category,
            ["target_agent_id"] = fragment.TargetAgentId
        }, ["prompt.fragment"]));

        return Results.Created($"/api/v1/prompt-fragments/{fragment.Id}", fragment);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new TinadecError("PROMPT_FRAGMENT_INVALID", ex.Message));
    }
});

app.MapPut("/api/v1/prompt-fragments/{fragmentId}", (string fragmentId, SavePromptFragmentRequest request, CoreStore coreStore, EventHub events) =>
{
    try
    {
        var fragment = coreStore.UpdatePromptFragment(fragmentId, request);
        if (fragment is null)
        {
            return Results.NotFound(new TinadecError("PROMPT_FRAGMENT_NOT_FOUND", "Prompt fragment was not found."));
        }

        Publish(events, coreStore.AppendNewEvent("prompt.fragment.updated", null, new JsonObject
        {
            ["fragment_id"] = fragment.Id,
            ["enabled"] = fragment.Enabled,
            ["priority"] = fragment.Priority
        }, ["prompt.fragment"]));

        return Results.Ok(fragment);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new TinadecError("PROMPT_FRAGMENT_READ_ONLY", ex.Message));
    }
});

app.MapDelete("/api/v1/prompt-fragments/{fragmentId}", (string fragmentId, CoreStore coreStore, EventHub events) =>
{
    try
    {
        var deleted = coreStore.DeletePromptFragment(fragmentId);
        if (!deleted)
        {
            return Results.NotFound(new TinadecError("PROMPT_FRAGMENT_NOT_FOUND", "Prompt fragment was not found."));
        }

        Publish(events, coreStore.AppendNewEvent("prompt.fragment.deleted", null, new JsonObject
        {
            ["fragment_id"] = fragmentId
        }, ["prompt.fragment"]));

        return Results.NoContent();
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new TinadecError("PROMPT_FRAGMENT_READ_ONLY", ex.Message));
    }
});

app.MapPost("/api/v1/prompt-fragments/{fragmentId}/clone", (string fragmentId, CoreStore coreStore, EventHub events) =>
{
    var fragment = coreStore.ClonePromptFragment(fragmentId);
    if (fragment is null)
    {
        return Results.NotFound(new TinadecError("PROMPT_FRAGMENT_NOT_FOUND", "Prompt fragment was not found."));
    }

    Publish(events, coreStore.AppendNewEvent("prompt.fragment.cloned", null, new JsonObject
    {
        ["source_fragment_id"] = fragmentId,
        ["fragment_id"] = fragment.Id
    }, ["prompt.fragment"]));

    return Results.Created($"/api/v1/prompt-fragments/{fragment.Id}", fragment);
});

app.MapPost("/api/v1/prompt-context/preview", async (
    PromptContextPreviewRequest request,
    PromptContextService promptContext,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await promptContext.PreviewAsync(request, cancellationToken: cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new TinadecError("PROMPT_CONTEXT_PREVIEW_INVALID", ex.Message));
    }
});

app.MapGet("/api/v1/agents", (CoreStore coreStore) => Results.Ok(coreStore.ListAgentProfiles()));

app.MapPut("/api/v1/agents/{agentId}", (string agentId, SaveAgentProfileRequest request, CoreStore coreStore, EventHub events) =>
{
    var agent = coreStore.SaveAgentProfile(agentId, request);
    if (agent is null)
    {
        return Results.NotFound(new TinadecError("AGENT_NOT_FOUND", "Agent profile was not found."));
    }

    Publish(events, coreStore.AppendNewEvent("agent.profile.updated", null, new JsonObject
    {
        ["agent_id"] = agent.Id,
        ["layer"] = agent.Layer,
        ["mode"] = agent.Mode,
        ["enabled"] = agent.Enabled
    }, ["agent.profile"]));

    return Results.Ok(agent);
});

app.MapPut("/api/v1/agents/{agentId}/mode", (string agentId, UpdateAgentModeRequest request, CoreStore coreStore, EventHub events) =>
{
    var agent = coreStore.UpdateAgentMode(agentId, request.Mode);
    if (agent is null)
    {
        return Results.NotFound(new TinadecError("AGENT_NOT_FOUND", "Agent profile was not found."));
    }

    Publish(events, coreStore.AppendNewEvent("agent.mode.updated", null, new JsonObject
    {
        ["agent_id"] = agent.Id,
        ["mode"] = agent.Mode
    }, ["agent.profile"]));

    return Results.Ok(agent);
});

app.MapGet("/api/v1/agent-candidates", (CoreStore coreStore) => Results.Ok(coreStore.ListAgentCandidates()));

// --- Agent Debug Studio API Endpoints ---
app.MapDebugApi();
app.MapSimulationApi();

app.MapGet("/api/v1/debug/ws", async (HttpContext context, DebugWebSocketHandler handler, CancellationToken cancellationToken) =>
{
    await handler.HandleAsync(context, cancellationToken);
});

app.Run();

static async Task WriteSseAsync(HttpResponse response, EventEnvelope envelope, CancellationToken cancellationToken)
{
    var json = JsonSerializer.Serialize(envelope, TinadecJson.Options);
    await response.WriteAsync($"id: {envelope.Seq}\n", cancellationToken);
    await response.WriteAsync($"event: {envelope.Type}\n", cancellationToken);
    await response.WriteAsync($"data: {json}\n\n", cancellationToken);
    await response.Body.FlushAsync(cancellationToken);
}

static void Publish(EventHub hub, EventEnvelope envelope)
{
    hub.Publish(envelope);
}

public partial class Program;
