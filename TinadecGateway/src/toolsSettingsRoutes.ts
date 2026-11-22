import type { AnyElysia } from 'elysia';
import { proxyRaw } from './coreClient.js';

const object = { type: 'object', additionalProperties: true };
const ref = (name: string) => ({ $ref: `#/components/schemas/${name}` });
export const toolsSettingsSchemas = {
  ToolSettingsDocument: { type: 'object', properties: {
    schema_version: { type: 'integer' }, revision: { type: 'integer' }, settings: object,
    effective_settings: object, settings_hash: { type: 'string' }, agent_definition_id: { type: 'string', nullable: true },
    updated_at: { type: 'string', format: 'date-time', nullable: true },
  }, required: ['schema_version', 'revision', 'settings', 'effective_settings', 'settings_hash'] },
  ToolSettingsSchema: { type: 'object', properties: { schema_version: { type: 'integer' }, schema: object, defaults: object, host_settings: object }, required: ['schema_version', 'schema', 'defaults', 'host_settings'] },
  ToolSettingsWrite: { type: 'object', properties: { settings: object }, required: ['settings'], additionalProperties: false },
  ToolSettingsEffective: { type: 'object', properties: { schema_version: { type: 'integer' }, agent_definition_id: { type: 'string', nullable: true }, settings: object, settings_hash: { type: 'string' }, allowed_tool_ids: { type: 'array', items: { type: 'string' } }, mcp_servers: { type: 'array', items: object }, read_roots: { type: 'array', items: object }, skill_resources: { type: 'array', items: object }, resource_diagnostics: { type: 'array', items: ref('ToolResourceDiagnostic') } }, required: ['schema_version', 'settings', 'settings_hash', 'allowed_tool_ids', 'mcp_servers', 'read_roots', 'skill_resources'], additionalProperties: true },
  ToolResourceDiagnostic: { type: 'object', properties: { kind: { type: 'string', enum: ['mcp', 'skills', 'host'] }, resource_id: { type: 'string', format: 'uuid', nullable: true }, status: { type: 'string', enum: ['missing', 'invalid', 'disabled', 'import_failed', 'unavailable'] }, reason: { type: 'string' } }, required: ['kind', 'status', 'reason'] },
  ToolCapabilities: { type: 'object', properties: { status: { type: 'string', enum: ['available', 'unavailable'] }, reason: { type: 'string', nullable: true }, capabilities: { ...object, nullable: true }, settings_hash: { type: 'string', nullable: true } }, required: ['status'] },
  ToolMcpResource: { type: 'object', properties: {
    resource_id: { type: 'string' }, id: { type: 'string' }, name: { type: 'string' }, enabled: { type: 'boolean' },
    transport: { type: 'string', enum: ['stdio', 'http', 'sse'] }, url: { type: 'string', nullable: true },
    command: { type: 'string' }, args: { type: 'array', items: { type: 'string' } }, env: { type: 'object', nullable: true, additionalProperties: { type: 'string', nullable: true } },
    headers: { type: 'object', nullable: true, additionalProperties: { type: 'string', nullable: true } },
    cwd: { type: 'string', nullable: true }, revision: { type: 'integer' }, configuration_hash: { type: 'string' }, project_id: { type: 'string', nullable: true },
    updated_at: { type: 'string', format: 'date-time' }, import_source: { type: 'string', nullable: true },
  }, required: ['resource_id', 'id', 'name', 'enabled', 'transport', 'command', 'args', 'env', 'revision', 'configuration_hash'] },
  ToolMcpResourceList: { type: 'array', items: ref('ToolMcpResource') },
  ManagedMcpProgramPreview: { type: 'object', properties: {
    resource_id: { type: 'string' }, project_id: { type: 'string', nullable: true }, action: { type: 'string' },
    expected_revision: { type: 'integer' }, manager: { type: 'string' }, package: { type: 'string' }, version: { type: 'string' },
    package_root: { type: 'string' }, plan_hash: { type: 'string' }, program_status: { type: 'string' },
    server_arguments: { type: 'array', items: { type: 'string' } }, preview_id: { type: 'string' },
  }, required: ['resource_id', 'action', 'expected_revision', 'manager', 'package', 'version', 'package_root', 'plan_hash', 'program_status', 'preview_id'], additionalProperties: true },
  ToolSkillResource: { type: 'object', properties: {
    resource_id: { type: 'string' }, name: { type: 'string' }, description: { type: 'string' }, scope: { type: 'string' },
    project_id: { type: 'string', nullable: true }, enabled: { type: 'boolean' }, valid: { type: 'boolean' }, path: { type: 'string' }, content_hash: { type: 'string' }, revision: { type: 'integer' }, content: { type: 'string', nullable: true },
    file_hash: { type: 'string', nullable: true }, reason: { type: 'string', nullable: true },
    reason_code: { type: 'string', nullable: true }, reason_data: { type: 'object', nullable: true, additionalProperties: { type: 'string' } },
    user_action_id: { type: 'string', nullable: true }, action_status: { type: 'string', nullable: true },
    package_hash: { type: 'string' }, package_reference: { type: 'string' }, source: { type: 'string' }, version: { type: 'string', nullable: true }, commit: { type: 'string', nullable: true }, availability: { type: 'string' }, package_files: { type: 'array', items: ref('ToolSkillPackageFile') },
  }, required: ['resource_id', 'name', 'description', 'scope', 'enabled', 'valid', 'path', 'revision', 'content_hash'], additionalProperties: true },
  ToolSkillPackageFile: { type: 'object', properties: { path: { type: 'string' }, content_hash: { type: 'string' }, size_bytes: { type: 'integer' } }, required: ['path', 'content_hash', 'size_bytes'] },
  ToolSkillFile: { type: 'object', properties: { path: { type: 'string' }, content_hash: { type: 'string' }, size_bytes: { type: 'integer' }, content: { type: 'string', nullable: true }, base64: { type: 'string', nullable: true } }, required: ['path', 'content_hash', 'size_bytes'] },
  ToolSkillCatalog: { type: 'object', properties: { skills: { type: 'array', items: ref('ToolSkillResource') }, diagnostics: { type: 'array', items: { type: 'string' } }, source: { type: 'string' } }, required: ['skills', 'diagnostics', 'source'] },
  ToolSkillDiagnostics: { type: 'object', properties: { source: { type: 'string' }, skills: { type: 'array', items: object }, diagnostics: { type: 'array', items: { type: 'string' } } }, required: ['source', 'skills', 'diagnostics'] },
  McpImportJsonRequest: { type: 'object', properties: {
    json: { type: 'string', nullable: true }, config: { ...object, nullable: true },
    project_id: { type: 'string', format: 'uuid', nullable: true }, overwrite: { type: 'boolean' },
    default_enabled: { type: 'boolean', nullable: true }, import_source: { type: 'string', nullable: true },
  }, additionalProperties: true },
  McpImportJsonSkipped: { type: 'object', properties: { id: { type: 'string' }, reason: { type: 'string' } }, required: ['id', 'reason'] },
  McpImportJsonResult: { type: 'object', properties: {
    imported: { type: 'array', items: ref('ToolMcpResource') },
    skipped: { type: 'array', items: ref('McpImportJsonSkipped') },
    imported_count: { type: 'integer' }, skipped_count: { type: 'integer' },
  }, required: ['imported', 'skipped', 'imported_count', 'skipped_count'] },
  McpServerEnabledRequest: { type: 'object', properties: { enabled: { type: 'boolean' }, project_id: { type: 'string', format: 'uuid', nullable: true } }, required: ['enabled'] },
  McpServerTestResult: { type: 'object', properties: {
    resource_id: { type: 'string' }, id: { type: 'string' }, name: { type: 'string' }, enabled: { type: 'boolean' },
    status: { type: 'string' }, reason: { type: 'string', nullable: true }, tool_count: { type: 'integer' },
    tools: { type: 'array', items: object },
  }, required: ['resource_id', 'id', 'name', 'enabled', 'status', 'tool_count', 'tools'] },
  ToolSkillEnabledRequest: { type: 'object', properties: { enabled: { type: 'boolean' }, project_id: { type: 'string', format: 'uuid', nullable: true } }, required: ['enabled'] },
  ToolSkillInstallRequest: { type: 'object', properties: {
    source_type: { type: 'string', enum: ['inline', 'local_dir', 'git_url', 'external_cli_import'] },
    scope: { type: 'string', enum: ['shared', 'project'] },
    project_id: { type: 'string', format: 'uuid', nullable: true },
    name: { type: 'string', nullable: true }, content: { type: 'string', nullable: true },
    files: { type: 'object', nullable: true, additionalProperties: { type: 'string' } },
    local_path: { type: 'string', nullable: true }, git_url: { type: 'string', nullable: true },
    subpath: { type: 'string', nullable: true }, item_ids: { type: 'array', items: { type: 'string' }, nullable: true },
    ecosystems: { type: 'array', items: { type: 'string' }, nullable: true },
    enabled: { type: 'boolean', nullable: true }, enable_stdio_mcp: { type: 'boolean', nullable: true },
    overwrite: { type: 'boolean' }, governed: { type: 'boolean' },
  }, required: ['source_type'], additionalProperties: true },
  ExternalCliImportedCommand: { type: 'object', properties: { name: { type: 'string' }, path: { type: 'string' }, ecosystem: { type: 'string' } }, required: ['name', 'path', 'ecosystem'] },
  ExternalCliSkippedItem: { type: 'object', properties: { item_id: { type: 'string' }, reason: { type: 'string' } }, required: ['item_id', 'reason'] },
  ToolSkillInstallResult: { type: 'object', properties: {
    source_type: { type: 'string' },
    installed_skills: { type: 'array', items: ref('ToolSkillResource') },
    installed_mcp_servers: { type: 'array', items: ref('ToolMcpResource') },
    installed_commands: { type: 'array', items: ref('ExternalCliImportedCommand') },
    skipped: { type: 'array', items: ref('ExternalCliSkippedItem') },
  }, required: ['source_type', 'installed_skills', 'installed_mcp_servers', 'installed_commands', 'skipped'] },
  ExternalCliEcosystemStatus: { type: 'object', properties: {
    id: { type: 'string' }, label: { type: 'string' }, detected: { type: 'boolean' },
    paths_scanned: { type: 'array', items: { type: 'string' } },
  }, required: ['id', 'label', 'detected', 'paths_scanned'] },
  ExternalCliDiscoveredItem: { type: 'object', properties: {
    item_id: { type: 'string' }, ecosystem: { type: 'string' }, kind: { type: 'string', enum: ['mcp-server', 'skill', 'command'] },
    name: { type: 'string' }, display_name: { type: 'string' }, description: { type: 'string', nullable: true },
    source_path: { type: 'string' }, transport: { type: 'string', nullable: true }, command: { type: 'string', nullable: true },
    args: { type: 'array', items: { type: 'string' } }, url: { type: 'string', nullable: true },
    env_keys: { type: 'array', items: { type: 'string' } }, header_keys: { type: 'array', items: { type: 'string' } },
    has_secrets: { type: 'boolean' }, default_enabled: { type: 'boolean' }, already_imported: { type: 'boolean' },
    content_preview: { type: 'string', nullable: true },
  }, required: ['item_id', 'ecosystem', 'kind', 'name', 'display_name', 'source_path', 'args', 'env_keys', 'header_keys', 'has_secrets', 'default_enabled', 'already_imported'] },
  ExternalCliDiscovery: { type: 'object', properties: {
    user_home: { type: 'string' }, workspace_root: { type: 'string', nullable: true },
    ecosystems: { type: 'array', items: ref('ExternalCliEcosystemStatus') },
    items: { type: 'array', items: ref('ExternalCliDiscoveredItem') },
  }, required: ['user_home', 'ecosystems', 'items'] },
};

