using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using System.ClientModel;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.Models;

/// <summary>
/// Models module registrar. Registers model provider and routing.
/// </summary>
public sealed class ModelsModuleRegistrar : IModuleRegistrar
{
    public string ModuleId => "models";

    public void Register(ITinadecCoreBuilder builder)
    {
        builder.Services.AddDbContextFactory<ModelControlDbContext>((sp, options) => options.UseTinadecDatabase(sp));
        builder.Services.AddSingleton<IStorageMigrationParticipant, DbContextMigrationParticipant<ModelControlDbContext>>();
        builder.Services.AddSingleton<IModelProvider, ModelProvider>();
        builder.Services.AddSingleton<IEmbeddingProvider, EmbeddingProvider>();
        builder.RegisterModule(new ModuleDescriptor
        {
            ModuleId = ModuleId,
            Version = "0.1.0",
            Dependencies = ["abstractions", "persistence"],
            Capabilities = ["provider_management", "model_routing", "credential_references", "error_normalization", "readiness", "embedding_generation"],
            Language = "C#",
            MafPrimitives = ["agent", "chat_client"],
            RegistrationStatus = ModuleRegistrationStatus.NotConfigured
        });
    }
}

/// <summary>
/// Skeleton model provider. Uses IChatClient / ChatClientAgent as entry point.
/// Does not rewrite model HTTP clients.
/// </summary>
internal sealed class ModelProvider : IModelProvider
{
    public Task<IChatClient?> GetChatClientAsync(
        string? routeId = null,
        CancellationToken cancellationToken = default)
    {
        // Skeleton: no providers configured.
        return Task.FromResult<IChatClient?>(null);
    }

    public Task<ModelReadiness> CheckReadinessAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ModelReadiness
        {
            IsReady = false,
            StatusMessage = "No model providers configured.",
            Warnings = ["models module is in skeleton state — no providers registered."]
        });
    }
}

/// <summary>
/// Resolves the configured <c>embedding</c> route, reads the provider configuration
/// from the immutable content store, fetches the API key from the secret store, and
/// calls an OpenAI-compatible embedding endpoint. Returns structured unavailable
/// results when the route, provider, model, or key is missing rather than faking vectors.
/// </summary>
internal sealed class EmbeddingProvider : IEmbeddingProvider
{
    internal const string EmbeddingPurpose = "embedding";

    private readonly IDbContextFactory<ModelControlDbContext> _dbFactory;
    private readonly IContentStore _content;
    private readonly ISecretStore _secrets;

    public EmbeddingProvider(IDbContextFactory<ModelControlDbContext> dbFactory, IContentStore content, ISecretStore secrets)
    {
        _dbFactory = dbFactory;
        _content = content;
        _secrets = secrets;
    }

    public async Task<EmbeddingResult> GenerateAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Inputs.Count == 0) return Unavailable("Embedding request has no inputs.");

        var resolved = await ResolveAsync(request.TenantId, request.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (resolved.Error is not null) return Unavailable(resolved.Error);

        var options = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(resolved.BaseUrl)) options.Endpoint = new Uri(resolved.BaseUrl);

        var client = new OpenAIClient(new ApiKeyCredential(resolved.ApiKey!), options);
        var embeddingClient = client.GetEmbeddingClient(resolved.Model!);
        using var generator = embeddingClient.AsIEmbeddingGenerator();
        var generated = await generator.GenerateAsync(request.Inputs, cancellationToken: cancellationToken).ConfigureAwait(false);

        var vectors = generated.Select(e => e.Vector.ToArray()).ToList();
        var dimension = vectors.Count > 0 ? vectors[0].Length : 0;
        return new EmbeddingResult
        {
            IsAvailable = true,
            ModelId = resolved.ModelId,
            Dimension = dimension,
            Vectors = vectors
        };
    }

    private async Task<ResolvedEmbedding> ResolveAsync(Guid tenantId, Guid? workspaceId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var route = await db.Routes.AsNoTracking().Where(x => x.Purpose == EmbeddingPurpose && x.TenantId == tenantId && x.WorkspaceId == workspaceId && x.DeletedAt == null).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (route is null) return new ResolvedEmbedding { Error = "No embedding model route is configured for this workspace." };
        var routeVersion = await db.RouteVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == route.CurrentVersionId, cancellationToken).ConfigureAwait(false);
        if (routeVersion is null) return new ResolvedEmbedding { Error = "Embedding route has no current version." };

        var provider = await db.Providers.AsNoTracking().Where(x => x.Id == routeVersion.ProviderId && x.DeletedAt == null).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (provider is null) return new ResolvedEmbedding { Error = "Embedding route references a missing provider instance." };
        if (!provider.Enabled) return new ResolvedEmbedding { Error = "Configured embedding provider is disabled." };

        var providerVersion = await db.ProviderVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == provider.CurrentVersionId, cancellationToken).ConfigureAwait(false);
        if (providerVersion is null) return new ResolvedEmbedding { Error = "Provider instance has no current version." };

        var configJson = await ReadContentAsync(providerVersion.ContentReference, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(configJson);
        string? String(string key) => doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var model = string.IsNullOrWhiteSpace(routeVersion.Model) ? String("model") : routeVersion.Model;
        if (string.IsNullOrWhiteSpace(model)) return new ResolvedEmbedding { Error = "Embedding model name is not configured." };
        var baseUrl = String("base_url");
        if (string.IsNullOrWhiteSpace(baseUrl)) return new ResolvedEmbedding { Error = "Provider base_url is not configured." };

        if (string.IsNullOrWhiteSpace(provider.SecretReference)) return new ResolvedEmbedding { Error = "Provider has no API key reference." };
        var apiKey = await _secrets.GetAsync(provider.SecretReference, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey)) return new ResolvedEmbedding { Error = "Provider API key is not stored." };

        return new ResolvedEmbedding
        {
            BaseUrl = baseUrl,
            Model = model,
            ApiKey = apiKey,
            ModelId = $"{provider.Driver}/{model}"
        };
    }

    private async Task<string> ReadContentAsync(string reference, CancellationToken cancellationToken)
    {
        await using var stream = await _content.OpenReadAsync(new ContentReference(reference, "", 0, "application/json"), cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static EmbeddingResult Unavailable(string detail) => new()
    {
        IsAvailable = false,
        Detail = detail
    };

    private sealed class ResolvedEmbedding
    {
        public string? BaseUrl { get; set; }
        public string? Model { get; set; }
        public string? ApiKey { get; set; }
        public string? ModelId { get; set; }
        public string? Error { get; set; }
    }
}