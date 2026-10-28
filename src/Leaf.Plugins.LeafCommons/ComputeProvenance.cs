using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.LeafCommons;

/// <summary>
/// Provenance for Compute work Leaf Community starts itself (the outbound Sentinel reviews), built
/// the way Leaf Discord builds its own: app leaf-commons, actor the Public Agent, beneficiary the
/// owner, and the thread and post as context. A settled-turn review runs with no HTTP request and
/// no execution scope to inherit; without this RedLeaf's ComputeGateway cannot issue a signed
/// execution identity and refuses the call (Sibylle on Fred's install, 2026-10-28).
/// </summary>
internal static class CommonsComputeProvenance
{
    public const string ReviewRoute = "/api/apps/leaf-commons/outbound-review";

    public static async Task<ComputeProvenance> CreateAsync(
        IEntityStore? entities,
        string agentRef,
        string? ownerUserId,
        IReadOnlyList<ComputeContextReference> context,
        string? correlationId,
        CancellationToken ct)
    {
        var plugin = entities is null ? null : await entities.GetBySlugAsync("plugin", LeafCommonsPlugin.PluginId, ct);
        var app = plugin is { TypeSlug: "plugin" }
            ? new ComputeAppReference("plugin", plugin.Slug, plugin.Id.ToString(), plugin.Name,
                Str(plugin.Data, "icon"))
            : new ComputeAppReference("plugin", LeafCommonsPlugin.PluginId, null, "Leaf Community", "ph-leaf", "emerald");
        var agent = entities is null
            ? null
            : Guid.TryParse(agentRef, out var agentId)
                ? await entities.GetAsync(agentId, ct)
                : await entities.GetBySlugAsync("agent", agentRef, ct);
        var actor = agent is { TypeSlug: "agent" }
            ? new ComputeActor("agent", agent.Name, agent.Id.ToString(), agent.Slug)
            : new ComputeActor("agent", agentRef, Id: agentRef);
        return new ComputeProvenance(
            ComputeProvenance.CurrentSchemaVersion,
            new ComputeOrigin("redleaf", app, new ComputeEntrypoint("background", ReviewRoute, "REVIEW")),
            actor,
            await BeneficiaryAsync(entities, ownerUserId, ct),
            context.Take(15).ToArray(),
            new ComputeTrace(CorrelationId: correlationId),
            ComputeProvenanceAssurance.Verified,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The owner when they still resolve to a User; otherwise the bounded system principal rather
    /// than naming a user RedLeaf's execution-token issuer cannot authenticate.
    /// </summary>
    internal static async Task<ComputeBeneficiary> BeneficiaryAsync(
        IEntityStore? entities, string? ownerUserId, CancellationToken ct)
    {
        if (entities is not null && Guid.TryParse(ownerUserId, out var id)
            && await entities.GetAsync(id, ct) is { TypeSlug: "user" } user)
            return new ComputeBeneficiary("user", user.Id.ToString(), user.Name,
                Str(user.Data, "avatar_url") ?? Str(user.Data, "avatar"));
        return new ComputeBeneficiary("system",
            Reason: "Leaf Community review for an owner without a resolvable User");
    }

    private static string? Str(JsonObject data, string key)
        => data[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
