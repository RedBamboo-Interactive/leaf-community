using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.LeafCommons;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using SkiaSharp;
using Xunit;

namespace Leaf.Plugins.LeafCommons.Tests;

/// <summary>An Outfit marked nsfw never becomes a public Envoy avatar.</summary>
public sealed class AvatarNsfwTests
{
    private const string AgentId = "a7b059ac-f678-8b58-819b-1bcf3acb22db";
    private static readonly Guid OutfitId = Guid.Parse("d0da69ca-8d2a-4a6d-8110-1e77fe7bf2dc");
    private const string AgentAsset = "c70791db-dd7f-4ed1-beab-6ac2f891af50.png";
    private const string OutfitAsset = "8b4771d3-16e6-4371-9ae9-5812439bc8df.png";

    [Theory]
    [InlineData("true", true)]
    [InlineData("\"true\"", true)]
    [InlineData("\"True\"", true)]
    [InlineData("\"TRUE\"", true)]
    [InlineData("false", false)]
    [InlineData("\"false\"", false)]
    [InlineData("\"yes\"", false)]
    [InlineData("\"1\"", false)]
    [InlineData("1", false)]
    [InlineData("null", false)]
    [InlineData(null, false)]
    public void Nsfw_is_read_as_Novas_public_self_context_reads_it(string? json, bool expected)
    {
        var data = new JsonObject();
        if (json is not null) data["nsfw"] = JsonNode.Parse(json);
        Assert.Equal(expected, AvatarSources.IsNsfw(data));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("\"true\"")]
    [InlineData("\"True\"")]
    public async Task An_nsfw_outfit_is_never_read_and_the_Agent_avatar_stands_in(string nsfw)
    {
        var world = await WorldAsync(JsonNode.Parse(nsfw));

        var resolved = await world.Source.ResolveAsync(AvatarSources.Outfit, AgentId);

        Assert.Equal(world.AgentImage, resolved.Bytes);
        Assert.Equal(AgentAsset, resolved.AssetId);
        Assert.Equal(AgentId, resolved.SourceReference);
        Assert.Equal("outfit_nsfw", resolved.FallbackReason);
        Assert.DoesNotContain(OutfitAsset, world.Assets.Reads);
    }

    [Fact]
    public async Task Without_an_Agent_avatar_an_nsfw_outfit_resolves_to_nothing()
    {
        var world = await WorldAsync(JsonValue.Create("True"), agentAvatar: false);

        var failure = await Assert.ThrowsAsync<AvatarSyncException>(
            () => world.Source.ResolveAsync(AvatarSources.Outfit, AgentId));

        Assert.Equal("outfit_nsfw", failure.Reason);
        Assert.DoesNotContain(OutfitAsset, world.Assets.Reads);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("\"false\"")]
    [InlineData(null)]
    public async Task A_safe_outfit_is_still_the_avatar(string? nsfw)
    {
        var world = await WorldAsync(nsfw is null ? null : JsonNode.Parse(nsfw));

        var resolved = await world.Source.ResolveAsync(AvatarSources.Outfit, AgentId);

        Assert.Equal(world.OutfitImage, resolved.Bytes);
        Assert.Equal(OutfitAsset, resolved.AssetId);
        Assert.Null(resolved.FallbackReason);
    }

