using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using TinadecCore.Services;
using Tinadec.Contracts.Events;
using TinadecModel.Providers;
using TinadecModel.Storage;
using Tinadec.Contracts.Models;

namespace TinadecCore.Storage;

public sealed class CoreStore
{
    private readonly object _gate = new();
    private readonly string _connectionString;

    public CoreStore(IConfiguration configuration)
        : this(ResolveDatabasePath(configuration))
    {
    }

    public CoreStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString();
    }

    public void Initialize()
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            Execute(connection, """
                create table if not exists projects (
                    id text primary key,
                    name text not null,
                    path text not null,
                    created_at text not null
                );

                create table if not exists sessions (
                    id text primary key,
                    project_id text not null,
                    title text not null,
                    status text not null,
                    created_at text not null,
                    updated_at text not null
                );

                create table if not exists messages (
                    id text primary key,
                    session_id text not null,
                    role text not null,
                    content text not null,
                    created_at text not null
                );

                create table if not exists events (
                    seq integer primary key,
                    v text not null,
                    type text not null,
                    request_id text not null,
                    session_id text null,
                    trace_id text not null,
                    ts text not null,
                    capabilities_json text not null,
                    payload_json text null,
                    error_json text null
                );

                create table if not exists approvals (
                    id text primary key,
                    session_id text null,
                    kind text not null,
                    summary text not null,
                    command text null,
                    cwd text null,
                    status text not null,
                    created_at text not null,
                    decided_at text null
                );

                create table if not exists model_settings (
                    id integer primary key check (id = 1),
                    base_url text not null,
                    model text not null,
                    encrypted_api_key text null,
                    updated_at text not null
                );

                create table if not exists model_provider_instances (
                    id text primary key,
                    driver text not null,
                    display_name text not null,
                    connection_kind text not null,
                    base_url text null,
                    model text null,
                    encrypted_api_key text null,
                    binary_path text null,
                    home_path text null,
                    server_url text null,
                    launch_args text null,
                    capabilities_json text not null,
                    enabled integer not null,
                    health_status text not null default 'healthy',
                    cooldown_until text null,
                    failure_count integer not null default 0,
                    last_failure_at text null,
                    last_error_category text null,
                    created_at text not null,
                    updated_at text not null
                );

                create table if not exists model_routes (
                    purpose text primary key,
                    provider_instance_id text not null,
                    model text null,
                    updated_at text not null
                );

                create table if not exists extension_sources (
                    id text primary key,
                    name text not null,
                    kind text not null,
                    location text not null,
                    enabled integer not null,
                    last_refreshed_at text null,
                    created_at text not null
                );

                create table if not exists extension_catalog_cache (
                    catalog_id text primary key,
                    source_id text not null,
                    extension_id text not null,
                    kind text not null,
                    version text not null,
                    publisher text not null,
                    display_name text not null,
                    description text not null,
                    source_kind text not null,
                    source_location text not null,
                    capabilities_json text not null,
                    permissions_json text not null,
                    manifest_json text not null,
                    updated_at text not null
                );

                create table if not exists installed_extensions (
                    id text primary key,
                    catalog_id text null,
                    extension_id text not null,
                    kind text not null,
                    version text not null,
                    publisher text not null,
                    display_name text not null,
                    description text not null,
                    source_kind text not null,
                    source_location text not null,
                    capabilities_json text not null,
                    permissions_json text not null,
                    manifest_json text not null,
                    config_json text null,
                    enabled integer not null,
                    status text not null,
                    status_message text not null,
                    installed_at text not null,
                    updated_at text not null
                );

                create table if not exists extension_events (
                    id text primary key,
                    extension_id text not null,
                    event_type text not null,
                    payload_json text null,
                    created_at text not null
                );

                create table if not exists mcp_servers (
                    id text primary key,
                    extension_id text not null,
                    name text not null,
                    transport text not null,
                    status text not null,
                    tools_json text not null,
                    updated_at text not null
                );

                create table if not exists mcp_capabilities_cache (
                    server_id text primary key,
                    capabilities_json text not null,
                    updated_at text not null
                );

                create table if not exists acp_adapters (
                    id text primary key,
                    extension_id text not null,
                    name text not null,
                    command text not null,
                    status text not null,
                    status_message text not null,
                    capabilities_json text not null,
                    updated_at text not null
                );

                create table if not exists acp_sessions (
                    id text primary key,
                    adapter_id text not null,
                    external_session_id text null,
                    status text not null,
                    created_at text not null,
                    updated_at text not null
                );

                create table if not exists agent_profiles (
                    id text primary key,
                    name text not null,
                    layer text not null,
                    agent_type text not null,
                    mode text not null,
                    description text not null,
                    model_route_purpose text not null,
                    allowed_tools_json text not null,
                    capabilities_json text not null,
                    system_prompt text,
                    enabled integer not null,
                    is_builtin integer not null,
                    updated_at text not null
                );

                create table if not exists agent_candidates (
                    id text primary key,
                    generated_by_agent_id text not null,
                    name text not null,
                    layer text not null,
                    agent_type text not null,
                    description text not null,
                    suggested_tools_json text not null,
                    evaluation_notes_json text not null,
                    status text not null,
                    created_at text not null
                );

                create table if not exists prompt_fragments (
                    id text primary key,
                    key text not null,
                    title text not null,
                    scope text not null,
                    target_agent_id text null,
                    category text not null,
                    content text not null,
                    priority integer not null,
                    enabled integer not null,
                    is_builtin integer not null,
                    created_at text not null,
                    updated_at text not null
                );

                create unique index if not exists idx_prompt_fragments_key_target
                on prompt_fragments(key, coalesce(target_agent_id, ''));

                create table if not exists prompt_context_plans (
                    id text primary key,
                    run_id text not null,
                    agent_id text not null,
                    strategy text not null,
                    selected_fragment_ids_json text not null,
                    summary text not null,
                    created_by_agent_id text not null,
                    created_at text not null
                );

                create table if not exists orchestration_runs (
                    id text primary key,
                    session_id text not null,
                    user_message_id text null,
                    status text not null,
                    summary text not null,
                    created_at text not null,
                    updated_at text not null
                );

                create table if not exists task_graphs (
                    id text primary key,
                    run_id text not null,
                    session_id text not null,
                    title text not null,
                    status text not null,
                    created_at text not null,
                    updated_at text not null
                );

                create table if not exists task_nodes (
                    id text primary key,
                    graph_id text not null,
                    run_id text not null,
                    session_id text not null,
                    title text not null,
                    description text not null,
                    status text not null,
                    priority integer not null,
                    risk text not null,
                    success_criteria_json text not null,
                    dependencies_json text not null,
                    required_capabilities_json text not null,
                    created_at text not null,
                    updated_at text not null
                );

                create table if not exists agent_assignments (
                    id text primary key,
                    run_id text not null,
                    task_node_id text not null,
                    agent_id text not null,
                    agent_name text not null,
                    agent_layer text not null,
                    agent_type text not null,
                    model_route_purpose text not null,
                    permission_mode text not null,
                    allowed_tools_json text not null,
                    status text not null,
                    created_at text not null
                );

                create table if not exists step_results (
                    id text primary key,
                    run_id text not null,
                    task_node_id text not null,
                    agent_id text not null,
                    status text not null,
                    summary text not null,
                    evidence_json text not null,
                    created_at text not null
                );

                create table if not exists context_packs (
                    id text primary key,
                    run_id text not null,
                    session_id text not null,
                    created_by_agent_id text not null,
                    summary text not null,
                    token_budget integer not null,
                    compression_ratio real not null,
                    evidence_map_json text not null,
                    created_at text not null
                );

                create table if not exists supervision_findings (
                    id text primary key,
                    run_id text not null,
                    session_id text not null,
                    severity text not null,
                    category text not null,
                    summary text not null,
                    recommendation text not null,
                    status text not null,
                    created_at text not null
                );

                create table if not exists prompt_fragment_versions (
                    id text primary key,
                    fragment_id text not null,
                    version integer not null,
                    content text not null,
                    changed_fields_json text not null,
                    change_summary text not null,
                    is_active integer not null default 0,
                    created_at text not null
                );

                create table if not exists prompt_fragment_signals (
                    id text primary key,
                    fragment_id text not null,
                    version integer,
                    signal text not null,
                    run_id text,
                    session_id text,
                    note text,
                    created_at text not null
                );

                create index if not exists idx_prompt_fragment_versions_fragment
                    on prompt_fragment_versions(fragment_id, version);

                create index if not exists idx_prompt_fragment_signals_fragment
                    on prompt_fragment_signals(fragment_id, version, created_at);
                """);

            // Migration: add system_prompt column to agent_profiles if missing
            try
            {
                Execute(connection, "alter table agent_profiles add column system_prompt text");
            }
            catch (SqliteException)
            {
                // Column already exists �?ignore
            }

            AddColumnIfMissing(connection, "model_provider_instances", "health_status", "text not null default 'healthy'");
            AddColumnIfMissing(connection, "model_provider_instances", "cooldown_until", "text null");
            AddColumnIfMissing(connection, "model_provider_instances", "failure_count", "integer not null default 0");
            AddColumnIfMissing(connection, "model_provider_instances", "last_failure_at", "text null");
            AddColumnIfMissing(connection, "model_provider_instances", "last_error_category", "text null");

            Execute(connection, """
                update model_provider_instances
                set capabilities_json = '["chat","streaming","tool-calls"]'
                where capabilities_json is null
                   or trim(capabilities_json) = ''
                   or lower(trim(capabilities_json)) = 'null';
                """);

            Execute(connection, """
                update model_provider_instances
                set health_status = 'healthy'
                where health_status is null
                   or trim(health_status) = ''
                   or lower(trim(health_status)) not in ('healthy', 'unhealthy', 'disabled', 'cooldown', 'unknown');
                """);

            Execute(connection, """
                update model_provider_instances
                set failure_count = 0
                where failure_count is null
                   or failure_count < 0;
                """);

            Execute(connection, """
                insert into model_settings (id, base_url, model, encrypted_api_key, updated_at)
                values (1, 'https://api.openai.com/v1', 'gpt-5.4-mini', null, $updated_at)
                on conflict(id) do nothing;
                """, command => command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O")));

            Execute(connection, """
                insert into model_provider_instances (
                    id,
                    driver,
                    display_name,
                    connection_kind,
                    base_url,
                    model,
                    encrypted_api_key,
                    binary_path,
                    home_path,
                    server_url,
                    launch_args,
                    capabilities_json,
                    enabled,
                    health_status,
                    cooldown_until,
                    failure_count,
                    last_failure_at,
                    last_error_category,
                    created_at,
                    updated_at
                )
                select
                    'openai_default',
                    'openai-compatible',
                    'OpenAI Compatible',
                    'api-key',
                    base_url,
                    model,
                    encrypted_api_key,
                    null,
                    null,
                    null,
                    null,
                    '["chat","streaming","tool-calls"]',
                    1,
                    'healthy',
                    null,
                    0,
                    null,
                    null,
                    $now,
                    $now
                from model_settings
                where id = 1
                on conflict(id) do nothing;
                """, command => command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")));

            Execute(connection, """
                insert into model_routes (purpose, provider_instance_id, model, updated_at)
                select 'chat', 'openai_default', model, $now
                from model_provider_instances
                where id = 'openai_default'
                on conflict(purpose) do nothing;
                """, command => command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")));

            SeedBuiltinExtensions(connection);
            NormalizeLegacyAgentSeeds(connection);
            SeedBuiltinAgents(connection);
            SeedBuiltinPromptFragments(connection);
        }
    }

    public IReadOnlyList<ProjectDto> ListProjects()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "select id, name, path, created_at from projects order by created_at desc";

        using var reader = command.ExecuteReader();
        var projects = new List<ProjectDto>();
        while (reader.Read())
        {
            projects.Add(new ProjectDto(reader.GetString(0), reader.GetString(1), reader.GetString(2), ParseTime(reader.GetString(3))));
        }

        return projects;
    }

    public ProjectDto CreateProject(string name, string path)
    {
        var now = DateTimeOffset.UtcNow;
        var project = new ProjectDto($"proj_{Guid.NewGuid():N}", name, path, now);

        lock (_gate)
        {
            using var connection = OpenConnection();
            Execute(connection, """
                insert into projects (id, name, path, created_at)
                values ($id, $name, $path, $created_at)
                """, command =>
            {
                command.Parameters.AddWithValue("$id", project.Id);
                command.Parameters.AddWithValue("$name", project.Name);
                command.Parameters.AddWithValue("$path", project.Path);
                command.Parameters.AddWithValue("$created_at", now.ToString("O"));
            });
        }

        return project;
    }

    public IReadOnlyList<SessionDto> ListSessions(string? projectId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(projectId)
            ? "select id, project_id, title, status, created_at, updated_at from sessions order by updated_at desc"
            : "select id, project_id, title, status, created_at, updated_at from sessions where project_id = $project_id order by updated_at desc";

        if (!string.IsNullOrWhiteSpace(projectId))
        {
            command.Parameters.AddWithValue("$project_id", projectId);
        }

        using var reader = command.ExecuteReader();
        var sessions = new List<SessionDto>();
        while (reader.Read())
        {
            sessions.Add(ReadSession(reader));
        }

        return sessions;
    }

    public SessionDto CreateSession(string projectId, string? title)
    {
        var now = DateTimeOffset.UtcNow;
        var session = new SessionDto(
            $"sess_{Guid.NewGuid():N}",
            projectId,
            string.IsNullOrWhiteSpace(title) ? "New session" : title.Trim(),
            "active",
            now,
            now);

        lock (_gate)
        {
            using var connection = OpenConnection();
            Execute(connection, """
                insert into sessions (id, project_id, title, status, created_at, updated_at)
                values ($id, $project_id, $title, $status, $created_at, $updated_at)
                """, command =>
            {
                command.Parameters.AddWithValue("$id", session.Id);
                command.Parameters.AddWithValue("$project_id", session.ProjectId);
                command.Parameters.AddWithValue("$title", session.Title);
                command.Parameters.AddWithValue("$status", session.Status);
                command.Parameters.AddWithValue("$created_at", now.ToString("O"));
                command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            });
        }

        return session;
    }

    public IReadOnlyList<MessageDto> ListMessages(string sessionId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select id, session_id, role, content, created_at
            from messages
            where session_id = $session_id
            order by created_at asc
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);

        using var reader = command.ExecuteReader();
        var messages = new List<MessageDto>();
        while (reader.Read())
        {
            messages.Add(new MessageDto(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), ParseTime(reader.GetString(4))));
        }

        return messages;
    }

    public MessageDto AddMessage(string sessionId, string role, string content)
    {
        var now = DateTimeOffset.UtcNow;
        var message = new MessageDto($"msg_{Guid.NewGuid():N}", sessionId, role, content, now);

        lock (_gate)
        {
            using var connection = OpenConnection();
            Execute(connection, """
                insert into messages (id, session_id, role, content, created_at)
                values ($id, $session_id, $role, $content, $created_at);

                update sessions set updated_at = $created_at where id = $session_id;
                """, command =>
            {
                command.Parameters.AddWithValue("$id", message.Id);
                command.Parameters.AddWithValue("$session_id", message.SessionId);
                command.Parameters.AddWithValue("$role", message.Role);
                command.Parameters.AddWithValue("$content", message.Content);
                command.Parameters.AddWithValue("$created_at", now.ToString("O"));
            });
        }

        return message;
    }

    public EventEnvelope AppendNewEvent(
        string type,
        string? sessionId,
        JsonObject? payload,
        IReadOnlyList<string>? capabilities = null,
        TinadecError? error = null)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            var seq = NextSeq(connection);
            var envelope = EventEnvelope.Create(type, seq, sessionId, payload, capabilities, error);

            Execute(connection, """
                insert into events (seq, v, type, request_id, session_id, trace_id, ts, capabilities_json, payload_json, error_json)
                values ($seq, $v, $type, $request_id, $session_id, $trace_id, $ts, $capabilities_json, $payload_json, $error_json)
                """, command =>
            {
                command.Parameters.AddWithValue("$seq", envelope.Seq);
                command.Parameters.AddWithValue("$v", envelope.V);
                command.Parameters.AddWithValue("$type", envelope.Type);
                command.Parameters.AddWithValue("$request_id", envelope.RequestId);
                command.Parameters.AddWithValue("$session_id", (object?)envelope.SessionId ?? DBNull.Value);
                command.Parameters.AddWithValue("$trace_id", envelope.TraceId);
                command.Parameters.AddWithValue("$ts", envelope.Ts.ToString("O"));
                command.Parameters.AddWithValue("$capabilities_json", JsonSerializer.Serialize(envelope.Capabilities, TinadecJson.Options));
                command.Parameters.AddWithValue("$payload_json", envelope.Payload?.ToJsonString(TinadecJson.Options) ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("$error_json", envelope.Error is null ? DBNull.Value : JsonSerializer.Serialize(envelope.Error, TinadecJson.Options));
            });

            return envelope;
        }
    }

    public IReadOnlyList<EventEnvelope> ListEvents(string? sessionId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(sessionId)
            ? "select seq, v, type, request_id, session_id, trace_id, ts, capabilities_json, payload_json, error_json from events order by seq asc"
            : "select seq, v, type, request_id, session_id, trace_id, ts, capabilities_json, payload_json, error_json from events where session_id = $session_id or session_id is null order by seq asc";

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            command.Parameters.AddWithValue("$session_id", sessionId);
        }

        using var reader = command.ExecuteReader();
        var events = new List<EventEnvelope>();
        while (reader.Read())
        {
            events.Add(ReadEvent(reader));
        }

        return events;
    }

    public IReadOnlyList<ApprovalDto> ListApprovals(string? status, string? sessionId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(status))
        {
            where.Add("status = $status");
            command.Parameters.AddWithValue("$status", status);
        }

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            where.Add("(session_id = $session_id or session_id is null)");
            command.Parameters.AddWithValue("$session_id", sessionId);
        }

        command.CommandText = "select id, session_id, kind, summary, command, cwd, status, created_at, decided_at from approvals"
            + (where.Count == 0 ? "" : " where " + string.Join(" and ", where))
            + " order by created_at desc";

        using var reader = command.ExecuteReader();
        var approvals = new List<ApprovalDto>();
        while (reader.Read())
        {
            approvals.Add(ReadApproval(reader));
        }

        return approvals;
    }

    public ApprovalDto CreateApproval(CreateApprovalRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var approval = new ApprovalDto(
            $"appr_{Guid.NewGuid():N}",
            request.SessionId,
            request.Kind,
            request.Summary,
            request.Command,
            request.Cwd,
            "pending",
            now,
            null);

        lock (_gate)
        {
            using var connection = OpenConnection();
            Execute(connection, """
                insert into approvals (id, session_id, kind, summary, command, cwd, status, created_at, decided_at)
                values ($id, $session_id, $kind, $summary, $command, $cwd, $status, $created_at, null)
                """, command =>
            {
                command.Parameters.AddWithValue("$id", approval.Id);
                command.Parameters.AddWithValue("$session_id", (object?)approval.SessionId ?? DBNull.Value);
                command.Parameters.AddWithValue("$kind", approval.Kind);
                command.Parameters.AddWithValue("$summary", approval.Summary);
                command.Parameters.AddWithValue("$command", (object?)approval.Command ?? DBNull.Value);
                command.Parameters.AddWithValue("$cwd", (object?)approval.Cwd ?? DBNull.Value);
                command.Parameters.AddWithValue("$status", approval.Status);
                command.Parameters.AddWithValue("$created_at", now.ToString("O"));
            });
        }

        return approval;
    }

    public ApprovalDto? DecideApproval(string approvalId, string decision)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            var decidedAt = DateTimeOffset.UtcNow;
            Execute(connection, """
                update approvals
                set status = $status, decided_at = $decided_at
                where id = $id
                """, command =>
            {
                command.Parameters.AddWithValue("$id", approvalId);
                command.Parameters.AddWithValue("$status", decision);
                command.Parameters.AddWithValue("$decided_at", decidedAt.ToString("O"));
            });

            using var command = connection.CreateCommand();
            command.CommandText = "select id, session_id, kind, summary, command, cwd, status, created_at, decided_at from approvals where id = $id";
            command.Parameters.AddWithValue("$id", approvalId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadApproval(reader) : null;
        }
    }

    public ApprovalDto? GetApproval(string approvalId)
    {
        using var connection = OpenConnection();
        return GetApproval(connection, approvalId);
    }

    public StoredModelSettings GetModelSettings()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "select base_url, model, encrypted_api_key, updated_at from model_settings where id = 1";

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new StoredModelSettings("https://api.openai.com/v1", "gpt-5.4-mini", null, DateTimeOffset.UtcNow);
        }

        return new StoredModelSettings(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            ParseTime(reader.GetString(3)));
    }

    public StoredModelSettings SaveModelSettings(string baseUrl, string model, string? encryptedApiKey)
    {
        var normalizedBaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1" : baseUrl.Trim().TrimEnd('/');
        var normalizedModel = string.IsNullOrWhiteSpace(model) ? "gpt-5.4-mini" : model.Trim();
        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            using var connection = OpenConnection();
            Execute(connection, """
                insert into model_settings (id, base_url, model, encrypted_api_key, updated_at)
                values (1, $base_url, $model, $encrypted_api_key, $updated_at)
                on conflict(id) do update set
                    base_url = excluded.base_url,
                    model = excluded.model,
                    encrypted_api_key = excluded.encrypted_api_key,
                    updated_at = excluded.updated_at
                """, command =>
            {
                command.Parameters.AddWithValue("$base_url", normalizedBaseUrl);
                command.Parameters.AddWithValue("$model", normalizedModel);
                command.Parameters.AddWithValue("$encrypted_api_key", (object?)encryptedApiKey ?? DBNull.Value);
                command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            });

            Execute(connection, """
                insert into model_provider_instances (
                    id,
                    driver,
                    display_name,
                    connection_kind,
                    base_url,
                    model,
                    encrypted_api_key,
                    binary_path,
                    home_path,
                    server_url,
                    launch_args,
                    capabilities_json,
                    enabled,
                    health_status,
                    cooldown_until,
                    failure_count,
                    last_failure_at,
                    last_error_category,
                    created_at,
                    updated_at
                )
                values (
                    'openai_default',
                    'openai-compatible',
                    'OpenAI Compatible',
                    'api-key',
                    $base_url,
                    $model,
                    $encrypted_api_key,
                    null,
                    null,
                    null,
                    null,
                    '["chat","streaming","tool-calls"]',
                    1,
                    'healthy',
                    null,
                    0,
                    null,
                    null,
                    $updated_at,
                    $updated_at
                )
                on conflict(id) do update set
                    base_url = excluded.base_url,
                    model = excluded.model,
                    encrypted_api_key = excluded.encrypted_api_key,
                    updated_at = excluded.updated_at
                """, command =>
            {
                command.Parameters.AddWithValue("$base_url", normalizedBaseUrl);
                command.Parameters.AddWithValue("$model", normalizedModel);
                command.Parameters.AddWithValue("$encrypted_api_key", (object?)encryptedApiKey ?? DBNull.Value);
                command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            });

            Execute(connection, """
                insert into model_routes (purpose, provider_instance_id, model, updated_at)
                values ('chat', 'openai_default', $model, $updated_at)
                on conflict(purpose) do update set
                    provider_instance_id = excluded.provider_instance_id,
                    model = excluded.model,
                    updated_at = excluded.updated_at
                """, command =>
            {
                command.Parameters.AddWithValue("$model", normalizedModel);
                command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            });
        }

        return new StoredModelSettings(normalizedBaseUrl, normalizedModel, encryptedApiKey, now);
    }

    public IReadOnlyList<ModelProviderInstanceDto> ListModelProviderInstances()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select id, driver, display_name, connection_kind, base_url, model, encrypted_api_key, binary_path, home_path,
                   server_url, launch_args, capabilities_json, enabled, health_status, cooldown_until, failure_count,
                   last_failure_at, last_error_category, created_at, updated_at
            from model_provider_instances
            order by updated_at desc, display_name
            """;

        using var reader = command.ExecuteReader();
        var providers = new List<ModelProviderInstanceDto>();
        while (reader.Read())
        {
            providers.Add(ReadModelProvider(reader).ToDto());
        }

        return providers;
    }

    public StoredModelProviderInstance? GetStoredModelProviderInstance(string providerInstanceId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select id, driver, display_name, connection_kind, base_url, model, encrypted_api_key, binary_path, home_path,
                   server_url, launch_args, capabilities_json, enabled, health_status, cooldown_until, failure_count,
                   last_failure_at, last_error_category, created_at, updated_at
            from model_provider_instances
            where id = $id
            """;
        command.Parameters.AddWithValue("$id", providerInstanceId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadModelProvider(reader) : null;
    }

    public ModelProviderInstanceDto SaveModelProviderInstance(SaveModelProviderInstanceRequest request, string? encryptedApiKey)
    {
        var now = DateTimeOffset.UtcNow;
        var id = string.IsNullOrWhiteSpace(request.Id)
            ? $"provider_{Guid.NewGuid():N}"
            : NormalizeProviderInstanceId(request.Id);
        var driver = NormalizePlain(request.Driver, "openai-compatible");
        var displayName = NormalizePlain(request.DisplayName, driver);
        var connectionKind = NormalizeConnectionKind(request.ConnectionKind, driver);
        var baseUrl = NormalizeOptionalUrl(request.BaseUrl);
        var model = NormalizeOptional(request.Model);
        var capabilities = request.Capabilities is { Count: > 0 }
            ? request.Capabilities.Select(NormalizeOptional).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : InferCapabilities(driver, connectionKind);
        var capabilitiesJson = JsonSerializer.Serialize(capabilities, TinadecJson.Options);
        var existing = GetStoredModelProviderInstance(id);
        var health = ResolveProviderHealth(capabilities, existing);

        lock (_gate)
        {
            using var connection = OpenConnection();
            Execute(connection, """
                insert into model_provider_instances (
                    id,
                    driver,
                    display_name,
                    connection_kind,
                    base_url,
                    model,
                    encrypted_api_key,
                    binary_path,
                    home_path,
                    server_url,
                    launch_args,
                    capabilities_json,
                    enabled,
                    health_status,
                    cooldown_until,
                    failure_count,
                    last_failure_at,
                    last_error_category,
                    created_at,
                    updated_at
                )
                values (
                    $id,
                    $driver,
                    $display_name,
                    $connection_kind,
                    $base_url,
                    $model,
                    $encrypted_api_key,
                    $binary_path,
                    $home_path,
                    $server_url,
                    $launch_args,
                    $capabilities_json,
                    $enabled,
                    $health_status,
                    $cooldown_until,
                    $failure_count,
                    $last_failure_at,
                    $last_error_category,
                    $created_at,
                    $updated_at
                )
                on conflict(id) do update set
                    driver = excluded.driver,
                    display_name = excluded.display_name,
                    connection_kind = excluded.connection_kind,
                    base_url = excluded.base_url,
                    model = excluded.model,
                    encrypted_api_key = excluded.encrypted_api_key,
                    binary_path = excluded.binary_path,
                    home_path = excluded.home_path,
                    server_url = excluded.server_url,
                    launch_args = excluded.launch_args,
                    capabilities_json = excluded.capabilities_json,
                    enabled = excluded.enabled,
                    health_status = excluded.health_status,
                    cooldown_until = excluded.cooldown_until,
                    failure_count = excluded.failure_count,
                    last_failure_at = excluded.last_failure_at,
                    last_error_category = excluded.last_error_category,
                    updated_at = excluded.updated_at
                """, command =>
            {
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$driver", driver);
                command.Parameters.AddWithValue("$display_name", displayName);
                command.Parameters.AddWithValue("$connection_kind", connectionKind);
                command.Parameters.AddWithValue("$base_url", (object?)baseUrl ?? DBNull.Value);
                command.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
                command.Parameters.AddWithValue("$encrypted_api_key", (object?)encryptedApiKey ?? DBNull.Value);
                command.Parameters.AddWithValue("$binary_path", (object?)NormalizeOptional(request.BinaryPath) ?? DBNull.Value);
                command.Parameters.AddWithValue("$home_path", (object?)NormalizeOptional(request.HomePath) ?? DBNull.Value);
                command.Parameters.AddWithValue("$server_url", (object?)NormalizeOptionalUrl(request.ServerUrl) ?? DBNull.Value);
                command.Parameters.AddWithValue("$launch_args", (object?)NormalizeOptional(request.LaunchArgs) ?? DBNull.Value);
                command.Parameters.AddWithValue("$capabilities_json", capabilitiesJson);
                command.Parameters.AddWithValue("$enabled", request.Enabled ? 1 : 0);
                command.Parameters.AddWithValue("$health_status", ToProviderHealthStatusStorageValue(health.HealthStatus));
                command.Parameters.AddWithValue("$cooldown_until", health.CooldownUntil is null ? DBNull.Value : health.CooldownUntil.Value.ToString("O"));
                command.Parameters.AddWithValue("$failure_count", health.FailureCount);
                command.Parameters.AddWithValue("$last_failure_at", health.LastFailureAt is null ? DBNull.Value : health.LastFailureAt.Value.ToString("O"));
                command.Parameters.AddWithValue("$last_error_category", health.LastErrorCategory is null ? DBNull.Value : ToProviderErrorCategoryStorageValue(health.LastErrorCategory.Value));
                command.Parameters.AddWithValue("$created_at", now.ToString("O"));
                command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            });
        }

        return GetStoredModelProviderInstance(id)?.ToDto()
            ?? throw new InvalidOperationException("Saved model provider instance was not found.");
    }

    public bool DeleteModelProviderInstance(string providerInstanceId)
    {
        var existing = GetStoredModelProviderInstance(providerInstanceId);
        if (existing is null)
        {
            return false;
        }

        lock (_gate)
        {
            using var connection = OpenConnection();
            // Remove model routes that reference this provider
            Execute(connection, "delete from model_routes where provider_instance_id = $provider_instance_id",
                command => command.Parameters.AddWithValue("$provider_instance_id", providerInstanceId));
            // Remove the provider instance itself
            Execute(connection, "delete from model_provider_instances where id = $id",
                command => command.Parameters.AddWithValue("$id", providerInstanceId));
        }

        return true;
    }

    public void RecordModelProviderFailure(string providerInstanceId, ProviderErrorCategory category, DateTimeOffset now)
    {
        var provider = GetStoredModelProviderInstance(providerInstanceId)
            ?? throw new InvalidOperationException($"Model provider '{providerInstanceId}' was not found.");
        var failureCount = provider.FailureCount + 1;
        var capabilities = provider.Capabilities
            .Where(capability => !HasCapabilityKey(capability, "health")
                && !HasCapabilityKey(capability, "cooldown_started_at")
                && !HasCapabilityKey(capability, "cooldown_until")
                && !HasCapabilityKey(capability, "last_error")
                && !HasCapabilityKey(capability, "failure_count"))
            .Concat([
                "health:cooldown",
                $"cooldown_started_at:{now:O}",
                $"cooldown_until:{now.AddMinutes(5):O}",
                $"last_error:{category.ToString()}",
                $"failure_count:{failureCount}"
            ])
            .ToArray();
        var request = new SaveModelProviderInstanceRequest(
            provider.Id,
            provider.Driver,
            provider.DisplayName,
            provider.ConnectionKind,
            provider.BaseUrl,
            provider.Model,
            ApiKey: null,
            ClearApiKey: false,
            provider.BinaryPath,
            provider.HomePath,
            provider.ServerUrl,
            provider.LaunchArgs,
            capabilities,
                provider.Enabled);

        SaveModelProviderInstance(request, provider.EncryptedApiKey);
    }

    public void RecordModelProviderSuccess(string providerInstanceId)
    {
        var provider = GetStoredModelProviderInstance(providerInstanceId)
            ?? throw new InvalidOperationException($"Model provider '{providerInstanceId}' was not found.");

        if (provider.HealthStatus is not ProviderHealthStatus.Cooldown
            && provider.HealthStatus is not ProviderHealthStatus.Unhealthy)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var capabilities = provider.Capabilities
            .Where(capability => !HasCapabilityKey(capability, "health")
                && !HasCapabilityKey(capability, "cooldown_started_at")
                && !HasCapabilityKey(capability, "cooldown_until")
                && !HasCapabilityKey(capability, "last_error")
                && !HasCapabilityKey(capability, "failure_count"))
            .Concat([
                "health:healthy",
                "failure_count:0"
            ])
            .ToArray();
        var capabilitiesJson = JsonSerializer.Serialize(capabilities, TinadecJson.Options);

        lock (_gate)
        {
            using var connection = OpenConnection();
            Execute(connection, """
                update model_provider_instances
                set health_status = $health_status,
                    cooldown_until = null,
                    failure_count = 0,
                    last_failure_at = null,
                    last_error_category = null,
                    capabilities_json = $capabilities_json,
                    updated_at = $updated_at
                where id = $id
                """, command =>
            {
                command.Parameters.AddWithValue("$health_status", "healthy");
                command.Parameters.AddWithValue("$capabilities_json", capabilitiesJson);
                command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
                command.Parameters.AddWithValue("$id", providerInstanceId);
            });
        }
    }

    public IReadOnlyList<ModelRouteDto> ListModelRoutes()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "select purpose, provider_instance_id, model, updated_at from model_routes order by purpose";

        using var reader = command.ExecuteReader();
        var routes = new List<ModelRouteDto>();
        while (reader.Read())
        {
            routes.Add(ReadModelRoute(reader));
        }

        return routes;
    }

    public ModelRouteDto? GetModelRoute(string purpose)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "select purpose, provider_instance_id, model, updated_at from model_routes where purpose = $purpose";
        command.Parameters.AddWithValue("$purpose", NormalizeRoutePurpose(purpose));

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadModelRoute(reader) : null;
    }

    public ModelRouteDto SaveModelRoute(string purpose, string providerInstanceId, string? model)
    {
        var normalizedPurpose = NormalizeRoutePurpose(purpose);
        var normalizedModel = NormalizeOptional(model);
        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            using var connection = OpenConnection();
            Execute(connection, """
                insert into model_routes (purpose, provider_instance_id, model, updated_at)
                values ($purpose, $provider_instance_id, $model, $updated_at)
                on conflict(purpose) do update set
                    provider_instance_id = excluded.provider_instance_id,
                    model = excluded.model,
                    updated_at = excluded.updated_at
                """, command =>
            {
                command.Parameters.AddWithValue("$purpose", normalizedPurpose);
                command.Parameters.AddWithValue("$provider_instance_id", providerInstanceId);
                command.Parameters.AddWithValue("$model", (object?)normalizedModel ?? DBNull.Value);
                command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            });
        }

        return new ModelRouteDto(normalizedPurpose, providerInstanceId, normalizedModel, now);
    }

    public IReadOnlyList<ExtensionSourceDto> ListExtensionSources()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select id, name, kind, location, enabled, last_refreshed_at, created_at
            from extension_sources
            order by name
            """;

        using var reader = command.ExecuteReader();
        var sources = new List<ExtensionSourceDto>();
        while (reader.Read())
        {
            sources.Add(ReadExtensionSource(reader));
        }

        return sources;
    }

    public ExtensionSourceDto CreateExtensionSource(CreateExtensionSourceRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var name = NormalizePlain(request.Name, "Custom Marketplace");
        var kind = NormalizeExtensionSourceKind(request.Kind);
        var location = NormalizePlain(request.Location, "local");
        var id = $"source_{ExtensionCatalog.NormalizeId($"{name}-{Guid.NewGuid():N}"[..Math.Min(name.Length + 9, name.Length + 9)])}";

        var source = new ExtensionSourceDto(id, name, kind, location, request.Enabled, null, now);
        lock (_gate)
        {
            using var connection = OpenConnection();
            Execute(connection, """
                insert into extension_sources (id, name, kind, location, enabled, last_refreshed_at, created_at)
                values ($id, $name, $kind, $location, $enabled, null, $created_at)
                """, command =>
            {
                command.Parameters.AddWithValue("$id", source.Id);
                command.Parameters.AddWithValue("$name", source.Name);
                command.Parameters.AddWithValue("$kind", source.Kind);
                command.Parameters.AddWithValue("$location", source.Location);
                command.Parameters.AddWithValue("$enabled", source.Enabled ? 1 : 0);
                command.Parameters.AddWithValue("$created_at", source.CreatedAt.ToString("O"));
            });
        }

        return source;
    }

    public ExtensionSourceDto? RefreshExtensionSource(string sourceId)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            using var connection = OpenConnection();
            Execute(connection, """
                update extension_sources
                set last_refreshed_at = $last_refreshed_at
                where id = $id
                """, command =>
            {
                command.Parameters.AddWithValue("$id", sourceId);
                command.Parameters.AddWithValue("$last_refreshed_at", now.ToString("O"));
            });

            using var command = connection.CreateCommand();
            command.CommandText = """
                select id, name, kind, location, enabled, last_refreshed_at, created_at
                from extension_sources
                where id = $id
                """;
            command.Parameters.AddWithValue("$id", sourceId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadExtensionSource(reader) : null;
        }
    }

    public IReadOnlyList<MarketCatalogItemDto> ListMarketCatalog(string? kind, string? query, string? sourceId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(kind) && kind != "all")
        {
            where.Add("c.kind = $kind");
            command.Parameters.AddWithValue("$kind", kind.Trim());
        }

        if (!string.IsNullOrWhiteSpace(sourceId))
        {
            where.Add("c.source_id = $source_id");
            command.Parameters.AddWithValue("$source_id", sourceId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            where.Add("(c.display_name like $query or c.description like $query or c.extension_id like $query)");
            command.Parameters.AddWithValue("$query", $"%{query.Trim()}%");
        }

        command.CommandText = """
            select c.catalog_id, c.source_id, c.extension_id, c.kind, c.version, c.publisher, c.display_name,
                   c.description, c.source_kind, c.source_location, c.capabilities_json, c.permissions_json,
                   i.id
            from extension_catalog_cache c
            left join installed_extensions i on i.extension_id = c.extension_id
            """
            + (where.Count == 0 ? string.Empty : " where " + string.Join(" and ", where))
            + " order by c.kind, c.display_name";

        using var reader = command.ExecuteReader();
        var items = new List<MarketCatalogItemDto>();
        while (reader.Read())
        {
            items.Add(ReadMarketCatalogItem(reader));
        }

        return items;
    }

    public MarketCatalogItemDto? GetMarketCatalogItem(string catalogId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select c.catalog_id, c.source_id, c.extension_id, c.kind, c.version, c.publisher, c.display_name,
                   c.description, c.source_kind, c.source_location, c.capabilities_json, c.permissions_json,
                   i.id
            from extension_catalog_cache c
            left join installed_extensions i on i.extension_id = c.extension_id
            where c.catalog_id = $catalog_id
            """;
        command.Parameters.AddWithValue("$catalog_id", catalogId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadMarketCatalogItem(reader) : null;
    }

    public ExtensionInstallPreviewDto PreviewExtensionInstall(InstallExtensionPreviewRequest request)
    {
        var catalogItem = string.IsNullOrWhiteSpace(request.CatalogId) ? null : GetMarketCatalogItem(request.CatalogId);
        if (!string.IsNullOrWhiteSpace(request.CatalogId) && catalogItem is null)
        {
            throw new InvalidOperationException("Market catalog item was not found.");
        }

        var descriptor = ExtensionCatalog.DescriptorFromRequest(request, catalogItem);
        ValidateExtensionDescriptor(descriptor);
        return ExtensionCatalog.BuildPreview(descriptor);
    }

    public ExtensionInstallResultDto InstallExtension(InstallExtensionRequest request)
    {
        var previewRequest = new InstallExtensionPreviewRequest(request.CatalogId, request.SourceKind, request.SourceLocation, request.ManifestJson);
        var catalogItem = string.IsNullOrWhiteSpace(request.CatalogId) ? null : GetMarketCatalogItem(request.CatalogId);
        var descriptor = ExtensionCatalog.DescriptorFromRequest(previewRequest, catalogItem);
        ValidateExtensionDescriptor(descriptor);
        var preview = ExtensionCatalog.BuildPreview(descriptor);

        if (string.IsNullOrWhiteSpace(request.ApprovalId))
        {
            var approval = CreateApproval(new CreateApprovalRequest(
                null,
                "extension.install",
                preview.ApprovalSummary,
                descriptor.SourceLocation,
                null));
            return new ExtensionInstallResultDto(true, approval, null, preview);
        }

        var approvalStatus = GetApprovalStatus(request.ApprovalId);
        if (!string.Equals(approvalStatus, "approved", StringComparison.OrdinalIgnoreCase))
        {
            var approval = GetApproval(request.ApprovalId);
            return new ExtensionInstallResultDto(true, approval, null, preview);
        }

        var installed = SaveInstalledExtension(catalogItem?.CatalogId, descriptor, enabled: false, "installed_disabled", "Installed and waiting for explicit enablement.");
        return new ExtensionInstallResultDto(false, null, installed, preview);
    }

    public IReadOnlyList<InstalledExtensionDto> ListInstalledExtensions()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select id, catalog_id, extension_id, kind, version, publisher, display_name, description, source_kind,
                   source_location, capabilities_json, permissions_json, enabled, status, status_message, installed_at, updated_at
            from installed_extensions
            order by updated_at desc, display_name
            """;

        using var reader = command.ExecuteReader();
        var items = new List<InstalledExtensionDto>();
        while (reader.Read())
        {
            items.Add(ReadInstalledExtension(reader));
        }

        return items;
    }

    public InstalledExtensionDto? SetExtensionEnabled(string installedExtensionId, bool enabled)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = GetInstalledExtension(installedExtensionId);
        if (existing is null)
        {
            return null;
        }

        var status = enabled ? "enabled" : "installed_disabled";
        var statusMessage = enabled
            ? "Enabled. Runtime adapters will be made available through Core policy."
            : "Installed and waiting for explicit enablement.";

        lock (_gate)
        {
            using var connection = OpenConnection();
            Execute(connection, """
                update installed_extensions
                set enabled = $enabled, status = $status, status_message = $status_message, updated_at = $updated_at
                where id = $id
                """, command =>
            {
                command.Parameters.AddWithValue("$id", installedExtensionId);
                command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
                command.Parameters.AddWithValue("$status", status);
                command.Parameters.AddWithValue("$status_message", statusMessage);
                command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            });

            if (enabled)
            {
                UpsertRuntimeCache(connection, existing, now);
            }
            else
            {
                RemoveRuntimeCache(connection, existing.Id);
            }
        }

        return GetInstalledExtension(installedExtensionId);
    }

    public bool DeleteInstalledExtension(string installedExtensionId)
    {
        var existing = GetInstalledExtension(installedExtensionId);
        if (existing is null)
        {
            return false;
        }

        lock (_gate)
        {
            using var connection = OpenConnection();
            RemoveRuntimeCache(connection, installedExtensionId);
            Execute(connection, "delete from installed_extensions where id = $id", command => command.Parameters.AddWithValue("$id", installedExtensionId));
        }

        return true;
    }

    public IReadOnlyList<McpServerDto> ListMcpServers()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select m.id, m.extension_id, m.name, m.transport, m.status, m.tools_json, m.updated_at, e.manifest_json
            from mcp_servers m
            left join installed_extensions e on e.id = m.extension_id
            order by m.name
            """;
        using var reader = command.ExecuteReader();
        var servers = new List<McpServerDto>();
        while (reader.Read())
        {
            servers.Add(ReadMcpServer(reader));
        }

        return servers;
    }

    public McpServerDto? ReloadMcpServer(string serverId)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            using var connection = OpenConnection();
            Execute(connection, """
                update mcp_servers
                set status = 'pending_connect', updated_at = $updated_at
                where id = $id
                """, command =>
            {
                command.Parameters.AddWithValue("$id", serverId);
                command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            });
        }

        return ListMcpServers().FirstOrDefault(server => server.Id == serverId);
    }

    public McpServerDto? UpdateMcpServerStatus(string serverId, string status, string? statusMessage)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            using var connection = OpenConnection();
            Execute(connection, """
                update mcp_servers
                set status = $status, updated_at = $updated_at
                where id = $id
                """, command =>
            {
                command.Parameters.AddWithValue("$id", serverId);
                command.Parameters.AddWithValue("$status", status);
                command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            });
        }

        return ListMcpServers().FirstOrDefault(server => server.Id == serverId);
    }

    public McpServerDto? UpdateMcpServerTools(string serverId, IReadOnlyList<string> tools)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            using var connection = OpenConnection();
            Execute(connection, """
                update mcp_servers
                set tools_json = $tools_json, updated_at = $updated_at
                where id = $id
                """, command =>
            {
                command.Parameters.AddWithValue("$id", serverId);
                command.Parameters.AddWithValue("$tools_json", JsonSerializer.Serialize(tools, TinadecJson.Options));
                command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            });
        }

        return ListMcpServers().FirstOrDefault(server => server.Id == serverId);
    }

    public IReadOnlyList<AcpAdapterDto> ListAcpAdapters()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "select id, extension_id, name, command, status, status_message, capabilities_json, updated_at from acp_adapters order by name";
        using var reader = command.ExecuteReader();
        var adapters = new List<AcpAdapterDto>();
        while (reader.Read())
        {
            adapters.Add(ReadAcpAdapter(reader));
        }

        return adapters;
    }

    public AcpAdapterDto? ProbeAcpAdapter(string adapterId)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            using var connection = OpenConnection();
            Execute(connection, """
                update acp_adapters
                set status = 'ready', status_message = 'Probe completed against declarative adapter metadata.', updated_at = $updated_at
                where id = $id
                """, command =>
            {
                command.Parameters.AddWithValue("$id", adapterId);
                command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            });
        }

        return ListAcpAdapters().FirstOrDefault(adapter => adapter.Id == adapterId);
    }

    public IReadOnlyList<AgentModeDto> ListAgentModes() => AgentCatalog.Modes;

    public IReadOnlyList<AgentProfileDto> ListAgentProfiles()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select id, name, layer, agent_type, mode, description, model_route_purpose, allowed_tools_json,
                   capabilities_json, system_prompt, enabled, is_builtin, updated_at
            from agent_profiles
            order by case layer when 'planning' then 0 else 1 end, name
            """;
        using var reader = command.ExecuteReader();
        var agents = new List<AgentProfileDto>();
        while (reader.Read())
        {
            agents.Add(ReadAgentProfile(reader));
        }

        return agents;
    }

    public AgentProfileDto? SaveAgentProfile(string agentId, SaveAgentProfileRequest request)
    {
        var existing = ListAgentProfiles().FirstOrDefault(agent => agent.Id == agentId);
        if (existing is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var mode = AgentCatalog.Modes.Any(item => item.Id == request.Mode) ? request.Mode : existing.Mode;
        var layer = request.Layer is "planning" or "execution" ? request.Layer : existing.Layer;
        var tools = request.AllowedTools is { Count: > 0 } ? request.AllowedTools : existing.AllowedTools;
        var capabilities = request.Capabilities is { Count: > 0 } ? request.Capabilities : existing.Capabilities;

        lock (_gate)
        {
            using var connection = OpenConnection();
            Execute(connection, """
                update agent_profiles
                set name = $name,
                    layer = $layer,
                    agent_type = $agent_type,
                    mode = $mode,
                    description = $description,
                    model_route_purpose = $model_route_purpose,
                    allowed_tools_json = $allowed_tools_json,
                    capabilities_json = $capabilities_json,
                    system_prompt = $system_prompt,
                    enabled = $enabled,
                    updated_at = $updated_at
                where id = $id
                """, command =>
            {
                command.Parameters.AddWithValue("$id", agentId);
                command.Parameters.AddWithValue("$name", NormalizePlain(request.Name, existing.Name));
                command.Parameters.AddWithValue("$layer", layer);
                command.Parameters.AddWithValue("$agent_type", NormalizePlain(request.AgentType, existing.AgentType));
                command.Parameters.AddWithValue("$mode", mode);
                command.Parameters.AddWithValue("$description", NormalizePlain(request.Description, existing.Description));
                command.Parameters.AddWithValue("$model_route_purpose", NormalizeRoutePurpose(request.ModelRoutePurpose));
                command.Parameters.AddWithValue("$allowed_tools_json", JsonSerializer.Serialize(tools, TinadecJson.Options));
                command.Parameters.AddWithValue("$capabilities_json", JsonSerializer.Serialize(capabilities, TinadecJson.Options));
                command.Parameters.AddWithValue("$system_prompt", (object?)request.SystemPrompt ?? DBNull.Value);
                command.Parameters.AddWithValue("$enabled", request.Enabled ? 1 : 0);
                command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            });

            UpsertAgentSystemPromptFragment(connection, agentId, request.SystemPrompt, now);
        }

        return ListAgentProfiles().FirstOrDefault(agent => agent.Id == agentId);
    }

    public AgentProfileDto? UpdateAgentMode(string agentId, string mode)
    {
        var existing = ListAgentProfiles().FirstOrDefault(agent => agent.Id == agentId);
        if (existing is null)
        {
            return null;
        }

        return SaveAgentProfile(agentId, new SaveAgentProfileRequest(
            existing.Name,
            existing.Layer,
            existing.AgentType,
            mode,
            existing.Description,
            existing.ModelRoutePurpose,
            existing.AllowedTools,
            existing.Capabilities,
            existing.SystemPrompt,
            existing.Enabled));
    }

    public IReadOnlyList<AgentCandidateDto> ListAgentCandidates()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select id, generated_by_agent_id, name, layer, agent_type, description, suggested_tools_json,
                   evaluation_notes_json, status, created_at
            from agent_candidates
            order by created_at desc
            """;
        using var reader = command.ExecuteReader();
        var candidates = new List<AgentCandidateDto>();
        while (reader.Read())
        {
            candidates.Add(ReadAgentCandidate(reader));
        }

        return candidates;
    }

    public IReadOnlyList<PromptFragmentDto> ListPromptFragments(
        string? scope = null,
        string? targetAgentId = null,
        string? category = null,
        bool? enabled = null)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(scope))
        {
            filters.Add("scope = $scope");
            command.Parameters.AddWithValue("$scope", NormalizePromptScope(scope));
        }

        if (!string.IsNullOrWhiteSpace(targetAgentId))
        {
            filters.Add("(target_agent_id is null or target_agent_id = $target_agent_id)");
            command.Parameters.AddWithValue("$target_agent_id", targetAgentId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            filters.Add("category = $category");
            command.Parameters.AddWithValue("$category", NormalizePlain(category, "general"));
        }

        if (enabled is not null)
        {
            filters.Add("enabled = $enabled");
            command.Parameters.AddWithValue("$enabled", enabled.Value ? 1 : 0);
        }

        command.CommandText = $"""
            select id, key, title, scope, target_agent_id, category, content, priority,
                   enabled, is_builtin, created_at, updated_at
            from prompt_fragments
            {(filters.Count == 0 ? "" : $"where {string.Join(" and ", filters)}")}
            order by priority desc, is_builtin asc, title
            """;

        using var reader = command.ExecuteReader();
        var fragments = new List<PromptFragmentDto>();
        while (reader.Read())
        {
            fragments.Add(ReadPromptFragment(reader));
        }

        return fragments;
    }

    public PromptFragmentDto? GetPromptFragment(string fragmentId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select id, key, title, scope, target_agent_id, category, content, priority,
                   enabled, is_builtin, created_at, updated_at
            from prompt_fragments
            where id = $id
            """;
        command.Parameters.AddWithValue("$id", fragmentId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPromptFragment(reader) : null;
    }

    public PromptFragmentDto CreatePromptFragment(SavePromptFragmentRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var fragment = new PromptFragmentDto(
            $"prompt_{Guid.NewGuid():N}",
            NormalizePromptKey(request.Key, $"custom.{Guid.NewGuid():N}"),
            NormalizePlain(request.Title, "Custom prompt fragment"),
            NormalizePromptScope(request.Scope),
            NormalizeOptional(request.TargetAgentId),
            NormalizePlain(request.Category, "general"),
            NormalizePlain(request.Content, ""),
            request.Priority,
            request.Enabled,
            false,
            now,
            now);

        lock (_gate)
        {
            using var connection = OpenConnection();
            InsertPromptFragment(connection, fragment, updateBuiltin: false);
        }

        return fragment;
    }

    public PromptFragmentDto? UpdatePromptFragment(string fragmentId, SavePromptFragmentRequest request)
    {
        var existing = GetPromptFragment(fragmentId);
        if (existing is null)
        {
            return null;
        }

        if (existing.IsBuiltIn)
        {
            throw new InvalidOperationException("Built-in prompt fragments are read-only. Clone the fragment before editing.");
        }

        var now = DateTimeOffset.UtcNow;
        var updated = existing with
        {
            Key = NormalizePromptKey(request.Key, existing.Key),
            Title = NormalizePlain(request.Title, existing.Title),
            Scope = NormalizePromptScope(request.Scope),
            TargetAgentId = NormalizeOptional(request.TargetAgentId),
            Category = NormalizePlain(request.Category, existing.Category),
            Content = NormalizePlain(request.Content, existing.Content),
            Priority = request.Priority,
            Enabled = request.Enabled,
            UpdatedAt = now
        };

        lock (_gate)
        {
            using var connection = OpenConnection();
            InsertPromptFragment(connection, updated, updateBuiltin: false);
        }

        return GetPromptFragment(fragmentId);
    }

    public bool DeletePromptFragment(string fragmentId)
    {
        var existing = GetPromptFragment(fragmentId);
        if (existing is null)
        {
            return false;
        }

        if (existing.IsBuiltIn)
        {
            throw new InvalidOperationException("Built-in prompt fragments are read-only. Clone the fragment before deleting a custom copy.");
        }

        lock (_gate)
        {
            using var connection = OpenConnection();
            Execute(connection, "delete from prompt_fragments where id = $id", command =>
            {
                command.Parameters.AddWithValue("$id", fragmentId);
            });
        }

        return true;
    }

    public PromptFragmentDto? ClonePromptFragment(string fragmentId)
    {
        var source = GetPromptFragment(fragmentId);
        if (source is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var cloneKey = NormalizePromptKey($"{source.Key}.custom.{Guid.NewGuid():N}", $"custom.{Guid.NewGuid():N}");
        var clone = source with
        {
            Id = $"prompt_{Guid.NewGuid():N}",
            Key = cloneKey,
            Title = $"{source.Title} (Custom)",
            Priority = source.Priority + 10,
            Enabled = true,
            IsBuiltIn = false,
            CreatedAt = now,
            UpdatedAt = now
        };

        lock (_gate)
        {
            using var connection = OpenConnection();
            InsertPromptFragment(connection, clone, updateBuiltin: false);
        }

        return clone;
    }

    public PromptContextPlanDto SavePromptContextPlan(PromptContextPlanDto plan)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            Execute(connection, """
                insert into prompt_context_plans (
                    id,
                    run_id,
                    agent_id,
                    strategy,
                    selected_fragment_ids_json,
                    summary,
                    created_by_agent_id,
                    created_at
                )
                values (
                    $id,
                    $run_id,
                    $agent_id,
                    $strategy,
                    $selected_fragment_ids_json,
                    $summary,
                    $created_by_agent_id,
                    $created_at
                )
                """, command =>
            {
                command.Parameters.AddWithValue("$id", $"prompt_plan_{Guid.NewGuid():N}");
                command.Parameters.AddWithValue("$run_id", plan.RunId);
                command.Parameters.AddWithValue("$agent_id", plan.AgentId);
                command.Parameters.AddWithValue("$strategy", plan.Strategy);
                command.Parameters.AddWithValue("$selected_fragment_ids_json", JsonSerializer.Serialize(plan.SelectedFragmentIds, TinadecJson.Options));
                command.Parameters.AddWithValue("$summary", plan.Summary);
                command.Parameters.AddWithValue("$created_by_agent_id", plan.CreatedByAgentId);
                command.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToString("O"));
            });
        }

        return plan;
    }

    public OrchestrationSnapshotDto CreateOrchestrationRun(string sessionId, string? userMessageId, string userContent)
    {
        var now = DateTimeOffset.UtcNow;
        var run = new OrchestrationRunDto(
            $"run_{Guid.NewGuid():N}",
            sessionId,
            userMessageId,
            "planned",
            SummarizeUserGoal(userContent),
            now,
            now);
        var graph = new TaskGraphDto(
            $"graph_{Guid.NewGuid():N}",
            run.Id,
            sessionId,
            "Two-layer orchestration",
            "ready",
            now,
            now);

        var taskSpecs = BuildTaskSpecs(userContent);
        var enabledAgents = ListAgentProfiles()
            .Where(agent => agent.Enabled && agent.Layer == "execution")
            .ToArray();
        var nodes = new List<TaskNodeDto>();
        var assignments = new List<AgentAssignmentDto>();
        var stepResults = new List<StepResultDto>();

        lock (_gate)
        {
            using var connection = OpenConnection();
            InsertRun(connection, run);
            InsertTaskGraph(connection, graph);

            for (var index = 0; index < taskSpecs.Count; index++)
            {
                var spec = taskSpecs[index];
                var node = new TaskNodeDto(
                    $"node_{Guid.NewGuid():N}",
                    graph.Id,
                    run.Id,
                    sessionId,
                    spec.Title,
                    spec.Description,
                    "queued",
                    index + 1,
                    spec.Risk,
                    spec.SuccessCriteria,
                    index == 0 ? [] : [nodes[0].Id],
                    spec.RequiredCapabilities,
                    now,
                    now);
                nodes.Add(node);
                InsertTaskNode(connection, node);

                var agent = ResolveExecutionAgent(enabledAgents, spec.AgentType, spec.RequiredCapabilities);
                if (agent is null)
                {
                    continue;
                }

                var assignment = new AgentAssignmentDto(
                    $"assign_{Guid.NewGuid():N}",
                    run.Id,
                    node.Id,
                    agent.Id,
                    agent.Name,
                    agent.Layer,
                    agent.AgentType,
                    agent.ModelRoutePurpose,
                    spec.PermissionMode,
                    agent.AllowedTools,
                    "assigned",
                    now);
                assignments.Add(assignment);
                InsertAgentAssignment(connection, assignment);

                var result = new StepResultDto(
                    $"step_{Guid.NewGuid():N}",
                    run.Id,
                    node.Id,
                    agent.Id,
                    "stubbed",
                    $"{agent.Name} is assigned. Runtime execution remains read-only until the task requests approved tools.",
                    ["assignment recorded", $"permission mode: {spec.PermissionMode}", $"model route: {agent.ModelRoutePurpose}"],
                    now);
                stepResults.Add(result);
                InsertStepResult(connection, result);
            }

            var contextPack = new ContextPackDto(
                $"ctx_{Guid.NewGuid():N}",
                run.Id,
                sessionId,
                "agent_realtime_context_compressor",
                $"Compressed planning context for {nodes.Count} task nodes. Source user goal: {run.Summary}",
                12000,
                0.35,
                nodes.Select(node => $"{node.Id}:{node.Title}").ToArray(),
                now);
            InsertContextPack(connection, contextPack);

            var finding = new SupervisionFindingDto(
                $"sup_{Guid.NewGuid():N}",
                run.Id,
                sessionId,
                "info",
                "goal-alignment",
                "Run is constrained to visible planning, read-only assignment stubs, and approval-gated execution.",
                "Review the task graph before approving mutating tools, shell, Git, network, MCP, or ACP actions.",
                "open",
                now);
            InsertSupervisionFinding(connection, finding);
        }

        return GetOrchestrationSnapshot(sessionId);
    }

    public OrchestrationSnapshotDto GetOrchestrationSnapshot(string sessionId)
    {
        var run = ListRuns(sessionId).FirstOrDefault();
        if (run is null)
        {
            return new OrchestrationSnapshotDto(null, null, [], [], [], [], []);
        }

        var graph = GetTaskGraph(run.Id);
        return new OrchestrationSnapshotDto(
            run,
            graph,
            ListTaskNodes(sessionId).Where(node => node.RunId == run.Id).ToArray(),
            ListAgentAssignments(run.Id),
            ListStepResults(run.Id),
            ListContextPacks(sessionId).Where(pack => pack.RunId == run.Id).ToArray(),
            ListSupervisionFindings(sessionId).Where(finding => finding.RunId == run.Id).ToArray());
    }

    public OrchestrationSnapshotDto? GetOrchestrationSnapshotByRun(string runId)
    {
        var run = GetRun(runId);
        if (run is null)
        {
            return null;
        }

        var graph = GetTaskGraph(run.Id);
        return new OrchestrationSnapshotDto(
            run,
            graph,
            ListTaskNodes(run.SessionId).Where(node => node.RunId == run.Id).ToArray(),
            ListAgentAssignments(run.Id),
            ListStepResults(run.Id),
            ListContextPacks(run.SessionId).Where(pack => pack.RunId == run.Id).ToArray(),
            ListSupervisionFindings(run.SessionId).Where(finding => finding.RunId == run.Id).ToArray());
    }

    public IReadOnlyList<OrchestrationRunDto> ListRuns(string sessionId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select id, session_id, user_message_id, status, summary, created_at, updated_at
            from orchestration_runs
            where session_id = $session_id
            order by created_at desc
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);

        using var reader = command.ExecuteReader();
        var runs = new List<OrchestrationRunDto>();
        while (reader.Read())
        {
            runs.Add(ReadOrchestrationRun(reader));
        }

        return runs;
    }

    public OrchestrationRunDto? GetRun(string runId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select id, session_id, user_message_id, status, summary, created_at, updated_at
            from orchestration_runs
            where id = $id
            """;
        command.Parameters.AddWithValue("$id", runId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadOrchestrationRun(reader) : null;
    }

    public StepResultDto AddStepResult(
        string runId,
        string taskNodeId,
        string agentId,
        string status,
        string summary,
        IReadOnlyList<string> evidence)
    {
        var result = new StepResultDto(
            $"step_{Guid.NewGuid():N}",
            runId,
            taskNodeId,
            agentId,
            status,
            summary,
            evidence,
            DateTimeOffset.UtcNow);

        lock (_gate)
        {
            using var connection = OpenConnection();
            InsertStepResult(connection, result);
        }

        return result;
    }

    public IReadOnlyList<TaskNodeDto> ListTaskNodes(string sessionId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select id, graph_id, run_id, session_id, title, description, status, priority, risk,
                   success_criteria_json, dependencies_json, required_capabilities_json, created_at, updated_at
            from task_nodes
            where session_id = $session_id
            order by priority asc, created_at asc
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);

        using var reader = command.ExecuteReader();
        var nodes = new List<TaskNodeDto>();
        while (reader.Read())
        {
            nodes.Add(ReadTaskNode(reader));
        }

        return nodes;
    }

    public IReadOnlyList<ContextPackDto> ListContextPacks(string sessionId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select id, run_id, session_id, created_by_agent_id, summary, token_budget,
                   compression_ratio, evidence_map_json, created_at
            from context_packs
            where session_id = $session_id
            order by created_at desc
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);

        using var reader = command.ExecuteReader();
        var packs = new List<ContextPackDto>();
        while (reader.Read())
        {
            packs.Add(ReadContextPack(reader));
        }

        return packs;
    }

    public IReadOnlyList<SupervisionFindingDto> ListSupervisionFindings(string sessionId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select id, run_id, session_id, severity, category, summary, recommendation, status, created_at
            from supervision_findings
            where session_id = $session_id
            order by created_at desc
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);

        using var reader = command.ExecuteReader();
        var findings = new List<SupervisionFindingDto>();
        while (reader.Read())
        {
            findings.Add(ReadSupervisionFinding(reader));
        }

        return findings;
    }

    private TaskGraphDto? GetTaskGraph(string runId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select id, run_id, session_id, title, status, created_at, updated_at
            from task_graphs
            where run_id = $run_id
            order by created_at desc
            limit 1
            """;
        command.Parameters.AddWithValue("$run_id", runId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTaskGraph(reader) : null;
    }

    private IReadOnlyList<AgentAssignmentDto> ListAgentAssignments(string runId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select id, run_id, task_node_id, agent_id, agent_name, agent_layer, agent_type,
                   model_route_purpose, permission_mode, allowed_tools_json, status, created_at
            from agent_assignments
            where run_id = $run_id
            order by created_at asc
            """;
        command.Parameters.AddWithValue("$run_id", runId);

        using var reader = command.ExecuteReader();
        var assignments = new List<AgentAssignmentDto>();
        while (reader.Read())
        {
            assignments.Add(ReadAgentAssignment(reader));
        }

        return assignments;
    }

    private IReadOnlyList<StepResultDto> ListStepResults(string runId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select id, run_id, task_node_id, agent_id, status, summary, evidence_json, created_at
            from step_results
            where run_id = $run_id
            order by created_at asc
            """;
        command.Parameters.AddWithValue("$run_id", runId);

        using var reader = command.ExecuteReader();
        var results = new List<StepResultDto>();
        while (reader.Read())
        {
            results.Add(ReadStepResult(reader));
        }

        return results;
    }

    private sealed record TaskSpec(
        string Title,
        string Description,
        string AgentType,
        string Risk,
        string PermissionMode,
        IReadOnlyList<string> SuccessCriteria,
        IReadOnlyList<string> RequiredCapabilities);

    private static IReadOnlyList<TaskSpec> BuildTaskSpecs(string userContent)
    {
        var goal = SummarizeUserGoal(userContent);
        var specs = new List<TaskSpec>
        {
            new(
                "Plan the work",
                $"Break down the user goal and keep success criteria visible: {goal}",
                "task-planner",
                "read-only",
                "observe",
                ["Task graph exists", "Success criteria are explicit", "Approval points are marked"],
                ["step.run", "plan.decompose"]),
            new(
                "Search relevant context",
                "Collect files, symbols, docs, or prior events needed before execution.",
                "search-specialist",
                "read-only",
                "observe",
                ["Relevant evidence is collected", "No workspace mutation occurs"],
                ["search.query", "evidence.collect", "web.search"]),
            new(
                "Locate code touchpoints",
                "Find concrete code locations, dependencies, and boundaries for the requested change.",
                "code-explorer",
                "read-only",
                "observe",
                ["Candidate files are identified", "Ownership boundaries are recorded"],
                ["code.search", "symbol.locate", "evidence.collect"]),
            new(
                "Prepare validation",
                "Identify tests, checks, or commands that should validate the result after approval.",
                "test-multimodal",
                "approval-gated",
                "approval",
                ["Validation commands are named", "Shell execution remains approval-gated"],
                ["test.run", "failure.classify"]),
        };

        if (NeedsGitManager(userContent))
        {
            specs.Add(new(
                "Prepare Git handoff",
                "Review branch, diff, commit, push readiness, and user-facing Git explanation under Core approval policy.",
                "git-manager",
                "approval-gated",
                "approval",
                ["Git state is explained", "Push or history mutation remains approval-gated", "Handoff notes are ready"],
                ["git.status", "git.diff", "git.push", "handoff.explain"]));
        }

        specs.Add(
            new(
                "Synthesize execution guidance",
                "Combine planning, evidence, supervision, and model reasoning into the next actionable step.",
                "code-writer",
                "read-only",
                "observe",
                ["Next step is clear", "Unresolved risks are visible"],
                ["code.write", "step.result"]));

        return specs;
    }

    private static bool NeedsGitManager(string userContent)
    {
        return ContainsAny(userContent, ["git", "commit", "push", "branch", "worktree", "merge", "rebase", "pull request", "pr "]);
    }

    private static bool ContainsAny(string content, IReadOnlyList<string> terms)
    {
        return terms.Any(term => content.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static AgentProfileDto? ResolveExecutionAgent(
        IReadOnlyList<AgentProfileDto> agents,
        string preferredAgentType,
        IReadOnlyList<string> requiredCapabilities)
    {
        return agents.FirstOrDefault(agent => agent.AgentType == preferredAgentType)
            ?? agents.FirstOrDefault(agent =>
                agent.AgentType != preferredAgentType &&
                requiredCapabilities.Any(required => agent.Capabilities.Contains(required)));
    }

    private static string SummarizeUserGoal(string content)
    {
        var normalized = NormalizePlain(content, "User requested a TinadecCode task.");
        normalized = normalized.ReplaceLineEndings(" ");
        return normalized.Length <= 140 ? normalized : $"{normalized[..137]}...";
    }

    private static void InsertRun(SqliteConnection connection, OrchestrationRunDto run)
    {
        Execute(connection, """
            insert into orchestration_runs (id, session_id, user_message_id, status, summary, created_at, updated_at)
            values ($id, $session_id, $user_message_id, $status, $summary, $created_at, $updated_at)
            """, command =>
        {
            command.Parameters.AddWithValue("$id", run.Id);
            command.Parameters.AddWithValue("$session_id", run.SessionId);
            command.Parameters.AddWithValue("$user_message_id", (object?)run.UserMessageId ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", run.Status);
            command.Parameters.AddWithValue("$summary", run.Summary);
            command.Parameters.AddWithValue("$created_at", run.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$updated_at", run.UpdatedAt.ToString("O"));
        });
    }

    private static void InsertTaskGraph(SqliteConnection connection, TaskGraphDto graph)
    {
        Execute(connection, """
            insert into task_graphs (id, run_id, session_id, title, status, created_at, updated_at)
            values ($id, $run_id, $session_id, $title, $status, $created_at, $updated_at)
            """, command =>
        {
            command.Parameters.AddWithValue("$id", graph.Id);
            command.Parameters.AddWithValue("$run_id", graph.RunId);
            command.Parameters.AddWithValue("$session_id", graph.SessionId);
            command.Parameters.AddWithValue("$title", graph.Title);
            command.Parameters.AddWithValue("$status", graph.Status);
            command.Parameters.AddWithValue("$created_at", graph.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$updated_at", graph.UpdatedAt.ToString("O"));
        });
    }

    private static void InsertTaskNode(SqliteConnection connection, TaskNodeDto node)
    {
        Execute(connection, """
            insert into task_nodes (
                id, graph_id, run_id, session_id, title, description, status, priority, risk,
                success_criteria_json, dependencies_json, required_capabilities_json, created_at, updated_at
            )
            values (
                $id, $graph_id, $run_id, $session_id, $title, $description, $status, $priority, $risk,
                $success_criteria_json, $dependencies_json, $required_capabilities_json, $created_at, $updated_at
            )
            """, command =>
        {
            command.Parameters.AddWithValue("$id", node.Id);
            command.Parameters.AddWithValue("$graph_id", node.GraphId);
            command.Parameters.AddWithValue("$run_id", node.RunId);
            command.Parameters.AddWithValue("$session_id", node.SessionId);
            command.Parameters.AddWithValue("$title", node.Title);
            command.Parameters.AddWithValue("$description", node.Description);
            command.Parameters.AddWithValue("$status", node.Status);
            command.Parameters.AddWithValue("$priority", node.Priority);
            command.Parameters.AddWithValue("$risk", node.Risk);
            command.Parameters.AddWithValue("$success_criteria_json", JsonSerializer.Serialize(node.SuccessCriteria, TinadecJson.Options));
            command.Parameters.AddWithValue("$dependencies_json", JsonSerializer.Serialize(node.Dependencies, TinadecJson.Options));
            command.Parameters.AddWithValue("$required_capabilities_json", JsonSerializer.Serialize(node.RequiredCapabilities, TinadecJson.Options));
            command.Parameters.AddWithValue("$created_at", node.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$updated_at", node.UpdatedAt.ToString("O"));
        });
    }

    private static void InsertAgentAssignment(SqliteConnection connection, AgentAssignmentDto assignment)
    {
        Execute(connection, """
            insert into agent_assignments (
                id, run_id, task_node_id, agent_id, agent_name, agent_layer, agent_type,
                model_route_purpose, permission_mode, allowed_tools_json, status, created_at
            )
            values (
                $id, $run_id, $task_node_id, $agent_id, $agent_name, $agent_layer, $agent_type,
                $model_route_purpose, $permission_mode, $allowed_tools_json, $status, $created_at
            )
            """, command =>
        {
            command.Parameters.AddWithValue("$id", assignment.Id);
            command.Parameters.AddWithValue("$run_id", assignment.RunId);
            command.Parameters.AddWithValue("$task_node_id", assignment.TaskNodeId);
            command.Parameters.AddWithValue("$agent_id", assignment.AgentId);
            command.Parameters.AddWithValue("$agent_name", assignment.AgentName);
            command.Parameters.AddWithValue("$agent_layer", assignment.AgentLayer);
            command.Parameters.AddWithValue("$agent_type", assignment.AgentType);
            command.Parameters.AddWithValue("$model_route_purpose", assignment.ModelRoutePurpose);
            command.Parameters.AddWithValue("$permission_mode", assignment.PermissionMode);
            command.Parameters.AddWithValue("$allowed_tools_json", JsonSerializer.Serialize(assignment.AllowedTools, TinadecJson.Options));
            command.Parameters.AddWithValue("$status", assignment.Status);
            command.Parameters.AddWithValue("$created_at", assignment.CreatedAt.ToString("O"));
        });
    }

    private static void InsertStepResult(SqliteConnection connection, StepResultDto result)
    {
        Execute(connection, """
            insert into step_results (id, run_id, task_node_id, agent_id, status, summary, evidence_json, created_at)
            values ($id, $run_id, $task_node_id, $agent_id, $status, $summary, $evidence_json, $created_at)
            """, command =>
        {
            command.Parameters.AddWithValue("$id", result.Id);
            command.Parameters.AddWithValue("$run_id", result.RunId);
            command.Parameters.AddWithValue("$task_node_id", result.TaskNodeId);
            command.Parameters.AddWithValue("$agent_id", result.AgentId);
            command.Parameters.AddWithValue("$status", result.Status);
            command.Parameters.AddWithValue("$summary", result.Summary);
            command.Parameters.AddWithValue("$evidence_json", JsonSerializer.Serialize(result.Evidence, TinadecJson.Options));
            command.Parameters.AddWithValue("$created_at", result.CreatedAt.ToString("O"));
        });
    }

    private static void InsertContextPack(SqliteConnection connection, ContextPackDto pack)
    {
        Execute(connection, """
            insert into context_packs (
                id, run_id, session_id, created_by_agent_id, summary, token_budget,
                compression_ratio, evidence_map_json, created_at
            )
            values (
                $id, $run_id, $session_id, $created_by_agent_id, $summary, $token_budget,
                $compression_ratio, $evidence_map_json, $created_at
            )
            """, command =>
        {
            command.Parameters.AddWithValue("$id", pack.Id);
            command.Parameters.AddWithValue("$run_id", pack.RunId);
            command.Parameters.AddWithValue("$session_id", pack.SessionId);
            command.Parameters.AddWithValue("$created_by_agent_id", pack.CreatedByAgentId);
            command.Parameters.AddWithValue("$summary", pack.Summary);
            command.Parameters.AddWithValue("$token_budget", pack.TokenBudget);
            command.Parameters.AddWithValue("$compression_ratio", pack.CompressionRatio);
            command.Parameters.AddWithValue("$evidence_map_json", JsonSerializer.Serialize(pack.EvidenceMap, TinadecJson.Options));
            command.Parameters.AddWithValue("$created_at", pack.CreatedAt.ToString("O"));
        });
    }

    private static void InsertPromptFragment(SqliteConnection connection, PromptFragmentDto fragment, bool updateBuiltin)
    {
        var conflictUpdate = updateBuiltin
            ? """
                    title = excluded.title,
                    scope = excluded.scope,
                    target_agent_id = excluded.target_agent_id,
                    category = excluded.category,
                    content = excluded.content,
                    priority = excluded.priority,
                    enabled = excluded.enabled,
                    is_builtin = excluded.is_builtin,
                    updated_at = excluded.updated_at
                """
            : """
                    key = excluded.key,
                    title = excluded.title,
                    scope = excluded.scope,
                    target_agent_id = excluded.target_agent_id,
                    category = excluded.category,
                    content = excluded.content,
                    priority = excluded.priority,
                    enabled = excluded.enabled,
                    updated_at = excluded.updated_at
                """;

        Execute(connection, $$"""
            insert into prompt_fragments (
                id, key, title, scope, target_agent_id, category, content, priority,
                enabled, is_builtin, created_at, updated_at
            )
            values (
                $id, $key, $title, $scope, $target_agent_id, $category, $content, $priority,
                $enabled, $is_builtin, $created_at, $updated_at
            )
            on conflict(id) do update set
            {{conflictUpdate}}
            """, command =>
        {
            command.Parameters.AddWithValue("$id", fragment.Id);
            command.Parameters.AddWithValue("$key", fragment.Key);
            command.Parameters.AddWithValue("$title", fragment.Title);
            command.Parameters.AddWithValue("$scope", fragment.Scope);
            command.Parameters.AddWithValue("$target_agent_id", (object?)fragment.TargetAgentId ?? DBNull.Value);
            command.Parameters.AddWithValue("$category", fragment.Category);
            command.Parameters.AddWithValue("$content", fragment.Content);
            command.Parameters.AddWithValue("$priority", fragment.Priority);
            command.Parameters.AddWithValue("$enabled", fragment.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$is_builtin", fragment.IsBuiltIn ? 1 : 0);
            command.Parameters.AddWithValue("$created_at", fragment.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$updated_at", fragment.UpdatedAt.ToString("O"));
        });
    }

    private static void UpsertAgentSystemPromptFragment(
        SqliteConnection connection,
        string agentId,
        string? systemPrompt,
        DateTimeOffset now)
    {
        var fragmentId = $"prompt_agent_override_{NormalizePromptKey(agentId, "agent")}";
        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            Execute(connection, "delete from prompt_fragments where id = $id and is_builtin = 0", command =>
            {
                command.Parameters.AddWithValue("$id", fragmentId);
            });
            return;
        }

        InsertPromptFragment(connection, new PromptFragmentDto(
            fragmentId,
            $"agent.override.{NormalizePromptKey(agentId, "agent")}",
            "Agent system prompt override",
            "agent",
            agentId,
            "override",
            systemPrompt.Trim(),
            1000,
            true,
            false,
            now,
            now), updateBuiltin: false);
    }

    private static void InsertSupervisionFinding(SqliteConnection connection, SupervisionFindingDto finding)
    {
        Execute(connection, """
            insert into supervision_findings (
                id, run_id, session_id, severity, category, summary, recommendation, status, created_at
            )
            values (
                $id, $run_id, $session_id, $severity, $category, $summary, $recommendation, $status, $created_at
            )
            """, command =>
        {
            command.Parameters.AddWithValue("$id", finding.Id);
            command.Parameters.AddWithValue("$run_id", finding.RunId);
            command.Parameters.AddWithValue("$session_id", finding.SessionId);
            command.Parameters.AddWithValue("$severity", finding.Severity);
            command.Parameters.AddWithValue("$category", finding.Category);
            command.Parameters.AddWithValue("$summary", finding.Summary);
            command.Parameters.AddWithValue("$recommendation", finding.Recommendation);
            command.Parameters.AddWithValue("$status", finding.Status);
            command.Parameters.AddWithValue("$created_at", finding.CreatedAt.ToString("O"));
        });
    }

    private void SeedBuiltinExtensions(SqliteConnection connection)
    {
        var now = DateTimeOffset.UtcNow;
        var source = ExtensionCatalog.BuiltinSource(now);
        Execute(connection, """
            insert into extension_sources (id, name, kind, location, enabled, last_refreshed_at, created_at)
            values ($id, $name, $kind, $location, $enabled, $last_refreshed_at, $created_at)
            on conflict(id) do update set
                name = excluded.name,
                kind = excluded.kind,
                location = excluded.location,
                enabled = excluded.enabled,
                last_refreshed_at = excluded.last_refreshed_at
            """, command =>
        {
            command.Parameters.AddWithValue("$id", source.Id);
            command.Parameters.AddWithValue("$name", source.Name);
            command.Parameters.AddWithValue("$kind", source.Kind);
            command.Parameters.AddWithValue("$location", source.Location);
            command.Parameters.AddWithValue("$enabled", source.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$last_refreshed_at", source.LastRefreshedAt?.ToString("O") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$created_at", source.CreatedAt.ToString("O"));
        });

        foreach (var descriptor in ExtensionCatalog.BuiltinDescriptors)
        {
            var catalogId = $"catalog_{descriptor.ExtensionId}";
            Execute(connection, """
                insert into extension_catalog_cache (
                    catalog_id,
                    source_id,
                    extension_id,
                    kind,
                    version,
                    publisher,
                    display_name,
                    description,
                    source_kind,
                    source_location,
                    capabilities_json,
                    permissions_json,
                    manifest_json,
                    updated_at
                )
                values (
                    $catalog_id,
                    $source_id,
                    $extension_id,
                    $kind,
                    $version,
                    $publisher,
                    $display_name,
                    $description,
                    $source_kind,
                    $source_location,
                    $capabilities_json,
                    $permissions_json,
                    $manifest_json,
                    $updated_at
                )
                on conflict(catalog_id) do update set
                    source_id = excluded.source_id,
                    extension_id = excluded.extension_id,
                    kind = excluded.kind,
                    version = excluded.version,
                    publisher = excluded.publisher,
                    display_name = excluded.display_name,
                    description = excluded.description,
                    source_kind = excluded.source_kind,
                    source_location = excluded.source_location,
                    capabilities_json = excluded.capabilities_json,
                    permissions_json = excluded.permissions_json,
                    manifest_json = excluded.manifest_json,
                    updated_at = excluded.updated_at
                """, command =>
            {
                command.Parameters.AddWithValue("$catalog_id", catalogId);
                command.Parameters.AddWithValue("$source_id", ExtensionCatalog.BuiltinSourceId);
                command.Parameters.AddWithValue("$extension_id", descriptor.ExtensionId);
                command.Parameters.AddWithValue("$kind", descriptor.Kind);
                command.Parameters.AddWithValue("$version", descriptor.Version);
                command.Parameters.AddWithValue("$publisher", descriptor.Publisher);
                command.Parameters.AddWithValue("$display_name", descriptor.DisplayName);
                command.Parameters.AddWithValue("$description", descriptor.Description);
                command.Parameters.AddWithValue("$source_kind", descriptor.SourceKind);
                command.Parameters.AddWithValue("$source_location", descriptor.SourceLocation);
                command.Parameters.AddWithValue("$capabilities_json", JsonSerializer.Serialize(descriptor.Capabilities, TinadecJson.Options));
                command.Parameters.AddWithValue("$permissions_json", JsonSerializer.Serialize(descriptor.Permissions, TinadecJson.Options));
                command.Parameters.AddWithValue("$manifest_json", descriptor.ManifestJson);
                command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            });
        }
    }

    private void SeedBuiltinAgents(SqliteConnection connection)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var profile in AgentCatalog.Profiles)
        {
            Execute(connection, """
                insert into agent_profiles (
                    id,
                    name,
                    layer,
                    agent_type,
                    mode,
                    description,
                    model_route_purpose,
                    allowed_tools_json,
                    capabilities_json,
                    system_prompt,
                    enabled,
                    is_builtin,
                    updated_at
                )
                values (
                    $id,
                    $name,
                    $layer,
                    $agent_type,
                    $mode,
                    $description,
                    $model_route_purpose,
                    $allowed_tools_json,
                    $capabilities_json,
                    $system_prompt,
                    1,
                    1,
                    $updated_at
                )
                on conflict(id) do update set
                    name = excluded.name,
                    layer = excluded.layer,
                    agent_type = excluded.agent_type,
                    mode = excluded.mode,
                    description = excluded.description,
                    model_route_purpose = excluded.model_route_purpose,
                    allowed_tools_json = excluded.allowed_tools_json,
                    capabilities_json = excluded.capabilities_json,
                    system_prompt = excluded.system_prompt,
                    is_builtin = 1,
                    updated_at = excluded.updated_at
                """, command =>
            {
                command.Parameters.AddWithValue("$id", profile.Id);
                command.Parameters.AddWithValue("$name", profile.Name);
                command.Parameters.AddWithValue("$layer", profile.Layer);
                command.Parameters.AddWithValue("$agent_type", profile.AgentType);
                command.Parameters.AddWithValue("$mode", profile.Mode);
                command.Parameters.AddWithValue("$description", profile.Description);
                command.Parameters.AddWithValue("$model_route_purpose", profile.ModelRoutePurpose);
                command.Parameters.AddWithValue("$allowed_tools_json", JsonSerializer.Serialize(profile.AllowedTools, TinadecJson.Options));
                command.Parameters.AddWithValue("$capabilities_json", JsonSerializer.Serialize(profile.Capabilities, TinadecJson.Options));
                command.Parameters.AddWithValue("$system_prompt", (object?)profile.SystemPrompt ?? DBNull.Value);
                command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            });

            Execute(connection, """
                insert into model_routes (purpose, provider_instance_id, model, updated_at)
                select $purpose, 'openai_default', model, $updated_at
                from model_provider_instances
                where id = 'openai_default'
                on conflict(purpose) do nothing
                """, command =>
            {
                command.Parameters.AddWithValue("$purpose", NormalizeRoutePurpose(profile.ModelRoutePurpose));
                command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            });
        }

        foreach (var candidate in AgentCatalog.Candidates)
        {
            Execute(connection, """
                insert into agent_candidates (
                    id,
                    generated_by_agent_id,
                    name,
                    layer,
                    agent_type,
                    description,
                    suggested_tools_json,
                    evaluation_notes_json,
                    status,
                    created_at
                )
                values (
                    $id,
                    $generated_by_agent_id,
                    $name,
                    $layer,
                    $agent_type,
                    $description,
                    $suggested_tools_json,
                    $evaluation_notes_json,
                    'proposed',
                    $created_at
                )
                on conflict(id) do update set
                    generated_by_agent_id = excluded.generated_by_agent_id,
                    name = excluded.name,
                    layer = excluded.layer,
                    agent_type = excluded.agent_type,
                    description = excluded.description,
                    suggested_tools_json = excluded.suggested_tools_json,
                    evaluation_notes_json = excluded.evaluation_notes_json
                """, command =>
            {
                command.Parameters.AddWithValue("$id", candidate.Id);
                command.Parameters.AddWithValue("$generated_by_agent_id", candidate.GeneratedByAgentId);
                command.Parameters.AddWithValue("$name", candidate.Name);
                command.Parameters.AddWithValue("$layer", candidate.Layer);
                command.Parameters.AddWithValue("$agent_type", candidate.AgentType);
                command.Parameters.AddWithValue("$description", candidate.Description);
                command.Parameters.AddWithValue("$suggested_tools_json", JsonSerializer.Serialize(candidate.SuggestedTools, TinadecJson.Options));
                command.Parameters.AddWithValue("$evaluation_notes_json", JsonSerializer.Serialize(candidate.EvaluationNotes, TinadecJson.Options));
                command.Parameters.AddWithValue("$created_at", now.ToString("O"));
            });
        }
    }

    private void SeedBuiltinPromptFragments(SqliteConnection connection)
    {
        var now = DateTimeOffset.UtcNow;
        var fragments = new[]
        {
            new PromptFragmentDto(
                "prompt_builtin_meeting_default",
                "builtin.meeting.default",
                "Meeting Agent Default",
                "agent",
                "agent_meeting",
                "identity",
                "You are TinadecCode's Meeting Agent. Interpret the user's goal, keep success criteria explicit, create auditable task graphs, and route concrete work through Core-governed planning and execution agents.",
                900,
                true,
                true,
                now,
                now),
            new PromptFragmentDto(
                "prompt_builtin_tool_approval_boundaries",
                "builtin.tool.approval-boundaries",
                "Tool And Approval Boundaries",
                "global",
                null,
                "policy",
                "Core owns state, model routes, approvals, permissions, events, and tool policy. Read-only evidence gathering can be automatic. Workspace writes, shell, Git, network, MCP, ACP, and other risky actions must remain approval-gated.",
                800,
                true,
                true,
                now,
                now),
            new PromptFragmentDto(
                "prompt_builtin_agent_mode",
                "builtin.agent.mode",
                "Agent Mode Guidance",
                "global",
                null,
                "mode",
                "Respect the active agent mode. Plan-first favors explicit task graphs before execution. Balanced favors concise plans plus bounded execution. Safe research favors read-only exploration. Parallel favors independent execution lanes when dependencies and budget allow it.",
                700,
                true,
                true,
                now,
                now),
            new PromptFragmentDto(
                "prompt_builtin_context_pack_rules",
                "builtin.context-pack.rules",
                "Context Pack Rules",
                "global",
                null,
                "context",
                "Use context packs as reversible summaries, not as hidden authority. Preserve evidence ids, call out missing context, and keep long-session compression visible to the user.",
                600,
                true,
                true,
                now,
                now)
        };

        foreach (var fragment in fragments)
        {
            InsertPromptFragment(connection, fragment, updateBuiltin: true);
        }
    }

    private static void NormalizeLegacyAgentSeeds(SqliteConnection connection)
    {
        Execute(connection, """
            update agent_profiles
            set id = 'agent_evolution_algorithm',
                name = 'Evolution Algorithm Agent',
                agent_type = 'evolution-algorithm',
                model_route_purpose = 'evolution',
                description = 'Observes repeated workflow patterns and proposes candidate skills, MCP manifests, prompts, or agent specs without hot-path publishing.'
            where id = 'agent_purifier'
              and not exists (select 1 from agent_profiles where id = 'agent_evolution_algorithm')
            """);

        Execute(connection, """
            delete from agent_profiles
            where id = 'agent_purifier'
              and exists (select 1 from agent_profiles where id = 'agent_evolution_algorithm')
            """);

        Execute(connection, """
            update agent_candidates
            set id = 'cand_evolution_review_agent',
                generated_by_agent_id = 'agent_evolution_algorithm',
                name = 'Evolved Review Agent'
            where id = 'cand_purified_review_agent'
              and not exists (select 1 from agent_candidates where id = 'cand_evolution_review_agent')
            """);

        Execute(connection, """
            delete from agent_candidates
            where id = 'cand_purified_review_agent'
              and exists (select 1 from agent_candidates where id = 'cand_evolution_review_agent')
            """);

        Execute(connection, """
            update agent_candidates
            set generated_by_agent_id = 'agent_evolution_algorithm'
            where generated_by_agent_id = 'agent_purifier'
            """);
    }

    private InstalledExtensionDto SaveInstalledExtension(string? catalogId, ExtensionDescriptor descriptor, bool enabled, string status, string statusMessage)
    {
        var now = DateTimeOffset.UtcNow;
        var id = $"ext_{ExtensionCatalog.NormalizeId(descriptor.ExtensionId)}";
        lock (_gate)
        {
            using var connection = OpenConnection();
            Execute(connection, """
                insert into installed_extensions (
                    id,
                    catalog_id,
                    extension_id,
                    kind,
                    version,
                    publisher,
                    display_name,
                    description,
                    source_kind,
                    source_location,
                    capabilities_json,
                    permissions_json,
                    manifest_json,
                    config_json,
                    enabled,
                    status,
                    status_message,
                    installed_at,
                    updated_at
                )
                values (
                    $id,
                    $catalog_id,
                    $extension_id,
                    $kind,
                    $version,
                    $publisher,
                    $display_name,
                    $description,
                    $source_kind,
                    $source_location,
                    $capabilities_json,
                    $permissions_json,
                    $manifest_json,
                    null,
                    $enabled,
                    $status,
                    $status_message,
                    $installed_at,
                    $updated_at
                )
                on conflict(id) do update set
                    catalog_id = excluded.catalog_id,
                    version = excluded.version,
                    publisher = excluded.publisher,
                    display_name = excluded.display_name,
                    description = excluded.description,
                    source_kind = excluded.source_kind,
                    source_location = excluded.source_location,
                    capabilities_json = excluded.capabilities_json,
                    permissions_json = excluded.permissions_json,
                    manifest_json = excluded.manifest_json,
                    enabled = excluded.enabled,
                    status = excluded.status,
                    status_message = excluded.status_message,
                    updated_at = excluded.updated_at
                """, command =>
            {
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$catalog_id", (object?)catalogId ?? DBNull.Value);
                command.Parameters.AddWithValue("$extension_id", descriptor.ExtensionId);
                command.Parameters.AddWithValue("$kind", descriptor.Kind);
                command.Parameters.AddWithValue("$version", descriptor.Version);
                command.Parameters.AddWithValue("$publisher", descriptor.Publisher);
                command.Parameters.AddWithValue("$display_name", descriptor.DisplayName);
                command.Parameters.AddWithValue("$description", descriptor.Description);
                command.Parameters.AddWithValue("$source_kind", descriptor.SourceKind);
                command.Parameters.AddWithValue("$source_location", descriptor.SourceLocation);
                command.Parameters.AddWithValue("$capabilities_json", JsonSerializer.Serialize(descriptor.Capabilities, TinadecJson.Options));
                command.Parameters.AddWithValue("$permissions_json", JsonSerializer.Serialize(descriptor.Permissions, TinadecJson.Options));
                command.Parameters.AddWithValue("$manifest_json", descriptor.ManifestJson);
                command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
                command.Parameters.AddWithValue("$status", status);
                command.Parameters.AddWithValue("$status_message", statusMessage);
                command.Parameters.AddWithValue("$installed_at", now.ToString("O"));
                command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            });
        }

        return GetInstalledExtension(id)
            ?? throw new InvalidOperationException("Installed extension was not found.");
    }

    private InstalledExtensionDto? GetInstalledExtension(string installedExtensionId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select id, catalog_id, extension_id, kind, version, publisher, display_name, description, source_kind,
                   source_location, capabilities_json, permissions_json, enabled, status, status_message, installed_at, updated_at
            from installed_extensions
            where id = $id
            """;
        command.Parameters.AddWithValue("$id", installedExtensionId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadInstalledExtension(reader) : null;
    }

    private string? GetApprovalStatus(string approvalId) => GetApproval(approvalId)?.Status;

    private static ApprovalDto? GetApproval(SqliteConnection connection, string approvalId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "select id, session_id, kind, summary, command, cwd, status, created_at, decided_at from approvals where id = $id";
        command.Parameters.AddWithValue("$id", approvalId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadApproval(reader) : null;
    }

    private static void ValidateExtensionDescriptor(ExtensionDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.ExtensionId))
        {
            throw new InvalidOperationException("Extension id is required.");
        }

        if (descriptor.ExtensionId.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_'))
        {
            throw new InvalidOperationException("Extension id may only contain ASCII letters, numbers, '-' and '_'.");
        }

        if (descriptor.SourceLocation.Contains("..", StringComparison.Ordinal) ||
            descriptor.SourceLocation.Contains('\r', StringComparison.Ordinal) ||
            descriptor.SourceLocation.Contains('\n', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Extension source location is not safe.");
        }
    }

    private static void UpsertRuntimeCache(SqliteConnection connection, InstalledExtensionDto extension, DateTimeOffset now)
    {
        if (extension.Kind == "mcp-server")
        {
            var serverId = $"mcp_{extension.Id}";
            Execute(connection, """
                insert into mcp_servers (id, extension_id, name, transport, status, tools_json, updated_at)
                values ($id, $extension_id, $name, $transport, 'pending_connect', $tools_json, $updated_at)
                on conflict(id) do update set
                    name = excluded.name,
                    transport = excluded.transport,
                    status = excluded.status,
                    tools_json = excluded.tools_json,
                    updated_at = excluded.updated_at
                """, command =>
            {
                command.Parameters.AddWithValue("$id", serverId);
                command.Parameters.AddWithValue("$extension_id", extension.Id);
                command.Parameters.AddWithValue("$name", extension.DisplayName);
                command.Parameters.AddWithValue("$transport", extension.Capabilities.Contains("stdio") ? "stdio" : "http");
                command.Parameters.AddWithValue("$tools_json", JsonSerializer.Serialize(Array.Empty<string>(), TinadecJson.Options));
                command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            });
        }

        if (extension.Kind == "acp-adapter")
        {
            var adapterId = $"acp_{extension.Id}";
            Execute(connection, """
                insert into acp_adapters (id, extension_id, name, command, status, status_message, capabilities_json, updated_at)
                values ($id, $extension_id, $name, $command, 'pending_connect', $status_message, $capabilities_json, $updated_at)
                on conflict(id) do update set
                    name = excluded.name,
                    command = excluded.command,
                    status = excluded.status,
                    status_message = excluded.status_message,
                    capabilities_json = excluded.capabilities_json,
                    updated_at = excluded.updated_at
                """, command =>
            {
                command.Parameters.AddWithValue("$id", adapterId);
                command.Parameters.AddWithValue("$extension_id", extension.Id);
                command.Parameters.AddWithValue("$name", extension.DisplayName);
                command.Parameters.AddWithValue("$command", "agent acp");
                command.Parameters.AddWithValue("$status_message", "Adapter metadata is enabled. Awaiting Gateway connect.");
                command.Parameters.AddWithValue("$capabilities_json", JsonSerializer.Serialize(extension.Capabilities, TinadecJson.Options));
                command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            });
        }
    }

    private static void RemoveRuntimeCache(SqliteConnection connection, string installedExtensionId)
    {
        Execute(connection, "delete from mcp_servers where extension_id = $extension_id", command => command.Parameters.AddWithValue("$extension_id", installedExtensionId));
        Execute(connection, "delete from mcp_capabilities_cache where server_id = $server_id", command => command.Parameters.AddWithValue("$server_id", $"mcp_{installedExtensionId}"));
        Execute(connection, "delete from acp_adapters where extension_id = $extension_id", command => command.Parameters.AddWithValue("$extension_id", installedExtensionId));
    }

    public static string ResolveDatabasePath(IConfiguration configuration)
    {
        var configured = configuration["Tinadec:DatabasePath"] ?? Environment.GetEnvironmentVariable("TINADEC_DB");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = AppContext.BaseDirectory;
        }

        return Path.Combine(root, "TinadecCode", "tinadec.db");
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static long NextSeq(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "select coalesce(max(seq), 0) + 1 from events";
        return (long)command.ExecuteScalar()!;
    }

    private static void Execute(SqliteConnection connection, string sql, Action<SqliteCommand>? configure = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure?.Invoke(command);
        command.ExecuteNonQuery();
    }

    private static void AddColumnIfMissing(SqliteConnection connection, string table, string column, string definition)
    {
        try
        {
            Execute(connection, $"alter table {table} add column {column} {definition}");
        }
        catch (SqliteException)
        {
        }
    }

    private static DateTimeOffset ParseTime(string value)
    {
        return DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);
    }

    private static SessionDto ReadSession(SqliteDataReader reader)
    {
        return new SessionDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            ParseTime(reader.GetString(4)),
            ParseTime(reader.GetString(5)));
    }

    private static ApprovalDto ReadApproval(SqliteDataReader reader)
    {
        return new ApprovalDto(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            ParseTime(reader.GetString(7)),
            reader.IsDBNull(8) ? null : ParseTime(reader.GetString(8)));
    }

    private static EventEnvelope ReadEvent(SqliteDataReader reader)
    {
        var capabilities = JsonSerializer.Deserialize<string[]>(reader.GetString(7), TinadecJson.Options) ?? [];
        var payload = reader.IsDBNull(8) ? null : JsonNode.Parse(reader.GetString(8)) as JsonObject;
        var error = reader.IsDBNull(9)
            ? null
            : JsonSerializer.Deserialize<TinadecError>(reader.GetString(9), TinadecJson.Options);

        return new EventEnvelope(
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(0),
            ParseTime(reader.GetString(6)),
            capabilities,
            payload,
            error);
    }

    private static StoredModelProviderInstance ReadModelProvider(SqliteDataReader reader)
    {
        var capabilities = JsonSerializer.Deserialize<string[]>(reader.GetString(11), TinadecJson.Options) ?? [];
        return new StoredModelProviderInstance(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            capabilities,
            reader.GetInt32(12) == 1,
            ParseProviderHealthStatus(reader.GetString(13)),
            reader.IsDBNull(14) ? null : ParseTime(reader.GetString(14)),
            reader.GetInt32(15),
            reader.IsDBNull(16) ? null : ParseTime(reader.GetString(16)),
            reader.IsDBNull(17) ? null : ParseProviderErrorCategory(reader.GetString(17)),
            ParseTime(reader.GetString(18)),
            ParseTime(reader.GetString(19)));
    }

    private static ModelRouteDto ReadModelRoute(SqliteDataReader reader)
    {
        return new ModelRouteDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            ParseTime(reader.GetString(3)));
    }

    private static ExtensionSourceDto ReadExtensionSource(SqliteDataReader reader)
    {
        return new ExtensionSourceDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4) == 1,
            reader.IsDBNull(5) ? null : ParseTime(reader.GetString(5)),
            ParseTime(reader.GetString(6)));
    }

    private static MarketCatalogItemDto ReadMarketCatalogItem(SqliteDataReader reader)
    {
        var capabilities = JsonSerializer.Deserialize<string[]>(reader.GetString(10), TinadecJson.Options) ?? [];
        var permissions = JsonSerializer.Deserialize<string[]>(reader.GetString(11), TinadecJson.Options) ?? [];
        var installedId = reader.IsDBNull(12) ? null : reader.GetString(12);
        return new MarketCatalogItemDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            capabilities,
            permissions,
            installedId is null ? "available" : "installed",
            installedId);
    }

    private static InstalledExtensionDto ReadInstalledExtension(SqliteDataReader reader)
    {
        var capabilities = JsonSerializer.Deserialize<string[]>(reader.GetString(10), TinadecJson.Options) ?? [];
        var permissions = JsonSerializer.Deserialize<string[]>(reader.GetString(11), TinadecJson.Options) ?? [];
        return new InstalledExtensionDto(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            capabilities,
            permissions,
            reader.GetInt32(12) == 1,
            reader.GetString(13),
            reader.GetString(14),
            ParseTime(reader.GetString(15)),
            ParseTime(reader.GetString(16)));
    }

    private static McpServerDto ReadMcpServer(SqliteDataReader reader)
    {
        var tools = JsonSerializer.Deserialize<string[]>(reader.GetString(5), TinadecJson.Options) ?? [];
        var manifestJson = reader.IsDBNull(7) ? "{}" : reader.GetString(7);
        return new McpServerDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            tools,
            manifestJson,
            ParseTime(reader.GetString(6)));
    }

    private static AcpAdapterDto ReadAcpAdapter(SqliteDataReader reader)
    {
        var capabilities = JsonSerializer.Deserialize<string[]>(reader.GetString(6), TinadecJson.Options) ?? [];
        return new AcpAdapterDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            capabilities,
            ParseTime(reader.GetString(7)));
    }

    private static AgentProfileDto ReadAgentProfile(SqliteDataReader reader)
    {
        var allowedTools = JsonSerializer.Deserialize<string[]>(reader.GetString(7), TinadecJson.Options) ?? [];
        var capabilities = JsonSerializer.Deserialize<string[]>(reader.GetString(8), TinadecJson.Options) ?? [];
        var systemPrompt = reader.IsDBNull(9) ? null : reader.GetString(9);
        return new AgentProfileDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            allowedTools,
            capabilities,
            systemPrompt,
            reader.GetInt32(10) == 1,
            reader.GetInt32(11) == 1,
            ParseTime(reader.GetString(12)));
    }

    private static AgentCandidateDto ReadAgentCandidate(SqliteDataReader reader)
    {
        var suggestedTools = JsonSerializer.Deserialize<string[]>(reader.GetString(6), TinadecJson.Options) ?? [];
        var evaluationNotes = JsonSerializer.Deserialize<string[]>(reader.GetString(7), TinadecJson.Options) ?? [];
        return new AgentCandidateDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            suggestedTools,
            evaluationNotes,
            reader.GetString(8),
            ParseTime(reader.GetString(9)));
    }

    private static PromptFragmentDto ReadPromptFragment(SqliteDataReader reader)
    {
        return new PromptFragmentDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt32(7),
            reader.GetInt32(8) == 1,
            reader.GetInt32(9) == 1,
            ParseTime(reader.GetString(10)),
            ParseTime(reader.GetString(11)));
    }

    private static OrchestrationRunDto ReadOrchestrationRun(SqliteDataReader reader)
    {
        return new OrchestrationRunDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            ParseTime(reader.GetString(5)),
            ParseTime(reader.GetString(6)));
    }

    private static TaskGraphDto ReadTaskGraph(SqliteDataReader reader)
    {
        return new TaskGraphDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            ParseTime(reader.GetString(5)),
            ParseTime(reader.GetString(6)));
    }

    private static TaskNodeDto ReadTaskNode(SqliteDataReader reader)
    {
        return new TaskNodeDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt32(7),
            reader.GetString(8),
            ReadStringArrayJson(reader.GetString(9)),
            ReadStringArrayJson(reader.GetString(10)),
            ReadStringArrayJson(reader.GetString(11)),
            ParseTime(reader.GetString(12)),
            ParseTime(reader.GetString(13)));
    }

    private static AgentAssignmentDto ReadAgentAssignment(SqliteDataReader reader)
    {
        return new AgentAssignmentDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            ReadStringArrayJson(reader.GetString(9)),
            reader.GetString(10),
            ParseTime(reader.GetString(11)));
    }

    private static StepResultDto ReadStepResult(SqliteDataReader reader)
    {
        return new StepResultDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            ReadStringArrayJson(reader.GetString(6)),
            ParseTime(reader.GetString(7)));
    }

    private static ContextPackDto ReadContextPack(SqliteDataReader reader)
    {
        return new ContextPackDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetDouble(6),
            ReadStringArrayJson(reader.GetString(7)),
            ParseTime(reader.GetString(8)));
    }

    private static SupervisionFindingDto ReadSupervisionFinding(SqliteDataReader reader)
    {
        return new SupervisionFindingDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            ParseTime(reader.GetString(8)));
    }

    private static IReadOnlyList<string> ReadStringArrayJson(string value)
    {
        return JsonSerializer.Deserialize<string[]>(value, TinadecJson.Options) ?? [];
    }

    private static string NormalizeProviderInstanceId(string value)
    {
        var normalized = new string(value.Trim().ToLowerInvariant().Select(ch =>
            char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_').ToArray());
        normalized = normalized.Trim('_', '-');
        return string.IsNullOrWhiteSpace(normalized) ? $"provider_{Guid.NewGuid():N}" : normalized;
    }

    private static string NormalizeRoutePurpose(string value)
    {
        return NormalizePlain(value, "chat").ToLowerInvariant();
    }

    private static string NormalizePromptScope(string? value)
    {
        var normalized = NormalizePlain(value, "global").ToLowerInvariant();
        return normalized is "global" or "agent" or "mode" or "session" or "project" ? normalized : "global";
    }

    private static string NormalizePromptKey(string? value, string fallback)
    {
        var normalized = new string(NormalizePlain(value, fallback)
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '.')
            .ToArray());
        while (normalized.Contains("..", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("..", ".", StringComparison.Ordinal);
        }

        normalized = normalized.Trim('.', '_', '-');
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string NormalizePlain(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? NormalizeOptionalUrl(string? value)
    {
        var normalized = NormalizeOptional(value);
        return normalized?.TrimEnd('/');
    }

    private static string NormalizeExtensionSourceKind(string? value)
    {
        var normalized = ExtensionCatalog.NormalizeId(value);
        return normalized is "local-directory" or "local-archive" or "github" or "git" or "https-archive" or "marketplace-url" or "mcpb" or "dxt" or "builtin"
            ? normalized
            : "local-directory";
    }

    private static string NormalizeConnectionKind(string? value, string driver)
    {
        var normalized = NormalizePlain(value, InferConnectionKind(driver)).ToLowerInvariant();
        return normalized is "api-key" or "cli" or "local-server" or "http" or "public-api" ? normalized : InferConnectionKind(driver);
    }

    private static string InferConnectionKind(string driver)
    {
        var normalized = driver.ToLowerInvariant();
        if (normalized.Contains("cli", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("cursor", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("opencode", StringComparison.OrdinalIgnoreCase))
        {
            return "cli";
        }

        if (ProviderTemplateRules.IsLocalOpenAiCompatibleDriver(normalized))
        {
            return "local-server";
        }

        if (normalized is "pollinations")
        {
            return "public-api";
        }

        return "api-key";
    }

    private static string[] InferCapabilities(string driver, string connectionKind)
    {
        if (connectionKind.Equals("cli", StringComparison.OrdinalIgnoreCase))
        {
            return driver.Contains("cursor", StringComparison.OrdinalIgnoreCase)
                ? ["agent", "cli", "acp"]
                : ["agent", "cli", "workspace"];
        }

        if (connectionKind.Equals("local-server", StringComparison.OrdinalIgnoreCase)
            || connectionKind.Equals("public-api", StringComparison.OrdinalIgnoreCase))
        {
            return connectionKind.Equals("local-server", StringComparison.OrdinalIgnoreCase)
                ? ["chat", "local", "no-api-key"]
                : ["chat", "streaming", "public-api", "no-api-key"];
        }

        return ["chat", "streaming", "tool-calls"];
    }

    private static ProviderHealthState ResolveProviderHealth(
        IReadOnlyList<string> capabilities,
        StoredModelProviderInstance? existing)
    {
        var health = ResolveCapabilityValue(capabilities, "health") is { } healthValue
            ? ParseProviderHealthStatus(healthValue)
            : existing?.HealthStatus ?? ProviderHealthStatus.Healthy;
        var cooldownUntil = ResolveCapabilityValue(capabilities, "cooldown_until") is { } cooldownValue
            ? ParseTime(cooldownValue)
            : existing?.CooldownUntil;
        var failureCount = ResolveCapabilityValue(capabilities, "failure_count") is { } failureValue && int.TryParse(failureValue, out var parsedFailureCount)
            ? parsedFailureCount
            : existing?.FailureCount ?? 0;
        var lastFailureAt = ResolveCapabilityValue(capabilities, "cooldown_started_at") is { } failureAtValue
            ? ParseTime(failureAtValue)
            : existing?.LastFailureAt;
        var lastErrorCategory = ResolveCapabilityValue(capabilities, "last_error") is { } errorValue
            ? ParseProviderErrorCategory(errorValue)
            : existing?.LastErrorCategory;

        return new ProviderHealthState(health, cooldownUntil, failureCount, lastFailureAt, lastErrorCategory);
    }

    private static ProviderHealthStatus ParseProviderHealthStatus(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "healthy" => ProviderHealthStatus.Healthy,
            "unhealthy" => ProviderHealthStatus.Unhealthy,
            "disabled" => ProviderHealthStatus.Disabled,
            "cooldown" => ProviderHealthStatus.Cooldown,
            _ => ProviderHealthStatus.Unknown
        };
    }

    private static string ToProviderHealthStatusStorageValue(ProviderHealthStatus status)
    {
        return status switch
        {
            ProviderHealthStatus.Healthy => "healthy",
            ProviderHealthStatus.Unhealthy => "unhealthy",
            ProviderHealthStatus.Disabled => "disabled",
            ProviderHealthStatus.Cooldown => "cooldown",
            _ => "unknown"
        };
    }

    private static ProviderErrorCategory ParseProviderErrorCategory(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "authentication_failed" or "authenticationfailed" => ProviderErrorCategory.AuthenticationFailed,
            "rate_limited" or "ratelimited" => ProviderErrorCategory.RateLimited,
            "timeout" => ProviderErrorCategory.Timeout,
            "provider_unavailable" or "providerunavailable" => ProviderErrorCategory.ProviderUnavailable,
            "invalid_request" or "invalidrequest" => ProviderErrorCategory.InvalidRequest,
            "cancelled" => ProviderErrorCategory.Cancelled,
            _ => ProviderErrorCategory.Unknown
        };
    }

    private static string ToProviderErrorCategoryStorageValue(ProviderErrorCategory category)
    {
        return category switch
        {
            ProviderErrorCategory.AuthenticationFailed => "authentication_failed",
            ProviderErrorCategory.RateLimited => "rate_limited",
            ProviderErrorCategory.Timeout => "timeout",
            ProviderErrorCategory.ProviderUnavailable => "provider_unavailable",
            ProviderErrorCategory.InvalidRequest => "invalid_request",
            ProviderErrorCategory.Cancelled => "cancelled",
            _ => "unknown"
        };
    }

    private static string? ResolveCapabilityValue(IReadOnlyList<string> capabilities, string key)
    {
        var prefix = key + ":";
        return capabilities.FirstOrDefault(capability => capability.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
    }

    private static bool HasCapabilityKey(string capability, string key)
    {
        return capability.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase);
    }

    private static int ResolveCapabilityInt(IReadOnlyList<string> capabilities, string key)
    {
        var prefix = key + ":";
        var value = capabilities.FirstOrDefault(capability => capability.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
        return int.TryParse(value, out var parsed) ? parsed : 0;
    }

    private sealed record ProviderHealthState(
        ProviderHealthStatus HealthStatus,
        DateTimeOffset? CooldownUntil,
        int FailureCount,
        DateTimeOffset? LastFailureAt,
        ProviderErrorCategory? LastErrorCategory);

    // === Agent Evolution Support ===

    public void AddAgentCandidate(AgentCandidateSeed seed)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            insert into agent_candidates (id, generated_by_agent_id, name, layer, agent_type, description,
                                          suggested_tools_json, evaluation_notes_json, status, created_at)
            values ($id, $generated_by_agent_id, $name, $layer, $agent_type, $description,
                    $suggested_tools_json, $evaluation_notes_json, $status, $created_at)
            """;
        command.Parameters.AddWithValue("$id", seed.Id);
        command.Parameters.AddWithValue("$generated_by_agent_id", seed.GeneratedByAgentId);
        command.Parameters.AddWithValue("$name", seed.Name);
        command.Parameters.AddWithValue("$layer", seed.Layer);
        command.Parameters.AddWithValue("$agent_type", seed.AgentType);
        command.Parameters.AddWithValue("$description", seed.Description);
        command.Parameters.AddWithValue("$suggested_tools_json", JsonSerializer.Serialize(seed.SuggestedTools));
        command.Parameters.AddWithValue("$evaluation_notes_json", JsonSerializer.Serialize(seed.EvaluationNotes));
        command.Parameters.AddWithValue("$status", "proposed");
        command.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public bool UpdateCandidateStatus(string candidateId, string status)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "update agent_candidates set status = $status where id = $id";
        command.Parameters.AddWithValue("$id", candidateId);
        command.Parameters.AddWithValue("$status", status);
        return command.ExecuteNonQuery() > 0;
    }

    // === Prompt Fragment Versioning Support ===

    public IReadOnlyList<PromptFragmentVersionDto> ListPromptFragmentVersions(string fragmentId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select id, fragment_id, version, content, changed_fields_json, change_summary,
                   is_active, created_at
            from prompt_fragment_versions
            where fragment_id = $fragment_id
            order by version desc
            """;
        command.Parameters.AddWithValue("$fragment_id", fragmentId);
        using var reader = command.ExecuteReader();
        var versions = new List<PromptFragmentVersionDto>();
        while (reader.Read())
        {
            versions.Add(new PromptFragmentVersionDto(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                JsonSerializer.Deserialize<string[]>(reader.GetString(4)) ?? Array.Empty<string>(),
                reader.GetString(5),
                reader.GetBoolean(6),
                DateTimeOffset.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }
        return versions;
    }

    public void SavePromptFragmentVersion(PromptFragmentVersionDto version)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            insert into prompt_fragment_versions (id, fragment_id, version, content, changed_fields_json,
                                                   change_summary, is_active, created_at)
            values ($id, $fragment_id, $version, $content, $changed_fields_json,
                    $change_summary, $is_active, $created_at)
            """;
        command.Parameters.AddWithValue("$id", version.Id);
        command.Parameters.AddWithValue("$fragment_id", version.FragmentId);
        command.Parameters.AddWithValue("$version", version.Version);
        command.Parameters.AddWithValue("$content", version.Content);
        command.Parameters.AddWithValue("$changed_fields_json", JsonSerializer.Serialize(version.ChangedFields));
        command.Parameters.AddWithValue("$change_summary", version.ChangeSummary);
        command.Parameters.AddWithValue("$is_active", version.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$created_at", version.CreatedAt.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void DeactivatePromptFragmentVersion(string versionId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "update prompt_fragment_versions set is_active = 0 where id = $id";
        command.Parameters.AddWithValue("$id", versionId);
        command.ExecuteNonQuery();
    }

    public (int PositiveCount, int NegativeCount, DateTimeOffset LastEvaluatedAt) GetPromptFragmentSignalStats(string fragmentId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select
                sum(case when signal = 'positive' then 1 else 0 end) as positive,
                sum(case when signal = 'negative' then 1 else 0 end) as negative,
                max(created_at) as last_at
            from prompt_fragment_signals
            where fragment_id = $fragment_id
            """;
        command.Parameters.AddWithValue("$fragment_id", fragmentId);
        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            var positive = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            var negative = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            var lastAt = reader.IsDBNull(2) ? DateTimeOffset.UtcNow : DateTimeOffset.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind);
            return (positive, negative, lastAt);
        }
        return (0, 0, DateTimeOffset.UtcNow);
    }

    public (int PositiveCount, int NegativeCount) GetPromptFragmentVersionSignalStats(string fragmentId, int version)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select
                sum(case when signal = 'positive' then 1 else 0 end) as positive,
                sum(case when signal = 'negative' then 1 else 0 end) as negative
            from prompt_fragment_signals
            where fragment_id = $fragment_id and version = $version
            """;
        command.Parameters.AddWithValue("$fragment_id", fragmentId);
        command.Parameters.AddWithValue("$version", version);
        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            var positive = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            var negative = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            return (positive, negative);
        }
        return (0, 0);
    }

    public void RecordPromptFragmentSignal(string fragmentId, string signal, string? runId, string? sessionId, string? note, int? version = null)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            insert into prompt_fragment_signals (id, fragment_id, version, signal, run_id, session_id, note, created_at)
            values ($id, $fragment_id, $version, $signal, $run_id, $session_id, $note, $created_at)
            """;
        command.Parameters.AddWithValue("$id", $"sig_{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("$fragment_id", fragmentId);
        command.Parameters.AddWithValue("$version", (object?)version ?? DBNull.Value);
        command.Parameters.AddWithValue("$signal", signal);
        command.Parameters.AddWithValue("$run_id", (object?)runId ?? DBNull.Value);
        command.Parameters.AddWithValue("$session_id", (object?)sessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
        command.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }
}
