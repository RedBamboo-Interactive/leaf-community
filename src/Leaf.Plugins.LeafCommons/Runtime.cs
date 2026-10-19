using Leaf.Sdk;

namespace Leaf.Plugins.LeafCommons;

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

    public async Task<EnvoyRunResult> DispatchApprovedBriefAsync(LeafEntity brief, CancellationToken ct = default)
    {
        if (brief.Data["state"]?.GetValue<string>() != "approved") throw new InvalidOperationException("Only approved briefs may be dispatched");
        var settings = await state.GetSettingsAsync(ct);
        var kind = brief.Data["kind"]?.GetValue<string>() ?? throw new InvalidDataException("Brief kind is missing");
        var scenario = ScenarioFor(kind);
        var toggle = scenario == "open_question" ? "consult" : scenario;
        if (settings.ScenarioToggles.TryGetValue(toggle, out var enabled) && !enabled)
        {
            await state.RecordAsync("scenario_disabled", new Dictionary<string, object?> { ["scenario"] = toggle, ["briefId"] = brief.Id }, ct);
            return new("disabled", 0, toggle);
        }

        var content = brief.Data["content"]?.GetValue<string>() ?? throw new InvalidDataException("Brief content is missing");
        var title = brief.Data["title"]?.GetValue<string>();
        var addresses = brief.Data["addresses"] is System.Text.Json.Nodes.JsonArray array
            ? array.Select(x => x?.GetValue<string>()).Where(x => x is not null).Cast<string>().ToArray()
            : [];
        var decision = filter.Check(content, settings.PersonalWords, settings.Canaries);
        if (decision.Allowed && title is not null) decision = filter.Check(title, settings.PersonalWords, settings.Canaries);
        if (decision.Allowed)
            foreach (var address in addresses)
            {
                decision = filter.Check(address, settings.PersonalWords, settings.Canaries);
                if (!decision.Allowed) break;
            }
        await state.RecordAsync("filter_decision", new Dictionary<string, object?> { ["briefId"] = brief.Id, ["allowed"] = decision.Allowed, ["rule"] = decision.Rule, ["alarm"] = decision.Alarm }, ct);
        if (!decision.Allowed) return new("blocked", 0, decision.Rule);

        var posted = await relay.OpenAsync(scenario, title, content, addresses, $"leaf-commons-brief-{brief.Id:N}", ct);
        await state.RecordAsync("relay_post", new Dictionary<string, object?> { ["briefId"] = brief.Id, ["accepted"] = posted.Accepted, ["code"] = posted.Code, ["messageId"] = posted.MessageId, ["threadId"] = posted.ThreadId }, ct);
        var result = posted.Accepted ? new EnvoyRunResult("posted", 0, posted.ThreadId) : new EnvoyRunResult(posted.Code ?? "refused", 0);
        await state.RecordAsync("brief_dispatch", new Dictionary<string, object?> { ["briefId"] = brief.Id, ["state"] = result.State, ["steps"] = 0 }, ct);
        return result;
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
