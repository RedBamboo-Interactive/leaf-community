using Leaf.Sdk.Services;

namespace Leaf.Plugins.LeafCommons.Tests;

/// <summary>A scripted Sentinel that proves the direction it applied unless told not to.</summary>
internal sealed class TestSentinel(Func<UntrustedContentReviewRequest, UntrustedContentVerdict>? decide = null)
    : IUntrustedContentReview
{
    public static UntrustedContentVerdict Clean { get; } =
        new("none", [], "normal", true, UntrustedContentIsolation.VerifiedToolFree);

    public static UntrustedContentVerdict Leak { get; } =
        new("high", ["personal-data"], "require_private_approval", true, UntrustedContentIsolation.VerifiedToolFree);

    public List<UntrustedContentReviewRequest> Requests { get; } = [];
    public Func<UntrustedContentReviewRequest, UntrustedContentVerdict> Decide { get; set; } = decide ?? (_ => Clean);
    public bool EchoDirection { get; set; } = true;
    /// <summary>False acts like a reviewer that predates images: it reports none reviewed.</summary>
    public bool EchoImages { get; set; } = true;

    public static CommonsOutboundReview Passing(MemoryEntityStore entities)
        => new(new TestSentinel());

    public Task<UntrustedContentReviewResult> ReviewAsync(
        UntrustedContentReviewRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(new UntrustedContentReviewResult(
            Decide(request), null, [], new UntrustedContentScreening(0, [], [], request.Content, []), "job", "trace")
        {
            Direction = EchoDirection ? request.Direction : UntrustedContentDirection.Inbound,
            ImagesReviewed = EchoImages ? request.Images?.Count ?? 0 : 0,
        });
    }

    public UntrustedContentScreening Screen(
        string content, UntrustedContentFormat format = UntrustedContentFormat.Text,
        string? ownerDisplayName = null)
        => new(0, [], [], content, []);
}
