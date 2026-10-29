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

    private const string AgentId = "a7b059ac-f678-8b58-819b-1bcf3acb22db";

    [Fact]
    public async Task Verified_private_nova_brief_posts_immediately_without_a_decision_call()
    {
        var (state, entities) = await PublicAgentAsync();
        var title = "  Retry strategy — exact title  ";
        var content = "First line\r\nSecond line with  double spaces.\n";
        var model = new CountingModel("{\"action\":\"post\",\"content\":\"MODEL REPLACED THE BRIEF\"}");
        var relay = new MultiAgentRelayHandler();

        var result = await Runtime(state, relay, model).PostBriefAsync(
            new("consult", content, title, ["envoy-b"], OwnerRequested: true), new(AgentId, "owner-1"));

        Assert.Equal("posted", result.Decision);
        Assert.Equal("message-1", result.MessageId);
        Assert.Null(result.Reason);
        Assert.Equal(0, model.Calls);
        // A consult goes to the relay's default consult forum, with the title and text as written.
        var opened = JsonNode.Parse(Assert.Single(relay.Requests, item => item.Route == "POST /v1/posts").Body)!;
        Assert.Equal("200", opened["channelId"]!.GetValue<string>());
        Assert.Equal(title, opened["title"]!.GetValue<string>());
        Assert.Equal(content, opened["content"]!.GetValue<string>());
        Assert.Equal(["envoy-b"], opened["addresses"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Null(opened["scenario"]);
        var history = Assert.Single(await state.ListBriefsAsync());
        Assert.Equal("consult", history.Kind);
        Assert.Equal(title, history.Title);
        Assert.Equal(CommonsStateStore.ContentHash(content), history.ContentHash);
        Assert.Equal("posted", history.Outcome);
        Assert.Equal("message-1", history.MessageId);
        var stored = Assert.Single(await entities.QueryAsync(new EntityQuery { TypeSlug = "leaf-commons-brief" }));
        Assert.Null(stored.Data["content"]);
        Assert.Null(stored.Data["addresses"]);
    }

    [Theory]
    [InlineData("bug", null, "300")]
    [InlineData("showcase", null, "100")]
    [InlineData("intro_draft", null, "400")]
    [InlineData("consult", "#bugs", "300")]
    [InlineData("showcase", "400", "400")]
    public async Task A_brief_goes_to_the_channel_it_names_or_the_one_its_kind_suggests(
        string kind, string? channel, string expected)
    {
        var (state, _) = await PublicAgentAsync();
        var relay = new MultiAgentRelayHandler();

        var result = await Runtime(state, relay).PostBriefAsync(
            new(kind, "A note.", "A title", Channel: channel, OwnerRequested: true), new(AgentId, "owner-1"));

        Assert.Equal("posted", result.Decision);
        var opened = JsonNode.Parse(Assert.Single(relay.Requests, item => item.Route == "POST /v1/posts").Body)!;
        Assert.Equal(expected, opened["channelId"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_brief_naming_an_unknown_channel_is_refused_before_review()
    {
        var (state, _) = await PublicAgentAsync();
        var relay = new MultiAgentRelayHandler();

        var result = await Runtime(state, relay).PostBriefAsync(
            new("showcase", "A note.", Channel: "nowhere", OwnerRequested: true), new(AgentId, "owner-1"));

        Assert.Equal("rejected", result.Decision);
        Assert.Equal("unknown_channel", result.Reason);
        Assert.DoesNotContain(relay.Requests, item => item.Route == "POST /v1/posts");
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
    public async Task Addressed_showcase_text_reply_posts_to_its_message_chain_and_leaves_the_context_line_to_the_relay()
    {
        var relay = new FakeRelay();
        var runner = new EnvoyRunner(
            new CountingModel("{\"action\":\"post\",\"content\":\"2 + 2 is 4.\"}"),
            new ShareableStore(root), relay, new OutboundFilter(new SecretScanner()),
            new MemoryAudit(), [], []);

        var result = await runner.RunAsync(new("message:showcase-root", "ping",
            "Hello! What's 2+2?", TriggerId: "live-1644", Addressed: true,
            Channel: "showcase", ReplyToDisplayName: "Laurent"));

        Assert.Equal("done", result.State);
        var reply = Assert.Single(relay.Replies);
        Assert.Equal("message:showcase-root", reply.ThreadRef);
        Assert.Equal("2 + 2 is 4.", reply.Content);
    }

    [Fact]
    public async Task Addressed_text_channel_failure_posts_the_exact_safe_fallback()
    {
        var relay = new FakeRelay();
        var runner = new EnvoyRunner(
            new ThrowingModel(new ToolFreeGenerateException("fixture", "gateway_bad_gateway", 502)),
            new ShareableStore(root), relay, new OutboundFilter(new SecretScanner()),
            new MemoryAudit(), [], []);

        var result = await runner.RunAsync(new("message:showcase-root", "ping",
            "question", TriggerId: "ping", Addressed: true,
            Channel: "showcase", ReplyToDisplayName: "<@123> Laurent"));

        Assert.Equal("failed", result.State);
        var reply = Assert.Single(relay.Replies);
        Assert.Equal(EnvoyRunner.CoverageFallback, reply.Content);
    }

    [Theory]
    [InlineData(403, "gateway_forbidden")]
    [InlineData(422, "tool_free_unavailable")]
    [InlineData(502, "gateway_bad_gateway")]
    public async Task Addressed_model_failure_is_audited_and_delivers_owner_notice(int status, string reason)
    {
        var (state, entities) = await PublicAgentAsync();
        var model = new ThrowingModel(new ToolFreeGenerateException("fixture failure", reason, status));
        var digests = new CapturingDigestSink();

        var result = await Runtime(state, new MultiAgentRelayHandler(), model, digests).HandleAsync(AgentId,
            new("thread-1", "ping", "what is 2 + 2?", TriggerId: "ping-1", Addressed: true));

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
        var (state, entities) = await PublicAgentAsync(canaries: ["CANARY-BRIEF"]);
        var model = new CountingModel("{\"action\":\"post\",\"content\":\"irrelevant\"}");
        var relay = new MultiAgentRelayHandler();

        var result = await Runtime(state, relay, model).PostBriefAsync(
            new("consult", "CANARY-BRIEF", "Safe title", ["envoy-b"], OwnerRequested: true),
            new(AgentId, "owner-1"));

        Assert.Equal("blocked", result.Decision);
        Assert.Equal("canary", result.Reason);
        Assert.Null(result.MessageId);
        Assert.Equal(0, model.Calls);
        Assert.Empty(relay.Requests);
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
        var (state, _) = await PublicAgentAsync();
        var relay = new MultiAgentRelayHandler(rejectFirstPost: "link_not_allowed");

        var result = await Runtime(state, relay).PostBriefAsync(
            new("consult", "See https://github.com/example/repo", "Reference", [], OwnerRequested: true),
            new(AgentId, "owner-1"));

        Assert.Equal("rejected", result.Decision);
        Assert.Equal("link_not_allowed", result.Reason);
        Assert.Null(result.MessageId);
        Assert.Single(relay.Requests, item => item.Route == "POST /v1/posts");
        var history = Assert.Single(await state.ListBriefsAsync());
        Assert.Equal("rejected", history.Outcome);
        Assert.Equal("link_not_allowed", history.Reason);
    }

    private static async Task<(CommonsStateStore State, MemoryEntityStore Entities)> PublicAgentAsync(
        ConversationSwitches? switches = null, string[]? canaries = null)
    {
        var entities = new MemoryEntityStore();
        var state = new CommonsStateStore(entities);
        await state.SaveOwnerSettingsAsync(new(null, "https://relay.example", "Laurent", [], canaries ?? [], [], "owner-1"));
        await state.SavePublicAgentAsync(new(null, AgentId, "Nova", true, "credential", "active", null,
            switches ?? ConversationSwitches.Default, [], RestrictedMode: true));
        return (state, entities);
    }

    private PublicAgentRuntime Runtime(CommonsStateStore state, MultiAgentRelayHandler relay,
        IToolFreeModelClient? model = null, IDigestSink? digests = null)
        => new(model ?? new CountingModel("unused"), new AgentShareableStores(root),
            new RelayClientFactory(new FixedHttpClientFactory(relay), state),
            new OutboundFilter(new SecretScanner()), state, digests ?? new NullDigestSink(),
            outboundReview: TestSentinel.Passing(new MemoryEntityStore()));

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
        ConversationSwitches.Default, [], [], [], null, [], owner, "agent-1");
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
