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
                context.ProblemDetails.Extensions["trace_id"] = context.HttpContext.TraceIdentifier;
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
                    TinadecCore.AgentConfiguration.AgentPackDomainException ape => (ape.StatusCode, ape.Code, ape.Message),
                    TinadecCore.DmaEA.RunAdmissionException rae when rae.Code == "CONTEXT_REVISION_CONFLICT" => (StatusCodes.Status409Conflict, "context_conflict", rae.Message),
                    TinadecCore.DmaEA.RunAdmissionException rae => (StatusCodes.Status409Conflict, "conflict", rae.Message),
                    UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "forbidden", exception.Message),
                    ArgumentException => (StatusCodes.Status400BadRequest, "invalid_request", exception.Message),
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
