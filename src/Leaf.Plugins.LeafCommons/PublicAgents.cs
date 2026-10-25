using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.LeafCommons;

public sealed record OwnedAgent(string AgentEntityId, string Name, string? AvatarUrl);

/// <summary>What lifting an Agent's relay suspension did, per Agent, for the settings response.</summary>
/// <param name="Status"><c>resumed</c>; <c>suspended</c> (a host, filter or membership suspension
/// is still in place, so the Agent stays off); or <c>failed</c> (the relay could not be asked).</param>
public sealed record EnvoyResumeResult(string AgentId, string Status, string? SuspendedBy = null, string? Reason = null);

public static class EnvoyResumeStatuses
{
    public const string Resumed = "resumed";
    public const string Suspended = "suspended";
    public const string Failed = "failed";
}

public sealed class AgentDirectory(IEntityStore entities)
{
    public async Task<IReadOnlyList<OwnedAgent>> ListAsync(string ownerUserId, CancellationToken ct = default)
    {
        var found = await entities.QueryAsync(new EntityQuery { TypeSlug = "agent", Limit = 500 }, ct);
        return found.Where(agent => OwnedBy(agent, ownerUserId))
            .Select(agent => new OwnedAgent(agent.Id.ToString(), agent.Name, Avatar(agent.Data)))
            .OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(agent => agent.AgentEntityId, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<OwnedAgent?> GetAsync(string agentEntityId, string ownerUserId,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(agentEntityId, out var id)) return null;
        var entity = await entities.GetAsync(id, ct);
        return entity?.TypeSlug == "agent" && OwnedBy(entity, ownerUserId)
            ? new(entity.Id.ToString(), entity.Name, Avatar(entity.Data)) : null;
    }

    internal static bool OwnedBy(LeafEntity entity, string ownerUserId)
    {
        // Agent entities created before account-backed ownership use local-user for
        // the installation owner. Plugin-owned Agents carry the kernel's canonical
        // owner_plugin scope stamp and belong to their project/application even when
        // that plugin also records the human owner_id.
        if (!string.IsNullOrWhiteSpace(Text(entity.Data, "owner_plugin"))) return false;
        var explicitOwner = Text(entity.Data, "owner_id");
        if (!string.IsNullOrWhiteSpace(explicitOwner))
            return IsOwnerSubject(explicitOwner, ownerUserId);
        var creator = entity.CreatedBy;
        if (string.IsNullOrWhiteSpace(creator)
            || creator.Equals("system", StringComparison.OrdinalIgnoreCase)
            || creator.StartsWith("system:", StringComparison.OrdinalIgnoreCase)
            || creator.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase))
            return false;
        return IsOwnerSubject(creator, ownerUserId);
    }

    private static bool IsOwnerSubject(string subject, string ownerUserId)
        => subject.Equals(ownerUserId, StringComparison.OrdinalIgnoreCase)
            || subject.Equals("local-user", StringComparison.OrdinalIgnoreCase);

    private static string? Avatar(JsonObject data)
    {
        if (Text(data, "avatar") is { } direct) return direct;
        return data["avatar"] is JsonObject nested
            ? Text(nested, "url") ?? Text(nested, "filename") : null;
    }

    private static string? Text(JsonObject data, string key)
        => data[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

public sealed class AgentShareableStores(string pluginDataRoot)
{
    public ShareableStore For(string agentEntityId)
    {
        var path = string.Equals(agentEntityId, CommonsStateStore.LegacyNovaAgentId,
            StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(pluginDataRoot, "shareable")
            : Path.Combine(pluginDataRoot, "shareable-agents", StableSegment(agentEntityId));
        return new ShareableStore(path);
    }

    private static string StableSegment(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            value.Trim().ToLowerInvariant()))).ToLowerInvariant()[..24];
}

public sealed class RelayClientFactory(IHttpClientFactory clients, CommonsStateStore state)
{
    public RelayHttpClient For(string agentEntityId) => new(clients.CreateClient("commons-relay"), async ct =>
    {
        var configuration = await state.GetConfigurationAsync(ct).ConfigureAwait(false);
        var agent = configuration.Agents.FirstOrDefault(item => string.Equals(
            item.AgentEntityId, agentEntityId, StringComparison.OrdinalIgnoreCase));
        return (new Uri(CommonsRelayDefaults.EffectiveBaseUrl(configuration.Owner.RelayBaseUrl)), agent?.Credential);
    });

