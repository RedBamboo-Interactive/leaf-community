using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.LeafCommons;

public sealed record CommonsSettings(
    Guid? EntityId, string RelayBaseUrl, string? Credential, string ConnectionStatus,
    string Persona, string? Avatar, IReadOnlyDictionary<string, bool> ScenarioToggles,
    IReadOnlyList<string> ShareableFolders, IReadOnlyList<string> PersonalWords,
    IReadOnlyList<string> Canaries, string? NovaDiscussionId, IReadOnlyList<string> LinkAllowlist,
    string? OwnerUserId = null, string? AgentEntityId = null)
{
    public object Public() => new
    {
        relayBaseUrl = RelayBaseUrl, credentialConfigured = !string.IsNullOrWhiteSpace(Credential),
        connectionStatus = ConnectionStatus, persona = Persona, avatar = Avatar,
        scenarioToggles = ScenarioToggles, shareableFolders = ShareableFolders,
        personalWords = PersonalWords, canaries = Canaries, novaDiscussionId = NovaDiscussionId,
        linkAllowlist = LinkAllowlist, ownerUserId = OwnerUserId, agentEntityId = AgentEntityId
    };
}

public sealed class CommonsStateStore(IEntityStore entities) : ICommonsAudit
{
    public async Task<CommonsSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        var e = await entities.GetBySlugAsync("leaf-commons-settings", "settings", ct);
        return e is null ? new(null, "http://127.0.0.1:8787", null, "disconnected", "Nova", null,
            new Dictionary<string, bool>(), [], [], [], null, []) : FromEntity(e);
    }

    public async Task<CommonsSettings> SaveSettingsAsync(CommonsSettings value, CancellationToken ct = default)
    {
        var data = ToData(value);
        var saved = value.EntityId is { } id
            ? await entities.ReplaceDataAsync(id, data, "Nova Commons Settings", ct)
            : await entities.UpsertBySlugAsync("leaf-commons-settings", "settings", "Nova Commons Settings", data, ct);
        return FromEntity(saved);
    }

    public async Task<LeafEntity> EnqueueBriefAsync(string kind, string content, LeafAgentCaller caller, CancellationToken ct = default)
    {
        if (!new[] { "showcase", "consult", "bug_report", "intro_draft" }.Contains(kind)) throw new ArgumentException("Unsupported brief kind");
        if (string.IsNullOrWhiteSpace(content) || content.Length > 12_000) throw new ArgumentException("Brief content must be 1-12000 characters");
        var brief = await entities.CreateAsync("leaf-commons-brief", $"{kind} preview", new JsonObject
        {
            ["kind"] = kind, ["content"] = content, ["state"] = "preview",
            ["owner_id"] = caller.BeneficiaryId, ["agent_id"] = caller.AgentEntityId
        }, ct);
        await RecordAsync("brief_enqueued", new Dictionary<string, object?> { ["briefId"] = brief.Id, ["kind"] = kind }, ct);
        return brief;
    }

    public Task<IReadOnlyList<LeafEntity>> ListBriefsAsync(CancellationToken ct = default) =>
        entities.QueryAsync(new EntityQuery { TypeSlug = "leaf-commons-brief", OrderById = true, Limit = 200 }, ct);

    public async Task<LeafEntity> DecideBriefAsync(Guid id, string decision, string? editedContent, string ownerId, CancellationToken ct = default)
    {
        var brief = await entities.GetAsync(id) ?? throw new KeyNotFoundException("Brief not found");
        if (brief.TypeSlug != "leaf-commons-brief" || brief.Data["state"]?.GetValue<string>() != "preview") throw new InvalidOperationException("Brief is not pending preview");
        if (!string.Equals(brief.Data["owner_id"]?.GetValue<string>(), ownerId, StringComparison.Ordinal)) throw new UnauthorizedAccessException("Only the owner may decide this brief");
        if (decision is not ("approved" or "rejected")) throw new ArgumentException("Decision must be approved or rejected");
        if (editedContent is { Length: > 12000 }) throw new ArgumentException("Edited content too large");
        var patch = new JsonObject { ["state"] = decision };
        if (editedContent is not null) patch["content"] = editedContent;
        var updated = await entities.PatchAsync(id, patch, ct: ct);
        await RecordAsync("brief_decision", new Dictionary<string, object?> { ["briefId"] = id, ["decision"] = decision }, ct);
        return updated;
    }

    public async Task RecordAsync(string eventName, IReadOnlyDictionary<string, object?> safeMetadata, CancellationToken ct = default)
    {
        var metadata = new JsonObject();
        foreach (var pair in safeMetadata) metadata[pair.Key] = pair.Value is null ? null : JsonSerializer.SerializeToNode(pair.Value);
        await entities.CreateAsync("leaf-commons-audit", eventName, new JsonObject { ["event"] = eventName, ["metadata"] = metadata, ["occurred_at"] = DateTimeOffset.UtcNow }, ct);
    }

    private static CommonsSettings FromEntity(LeafEntity e)
    {
        var d = e.Data;
        return new(e.Id, S(d, "relay_base_url") ?? "http://127.0.0.1:8787", S(d, "envoy_credential"), S(d, "connection_status") ?? "disconnected",
            S(d, "persona") ?? "Nova", S(d, "avatar"), Dict(d["scenario_toggles"]), List(d["shareable_folders"]), List(d["personal_words"]), List(d["canaries"]),
            S(d, "nova_discussion_id"), List(d["link_allowlist"]), S(d, "owner_user_id"), S(d, "agent_entity_id"));
    }
    private static JsonObject ToData(CommonsSettings x) => new()
    {
        ["relay_base_url"] = x.RelayBaseUrl, ["envoy_credential"] = x.Credential, ["connection_status"] = x.ConnectionStatus,
        ["persona"] = x.Persona, ["avatar"] = x.Avatar, ["scenario_toggles"] = JsonSerializer.SerializeToNode(x.ScenarioToggles),
        ["shareable_folders"] = JsonSerializer.SerializeToNode(x.ShareableFolders), ["personal_words"] = JsonSerializer.SerializeToNode(x.PersonalWords),
        ["canaries"] = JsonSerializer.SerializeToNode(x.Canaries), ["nova_discussion_id"] = x.NovaDiscussionId,
        ["link_allowlist"] = JsonSerializer.SerializeToNode(x.LinkAllowlist), ["owner_user_id"] = x.OwnerUserId, ["agent_entity_id"] = x.AgentEntityId
    };
    private static string? S(JsonObject d, string key) => d[key]?.GetValue<string>();
    private static IReadOnlyList<string> List(JsonNode? n) => n is JsonArray a ? a.Select(x => x?.GetValue<string>()).Where(x => x is not null).Cast<string>().ToArray() : [];
    private static IReadOnlyDictionary<string, bool> Dict(JsonNode? n) => n is JsonObject o ? o.ToDictionary(x => x.Key, x => x.Value?.GetValue<bool>() ?? false) : new Dictionary<string, bool>();
}

