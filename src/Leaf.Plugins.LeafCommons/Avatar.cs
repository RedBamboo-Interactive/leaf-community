using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using SkiaSharp;

namespace Leaf.Plugins.LeafCommons;

public static class AvatarSources
{
    public const string AgentAvatar = "agent-avatar";
    public const string Outfit = "outfit";

    public static bool IsValid(string? value) => value is AgentAvatar or Outfit;

    public static async Task<string?> ValidateAsync(
        string? value, IAvatarImageSource source, CancellationToken ct = default)
    {
        if (!IsValid(value)) return "invalid_avatar_source";
        return value == Outfit && !await source.IsOutfitsAvailableAsync(ct)
            ? "outfit_unavailable"
            : null;
    }
}

public sealed class AvatarSyncException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}

public sealed record AvatarImage(byte[] Bytes, string ContentType);
public sealed record PreparedAvatar(byte[] Bytes, string ContentType, string ContentHash);
public sealed record AvatarUploadResult(string AvatarKey, string AvatarUrl, string ContentHash, bool Changed);
public sealed record AvatarSyncResult(bool Uploaded, string ContentHash, string? AvatarUrl = null);

public interface IAvatarImageSource
{
    Task<bool> IsOutfitsAvailableAsync(CancellationToken ct = default);
    Task<AvatarImage> ResolveAsync(string source, string agentEntityId, CancellationToken ct = default);
}

public interface IAvatarUploader
{
    Task<AvatarUploadResult> UploadAvatarAsync(byte[] bytes, string contentType, CancellationToken ct = default);
}

public sealed class RedLeafAvatarImageSource(IEntityStore entities, IAssets assets) : IAvatarImageSource
{
    public async Task<bool> IsOutfitsAvailableAsync(CancellationToken ct = default)
    {
        var plugin = await entities.GetBySlugAsync("plugin", "outfits", ct);
        var enabled = await entities.GetBySlugAsync("plugin-status", "enabled", ct);
        return plugin is not null && enabled is not null
            && String(plugin.Data, "status") == enabled.Id.ToString();
    }

    public async Task<AvatarImage> ResolveAsync(string source, string agentEntityId, CancellationToken ct = default)
    {
        if (!AvatarSources.IsValid(source)) throw new AvatarSyncException("invalid_avatar_source");
        var agent = await ResolveAgentAsync(agentEntityId, ct)
            ?? throw new AvatarSyncException("agent_not_found");
        string? assetValue;
        if (source == AvatarSources.Outfit)
        {
            if (!await IsOutfitsAvailableAsync(ct)) throw new AvatarSyncException("outfit_unavailable");
            var outfitId = String(agent.Data, "outfit");
            if (string.IsNullOrWhiteSpace(outfitId))
            {
                assetValue = AvatarValue(agent.Data);
            }
            else
            {
                if (!Guid.TryParse(outfitId, out var parsed)) throw new AvatarSyncException("outfit_not_found");
                var outfit = await entities.GetAsync(parsed, ct);
                if (outfit?.TypeSlug != "outfit") throw new AvatarSyncException("outfit_not_found");
                var owner = String(outfit.Data, "agent");
                if (!string.IsNullOrWhiteSpace(owner) && owner != agent.Id.ToString())
                    throw new AvatarSyncException("outfit_agent_mismatch");
                assetValue = String(outfit.Data, "asset");
            }
        }
        else
        {
            assetValue = AvatarValue(agent.Data);
        }

        var assetId = AssetId(assetValue) ?? throw new AvatarSyncException("avatar_asset_not_local");
        var file = await assets.ReadAsync(assetId, ct) ?? throw new AvatarSyncException("avatar_asset_not_found");
        if (!file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new AvatarSyncException("avatar_asset_not_image");
        return new(file.Bytes, file.ContentType);
    }

    private async Task<LeafEntity?> ResolveAgentAsync(string value, CancellationToken ct)
    {
        LeafEntity? entity = Guid.TryParse(value, out var id)
            ? await entities.GetAsync(id, ct)
            : await entities.GetBySlugAsync(value, ct);
        return entity?.TypeSlug == "agent" ? entity : null;
    }

    private static string? AvatarValue(JsonObject data)
    {
        if (String(data, "avatar") is { } direct) return direct;
        if (data["avatar"] is not JsonObject avatar) return null;
        return String(avatar, "filename") ?? String(avatar, "url");
    }

    internal static string? AssetId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim();
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute))
        {
            if (!absolute.IsLoopback) return null;
            candidate = absolute.AbsolutePath;
        }
        const string prefix = "/api/assets/";
        if (candidate.StartsWith(prefix, StringComparison.Ordinal))
            candidate = Uri.UnescapeDataString(candidate[prefix.Length..]);
        else if (candidate.Contains('/', StringComparison.Ordinal) || candidate.Contains("\\", StringComparison.Ordinal)
                 || candidate.Contains("://", StringComparison.Ordinal))
            return null;
        return string.IsNullOrWhiteSpace(candidate) || candidate is "." or ".." ? null : candidate;
    }

    private static string? String(JsonObject data, string key)
        => data[key] is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
}

