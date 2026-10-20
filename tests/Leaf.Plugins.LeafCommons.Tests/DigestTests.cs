using System.Net;
using System.Text.Json;
using Leaf.Plugins.LeafCommons;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.LeafCommons.Tests;

public sealed class DigestTests
{
    [Fact]
    public async Task Forbidden_human_owned_discussion_is_replaced_with_plugin_system_owned_discussion()
    {
        var entities = new MemoryEntityStore();
        var state = new CommonsStateStore(entities);
        await state.SaveSettingsAsync(new(null, "https://relay.example", "credential", "active", "Nova", null,
            new Dictionary<string, bool>(), [], [], [], "human-owned", [], "owner-1", "agent-1"));
        var handler = new DigestHttpHandler();
        var audit = new MemoryAudit();
        var sink = new NovaDigestSink(new HttpClient(handler), new FakeExecutionTokens(), state,
            new DigestFormatter(), audit);

        await sink.DeliverAsync(new("owner_notice", "I couldn't answer thread t1: repeated_action.", "t1"));

        var settings = await state.GetSettingsAsync();
        Assert.Equal("plugin-owned", settings.NovaDiscussionId);
        Assert.Equal(2, handler.EventCalls);
        Assert.Equal(1, handler.CreateCalls);
        Assert.Equal(1, handler.TitleCalls);
        Assert.True(handler.CreateDeferredSession);
        Assert.Equal("leaf-commons-digests-v1", handler.CreateIdempotencyKey);
        Assert.Equal("Nova Commons digests (lab)", handler.Title);
        Assert.All(handler.AuthorizationSchemes, value => Assert.Equal("Bearer", value));
        Assert.Contains(audit.Events, item => item.Name == "digest_discussion_provisioned"
            && Equals(item.Data["discussionId"], "plugin-owned")
            && Equals(item.Data["owner"], "plugin-system"));
        Assert.Contains(audit.Events, item => item.Name == "digest_delivered");
    }
}

internal sealed class FakeExecutionTokens : IExecutionTokens
{
    public PluginExecutionToken IssueSystem(PluginSystemExecutionTokenRequest request) =>
        new("test-system-execution-token", DateTimeOffset.UtcNow.AddMinutes(2), Guid.NewGuid().ToString());
}

internal sealed class DigestHttpHandler : HttpMessageHandler
{
    public int EventCalls { get; private set; }
    public int CreateCalls { get; private set; }
    public int TitleCalls { get; private set; }
    public bool CreateDeferredSession { get; private set; }
    public string? CreateIdempotencyKey { get; private set; }
    public string? Title { get; private set; }
    public List<string?> AuthorizationSchemes { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        AuthorizationSchemes.Add(request.Headers.Authorization?.Scheme);
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Post && path.EndsWith("/event", StringComparison.Ordinal))
        {
            EventCalls++;
            return new(EventCalls == 1 ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
                { Content = new StringContent(EventCalls == 1 ? "{\"error\":\"Forbidden\"}" : "{\"success\":true}") };
        }
        if (request.Method == HttpMethod.Post && path.EndsWith("/discussions", StringComparison.Ordinal))
        {
            CreateCalls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            CreateDeferredSession = body.RootElement.GetProperty("deferSession").GetBoolean();
            CreateIdempotencyKey = body.RootElement.GetProperty("idempotencyKey").GetString();
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"plugin-owned\"}") };
        }
        if (request.Method == HttpMethod.Put && path.EndsWith("/title", StringComparison.Ordinal))
        {
            TitleCalls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Title = body.RootElement.GetProperty("title").GetString();
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"plugin-owned\"}") };
        }
        return new(HttpStatusCode.NotFound);
    }
}
