using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Leaf.Plugins.LeafCommons;
using Xunit;

namespace Leaf.Plugins.LeafCommons.Tests;

public sealed class WebhookPendingTests
{
    [Fact]
    public async Task First_post_to_a_channel_waits_for_the_host_webhook_and_retries_the_same_body()
    {
        var handler = new SequenceHandler(
            (HttpStatusCode.ServiceUnavailable, new { decision = "rejected", reason = "webhook_pending", retryAfterSeconds = 2 }),
            (HttpStatusCode.ServiceUnavailable, new { decision = "rejected", reason = "webhook_pending", retryAfterSeconds = 2 }),
            (HttpStatusCode.OK, new { decision = "posted", messageId = "m-1", threadId = "t-1" }));
        var waits = new List<TimeSpan>();
        var relay = Relay(handler, waits);

        var result = await relay.OpenAsync("consult", "Workstream feature request", "Hello", idempotencyKey: "key-1");

        Assert.True(result.Accepted);
        Assert.Equal("m-1", result.MessageId);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)], waits);
        Assert.Equal(3, handler.Bodies.Count);
        Assert.All(handler.Bodies, body =>
            Assert.Equal("key-1", JsonDocument.Parse(body).RootElement.GetProperty("idempotencyKey").GetString()));
    }

    [Fact]
    public async Task Webhook_that_never_arrives_still_reports_webhook_pending()
    {
        var pending = (HttpStatusCode.ServiceUnavailable, (object)new { decision = "rejected", reason = "webhook_pending", retryAfterSeconds = 60 });
        var handler = new SequenceHandler(Enumerable.Repeat(pending, 20).ToArray());
        var waits = new List<TimeSpan>();
        var relay = Relay(handler, waits);

        var result = await relay.OpenAsync("consult", "Thread", "Hello");

        Assert.False(result.Accepted);
        Assert.Equal("webhook_pending", result.Code);
        Assert.Equal(13, handler.Bodies.Count);
        Assert.All(waits, wait => Assert.Equal(TimeSpan.FromSeconds(5), wait));
        Assert.Equal(RelayHttpClient.WebhookPendingBudget, TimeSpan.FromTicks(waits.Sum(wait => wait.Ticks)));
    }

    [Fact]
    public async Task A_host_that_takes_most_of_a_minute_still_gets_the_post()
    {
        // The host was stalled for about a minute (2026-10-31); twelve seconds was not enough.
        var pending = (HttpStatusCode.ServiceUnavailable, (object)new { decision = "rejected", reason = "webhook_pending", retryAfterSeconds = 2 });
        var handler = new SequenceHandler([
            .. Enumerable.Repeat(pending, 25),
            (HttpStatusCode.OK, new { decision = "posted", messageId = "m-1", threadId = "t-1" }),
        ]);
        var waits = new List<TimeSpan>();
        var relay = Relay(handler, waits);

        var result = await relay.PostAsync("message:root", "Hello", idempotencyKey: "key-1");

        Assert.True(result.Accepted);
        Assert.Equal(TimeSpan.FromSeconds(50), TimeSpan.FromTicks(waits.Sum(wait => wait.Ticks)));
    }

    [Fact]
    public async Task Other_rejections_are_returned_without_retrying()
    {
        var handler = new SequenceHandler(
            (HttpStatusCode.Forbidden, new { decision = "rejected", reason = "scenario_not_allowed" }));
        var relay = Relay(handler, []);

        var result = await relay.OpenAsync("consult", "Thread", "Hello");

        Assert.Equal("scenario_not_allowed", result.Code);
        Assert.Single(handler.Bodies);
    }

    private static RelayHttpClient Relay(HttpMessageHandler handler, List<TimeSpan> waits)
        => new(new HttpClient(handler),
            _ => Task.FromResult<(Uri, string?)>((new Uri("https://relay.example"), "envoy.secret")),
            (wait, _) => { waits.Add(wait); return Task.CompletedTask; });

    private sealed class SequenceHandler(params (HttpStatusCode Status, object Payload)[] responses) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var (status, payload) = responses[Math.Min(Bodies.Count - 1, responses.Length - 1)];
            return new HttpResponseMessage(status) { Content = JsonContent.Create(payload) };
        }
    }
}
