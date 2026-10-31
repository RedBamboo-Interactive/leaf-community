using System.Security.Claims;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leaf.Plugins.LeafCommons.Tests;

/// <summary>
/// The requests an outbound review can find in flight. Field report (31 Oct 2026, Release
/// install): every brief a Public Agent posted came back <c>sentinel_unavailable</c> with
/// "beneficiary does not match the authenticated caller" between the Sentinel and Compute.
/// </summary>
internal static class ReviewIdentityShapes
{
    public const string OwnerAgent = "owner's Agent request";
    public const string OtherAgent = "another user's Agent request";
    public const string OwnerSignedIn = "owner signed in";
    public const string OtherSignedIn = "another user signed in";
    public const string LocalDefault = "LocalDefault";
    public const string None = "no request";

    public static TheoryData<string> All => new()
        { OwnerAgent, OtherAgent, OwnerSignedIn, OtherSignedIn, LocalDefault, None };

    public static ClaimsPrincipal? Caller(string shape, string owner, string other, string agentId) => shape switch
    {
        OwnerAgent => Callers.AgentExecution(owner, agentId),
        OtherAgent => Callers.AgentExecution(other, agentId),
        OwnerSignedIn => Callers.SignedIn(owner),
        OtherSignedIn => Callers.SignedIn(other),
        LocalDefault => Callers.LocalDefault(),
        _ => null,
    };

    /// <summary>Who a review made inside <paramref name="shape"/> acts for: the request's own
    /// caller when there is one, the owner otherwise.</summary>
    public static (string Kind, string? Id) Expected(string shape, string owner, string other) => shape switch
    {
        OtherAgent or OtherSignedIn => ("user", other),
        LocalDefault => ("system", null),
        _ => ("user", owner),
    };
}

public sealed class ReviewIdentityTests : IDisposable
{
    private const string AgentId = "a7b059ac-f678-8b58-819b-1bcf3acb22db";
    private readonly string owner = Guid.NewGuid().ToString();
    private readonly string other = Guid.NewGuid().ToString();
    private readonly string root = Path.Combine(Path.GetTempPath(), "leaf-commons-identity-" + Guid.NewGuid().ToString("N"));

