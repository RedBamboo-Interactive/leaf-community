using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Leaf.Plugins.LeafCommons.Tests;

public sealed class MultiAgentTests : IDisposable
{
    private const string NovaId = CommonsStateStore.LegacyNovaAgentId;
    private const string AgentTwoId = "c816c9c9-c2c9-4d59-a2d8-caba898dcfaa";
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "leaf-commons-multi-agent-" + Guid.NewGuid().ToString("N"));

    public MultiAgentTests() => Directory.CreateDirectory(root);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public async Task Legacy_single_config_migrates_to_Nova_entity_id_without_losing_credential()
    {
        var entities = new MemoryEntityStore();
        entities.Seed(Guid.NewGuid(), "leaf-commons-settings", "Nova Commons Settings", "settings",
            new JsonObject
            {
                ["relay_base_url"] = "https://relay.example",
                ["envoy_credential"] = "credential-to-preserve",
                ["connection_status"] = "active",
                ["persona"] = "Nova · Laurent",
                ["public_name"] = "Laurent",
                ["owner_user_id"] = "test",
                ["scenario_toggles"] = new JsonObject { ["consult"] = true },
                ["shareable_folders"] = new JsonArray("T:/Projects/leaf-commons"),
                ["personal_words"] = new JsonArray("private-word"),
                ["canaries"] = new JsonArray("canary"),
            });
        var store = new CommonsStateStore(entities);

        var configuration = await store.GetConfigurationAsync();

        var agent = Assert.Single(configuration.Agents);
        Assert.Equal(NovaId, agent.AgentEntityId);
        Assert.Equal("credential-to-preserve", agent.Credential);
        Assert.Equal("Nova", agent.AgentName);
        Assert.Equal("", configuration.Owner.PublicName);
        Assert.Null(configuration.Owner.RelayConfirmedPublicName);
        var ownerEntity = await entities.GetBySlugAsync("leaf-commons-settings", "settings");
        Assert.Equal(2, ownerEntity!.Data["schema_version"]!.GetValue<int>());
        Assert.False(ownerEntity.Data.ContainsKey("envoy_credential"));
    }

    [Fact]
    public async Task Two_public_Agents_keep_separate_credentials_and_shareable_copies()
    {
        var store = await StoreWithTwoAgentsAsync();
        Assert.Equal("credential-one", (await store.GetPublicAgentAsync(NovaId))!.Credential);
        Assert.Equal("credential-two", (await store.GetPublicAgentAsync(AgentTwoId))!.Credential);

        var copies = new AgentShareableStores(root);
        var first = copies.For(NovaId);
        var second = copies.For(AgentTwoId);
        Assert.NotEqual(first.RootPath, second.RootPath);
        Directory.CreateDirectory(first.RootPath);
        Directory.CreateDirectory(second.RootPath);
        await File.WriteAllTextAsync(Path.Combine(first.RootPath, "first.txt"), "first");
        await File.WriteAllTextAsync(Path.Combine(second.RootPath, "second.txt"), "second");
        Assert.Equal(["first.txt"], await first.ListAsync());
        Assert.Equal(["second.txt"], await second.ListAsync());
    }

    [Fact]
    public async Task Agent_rename_syncs_entity_name_to_that_Envoy()
    {
        var entities = new MemoryEntityStore();
        var store = new CommonsStateStore(entities);
        await SaveOwnerAsync(store);
        await store.SavePublicAgentAsync(Agent(NovaId, "Old name", "credential-one"));
        entities.Seed(Guid.Parse(NovaId), "agent", "Renamed Agent", "renamed-agent", []);
        var handler = new MultiAgentRelayHandler();
        await using var coordinator = Coordinator(store, entities, handler);

        await coordinator.SyncAgentNameAsync(NovaId);

        var saved = await store.GetPublicAgentAsync(NovaId);
        Assert.Equal("Renamed Agent", saved!.AgentName);
        Assert.Equal("Renamed Agent · Laurent", saved.DisplayPersona("Laurent"));
        var request = Assert.Single(handler.Requests);
        Assert.Equal("PATCH /v1/profile", request.Route);
        Assert.Equal("credential-one", request.Credential);
        Assert.Contains("\"agentName\":\"Renamed Agent\"", request.Body);
        Assert.DoesNotContain("publicName", request.Body);
    }

    [Fact]
    public async Task Stale_local_public_name_is_replaced_from_relay_and_is_never_written_back()
    {
        var entities = new MemoryEntityStore();
        var store = new CommonsStateStore(entities);
        await store.SaveOwnerSettingsAsync(new(null, "https://relay.example", "Laurent",
            [], [], [], "test"));
        await store.SavePublicAgentAsync(Agent(NovaId, "Nova", "credential-one"));
        var handler = new MultiAgentRelayHandler("Lynial");
        await using var coordinator = Coordinator(store, entities, handler);

        var profile = await coordinator.ReconcileRelayProfileAsync(NovaId);

        Assert.Equal("Lynial", profile.PublicName);
        var owner = await store.GetOwnerSettingsAsync();
        Assert.Equal("Lynial", owner.PublicName);
        Assert.Equal("Lynial", owner.RelayConfirmedPublicName);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("{}", request.Body);
        Assert.DoesNotContain("Laurent", request.Body);
        Assert.False(CommonsEndpoints.IsExplicitPublicNameChange(
            "Laurent", clientConfirmed: null, currentRelayConfirmed: "Lynial"));
    }

    [Fact]
    public async Task Derived_persona_round_trips_middle_dot_as_utf8_and_is_not_persisted()
    {
        var entities = new MemoryEntityStore();
        var store = new CommonsStateStore(entities);
        var owner = await store.SaveOwnerSettingsAsync(new(null, "https://relay.example",
            "Lynial", [], [], [], "test", "Lynial"));
        var agent = await store.SavePublicAgentAsync(Agent(NovaId, "Nova", "credential-one"));

        var persona = agent.DisplayPersona(owner.PublicName);
        var utf8 = JsonSerializer.SerializeToUtf8Bytes(new { persona });
        using var json = JsonDocument.Parse(utf8);

        Assert.Equal("Nova · Lynial", json.RootElement.GetProperty("persona").GetString());
        var stored = Assert.Single(await entities.QueryAsync(new() { TypeSlug = "leaf-commons-public-agent" }));
        Assert.False(stored.Data.ContainsKey("persona"));
    }

    [Fact]
    public async Task Agent_removal_suspends_its_Envoy_and_stops_public_state()
    {
        var entities = new MemoryEntityStore();
        var store = new CommonsStateStore(entities);
        await SaveOwnerAsync(store);
        await store.SavePublicAgentAsync(Agent(NovaId, "Nova", "credential-one"));
        var handler = new MultiAgentRelayHandler();
        await using var coordinator = Coordinator(store, entities, handler);

        await coordinator.HandleAgentRemovedAsync(NovaId);

        var saved = await store.GetPublicAgentAsync(NovaId);
        Assert.False(saved!.Enabled);
        Assert.Equal("suspended", saved.ConnectionStatus);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("PATCH /v1/profile", request.Route);
        Assert.Equal("credential-one", request.Credential);
        Assert.Contains("\"suspended\":true", request.Body);
    }

    [Fact]
    public async Task Brief_from_non_public_Agent_is_rejected()
    {
        var store = await StoreWithTwoAgentsAsync(secondEnabled: false);
        var handler = new MultiAgentRelayHandler();
        var runtime = Runtime(store, handler);

        var result = await runtime.PostBriefAsync(new("showcase", "safe engineering note"),
            new(AgentTwoId, "test"));

        Assert.Equal("blocked", result.Decision);
        Assert.Equal("agent_not_public", result.Reason);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Brief_identity_accepts_any_app_but_keeps_owner_and_private_discussion_guards()
    {
        var identity = $$"""{"app":{"id":"another-agent-app"},"actor":{"kind":"agent","entityId":"{{AgentTwoId}}"},"beneficiary":{"kind":"user","id":"test"},"context":[{"kind":"discussion","id":"discussion-1"}]}""";
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("token_use", "execution"), new Claim("execution_identity", identity)], "test"));

        var caller = OwnerGuard.ReadPrivateAgent(principal,
            new CommonsOwnerSettings(null, "https://relay.example", "Laurent", [], [], [], "test"));

        Assert.NotNull(caller);
        Assert.Equal(AgentTwoId, caller.AgentEntityId);
    }

    [Fact]
    public async Task Brief_is_routed_through_the_calling_Agents_credential()
    {
        var store = await StoreWithTwoAgentsAsync();
        var handler = new MultiAgentRelayHandler();
        var runtime = Runtime(store, handler);

        var result = await runtime.PostBriefAsync(new("showcase", "safe engineering note"),
            new(AgentTwoId, "test"));

        Assert.Equal("posted", result.Decision);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST /v1/posts", request.Route);
        Assert.Equal("credential-two", request.Credential);
    }

    private async Task<CommonsStateStore> StoreWithTwoAgentsAsync(bool secondEnabled = true)
    {
        var store = new CommonsStateStore(new MemoryEntityStore());
        await SaveOwnerAsync(store);
        await store.SavePublicAgentAsync(Agent(NovaId, "Nova", "credential-one"));
        await store.SavePublicAgentAsync(Agent(AgentTwoId, "Ada", "credential-two") with
            { Enabled = secondEnabled });
        return store;
    }

    private static Task<CommonsOwnerSettings> SaveOwnerAsync(CommonsStateStore store)
        => store.SaveOwnerSettingsAsync(new(null, "https://relay.example", "Laurent", [], [], [], "test"));

    private static PublicAgentSettings Agent(string id, string name, string credential) => new(
        null, id, name, true, credential, "active", null,
        new Dictionary<string, bool> { ["showcase"] = true }, []);

    private PublicAgentRuntime Runtime(CommonsStateStore store, MultiAgentRelayHandler handler)
        => new(new CountingModel("unused"), new AgentShareableStores(root),
            new RelayClientFactory(new FixedHttpClientFactory(handler), store),
            new OutboundFilter(new SecretScanner()), store, new NullDigestSink());

    private PublicAgentCoordinator Coordinator(CommonsStateStore store,
        MemoryEntityStore entities, MultiAgentRelayHandler handler)
    {
        var relays = new RelayClientFactory(new FixedHttpClientFactory(handler), store);
        return new(store, new AgentDirectory(entities), relays, Runtime(store, handler),
            new NullPluginEvents(), store, LoggerFactory.Create(_ => { }));
    }
}

internal sealed class FixedHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

internal sealed class MultiAgentRelayHandler(string relayPublicName = "Laurent") : HttpMessageHandler
{
    public List<(string Route, string? Credential, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(($"{request.Method} {request.RequestUri!.AbsolutePath}",
            request.Headers.Authorization?.Parameter, body));
        object payload;
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        if (request.RequestUri.AbsolutePath == "/v1/profile"
            && !document.RootElement.TryGetProperty("suspended", out _))
        {
            var agentName = document.RootElement.TryGetProperty("agentName", out var requestedAgentName)
                ? requestedAgentName.GetString() : "Nova";
            payload = new { envoy = new { publicName = relayPublicName,
                agentName, personaName = $"{agentName} · {relayPublicName}" } };
        }
        else if (request.RequestUri.AbsolutePath == "/v1/posts")
            payload = new { decision = "posted", messageId = "message-1", threadId = "thread-1" };
        else payload = new { ok = true };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
    }
}
