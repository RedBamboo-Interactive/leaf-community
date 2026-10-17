using Leaf.Sdk;

namespace Leaf.Plugins.LeafCommons;

public sealed class CommonsRuntime(
    IToolFreeModelClient model, ShareableStore shareable, RelayHttpClient relay,
    OutboundFilter filter, CommonsStateStore state, IDigestSink digests)
{
    public async Task<EnvoyRunResult> HandleAsync(EnvoyRunRequest request, CancellationToken ct = default)
    {
        var settings = await state.GetSettingsAsync(ct);
        var toggle = request.Kind is "consult_reply" ? "consult" : request.Kind is "bug_report" ? "bug" : request.Kind;
        if (settings.ScenarioToggles.TryGetValue(toggle, out var enabled) && !enabled)
        {
            await state.RecordAsync("scenario_disabled", new Dictionary<string, object?> { ["scenario"] = toggle, ["thread"] = request.ThreadRef }, ct);
            return new("disabled", 0, toggle);
        }
        var runner = new EnvoyRunner(model, shareable, relay, filter, state, settings.PersonalWords, settings.Canaries);
        var result = await runner.RunAsync(request, ct);
        if (result.State == "blocked" && !string.IsNullOrWhiteSpace(request.ThreadRef))
            await digests.DeliverAsync(new("owner_notice", $"An Envoy post was blocked locally by rule `{result.Summary}`. No blocked content was sent.", request.ThreadRef), ct);
        if (result.State != "blocked" && !string.IsNullOrWhiteSpace(request.ThreadRef) && !string.IsNullOrWhiteSpace(result.Summary))
        {
            var type = request.Kind == "owner_notice" ? "owner_notice" : request.Kind == "event" ? "announcement" : "consult_answer";
            await digests.DeliverAsync(new(type, result.Summary, request.ThreadRef), ct);
        }
        return result;
    }

    public async Task<EnvoyRunResult> DispatchApprovedBriefAsync(LeafEntity brief, CancellationToken ct = default)
    {
        if (brief.Data["state"]?.GetValue<string>() != "approved") throw new InvalidOperationException("Only approved briefs may reach the Envoy");
        var result = await HandleAsync(new(null, brief.Data["kind"]!.GetValue<string>(), brief.Data["content"]!.GetValue<string>(), TriggerId: brief.Id.ToString("N")), ct);
        await state.RecordAsync("brief_envoy_result", new Dictionary<string, object?> { ["briefId"] = brief.Id, ["state"] = result.State, ["steps"] = result.Steps }, ct);
        return result;
    }
}