/** Configuration authority, credentials, resource writes and revisions remain in Core. */
export function registerToolsSettingsRoutes(app: AnyElysia, forwardHeaders: (request: Request) => Record<string, string>) {
  const routes: [string, string, string, string?][] = [
    ['GET', '/api/v1/tools/settings/schema', 'ToolSettingsSchema'],
    ['GET', '/api/v1/tools/settings/defaults', 'ToolSettingsDocument'],
    ['PUT', '/api/v1/tools/settings/defaults', 'ToolSettingsDocument', 'ToolSettingsWrite'],
    ['GET', '/api/v1/tools/settings/agents/:agentId', 'ToolSettingsDocument'],
    ['PUT', '/api/v1/tools/settings/agents/:agentId', 'ToolSettingsDocument', 'ToolSettingsWrite'],
    ['DELETE', '/api/v1/tools/settings/agents/:agentId', 'ToolSettingsDocument'],
    ['GET', '/api/v1/tools/settings/effective', 'ToolSettingsEffective'],
    ['GET', '/api/v1/tools/settings/capabilities', 'ToolCapabilities'],
    ['GET', '/api/v1/tools/mcp/servers', 'ToolMcpResourceList'],
    ['POST', '/api/v1/tools/mcp/servers/import-json', 'McpImportJsonResult', 'McpImportJsonRequest'],
    ['POST', '/api/v1/tools/mcp/servers', 'ToolMcpResource'],
    ['PUT', '/api/v1/tools/mcp/servers/:resourceId', 'ToolMcpResource'],
    ['PATCH', '/api/v1/tools/mcp/servers/:resourceId/enabled', 'ToolMcpResource', 'McpServerEnabledRequest'],
    ['DELETE', '/api/v1/tools/mcp/servers/:resourceId', ''],
    ['POST', '/api/v1/tools/mcp/servers/:resourceId/test', 'McpServerTestResult'],
    ['POST', '/api/v1/tools/mcp/servers/:resourceId/install-preview', 'ManagedMcpProgramPreview'],
    ['POST', '/api/v1/tools/mcp/servers/:resourceId/install', ''],
    ['GET', '/api/v1/tools/external-cli/discover', 'ExternalCliDiscovery'],
    ['GET', '/api/v1/tools/skills', 'ToolSkillCatalog'],
    ['GET', '/api/v1/tools/skills/diagnostics', 'ToolSkillDiagnostics'],
    ['POST', '/api/v1/tools/skills/install', 'ToolSkillInstallResult', 'ToolSkillInstallRequest'],
    ['POST', '/api/v1/tools/skills/reload', 'ToolSkillCatalog'],
    ['POST', '/api/v1/tools/skills/import', 'ToolSkillResource'],
    ['POST', '/api/v1/tools/skills/:resourceId/copy-to-project', 'ToolSkillResource'],
    ['PATCH', '/api/v1/tools/skills/:resourceId/enabled', 'ToolSkillResource', 'ToolSkillEnabledRequest'],
    ['GET', '/api/v1/tools/skills/:resourceId', 'ToolSkillResource'],
    ['GET', '/api/v1/tools/skills/:resourceId/files', 'ToolSkillFile'],
    ['PUT', '/api/v1/tools/skills/:resourceId', 'ToolSkillResource'],
    ['DELETE', '/api/v1/tools/skills/:resourceId', 'ToolSkillResource'],
  ];
  for (const [method, path, response, bodySchema] of routes) {
    app.route(method as 'GET' | 'PUT' | 'POST' | 'PATCH' | 'DELETE', path, async context => {
      const request = context.request as Request;
      const url = new URL(request.url);
      return proxyRaw(url.pathname + url.search, {
        method,
        headers: forwardHeaders(request),
        body: method === 'GET' || context.body === undefined ? undefined : JSON.stringify(context.body),
        signal: request.signal,
      });
    }, { detail: {
      summary: `${method} ${path.replace('/api/v1/tools/', '')}`, tags: ['Tools'],
      ...(path.endsWith('/files') ? { parameters: [{ name: 'path', in: 'query', required: true, schema: { type: 'string' }, description: 'Exact relative package file path; Core enforces the resource root.' }, { name: 'project_id', in: 'query', required: false, schema: { type: 'string', format: 'uuid' } }] } : {}),
      ...(bodySchema ? { requestBody: { required: true, content: { 'application/json': { schema: ref(bodySchema) } } } } : {}),
      responses: { [method === 'DELETE' && !response ? 204 : 200]: { description: 'Core-owned configuration and resource result', ...(response ? { content: { 'application/json': { schema: ref(response) } } } : {}) } },
      ...(method === 'PUT' || method === 'DELETE' || path === '/api/v1/tools/mcp/servers' || path === '/api/v1/tools/skills/import' ? { parameters: [{ name: 'If-Match', in: 'header', required: true, schema: { type: 'string' }, description: method === 'POST' ? 'Quoted zero revision for a new resource' : 'Quoted revision returned by Core' }] } : {}),
    } as never });
  }
}
