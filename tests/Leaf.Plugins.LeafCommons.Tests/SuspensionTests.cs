using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Leaf.Plugins.LeafCommons.Tests;

/// <summary>
/// A loopback relay with the suspension contract of leaf-commons-relay ddee48d: every Envoy route
/// refuses a suspended Envoy with 403 <c>envoy_suspended</c>, except exactly
/// <c>{"suspended":false}</c> on <c>PATCH /v1/profile</c> when the owner suspended it; the
/// WebSocket refuses any non-active Envoy with 403 <c>envoy_&lt;status&gt;</c>.
/// </summary>
internal sealed class FakeSuspensionRelay : IAsyncDisposable
{
    internal sealed class Envoy
    {
        public string Status { get; set; } = "active";
        public string? SuspendedBy { get; set; }
        public string? ResumeStatus { get; set; }
        public int ConnectAttempts;
        public int ResumeRequests;
    }

    private readonly WebApplication app;
    public ConcurrentDictionary<string, Envoy> Envoys { get; } = new();
    public ConcurrentQueue<(string Credential, string Body)> ProfileRequests { get; } = new();
    public string BaseUrl { get; }

    private FakeSuspensionRelay(WebApplication app, string baseUrl) { this.app = app; BaseUrl = baseUrl; }

    public static async Task<FakeSuspensionRelay> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        FakeSuspensionRelay? relay = null;
        app.UseWebSockets();
        app.MapMethods("/v1/profile", ["PATCH"], async (HttpContext context) =>
        {
            var credential = Credential(context);
            if (credential is null || !relay!.Envoys.TryGetValue(credential, out var envoy))
                return Results.Json(new { error = "unauthorized" }, statusCode: 401);
            var raw = await new StreamReader(context.Request.Body).ReadToEndAsync();
            relay.ProfileRequests.Enqueue((credential, raw));
            var body = JsonNode.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw)!.AsObject();
            var lift = body.Count == 1 && body["suspended"]?.GetValueKind() == JsonValueKind.False;
            if (lift) Interlocked.Increment(ref envoy.ResumeRequests);
            if (envoy.Status == "suspended" && !(lift && envoy.SuspendedBy == "owner"))
                return Results.Json(new { error = "envoy_suspended" }, statusCode: 403);
            if (body["suspended"]?.GetValueKind() == JsonValueKind.True)
            {
                envoy.ResumeStatus = envoy.Status;
                envoy.Status = "suspended";
                envoy.SuspendedBy = "owner";
            }
            else if (lift && envoy.Status == "suspended")
            {
                envoy.Status = envoy.ResumeStatus ?? "active";
                envoy.SuspendedBy = null;
            }
            var agentName = body["agentName"]?.GetValue<string>() ?? "Agent";
            return Results.Json(new { envoy = new
            {
                status = envoy.Status, suspendedBy = envoy.SuspendedBy, publicName = "Pseudonym",
                agentName, personaName = $"{agentName} · Pseudonym",
            } });
        });
        app.MapGet("/v1/connect", async (HttpContext context) =>
        {
            var credential = Credential(context);
            if (credential is null || !relay!.Envoys.TryGetValue(credential, out var envoy))
            {
                context.Response.StatusCode = 401;
                return;
            }
            Interlocked.Increment(ref envoy.ConnectAttempts);
            if (envoy.Status != "active" || !context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { error = $"envoy_{envoy.Status}" });
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"hello\"}"), WebSocketMessageType.Text, true, context.RequestAborted);
            var buffer = new byte[1024];
            try
            {
                while (socket.State == WebSocketState.Open)
                    if ((await socket.ReceiveAsync(buffer, context.RequestAborted)).MessageType == WebSocketMessageType.Close) break;
            }
            catch (Exception) { }
        });
        await app.StartAsync();
        relay = new FakeSuspensionRelay(app, app.Urls.First());
        return relay;
    }

    public Envoy Add(string credential, string status = "active", string? suspendedBy = null)
        => Envoys[credential] = new Envoy { Status = status, SuspendedBy = suspendedBy };

    private static string? Credential(HttpContext context)
        => context.Request.Headers.Authorization.ToString() is { Length: > 7 } header
           && header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : null;

    public ValueTask DisposeAsync() => app.DisposeAsync();
}

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Logger(this);
    public void Dispose() { }

    private sealed class Logger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => owner.Entries.Enqueue((logLevel, formatter(state, exception), exception));
    }
}

