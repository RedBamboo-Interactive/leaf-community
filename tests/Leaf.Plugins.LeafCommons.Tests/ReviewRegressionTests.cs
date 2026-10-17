using System.Text.Json.Nodes;
using Leaf.Plugins.LeafCommons;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.LeafCommons.Tests;

public sealed class ReviewRegressionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "leaf-commons-review-" + Guid.NewGuid().ToString("N"));

    public ReviewRegressionTests() => Directory.CreateDirectory(root);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public async Task Approved_brief_posts_approved_title_and_content_byte_for_byte_without_model_call()
    {
        var entities = new MemoryEntityStore();
        var state = new CommonsStateStore(entities);
        await state.SaveSettingsAsync(new(null, "https://relay.example", "credential", "active", "Nova", null,
            new Dictionary<string, bool> { ["consult"] = true }, [], [], [], null, [], "owner-1", "agent-1"));
        var title = "  Retry strategy — exact title  ";
        var content = "First line\r\nSecond line with  double spaces.\n";
        var id = Guid.NewGuid();
        var brief = new LeafEntity(id, "leaf-commons-brief", "brief-1", "consult preview", new JsonObject
        {
            ["kind"] = "consult", ["title"] = title, ["content"] = content,
            ["addresses"] = new JsonArray("envoy-b"), ["state"] = "approved",
            ["owner_id"] = "owner-1", ["agent_id"] = "agent-1"
        }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "owner-1");
        var model = new CountingModel("{\"action\":\"post\",\"content\":\"MODEL REPLACED THE PREVIEW\"}");
        var relay = new FakeRelay();
        var runtime = new CommonsRuntime(model, new ShareableStore(root), relay, new OutboundFilter(new SecretScanner()), state, new NullDigestSink());

        var result = await runtime.DispatchApprovedBriefAsync(brief);

        Assert.Equal("posted", result.State);
        Assert.Equal(0, result.Steps);
        Assert.Equal(0, model.Calls);
        var opened = Assert.Single(relay.Opened);
        Assert.Equal("consult", opened.Scenario);
        Assert.Equal(title, opened.Title);
        Assert.Equal(content, opened.Content);
        Assert.Equal(["envoy-b"], opened.Addresses);
    }

    [Fact]
    public async Task Envoy_model_cannot_open_a_thread()
    {
        var model = new CountingModel("{\"action\":\"post\",\"content\":\"anything\"}");
        var runner = new EnvoyRunner(model, new ShareableStore(root), new FakeRelay(), new OutboundFilter(new SecretScanner()), new MemoryAudit(), [], []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(new(null, "consult", "new thread")));
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task Approved_brief_is_still_blocked_locally_before_relay_delivery()
    {
        var entities = new MemoryEntityStore();
        var state = new CommonsStateStore(entities);
        await state.SaveSettingsAsync(new(null, "https://relay.example", "credential", "active", "Nova", null,
            new Dictionary<string, bool> { ["consult"] = true }, [], [], ["CANARY-APPROVAL"], null, [], "owner-1", "agent-1"));
        var brief = new LeafEntity(Guid.NewGuid(), "leaf-commons-brief", "brief-blocked", "consult preview", new JsonObject
        {
            ["kind"] = "consult", ["title"] = "Safe title", ["content"] = "CANARY-APPROVAL",
            ["addresses"] = new JsonArray("envoy-b"), ["state"] = "approved"
        }, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "owner-1");
        var model = new CountingModel("{\"action\":\"post\",\"content\":\"irrelevant\"}");
        var relay = new FakeRelay();
        var runtime = new CommonsRuntime(model, new ShareableStore(root), relay, new OutboundFilter(new SecretScanner()), state, new NullDigestSink());

        var result = await runtime.DispatchApprovedBriefAsync(brief);

        Assert.Equal("blocked", result.State);
        Assert.Equal("canary", result.Summary);
        Assert.Equal(0, model.Calls);
        Assert.Empty(relay.Opened);
        var audits = await entities.QueryAsync(new EntityQuery { TypeSlug = "leaf-commons-audit" });
        Assert.Contains(audits, x => x.Data["event"]?.GetValue<string>() == "filter_decision"
            && x.Data["metadata"]?["alarm"]?.GetValue<bool>() == true);
    }

    [Fact]
    public async Task OAuth_callback_rejects_missing_state()
    {
        var result = await new CommonsStateStore(new MemoryEntityStore()).ConsumeOAuthStateAsync(null, "owner-1");
        Assert.Equal(OAuthStateStatus.Missing, result.Status);
    }

    [Fact]
    public async Task OAuth_callback_rejects_unknown_state()
    {
        var result = await new CommonsStateStore(new MemoryEntityStore()).ConsumeOAuthStateAsync("not-issued", "owner-1");
        Assert.Equal(OAuthStateStatus.Unknown, result.Status);
    }

    [Fact]
    public async Task OAuth_callback_rejects_expired_state()
    {
        var store = new CommonsStateStore(new MemoryEntityStore());
        var now = DateTimeOffset.Parse("2026-10-17T12:00:00Z");
        var issued = await store.CreateOAuthStateAsync("owner-1", false, now);
        var result = await store.ConsumeOAuthStateAsync(issued.State, "owner-1", now.AddMinutes(11));
        Assert.Equal(OAuthStateStatus.Expired, result.Status);
    }

    [Fact]
    public async Task OAuth_callback_rejects_reused_state()
    {
        var store = new CommonsStateStore(new MemoryEntityStore());
        var issued = await store.CreateOAuthStateAsync("owner-1", false);
        Assert.Equal(OAuthStateStatus.Accepted, (await store.ConsumeOAuthStateAsync(issued.State, "owner-1")).Status);
        Assert.Equal(OAuthStateStatus.Reused, (await store.ConsumeOAuthStateAsync(issued.State, "owner-1")).Status);
    }

    [Fact]
    public async Task OAuth_callback_rejects_state_bound_to_another_user()
    {
        var store = new CommonsStateStore(new MemoryEntityStore());
        var issued = await store.CreateOAuthStateAsync("owner-1", false);
        Assert.Equal(OAuthStateStatus.OtherUser, (await store.ConsumeOAuthStateAsync(issued.State, "owner-2")).Status);
    }

    [Fact]
    public void OAuth_callback_cannot_replace_active_credential_without_explicit_rejoin()
    {
        var settings = Settings("owner-1") with { Credential = "active-secret", ConnectionStatus = "active" };
        Assert.False(CommonsEndpoints.CanCompleteOAuthCallback(settings, new(OAuthStateStatus.Accepted, false)));
        Assert.True(CommonsEndpoints.CanCompleteOAuthCallback(settings, new(OAuthStateStatus.Accepted, true)));
    }

    [Fact]
    public void Non_admin_cannot_claim_an_unowned_plugin()
    {
        var unowned = Settings(null);
        Assert.False(CommonsEndpoints.CanWriteSettings(new TestAuth("user-1"), unowned, "user-1"));
        Assert.True(CommonsEndpoints.CanWriteSettings(new TestAuth("admin-1", "admin"), unowned, "user-1"));
        Assert.Equal("user-1", CommonsEndpoints.ResolveOwnerUserId(new TestAuth("admin-1", "admin"), unowned, "user-1"));
    }

    [Fact]
    public void Normal_owner_can_update_but_cannot_transfer_ownership()
    {
        var owned = Settings("owner-1");
        Assert.True(CommonsEndpoints.CanWriteSettings(new TestAuth("owner-1"), owned, null));
        Assert.False(CommonsEndpoints.CanWriteSettings(new TestAuth("owner-1"), owned, "owner-2"));
    }

    private static CommonsSettings Settings(string? owner) => new(null, "http://127.0.0.1:8787", null, "disconnected", "Nova", null,
        new Dictionary<string, bool>(), [], [], [], null, [], owner, "agent-1");
}

