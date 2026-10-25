using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.LeafCommons;

public static class CommonsRelayDefaults
{
    public const string ProductionBaseUrl =
        "https://nova-commons-relay.becherel-laurent.workers.dev";

    public static string EffectiveBaseUrl(string? storedValue)
        => string.IsNullOrWhiteSpace(storedValue)
            ? ProductionBaseUrl
            : storedValue.Trim();

    public static string StoredValue(string? value)
    {
        var effective = EffectiveBaseUrl(value);
        return SameEndpoint(effective, ProductionBaseUrl) ? "" : effective;
    }

    public static bool IsCustom(string? value)
        => !SameEndpoint(EffectiveBaseUrl(value), ProductionBaseUrl);

    private static bool SameEndpoint(string left, string right)
    {
        if (!Uri.TryCreate(left, UriKind.Absolute, out var leftUri)
            || !Uri.TryCreate(right, UriKind.Absolute, out var rightUri))
            return string.Equals(left.TrimEnd('/'), right.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase);
        return Uri.Compare(leftUri, rightUri, UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
    }
}

public sealed record CommonsSettings(
    Guid? EntityId, string RelayBaseUrl, string? Credential, string ConnectionStatus,
    string Persona, string? Avatar, IReadOnlyDictionary<string, bool> ScenarioToggles,
    IReadOnlyList<string> ShareableFolders, IReadOnlyList<string> PersonalWords,
    IReadOnlyList<string> Canaries, string? NovaDiscussionId, IReadOnlyList<string> LinkAllowlist,
    string? OwnerUserId = null, string? AgentEntityId = null,
    string AvatarSource = AvatarSources.AgentAvatar, string? AvatarContentHash = null,
    string AvatarSyncStatus = "not-synced", string? AvatarSyncReason = null,
    string? AvatarResolvedAssetId = null, DateTimeOffset? AvatarSyncedAt = null,
    string? PublicName = null, string? EnvoyId = null)
{
    public string EffectivePublicName => !string.IsNullOrWhiteSpace(PublicName)
        ? PublicName
        : PublicNameFromPersona(Persona);

    public object Public(bool outfitsAvailable = false) => new
    {
        relayBaseUrl = RelayBaseUrl, credentialConfigured = !string.IsNullOrWhiteSpace(Credential),
        connectionStatus = ConnectionStatus, publicName = EffectivePublicName, persona = Persona, avatar = Avatar,
        avatarSource = AvatarSource, outfitsAvailable,
        avatarSyncStatus = AvatarSyncStatus, avatarSyncReason = AvatarSyncReason,
        avatarResolvedAssetId = AvatarResolvedAssetId, avatarSyncedAt = AvatarSyncedAt,
        scenarioToggles = ScenarioToggles, shareableFolders = ShareableFolders,
        personalWords = PersonalWords, canaries = Canaries, novaDiscussionId = NovaDiscussionId,
        linkAllowlist = LinkAllowlist, ownerUserId = OwnerUserId, agentEntityId = AgentEntityId
    };

    public static string PublicNameFromPersona(string? persona)
    {
        var value = persona?.Trim() ?? "";
        var separator = value.LastIndexOf(" · ", StringComparison.Ordinal);
        return separator >= 0 ? value[(separator + 3)..].Trim() : value;
    }
}

public sealed record CommonsOwnerSettings(
    Guid? EntityId,
    string RelayBaseUrl,
    string PublicName,
    IReadOnlyList<string> PersonalWords,
    IReadOnlyList<string> Canaries,
    IReadOnlyList<string> LinkAllowlist,
    string? OwnerUserId,
    string? RelayConfirmedPublicName = null)
{
    public object Public(IReadOnlyList<PublicAgentSettings> agents, bool outfitsAvailable) => new
    {
        relayBaseUrl = RelayBaseUrl,
        relayOverrideActive = CommonsRelayDefaults.IsCustom(RelayBaseUrl),
        publicName = PublicName,
        relayConfirmedPublicName = RelayConfirmedPublicName,
        personalWords = PersonalWords,
        canaries = Canaries,
        linkAllowlist = LinkAllowlist,
        ownerUserId = OwnerUserId,
        outfitsAvailable,
        agents = agents.Select(agent => agent.Public(PublicName)).ToArray(),
    };
}

public sealed record PublicAgentSettings(
    Guid? EntityId,
    string AgentEntityId,
    string AgentName,
    bool Enabled,
    string? Credential,
    string ConnectionStatus,
    string? Avatar,
    IReadOnlyDictionary<string, bool> ScenarioToggles,
    IReadOnlyList<string> ShareableFolders,
    string AvatarSource = AvatarSources.AgentAvatar,
    string? AvatarContentHash = null,
    string AvatarSyncStatus = "not-synced",
    string? AvatarSyncReason = null,
    string? AvatarResolvedAssetId = null,
    DateTimeOffset? AvatarSyncedAt = null,
    string? EnvoyId = null,
    bool RestrictedMode = false,
    string? SuspendedBy = null)
{
    public string DisplayPersona(string publicName) => $"{AgentName} \u00b7 {publicName}";

    public object Public(string publicName) => new
    {
        agentId = AgentEntityId,
        agentName = AgentName,
        enabled = Enabled,
        credentialConfigured = !string.IsNullOrWhiteSpace(Credential),
        connectionStatus = ConnectionStatus,
        persona = DisplayPersona(publicName),
        avatar = Avatar,
        avatarSource = AvatarSource,
        avatarSyncStatus = AvatarSyncStatus,
        avatarSyncReason = AvatarSyncReason,
        avatarResolvedAssetId = AvatarResolvedAssetId,
        avatarSyncedAt = AvatarSyncedAt,
        scenarioToggles = ScenarioToggles,
        shareableFolders = ShareableFolders,
        envoyId = EnvoyId,
        restrictedMode = RestrictedMode,
        suspendedBy = SuspendedBy,
    };
}

public sealed record CommonsConfiguration(
    CommonsOwnerSettings Owner,
    IReadOnlyList<PublicAgentSettings> Agents);

public sealed record StoredDigest(
    Guid Id, string Type, string Content, string? ThreadRef, bool Read,
    DateTimeOffset CreatedAt, string? AgentEntityId = null, string? SourceChannel = null,
    string? SourceMessageId = null);

public sealed record StoredBrief(
    Guid Id, string Kind, string? Title, string ContentHash, string Outcome,
    string? MessageId, string? Reason, DateTimeOffset CreatedAt,
    string? AgentEntityId = null);

public sealed record StoredAudit(
    Guid Id, string Event, JsonObject Metadata, DateTimeOffset CreatedAt);

public sealed class CommonsStateStore(IEntityStore entities) : ICommonsAudit
{
    public const string LegacyNovaAgentId = "a7b059ac-f678-8b58-819b-1bcf3acb22db";
    private static readonly TimeSpan OAuthStateLifetime = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim migration = new(1, 1);

