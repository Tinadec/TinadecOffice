/** Let the downstream server own framing and cancellation of the upstream body. */
export function createStreamProxyResponse(response: Response): Response {
  const headers = new Headers(response.headers);
  const connectionHeaders = headers.get('connection')?.split(',') ?? [];
  for (const name of [...connectionHeaders, 'connection', 'keep-alive', 'proxy-authenticate',
    'proxy-authorization', 'te', 'trailer', 'transfer-encoding', 'upgrade']) {
    if (name.trim()) headers.delete(name.trim());
  }
  // Elysia converts a Response with chunked transfer-encoding into another reader.
  // Passing only end-to-end headers keeps Fetch's own stream cancellation lifecycle.
  return new Response(response.body, { status: response.status, statusText: response.statusText, headers });
}
