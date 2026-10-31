using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.LeafCommons.Tests;

public sealed partial class ExternalConversationTests : IDisposable
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
    public async Task Public_session_runs_on_the_Agents_own_provider_and_tier()
    {
        var fixture = await CreateAsync();
        SeedAgentCompute(fixture.Entities, "anthropic-direct", "deep");
        fixture.Entities.Seed(Guid.NewGuid(), "quality-mode", "Deep Claude (custom)", "deep-claude-custom",
            new JsonObject { ["provider"] = "anthropic-direct", ["quality_tier"] = "deep", ["model"] = "custom" });

        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, Request("ping-1", "Hello"));

        var opened = Assert.Single(fixture.Provider.Opens);
        Assert.Equal("deep-claude", opened.SessionCompute!.ModeId);
        Assert.Equal("anthropic-direct", opened.SessionCompute.Provider);
        Assert.Equal("claude-model", opened.SessionCompute.Model);
        Assert.Equal("deep-claude", fixture.Provider.Inputs.Single().Metadata!["session_quality_mode"]!.GetValue<string>());
    }

    [Fact]
    public async Task Agent_without_a_provider_keeps_the_fallback_mode()
    {
        var resolver = new CommonsRuntimeOptionsResolver(QualityModes(new MemoryEntityStore()));

        var mode = await resolver.ResolveForAgentAsync(Guid.NewGuid().ToString());

        Assert.Equal(CommonsRuntimeOptionsResolver.FallbackQualityMode, mode.Id);
    }

    [Fact]
    public async Task Agent_on_a_provider_without_a_matching_mode_fails_instead_of_borrowing_another()
    {
        var entities = QualityModes(new MemoryEntityStore());
        var agentId = Guid.NewGuid();
        entities.Seed(agentId, "agent", "Agent", "agent-x",
            new JsonObject { ["provider"] = "ollama-local", ["quality_tier"] = "deep" });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CommonsRuntimeOptionsResolver(entities).ResolveForAgentAsync(agentId.ToString()));

        Assert.Contains("ollama-local", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_opened_on_another_mode_moves_the_thread_to_a_new_generation()
    {
        var fixture = await CreateAsync();
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, Request("ping-1", "On the old mode"));
        Assert.Equal("deep-codex", (await new CommonsExternalConversationStore(fixture.Entities)
            .FindAsync(AgentId, "message:root"))!.QualityMode);

        SeedAgentCompute(fixture.Entities, "anthropic-direct", "deep");
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, Request("ping-2", "Now on the Agent's mode"));
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, Request("ping-3", "Still on it"));

        Assert.Equal([1L, 2L], fixture.Provider.Opens.Select(open => open.Generation).ToArray());
        Assert.Equal("deep-claude", fixture.Provider.Opens[1].SessionCompute!.ModeId);
        var record = await new CommonsExternalConversationStore(fixture.Entities).FindAsync(AgentId, "message:root");
        Assert.Equal(2, record!.Generation);
        Assert.Equal("session-2", record.SessionId);
        Assert.Equal("deep-claude", record.QualityMode);
    }

    private static void SeedAgentCompute(MemoryEntityStore entities, string provider, string tier)
    {
        var providerId = Guid.NewGuid();
        var tierId = Guid.NewGuid();
        entities.Seed(providerId, "provider", provider, provider, []);
        entities.Seed(tierId, "quality-tier", tier, tier, []);
        entities.Seed(Guid.Parse(AgentId), "agent", "Nova", "nova", new JsonObject
        {
            ["provider"] = providerId.ToString(),
            ["quality_tier"] = tierId.ToString(),
        });
        entities.Seed(Guid.NewGuid(), "quality-mode", "Deep Claude", "deep-claude", new JsonObject
        {
            ["provider"] = provider, ["quality_tier"] = tier, ["model"] = "claude-model", ["is_default"] = true,
        });
    }

    private static MemoryEntityStore QualityModes(MemoryEntityStore entities)
    {
        entities.Seed(Guid.NewGuid(), "quality-mode", "Deep Codex", "deep-codex",
            new JsonObject { ["provider"] = "codex-default", ["quality_tier"] = "deep" });
        return entities;
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
        Assert.False(body.RootElement.TryGetProperty("scenario", out _));
        Assert.Equal("message:root", body.RootElement.GetProperty("threadRef").GetString());
        // The relay writes the "Replying to" line of a text-channel chain reply.
        Assert.Equal("The failing cache key is stale; clear it and retry.", body.RootElement.GetProperty("content").GetString());
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
            Assert.Equal("I wrote a reply but couldn't post it here (error code: private_content).",
                body.RootElement.GetProperty("content").GetString());
        Assert.DoesNotContain("canary", post.Body, StringComparison.OrdinalIgnoreCase);
        var blocked = Assert.Single(await fixture.State.ListAuditsAsync(),
            item => item.Event == "envoy_blocked");
        Assert.Equal("canary", blocked.Metadata["reason"]!.GetValue<string>());
        Assert.True(blocked.Metadata["alarm"]!.GetValue<bool>());
        Assert.Contains(await fixture.State.ListDigestsAsync(),
            digest => digest.Type == "owner_notice");
    }

    [Fact]
    public async Task Relay_rejection_of_settled_output_posts_only_a_notice_with_the_relay_code()
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
        Assert.Equal("I wrote a reply but couldn't post it here (error code: relay_link_not_allowed).",
            fallback.RootElement.GetProperty("content").GetString());
        Assert.Contains(await fixture.State.ListDigestsAsync(),
            digest => digest.Type == "owner_notice");
    }

    [Fact]
    public async Task A_reply_quoting_a_local_path_is_blocked_with_the_local_path_code()
    {
        var fixture = await CreateAsync();
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner,
            Request("ping-1", "Where does the crash log go?"));
        fixture.Provider.Messages =
        [
            Message(1, "assistant", "text", "It is in C:\\Users\\fred\\AppData\\Leaf\\crash.log."),
        ];

        await fixture.Provider.NotifyAsync();
        await fixture.Coordinator.WhenIdleAsync();

        var post = Assert.Single(fixture.Relay.Requests, item => item.Route == "POST /v1/posts");
        Assert.DoesNotContain("fred", post.Body, StringComparison.Ordinal);
        using (var body = JsonDocument.Parse(post.Body))
            Assert.Equal("I wrote a reply but couldn't post it here (error code: local_path).",
                body.RootElement.GetProperty("content").GetString());
        Assert.Contains(await fixture.State.ListDigestsAsync(), digest => digest.Type == "owner_notice"
            && digest.Content.Contains("`user-profile-path`", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("canary", "private_content")]
    [InlineData("personal-word", "private_content")]
    [InlineData("secret:github-token", "private_content")]
    [InlineData("absolute-path", "local_path")]
    [InlineData("user-profile-path", "local_path")]
    [InlineData("content-too-long", "reply_too_long")]
    [InlineData("empty", "empty_reply")]
    [InlineData("relay_link_not_allowed", "relay_link_not_allowed")]
    [InlineData("external_conversation_failed", "external_conversation_failed")]
    [InlineData("Weird Code! (with) <html>", "weird_code___with___html")]
    [InlineData(null, "unknown")]
    [InlineData("---", "unknown")]
    public void Delivery_failures_have_a_public_code(string? reason, string code)
    {
        Assert.Equal(code, CommonsOutboundReview.PublicErrorCode(reason));
        Assert.True(CommonsOutboundReview.IsFixedNotice(CommonsOutboundReview.UndeliveredNotice(reason)));
    }

    [Fact]
    public void A_public_code_is_bounded()
    {
        var code = CommonsOutboundReview.PublicErrorCode(new string('x', 200));
        Assert.Equal(CommonsOutboundReview.MaxErrorCodeLength, code.Length);
        Assert.True(CommonsOutboundReview.IsFixedNotice(CommonsOutboundReview.UndeliveredNotice(code)));
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
        // The settled-turn review has no request scope to inherit, so it carries its own
        // provenance; without it RedLeaf refuses the Compute call (Fred's install, 2026-10-28).
        var provenance = Assert.IsType<ComputeProvenance>(request.ComputeProvenance);
        Assert.Equal(LeafCommonsPlugin.PluginId, provenance.Origin.App.Id);
        Assert.Equal("agent", provenance.Actor.Kind);
        Assert.Equal("system", provenance.OnBehalfOf.Kind);
        Assert.Contains(provenance.Context, item => item.Kind == "external-conversation");
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
    [InlineData(EnvoyRunner.CoverageFallback, true)]
    [InlineData("Replying to Morgan: " + EnvoyRunner.CoverageFallback, false)]
    [InlineData("I wrote a reply but couldn't post it here (error code: local_path).", true)]
    [InlineData("I wrote a reply but couldn't post it here (error code: local_path). His PIN is 4821.", false)]
    [InlineData("I wrote a reply but couldn't post it here (error code: PIN 4821).", false)]
    [InlineData("I wrote a reply but couldn't post it here (error code: local_path).\nHis PIN is 4821", false)]
    public async Task Only_the_exact_fixed_fallback_skips_review(string content, bool skipped)
    {
        var fixture = await CreateAsync();
        var relay = new RecordingEnvoyRelay();
        var reviewed = new ReviewedEnvoyRelay(relay,
            new CommonsOutboundReview(fixture.Sentinel), AgentId, fixture.State);

        await reviewed.PostAsync("message:root", content);

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
            new CommonsOutboundReview(fixture.Sentinel), AgentId, fixture.State);

        var prefixed = await reviewed.PostAsync("message:root",
            "his PIN is 4821: " + EnvoyRunner.CoverageFallback);
        var addressed = await reviewed.PostAsync("message:root",
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

        public Task<RelayPostResult> OpenAsync(string channelId, string? title, string content,
            IReadOnlyList<string>? addresses = null, string? idempotencyKey = null, CancellationToken ct = default)
            => PostAsync(channelId, content, addresses, idempotencyKey, ct);
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
        string? rejectFirstPost = null, IAssets? assets = null, HttpMessageHandler? cdn = null,
        bool noSentinel = false, string relayBaseUrl = "https://relay.example", string credential = "credential",
        HttpMessageHandler? liveRelay = null, KernelBoundSentinel? kernel = null,
        Microsoft.AspNetCore.Http.IHttpContextAccessor? requests = null, string ownerUserId = "owner-1")
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
        var owner = await state.SaveOwnerSettingsAsync(new(null, relayBaseUrl,
            "Lynial", [], canaries ?? [], [], ownerUserId, "Lynial"));
        var agent = await state.SavePublicAgentAsync(new(null, AgentId, "Nova", true,
            credential, "active", null,
            new ConversationSwitches(StartsConversations: true), [],
            EnvoyId: "envoy-own", RestrictedMode: restrictedMode));
        var relay = new MultiAgentRelayHandler("Lynial", rejectFirstPost);
        var provider = new FakeExternalProvider();
        var digest = new CommonsDigestSink(state, new DigestFormatter(), state);
        var sentinel = kernel?.Inner ?? new TestSentinel();
        var coordinator = new PublicAgentConversationCoordinator(
            new CommonsExternalConversationStore(entities), state,
            new CommonsRuntimeOptionsResolver(entities),
            new RelayClientFactory(new FixedHttpClientFactory(liveRelay ?? relay), state),
            new OutboundFilter(new SecretScanner()), digest, state, [provider],
            NullLogger<PublicAgentConversationCoordinator>.Instance,
            noSentinel ? CommonsOutboundReview.Unavailable
                : new CommonsOutboundReview((IUntrustedContentReview?)kernel ?? sentinel, entities: entities, requests: requests),
            cdn is null || assets is null ? null : new CommonsInboundImages(new HttpClient(cdn), assets, state),
            assets)
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

    [Fact]
    public async Task A_ping_carries_what_was_said_in_the_thread_since_the_Agent_last_looked()
    {
        // Sibylle, 2026-10-29: "je ne lis pas tout le thread". Being addressed decides when the
        // Agent speaks, not what it can read.
        var fixture = await CreateAsync();
        var start = DateTimeOffset.Parse("2026-10-29T18:00:00Z");
        for (var index = 0; index < 120; index++)
            fixture.Relay.ThreadMessages.Add(Human($"old-{index}", start.AddSeconds(index), "Bob", $"filler {index}"));
        fixture.Relay.ThreadMessages.Add(Human("h-1", start.AddMinutes(5), "Bob", "The build fails on step 3."));
        fixture.Relay.ThreadMessages.Add(Human("h-2", start.AddMinutes(6), "Lynial", "Can you look at this?"));

        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, Request("ping-1", "Can you look at this?"));

        var first = fixture.Provider.Inputs[^1];
        Assert.Contains("Bob: The build fails on step 3.", first.Content, StringComparison.Ordinal);
        Assert.EndsWith("[The message addressed to you]\nCan you look at this?", first.Content, StringComparison.Ordinal);
        Assert.Equal(1, first.Content.Split("Can you look at this?").Length - 1);
        Assert.Equal(PublicAgentConversationCoordinator.ThreadContextMessages,
            first.Metadata!["thread_context_messages"]!.GetValue<int>());

        fixture.Relay.ThreadMessages.Add(Envoy("e-1", start.AddMinutes(7), "Nova · Lynial", fixture.Agent.EnvoyId, "Looking."));
        fixture.Relay.ThreadMessages.Add(Human("h-3", start.AddMinutes(8), "Bob", "It also fails on step 4."));
        fixture.Relay.ThreadMessages.Add(Human("h-4", start.AddMinutes(9), "Lynial", "And now?"));

        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, Request("ping-2", "And now?"));

        var second = fixture.Provider.Inputs[^1].Content;
        Assert.Contains("Bob: It also fails on step 4.", second, StringComparison.Ordinal);
        Assert.DoesNotContain("step 3", second, StringComparison.Ordinal);
        Assert.DoesNotContain("Looking.", second, StringComparison.Ordinal);
    }

    [Fact]
    public void Thread_context_without_anything_new_is_the_ping_alone()
    {
        var seen = DateTimeOffset.Parse("2026-10-29T18:00:00Z");
        var context = PublicAgentConversationCoordinator.BuildThreadContext(
            [new("h-1", seen, new("human", "Bob"), "Hi"), new("h-2", seen.AddMinutes(1), new("human", "Bob"), "Ping")],
            seen, null, "Ping");

        Assert.Equal(("Ping", 0, (DateTimeOffset?)seen.AddMinutes(1)), (context.Content, context.Count, context.SeenAt));
    }

    private static object Human(string id, DateTimeOffset at, string name, string text) => new
    {
        id, createdAt = at.ToUnixTimeMilliseconds(), content = text, scenario = "consult",
        actor = new { kind = "human", discordUserId = "u-" + name, displayName = name },
    };

    private static object Envoy(string id, DateTimeOffset at, string persona, string? envoyId, string text) => new
    {
        id, createdAt = at.ToUnixTimeMilliseconds(), content = text, scenario = "consult_reply",
        actor = new { kind = "envoy", envoyId, personaName = persona },
    };

    [Fact]
    public async Task Outbound_review_provenance_names_the_owner_when_they_resolve_to_a_user()
    {
        var entities = new MemoryEntityStore();
        var userId = Guid.NewGuid();
        entities.Seed(userId, "user", "Fred", "fred", new JsonObject());

        var owner = await CommonsComputeProvenance.BeneficiaryAsync(entities, userId.ToString(), default);
        var missing = await CommonsComputeProvenance.BeneficiaryAsync(entities, Guid.NewGuid().ToString(), default);

        Assert.Equal(("user", userId.ToString()), (owner.Kind, owner.Id));
        Assert.Equal("system", missing.Kind);
    }

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