    public async Task<CommonsConfiguration> GetConfigurationAsync(CancellationToken ct = default)
    {
        await EnsureMultiAgentSchemaAsync(ct);
        var ownerEntity = await entities.GetBySlugAsync("leaf-commons-settings", "settings", ct);
        var owner = ownerEntity is null ? DefaultOwner() : OwnerFromEntity(ownerEntity);
        var found = await entities.QueryAsync(new EntityQuery
            { TypeSlug = "leaf-commons-public-agent", Limit = 500 }, ct);
        var agents = found.Select(AgentFromEntity)
            .OrderBy(x => x.AgentName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.AgentEntityId, StringComparer.Ordinal)
            .ToArray();
        return new(owner, agents);
    }

    public async Task<CommonsOwnerSettings> GetOwnerSettingsAsync(CancellationToken ct = default)
        => (await GetConfigurationAsync(ct)).Owner;

    public async Task<IReadOnlyList<PublicAgentSettings>> ListPublicAgentsAsync(
        bool enabledOnly = false, CancellationToken ct = default)
    {
        var agents = (await GetConfigurationAsync(ct)).Agents;
        return enabledOnly ? agents.Where(x => x.Enabled).ToArray() : agents;
    }

    public async Task<PublicAgentSettings?> GetPublicAgentAsync(
        string agentEntityId, CancellationToken ct = default)
        => (await ListPublicAgentsAsync(ct: ct)).FirstOrDefault(x =>
            string.Equals(x.AgentEntityId, agentEntityId, StringComparison.OrdinalIgnoreCase));

