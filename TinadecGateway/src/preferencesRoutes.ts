import type { AnyElysia } from 'elysia';
import { proxyJson, proxySse } from './coreClient.js';
import {
  externalJsonRequest,
  externalJsonResponse,
  UI_PREFERENCES_ACCENTS,
  UI_PREFERENCES_PANEL_EFFECTS,
  UI_PREFERENCES_PANEL_PRESETS,
  UI_PREFERENCES_REDUCED_MOTIONS,
  UI_PREFERENCES_SPATIAL_PROMOTE_MODES,
  UI_PREFERENCES_THEMES,
  UI_PREFERENCES_WORKSPACE_DROP_ACTIONS,
  UI_PREFERENCES_WORKSPACE_FOLDER_TONES,
  UI_PREFERENCES_WORKSPACE_VIEWS,
  uiPreferencesOpenApiSchemas,
} from './externalDtoOpenApi.js';
import { ensureRequestId, PRINCIPAL_VALUE } from './headers.js';
import { mapCoreErrorToExternal, toProblemDetails } from './mappers/errorMapper.js';
import { createSseResponse, setStreamHeaders } from './streaming.js';

export { uiPreferencesOpenApiSchemas as preferencesSchemas };

const THEME_SET = new Set<string>(UI_PREFERENCES_THEMES);
const ACCENT_SET = new Set<string>(UI_PREFERENCES_ACCENTS);
const PANEL_EFFECT_SET = new Set<string>(UI_PREFERENCES_PANEL_EFFECTS);
const PANEL_PRESET_SET = new Set<string>(UI_PREFERENCES_PANEL_PRESETS);
const WORKSPACE_VIEW_SET = new Set<string>(UI_PREFERENCES_WORKSPACE_VIEWS);
const WORKSPACE_FOLDER_TONE_SET = new Set<string>(UI_PREFERENCES_WORKSPACE_FOLDER_TONES);
const WORKSPACE_DROP_ACTION_SET = new Set<string>(UI_PREFERENCES_WORKSPACE_DROP_ACTIONS);
const SPATIAL_PROMOTE_MODE_SET = new Set<string>(UI_PREFERENCES_SPATIAL_PROMOTE_MODES);
const REDUCED_MOTION_SET = new Set<string>(UI_PREFERENCES_REDUCED_MOTIONS);

const PANEL_PRESET_DEFAULT_EFFECT: Record<string, string> = {
  solid: 'solid',
  frosted: 'blur',
  crystal: 'blur',
  mica: 'blur',
  translucent: 'translucent',
};

const PUT_ROOT_KEYS = new Set(['expected_content_hash', 'reset_defaults', 'appearance', 'interaction']);
const PATCH_ROOT_KEYS = new Set(['expected_content_hash', 'appearance', 'interaction']);
const APPEARANCE_REQUIRED_PUT_KEYS = new Set([
  'theme',
  'accent',
  'custom_accent_hex',
  'panel_effect',
  'workspace_view',
  'workspace_folder_tone',
]);
const APPEARANCE_KEYS = new Set([
  ...APPEARANCE_REQUIRED_PUT_KEYS,
  'panel_preset',
  'panel_opacity',
  'panel_blur',
  'panel_saturation',
]);
const INTERACTION_KEYS = new Set([
  'workspace_drop_action',
  'spatial_promote_mode',
  'reduced_motion',
]);

const HEX_COLOR_RE = /^#[0-9a-fA-F]{6}$/;

const SPATIAL_PROMOTE_MODE_ALIASES: Record<string, string> = {
  merge_single: 'merge',
  split_many: 'split',
  parent_with_children: 'parent_children',
};

function keepStreamOpen(server: unknown, request: Request) {
  try {
    (server as { timeout?(req: Request, sec: number): void } | null)?.timeout?.(request, 0);
  } catch {
    // app.handle() in unit tests runs without a live Bun server socket.
  }
}

function setProxyHeaders(
  set: { headers: Record<string, string | number>; status?: number | string },
  status: number,
  requestId: string,
  responseHeaders?: Headers,
) {
  set.status = status;
  set.headers['x-request-id'] = requestId;
  set.headers['x-tinadec-principal'] = PRINCIPAL_VALUE;
  const etag = responseHeaders?.get('etag');
  if (etag) {
    set.headers.etag = etag;
  }
}

function problemResponse(description: string) {
  return {
    description,
    content: {
      'application/problem+json': {
        schema: { $ref: '#/components/schemas/ProblemDetails' },
      },
    },
  } as const;
}

function isPlainRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

type ValidationResult =
  | { ok: true; body: Record<string, unknown> }
  | { ok: false; detail: string };

function validateAppearanceSection(
  raw: unknown,
  requireAllFields: boolean,
): { ok: true; value: Record<string, unknown> } | { ok: false; detail: string } {
  if (!isPlainRecord(raw)) {
    return { ok: false, detail: 'appearance must be a JSON object.' };
  }
  for (const key of Object.keys(raw)) {
    if (!APPEARANCE_KEYS.has(key)) {
      return { ok: false, detail: `Unknown appearance preference key "${key}".` };
    }
  }
  if (requireAllFields) {
    for (const requiredKey of APPEARANCE_REQUIRED_PUT_KEYS) {
      if (raw[requiredKey] === undefined || raw[requiredKey] === null) {
        if (
          requiredKey === 'panel_effect'
          && typeof raw.panel_preset === 'string'
          && raw.panel_preset in PANEL_PRESET_DEFAULT_EFFECT
        ) {
          continue;
        }
        return { ok: false, detail: `Missing required appearance preference "${requiredKey}".` };
      }
    }
  }

  const normalized: Record<string, unknown> = {};

  if ('theme' in raw) {
    const v = raw.theme;
    if (v === null || v === undefined) {
      if (requireAllFields) return { ok: false, detail: 'appearance.theme is required.' };
      if (v === null) normalized.theme = null;
    } else if (typeof v !== 'string' || !THEME_SET.has(v)) {
      return { ok: false, detail: 'Invalid appearance.theme value.' };
    } else {
      normalized.theme = v;
    }
  }

  if ('accent' in raw) {
    const v = raw.accent;
    if (v === null || v === undefined) {
      if (requireAllFields) return { ok: false, detail: 'appearance.accent is required.' };
      if (v === null) normalized.accent = null;
    } else if (typeof v !== 'string' || !ACCENT_SET.has(v)) {
      return { ok: false, detail: 'Invalid appearance.accent value.' };
    } else {
      normalized.accent = v;
    }
  }

  if ('custom_accent_hex' in raw) {
    const v = raw.custom_accent_hex;
    if (v === null || v === undefined) {
      if (requireAllFields) return { ok: false, detail: 'appearance.custom_accent_hex is required.' };
      if (v === null) normalized.custom_accent_hex = null;
    } else if (typeof v !== 'string' || !HEX_COLOR_RE.test(v)) {
      return { ok: false, detail: 'Invalid appearance.custom_accent_hex value; expected #RRGGBB.' };
    } else {
      normalized.custom_accent_hex = v.toLowerCase();
    }
  }

  if ('panel_effect' in raw) {
    const v = raw.panel_effect;
    if (v === null || v === undefined) {
      if (requireAllFields) {
        if (typeof raw.panel_preset === 'string' && raw.panel_preset in PANEL_PRESET_DEFAULT_EFFECT) {
          normalized.panel_effect = PANEL_PRESET_DEFAULT_EFFECT[raw.panel_preset];
        } else {
          return { ok: false, detail: 'appearance.panel_effect is required.' };
        }
      } else if (v === null) {
        normalized.panel_effect = null;
      }
    } else if (typeof v !== 'string') {
      return { ok: false, detail: 'Invalid appearance.panel_effect value.' };
    } else {
      const mapped = v === 'opaque' ? 'solid' : v;
      if (!PANEL_EFFECT_SET.has(mapped)) {
        return { ok: false, detail: 'Invalid appearance.panel_effect value.' };
      }
      normalized.panel_effect = mapped;
    }
  } else if (
    requireAllFields
    && typeof raw.panel_preset === 'string'
    && raw.panel_preset in PANEL_PRESET_DEFAULT_EFFECT
  ) {
    normalized.panel_effect = PANEL_PRESET_DEFAULT_EFFECT[raw.panel_preset];
  }

  if ('panel_preset' in raw) {
    const v = raw.panel_preset;
    if (v === null || v === undefined) {
      if (v === null) normalized.panel_preset = null;
    } else if (typeof v !== 'string' || !PANEL_PRESET_SET.has(v)) {
      return { ok: false, detail: 'Invalid appearance.panel_preset value.' };
    } else {
      normalized.panel_preset = v;
    }
  }

  if ('panel_opacity' in raw) {
    const v = raw.panel_opacity;
    if (v === null || v === undefined) {
      if (v === null) normalized.panel_opacity = null;
    } else if (typeof v !== 'number' || !Number.isInteger(v) || v < 0 || v > 100) {
      return { ok: false, detail: 'Invalid appearance.panel_opacity value; expected integer 0..100.' };
    } else {
      normalized.panel_opacity = v;
    }
  }

  if ('panel_blur' in raw) {
    const v = raw.panel_blur;
    if (v === null || v === undefined) {
      if (v === null) normalized.panel_blur = null;
    } else if (typeof v !== 'number' || !Number.isInteger(v) || v < 0 || v <= -1 || v > 32) {
      return { ok: false, detail: 'Invalid appearance.panel_blur value; expected integer 0..32.' };
    } else {
      normalized.panel_blur = v;
    }
  }

  if ('panel_saturation' in raw) {
    const v = raw.panel_saturation;
    if (v === null || v === undefined) {
      if (v === null) normalized.panel_saturation = null;
    } else if (typeof v !== 'number' || !Number.isInteger(v) || v < 100 || v > 200) {
      return { ok: false, detail: 'Invalid appearance.panel_saturation value; expected integer 100..200.' };
    } else {
      normalized.panel_saturation = v;
    }
  }

  if ('workspace_view' in raw) {
    const v = raw.workspace_view;
    if (v === null || v === undefined) {
      if (requireAllFields) return { ok: false, detail: 'appearance.workspace_view is required.' };
      if (v === null) normalized.workspace_view = null;
    } else if (typeof v !== 'string' || !WORKSPACE_VIEW_SET.has(v)) {
      return { ok: false, detail: 'Invalid appearance.workspace_view value.' };
    } else {
      normalized.workspace_view = v;
    }
  }

  if ('workspace_folder_tone' in raw) {
    const v = raw.workspace_folder_tone;
    if (v === null || v === undefined) {
      if (requireAllFields) return { ok: false, detail: 'appearance.workspace_folder_tone is required.' };
      if (v === null) normalized.workspace_folder_tone = null;
    } else if (typeof v !== 'string' || !WORKSPACE_FOLDER_TONE_SET.has(v)) {
      return { ok: false, detail: 'Invalid appearance.workspace_folder_tone value.' };
    } else {
      normalized.workspace_folder_tone = v;
    }
  }

  return { ok: true, value: normalized };
}