    [Fact]
    public async Task Marking_the_worn_outfit_nsfw_republishes_the_Agent_avatar_and_says_why()
    {
        var world = await WorldAsync(nsfw: null);
        var (store, relay, audit, events, sync) = await PublicSyncAsync(world);

        // A safe outfit is published as before.
        Assert.True((await sync.SyncAsync(AgentId)).Uploaded);
        Assert.Equal([Processed(world.OutfitImage)], relay.Uploads.Select(Sha));
        var settings = (await store.GetPublicAgentAsync(AgentId))!;
        Assert.Equal(("synced", (string?)null), (settings.AvatarSyncStatus, settings.AvatarSyncReason));

        // The owner marks it nsfw in place: that outfit edit alone replaces it with the Agent avatar.
        await world.Entities.PatchAsync(OutfitId, new JsonObject { ["nsfw"] = "True" });
        await events.EmitAsync("entity.updated", new JsonObject { ["typeSlug"] = "outfit", ["id"] = OutfitId.ToString() });
        Assert.Equal(2, relay.Uploads.Count);
        Assert.Equal(Processed(world.AgentImage), Sha(relay.Uploads[1]));
        settings = (await store.GetPublicAgentAsync(AgentId))!;
        Assert.Equal(("synced", (string?)"outfit_nsfw", (string?)AgentAsset),
            (settings.AvatarSyncStatus, settings.AvatarSyncReason, settings.AvatarResolvedAssetId));
        Assert.Contains(audit.Events, item => item.Name == "avatar_synced"
            && Equals(item.Data["fallback_reason"], "outfit_nsfw"));

        // Selecting it again keeps the safe avatar already published; the reason stays visible.
        await events.EmitAsync("agent.avatar-changed", new JsonObject { ["agentId"] = AgentId });
        Assert.Equal(2, relay.Uploads.Count);
        settings = (await store.GetPublicAgentAsync(AgentId))!;
        Assert.Equal(("current", (string?)"outfit_nsfw"), (settings.AvatarSyncStatus, settings.AvatarSyncReason));

        // Unmarked, the outfit is the avatar again and the reason clears.
        await world.Entities.PatchAsync(OutfitId, new JsonObject { ["nsfw"] = false });
        await events.EmitAsync("entity.updated", new JsonObject { ["typeSlug"] = "outfit", ["id"] = OutfitId.ToString() });
        Assert.Equal(3, relay.Uploads.Count);
        Assert.Equal(Processed(world.OutfitImage), Sha(relay.Uploads[2]));
        settings = (await store.GetPublicAgentAsync(AgentId))!;
        Assert.Equal(("synced", (string?)null), (settings.AvatarSyncStatus, settings.AvatarSyncReason));
    }

    [Fact]
    public async Task An_nsfw_outfit_with_no_Agent_avatar_uploads_nothing_and_says_why()
    {
        var world = await WorldAsync(JsonValue.Create(true), agentAvatar: false);
        var (store, relay, audit, _, sync) = await PublicSyncAsync(world);

        var failure = await Assert.ThrowsAsync<AvatarSyncException>(() => sync.SyncAsync(AgentId));

        Assert.Equal("outfit_nsfw", failure.Reason);
        Assert.Empty(relay.Uploads);
        var settings = (await store.GetPublicAgentAsync(AgentId))!;
        Assert.Equal(("failed", (string?)"outfit_nsfw"), (settings.AvatarSyncStatus, settings.AvatarSyncReason));
        Assert.Contains(audit.Events, item => item.Name == "avatar_sync_failed" && Equals(item.Data["reason"], "outfit_nsfw"));
    }

    [Fact]
    public async Task An_outfit_edit_leaves_Agents_showing_their_own_avatar_alone()
    {
        var world = await WorldAsync(JsonValue.Create(true));
        var (store, relay, _, events, _) = await PublicSyncAsync(world);
        await store.SavePublicAgentAsync((await store.GetPublicAgentAsync(AgentId))! with
            { AvatarSource = AvatarSources.AgentAvatar });

        await events.EmitAsync("entity.updated", new JsonObject { ["typeSlug"] = "outfit", ["id"] = OutfitId.ToString() });

        Assert.Empty(relay.Uploads);
    }

    private sealed record NsfwWorld(MemoryEntityStore Entities, RecordingAssets Assets,
        RedLeafAvatarImageSource Source, byte[] AgentImage, byte[] OutfitImage);

