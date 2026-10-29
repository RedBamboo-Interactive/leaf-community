using System.Diagnostics;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.LeafCommons;

/// <summary>The outbound Sentinel's answer for one Leaf Community post.</summary>
/// <param name="Allowed">The post may go out.</param>
/// <param name="Unavailable">No usable verdict: the Sentinel is missing, failed, or did not
/// prove it applied the outbound rubric. Never allowed (fail closed).</param>
/// <param name="Review">What happened, for the audit, the owner and the writing session.</param>
public sealed record CommonsOutboundVerdict(bool Allowed, bool Unavailable, JsonObject Review);

/// <summary>
/// The outbound Sentinel pass for text a Public Agent posts to Discord through the Leaf
/// Community relay. Fail closed (Laurent, 2026-10-23): a post goes out only with an available
/// verdict that proves the outbound direction and does not hold it. The fixed fallback notices
/// are the only text that leaves without a review.
/// </summary>
public sealed class CommonsOutboundReview(
    IUntrustedContentReview? sentinel = null,
    ILogger<CommonsOutboundReview>? logger = null,
    IEntityStore? entities = null)
{
    public const string Audience =
        "a public Discord channel or thread in a Leaf Community server, readable by every member who can see it";
    /// <summary>Used where no Sentinel is wired: every post is unavailable, so nothing leaves.</summary>
    public static CommonsOutboundReview Unavailable { get; } = new(null);

    /// <summary>
    /// Reviews <paramref name="content"/> and, when given, the images that go out with it (as Leaf
    /// Discord does): the Sentinel reads the Asset bytes and reviews them on a vision model with the
    /// same rubric. A verdict counts only when it echoes the outbound direction and reviewed every
    /// image sent; a reviewer that ignored the images, or could not see them, is unavailable.
    /// The review states its own provenance for <paramref name="ownerUserId"/>, so it runs with a
    /// signed execution identity even when no request or execution scope is there to inherit.
    /// </summary>
    public async Task<CommonsOutboundVerdict> ReviewAsync(
        string agentEntityId, string threadRef, string content, string key, CancellationToken ct = default,
        IReadOnlyList<ExternalConversationOutputAsset>? images = null, string? ownerUserId = null)
    {
        images ??= [];
        var watch = Stopwatch.StartNew();
        UntrustedContentReviewResult? result = null;
        if (sentinel is not null)
        {
            try
            {
                // No compute: the Sentinel always runs on its configured tier (fast by default) on
                // the installation's default provider.
                var reviewKey = key.Length <= 200 ? key : key[..200];
                var provenance = await CommonsComputeProvenance.CreateAsync(entities, agentEntityId, ownerUserId,
                    [new ComputeContextReference("external-conversation", threadRef),
                     new ComputeContextReference("outbound-review", reviewKey)],
                    reviewKey, ct);
                result = await sentinel.ReviewAsync(new UntrustedContentReviewRequest
                {
                    Content = content,
                    Origin = new UntrustedContentOrigin(
                        UntrustedContentSources.LeafCommons, threadRef, null, LeafCommonsPlugin.PluginId),
                    AgentRef = agentEntityId,
                    ComputeProvenance = provenance,
                    Direction = UntrustedContentDirection.Outbound,
                    Audience = Audience,
                    IdempotencyKey = reviewKey,
                    JobName = "Leaf Community outbound review",
                    Images = images.Count == 0 ? null : images.Select(CommonsImages.ReviewImage).ToArray(),
                }, ct);
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                logger?.LogWarning(error, "Leaf Community outbound review {Key} failed", key);
            }
        }
        watch.Stop();
        var verdict = result is { Direction: UntrustedContentDirection.Outbound }
                      && result.ImagesReviewed == images.Count
            ? result.Verdict
            : UntrustedContentVerdict.Unavailable();
        var held = verdict.Available
                   && (verdict.Risk == "high" || verdict.Handling == "require_private_approval");
        var allowed = verdict.Available && !held;
        var review = new JsonObject
        {
            ["direction"] = "outbound",
            ["outcome"] = allowed ? "delivered" : "withheld",
            ["risk"] = verdict.Risk,
            ["signals"] = new JsonArray(verdict.Signals.Select(signal => (JsonNode?)signal).ToArray()),
            ["handling"] = verdict.Handling,
            ["available"] = verdict.Available,
            ["isolation"] = verdict.Isolation,
            ["job_id"] = result?.JobId,
            ["images"] = images.Count,
            ["images_reviewed"] = verdict.Available ? result?.ImagesReviewed ?? 0 : 0,
            ["elapsed_ms"] = watch.ElapsedMilliseconds,
            ["reviewed_at"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        return new(allowed, !verdict.Available, review);
    }

    /// <summary>
    /// A fixed notice that needs no review: exactly one of the notices. Any prefix or suffix could
    /// be model-written and is reviewed. The relay adds a chain reply's "Replying to" line itself.
    /// </summary>
    public static bool IsFixedNotice(string content) => FixedNotices.Contains(content);

    public const string WithheldNotice =
        "I wrote a reply here but withheld it because it could have shared private information.";

    public const string UnavailableNotice =
        "I wrote a reply but couldn't send it, my safety check is unavailable. Try again in a bit.";

    private static readonly string[] FixedNotices =
    [
        EnvoyRunner.CoverageFallback,
        PublicAgentConversationCoordinator.SentinelUnavailableNotice,
        WithheldNotice,
        UnavailableNotice,
    ];
}

/// <summary>
/// Puts the outbound pass in front of a Public Agent's relay for the restricted-mode Envoy runner.
/// A refused post returns <c>Accepted = false</c> with code <c>sentinel_held</c> or
/// <c>sentinel_unavailable</c>, which the runner already answers with its fixed fallback.
/// </summary>
public sealed class ReviewedEnvoyRelay(
    IEnvoyRelay inner, CommonsOutboundReview review, string agentEntityId, ICommonsAudit audit,
    string? ownerUserId = null) : IEnvoyRelay
{
    public Task<IReadOnlyList<RelayMessage>> ReadHistoryAsync(string threadRef, CancellationToken ct = default)
        => inner.ReadHistoryAsync(threadRef, ct);

    public async Task<RelayPostResult> PostAsync(string threadRef, string content,
        IReadOnlyList<string>? addresses = null, string? idempotencyKey = null, CancellationToken ct = default)
        => await RefusalAsync(threadRef, content, addresses, idempotencyKey, ct)
           ?? await inner.PostAsync(threadRef, content, addresses, idempotencyKey, ct);

    public async Task<RelayPostResult> OpenAsync(string channelId, string? title, string content,
        IReadOnlyList<string>? addresses = null, string? idempotencyKey = null, CancellationToken ct = default)
        => await RefusalAsync(channelId, $"{title}\n\n{content}", addresses, idempotencyKey, ct)
           ?? await inner.OpenAsync(channelId, title, content, addresses, idempotencyKey, ct);

    private async Task<RelayPostResult?> RefusalAsync(string threadRef, string content,
        IReadOnlyList<string>? addresses, string? idempotencyKey, CancellationToken ct)
    {
        // Addresses are model-chosen, so a notice that carries any is reviewed too.
        if (addresses is not { Count: > 0 } && CommonsOutboundReview.IsFixedNotice(content))
            return null;
        var text = addresses is { Count: > 0 } ? $"{content}\n\n[addresses] {string.Join(", ", addresses)}" : content;
        var verdict = await review.ReviewAsync(agentEntityId, threadRef, text,
            $"commons-envoy:{agentEntityId}:{idempotencyKey ?? threadRef}", ct, ownerUserId: ownerUserId);
        await audit.RecordAsync("outbound_review", new Dictionary<string, object?>
        {
            ["thread"] = threadRef,
            ["outcome"] = verdict.Review["outcome"]?.GetValue<string>(),
            ["available"] = !verdict.Unavailable,
            ["risk"] = verdict.Review["risk"]?.GetValue<string>(),
        }, ct);
        return verdict.Allowed
            ? null
            : new RelayPostResult(false, verdict.Unavailable ? "sentinel_unavailable" : "sentinel_held");
    }
}