function validateInteractionSection(
  raw: unknown,
  requireAllFields: boolean,
): { ok: true; value: Record<string, unknown> } | { ok: false; detail: string } {
  if (!isPlainRecord(raw)) {
    return { ok: false, detail: 'interaction must be a JSON object.' };
  }
  for (const key of Object.keys(raw)) {
    if (!INTERACTION_KEYS.has(key)) {
      return { ok: false, detail: `Unknown interaction preference key "${key}".` };
    }
  }
  if (requireAllFields) {
    for (const requiredKey of INTERACTION_KEYS) {
      if (raw[requiredKey] === undefined || raw[requiredKey] === null) {
        return { ok: false, detail: `Missing required interaction preference "${requiredKey}".` };
      }
    }
  }

  const normalized: Record<string, unknown> = {};

  if ('workspace_drop_action' in raw) {
    const v = raw.workspace_drop_action;
    if (v === null || v === undefined) {
      if (requireAllFields) return { ok: false, detail: 'interaction.workspace_drop_action is required.' };
      if (v === null) normalized.workspace_drop_action = null;
    } else if (typeof v !== 'string' || !WORKSPACE_DROP_ACTION_SET.has(v)) {
      return { ok: false, detail: 'Invalid interaction.workspace_drop_action value.' };
    } else {
      normalized.workspace_drop_action = v;
    }
  }

  if ('spatial_promote_mode' in raw) {
    const v = raw.spatial_promote_mode;
    if (v === null || v === undefined) {
      if (requireAllFields) return { ok: false, detail: 'interaction.spatial_promote_mode is required.' };
      if (v === null) normalized.spatial_promote_mode = null;
    } else if (typeof v !== 'string') {
      return { ok: false, detail: 'Invalid interaction.spatial_promote_mode value.' };
    } else {
      const mapped = SPATIAL_PROMOTE_MODE_ALIASES[v] ?? v;
      if (!SPATIAL_PROMOTE_MODE_SET.has(mapped)) {
        return { ok: false, detail: 'Invalid interaction.spatial_promote_mode value.' };
      }
      normalized.spatial_promote_mode = mapped;
    }
  }

  if ('reduced_motion' in raw) {
    const v = raw.reduced_motion;
    if (v === null || v === undefined) {
      if (requireAllFields) return { ok: false, detail: 'interaction.reduced_motion is required.' };
      if (v === null) normalized.reduced_motion = null;
    } else if (typeof v !== 'string' || !REDUCED_MOTION_SET.has(v)) {
      return { ok: false, detail: 'Invalid interaction.reduced_motion value.' };
    } else {
      normalized.reduced_motion = v;
    }
  }

  return { ok: true, value: normalized };
}