public sealed class RelayResumeClientTests
{
    [Fact]
    public async Task Resume_sends_exactly_suspended_false_and_reports_the_relay_status()
    {
        await using var relay = await FakeSuspensionRelay.StartAsync();
        relay.Add("credential-a", "suspended", "owner").ResumeStatus = "pending";
        var client = new RelayHttpClient(new HttpClient(), _ => Task.FromResult((new Uri(relay.BaseUrl), (string?)"credential-a")));

        Assert.Equal("pending", await client.ResumeAsync());
        Assert.Equal("""{"suspended":false}""", Assert.Single(relay.ProfileRequests).Body);
        Assert.Equal("pending", await client.ReadStatusAsync()); // an empty patch reads the status
    }

    [Fact]
    public async Task Resume_refused_by_a_host_suspension_carries_the_relay_reason()
    {
        await using var relay = await FakeSuspensionRelay.StartAsync();
        relay.Add("credential-a", "suspended", "host");
        var client = new RelayHttpClient(new HttpClient(), _ => Task.FromResult((new Uri(relay.BaseUrl), (string?)"credential-a")));

        var refused = await Assert.ThrowsAsync<RelayApiException>(() => client.ResumeAsync());
        Assert.Equal("envoy_suspended", refused.Reason);
        Assert.Equal(403, refused.StatusCode);
        Assert.Equal("envoy_suspended", (await Assert.ThrowsAsync<RelayApiException>(() => client.ReadStatusAsync())).Reason);
    }

    [Fact]
    public async Task Resume_without_an_error_body_falls_back_to_envoy_resume_failed()
    {
        var handler = new StatusHandler(HttpStatusCode.BadGateway, "");
        var client = new RelayHttpClient(new HttpClient(handler), _ => Task.FromResult((new Uri("http://127.0.0.1:9/"), (string?)"credential-a")));
        var failed = await Assert.ThrowsAsync<RelayApiException>(() => client.ResumeAsync());
        Assert.Equal("envoy_resume_failed", failed.Reason);
        Assert.Equal(502, failed.StatusCode);
    }

    [Fact]
    public async Task An_owner_notice_reports_who_suspended_the_Envoy()
    {
        RelayListeningEvent? seen = null;
        var core = new ConnectorCore((_, _) => Task.CompletedTask, (listening, _) => { seen = listening; return Task.CompletedTask; });
        using var frame = JsonDocument.Parse("""{"type":"owner_notice","id":"n1","reason":"membership_suspended","suspendedBy":"membership"}""");
        await core.ProcessAsync(frame.RootElement, _ => Task.CompletedTask);
        Assert.Equal(SuspensionSources.Membership, seen!.SuspendedBy);

        using var legacy = JsonDocument.Parse("""{"type":"owner_notice","id":"n2","reason":"auto_suspended_filter_violations","count":5}""");
        await core.ProcessAsync(legacy.RootElement, _ => Task.CompletedTask);
        Assert.Equal(SuspensionSources.Filter, seen!.SuspendedBy);
    }

    private sealed class StatusHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}

public sealed class RelayConnectorRefusalTests
{
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not reached in time");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Repeated_refusals_log_one_concise_warning_and_no_error_stack()
    {
        await using var relay = await FakeSuspensionRelay.StartAsync();
        var envoy = relay.Add("credential-a", "pending");
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var refusals = new ConcurrentQueue<int>();
        await using var connector = new RelayConnector(
            _ => Task.FromResult((new Uri(relay.BaseUrl), (string?)"credential-a")),
            new ConnectorCore((_, _) => Task.CompletedTask), (_, _) => Task.CompletedTask,
            factory.CreateLogger<RelayConnector>(),
            onRefused: (status, _) =>
            {
                refusals.Enqueue(status);
                return Task.FromResult<TimeSpan?>(refusals.Count >= 3 ? null : TimeSpan.Zero);
            });

        await connector.StartAsync(CancellationToken.None);
        await connector.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal([403, 403, 403], refusals.ToArray());
        Assert.Equal(3, envoy.ConnectAttempts);
        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Error || entry.Error is not null);
        Assert.Single(logs.Entries, entry => entry.Message.Contains("refused the connection", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failing_reconnecting_status_save_does_not_end_the_retry_loop()
    {
        await using var relay = await FakeSuspensionRelay.StartAsync();
        var envoy = relay.Add("credential-a", "suspended", "host");
        var saves = 0;
        await using var connector = new RelayConnector(
            _ => Task.FromResult((new Uri(relay.BaseUrl), (string?)"credential-a")),
            new ConnectorCore((_, _) => Task.CompletedTask),
            (_, _) => { Interlocked.Increment(ref saves); throw new InvalidOperationException("database unavailable"); },
            LoggerFactory.Create(_ => { }).CreateLogger<RelayConnector>());

        await connector.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref envoy.ConnectAttempts) >= 2);

        Assert.False(connector.Completion.IsCompleted);
        Assert.True(Volatile.Read(ref saves) >= 1);
    }
}

