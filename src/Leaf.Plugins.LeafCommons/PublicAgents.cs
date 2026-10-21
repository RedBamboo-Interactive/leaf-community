using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.LeafCommons;

public sealed record OwnedAgent(string AgentEntityId, string Name, string? AvatarUrl);

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

    private static bool OwnedBy(LeafEntity entity, string ownerUserId)
        => string.Equals(entity.CreatedBy, ownerUserId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Text(entity.Data, "owner_id"), ownerUserId, StringComparison.OrdinalIgnoreCase);

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
        return (new Uri(configuration.Owner.RelayBaseUrl), agent?.Credential);
    });

    public RelayHttpClient ForEnrollment(string agentEntityId) => new(clients.CreateClient("commons-relay"), async ct =>
    {
        var owner = await state.GetOwnerSettingsAsync(ct).ConfigureAwait(false);
        return (new Uri(owner.RelayBaseUrl), (string?)null);
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
    ILoggerFactory loggers) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, RelayConnector> connectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDisposable> subscriptions = [];
    private CancellationToken stopping;

    public async Task StartAsync(CancellationToken applicationStopping)
    {
        stopping = applicationStopping;
        subscriptions.Add(events.Subscribe("entity.updated", OnEntityUpdatedAsync));
        subscriptions.Add(events.Subscribe("entity.deleted", OnEntityDeletedAsync));
        foreach (var agent in await state.ListPublicAgentsAsync(enabledOnly: true, ct: applicationStopping))
        {
            await ReconcileRelayProfileSafeAsync(agent.AgentEntityId);
            await SyncAgentNameSafeAsync(agent.AgentEntityId);
        }
        await ReconcileAsync(applicationStopping);
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
        if (!string.IsNullOrWhiteSpace(settings.Credential))
        {
            try { await relays.For(agentEntityId).SuspendAsync(ct); }
            catch (RelayApiException error)
            {
                await audit.RecordAsync("envoy_suspend_failed", new Dictionary<string, object?>
                    { ["agent_entity_id"] = agentEntityId, ["reason"] = error.Reason }, ct);
            }
        }
        await state.SavePublicAgentAsync(settings with
            { Enabled = false, ConnectionStatus = "suspended" }, ct);
        await audit.RecordAsync("envoy_suspended", new Dictionary<string, object?>
            { ["agent_entity_id"] = agentEntityId, ["reason"] = reason }, ct);
        await RefreshAsync(agentEntityId, ct);
    }

    public Task HandleAgentRemovedAsync(string agentEntityId, CancellationToken ct = default)
        => SuspendAsync(agentEntityId, "agent_removed", ct);

    public async Task SyncAgentNameAsync(string agentEntityId, CancellationToken ct = default)
    {
        var configuration = await state.GetConfigurationAsync(ct);
        var settings = configuration.Agents.FirstOrDefault(agent => string.Equals(
            agent.AgentEntityId, agentEntityId, StringComparison.OrdinalIgnoreCase));
        if (settings is null || !settings.Enabled || string.IsNullOrWhiteSpace(configuration.Owner.OwnerUserId)) return;
        var entity = await directory.GetAsync(agentEntityId, configuration.Owner.OwnerUserId, ct);
        if (entity is null) { await SuspendAsync(agentEntityId, "agent_removed", ct); return; }
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
            return (new Uri(configuration.Owner.RelayBaseUrl), agent.Credential);
        }, core, (status, ct) => SetStatusAsync(agentEntityId, status, ct),
            loggers.CreateLogger<RelayConnector>(),
            ct => ReconcileRelayProfileAsync(agentEntityId, ct));
    }

    private async Task SetStatusAsync(string agentEntityId, string status, CancellationToken ct)
    {
        var current = await state.GetPublicAgentAsync(agentEntityId, ct);
        if (current is null || !current.Enabled) return;
        if (status == "reconnecting" && current.ConnectionStatus == "pending approval") return;
        if (current.ConnectionStatus != status)
            await state.SavePublicAgentAsync(current with { ConnectionStatus = status }, ct);
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
