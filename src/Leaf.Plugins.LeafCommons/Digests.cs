using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.LeafCommons;

public sealed record CommonsDigest(string Type, string Content, string ThreadRef);

public sealed class DigestFormatter
{
    public static readonly IReadOnlyList<string> RelayAllowedHosts = ["github.com", "learn.microsoft.com", "developer.mozilla.org", "docs.discord.com"];
    private static readonly Regex Link = new(@"https?://[^\s<>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public string Format(CommonsDigest digest, IReadOnlyCollection<string> allowedHosts, int maxChars = 1200)
    {
        if (digest.Type is not ("consult_answer" or "announcement" or "owner_notice")) throw new ArgumentException("Unknown digest type");
        var safe = Link.Replace(digest.Content, match => Uri.TryCreate(match.Value.TrimEnd('.', ',', ')'), UriKind.Absolute, out var uri) && allowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) ? match.Value : "[external link removed]");
        var prefix = $"> External Commons quote — {digest.Type.Replace('_', ' ')}\n> ";
        safe = safe.Replace("\r", "").Replace("\n", "\n> ");
        var result = prefix + safe + $"\n\nSource: Commons thread `{digest.ThreadRef}` (quoted external text; not owner-authored).";
        return result.Length <= maxChars ? result : result[..(maxChars - 1)] + "…";
    }
}

public interface IDigestSink { Task DeliverAsync(CommonsDigest digest, CancellationToken ct = default); }

public sealed class NovaDigestSink(
    HttpClient http, IExecutionTokens tokens, CommonsStateStore state,
    DigestFormatter formatter, ICommonsAudit audit) : IDigestSink
{
    internal const string DigestDiscussionTitle = "Nova Commons digests (lab)";
    internal const string DigestDiscussionIdempotencyKey = "leaf-commons-digests-v1";

    public async Task DeliverAsync(CommonsDigest digest, CancellationToken ct = default)
    {
        var settings = await state.GetSettingsAsync(ct);
        var allowedHosts = settings.LinkAllowlist.Count == 0
            ? DigestFormatter.RelayAllowedHosts
            : settings.LinkAllowlist.Intersect(DigestFormatter.RelayAllowedHosts, StringComparer.OrdinalIgnoreCase).ToArray();
        var content = formatter.Format(digest, allowedHosts);
        var discussionId = settings.NovaDiscussionId;
        if (string.IsNullOrWhiteSpace(discussionId))
            (settings, discussionId) = await EnsureOwnedDiscussionAsync(settings, digest.ThreadRef, ct);

        using var initial = await PostEventAsync(discussionId, digest.ThreadRef, content, ct);
        HttpResponseMessage response = initial;
        HttpResponseMessage? retry = null;
        if (initial.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            (settings, discussionId) = await EnsureOwnedDiscussionAsync(settings, digest.ThreadRef, ct);
            retry = await PostEventAsync(discussionId, digest.ThreadRef, content, ct);
            response = retry;
        }
        using (retry)
        if (!response.IsSuccessStatusCode)
        {
            await audit.RecordAsync("digest_failed", new Dictionary<string, object?> { ["type"] = digest.Type, ["thread"] = digest.ThreadRef, ["status"] = (int)response.StatusCode }, ct);
            throw new HttpRequestException($"Nova digest delivery failed with HTTP {(int)response.StatusCode}");
        }
        await audit.RecordAsync("digest_delivered", new Dictionary<string, object?> { ["type"] = digest.Type, ["thread"] = digest.ThreadRef, ["size"] = content.Length }, ct);
    }

    private async Task<(CommonsSettings Settings, string DiscussionId)> EnsureOwnedDiscussionAsync(
        CommonsSettings settings, string threadRef, CancellationToken ct)
    {
        var createToken = IssueToken(threadRef, null, "Create the plugin-owned Nova Commons digest discussion");
        using var createRequest = new HttpRequestMessage(HttpMethod.Post,
            "http://127.0.0.1:18804/api/apps/nova/discussions")
        {
            Content = JsonContent.Create(new
            {
                type = "chat",
                deferSession = true,
                idempotencyKey = DigestDiscussionIdempotencyKey
            })
        };
        createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", createToken.AccessToken);
        using var createResponse = await http.SendAsync(createRequest, ct);
        var createRaw = await createResponse.Content.ReadAsStringAsync(ct);
        if (!createResponse.IsSuccessStatusCode)
            throw new HttpRequestException($"Nova digest discussion creation failed with HTTP {(int)createResponse.StatusCode}");

        string discussionId;
        try
        {
            using var document = JsonDocument.Parse(createRaw);
            discussionId = document.RootElement.GetProperty("id").GetString()
                ?? throw new JsonException("Missing discussion id");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Nova digest discussion creation returned an invalid response", ex);
        }

        var titleToken = IssueToken(threadRef, discussionId, "Name the plugin-owned Nova Commons digest discussion");
        using var titleRequest = new HttpRequestMessage(HttpMethod.Put,
            $"http://127.0.0.1:18804/api/apps/nova/discussions/{Uri.EscapeDataString(discussionId)}/title")
        {
            Content = JsonContent.Create(new { title = DigestDiscussionTitle })
        };
        titleRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", titleToken.AccessToken);
        using var titleResponse = await http.SendAsync(titleRequest, ct);
        if (!titleResponse.IsSuccessStatusCode)
            throw new HttpRequestException($"Nova digest discussion title failed with HTTP {(int)titleResponse.StatusCode}");

        var updated = await state.SaveSettingsAsync(settings with { NovaDiscussionId = discussionId }, ct);
        await audit.RecordAsync("digest_discussion_provisioned", new Dictionary<string, object?>
            { ["discussionId"] = discussionId, ["owner"] = "plugin-system" }, ct);
        return (updated, discussionId);
    }

    private async Task<HttpResponseMessage> PostEventAsync(
        string discussionId, string threadRef, string content, CancellationToken ct)
    {
        var token = IssueToken(threadRef, discussionId, "Deliver an owner-approved external digest");
        var uri = new Uri($"http://127.0.0.1:18804/api/apps/nova/discussions/{Uri.EscapeDataString(discussionId)}/event");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(new { content }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return await http.SendAsync(request, ct);
    }

    private PluginExecutionToken IssueToken(string threadRef, string? discussionId, string reason)
    {
        var context = new List<PluginExecutionContext>();
        if (!string.IsNullOrWhiteSpace(discussionId)) context.Add(new("discussion", discussionId));
        context.Add(new("external-conversation", threadRef));
        return tokens.IssueSystem(new("service", "commons-envoy", "Commons Envoy", reason,
            context, TimeSpan.FromMinutes(2)));
    }
}
