using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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

public sealed record StoredDigest(
    Guid Id, string Type, string Content, string ThreadRef, bool Read,
    DateTimeOffset CreatedAt);

public sealed class CommonsStateStore(IEntityStore entities) : ICommonsAudit
{
    private static readonly TimeSpan OAuthStateLifetime = TimeSpan.FromMinutes(10);

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

    public async Task<LeafEntity> EnqueueBriefAsync(string kind, string content, string? title, IReadOnlyList<string>? addresses, LeafAgentCaller caller, CancellationToken ct = default)
    {
        if (!new[] { "showcase", "consult", "open_question", "bug", "bug_report", "intro_draft" }.Contains(kind)) throw new ArgumentException("Unsupported brief kind");
        if (string.IsNullOrWhiteSpace(content) || content.Length > 12_000) throw new ArgumentException("Brief content must be 1-12000 characters");
        ValidateTitle(kind, title);
        if (addresses is { Count: > 10 } || addresses?.Any(string.IsNullOrWhiteSpace) == true) throw new ArgumentException("Addresses must contain at most 10 non-empty values");
        var brief = await entities.CreateAsync("leaf-commons-brief", $"{kind} preview", new JsonObject
        {
            ["kind"] = kind, ["title"] = title, ["content"] = content,
            ["addresses"] = JsonSerializer.SerializeToNode(addresses ?? []), ["state"] = "preview",
            ["owner_id"] = caller.BeneficiaryId, ["agent_id"] = caller.AgentEntityId
        }, ct);
        await RecordAsync("brief_enqueued", new Dictionary<string, object?> { ["briefId"] = brief.Id, ["kind"] = kind }, ct);
        return brief;
    }

    public Task<IReadOnlyList<LeafEntity>> ListBriefsAsync(CancellationToken ct = default) =>
        entities.QueryAsync(new EntityQuery { TypeSlug = "leaf-commons-brief", OrderById = true, Limit = 200 }, ct);

    public async Task<StoredDigest> StoreDigestAsync(
        string type, string content, string threadRef, CancellationToken ct = default)
    {
        if (type is not ("consult_answer" or "announcement" or "owner_notice"))
            throw new ArgumentException("Unknown digest type", nameof(type));
        if (string.IsNullOrWhiteSpace(content) || content.Length > 1200)
            throw new ArgumentException("Digest content must be 1-1200 characters", nameof(content));
        if (string.IsNullOrWhiteSpace(threadRef) || threadRef.Length > 200)
            throw new ArgumentException("Digest thread reference must be 1-200 characters", nameof(threadRef));
        var createdAt = DateTimeOffset.UtcNow;
        var entity = await entities.CreateAsync("leaf-commons-digest", $"{type.Replace('_', ' ')} digest", new JsonObject
        {
            ["digest_type"] = type,
            ["content"] = content,
            ["thread_ref"] = threadRef,
            ["read"] = false,
            ["created_at"] = createdAt.ToString("O")
        }, ct);
        return ToDigest(entity);
    }

    public async Task<IReadOnlyList<StoredDigest>> ListDigestsAsync(
        bool unreadOnly = false, CancellationToken ct = default)
    {
        var entitiesFound = await entities.QueryAsync(new EntityQuery
            { TypeSlug = "leaf-commons-digest", Limit = 500 }, ct);
        return entitiesFound.Select(ToDigest)
            .Where(x => !unreadOnly || !x.Read)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .ToArray();
    }

