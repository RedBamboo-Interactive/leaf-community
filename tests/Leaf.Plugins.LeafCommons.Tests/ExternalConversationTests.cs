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
        Assert.NotNull(opened.SentinelCompute);
        Assert.Equal("anthropic-direct", opened.SentinelCompute!.Provider);
        Assert.Equal(2, fixture.Provider.Inputs.Count);
        Assert.All(fixture.Provider.Inputs, input =>
        {
            Assert.Equal("leaf-commons", input.Metadata!["transport"]!.GetValue<string>());
            Assert.Equal("anthropic-direct",
                input.Metadata["sentinel_provider"]!.GetValue<string>());
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
    public async Task Restricted_mode_keeps_the_shareable_tool_free_runner()
    {
        var fixture = await CreateAsync(restrictedMode: true);
        var runtime = new PublicAgentRuntime(
            new CountingModel("{\"action\":\"post\",\"content\":\"Restricted answer.\"}"),
            new AgentShareableStores(root),
            new RelayClientFactory(new FixedHttpClientFactory(fixture.Relay), fixture.State),
            new OutboundFilter(new SecretScanner()), fixture.State, new NullDigestSink(),
            fixture.Coordinator);

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
        var coordinator = new PublicAgentConversationCoordinator(
            new CommonsExternalConversationStore(entities), state,
            new CommonsRuntimeOptionsResolver(entities),
            new RelayClientFactory(new FixedHttpClientFactory(relay), state),
            new OutboundFilter(new SecretScanner()), digest, state, [provider],
            NullLogger<PublicAgentConversationCoordinator>.Instance);
        coordinator.Start();
        return new(entities, state, owner, agent, relay, provider, coordinator);
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
        PublicAgentConversationCoordinator Coordinator);

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
