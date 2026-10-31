using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Leaf.Plugins.LeafCommons.Tests;

public sealed partial class ExternalConversationTests
{
    /// <summary>
    /// RedCompute's settle callback is itself a request (signed in as the last browser user, or
    /// LocalDefault), and it starts the delivery. The delivery runs detached from it: whatever
    /// request is in flight, the settled reply's review states the owner's provenance (2dff982).
    /// </summary>
    [Theory]
    [MemberData(nameof(ReviewIdentityShapes.All), MemberType = typeof(ReviewIdentityShapes))]
    public async Task A_settled_reply_is_reviewed_for_the_owner_whatever_request_settled_it(string shape)
    {
        var owner = Guid.NewGuid().ToString();
        var other = Guid.NewGuid().ToString();
        var requests = new HttpContextAccessor();
        var kernel = new KernelBoundSentinel(requests, owner, other);
        var fixture = await CreateAsync(kernel: kernel, requests: requests, ownerUserId: owner);
        fixture.Entities.Seed(Guid.Parse(owner), "user", "Frank", "frank", new JsonObject());
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, Request("ping-1", "Can you look?"));
        fixture.Provider.Messages = [Message(1, "assistant", "text", "Here is the fix.")];

        if (ReviewIdentityShapes.Caller(shape, owner, other, AgentId) is { } caller)
            requests.HttpContext = new DefaultHttpContext { User = caller };
        await fixture.Provider.NotifyAsync();
        await fixture.Coordinator.WhenIdleAsync();
        requests.HttpContext = null;

        var call = Assert.Single(kernel.Calls);
        Assert.True(call.Issued, call.Refusal);
        Assert.Equal(("user", owner), (call.BeneficiaryKind!, call.BeneficiaryId));
        Assert.Contains(fixture.Relay.Requests,
            item => item.Route == "POST /v1/posts" && item.Body.Contains("Here is the fix.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_settled_reply_for_an_owner_without_a_user_is_still_reviewed_as_the_system()
    {
        // 2dff982 (a peer install): no request, no execution scope, and an owner RedLeaf cannot
        // authenticate. The review names the bounded system principal and is signed.
        var requests = new HttpContextAccessor();
        var kernel = new KernelBoundSentinel(requests);
        var fixture = await CreateAsync(kernel: kernel, requests: requests);
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, Request("ping-1", "Hi"));
        fixture.Provider.Messages = [Message(1, "assistant", "text", "Here is the fix.")];

        await fixture.Provider.NotifyAsync();
        await fixture.Coordinator.WhenIdleAsync();

        var call = Assert.Single(kernel.Calls);
        Assert.Equal(("system", "system"), (call.BeneficiaryKind!, call.Subject!));
        Assert.Contains(fixture.Relay.Requests, item => item.Route == "POST /v1/posts"
            && item.Body.Contains("Here is the fix.", StringComparison.Ordinal));
    }
}