    public RelayHttpClient ForEnrollment(string agentEntityId) => new(clients.CreateClient("commons-relay"), async ct =>
    {
        var owner = await state.GetOwnerSettingsAsync(ct).ConfigureAwait(false);
        return (new Uri(CommonsRelayDefaults.EffectiveBaseUrl(owner.RelayBaseUrl)), (string?)null);
    });
}

public sealed class PublicAgentAvatarSync(
    CommonsStateStore state,
    IAvatarImageSource source,
    AvatarImageProcessor processor,
    RelayClientFactory relays,
    IPluginEvents events,
    ICommonsAudit audit) : IDisposable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDisposable> subscriptions = [];

    public async Task StartAsync(CancellationToken stopping)
    {
        subscriptions.Add(events.Subscribe("agent.avatar-changed", OnAvatarChangedAsync));
        subscriptions.Add(events.Subscribe("entity.updated", OnEntityUpdatedAsync));
        stopping.Register(Dispose);
        foreach (var agent in await state.ListPublicAgentsAsync(enabledOnly: true, ct: stopping))
            if (!string.IsNullOrWhiteSpace(agent.Credential)) _ = TrySyncAsync(agent.AgentEntityId, stopping);
    }

    public Task<bool> IsOutfitsAvailableAsync(CancellationToken ct = default)
        => source.IsOutfitsAvailableAsync(ct);

