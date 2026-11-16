using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Leaf.Plugins.LeafCommons.Tests;

/// <summary>
/// A frame counts as processed only once its handling succeeded. A failed frame stays unacked, so
/// the relay redelivers it and it is handled again; a successful one is never handled twice.
/// </summary>
public sealed class ConnectorRedeliveryTests : IDisposable
{
    private const string NovaId = CommonsStateStore.LegacyNovaAgentId;
    private const string MessageId = "1571775128134750239";
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "leaf-commons-redelivery-" + Guid.NewGuid().ToString("N"));

    public ConnectorRedeliveryTests() => Directory.CreateDirectory(root);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private static readonly string Announcement = JsonSerializer.Serialize(new
    {
        type = "announcement", id = "announcement-delivery-1", scenario = "announcements", messageId = MessageId,
        operation = "create", timestamp = 1794816000000, authorDisplayName = "Laurent", channel = "announcements",
        threadRef = (string?)null, content = "Relay maintenance complete.",
    });

    private static readonly string Retraction = JsonSerializer.Serialize(new
    {
        type = "announcement_retraction", id = "retraction-delivery-1", messageId = MessageId,
        sourceEventId = $"announcement:{MessageId}:retract", channel = "announcements", timestamp = 1794816060000,
    });

    [Fact]
    public async Task A_retraction_whose_handling_failed_is_handled_again_when_redelivered()
    {
        var world = await WorldAsync();
        await world.Core.ProcessAsync(Parse(Announcement), _ => Task.CompletedTask);
        Assert.Single(await world.Store.ListDigestsAsync());
        var acked = new List<string>();

        world.FailNext = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Core.ProcessAsync(Parse(Retraction),
            id => { acked.Add(id); return Task.CompletedTask; }));

        Assert.Empty(acked);
        Assert.Single(await world.Store.ListDigestsAsync());

        Assert.True(await world.Core.ProcessAsync(Parse(Retraction), id => { acked.Add(id); return Task.CompletedTask; }));

        Assert.Equal(["retraction-delivery-1"], acked);
        Assert.Empty(await world.Store.ListDigestsAsync());
        var audit = Assert.Single(await world.Store.ListAuditsAsync(), item => item.Event == "digest_retracted");
        Assert.Equal(1, audit.Metadata["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task An_announcement_whose_handling_failed_is_handled_again_when_redelivered()
    {
        var world = await WorldAsync();
        var acked = new List<string>();

        world.FailNext = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Core.ProcessAsync(Parse(Announcement),
            id => { acked.Add(id); return Task.CompletedTask; }));

        Assert.Empty(acked);
        Assert.Empty(await world.Store.ListDigestsAsync());

        Assert.True(await world.Core.ProcessAsync(Parse(Announcement), id => { acked.Add(id); return Task.CompletedTask; }));

        Assert.Equal(["announcement-delivery-1"], acked);
        var digest = Assert.Single(await world.Store.ListDigestsAsync());
        Assert.Equal(MessageId, digest.SourceMessageId);
        Assert.Equal(2, world.ListeningCalls);
    }

    [Fact]
    public async Task A_frame_handled_successfully_is_acked_without_being_handled_again()
    {
        var world = await WorldAsync();
        var acked = new List<string>();
        Task Ack(string id) { acked.Add(id); return Task.CompletedTask; }

        Assert.True(await world.Core.ProcessAsync(Parse(Announcement), Ack));
        var digest = Assert.Single(await world.Store.ListDigestsAsync());
        await world.Store.MarkDigestReadAsync(digest.Id);
        Assert.False(await world.Core.ProcessAsync(Parse(Announcement), Ack));
        Assert.True(await world.Core.ProcessAsync(Parse(Retraction), Ack));
        Assert.False(await world.Core.ProcessAsync(Parse(Retraction), Ack));

        Assert.Equal(["announcement-delivery-1", "announcement-delivery-1",
            "retraction-delivery-1", "retraction-delivery-1"], acked);
        Assert.Equal(2, world.ListeningCalls);
        Assert.Single(await world.Store.ListAuditsAsync(), item => item.Event == "digest_retracted");

        var runs = 0;
        var core = new ConnectorCore((_, _) => { runs++; return Task.CompletedTask; });
        var ping = Parse("""{"type":"ping","id":"ping-1","threadRef":"thread-1","content":"hello"}""");
        Assert.True(await core.ProcessAsync(ping, _ => Task.CompletedTask));
        Assert.False(await core.ProcessAsync(ping, _ => Task.CompletedTask));
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task A_frame_without_a_usable_type_is_acked_once_and_never_retried()
    {
        var runs = 0;
        var core = new ConnectorCore((_, _) => { runs++; return Task.CompletedTask; },
            (_, _) => { runs++; return Task.CompletedTask; });
        var acked = new List<string>();

        Assert.True(await core.ProcessAsync(Parse("""{"id":"untyped-1"}"""), id => { acked.Add(id); return Task.CompletedTask; }));
        Assert.True(await core.ProcessAsync(Parse("""{"id":"numeric-1","type":7}"""), id => { acked.Add(id); return Task.CompletedTask; }));

        Assert.Equal(["untyped-1", "numeric-1"], acked);
        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task The_connector_reconnects_after_a_failed_frame_and_the_relay_redelivery_is_handled()
    {
        var world = await WorldAsync();
        await world.Core.ProcessAsync(Parse(Announcement), _ => Task.CompletedTask);
        await using var relay = await ReplayingRelay.StartAsync(Retraction);
        world.FailNext = true;
        await using var connector = new RelayConnector(
            _ => Task.FromResult((new Uri(relay.BaseUrl), (string?)"credential")),
            world.Core, (_, _) => Task.CompletedTask,
            LoggerFactory.Create(_ => { }).CreateLogger<RelayConnector>());

        await connector.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!relay.Inbox.IsEmpty)
        {
            Assert.True(DateTime.UtcNow < deadline, "The retraction was never acked");
            await Task.Delay(20);
        }

        Assert.True(relay.Connections >= 2);
        Assert.Equal(["retraction-delivery-1"], relay.Acks);
        Assert.Empty(await world.Store.ListDigestsAsync());
        Assert.Single(await world.Store.ListAuditsAsync(), item => item.Event == "digest_retracted");
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private async Task<World> WorldAsync()
    {
        var store = new CommonsStateStore(new MemoryEntityStore());
        await store.SaveOwnerSettingsAsync(new(null, "https://relay.example", "Laurent", [], [], [], "owner-1"));
        await store.SavePublicAgentAsync(new(null, NovaId, "Nova", true, "credential", "active", null,
            new ConversationSwitches(StartsConversations: true), []));
        var runtime = new PublicAgentRuntime(new CountingModel("unused"), new AgentShareableStores(root),
            new RelayClientFactory(new FixedHttpClientFactory(new MultiAgentRelayHandler()), store),
            new OutboundFilter(new SecretScanner()), store,
            new CommonsDigestSink(store, new DigestFormatter(), store));
        return new(store, runtime);
    }

    private sealed class World
    {
        public World(CommonsStateStore store, PublicAgentRuntime runtime)
        {
            Store = store;
            Core = new ConnectorCore((request, ct) => runtime.HandleAsync(NovaId, request, ct),
                async (listening, ct) =>
                {
                    ListeningCalls++;
                    if (FailNext)
                    {
                        FailNext = false;
                        throw new InvalidOperationException("database unavailable");
                    }
                    await runtime.HandleListeningEventAsync(NovaId, listening, ct);
                });
        }

        public CommonsStateStore Store { get; }
        public ConnectorCore Core { get; }
        public bool FailNext { get; set; }
        public int ListeningCalls { get; private set; }
    }

    /// <summary>
    /// The relay's inbox contract: every unacked item is sent again on each new connection, and an
    /// ack removes it.
    /// </summary>
    private sealed class ReplayingRelay : IAsyncDisposable
    {
        private readonly WebApplication app;
        public ConcurrentDictionary<string, string> Inbox { get; } = new();
        public ConcurrentQueue<string> Acks { get; } = new();
        public int Connections;
        public string BaseUrl { get; private set; } = "";

        private ReplayingRelay(WebApplication app) => this.app = app;

        public static async Task<ReplayingRelay> StartAsync(params string[] items)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var relay = new ReplayingRelay(builder.Build());
            foreach (var item in items)
                relay.Inbox[JsonDocument.Parse(item).RootElement.GetProperty("id").GetString()!] = item;
            relay.app.UseWebSockets();
            relay.app.MapGet("/v1/connect", async (HttpContext context) =>
            {
                if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 426; return; }
                Interlocked.Increment(ref relay.Connections);
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"hello\"}"), WebSocketMessageType.Text, true, context.RequestAborted);
                foreach (var item in relay.Inbox.Values)
                    await socket.SendAsync(Encoding.UTF8.GetBytes(item), WebSocketMessageType.Text, true, context.RequestAborted);
                var buffer = new byte[4096];
                try
                {
                    while (socket.State == WebSocketState.Open)
                    {
                        var received = await socket.ReceiveAsync(buffer, context.RequestAborted);
                        if (received.MessageType == WebSocketMessageType.Close) break;
                        using var message = JsonDocument.Parse(buffer.AsMemory(0, received.Count));
                        if (message.RootElement.GetProperty("type").GetString() == "ack"
                            && message.RootElement.GetProperty("id").GetString() is { } id
                            && relay.Inbox.TryRemove(id, out _))
                            relay.Acks.Enqueue(id);
                    }
                }
                catch (Exception) { }
            });
            await relay.app.StartAsync();
            relay.BaseUrl = relay.app.Urls.First();
            return relay;
        }

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }
}
