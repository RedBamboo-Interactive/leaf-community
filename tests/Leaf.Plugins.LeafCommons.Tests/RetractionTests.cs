using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Leaf.Plugins.LeafCommons.Tests;

/// <summary>
/// Contract v1 (2026-11-16), step 5 and the client half of step 4: a retracted announcement is
/// deleted for every public Agent, and the connector declares that it understands retractions.
/// </summary>
public sealed class RetractionTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string NovaId = CommonsStateStore.LegacyNovaAgentId;
    private const string AdaId = "c816c9c9-c2c9-4d59-a2d8-caba898dcfaa";
    private const string Retracted = "1571775128134750239";
    private const string Kept = "1571775128134750240";
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "leaf-commons-retraction-" + Guid.NewGuid().ToString("N"));

    public RetractionTests() => Directory.CreateDirectory(root);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public async Task A_retraction_deletes_that_announcement_for_every_public_Agent_and_nothing_else()
    {
        var world = await WorldAsync();
        await world.AnnounceAsync(NovaId, Retracted, "That's a lazy patch note");
        await world.AnnounceAsync(AdaId, Retracted, "That's a lazy patch note");
        await world.AnnounceAsync(NovaId, Kept, "Relay maintenance complete.");
        await world.AnnounceAsync(AdaId, Kept, "Relay maintenance complete.");
        // Other digest types never match, even with the same ID.
        await world.Store.StoreDigestAsync("consult_answer", "> quoted answer", "thread-1",
            agentEntityId: NovaId, sourceMessageId: Retracted);
        await world.Store.StoreDigestAsync("owner_notice", "> quoted notice", null,
            agentEntityId: AdaId, sourceChannel: "announcements", sourceMessageId: Retracted);
        var before = await world.Store.ListDigestsAsync();
        Assert.Equal(4, before.Count(item => item.Type == "announcement"));
        var storedBefore = (await world.Store.ListAuditsAsync()).Count(item => item.Event == "digest_stored");

        var acked = await world.RetractAsync(NovaId, "retraction-delivery-1", Retracted);

        Assert.Equal(["retraction-delivery-1"], acked);
        var after = await world.Store.ListDigestsAsync();
        Assert.DoesNotContain(after, item => item.Type == "announcement" && item.SourceMessageId == Retracted);
        Assert.Equal(before.Where(item => !(item.Type == "announcement" && item.SourceMessageId == Retracted))
            .Select(item => (item.Id, item.Content, item.Read)).OrderBy(item => item.Id),
            after.Select(item => (item.Id, item.Content, item.Read)).OrderBy(item => item.Id));
        Assert.Equal(2, after.Count(item => item.Type == "announcement" && item.SourceMessageId == Kept));
        Assert.Single(after, item => item.Type == "consult_answer" && item.SourceMessageId == Retracted);
        Assert.Single(after, item => item.Type == "owner_notice" && item.SourceMessageId == Retracted);
        var audit = Assert.Single(await world.Store.ListAuditsAsync(), item => item.Event == "digest_retracted");
        Assert.Equal(Retracted, audit.Metadata["messageId"]!.GetValue<string>());
        Assert.Equal(2, audit.Metadata["count"]!.GetValue<int>());
        Assert.Equal(NovaId, audit.Metadata["agent_entity_id"]!.GetValue<string>());
        // No Agent run, no relay call, no model call, and the retraction itself is never a digest.
        Assert.Equal(0, world.EnvoyRuns);
        Assert.Equal(0, world.Model.Calls);
        Assert.Empty(world.Relay.Requests);
        Assert.Equal(storedBefore, (await world.Store.ListAuditsAsync()).Count(item => item.Event == "digest_stored"));
    }

    [Fact]
    public async Task A_retraction_with_nothing_to_delete_is_an_acked_no_op()
    {
        var world = await WorldAsync();
        await world.AnnounceAsync(NovaId, Kept, "Relay maintenance complete.");
        var before = await world.Store.ListDigestsAsync();

        var acked = await world.RetractAsync(AdaId, "retraction-delivery-1", Retracted);

        Assert.Equal(["retraction-delivery-1"], acked);
        Assert.Equal(before, await world.Store.ListDigestsAsync());
        var audit = Assert.Single(await world.Store.ListAuditsAsync(), item => item.Event == "digest_retracted");
        Assert.Equal(0, audit.Metadata["count"]!.GetValue<int>());
        Assert.Equal(0, world.EnvoyRuns);
    }

    [Fact]
    public async Task A_duplicate_retraction_is_harmless()
    {
        var world = await WorldAsync();
        await world.AnnounceAsync(NovaId, Retracted, "That's a lazy patch note");
        await world.AnnounceAsync(AdaId, Retracted, "That's a lazy patch note");
        await world.AnnounceAsync(NovaId, Kept, "Relay maintenance complete.");

        var acked = new List<string>();
        acked.AddRange(await world.RetractAsync(NovaId, "retraction-delivery-1", Retracted));
        // The same delivery again on the same connection, the second Envoy's delivery, and a
        // redelivery after a restart (a fresh connector that has never seen the ID).
        acked.AddRange(await world.RetractAsync(NovaId, "retraction-delivery-1", Retracted));
        acked.AddRange(await world.RetractAsync(AdaId, "retraction-delivery-2", Retracted));
        acked.AddRange(await world.RetractAsync(NovaId, "retraction-delivery-1", Retracted, freshConnector: true));

        Assert.Equal(["retraction-delivery-1", "retraction-delivery-1", "retraction-delivery-2",
            "retraction-delivery-1"], acked);
        var left = Assert.Single(await world.Store.ListDigestsAsync());
        Assert.Equal(Kept, left.SourceMessageId);
        Assert.Equal([2, 0, 0], (await world.Store.ListAuditsAsync())
            .Where(item => item.Event == "digest_retracted")
            .Select(item => item.Metadata["count"]!.GetValue<int>())
            .OrderDescending());
    }

    [Fact]
    public async Task A_retraction_without_a_message_id_is_acked_and_deletes_nothing()
    {
        var world = await WorldAsync();
        await world.AnnounceAsync(NovaId, Retracted, "That's a lazy patch note");
        using var frame = JsonDocument.Parse("""
            {"type":"announcement_retraction","id":"retraction-delivery-1","channel":"announcements",
             "timestamp":1794816000000}
            """);
        var acked = new List<string>();

        await world.Connector(NovaId).ProcessAsync(frame.RootElement, id => { acked.Add(id); return Task.CompletedTask; });

        Assert.Equal(["retraction-delivery-1"], acked);
        Assert.Single(await world.Store.ListDigestsAsync());
        Assert.DoesNotContain(await world.Store.ListAuditsAsync(), item => item.Event == "digest_retracted");
    }

    [Fact]
    public async Task A_retracted_announcement_leaves_what_Nova_is_told()
    {
        var world = await WorldAsync();
        await world.AnnounceAsync(NovaId, Retracted, "That's a lazy patch note");
        await world.Store.StoreDigestAsync("owner_notice", "> External Leaf Community quote — owner notice\n> notice",
            "thread-1", agentEntityId: NovaId);
        var extensions = new CapturingPluginExtensions();
        using var contribution = new CommonsNovaContextExtension(extensions, world.Store);
        contribution.Start();
        var request = new Leaf.Sdk.Services.PluginContextRequest(Owner, NovaId, "discussion", "conversation");

        var before = await extensions.Provider!(request, default);
        Assert.Contains("lazy patch note", before!.Content);
        Assert.Equal(2, before.Data!["unreadCount"]!.GetValue<int>());

        await world.RetractAsync(NovaId, "retraction-delivery-1", Retracted);

        var after = await extensions.Provider!(request, default);
        Assert.NotNull(after);
        Assert.DoesNotContain("lazy patch note", after.Content);
        Assert.DoesNotContain("announcement", after.Content);
        Assert.Equal(1, after.Data!["unreadCount"]!.GetValue<int>());

        // With only the announcement, nothing is disclosed at all.
        await world.AnnounceAsync(AdaId, Kept, "Relay maintenance complete.");
        await world.AnnounceAsync(AdaId, Retracted, "That's a lazy patch note");
        await world.RetractAsync(AdaId, "retraction-delivery-2", Retracted);
        await world.RetractAsync(AdaId, "retraction-delivery-3", Kept);
        Assert.Null(await extensions.Provider!(new(Owner, AdaId, "discussion", "conversation"), default));
    }

    [Fact]
    public void The_connect_URL_declares_announcement_retraction()
    {
        Assert.Equal("wss://relay.example/v1/connect?features=announcement_retraction",
            RelayConnector.ConnectUri(new Uri("https://relay.example")).ToString());
        Assert.Equal("ws://127.0.0.1:8787/v1/connect?features=announcement_retraction",
            RelayConnector.ConnectUri(new Uri("http://127.0.0.1:8787/")).ToString());
        Assert.Equal("features=announcement_retraction", RelayFeatures.Query);
    }

    [Fact]
    public async Task The_connector_declares_announcement_retraction_when_it_connects()
    {
        await using var relay = await FakeSuspensionRelay.StartAsync();
        var envoy = relay.Add("credential-a");
        await using var connector = new RelayConnector(
            _ => Task.FromResult((new Uri(relay.BaseUrl), (string?)"credential-a")),
            new ConnectorCore((_, _) => Task.CompletedTask), (_, _) => Task.CompletedTask,
            LoggerFactory.Create(_ => { }).CreateLogger<RelayConnector>());

        await connector.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (relay.ConnectQueries.IsEmpty)
        {
            Assert.True(DateTime.UtcNow < deadline, "The connector never connected");
            await Task.Delay(20);
        }

        Assert.True(relay.ConnectQueries.TryPeek(out var query));
        Assert.Equal("?features=announcement_retraction", query);
        Assert.True(envoy.ConnectAttempts >= 1);
    }

    private async Task<World> WorldAsync()
    {
        var store = new CommonsStateStore(new MemoryEntityStore());
        await store.SaveOwnerSettingsAsync(new(null, "https://relay.example", "Laurent", [], [], [], Owner));
        foreach (var (id, name, credential) in new[] { (NovaId, "Nova", "credential-one"), (AdaId, "Ada", "credential-two") })
            await store.SavePublicAgentAsync(new(null, id, name, true, credential, "active", null,
                new ConversationSwitches(StartsConversations: true), []));
        var relay = new MultiAgentRelayHandler();
        var model = new CountingModel("{\"action\":\"post\",\"content\":\"must not run\"}");
        var runtime = new PublicAgentRuntime(model, new AgentShareableStores(root),
            new RelayClientFactory(new FixedHttpClientFactory(relay), store),
            new OutboundFilter(new SecretScanner()), store,
            new CommonsDigestSink(store, new DigestFormatter(), store));
        return new(store, runtime, relay, model);
    }

    private sealed class World(CommonsStateStore store, PublicAgentRuntime runtime,
        MultiAgentRelayHandler relay, CountingModel model)
    {
        private readonly Dictionary<string, ConnectorCore> connectors = [];
        private int deliveries;
        public CommonsStateStore Store => store;
        public MultiAgentRelayHandler Relay => relay;
        public CountingModel Model => model;
        public int EnvoyRuns { get; private set; }

        /// <summary>One connector per Agent, kept across frames like a live connection.</summary>
        public ConnectorCore Connector(string agentId, bool fresh = false)
        {
            if (fresh || !connectors.TryGetValue(agentId, out var core))
                connectors[agentId] = core = new ConnectorCore(
                    async (request, ct) => { EnvoyRuns++; await runtime.HandleAsync(agentId, request, ct); },
                    (listening, ct) => runtime.HandleListeningEventAsync(agentId, listening, ct));
            return core;
        }

        public async Task AnnounceAsync(string agentId, string messageId, string content)
        {
            using var frame = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                type = "announcement", id = $"announcement-delivery-{++deliveries}", scenario = "announcements",
                messageId, operation = "create", timestamp = 1794816000000, authorDisplayName = "Laurent",
                channel = "announcements", threadRef = (string?)null, content,
            }));
            await Connector(agentId).ProcessAsync(frame.RootElement, _ => Task.CompletedTask);
        }

        public async Task<List<string>> RetractAsync(string agentId, string deliveryId, string messageId,
            bool freshConnector = false)
        {
            using var frame = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                type = "announcement_retraction", id = deliveryId, messageId,
                sourceEventId = $"announcement:{messageId}:retract", channel = "announcements",
                timestamp = 1794816060000,
            }));
            var acked = new List<string>();
            await Connector(agentId, freshConnector).ProcessAsync(frame.RootElement,
                id => { acked.Add(id); return Task.CompletedTask; });
            return acked;
        }
    }
}
