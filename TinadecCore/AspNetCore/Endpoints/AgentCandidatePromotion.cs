using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TinadecCore.AgentConfiguration;
using TinadecCore.DmaEA;

namespace TinadecCore.AspNetCore.Endpoints;

/// <summary>
/// Review-driven promotion of generated agent candidates. The pipeline stages
/// owned here are sanitization (minimal, clamped draft), review (the human
/// decision that calls this endpoint), and publish (immutable AgentVersion
/// through the AgentConfiguration boundary). Canary and activation remain
/// future stages; a promoted agent only becomes routable after an explicit
/// mode binding by an operator.
/// </summary>
internal static class AgentCandidatePromotion
{
    public static async Task<IResult> PromoteAsync(
        IAgentInstanceService instances,
        IAgentConfigurationService configurations,
        Guid candidateId,
        string? reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var (candidate, proposal) = await instances.GetCandidateWithProposalAsync(candidateId, cancellationToken).ConfigureAwait(false);
            if (candidate.Status != "proposed")
            {
                return Results.Conflict(new { code = "ALREADY_DECIDED", candidate_id = candidateId, message = "Candidate has already been decided." });
            }

            var payload = SanitizeProposal(candidate, proposal);
            var draft = JsonSerializer.SerializeToElement(await configurations.CreateDraftAsync("agent", payload, cancellationToken).ConfigureAwait(false));
            var agentId = draft.GetProperty("id").GetGuid();
            var revision = draft.GetProperty("revision").GetInt64();
            var published = JsonSerializer.SerializeToElement(await configurations.PublishAsync(agentId, revision, cancellationToken).ConfigureAwait(false));
            var decided = await instances.DecideCandidateAsync(candidateId, "promoted", reason, cancellationToken, agentId).ConfigureAwait(false);
            return Results.Ok(new
            {
                id = decided.Id,
                source_run_id = decided.SourceRunId,
                name = decided.Name,
                layer = decided.Layer,
                agent_type = decided.AgentType,
                status = decided.Status,
                decision_reason = decided.DecisionReason,
                promoted_agent_id = decided.PromotedAgentId,
                published_agent = new
                {
                    id = agentId,
                    version = published.GetProperty("version").GetInt32(),
                    content_hash = published.GetProperty("content_hash").GetString()
                }
            });
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { code = "NOT_FOUND", candidate_id = candidateId, message = "Agent candidate was not found." });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { code = "INVALID_PROPOSAL", candidate_id = candidateId, message = ex.Message });
        }
        catch (InvalidDataException ex)
        {
            return Results.BadRequest(new { code = "INVALID_PROPOSAL", candidate_id = candidateId, message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { code = "ALREADY_DECIDED", candidate_id = candidateId, message = ex.Message });
        }
    }

    /// <summary>
    /// Builds a minimal, clamped agent draft from the immutable proposal body.
    /// Promoted agents inherit the default model route and start without
    /// autonomous tools; expanding either requires an explicit operator edit.
    /// </summary>
    private static Dictionary<string, object?> SanitizeProposal(AgentCandidateRecord candidate, JsonElement proposal)
    {
        if (proposal.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Candidate proposal must be a JSON object.");
        var name = String(proposal, "display_name") ?? String(proposal, "name") ?? candidate.Name;
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidDataException("Candidate proposal needs a display name.");
        var layer = (String(proposal, "layer") ?? candidate.Layer).Trim().ToLowerInvariant();
        AgentConfigurationService.ValidateLayer(layer);
        var systemPrompt = String(proposal, "system_prompt");
        if (string.IsNullOrWhiteSpace(systemPrompt))
            throw new InvalidDataException("Candidate proposal needs a system_prompt before promotion.");

        var capabilities = new List<string>();
        if (proposal.TryGetProperty("capabilities", out var capabilitiesNode) && capabilitiesNode.ValueKind == JsonValueKind.Array)
        {
            capabilities.AddRange(capabilitiesNode.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!.Trim())
                .Where(item => item.Length > 0));
        }

        return new Dictionary<string, object?>
        {
            ["display_name"] = name.Trim(),
            ["slug"] = String(proposal, "slug") ?? name,
            ["layer"] = layer,
            ["role"] = String(proposal, "role") ?? candidate.AgentType,
            ["description"] = String(proposal, "description") ?? $"Promoted from candidate {candidate.Id}.",
            ["capabilities"] = capabilities,
            ["model_strategy"] = new Dictionary<string, object?> { ["kind"] = "inherit" },
            ["tool_scope"] = Array.Empty<string>(),
            ["system_prompt"] = systemPrompt.Trim()
        };
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