    public async Task<CommonsOwnerSettings> SaveOwnerSettingsAsync(
        CommonsOwnerSettings value, CancellationToken ct = default)
    {
        var data = OwnerToData(value);
        var saved = value.EntityId is { } id
            ? await entities.ReplaceDataAsync(id, data, "Public Agent owner settings", ct)
            : await entities.UpsertBySlugAsync("leaf-commons-settings", "settings",
                "Public Agent owner settings", data, ct);
        return OwnerFromEntity(saved);
    }

    public async Task<PublicAgentSettings> SavePublicAgentAsync(
        PublicAgentSettings value, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(value.AgentEntityId))
            throw new ArgumentException("Public Agent records require an Agent entity ID", nameof(value));
        var slug = PublicAgentSlug(value.AgentEntityId);
        var saved = await entities.UpsertBySlugAsync("leaf-commons-public-agent", slug,
            $"Public Agent: {value.AgentName}", AgentToData(value), ct);
        return AgentFromEntity(saved);
    }

    public async Task<CommonsSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        var configuration = await GetConfigurationAsync(ct);
        var agent = configuration.Agents.FirstOrDefault(x =>
                string.Equals(x.AgentEntityId, LegacyNovaAgentId, StringComparison.OrdinalIgnoreCase))
            ?? configuration.Agents.FirstOrDefault();
        return ToLegacy(configuration.Owner, agent);
    }

    public async Task<CommonsSettings> SaveSettingsAsync(CommonsSettings value, CancellationToken ct = default)
    {
        var current = await GetOwnerSettingsAsync(ct);
        var owner = await SaveOwnerSettingsAsync(new(value.EntityId, value.RelayBaseUrl,
            value.EffectivePublicName, value.PersonalWords, value.Canaries,
            value.LinkAllowlist, value.OwnerUserId, current.RelayConfirmedPublicName), ct);
        var agentId = string.IsNullOrWhiteSpace(value.AgentEntityId)
            ? LegacyNovaAgentId : value.AgentEntityId;
        var agent = await SavePublicAgentAsync(new(null, agentId!,
            string.IsNullOrWhiteSpace(value.Persona) ? "Agent" : AgentNameFromPersona(value.Persona, owner.PublicName),
            true, value.Credential, value.ConnectionStatus, value.Avatar,
            value.ScenarioToggles, value.ShareableFolders, value.AvatarSource,
            value.AvatarContentHash, value.AvatarSyncStatus, value.AvatarSyncReason,
            value.AvatarResolvedAssetId, value.AvatarSyncedAt, value.EnvoyId), ct);
        return ToLegacy(owner, agent);
    }

    public async Task<StoredBrief> RecordBriefAsync(
        string kind, string? title, string content, string outcome,
        string? messageId, string? reason, CancellationToken ct = default,
        string? agentEntityId = null)
    {
        agentEntityId ??= (await ListPublicAgentsAsync(enabledOnly: true, ct: ct))
            .Select(agent => agent.AgentEntityId).SingleOrDefault();
        if (outcome is not ("posted" or "blocked" or "rejected"))
            throw new ArgumentException("Brief outcome must be posted, blocked, or rejected", nameof(outcome));
        var createdAt = DateTimeOffset.UtcNow;
        var brief = await entities.CreateAsync("leaf-commons-brief", $"{kind} {outcome}", new JsonObject
        {
            ["kind"] = kind,
            ["title"] = title,
            ["content_hash"] = ContentHash(content),
            ["outcome"] = outcome,
            ["message_id"] = messageId,
            ["reason"] = reason,
            ["agent_entity_id"] = agentEntityId,
            ["created_at"] = createdAt.ToString("O")
        }, ct);
        return ToBrief(brief);
    }

    public async Task<IReadOnlyList<StoredBrief>> ListBriefsAsync(CancellationToken ct = default)
    {
        var found = await entities.QueryAsync(new EntityQuery
            { TypeSlug = "leaf-commons-brief", Limit = 500 }, ct);
        return found.Select(ToBrief)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .ToArray();
    }

    public async Task<StoredDigest> StoreDigestAsync(
        string type, string content, string? threadRef, CancellationToken ct = default,
        string? agentEntityId = null, string? sourceChannel = null,
        string? sourceMessageId = null)
    {
        agentEntityId ??= (await ListPublicAgentsAsync(enabledOnly: true, ct: ct))
            .Select(agent => agent.AgentEntityId).SingleOrDefault();
        if (type is not ("consult_answer" or "announcement" or "owner_notice"))
            throw new ArgumentException("Unknown digest type", nameof(type));
        if (string.IsNullOrWhiteSpace(content) || content.Length > 1200)
            throw new ArgumentException("Digest content must be 1-1200 characters", nameof(content));
        if (threadRef?.Length > 200)
            throw new ArgumentException("Digest thread reference must be at most 200 characters", nameof(threadRef));
        if (string.IsNullOrWhiteSpace(threadRef) && string.IsNullOrWhiteSpace(sourceChannel))
            throw new ArgumentException("A digest requires a thread reference or source channel");
        if (sourceChannel?.Length > 100)
            throw new ArgumentException("Digest source channel must be at most 100 characters", nameof(sourceChannel));
        if (sourceMessageId?.Length > 200)
            throw new ArgumentException("Digest source message ID must be at most 200 characters", nameof(sourceMessageId));
        var createdAt = DateTimeOffset.UtcNow;
        if (type == "announcement" && !string.IsNullOrWhiteSpace(sourceMessageId))
        {
            var matches = await entities.QueryAsync(new EntityQuery
            {
                TypeSlug = "leaf-commons-digest",
                DataEquals = new Dictionary<string, object?>
                {
                    ["digest_type"] = type,
                    ["agent_entity_id"] = agentEntityId,
                    ["source_message_id"] = sourceMessageId,
                },
                Limit = 2,
            }, ct);
            var existing = matches.OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
            if (existing is not null)
            {
                return ToDigest(await entities.PatchAsync(existing.Id, new JsonObject
                {
                    ["content"] = content,
                    ["source_channel"] = sourceChannel,
                    ["read"] = false,
                    ["created_at"] = createdAt.ToString("O"),
                }, ct: ct));
            }
        }
        var entity = await entities.CreateAsync("leaf-commons-digest", $"{type.Replace('_', ' ')} digest", new JsonObject
        {
            ["digest_type"] = type,
            ["content"] = content,
            ["thread_ref"] = threadRef,
            ["source_channel"] = sourceChannel,
            ["agent_entity_id"] = agentEntityId,
            ["source_message_id"] = sourceMessageId,
            ["read"] = false,
            ["created_at"] = createdAt.ToString("O")
        }, ct);
        return ToDigest(entity);
    }

    public async Task<IReadOnlyList<StoredDigest>> ListDigestsAsync(
        bool unreadOnly = false, CancellationToken ct = default,
        string? agentEntityId = null)
    {
        var entitiesFound = await entities.QueryAsync(new EntityQuery
            { TypeSlug = "leaf-commons-digest", Limit = 500 }, ct);
        return entitiesFound.Select(ToDigest)
            .Where(x => (!unreadOnly || !x.Read)
                && (agentEntityId is null || string.Equals(x.AgentEntityId, agentEntityId,
                    StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .ToArray();
    }

    public async Task<IReadOnlyList<StoredDigest>> ListDigestsForThreadAsync(
        string threadRef, CancellationToken ct = default, string? agentEntityId = null)
    {
        var all = await ListDigestsAsync(ct: ct, agentEntityId: agentEntityId);
        return all.Where(item => string.Equals(item.ThreadRef, threadRef, StringComparison.Ordinal)).ToArray();
    }

    public async Task<IReadOnlyList<StoredAudit>> ListAuditsAsync(CancellationToken ct = default)
    {
        var found = await entities.QueryAsync(new EntityQuery
            { TypeSlug = "leaf-commons-audit", Limit = 500 }, ct);
        return found.Select(ToAudit)
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
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

    public Task<OAuthStateIssue> CreateOAuthStateAsync(string ownerId, bool allowActiveOverwrite,
        DateTimeOffset? now = null, CancellationToken ct = default)
        => CreateOAuthStateAsync(ownerId, LegacyNovaAgentId, allowActiveOverwrite, now, ct);

    public async Task<OAuthStateIssue> CreateOAuthStateAsync(string ownerId, string agentEntityId,
        bool allowActiveOverwrite, DateTimeOffset? now = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("OAuth state requires an owner", nameof(ownerId));
        if (!Guid.TryParse(agentEntityId, out _)) throw new ArgumentException("OAuth state requires an Agent entity GUID", nameof(agentEntityId));
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var slug = OAuthStateSlug(token);
        var issuedAt = now ?? DateTimeOffset.UtcNow;
        await entities.UpsertBySlugAsync("leaf-commons-oauth-state", slug, "Leaf Community OAuth state", new JsonObject
        {
            ["owner_id"] = ownerId,
            ["agent_entity_id"] = agentEntityId,
            ["expires_at"] = issuedAt.Add(OAuthStateLifetime).ToString("O"),
            ["consumed"] = false,
            ["allow_active_overwrite"] = allowActiveOverwrite
        }, ct);
        return new(token, issuedAt.Add(OAuthStateLifetime), agentEntityId);
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
        return new(OAuthStateStatus.Accepted,
            entity.Data["allow_active_overwrite"]?.GetValue<bool>() == true,
            S(entity.Data, "agent_entity_id") ?? LegacyNovaAgentId);
    }

    public async Task RecordAsync(string eventName, IReadOnlyDictionary<string, object?> safeMetadata, CancellationToken ct = default)
    {
        var metadata = new JsonObject();
        foreach (var pair in safeMetadata) metadata[pair.Key] = pair.Value is null ? null : JsonSerializer.SerializeToNode(pair.Value);
        await entities.CreateAsync("leaf-commons-audit", eventName, new JsonObject { ["event"] = eventName, ["metadata"] = metadata, ["occurred_at"] = DateTimeOffset.UtcNow }, ct);
    }

    private async Task EnsureMultiAgentSchemaAsync(CancellationToken ct)
    {
        await migration.WaitAsync(ct);
        try
        {
            var entity = await entities.GetBySlugAsync("leaf-commons-settings", "settings", ct);
            if (entity is null || entity.Data["schema_version"]?.GetValue<int?>() == 2) return;

            var legacy = FromEntity(entity);
            var legacyPublicName = legacy.EffectivePublicName;
            var owner = new CommonsOwnerSettings(entity.Id, legacy.RelayBaseUrl,
                "", legacy.PersonalWords, legacy.Canaries,
                legacy.LinkAllowlist, legacy.OwnerUserId);
            var agentId = Guid.TryParse(legacy.AgentEntityId, out var parsed)
                ? parsed : Guid.Parse(LegacyNovaAgentId);
            var agentName = AgentNameFromPersona(legacy.Persona, legacyPublicName);
            await entities.UpsertBySlugAsync("leaf-commons-public-agent", PublicAgentSlug(agentId.ToString()),
                $"Public Agent: {agentName}", AgentToData(new(null, agentId.ToString(), agentName,
                    true, legacy.Credential, legacy.ConnectionStatus, legacy.Avatar,
                    legacy.ScenarioToggles, legacy.ShareableFolders, legacy.AvatarSource,
                    legacy.AvatarContentHash, legacy.AvatarSyncStatus, legacy.AvatarSyncReason,
                    legacy.AvatarResolvedAssetId, legacy.AvatarSyncedAt, legacy.EnvoyId)), ct);
            await entities.ReplaceDataAsync(entity.Id, OwnerToData(owner),
                "Public Agent owner settings", ct);
        }
        finally
        {
            migration.Release();
        }
    }

    private static CommonsOwnerSettings DefaultOwner() => new(null,
        CommonsRelayDefaults.ProductionBaseUrl, "", [], [], [], null);

    private static CommonsOwnerSettings OwnerFromEntity(LeafEntity entity)
    {
        var data = entity.Data;
        return new(entity.Id, CommonsRelayDefaults.EffectiveBaseUrl(S(data, "relay_base_url")),
            S(data, "public_name") ?? "", List(data["personal_words"]),
            List(data["canaries"]), List(data["link_allowlist"]), S(data, "owner_user_id"),
            S(data, "relay_confirmed_public_name"));
    }

    private static JsonObject OwnerToData(CommonsOwnerSettings value) => new()
    {
        ["schema_version"] = 2,
        ["relay_base_url"] = CommonsRelayDefaults.StoredValue(value.RelayBaseUrl),
        ["public_name"] = value.PublicName,
        ["relay_confirmed_public_name"] = value.RelayConfirmedPublicName,
        ["personal_words"] = JsonSerializer.SerializeToNode(value.PersonalWords),
        ["canaries"] = JsonSerializer.SerializeToNode(value.Canaries),
        ["link_allowlist"] = JsonSerializer.SerializeToNode(value.LinkAllowlist),
        ["owner_user_id"] = value.OwnerUserId,
    };

    private static PublicAgentSettings AgentFromEntity(LeafEntity entity)
    {
        var data = entity.Data;
        var agentId = S(data, "agent_entity_id") ?? "";
        var agentName = S(data, "agent_name") ?? "Agent";
        return new(entity.Id, agentId, agentName,
            data["enabled"]?.GetValue<bool?>() == true,
            S(data, "envoy_credential"), S(data, "connection_status") ?? "disconnected",
            S(data, "avatar"),
            Dict(data["scenario_toggles"]), List(data["shareable_folders"]),
            S(data, "avatar_source") is { } source && AvatarSources.IsValid(source)
                ? source : AvatarSources.AgentAvatar,
            S(data, "avatar_content_hash"), S(data, "avatar_sync_status") ?? "not-synced",
            S(data, "avatar_sync_reason"), S(data, "avatar_resolved_asset_id"),
            Date(data, "avatar_synced_at"), S(data, "envoy_id"),
            data["restricted_mode"]?.GetValue<bool?>() == true,
            SuspensionSources.Normalize(S(data, "suspended_by")));
    }

    private static JsonObject AgentToData(PublicAgentSettings value) => new()
    {
        ["agent_entity_id"] = value.AgentEntityId,
        ["agent_name"] = value.AgentName,
        ["enabled"] = value.Enabled,
        ["envoy_credential"] = value.Credential,
        ["connection_status"] = value.ConnectionStatus,
        ["avatar"] = value.Avatar,
        ["scenario_toggles"] = JsonSerializer.SerializeToNode(value.ScenarioToggles),
        ["shareable_folders"] = JsonSerializer.SerializeToNode(value.ShareableFolders),
        ["avatar_source"] = value.AvatarSource,
        ["avatar_content_hash"] = value.AvatarContentHash,
        ["avatar_sync_status"] = value.AvatarSyncStatus,
        ["avatar_sync_reason"] = value.AvatarSyncReason,
        ["avatar_resolved_asset_id"] = value.AvatarResolvedAssetId,
        ["avatar_synced_at"] = value.AvatarSyncedAt?.ToString("O"),
        ["envoy_id"] = value.EnvoyId,
        ["restricted_mode"] = value.RestrictedMode,
        ["suspended_by"] = value.SuspendedBy,
    };

    private static CommonsSettings ToLegacy(CommonsOwnerSettings owner, PublicAgentSettings? agent)
        => new(owner.EntityId, owner.RelayBaseUrl, agent?.Credential,
            agent?.ConnectionStatus ?? "disconnected", agent?.DisplayPersona(owner.PublicName) ?? "Agent", agent?.Avatar,
            agent?.ScenarioToggles ?? new Dictionary<string, bool>(),
            agent?.ShareableFolders ?? [], owner.PersonalWords, owner.Canaries, null,
            owner.LinkAllowlist, owner.OwnerUserId, agent?.AgentEntityId,
            agent?.AvatarSource ?? AvatarSources.AgentAvatar, agent?.AvatarContentHash,
            agent?.AvatarSyncStatus ?? "not-synced", agent?.AvatarSyncReason,
            agent?.AvatarResolvedAssetId, agent?.AvatarSyncedAt, owner.PublicName, agent?.EnvoyId);

    private static string AgentNameFromPersona(string? persona, string publicName)
    {
        var value = persona?.Trim() ?? "";
        var suffix = " · " + publicName;
        return suffix.Length > 3 && value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? value[..^suffix.Length].Trim()
            : value.Contains(" · ", StringComparison.Ordinal)
                ? value[..value.IndexOf(" · ", StringComparison.Ordinal)].Trim()
                : string.IsNullOrWhiteSpace(value) ? "Agent" : value;
    }

    private static string PublicAgentSlug(string agentId)
        => Guid.TryParse(agentId, out var parsed)
            ? "agent-" + parsed.ToString("N")
            : "agent-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                agentId.Trim().ToLowerInvariant()))).ToLowerInvariant();

    private static CommonsSettings FromEntity(LeafEntity e)
    {
        var d = e.Data;
        return new(e.Id, CommonsRelayDefaults.EffectiveBaseUrl(S(d, "relay_base_url")), S(d, "envoy_credential"), S(d, "connection_status") ?? "disconnected",
            S(d, "persona") ?? "Nova", S(d, "avatar"), Dict(d["scenario_toggles"]), List(d["shareable_folders"]), List(d["personal_words"]), List(d["canaries"]),
            S(d, "nova_discussion_id"), List(d["link_allowlist"]), S(d, "owner_user_id"), S(d, "agent_entity_id"),
            S(d, "avatar_source") is { } source && AvatarSources.IsValid(source) ? source : AvatarSources.AgentAvatar,
            S(d, "avatar_content_hash"), S(d, "avatar_sync_status") ?? "not-synced",
            S(d, "avatar_sync_reason"), S(d, "avatar_resolved_asset_id"), Date(d, "avatar_synced_at"),
            S(d, "public_name") ?? CommonsSettings.PublicNameFromPersona(S(d, "persona")), S(d, "envoy_id"));
    }
    private static JsonObject ToData(CommonsSettings x) => new()
    {
        ["relay_base_url"] = CommonsRelayDefaults.StoredValue(x.RelayBaseUrl), ["envoy_credential"] = x.Credential, ["connection_status"] = x.ConnectionStatus,
        ["persona"] = x.Persona, ["avatar"] = x.Avatar, ["scenario_toggles"] = JsonSerializer.SerializeToNode(x.ScenarioToggles),
        ["shareable_folders"] = JsonSerializer.SerializeToNode(x.ShareableFolders), ["personal_words"] = JsonSerializer.SerializeToNode(x.PersonalWords),
        ["canaries"] = JsonSerializer.SerializeToNode(x.Canaries), ["nova_discussion_id"] = x.NovaDiscussionId,
        ["link_allowlist"] = JsonSerializer.SerializeToNode(x.LinkAllowlist), ["owner_user_id"] = x.OwnerUserId, ["agent_entity_id"] = x.AgentEntityId,
        ["avatar_source"] = x.AvatarSource, ["avatar_content_hash"] = x.AvatarContentHash,
        ["avatar_sync_status"] = x.AvatarSyncStatus, ["avatar_sync_reason"] = x.AvatarSyncReason,
        ["avatar_resolved_asset_id"] = x.AvatarResolvedAssetId,
        ["avatar_synced_at"] = x.AvatarSyncedAt?.ToString("O"),
        ["public_name"] = x.PublicName, ["envoy_id"] = x.EnvoyId
    };
    private static string? S(JsonObject d, string key) => d[key]?.GetValue<string>();
    private static DateTimeOffset? Date(JsonObject d, string key)
        => DateTimeOffset.TryParse(S(d, key), out var value) ? value : null;
    private static IReadOnlyList<string> List(JsonNode? n) => n is JsonArray a ? a.Select(x => x?.GetValue<string>()).Where(x => x is not null).Cast<string>().ToArray() : [];
    private static IReadOnlyDictionary<string, bool> Dict(JsonNode? n) => n is JsonObject o ? o.ToDictionary(x => x.Key, x => x.Value?.GetValue<bool>() ?? false) : new Dictionary<string, bool>();
    private static StoredDigest ToDigest(LeafEntity entity)
    {
        var createdAt = DateTimeOffset.TryParse(S(entity.Data, "created_at"), out var parsed)
            ? parsed
            : entity.CreatedAt;
        var sourceChannel = S(entity.Data, "source_channel");
        var threadRef = S(entity.Data, "thread_ref");
        if (threadRef is null && sourceChannel is null) threadRef = "unknown";
        return new(entity.Id, S(entity.Data, "digest_type") ?? "owner_notice",
            S(entity.Data, "content") ?? "", threadRef,
            entity.Data["read"]?.GetValue<bool>() == true, createdAt,
            S(entity.Data, "agent_entity_id"), sourceChannel,
            S(entity.Data, "source_message_id"));
    }

    private static StoredBrief ToBrief(LeafEntity entity)
    {
        var createdAt = DateTimeOffset.TryParse(S(entity.Data, "created_at"), out var parsed)
            ? parsed
            : entity.CreatedAt;
        var legacyContent = S(entity.Data, "content");
        var storedOutcome = S(entity.Data, "outcome");
        var outcome = storedOutcome ?? "legacy";
        var reason = S(entity.Data, "reason")
            ?? (storedOutcome is null ? "pre-immediate-dispatch-history" : null);
        return new(entity.Id, S(entity.Data, "kind") ?? "unknown", S(entity.Data, "title"),
            S(entity.Data, "content_hash") ?? ContentHash(legacyContent ?? ""), outcome,
            S(entity.Data, "message_id"), reason, createdAt,
            S(entity.Data, "agent_entity_id"));
    }

    private static StoredAudit ToAudit(LeafEntity entity)
    {
        var occurredAt = entity.Data["occurred_at"] switch
        {
            JsonValue value when value.TryGetValue<DateTimeOffset>(out var direct) => direct,
            JsonValue value when value.TryGetValue<string>(out var text)
                && DateTimeOffset.TryParse(text, out var parsed) => parsed,
            _ => entity.CreatedAt,
        };
        return new(entity.Id, S(entity.Data, "event") ?? entity.Name,
            entity.Data["metadata"] as JsonObject ?? [], occurredAt);
    }

    public static string ContentHash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    private static string OAuthStateSlug(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return "oauth-state-" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// Who suspended an Envoy on the relay (its <c>suspendedBy</c>). Only <see cref="Owner"/> can be
/// lifted from here; the others need the server host.
/// </summary>
public static class SuspensionSources
{
    public const string Owner = "owner";
    public const string Host = "host";
    public const string Filter = "filter";
    public const string Membership = "membership";

    public static string? Normalize(string? value) => value switch
    {
        Owner or Host or Filter or Membership => value,
        _ => null,
    };

    /// <summary>The source an owner notice reports, from <c>suspendedBy</c> or its reason code.</summary>
    public static string? FromNotice(string? suspendedBy, string? reason)
        => Normalize(suspendedBy) ?? reason switch
        {
            "admin_suspended" => Host,
            "membership_suspended" => Membership,
            "auto_suspended_filter_violations" => Filter,
            _ => null,
        };
}

public sealed record OAuthStateIssue(string State, DateTimeOffset ExpiresAt, string? AgentEntityId = null);
public enum OAuthStateStatus { Accepted, Missing, Unknown, Expired, Reused, OtherUser }
public sealed record OAuthStateConsumption(OAuthStateStatus Status, bool AllowActiveOverwrite = false,
    string? AgentEntityId = null);

public sealed record LeafAgentCaller(string AgentEntityId, string BeneficiaryId);

public static class OwnerGuard
{
    public static LeafAgentCaller? ReadPrivateAgent(ClaimsPrincipal user, CommonsOwnerSettings ownerSettings)
    {
        var caller = ReadPrivateAgent(user);
        return caller is not null
            && string.Equals(ownerSettings.OwnerUserId, caller.BeneficiaryId, StringComparison.Ordinal)
            ? caller : null;
    }

    public static LeafAgentCaller? ReadPrivateAgent(ClaimsPrincipal user)
    {
        if (!string.Equals(user.FindFirst("token_use")?.Value, "execution", StringComparison.OrdinalIgnoreCase)) return null;
        var raw = user.FindFirst("execution_identity")?.Value;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var actor = root.GetProperty("actor"); var beneficiary = root.GetProperty("beneficiary");
            if (actor.GetProperty("kind").GetString() != "agent" || beneficiary.GetProperty("kind").GetString() != "user") return null;
            var agent = actor.GetProperty("entityId").GetString(); var owner = beneficiary.GetProperty("id").GetString();
            var hasDiscussion = false; var hasExternal = false;
            foreach (var context in root.GetProperty("context").EnumerateArray())
            {
                var kind = context.GetProperty("kind").GetString();
                hasDiscussion |= kind == "discussion"; hasExternal |= kind == "external-conversation";
            }
            return !hasDiscussion || hasExternal || agent is null || owner is null
                ? null : new(agent, owner);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { return null; }
    }

    public static LeafAgentCaller? ReadPrivateNovaAgent(ClaimsPrincipal user, CommonsSettings settings)
    {
        var caller = ReadPrivateAgent(user);
        return caller is not null
            && string.Equals(settings.OwnerUserId, caller.BeneficiaryId, StringComparison.Ordinal)
            && string.Equals(settings.AgentEntityId, caller.AgentEntityId, StringComparison.OrdinalIgnoreCase)
            ? caller : null;
    }
}
