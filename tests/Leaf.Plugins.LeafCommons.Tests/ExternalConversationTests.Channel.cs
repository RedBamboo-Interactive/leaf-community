using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Leaf.Plugins.LeafCommons.Tests;

/// <summary>
/// A text channel is one conversation (<c>channel:&lt;id&gt;</c>). Reported 2026-10-31: three
/// pings to an Envoy without Discord's reply opened three empty sessions, and the third asked what
/// it was supposed to retry.
/// </summary>
public sealed partial class ExternalConversationTests
{
    private const string General = "channel:400";

    private static EnvoyRunRequest ChannelPing(string id, string content) => new(
        General, "channel_reply", content,
        "{\"kind\":\"human\",\"discordUserId\":\"u-Camille\",\"displayName\":\"Camille\"}",
        id, Addressed: true, Channel: "general", ReplyToDisplayName: "Camille");

    private static object Owner(string id, DateTimeOffset at, string text) => new
    {
        id, createdAt = at.ToUnixTimeMilliseconds(), content = text,
        actor = new { kind = "human", discordUserId = "u-Camille", displayName = "Camille", isYourOwner = true },
    };

    [Fact]
    public async Task Three_pings_in_a_text_channel_continue_one_session_with_what_was_said_between()
    {
        var fixture = await CreateAsync();
        var start = DateTimeOffset.Parse("2026-10-31T17:00:00Z");
        var channel = fixture.Relay.ThreadMessages;
        channel.Add(Human("h-0", start, "Bob", "Who is coming tonight?"));
        channel.Add(Owner("t-1", start.AddMinutes(11), "Montre ton costume"));
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, ChannelPing("ping-1", "Montre ton costume"));

