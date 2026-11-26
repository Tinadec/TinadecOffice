using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.AspNetCore;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Contracts.Events;
using TinadecCore.DmaEA;
using TinadecCore.Lifecycle;
using TinadecCore.Memory;
using TinadecCore.Persistence;
using TinadecCore.Runtime;
using TinadecCore.Tools;

namespace TinadecCore.AcpServer;

/// <summary>
/// Mode A (default standalone <c>--acp</c>): boots TinadecCore's full DI, SQLite persistence, storage scopes,
/// governance, and <c>FullDuplexRunEngine</c> in-process with zero Kestrel HTTP listener and 100% stderr logging.
/// </summary>
internal sealed class EmbeddedAcpCoreBackend : IAcpCoreBackend
{
    private const int MaxAttachmentsPerPrompt = 8;

    private static readonly JsonSerializerOptions SnakeJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private const string FallbackStarterPackJson = """
        {
          "api_version": "tinadec.io/agent-pack/v1alpha1",
          "kind": "AgentPack",
          "metadata": {
            "pack_id": "tinadec.acp.starter-pack",
            "owner": "tinadec.acp",
            "product_id": "tinadec.office",
            "name": "TinadecAcpStarterPack",
            "version": "0.1.0"
          },
          "compatibility": {
            "minimum_core_version": "0.1.0",
            "required_core_capabilities": [
              "agent_pack_lifecycle",
              "versioned_agent_configuration",
              "frozen_agent_version_binding",
              "graph_mode_packs"
            ]
          },
          "resources": {
            "agents": [
              {
                "resource_key": "solo",
                "slug": "solo",
                "display_name": "Solo Agent",
                "description": "Direct governed coding agent for interactive ACP sessions.",
                "layer": "operation",
                "role": "session_coordinator",
                "capabilities": [
                  "user.respond",
                  "task.dispatch",
                  "agent.create_temporary",
                  "agent.spawn"
                ],
                "model_strategy": {
                  "kind": "inherit"
                },
                "tool_scope": [
                  "*"
                ],
                "system_prompt": "Act as a precise software engineering agent inside the workspace. Inspect files before editing, respect approval boundaries, and report concise, verifiable conclusions.",
                "enabled": true,
                "base_prompt_pipeline_ref": "prompt:baseline-prompt"
              },
              {
                "resource_key": "meeting",
                "slug": "meeting",
                "display_name": "Meeting Agent",
                "description": "Multi-agent coordinator.",
                "layer": "operation",
                "role": "session_coordinator",
                "capabilities": [
                  "user.respond",
                  "task.dispatch",
                  "agent.create_temporary"
                ],
                "model_strategy": {
                  "kind": "inherit"
                },
                "tool_scope": [],
                "system_prompt": "Coordinate multi-step engineering tasks across planner and worker agents and report concise evidence-backed conclusions.",
                "enabled": true,
                "base_prompt_pipeline_ref": "prompt:baseline-prompt"
              },
              {
                "resource_key": "task_planner",
                "slug": "task_planner",
                "display_name": "Task Planner",
                "description": "Execution planner.",
                "layer": "execution",
                "role": "execution_coordinator",
                "capabilities": [
                  "task.plan",
                  "task.replan",
                  "agent.create_temporary"
                ],
                "model_strategy": {
                  "kind": "inherit"
                },
                "tool_scope": [
                  "*"
                ],
                "system_prompt": "Decompose goals into clear executable tasks with concrete verification criteria.",
                "enabled": true,
                "base_prompt_pipeline_ref": "prompt:baseline-prompt"
              },
              {
                "resource_key": "worker.general",
                "slug": "worker.general",
                "display_name": "General Worker",
                "description": "General workspace worker.",
                "layer": "execution",
                "role": "task_executor",
                "capabilities": [
                  "task.execute",
                  "tool.code",
                  "tool.file"
                ],
                "model_strategy": {
                  "kind": "inherit"
                },
                "tool_scope": [
                  "*"
                ],
                "system_prompt": "Execute delegated workspace operations accurately and report verifiable results.",
                "enabled": true,
                "base_prompt_pipeline_ref": "prompt:baseline-prompt"
              }
            ],
            "prompt_pipelines": [
              {
                "resource_key": "baseline-prompt",
                "slug": "baseline-prompt",
                "display_name": "Baseline Prompt",
                "description": "ACP starter prompt pipeline.",
                "graph": {
                  "nodes": [
                    {
                      "id": "template",
                      "type": "template"
                    },
                    {
                      "id": "assemble",
                      "type": "assemble"
                    }
                  ],
                  "edges": [
                    {
                      "source": "template",
                      "target": "assemble"
                    }
                  ]
                }
              }
            ],
            "modes": [
              {
                "resource_key": "default-mode",
                "slug": "default-mode",
                "display_name": "Default Mode",
                "description": "Direct governed ACP coding mode.",
                "orchestration_tier": "solo_dispatch",
                "default_options": {
                  "plan_first": false,
                  "spec_enabled": false,
                  "multi_agent": false,
                  "bulletin_board": false,
                  "worktree": false
                },
                "nodes": [
                  {
                    "node_key": "n-01-solo",
                    "agent_ref": "agent:solo",
                    "layer": "operation",
                    "label": "Solo Agent",
                    "config": {
                      "conversation": true
                    },
                    "relationship": {
                      "duty": "Execute workspace inspections, edits, and commands directly with full approval governance.",
                      "inputs_outputs": {
                        "receives": "user goal",
                        "returns": "final answer"
                      },
                      "allowed_dispatch_targets": [
                        "worker.general"
                      ],
                      "success_criteria": [],
                      "agent_types": [
                        "worker.general"
                      ]
                    },
                    "position": {}
                  }
                ],
                "edges": [],
                "bindings": [
                  {
                    "node_key": "n-01-solo",
                    "agent_ref": "agent:solo",
                    "tool_switches": {},
                    "envelope": {
                      "resources": {
                        "read": [
                          ""
                        ],
                        "write": [
                          ""
                        ]
                      },
                      "spawn": {
                        "max_depth": 1,
                        "max_agents_per_run": 8,
                        "max_parallel_workers": 4
                      }
                    },
                    "includes_core_reserved": false
                  },
                  {
                    "agent_ref": "agent:worker.general",
                    "tool_switches": {},
                    "envelope": {
                      "resources": {
                        "read": [
                          ""
                        ],
                        "write": [
                          ""
                        ]
                      }
                    },
                    "includes_core_reserved": false
                  }
                ],
                "canvas_layout": {}
              },
              {
                "resource_key": "team",
                "slug": "team",
                "display_name": "Team",
                "description": "Two-layer coordinated multi-agent mode.",
                "nodes": [
                  {
                    "node_key": "n-01-meeting",
                    "agent_ref": "agent:meeting",
                    "layer": "operation",
                    "label": "Meeting Agent",
                    "config": {},
                    "position": {}
                  },
                  {
                    "node_key": "n-02-task-planner",
                    "agent_ref": "agent:task_planner",
                    "layer": "execution",
                    "label": "Task Planner",
                    "config": {},
                    "position": {}
                  },
                  {
                    "node_key": "n-03-worker-general",
                    "agent_ref": "agent:worker.general",
                    "layer": "execution",
                    "label": "General Worker",
                    "config": {},
                    "position": {}
                  }
                ],
                "edges": [],
                "canvas_layout": {}
              }
            ]
          },
          "activation": {
            "workspace_defaults": {
              "agent_ref": "agent:solo",
              "mode_ref": "mode:default-mode",
              "prompt_pipeline_ref": "prompt:baseline-prompt"
            }
          }
        }
        """;

