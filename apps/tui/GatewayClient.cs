using System.Text.Json;

namespace TinadecOffice.Tui;

/// <summary>
/// Gateway 健康检查客户端：GET /api/v1/health，契约与桌面 api.ts 同款
/// { name, status: "ok", version, time }。
///
/// TUI 与 Desktop 一样只是呈现面：只经 Gateway 访问、不缓存业务状态。
/// </summary>
internal sealed class GatewayClient
{
    private readonly HttpClient _http;

    public GatewayClient()
    {
        BaseUrl = Environment.GetEnvironmentVariable("TINADEC_GATEWAY_URL") ?? "http://127.0.0.1:48730";
        _http = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(3),
        };
    }

    /// <summary>Gateway 基地址（默认 127.0.0.1:48730，可用 TINADEC_GATEWAY_URL 覆盖）。</summary>
    public string BaseUrl { get; }

    public async Task<HealthResult> CheckHealthAsync(CancellationToken token = default)
    {
        try
        {
            using var response = await _http.GetAsync("/api/v1/health", token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            var root = doc.RootElement;
            string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            string status = root.TryGetProperty("status", out var s) ? s.GetString() ?? string.Empty : string.Empty;
            string version = root.TryGetProperty("version", out var v) ? v.GetString() ?? string.Empty : string.Empty;

            return new HealthResult(true, name, status, version, null);
        }
        catch (Exception ex) when (
            ex is HttpRequestException
                or TaskCanceledException
                or JsonException
                or InvalidOperationException)
        {
            return new HealthResult(false, null, null, null, ex.Message);
        }
    }

    public readonly record struct HealthResult(
        bool Ok,
        string? Name,
        string? Status,
        string? Version,
        string? Error);
}
