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
    public async Task Verified_private_nova_brief_posts_immediately_without_a_decision_call()
    {
        var entities = new MemoryEntityStore();
        var state = new CommonsStateStore(entities);
        await state.SaveSettingsAsync(new(null, "https://relay.example", "credential", "active", "Nova", null,
            new Dictionary<string, bool> { ["consult"] = true }, [], [], [], null, [], "owner-1", "agent-1"));
        var title = "  Retry strategy — exact title  ";
        var content = "First line\r\nSecond line with  double spaces.\n";
        var model = new CountingModel("{\"action\":\"post\",\"content\":\"MODEL REPLACED THE BRIEF\"}");
        var relay = new FakeRelay();
        var runtime = new CommonsRuntime(model, new ShareableStore(root), relay, new OutboundFilter(new SecretScanner()), state, new NullDigestSink());

        var result = await runtime.PostBriefAsync(
            new("consult", content, title, ["envoy-b"]), new("agent-1", "owner-1"));

        Assert.Equal("posted", result.Decision);
        Assert.Equal("m1", result.MessageId);
        Assert.Null(result.Reason);
        Assert.Equal(0, model.Calls);
        var opened = Assert.Single(relay.Opened);
        Assert.Equal("consult", opened.Scenario);
        Assert.Equal(title, opened.Title);
        Assert.Equal(content, opened.Content);
        Assert.Equal(["envoy-b"], opened.Addresses);
        var history = Assert.Single(await state.ListBriefsAsync());
        Assert.Equal("consult", history.Kind);
        Assert.Equal(title, history.Title);
        Assert.Equal(CommonsStateStore.ContentHash(content), history.ContentHash);
        Assert.Equal("posted", history.Outcome);
        Assert.Equal("m1", history.MessageId);
        var stored = Assert.Single(await entities.QueryAsync(new EntityQuery { TypeSlug = "leaf-commons-brief" }));
        Assert.Null(stored.Data["content"]);
        Assert.Null(stored.Data["addresses"]);
    }

    [Fact]
    public async Task Envoy_model_cannot_open_a_thread()
    {
        var model = new CountingModel("{\"action\":\"post\",\"content\":\"anything\"}");
        var audit = new MemoryAudit();
        var runner = new EnvoyRunner(model, new ShareableStore(root), new FakeRelay(), new OutboundFilter(new SecretScanner()), audit, [], []);
        var result = await runner.RunAsync(new(null, "consult", "new thread"));
        Assert.Equal("failed", result.State);
        Assert.Equal("thread_required", result.Summary);
        Assert.Contains(audit.Events, x => x.Name == "envoy_failed" && Equals(x.Data["reason"], "thread_required"));
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task Addressed_showcase_text_reply_posts_to_its_message_chain_with_plain_text_context()
    {
        var relay = new FakeRelay();
        var runner = new EnvoyRunner(
            new CountingModel("{\"action\":\"post\",\"content\":\"2 + 2 is 4.\"}"),
            new ShareableStore(root), relay, new OutboundFilter(new SecretScanner()),
            new MemoryAudit(), [], []);

        var result = await runner.RunAsync(new("message:showcase-root", "channel_reply",
            "Hello! What's 2+2?", TriggerId: "live-1644", Addressed: true,
            Channel: "showcase", ReplyToDisplayName: "Laurent"));

        Assert.Equal("done", result.State);
        var reply = Assert.Single(relay.Replies);
        Assert.Equal("message:showcase-root", reply.ThreadRef);
        Assert.Equal("channel_reply", reply.Scenario);
        Assert.Equal("Replying to Laurent: 2 + 2 is 4.", reply.Content);
    }

    [Fact]
    public async Task Addressed_text_channel_failure_posts_prefixed_safe_fallback()
    {
        var relay = new FakeRelay();
        var runner = new EnvoyRunner(
            new ThrowingModel(new ToolFreeGenerateException("fixture", "gateway_bad_gateway", 502)),
            new ShareableStore(root), relay, new OutboundFilter(new SecretScanner()),
            new MemoryAudit(), [], []);

        var result = await runner.RunAsync(new("message:showcase-root", "channel_reply",
            "question", TriggerId: "ping", Addressed: true,
            Channel: "showcase", ReplyToDisplayName: "<@123> Laurent"));

        Assert.Equal("failed", result.State);
        var reply = Assert.Single(relay.Replies);
        Assert.Equal("channel_reply", reply.Scenario);
        Assert.DoesNotContain("<@", reply.Content);
        Assert.EndsWith(EnvoyRunner.CoverageFallback, reply.Content);
    }

    [Theory]
    [InlineData(403, "gateway_forbidden")]
    [InlineData(422, "tool_free_unavailable")]
    [InlineData(502, "gateway_bad_gateway")]
    public async Task Addressed_model_failure_is_audited_and_delivers_owner_notice(int status, string reason)
    {
        var entities = new MemoryEntityStore();
        var state = new CommonsStateStore(entities);
        await state.SaveSettingsAsync(new(null, "https://relay.example", "credential", "active", "Nova", null,
            new Dictionary<string, bool> { ["consult"] = true }, [], [], [], "digest-1", [], "owner-1", "agent-1"));
        var model = new ThrowingModel(new ToolFreeGenerateException("fixture failure", reason, status));
        var digests = new CapturingDigestSink();
        var runtime = new CommonsRuntime(model, new ShareableStore(root), new FakeRelay(),
            new OutboundFilter(new SecretScanner()), state, digests);

        var result = await runtime.HandleAsync(new("thread-1", "consult", "what is 2 + 2?",
            TriggerId: "ping-1", Addressed: true));

        Assert.Equal("failed", result.State);
        Assert.Equal(reason, result.Summary);
        var audits = await entities.QueryAsync(new EntityQuery { TypeSlug = "leaf-commons-audit" });
        Assert.Contains(audits, x => x.Data["event"]?.GetValue<string>() == "envoy_step"
            && x.Data["metadata"]?["step"]?.GetValue<int>() == 1);
        Assert.Contains(audits, x => x.Data["event"]?.GetValue<string>() == "envoy_failed"
            && x.Data["metadata"]?["reason"]?.GetValue<string>() == reason
            && x.Data["metadata"]?["thread"]?.GetValue<string>() == "thread-1");
        var notice = Assert.Single(digests.Items);
        Assert.Equal("owner_notice", notice.Type);
        Assert.Equal("thread-1", notice.ThreadRef);
        Assert.Equal($"I couldn't answer thread thread-1: {reason}.", notice.Content);
    }

    [Fact]
    public async Task Canary_in_brief_is_blocked_immediately_and_recorded_without_relay_delivery()
    {
        var entities = new MemoryEntityStore();
        var state = new CommonsStateStore(entities);
        await state.SaveSettingsAsync(new(null, "https://relay.example", "credential", "active", "Nova", null,
            new Dictionary<string, bool> { ["consult"] = true }, [], [], ["CANARY-BRIEF"], null, [], "owner-1", "agent-1"));
        var model = new CountingModel("{\"action\":\"post\",\"content\":\"irrelevant\"}");
        var relay = new FakeRelay();
        var runtime = new CommonsRuntime(model, new ShareableStore(root), relay, new OutboundFilter(new SecretScanner()), state, new NullDigestSink());

        var result = await runtime.PostBriefAsync(
            new("consult", "CANARY-BRIEF", "Safe title", ["envoy-b"]),
            new("agent-1", "owner-1"));

        Assert.Equal("blocked", result.Decision);
        Assert.Equal("canary", result.Reason);
        Assert.Null(result.MessageId);
        Assert.Equal(0, model.Calls);
        Assert.Empty(relay.Opened);
        var history = Assert.Single(await state.ListBriefsAsync());
        Assert.Equal("blocked", history.Outcome);
        Assert.Equal("canary", history.Reason);
        Assert.Equal(CommonsStateStore.ContentHash("CANARY-BRIEF"), history.ContentHash);
        var audits = await entities.QueryAsync(new EntityQuery { TypeSlug = "leaf-commons-audit" });
        Assert.Contains(audits, x => x.Data["event"]?.GetValue<string>() == "filter_decision"
            && x.Data["metadata"]?["alarm"]?.GetValue<bool>() == true);
    }

    [Fact]
    public async Task Relay_rejected_brief_returns_reason_and_is_recorded_without_retry()
    {
        var state = new CommonsStateStore(new MemoryEntityStore());
        await state.SaveSettingsAsync(new(null, "https://relay.example", "credential", "active", "Nova", null,
            new Dictionary<string, bool> { ["consult"] = true }, [], [], [], null, [], "owner-1", "agent-1"));
        var relay = new RejectingBriefRelay("relay_link_not_allowed");
        var runtime = new CommonsRuntime(new CountingModel("unused"), new ShareableStore(root), relay,
            new OutboundFilter(new SecretScanner()), state, new NullDigestSink());

        var result = await runtime.PostBriefAsync(
            new("consult", "See https://github.com/example/repo", "Reference", []),
            new("agent-1", "owner-1"));

        Assert.Equal("rejected", result.Decision);
        Assert.Equal("relay_link_not_allowed", result.Reason);
        Assert.Null(result.MessageId);
        Assert.Equal(1, relay.Calls);
        var history = Assert.Single(await state.ListBriefsAsync());
        Assert.Equal("rejected", history.Outcome);
        Assert.Equal("relay_link_not_allowed", history.Reason);
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
    public Task<string> GenerateActionAsync(string system, string prompt, EnvoyModelContext context,
        CancellationToken ct = default) { Calls++; return Task.FromResult(response); }
}

internal sealed class ThrowingModel(Exception error) : IToolFreeModelClient
{
    public Task<string> GenerateActionAsync(string system, string prompt, EnvoyModelContext context,
        CancellationToken ct = default) => Task.FromException<string>(error);
}

internal sealed class NullDigestSink : IDigestSink
{
    public Task DeliverAsync(CommonsDigest digest, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class CapturingDigestSink : IDigestSink
{
    public List<CommonsDigest> Items { get; } = [];
    public Task DeliverAsync(CommonsDigest digest, CancellationToken ct = default)
    {
        Items.Add(digest);
        return Task.CompletedTask;
    }
}

internal sealed class RejectingBriefRelay(string reason) : IEnvoyRelay
{
    public int Calls { get; private set; }
    public Task<IReadOnlyList<RelayMessage>> ReadHistoryAsync(string threadRef, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RelayMessage>>([]);
    public Task<RelayPostResult> PostAsync(string threadRef, string content,
        IReadOnlyList<string>? addresses = null, string? idempotencyKey = null,
        CancellationToken ct = default) => throw new NotSupportedException();
    public Task<RelayPostResult> OpenAsync(string scenario, string? threadName, string content,
        IReadOnlyList<string>? addresses = null, string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(new RelayPostResult(false, reason));
    }
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

    public LeafEntity Seed(Guid id, string typeSlug, string name, string slug, JsonObject data,
        string createdBy = "test")
    {
        var entity = New(id, typeSlug, slug, name, data, createdBy: createdBy);
        byId[id] = entity;
        return entity;
    }
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

    /// <summary>When set, writes to an entity another plugin owns are refused, as the kernel's
    /// plugin-scoped store refuses them.</summary>
    public Guid? PluginEntityId { get; set; }

    private void EnforceOwnership(LeafEntity entity)
    {
        if (PluginEntityId is { } mine
            && entity.Data["owner_plugin"] is JsonValue value && value.TryGetValue<string>(out var text)
            && Guid.TryParse(text, out var owner) && owner != mine)
            throw new InvalidOperationException(
                $"Plugin 'leaf-commons' cannot modify entity '{entity.Slug}' -- it is owned by another plugin");
    }

    public Task<LeafEntity> PatchAsync(Guid id, JsonObject dataPatch, string? name = null, CancellationToken ct = default)
    {
        var current = byId[id];
        EnforceOwnership(current);
        var data = (JsonObject)current.Data.DeepClone();
        foreach (var item in dataPatch) data[item.Key] = item.Value?.DeepClone();
        var entity = New(id, current.TypeSlug, current.Slug, name ?? current.Name, data, current.CreatedAt);
        byId[id] = entity;
        return Task.FromResult(entity);
    }

    public Task<LeafEntity> ReplaceDataAsync(Guid id, JsonObject data, string? name = null, CancellationToken ct = default)
    {
        var current = byId[id];
        EnforceOwnership(current);
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

    private LeafEntity New(Guid id, string typeSlug, string slug, string name, JsonObject data,
        DateTimeOffset? created = null, string createdBy = "test")
    {
        var updated = DateTimeOffset.UnixEpoch.AddTicks(++revision);
        return new(id, typeSlug, slug, name, (JsonObject)data.DeepClone(), created ?? updated,
            updated, createdBy);
    }
}