    private readonly IHost _host;
    private readonly bool _scopesEnabled;
    private readonly ConcurrentDictionary<Guid, string?> _sessionStorageIds = new();
    private readonly ConcurrentDictionary<Guid, string> _sessionCwds = new();
    private int _disposed;

    private EmbeddedAcpCoreBackend(IHost host, bool scopesEnabled)
    {
        _host = host;
        _scopesEnabled = scopesEnabled;
    }

    public IServiceProvider HostServices => _host.Services;

    public ILogger<T> CreateLogger<T>() =>
        _host.Services.GetRequiredService<ILoggerFactory>().CreateLogger<T>();

    public static void VerifyUserStorageLockAvailable(AcpCliOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ForwardedHostArgs.Any(a =>
            a.Contains("TinadecStorage:Enabled=false", StringComparison.OrdinalIgnoreCase)
            || a.Contains("environment=Testing", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var userRoot = StorageScopePaths.UserRoot(options.StorageRoot);
        var stateDir = Path.Combine(userRoot, "state");
        var lockFile = Path.Combine(stateDir, "host.lock");
        if (!File.Exists(lockFile))
        {
            return;
        }

        try
        {
            using var probe = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                "Tinadec user storage root is already locked by another running TinadecCore instance. " +
                "Attach to the running Core with '--core-url http://127.0.0.1:<port>' or specify an isolated '--storage-root <path>'.",
                ex);
        }
    }

    public static async Task<EmbeddedAcpCoreBackend> StartAsync(
        AcpCliOptions options,
        Action<HostApplicationBuilder>? configureBuilder,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = options.ForwardedHostArgs.ToArray(),
            ContentRootPath = AppContext.BaseDirectory
        });

        builder.Services.Configure<ConsoleLifetimeOptions>(o => o.SuppressStatusMessages = true);

        if (options.StorageRoot is not null)
        {
            builder.Configuration["TinadecStorage:UserRoot"] = options.StorageRoot;
        }

        var scopesEnabled = builder.Configuration.GetValue<bool?>("TinadecStorage:Enabled")
            ?? !builder.Environment.IsEnvironment("Testing");
        var initialConfiguration = scopesEnabled
            && !File.Exists(Path.Combine(
                StorageScopePaths.UserRoot(builder.Configuration["TinadecStorage:UserRoot"]),
                "config",
                "agents.toml"));

        var userScope = scopesEnabled ? StorageHostBootstrap.Configure(builder.Configuration) : null;
        if (userScope is not null)
        {
            builder.Services.AddSingleton<IScopeStorageLocations>(userScope);
        }

        // Strict ACP stdio purity: every log level from Trace upward goes exclusively to stderr.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        if (userScope is not null)
        {
            builder.Logging.AddProvider(new ScopeDiagnosticLoggerProvider(userScope));
        }

        builder.Services.AddTinadecPersistence(builder.Configuration, builder.Environment.ContentRootPath);
        if (scopesEnabled)
        {
            builder.Services.AddHostedService<StorageHostShutdownService>();
        }

        builder.Services.AddTinadecCore();
        builder.Services.AddTinadecCoreHttp();

        if (scopesEnabled)
        {
            builder.Services.AddTinadecConfigurationFiles();
            builder.Services.AddTinadecStorageScopes();
            builder.Services.AddTinadecSessionTransfers();
            builder.Services.AddSingleton<UserStorageHostLease>();
            builder.Services.AddSingleton<IProtectedStorageRoots>(
                sp => new HostProtectedRoots(sp.GetRequiredService<IStorageScopeRegistry>(), "user"));
        }

        configureBuilder?.Invoke(builder);