/// <summary>Switching Agents on and off against a relay that records who suspended each Envoy.</summary>
public sealed class SuspensionLifecycleTests : IAsyncLifetime
{
    private const string Owner = AgentSkillAssignmentTests.Owner;
    private const string AgentA = "a1000000-0000-0000-0000-000000000001";
    private const string AgentB = "b2000000-0000-0000-0000-000000000002";
    private readonly string root = Path.Combine(Path.GetTempPath(), "leaf-commons-suspension-" + Guid.NewGuid().ToString("N"));
    private AgentSkillAssignmentTests.World world = null!;
    private FakeSuspensionRelay relay = null!;
    private CommonsStateStore store = null!;
    private PublicAgentCoordinator coordinator = null!;
    private WebApplication? app;
    private HttpClient http = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        relay = await FakeSuspensionRelay.StartAsync();
        world = AgentSkillAssignmentTests.Create();
        world.Entities.Seed(Guid.Parse(AgentB), "agent", "Agent B", "agent-b", new JsonObject { ["owner_id"] = Owner });
        store = new CommonsStateStore(world.Entities);
        await store.SaveOwnerSettingsAsync(new(null, relay.BaseUrl, "Pseudonym", [], [], [], Owner, "Pseudonym"));
        var relays = new RelayClientFactory(new FixedHttpClientFactory(new SocketsHttpHandler()), store);
        var runtime = new PublicAgentRuntime(new CountingModel("unused"), new AgentShareableStores(root), relays,
            new OutboundFilter(new SecretScanner()), store, new NullDigestSink(),
            outboundReview: TestSentinel.Passing(world.Entities));
        coordinator = new PublicAgentCoordinator(store, new AgentDirectory(world.Entities), relays, runtime,
            new NullPluginEvents(), store, LoggerFactory.Create(_ => { }), world.Skills)
        { PendingRetryDelay = TimeSpan.FromHours(1) };
        var images = new ThrowingAvatarImageSource("unused");

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IAuthContext>(new TestAuth(Owner));
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(new AgentDirectory(world.Entities));
        builder.Services.AddSingleton(relays);
        builder.Services.AddSingleton<IAvatarImageSource>(images);
        builder.Services.AddSingleton(new PublicAgentAvatarSync(store, images, new AvatarImageProcessor(), relays,
            new NullPluginEvents(), store));
        builder.Services.AddSingleton(coordinator);
        builder.Services.AddSingleton(runtime);
        builder.Services.AddSingleton<PublicAgentThreads>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<PublicAgentConversationCoordinator>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<ShareableSetBuilder>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<AgentShareableStores>(_ => throw new NotSupportedException());
        app = builder.Build();
        CommonsEndpoints.Map(app.MapGroup("/api/apps/leaf-commons"));
        await app.StartAsync();
        http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        http?.Dispose();
        if (app is not null) await app.DisposeAsync();
        await coordinator.DisposeAsync();
        await relay.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private Task SaveAgentAsync(string id, bool enabled, string status, string? suspendedBy = null)
        => store.SavePublicAgentAsync(new(null, id, id == AgentA ? "Agent A" : "Agent B", enabled,
            id == AgentA ? "credential-a" : "credential-b", status, null,
            new Dictionary<string, bool>(), [], SuspendedBy: suspendedBy));

    private object Settings(params (string Id, bool Enabled)[] agents) => new
    {
        relayBaseUrl = relay.BaseUrl, publicName = "Pseudonym", relayConfirmedPublicName = "Pseudonym",
        personalWords = Array.Empty<string>(), canaries = Array.Empty<string>(), linkAllowlist = Array.Empty<string>(),
        agents = agents.Select(agent => new
        {
            agentId = agent.Id, enabled = agent.Enabled, avatarSource = AvatarSources.AgentAvatar,
            scenarioToggles = new Dictionary<string, bool>(), shareableFolders = Array.Empty<string>(),
        }).ToArray(),
    };

