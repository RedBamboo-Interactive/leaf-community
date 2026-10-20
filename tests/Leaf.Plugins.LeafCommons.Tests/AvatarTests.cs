using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Leaf.Plugins.LeafCommons;
using Leaf.Sdk.Services;
using SkiaSharp;
using Xunit;

namespace Leaf.Plugins.LeafCommons.Tests;

public sealed class AvatarTests
{
    [Fact]
    public void Avatar_source_defaults_to_agent_avatar()
    {
        var settings = new CommonsSettings(null, "https://relay.example", null, "disconnected", "Nova", null,
            new Dictionary<string, bool>(), [], [], [], null, []);
        Assert.Equal(AvatarSources.AgentAvatar, settings.AvatarSource);
    }

    [Fact]
    public async Task Outfit_source_is_rejected_when_the_extension_is_not_enabled()
    {
        var source = new FakeAvatarImageSource(false, Image(SKColors.Blue), Image(SKColors.Red));
        Assert.Equal("outfit_unavailable", await AvatarSources.ValidateAsync(AvatarSources.Outfit, source));
        Assert.Null(await AvatarSources.ValidateAsync(AvatarSources.AgentAvatar, source));
    }

    [Fact]
    public void Avatar_is_resized_to_256_square_and_encoded_below_the_relay_cap()
    {
        var prepared = new AvatarImageProcessor().Prepare(Image(SKColors.CornflowerBlue, 1200, 700));
        Assert.Equal("image/webp", prepared.ContentType);
        Assert.InRange(prepared.Bytes.Length, 1, AvatarImageProcessor.MaximumBytes);
        Assert.Matches("^[a-f0-9]{64}$", prepared.ContentHash);
        using var decoded = SKBitmap.Decode(prepared.Bytes);
        Assert.NotNull(decoded);
        Assert.Equal(AvatarImageProcessor.Dimension, decoded.Width);
        Assert.Equal(AvatarImageProcessor.Dimension, decoded.Height);
    }

    [Fact]
    public async Task Unchanged_processed_hash_is_not_uploaded_again()
    {
        var entities = new MemoryEntityStore();
        var store = await SettingsAsync(entities);
        var uploader = new CapturingAvatarUploader();
        var service = Service(store, new FakeAvatarImageSource(true, Image(SKColors.Blue), Image(SKColors.Red)), uploader);

        Assert.True((await service.SyncAsync()).Uploaded);
        Assert.False((await service.SyncAsync()).Uploaded);
        Assert.Equal(1, uploader.Calls);
        Assert.NotNull((await store.GetSettingsAsync()).AvatarContentHash);
    }

    [Fact]
    public async Task Switching_sources_uploads_each_new_selected_image()
    {
        var entities = new MemoryEntityStore();
        var store = await SettingsAsync(entities);
        var uploader = new CapturingAvatarUploader();
        var service = Service(store, new FakeAvatarImageSource(true, Image(SKColors.Blue), Image(SKColors.Red)), uploader);

        await service.SyncAsync();
        var outfit = (await store.GetSettingsAsync()) with
            { AvatarSource = AvatarSources.Outfit, AvatarContentHash = null };
        await store.SaveSettingsAsync(outfit);
        await service.SyncAsync();
        var agent = (await store.GetSettingsAsync()) with
            { AvatarSource = AvatarSources.AgentAvatar, AvatarContentHash = null };
        await store.SaveSettingsAsync(agent);
        await service.SyncAsync();

        Assert.Equal(3, uploader.Calls);
        Assert.Equal(2, uploader.Hashes.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(uploader.Hashes[0], uploader.Hashes[2]);
        Assert.NotEqual(uploader.Hashes[0], uploader.Hashes[1]);
    }

    [Fact]
    public async Task Supported_outfit_change_event_refreshes_only_the_configured_agent()
    {
        var entities = new MemoryEntityStore();
        var store = await SettingsAsync(entities);
        await store.SaveSettingsAsync((await store.GetSettingsAsync()) with
            { AvatarSource = AvatarSources.Outfit, AvatarContentHash = null });
        var source = new FakeAvatarImageSource(true, Image(SKColors.Blue), Image(SKColors.Red));
        var uploader = new CapturingAvatarUploader();
        var events = new CapturingPluginEvents();
        using var service = new AvatarSyncService(store, source, new AvatarImageProcessor(), uploader, events, new MemoryAudit());
        using var stopping = new CancellationTokenSource();
        await service.StartAsync(stopping.Token);
        Assert.Equal(1, uploader.Calls);

        source.Outfit = Image(SKColors.Green);
        await events.EmitAsync(new PluginEvent("agent.avatar-changed", "outfits",
            new JsonObject { ["agentId"] = "agent-1" }, DateTimeOffset.UtcNow));
        Assert.Equal(2, uploader.Calls);

        source.Outfit = Image(SKColors.Yellow);
        await events.EmitAsync(new PluginEvent("agent.avatar-changed", "another-plugin",
            new JsonObject { ["agentId"] = "agent-1" }, DateTimeOffset.UtcNow));
        await events.EmitAsync(new PluginEvent("agent.avatar-changed", "outfits",
            new JsonObject { ["agentId"] = "another-agent" }, DateTimeOffset.UtcNow));
        Assert.Equal(2, uploader.Calls);
    }

    [Fact]
    public async Task Resolver_uses_enabled_plugin_status_and_outfits_canonical_agent_reference()
    {
        var entities = new MemoryEntityStore();
        var enabled = await entities.UpsertBySlugAsync("plugin-status", "enabled", "Enabled");
        await entities.UpsertBySlugAsync("plugin", "outfits", "Outfits",
            new JsonObject { ["status"] = enabled.Id.ToString() });
        var agent = await entities.UpsertBySlugAsync("agent", "nova", "Nova",
            new JsonObject { ["avatar"] = "/api/assets/base.png" });
        var outfit = await entities.CreateAsync("outfit", "Red jacket",
            new JsonObject { ["agent"] = agent.Id.ToString(), ["asset"] = "/api/assets/outfit.webp" });
        await entities.PatchAsync(agent.Id, new JsonObject { ["outfit"] = outfit.Id.ToString() });
        var assets = new FakeAssets(new Dictionary<string, AssetFile>
        {
            ["base.png"] = new(Image(SKColors.Blue).Bytes, "image/png"),
            ["outfit.webp"] = new(Image(SKColors.Red).Bytes, "image/webp"),
        });
        var resolver = new RedLeafAvatarImageSource(entities, assets);

        Assert.True(await resolver.IsOutfitsAvailableAsync());
        Assert.Equal("image/png", (await resolver.ResolveAsync(AvatarSources.AgentAvatar, agent.Id.ToString())).ContentType);
        Assert.Equal("image/webp", (await resolver.ResolveAsync(AvatarSources.Outfit, agent.Id.ToString())).ContentType);
        await entities.PatchAsync(agent.Id, new JsonObject { ["outfit"] = "" });
        Assert.Equal("image/png", (await resolver.ResolveAsync(AvatarSources.Outfit, agent.Id.ToString())).ContentType);
    }

    private static AvatarImage Image(SKColor color, int width = 640, int height = 640)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return new(data.ToArray(), "image/png");
    }

