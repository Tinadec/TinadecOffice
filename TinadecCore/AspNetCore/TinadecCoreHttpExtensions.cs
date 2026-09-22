using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TinadecCore.Runtime;

namespace TinadecCore.AspNetCore;

/// <summary>
/// HTTP composition for embeddable TinadecCore hosting. Pair with
/// <c>AddTinadecPersistence</c> + <c>AddTinadecCore()</c> on the service side
/// and <c>MapTinadecCore()</c> on the endpoint side.
/// </summary>
public static class TinadecCoreHttpExtensions
{
    /// <summary>
    /// Registers the HTTP-facing services shared by every TinadecCore endpoint:
    /// snake_case JSON, RFC 9457 validation ProblemDetails, trace_id enrichment,
    /// and the scoped control-plane/session lifecycle services.
    /// </summary>
    public static IServiceCollection AddTinadecCoreHttp(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            options.SerializerOptions.DefaultIgnoreCondition =
                System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        });
        services.Configure<ApiBehaviorOptions>(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
            {
                var detail = string.Join("; ", context.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
                var problem = new ProblemDetails
                {
                    Type = "https://tinadec.dev/errors/invalid_request",
                    Title = "invalid_request",
                    Detail = string.IsNullOrWhiteSpace(detail) ? "Request validation failed." : detail,
                    Status = StatusCodes.Status400BadRequest,
                    Instance = context.HttpContext.Request.Path
                };
                problem.Extensions["code"] = "invalid_request";
                problem.Extensions["trace_id"] = context.HttpContext.TraceIdentifier;
                return new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
            };
        });

        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                var problem = context.ProblemDetails;
                // A request the framework rejects before any handler runs — a missing or
                // unparseable required route/query value, a 404/405 with no matching endpoint —
                // reaches this service as a bare RFC 9110 problem. Every other error body here
                // carries `code` and clients branch on it, so these must not be the one family
                // that says "no" without saying which rule refused.
                if (!problem.Extensions.ContainsKey("code"))
                {
                    var code = problem.Status switch
                    {
                        StatusCodes.Status400BadRequest or StatusCodes.Status422UnprocessableEntity => "invalid_request",
                        StatusCodes.Status401Unauthorized => "unauthorized",
                        StatusCodes.Status403Forbidden => "forbidden",
                        StatusCodes.Status404NotFound => "not_found",
                        StatusCodes.Status405MethodNotAllowed => "method_not_allowed",
                        StatusCodes.Status409Conflict => "conflict",
                        StatusCodes.Status413PayloadTooLarge => "payload_too_large",
                        StatusCodes.Status415UnsupportedMediaType => "unsupported_media_type",
                        StatusCodes.Status429TooManyRequests => "rate_limited",
                        >= StatusCodes.Status500InternalServerError => "internal_error",
                        _ => "request_failed",
                    };
                    problem.Extensions["code"] = code;
                    problem.Type = $"https://tinadec.dev/errors/{code}";
                    problem.Title = code;
                    problem.Detail ??= code switch
                    {
                        "invalid_request" => "A required route or query parameter is missing or cannot be parsed.",
                        "not_found" => "No endpoint matches this request.",
                        "method_not_allowed" => "This endpoint does not accept that HTTP method.",
                        "payload_too_large" => "The request body exceeds the configured limit.",
                        "unsupported_media_type" => "Send the body as application/json.",
                        "rate_limited" => "Too many requests; retry later.",
                        "internal_error" => "An unexpected error occurred.",
                        _ => "The request was rejected.",
                    };
                }
                problem.Extensions["trace_id"] = context.HttpContext.TraceIdentifier;
            };
        });

        services.AddScoped<ControlPlaneService>();
        services.AddScoped<ProjectSessionLifecycleService>();
        return services;
    }

    /// <summary>
    /// RFC 9457 + snake_case ProblemDetails with trace_id/code extension.
    /// Must be registered before endpoint mapping so it wraps all handlers.
    /// </summary>
    public static IApplicationBuilder UseTinadecCoreExceptionHandler(this IApplicationBuilder app)
    {
        app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
                var exception = feature?.Error;
                // Map exception types to required code set:
                // invalid_request, context_conflict, model_not_configured, run_not_found, forbidden, conflict
                var (status, code, detail) = exception switch
                {
                    TinadecCore.Abstractions.Ports.TinaChatException chat => (chat.StatusCode, chat.Code, chat.Message),
                    TinadecCore.AgentConfiguration.AgentPackDomainException ape => (ape.StatusCode, ape.Code, ape.Message),
                    TinadecCore.DmaEA.RunAdmissionException rae when rae.Code == "CONTEXT_REVISION_CONFLICT" => (StatusCodes.Status409Conflict, "context_conflict", rae.Message),
                    TinadecCore.DmaEA.RunAdmissionException rae => (StatusCodes.Status409Conflict, "conflict", rae.Message),
                    UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "forbidden", exception.Message),
                    ArgumentException => (StatusCodes.Status400BadRequest, "invalid_request", exception.Message),
                    // Minimal-API throws this before the handler ever runs when a required route or
                    // query parameter is missing or unparseable. It carries its own 4xx status, so
                    // letting it fall through to the catch-all turned "you forgot actor_id" into a 500.
                    Microsoft.AspNetCore.Http.BadHttpRequestException binding => (binding.StatusCode, "invalid_request", binding.Message),
                    KeyNotFoundException => (StatusCodes.Status404NotFound, "run_not_found", exception.Message),
                    InvalidOperationException ioe when ioe.Message.Contains("model", StringComparison.OrdinalIgnoreCase) || ioe.Message.Contains("Provider", StringComparison.OrdinalIgnoreCase) => (StatusCodes.Status400BadRequest, "model_not_configured", ioe.Message),
                    InvalidOperationException => (StatusCodes.Status409Conflict, "conflict", exception.Message),
                    _ => (StatusCodes.Status500InternalServerError, "internal_error", "An unexpected error occurred.")
                };
                var problem = new ProblemDetails
                {
                    Type = $"https://tinadec.dev/errors/{code}",
                    Title = code,
                    Detail = detail,
                    Status = status,
                    Instance = context.Request.Path
                };
                problem.Extensions["code"] = code;
                problem.Extensions["trace_id"] = context.TraceIdentifier;
                context.Response.StatusCode = status;
                await context.Response.WriteAsJsonAsync(
                    problem,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower },
                    "application/problem+json");
            });
        });
        return app;
    }
}
