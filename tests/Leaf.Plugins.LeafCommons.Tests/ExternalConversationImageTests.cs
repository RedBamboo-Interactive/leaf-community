using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Leaf.Sdk.Services;
using SkiaSharp;
using Xunit;

namespace Leaf.Plugins.LeafCommons.Tests;

/// <summary>Images in both directions of a full-mode Public Agent conversation.</summary>
public sealed partial class ExternalConversationTests
{
    private const string AnimatedWebp =
        "UklGRjgBAABXRUJQVlA4WAoAAAACAAAAFwAAFwAAQU5JTQYAAAAAAAAAAABBTk1GrAAAAAAAAAAAABcAABcAAGQAAAJWUDgglAAAANAEAJ0BKhgAGAA+YSaPRaQiIZv0AEAGBLUWYAFSa4nKfxaTAATc/TaSNLLNb+gAAP79bxFYg42pNVBevwKFvfux//JinOJrrHIgnuw2T+akq4Sf7Y3zunG9919Qimrnjta4CcFoRiViCLelT4wev/MZ/9i3L//YazgpgU5OIAl9YYkyHNkq5sJnJTbv48b0Up2PAABBTk1GWAAAAAAAAAAAABcAABcAAGQAAABWUDggQAAAADQDAJ0BKhgAGAA+bTSWR4KAgAAA2JZQDJAoB+AALWkGO1AA/vCbQ//ILlhdcjX/8gP+QH/ID/+QH/6b2anzgAA=";

    [Fact]
    public void Ping_images_are_descriptors_only_and_never_asset_references()
    {
        EnvoyRunRequest? seen = null;
        var core = new ConnectorCore((request, _) => { seen = request; return Task.CompletedTask; });
        using var frame = JsonDocument.Parse("""
            {"type":"ping","id":"ping-images","scenario":"consult","threadRef":"thread-1","content":"Look",
             "sender":{"kind":"human","discordUserId":"h","displayName":"Morgan"},
             "omittedAttachments":2,
             "attachments":[
               {"id":"1","filename":"shot.png","contentType":"image/png","size":271,"width":32,"height":32,
                "url":"https://cdn.discordapp.com/attachments/9/1/shot.png","description":"the log"},
               {"id":"2","name":"secrets.png","mediaType":"image/png","size":5,"sha256":"00",
                "scopeToken":"/api/assets/owner-private.png"}
             ]}
            """);

        Assert.True(core.ProcessAsync(frame.RootElement, _ => Task.CompletedTask).GetAwaiter().GetResult());

        var image = Assert.Single(seen!.Images!);
        Assert.Equal(new RelayImageAttachment("1", "shot.png", "image/png", 271,
            "https://cdn.discordapp.com/attachments/9/1/shot.png", 32, 32, "the log"), image);
        Assert.Equal(3, seen.OmittedImages);
    }

    [Fact]
    public async Task Thread_history_keeps_image_descriptors_and_names_them_for_text_readers()
    {
        var handler = new HistoryHandler("""
            {"messages":[
              {"id":"m1","createdAt":1,"actor":{"kind":"human","displayName":"Morgan"},"content":"Look",
               "attachments":[{"id":"9","filename":"shot.png","contentType":"image/png","size":10,
                 "url":"https://cdn.discordapp.com/attachments/1/9/shot.png"}]},
              {"id":"m2","createdAt":2,"actor":{"kind":"envoy","personaName":"Atlas · North"},"content":"Plain"}
            ],"nextCursor":null}
            """);
        var client = new RelayHttpClient(new HttpClient(handler),
            _ => Task.FromResult((new Uri("https://relay.example"), (string?)"credential")));

        var messages = await client.GetThreadMessagesAsync("thread-1");
        var history = await client.ReadHistoryAsync("thread-1");

        Assert.Equal("shot.png", Assert.Single(messages[0].Images!).Filename);
        Assert.Null(messages[1].Images);
        Assert.Equal("Look\n[image: shot.png]", history[0].Content);
        Assert.Equal("Plain", history[1].Content);
    }