    private static async Task<CommonsStateStore> SettingsAsync(MemoryEntityStore entities)
    {
        var store = new CommonsStateStore(entities);
        await store.SaveSettingsAsync(new(null, "https://relay.example", "credential", "active", "Nova", null,
            new Dictionary<string, bool>(), [], [], [], null, [], "owner-1", "agent-1"));
        return store;
    }

    private static AvatarSyncService Service(CommonsStateStore store, IAvatarImageSource source, IAvatarUploader uploader)
        => new(store, source, new AvatarImageProcessor(), uploader, new NullPluginEvents(), new MemoryAudit());
}

internal sealed class FakeAvatarImageSource(bool outfitsAvailable, AvatarImage agent, AvatarImage outfit) : IAvatarImageSource
{
    public AvatarImage Agent { get; set; } = agent;
    public AvatarImage Outfit { get; set; } = outfit;
    public Task<bool> IsOutfitsAvailableAsync(CancellationToken ct = default) => Task.FromResult(outfitsAvailable);
    public Task<AvatarImage> ResolveAsync(string source, string agentEntityId, CancellationToken ct = default)
        => Task.FromResult(source == AvatarSources.Outfit ? Outfit : Agent);
}

internal sealed class CapturingAvatarUploader : IAvatarUploader
{
    public int Calls { get; private set; }
    public List<string> Hashes { get; } = [];
    public Task<AvatarUploadResult> UploadAvatarAsync(byte[] bytes, string contentType, CancellationToken ct = default)
    {
        Calls++;
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Hashes.Add(hash);
        return Task.FromResult(new AvatarUploadResult($"envoys/test/avatar-{hash}.webp", $"https://relay.test/{hash}", hash, true));
    }
}

internal sealed class FakeAssets(IReadOnlyDictionary<string, AssetFile> files) : IAssets
{
    public Task<AssetFile?> ReadAsync(string assetId, CancellationToken ct = default)
        => Task.FromResult(files.GetValueOrDefault(assetId));
    public Task<AssetRef> UploadAsync(Stream content, string fileName, string? contentType = null, CancellationToken ct = default)
        => throw new NotSupportedException();
    public string GetUrl(string assetId) => $"/api/assets/{assetId}";
}

internal sealed class NullPluginEvents : IPluginEvents
{
    public Task PublishAsync(string eventType, JsonObject payload, CancellationToken ct = default) => Task.CompletedTask;
    public IDisposable Subscribe(string eventType, Func<PluginEvent, Task> handler) => new NullDisposable();
    private sealed class NullDisposable : IDisposable { public void Dispose() { } }
}

internal sealed class CapturingPluginEvents : IPluginEvents
{
    private string? eventType;
    private Func<PluginEvent, Task>? handler;
    public Task PublishAsync(string eventType, JsonObject payload, CancellationToken ct = default) => Task.CompletedTask;
    public IDisposable Subscribe(string subscribedType, Func<PluginEvent, Task> subscribedHandler)
    {
        eventType = subscribedType;
        handler = subscribedHandler;
        return new NullDisposable();
    }
    public Task EmitAsync(PluginEvent evt) => evt.EventType == eventType && handler is not null ? handler(evt) : Task.CompletedTask;
    private sealed class NullDisposable : IDisposable { public void Dispose() { } }
}
