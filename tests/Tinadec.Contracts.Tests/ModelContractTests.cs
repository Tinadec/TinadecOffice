using System.Text.Json;
using System.Text.Json.Nodes;
using Tinadec.Contracts.Models;

namespace Tinadec.Contracts.Tests;

public sealed class ModelContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    [Fact]
    public void ModelInvocationContractsSerializeWithSnakeCaseFields()
    {
        var request = new ModelInvocationRequestDto(
            Messages:
            [
                new MessageDto("msg_1", "sess_1", "user", "Hello", DateTimeOffset.UnixEpoch)
            ],
            SystemPrompt: "Be concise.",
            Tools:
            [
                new JsonObject
                {
                    ["name"] = "search",
                    ["description"] = "Search workspace"
                }
            ],
            Settings: new ModelSettingsDto("https://example.test", "model-a", true, DateTimeOffset.UnixEpoch),
            StateHandle: new ModelStateHandleDto("state_1", DateTimeOffset.UnixEpoch));

        var response = new ModelInvocationResponseDto(
            TextContent: "Done",
            Usage: new ModelUsageDto(10, 5, 15),
            FinishReason: ModelFinishReason.ToolCalls,
            Metadata: new ProviderMetadataDto(
                ProviderId: "provider_1",
                Model: "model-a",
                RawProviderName: "ExampleProvider",
                Custom: new Dictionary<string, object?> { ["request_id"] = "req_1" }),
            StateHandle: new ModelStateHandleDto("state_2", DateTimeOffset.UnixEpoch),
            ErrorCategory: ProviderErrorCategory.RateLimited,
            ErrorMessage: "Too many requests");

        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        var responseJson = JsonSerializer.Serialize(response, JsonOptions);

        Assert.Contains("\"system_prompt\":\"Be concise.\"", requestJson);
        Assert.Contains("\"state_handle\"", requestJson);
        Assert.Contains("\"text_content\":\"Done\"", responseJson);
        Assert.Contains("\"prompt_tokens\":10", responseJson);
        Assert.Contains("\"completion_tokens\":5", responseJson);
        Assert.Contains("\"total_tokens\":15", responseJson);
        Assert.Contains("\"finish_reason\":\"tool_calls\"", responseJson);
        Assert.Contains("\"provider_id\":\"provider_1\"", responseJson);
        Assert.Contains("\"raw_provider_name\":\"ExampleProvider\"", responseJson);
        Assert.Contains("\"error_category\":\"rate_limited\"", responseJson);
    }

    [Fact]
    public void ProviderErrorCategoriesIncludeRequiredNormalizedValues()
    {
        var values = Enum.GetNames<ProviderErrorCategory>();

        Assert.Equal(
            [
                "AuthenticationFailed",
                "RateLimited",
                "Timeout",
                "ProviderUnavailable",
                "InvalidRequest",
                "Cancelled",
                "Unknown"
            ],
            values);
    }

    [Fact]
    public void ProviderCapabilityMetadataRepresentsRequiredFields()
    {
        var capability = new ProviderCapabilityDto(
            SupportsStreaming: true,
            SupportsTools: true,
            SupportsJsonMode: true,
            SupportsSystemPrompt: true,
            MaxContextTokens: 128000,
            RequiresWorkspace: false,
            CredentialKind: "api_key",
            HealthStatus: ProviderHealthStatus.Cooldown);

        var json = JsonSerializer.Serialize(capability, JsonOptions);

        Assert.Contains("\"supports_streaming\":true", json);
        Assert.Contains("\"supports_tools\":true", json);
        Assert.Contains("\"supports_json_mode\":true", json);
        Assert.Contains("\"supports_system_prompt\":true", json);
        Assert.Contains("\"max_context_tokens\":128000", json);
        Assert.Contains("\"requires_workspace\":false", json);
        Assert.Contains("\"credential_kind\":\"api_key\"", json);
        Assert.Contains("\"health_status\":\"cooldown\"", json);
    }

    [Fact]
    public void HarnessManifestContractSerializesToolAndLayerSummaries()
    {
        var manifest = new HarnessManifestDto(
            Runtime: "tinadec-core-workflow",
            OwnershipModel: "Core owns orchestration.",
            ToolRegistry: new ToolRegistrySummaryDto(
                DeclaredToolCount: 4,
                CanonicalToolCount: 3,
                DuplicateToolIdCount: 1,
                DuplicateToolIds: ["apply_patch"],
                SourcePrecedence: ["core", "code", "codex-rust", "extension"],
                SelectionPolicy: "Core canonicalizes duplicate tool ids by source precedence."),
            AgentLayers:
            [
                new AgentLayerManifestDto(
                    "planning",
                    "Active planning and supervision layer",
                    AgentCount: 2,
                    EnabledAgentCount: 2,
                    MaxParallelExecutors: 1,
                    WorktreeIsolation: false,
                    ApprovalRequired: true,
                    AgentTypes: ["meeting", "supervisor"],
                    ToolIds: ["prompt_context_resolve"])
            ],
            ToolProviders:
            [
                new ToolProviderManifestDto(
                    "code",
                    "Code Tool Suite",
                    "tool-layer",
                    "active",
                    ToolCount: 3,
                    ActiveToolCount: 3,
                    FutureToolCount: 0,
                    ApprovalRequiredCount: 2,
                    ReadOnlyCount: 1,
                    CapabilityPrefixes: ["project", "runtime"])
            ],
            ToolRisks:
            [
                new ToolRiskManifestDto(
                    "workspace-write",
                    ToolCount: 1,
                    RequiresHumanCheckpoint: true,
                    PolicySummary: "Requires approval.")
            ],
            Tools:
            [
                new ToolDescriptorDto(
                    "apply_patch",
                    "Apply Patch",
                    "programming",
                    "codex-rust",
                    "workspace-write",
                    RequiresApproval: true,
                    ExecuteEndpoint: "/api/v1/code/tools/apply_patch/execute",
                    Capabilities: ["patch.apply"])
            ],
            DesignNotes: ["Code is a Tool-layer suite."]);

        var json = JsonSerializer.Serialize(manifest, JsonOptions);

        Assert.Contains("\"ownership_model\":\"Core owns orchestration.\"", json);
        Assert.Contains("\"tool_registry\"", json);
        Assert.Contains("\"declared_tool_count\":4", json);
        Assert.Contains("\"canonical_tool_count\":3", json);
        Assert.Contains("\"duplicate_tool_id_count\":1", json);
        Assert.Contains("\"agent_layers\"", json);
        Assert.Contains("\"enabled_agent_count\":2", json);
        Assert.Contains("\"max_parallel_executors\":1", json);
        Assert.Contains("\"tool_providers\"", json);
        Assert.Contains("\"approval_required_count\":2", json);
        Assert.Contains("\"tool_risks\"", json);
        Assert.Contains("\"requires_human_checkpoint\":true", json);
        Assert.Contains("\"execute_endpoint\":\"/api/v1/code/tools/apply_patch/execute\"", json);
    }

    [Fact]
    public void ToolSearchResultContractSerializesCoreDiscoveryMetadata()
    {
        var result = new ToolSearchResultDto(
            new ToolDescriptorDto(
                "git_worktree_manager",
                "Git Worktree Manager",
                "programming",
                "code",
                "git-write",
                RequiresApproval: true,
                ExecuteEndpoint: "/api/v1/code/tools/git_worktree_manager/execute",
                Capabilities: ["git.worktree", "workspace.isolation"]),
            Score: 420,
            MatchedFields: ["id", "capabilities"],
            ProviderLayer: "tool-layer",
            RequiresHumanCheckpoint: true,
            ApprovalSummary: "Requires Core approval before dispatch.");

        var json = JsonSerializer.Serialize(result, JsonOptions);

        Assert.Contains("\"score\":420", json);
        Assert.Contains("\"matched_fields\":[\"id\",\"capabilities\"]", json);
        Assert.Contains("\"provider_layer\":\"tool-layer\"", json);
        Assert.Contains("\"requires_human_checkpoint\":true", json);
        Assert.Contains("\"approval_summary\":\"Requires Core approval before dispatch.\"", json);
    }

    [Fact]
    public void ToolExecutionTimelineContractSerializesAuditMetadata()
    {
        var item = new ToolExecutionTimelineItemDto(
            "step_1",
            "run_1",
            "sess_1",
            "read_file",
            "Read File",
            "codex-rust",
            "native-glue",
            "read-only",
            RequiresApproval: false,
            Status: "completed",
            ApprovalId: null,
            StepResultId: "step_1",
            Summary: "Read a file.",
            Evidence: ["file:README.md"],
            RequestedAt: DateTimeOffset.UnixEpoch,
            UpdatedAt: DateTimeOffset.UnixEpoch.AddSeconds(2),
            DurationMs: 2000,
            RequestedSeq: 10,
            UpdatedSeq: 11,
            EventTypes: ["tool.execution.requested", "tool.execution.completed"],
            CheckpointSummary: "Read-only tool execution was auto-dispatchable under Core policy.");

        var json = JsonSerializer.Serialize(item, JsonOptions);

        Assert.Contains("\"tool_display_name\":\"Read File\"", json);
        Assert.Contains("\"provider_layer\":\"native-glue\"", json);
        Assert.Contains("\"requires_approval\":false", json);
        Assert.Contains("\"step_result_id\":\"step_1\"", json);
        Assert.Contains("\"duration_ms\":2000", json);
        Assert.Contains("\"requested_seq\":10", json);
        Assert.Contains("\"updated_seq\":11", json);
        Assert.Contains("\"event_types\":[\"tool.execution.requested\",\"tool.execution.completed\"]", json);
        Assert.Contains("\"checkpoint_summary\":\"Read-only tool execution was auto-dispatchable under Core policy.\"", json);
    }

    [Fact]
    public void RuntimeReadinessReceiptContractSerializesCoreStartupEvidence()
    {
        var receipt = new RuntimeReadinessReceiptDto(
            Status: "warning",
            GeneratedAt: DateTimeOffset.UnixEpoch,
            Runtime: "tinadec-core-workflow",
            ReceiptId: "readiness_1",
            Components:
            [
                new RuntimeReadinessComponentDto(
                    "tool_registry",
                    "Tool Registry",
                    "ready",
                    "Core tool registry is canonical.",
                    ["canonical_tool_count:15"])
            ],
            ReadyCount: 1,
            WarningCount: 1,
            BlockedCount: 0);

        var json = JsonSerializer.Serialize(receipt, JsonOptions);

        Assert.Contains("\"generated_at\":\"1970-01-01T00:00:00+00:00\"", json);
        Assert.Contains("\"receipt_id\":\"readiness_1\"", json);
        Assert.Contains("\"ready_count\":1", json);
        Assert.Contains("\"warning_count\":1", json);
        Assert.Contains("\"blocked_count\":0", json);
        Assert.Contains("\"components\"", json);
        Assert.Contains("\"evidence\":[\"canonical_tool_count:15\"]", json);
    }

    [Fact]
    public void ModelReadinessReceiptContractSerializesProviderAndRouteEvidence()
    {
        var receipt = new ModelReadinessReceiptDto(
            Status: "blocked",
            GeneratedAt: DateTimeOffset.UnixEpoch,
            ReceiptId: "model_readiness_1",
            ProviderCount: 1,
            ReadyProviderCount: 0,
            WarningProviderCount: 0,
            BlockedProviderCount: 1,
            RouteCount: 1,
            ReadyRouteCount: 0,
            WarningRouteCount: 0,
            BlockedRouteCount: 1,
            Providers:
            [
                new ModelProviderReadinessDto(
                    "openai_default",
                    "OpenAI Compatible",
                    "openai-compatible",
                    "api-key",
                    "blocked",
                    "needs_key",
                    Enabled: true,
                    HasCredential: false,
                    RoutePurposes: ["chat"],
                    Summary: "Provider status 'needs_key' blocks routed model traffic.",
                    Evidence: ["provider_status:needs_key"])
            ],
            Routes:
            [
                new ModelRouteReadinessDto(
                    "chat",
                    "openai_default",
                    "OpenAI Compatible",
                    "gpt-5.4-mini",
                    "blocked",
                    "Route provider is needs_key.",
                    ["provider_status:needs_key"])
            ],
            DesignNotes: ["Core owns provider readiness."]);

        var json = JsonSerializer.Serialize(receipt, JsonOptions);

        Assert.Contains("\"receipt_id\":\"model_readiness_1\"", json);
        Assert.Contains("\"provider_count\":1", json);
        Assert.Contains("\"blocked_provider_count\":1", json);
        Assert.Contains("\"blocked_route_count\":1", json);
        Assert.Contains("\"provider_instance_id\":\"openai_default\"", json);
        Assert.Contains("\"provider_status\":\"needs_key\"", json);
        Assert.Contains("\"has_credential\":false", json);
        Assert.Contains("\"route_purposes\":[\"chat\"]", json);
    }

    [Fact]
    public void PromptFragmentContractUsesPlanFieldNames()
    {
        var fragment = new PromptFragmentDto(
            "prompt_1",
            "builtin.meeting.default",
            "Meeting Agent Default",
            "agent",
            "agent_meeting",
            "identity",
            "Be useful.",
            900,
            true,
            true,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

        var json = JsonSerializer.Serialize(fragment, JsonOptions);

        Assert.Contains("\"target_agent_id\":\"agent_meeting\"", json);
        Assert.Contains("\"is_builtin\":true", json);
        Assert.DoesNotContain("\"is_built_in\"", json);
    }

    [Fact]
    public void ProviderSpecificWireFieldsDoNotLeakToNormalizedDtoTopLevel()
    {
        var response = new ModelInvocationResponseDto(
            TextContent: "Done",
            Usage: new ModelUsageDto(1, 2, 3),
            FinishReason: ModelFinishReason.Stop,
            Metadata: new ProviderMetadataDto(
                ProviderId: "provider_1",
                Model: "model-a",
                RawProviderName: "ExampleProvider",
                Custom: new Dictionary<string, object?>
                {
                    ["previous_response_id"] = "resp_1",
                    ["choices"] = 1
                }),
            StateHandle: null,
            ErrorCategory: null,
            ErrorMessage: null);

        var json = JsonSerializer.Serialize(response, JsonOptions);
        var root = JsonNode.Parse(json)!.AsObject();

        Assert.DoesNotContain("tool_use", root.Select(property => property.Key));
        Assert.DoesNotContain("previous_response_id", root.Select(property => property.Key));
        Assert.DoesNotContain("choices", root.Select(property => property.Key));
    }
}
