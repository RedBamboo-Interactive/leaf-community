using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.LeafCommons;

public sealed record SettingsUpdate(string RelayBaseUrl, string? Credential, string PublicName,
    Dictionary<string, bool> ScenarioToggles, string[] ShareableFolders, string[] PersonalWords, string[] Canaries,
    string? NovaDiscussionId, string[] LinkAllowlist, string? OwnerUserId, string AgentEntityId,
    string AvatarSource = AvatarSources.AgentAvatar);

public static class CommonsEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (context, next) => { context.HttpContext.Response.Headers.CacheControl = "private, no-store"; return await next(context); });
        group.MapGet("/settings", async (IAuthContext auth, CommonsStateStore store, AvatarSyncService avatars, CancellationToken ct) =>
        {
            var settings = await store.GetSettingsAsync(ct);
            return IsOwner(auth, settings)
                ? Results.Ok(settings.Public(await avatars.IsOutfitsAvailableAsync(ct)))
                : Forbidden();
        });
        group.MapPut("/settings", async (SettingsUpdate update, IAuthContext auth, CommonsStateStore store,
            RelayHttpClient relayClient, AvatarSyncService avatars, IAvatarImageSource avatarImages, CancellationToken ct) =>
        {
            if (!Uri.TryCreate(update.RelayBaseUrl, UriKind.Absolute, out var relay) || relay.Scheme is not ("http" or "https")) return Results.BadRequest(new { error = "invalid_relay_url" });
            var current = await store.GetSettingsAsync(ct);
            if (!CanWriteSettings(auth, current, update.OwnerUserId)) return Forbidden();
            if (await AvatarSources.ValidateAsync(update.AvatarSource, avatarImages, ct) is { } avatarError)
                return Results.BadRequest(new { error = avatarError });
            var credential = string.IsNullOrWhiteSpace(update.Credential) ? current.Credential : update.Credential;
            var avatarChanged = current.AvatarSource != update.AvatarSource
                || !string.Equals(current.AgentEntityId, update.AgentEntityId, StringComparison.OrdinalIgnoreCase);
            var profile = new RelayProfile(current.EffectivePublicName, current.Persona);
            if (!string.IsNullOrWhiteSpace(credential))
            {
                try
                {
                    profile = await relayClient.UpdateProfileAtAsync(relay, credential,
                        update.PublicName, update.ScenarioToggles, ct);
                }
                catch (RelayApiException error)
                {
                    return Results.Json(new { error = error.Reason, message = error.Message },
                        statusCode: error.StatusCode);
                }
            }
            else if (!string.Equals(update.PublicName, current.EffectivePublicName, StringComparison.Ordinal))
            {
                return Results.Conflict(new { error = "envoy_credential_missing", message = "Connect Public Nova before changing its public name." });
            }
            var saved = await store.SaveSettingsAsync(current with
            {
                RelayBaseUrl = relay.ToString(), Credential = credential,
                PublicName = profile.PublicName, Persona = profile.PersonaName,
                ScenarioToggles = update.ScenarioToggles, ShareableFolders = update.ShareableFolders,
                PersonalWords = update.PersonalWords, Canaries = update.Canaries,
                NovaDiscussionId = update.NovaDiscussionId, LinkAllowlist = update.LinkAllowlist,
                OwnerUserId = ResolveOwnerUserId(auth, current, update.OwnerUserId),
                AgentEntityId = update.AgentEntityId, AvatarSource = update.AvatarSource,
                AvatarContentHash = avatarChanged ? null : current.AvatarContentHash,
                AvatarSyncStatus = avatarChanged ? "pending" : current.AvatarSyncStatus,
                AvatarSyncReason = avatarChanged ? null : current.AvatarSyncReason,
                AvatarResolvedAssetId = avatarChanged ? null : current.AvatarResolvedAssetId,
            }, ct);
            if (!string.IsNullOrWhiteSpace(saved.Credential))
            {
                try { await avatars.SyncAsync(saved, ct); }
                catch (AvatarSyncException error)
                {
                    return Results.Json(new { error = "avatar_sync_failed", reason = error.Reason }, statusCode: 422);
                }
                saved = await store.GetSettingsAsync(ct);
            }
            return Results.Ok(saved.Public(await avatars.IsOutfitsAvailableAsync(ct)));
        });
        group.MapGet("/oauth/start", async (string callback, string? scenarios, bool? rejoin, IAuthContext auth, RelayHttpClient relay, CommonsStateStore store, CancellationToken ct) =>
        {
            var settings = await store.GetSettingsAsync(ct);
            if (!IsConfiguredOwner(auth, settings) || string.IsNullOrWhiteSpace(auth.UserId)) return Forbidden();
            if (IsActive(settings) && rejoin != true) return Results.Conflict(new { error = "active_credential_requires_rejoin" });
            var oauth = await store.CreateOAuthStateAsync(auth.UserId, rejoin == true, ct: ct);
            var callbackWithState = AddQuery(callback, "state", oauth.State);
            return Results.Ok(new { authorizeUrl = relay.BuildAuthorizeUri(callbackWithState, (scenarios ?? "showcase,consult,open_question,bug").Split(',', StringSplitOptions.RemoveEmptyEntries)) });
        });
        group.MapGet("/oauth/callback", async (string exchangeCode, string? state, IAuthContext auth, RelayHttpClient relay,
            CommonsStateStore store, AvatarSyncService avatars, CancellationToken ct) =>
        {
            var before = await store.GetSettingsAsync(ct);
            if (!IsConfiguredOwner(auth, before) || string.IsNullOrWhiteSpace(auth.UserId)) return Forbidden();
            var consumed = await store.ConsumeOAuthStateAsync(state, auth.UserId, ct: ct);
            if (consumed.Status != OAuthStateStatus.Accepted) return OAuthStateFailure(consumed.Status);
            if (!CanCompleteOAuthCallback(before, consumed)) return Results.Conflict(new { error = "active_credential_requires_rejoin" });
            var exchange = await relay.ExchangeAsync(exchangeCode, ct);
            var saved = await store.SaveSettingsAsync(before with
            {
                Credential = exchange.Credential,
                ConnectionStatus = exchange.Status == "pending" ? "pending approval" : exchange.Status,
                Persona = exchange.PersonaName,
                PublicName = exchange.PublicName,
                EnvoyId = exchange.EnvoyId,
                Avatar = exchange.AvatarObjectKey,
                AvatarContentHash = null,
            }, ct);
            try { await avatars.SyncAsync(saved, ct); } catch (AvatarSyncException) { }
            return Results.Redirect("/extensions/leaf-commons?joined=pending");
        });
        group.MapGet("/threads", async (IAuthContext auth, CommonsStateStore store,
            PublicNovaThreads threads, CancellationToken ct) =>
        {
            var settings = await store.GetSettingsAsync(ct);
            if (!IsOwner(auth, settings)) return Forbidden();
            try { return Results.Ok(new { items = await threads.ListAsync(settings, ct) }); }
            catch (RelayApiException error)
            {
                return Results.Json(new { error = error.Reason, message = error.Message }, statusCode: error.StatusCode);
            }
        });
        group.MapGet("/threads/{threadRef}", async (string threadRef, IAuthContext auth,
            CommonsStateStore store, PublicNovaThreads threads, CancellationToken ct) =>
        {
            var settings = await store.GetSettingsAsync(ct);
            if (!IsOwner(auth, settings)) return Forbidden();
            try { return Results.Ok(await threads.GetAsync(threadRef, settings, ct)); }
            catch (RelayApiException error)
            {
                return Results.Json(new { error = error.Reason, message = error.Message }, statusCode: error.StatusCode);
            }
        });
        group.MapPost("/briefs", async (BriefSubmission request, HttpContext http,
            CommonsStateStore store, CommonsRuntime runtime, CancellationToken ct) =>
        {
            var settings = await store.GetSettingsAsync(ct); var caller = OwnerGuard.ReadPrivateNovaAgent(http.User, settings);
            if (caller is null) return Results.Json(new { error = "owner_agent_required" }, statusCode: 403);
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
            var settings = await store.GetSettingsAsync(ct);
            return IsOwner(auth, settings)
                ? Results.Ok(new { items = await store.ListBriefsAsync(ct) })
                : Forbidden();
        });
        group.MapGet("/digests", async (IAuthContext auth, CommonsStateStore store, CancellationToken ct) =>
        {
            var settings = await store.GetSettingsAsync(ct);
            if (!IsOwner(auth, settings)) return Forbidden();
            return Results.Ok(new { items = await store.ListDigestsAsync(ct: ct) });
        });
        group.MapPost("/digests/{id:guid}/read", async (Guid id, IAuthContext auth, CommonsStateStore store, CancellationToken ct) =>
        {
            var settings = await store.GetSettingsAsync(ct);
            if (!IsOwner(auth, settings)) return Forbidden();
            try { return Results.Ok(await store.MarkDigestReadAsync(id, ct)); }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "digest_not_found" }); }
        });
        group.MapPost("/briefs/{id:guid}/decision", (Guid id) =>
            Results.Json(new { error = "brief_decision_removed" }, statusCode: StatusCodes.Status410Gone));
        group.MapPost("/shareable/rebuild", async (IAuthContext auth, CommonsStateStore store, ShareableSetBuilder builder, ShareableStore shareable, CancellationToken ct) =>
        {
            var settings = await store.GetSettingsAsync(ct); if (!IsOwner(auth, settings)) return Forbidden();
            var result = await builder.BuildAsync(settings.ShareableFolders, shareable.RootPath, ct);
            await store.RecordAsync("shareable_rebuilt", new Dictionary<string, object?> { ["copied"] = result.Copied, ["excluded"] = result.Excluded.Count }, ct);
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

    public static bool CanCompleteOAuthCallback(CommonsSettings settings, OAuthStateConsumption state) =>
        state.Status == OAuthStateStatus.Accepted && (!IsActive(settings) || state.AllowActiveOverwrite);

    private static bool IsOwner(IAuthContext auth, CommonsSettings settings) => auth.IsInRole("admin") || IsConfiguredOwner(auth, settings);
    private static bool IsConfiguredOwner(IAuthContext auth, CommonsSettings settings) => !string.IsNullOrWhiteSpace(auth.UserId) && string.Equals(auth.UserId, settings.OwnerUserId, StringComparison.Ordinal);
    private static bool IsActive(CommonsSettings settings) => !string.IsNullOrWhiteSpace(settings.Credential) && string.Equals(settings.ConnectionStatus, "active", StringComparison.OrdinalIgnoreCase);
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
