import type { AnyElysia } from 'elysia';
import { proxyRaw, proxySse } from './coreClient.js';
import { createSseResponse } from './streaming.js';

const ref = (name: string) => ({ $ref: `#/components/schemas/${name}` });
const nullableRef = (name: string) => ({ anyOf: [ref(name), { type: 'null' }] });

const STORAGE_MODES = ['repo_tinadec', 'app_data', 'custom_dir'] as const;
const TASK_STATUSES = ['backlog', 'ready', 'in_progress', 'in_review', 'done'] as const;
const TASK_PRIORITIES = ['urgent', 'high', 'medium', 'low', 'none'] as const;
const ASSIGNEE_KINDS = ['human', 'native', 'cli', 'coordinator'] as const;
const VIEW_SCOPES = ['global', 'project'] as const;
const VIEW_LAYOUTS = ['kanban', 'list', 'swimlane'] as const;
const VIEW_GROUP_BYS = ['status', 'project', 'assignee', 'priority', 'none'] as const;
const VIEW_SWIMLANE_BYS = ['project', 'assignee'] as const;
const VIEW_SORT_BYS = ['manual', 'priority', 'updated_at', 'dependency'] as const;
const SPATIAL_PROMOTION_MODES = ['merge_single', 'split_many', 'parent_with_children'] as const;
const PLANNER_SOURCES = ['llm', 'heuristic'] as const;
const SPATIAL_OBJECT_KINDS = [
  'work',
  'section',
  'sticky',
  'text',
  'drawing',
  'media',
  'connector',
  'board',
] as const;
const BOARD_EVENT_TYPES = [
  'snapshot',
  'task_upserted',
  'task_deleted',
  'settings_updated',
  'view_upserted',
  'view_deleted',
  'sync_completed',
  'notes_updated',
  'autopilot_updated',
] as const;

