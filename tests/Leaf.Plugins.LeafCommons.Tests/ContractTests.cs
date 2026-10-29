using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Leaf.Plugins.LeafCommons;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.LeafCommons.Tests;

public sealed class ContractTests
{
    [Fact]
    public void Manifest_contributes_digest_context_without_system_token_permission()
    {
        var manifest = new LeafCommonsPlugin().Manifest;
        Assert.Equal("Leaf Community", manifest.Name);
        Assert.Empty(manifest.Frontend!.Extensions);
        var extension = Assert.Single(manifest.Backend!.Extensions);
        Assert.Equal(CommonsNovaContextExtension.ExtensionId, extension.Id);
        Assert.Equal("nova", extension.Target);
        Assert.Equal("context-contributor", extension.Slot);
        Assert.Equal(PluginExtensionContracts.ContextFragmentV1, extension.Contract);
        Assert.DoesNotContain(manifest.Permissions,
            permission => permission.Kind == "os" && permission.Target == "issue-system-execution-token");
    }

    [Fact]
    public async Task Generate_request_is_always_anthropic_direct_and_verified_tool_free()
    {
        var gateway = new CaptureGateway(HttpStatusCode.OK, "{\"success\":true,\"text\":\"{\\\"action\\\":\\\"done\\\"}\"}");
        var result = await new ToolFreeModelClient(gateway).GenerateActionAsync("system", "prompt", new("thread-1", "ping-1"));
        using var body = JsonDocument.Parse(gateway.Body!);
        Assert.Equal("anthropic-direct", body.RootElement.GetProperty("provider").GetString());
        Assert.Equal("standard", body.RootElement.GetProperty("qualityTier").GetString());
        Assert.False(body.RootElement.TryGetProperty("model", out _));
        Assert.True(body.RootElement.GetProperty("requireToolFree").GetBoolean());
        Assert.Equal("oneshot", body.RootElement.GetProperty("mode").GetString());
        Assert.False(body.RootElement.GetProperty("confidential").GetBoolean());
        Assert.Equal("/ai-session/generate", gateway.RequestUri);
        Assert.False(gateway.HadAuthorizationHeader);
        Assert.False(gateway.HadProvenanceHeader);
        Assert.Equal("Leaf Community restricted reply: thread-1", gateway.JobName);
        Assert.NotNull(gateway.Provenance);
        Assert.Equal("leaf-commons", gateway.Provenance.Origin.App.Id);
        Assert.Equal("service", gateway.Provenance.Actor.Kind);
        Assert.Equal("commons-envoy", gateway.Provenance.Actor.Id);
        Assert.Equal("system", gateway.Provenance.OnBehalfOf.Kind);
        Assert.Equal(ComputeProvenanceAssurance.Asserted, gateway.Provenance.Assurance);
        Assert.Contains(gateway.Provenance.Context, x => x.Kind == "external-conversation" && x.Id == "thread-1");
        Assert.Contains(gateway.Provenance.Context, x => x.Kind == "relay-event" && x.Id == "ping-1");
        Assert.Equal("{\"action\":\"done\"}", result);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "gateway_forbidden")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "tool_free_unavailable")]
    [InlineData(HttpStatusCode.BadGateway, "gateway_bad_gateway")]
    public async Task Generate_failure_has_a_stable_reason_without_fallback(HttpStatusCode status, string reason)
    {
        var response = status == HttpStatusCode.UnprocessableEntity
            ? "{\"error\":\"tool_free_unavailable\"}"
            : "{\"error\":\"failed\"}";
        var gateway = new CaptureGateway(status, response);
        var error = await Assert.ThrowsAsync<ToolFreeGenerateException>(() =>
            new ToolFreeModelClient(gateway).GenerateActionAsync("system", "prompt", new("thread-1", "ping-1")));
        Assert.Equal((int)status, error.Status);
        Assert.Equal(reason, error.ReasonCode);
        Assert.Equal(1, gateway.Calls);
    }

    [Fact]
    public void Owner_guard_requires_matching_private_nova_agent_and_beneficiary()
    {
        var settings = new CommonsSettings(null, "http://127.0.0.1", null, "pending", "Nova", null, ConversationSwitches.Default, [], [], [], null, [], "owner-1", "agent-1");
        var identity = """{"app":{"id":"nova"},"actor":{"kind":"agent","entityId":"agent-1"},"beneficiary":{"kind":"user","id":"owner-1"},"context":[{"kind":"discussion","id":"d1"}]}""";
        var principal = Principal(identity);
        Assert.NotNull(OwnerGuard.ReadPrivateNovaAgent(principal, settings));
        Assert.Null(OwnerGuard.ReadPrivateNovaAgent(Principal(identity.Replace("owner-1", "intruder")), settings));
        Assert.Null(OwnerGuard.ReadPrivateNovaAgent(Principal(identity.Replace("discussion", "external-conversation")), settings));
        Assert.Null(OwnerGuard.ReadPrivateNovaAgent(Principal(identity.Replace("]}", ",{\"kind\":\"external-conversation\",\"id\":\"external-1\"}]}")), settings));
        Assert.Null(OwnerGuard.ReadPrivateNovaAgent(new ClaimsPrincipal(new ClaimsIdentity()), settings));
    }

    [Fact]
    public void Public_settings_projection_never_returns_the_envoy_credential()
    {
        var settings = new CommonsSettings(null, "https://relay.example", "envoy.secret-value", "active", "Nova", null, ConversationSwitches.Default, [], [], [], null, []);
        var json = JsonSerializer.Serialize(settings.Public());
        Assert.DoesNotContain("secret-value", json); Assert.Contains("credentialConfigured", json);
    }

    [Fact]
    public void Digest_is_typed_bounded_external_quote_and_reduces_links_to_allowlist()
    {
        var text = new DigestFormatter().Format(new("consult_answer", "See https://docs.example.com/ok and https://evil.test/leak", "thread-1"), ["docs.example.com"], 240);
        Assert.StartsWith("> External Leaf Community quote", text); Assert.Contains("https://docs.example.com/ok", text); Assert.DoesNotContain("evil.test", text); Assert.True(text.Length <= 240);
    }

    [Fact]
    public async Task Connector_core_deduplicates_and_acks_expired_items_without_dispatch()
    {
        var handled = new List<EnvoyRunRequest>(); var acked = new List<string>();
        var core = new ConnectorCore((request, _) => { handled.Add(request); return Task.CompletedTask; });
        using var live = JsonDocument.Parse("{\"type\":\"ping\",\"id\":\"p1\",\"threadRef\":\"t1\",\"content\":\"question\"}");
        Assert.True(await core.ProcessAsync(live.RootElement, id => { acked.Add(id); return Task.CompletedTask; }));
        Assert.False(await core.ProcessAsync(live.RootElement, id => { acked.Add(id); return Task.CompletedTask; }));
        using var expired = JsonDocument.Parse("{\"type\":\"ping\",\"id\":\"p2\",\"threadRef\":\"t1\",\"content\":\"late\",\"expiresAt\":1}");
        Assert.False(await core.ProcessAsync(expired.RootElement, id => { acked.Add(id); return Task.CompletedTask; }));
        Assert.Single(handled);
        Assert.True(handled[0].Addressed);
        Assert.Equal(["p1", "p1", "p2"], acked);
    }

    [Fact]
    public async Task Listening_event_without_thread_preserves_null_and_never_becomes_a_run()
    {
        var runs = new List<EnvoyRunRequest>();
        var listening = new List<RelayListeningEvent>();
        var core = new ConnectorCore(
            (request, _) => { runs.Add(request); return Task.CompletedTask; },
            (item, _) => { listening.Add(item); return Task.CompletedTask; });
        using var frame = JsonDocument.Parse("""
            {"type":"event","id":"event-1","scenario":"showcase",
             "channel":"showcase","threadRef":null,"content":"A read-only update"}
            """);

        Assert.True(await core.ProcessAsync(frame.RootElement, _ => Task.CompletedTask));

        Assert.Empty(runs);
        Assert.Null(Assert.Single(listening).ThreadRef);
    }

    [Fact]
    public async Task Text_channel_ping_keeps_its_message_chain_channel_and_reply_name_without_reading_a_scenario()
    {
        var runs = new List<EnvoyRunRequest>();
        var core = new ConnectorCore((request, _) => { runs.Add(request); return Task.CompletedTask; });
        using var frame = JsonDocument.Parse("""
            {"type":"ping","id":"live-1644","scenario":"channel_reply",
             "channel":"showcase","threadRef":"message:1563000000000000000",
             "sender":{"kind":"human","displayName":"Laurent"},
             "content":"Hello! What's 2+2?"}
            """);

        Assert.True(await core.ProcessAsync(frame.RootElement, _ => Task.CompletedTask));

        var run = Assert.Single(runs);
        Assert.True(run.Addressed);
        Assert.Equal("ping", run.Kind);
        Assert.Equal("showcase", run.Channel);
        Assert.Equal("message:1563000000000000000", run.ThreadRef);
        Assert.Equal("Laurent", run.ReplyToDisplayName);
    }

    [Fact]
    public async Task Relay_reply_names_only_its_thread_and_leaves_the_replying_line_to_the_relay()
    {
        var handler = new CaptureHttpHandler();
        var relay = new RelayHttpClient(new HttpClient(handler), _ =>
            Task.FromResult<(Uri, string?)>((new Uri("https://relay.example"), "envoy.secret")));

        await relay.PostAsync("message:root-1", "4.", idempotencyKey: "key-1");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(["addresses", "content", "idempotencyKey", "threadRef"],
            body.RootElement.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.Equal("message:root-1", body.RootElement.GetProperty("threadRef").GetString());
        Assert.Equal("4.", body.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Relay_open_names_a_channel_and_a_title()
    {
        var handler = new CaptureHttpHandler();
        var relay = new RelayHttpClient(new HttpClient(handler), _ =>
            Task.FromResult<(Uri, string?)>((new Uri("https://relay.example"), "envoy.secret")));

        await relay.OpenAsync("200", "Retry strategy", "How do you retry?", ["Nova"], "key-2");

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(["addresses", "channelId", "content", "idempotencyKey", "title"],
            body.RootElement.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.Equal("200", body.RootElement.GetProperty("channelId").GetString());
        Assert.Equal("Retry strategy", body.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Relay_profile_sync_never_overwrites_the_relay_owned_avatar_key()
    {
        var handler = new CaptureHttpHandler();
        var relay = new RelayHttpClient(new HttpClient(handler), _ => Task.FromResult<(Uri, string?)>((new Uri("https://relay.example"), "envoy.secret")));
        await relay.UpdateProfileAsync("Owner");
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(["publicName"], body.RootElement.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.Equal("Owner", body.RootElement.GetProperty("publicName").GetString());
        Assert.False(body.RootElement.TryGetProperty("avatarObjectKey", out _));
        Assert.Equal("Bearer", handler.AuthorizationScheme); Assert.Equal("envoy.secret", handler.AuthorizationParameter);
    }

    [Fact]
    public async Task OAuth_exchange_reads_the_relay_owned_default_public_name()
    {
        var handler = new StaticJsonHandler(HttpStatusCode.OK, new
        {
            credential = "envoy.credential",
            envoy = new
            {
                id = "envoy-1", status = "active", publicName = "Laurent",
                personaName = "Nova · Laurent", avatarObjectKey = "envoys/envoy-1/avatar.webp",
            },
        });
        var relay = new RelayHttpClient(new HttpClient(handler), _ =>
            Task.FromResult<(Uri, string?)>((new Uri("https://relay.example"), null)));

        var exchange = await relay.ExchangeAsync("exchange-code");

        Assert.Equal("Laurent", exchange.PublicName);
        Assert.Equal("Nova · Laurent", exchange.PersonaName);
    }

    [Fact]
    public async Task Relay_profile_error_is_preserved_for_the_settings_caller()
    {
        var handler = new StaticJsonHandler(HttpStatusCode.Conflict, new
            { error = "public_name_reserved", message = "That name belongs to a Discord member." });
        var relay = new RelayHttpClient(new HttpClient(handler), _ =>
            Task.FromResult<(Uri, string?)>((new Uri("https://relay.example"), "envoy.secret")));

        var error = await Assert.ThrowsAsync<RelayApiException>(() =>
            relay.UpdateProfileAsync("Reserved"));

        Assert.Equal("public_name_reserved", error.Reason);
        Assert.Equal("That name belongs to a Discord member.", error.Message);
        Assert.Equal(409, error.StatusCode);
    }

    private static ClaimsPrincipal Principal(string executionIdentity) => new(new ClaimsIdentity([new Claim("token_use", "execution"), new Claim("execution_identity", executionIdentity)], "test"));
}

internal sealed class CaptureGateway(HttpStatusCode status, string response) : IComputeGateway
{
    public string? Body { get; private set; }
    public int Calls { get; private set; }
    public string? RequestUri { get; private set; }
    public string? JobName { get; private set; }
    public bool HadAuthorizationHeader { get; private set; }
    public bool HadProvenanceHeader { get; private set; }
    public ComputeProvenance? Provenance { get; private set; }
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, ComputeProvenance? provenance = null, CancellationToken ct = default)
    {
        Calls++;
        Body = await request.Content!.ReadAsStringAsync(ct);
        RequestUri = request.RequestUri?.OriginalString;
        JobName = request.Headers.TryGetValues("X-Job-Name", out var names) ? names.Single() : null;
        HadAuthorizationHeader = request.Headers.Authorization is not null;
        HadProvenanceHeader = request.Headers.Contains("X-Compute-Provenance");
        Provenance = provenance;
        return new(status) { Content = JsonContent.Create(JsonDocument.Parse(response).RootElement) };
    }
}

internal sealed class CaptureHttpHandler : HttpMessageHandler
{
    public string? Body { get; private set; } public string? AuthorizationScheme { get; private set; } public string? AuthorizationParameter { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Body = await request.Content!.ReadAsStringAsync(cancellationToken); AuthorizationScheme = request.Headers.Authorization?.Scheme; AuthorizationParameter = request.Headers.Authorization?.Parameter;
        return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { envoy = new { } }) };
    }
}

internal sealed class StaticJsonHandler(HttpStatusCode status, object payload) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = JsonContent.Create(payload) });
}
