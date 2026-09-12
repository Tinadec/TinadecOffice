using System.Text.Json;
using TinadecCore.Runtime;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// ConversationIdentity resolution tiers (session-creation freeze): explicit
/// conversation marker → operation-layer node with a conversation capability →
/// literal "meeting" node. A mode with none of the three must resolve to null so
/// callers fail closed instead of guessing an identity; a caller-requested node
/// must satisfy a tier or be rejected.
/// </summary>
public sealed class ConversationIdentityResolverTests
{
    private static readonly Guid MeetingId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid SearchId = Guid.Parse("10000000-0000-0000-0000-000000000002");

    private static readonly ConversationIdentityResolver.DefinitionInput Meeting =
        new(MeetingId, "meeting", "operation", """["user.respond","task.dispatch"]""");

    private static readonly ConversationIdentityResolver.DefinitionInput Search =
        new(SearchId, "worker.search", "execution", """["task.dispatch"]""");

    private static ConversationIdentityResolver.NodeInput Node(Guid definitionId, string key, string layer, string? label = null, string? config = null) =>
        new(definitionId, key, layer, label, config is null ? null : JsonDocument.Parse(config).RootElement.Clone());

    [Fact]
    public void Tier1_ExplicitConversationMarkerWins_EvenOverMeetingCandidate()
    {
        var nodes = new[]
        {
            Node(Meeting.Id, "meeting", "operation"),
            Node(Search.Id, "dispatch", "operation", config: """{"conversation": true}"""),
        };
        var identity = ConversationIdentityResolver.Resolve(nodes, [Meeting, Search]);
        Assert.NotNull(identity);
        Assert.Equal("dispatch", identity.NodeKey);
        Assert.Equal("worker.search", identity.TemplateSlug);
    }

    [Fact]
    public void Tier2_OperationLayerWithConversationCapability_IsUsedWithoutMarker()
    {
        var nodes = new[]
        {
            Node(Search.Id, "worker.search", "execution"),
            Node(Meeting.Id, "meeting", "operation"),
        };
        var identity = ConversationIdentityResolver.Resolve(nodes, [Meeting, Search]);
        Assert.NotNull(identity);
        Assert.Equal("meeting", identity.NodeKey);
        Assert.Equal("meeting", identity.TemplateSlug);
    }

    [Fact]
    public void Tier3_LiteralMeetingNode_IsLegacyFallback_WhenCapabilitiesAreMissing()
    {
        var bare = new ConversationIdentityResolver.DefinitionInput(MeetingId, "meeting", "operation", null);
        var nodes = new[] { Node(bare.Id, "meeting", "operation", label: "Meeting") };
        var identity = ConversationIdentityResolver.Resolve(nodes, [bare]);
        Assert.NotNull(identity);
        Assert.Equal("meeting", identity.NodeKey);
        Assert.Equal("meeting", identity.TemplateSlug);
    }

    [Fact]
    public void ExecutionLayerOnlyMode_ResolvesToNull_FailClosed()
    {
        var nodes = new[] { Node(Search.Id, "worker.search", "execution") };
        Assert.Null(ConversationIdentityResolver.Resolve(nodes, [Search]));
    }

    [Fact]
    public void RequestedNode_MustExist_AndSatisfyATier_OrIsRejected()
    {
        const string snapshot = """
        {
          "schema": "tinadec.mode_version/v1",
          "nodes": [
            { "node_key": "meeting", "agent_definition_id": "10000000-0000-0000-0000-000000000001", "layer": "operation" },
            { "node_key": "worker.search", "agent_definition_id": "10000000-0000-0000-0000-000000000002", "layer": "execution" }
          ],
          "edges": []
        }
        """;

        var valid = ConversationIdentityResolver.ResolveRequested("meeting", snapshot, [Meeting, Search]);
        Assert.NotNull(valid);
        Assert.Equal("meeting", valid.NodeKey);

        // execution-only node: exists in the snapshot but satisfies no tier
        Assert.Null(ConversationIdentityResolver.ResolveRequested("worker.search", snapshot, [Meeting, Search]));
        // unknown key: not a node of this mode at all
        Assert.Null(ConversationIdentityResolver.ResolveRequested("does_not_exist", snapshot, [Meeting, Search]));
    }

    [Fact]
    public void SnapshotJson_IsParsed_WithUnknownFieldsTolerated()
    {
        const string snapshot = """
        {
          "schema": "tinadec.mode_version/v1",
          "future_field": { "anything": true },
          "nodes": [
            { "node_key": "worker.search", "agent_definition_id": "10000000-0000-0000-0000-000000000002", "layer": "execution", "config": {"conversation": "true"} }
          ],
          "edges": [ { "edge_key": "e1", "source_node_key": "worker.search", "target_node_key": "worker.search" } ]
        }
        """;
        var identity = ConversationIdentityResolver.Resolve(snapshot, [Search]);
        Assert.NotNull(identity);
        Assert.Equal("worker.search", identity.NodeKey);
    }

    [Fact]
    public void MalformedOrEmptySnapshot_ResolvesToNull()
    {
        Assert.Null(ConversationIdentityResolver.Resolve((string?)null, [Meeting]));
        Assert.Null(ConversationIdentityResolver.Resolve("", [Meeting]));
        Assert.Null(ConversationIdentityResolver.Resolve("{not json", [Meeting]));
        Assert.Null(ConversationIdentityResolver.Resolve("""{"nodes": "not-an-array"}""", [Meeting]));
    }
}