export function validateAndNormalizePreferencesBody(
  method: 'PUT' | 'PATCH',
  rawBody: unknown,
): ValidationResult {
  if (!isPlainRecord(rawBody)) {
    return { ok: false, detail: 'Preferences request body must be a JSON object.' };
  }

  const allowedRootKeys = method === 'PUT' ? PUT_ROOT_KEYS : PATCH_ROOT_KEYS;
  for (const key of Object.keys(rawBody)) {
    if (!allowedRootKeys.has(key)) {
      return { ok: false, detail: `Unknown property "${key}" in preferences ${method} request.` };
    }
  }

  const normalized: Record<string, unknown> = {};

  if ('expected_content_hash' in rawBody && rawBody.expected_content_hash !== undefined) {
    const hash = rawBody.expected_content_hash;
    if (hash !== null && typeof hash !== 'string') {
      return { ok: false, detail: 'expected_content_hash must be a string or null.' };
    }
    normalized.expected_content_hash = hash;
  }

  let resetDefaults = false;
  if (method === 'PUT' && 'reset_defaults' in rawBody && rawBody.reset_defaults !== undefined) {
    if (rawBody.reset_defaults !== null && typeof rawBody.reset_defaults !== 'boolean') {
      return { ok: false, detail: 'reset_defaults must be a boolean.' };
    }
    if (typeof rawBody.reset_defaults === 'boolean') {
      resetDefaults = rawBody.reset_defaults;
      normalized.reset_defaults = resetDefaults;
    }
  }

  const requireFullSections = method === 'PUT' && !resetDefaults;
  if (requireFullSections) {
    if (rawBody.appearance === undefined || rawBody.appearance === null) {
      return { ok: false, detail: 'appearance section is required for PUT unless reset_defaults is true.' };
    }
    if (rawBody.interaction === undefined || rawBody.interaction === null) {
      return { ok: false, detail: 'interaction section is required for PUT unless reset_defaults is true.' };
    }
  }

  if ('appearance' in rawBody && rawBody.appearance !== undefined) {
    if (rawBody.appearance === null) {
      normalized.appearance = null;
    } else {
      const checked = validateAppearanceSection(rawBody.appearance, requireFullSections);
      if (!checked.ok) return checked;
      normalized.appearance = checked.value;
    }
  }

  if ('interaction' in rawBody && rawBody.interaction !== undefined) {
    if (rawBody.interaction === null) {
      normalized.interaction = null;
    } else {
      const checked = validateInteractionSection(rawBody.interaction, requireFullSections);
      if (!checked.ok) return checked;
      normalized.interaction = checked.value;
    }
  }

  return { ok: true, body: normalized };
}

async function readJsonRequestBody(
  request: Request,
  parsedBody: unknown,
): Promise<{ ok: true; value: unknown } | { ok: false; detail: string }> {
  if (parsedBody !== undefined) {
    if (typeof parsedBody === 'string') {
      const trimmed = parsedBody.trim();
      if (!trimmed) return { ok: false, detail: 'Preferences request body must not be empty.' };
      try {
        return { ok: true, value: JSON.parse(trimmed) };
      } catch {
        return { ok: false, detail: 'Preferences request body contains invalid JSON.' };
      }
    }
    return { ok: true, value: parsedBody };
  }

  const rawText = (await request.text()).trim();
  if (!rawText) {
    return { ok: false, detail: 'Preferences request body must not be empty.' };
  }
  try {
    return { ok: true, value: JSON.parse(rawText) };
  } catch {
    return { ok: false, detail: 'Preferences request body contains invalid JSON.' };
  }
}

/**
 * Task #57: Declarative User UI Preferences (`preferences.toml`) proxy routes & SSE hot-reload stream.
 */
