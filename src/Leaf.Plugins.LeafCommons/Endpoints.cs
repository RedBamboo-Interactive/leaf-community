using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.LeafCommons;

public sealed record BriefRequest(string Kind, string Content);
public sealed record BriefDecisionRequest(string Decision, string? Content);
public sealed record SettingsUpdate(string RelayBaseUrl, string? Credential, string Persona, string? Avatar,
    Dictionary<string, bool> ScenarioToggles, string[] ShareableFolders, string[] PersonalWords, string[] Canaries,
    string? NovaDiscussionId, string[] LinkAllowlist, string? OwnerUserId, string AgentEntityId);

public static class CommonsEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (context, next) => { context.HttpContext.Response.Headers.CacheControl = "private, no-store"; return await next(context); });
        group.MapGet("/settings", async (IAuthContext auth, CommonsStateStore store, CancellationToken ct) =>
        {
            var settings = await store.GetSettingsAsync(ct); return IsOwner(auth, settings) ? Results.Ok(settings.Public()) : Forbidden();
        });
        group.MapPut("/settings", async (SettingsUpdate update, IAuthContext auth, CommonsStateStore store, CancellationToken ct) =>
        {
            if (!Uri.TryCreate(update.RelayBaseUrl, UriKind.Absolute, out var relay) || relay.Scheme is not ("http" or "https")) return Results.BadRequest(new { error = "invalid_relay_url" });
            var current = await store.GetSettingsAsync(ct);
            if (!IsOwner(auth, current, allowInitialize: true) || (!auth.IsInRole("admin") && update.OwnerUserId is not null && update.OwnerUserId != auth.UserId)) return Forbidden();
            var credential = string.IsNullOrWhiteSpace(update.Credential) ? current.Credential : update.Credential;
            var saved = await store.SaveSettingsAsync(new(current.EntityId, relay.ToString(), credential, current.ConnectionStatus,
                update.Persona, update.Avatar, update.ScenarioToggles, update.ShareableFolders, update.PersonalWords,
                update.Canaries, update.NovaDiscussionId, update.LinkAllowlist, current.OwnerUserId ?? auth.UserId, update.AgentEntityId), ct);
            return Results.Ok(saved.Public());
        });
        group.MapGet("/oauth/start", async (string callback, string? scenarios, IAuthContext auth, RelayHttpClient relay, CommonsStateStore store, CancellationToken ct) =>
            IsOwner(auth, await store.GetSettingsAsync(ct)) ? Results.Ok(new { authorizeUrl = relay.BuildAuthorizeUri(new Uri(callback), (scenarios ?? "showcase,consult,bug").Split(',', StringSplitOptions.RemoveEmptyEntries)) }) : Forbidden());
        group.MapGet("/oauth/callback", async (string exchangeCode, IAuthContext auth, RelayHttpClient relay, CommonsStateStore store, CancellationToken ct) =>
        {
            var before = await store.GetSettingsAsync(ct); if (!IsOwner(auth, before)) return Forbidden();
            var exchange = await relay.ExchangeAsync(exchangeCode, ct);
            await store.SaveSettingsAsync(before with { Credential = exchange.Credential, ConnectionStatus = exchange.Status == "pending" ? "pending approval" : exchange.Status, Persona = exchange.PersonaName, Avatar = exchange.AvatarObjectKey }, ct);
            return Results.Redirect("/apps/leaf-commons?joined=pending");
        });
        group.MapPost("/briefs", async (BriefRequest request, HttpContext http, CommonsStateStore store, CancellationToken ct) =>
        {
            var settings = await store.GetSettingsAsync(ct); var caller = OwnerGuard.ReadPrivateNovaAgent(http.User, settings);
            if (caller is null) return Results.Json(new { error = "owner_agent_required" }, statusCode: 403);
            var brief = await store.EnqueueBriefAsync(request.Kind, request.Content, caller, ct);
            return Results.Accepted(value: new { id = brief.Id, state = "preview" });
        });
        group.MapGet("/briefs", async (IAuthContext auth, CommonsStateStore store, CancellationToken ct) =>
        {
            var settings = await store.GetSettingsAsync(ct); return IsOwner(auth, settings) ? Results.Ok(new { items = (await store.ListBriefsAsync(ct)).Select(x => new { id = x.Id, kind = x.Data["kind"], content = x.Data["content"], state = x.Data["state"] }) }) : Forbidden();
        });
        group.MapPost("/briefs/{id:guid}/decision", async (Guid id, BriefDecisionRequest request, IAuthContext auth, CommonsStateStore store, CommonsRuntime runtime, CancellationToken ct) =>
        {
            var settings = await store.GetSettingsAsync(ct); if (!IsOwner(auth, settings) || string.IsNullOrWhiteSpace(auth.UserId)) return Forbidden();
            var brief = await store.DecideBriefAsync(id, request.Decision, request.Content, auth.UserId, ct);
            EnvoyRunResult? result = null;
            if (request.Decision == "approved") result = await runtime.DispatchApprovedBriefAsync(brief, ct);
            return Results.Ok(new { id = brief.Id, state = brief.Data["state"], result });
        });
        group.MapPost("/shareable/rebuild", async (IAuthContext auth, CommonsStateStore store, ShareableSetBuilder builder, ShareableStore shareable, CancellationToken ct) =>
        {
            var settings = await store.GetSettingsAsync(ct); if (!IsOwner(auth, settings)) return Forbidden();
            var result = await builder.BuildAsync(settings.ShareableFolders, shareable.RootPath, ct);
            await store.RecordAsync("shareable_rebuilt", new Dictionary<string, object?> { ["copied"] = result.Copied, ["excluded"] = result.Excluded.Count }, ct);
            return Results.Ok(result);
        });
    }

    private static bool IsOwner(IAuthContext auth, CommonsSettings settings, bool allowInitialize = false) => auth.IsInRole("admin") || (!string.IsNullOrWhiteSpace(auth.UserId) && (string.Equals(auth.UserId, settings.OwnerUserId, StringComparison.Ordinal) || (allowInitialize && string.IsNullOrWhiteSpace(settings.OwnerUserId))));
    private static IResult Forbidden() => Results.Json(new { error = "owner_required" }, statusCode: 403);
}
