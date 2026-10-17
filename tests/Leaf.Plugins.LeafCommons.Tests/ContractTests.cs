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
    public async Task Generate_request_is_always_anthropic_direct_and_verified_tool_free()
    {
        var gateway = new CaptureGateway(HttpStatusCode.OK, "{\"success\":true,\"text\":\"{\\\"action\\\":\\\"done\\\"}\"}");
        var result = await new ToolFreeModelClient(gateway).GenerateActionAsync("system", "prompt");
        using var body = JsonDocument.Parse(gateway.Body!);
        Assert.Equal("anthropic-direct", body.RootElement.GetProperty("provider").GetString());
        Assert.True(body.RootElement.GetProperty("requireToolFree").GetBoolean());
        Assert.Equal("oneshot", body.RootElement.GetProperty("mode").GetString());
        Assert.Equal("{\"action\":\"done\"}", result);
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Generate_422_or_502_is_a_hard_failure_without_fallback(HttpStatusCode status)
    {
        var gateway = new CaptureGateway(status, "{\"error\":\"failed\"}");
        var error = await Assert.ThrowsAsync<ToolFreeGenerateException>(() => new ToolFreeModelClient(gateway).GenerateActionAsync("system", "prompt"));
        Assert.Equal((int)status, error.Status); Assert.Equal(1, gateway.Calls);
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
        var handled = 0; var acked = new List<string>();
        var core = new ConnectorCore((_, _) => { handled++; return Task.CompletedTask; });
        using var live = JsonDocument.Parse("{\"type\":\"ping\",\"id\":\"p1\",\"threadRef\":\"t1\",\"content\":\"question\"}");
        Assert.True(await core.ProcessAsync(live.RootElement, id => { acked.Add(id); return Task.CompletedTask; }));
        Assert.False(await core.ProcessAsync(live.RootElement, id => { acked.Add(id); return Task.CompletedTask; }));
        using var expired = JsonDocument.Parse("{\"type\":\"ping\",\"id\":\"p2\",\"threadRef\":\"t1\",\"content\":\"late\",\"expiresAt\":1}");
        Assert.False(await core.ProcessAsync(expired.RootElement, id => { acked.Add(id); return Task.CompletedTask; }));
        Assert.Equal(1, handled); Assert.Equal(["p1", "p1", "p2"], acked);
    }

    private static ClaimsPrincipal Principal(string executionIdentity) => new(new ClaimsIdentity([new Claim("token_use", "execution"), new Claim("execution_identity", executionIdentity)], "test"));
}

internal sealed class CaptureGateway(HttpStatusCode status, string response) : IComputeGateway
{
    public string? Body { get; private set; } public int Calls { get; private set; }
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, ComputeProvenance? provenance = null, CancellationToken ct = default)
    {
        Calls++; Body = await request.Content!.ReadAsStringAsync(ct);
        return new(status) { Content = JsonContent.Create(JsonDocument.Parse(response).RootElement) };
    }
}