        var host = builder.Build();
        try
        {
            if (scopesEnabled)
            {
                try
                {
                    _ = host.Services.GetRequiredService<UserStorageHostLease>();
                }
                catch (Exception ex) when (FindInnerIoException(ex) is not null)
                {
                    throw new InvalidOperationException(
                        "Tinadec user storage root is already locked by another running TinadecCore instance. " +
                        "Attach to the running Core with '--core-url http://127.0.0.1:<port>' or specify an isolated '--storage-root <path>'.",
                        ex);
                }
            }

            using (var scope = host.Services.CreateScope())
            {
                var storageOptions = scope.ServiceProvider.GetRequiredService<IOptions<TinadecPersistenceOptions>>().Value;
                var connection = scope.ServiceProvider.GetRequiredService<IDatabaseConnectionInfo>();
                if (storageOptions.Enabled && connection.IsConfigured)
                {
                    await scope.ServiceProvider.GetRequiredService<IStorageMigrationRunner>().RunAsync(cancellationToken).ConfigureAwait(false);
                    await scope.ServiceProvider.GetRequiredService<StorageLifecycleService>().ReconcileAsync(cancellationToken).ConfigureAwait(false);
                    if (scopesEnabled)
                    {
                        await scope.ServiceProvider.GetRequiredService<ProjectSessionLifecycleService>().RecoverPendingPurgesAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            using (var seedScope = host.Services.CreateScope())
            {
                if (!scopesEnabled || initialConfiguration)
                {
                    await DevSeed.SeedIfMissingAsync(seedScope.ServiceProvider, cancellationToken).ConfigureAwait(false);
                }

                await EnsureFallbackStarterPackAsync(seedScope.ServiceProvider, cancellationToken).ConfigureAwait(false);

                if (scopesEnabled)
                {
                    await seedScope.ServiceProvider.GetRequiredService<IConfigurationProjectionCoordinator>().ReconcileAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await host.StartAsync(cancellationToken).ConfigureAwait(false);
            return new EmbeddedAcpCoreBackend(host, scopesEnabled);
        }
        catch
        {
            try { host.Dispose(); } catch { }
            throw;
        }
    }

    public async Task<AcpSessionSnapshot> CreateSessionAsync(string cwd, CancellationToken cancellationToken)
    {
        var canonicalCwd = WorkspacePathSpelling.Canonical(Path.GetFullPath(cwd));
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(canonicalCwd));
        if (string.IsNullOrWhiteSpace(folderName)) folderName = "Workspace";

        if (_scopesEnabled && _host.Services.GetService<IStorageScopeRegistry>() is { } registry)
        {
            var descriptor = await registry.OpenAsync(new OpenStorageScopeRequest(canonicalCwd, folderName), cancellationToken).ConfigureAwait(false);
            await using var lease = await registry.AcquireAsync(descriptor.StorageId, cancellationToken).ConfigureAwait(false);
            var session = await CreateCoreSessionInScopeAsync(lease.Services, descriptor.ProjectId, folderName, cancellationToken).ConfigureAwait(false);
            _sessionStorageIds[session.Id] = descriptor.StorageId;
            _sessionCwds[session.Id] = canonicalCwd;
            return await BuildSessionSnapshotAsync(lease.Services, session, canonicalCwd, cancellationToken).ConfigureAwait(false);
        }

        var store = _host.Services.GetRequiredService<ProjectSessionStore>();
        var project = await FindOrCreateUnscopedProjectAsync(store, canonicalCwd, folderName, cancellationToken).ConfigureAwait(false);
        var unscopedSession = await CreateCoreSessionInScopeAsync(_host.Services, project.Id, folderName, cancellationToken).ConfigureAwait(false);
        _sessionStorageIds[unscopedSession.Id] = null;
        _sessionCwds[unscopedSession.Id] = canonicalCwd;
        return await BuildSessionSnapshotAsync(_host.Services, unscopedSession, canonicalCwd, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AcpSessionSnapshot> LoadSessionAsync(Guid sessionId, string cwd, CancellationToken cancellationToken)
    {
        var canonicalCwd = WorkspacePathSpelling.Canonical(Path.GetFullPath(cwd));
        await using var scoped = await AcquireSessionServicesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var session = await scoped.Services.GetRequiredService<ProjectSessionStore>().GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' was not found.");

        _sessionCwds[sessionId] = canonicalCwd;
        return await BuildSessionSnapshotAsync(scoped.Services, session, canonicalCwd, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AcpHistoryMessage>> ListHistoryAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var scoped = await AcquireSessionServicesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var store = scoped.Services.GetRequiredService<ProjectSessionStore>();
        var messages = await store.ListMessagesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var list = new List<AcpHistoryMessage>(messages.Count);
        foreach (var msg in messages)
        {
            if (msg.Role is not ("user" or "assistant")) continue;
            list.Add(new AcpHistoryMessage(msg.Id, msg.Role, msg.Content));
        }

        return list;
    }

    public async Task<AcpSessionSnapshot> SetModeAsync(Guid sessionId, string modeId, CancellationToken cancellationToken)
    {
        await using var scoped = await AcquireSessionServicesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var store = scoped.Services.GetRequiredService<ProjectSessionStore>();
        var session = await store.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' was not found.");

        var existingSpace = ProjectSessionStore.ReadSpaceOptions(session);
        var publishedModes = await ListPublishedModesAsync(scoped.Services, session.TenantId, session.WorkspaceId, cancellationToken).ConfigureAwait(false);

        SessionRecord? updated;
        if (AcpModeCatalog.TryResolveBuiltInSettings(modeId, existingSpace, out var permissionMode, out var spaceOptions, out var clearSpace))
        {
            updated = await store.UpdateSessionModeAsync(
                sessionId,
                modeVersionId: null,
                meetingModelOverride: null,
                cancellationToken: cancellationToken,
                permissionMode: permissionMode,
                spaceOptions: spaceOptions,
                clearSpaceOptions: clearSpace).ConfigureAwait(false);
        }
        else
        {
            var matched = publishedModes.FirstOrDefault(m => string.Equals(m.Slug, modeId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (matched is null)
            {
                throw new ArgumentException($"Unsupported ACP modeId '{modeId}'.");
            }

            updated = await store.UpdateSessionModeAsync(
                sessionId,
                modeVersionId: matched.ModeVersionId,
                meetingModelOverride: null,
                cancellationToken: cancellationToken,
                permissionMode: "default",
                clearSpaceOptions: true).ConfigureAwait(false);
        }

        if (updated is null)
        {
            throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
        }

        var cwd = _sessionCwds.GetValueOrDefault(sessionId, string.Empty);
        return await BuildSessionSnapshotAsync(scoped.Services, updated, cwd, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AcpSessionSnapshot> SetModelAsync(Guid sessionId, string modelId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new ArgumentException("modelId is required.");
        }

        await using var scoped = await AcquireSessionServicesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var store = scoped.Services.GetRequiredService<ProjectSessionStore>();
        var session = await store.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' was not found.");

        var catalog = await LoadModelCatalogAsync(scoped.Services, cancellationToken).ConfigureAwait(false);
        if (!TryMatchModelSelection(modelId, catalog.Entries, out var providerId, out var modelName))
        {
            throw new ArgumentException($"Unknown or unavailable modelId '{modelId}'.");
        }

        var modelResolver = scoped.Services.GetRequiredService<IAgentModelResolver>();
        try
        {
            await modelResolver.PreviewAsync(new ModelResolutionPreviewRequestDto
            {
                MeetingModelOverride = new MeetingModelOverrideDto
                {
                    ProviderInstanceId = providerId,
                    Model = modelName
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            throw new ArgumentException(ex.Message, ex);
        }

        var updated = await store.UpdateSessionModeAsync(
            sessionId,
            modeVersionId: null,
            meetingModelOverride: new SessionModelOverride(providerId, modelName),
            cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' was not found.");

        var cwd = _sessionCwds.GetValueOrDefault(sessionId, string.Empty);
        return await BuildSessionSnapshotAsync(scoped.Services, updated, cwd, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid> StoreAttachmentAsync(
        Guid sessionId,
        string fileName,
        string mediaType,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        await using var scoped = await AcquireSessionServicesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var attachments = scoped.Services.GetRequiredService<IMessageAttachmentStore>();
        await using var contentStream = new MemoryStream(bytes.ToArray(), writable: false);
        var stored = await attachments.StoreAsync(
            new StoreAttachmentRequest(sessionId, fileName, mediaType, contentStream)
            {
                Origin = AttachmentOrigins.Composer,
                ProjectId = (await scoped.Services.GetRequiredService<ProjectSessionStore>().GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false))?.ProjectId
            },
            cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' was not found.");

        return stored.Id;
    }

    public async Task<AcpPromptSubmission> SubmitPromptAsync(
        Guid sessionId,
        string content,
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken)
    {
        if (attachmentIds.Count > MaxAttachmentsPerPrompt)
        {
            throw new ArgumentException($"A message carries at most {MaxAttachmentsPerPrompt} attachments.");
        }

        await using var scoped = await AcquireSessionServicesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var store = scoped.Services.GetRequiredService<ProjectSessionStore>();
        var session = await store.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' was not found.");

        var attachments = scoped.Services.GetRequiredService<IMessageAttachmentStore>();
        if (attachmentIds.Count > 0)
        {
            var existing = await attachments.ListAsync(sessionId, cancellationToken).ConfigureAwait(false);
            var unboundIds = existing.Where(x => x.MessageId is null).Select(x => x.Id).ToHashSet();
            var missing = attachmentIds.Where(id => !unboundIds.Contains(id)).ToArray();
            if (missing.Length > 0)
            {
                throw new ArgumentException($"No unbound attachment in session '{sessionId}' matches id(s): {string.Join(", ", missing)}.");
            }
        }

        var cfgFactory = scoped.Services.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>();
        var globalConfig = scoped.Services.GetRequiredService<GlobalAgentConfiguration>();
        var modeVersionId = session.ModeVersionId
            ?? await AgentConfigurationLookup.GetDefaultModeVersionIdAsync(
                cfgFactory,
                globalConfig,
                session.TenantId,
                session.WorkspaceId,
                cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No published Agent Mode is configured for this workspace.");

        var modeFactory = await AgentConfigurationLookup.SelectModeFactoryAsync(
            cfgFactory,
            globalConfig,
            session.TenantId,
            session.WorkspaceId,
            modeVersionId,
            cancellationToken).ConfigureAwait(false);

        await using (var cfg = await modeFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var snapshotJson = await cfg.ModeVersions.AsNoTracking()
                .Where(x => x.Id == modeVersionId && x.TenantId == session.TenantId && x.WorkspaceId == session.WorkspaceId)
                .Select(x => x.SnapshotJson)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            var definitions = await cfg.AgentDefinitions.AsNoTracking()
                .Where(x => x.TenantId == session.TenantId && x.WorkspaceId == session.WorkspaceId)
                .Select(x => new ConversationIdentityResolver.DefinitionInput(x.Id, x.Slug, x.Layer, x.CapabilitiesJson))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var resolvedIdentity = ConversationIdentityResolver.Resolve(snapshotJson, definitions);
            var identityDiffers = resolvedIdentity is not null
                && !string.Equals(resolvedIdentity.TemplateSlug, session.ConversationTemplateSlug, StringComparison.OrdinalIgnoreCase);

            if (identityDiffers)
            {
                if (await store.GetContextRevisionAsync(sessionId, cancellationToken).ConfigureAwait(false) == 0)
                {
                    await store.AdoptConversationIdentityIfEmptyAsync(
                        sessionId,
                        resolvedIdentity!.NodeKey,
                        resolvedIdentity.TemplateSlug,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var lifecycleManager = scoped.Services.GetRequiredService<ILifecycleManager>();
                    if ((await lifecycleManager.ListActiveRunsAsync(sessionId, cancellationToken).ConfigureAwait(false)).Count == 0)
                    {
                        await store.MigrateConversationIdentityAsync(
                            sessionId,
                            resolvedIdentity!.NodeKey,
                            resolvedIdentity.TemplateSlug,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        SessionModelOverride? invocationOverride = null;
        if (session.MeetingModelOverrideProviderInstanceId is { } savedProviderId && savedProviderId != Guid.Empty)
        {
            var savedModel = new SessionModelOverride(savedProviderId, session.MeetingModelOverrideModel);
            var modelResolver = scoped.Services.GetRequiredService<IAgentModelResolver>();
            try
            {
                await modelResolver.PreviewAsync(new ModelResolutionPreviewRequestDto
                {
                    MeetingModelOverride = new MeetingModelOverrideDto
                    {
                        ProviderInstanceId = savedModel.ProviderInstanceId,
                        Model = savedModel.Model
                    }
                }, cancellationToken).ConfigureAwait(false);
                invocationOverride = savedModel;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
            {
                // Stale provider override falls back to workspace default route without failing admission.
            }
        }

        var coordinator = scoped.Services.GetRequiredService<IFullDuplexRunCoordinator>();
        var submission = await coordinator.SubmitAsync(
            new FullDuplexInvocation(
                sessionId,
                content,
                Guid.NewGuid().ToString("N"),
                session.PermissionMode,
                TargetRunId: null,
                ExpectedContextRevision: null,
                MeetingModelOverride: invocationOverride,
                ModeVersionId: modeVersionId,
                QueueBehindActiveRun: true,
                SpaceOptions: ProjectSessionStore.ReadSpaceOptions(session),
                SessionSettingsCaptured: true),
            cancellationToken).ConfigureAwait(false);

        if (attachmentIds.Count > 0)
        {
            await attachments.BindToMessageAsync(sessionId, submission.MessageId, attachmentIds, cancellationToken).ConfigureAwait(false);
        }

        return new AcpPromptSubmission(submission.RunId, submission.TurnId, submission.MessageId, StreamCursor: 0);
    }

    public async IAsyncEnumerable<AcpRunStreamFrame> FollowRunStreamAsync(
        Guid sessionId,
        Guid runId,
        Guid? turnId,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var scoped = await AcquireSessionServicesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var coordinator = scoped.Services.GetRequiredService<IFullDuplexRunCoordinator>();
        await foreach (var chunk in coordinator.FollowAsync(runId, turnId, afterSequence, cancellationToken).ConfigureAwait(false))
        {
            yield return new AcpRunStreamFrame(
                chunk.Seq,
                chunk.Kind,
                chunk.Delta,
                chunk.FinishReason,
                chunk.ErrorCategory,
                chunk.SafeErrorMessage);
        }
    }

    public async IAsyncEnumerable<AcpBackendEvent> SubscribeSessionEventsAsync(
        Guid sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var scoped = await AcquireSessionServicesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var lifecycle = scoped.Services.GetRequiredService<StorageLifecycleService>();
        var watermarks = new Dictionary<Guid, long>();

        while (!cancellationToken.IsCancellationRequested)
        {
            var signalTask = lifecycle.GetEventSignalTask();
            var batch = await lifecycle.FollowEventsAsync(sessionId, watermarks, cancellationToken).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                try
                {
                    await signalTask.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                }
                continue;
            }

            foreach (var envelope in batch)
            {
                yield return MapEventEnvelope(envelope);
            }
        }
    }

    public async Task<AcpRunStatusSnapshot?> GetRunStatusAsync(
        Guid sessionId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        await using var scoped = await AcquireSessionServicesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var lifecycle = scoped.Services.GetRequiredService<StorageLifecycleService>();
        var run = await lifecycle.FindRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null) return null;

        bool? isLeaseHeld = null;
        try
        {
            var runState = await scoped.Services.GetRequiredService<ILifecycleManager>()
                .GetRunStateAsync(runId.ToString("D"), cancellationToken)
                .ConfigureAwait(false);
            isLeaseHeld = !string.IsNullOrWhiteSpace(runState.LeaseOwner);
        }
        catch (KeyNotFoundException)
        {
        }

        return new AcpRunStatusSnapshot(run.Id, run.Status, run.LastEventSequence, run.Summary, isLeaseHeld);
    }

    public async Task<AcpToolExecutionDetail?> GetToolExecutionDetailAsync(
        Guid sessionId,
        Guid runId,
        Guid executionId,
        CancellationToken cancellationToken)
    {
        await using var scoped = await AcquireSessionServicesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var dbFactory = scoped.Services.GetRequiredService<IDbContextFactory<LifecycleDbContext>>();
        var tenant = scoped.Services.GetRequiredService<ITenantContextAccessor>().Current;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.ToolExecutions.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == executionId && x.RunId == runId && x.TenantId == tenant.TenantId && x.WorkspaceId == tenant.WorkspaceId,
            cancellationToken).ConfigureAwait(false);
        if (row is null) return null;

        var contentStore = scoped.Services.GetRequiredService<IContentStore>();
        JsonElement? rawInput = await TryReadContentJsonAsync(contentStore, row.ParametersReference, cancellationToken).ConfigureAwait(false);
        var (rawOutput, resultText) = await TryReadContentJsonOrTextAsync(contentStore, row.ResultReference, cancellationToken).ConfigureAwait(false);

        string? resourcePath = null;
        string? command = null;
        string? cwd = null;
        if (ApprovalEvidenceProjector.TryDecode(row.ArgumentsDigest, out var evidence))
        {
            resourcePath = evidence.ResourcePath;
            command = evidence.Command;
            cwd = evidence.WorkingDirectory;
        }

        if (rawInput is { ValueKind: JsonValueKind.Object } inputObj)
        {
            resourcePath ??= ReadString(inputObj, "path") ?? ReadString(inputObj, "filepath") ?? ReadString(inputObj, "file_path");
            command ??= ReadString(inputObj, "command") ?? ReadString(inputObj, "cmd");
            cwd ??= ReadString(inputObj, "cwd") ?? ReadString(inputObj, "working_directory");
        }

        return new AcpToolExecutionDetail(
            row.Id,
            row.ToolId,
            row.SafeErrorMessage,
            resourcePath,
            command,
            cwd,
            rawInput,
            rawOutput,
            resultText ?? row.SafeErrorMessage,
            row.ToolSuccess);
    }

    public async Task<IReadOnlyList<AcpPendingApproval>> ListPendingApprovalsAsync(
        Guid sessionId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        await using var scoped = await AcquireSessionServicesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        using var diScope = scoped.Services.CreateScope();
        var controlPlane = diScope.ServiceProvider.GetRequiredService<ControlPlaneService>();
        var result = await controlPlane.ListApprovals("pending", sessionId.ToString("D"), runId.ToString("D"), cancellationToken).ConfigureAwait(false);
        if (ExtractResultElement(result, out var statusCode, out var element) && statusCode == StatusCodes.Status200OK && element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray()
                .Select(ParseApprovalElement)
                .Where(a => a is not null)
                .Select(a => a!)
                .ToArray();
        }

        return [];
    }

    public async Task<AcpPendingApproval?> GetApprovalAsync(
        Guid sessionId,
        Guid approvalId,
        CancellationToken cancellationToken)
    {
        await using var scoped = await AcquireSessionServicesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        using var diScope = scoped.Services.CreateScope();
        var controlPlane = diScope.ServiceProvider.GetRequiredService<ControlPlaneService>();
        var result = await controlPlane.GetApproval(approvalId, cancellationToken).ConfigureAwait(false);
        if (!ExtractResultElement(result, out var statusCode, out var element) || statusCode != StatusCodes.Status200OK)
        {
            return null;
        }

        return ParseApprovalElement(element);
    }

    public async Task<AcpApprovalDecisionOutcome> DecideApprovalAsync(
        Guid sessionId,
        Guid approvalId,
        AcpApprovalDecisionInput input,
        CancellationToken cancellationToken)
    {
        await using var scoped = await AcquireSessionServicesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        using var diScope = scoped.Services.CreateScope();
        var controlPlane = diScope.ServiceProvider.GetRequiredService<ControlPlaneService>();

        var result = await controlPlane.DecideApproval(
            approvalId,
            new ApprovalDecisionRequestDto
            {
                Decision = input.Decision,
                Scope = input.Scope,
                Reason = input.Reason,
                RememberRule = input.RememberRule
            },
            cancellationToken).ConfigureAwait(false);

        var hasPayload = ExtractResultElement(result, out var statusCode, out var body);
        if (statusCode is StatusCodes.Status200OK or StatusCodes.Status202Accepted)
        {
            var decidedStatus = hasPayload ? ReadString(body, "status") : null;
            return new AcpApprovalDecisionOutcome(
                Applied: true,
                AlreadyDecidedConflict: false,
                InvalidRuleRejected: false,
                Status: decidedStatus ?? input.Decision);
        }

        var errorCode = hasPayload ? ReadString(body, "code") : null;
        var errorMessage = hasPayload ? (ReadString(body, "message") ?? ReadString(body, "detail")) : null;

        if (statusCode is StatusCodes.Status409Conflict or StatusCodes.Status404NotFound)
        {
            return new AcpApprovalDecisionOutcome(
                Applied: false,
                AlreadyDecidedConflict: true,
                InvalidRuleRejected: false,
                ErrorCode: errorCode,
                ErrorMessage: errorMessage);
        }

        if (statusCode == StatusCodes.Status400BadRequest
            && errorCode is "invalid_rule" or "spec_confirmation_scope")
        {
            return new AcpApprovalDecisionOutcome(
                Applied: false,
                AlreadyDecidedConflict: false,
                InvalidRuleRejected: true,
                ErrorCode: errorCode,
                ErrorMessage: errorMessage);
        }

        return new AcpApprovalDecisionOutcome(
            Applied: false,
            AlreadyDecidedConflict: false,
            InvalidRuleRejected: false,
            ErrorCode: errorCode ?? $"http_{statusCode}",
            ErrorMessage: errorMessage ?? "Approval decision failed.");
    }

    public async Task ResumeAwaitingUserSupervisionAsync(
        Guid sessionId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var pending = await ListPendingApprovalsAsync(sessionId, runId, cancellationToken).ConfigureAwait(false);
        if (pending.Count > 0)
        {
            return;
        }

        await using var scoped = await AcquireSessionServicesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var lifecycle = scoped.Services.GetRequiredService<StorageLifecycleService>();
        var run = await lifecycle.FindRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null
            || run.Status.Trim().ToLowerInvariant() is not ("awaiting_user" or "awaiting_approval" or "awaiting_delegate"))
        {
            return;
        }

        await lifecycle.SetRunStatusAsync(
            runId,
            "executing",
            "ACP non-interactive gate resumed.",
            cancellationToken).ConfigureAwait(false);

        var engine = scoped.Services.GetRequiredService<IFullDuplexRunEngine>();
        await engine.EnqueueAsync(runId, cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelRunAsync(
        Guid sessionId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        await using var scoped = await AcquireSessionServicesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var coordinator = scoped.Services.GetRequiredService<IFullDuplexRunCoordinator>();
        try
        {
            var result = await coordinator.ControlAsync(
                runId,
                new RunControlCommand("cancel", $"acp-cancel-{Guid.NewGuid():N}"),
                cancellationToken).ConfigureAwait(false);

            if (result.Accepted && scoped.Services.GetService<ITerminalSessionControl>() is { } terminals)
            {
                await terminals.KillForRunAsync(runId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (RunAdmissionException ex) when (ex.Code == "RUN_NOT_ACTIVE")
        {
            // Already terminal.
        }
        catch (KeyNotFoundException)
        {
            // Already purged or unknown.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _host.StopAsync(stopCts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort host shutdown.
        }

        if (_host.Services.GetService<IStorageScopeRegistry>() is IAsyncDisposable asyncRegistry)
        {
            try { await asyncRegistry.DisposeAsync().ConfigureAwait(false); } catch { }
        }

        if (_host is IAsyncDisposable asyncHost)
        {
            try { await asyncHost.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        else
        {
            try { _host.Dispose(); } catch { }
        }

        SqliteConnection.ClearAllPools();
    }

    internal static async Task EnsureFallbackStarterPackAsync(IServiceProvider services, CancellationToken ct)
    {
        var tenant = services.GetRequiredService<ITenantContextAccessor>().Current;
        if (!string.Equals(tenant.Role, "owner", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var cfgFactory = services.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>();
        var globalConfig = services.GetRequiredService<GlobalAgentConfiguration>();
        var existingDefault = await AgentConfigurationLookup.GetDefaultModeVersionIdAsync(
            cfgFactory,
            globalConfig,
            tenant.TenantId,
            tenant.WorkspaceId,
            ct).ConfigureAwait(false);
        if (existingDefault is not null)
        {
            return;
        }

        await using (var db = await cfgFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            if (await db.AgentPackInstallations.AnyAsync(ct).ConfigureAwait(false))
            {
                return;
            }
        }

        var manifest = JsonSerializer.Deserialize<AgentPackManifestDto>(FallbackStarterPackJson, WebJson)
            ?? throw new InvalidOperationException("Built-in ACP starter pack manifest could not be parsed.");
        var envelope = AgentPackService.CreateEnvelope(manifest);
        var packService = services.GetRequiredService<IAgentPackService>();
        var preview = await packService.PreviewAsync(envelope, ct).ConfigureAwait(false);
        await packService.ApplyAsync(
            preview.PackId,
            new AgentPackApplyRequestDto { PreviewId = preview.PreviewId, Envelope = envelope },
            expectedRevision: null,
            idempotencyKey: $"acp-starter:{preview.PackId}:{preview.IntegrityDigest}",
            ct).ConfigureAwait(false);
    }

    private static async Task<ProjectRecord> FindOrCreateUnscopedProjectAsync(
        ProjectSessionStore store,
        string canonicalCwd,
        string folderName,
        CancellationToken ct)
    {
        var trimmedCwd = Path.TrimEndingDirectorySeparator(canonicalCwd);
        var existing = (await store.ListProjectsAsync(LifecycleStatuses.Active, ct).ConfigureAwait(false))
            .FirstOrDefault(p => string.Equals(Path.TrimEndingDirectorySeparator(p.RootPath), trimmedCwd, PathComparison));
        if (existing is not null) return existing;

        try
        {
            return await store.CreateProjectAsync(folderName, canonicalCwd, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return (await store.ListProjectsAsync(LifecycleStatuses.Active, ct).ConfigureAwait(false))
                .First(p => string.Equals(Path.TrimEndingDirectorySeparator(p.RootPath), trimmedCwd, PathComparison));
        }
    }

    private static async Task<SessionRecord> CreateCoreSessionInScopeAsync(
        IServiceProvider services,
        Guid? projectId,
        string folderName,
        CancellationToken ct)
    {
        var tenant = services.GetRequiredService<ITenantContextAccessor>().Current;
        var cfgFactory = services.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>();
        var globalConfig = services.GetRequiredService<GlobalAgentConfiguration>();

        var modeVersionId = await AgentConfigurationLookup.GetDefaultModeVersionIdAsync(
            cfgFactory,
            globalConfig,
            tenant.TenantId,
            tenant.WorkspaceId,
            ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A published default Agent Mode must be configured before creating an ACP session.");

        var modeFactory = await AgentConfigurationLookup.SelectModeFactoryAsync(
            cfgFactory,
            globalConfig,
            tenant.TenantId,
            tenant.WorkspaceId,
            modeVersionId,
            ct).ConfigureAwait(false);

        string conversationNodeKey;
        string conversationTemplateSlug;
        await using (var cfg = await modeFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var modeVersion = await cfg.ModeVersions.AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.Id == modeVersionId
                        && x.TenantId == tenant.TenantId
                        && x.WorkspaceId == tenant.WorkspaceId
                        && x.Status == "published",
                    ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Default Agent Mode version '{modeVersionId}' is not published.");

            var definitions = await cfg.AgentDefinitions.AsNoTracking()
                .Where(x => x.TenantId == tenant.TenantId && x.WorkspaceId == tenant.WorkspaceId)
                .Select(x => new ConversationIdentityResolver.DefinitionInput(x.Id, x.Slug, x.Layer, x.CapabilitiesJson))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var identity = ConversationIdentityResolver.Resolve(modeVersion.SnapshotJson, definitions)
                ?? throw new InvalidOperationException("The default Agent Mode does not declare a conversation-capable node.");
            conversationNodeKey = identity.NodeKey;
            conversationTemplateSlug = identity.TemplateSlug;
        }

        var store = services.GetRequiredService<ProjectSessionStore>();
        return await store.CreateSessionAsync(
            projectId,
            $"ACP · {folderName}",
            modeVersionId,
            meetingModelOverride: null,
            conversationNodeKey: conversationNodeKey,
            conversationTemplateSlug: conversationTemplateSlug,
            cancellationToken: ct,
            viewMode: "flat",
            permissionMode: "default",
            spaceOptions: null,
            origin: "acp").ConfigureAwait(false);
    }

    private async Task<AcpSessionSnapshot> BuildSessionSnapshotAsync(
        IServiceProvider services,
        SessionRecord session,
        string cwd,
        CancellationToken ct)
    {
        var cfgFactory = services.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>();
        var globalConfig = services.GetRequiredService<GlobalAgentConfiguration>();
        var defaultModeVersionId = await AgentConfigurationLookup.GetDefaultModeVersionIdAsync(
            cfgFactory,
            globalConfig,
            session.TenantId,
            session.WorkspaceId,
            ct).ConfigureAwait(false);

        var publishedModes = await ListPublishedModesAsync(services, session.TenantId, session.WorkspaceId, ct).ConfigureAwait(false);
        var spaceOptions = ProjectSessionStore.ReadSpaceOptions(session);
        var currentModeId = AcpModeCatalog.ResolveCurrentModeId(
            session.PermissionMode,
            spaceOptions,
            session.ModeVersionId,
            defaultModeVersionId,
            publishedModes);
        var availableModes = AcpModeCatalog.BuildAvailableModes(publishedModes);

        var catalog = await LoadModelCatalogAsync(services, ct).ConfigureAwait(false);
        var currentModelId = ResolveCurrentModelId(session, catalog);

        var availableModels = catalog.Entries
            .Select(e => new AcpModelOption(e.CompositeId, e.DisplayName, e.Description))
            .ToList();

        if (availableModels.Count == 0 && !string.IsNullOrWhiteSpace(currentModelId))
        {
            availableModels.Add(new AcpModelOption(currentModelId, currentModelId));
        }

        return new AcpSessionSnapshot(
            session.Id,
            cwd,
            new AcpSessionModesState(currentModeId, availableModes),
            new AcpSessionModelsState(currentModelId, availableModels));
    }

    private static async Task<IReadOnlyList<AcpPublishedModeInfo>> ListPublishedModesAsync(
        IServiceProvider services,
        Guid tenantId,
        Guid workspaceId,
        CancellationToken ct)
    {
        var cfgFactory = services.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>();
        var globalConfig = services.GetRequiredService<GlobalAgentConfiguration>();
        var catalogFactory = await AgentConfigurationLookup.SelectCatalogFactoryAsync(cfgFactory, globalConfig, tenantId, workspaceId, ct).ConfigureAwait(false);

        await using var cfg = await catalogFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await (
            from mode in cfg.AgentModes.AsNoTracking()
            join version in cfg.ModeVersions.AsNoTracking() on mode.Id equals version.AgentModeId
            where mode.TenantId == tenantId
                && mode.WorkspaceId == workspaceId
                && mode.ArchivedAt == null
                && version.Status == "published"
            select new
            {
                ModeId = mode.Id,
                ModeVersionId = version.Id,
                version.Version,
                mode.Slug,
                mode.DisplayName,
                mode.Description
            }).ToListAsync(ct).ConfigureAwait(false);

        var disabledModeIds = await (
            from managed in cfg.AgentPackManagedResources.AsNoTracking()
            join install in cfg.AgentPackInstallations.AsNoTracking() on managed.InstallationId equals install.Id
            where managed.TenantId == tenantId
                && managed.WorkspaceId == workspaceId
                && managed.ResourceKind == "mode"
                && install.Status != "active"
            select managed.LogicalEntityId).ToListAsync(ct).ConfigureAwait(false);
        var disabledSet = disabledModeIds.ToHashSet();

        return rows
            .Where(r => !disabledSet.Contains(r.ModeId) && !string.IsNullOrWhiteSpace(r.Slug))
            .GroupBy(r => r.ModeId)
            .Select(g => g.OrderByDescending(x => x.Version).First())
            .OrderBy(x => x.Slug, StringComparer.Ordinal)
            .Select(x => new AcpPublishedModeInfo(x.ModeId, x.ModeVersionId, x.Slug, x.DisplayName, x.Description))
            .ToArray();
    }

    private static async Task<ModelCatalog> LoadModelCatalogAsync(IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var controlPlane = scope.ServiceProvider.GetRequiredService<ControlPlaneService>();

        var providersResult = await controlPlane.ListProviders(ct).ConfigureAwait(false);
        var routesResult = await controlPlane.ListRoutes(ct).ConfigureAwait(false);

        var providers = ExtractResultElement(providersResult, out var pStatus, out var pEl)
            && pStatus == StatusCodes.Status200OK
            && pEl.ValueKind == JsonValueKind.Array
                ? pEl.EnumerateArray().ToArray()
                : [];

        var routes = ExtractResultElement(routesResult, out var rStatus, out var rEl)
            && rStatus == StatusCodes.Status200OK
            && rEl.ValueKind == JsonValueKind.Array
                ? rEl.EnumerateArray().ToArray()
                : [];

        var entries = new List<ModelCatalogEntry>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var providerNames = new Dictionary<Guid, string>();

        foreach (var p in providers)
        {
            if (!TryReadGuid(p, "id", out var providerId)) continue;
            if (p.TryGetProperty("enabled", out var enabledEl) && enabledEl.ValueKind == JsonValueKind.False) continue;

            var displayName = ReadString(p, "display_name") ?? ReadString(p, "driver") ?? "Provider";
            providerNames[providerId] = displayName;
            var defaultModel = ReadString(p, "model");
            var modelChosenByHarness = p.TryGetProperty("model_chosen_by_harness", out var mch) && mch.ValueKind == JsonValueKind.True;

            var discoveredModels = new List<string>();
            foreach (var listPropName in new[] { "models", "available_models" })
            {
                if (p.TryGetProperty(listPropName, out var availEl) && availEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in availEl.EnumerateArray())
                    {
                        var id = m.ValueKind == JsonValueKind.String ? m.GetString() : ReadString(m, "id") ?? ReadString(m, "model");
                        if (!string.IsNullOrWhiteSpace(id) && !discoveredModels.Contains(id.Trim(), StringComparer.OrdinalIgnoreCase))
                        {
                            discoveredModels.Add(id.Trim());
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(defaultModel) && !discoveredModels.Contains(defaultModel.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                discoveredModels.Insert(0, defaultModel.Trim());
            }

            if (discoveredModels.Count == 0 && modelChosenByHarness)
            {
                var composite = providerId.ToString("D");
                if (seenIds.Add(composite))
                {
                    entries.Add(new ModelCatalogEntry(composite, providerId, null, displayName, "Harness-managed model"));
                }
            }
            else
            {
                foreach (var m in discoveredModels)
                {
                    var composite = $"{providerId:D}/{m}";
                    if (seenIds.Add(composite))
                    {
                        entries.Add(new ModelCatalogEntry(composite, providerId, m, $"{displayName} · {m}", displayName));
                    }
                }
            }
        }

        string? defaultChatComposite = null;
        foreach (var route in routes)
        {
            if (!string.Equals(ReadString(route, "purpose"), "chat", StringComparison.OrdinalIgnoreCase)) continue;
            if (!route.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array) continue;

            foreach (var cand in candidates.EnumerateArray())
            {
                if (!TryReadGuid(cand, "provider_instance_id", out var candProviderId)) continue;
                var candModel = ReadString(cand, "model");
                var composite = string.IsNullOrWhiteSpace(candModel)
                    ? candProviderId.ToString("D")
                    : $"{candProviderId:D}/{candModel.Trim()}";

                defaultChatComposite ??= composite;
                if (seenIds.Add(composite))
                {
                    var pName = providerNames.GetValueOrDefault(candProviderId, "Provider");
                    var label = string.IsNullOrWhiteSpace(candModel) ? pName : $"{pName} · {candModel.Trim()}";
                    entries.Insert(0, new ModelCatalogEntry(composite, candProviderId, candModel?.Trim(), label, pName));
                }
            }
        }

        return new ModelCatalog(entries, defaultChatComposite);
    }

    private static string ResolveCurrentModelId(SessionRecord session, ModelCatalog catalog)
    {
        if (session.MeetingModelOverrideProviderInstanceId is { } ovrProviderId && ovrProviderId != Guid.Empty)
        {
            return string.IsNullOrWhiteSpace(session.MeetingModelOverrideModel)
                ? ovrProviderId.ToString("D")
                : $"{ovrProviderId:D}/{session.MeetingModelOverrideModel.Trim()}";
        }

        if (!string.IsNullOrWhiteSpace(catalog.DefaultChatCompositeId))
        {
            return catalog.DefaultChatCompositeId!;
        }

        return catalog.Entries.FirstOrDefault()?.CompositeId ?? "default";
    }

    private static bool TryMatchModelSelection(
        string requestedModelId,
        IReadOnlyList<ModelCatalogEntry> entries,
        out Guid providerId,
        out string? modelName)
    {
        var trimmed = requestedModelId.Trim();

        var exact = entries.FirstOrDefault(e => string.Equals(e.CompositeId, trimmed, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            providerId = exact.ProviderId;
            modelName = exact.ModelName;
            return true;
        }

        var slashIndex = trimmed.IndexOf('/');
        if (slashIndex > 0 && Guid.TryParse(trimmed[..slashIndex], out var explicitProvider))
        {
            var explicitModel = trimmed[(slashIndex + 1)..].Trim();
            if (explicitModel.Length > 0 && entries.Any(e => e.ProviderId == explicitProvider))
            {
                providerId = explicitProvider;
                modelName = explicitModel;
                return true;
            }
        }

        var byModelName = entries.FirstOrDefault(e =>
            !string.IsNullOrWhiteSpace(e.ModelName)
            && string.Equals(e.ModelName, trimmed, StringComparison.OrdinalIgnoreCase));
        if (byModelName is not null)
        {
            providerId = byModelName.ProviderId;
            modelName = byModelName.ModelName;
            return true;
        }

        providerId = Guid.Empty;
        modelName = null;
        return false;
    }

    private async Task<ScopedServiceHandle> AcquireSessionServicesAsync(Guid sessionId, CancellationToken ct)
    {
        if (!_scopesEnabled || _host.Services.GetService<IStorageScopeRegistry>() is not { } registry)
        {
            return new ScopedServiceHandle(_host.Services, null);
        }

        if (_sessionStorageIds.TryGetValue(sessionId, out var cachedStorageId) && !string.IsNullOrWhiteSpace(cachedStorageId))
        {
            var lease = await registry.AcquireAsync(cachedStorageId, ct).ConfigureAwait(false);
            return new ScopedServiceHandle(lease.Services, lease);
        }

        foreach (var descriptor in registry.List())
        {
            var lease = await registry.AcquireAsync(descriptor.StorageId, ct).ConfigureAwait(false);
            try
            {
                var found = await lease.Services.GetRequiredService<ProjectSessionStore>().GetSessionAsync(sessionId, ct).ConfigureAwait(false);
                if (found is not null)
                {
                    _sessionStorageIds[sessionId] = descriptor.StorageId;
                    return new ScopedServiceHandle(lease.Services, lease);
                }
            }
            catch
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            await lease.DisposeAsync().ConfigureAwait(false);
        }

        throw new KeyNotFoundException($"Session '{sessionId}' was not found.");
    }

    private static AcpBackendEvent MapEventEnvelope(EventEnvelope envelope)
    {
        Guid? runId = Guid.TryParse(envelope.RunId, out var parsedRun) ? parsedRun : null;
        long sequence = 0;
        string? summary = null;
        JsonElement innerPayload = default;

        if (envelope.Payload.TryGetValue("sequence", out var seqObj) && seqObj is long seqLong)
        {
            sequence = seqLong;
        }
        else if (envelope.Payload.TryGetValue("sequence", out var seqElObj) && seqElObj is JsonElement seqEl && seqEl.TryGetInt64(out var seqParsed))
        {
            sequence = seqParsed;
        }

        if (envelope.Payload.TryGetValue("summary", out var sumObj))
        {
            summary = sumObj as string ?? (sumObj is JsonElement sumEl && sumEl.ValueKind == JsonValueKind.String ? sumEl.GetString() : null);
        }

        if (envelope.Payload.TryGetValue("payload", out var payloadObj))
        {
            if (payloadObj is JsonElement je)
            {
                innerPayload = je.Clone();
            }
            else if (payloadObj is not null)
            {
                innerPayload = JsonSerializer.SerializeToElement(payloadObj, SnakeJson);
            }
        }

        if (innerPayload.ValueKind == JsonValueKind.Undefined)
        {
            innerPayload = JsonSerializer.SerializeToElement(envelope.Payload, SnakeJson);
        }

        return new AcpBackendEvent(
            envelope.EventId,
            envelope.EventType,
            runId,
            sequence,
            summary,
            innerPayload);
    }

    private static AcpPendingApproval? ParseApprovalElement(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!TryReadGuid(el, "id", out var id)) return null;

        Guid? sessionId = TryReadGuid(el, "session_id", out var sid) ? sid : null;
        Guid? runId = TryReadGuid(el, "run_id", out var rid) ? rid : null;
        Guid? executionId = TryReadGuid(el, "execution_id", out var eid) ? eid : null;

        var kind = ReadString(el, "kind") ?? "tool";
        var toolId = ReadString(el, "tool_id") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(toolId) && el.TryGetProperty("claim", out var claim) && claim.ValueKind == JsonValueKind.Object)
        {
            var resource = ReadString(claim, "resource") ?? string.Empty;
            if (resource.StartsWith("tool://", StringComparison.OrdinalIgnoreCase))
            {
                toolId = resource["tool://".Length..];
            }
        }

        var risk = ReadString(el, "risk") ?? "medium";
        var status = ReadString(el, "status") ?? "pending";
        var summary = ReadString(el, "summary") ?? toolId;
        var argsJson = ReadString(el, "arguments") ?? ReadString(el, "arguments_json") ?? ReadString(el, "arguments_digest");
        var command = ReadString(el, "command");
        var cwd = ReadString(el, "cwd");
        var resourcePath = ReadString(el, "resource_path");

        return new AcpPendingApproval(
            id,
            sessionId,
            runId,
            executionId,
            kind,
            toolId,
            risk,
            status,
            summary,
            argsJson,
            command,
            cwd,
            resourcePath);
    }

    private static async Task<JsonElement?> TryReadContentJsonAsync(IContentStore content, string? reference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        try
        {
            await using var stream = await content.OpenReadAsync(new ContentReference(reference, string.Empty, 0, "application/json"), ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(JsonElement? Json, string? Text)> TryReadContentJsonOrTextAsync(
        IContentStore content,
        string? reference,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference)) return (null, null);
        try
        {
            await using var stream = await content.OpenReadAsync(new ContentReference(reference, string.Empty, 0, "application/json"), ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var raw = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw)) return (null, null);
            try
            {
                using var doc = JsonDocument.Parse(raw);
                return (doc.RootElement.Clone(), raw);
            }
            catch (JsonException)
            {
                return (null, raw);
            }
        }
        catch
        {
            return (null, null);
        }
    }

    private static bool ExtractResultElement(IResult result, out int statusCode, out JsonElement element)
    {
        statusCode = result is IStatusCodeHttpResult statusResult && statusResult.StatusCode is { } code
            ? code
            : StatusCodes.Status200OK;

        if (result is IValueHttpResult { Value: { } value })
        {
            element = value is JsonElement je ? je.Clone() : JsonSerializer.SerializeToElement(value, SnakeJson);
            return true;
        }

        element = default;
        return false;
    }

    private static IOException? FindInnerIoException(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is IOException io) return io;
        }

        return null;
    }

    private static string? ReadString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var prop)
        && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static bool TryReadGuid(JsonElement el, string name, out Guid value)
    {
        value = Guid.Empty;
        return el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty(name, out var prop)
            && prop.ValueKind == JsonValueKind.String
            && Guid.TryParse(prop.GetString(), out value)
            && value != Guid.Empty;
    }

    private readonly struct ScopedServiceHandle(IServiceProvider services, StorageRuntimeLease? lease) : IAsyncDisposable
    {
        public IServiceProvider Services { get; } = services;

        public ValueTask DisposeAsync() => lease?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private sealed record ModelCatalogEntry(
        string CompositeId,
        Guid ProviderId,
        string? ModelName,
        string DisplayName,
        string? Description);

    private sealed record ModelCatalog(
        IReadOnlyList<ModelCatalogEntry> Entries,
        string? DefaultChatCompositeId);
}
