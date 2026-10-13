import type { AnyElysia } from 'elysia';
import { proxyRaw } from './coreClient.js';

const string = { type: 'string' };
const diagnostic = { type: 'object', additionalProperties: true };
const ref = (name: string) => ({ $ref: `#/components/schemas/${name}` });
export const storageSchemas = {
  WorkspaceRoot: { type: 'object', properties: { id: string, path: string }, required: ['id', 'path'] },
  WorkspaceDefinition: { type: 'object', properties: { name: string, roots: { type: 'array', items: ref('WorkspaceRoot') }, primary_root_id: string, icon: string, color: string, content_hash: string, primary_path: string }, required: ['name', 'roots', 'primary_root_id', 'icon', 'color', 'content_hash'] },
  WorkspaceEditRequest: { type: 'object', properties: { name: string, roots: { type: 'array', items: ref('WorkspaceRoot') }, primary_root_id: string, icon: string, color: string }, required: ['name', 'roots', 'primary_root_id'] },
  WorkspacePreviewRequest: { type: 'object', properties: { project_path: string }, required: ['project_path'] },
  WorkspacePreview: { type: 'object', properties: { exists: { type: 'boolean' }, project_path: string, storage_id: { ...string, nullable: true }, storage_root: { ...string, nullable: true }, workspace: { anyOf: [ref('WorkspaceDefinition'), { type: 'null' }] } }, required: ['exists', 'project_path'] },
  StorageScope: { type: 'object', properties: {
    storage_id: string, scope_kind: string, project_id: { ...string, nullable: true }, project_root: { ...string, nullable: true },
    workspace: { anyOf: [ref('WorkspaceDefinition'), { type: 'null' }] },
    availability: { type: 'string', enum: ['ready', 'error'] }, availability_error: string, availability_code: string, trace_id: string,
    category: ref('ErrorCategory'), retryable: { type: 'boolean' }, actions: { type: 'array', items: ref('ErrorRecoveryAction') },
    storage_root: string, backend: string, external: { type: 'boolean' }, paths: { type: 'object', additionalProperties: string },
    diagnostics: { type: 'array', items: diagnostic }, allow_storage_write: { type: 'boolean' },
    follow_user_models: { type: 'boolean', description: 'The workspace runs on the user scope models.toml (always true for the user scope); false when it keeps its own copy.' },
    postgres_connection_reference: { ...string, nullable: true }, restart_required: { type: 'boolean' }, requested_storage_root: string,
  }, required: ['storage_id', 'scope_kind', 'storage_root', 'backend', 'external', 'paths', 'workspace', 'availability'] },
  StorageScopeList: { type: 'array', items: ref('StorageScope') },
  StorageStatistics: { type: 'object', properties: { storage_id: string, categories: { type: 'array', items: { type: 'object', properties: {
    category: string, path: string, size_bytes: { type: 'integer' }, file_count: { type: 'integer' }, clearable: { type: 'boolean' },
  }, required: ['category', 'path', 'size_bytes', 'file_count', 'clearable'] } }, diagnostics: { type: 'array', items: diagnostic } }, required: ['storage_id', 'categories', 'diagnostics'] },
  StorageCleanupPreview: { type: 'object', properties: { preview_id: string, storage_id: string, category: string, path: string, file_count: { type: 'integer' }, size_bytes: { type: 'integer' }, expires_at: { type: 'string', format: 'date-time' } }, required: ['preview_id', 'storage_id', 'category', 'path', 'file_count', 'size_bytes', 'expires_at'] },
  StorageContentPreview: { type: 'object', properties: { preview_id: string, storage_id: string, file_count: { type: 'integer' }, size_bytes: { type: 'integer' }, references: { type: 'array', items: string }, expires_at: { type: 'string', format: 'date-time' } }, required: ['preview_id', 'storage_id', 'file_count', 'size_bytes', 'references', 'expires_at'] },
  SessionTransfer: { type: 'object', additionalProperties: true, properties: { transfer_id: string, session_id: string, source_storage_id: string, storage_id: string, project_id: string, status: string }, required: ['transfer_id', 'session_id', 'source_storage_id', 'storage_id', 'project_id', 'status'] },
  ConfigurationDocument: { type: 'object', properties: { document_id: string, path: string, text: string, content_hash: string, diagnostics: { type: 'array', items: diagnostic }, version: { type: 'integer' } }, required: ['document_id', 'path', 'text', 'content_hash', 'diagnostics', 'version'] },
  ConfigurationValidation: { type: 'object', properties: { valid: { type: 'boolean' }, diagnostics: { type: 'array', items: diagnostic } }, required: ['valid', 'diagnostics'] },
  StorageWritePolicy: { type: 'object', properties: { storage_id: string, allow_storage_write: { type: 'boolean' } }, required: ['storage_id', 'allow_storage_write'] },
  StorageModelSource: { type: 'object', properties: { storage_id: string, follow_user_models: { type: 'boolean' } }, required: ['storage_id', 'follow_user_models'] },
  StorageDiagnostics: { type: 'object', properties: { storage_id: string, scope: ref('StorageScope'), diagnostics: { type: 'array', items: diagnostic }, configuration: { type: 'object', additionalProperties: true } }, required: ['storage_id', 'scope', 'diagnostics', 'configuration'] },
  StorageOpenRequest: { type: 'object', properties: { project_path: string, name: string, backend: string, storage_root: string, postgres_connection_reference: string,
    roots: { type: 'array', items: ref('WorkspaceRoot') }, primary_root_id: string, icon: string, color: string }, required: ['project_path'] },
  StorageConfigureRequest: { type: 'object', properties: { backend: string, storage_root: string, postgres_connection_reference: string }, required: ['backend'] },
  StoragePreviewApplyRequest: { type: 'object', properties: { preview_id: string }, required: ['preview_id'] },
  StorageCleanupRequest: { type: 'object', properties: { category: { type: 'string', enum: ['cache', 'temp', 'logs'] } }, required: ['category'] },
  StorageWritePolicyRequest: { type: 'object', properties: { allow_storage_write: { type: 'boolean' } }, required: ['allow_storage_write'] },
  StorageModelSourceRequest: { type: 'object', properties: { follow_user: { type: 'boolean' } }, required: ['follow_user'] },
  ConfigurationTextRequest: { type: 'object', properties: { text: string }, required: ['text'] },
  SessionTransferRequest: { type: 'object', properties: { target_storage_id: string, target_project_id: string }, required: ['target_storage_id'] },
};