    private sealed class HistoryHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    [Fact]
    public async Task Inbound_image_is_fetched_checked_stored_and_reaches_the_conversation_as_an_image()
    {
        var png = Png(40, 30);
        var cdn = new CdnHandler(new() { ["/attachments/9/1/shot.png"] = (HttpStatusCode.OK, png) });
        var assets = new MemoryAssets();
        var fixture = await CreateAsync(assets: assets, cdn: cdn);

        var result = await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner,
            Request("ping-image", "What is wrong in this screenshot?") with
            {
                Images = [Descriptor("1", "../../C:/Users/me/shot.png", "/attachments/9/1/shot.png")],
            });

        Assert.Equal("admitted", result.State);
        var input = Assert.Single(fixture.Provider.Inputs);
        Assert.Equal("What is wrong in this screenshot?", input.Content);
        var attachment = Assert.Single(input.Attachments);
        Assert.Equal("image/png", attachment.MediaType);
        Assert.Equal("shot.png", attachment.Name);
        Assert.Equal(png.Length, attachment.Size);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant(), attachment.Sha256);
        Assert.StartsWith("/api/assets/stable-", attachment.ScopeToken);
        Assert.EndsWith(".png", attachment.ScopeToken);
        Assert.Equal(png, assets.Files[attachment.ScopeToken["/api/assets/".Length..]].Bytes);
        Assert.False(input.Metadata!.ContainsKey("omitted_attachments"));
    }

    [Fact]
    public async Task Inbound_images_that_fail_a_check_are_left_out_and_counted_never_passed_as_urls()
    {
        var cdn = new CdnHandler(new()
        {
            ["/ok.png"] = (HttpStatusCode.OK, Png(8, 8)),
            ["/redirect.png"] = (HttpStatusCode.Found, []),
            ["/spoofed.png"] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes("<script>not an image</script>")),
            ["/wide.png"] = (HttpStatusCode.OK, Png(4097, 1)),
            ["/animated.webp"] = (HttpStatusCode.OK, Convert.FromBase64String(AnimatedWebp)),
            ["/huge.png"] = (HttpStatusCode.OK, new byte[CommonsImages.MaxImageBytes + 1]),
            ["/missing.png"] = (HttpStatusCode.NotFound, []),
        });
        var assets = new MemoryAssets();
        var fixture = await CreateAsync(assets: assets, cdn: cdn);

        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner,
            Request("ping-bad-images", "Several files.") with
            {
                OmittedImages = 1,
                Images =
                [
                    Descriptor("1", "ok.png", "/ok.png"),
                    Descriptor("2", "elsewhere.png", "/ok.png") with { Url = "https://evil.example/ok.png" },
                    Descriptor("3", "plain.png", "/ok.png") with { Url = "http://cdn.discordapp.com/ok.png" },
                    Descriptor("4", "redirect.png", "/redirect.png"),
                    Descriptor("5", "spoofed.png", "/spoofed.png"),
                    Descriptor("6", "wide.png", "/wide.png"),
                    Descriptor("7", "animated.webp", "/animated.webp"),
                    Descriptor("8", "huge.png", "/huge.png"),
                    Descriptor("9", "missing.png", "/missing.png"),
                ],
            });

        var input = Assert.Single(fixture.Provider.Inputs);
        Assert.Equal("1", Assert.Single(input.Attachments).Id);
        Assert.Equal(9, input.Metadata!["omitted_attachments"]!.GetValue<int>());
        Assert.DoesNotContain(cdn.Requested, path => path.Contains("evil", StringComparison.Ordinal));
        Assert.Single(assets.Files);
        var reasons = (await fixture.State.ListAuditsAsync())
            .Where(item => item.Event == "inbound_image_omitted")
            .Select(item => item.Metadata["reason"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[]
        {
            "image_animated", "image_dimensions_too_large", "image_fetch_failed", "image_redirected",
            "image_too_large", "image_type_not_allowed", "image_url_not_allowed", "image_url_not_allowed",
        }, reasons);
    }

    [Fact]
    public async Task Inbound_images_wait_for_the_sentinel_like_text_when_the_provider_refuses()
    {
        var fixture = await CreateAsync(assets: new MemoryAssets(),
            cdn: new CdnHandler(new() { ["/ok.png"] = (HttpStatusCode.OK, Png(8, 8)) }));
        fixture.Provider.Refuse = true;

        var result = await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner,
            Request("ping-refused-image", "Look.") with { Images = [Descriptor("1", "ok.png", "/ok.png")] });

        Assert.Equal("failed", result.State);
        Assert.Equal("sentinel_unavailable", result.Summary);
        Assert.Single(Assert.Single(fixture.Provider.Inputs).Attachments);
        var notice = Assert.Single(fixture.Relay.Requests, item => item.Route == "POST /v1/posts");
        Assert.EndsWith(PublicAgentConversationCoordinator.SentinelUnavailableNotice, PostedContent(notice.Body));
        Assert.Empty(fixture.Relay.Files);
    }

    [Fact]
    public async Task Settled_reply_images_are_reviewed_with_the_text_and_posted_with_it()
    {
        var png = Png(16, 16);
        var assets = new MemoryAssets();
        assets.Files["chart.png"] = new AssetFile(png, "image/png");
        var fixture = await CreateAsync(assets: assets);
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, Request("ping-1", "Plot it."));
        fixture.Provider.Messages =
        [
            Message(1, "assistant", "text", "Here is the chart.\n![Latency by hour](/api/assets/chart.png)") with
            {
                DeliveryContent = "Here is the chart.",
                OutputAssets = [new("chart.png", "Latency by hour")],
            },
        ];

        await fixture.Provider.NotifyAsync();
        await fixture.Coordinator.WhenIdleAsync();

        var reviewed = Assert.Single(fixture.Sentinel.Requests,
            request => request.Direction == UntrustedContentDirection.Outbound);
        Assert.Equal("Here is the chart.", reviewed.Content);
        Assert.Equal(new UntrustedContentImage("chart.png", "image/png", "Latency by hour"), Assert.Single(reviewed.Images!));
        var index = fixture.Relay.Requests.FindIndex(item => item.Route == "POST /v1/posts");
        using var body = JsonDocument.Parse(fixture.Relay.Requests[index].Body);
        Assert.Equal("Replying to the previous message: Here is the chart.", body.RootElement.GetProperty("content").GetString());
        Assert.Equal("Latency by hour", body.RootElement.GetProperty("images")[0].GetProperty("description").GetString());
        var file = Assert.Single(fixture.Relay.Files[index]);
        Assert.Equal(("files[0]", "image/png"), (file.Name, file.MediaType));
        Assert.Equal(png, file.Bytes);
        var record = await new CommonsExternalConversationStore(fixture.Entities).FindAsync(AgentId, "message:root");
        var verdict = Assert.Single(record!.OutboundReviews!)!.AsObject();
        Assert.Equal("delivered", verdict["outcome"]!.GetValue<string>());
        Assert.Equal(1, verdict["images"]!.GetValue<int>());
        Assert.Equal(1, verdict["images_reviewed"]!.GetValue<int>());
    }

    [Fact]
    public async Task An_image_only_reply_is_one_post_with_no_text_but_the_channel_prefix()
    {
        var assets = new MemoryAssets();
        assets.Files["only.webp"] = new AssetFile(Convert.FromBase64String(StillWebp), "image/webp");
        var fixture = await CreateAsync(assets: assets);
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, Request("ping-1", "Show me."));
        fixture.Provider.Messages =
        [
            Message(1, "assistant", "text", "![](/api/assets/only.webp)") with
            {
                DeliveryContent = "",
                OutputAssets = [new("only.webp", "")],
            },
        ];

        await fixture.Provider.NotifyAsync();
        await fixture.Coordinator.WhenIdleAsync();

        var index = Assert.Single(Enumerable.Range(0, fixture.Relay.Requests.Count),
            item => fixture.Relay.Requests[item].Route == "POST /v1/posts");
        Assert.Equal("image/webp", Assert.Single(fixture.Relay.Files[index]).MediaType);
        using var body = JsonDocument.Parse(fixture.Relay.Requests[index].Body);
        Assert.Equal("Replying to the previous message: ", body.RootElement.GetProperty("content").GetString());
        Assert.False(body.RootElement.GetProperty("images")[0].TryGetProperty("description", out _));
    }

    [Fact]
    public async Task A_held_image_reply_posts_only_the_withheld_notice()
    {
        var assets = new MemoryAssets();
        assets.Files["badge.png"] = new AssetFile(Png(8, 8), "image/png");
        var fixture = await CreateAsync(assets: assets);
        // The text alone is innocuous; the verdict on the image is what holds the reply.
        fixture.Sentinel.Decide = request => request.Images is { Count: > 0 } ? TestSentinel.Leak : TestSentinel.Clean;
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, Request("ping-1", "Show your badge."));
        fixture.Provider.Messages =
        [
            Message(1, "assistant", "text", "Here it is.") with { OutputAssets = [new("badge.png", "My badge")] },
        ];

        await fixture.Provider.NotifyAsync();
        await fixture.Coordinator.WhenIdleAsync();

        var post = Assert.Single(fixture.Relay.Requests, item => item.Route == "POST /v1/posts");
        Assert.EndsWith(CommonsOutboundReview.WithheldNotice, PostedContent(post.Body));
        Assert.DoesNotContain("Here it is", post.Body);
        Assert.Empty(fixture.Relay.Files);
        Assert.Contains(await fixture.State.ListDigestsAsync(), digest => digest.Type == "owner_notice");
    }

    [Fact]
    public async Task Require_private_approval_on_an_image_holds_the_reply()
    {
        var assets = new MemoryAssets();
        assets.Files["map.png"] = new AssetFile(Png(8, 8), "image/png");
        var fixture = await CreateAsync(assets: assets);
        fixture.Sentinel.Decide = _ => new("low", ["location"], "require_private_approval", true,
            UntrustedContentIsolation.VerifiedToolFree);
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, Request("ping-1", "Where?"));
        fixture.Provider.Messages =
        [
            Message(1, "assistant", "text", "Here.") with { OutputAssets = [new("map.png", "")] },
        ];

        await fixture.Provider.NotifyAsync();
        await fixture.Coordinator.WhenIdleAsync();

        Assert.EndsWith(CommonsOutboundReview.WithheldNotice, PostedContent(
            Assert.Single(fixture.Relay.Requests, item => item.Route == "POST /v1/posts").Body));
        Assert.Empty(fixture.Relay.Files);
    }

    [Theory]
    [InlineData("older-reviewer")]
    [InlineData("no-sentinel")]
    [InlineData("unavailable")]
    public async Task An_image_reply_without_a_verdict_that_saw_every_image_is_retried_then_withheld(string mode)
    {
        var assets = new MemoryAssets();
        assets.Files["chart.png"] = new AssetFile(Png(8, 8), "image/png");
        var fixture = await CreateAsync(assets: assets, noSentinel: mode == "no-sentinel");
        if (mode == "older-reviewer") fixture.Sentinel.EchoImages = false;
        if (mode == "unavailable") fixture.Sentinel.Decide = _ => UntrustedContentVerdict.Unavailable();
        var coordinator = fixture.Coordinator;
        await coordinator.SubmitAsync(fixture.Agent, fixture.Owner, Request("ping-1", "Plot it."));
        fixture.Provider.Messages =
        [
            Message(1, "assistant", "text", "Here is the chart.") with { OutputAssets = [new("chart.png", "")] },
        ];

        await fixture.Provider.NotifyAsync();
        await coordinator.WhenIdleAsync();
        for (var attempt = 0; attempt < 10 && !fixture.Relay.Requests.Any(item => item.Route == "POST /v1/posts"); attempt++)
        {
            await Task.Delay(50);
            await coordinator.WhenIdleAsync();
        }

        var post = Assert.Single(fixture.Relay.Requests, item => item.Route == "POST /v1/posts");
        Assert.EndsWith(CommonsOutboundReview.UnavailableNotice, PostedContent(post.Body));
        Assert.DoesNotContain("Here is the chart", post.Body);
        Assert.Empty(fixture.Relay.Files);
        if (mode != "no-sentinel")
        {
            var asked = fixture.Sentinel.Requests.Where(request => request.Direction == UntrustedContentDirection.Outbound).ToArray();
            Assert.Equal(PublicAgentConversationCoordinator.MaxOutboundReviewAttempts, asked.Length);
            // Every re-ask still carries the image: it is never reviewed as text alone.
            Assert.All(asked, request => Assert.Single(request.Images!));
        }
    }

    [Fact]
    public async Task An_unreadable_reviewed_asset_fails_the_reply_without_posting_it()
    {
        var fixture = await CreateAsync(assets: new MemoryAssets());
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, Request("ping-1", "Plot it."));
        fixture.Provider.Messages =
        [
            Message(1, "assistant", "text", "Here is the chart.") with { OutputAssets = [new("gone.png", "")] },
        ];

        await fixture.Provider.NotifyAsync();
        await fixture.Coordinator.WhenIdleAsync();

        var post = Assert.Single(fixture.Relay.Requests, item => item.Route == "POST /v1/posts");
        Assert.DoesNotContain("Here is the chart", post.Body);
        Assert.Contains(await fixture.State.ListAuditsAsync(), item => item.Event == "envoy_failed"
            && item.Metadata["reason"]!.GetValue<string>() == "image_unreadable");
    }

    [Fact]
    public async Task A_text_only_reply_is_still_a_json_post_reviewed_without_images()
    {
        var fixture = await CreateAsync(assets: new MemoryAssets());
        await fixture.Coordinator.SubmitAsync(fixture.Agent, fixture.Owner, Request("ping-1", "Explain."));
        fixture.Provider.Messages = [Message(1, "assistant", "text", "Plain words.")];

        await fixture.Provider.NotifyAsync();
        await fixture.Coordinator.WhenIdleAsync();

        Assert.Null(Assert.Single(fixture.Sentinel.Requests,
            request => request.Direction == UntrustedContentDirection.Outbound).Images);
        var post = Assert.Single(fixture.Relay.Requests, item => item.Route == "POST /v1/posts");
        using var body = JsonDocument.Parse(post.Body);
        Assert.False(body.RootElement.TryGetProperty("images", out _));
        Assert.Empty(fixture.Relay.Files);
    }

    private const string StillWebp = "UklGRjAAAABXRUJQVlA4TCMAAAAvD8ADEJkyRPQ/NhHR/wCRtk0t3L/gwdOJGMBJngCWOkD9BwA=";

    private static string PostedContent(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("content").GetString() ?? "";
    }

    private static RelayImageAttachment Descriptor(string id, string filename, string path)
        => new(id, filename, "image/png", 100, "https://cdn.discordapp.com" + path);

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(new SKColor(40, 120, 200));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed class CdnHandler(Dictionary<string, (HttpStatusCode Status, byte[] Body)> files) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!.ToString());
            var (status, body) = files.TryGetValue(request.RequestUri.AbsolutePath, out var file)
                ? file : (HttpStatusCode.NotFound, Array.Empty<byte>());
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
            if (status == HttpStatusCode.Found) response.Headers.Location = new Uri("https://evil.example/elsewhere.png");
            return Task.FromResult(response);
        }
    }
}

/// <summary>An Asset store in memory that keeps idempotent uploads under the kernel's stable ids.</summary>
internal sealed class MemoryAssets : IAssets
{
    public Dictionary<string, AssetFile> Files { get; } = new(StringComparer.Ordinal);

    public Task<AssetRef> UploadAsync(Stream content, string fileName, string? contentType = null, CancellationToken ct = default)
        => throw new NotSupportedException();

    public async Task<AssetRef> UploadIdempotentAsync(Stream content, string stableKey, string fileName,
        string? contentType = null, CancellationToken ct = default)
    {
        var id = "stable-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stableKey))).ToLowerInvariant()
            + Path.GetExtension(fileName);
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        Files[id] = new AssetFile(buffer.ToArray(), contentType ?? "application/octet-stream");
        return new AssetRef(id, GetUrl(id));
    }

    public string GetUrl(string assetId) => $"/api/assets/{assetId}";

    public Task<AssetFile?> ReadAsync(string assetId, CancellationToken ct = default)
        => Task.FromResult(Files.GetValueOrDefault(assetId));
}
