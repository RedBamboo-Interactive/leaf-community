using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.LeafCommons;

public sealed record PublicAgentUpdate(
    string AgentId, bool Enabled, string AvatarSource,
    Dictionary<string, bool> ScenarioToggles, string[] ShareableFolders,
    bool RestrictedMode = false);

public sealed record SettingsUpdate(
    string RelayBaseUrl, string PublicName, string[] PersonalWords, string[] Canaries,
    string[] LinkAllowlist, string? OwnerUserId, PublicAgentUpdate[] Agents,
    string? RelayConfirmedPublicName = null);

public static class CommonsEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (context, next) => { context.HttpContext.Response.Headers.CacheControl = "private, no-store"; return await next(context); });
        group.MapGet("/settings", async (IAuthContext auth, CommonsStateStore store,
            AgentDirectory directory, PublicAgentAvatarSync avatars, CancellationToken ct) =>
        {
            var configuration = await store.GetConfigurationAsync(ct);
            if (!IsOwner(auth, configuration.Owner)) return Forbidden();
            var directoryOwner = configuration.Owner.OwnerUserId ?? auth.UserId;
            var owned = string.IsNullOrWhiteSpace(directoryOwner)
                ? [] : await directory.ListAsync(directoryOwner, ct);
            return Results.Ok(SettingsProjection(configuration, owned,
                await avatars.IsOutfitsAvailableAsync(ct)));
        });
        group.MapPut("/settings", async (SettingsUpdate update, IAuthContext auth,
            CommonsStateStore store, AgentDirectory directory, RelayClientFactory relays,
            PublicAgentAvatarSync avatars, PublicAgentCoordinator coordinator,
            IAvatarImageSource avatarImages, CancellationToken ct) =>
        {
            if (!Uri.TryCreate(update.RelayBaseUrl, UriKind.Absolute, out var relay)
                || relay.Scheme is not ("http" or "https"))
                return Results.BadRequest(new { error = "invalid_relay_url" });
            var configuration = await store.GetConfigurationAsync(ct);
            if (!CanWriteSettings(auth, configuration.Owner, update.OwnerUserId)) return Forbidden();
            var ownerId = ResolveOwnerUserId(auth, configuration.Owner, update.OwnerUserId);
            var owned = (await directory.ListAsync(ownerId, ct)).ToDictionary(
                agent => agent.AgentEntityId, StringComparer.OrdinalIgnoreCase);
            foreach (var requested in update.Agents)
            {
                if (!owned.ContainsKey(requested.AgentId))
                    return Results.BadRequest(new { error = "agent_not_owned", agentId = requested.AgentId });
                if (await AvatarSources.ValidateAsync(requested.AvatarSource, avatarImages, ct) is { } error)
                    return Results.BadRequest(new { error, agentId = requested.AgentId });
            }

            var owner = await store.SaveOwnerSettingsAsync(configuration.Owner with
            {
                RelayBaseUrl = relay.ToString(),
                PersonalWords = update.PersonalWords, Canaries = update.Canaries,
                LinkAllowlist = update.LinkAllowlist, OwnerUserId = ownerId,
            }, ct);
            var profileAgent = configuration.Agents.FirstOrDefault(agent => agent.Enabled
                && !string.IsNullOrWhiteSpace(agent.Credential));
            if (profileAgent is not null)
            {
                try
                {
                    await coordinator.ReconcileRelayProfileAsync(profileAgent.AgentEntityId, ct);
                    owner = await store.GetOwnerSettingsAsync(ct);
                }
                catch (RelayApiException error)
                {
                    return Results.Json(new { error = error.Reason, message = error.Message,
                        agentId = profileAgent.AgentEntityId }, statusCode: error.StatusCode);
                }
            }
            var requestedNameDiffers = owner.RelayConfirmedPublicName is not null
                && !string.Equals(update.PublicName,
                    owner.RelayConfirmedPublicName, StringComparison.Ordinal);
            var publicNameChanged = IsExplicitPublicNameChange(update.PublicName,
                update.RelayConfirmedPublicName, owner.RelayConfirmedPublicName);
            if (requestedNameDiffers && !publicNameChanged)
                return Results.Conflict(new { error = "public_name_stale",
                    message = "The relay public name changed. Reload settings before changing it.",
                    publicName = owner.PublicName });
            var publicNamePending = publicNameChanged;
            if (!requestedNameDiffers && owner.PublicName != owner.RelayConfirmedPublicName)
                owner = await store.SaveOwnerSettingsAsync(owner with
                    { PublicName = owner.RelayConfirmedPublicName ?? owner.PublicName }, ct);
            foreach (var requested in update.Agents)
            {
                var live = owned[requested.AgentId];
                var current = await store.GetPublicAgentAsync(requested.AgentId, ct)
                    ?? new(null, requested.AgentId, live.Name, false, null, "disconnected",
                        null, new Dictionary<string, bool>(), []);
                if (!requested.Enabled && current.Enabled)
                {
                    await coordinator.SuspendAsync(requested.AgentId, "owner_disabled", ct);
                    current = await store.GetPublicAgentAsync(requested.AgentId, ct) ?? current;
                }
                var avatarChanged = current.AvatarSource != requested.AvatarSource;
                var saved = await store.SavePublicAgentAsync(current with
                {
                    Enabled = requested.Enabled, AgentName = live.Name,
                    ScenarioToggles = requested.ScenarioToggles,
                    ShareableFolders = requested.ShareableFolders,
                    RestrictedMode = requested.RestrictedMode,
                    AvatarSource = requested.AvatarSource,
                    AvatarContentHash = avatarChanged ? null : current.AvatarContentHash,
                    AvatarSyncStatus = avatarChanged ? "pending" : current.AvatarSyncStatus,
                    AvatarSyncReason = avatarChanged ? null : current.AvatarSyncReason,
                    AvatarResolvedAssetId = avatarChanged ? null : current.AvatarResolvedAssetId,
                }, ct);
                if (saved.Enabled && !string.IsNullOrWhiteSpace(saved.Credential))
                {
                    try
                    {
                        var profile = await relays.For(saved.AgentEntityId).PatchProfileAsync(
                            publicNamePending ? update.PublicName : null,
                            live.Name, saved.ScenarioToggles, ct);
                        publicNamePending = false;
                        if (!string.Equals(owner.PublicName, profile.PublicName, StringComparison.Ordinal)
                            || !string.Equals(owner.RelayConfirmedPublicName, profile.PublicName,
                                StringComparison.Ordinal))
                            owner = await store.SaveOwnerSettingsAsync(owner with
                                { PublicName = profile.PublicName,
                                    RelayConfirmedPublicName = profile.PublicName }, ct);
                        saved = await store.SavePublicAgentAsync(saved with
                            { AgentName = profile.AgentName ?? live.Name }, ct);
                        if (avatarChanged) await avatars.SyncAsync(saved.AgentEntityId, ct);
                    }
                    catch (RelayApiException error)
                    {
                        return Results.Json(new { error = error.Reason, message = error.Message,
                            agentId = saved.AgentEntityId }, statusCode: error.StatusCode);
                    }
                    catch (AvatarSyncException error)
                    {
                        return Results.Json(new { error = "avatar_sync_failed", reason = error.Reason,
                            agentId = saved.AgentEntityId }, statusCode: 422);
                    }
                }
                await coordinator.RefreshAsync(saved.AgentEntityId, ct);
            }
            if (publicNamePending)
                return Results.Conflict(new { error = "public_name_requires_public_agent",
                    message = "Enable and join an Agent before changing the relay public name." });
            configuration = await store.GetConfigurationAsync(ct);
            return Results.Ok(SettingsProjection(configuration, await directory.ListAsync(ownerId, ct),
                await avatars.IsOutfitsAvailableAsync(ct)));
        });
        group.MapGet("/oauth/start", async (string callback, string agentId, string? scenarios,
            bool? rejoin, IAuthContext auth, RelayClientFactory relays, CommonsStateStore store,
            AgentDirectory directory, CancellationToken ct) =>
        {
            var configuration = await store.GetConfigurationAsync(ct);
            if (!IsConfiguredOwner(auth, configuration.Owner) || string.IsNullOrWhiteSpace(auth.UserId)) return Forbidden();
            var publicAgent = await store.GetPublicAgentAsync(agentId, ct);
            var owned = await directory.GetAsync(agentId, auth.UserId, ct);
            if (owned is null || publicAgent?.Enabled != true)
                return Results.BadRequest(new { error = "agent_not_public" });
            if (IsActive(publicAgent) && rejoin != true)
                return Results.Conflict(new { error = "active_credential_requires_rejoin" });
            var oauth = await store.CreateOAuthStateAsync(auth.UserId, agentId,
                rejoin == true, ct: ct);
            var callbackWithState = AddQuery(callback, "state", oauth.State);
            return Results.Ok(new { authorizeUrl = await relays.ForEnrollment(agentId)
                .BuildAuthorizeUriAsync(callbackWithState,
                    (scenarios ?? "showcase,consult,open_question,bug").Split(',', StringSplitOptions.RemoveEmptyEntries),
                    ct, agentId, owned.Name) });
        });
        group.MapGet("/oauth/callback", async (string exchangeCode, string? state, IAuthContext auth,
            RelayClientFactory relays, CommonsStateStore store, AgentDirectory directory,
            PublicAgentAvatarSync avatars, PublicAgentCoordinator coordinator, CancellationToken ct) =>
        {
            var configuration = await store.GetConfigurationAsync(ct);
            if (!IsConfiguredOwner(auth, configuration.Owner) || string.IsNullOrWhiteSpace(auth.UserId)) return Forbidden();
            var consumed = await store.ConsumeOAuthStateAsync(state, auth.UserId, ct: ct);
            if (consumed.Status != OAuthStateStatus.Accepted) return OAuthStateFailure(consumed.Status);
            var agentId = consumed.AgentEntityId ?? CommonsStateStore.LegacyNovaAgentId;
            var before = await store.GetPublicAgentAsync(agentId, ct);
            if (before is null || !before.Enabled) return Results.BadRequest(new { error = "agent_not_public" });
            if (!CanCompleteOAuthCallback(before, consumed))
                return Results.Conflict(new { error = "active_credential_requires_rejoin" });
            var owned = await directory.GetAsync(agentId, auth.UserId, ct);
            if (owned is null) return Results.BadRequest(new { error = "agent_not_owned" });
            var exchange = await relays.ForEnrollment(agentId).ExchangeAsync(exchangeCode, ct);
            if (!string.Equals(configuration.Owner.PublicName, exchange.PublicName, StringComparison.Ordinal)
                || !string.Equals(configuration.Owner.RelayConfirmedPublicName,
                    exchange.PublicName, StringComparison.Ordinal))
                await store.SaveOwnerSettingsAsync(configuration.Owner with
                    { PublicName = exchange.PublicName,
                        RelayConfirmedPublicName = exchange.PublicName }, ct);
            var saved = await store.SavePublicAgentAsync(before with
            {
                Credential = exchange.Credential,
                ConnectionStatus = exchange.Status == "pending" ? "pending approval" : exchange.Status,
                AgentName = exchange.AgentName ?? owned.Name,
                EnvoyId = exchange.EnvoyId,
                Avatar = exchange.AvatarObjectKey,
                AvatarContentHash = null,
            }, ct);
            try { await avatars.SyncAsync(saved.AgentEntityId, ct); } catch (AvatarSyncException) { }
            await coordinator.RefreshAsync(saved.AgentEntityId, ct);
            return Results.Redirect("/extensions/leaf-commons?joined=pending");
        });
        group.MapGet("/threads", async (string? agentId, IAuthContext auth, CommonsStateStore store,
            PublicAgentThreads threads, CancellationToken ct) =>
        {
            var settings = await store.GetOwnerSettingsAsync(ct);
            if (!IsOwner(auth, settings)) return Forbidden();
            try { return Results.Ok(new { items = await threads.ListAsync(agentId, ct) }); }
            catch (RelayApiException error)
            {
                return Results.Json(new { error = error.Reason, message = error.Message }, statusCode: error.StatusCode);
            }
        });
        group.MapGet("/threads/{threadRef}", async (string threadRef, IAuthContext auth,
            CommonsStateStore store, PublicAgentThreads threads, CancellationToken ct) =>
        {
            var settings = await store.GetOwnerSettingsAsync(ct);
            if (!IsOwner(auth, settings)) return Forbidden();
            try { return Results.Ok(await threads.GetAsync(threadRef, ct)); }
            catch (RelayApiException error)
            {
                return Results.Json(new { error = error.Reason, message = error.Message }, statusCode: error.StatusCode);
            }
        });
        group.MapPost("/threads/{threadRef}/clear", async (string threadRef, string agentId,
            IAuthContext auth, CommonsStateStore store,
            PublicAgentConversationCoordinator conversations, CancellationToken ct) =>
        {
            var owner = await store.GetOwnerSettingsAsync(ct);
            if (!IsOwner(auth, owner)) return Forbidden();
            var cleared = await conversations.ClearAsync(agentId, threadRef, ct);
            return cleared is null
                ? Results.NotFound(new { error = "public_conversation_not_found" })
                : Results.Ok(new { cleared.Generation, cleared.SessionId });
        });
        group.MapPost("/briefs", async (BriefSubmission request, HttpContext http,
            CommonsStateStore store, AgentDirectory directory, PublicAgentRuntime runtime,
            CancellationToken ct) =>
        {
            var configuration = await store.GetConfigurationAsync(ct);
            var caller = OwnerGuard.ReadPrivateAgent(http.User, configuration.Owner);
            if (caller is null) return Results.Json(new { error = "owner_agent_required" }, statusCode: 403);
            var publicAgent = configuration.Agents.FirstOrDefault(agent => agent.Enabled
                && string.Equals(agent.AgentEntityId, caller.AgentEntityId, StringComparison.OrdinalIgnoreCase));
            if (publicAgent is null || await directory.GetAsync(caller.AgentEntityId,
                    caller.BeneficiaryId, ct) is null)
                return Results.Json(new { error = "agent_not_public" }, statusCode: 403);
            try
            {
                var result = await runtime.PostBriefAsync(request, caller, ct);
                return Results.Ok(new
                    { decision = result.Decision, messageId = result.MessageId, reason = result.Reason });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = "invalid_brief", message = ex.Message }); }
        });
        group.MapGet("/briefs", async (IAuthContext auth, CommonsStateStore store, CancellationToken ct) =>
        {
            var settings = await store.GetOwnerSettingsAsync(ct);
            return IsOwner(auth, settings)
                ? Results.Ok(new { items = await store.ListBriefsAsync(ct) })
                : Forbidden();
        });
        group.MapGet("/digests", async (IAuthContext auth, CommonsStateStore store, CancellationToken ct) =>
        {
            var settings = await store.GetOwnerSettingsAsync(ct);
            if (!IsOwner(auth, settings)) return Forbidden();
            return Results.Ok(new { items = await store.ListDigestsAsync(ct: ct) });
        });
        group.MapPost("/digests/{id:guid}/read", async (Guid id, IAuthContext auth, CommonsStateStore store, CancellationToken ct) =>
        {
            var settings = await store.GetOwnerSettingsAsync(ct);
            if (!IsOwner(auth, settings)) return Forbidden();
            try { return Results.Ok(await store.MarkDigestReadAsync(id, ct)); }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "digest_not_found" }); }
        });
        group.MapPost("/briefs/{id:guid}/decision", (Guid id) =>
            Results.Json(new { error = "brief_decision_removed" }, statusCode: StatusCodes.Status410Gone));
        group.MapPost("/shareable/rebuild", async (string agentId, IAuthContext auth,
            CommonsStateStore store, ShareableSetBuilder builder, AgentShareableStores shareables,
            CancellationToken ct) =>
        {
            var owner = await store.GetOwnerSettingsAsync(ct); if (!IsOwner(auth, owner)) return Forbidden();
            var settings = await store.GetPublicAgentAsync(agentId, ct);
            if (settings is null || !settings.Enabled) return Results.BadRequest(new { error = "agent_not_public" });
            var result = await builder.BuildAsync(settings.ShareableFolders,
                shareables.For(agentId).RootPath, ct);
            await store.RecordAsync("shareable_rebuilt", new Dictionary<string, object?>
                { ["agent_entity_id"] = agentId, ["copied"] = result.Copied,
                    ["excluded"] = result.Excluded.Count }, ct);
            return Results.Ok(result);
        });
    }

    public static bool CanWriteSettings(IAuthContext auth, CommonsSettings settings, string? requestedOwnerId)
    {
        if (auth.IsInRole("admin")) return !string.IsNullOrWhiteSpace(auth.UserId);
        return !string.IsNullOrWhiteSpace(auth.UserId)
            && string.Equals(auth.UserId, settings.OwnerUserId, StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(requestedOwnerId) || string.Equals(requestedOwnerId, settings.OwnerUserId, StringComparison.Ordinal));
    }

    public static string ResolveOwnerUserId(IAuthContext auth, CommonsSettings settings, string? requestedOwnerId)
    {
        if (!CanWriteSettings(auth, settings, requestedOwnerId)) throw new UnauthorizedAccessException("Only an admin may initialize or change ownership");
        if (auth.IsInRole("admin") && !string.IsNullOrWhiteSpace(requestedOwnerId)) return requestedOwnerId;
        return settings.OwnerUserId ?? auth.UserId!;
    }

    public static bool CanWriteSettings(IAuthContext auth, CommonsOwnerSettings settings,
        string? requestedOwnerId)
    {
        if (auth.IsInRole("admin")) return !string.IsNullOrWhiteSpace(auth.UserId);
        return !string.IsNullOrWhiteSpace(auth.UserId)
            && string.Equals(auth.UserId, settings.OwnerUserId, StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(requestedOwnerId)
                || string.Equals(requestedOwnerId, settings.OwnerUserId, StringComparison.Ordinal));
    }

    public static string ResolveOwnerUserId(IAuthContext auth, CommonsOwnerSettings settings,
        string? requestedOwnerId)
    {
        if (!CanWriteSettings(auth, settings, requestedOwnerId))
            throw new UnauthorizedAccessException("Only an admin may initialize or change ownership");
        if (auth.IsInRole("admin") && !string.IsNullOrWhiteSpace(requestedOwnerId)) return requestedOwnerId;
        return settings.OwnerUserId ?? auth.UserId!;
    }

    public static bool CanCompleteOAuthCallback(CommonsSettings settings, OAuthStateConsumption state) =>
        state.Status == OAuthStateStatus.Accepted && (!IsActive(settings) || state.AllowActiveOverwrite);

    public static bool CanCompleteOAuthCallback(PublicAgentSettings settings, OAuthStateConsumption state) =>
        state.Status == OAuthStateStatus.Accepted && (!IsActive(settings) || state.AllowActiveOverwrite);

    public static bool IsExplicitPublicNameChange(string requested, string? clientConfirmed,
        string? currentRelayConfirmed) =>
        !string.IsNullOrWhiteSpace(clientConfirmed)
        && string.Equals(clientConfirmed, currentRelayConfirmed, StringComparison.Ordinal)
        && !string.Equals(requested, currentRelayConfirmed, StringComparison.Ordinal);

    private static bool IsOwner(IAuthContext auth, CommonsSettings settings) => auth.IsInRole("admin") || IsConfiguredOwner(auth, settings);
    private static bool IsOwner(IAuthContext auth, CommonsOwnerSettings settings) => auth.IsInRole("admin") || IsConfiguredOwner(auth, settings);
    private static bool IsConfiguredOwner(IAuthContext auth, CommonsSettings settings) => !string.IsNullOrWhiteSpace(auth.UserId) && string.Equals(auth.UserId, settings.OwnerUserId, StringComparison.Ordinal);
    private static bool IsConfiguredOwner(IAuthContext auth, CommonsOwnerSettings settings) => !string.IsNullOrWhiteSpace(auth.UserId) && string.Equals(auth.UserId, settings.OwnerUserId, StringComparison.Ordinal);
    private static bool IsActive(CommonsSettings settings) => !string.IsNullOrWhiteSpace(settings.Credential) && string.Equals(settings.ConnectionStatus, "active", StringComparison.OrdinalIgnoreCase);
    private static bool IsActive(PublicAgentSettings settings) => !string.IsNullOrWhiteSpace(settings.Credential) && string.Equals(settings.ConnectionStatus, "active", StringComparison.OrdinalIgnoreCase);

    private static object SettingsProjection(CommonsConfiguration configuration,
        IReadOnlyList<OwnedAgent> ownedAgents, bool outfitsAvailable)
    {
        var configured = configuration.Agents.ToDictionary(
            agent => agent.AgentEntityId, StringComparer.OrdinalIgnoreCase);
        return new
        {
            relayBaseUrl = configuration.Owner.RelayBaseUrl,
            publicName = configuration.Owner.PublicName,
            relayConfirmedPublicName = configuration.Owner.RelayConfirmedPublicName,
            personalWords = configuration.Owner.PersonalWords,
            canaries = configuration.Owner.Canaries,
            linkAllowlist = configuration.Owner.LinkAllowlist,
            ownerUserId = configuration.Owner.OwnerUserId,
            outfitsAvailable,
            agents = ownedAgents.Select(agent =>
            {
                configured.TryGetValue(agent.AgentEntityId, out var value);
                return new
                {
                    agentId = agent.AgentEntityId,
                    agentName = agent.Name,
                    agentAvatarUrl = agent.AvatarUrl,
                    enabled = value?.Enabled == true,
                    credentialConfigured = !string.IsNullOrWhiteSpace(value?.Credential),
                    connectionStatus = value?.ConnectionStatus ?? "not-public",
                    persona = $"{agent.Name} \u00b7 {configuration.Owner.PublicName}",
                    avatarSource = value?.AvatarSource ?? AvatarSources.AgentAvatar,
                    avatarSyncStatus = value?.AvatarSyncStatus ?? "not-synced",
                    avatarSyncReason = value?.AvatarSyncReason,
                    scenarioToggles = value?.ScenarioToggles ?? new Dictionary<string, bool>(),
                    shareableFolders = value?.ShareableFolders ?? [],
                    restrictedMode = value?.RestrictedMode ?? false,
                };
            }).ToArray(),
        };
    }
    private static Uri AddQuery(string callback, string name, string value)
    {
        var uri = new Uri(callback, UriKind.Absolute);
        var builder = new UriBuilder(uri);
        var existing = builder.Query.TrimStart('?');
        builder.Query = (existing.Length == 0 ? "" : existing + "&") + Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value);
        return builder.Uri;
    }
    private static IResult OAuthStateFailure(OAuthStateStatus status) => status switch
    {
        OAuthStateStatus.Missing => Results.BadRequest(new { error = "oauth_state_missing" }),
        OAuthStateStatus.Unknown => Results.BadRequest(new { error = "oauth_state_unknown" }),
        OAuthStateStatus.Expired => Results.BadRequest(new { error = "oauth_state_expired" }),
        OAuthStateStatus.Reused => Results.Conflict(new { error = "oauth_state_reused" }),
        OAuthStateStatus.OtherUser => Results.Json(new { error = "oauth_state_owner_mismatch" }, statusCode: 403),
        _ => Results.BadRequest(new { error = "oauth_state_invalid" })
    };
    private static IResult Forbidden() => Results.Json(new { error = "owner_required" }, statusCode: 403);
}