    private async Task<JsonObject> PutSettingsAsync(params (string Id, bool Enabled)[] agents)
    {
        using var response = await http.PutAsJsonAsync("/api/apps/leaf-commons/settings", Settings(agents));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private static JsonObject Row(JsonObject settings, string agentId)
        => settings["agents"]!.AsArray().Select(item => item!.AsObject())
            .Single(item => item["agentId"]!.GetValue<string>() == agentId);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not reached in time");
            await Task.Delay(20);
        }
    }

    private Task WaitForStatusAsync(string agentId, string status)
        => WaitUntilAsync(async () => (await store.GetPublicAgentAsync(agentId))?.ConnectionStatus == status);

    [Fact]
    public async Task Switching_off_and_back_on_lifts_the_owner_suspension_before_reconnecting()
    {
        var envoy = relay.Add("credential-a");
        await SaveAgentAsync(AgentA, enabled: false, "disconnected");
        await PutSettingsAsync((AgentA, true));
        await WaitForStatusAsync(AgentA, "active");

        var off = await PutSettingsAsync((AgentA, false));
        Assert.Equal(("suspended", "owner"), (envoy.Status, envoy.SuspendedBy));
        Assert.Equal("owner", Row(off, AgentA)["suspendedBy"]!.GetValue<string>());
        Assert.False(coordinator.HasConnector(AgentA));

        while (relay.ProfileRequests.TryDequeue(out _)) { }
        var on = await PutSettingsAsync((AgentA, true));

        var result = Assert.Single(on["relayResume"]!.AsArray())!;
        Assert.Equal(EnvoyResumeStatuses.Resumed, result["status"]!.GetValue<string>());
        // The resume is the first profile request of the save: before the profile patch and connect.
        Assert.Equal("""{"suspended":false}""", relay.ProfileRequests.First().Body);
        Assert.Equal("active", envoy.Status);
        Assert.True(Row(on, AgentA)["enabled"]!.GetValue<bool>());
        Assert.Null(Row(on, AgentA)["suspendedBy"]);
        Assert.Contains(world.Skill.Id.ToString(), await AgentSkillAssignmentTests.SkillsOf(world.Entities, AgentA));
        await WaitForStatusAsync(AgentA, "active");
    }

    [Fact]
    public async Task A_host_suspension_keeps_that_Agent_off_and_the_rest_of_the_save_applies()
    {
        var envoy = relay.Add("credential-a", "suspended", "host");
        relay.Add("credential-b");
        await SaveAgentAsync(AgentA, enabled: false, "suspended", SuspensionSources.Owner);
        await SaveAgentAsync(AgentB, enabled: false, "disconnected");

        var saved = await PutSettingsAsync((AgentA, true), (AgentB, true));

        var result = Assert.Single(saved["relayResume"]!.AsArray())!;
        Assert.Equal((AgentA, EnvoyResumeStatuses.Suspended, "host", "envoy_suspended"),
            (result["agentId"]!.GetValue<string>(), result["status"]!.GetValue<string>(),
                result["suspendedBy"]!.GetValue<string>(), result["reason"]!.GetValue<string>()));
        var rowA = Row(saved, AgentA);
        Assert.False(rowA["enabled"]!.GetValue<bool>());
        Assert.Equal("suspended", rowA["connectionStatus"]!.GetValue<string>());
        Assert.Equal("host", rowA["suspendedBy"]!.GetValue<string>());
        Assert.False(coordinator.HasConnector(AgentA));
        Assert.Equal(0, envoy.ConnectAttempts);
        Assert.Equal(1, envoy.ResumeRequests);
        Assert.DoesNotContain(world.Skill.Id.ToString(), await AgentSkillAssignmentTests.SkillsOf(world.Entities, AgentA));

        Assert.True(Row(saved, AgentB)["enabled"]!.GetValue<bool>());
        Assert.Contains(world.Skill.Id.ToString(), await AgentSkillAssignmentTests.SkillsOf(world.Entities, AgentB));
        await WaitForStatusAsync(AgentB, "active");
    }

    [Fact]
    public async Task A_membership_suspension_reported_by_a_notice_is_named_when_resuming_is_refused()
    {
        relay.Add("credential-a", "suspended", "membership");
        await SaveAgentAsync(AgentA, enabled: true, "active");
        var runtime = new PublicAgentRuntime(new CountingModel("unused"), new AgentShareableStores(root),
            new RelayClientFactory(new FixedHttpClientFactory(new SocketsHttpHandler()), store),
            new OutboundFilter(new SecretScanner()), store, new NullDigestSink());
        await runtime.HandleListeningEventAsync(AgentA, new("n1", "owner_notice", "announcements",
            "membership_suspended", null, null, SuspendedBy: SuspensionSources.Membership));
        Assert.Equal(SuspensionSources.Membership, (await store.GetPublicAgentAsync(AgentA))!.SuspendedBy);

        var saved = await PutSettingsAsync((AgentA, true)); // the Agent is on but its Envoy is refused

        Assert.Equal("membership", Row(saved, AgentA)["suspendedBy"]!.GetValue<string>());
        Assert.False(Row(saved, AgentA)["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task An_Agent_left_on_while_suspended_is_resumed_once_at_startup()
    {
        // The state from before resuming existed: switched off, then on, looping on refusals.
        var envoy = relay.Add("credential-a", "suspended", "owner");
        await SaveAgentAsync(AgentA, enabled: true, "reconnecting");

        await coordinator.StartAsync(CancellationToken.None);

        Assert.Equal(1, envoy.ResumeRequests);
        await WaitForStatusAsync(AgentA, "active");
        Assert.True((await store.GetPublicAgentAsync(AgentA))!.Enabled);
    }

    [Fact]
    public async Task An_Agent_left_on_while_host_suspended_is_switched_off_at_startup_without_connecting()
    {
        var envoy = relay.Add("credential-a", "suspended", "host");
        await SaveAgentAsync(AgentA, enabled: true, "reconnecting");
        await world.Skills.AddAsync(AgentA, Owner);

        await coordinator.StartAsync(CancellationToken.None);

        var saved = (await store.GetPublicAgentAsync(AgentA))!;
        Assert.Equal((false, "suspended", "host"), (saved.Enabled, saved.ConnectionStatus, saved.SuspendedBy));
        Assert.Equal(1, envoy.ResumeRequests);
        Assert.Equal(0, envoy.ConnectAttempts);
        Assert.False(coordinator.HasConnector(AgentA));
        Assert.DoesNotContain(world.Skill.Id.ToString(), await AgentSkillAssignmentTests.SkillsOf(world.Entities, AgentA));
    }

    [Fact]
    public async Task A_settings_save_that_keeps_a_stuck_Agent_on_attempts_one_resume()
    {
        var envoy = relay.Add("credential-a", "suspended", "host");
        await SaveAgentAsync(AgentA, enabled: true, "reconnecting");

        // Before: the profile read through this Agent failed the whole save with 403.
        var saved = await PutSettingsAsync((AgentA, true));

        Assert.Equal(EnvoyResumeStatuses.Suspended, saved["relayResume"]![0]!["status"]!.GetValue<string>());
        Assert.False(Row(saved, AgentA)["enabled"]!.GetValue<bool>());
        Assert.Equal(1, envoy.ResumeRequests);
        Assert.Equal(0, envoy.ConnectAttempts);
    }

    [Fact]
    public async Task A_refused_connection_asks_once_then_stops_instead_of_looping()
    {
        // Locally active, but the host suspended the Envoy while Leaf was offline.
        var envoy = relay.Add("credential-a", "suspended", "host");
        await SaveAgentAsync(AgentA, enabled: true, "active");

        await coordinator.RefreshAsync(AgentA);
        await WaitUntilAsync(async () => !(await store.GetPublicAgentAsync(AgentA))!.Enabled);
        await Task.Delay(300); // nothing keeps retrying

        Assert.Equal(1, envoy.ConnectAttempts);
        Assert.Equal(1, envoy.ResumeRequests);
        Assert.False(coordinator.HasConnector(AgentA));
        Assert.Equal("host", (await store.GetPublicAgentAsync(AgentA))!.SuspendedBy);
    }

    [Fact]
    public async Task A_refused_connection_to_an_owner_suspended_Envoy_resumes_and_reconnects()
    {
        var envoy = relay.Add("credential-a", "suspended", "owner");
        await SaveAgentAsync(AgentA, enabled: true, "active");

        await coordinator.RefreshAsync(AgentA);
        await WaitUntilAsync(() => Task.FromResult(envoy.Status == "active" && envoy.ConnectAttempts >= 2));
        await WaitForStatusAsync(AgentA, "active");

        Assert.Equal(1, envoy.ResumeRequests);
        Assert.True(coordinator.HasConnector(AgentA));
    }

    [Fact]
    public async Task A_pending_Envoy_waits_for_the_server_host_without_retrying_fast()
    {
        var envoy = relay.Add("credential-a", "pending");
        await SaveAgentAsync(AgentA, enabled: true, "active");

        await coordinator.RefreshAsync(AgentA);
        await WaitForStatusAsync(AgentA, "pending approval");
        await Task.Delay(300);

        Assert.Equal(1, envoy.ConnectAttempts);
        Assert.Equal(0, envoy.ResumeRequests);
        Assert.True((await store.GetPublicAgentAsync(AgentA))!.Enabled);
        Assert.True(coordinator.HasConnector(AgentA));
    }
}
