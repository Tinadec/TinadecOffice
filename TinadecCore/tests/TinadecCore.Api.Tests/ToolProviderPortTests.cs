using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Tools;

namespace TinadecCore.Api.Tests;

public sealed class ToolProviderPortTests
{
    [Fact]
    public async Task CoreToolRegistry_UsesProviderPort_NotProcessManagerContract()
    {
        var provider = new InMemoryToolProvider();
        var registry = new CoreToolRegistry(provider);

        var tools = await registry.ListToolsAsync("workspace");

        var tool = Assert.Single(tools);
        Assert.Equal("memory.search", tool.Id);
        Assert.Equal("workspace", provider.LastManifestWorkspace);
    }

    private sealed class InMemoryToolProvider : IToolProvider
    {
        public string? LastManifestWorkspace { get; private set; }

        public Task<ToolManifestDto> EnsureStartedAsync(string workspaceRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateManifest());

        public Task<ToolWireResponseDto> CallAsync(
            string workspaceRoot,
            ToolWireRequestDto request,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolWireResponseDto { CallId = request.ToolCallId, IsSuccess = true });

        public Task<ToolManifestDto> GetManifestAsync(string workspaceRoot, CancellationToken cancellationToken = default)
        {
            LastManifestWorkspace = workspaceRoot;
            return Task.FromResult(CreateManifest());
        }

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        private static ToolManifestDto CreateManifest() => new()
        {
            ProtocolVersion = 2,
            ManifestHash = "test-provider",
            Tools =
            [
                new ToolManifestEntryDto
                {
                    Id = "memory.search",
                    Description = "Search memory",
                    InputSchema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone()
                }
            ]
        };
    }
}
