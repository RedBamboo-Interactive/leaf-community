using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Leaf.Plugins.LeafCommons.Tests;

/// <summary>
/// The image chain against a real relay Worker under <c>wrangler dev --local</c>. Runs only when
/// <c>tests/e2e-images.mjs</c> starts the relay and sets <c>LEAF_COMMONS_E2E_RELAY</c>; Discord
/// is that script's local mock and the Discord CDN is simulated here.
/// </summary>
public sealed partial class ExternalConversationTests
{
    [Fact]
    public async Task Live_relay_images_flow_through_the_connector_both_ways()
    {
        var relayUrl = Environment.GetEnvironmentVariable("LEAF_COMMONS_E2E_RELAY");
        if (string.IsNullOrWhiteSpace(relayUrl)) return;
        var credential = Environment.GetEnvironmentVariable("LEAF_COMMONS_E2E_CREDENTIAL")!;
        var hostCredential = Environment.GetEnvironmentVariable("LEAF_COMMONS_E2E_HOST")!;
        var envoyId = Environment.GetEnvironmentVariable("LEAF_COMMONS_E2E_ENVOY")!;
        var fixtures = Environment.GetEnvironmentVariable("LEAF_COMMONS_E2E_FIXTURES")!;
        var photo = await File.ReadAllBytesAsync(Path.Combine(fixtures, "photo-exif-gps.jpg"));
        var chart = await File.ReadAllBytesAsync(Path.Combine(fixtures, "text-chunks.png"));
        const string cdnPath = "/attachments/9/777/IMG_0001.jpg";

        var assets = new MemoryAssets();
        assets.Files["reply-chart.png"] = new AssetFile(chart, "image/png");
        var fixture = await CreateAsync(assets: assets, relayBaseUrl: relayUrl, credential: credential,
            liveRelay: new HttpClientHandler(),
            cdn: new CdnHandler(new() { [cdnPath] = (HttpStatusCode.OK, photo) }));

        // 1. The connector's socket receives a human's image as a relay descriptor.
        var received = new TaskCompletionSource<EnvoyRunRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var core = new ConnectorCore((request, _) =>
        {
            if (request.Images is { Count: > 0 }) received.TrySetResult(request);
            return Task.CompletedTask;
        });
        await using var connector = new RelayConnector(_ => Task.FromResult((new Uri(relayUrl), (string?)credential)),
            core, (status, _) => { if (status == "active") connected.TrySetResult(); return Task.CompletedTask; },
            NullLogger<RelayConnector>.Instance);
        await connector.StartAsync(CancellationToken.None);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(15));

        using var host = new HttpClient { BaseAddress = new Uri(relayUrl) };
        host.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", hostCredential);
        using var ingest = await host.PostAsJsonAsync("/v1/ingest", new
        {
            type = "human_message", eventId = "e2e-human-image", authorId = "human-1", displayName = "Morgan",
            content = "Nova, what is in this photo?", mentions = new[] { envoyId }, mentionRoles = Array.Empty<string>(),
            channel = "nova-consult", threadRef = "e2e-image-thread", threadName = "Photo question",
            attachments = new[]
            {
                new { id = "777", filename = "IMG_0001.jpg", contentType = "image/jpeg", size = photo.Length,
                    width = 64, height = 48, url = "https://cdn.discordapp.com" + cdnPath },
            },
        });
        Assert.Equal(HttpStatusCode.OK, ingest.StatusCode);
        var ping = await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("e2e-image-thread", ping.ThreadRef);
        Assert.Equal("777", Assert.Single(ping.Images!).Id);

        // 2. The coordinator brings the image into Assets and hands it to the provider as an image.
        Assert.Equal("admitted", (await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, ping)).State);
        var attachment = Assert.Single(Assert.Single(fixture.Provider.Inputs).Attachments);
        Assert.Equal("image/jpeg", attachment.MediaType);
        Assert.Equal(photo, assets.Files[attachment.ScopeToken["/api/assets/".Length..]].Bytes);

        // 3. The settled reply's image is reviewed with its text and posted through the Worker.
        fixture.Provider.Messages =
        [
            Message(1, "assistant", "text", "It is a gradient test card. Here is my chart.") with
            {
                OutputAssets = [new("reply-chart.png", "A test chart")],
            },
        ];
        await fixture.Provider.NotifyAsync();
        await fixture.Coordinator.WhenIdleAsync();
        var reviewed = Assert.Single(fixture.Sentinel.Requests, request => request.Direction == UntrustedContentDirection.Outbound);
        Assert.Single(reviewed.Images!);
        var posts = (await fixture.State.ListAuditsAsync()).Where(item => item.Event == "relay_post").ToArray();
        var posted = Assert.Single(posts, item => item.Metadata["delivery"]?.GetValue<string>() == "settled-final");
        Assert.True(posted.Metadata["accepted"]!.GetValue<bool>(), posted.Metadata.ToJsonString());
        Assert.Equal(1, posted.Metadata["images"]!.GetValue<int>());
    }
}
