namespace Leaf.Plugins.LeafCommons;

public sealed record BriefSubmission(
    string Kind, string Content, string? Title = null, string[]? Addresses = null);
public sealed record BriefPostResult(
    string Decision, string? MessageId = null, string? Reason = null);

public sealed class CommonsRuntime(
    IToolFreeModelClient model, ShareableStore shareable, IEnvoyRelay relay,
    OutboundFilter filter, CommonsStateStore state, IDigestSink digests)
{
    public async Task<EnvoyRunResult> HandleAsync(EnvoyRunRequest request, CancellationToken ct = default)
    {
        var settings = await state.GetSettingsAsync(ct);
        var toggle = request.Kind is "consult_reply" ? "consult" : request.Kind is "bug_report" ? "bug" : request.Kind;
        if (settings.ScenarioToggles.TryGetValue(toggle, out var enabled) && !enabled)
        {
            await state.RecordAsync("scenario_disabled", new Dictionary<string, object?> { ["scenario"] = toggle, ["thread"] = request.ThreadRef }, ct);
            await state.RecordAsync("envoy_done", new Dictionary<string, object?>
                { ["thread"] = request.ThreadRef, ["steps"] = 0, ["reason"] = "scenario_disabled" }, ct);
            return new("disabled", 0, toggle);
        }
        var runner = new EnvoyRunner(model, shareable, relay, filter, state, settings.PersonalWords, settings.Canaries);
        var result = await runner.RunAsync(request, ct);
        if (result.State == "blocked" && !string.IsNullOrWhiteSpace(request.ThreadRef))
            await digests.DeliverAsync(new("owner_notice", $"An Envoy post was blocked locally by rule `{result.Summary}`. No blocked content was sent.", request.ThreadRef), ct);
        if (result.State == "failed" && request.Addressed && !string.IsNullOrWhiteSpace(request.ThreadRef))
            await digests.DeliverAsync(new("owner_notice", $"I couldn't answer thread {request.ThreadRef}: {result.Summary ?? "unknown_failure"}.", request.ThreadRef), ct);
        if (result.State is not ("blocked" or "failed") && !string.IsNullOrWhiteSpace(request.ThreadRef) && !string.IsNullOrWhiteSpace(result.Summary))
        {
            var type = request.Kind == "owner_notice" ? "owner_notice" : request.Kind == "event" ? "announcement" : "consult_answer";
            await digests.DeliverAsync(new(type, result.Summary, request.ThreadRef), ct);
        }
        return result;
    }

    public async Task<BriefPostResult> PostBriefAsync(
        BriefSubmission brief, LeafAgentCaller caller, CancellationToken ct = default)
    {
        ValidateBrief(brief);
        var addresses = brief.Addresses ?? [];
        var dispatchId = Guid.NewGuid();
        var settings = await state.GetSettingsAsync(ct);
        var scenario = ScenarioFor(brief.Kind);
        var toggle = scenario == "open_question" ? "consult" : scenario;
        if (settings.ScenarioToggles.TryGetValue(toggle, out var enabled) && !enabled)
        {
            await state.RecordAsync("scenario_disabled", new Dictionary<string, object?>
                { ["scenario"] = toggle, ["dispatchId"] = dispatchId }, ct);
            return await FinishBriefAsync(brief, "blocked", null, "scenario_disabled", dispatchId, ct);
        }

        var decision = filter.Check(brief.Content, settings.PersonalWords, settings.Canaries);
        if (decision.Allowed && brief.Title is not null)
            decision = filter.Check(brief.Title, settings.PersonalWords, settings.Canaries);
        if (decision.Allowed)
            foreach (var address in addresses)
            {
                decision = filter.Check(address, settings.PersonalWords, settings.Canaries);
                if (!decision.Allowed) break;
            }
        await state.RecordAsync("filter_decision", new Dictionary<string, object?>
            { ["dispatchId"] = dispatchId, ["allowed"] = decision.Allowed, ["rule"] = decision.Rule, ["alarm"] = decision.Alarm }, ct);
        if (!decision.Allowed)
            return await FinishBriefAsync(brief, "blocked", null,
                decision.Rule ?? "filter_blocked", dispatchId, ct);

        var posted = await relay.OpenAsync(scenario, brief.Title, brief.Content, addresses,
            $"leaf-commons-brief-{dispatchId:N}", ct);
        await state.RecordAsync("relay_post", new Dictionary<string, object?>
        {
            ["dispatchId"] = dispatchId,
            ["accepted"] = posted.Accepted,
            ["code"] = posted.Code,
            ["messageId"] = posted.MessageId,
            ["threadId"] = posted.ThreadId
        }, ct);
        return posted.Accepted
            ? await FinishBriefAsync(brief, "posted", posted.MessageId, null, dispatchId, ct)
            : await FinishBriefAsync(brief, "rejected", null,
                posted.Code ?? "relay_rejected", dispatchId, ct);
    }

    private async Task<BriefPostResult> FinishBriefAsync(
        BriefSubmission brief, string outcome, string? messageId, string? reason,
        Guid dispatchId, CancellationToken ct)
    {
        var history = await state.RecordBriefAsync(
            brief.Kind, brief.Title, brief.Content, outcome, messageId, reason, ct);
        await state.RecordAsync("brief_dispatch", new Dictionary<string, object?>
        {
            ["briefId"] = history.Id,
            ["dispatchId"] = dispatchId,
            ["outcome"] = outcome,
            ["messageId"] = messageId,
            ["reason"] = reason
        }, ct);
        return new(outcome, messageId, reason);
    }

    private static void ValidateBrief(BriefSubmission brief)
    {
        if (!new[] { "showcase", "consult", "open_question", "bug", "bug_report", "intro_draft" }.Contains(brief.Kind))
            throw new ArgumentException("Unsupported brief kind");
        if (string.IsNullOrWhiteSpace(brief.Content) || brief.Content.Length > 12_000)
            throw new ArgumentException("Brief content must be 1-12000 characters");
        if (brief.Kind is "consult" or "open_question" or "bug" or "bug_report")
        {
            if (string.IsNullOrWhiteSpace(brief.Title) || brief.Title.Length > 100)
                throw new ArgumentException("Thread-opening briefs require a 1-100 character title");
        }
        else if (brief.Title is { Length: > 100 })
            throw new ArgumentException("Brief title must be at most 100 characters");
        if (brief.Addresses is { Length: > 10 } || brief.Addresses?.Any(string.IsNullOrWhiteSpace) == true)
            throw new ArgumentException("Addresses must contain at most 10 non-empty values");
    }

    private static string ScenarioFor(string kind) => kind switch
    {
        "showcase" => "showcase",
        "consult" => "consult",
        "open_question" => "open_question",
        "bug" or "bug_report" => "bug",
        "intro_draft" => "intro",
        _ => throw new InvalidDataException("Unsupported brief kind")
    };
}