export const projectBoardSchemas = {
  ProjectBoardSettings: {
    type: 'object',
    properties: {
      project_id: { type: 'string', format: 'uuid' },
      key_prefix: { type: 'string' },
      next_sequence: { type: 'integer' },
      storage_mode: { type: 'string', enum: [...STORAGE_MODES] },
      custom_sync_root: { type: 'string', nullable: true },
      resolved_board_dir: { type: 'string' },
      project_instructions: { type: 'string' },
      default_assignee_kind: { type: 'string', enum: [...ASSIGNEE_KINDS] },
      max_concurrency: { type: 'integer' },
      autopilot_enabled: { type: 'boolean' },
      updated_at_unix_ms: { type: 'integer' },
    },
    required: [
      'project_id',
      'key_prefix',
      'next_sequence',
      'storage_mode',
      'custom_sync_root',
      'resolved_board_dir',
      'project_instructions',
      'default_assignee_kind',
      'max_concurrency',
      'autopilot_enabled',
      'updated_at_unix_ms',
    ],
  },
  UpdateProjectBoardSettingsRequest: {
    type: 'object',
    properties: {
      key_prefix: { type: 'string', nullable: true },
      storage_mode: { type: 'string', enum: [...STORAGE_MODES], nullable: true },
      custom_sync_root: { type: 'string', nullable: true },
      project_instructions: { type: 'string', nullable: true },
      default_assignee_kind: { type: 'string', enum: [...ASSIGNEE_KINDS], nullable: true },
      max_concurrency: { type: 'integer', nullable: true },
      autopilot_enabled: { type: 'boolean', nullable: true },
    },
    additionalProperties: false,
  },
  ProjectBoardSyncResult: {
    type: 'object',
    properties: {
      project_id: { type: 'string', format: 'uuid' },
      storage_mode: { type: 'string', enum: [...STORAGE_MODES] },
      resolved_board_dir: { type: 'string' },
      imported_count: { type: 'integer' },
      exported_count: { type: 'integer' },
      merged_count: { type: 'integer' },
      unchanged_count: { type: 'integer' },
      conflict_count: { type: 'integer' },
      synced_at_unix_ms: { type: 'integer' },
    },
    required: [
      'project_id',
      'storage_mode',
      'resolved_board_dir',
      'imported_count',
      'exported_count',
      'merged_count',
      'unchanged_count',
      'conflict_count',
      'synced_at_unix_ms',
    ],
  },
  ProjectBoardChecklistItem: {
    type: 'object',
    properties: {
      id: { type: 'string' },
      text: { type: 'string' },
      done: { type: 'boolean' },
    },
    required: ['id', 'text', 'done'],
  },
  ProjectBoardAssignee: {
    type: 'object',
    properties: {
      kind: { type: 'string', enum: [...ASSIGNEE_KINDS] },
      id: { type: 'string', nullable: true },
      display_name: { type: 'string', nullable: true },
      mode_version_id: { type: 'string', format: 'uuid', nullable: true },
      harness_id: { type: 'string', nullable: true },
    },
    required: ['kind'],
  },
  ProjectBoardSpatialRef: {
    type: 'object',
    properties: {
      session_id: { type: 'string', format: 'uuid' },
      object_id: { type: 'string' },
      viewport_hint: { type: 'object', additionalProperties: true, nullable: true },
    },
    required: ['session_id', 'object_id'],
  },
  ProjectBoardDiffStats: {
    type: 'object',
    properties: {
      added: { type: 'integer' },
      removed: { type: 'integer' },
      files: { type: 'integer' },
    },
    required: ['added', 'removed', 'files'],
  },
  ProjectBoardTaskSnapshot: {
    type: 'object',
    properties: {
      title: { type: 'string' },
      description_md: { type: 'string' },
      checklist: { type: 'array', items: ref('ProjectBoardChecklistItem') },
      status: { type: 'string', enum: [...TASK_STATUSES] },
      priority: { type: 'string', enum: [...TASK_PRIORITIES] },
      blocked: { type: 'boolean' },
      blocked_reason: { type: 'string', nullable: true },
      assignee: nullableRef('ProjectBoardAssignee'),
      parent_task_id: { type: 'string', format: 'uuid', nullable: true },
      depends_on: { type: 'array', items: { type: 'string', format: 'uuid' } },
      labels: { type: 'array', items: { type: 'string' } },
      spatial_ref: nullableRef('ProjectBoardSpatialRef'),
      sort_key: { type: 'string' },
      revision: { type: 'integer' },
      updated_at_unix_ms: { type: 'integer' },
    },
    required: [
      'title',
      'description_md',
      'checklist',
      'status',
      'priority',
      'blocked',
      'blocked_reason',
      'assignee',
      'parent_task_id',
      'depends_on',
      'labels',
      'spatial_ref',
      'sort_key',
      'revision',
      'updated_at_unix_ms',
    ],
  },
  ProjectBoardSyncConflict: {
    type: 'object',
    properties: {
      file_rel_path: { type: 'string' },
      detected_at_unix_ms: { type: 'integer' },
      file_snapshot: ref('ProjectBoardTaskSnapshot'),
      board_snapshot: ref('ProjectBoardTaskSnapshot'),
      conflicting_fields: { type: 'array', items: { type: 'string' } },
    },
    required: [
      'file_rel_path',
      'detected_at_unix_ms',
      'file_snapshot',
      'board_snapshot',
      'conflicting_fields',
    ],
  },
  ProjectBoardTask: {
    type: 'object',
    properties: {
      task_id: { type: 'string', format: 'uuid' },
      project_id: { type: 'string', format: 'uuid' },
      project_name: { type: 'string' },
      task_key: { type: 'string' },
      title: { type: 'string' },
      description_md: { type: 'string' },
      checklist: { type: 'array', items: ref('ProjectBoardChecklistItem') },
      status: { type: 'string', enum: [...TASK_STATUSES] },
      priority: { type: 'string', enum: [...TASK_PRIORITIES] },
      blocked: { type: 'boolean' },
      blocked_reason: { type: 'string', nullable: true },
      assignee: nullableRef('ProjectBoardAssignee'),
      parent_task_id: { type: 'string', format: 'uuid', nullable: true },
      depends_on: { type: 'array', items: { type: 'string', format: 'uuid' } },
      labels: { type: 'array', items: { type: 'string' } },
      spatial_ref: nullableRef('ProjectBoardSpatialRef'),
      sort_key: { type: 'string' },
      active_session_id: { type: 'string', format: 'uuid', nullable: true },
      active_run_id: { type: 'string', format: 'uuid', nullable: true },
      run_status: { type: 'string', nullable: true },
      worktree_path: { type: 'string', nullable: true },
      lease_id: { type: 'string', format: 'uuid', nullable: true },
      live_digest_summary: { type: 'string', nullable: true },
      diff_stats: nullableRef('ProjectBoardDiffStats'),
      has_sync_conflict: { type: 'boolean' },
      sync_conflict: nullableRef('ProjectBoardSyncConflict'),
      revision: { type: 'integer' },
      created_at_unix_ms: { type: 'integer' },
      updated_at_unix_ms: { type: 'integer' },
    },
    required: [
      'task_id',
      'project_id',
      'project_name',
      'task_key',
      'title',
      'description_md',
      'checklist',
      'status',
      'priority',
      'blocked',
      'blocked_reason',
      'assignee',
      'parent_task_id',
      'depends_on',
      'labels',
      'spatial_ref',
      'sort_key',
      'active_session_id',
      'active_run_id',
      'run_status',
      'worktree_path',
      'lease_id',
      'live_digest_summary',
      'diff_stats',
      'has_sync_conflict',
      'sync_conflict',
      'revision',
      'created_at_unix_ms',
      'updated_at_unix_ms',
    ],
  },
  CreateProjectBoardTaskRequest: {
    type: 'object',
    properties: {
      title: { type: 'string' },
      description_md: { type: 'string', nullable: true },
      checklist: { type: 'array', items: ref('ProjectBoardChecklistItem'), nullable: true },
      status: { type: 'string', enum: [...TASK_STATUSES], nullable: true },
      priority: { type: 'string', enum: [...TASK_PRIORITIES], nullable: true },
      blocked: { type: 'boolean', nullable: true },
      blocked_reason: { type: 'string', nullable: true },
      assignee: nullableRef('ProjectBoardAssignee'),
      parent_task_id: { type: 'string', format: 'uuid', nullable: true },
      depends_on: { type: 'array', items: { type: 'string', format: 'uuid' }, nullable: true },
      labels: { type: 'array', items: { type: 'string' }, nullable: true },
      spatial_ref: nullableRef('ProjectBoardSpatialRef'),
      sort_key: { type: 'string', nullable: true },
    },
    required: ['title'],
    additionalProperties: false,
  },
  UpdateProjectBoardTaskRequest: {
    type: 'object',
    properties: {
      title: { type: 'string', nullable: true },
      description_md: { type: 'string', nullable: true },
      checklist: { type: 'array', items: ref('ProjectBoardChecklistItem'), nullable: true },
      status: { type: 'string', enum: [...TASK_STATUSES], nullable: true },
      priority: { type: 'string', enum: [...TASK_PRIORITIES], nullable: true },
      blocked: { type: 'boolean', nullable: true },
      blocked_reason: { type: 'string', nullable: true },
      assignee: nullableRef('ProjectBoardAssignee'),
      parent_task_id: { type: 'string', format: 'uuid', nullable: true },
      depends_on: { type: 'array', items: { type: 'string', format: 'uuid' }, nullable: true },
      labels: { type: 'array', items: { type: 'string' }, nullable: true },
      spatial_ref: nullableRef('ProjectBoardSpatialRef'),
      sort_key: { type: 'string', nullable: true },
      expected_revision: { type: 'integer', nullable: true },
    },
    additionalProperties: false,
  },
  ResolveProjectBoardConflictRequest: {
    type: 'object',
    properties: {
      resolution: { type: 'string', enum: ['keep_file', 'keep_board'] },
    },
    required: ['resolution'],
    additionalProperties: false,
  },
  DispatchProjectBoardTaskRequest: {
    type: 'object',
    properties: {
      assignee_override: nullableRef('ProjectBoardAssignee'),
      use_worktree: { type: 'boolean', nullable: true },
      extra_prompt: { type: 'string', nullable: true },
    },
    additionalProperties: false,
  },
  ProjectBoardDiffFile: {
    type: 'object',
    properties: {
      path: { type: 'string' },
      status: { type: 'string' },
      added: { type: 'integer' },
      removed: { type: 'integer' },
    },
    required: ['path', 'status', 'added', 'removed'],
  },
  ProjectBoardTaskDiff: {
    type: 'object',
    properties: {
      worktree_path: { type: 'string', nullable: true },
      diff_text: { type: 'string' },
      files: { type: 'array', items: ref('ProjectBoardDiffFile') },
    },
    required: ['worktree_path', 'diff_text', 'files'],
  },
  ApproveProjectBoardTaskRequest: {
    type: 'object',
    properties: {
      merge_or_clean_worktree: { type: 'boolean', nullable: true },
      note: { type: 'string', nullable: true },
    },
    additionalProperties: false,
  },
  RequestTaskChangesRequest: {
    type: 'object',
    properties: {
      feedback: { type: 'string' },
    },
    required: ['feedback'],
    additionalProperties: false,
  },
  ProjectBoardViewFilters: {
    type: 'object',
    properties: {
      project_ids: { type: 'array', items: { type: 'string', format: 'uuid' }, nullable: true },
      assignee_kinds: { type: 'array', items: { type: 'string', enum: [...ASSIGNEE_KINDS] }, nullable: true },
      statuses: { type: 'array', items: { type: 'string', enum: [...TASK_STATUSES] }, nullable: true },
      priorities: { type: 'array', items: { type: 'string', enum: [...TASK_PRIORITIES] }, nullable: true },
      labels: { type: 'array', items: { type: 'string' }, nullable: true },
      needs_my_action: { type: 'boolean', nullable: true },
      has_conflict: { type: 'boolean', nullable: true },
      query: { type: 'string', nullable: true },
    },
  },
  ProjectBoardViewConfig: {
    type: 'object',
    properties: {
      layout: { type: 'string', enum: [...VIEW_LAYOUTS] },
      group_by: { type: 'string', enum: [...VIEW_GROUP_BYS] },
      swimlane_by: { type: 'string', enum: [...VIEW_SWIMLANE_BYS], nullable: true },
      sort_by: { type: 'string', enum: [...VIEW_SORT_BYS] },
      filters: ref('ProjectBoardViewFilters'),
      visible_fields: { type: 'array', items: { type: 'string' } },
    },
    required: ['layout', 'group_by', 'swimlane_by', 'sort_by', 'filters', 'visible_fields'],
  },
  ProjectBoardView: {
    type: 'object',
    properties: {
      view_id: { type: 'string', format: 'uuid' },
      scope: { type: 'string', enum: [...VIEW_SCOPES] },
      project_id: { type: 'string', format: 'uuid', nullable: true },
      name: { type: 'string' },
      config: ref('ProjectBoardViewConfig'),
      sort_order: { type: 'integer' },
      created_at_unix_ms: { type: 'integer' },
      updated_at_unix_ms: { type: 'integer' },
    },
    required: [
      'view_id',
      'scope',
      'project_id',
      'name',
      'config',
      'sort_order',
      'created_at_unix_ms',
      'updated_at_unix_ms',
    ],
  },
  CreateProjectBoardViewRequest: {
    type: 'object',
    properties: {
      scope: { type: 'string', enum: [...VIEW_SCOPES], nullable: true },
      project_id: { type: 'string', format: 'uuid', nullable: true },
      name: { type: 'string' },
      config: nullableRef('ProjectBoardViewConfig'),
      sort_order: { type: 'integer', nullable: true },
    },
    required: ['name'],
    additionalProperties: false,
  },
  UpdateProjectBoardViewRequest: {
    type: 'object',
    properties: {
      name: { type: 'string', nullable: true },
      config: nullableRef('ProjectBoardViewConfig'),
      sort_order: { type: 'integer', nullable: true },
    },
    additionalProperties: false,
  },
  ProjectBoardProjectSummaryItem: {
    type: 'object',
    properties: {
      project_id: { type: 'string', format: 'uuid' },
      project_name: { type: 'string' },
      total_tasks: { type: 'integer' },
      in_progress_count: { type: 'integer' },
      in_review_count: { type: 'integer' },
      blocked_count: { type: 'integer' },
      conflict_count: { type: 'integer' },
      needs_action_count: { type: 'integer' },
    },
    required: [
      'project_id',
      'project_name',
      'total_tasks',
      'in_progress_count',
      'in_review_count',
      'blocked_count',
      'conflict_count',
      'needs_action_count',
    ],
  },
  ProjectBoardSummary: {
    type: 'object',
    properties: {
      total_tasks: { type: 'integer' },
      backlog_count: { type: 'integer' },
      ready_count: { type: 'integer' },
      in_progress_count: { type: 'integer' },
      in_review_count: { type: 'integer' },
      done_count: { type: 'integer' },
      blocked_count: { type: 'integer' },
      conflict_count: { type: 'integer' },
      active_run_count: { type: 'integer' },
      needs_action_count: { type: 'integer' },
      projects: { type: 'array', items: ref('ProjectBoardProjectSummaryItem') },
      updated_at_unix_ms: { type: 'integer' },
    },
    required: [
      'total_tasks',
      'backlog_count',
      'ready_count',
      'in_progress_count',
      'in_review_count',
      'done_count',
      'blocked_count',
      'conflict_count',
      'active_run_count',
      'needs_action_count',
      'projects',
      'updated_at_unix_ms',
    ],
  },
  ProjectBoardEvent: {
    type: 'object',
    properties: {
      seq: { type: 'integer' },
      event_type: { type: 'string', enum: [...BOARD_EVENT_TYPES] },
      project_id: { type: 'string', format: 'uuid', nullable: true },
      task_id: { type: 'string', format: 'uuid', nullable: true },
      task: nullableRef('ProjectBoardTask'),
      settings: nullableRef('ProjectBoardSettings'),
      view_id: { type: 'string', format: 'uuid', nullable: true },
      view: nullableRef('ProjectBoardView'),
      notes: nullableRef('ProjectNotes'),
      autopilot: nullableRef('ProjectAutopilotState'),
      occurred_at_unix_ms: { type: 'integer' },
    },
    required: ['seq', 'event_type', 'occurred_at_unix_ms'],
  },
  SpatialObjectKind: {
    type: 'string',
    enum: [...SPATIAL_OBJECT_KINDS],
  },
  SpatialLwwTimestamp: {
    type: 'object',
    properties: {
      lamport: { type: 'integer' },
      writer_id: { type: 'string' },
    },
    required: ['lamport', 'writer_id'],
  },
  SpatialBoardObject: {
    type: 'object',
    properties: {
      id: { type: 'string' },
      kind: { type: 'string', enum: [...SPATIAL_OBJECT_KINDS] },
      parent_id: { type: 'string', nullable: true },
      sort_key: { type: 'string' },
      x: { type: 'number' },
      y: { type: 'number' },
      width: { type: 'number' },
      height: { type: 'number' },
      rotation: { type: 'number' },
      props: { type: 'object', additionalProperties: true },
      prop_timestamps: { type: 'object', additionalProperties: ref('SpatialLwwTimestamp') },
      deleted: { type: 'boolean' },
      deleted_ts: nullableRef('SpatialLwwTimestamp'),
      updated_at_unix_ms: { type: 'integer' },
    },
    required: [
      'id',
      'kind',
      'parent_id',
      'sort_key',
      'x',
      'y',
      'width',
      'height',
      'rotation',
      'props',
      'prop_timestamps',
      'deleted',
      'updated_at_unix_ms',
    ],
  },
  SpatialPropertyOp: {
    type: 'object',
    properties: {
      client_op_id: { type: 'string' },
      object_id: { type: 'string' },
      kind: { type: 'string', enum: [...SPATIAL_OBJECT_KINDS], nullable: true },
      parent_id: { type: 'string', nullable: true },
      has_parent_id: { type: 'boolean' },
      sort_key: { type: 'string', nullable: true },
      x: { type: 'number', nullable: true },
      y: { type: 'number', nullable: true },
      width: { type: 'number', nullable: true },
      height: { type: 'number', nullable: true },
      rotation: { type: 'number', nullable: true },
      props: { type: 'object', additionalProperties: true, nullable: true },
      timestamps: { type: 'object', additionalProperties: ref('SpatialLwwTimestamp'), nullable: true },
      deleted: { type: 'boolean', nullable: true },
      deleted_ts: nullableRef('SpatialLwwTimestamp'),
    },
    required: ['client_op_id', 'object_id'],
  },
  SpatialBoardOpsRequest: {
    type: 'object',
    properties: {
      client_id: { type: 'string' },
      base_seq: { type: 'integer' },
      ops: { type: 'array', items: ref('SpatialPropertyOp') },
    },
    required: ['client_id', 'base_seq', 'ops'],
  },
  SpatialBoardRejectedOp: {
    type: 'object',
    properties: {
      client_op_id: { type: 'string' },
      object_id: { type: 'string' },
      code: { type: 'string' },
      message: { type: 'string' },
    },
    required: ['client_op_id', 'object_id', 'code', 'message'],
  },
  SpatialBoardOpsResponse: {
    type: 'object',
    properties: {
      seq: { type: 'integer' },
      accepted_op_ids: { type: 'array', items: { type: 'string' } },
      rejected_ops: { type: 'array', items: ref('SpatialBoardRejectedOp') },
      objects: { type: 'object', additionalProperties: ref('SpatialBoardObject') },
    },
    required: ['seq', 'accepted_op_ids', 'rejected_ops', 'objects'],
  },
  SpatialBoardSnapshot: {
    type: 'object',
    properties: {
      session_id: { type: 'string', format: 'uuid' },
      seq: { type: 'integer' },
      objects: { type: 'object', additionalProperties: ref('SpatialBoardObject') },
      comments: { type: 'array', items: { type: 'object', additionalProperties: true } },
      collaborators: { type: 'array', items: { type: 'object', additionalProperties: true } },
      updated_at_unix_ms: { type: 'integer' },
    },
    required: ['session_id', 'seq', 'objects', 'comments', 'collaborators', 'updated_at_unix_ms'],
  },
  PromoteFromSpatialRequest: {
    type: 'object',
    properties: {
      session_id: { type: 'string', format: 'uuid' },
      object_ids: { type: 'array', items: { type: 'string' }, minItems: 1 },
      mode: { type: 'string', enum: [...SPATIAL_PROMOTION_MODES] },
      target_status: { type: 'string', enum: [...TASK_STATUSES] },
      priority: { type: 'string', nullable: true, enum: [...TASK_PRIORITIES, null] },
      assignee: nullableRef('ProjectBoardAssignee'),
      labels: { type: 'array', items: { type: 'string' }, nullable: true },
      viewport_hint: { type: 'object', additionalProperties: true, nullable: true },
      writer_id: { type: 'string', nullable: true },
    },
    required: ['session_id', 'object_ids'],
    additionalProperties: false,
  },
  SpatialObjectTaskLink: {
    type: 'object',
    properties: {
      object_id: { type: 'string' },
      task_id: { type: 'string', format: 'uuid' },
      task_key: { type: 'string' },
    },
    required: ['object_id', 'task_id', 'task_key'],
  },
  PromoteFromSpatialResult: {
    type: 'object',
    properties: {
      mode: { type: 'string', enum: [...SPATIAL_PROMOTION_MODES] },
      parent_task_id: { type: 'string', format: 'uuid', nullable: true },
      tasks: { type: 'array', items: ref('ProjectBoardTask') },
      updated_object_links: { type: 'array', items: ref('SpatialObjectTaskLink') },
      spatial_seq: { type: 'integer' },
    },
    required: ['mode', 'parent_task_id', 'tasks', 'updated_object_links', 'spatial_seq'],
  },
  ProjectNotes: {
    type: 'object',
    properties: {
      project_id: { type: 'string', format: 'uuid' },
      content_md: { type: 'string' },
      resolved_file_path: { type: 'string' },
      storage_mode: { type: 'string', enum: [...STORAGE_MODES] },
      revision: { type: 'integer' },
      last_synced_hash: { type: 'string' },
      updated_at_unix_ms: { type: 'integer' },
    },
    required: [
      'project_id',
      'content_md',
      'resolved_file_path',
      'storage_mode',
      'revision',
      'last_synced_hash',
      'updated_at_unix_ms',
    ],
  },
  UpdateProjectNotesRequest: {
    type: 'object',
    properties: {
      content_md: { type: 'string' },
      expected_revision: { type: 'integer', nullable: true },
    },
    required: ['content_md'],
    additionalProperties: false,
  },
  CoordinatorTaskDraft: {
    type: 'object',
    properties: {
      draft_id: { type: 'string' },
      title: { type: 'string' },
      description_md: { type: 'string' },
      checklist: { type: 'array', items: ref('ProjectBoardChecklistItem') },
      priority: { type: 'string', enum: [...TASK_PRIORITIES] },
      status: { type: 'string', enum: [...TASK_STATUSES] },
      assignee: nullableRef('ProjectBoardAssignee'),
      labels: { type: 'array', items: { type: 'string' } },
      depends_on_draft_ids: { type: 'array', items: { type: 'string' } },
      depends_on_task_ids: { type: 'array', items: { type: 'string', format: 'uuid' } },
    },
    required: [
      'draft_id',
      'title',
      'description_md',
      'checklist',
      'priority',
      'status',
      'assignee',
      'labels',
      'depends_on_draft_ids',
      'depends_on_task_ids',
    ],
  },
  CoordinatorPlanRequest: {
    type: 'object',
    properties: {
      goal: { type: 'string' },
      default_status: { type: 'string', enum: [...TASK_STATUSES], nullable: true },
      default_assignee: nullableRef('ProjectBoardAssignee'),
      auto_apply: { type: 'boolean' },
      max_tasks: { type: 'integer', nullable: true },
    },
    required: ['goal'],
    additionalProperties: false,
  },
  CoordinatorPlanResult: {
    type: 'object',
    properties: {
      project_id: { type: 'string', format: 'uuid' },
      goal: { type: 'string' },
      summary: { type: 'string' },
      planner_source: { type: 'string', enum: [...PLANNER_SOURCES] },
      auto_applied: { type: 'boolean' },
      drafts: { type: 'array', items: ref('CoordinatorTaskDraft') },
      created_tasks: { type: 'array', items: ref('ProjectBoardTask') },
    },
    required: [
      'project_id',
      'goal',
      'summary',
      'planner_source',
      'auto_applied',
      'drafts',
      'created_tasks',
    ],
  },
  CoordinatorApplyPlanRequest: {
    type: 'object',
    properties: {
      goal: { type: 'string', nullable: true },
      update_notes: { type: 'boolean', nullable: true },
      drafts: { type: 'array', items: ref('CoordinatorTaskDraft') },
    },
    required: ['drafts'],
    additionalProperties: false,
  },
  CoordinatorApplyPlanResult: {
    type: 'object',
    properties: {
      project_id: { type: 'string', format: 'uuid' },
      created_tasks: { type: 'array', items: ref('ProjectBoardTask') },
    },
    required: ['project_id', 'created_tasks'],
  },
  UpdateProjectAutopilotRequest: {
    type: 'object',
    properties: {
      autopilot_enabled: { type: 'boolean', nullable: true },
      max_concurrency: { type: 'integer', nullable: true },
      cancel_active: { type: 'boolean', nullable: true },
    },
    additionalProperties: false,
  },
  ProjectAutopilotState: {
    type: 'object',
    properties: {
      project_id: { type: 'string', format: 'uuid' },
      autopilot_enabled: { type: 'boolean' },
      max_concurrency: { type: 'integer' },
      active_task_count: { type: 'integer' },
      active_task_ids: { type: 'array', items: { type: 'string', format: 'uuid' } },
      queued_ready_count: { type: 'integer' },
      blocked_task_count: { type: 'integer' },
      blocked_tasks: { type: 'array', items: ref('ProjectBoardTask') },
      last_tick_at_unix_ms: { type: 'integer', nullable: true },
      last_error: { type: 'string', nullable: true },
      updated_at_unix_ms: { type: 'integer' },
    },
    required: [
      'project_id',
      'autopilot_enabled',
      'max_concurrency',
      'active_task_count',
      'active_task_ids',
      'queued_ready_count',
      'blocked_task_count',
      'blocked_tasks',
      'last_tick_at_unix_ms',
      'last_error',
      'updated_at_unix_ms',
    ],
  },
};

