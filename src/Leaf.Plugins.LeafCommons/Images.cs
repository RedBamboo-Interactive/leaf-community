using System.Net;
using System.Security.Cryptography;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Leaf.Plugins.LeafCommons;

/// <summary>
/// An image a relay ping reports: untrusted metadata about a file on Discord's CDN. The bytes
/// are fetched and checked here, never trusted from the descriptor.
/// </summary>
public sealed record RelayImageAttachment(
    string Id, string Filename, string ContentType, long Size, string Url,
    int? Width = null, int? Height = null, string? Description = null);

/// <summary>One image attached to an outbound relay post.</summary>
public sealed record RelayOutboundImage(byte[] Bytes, string ContentType, string? Description = null);

/// <summary>Images that reached RedLeaf Assets for one inbound message, and how many did not.</summary>
public sealed record CommonsInboundImageSet(IReadOnlyList<ExternalConversationAttachment> Attachments, int Omitted);

/// <summary>
/// The image bounds of Leaf Community, shared by the relay, the Sentinel and RedCompute's
/// validated image input: at most four images, 5 MiB and 4096 px per side, PNG, JPEG, still
/// WebP or GIF.
/// </summary>
public static class CommonsImages
{
    public const int MaxImages = UntrustedContentReviewRequest.MaxImages;
    public const int MaxImageBytes = UntrustedContentReviewRequest.MaxImageBytes;
    public const int MaxDimension = 4096;

    /// <summary>The media type read from the bytes, or null when they are not an accepted image.</summary>
    public static string? Sniff(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }))
            return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff) return "image/jpeg";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";
        if (bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
            return "image/gif";
        return null;
    }

    /// <summary>
    /// Why the bytes cannot be shown to the model, or null when they can: the real type, the size,
    /// the dimensions, and (for WebP) a single frame. GIF keeps only its first frame downstream.
    /// </summary>
    public static string? Problem(byte[] bytes, out string? mediaType)
    {
        mediaType = Sniff(bytes);
        if (bytes.Length > MaxImageBytes) return "image_too_large";
        if (mediaType is null) return "image_type_not_allowed";
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.Info.Width < 1 || codec.Info.Height < 1) return "image_invalid";
        if (codec.Info.Width > MaxDimension || codec.Info.Height > MaxDimension) return "image_dimensions_too_large";
        if (mediaType == "image/webp" && codec.FrameCount > 1) return "image_animated";
        return null;
    }

    /// <summary>
    /// An outgoing Asset as the Sentinel reviews it. The media type follows the stored filename's
    /// extension, as the kernel derives it when the bytes are read; the Sentinel checks it against
    /// the stored Asset and refuses anything else as unreadable.
    /// </summary>
    public static UntrustedContentImage ReviewImage(ExternalConversationOutputAsset asset)
        => new(asset.AssetId, AssetMediaType(asset.AssetId), Caption(asset.Description));

    public static string? Caption(string? description)
    {
        var caption = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        return caption is { Length: > UntrustedContentImage.MaxDescriptionLength }
            ? caption[..UntrustedContentImage.MaxDescriptionLength] : caption;
    }

    public static string AssetMediaType(string assetId)
        => Path.GetExtension(assetId.Trim()).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "application/octet-stream",
        };
}

/// <summary>
/// Brings the images of an inbound relay ping into RedLeaf Assets so the Agent's provider can
/// review them with the Sentinel and show them to the model. Only HTTPS URLs on Discord's CDN
/// are fetched, without redirects and within the size bound; the type and dimensions come from
/// the bytes. An image that fails any check is left out and counted, never passed on as a URL.
/// </summary>
public sealed class CommonsInboundImages(HttpClient http, IAssets assets, ICommonsAudit audit,
    ILogger<CommonsInboundImages>? logger = null)
{
    public const string HttpClientName = "commons-discord-cdn";
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
        { "cdn.discordapp.com", "media.discordapp.net" };

    public async Task<CommonsInboundImageSet> ResolveAsync(
        string agentEntityId, string requestId, IReadOnlyList<RelayImageAttachment> images,
        int omittedByRelay, CancellationToken ct = default)
    {
        var attachments = new List<ExternalConversationAttachment>();
        var omitted = omittedByRelay;
        foreach (var image in images)
        {
            if (attachments.Count >= CommonsImages.MaxImages)
            {
                omitted++;
                continue;
            }
            var (attachment, reason) = await FetchAsync(agentEntityId, requestId, image, ct);
            if (attachment is not null)
            {
                attachments.Add(attachment);
                continue;
            }
            omitted++;
            await audit.RecordAsync("inbound_image_omitted", new Dictionary<string, object?>
            {
                ["agent_entity_id"] = agentEntityId,
                ["trigger_id"] = requestId,
                ["attachment_id"] = image.Id,
                ["reason"] = reason,
            }, ct);
        }
        return new(attachments, omitted);
    }

    private async Task<(ExternalConversationAttachment? Attachment, string Reason)> FetchAsync(
        string agentEntityId, string requestId, RelayImageAttachment image, CancellationToken ct)
    {
        if (!Uri.TryCreate(image.Url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(uri.Host))
            return (null, "image_url_not_allowed");
        if (image.Size > CommonsImages.MaxImageBytes) return (null, "image_too_large");
        byte[] bytes;
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400) return (null, "image_redirected");
            if (response.StatusCode != HttpStatusCode.OK) return (null, "image_fetch_failed");
            if (response.Content.Headers.ContentLength > CommonsImages.MaxImageBytes)
                return (null, "image_too_large");
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var block = new byte[64 * 1024];
            while (true)
            {
                var read = await source.ReadAsync(block, ct);
                if (read == 0) break;
                if (buffer.Length + read > CommonsImages.MaxImageBytes) return (null, "image_too_large");
                buffer.Write(block, 0, read);
            }
            bytes = buffer.ToArray();
        }
        catch (Exception error) when (error is HttpRequestException or IOException
            || (error is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger?.LogWarning(error, "Leaf Community image {AttachmentId} could not be fetched", image.Id);
            return (null, "image_fetch_failed");
        }
        var problem = CommonsImages.Problem(bytes, out var mediaType);
        if (problem is not null) return (null, problem);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var name = SafeName(image.Filename, mediaType!);
        using var upload = new MemoryStream(bytes, writable: false);
        var asset = await assets.UploadIdempotentAsync(upload,
            $"leaf-commons:{agentEntityId}:{requestId}:{image.Id}:{sha256}", name, mediaType, ct);
        return (new ExternalConversationAttachment(image.Id, name, mediaType!, bytes.Length, sha256, asset.Url), "");
    }

    /// <summary>A display name without paths or control characters, ending in the real extension.</summary>
    internal static string SafeName(string filename, string mediaType)
    {
        var stem = Path.GetFileNameWithoutExtension(Path.GetFileName(filename.Replace('\\', '/')));
        stem = string.Concat(stem.Where(ch => !char.IsControl(ch) && ch is not '<' and not '>' and not '/')).Trim();
        if (stem.Length == 0) stem = "image";
        if (stem.Length > 80) stem = stem[..80];
        var extension = mediaType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            _ => ".gif",
        };
        return stem + extension;
    }
}
