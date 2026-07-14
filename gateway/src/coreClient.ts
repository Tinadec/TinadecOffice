export type ProxyBody = Record<string, unknown> | string | undefined;

export interface ProxyOptions {
  method?: string;
  body?: ProxyBody;
  headers?: HeadersInit;
}

export interface ProxyResult {
  status: number;
  data: unknown;
}

export const coreUrl = process.env.TINADEC_CORE_URL ?? 'http://127.0.0.1:48731';

export function coreEndpoint(path: string): string {
  return new URL(path, coreUrl).toString();
}

/**
 * Proxy a JSON request to TinadecCore.
 *
 * Returns a ProxyResult with the HTTP status and parsed JSON data.
 * If Core is unreachable or returns a non-JSON body, a 502 response
 * with a descriptive error object is returned instead of throwing.
 */
export async function proxyJson(path: string, options: ProxyOptions = {}): Promise<ProxyResult> {
  const body = typeof options.body === 'string'
    ? options.body
    : options.body === undefined
      ? undefined
      : JSON.stringify(options.body);

  let response: Response;
  try {
    response = await fetch(coreEndpoint(path), {
      method: options.method ?? 'GET',
      headers: {
        accept: 'application/json',
        ...(body ? { 'content-type': 'application/json' } : {}),
        ...options.headers
      },
      body
    });
  } catch (err) {
    const msg = err instanceof Error ? err.message : 'Network request failed';
    return {
      status: 502,
      data: {
        code: 'CORE_UNREACHABLE',
        message: `Cannot reach Core at ${coreUrl}: ${msg}`
      }
    };
  }

  const text = await response.text();
  let data: unknown = null;
  if (text.length > 0) {
    try {
      data = JSON.parse(text);
    } catch {
      return {
        status: 502,
        data: {
          code: 'CORE_INVALID_RESPONSE',
          message: `Core returned a non-JSON response: ${text.substring(0, 200)}`
        }
      };
    }
  }

  return {
    status: response.status,
    data
  };
}

export async function proxySse(path: string, init?: RequestInit): Promise<Response> {
  return fetch(coreEndpoint(path), {
    ...init,
    headers: {
      accept: 'text/event-stream',
      ...(init?.headers ?? {})
    }
  });
}
