export interface StorageDiagnostic { code?: string; severity?: string; message?: string; [key: string]: unknown }
export interface StorageScopeDto {
  availability?: 'ready' | 'error'
  availability_error?: string; availability_code?: string; trace_id?: string
  category?: import('@/lib/apiError').ApiErrorCategory; retryable?: boolean; actions?: import('@/lib/apiError').ApiErrorActionKind[]
  workspace?: import('@/lib/workspaces').WorkspaceDefinition | null
  storage_id: string; scope_kind: string; project_id?: string | null; project_root?: string | null
  storage_root: string; backend: string; external: boolean; allow_storage_write?: boolean;
  /** The workspace runs on the user scope's models.toml (true for the user scope); false: its own copy. */
  follow_user_models?: boolean; postgres_connection_reference?: string | null; restart_required?: boolean; requested_storage_root?: string
  paths: Record<string, string>; diagnostics?: StorageDiagnostic[]
}
export interface StorageStatsDto { storage_id: string; categories: Array<{ category: string; path: string; size_bytes: number; file_count: number; clearable: boolean }>; diagnostics: StorageDiagnostic[] }
export interface StorageCleanupPreviewDto { preview_id: string; storage_id: string; category: string; path: string; file_count: number; size_bytes: number; expires_at: string }
export interface StorageContentPreviewDto { preview_id: string; storage_id: string; file_count: number; size_bytes: number; references: string[]; expires_at: string }
export interface StorageDeletePreviewDto { preview_id: string; storage_id: string; category: 'project_storage'; path: string; file_count: number; size_bytes: number; expires_at: string }
export interface SessionTransferDto { transfer_id: string; session_id: string; source_storage_id: string; storage_id: string; project_id: string; status: string; error?: string | null; error_code?: string | null; [key: string]: unknown }
export interface ConfigurationDocumentDto { document_id: string; path: string; text: string; content_hash: string; diagnostics: StorageDiagnostic[]; version: number }
export interface StorageConfigureInput { backend: string; storage_root?: string; postgres_connection_reference?: string }