public sealed record LeafAgentCaller(string AgentEntityId, string BeneficiaryId);

public static class OwnerGuard
{
    public static LeafAgentCaller? ReadPrivateNovaAgent(ClaimsPrincipal user, CommonsSettings settings)
    {
        if (!string.Equals(user.FindFirst("token_use")?.Value, "execution", StringComparison.OrdinalIgnoreCase)) return null;
        var raw = user.FindFirst("execution_identity")?.Value;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.GetProperty("app").GetProperty("id").GetString() != "nova") return null;
            var actor = root.GetProperty("actor"); var beneficiary = root.GetProperty("beneficiary");
            if (actor.GetProperty("kind").GetString() != "agent" || beneficiary.GetProperty("kind").GetString() != "user") return null;
            var agent = actor.GetProperty("entityId").GetString(); var owner = beneficiary.GetProperty("id").GetString();
            var hasDiscussion = false; var hasExternal = false;
            foreach (var context in root.GetProperty("context").EnumerateArray())
            {
                var kind = context.GetProperty("kind").GetString();
                hasDiscussion |= kind == "discussion"; hasExternal |= kind == "external-conversation";
            }
            if (!hasDiscussion || hasExternal || agent is null || owner is null) return null;
            if (!string.Equals(settings.OwnerUserId, owner, StringComparison.Ordinal) || !string.Equals(settings.AgentEntityId, agent, StringComparison.Ordinal)) return null;
            return new(agent, owner);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { return null; }
    }
}
