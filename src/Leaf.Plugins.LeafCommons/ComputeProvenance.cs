using System.Security.Claims;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.LeafCommons;

/// <summary>
/// Provenance for Compute work Leaf Community starts itself (the outbound Sentinel reviews), built
/// the way Leaf Discord builds its own: app leaf-commons, actor the Public Agent, beneficiary the
/// owner, and the thread and post as context. A settled-turn review runs with no HTTP request and
/// no execution scope to inherit; without this RedLeaf's ComputeGateway cannot issue a signed
/// execution identity and refuses the call (a peer install, 2026-10-28).
/// </summary>
internal static class CommonsComputeProvenance
{
    public const string ReviewRoute = "/api/apps/leaf-commons/outbound-review";

    /// <summary>
    /// The provenance for a review made while <paramref name="caller"/> is the authenticated
    /// request, or null when the review inherits the caller's own execution identity. RedLeaf's
    /// ComputeGateway signs a stated provenance only for a beneficiary that request may act for:
    /// an execution bearer is the review's parent, so the review inherits it (as RedLeaf does for
    /// any call without a provenance) instead of naming anyone; LocalDefault, system and service
    /// callers act only for the system; a signed-in user only for themself. Any other stated
    /// beneficiary is refused as "beneficiary does not match the authenticated caller": the
    /// system inside the owner's own Agent request failed every brief (field report, 2026-10-31).
    /// Without a request the review states its own provenance for the owner.
    /// </summary>
    public static async Task<ComputeProvenance?> ForCallerAsync(
        ClaimsPrincipal? caller,
        IEntityStore? entities,
        string agentRef,
        string? ownerUserId,
        IReadOnlyList<ComputeContextReference> context,
        string? correlationId,
        CancellationToken ct)
    {
        if (caller?.Identity?.IsAuthenticated != true)
            return await CreateAsync(entities, agentRef, await BeneficiaryAsync(entities, ownerUserId, ct),
                context, correlationId, ct);
        if (string.Equals(caller.FindFirst("token_use")?.Value, "execution", StringComparison.OrdinalIgnoreCase))
            return null;
        return await CreateAsync(entities, agentRef, CallerBeneficiary(caller), context, correlationId, ct);
    }

    public static async Task<ComputeProvenance> CreateAsync(
        IEntityStore? entities,
        string agentRef,
        ComputeBeneficiary beneficiary,
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
            beneficiary,
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

    /// <summary>
    /// The one beneficiary RedLeaf lets an authenticated request without an execution identity
    /// name: the signed-in user themself, or the system for LocalDefault, system and service
    /// callers (a bearer merely named local-user stays refused by RedLeaf).
    /// </summary>
    internal static ComputeBeneficiary CallerBeneficiary(ClaimsPrincipal caller)
    {
        var subject = caller.FindFirst("sub")?.Value;
        return caller.Identity?.AuthenticationType != "LocalDefault"
               && !string.IsNullOrWhiteSpace(subject)
               && subject is not ("local-user" or "system")
               && !subject.StartsWith("service:", StringComparison.OrdinalIgnoreCase)
            ? new ComputeBeneficiary("user", subject, caller.FindFirst("name")?.Value,
                caller.FindFirst("picture")?.Value)
            : new ComputeBeneficiary("system",
                Reason: "Leaf Community review requested by a local, system or service caller");
    }

    private static string? Str(JsonObject data, string key)
        => data[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
