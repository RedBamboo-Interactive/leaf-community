using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.LeafCommons;

public sealed record SkillAssignment(string AgentId, string Status);

public static class SkillAssignmentStatus
{
    public const string Added = "added";
    public const string AlreadyPresent = "already_present";
    public const string Removed = "removed";
    public const string NotPresent = "not_present";
    public const string AgentMissing = "agent_missing";
    public const string SkillMissing = "skill_missing";
    /// <summary>The Agent is owned by another plugin; the kernel refuses Leaf Community's write.</summary>
    public const string ManualRequired = "manual_required";
    public const string Failed = "failed";
}

/// <summary>
/// Keeps a public Agent's <c>skills</c> in step with Leaf Community, as Mail does for its bound
/// agent: making an Agent public adds Leaf Community's own <c>leaf-community</c> agent-skill,
/// disabling or removing it takes that skill away. Only references to that one skill (by id or
/// slug) are ever added or removed; other skills are untouched. The write goes through the
/// kernel's plugin-scoped entity store (validated, versioned, and refused for Agents another
/// plugin owns) as a shallow patch of <c>skills</c> alone, so the Agent's ownership stamp is
/// never changed.
/// </summary>
public sealed class CommonsAgentSkills(IEntityStore entities, ILogger<CommonsAgentSkills>? logger = null)
{
    public const string SkillSlug = "leaf-community";
    public const string SkillType = "agent-skill";

    public Task<SkillAssignment> AddAsync(string agentId, string ownerUserId, CancellationToken ct = default)
        => ChangeAsync(agentId, ownerUserId, add: true, ct);

    public Task<SkillAssignment> RemoveAsync(string agentId, string ownerUserId, CancellationToken ct = default)
        => ChangeAsync(agentId, ownerUserId, add: false, ct);

    /// <summary>Leaf Community's own seeded skill: slug <c>leaf-community</c>, owned by this plugin's entity.</summary>
    private async Task<LeafEntity?> ResolveSkillAsync(CancellationToken ct)
    {
        var skill = await entities.GetBySlugAsync(SkillType, SkillSlug, ct);
        var plugin = await entities.GetBySlugAsync("plugin", LeafCommonsPlugin.PluginId, ct);
        if (skill == null || plugin == null) return null;
        return SameId(Text(skill.Data, "owner_plugin"), plugin.Id.ToString()) ? skill : null;
    }

    private async Task<SkillAssignment> ChangeAsync(string agentId, string ownerUserId, bool add, CancellationToken ct)
    {
        try
        {
            var skill = await ResolveSkillAsync(ct);
            if (skill == null) return new(agentId, SkillAssignmentStatus.SkillMissing);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var agent = Guid.TryParse(agentId, out var id) ? await entities.GetAsync(id, ct) : null;
                if (agent?.TypeSlug != "agent" || !OwnerMaySee(agent, ownerUserId))
                    return new(agentId, SkillAssignmentStatus.AgentMissing);
                var current = References(agent.Data);
                var matches = current.Where(r => IsCommunitySkill(r, skill)).ToArray();
                if (add && matches.Length > 0) return new(agentId, SkillAssignmentStatus.AlreadyPresent);
                if (!add && matches.Length == 0) return new(agentId, SkillAssignmentStatus.NotPresent);
                var next = add
                    ? current.Append(skill.Id.ToString()).ToArray()
                    : current.Where(r => !IsCommunitySkill(r, skill)).ToArray();

                // Narrow the window for a concurrent edit of this Agent: only write if it is
                // unchanged since it was read, otherwise recompute from the fresh copy.
                var check = await entities.GetAsync(agent.Id, ct);
                if (check?.UpdatedAt != agent.UpdatedAt) continue;
                var patch = new JsonObject { ["skills"] = new JsonArray(next.Select(r => (JsonNode)JsonValue.Create(r)).ToArray()) };
                await entities.PatchAsync(agent.Id, patch, ct: ct);
                logger?.LogInformation("Leaf Community skill {Change} for public Agent {Agent}", add ? "added" : "removed", agent.Id);
                return new(agentId, add ? SkillAssignmentStatus.Added : SkillAssignmentStatus.Removed);
            }
            return new(agentId, SkillAssignmentStatus.Failed);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("owned by another plugin", StringComparison.Ordinal))
        {
            return new(agentId, SkillAssignmentStatus.ManualRequired);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning("Leaf Community skill assignment for Agent {Agent} failed ({Type})", agentId, ex.GetType().Name);
            return new(agentId, SkillAssignmentStatus.Failed);
        }
    }

    private static string[] References(JsonObject data)
        => data["skills"] is JsonArray array
            ? array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
                .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).ToArray()
            : [];

    private static bool IsCommunitySkill(string reference, LeafEntity skill)
        => SameId(reference, skill.Id.ToString()) || string.Equals(reference, skill.Slug, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The kernel's confidentiality rule: another user's confidential Agent is never touched.
    /// <c>local-user</c> is the installation owner, as in <see cref="AgentDirectory"/>.
    /// </summary>
    private static bool OwnerMaySee(LeafEntity agent, string ownerUserId)
        => agent.Data["confidential"]?.GetValueKind() != System.Text.Json.JsonValueKind.True
            || Text(agent.Data, "owner_id") is { } owner
                && (owner.Equals(ownerUserId, StringComparison.OrdinalIgnoreCase)
                    || owner.Equals("local-user", StringComparison.OrdinalIgnoreCase));

    private static string? Text(JsonObject data, string key)
        => data[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool SameId(string? a, string? b)
        => Guid.TryParse(a, out var x) && Guid.TryParse(b, out var y) && x == y;
}
