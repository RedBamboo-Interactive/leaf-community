using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.LeafCommons;
using Xunit;

namespace Leaf.Plugins.LeafCommons.Tests;

public sealed class ThreadsTests
{
    [Fact]
    public async Task Threads_merge_relay_history_safe_local_outcomes_and_quoted_digests()
    {
        var handler = new ThreadRelayHandler();
        var relay = new RelayHttpClient(new HttpClient(handler), _ =>
            Task.FromResult<(Uri, string?)>((new Uri("https://relay.example"), "envoy.secret")));
        var entities = new MemoryEntityStore();
        var state = new CommonsStateStore(entities);
        var settings = await state.SaveSettingsAsync(new(null, "https://relay.example", "envoy.secret", "active",
            "Nova · Laurent", null, new Dictionary<string, bool>(), [], [], [], null, [], "owner-1", "agent-1",
            PublicName: "Laurent", EnvoyId: "envoy-1"));
        await state.RecordAsync("relay_post", new Dictionary<string, object?>
        {
            ["thread"] = "thread-1", ["scenario"] = "consult", ["delivery"] = "safe-fallback",
            ["accepted"] = true, ["messageId"] = "message-1",
        });
        await state.RecordAsync("envoy_blocked", new Dictionary<string, object?>
        {
            ["thread"] = "thread-1", ["scenario"] = "consult", ["reason"] = "absolute-path",
            ["content"] = "BLOCKED CONTENT MUST NEVER APPEAR",
        });
        await state.StoreDigestAsync("owner_notice", "> External Commons quote — owner notice\n> Safe notice", "thread-1");
        var service = new PublicNovaThreads(relay, state);

        var listed = Assert.Single(await service.ListAsync(settings));
        Assert.Equal("engineering", listed.ChannelName);
        Assert.Equal("Exchange cap", listed.Title);
        Assert.True(listed.Unread);
        Assert.Equal("consult", listed.Scenario);

        var thread = await service.GetAsync("thread-1", settings);
        var posted = Assert.Single(thread.Messages, message => message.Id == "message-1");
        Assert.Equal("self", posted.Author.Kind);
        Assert.Equal("fallback", posted.Outcome);
        var blocked = Assert.Single(thread.Messages, message => message.Outcome == "blocked");
        Assert.Equal("", blocked.Text);
        Assert.Equal("absolute-path", blocked.Reason);
        var digest = Assert.Single(thread.Messages, message => message.Id.StartsWith("digest-", StringComparison.Ordinal));
        Assert.Equal("Public Nova digest", digest.Author.Name);
        Assert.StartsWith("> External Commons quote", digest.Text);
        Assert.DoesNotContain("BLOCKED CONTENT MUST NEVER APPEAR",
            string.Join("\n", thread.Messages.Select(message => message.Text)));
    }

    [Fact]
    public async Task Missing_relay_threads_feature_falls_back_to_local_outcomes()
    {
        var handler = new ThreadRelayHandler(threadsAvailable: false);
        var relay = new RelayHttpClient(new HttpClient(handler), _ =>
            Task.FromResult<(Uri, string?)>((new Uri("https://relay.example"), "envoy.secret")));
        var entities = new MemoryEntityStore();
        var state = new CommonsStateStore(entities);
        var settings = await state.SaveSettingsAsync(new(null, "https://relay.example", "envoy.secret", "active",
            "Nova · Laurent", null, new Dictionary<string, bool>(), [], [], [], null, [], "owner-1", "agent-1",
            PublicName: "Laurent"));
        await state.RecordAsync("envoy_failed", new Dictionary<string, object?>
        {
            ["thread"] = "thread-local", ["scenario"] = "bug", ["reason"] = "gateway_forbidden",
        });

        var service = new PublicNovaThreads(relay, state);
        var item = Assert.Single(await service.ListAsync(settings));
        Assert.Equal("thread-local", item.ThreadRef);
        Assert.Equal("bug", item.Scenario);
        var message = Assert.Single((await service.GetAsync("thread-local", settings)).Messages);
        Assert.Equal("failed", message.Outcome);
        Assert.Equal("", message.Text);
    }

    [Fact]
    public async Task Local_outcomes_are_humanized_and_stale_announcement_rows_are_not_threads()
    {
        var relay = new RelayHttpClient(new HttpClient(new ThreadRelayHandler(threadsAvailable: false)), _ =>
            Task.FromResult<(Uri, string?)>((new Uri("https://relay.example"), "envoy.secret")));
        var state = new CommonsStateStore(new MemoryEntityStore());
        var settings = await state.SaveSettingsAsync(new(null, "https://relay.example", "envoy.secret", "active",
            "Nova · Laurent", null, new Dictionary<string, bool>(), [], [], [], null, [], "owner-1", "agent-1"));
        await state.RecordAsync("envoy_failed", new Dictionary<string, object?>
        {
            ["thread"] = "announcements", ["reason"] = "thread_history_failed",
        });
        await state.RecordAsync("envoy_blocked", new Dictionary<string, object?>
        {
            ["thread"] = "thread-path", ["reason"] = "absolute-path",
        });

        var item = Assert.Single(await new PublicNovaThreads(relay, state).ListAsync(settings));
        Assert.Equal("thread-path", item.ThreadRef);
        Assert.Equal("Withheld: it contained a file path", item.LastSnippet);
    }

    [Fact]
    public async Task Relay_thread_list_follows_all_pages()
    {
        var handler = new PaginatedThreadRelayHandler();
        var relay = new RelayHttpClient(new HttpClient(handler), _ =>
            Task.FromResult<(Uri, string?)>((new Uri("https://relay.example"), "envoy.secret")));

        var items = await relay.GetThreadsAsync();

        Assert.Equal(["thread-1", "thread-2"], items.Select(item => item.ThreadRef));
        Assert.Equal(2, handler.Calls);
    }
}

internal sealed class PaginatedThreadRelayHandler : HttpMessageHandler
{
    public int Calls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        var second = request.RequestUri!.Query.Contains("cursor=next", StringComparison.Ordinal);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                items = new[]
                {
                    new
                    {
                        threadRef = second ? "thread-2" : "thread-1", channel = "engineering",
                        title = second ? "Second" : "First", lastActivityAt = 1792227600000L,
                        lastSnippet = "A real message",
                    },
                },
                nextCursor = second ? null : "next",
            }),
        });
    }
}

internal sealed class ThreadRelayHandler(bool threadsAvailable = true) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!threadsAvailable)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                { Content = JsonContent.Create(new { error = "not_found" }) });
        if (request.RequestUri!.AbsolutePath == "/v1/threads")
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    items = new[]
                    {
                        new
                        {
                            threadRef = "thread-1", channel = "engineering", title = "Exchange cap",
                            lastActivityAt = 1792227600000L, lastSnippet = "Safe fallback",
                            unreadPings = 1, scenario = "consult",
                        },
                    },
                }),
            });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                messages = new object[]
                {
                    new
                    {
                        id = "message-1", scenario = "consult", createdAt = 1792227600001L,
                        actor = new { kind = "envoy", envoyId = "envoy-1", personaName = "Nova · Laurent" },
                        content = "I can't help with that from the shared engineering material.",
                    },
                    new
                    {
                        id = "message-2", scenario = "consult", createdAt = 1792227600002L,
                        actor = new { kind = "human", discordUserId = "human-1", displayName = "Morgan" },
                        content = "Thanks.",
                    },
                },
            }),
        });
    }
}