    public async Task<StoredDigest> MarkDigestReadAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await entities.GetAsync(id, ct) ?? throw new KeyNotFoundException("Digest not found");
        if (entity.TypeSlug != "leaf-commons-digest") throw new KeyNotFoundException("Digest not found");
        if (entity.Data["read"]?.GetValue<bool>() != true)
            entity = await entities.PatchAsync(id, new JsonObject { ["read"] = true }, ct: ct);
        return ToDigest(entity);
    }

    public async Task<LeafEntity> DecideBriefAsync(Guid id, string decision, string? editedContent, string? editedTitle, IReadOnlyList<string>? editedAddresses, string ownerId, CancellationToken ct = default)
    {
        var brief = await entities.GetAsync(id) ?? throw new KeyNotFoundException("Brief not found");
        if (brief.TypeSlug != "leaf-commons-brief" || brief.Data["state"]?.GetValue<string>() != "preview") throw new InvalidOperationException("Brief is not pending preview");
        if (!string.Equals(brief.Data["owner_id"]?.GetValue<string>(), ownerId, StringComparison.Ordinal)) throw new UnauthorizedAccessException("Only the owner may decide this brief");
        if (decision is not ("approved" or "rejected")) throw new ArgumentException("Decision must be approved or rejected");
        if (editedContent is { Length: > 12000 }) throw new ArgumentException("Edited content too large");
        var finalContent = editedContent ?? brief.Data["content"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(finalContent)) throw new ArgumentException("Edited content must not be empty");
        var finalTitle = editedTitle ?? S(brief.Data, "title");
        ValidateTitle(S(brief.Data, "kind")!, finalTitle);
        var finalAddresses = editedAddresses ?? List(brief.Data["addresses"]);
        if (finalAddresses.Count > 10 || finalAddresses.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Addresses must contain at most 10 non-empty values");
        var patch = new JsonObject { ["state"] = decision };
        if (editedContent is not null) patch["content"] = editedContent;
        if (editedTitle is not null) patch["title"] = editedTitle;
        if (editedAddresses is not null) patch["addresses"] = JsonSerializer.SerializeToNode(editedAddresses);
        var updated = await entities.PatchAsync(id, patch, ct: ct);
        await RecordAsync("brief_decision", new Dictionary<string, object?> { ["briefId"] = id, ["decision"] = decision }, ct);
        return updated;
    }

    public async Task<OAuthStateIssue> CreateOAuthStateAsync(string ownerId, bool allowActiveOverwrite, DateTimeOffset? now = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("OAuth state requires an owner", nameof(ownerId));
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var slug = OAuthStateSlug(token);
        var issuedAt = now ?? DateTimeOffset.UtcNow;
        await entities.UpsertBySlugAsync("leaf-commons-oauth-state", slug, "Commons OAuth state", new JsonObject
        {
            ["owner_id"] = ownerId,
            ["expires_at"] = issuedAt.Add(OAuthStateLifetime).ToString("O"),
            ["consumed"] = false,
            ["allow_active_overwrite"] = allowActiveOverwrite
        }, ct);
        return new(token, issuedAt.Add(OAuthStateLifetime));
    }

    public async Task<OAuthStateConsumption> ConsumeOAuthStateAsync(string? token, string ownerId, DateTimeOffset? now = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return new(OAuthStateStatus.Missing);
        var slug = OAuthStateSlug(token);
        var entity = await entities.GetBySlugAsync("leaf-commons-oauth-state", slug, ct);
        if (entity is null) return new(OAuthStateStatus.Unknown);
        if (!string.Equals(S(entity.Data, "owner_id"), ownerId, StringComparison.Ordinal)) return new(OAuthStateStatus.OtherUser);
        if (entity.Data["consumed"]?.GetValue<bool>() == true) return new(OAuthStateStatus.Reused);
        if (!DateTimeOffset.TryParse(S(entity.Data, "expires_at"), out var expiresAt) || expiresAt <= (now ?? DateTimeOffset.UtcNow)) return new(OAuthStateStatus.Expired);
        var replacement = (JsonObject)entity.Data.DeepClone();
        replacement["consumed"] = true;
        replacement["consumed_at"] = (now ?? DateTimeOffset.UtcNow).ToString("O");
        var consumed = await entities.CompareExchangeBySlugAsync(entity.TypeSlug, entity.Slug, entity.Name, replacement, entity.UpdatedAt, ct);
        if (consumed is null) return new(OAuthStateStatus.Reused);
        return new(OAuthStateStatus.Accepted, entity.Data["allow_active_overwrite"]?.GetValue<bool>() == true);
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
    private static StoredDigest ToDigest(LeafEntity entity)
    {
        var createdAt = DateTimeOffset.TryParse(S(entity.Data, "created_at"), out var parsed)
            ? parsed
            : entity.CreatedAt;
        return new(entity.Id, S(entity.Data, "digest_type") ?? "owner_notice",
            S(entity.Data, "content") ?? "", S(entity.Data, "thread_ref") ?? "unknown",
            entity.Data["read"]?.GetValue<bool>() == true, createdAt);
    }
    private static void ValidateTitle(string kind, string? title)
    {
        if (kind is "consult" or "open_question" or "bug" or "bug_report")
        {
            if (string.IsNullOrWhiteSpace(title) || title.Length > 100) throw new ArgumentException("Thread-opening briefs require a 1-100 character title");
        }
        else if (title is { Length: > 100 }) throw new ArgumentException("Brief title must be at most 100 characters");
    }
    private static string OAuthStateSlug(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return "oauth-state-" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed record OAuthStateIssue(string State, DateTimeOffset ExpiresAt);
public enum OAuthStateStatus { Accepted, Missing, Unknown, Expired, Reused, OtherUser }
public sealed record OAuthStateConsumption(OAuthStateStatus Status, bool AllowActiveOverwrite = false);

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