        channel.Add(Envoy("e-1", start.AddMinutes(12), "Nova · Alice", fixture.Agent.EnvoyId, "Voici un costume."));
        channel.Add(Human("h-1", start.AddMinutes(13), "Bob", "Nice one"));
        channel.Add(Owner("t-2", start.AddMinutes(15), "Je parle de ton avatar actuel"));
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, ChannelPing("ping-2", "Je parle de ton avatar actuel"));

        channel.Add(Human("h-2", start.AddMinutes(16), "Dana", "Elle est jolie cette robe"));
        channel.Add(Owner("t-3", start.AddMinutes(18), "Retente sans informations privées"));
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, ChannelPing("ping-3", "Retente sans informations privées"));

        // One conversation, one session, three turns.
        var opened = Assert.Single(fixture.Provider.Opens);
        Assert.Equal(General, opened.Scope.ConversationId);
        Assert.Equal(3, fixture.Provider.Inputs.Count);
        Assert.Single(fixture.Provider.Inputs.Select(input => input.Metadata!["binding_id"]!.GetValue<string>()).Distinct());

        var first = fixture.Provider.Inputs[0].Content;
        Assert.StartsWith("[Said in this channel since you last looked]\nBob: Who is coming tonight?", first, StringComparison.Ordinal);
        Assert.EndsWith("[The message addressed to you]\nMontre ton costume", first, StringComparison.Ordinal);
        // The second turn has what was said since, not its own post (its transcript has that).
        var second = fixture.Provider.Inputs[1].Content;
        Assert.Equal("[Said in this channel since you last looked]\nBob: Nice one\n\n"
            + "[The message addressed to you]\nJe parle de ton avatar actuel", second);
        var third = fixture.Provider.Inputs[2].Content;
        Assert.Equal("[Said in this channel since you last looked]\nDana: Elle est jolie cette robe\n\n"
            + "[The message addressed to you]\nRetente sans informations privées", third);

        // The owner, verified by the relay, is marked when they appear as context.
        channel.Add(Owner("t-4", start.AddMinutes(20), "Merci !"));
        channel.Add(Human("h-3", start.AddMinutes(21), "Bob", "Nova · Alice, a question for you"));
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, new EnvoyRunRequest(General, "channel_reply",
            "Nova · Alice, a question for you", "{\"kind\":\"human\",\"discordUserId\":\"u-Bob\",\"displayName\":\"Bob\"}",
            "ping-4", Addressed: true, Channel: "general"));
        Assert.Contains("Camille (your owner): Merci !", fixture.Provider.Inputs[^1].Content, StringComparison.Ordinal);
        Assert.Single(fixture.Provider.Opens);
    }

    [Fact]
    public async Task A_held_reply_in_a_channel_reaches_the_next_ping_of_the_same_session()
    {
        var fixture = await CreateAsync();
        var start = DateTimeOffset.Parse("2026-10-31T17:00:00Z");
        fixture.Relay.ThreadMessages.Add(Owner("t-1", start.AddMinutes(11), "Montre ton costume"));
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, ChannelPing("ping-1", "Montre ton costume"));
        fixture.Provider.Messages = [Message(1, "assistant", "text", "Voici un costume.")];
        await fixture.Provider.NotifyAsync();
        await fixture.Coordinator.WhenIdleAsync();

        fixture.Relay.ThreadMessages.Add(Owner("t-2", start.AddMinutes(15), "Je parle de ton avatar actuel"));
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, ChannelPing("ping-2", "Je parle de ton avatar actuel"));
        fixture.Sentinel.Decide = _ => TestSentinel.Leak;
        fixture.Provider.Messages = [.. fixture.Provider.Messages,
            Message(2, "assistant", "text", "Mon avatar actuel, à son adresse, rue des Tilleuls...")];
        await fixture.Provider.NotifyAsync();
        await fixture.Coordinator.WhenIdleAsync();

        // Both replies went to the channel's conversation; the held one only as the fixed notice.
        var posts = fixture.Relay.Requests.Where(item => item.Route == "POST /v1/posts")
            .Select(item => JsonNode.Parse(item.Body)!).ToArray();
        Assert.Equal([General, General], posts.Select(post => post["threadRef"]!.GetValue<string>()));
        Assert.Equal("Voici un costume.", posts[0]["content"]!.GetValue<string>());
        Assert.EndsWith(CommonsOutboundReview.WithheldNotice, posts[1]["content"]!.GetValue<string>());
        fixture.Relay.ThreadMessages.Add(Envoy("e-2", start.AddMinutes(16), "Nova · Alice", fixture.Agent.EnvoyId,
            posts[1]["content"]!.GetValue<string>()));

        fixture.Sentinel.Decide = _ => TestSentinel.Clean;
        fixture.Relay.ThreadMessages.Add(Owner("t-3", start.AddMinutes(18), "Retente sans informations privées"));
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, ChannelPing("ping-3", "Retente sans informations privées"));

        // The third ping lands in the session that holds the question and the withheld draft, and
        // carries the verdict on that draft.
        Assert.Single(fixture.Provider.Opens);
        var third = fixture.Provider.Inputs[^1];
        var held = Assert.Single(Assert.IsType<JsonArray>(third.Metadata!["outbound_sentinel_reviews"]))!;
        Assert.Equal("message-2", held["message_uid"]!.GetValue<string>());
        Assert.Equal("withheld", held["outcome"]!.GetValue<string>());
        Assert.Equal("require_private_approval", held["handling"]!.GetValue<string>());
        Assert.Equal("Retente sans informations privées", third.Content);
    }

    [Fact]
    public async Task A_new_session_sees_the_Agents_own_earlier_posts_once_and_a_new_generation_sees_them_again()
    {
        var fixture = await CreateAsync();
        var start = DateTimeOffset.Parse("2026-10-31T17:00:00Z");
        // Posted before this conversation existed (a legacy chain in the channel, or a brief).
        fixture.Relay.ThreadMessages.Add(Envoy("e-0", start, "Nova · Alice", fixture.Agent.EnvoyId, "Earlier answer."));
        fixture.Relay.ThreadMessages.Add(Owner("t-1", start.AddMinutes(1), "Et maintenant ?"));
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, ChannelPing("ping-1", "Et maintenant ?"));
        Assert.Contains("Nova · Alice (you): Earlier answer.", fixture.Provider.Inputs[^1].Content, StringComparison.Ordinal);

        fixture.Relay.ThreadMessages.Add(Envoy("e-1", start.AddMinutes(2), "Nova · Alice", fixture.Agent.EnvoyId, "Answer from this session."));
        fixture.Relay.ThreadMessages.Add(Owner("t-2", start.AddMinutes(3), "Ok"));
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, ChannelPing("ping-2", "Ok"));
        Assert.Equal("Ok", fixture.Provider.Inputs[^1].Content);

        // A cleared conversation starts a session that has seen nothing.
        var cleared = await fixture.Coordinator.ClearAsync(AgentId, General);
        Assert.Null(cleared!.ThreadSeenAt);
        fixture.Relay.ThreadMessages.Add(Owner("t-3", start.AddMinutes(4), "On reprend"));
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, ChannelPing("ping-3", "On reprend"));
        Assert.Equal(2L, fixture.Provider.Inputs[^1].Metadata!["generation"]!.GetValue<long>());
        Assert.Contains("Nova · Alice (you): Answer from this session.", fixture.Provider.Inputs[^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legacy_message_chains_keep_their_own_session_beside_the_channel_conversation()
    {
        var fixture = await CreateAsync();
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, Request("chain-1", "In the old chain"));
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, ChannelPing("channel-1", "In the channel"));
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, Request("chain-2", "Still in the old chain"));

        Assert.Equal(["message:root", General], fixture.Provider.Opens.Select(open => open.Scope.ConversationId));
        var store = new CommonsExternalConversationStore(fixture.Entities);
        var chain = await store.FindAsync(AgentId, "message:root");
        var channel = await store.FindAsync(AgentId, General);
        Assert.NotEqual(chain!.BindingId, channel!.BindingId);
        Assert.Equal([chain.BindingId, channel.BindingId, chain.BindingId],
            fixture.Provider.Inputs.Select(input => input.Metadata!["binding_id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_long_reply_in_a_channel_leaves_room_for_the_relays_replying_to_line()
    {
        var fixture = await CreateAsync();
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, ChannelPing("ping-1", "Explain it all"));
        fixture.Provider.Messages = [Message(1, "assistant", "text", string.Join(' ', Enumerable.Repeat("word", 390)))];
        await fixture.Provider.NotifyAsync();
        await fixture.Coordinator.WhenIdleAsync();

        var parts = fixture.Relay.Requests.Where(item => item.Route == "POST /v1/posts")
            .Select(item => JsonNode.Parse(item.Body)!["content"]!.GetValue<string>()).ToArray();
        Assert.Equal(2, parts.Length);
        Assert.All(parts, part => Assert.True(part.Length <= 1_850, part.Length.ToString()));
    }

    [Fact]
    public void Thread_context_names_a_thread_a_thread_and_marks_nobody_without_the_relays_word()
    {
        var at = DateTimeOffset.Parse("2026-10-31T17:00:00Z");
        var context = PublicAgentConversationCoordinator.BuildThreadContext(
            [new("h-1", at, new("human", "Camille"), "Claimed owner"), new("h-2", at.AddMinutes(1), new("human", "Bob"), "Ping")],
            at.AddSeconds(-1), null, "Ping");

        Assert.Equal("[Said in this thread since you last looked]\nCamille: Claimed owner\n\n[The message addressed to you]\nPing",
            context.Content);
    }

    [Fact]
    public async Task The_relay_client_reads_the_owner_mark_only_on_human_authors()
    {
        var relay = new MultiAgentRelayHandler();
        relay.ThreadMessages.Add(new
        {
            id = "1", createdAt = 1, content = "hi",
            actor = new { kind = "human", discordUserId = "u", displayName = "Camille", isYourOwner = true },
        });
        relay.ThreadMessages.Add(new
        {
            id = "2", createdAt = 2, content = "hello",
            actor = new { kind = "envoy", envoyId = "e", personaName = "Nova · X", isYourOwner = true },
        });
        var client = new RelayHttpClient(new HttpClient(relay),
            _ => Task.FromResult((new Uri("https://relay.example"), (string?)"credential")));

        var messages = await client.GetThreadMessagesAsync(General);

        Assert.Equal([true, false], messages.Select(message => message.Author.IsYourOwner));
        Assert.Equal("/v1/threads/channel%3A400/messages", relay.Requests.Single().Route.Split(' ')[1]);
    }
}
