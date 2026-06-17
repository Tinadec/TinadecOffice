using System.Diagnostics;

namespace TinadecModel.Tracing;

public static class ModelActivitySource
{
    public const string SourceName = "TinadecModel";
    public static readonly ActivitySource Instance = new(SourceName, "1.0.0");
}

public static class ModelSpanNames
{
    public const string ModelRequest = "model.request";
    public const string ModelRouteSelection = "model.route_selection";
    public const string ModelProviderInvocation = "model.provider_invocation";
    public const string AgentInference = "agent.inference";
}

public static class ModelSpanAttrs
{
    public const string ProviderId = "provider_id";
    public const string SessionId = "session_id";
    public const string RunId = "run_id";
    public const string Model = "model";
    public const string ProviderInstanceId = "provider_instance_id";
    public const string Driver = "driver";
    public const string ConnectionKind = "connection_kind";
    public const string StatusCode = "status_code";
    public const string Status = "status";
    public const string RoutePurpose = "route_purpose";
    public const string ErrorCategory = "error_category";
    public const string RetryCount = "retry_count";
    public const string FallbackProviderId = "fallback_provider_id";
    public const string HealthStatus = "health_status";
    public const string MessageCount = "message_count";
    public const string LatencyMs = "latency_ms";
    public const string BaseUrl = "base_url";
    public const string HasApiKey = "has_api_key";
}
