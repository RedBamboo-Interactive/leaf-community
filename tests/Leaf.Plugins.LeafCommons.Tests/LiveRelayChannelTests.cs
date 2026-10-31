using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.LeafCommons.Tests;

/// <summary>
/// The reported text-channel sequence against a real relay Worker under <c>wrangler dev --local</c>: three
/// pings that reply to nothing, chatter between them, the second answer held by the outbound
/// Sentinel. Runs only when <c>tests/e2e-channel.mjs</c> starts the relay and sets
/// <c>LEAF_COMMONS_E2E_RELAY</c>; Discord is that script's local mock.
/// </summary>
public sealed partial class ExternalConversationTests
{
    [Fact]
    public async Task Live_relay_text_channel_pings_reach_one_session_with_the_channel_between()
    {
        var relayUrl = Environment.GetEnvironmentVariable("LEAF_COMMONS_E2E_RELAY");
        if (string.IsNullOrWhiteSpace(relayUrl)) return;
        var credential = Environment.GetEnvironmentVariable("LEAF_COMMONS_E2E_CREDENTIAL")!;
        var hostCredential = Environment.GetEnvironmentVariable("LEAF_COMMONS_E2E_HOST")!;
        var envoyId = Environment.GetEnvironmentVariable("LEAF_COMMONS_E2E_ENVOY")!;
        var ownerId = Environment.GetEnvironmentVariable("LEAF_COMMONS_E2E_OWNER")!;

        var fixture = await CreateAsync(relayBaseUrl: relayUrl, credential: credential, liveRelay: new HttpClientHandler());
        var agent = await fixture.State.SavePublicAgentAsync(fixture.Agent with { EnvoyId = envoyId });

        var pings = Channel.CreateUnbounded<EnvoyRunRequest>();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var core = new ConnectorCore((request, _) => pings.Writer.WriteAsync(request).AsTask());
        await using var connector = new RelayConnector(_ => Task.FromResult((new Uri(relayUrl), (string?)credential)),
            core, (status, _) => { if (status == "active") connected.TrySetResult(); return Task.CompletedTask; },
            NullLogger<RelayConnector>.Instance);
        await connector.StartAsync(CancellationToken.None);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(15));

        using var host = new HttpClient { BaseAddress = new Uri(relayUrl) };
        host.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", hostCredential);
        var sequence = 0;
        async Task SayAsync(string author, string name, string content, bool addressed)
        {
            using var response = await host.PostAsJsonAsync("/v1/ingest", new
            {
                type = "human_message", eventId = $"e2e-channel-{++sequence}", authorId = author, displayName = name,
                content, mentions = addressed ? new[] { envoyId } : [], mentionRoles = Array.Empty<string>(),
                channel = "general", channelId = "general",
            });
            response.EnsureSuccessStatusCode();
        }
        async Task<EnvoyRunRequest> PingAsync(string content)
        {
            await SayAsync(ownerId, "Camille", content, addressed: true);
            var ping = await pings.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("admitted", (await fixture.Coordinator.SubmitAsync(agent, fixture.Owner, ping)).State);
            return ping;
        }
        async Task SettleAsync(long sequenceNumber, string text)
        {
            fixture.Provider.Messages = [.. fixture.Provider.Messages, Message(sequenceNumber, "assistant", "text", text)];
            await fixture.Provider.NotifyAsync();
            await fixture.Coordinator.WhenIdleAsync();
        }

        await SayAsync("u-bob", "Bob", "Who is coming tonight?", addressed: false);
        var first = await PingAsync("Montre ton costume");
        await SettleAsync(1, "Voici un nouveau costume.");

        await SayAsync("u-bob", "Bob", "Nice one", addressed: false);
        var second = await PingAsync("Je parle de ton avatar actuel");
        fixture.Sentinel.Decide = _ => TestSentinel.Leak;
        await SettleAsync(2, "Mon avatar actuel, chez mon owner...");
        fixture.Sentinel.Decide = _ => TestSentinel.Clean;

        await SayAsync("u-dana", "Dana", "Elle est jolie cette robe", addressed: false);
        var third = await PingAsync("Retente sans informations privées");

        Assert.Equal(["channel:general", "channel:general", "channel:general"],
            new[] { first, second, third }.Select(ping => ping.ThreadRef));
        Assert.Single(fixture.Provider.Opens);
        var inputs = fixture.Provider.Inputs;
        Assert.Equal(3, inputs.Count);
        Assert.Equal("[Said in this channel since you last looked]\nBob: Who is coming tonight?\n\n"
            + "[The message addressed to you]\nMontre ton costume", inputs[0].Content);
        Assert.Equal("[Said in this channel since you last looked]\nBob: Nice one\n\n"
            + "[The message addressed to you]\nJe parle de ton avatar actuel", inputs[1].Content);
        Assert.Equal("[Said in this channel since you last looked]\nDana: Elle est jolie cette robe\n\n"
            + "[The message addressed to you]\nRetente sans informations privées", inputs[2].Content);
        var held = Assert.Single(Assert.IsType<JsonArray>(inputs[2].Metadata!["outbound_sentinel_reviews"]))!;
        Assert.Equal(("message-2", "withheld"),
            (held["message_uid"]!.GetValue<string>(), held["outcome"]!.GetValue<string>()));

        // The owner's own words in the window carry the relay's verified mark.
        var window = await new RelayHttpClient(new HttpClient(),
            _ => Task.FromResult((new Uri(relayUrl), (string?)credential))).GetThreadMessagesAsync("channel:general");
        Assert.All(window.Where(message => message.Author.Name == "Camille"), message => Assert.True(message.Author.IsYourOwner));
        Assert.All(window.Where(message => message.Author.Name != "Camille"), message => Assert.False(message.Author.IsYourOwner));
        var posts = (await fixture.State.ListAuditsAsync()).Where(item => item.Event == "relay_post").ToArray();
        Assert.Equal(2, posts.Length);
        Assert.All(posts, post => Assert.True(post.Metadata["accepted"]!.GetValue<bool>(), post.Metadata.ToJsonString()));
    }
}