export function registerPreferencesRoutes(
  app: AnyElysia,
  forwardHeaders: (request: Request) => Record<string, string>,
) {
  app.route(
    'GET',
    '/api/v1/preferences/events',
    async (context) => {
      const request = context.request as Request;
      keepStreamOpen(context.server, request);
      const url = new URL(request.url);
      const headers = forwardHeaders(request);
      const rid = ensureRequestId(headers['x-request-id'] ?? null);
      const cursor =
        url.searchParams.get('after_seq') ??
        url.searchParams.get('cursor') ??
        request.headers.get('last-event-id') ??
        request.headers.get('Last-Event-ID');
      const queryStorageId = url.searchParams.get('storage_id')?.trim();
      const suffix = url.search || '';

      const response = await proxySse(`/api/v1/preferences/events${suffix}`, {
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
      setStreamHeaders(set, response);
      set.headers['x-request-id'] = rid;
      set.headers['x-tinadec-principal'] = PRINCIPAL_VALUE;

      return createSseResponse(response, request.signal, set.headers);
    },
    {
      detail: {
        summary: 'Stream UI preferences updates',
        tags: ['System'],
        description:
          'Unbuffered SSE stream emitting preferences.updated events with UiPreferencesDocumentDto payloads and 15s keepalive comments.',
        responses: {
          200: {
            description: 'Unbuffered SSE stream of UI preferences updates (event: preferences.updated).',
            content: { 'text/event-stream': { schema: { type: 'string' } } },
          },
          502: problemResponse('Upstream Core unreachable.'),
        },
      } as never,
    },
  );

  app.route(
    'GET',
    '/api/v1/preferences',
    async (context) => {
      const request = context.request as Request;
      const set = context.set as { headers: Record<string, string | number>; status?: number | string };
      const url = new URL(request.url);
      const headers = forwardHeaders(request);
      const rid = ensureRequestId(headers['x-request-id'] ?? null);
      const path = '/api/v1/preferences';

      const result = await proxyJson(path + url.search, {
        headers,
        signal: request.signal,
      });

      setProxyHeaders(set, result.status, rid, result.headers);
      if (result.status >= 400) {
        set.headers['content-type'] = 'application/problem+json';
        return mapCoreErrorToExternal(result.status, result.data, path);
      }
      return result.data;
    },
    {
      detail: {
        summary: 'Get user UI preferences',
        tags: ['System'],
        responses: {
          200: externalJsonResponse(
            'UiPreferencesDocumentDto',
            'Current user-level UI preferences document with file path and content hash.',
          ),
          502: problemResponse('Upstream Core unreachable.'),
        },
      } as never,
    },
  );

  for (const method of ['PUT', 'PATCH'] as const) {
    const bodySchemaName =
      method === 'PUT' ? 'UiPreferencesPutRequestDto' : 'UiPreferencesPatchRequestDto';
    const summary =
      method === 'PUT'
        ? 'Replace or reset user UI preferences'
        : 'Partially update user UI preferences';

    app.route(
      method,
      '/api/v1/preferences',
      async (context) => {
        const request = context.request as Request;
        const set = context.set as { headers: Record<string, string | number>; status?: number | string };
        const url = new URL(request.url);
        const headers = forwardHeaders(request);
        const rid = ensureRequestId(headers['x-request-id'] ?? null);
        const path = '/api/v1/preferences';

        const parsed = await readJsonRequestBody(request, context.body);
        if (!parsed.ok) {
          setProxyHeaders(set, 400, rid);
          set.headers['content-type'] = 'application/problem+json';
          return toProblemDetails(400, 'invalid_request', parsed.detail, path, rid);
        }

        const validated = validateAndNormalizePreferencesBody(method, parsed.value);
        if (!validated.ok) {
          setProxyHeaders(set, 400, rid);
          set.headers['content-type'] = 'application/problem+json';
          return toProblemDetails(400, 'invalid_request', validated.detail, path, rid);
        }

        const result = await proxyJson(path + url.search, {
          method,
          body: validated.body,
          headers,
          signal: request.signal,
        });

        setProxyHeaders(set, result.status, rid, result.headers);
        if (result.status >= 400) {
          set.headers['content-type'] = 'application/problem+json';
          return mapCoreErrorToExternal(result.status, result.data, path);
        }
        return result.data;
      },
      {
        parse: 'none',
        detail: {
          summary,
          tags: ['System'],
          requestBody: externalJsonRequest(bodySchemaName),
          responses: {
            200: externalJsonResponse(
              'UiPreferencesDocumentDto',
              'Updated user-level UI preferences document.',
            ),
            400: problemResponse('Invalid UI preferences payload or unknown keys.'),
            409: problemResponse('Preferences content_hash concurrency conflict.'),
            412: problemResponse('Preferences precondition failed (expected_content_hash mismatch).'),
            502: problemResponse('Upstream Core unreachable.'),
          },
        } as never,
      },
    );
  }
}