    private static async Task<NsfwWorld> WorldAsync(JsonNode? nsfw, bool agentAvatar = true)
    {
        var entities = new MemoryEntityStore();
        var enabled = await entities.UpsertBySlugAsync("plugin-status", "enabled", "Enabled");
        await entities.UpsertBySlugAsync("plugin", "outfits", "Outfits",
            new JsonObject { ["status"] = enabled.Id.ToString() });
        var agent = new JsonObject { ["outfit"] = OutfitId.ToString() };
        if (agentAvatar) agent["avatar"] = $"/api/assets/{AgentAsset}";
        entities.Seed(Guid.Parse(AgentId), "agent", "Nova", "nova", agent);
        var outfit = new JsonObject { ["agent"] = AgentId, ["asset"] = $"/api/assets/{OutfitAsset}" };
        if (nsfw is not null) outfit["nsfw"] = nsfw;
        entities.Seed(OutfitId, "outfit", "Evening outfit", "evening-outfit", outfit);
        var agentImage = Png(SKColors.White);
        var outfitImage = Png(SKColors.Green);
        var assets = new RecordingAssets(new Dictionary<string, AssetFile>
        {
            [AgentAsset] = new(agentImage, "image/png"),
            [OutfitAsset] = new(outfitImage, "image/png"),
        });
        return new(entities, assets, new RedLeafAvatarImageSource(entities, assets), agentImage, outfitImage);
    }

    private static async Task<(CommonsStateStore, AvatarRelay, MemoryAudit, ListeningPluginEvents, PublicAgentAvatarSync)>
        PublicSyncAsync(NsfwWorld world)
    {
        var store = new CommonsStateStore(world.Entities);
        await store.SaveOwnerSettingsAsync(new(null, "https://relay.example", "Camille", [], [], [], "owner-1"));
        var relay = new AvatarRelay();
        var audit = new MemoryAudit();
        var events = new ListeningPluginEvents();
        var sync = new PublicAgentAvatarSync(store, world.Source, new AvatarImageProcessor(),
            new RelayClientFactory(new FixedHttpClientFactory(relay), store), events, audit);
        // Subscribed before the Agent is public, so startup schedules no sync of its own.
        await sync.StartAsync(CancellationToken.None);
        await store.SavePublicAgentAsync(new(null, AgentId, "Nova", true, "credential-one", "active", null,
            ConversationSwitches.Default, [], AvatarSources.Outfit));
        return (store, relay, audit, events, sync);
    }

    /// <summary>The hash of what the relay receives for a source image.</summary>
    private static string Processed(byte[] source)
        => new AvatarImageProcessor().Prepare(new(source, "image/png")).ContentHash;

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] Png(SKColor color)
    {
        using var bitmap = new SKBitmap(64, 64);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed class RecordingAssets(IReadOnlyDictionary<string, AssetFile> files) : IAssets
    {
        public List<string> Reads { get; } = [];
        public Task<AssetFile?> ReadAsync(string assetId, CancellationToken ct = default)
        {
            Reads.Add(assetId);
            return Task.FromResult(files.GetValueOrDefault(assetId));
        }
        public Task<AssetRef> UploadAsync(Stream content, string fileName, string? contentType = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public string GetUrl(string assetId) => $"/api/assets/{assetId}";
    }

    /// <summary>The relay's <c>PUT /v1/envoy/avatar</c>, keeping each upload.</summary>
    private sealed class AvatarRelay : HttpMessageHandler
    {
        public List<byte[]> Uploads { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("PUT /v1/envoy/avatar", $"{request.Method} {request.RequestUri!.AbsolutePath}");
            var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
            Uploads.Add(bytes);
            var hash = Sha(bytes);
            return new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    avatarKey = $"envoys/test/avatar-{hash}.webp", avatarUrl = $"https://relay.example/{hash}",
                    contentHash = hash, changed = true,
                })),
            };
        }
    }

    private sealed class ListeningPluginEvents : IPluginEvents
    {
        private readonly List<(string Type, Func<PluginEvent, Task> Handler)> handlers = [];
        public Task PublishAsync(string eventType, JsonObject payload, CancellationToken ct = default) => Task.CompletedTask;
        public IDisposable Subscribe(string eventType, Func<PluginEvent, Task> handler)
        {
            handlers.Add((eventType, handler));
            return new Subscription();
        }
        public async Task EmitAsync(string type, JsonObject payload)
        {
            foreach (var (subscribed, handler) in handlers.ToArray())
                if (subscribed == type) await handler(new PluginEvent(type, "outfits", payload, DateTimeOffset.UtcNow));
        }
        private sealed class Subscription : IDisposable { public void Dispose() { } }
    }
}