function keepStreamOpen(server: unknown, request: Request) {
  try {
    (server as { timeout?(req: Request, sec: number): void } | null)?.timeout?.(request, 0);
  } catch {
    // app.handle() in unit tests runs without a live Bun server socket.
  }
}

type JsonRouteSpec = {
  method: 'GET' | 'PUT' | 'POST' | 'PATCH' | 'DELETE';
  path: string;
  openApiPath: string;
  responseSchema?: string;
  responseIsArray?: boolean;
  noContent?: boolean;
  createdStatus?: boolean;
  bodySchema?: string;
  bodyRequired?: boolean;
  errorResponses?: Record<number, string>;
};

function registerSseRoute(
  app: AnyElysia,
  path: string,
  openApiPath: string,
  description: string,
  forwardHeaders: (request: Request) => Record<string, string>,
) {
  app.route(
    'GET',
    path,
    async (context) => {
      const request = context.request as Request;
      keepStreamOpen(context.server, request);
      const url = new URL(request.url);
      const headers = forwardHeaders(request);
      const cursor =
        url.searchParams.get('after_seq') ??
        url.searchParams.get('cursor') ??
        request.headers.get('last-event-id') ??
        request.headers.get('Last-Event-ID');

      const search = new URLSearchParams(url.searchParams);
      if (cursor && !search.has('after_seq')) {
        search.set('after_seq', cursor);
      }
      const queryStorageId = url.searchParams.get('storage_id')?.trim();
      const suffix = search.toString() ? `?${search.toString()}` : '';

      const response = await proxySse(`${url.pathname}${suffix}`, {
        headers: {
          ...headers,
          ...(queryStorageId && !headers['x-tinadec-storage-id']
            ? { 'x-tinadec-storage-id': queryStorageId }
            : {}),
          ...(cursor ? { 'last-event-id': cursor } : {}),
        },
        signal: request.signal,
      });

      if (response.status >= 400) {
        return response;
      }

      const set = context.set as { headers: Record<string, string | number>; status?: number | string };
      set.status = response.status;
      set.headers['content-type'] = response.headers.get('content-type') ?? 'text/event-stream';
      set.headers['cache-control'] = 'no-cache, no-transform';
      set.headers['x-accel-buffering'] = 'no';

      return createSseResponse(response, request.signal, set.headers);
    },
    {
      detail: {
        summary: `GET ${openApiPath}`,
        tags: ['Projects'],
        description,
        responses: {
          200: {
            description,
            content: { 'text/event-stream': { schema: { type: 'string' } } },
          },
        },
      } as never,
    },
  );
}

