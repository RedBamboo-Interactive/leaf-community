using System.Net.Http.Headers;
using System.Net.Http.Json;
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
    public async Task DeliverAsync(CommonsDigest digest, CancellationToken ct = default)
    {
        var settings = await state.GetSettingsAsync(ct);
        if (string.IsNullOrWhiteSpace(settings.NovaDiscussionId)) throw new InvalidOperationException("Dedicated Nova digest discussion is not configured");
        var allowedHosts = settings.LinkAllowlist.Count == 0
            ? DigestFormatter.RelayAllowedHosts
            : settings.LinkAllowlist.Intersect(DigestFormatter.RelayAllowedHosts, StringComparer.OrdinalIgnoreCase).ToArray();
        var content = formatter.Format(digest, allowedHosts);
        var token = tokens.IssueSystem(new("service", "commons-envoy", "Commons Envoy", "Deliver an owner-approved external digest",
            [new("discussion", settings.NovaDiscussionId), new("external-conversation", digest.ThreadRef)], TimeSpan.FromMinutes(2)));
        var uri = new Uri($"http://127.0.0.1:18804/api/apps/nova/discussions/{Uri.EscapeDataString(settings.NovaDiscussionId)}/event");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(new { content }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            await audit.RecordAsync("digest_failed", new Dictionary<string, object?> { ["type"] = digest.Type, ["thread"] = digest.ThreadRef, ["status"] = (int)response.StatusCode }, ct);
            throw new HttpRequestException($"Nova digest delivery failed with HTTP {(int)response.StatusCode}");
        }
        await audit.RecordAsync("digest_delivered", new Dictionary<string, object?> { ["type"] = digest.Type, ["thread"] = digest.ThreadRef, ["size"] = content.Length }, ct);
    }
}