    public ReviewIdentityTests() => Directory.CreateDirectory(root);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Theory]
    [MemberData(nameof(ReviewIdentityShapes.All), MemberType = typeof(ReviewIdentityShapes))]
    public async Task A_brief_is_reviewed_with_an_identity_RedLeaf_signs_in_every_request(string shape)
    {
        var (state, entities) = await PublicAgentAsync();
        var requests = Callers.InFlight(ReviewIdentityShapes.Caller(shape, owner, other, AgentId));
        var kernel = new KernelBoundSentinel(requests, owner, other);
        var relay = new MultiAgentRelayHandler();

        var result = await Runtime(state, relay, new CommonsOutboundReview(kernel, entities: entities, requests: requests))
            .PostBriefAsync(new("consult", "A note.", "A title", OwnerRequested: true), new(AgentId, owner));

        var call = Assert.Single(kernel.Calls);
        Assert.True(call.Issued, call.Refusal);
        Assert.Equal(ReviewIdentityShapes.Expected(shape, owner, other), (call.BeneficiaryKind!, call.BeneficiaryId));
        Assert.Equal("posted", result.Decision);
    }

    [Theory]
    [MemberData(nameof(ReviewIdentityShapes.All), MemberType = typeof(ReviewIdentityShapes))]
    public async Task An_Envoy_relay_post_is_reviewed_with_an_identity_RedLeaf_signs_in_every_request(string shape)
    {
        var (state, entities) = await PublicAgentAsync();
        var requests = Callers.InFlight(ReviewIdentityShapes.Caller(shape, owner, other, AgentId));
        var kernel = new KernelBoundSentinel(requests, owner, other);
        var relay = new MultiAgentRelayHandler();
        var envoy = new ReviewedEnvoyRelay(new RelayClientFactory(new FixedHttpClientFactory(relay), state).For(AgentId),
            new CommonsOutboundReview(kernel, entities: entities, requests: requests), AgentId,
            state, owner);

        var posted = await envoy.PostAsync("message:root", "Here is what I found.");

        var call = Assert.Single(kernel.Calls);
        Assert.True(call.Issued, call.Refusal);
        Assert.Equal(ReviewIdentityShapes.Expected(shape, owner, other), (call.BeneficiaryKind!, call.BeneficiaryId));
        Assert.True(posted.Accepted, posted.Code);
        Assert.Contains(relay.Requests, item => item.Route == "POST /v1/posts" && item.Body.Contains("Here is what I found."));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_owners_Agent_brief_is_no_longer_refused_as_a_beneficiary_mismatch(bool ownerResolves)
    {
        // The field shape: the owner's own Agent posts a brief from its private discussion, so the
        // review runs inside that Agent's signed execution request. 0.1.9 named the system there
        // (its entity store was never wired, so the owner never resolved) and RedLeaf refused it.
        var (state, entities) = await PublicAgentAsync(seedOwner: ownerResolves);
        var requests = Callers.InFlight(Callers.AgentExecution(owner, AgentId));
        var kernel = new KernelBoundSentinel(requests, owner);
        var relay = new MultiAgentRelayHandler();

        var result = await Runtime(state, relay,
                new CommonsOutboundReview(kernel, entities: ownerResolves ? entities : null, requests: requests))
            .PostBriefAsync(new("showcase", "Look what I built.", OwnerRequested: true), new(AgentId, owner));

        Assert.Equal("posted", result.Decision);
        Assert.Null(Assert.Single(kernel.Calls).Refusal);
        // It inherits the Agent's own identity rather than stating one.
        Assert.Null(Assert.Single(kernel.Inner.Requests).ComputeProvenance);
    }

    /// <summary>
    /// The root cause: an explicit provenance is signed only for the beneficiary the request in
    /// flight may act for. 0.1.9's always named the system (unwired entity store); the provenance
    /// 2dff982 meant to state names the owner. Each is refused in exactly these requests.
    /// </summary>
    [Theory]
    [InlineData(false, ReviewIdentityShapes.OwnerAgent, true)]
    [InlineData(false, ReviewIdentityShapes.OtherAgent, true)]
    [InlineData(false, ReviewIdentityShapes.OwnerSignedIn, true)]
    [InlineData(false, ReviewIdentityShapes.OtherSignedIn, true)]
    [InlineData(false, ReviewIdentityShapes.LocalDefault, false)]
    [InlineData(false, ReviewIdentityShapes.None, false)]
    [InlineData(true, ReviewIdentityShapes.OwnerAgent, false)]
    [InlineData(true, ReviewIdentityShapes.OtherAgent, true)]
    [InlineData(true, ReviewIdentityShapes.OwnerSignedIn, false)]
    [InlineData(true, ReviewIdentityShapes.OtherSignedIn, true)]
    [InlineData(true, ReviewIdentityShapes.LocalDefault, true)]
    [InlineData(true, ReviewIdentityShapes.None, false)]
    public async Task A_stated_owner_or_system_provenance_is_refused_in_these_requests(
        bool ownerResolves, string shape, bool refused)
    {
        var (_, entities) = await PublicAgentAsync(seedOwner: true);
        var store = ownerResolves ? entities : null;
        var stated = await CommonsComputeProvenance.CreateAsync(store, AgentId,
            await CommonsComputeProvenance.BeneficiaryAsync(store, owner, default), [], null, default);

        var issue = KernelComputeIdentity.Issue(stated, ReviewIdentityShapes.Caller(shape, owner, other, AgentId),
            id => id == owner || id == other);

        Assert.Equal(refused ? KernelComputeIdentity.Mismatch : null, issue.Refusal);
    }

    [Fact]
    public async Task Only_the_owners_own_Agent_request_reaches_a_brief_review()
    {
        var (state, _) = await PublicAgentAsync();
        var settings = await state.GetOwnerSettingsAsync();

        Assert.Equal(new LeafAgentCaller(AgentId, owner),
            OwnerGuard.ReadPrivateAgent(Callers.AgentExecution(owner, AgentId), settings));
        // An owner id recorded at join that is not the signed-in user, another user's Agent, a
        // plain sign-in and LocalDefault are refused (403 owner_agent_required) before any review.
        Assert.Null(OwnerGuard.ReadPrivateAgent(Callers.AgentExecution(other, AgentId), settings));
        Assert.Null(OwnerGuard.ReadPrivateAgent(Callers.SignedIn(owner), settings));
        Assert.Null(OwnerGuard.ReadPrivateAgent(Callers.LocalDefault(), settings));
    }

    [Fact]
    public async Task The_plugin_wires_the_review_to_its_entity_store_and_the_request_in_flight()
    {
        var (state, entities) = await PublicAgentAsync();
        var requests = Callers.InFlight(null);
        var kernel = new KernelBoundSentinel(requests, owner);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKeyedSingleton<IEntityStore>(LeafCommonsPlugin.PluginId, entities);
        services.AddSingleton<IUntrustedContentReview>(kernel);
        services.AddSingleton<IHttpContextAccessor>(requests);
        var plugin = new LeafCommonsPlugin();
        plugin.ConfigureServices(services, new PluginContext { Manifest = plugin.Manifest, PluginDirectory = root, DevMode = true });
        var review = services.BuildServiceProvider().GetRequiredService<CommonsOutboundReview>();

        await review.ReviewAsync(AgentId, "message:root", "A reply.", "key-1", ownerUserId: owner);
        requests.HttpContext = new DefaultHttpContext { User = Callers.AgentExecution(owner, AgentId) };
        await review.ReviewAsync(AgentId, "message:root", "A brief.", "key-2", ownerUserId: owner);

        // With no request the owner is named (RedLeaf keys the store by plugin id; 0.1.9 looked it
        // up unkeyed, got null, and named the system); inside the Agent's request it is inherited.
        var stated = Assert.IsType<ComputeProvenance>(kernel.Inner.Requests[0].ComputeProvenance);
        Assert.Equal(("user", owner), (stated.OnBehalfOf.Kind, stated.OnBehalfOf.Id));
        Assert.Equal(AgentId, stated.Actor.EntityId);
        Assert.Null(kernel.Inner.Requests[1].ComputeProvenance);
        Assert.All(kernel.Calls, call => Assert.True(call.Issued, call.Refusal));
    }

    private async Task<(CommonsStateStore State, MemoryEntityStore Entities)> PublicAgentAsync(bool seedOwner = true)
    {
        var entities = new MemoryEntityStore();
        if (seedOwner) entities.Seed(Guid.Parse(owner), "user", "Fred", "fred", new JsonObject());
        entities.Seed(Guid.Parse(AgentId), "agent", "Nova", "nova", new JsonObject());
        var state = new CommonsStateStore(entities);
        await state.SaveOwnerSettingsAsync(new(null, "https://relay.example", "Fred", [], [], [], owner));
        await state.SavePublicAgentAsync(new(null, AgentId, "Nova", true, "credential", "active", null,
            ConversationSwitches.Default, [], RestrictedMode: true));
        return (state, entities);
    }

    private PublicAgentRuntime Runtime(CommonsStateStore state, MultiAgentRelayHandler relay, CommonsOutboundReview review)
        => new(new CountingModel("unused"), new AgentShareableStores(root),
            new RelayClientFactory(new FixedHttpClientFactory(relay), state),
            new OutboundFilter(new SecretScanner()), state, new NullDigestSink(), outboundReview: review);
}