internal sealed class CountingModel(string response) : IToolFreeModelClient
{
    public int Calls { get; private set; }
    public Task<string> GenerateActionAsync(string system, string prompt, CancellationToken ct = default) { Calls++; return Task.FromResult(response); }
}

internal sealed class NullDigestSink : IDigestSink
{
    public Task DeliverAsync(CommonsDigest digest, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class TestAuth(string? userId, params string[] roles) : IAuthContext
{
    public string? UserId => userId;
    public string? Email => null;
    public IReadOnlyList<string> Roles => roles;
    public bool IsInRole(string role) => roles.Contains(role, StringComparer.OrdinalIgnoreCase);
}

internal sealed class MemoryEntityStore : IEntityStore
{
    private readonly Dictionary<Guid, LeafEntity> byId = [];
    private long revision;

    public Task<LeafEntity?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(byId.GetValueOrDefault(id));
    public Task<LeafEntity?> GetBySlugAsync(string slug, CancellationToken ct = default) => Task.FromResult(byId.Values.FirstOrDefault(x => x.Slug == slug));
    public Task<LeafEntity?> GetBySlugAsync(string typeSlug, string slug, CancellationToken ct = default) => Task.FromResult(byId.Values.FirstOrDefault(x => x.TypeSlug == typeSlug && x.Slug == slug));
    public Task<IReadOnlyList<LeafEntity>> QueryAsync(EntityQuery query, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LeafEntity>>(byId.Values.Where(x => x.TypeSlug == query.TypeSlug).OrderBy(x => x.Id).Skip(query.Offset).Take(query.Limit).ToArray());

    public Task<LeafEntity> CreateAsync(string typeSlug, string name, JsonObject? data = null, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        var entity = New(id, typeSlug, id.ToString("N"), name, data ?? []);
        byId[id] = entity;
        return Task.FromResult(entity);
    }

    public Task<LeafEntity> UpsertBySlugAsync(string typeSlug, string slug, string name, JsonObject? data = null, CancellationToken ct = default)
    {
        var current = byId.Values.FirstOrDefault(x => x.TypeSlug == typeSlug && x.Slug == slug);
        var entity = New(current?.Id ?? Guid.NewGuid(), typeSlug, slug, name, data ?? [], current?.CreatedAt);
        byId[entity.Id] = entity;
        return Task.FromResult(entity);
    }

    public Task<LeafEntity> PatchAsync(Guid id, JsonObject dataPatch, string? name = null, CancellationToken ct = default)
    {
        var current = byId[id];
        var data = (JsonObject)current.Data.DeepClone();
        foreach (var item in dataPatch) data[item.Key] = item.Value?.DeepClone();
        var entity = New(id, current.TypeSlug, current.Slug, name ?? current.Name, data, current.CreatedAt);
        byId[id] = entity;
        return Task.FromResult(entity);
    }

    public Task<LeafEntity> ReplaceDataAsync(Guid id, JsonObject data, string? name = null, CancellationToken ct = default)
    {
        var current = byId[id];
        var entity = New(id, current.TypeSlug, current.Slug, name ?? current.Name, data, current.CreatedAt);
        byId[id] = entity;
        return Task.FromResult(entity);
    }

    public Task<LeafEntity?> CompareExchangeBySlugAsync(string typeSlug, string slug, string name, JsonObject data, DateTimeOffset? expectedUpdatedAt, CancellationToken ct = default)
    {
        var current = byId.Values.FirstOrDefault(x => x.TypeSlug == typeSlug && x.Slug == slug);
        if ((current is null) != (expectedUpdatedAt is null) || current is not null && current.UpdatedAt != expectedUpdatedAt) return Task.FromResult<LeafEntity?>(null);
        var entity = New(current?.Id ?? Guid.NewGuid(), typeSlug, slug, name, data, current?.CreatedAt);
        byId[entity.Id] = entity;
        return Task.FromResult<LeafEntity?>(entity);
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default) { byId.Remove(id); return Task.CompletedTask; }

    private LeafEntity New(Guid id, string typeSlug, string slug, string name, JsonObject data, DateTimeOffset? created = null)
    {
        var updated = DateTimeOffset.UnixEpoch.AddTicks(++revision);
        return new(id, typeSlug, slug, name, (JsonObject)data.DeepClone(), created ?? updated, updated, "test");
    }
}