/** A byte-preserving facade. Core alone opens stores, writes TOML, and authorizes cleanup. */
export function registerStorageRoutes(app: AnyElysia, forwardHeaders: (request: Request) => Record<string, string>) {
  const routes: [string, string, string?][] = [
    ['GET', '/api/v1/storage/scopes', 'StorageScopeList'],
    ['POST', '/api/v1/storage/scopes/open', 'StorageScope'],
    ['POST', '/api/v1/storage/scopes/preview', 'WorkspacePreview'],
    ['GET', '/api/v1/storage/scopes/:storageId/workspace', 'WorkspaceDefinition'],
    ['PUT', '/api/v1/storage/scopes/:storageId/workspace', 'WorkspaceDefinition'],
    ['GET', '/api/v1/storage/scopes/:storageId/diagnostics', 'StorageDiagnostics'],
    ['GET', '/api/v1/storage/scopes/:storageId/stats', 'StorageStatistics'],
    ['POST', '/api/v1/storage/scopes/:storageId/cleanup-preview', 'StorageCleanupPreview'],
    ['POST', '/api/v1/storage/scopes/:storageId/cleanup'],
    ['POST', '/api/v1/storage/scopes/:storageId/content-preview', 'StorageContentPreview'],
    ['POST', '/api/v1/storage/scopes/:storageId/content-collect'],
    ['POST', '/api/v1/storage/scopes/:storageId/storage-delete-preview', 'StorageCleanupPreview'],
    ['POST', '/api/v1/storage/scopes/:storageId/storage-delete'],
    ['POST', '/api/v1/storage/scopes/:storageId/configure', 'StorageScope'],
    ['POST', '/api/v1/storage/scopes/:storageId/write-policy', 'StorageWritePolicy'],
    ['POST', '/api/v1/storage/scopes/:storageId/model-source', 'StorageModelSource'],
    ['POST', '/api/v1/storage/scopes/:storageId/close'],
    ['DELETE', '/api/v1/storage/scopes/:storageId'],
    ['POST', '/api/v1/storage/scopes/:storageId/export'],
    ['GET', '/api/v1/configuration/documents'],
    ['GET', '/api/v1/session-transfers/:transferId', 'SessionTransfer'],
    ['GET', '/api/v1/configuration/documents/:documentId', 'ConfigurationDocument'],
    ['POST', '/api/v1/configuration/documents/:documentId/validate', 'ConfigurationValidation'],
    ['PUT', '/api/v1/configuration/documents/:documentId', 'ConfigurationDocument'],
  ];
  const bodySchema = (method: string, path: string) => {
    if (path.endsWith('/open')) return 'StorageOpenRequest';
    if (path === '/api/v1/storage/scopes/preview') return 'WorkspacePreviewRequest';
    if (path.endsWith('/workspace') && method === 'PUT') return 'WorkspaceEditRequest';
    if (path.endsWith('/configure')) return 'StorageConfigureRequest';
    if (path.endsWith('/write-policy')) return 'StorageWritePolicyRequest';
    if (path.endsWith('/model-source')) return 'StorageModelSourceRequest';
    if (path.endsWith('/cleanup-preview')) return 'StorageCleanupRequest';
    if (path.endsWith('/cleanup') || path.endsWith('/content-collect') || path.endsWith('/storage-delete')) return 'StoragePreviewApplyRequest';
    if (path.includes('/configuration/') && (method === 'PUT' || path.endsWith('/validate'))) return 'ConfigurationTextRequest';
  };
  for (const [method, path, response] of routes) app.route(method as 'GET' | 'POST' | 'PUT' | 'DELETE', path, async context => {
    const request = context.request as Request;
    const url = new URL(request.url);
    return proxyRaw(url.pathname + url.search, { method, headers: forwardHeaders(request),
      body: method === 'GET' || context.body === undefined ? undefined : JSON.stringify(context.body), signal: request.signal });
  }, { detail: { summary: `${method} ${path}`, tags: ['Storage'],
    parameters: [{ name: 'X-Tinadec-Storage-Id', in: 'header', required: false, schema: string },
       { name: 'X-Tinadec-Host-Control', in: 'header', required: true, schema: string, description: 'Scope-enabled Core requires a private trusted host credential for reads, previews, writes, and lifecycle actions. Never expose it to an Agent or renderer.' },
      ...(method === 'PUT' ? [{ name: 'If-Match', in: 'header', required: true, schema: string }] : [])],
    ...(bodySchema(method, path) ? { requestBody: { required: true, content: { 'application/json': { schema: ref(bodySchema(method, path)!) } } } } : {}),
    responses: method === 'DELETE' || path.endsWith('/close') ? { 204: { description: 'Scope closed or unregistered; files retained.' } }
      : path.endsWith('/export') ? { 200: { description: 'Consistent scope archive without credentials.', content: { 'application/zip': { schema: { type: 'string', format: 'binary' } } } } }
      : { 200: { description: 'Core storage/configuration result', ...(response ? { content: { 'application/json': { schema: ref(response) } } } : {}) } },
  } as never });
}