    public async Task<AvatarSyncResult> SyncAsync(string agentEntityId, CancellationToken ct = default)
    {
        var gate = gates.GetOrAdd(agentEntityId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        PublicAgentSettings? settings = null;
        try
        {
            settings = await state.GetPublicAgentAsync(agentEntityId, ct)
                ?? throw new AvatarSyncException("agent_not_public");
            if (!settings.Enabled) throw new AvatarSyncException("agent_not_public");
            if (string.IsNullOrWhiteSpace(settings.Credential)) throw new AvatarSyncException("envoy_credential_missing");
            var image = await source.ResolveAsync(settings.AvatarSource, settings.AgentEntityId, ct);
            var prepared = processor.Prepare(image);
            if (string.Equals(prepared.ContentHash, settings.AvatarContentHash, StringComparison.Ordinal))
            {
                await state.SavePublicAgentAsync(settings with
                {
                    AvatarSyncStatus = "current", AvatarSyncReason = null,
                    AvatarResolvedAssetId = image.AssetId, AvatarSyncedAt = DateTimeOffset.UtcNow,
                }, ct);
                return new(false, prepared.ContentHash);
            }
            var uploaded = await relays.For(agentEntityId).UploadAvatarAsync(
                prepared.Bytes, prepared.ContentType, ct);
            if (!string.Equals(uploaded.ContentHash, prepared.ContentHash, StringComparison.Ordinal))
                throw new AvatarSyncException("relay_avatar_hash_mismatch");
            await state.SavePublicAgentAsync(settings with
            {
                Avatar = uploaded.AvatarKey, AvatarContentHash = prepared.ContentHash,
                AvatarSyncStatus = "synced", AvatarSyncReason = null,
                AvatarResolvedAssetId = image.AssetId, AvatarSyncedAt = DateTimeOffset.UtcNow,
            }, ct);
            await audit.RecordAsync("avatar_synced", new Dictionary<string, object?>
            {
                ["agent_entity_id"] = agentEntityId, ["source"] = settings.AvatarSource,
                ["asset_id"] = image.AssetId, ["content_hash"] = prepared.ContentHash,
                ["changed"] = uploaded.Changed,
            }, ct);
            return new(true, prepared.ContentHash, uploaded.AvatarUrl);
        }
        catch (AvatarSyncException error)
        {
            if (settings is not null)
                await state.SavePublicAgentAsync(settings with
                    { AvatarSyncStatus = "failed", AvatarSyncReason = error.Reason }, CancellationToken.None);
            await audit.RecordAsync("avatar_sync_failed", new Dictionary<string, object?>
                { ["agent_entity_id"] = agentEntityId, ["reason"] = error.Reason }, CancellationToken.None);
            throw;
        }
        finally { gate.Release(); }
    }

    private Task OnAvatarChangedAsync(PluginEvent evt)
        => evt.Payload["agentId"]?.GetValue<string>() is { } agentId
            ? TrySyncAsync(agentId, CancellationToken.None) : Task.CompletedTask;

    private Task OnEntityUpdatedAsync(PluginEvent evt)
        => evt.Payload["typeSlug"]?.GetValue<string>() == "agent"
            && evt.Payload["id"]?.GetValue<string>() is { } agentId
            ? TrySyncAsync(agentId, CancellationToken.None) : Task.CompletedTask;

    private async Task TrySyncAsync(string agentEntityId, CancellationToken ct)
    {
        var settings = await state.GetPublicAgentAsync(agentEntityId, ct);
        if (settings is null || !settings.Enabled || string.IsNullOrWhiteSpace(settings.Credential)) return;
        try { await SyncAsync(agentEntityId, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { }
    }

    public void Dispose()
    {
        foreach (var subscription in subscriptions) subscription.Dispose();
        subscriptions.Clear();
        foreach (var gate in gates.Values) gate.Dispose();
        gates.Clear();
    }
}

public sealed class PublicAgentCoordinator(
    CommonsStateStore state,
    AgentDirectory directory,
    RelayClientFactory relays,
    PublicAgentRuntime runtime,
    IPluginEvents events,
    ICommonsAudit audit,
    ILoggerFactory loggers,
    CommonsAgentSkills? skills = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, RelayConnector> connectors = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Agents whose suspension was already asked to lift since they last connected: a
    /// refused connection never asks twice, so a suspended Envoy cannot cause a retry storm.</summary>
    private readonly ConcurrentDictionary<string, byte> resumeAttempted = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDisposable> subscriptions = [];
    private readonly ILogger logger = loggers.CreateLogger<PublicAgentCoordinator>();
    private CancellationToken stopping;

    /// <summary>A refused connection to a pending Envoy is retried this often.</summary>
    internal TimeSpan PendingRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    public async Task StartAsync(CancellationToken applicationStopping)
    {
        stopping = applicationStopping;
        subscriptions.Add(events.Subscribe("entity.updated", OnEntityUpdatedAsync));
        subscriptions.Add(events.Subscribe("entity.deleted", OnEntityDeletedAsync));
        foreach (var agent in await state.ListPublicAgentsAsync(enabledOnly: true, ct: applicationStopping))
        {
            // An Agent left enabled on a suspended Envoy (switched off, then on, before resuming
            // existed) gets one attempt to lift it instead of looping on refused connections.
            if (!string.IsNullOrWhiteSpace(agent.Credential) && NeedsResume(agent)
                && (await ResumeSuspendedAsync(agent.AgentEntityId, applicationStopping)).Status
                    == EnvoyResumeStatuses.Suspended)
                continue;
            await ReconcileRelayProfileSafeAsync(agent.AgentEntityId);
            await SyncAgentNameSafeAsync(agent.AgentEntityId);
        }
        await ReconcileSkillsAsync(applicationStopping);
        await ReconcileAsync(applicationStopping);
    }

    /// <summary>
    /// Adds or removes the Leaf Community skill on one Agent for the configured owner. Null when
    /// skill assignment is not wired (tests) or no owner is configured yet.
    /// </summary>
    public async Task<SkillAssignment?> AssignSkillAsync(string agentEntityId, bool add,
        CancellationToken ct = default)
    {
        if (skills is null) return null;
        var owner = (await state.GetOwnerSettingsAsync(ct)).OwnerUserId;
        if (string.IsNullOrWhiteSpace(owner)) return null;
        var result = add
            ? await skills.AddAsync(agentEntityId, owner, ct)
            : await skills.RemoveAsync(agentEntityId, owner, ct);
        if (result.Status is SkillAssignmentStatus.Added or SkillAssignmentStatus.Removed
            or SkillAssignmentStatus.ManualRequired or SkillAssignmentStatus.Failed)
            await audit.RecordAsync("agent_skill_assignment", new Dictionary<string, object?>
                { ["agent_entity_id"] = agentEntityId, ["status"] = result.Status }, ct);
        return result;
    }

    /// <summary>
    /// Agents made public before Leaf Community shipped its skill get it once at startup: only
    /// Agents the owner enabled and still owns, and only ever added. Removal stays tied to the
    /// owner disabling an Agent or the Agent being removed.
    /// </summary>
    public async Task ReconcileSkillsAsync(CancellationToken ct = default)
    {
        if (skills is null) return;
        var configuration = await state.GetConfigurationAsync(ct);
        var owner = configuration.Owner.OwnerUserId;
        if (string.IsNullOrWhiteSpace(owner)) return;
        foreach (var agent in configuration.Agents.Where(agent => agent.Enabled))
        {
            if (await directory.GetAsync(agent.AgentEntityId, owner, ct) is null) continue;
            await AssignSkillAsync(agent.AgentEntityId, add: true, ct);
        }
    }

    /// <summary>
    /// The local state of an Agent whose Envoy may be suspended on the relay: switched off here,
    /// or enabled but stuck reconnecting. Asking to resume an Envoy that is not suspended is a
    /// no-op on the relay.
    /// </summary>
    internal bool HasConnector(string agentEntityId) => connectors.ContainsKey(agentEntityId);

    public static bool NeedsResume(PublicAgentSettings agent)
        => agent.ConnectionStatus is "suspended" or "reconnecting";

    /// <summary>
    /// Asks the relay once to lift the owner's own suspension of this Agent's Envoy. A refusal
    /// means a host, filter or membership suspension is in place: the Agent is switched off here
    /// with that source, its connector stops and its skill is removed.
    /// </summary>
    public async Task<EnvoyResumeResult> ResumeSuspendedAsync(string agentEntityId, CancellationToken ct = default)
    {
        resumeAttempted[agentEntityId] = 0;
        try
        {
            var status = await relays.For(agentEntityId).ResumeAsync(ct);
            var current = await state.GetPublicAgentAsync(agentEntityId, ct);
            if (current is not null)
                await state.SavePublicAgentAsync(current with
                {
                    ConnectionStatus = status == "pending" ? "pending approval" : "reconnecting",
                    SuspendedBy = null,
                }, ct);
            await audit.RecordAsync("envoy_resumed", new Dictionary<string, object?>
                { ["agent_entity_id"] = agentEntityId, ["relay_status"] = status }, ct);
            return new(agentEntityId, EnvoyResumeStatuses.Resumed);
        }
        catch (RelayApiException error) when (error.Reason == "envoy_suspended")
        {
            return await MarkSuspendedAsync(agentEntityId, ct);
        }
        catch (RelayApiException error)
        {
            await audit.RecordAsync("envoy_resume_failed", new Dictionary<string, object?>
                { ["agent_entity_id"] = agentEntityId, ["reason"] = error.Reason }, ct);
            return new(agentEntityId, EnvoyResumeStatuses.Failed, Reason: error.Reason);
        }
        catch (Exception error) when (error is HttpRequestException
            || (error is TaskCanceledException && !ct.IsCancellationRequested))
        {
            await audit.RecordAsync("envoy_resume_failed", new Dictionary<string, object?>
                { ["agent_entity_id"] = agentEntityId, ["reason"] = "relay_unreachable" }, ct);
            return new(agentEntityId, EnvoyResumeStatuses.Failed, Reason: "relay_unreachable");
        }
    }

    /// <summary>
    /// The relay keeps this Envoy suspended: switch the Agent off here, record who suspended it
    /// (the source an owner notice reported, else the host, which also covers suspensions from
    /// before sources existed), stop its connector and remove its skill.
    /// </summary>
    private async Task<EnvoyResumeResult> MarkSuspendedAsync(string agentEntityId, CancellationToken ct)
    {
        var current = await state.GetPublicAgentAsync(agentEntityId, ct);
        var by = current?.SuspendedBy is SuspensionSources.Host or SuspensionSources.Filter or SuspensionSources.Membership
            ? current.SuspendedBy : SuspensionSources.Host;
        if (current is not null)
            await state.SavePublicAgentAsync(current with
                { Enabled = false, ConnectionStatus = "suspended", SuspendedBy = by }, ct);
        await audit.RecordAsync("envoy_resume_refused", new Dictionary<string, object?>
            { ["agent_entity_id"] = agentEntityId, ["suspended_by"] = by }, ct);
        logger.LogWarning("Leaf Community Agent {Agent} is suspended on the relay ({Source}); switched off",
            agentEntityId, by);
        await AssignSkillAsync(agentEntityId, add: false, ct);
        if (connectors.TryRemove(agentEntityId, out var connector)) await connector.DisposeAsync();
        return new(agentEntityId, EnvoyResumeStatuses.Suspended, by, "envoy_suspended");
    }

    /// <summary>
    /// The relay refused this Agent's WebSocket. Reads why (one empty profile patch) and returns
    /// the next attempt's delay, or null to stop: a suspended Envoy gets one resume attempt and is
    /// then switched off; a pending one waits for the host and is retried slowly.
    /// </summary>
    internal async Task<TimeSpan?> HandleConnectRefusedAsync(string agentEntityId, int statusCode, CancellationToken ct)
    {
        var settings = await state.GetPublicAgentAsync(agentEntityId, ct);
        if (settings is null || !settings.Enabled) return null;
        if (statusCode == 401) return PendingRetryDelay; // the credential was replaced or revoked
        string? relayStatus;
        try { relayStatus = await relays.For(agentEntityId).ReadStatusAsync(ct); }
        catch (RelayApiException error) when (error.Reason == "envoy_suspended") { relayStatus = "suspended"; }
        catch (Exception error) when (error is RelayApiException or HttpRequestException
            || (error is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return TimeSpan.FromSeconds(30);
        }
        switch (relayStatus)
        {
            case "suspended":
                if (!resumeAttempted.TryAdd(agentEntityId, 0))
                {
                    await MarkSuspendedAsync(agentEntityId, ct);
                    return null;
                }
                var resumed = await ResumeSuspendedAsync(agentEntityId, ct);
                return resumed.Status switch
                {
                    EnvoyResumeStatuses.Resumed => TimeSpan.Zero,
                    EnvoyResumeStatuses.Suspended => null,
                    _ => TimeSpan.FromSeconds(30),
                };
            case "pending":
                if (settings.ConnectionStatus != "pending approval")
                {
                    logger.LogWarning("Leaf Community Agent {Agent} is waiting for the server host", agentEntityId);
                    await state.SavePublicAgentAsync(settings with { ConnectionStatus = "pending approval" }, ct);
                }
                return PendingRetryDelay;
            default:
                return TimeSpan.FromSeconds(30);
        }
    }

    public async Task<RelayProfile> ReconcileRelayProfileAsync(
        string agentEntityId, CancellationToken ct = default)
    {
        var profile = await relays.For(agentEntityId).ReadProfileAsync(ct);
        var owner = await state.GetOwnerSettingsAsync(ct);
        await state.SaveOwnerSettingsAsync(owner with
        {
            PublicName = profile.PublicName,
            RelayConfirmedPublicName = profile.PublicName,
        }, ct);
        var agent = await state.GetPublicAgentAsync(agentEntityId, ct);
        if (agent is not null)
            await state.SavePublicAgentAsync(agent with
            {
                AgentName = string.IsNullOrWhiteSpace(profile.AgentName)
                    ? agent.AgentName : profile.AgentName,
            }, ct);
        return profile;
    }

    public async Task RefreshAsync(string agentEntityId, CancellationToken ct = default)
    {
        if (connectors.TryRemove(agentEntityId, out var existing)) await existing.DisposeAsync();
        var agent = await state.GetPublicAgentAsync(agentEntityId, ct);
        if (agent is null || !agent.Enabled || string.IsNullOrWhiteSpace(agent.Credential)) return;
        var connector = CreateConnector(agentEntityId);
        if (!connectors.TryAdd(agentEntityId, connector)) { await connector.DisposeAsync(); return; }
        await connector.StartAsync(stopping);
    }

    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        var enabled = (await state.ListPublicAgentsAsync(enabledOnly: true, ct: ct))
            .Where(agent => !string.IsNullOrWhiteSpace(agent.Credential))
            .Select(agent => agent.AgentEntityId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in connectors.Keys.Where(id => !enabled.Contains(id)).ToArray())
            if (connectors.TryRemove(id, out var connector)) await connector.DisposeAsync();
        foreach (var id in enabled.Where(id => !connectors.ContainsKey(id))) await RefreshAsync(id, ct);
    }

    public async Task SuspendAsync(string agentEntityId, string reason, CancellationToken ct = default)
    {
        var settings = await state.GetPublicAgentAsync(agentEntityId, ct);
        if (settings is null) return;
        if (!settings.Enabled && settings.ConnectionStatus == "suspended")
        {
            await RefreshAsync(agentEntityId, ct);
            return;
        }
        string? suspendedBy = null;
        if (!string.IsNullOrWhiteSpace(settings.Credential))
        {
            try
            {
                await relays.For(agentEntityId).SuspendAsync(ct);
                suspendedBy = SuspensionSources.Owner;
            }
            catch (RelayApiException error)
            {
                await audit.RecordAsync("envoy_suspend_failed", new Dictionary<string, object?>
                    { ["agent_entity_id"] = agentEntityId, ["reason"] = error.Reason }, ct);
            }
        }
        await state.SavePublicAgentAsync(settings with
            { Enabled = false, ConnectionStatus = "suspended", SuspendedBy = suspendedBy ?? settings.SuspendedBy }, ct);
        await audit.RecordAsync("envoy_suspended", new Dictionary<string, object?>
            { ["agent_entity_id"] = agentEntityId, ["reason"] = reason }, ct);
        await RefreshAsync(agentEntityId, ct);
    }

    public async Task HandleAgentRemovedAsync(string agentEntityId, CancellationToken ct = default)
    {
        await SuspendAsync(agentEntityId, "agent_removed", ct);
        await AssignSkillAsync(agentEntityId, add: false, ct);
    }

    public async Task SyncAgentNameAsync(string agentEntityId, CancellationToken ct = default)
    {
        var configuration = await state.GetConfigurationAsync(ct);
        var settings = configuration.Agents.FirstOrDefault(agent => string.Equals(
            agent.AgentEntityId, agentEntityId, StringComparison.OrdinalIgnoreCase));
        if (settings is null || !settings.Enabled || string.IsNullOrWhiteSpace(configuration.Owner.OwnerUserId)) return;
        var entity = await directory.GetAsync(agentEntityId, configuration.Owner.OwnerUserId, ct);
        if (entity is null) { await HandleAgentRemovedAsync(agentEntityId, ct); return; }
        if (string.Equals(entity.Name, settings.AgentName, StringComparison.Ordinal)) return;
        var profile = string.IsNullOrWhiteSpace(settings.Credential)
            ? new RelayProfile(configuration.Owner.PublicName,
                $"{entity.Name} \u00b7 {configuration.Owner.PublicName}", entity.Name)
            : await relays.For(agentEntityId).PatchProfileAsync(null, entity.Name, null, ct);
        await state.SavePublicAgentAsync(settings with
        {
            AgentName = profile.AgentName ?? entity.Name,
        }, ct);
        await audit.RecordAsync("agent_name_synced", new Dictionary<string, object?>
            { ["agent_entity_id"] = agentEntityId, ["agent_name"] = entity.Name }, ct);
    }

    private RelayConnector CreateConnector(string agentEntityId)
    {
        var core = new ConnectorCore(
            (request, ct) => runtime.HandleAsync(agentEntityId, request, ct),
            (listening, ct) => runtime.HandleListeningEventAsync(agentEntityId, listening, ct));
        return new RelayConnector(async ct =>
        {
            var configuration = await state.GetConfigurationAsync(ct).ConfigureAwait(false);
            var agent = configuration.Agents.First(item => string.Equals(item.AgentEntityId,
                agentEntityId, StringComparison.OrdinalIgnoreCase));
            return (new Uri(CommonsRelayDefaults.EffectiveBaseUrl(configuration.Owner.RelayBaseUrl)), agent.Credential);
        }, core, (status, ct) => SetStatusAsync(agentEntityId, status, ct),
            loggers.CreateLogger<RelayConnector>(),
            async ct =>
            {
                resumeAttempted.TryRemove(agentEntityId, out _);
                await ReconcileRelayProfileAsync(agentEntityId, ct);
            },
            (statusCode, ct) => HandleConnectRefusedAsync(agentEntityId, statusCode, ct));
    }

    private async Task SetStatusAsync(string agentEntityId, string status, CancellationToken ct)
    {
        var current = await state.GetPublicAgentAsync(agentEntityId, ct);
        if (current is null || !current.Enabled) return;
        if (status == "reconnecting" && current.ConnectionStatus == "pending approval") return;
        if (current.ConnectionStatus != status || (status == "active" && current.SuspendedBy is not null))
            await state.SavePublicAgentAsync(current with
            {
                ConnectionStatus = status,
                SuspendedBy = status == "active" ? null : current.SuspendedBy,
            }, ct);
    }

    private Task OnEntityUpdatedAsync(PluginEvent evt)
        => evt.Payload["typeSlug"]?.GetValue<string>() == "agent"
            && evt.Payload["id"]?.GetValue<string>() is { } agentId
            ? SyncAgentNameSafeAsync(agentId) : Task.CompletedTask;

    private Task OnEntityDeletedAsync(PluginEvent evt)
        => evt.Payload["typeSlug"]?.GetValue<string>() == "agent"
            && evt.Payload["id"]?.GetValue<string>() is { } agentId
            ? HandleAgentRemovedAsync(agentId, CancellationToken.None) : Task.CompletedTask;

    private async Task SyncAgentNameSafeAsync(string agentEntityId)
    {
        try { await SyncAgentNameAsync(agentEntityId, CancellationToken.None); }
        catch (RelayApiException error)
        {
            await audit.RecordAsync("agent_name_sync_failed", new Dictionary<string, object?>
                { ["agent_entity_id"] = agentEntityId, ["reason"] = error.Reason }, CancellationToken.None);
        }
    }

    private async Task ReconcileRelayProfileSafeAsync(string agentEntityId)
    {
        var settings = await state.GetPublicAgentAsync(agentEntityId, CancellationToken.None);
        if (string.IsNullOrWhiteSpace(settings?.Credential)) return;
        try { await ReconcileRelayProfileAsync(agentEntityId, CancellationToken.None); }
        catch (RelayApiException error)
        {
            await audit.RecordAsync("relay_profile_read_failed", new Dictionary<string, object?>
                { ["agent_entity_id"] = agentEntityId, ["reason"] = error.Reason }, CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var subscription in subscriptions) subscription.Dispose();
        subscriptions.Clear();
        foreach (var connector in connectors.Values) await connector.DisposeAsync();
        connectors.Clear();
    }
}
