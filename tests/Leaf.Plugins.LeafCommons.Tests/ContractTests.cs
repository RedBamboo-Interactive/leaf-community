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
        Assert.Equal("Nova Commons Envoy: thread-1", gateway.JobName);
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
        var settings = new CommonsSettings(null, "http://127.0.0.1", null, "pending", "Nova", null, new Dictionary<string, bool>(), [], [], [], null, [], "owner-1", "agent-1");
        var identity = """{"app":{"id":"nova"},"actor":{"kind":"agent","entityId":"agent-1"},"beneficiary":{"kind":"user","id":"owner-1"},"context":[{"kind":"discussion","id":"d1"}]}""";
        var principal = Principal(identity);
        Assert.NotNull(OwnerGuard.ReadPrivateNovaAgent(principal, settings));
        Assert.Null(OwnerGuard.ReadPrivateNovaAgent(Principal(identity.Replace("owner-1", "intruder")), settings));
        Assert.Null(OwnerGuard.ReadPrivateNovaAgent(Principal(identity.Replace("discussion", "external-conversation")), settings));
        Assert.Null(OwnerGuard.ReadPrivateNovaAgent(new ClaimsPrincipal(new ClaimsIdentity()), settings));
    }

    [Fact]
    public void Public_settings_projection_never_returns_the_envoy_credential()
    {
        var settings = new CommonsSettings(null, "https://relay.example", "envoy.secret-value", "active", "Nova", null, new Dictionary<string, bool>(), [], [], [], null, []);
        var json = JsonSerializer.Serialize(settings.Public());
        Assert.DoesNotContain("secret-value", json); Assert.Contains("credentialConfigured", json);
    }

    [Fact]
    public void Digest_is_typed_bounded_external_quote_and_reduces_links_to_allowlist()
    {
        var text = new DigestFormatter().Format(new("consult_answer", "See https://docs.example.com/ok and https://evil.test/leak", "thread-1"), ["docs.example.com"], 240);
        Assert.StartsWith("> External Commons quote", text); Assert.Contains("https://docs.example.com/ok", text); Assert.DoesNotContain("evil.test", text); Assert.True(text.Length <= 240);
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
    public async Task Relay_profile_request_contains_only_public_fields()
    {
        var handler = new CaptureHttpHandler();
        var relay = new RelayHttpClient(new HttpClient(handler), () => (new Uri("https://relay.example"), "envoy.secret"));
        await relay.UpdateProfileAsync("Nova · Owner", "envoys/envoy-1/profile.webp", new Dictionary<string, bool> { ["consult"] = true });
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(["avatarObjectKey", "personaName", "scenarioToggles"], body.RootElement.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.Equal("Bearer", handler.AuthorizationScheme); Assert.Equal("envoy.secret", handler.AuthorizationParameter);
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