/**
 * Task #54 (Phase 1): Project Hub, Composable Project/Global Board, Saved Views,
 * `.tinadec/` Sync, Conflict Resolution, Execution Dispatch, Diff Inspection, and SSE Streams.
 */
export function registerProjectBoardRoutes(
  app: AnyElysia,
  forwardHeaders: (request: Request) => Record<string, string>,
) {
  // Register SSE routes before parameterized sub-routes so path matching stays unambiguous.
  registerSseRoute(
    app,
    '/api/v1/projects/:projectId/board/events',
    '/api/v1/projects/{projectId}/board/events',
    'Unbuffered SSE stream for single-project board events (snapshot, task_upserted, task_deleted, settings_updated, view_upserted, view_deleted, sync_completed).',
    forwardHeaders,
  );

  registerSseRoute(
    app,
    '/api/v1/board/events',
    '/api/v1/board/events',
    'Unbuffered SSE stream for cross-project global board events with Last-Event-ID / ?after_seq resume.',
    forwardHeaders,
  );

  const routes: JsonRouteSpec[] = [
    // Project settings & sync
    {
      method: 'GET',
      path: '/api/v1/projects/:projectId/board/settings',
      openApiPath: '/api/v1/projects/{projectId}/board/settings',
      responseSchema: 'ProjectBoardSettings',
    },
    {
      method: 'PUT',
      path: '/api/v1/projects/:projectId/board/settings',
      openApiPath: '/api/v1/projects/{projectId}/board/settings',
      bodySchema: 'UpdateProjectBoardSettingsRequest',
      bodyRequired: true,
      responseSchema: 'ProjectBoardSettings',
    },
    {
      method: 'POST',
      path: '/api/v1/projects/:projectId/board/sync',
      openApiPath: '/api/v1/projects/{projectId}/board/sync',
      responseSchema: 'ProjectBoardSyncResult',
    },
    // Phase 3 (#56): Project living document (`notes.md`), Coordinator planner, and Autopilot
    {
      method: 'GET',
      path: '/api/v1/projects/:projectId/notes',
      openApiPath: '/api/v1/projects/{projectId}/notes',
      responseSchema: 'ProjectNotes',
      errorResponses: {
        400: 'Invalid project notes request',
        404: 'Project not found',
        502: 'Upstream Core unreachable',
      },
    },
    {
      method: 'PUT',
      path: '/api/v1/projects/:projectId/notes',
      openApiPath: '/api/v1/projects/{projectId}/notes',
      bodySchema: 'UpdateProjectNotesRequest',
      bodyRequired: true,
      responseSchema: 'ProjectNotes',
      errorResponses: {
        400: 'Invalid project notes update request',
        404: 'Project not found',
        409: 'Notes revision conflict',
        502: 'Upstream Core unreachable',
      },
    },
    {
      method: 'POST',
      path: '/api/v1/projects/:projectId/board/coordinator/plan',
      openApiPath: '/api/v1/projects/{projectId}/board/coordinator/plan',
      bodySchema: 'CoordinatorPlanRequest',
      bodyRequired: true,
      responseSchema: 'CoordinatorPlanResult',
      errorResponses: {
        400: 'Invalid coordinator plan request',
        404: 'Project not found',
        409: 'Dependency cycle or revision conflict',
        502: 'Upstream Core unreachable',
      },
    },
    {
      method: 'POST',
      path: '/api/v1/projects/:projectId/board/coordinator/apply-plan',
      openApiPath: '/api/v1/projects/{projectId}/board/coordinator/apply-plan',
      bodySchema: 'CoordinatorApplyPlanRequest',
      bodyRequired: true,
      responseSchema: 'CoordinatorApplyPlanResult',
      errorResponses: {
        400: 'Invalid coordinator apply-plan request',
        404: 'Project not found',
        409: 'Dependency cycle detected',
        502: 'Upstream Core unreachable',
      },
    },
    {
      method: 'GET',
      path: '/api/v1/projects/:projectId/board/coordinator/autopilot',
      openApiPath: '/api/v1/projects/{projectId}/board/coordinator/autopilot',
      responseSchema: 'ProjectAutopilotState',
      errorResponses: {
        400: 'Invalid autopilot request',
        404: 'Project not found',
        502: 'Upstream Core unreachable',
      },
    },
    {
      method: 'PUT',
      path: '/api/v1/projects/:projectId/board/coordinator/autopilot',
      openApiPath: '/api/v1/projects/{projectId}/board/coordinator/autopilot',
      bodySchema: 'UpdateProjectAutopilotRequest',
      bodyRequired: true,
      responseSchema: 'ProjectAutopilotState',
      errorResponses: {
        400: 'Invalid autopilot update request',
        404: 'Project not found',
        409: 'Autopilot state conflict',
        502: 'Upstream Core unreachable',
      },
    },
    // Project task actions (registered before /tasks/:taskId)
    {
      method: 'POST',
      path: '/api/v1/projects/:projectId/board/tasks/promote-from-spatial',
      openApiPath: '/api/v1/projects/{projectId}/board/tasks/promote-from-spatial',
      bodySchema: 'PromoteFromSpatialRequest',
      bodyRequired: true,
      responseSchema: 'PromoteFromSpatialResult',
      errorResponses: {
        400: 'Invalid spatial promotion request',
        404: 'Project, spatial session, or spatial object not found',
        409: 'Dependency cycle or revision conflict',
        502: 'Upstream Core unreachable',
      },
    },
    {
      method: 'POST',
      path: '/api/v1/projects/:projectId/board/tasks/:taskId/resolve-conflict',
      openApiPath: '/api/v1/projects/{projectId}/board/tasks/{taskId}/resolve-conflict',
      bodySchema: 'ResolveProjectBoardConflictRequest',
      bodyRequired: true,
      responseSchema: 'ProjectBoardTask',
    },
    {
      method: 'POST',
      path: '/api/v1/projects/:projectId/board/tasks/:taskId/dispatch',
      openApiPath: '/api/v1/projects/{projectId}/board/tasks/{taskId}/dispatch',
      bodySchema: 'DispatchProjectBoardTaskRequest',
      bodyRequired: false,
      responseSchema: 'ProjectBoardTask',
    },
    {
      method: 'GET',
      path: '/api/v1/projects/:projectId/board/tasks/:taskId/diff',
      openApiPath: '/api/v1/projects/{projectId}/board/tasks/{taskId}/diff',
      responseSchema: 'ProjectBoardTaskDiff',
    },
    {
      method: 'POST',
      path: '/api/v1/projects/:projectId/board/tasks/:taskId/approve',
      openApiPath: '/api/v1/projects/{projectId}/board/tasks/{taskId}/approve',
      bodySchema: 'ApproveProjectBoardTaskRequest',
      bodyRequired: false,
      responseSchema: 'ProjectBoardTask',
    },
    {
      method: 'POST',
      path: '/api/v1/projects/:projectId/board/tasks/:taskId/request-changes',
      openApiPath: '/api/v1/projects/{projectId}/board/tasks/{taskId}/request-changes',
      bodySchema: 'RequestTaskChangesRequest',
      bodyRequired: true,
      responseSchema: 'ProjectBoardTask',
    },
    {
      method: 'POST',
      path: '/api/v1/projects/:projectId/board/tasks/:taskId/cancel',
      openApiPath: '/api/v1/projects/{projectId}/board/tasks/{taskId}/cancel',
      responseSchema: 'ProjectBoardTask',
    },
    // Project tasks CRUD
    {
      method: 'GET',
      path: '/api/v1/projects/:projectId/board/tasks',
      openApiPath: '/api/v1/projects/{projectId}/board/tasks',
      responseSchema: 'ProjectBoardTask',
      responseIsArray: true,
    },
    {
      method: 'POST',
      path: '/api/v1/projects/:projectId/board/tasks',
      openApiPath: '/api/v1/projects/{projectId}/board/tasks',
      bodySchema: 'CreateProjectBoardTaskRequest',
      bodyRequired: true,
      responseSchema: 'ProjectBoardTask',
      createdStatus: true,
    },
    {
      method: 'GET',
      path: '/api/v1/projects/:projectId/board/tasks/:taskId',
      openApiPath: '/api/v1/projects/{projectId}/board/tasks/{taskId}',
      responseSchema: 'ProjectBoardTask',
    },
    {
      method: 'PATCH',
      path: '/api/v1/projects/:projectId/board/tasks/:taskId',
      openApiPath: '/api/v1/projects/{projectId}/board/tasks/{taskId}',
      bodySchema: 'UpdateProjectBoardTaskRequest',
      bodyRequired: true,
      responseSchema: 'ProjectBoardTask',
    },
    {
      method: 'DELETE',
      path: '/api/v1/projects/:projectId/board/tasks/:taskId',
      openApiPath: '/api/v1/projects/{projectId}/board/tasks/{taskId}',
      noContent: true,
    },
    // Global board & saved views
    {
      method: 'GET',
      path: '/api/v1/board/tasks',
      openApiPath: '/api/v1/board/tasks',
      responseSchema: 'ProjectBoardTask',
      responseIsArray: true,
    },
    {
      method: 'GET',
      path: '/api/v1/board/summary',
      openApiPath: '/api/v1/board/summary',
      responseSchema: 'ProjectBoardSummary',
    },
    {
      method: 'GET',
      path: '/api/v1/board/views',
      openApiPath: '/api/v1/board/views',
      responseSchema: 'ProjectBoardView',
      responseIsArray: true,
    },
    {
      method: 'POST',
      path: '/api/v1/board/views',
      openApiPath: '/api/v1/board/views',
      bodySchema: 'CreateProjectBoardViewRequest',
      bodyRequired: true,
      responseSchema: 'ProjectBoardView',
      createdStatus: true,
    },
    {
      method: 'PATCH',
      path: '/api/v1/board/views/:viewId',
      openApiPath: '/api/v1/board/views/{viewId}',
      bodySchema: 'UpdateProjectBoardViewRequest',
      bodyRequired: true,
      responseSchema: 'ProjectBoardView',
    },
    {
      method: 'DELETE',
      path: '/api/v1/board/views/:viewId',
      openApiPath: '/api/v1/board/views/{viewId}',
      noContent: true,
    },
  ];

  for (const spec of routes) {
    const schemaPayload = spec.responseSchema
      ? spec.responseIsArray
        ? { type: 'array', items: ref(spec.responseSchema) }
        : ref(spec.responseSchema)
      : undefined;

    const responses: Record<number, unknown> = spec.noContent
      ? { 204: { description: 'Deleted successfully' } }
      : {
          200: {
            description: 'Core-owned Project Board response',
            content: { 'application/json': { schema: schemaPayload } },
          },
          ...(spec.createdStatus
            ? {
                201: {
                  description: 'Created resource',
                  content: { 'application/json': { schema: schemaPayload } },
                },
              }
            : {}),
          ...(spec.errorResponses
            ? Object.fromEntries(
                Object.entries(spec.errorResponses).map(([status, description]) => [
                  Number(status),
                  {
                    description,
                    content: { 'application/problem+json': { schema: ref('ProblemDetails') } },
                  },
                ]),
              )
            : {}),
        };

    app.route(
      spec.method,
      spec.path,
      async (context) => {
        const request = context.request as Request;
        const url = new URL(request.url);
        const isBodylessMethod = spec.method === 'GET' || spec.method === 'DELETE';
        const rawBody = isBodylessMethod
          ? undefined
          : context.body === undefined || context.body === null
            ? spec.bodySchema
              ? '{}'
              : undefined
            : typeof context.body === 'string'
              ? context.body.length > 0
                ? context.body
                : spec.bodySchema
                  ? '{}'
                  : undefined
              : JSON.stringify(context.body);

        return proxyRaw(url.pathname + url.search, {
          method: spec.method,
          headers: forwardHeaders(request),
          body: rawBody,
          signal: request.signal,
        });
      },
      {
        detail: {
          summary: `${spec.method} ${spec.openApiPath}`,
          tags: ['Projects'],
          ...(spec.bodySchema
            ? {
                requestBody: {
                  required: spec.bodyRequired ?? true,
                  content: { 'application/json': { schema: ref(spec.bodySchema) } },
                },
              }
            : {}),
          responses,
        } as never,
      },
    );
  }
}
