using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.LeafCommons.Tests;

public sealed class ExternalConversationTests : IDisposable
{
    private const string AgentId = CommonsStateStore.LegacyNovaAgentId;
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "leaf-commons-external-" + Guid.NewGuid().ToString("N"));

    public ExternalConversationTests() => Directory.CreateDirectory(root);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public async Task Ping_opens_then_reuses_the_same_agent_scoped_external_conversation()
    {
        var fixture = await CreateAsync();
        var first = Request("ping-1", "How should I debug this?");
        var second = Request("ping-2", "Here is the next observation.") with
        {
            SenderProvenance =
                "{\"kind\":\"envoy\",\"envoyId\":\"peer-1\",\"personaName\":\"Atlas · North\"}",
        };

        Assert.Equal("admitted", (await fixture.Coordinator.SubmitAsync(
            fixture.Agent, fixture.Owner, first)).State);
        Assert.Equal("admitted", (await fixture.Coordinator.SubmitAsync(
            fixture.Agent, fixture.Owner, second)).State);

        var opened = Assert.Single(fixture.Provider.Opens);
        Assert.Equal(AgentId, opened.AgentId);
        Assert.Equal("leaf-commons", opened.Scope.Transport);
        Assert.Equal("message:root", opened.Scope.ConversationId);
        // The session keeps its own quality mode; the Sentinel's compute is not Commons' choice.
        Assert.Equal("anthropic-direct", opened.SessionCompute!.Provider);
        Assert.Null(opened.SentinelCompute);
        Assert.Equal(2, fixture.Provider.Inputs.Count);
        Assert.All(fixture.Provider.Inputs, input =>
        {
            Assert.Equal("leaf-commons", input.Metadata!["transport"]!.GetValue<string>());
            Assert.DoesNotContain(input.Metadata, pair => pair.Key.StartsWith("sentinel_", StringComparison.Ordinal));
        });
        Assert.Equal("discord:human-1", fixture.Provider.Inputs[0].Requestor.ExternalId);
        Assert.Equal("Morgan", fixture.Provider.Inputs[0].Requestor.DisplayName);
        Assert.Equal("envoy:peer-1", fixture.Provider.Inputs[1].Requestor.ExternalId);
        Assert.Equal("Atlas · North", fixture.Provider.Inputs[1].Requestor.DisplayName);
    }

    [Fact]
    public async Task Clear_advances_generation_and_opens_a_fresh_persistent_session()
    {
        var fixture = await CreateAsync();
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner,
            Request("ping-1", "First context."));

        var cleared = await fixture.Coordinator.ClearAsync(AgentId, "message:root");

        Assert.NotNull(cleared);
        Assert.Equal(2, cleared.Generation);
        Assert.Equal(2, fixture.Provider.Opens.Count);
        Assert.Equal(2, fixture.Provider.Opens[1].Generation);
    }

    [Fact]
    public async Task Settled_final_answer_is_filtered_and_posted_through_the_relay()
    {
        var fixture = await CreateAsync();
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner,
            Request("ping-1", "Investigate the bug."));
        fixture.Provider.Messages =
        [
            Message(1, "assistant", "commentary", "I am inspecting it."),
            Message(2, "assistant", "tool_use", null),
            Message(3, "assistant", "text", "The failing cache key is stale; clear it and retry."),
        ];

        await fixture.Provider.NotifyAsync();
        await fixture.Coordinator.WhenIdleAsync();

        var post = Assert.Single(fixture.Relay.Requests,
            item => item.Route == "POST /v1/posts");
        using var body = JsonDocument.Parse(post.Body);
        Assert.Equal("channel_reply", body.RootElement.GetProperty("scenario").GetString());
        Assert.Equal("message:root", body.RootElement.GetProperty("threadRef").GetString());
        Assert.Contains("The failing cache key is stale", body.RootElement.GetProperty("content").GetString());
        Assert.DoesNotContain("inspecting", post.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tool_use", post.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Canary_in_settled_output_is_blocked_with_alarm_and_safe_fallback()
    {
        var fixture = await CreateAsync(canaries: ["PUBLIC-CANARY"]);
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner,
            Request("ping-1", "Investigate the bug."));
        fixture.Provider.Messages =
        [
            Message(1, "assistant", "text", "Never send PUBLIC-CANARY outside."),
        ];

        await fixture.Provider.NotifyAsync();
        await fixture.Coordinator.WhenIdleAsync();

        var post = Assert.Single(fixture.Relay.Requests,
            item => item.Route == "POST /v1/posts");
        Assert.DoesNotContain("PUBLIC-CANARY", post.Body, StringComparison.Ordinal);
        using (var body = JsonDocument.Parse(post.Body))
            Assert.EndsWith(EnvoyRunner.CoverageFallback,
                body.RootElement.GetProperty("content").GetString());
        var blocked = Assert.Single(await fixture.State.ListAuditsAsync(),
            item => item.Event == "envoy_blocked");
        Assert.Equal("canary", blocked.Metadata["reason"]!.GetValue<string>());
        Assert.True(blocked.Metadata["alarm"]!.GetValue<bool>());
        Assert.Contains(await fixture.State.ListDigestsAsync(),
            digest => digest.Type == "owner_notice");
    }

    [Fact]
    public async Task Relay_rejection_of_settled_output_posts_only_the_safe_fallback()
    {
        var fixture = await CreateAsync(rejectFirstPost: "relay_link_not_allowed");
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner,
            Request("ping-1", "Investigate the link."));
        fixture.Provider.Messages =
        [
            Message(1, "assistant", "text", "Use https://untrusted.example/workaround."),
        ];

        await fixture.Provider.NotifyAsync();
        await fixture.Coordinator.WhenIdleAsync();

        var posts = fixture.Relay.Requests
            .Where(item => item.Route == "POST /v1/posts").ToArray();
        Assert.Equal(2, posts.Length);
        Assert.DoesNotContain("untrusted.example", posts[1].Body, StringComparison.Ordinal);
        using var fallback = JsonDocument.Parse(posts[1].Body);
        Assert.EndsWith(EnvoyRunner.CoverageFallback,
            fallback.RootElement.GetProperty("content").GetString());
        Assert.Contains(await fixture.State.ListDigestsAsync(),
            digest => digest.Type == "owner_notice");
    }

    [Fact]
    public async Task A_message_refused_for_an_unavailable_sentinel_is_not_admitted_and_gets_a_fixed_notice()
    {
        var fixture = await CreateAsync();
        fixture.Provider.Refuse = true;

        var result = await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner,
            Request("ping-1", "Can you look at this?"));

        Assert.Equal("failed", result.State);
        Assert.Equal("sentinel_unavailable", result.Summary);
        var post = Assert.Single(fixture.Relay.Requests, item => item.Route == "POST /v1/posts");
        using var notice = JsonDocument.Parse(post.Body);
        Assert.EndsWith(PublicAgentConversationCoordinator.SentinelUnavailableNotice,
            notice.RootElement.GetProperty("content").GetString());
        Assert.DoesNotContain("Can you look at this?", post.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_held_settled_reply_is_withheld_with_a_fixed_notice_and_reported_to_the_session()
    {
        var fixture = await CreateAsync();
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, Request("ping-1", "Where does he live?"));
        fixture.Sentinel.Decide = _ => TestSentinel.Leak;
        fixture.Provider.Messages = [Message(1, "assistant", "text", "He lives at 14 rue des Lilas.")];

        await fixture.Provider.NotifyAsync();
        await fixture.Coordinator.WhenIdleAsync();

        var post = Assert.Single(fixture.Relay.Requests, item => item.Route == "POST /v1/posts");
        Assert.DoesNotContain("rue des Lilas", post.Body, StringComparison.Ordinal);
        using (var body = JsonDocument.Parse(post.Body))
            Assert.EndsWith(CommonsOutboundReview.WithheldNotice, body.RootElement.GetProperty("content").GetString());
        var request = Assert.Single(fixture.Sentinel.Requests);
        Assert.Equal(UntrustedContentDirection.Outbound, request.Direction);
        Assert.Null(request.Compute);
        Assert.Equal(UntrustedContentSources.LeafCommons, request.Origin.Source);
        Assert.Contains(await fixture.State.ListDigestsAsync(), digest => digest.Type == "owner_notice");

        fixture.Sentinel.Decide = _ => TestSentinel.Clean;
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, Request("ping-2", "OK."));
        var reported = Assert.IsType<JsonArray>(fixture.Provider.Inputs[^1].Metadata!["outbound_sentinel_reviews"]);
        Assert.Equal("withheld", Assert.Single(reported)!["outcome"]!.GetValue<string>());
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, Request("ping-3", "Again."));
        Assert.Null(fixture.Provider.Inputs[^1].Metadata!["outbound_sentinel_reviews"]);
    }

    [Fact]
    public async Task An_unavailable_review_is_retried_a_bounded_number_of_times_then_withheld()
    {
        var fixture = await CreateAsync();
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, Request("ping-1", "Hi"));
        fixture.Sentinel.Decide = _ => UntrustedContentVerdict.Unavailable();
        fixture.Provider.Messages = [Message(1, "assistant", "text", "Here is the fix.")];

        await fixture.Provider.NotifyAsync();
        await fixture.Coordinator.WhenIdleAsync();

        Assert.Equal(PublicAgentConversationCoordinator.MaxOutboundReviewAttempts, fixture.Sentinel.Requests.Count);
        var post = Assert.Single(fixture.Relay.Requests, item => item.Route == "POST /v1/posts");
        Assert.DoesNotContain("Here is the fix.", post.Body, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(post.Body);
        Assert.EndsWith(CommonsOutboundReview.UnavailableNotice, body.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public async Task An_unavailable_review_that_recovers_delivers_the_reply()
    {
        var fixture = await CreateAsync();
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, Request("ping-1", "Hi"));
        var calls = 0;
        fixture.Sentinel.Decide = _ => ++calls == 1 ? UntrustedContentVerdict.Unavailable() : TestSentinel.Clean;
        fixture.Provider.Messages = [Message(1, "assistant", "text", "Here is the fix.")];

        await fixture.Provider.NotifyAsync();
        await fixture.Coordinator.WhenIdleAsync();

        Assert.Equal(2, fixture.Sentinel.Requests.Count);
        var post = Assert.Single(fixture.Relay.Requests, item => item.Route == "POST /v1/posts");
        Assert.Contains("Here is the fix.", post.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_verdict_without_the_outbound_direction_is_unavailable()
    {
        var fixture = await CreateAsync();
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, Request("ping-1", "Hi"));
        fixture.Sentinel.EchoDirection = false;
        fixture.Provider.Messages = [Message(1, "assistant", "text", "Here is the fix.")];

        await fixture.Provider.NotifyAsync();
        await fixture.Coordinator.WhenIdleAsync();

        var post = Assert.Single(fixture.Relay.Requests, item => item.Route == "POST /v1/posts");
        Assert.DoesNotContain("Here is the fix.", post.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restricted_mode_answers_pass_the_outbound_review_or_fall_back()
    {
        var fixture = await CreateAsync(restrictedMode: true);
        fixture.Sentinel.Decide = request => request.Content.Contains("Restricted answer", StringComparison.Ordinal)
            ? TestSentinel.Leak : TestSentinel.Clean;
        var runtime = new PublicAgentRuntime(
            new CountingModel("{\"action\":\"post\",\"content\":\"Restricted answer.\"}"),
            new AgentShareableStores(root),
            new RelayClientFactory(new FixedHttpClientFactory(fixture.Relay), fixture.State),
            new OutboundFilter(new SecretScanner()), fixture.State, new NullDigestSink(),
            fixture.Coordinator,
            new CommonsOutboundReview(fixture.Sentinel));

        await runtime.HandleAsync(AgentId, Request("restricted-1", "What is the answer?"));

        var posts = fixture.Relay.Requests.Where(item => item.Route == "POST /v1/posts").ToArray();
        Assert.DoesNotContain(posts, item => item.Body.Contains("Restricted answer", StringComparison.Ordinal));
        Assert.Contains(posts, item => JsonDocument.Parse(item.Body).RootElement.GetProperty("content")
            .GetString()!.EndsWith(EnvoyRunner.CoverageFallback, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_held_brief_is_blocked_before_the_relay()
    {
        var fixture = await CreateAsync();
        fixture.Sentinel.Decide = _ => TestSentinel.Leak;
        var runtime = new PublicAgentRuntime(new CountingModel("unused"), new AgentShareableStores(root),
            new RelayClientFactory(new FixedHttpClientFactory(fixture.Relay), fixture.State),
            new OutboundFilter(new SecretScanner()), fixture.State, new NullDigestSink(), fixture.Coordinator,
            new CommonsOutboundReview(fixture.Sentinel));

        var result = await runtime.PostBriefAsync(new("showcase", "Laurent's address is 14 rue des Lilas"),
            new(AgentId, "test"));

        Assert.Equal("blocked", result.Decision);
        Assert.Equal("sentinel_held", result.Reason);
        Assert.DoesNotContain(fixture.Relay.Requests, item => item.Route == "POST /v1/posts");
    }

    [Fact]
    public async Task A_new_settle_does_not_shorten_the_backoff_and_a_restart_keeps_the_count()
    {
        var fixture = await CreateAsync();
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, Request("ping-1", "Hi"));
        fixture.Sentinel.Decide = _ => UntrustedContentVerdict.Unavailable();
        fixture.Coordinator.OutboundRetryDelay = _ => TimeSpan.FromHours(1);
        fixture.Provider.Messages = [Message(1, "assistant", "text", "Here is the fix.")];

        await fixture.Provider.NotifyAsync();
        await fixture.Coordinator.WhenIdleAsync();
        await fixture.Provider.NotifyAsync();
        await fixture.Coordinator.WhenIdleAsync();

        Assert.Single(fixture.Sentinel.Requests);
        var store = new CommonsExternalConversationStore(fixture.Entities);
        var waiting = await store.FindAsync(AgentId, "message:root");
        Assert.Equal("message-1", waiting!.ReviewWaitUid);
        Assert.Equal(1, waiting.ReviewAttempts);
        Assert.True(waiting.ReviewNextAt > DateTimeOffset.UtcNow.AddMinutes(50));
        Assert.DoesNotContain(fixture.Relay.Requests, item => item.Route == "POST /v1/posts");

        // Restart once the wait is over: the count continues, it does not start again.
        fixture.Coordinator.Dispose();
        await store.SaveAsync(waiting with { ReviewNextAt = DateTimeOffset.UtcNow.AddSeconds(-1) });
        var restarted = Restart(fixture);
        restarted.Start();
        await restarted.WhenIdleAsync();

        Assert.Equal(PublicAgentConversationCoordinator.MaxOutboundReviewAttempts, fixture.Sentinel.Requests.Count);
        var post = Assert.Single(fixture.Relay.Requests, item => item.Route == "POST /v1/posts");
        using var body = JsonDocument.Parse(post.Body);
        Assert.EndsWith(CommonsOutboundReview.UnavailableNotice, body.RootElement.GetProperty("content").GetString());
        Assert.Null((await store.FindAsync(AgentId, "message:root"))!.ReviewWaitUid);
    }

    [Fact]
    public async Task A_failed_reconcile_is_rescheduled()
    {
        var fixture = await CreateAsync();
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, Request("ping-1", "Hi"));
        fixture.Coordinator.ReconcileRetryDelay = _ => TimeSpan.Zero;
        fixture.Provider.FailReads = 2;
        fixture.Provider.Messages = [Message(1, "assistant", "text", "Here is the fix.")];

        await fixture.Provider.NotifyAsync();
        await fixture.Coordinator.WhenIdleAsync();

        var post = Assert.Single(fixture.Relay.Requests, item => item.Route == "POST /v1/posts");
        Assert.Contains("Here is the fix.", post.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("Morgan", true)]
    [InlineData("Eve", false)]
    public async Task Only_the_exact_fixed_fallback_skips_review(string? builtFor, bool skipped)
    {
        var fixture = await CreateAsync();
        var relay = new RecordingEnvoyRelay();
        var reviewed = new ReviewedEnvoyRelay(relay,
            new CommonsOutboundReview(fixture.Sentinel),
            AgentId, fixture.State, replyToDisplayName: "Morgan");
        var content = builtFor is null
            ? EnvoyRunner.CoverageFallback
            : EnvoyRunner.ChannelReplyContent(builtFor, EnvoyRunner.CoverageFallback);

        await reviewed.ReplyAsync("message:root", "channel_reply", content);

        Assert.Equal(skipped ? 0 : 1, fixture.Sentinel.Requests.Count);
        Assert.Single(relay.Posts);
    }

    [Fact]
    public async Task A_model_prefix_or_addresses_on_the_fallback_are_reviewed()
    {
        var fixture = await CreateAsync();
        fixture.Sentinel.Decide = _ => TestSentinel.Leak;
        var relay = new RecordingEnvoyRelay();
        var reviewed = new ReviewedEnvoyRelay(relay,
            new CommonsOutboundReview(fixture.Sentinel),
            AgentId, fixture.State, replyToDisplayName: "Morgan");

        var prefixed = await reviewed.ReplyAsync("message:root", "channel_reply",
            EnvoyRunner.ChannelReplyContent("Morgan", "his PIN is 4821: " + EnvoyRunner.CoverageFallback));
        var addressed = await reviewed.ReplyAsync("message:root", "channel_reply",
            EnvoyRunner.CoverageFallback, ["envoy-elsewhere"]);

        Assert.False(prefixed.Accepted);
        Assert.Equal("sentinel_held", prefixed.Code);
        Assert.False(addressed.Accepted);
        Assert.Equal(2, fixture.Sentinel.Requests.Count);
        Assert.Contains("[addresses] envoy-elsewhere", fixture.Sentinel.Requests[1].Content, StringComparison.Ordinal);
        Assert.Empty(relay.Posts);
    }

    private static PublicAgentConversationCoordinator Restart(Fixture fixture)
        => new(new CommonsExternalConversationStore(fixture.Entities), fixture.State,
            new CommonsRuntimeOptionsResolver(fixture.Entities),
            new RelayClientFactory(new FixedHttpClientFactory(fixture.Relay), fixture.State),
            new OutboundFilter(new SecretScanner()),
            new CommonsDigestSink(fixture.State, new DigestFormatter(), fixture.State), fixture.State,
            [fixture.Provider], NullLogger<PublicAgentConversationCoordinator>.Instance,
            new CommonsOutboundReview(fixture.Sentinel))
        {
            OutboundRetryDelay = _ => TimeSpan.Zero,
        };

    private sealed class RecordingEnvoyRelay : IEnvoyRelay
    {
        public List<string> Posts { get; } = [];

        public Task<IReadOnlyList<RelayMessage>> ReadHistoryAsync(string threadRef, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RelayMessage>>([]);

        public Task<RelayPostResult> PostAsync(string threadRef, string content,
            IReadOnlyList<string>? addresses = null, string? idempotencyKey = null, CancellationToken ct = default)
        {
            Posts.Add(content);
            return Task.FromResult(new RelayPostResult(true, MessageId: "m1"));
        }

        public Task<RelayPostResult> OpenAsync(string scenario, string? threadName, string content,
            IReadOnlyList<string>? addresses = null, string? idempotencyKey = null, CancellationToken ct = default)
            => PostAsync(scenario, content, addresses, idempotencyKey, ct);
    }

    [Fact]
    public async Task Restricted_mode_keeps_the_shareable_tool_free_runner()
    {
        var fixture = await CreateAsync(restrictedMode: true);
        var runtime = new PublicAgentRuntime(
            new CountingModel("{\"action\":\"post\",\"content\":\"Restricted answer.\"}"),
            new AgentShareableStores(root),
            new RelayClientFactory(new FixedHttpClientFactory(fixture.Relay), fixture.State),
            new OutboundFilter(new SecretScanner()), fixture.State, new NullDigestSink(),
            fixture.Coordinator,
            new CommonsOutboundReview(fixture.Sentinel));

        var result = await runtime.HandleAsync(AgentId,
            Request("restricted-ping", "Use the shareable copy only."));

        Assert.Equal("done", result.State);
        Assert.Empty(fixture.Provider.Inputs);
        Assert.Contains(fixture.Relay.Requests,
            item => item.Route == "POST /v1/posts" && item.Body.Contains("Restricted answer."));
    }

    private async Task<Fixture> CreateAsync(
        IReadOnlyList<string>? canaries = null, bool restrictedMode = false,
        string? rejectFirstPost = null)
    {
        var entities = new MemoryEntityStore();
        entities.Seed(Guid.NewGuid(), "quality-mode", "Deep Codex", "deep-codex",
            new JsonObject
            {
                ["provider"] = "anthropic-direct",
                ["quality_tier"] = "deep",
                ["model"] = "quality-model",
            });
        var state = new CommonsStateStore(entities);
        var owner = await state.SaveOwnerSettingsAsync(new(null, "https://relay.example",
            "Lynial", [], canaries ?? [], [], "owner-1", "Lynial"));
        var agent = await state.SavePublicAgentAsync(new(null, AgentId, "Nova", true,
            "credential", "active", null,
            new Dictionary<string, bool> { ["showcase"] = true, ["consult"] = true }, [],
            RestrictedMode: restrictedMode));
        var relay = new MultiAgentRelayHandler("Lynial", rejectFirstPost);
        var provider = new FakeExternalProvider();
        var digest = new CommonsDigestSink(state, new DigestFormatter(), state);
        var sentinel = new TestSentinel();
        var coordinator = new PublicAgentConversationCoordinator(
            new CommonsExternalConversationStore(entities), state,
            new CommonsRuntimeOptionsResolver(entities),
            new RelayClientFactory(new FixedHttpClientFactory(relay), state),
            new OutboundFilter(new SecretScanner()), digest, state, [provider],
            NullLogger<PublicAgentConversationCoordinator>.Instance,
            new CommonsOutboundReview(sentinel))
        {
            OutboundRetryDelay = _ => TimeSpan.Zero,
        };
        coordinator.Start();
        await coordinator.WhenIdleAsync();
        return new(entities, state, owner, agent, relay, provider, coordinator, sentinel);
    }

    private static EnvoyRunRequest Request(string id, string content) => new(
        "message:root", "channel_reply", content,
        "{\"kind\":\"human\",\"discordUserId\":\"human-1\",\"displayName\":\"Morgan\"}",
        id, Addressed: true, Channel: "showcase", ReplyToDisplayName: "Morgan");

    private static ExternalConversationMessage Message(
        long sequence, string role, string kind, string? content) => new(
            new ExternalConversationCursor("epoch-1", sequence), $"message-{sequence}",
            role, kind, content, DateTimeOffset.UtcNow,
            new JsonObject { ["phase"] = kind == "text" ? "final" : "commentary" });

    private sealed record Fixture(
        MemoryEntityStore Entities,
        CommonsStateStore State,
        CommonsOwnerSettings Owner,
        PublicAgentSettings Agent,
        MultiAgentRelayHandler Relay,
        FakeExternalProvider Provider,
        PublicAgentConversationCoordinator Coordinator,
        TestSentinel Sentinel);

    private sealed class FakeExternalProvider : IExternalAgentConversationProvider
    {
        private readonly List<Func<ExternalConversationSettled, CancellationToken, Task>> listeners = [];
        private ExternalConversationHandle? handle;
        public string ProviderId => "fake-external";
        public List<ExternalConversationOpenRequest> Opens { get; } = [];
        public List<ExternalConversationInput> Inputs { get; } = [];
        public IReadOnlyList<ExternalConversationMessage> Messages { get; set; } = [];
        /// <summary>Answers like Nova when its inbound Sentinel review is unavailable.</summary>
        public bool Refuse { get; set; }
        /// <summary>How many settled reads fail before they succeed.</summary>
        public int FailReads { get; set; }
        public bool CanHandle(string agentSlugOrId) => true;

        public Task<ExternalConversationHandle> OpenAsync(
            ExternalConversationOpenRequest request, CancellationToken ct = default)
        {
            Opens.Add(request);
            if (handle is null || handle.Generation != request.Generation)
                handle = new(ProviderId, request.BindingId, request.Generation,
                    $"conversation-{request.Generation}", $"session-{request.Generation}");
            return Task.FromResult(handle);
        }

        public Task<ExternalConversationAdmission> SendAsync(
            ExternalConversationHandle value, ExternalConversationInput input,
            CancellationToken ct = default)
        {
            handle = value;
            Inputs.Add(input);
            return Task.FromResult(new ExternalConversationAdmission(
                $"admitted-{input.RequestId}",
                Refuse ? ExternalConversationDispositions.RefusedSentinelUnavailable : "delivered"));
        }

        public Task<ExternalConversationPage> ReadSettledAsync(
            ExternalConversationHandle value, ExternalConversationCursor? after = null,
            int limit = 100, CancellationToken ct = default)
        {
            if (FailReads > 0)
            {
                FailReads--;
                throw new InvalidOperationException("RedCompute transcript is unavailable");
            }
            var selected = Messages.Where(item => after is null
                || item.Cursor.Epoch != after.Epoch || item.Cursor.Sequence > after.Sequence)
                .OrderBy(item => item.Cursor.Sequence).ToArray();
            return Task.FromResult(new ExternalConversationPage(selected,
                selected.LastOrDefault()?.Cursor ?? after, IsQuiescent: true));
        }

        public IDisposable SubscribeSettled(
            Func<ExternalConversationSettled, CancellationToken, Task> handler)
        {
            listeners.Add(handler);
            return new TestSubscription(() => listeners.Remove(handler));
        }

        public async Task NotifyAsync()
        {
            var current = handle ?? throw new InvalidOperationException("No conversation was opened");
            var cursor = Messages.LastOrDefault()?.Cursor ?? new("epoch-1", 0);
            foreach (var listener in listeners.ToArray())
                await listener(new(ProviderId, current.BindingId, current.Generation,
                    current.SessionId, cursor), CancellationToken.None);
        }
    }

    private sealed class TestSubscription(Action dispose) : IDisposable
    {
        private Action? action = dispose;
        public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
    }
}