public sealed class AvatarImageProcessor
{
    public const int Dimension = 256;
    public const int MaximumBytes = 256 * 1024;
    private const int MaximumInputBytes = 20 * 1024 * 1024;

    public PreparedAvatar Prepare(AvatarImage input)
    {
        if (input.Bytes.Length == 0 || input.Bytes.Length > MaximumInputBytes)
            throw new AvatarSyncException("avatar_image_invalid");
        using var stream = new MemoryStream(input.Bytes, writable: false);
        using var bitmap = SKBitmap.Decode(stream) ?? throw new AvatarSyncException("avatar_image_invalid");
        if (bitmap.Width < 1 || bitmap.Height < 1 || bitmap.Width > 10_000 || bitmap.Height > 10_000)
            throw new AvatarSyncException("avatar_image_dimensions_invalid");
        using var resized = bitmap.Resize(new SKSizeI(Dimension, Dimension),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            ?? throw new AvatarSyncException("avatar_resize_failed");
        using var image = SKImage.FromBitmap(resized);
        for (var quality = 90; quality >= 30; quality -= 10)
        {
            using var encoded = image.Encode(SKEncodedImageFormat.Webp, quality)
                ?? throw new AvatarSyncException("avatar_encode_failed");
            var bytes = encoded.ToArray();
            if (bytes.Length <= MaximumBytes)
            {
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                return new(bytes, "image/webp", hash);
            }
        }
        throw new AvatarSyncException("avatar_too_large");
    }
}

public sealed class AvatarSyncService(
    CommonsStateStore store,
    IAvatarImageSource source,
    AvatarImageProcessor processor,
    IAvatarUploader uploader,
    IPluginEvents events,
    ICommonsAudit audit) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IDisposable? subscription;

    public async Task StartAsync(CancellationToken stopping)
    {
        subscription = events.Subscribe("agent.avatar-changed", OnOutfitChangedAsync);
        stopping.Register(Dispose);
        await TrySyncAsync(stopping);
    }

    public async Task<AvatarSyncResult> SyncAsync(CommonsSettings? requested = null, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var settings = requested ?? await store.GetSettingsAsync(ct);
            if (string.IsNullOrWhiteSpace(settings.Credential)) throw new AvatarSyncException("envoy_credential_missing");
            if (string.IsNullOrWhiteSpace(settings.AgentEntityId)) throw new AvatarSyncException("agent_not_configured");
            var image = await source.ResolveAsync(settings.AvatarSource, settings.AgentEntityId, ct);
            var prepared = processor.Prepare(image);
            if (string.Equals(prepared.ContentHash, settings.AvatarContentHash, StringComparison.Ordinal))
                return new(false, prepared.ContentHash);
            var uploaded = await uploader.UploadAvatarAsync(prepared.Bytes, prepared.ContentType, ct);
            if (!string.Equals(uploaded.ContentHash, prepared.ContentHash, StringComparison.Ordinal))
                throw new AvatarSyncException("relay_avatar_hash_mismatch");
            await store.SaveSettingsAsync(settings with
            {
                Avatar = uploaded.AvatarKey,
                AvatarContentHash = prepared.ContentHash,
            }, ct);
            await audit.RecordAsync("avatar_synced", new Dictionary<string, object?>
            {
                ["source"] = settings.AvatarSource,
                ["content_hash"] = prepared.ContentHash,
                ["changed"] = uploaded.Changed,
            }, ct);
            return new(true, prepared.ContentHash, uploaded.AvatarUrl);
        }
        finally { gate.Release(); }
    }

    public Task<bool> IsOutfitsAvailableAsync(CancellationToken ct = default)
        => source.IsOutfitsAvailableAsync(ct);

    private async Task OnOutfitChangedAsync(PluginEvent evt)
    {
        if (evt.SourcePlugin != "outfits") return;
        var settings = await store.GetSettingsAsync();
        if (settings.AvatarSource != AvatarSources.Outfit) return;
        var changedAgent = evt.Payload["agentId"]?.GetValue<string>();
        if (!string.Equals(changedAgent, settings.AgentEntityId, StringComparison.OrdinalIgnoreCase)) return;
        await TrySyncAsync(CancellationToken.None);
    }

    private async Task TrySyncAsync(CancellationToken ct)
    {
        try { await SyncAsync(ct: ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (AvatarSyncException error)
        {
            await audit.RecordAsync("avatar_sync_failed", new Dictionary<string, object?> { ["reason"] = error.Reason }, CancellationToken.None);
        }
        catch
        {
            await audit.RecordAsync("avatar_sync_failed", new Dictionary<string, object?> { ["reason"] = "unexpected_error" }, CancellationToken.None);
        }
    }

    public void Dispose()
    {
        subscription?.Dispose();
        subscription = null;
        gate.Dispose();
    }
}
