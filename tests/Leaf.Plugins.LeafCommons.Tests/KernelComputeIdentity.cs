using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;

namespace Leaf.Plugins.LeafCommons.Tests;

/// <summary>What RedLeaf did with the Compute call a review makes: who signs it, or why it refused.</summary>
internal sealed record KernelIssue(string? Subject, string? BeneficiaryKind, string? BeneficiaryId, string? Refusal)
{
    public bool Issued => Refusal is null;
}

/// <summary>
/// RedLeaf's rule for signing the Compute call a Sentinel review makes, mirrored from
/// <c>ComputeGateway.TryIssueExecutionTokenAsync</c> and <c>ResolvePrincipalAsync</c> (redleaf
/// 1c5eb48c, src/RedLeaf.App/Services/Compute/ComputeGateway.cs), with AuthMiddleware's ambient
/// execution scope: present exactly when the request carries an execution bearer. Kept in step
/// with the kernel's own ComputeGatewayExecutionTokenTests.
/// </summary>
internal static class KernelComputeIdentity
{
    public const string Mismatch = "Compute provenance beneficiary does not match the authenticated caller";
    public const string Unsigned = "RedCompute mutations require a signed execution identity";

    public static KernelIssue Issue(ComputeProvenance? provenance, ClaimsPrincipal? current, Func<string, bool> userExists)
    {
        var authenticated = current?.Identity?.IsAuthenticated == true;
        // An explicit provenance wins; otherwise the ambient execution identity is the parent.
        var parent = authenticated && IsExecution(current!) ? Callers.ExecutionBeneficiary(current!) : null;
        var beneficiary = provenance is not null
            ? (provenance.OnBehalfOf.Kind, provenance.OnBehalfOf.Id)
            : parent;
        if (beneficiary is not { } chosen) return Refused(Unsigned);
        var (kind, id) = chosen;
        var system = string.Equals(kind, "system", StringComparison.OrdinalIgnoreCase);
        var user = string.Equals(kind, "user", StringComparison.OrdinalIgnoreCase);

        if (authenticated)
        {
            var subject = current!.FindFirst("sub")?.Value;
            if (IsLocalDefault(current))
                return system ? new("system", kind, id, null) : Refused(Mismatch);
            if (system && IsExecution(current) && parent?.Kind == "system")
                return new(subject, kind, id, null);
            if (user && string.Equals(subject, id, StringComparison.OrdinalIgnoreCase))
                return new(subject, kind, id, null);
            if (system && (string.Equals(subject, "system", StringComparison.OrdinalIgnoreCase)
                           || subject?.StartsWith("service:", StringComparison.OrdinalIgnoreCase) == true))
                return new(subject, kind, id, null);
            return Refused(Mismatch);
        }

        if (user && !string.IsNullOrWhiteSpace(id))
            return userExists(id) ? new(id, kind, id, null) : Refused(Unsigned);
        return system ? new("system", kind, id, null) : Refused(Unsigned);
    }

    private static bool IsExecution(ClaimsPrincipal principal)
        => string.Equals(principal.FindFirst("token_use")?.Value, "execution", StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalDefault(ClaimsPrincipal principal)
        => principal.Identity?.AuthenticationType == "LocalDefault"
           && string.Equals(principal.FindFirst("sub")?.Value, "local-user", StringComparison.OrdinalIgnoreCase);

    private static KernelIssue Refused(string reason) => new(null, null, null, reason);
}

/// <summary>
/// A Sentinel whose Compute call goes through <see cref="KernelComputeIdentity"/> with the request
/// in flight when it is called. A call RedLeaf refuses comes back unavailable, as the real
/// Sentinel reports a failed Compute call.
/// </summary>
internal sealed class KernelBoundSentinel(IHttpContextAccessor requests, params string[] users) : IUntrustedContentReview
{
    public TestSentinel Inner { get; } = new();
    public List<KernelIssue> Calls { get; } = [];

    public Task<UntrustedContentReviewResult> ReviewAsync(
        UntrustedContentReviewRequest request, CancellationToken ct = default)
    {
        var issue = KernelComputeIdentity.Issue(request.ComputeProvenance, requests.HttpContext?.User,
            id => users.Contains(id, StringComparer.OrdinalIgnoreCase));
        Calls.Add(issue);
        if (issue.Issued) return Inner.ReviewAsync(request, ct);
        Inner.Requests.Add(request);
        return Task.FromResult(new UntrustedContentReviewResult(
            UntrustedContentVerdict.Unavailable(), null, [],
            new UntrustedContentScreening(0, [], [], request.Content, []), null, "trace")
        {
            Direction = request.Direction,
        });
    }

    public UntrustedContentScreening Screen(
        string content, UntrustedContentFormat format = UntrustedContentFormat.Text,
        string? ownerDisplayName = null)
        => Inner.Screen(content, format, ownerDisplayName);
}

/// <summary>The callers a review can find in flight, as RedLeaf's AuthMiddleware authenticates them.</summary>
internal static class Callers
{
    /// <summary>An Agent's own signed execution bearer from a private discussion of
    /// <paramref name="userId"/>: the brief endpoint's caller. RedLeaf always signs it with that
    /// user as <paramref name="subject"/>; another subject models a token no issuer mints.</summary>
    public static ClaimsPrincipal AgentExecution(string userId, string agentId, string? subject = null) => new(new ClaimsIdentity(
    [
        new Claim("sub", subject ?? userId),
        new Claim("token_use", "execution"),
        new Claim("execution_identity", new JsonObject
        {
            ["actor"] = new JsonObject { ["kind"] = "agent", ["id"] = "nova", ["entityId"] = agentId },
            ["beneficiary"] = new JsonObject { ["kind"] = "user", ["id"] = userId },
            ["context"] = new JsonArray(new JsonObject { ["kind"] = "discussion", ["id"] = "discussion-1" }),
        }.ToJsonString()),
    ], "Bearer"));

    /// <summary>A signed-in user's ordinary bearer, as RedCompute's settle callback carries the last
    /// browser user's.</summary>
    public static ClaimsPrincipal SignedIn(string userId) => new(new ClaimsIdentity(
        [new Claim("sub", userId), new Claim("name", "Someone")], "Bearer"));

    /// <summary>A loopback request with no bearer.</summary>
    public static ClaimsPrincipal LocalDefault() => new(new ClaimsIdentity(
        [new Claim("sub", "local-user"), new Claim("name", "Local User")], "LocalDefault"));

    public static (string Kind, string? Id)? ExecutionBeneficiary(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirst("execution_identity")?.Value;
        if (raw is null) return null;
        using var document = JsonDocument.Parse(raw);
        var beneficiary = document.RootElement.GetProperty("beneficiary");
        return (beneficiary.GetProperty("kind").GetString()!,
            beneficiary.TryGetProperty("id", out var id) ? id.GetString() : null);
    }

    /// <summary>A request accessor holding <paramref name="caller"/>, or no request at all.</summary>
    public static HttpContextAccessor InFlight(ClaimsPrincipal? caller)
        => new() { HttpContext = caller is null ? null : new DefaultHttpContext { User = caller } };
}
