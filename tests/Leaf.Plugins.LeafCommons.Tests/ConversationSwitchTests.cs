using System.Text.Json.Nodes;
using Leaf.Plugins.LeafCommons;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.LeafCommons.Tests;

public sealed class ConversationSwitchTests : IDisposable
{
    private const string AgentId = "a7b059ac-f678-8b58-819b-1bcf3acb22db";
    private readonly string root = Path.Combine(Path.GetTempPath(), "leaf-commons-switches-" + Guid.NewGuid().ToString("N"));

    public ConversationSwitchTests() => Directory.CreateDirectory(root);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Theory]
    [InlineData("{}", true, false)]
    [InlineData("""{"consult":false}""", false, false)]
    [InlineData("""{"showcase":true}""", true, true)]
    [InlineData("""{"consult":true,"bug":false}""", true, true)]
    [InlineData("""{"open_question":true,"consult":false}""", false, true)]
    [InlineData("""{"showcase":false,"consult":false,"open_question":false,"bug":false}""", false, false)]
    public async Task Saved_scenario_switches_become_the_two_conversation_switches(
        string toggles, bool answers, bool starts)
    {
        var entities = new MemoryEntityStore();
        var state = new CommonsStateStore(entities);
        await state.SavePublicAgentAsync(new(null, AgentId, "Nova", true, "credential", "active", null,
            ConversationSwitches.Default, []));
        // What a release before the two switches stored.
        var entity = Assert.Single(await entities.QueryAsync(new EntityQuery { TypeSlug = "leaf-commons-public-agent" }));
        var data = entity.Data.DeepClone().AsObject();
        data.Remove("answers_when_spoken_to");
        data.Remove("starts_conversations");
        data["scenario_toggles"] = JsonNode.Parse(toggles);
        await entities.ReplaceDataAsync(entity.Id, data);

        var migrated = (await state.GetPublicAgentAsync(AgentId))!.Switches;

        Assert.Equal(new ConversationSwitches(answers, starts), migrated);
    }

    [Fact]
    public async Task With_answering_off_the_Agent_does_not_answer_when_spoken_to()
    {
        var (runtime, relay, model) = await RuntimeAsync(new(AnswersWhenSpokenTo: false, StartsConversations: true));

        var result = await runtime.HandleAsync(AgentId,
            new("thread-1", "ping", "Nova, can you help?", TriggerId: "ping-1", Addressed: true));

        Assert.Equal(("disabled", "answers_disabled"), (result.State, result.Summary));
        Assert.Equal(0, model.Calls);
        Assert.Empty(relay.Requests);
    }

    [Fact]
    public async Task With_starting_off_the_Agent_does_not_join_in_unasked_but_still_answers()
    {
        var (runtime, relay, model) = await RuntimeAsync(new(AnswersWhenSpokenTo: true, StartsConversations: false));

        var unasked = await runtime.HandleAsync(AgentId,
            new("thread-1", "event", "Anyone know a good cache?", TriggerId: "event-1"));
        Assert.Equal(("disabled", "starts_disabled"), (unasked.State, unasked.Summary));
        Assert.Equal(0, model.Calls);
        Assert.Empty(relay.Requests);

        var asked = await runtime.HandleAsync(AgentId,
            new("thread-1", "ping", "Nova, any idea?", TriggerId: "ping-1", Addressed: true));
        Assert.Equal("done", asked.State);
        Assert.Single(relay.Requests, item => item.Route == "POST /v1/posts");
    }

    private async Task<(PublicAgentRuntime Runtime, MultiAgentRelayHandler Relay, CountingModel Model)> RuntimeAsync(
        ConversationSwitches switches)
    {
        var state = new CommonsStateStore(new MemoryEntityStore());
        await state.SaveOwnerSettingsAsync(new(null, "https://relay.example", "Laurent", [], [], [], "owner-1"));
        await state.SavePublicAgentAsync(new(null, AgentId, "Nova", true, "credential", "active", null,
            switches, [], RestrictedMode: true));
        var relay = new MultiAgentRelayHandler();
        var model = new CountingModel("{\"action\":\"post\",\"content\":\"Try a shorter TTL.\"}");
        var runtime = new PublicAgentRuntime(model, new AgentShareableStores(root),
            new RelayClientFactory(new FixedHttpClientFactory(relay), state),
            new OutboundFilter(new SecretScanner()), state, new NullDigestSink(),
            outboundReview: TestSentinel.Passing(new MemoryEntityStore()));
        return (runtime, relay, model);
    }
}
