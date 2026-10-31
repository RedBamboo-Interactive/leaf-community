namespace Leaf.Plugins.LeafCommons;

/// <summary>
/// A post an Agent writes from a private discussion with its owner. <paramref name="Channel"/> is a
/// channel name or ID; without it the kind picks one. <paramref name="OwnerRequested"/> says the
/// owner asked for this post, so it goes out even when the Agent does not start conversations.
/// </summary>
public sealed record BriefSubmission(
    string Kind, string Content, string? Title = null, string[]? Addresses = null,
    string? Channel = null, bool OwnerRequested = false);
public sealed record BriefPostResult(
    string Decision, string? MessageId = null, string? Reason = null);

public sealed class PublicAgentRuntime(
    IToolFreeModelClient model,
    AgentShareableStores shareables,
    RelayClientFactory relays,
    OutboundFilter filter,
    CommonsStateStore state,
    IDigestSink digests,
    PublicAgentConversationCoordinator? conversations = null,
    CommonsOutboundReview? outboundReview = null)
{
    private readonly CommonsOutboundReview review = outboundReview ?? CommonsOutboundReview.Unavailable;

    public async Task HandleListeningEventAsync(
        string agentEntityId, RelayListeningEvent listening, CancellationToken ct = default)
    {
        var configuration = await state.GetConfigurationAsync(ct);
        var agent = configuration.Agents.FirstOrDefault(item => item.Enabled && string.Equals(
            item.AgentEntityId, agentEntityId, StringComparison.OrdinalIgnoreCase));
        if (agent is null) return;
        // A host, filter or membership suspension notice says who suspended the Envoy; the relay's
        // later refusals do not, so the source is kept for the owner's settings row.
        if (listening.SuspendedBy is SuspensionSources.Host or SuspensionSources.Filter or SuspensionSources.Membership
            && agent.SuspendedBy != listening.SuspendedBy)
            agent = await state.SavePublicAgentAsync(agent with { SuspendedBy = listening.SuspendedBy }, ct);
        var decision = filter.Check(listening.Content, configuration.Owner.PersonalWords,
            configuration.Owner.Canaries);
        if (!decision.Allowed)
        {
            await state.RecordAsync("listening_event_blocked", Metadata(agentEntityId,
                ("event_id", listening.Id), ("reason", decision.Rule)), ct);
            return;
        }
        var channel = string.IsNullOrWhiteSpace(listening.Channel)
            ? "relay" : listening.Channel.Trim();
        var snippet = string.Join(' ', listening.Content.Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (snippet.Length > 600) snippet = snippet[..599] + "…";
        var ownerNotice = string.Equals(listening.Type, "owner_notice", StringComparison.Ordinal);
        var author = string.IsNullOrWhiteSpace(listening.AuthorDisplayName)
            ? "a community member" : listening.AuthorDisplayName.Trim();
        var update = string.Equals(listening.Operation, "update", StringComparison.Ordinal)
            ? "Updated announcement" : "New announcement";
        var content = ownerNotice
            ? $"Relay notice: {snippet}"
            : $"{update} in #{channel} from {author}: {snippet}";
        await digests.DeliverAsync(new(ownerNotice ? "owner_notice" : "announcement",
            content, null, agentEntityId, channel, listening.MessageId), ct);
    }

    public async Task<EnvoyRunResult> HandleAsync(
        string agentEntityId, EnvoyRunRequest request, CancellationToken ct = default)
    {
        var configuration = await state.GetConfigurationAsync(ct);
        var agent = configuration.Agents.FirstOrDefault(item => item.Enabled && string.Equals(
            item.AgentEntityId, agentEntityId, StringComparison.OrdinalIgnoreCase));
        if (agent is null) return new("disabled", 0, "agent_not_public");
        // A ping means someone spoke to the Agent, and so does an open invitation, which speaks
        // to every Agent at once (Nova, 2026-10-31); it stays unaddressed otherwise. Any other
        // relay event would be the Agent joining in on its own.
        var switchedOff = request.Addressed || request.OpenInvitation
            ? agent.Switches.AnswersWhenSpokenTo ? null : "answers_disabled"
            : agent.Switches.StartsConversations ? null : "starts_disabled";
        if (switchedOff is not null)
        {
            await state.RecordAsync("envoy_done", Metadata(agentEntityId,
                ("thread", request.ThreadRef), ("steps", 0), ("reason", switchedOff)), ct);
            return new("disabled", 0, switchedOff);
        }
        if (!agent.RestrictedMode)
            return await (conversations ?? throw new InvalidOperationException(
                "Full Public Agent mode requires the external conversation coordinator"))
                .SubmitAsync(agent, configuration.Owner, request, ct);
        var audit = new AgentScopedAudit(state, agentEntityId);
        // Every post the restricted runner makes passes the outbound Sentinel first; a refusal
        // reads as a rejected post, which the runner answers with its fixed fallback.
        var runner = new EnvoyRunner(model, shareables.For(agentEntityId),
            new ReviewedEnvoyRelay(relays.For(agentEntityId), review, agentEntityId, audit,
                configuration.Owner.OwnerUserId),
            filter, audit, configuration.Owner.PersonalWords, configuration.Owner.Canaries,
            speakerName: agent.DisplayPersona(configuration.Owner.PublicName));
        var result = await runner.RunAsync(request, ct);
        if (result.State == "blocked" && !string.IsNullOrWhiteSpace(request.ThreadRef))
            await digests.DeliverAsync(new("owner_notice",
                $"A public Agent reply was blocked locally by rule `{result.Summary}`. No blocked content was sent.",
                request.ThreadRef, agentEntityId), ct);
        if (result.State == "failed" && request.Addressed && !string.IsNullOrWhiteSpace(request.ThreadRef))
            await digests.DeliverAsync(new("owner_notice",
                $"I couldn't answer thread {request.ThreadRef}: {result.Summary ?? "unknown_failure"}.",
                request.ThreadRef, agentEntityId), ct);
        if (result.State is not ("blocked" or "failed") && !string.IsNullOrWhiteSpace(request.ThreadRef)
            && !string.IsNullOrWhiteSpace(result.Summary))
        {
            var type = request.Kind == "owner_notice" ? "owner_notice"
                : request.Kind == "event" ? "announcement" : "consult_answer";
            await digests.DeliverAsync(new(type, result.Summary, request.ThreadRef, agentEntityId), ct);
        }
        return result;
    }

    public async Task<BriefPostResult> PostBriefAsync(
        BriefSubmission brief, LeafAgentCaller caller, CancellationToken ct = default)
    {
        ValidateBrief(brief);
        var configuration = await state.GetConfigurationAsync(ct);
        var agent = configuration.Agents.FirstOrDefault(item => item.Enabled && string.Equals(
            item.AgentEntityId, caller.AgentEntityId, StringComparison.OrdinalIgnoreCase));
        if (agent is null) return new("blocked", Reason: "agent_not_public");
        var addresses = brief.Addresses ?? [];
        var dispatchId = Guid.NewGuid();
        if (!brief.OwnerRequested && !agent.Switches.StartsConversations)
            return await FinishBriefAsync(brief, agent.AgentEntityId, "blocked", null,
                "starts_disabled", dispatchId, ct);

        var decision = filter.Check(brief.Content, configuration.Owner.PersonalWords,
            configuration.Owner.Canaries);
        if (decision.Allowed && brief.Title is not null)
            decision = filter.Check(brief.Title, configuration.Owner.PersonalWords,
                configuration.Owner.Canaries);
        if (decision.Allowed)
            foreach (var address in addresses)
            {
                decision = filter.Check(address, configuration.Owner.PersonalWords,
                    configuration.Owner.Canaries);
                if (!decision.Allowed) break;
            }
        await state.RecordAsync("filter_decision", Metadata(agent.AgentEntityId,
            ("dispatchId", dispatchId), ("allowed", decision.Allowed),
            ("rule", decision.Rule), ("alarm", decision.Alarm)), ct);
        if (!decision.Allowed)
            return await FinishBriefAsync(brief, agent.AgentEntityId, "blocked", null,
                decision.Rule ?? "filter_blocked", dispatchId, ct);

        var relay = relays.For(agent.AgentEntityId);
        var channelId = await BriefChannelAsync(relay, brief, ct);
        if (channelId is null)
            return await FinishBriefAsync(brief, agent.AgentEntityId, "rejected", null,
                "unknown_channel", dispatchId, ct);
        var verdict = await review.ReviewAsync(agent.AgentEntityId, channelId,
            $"{brief.Title}\n\n{brief.Content}" + (addresses.Length > 0 ? $"\n\n[addresses] {string.Join(", ", addresses)}" : ""),
            $"commons-brief:{dispatchId:N}", ct, ownerUserId: configuration.Owner.OwnerUserId);
        await state.RecordAsync("outbound_review", Metadata(agent.AgentEntityId,
            ("dispatchId", dispatchId), ("outcome", verdict.Review["outcome"]?.GetValue<string>()),
            ("available", !verdict.Unavailable)), ct);
        if (!verdict.Allowed)
            return await FinishBriefAsync(brief, agent.AgentEntityId, "blocked", null,
                verdict.Unavailable ? "sentinel_unavailable" : "sentinel_held", dispatchId, ct);
        var posted = await relay.OpenAsync(channelId, brief.Title,
            brief.Content, addresses, $"leaf-commons-brief-{dispatchId:N}", ct);
        await state.RecordAsync("relay_post", Metadata(agent.AgentEntityId,
            ("dispatchId", dispatchId), ("accepted", posted.Accepted), ("code", posted.Code),
            ("messageId", posted.MessageId), ("threadId", posted.ThreadId)), ct);
        return posted.Accepted
            ? await FinishBriefAsync(brief, agent.AgentEntityId, "posted", posted.MessageId,
                null, dispatchId, ct)
            : await FinishBriefAsync(brief, agent.AgentEntityId, "rejected", null,
                posted.Code ?? "relay_rejected", dispatchId, ct);
    }

    private async Task<BriefPostResult> FinishBriefAsync(BriefSubmission brief, string agentEntityId,
        string outcome, string? messageId, string? reason, Guid dispatchId, CancellationToken ct)
    {
        var history = await state.RecordBriefAsync(brief.Kind, brief.Title, brief.Content,
            outcome, messageId, reason, ct, agentEntityId);
        await state.RecordAsync("brief_dispatch", Metadata(agentEntityId,
            ("briefId", history.Id), ("dispatchId", dispatchId), ("outcome", outcome),
            ("messageId", messageId), ("reason", reason)), ct);
        return new(outcome, messageId, reason);
    }

    private static Dictionary<string, object?> Metadata(string agentEntityId,
        params (string Key, object? Value)[] values)
    {
        var result = new Dictionary<string, object?> { ["agent_entity_id"] = agentEntityId };
        foreach (var (key, value) in values) result[key] = value;
        return result;
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

    /// <summary>
    /// The channel a brief names, by ID or name, else the one its kind suggests: questions go to the
    /// relay's default consult forum, the rest to the first channel whose name fits the kind.
    /// </summary>
    private static async Task<string?> BriefChannelAsync(RelayHttpClient relay, BriefSubmission brief,
        CancellationToken ct)
    {
        var catalog = await relay.GetChannelsAsync(ct);
        var named = brief.Channel?.Trim().TrimStart('#');
        if (!string.IsNullOrEmpty(named))
            return catalog.Channels.FirstOrDefault(channel => channel.Id == named
                || string.Equals(channel.Name, named, StringComparison.OrdinalIgnoreCase))?.Id;
        string[] hints = brief.Kind switch
        {
            "consult" or "open_question" => [],
            "bug" or "bug_report" => ["bug"],
            "showcase" => ["showcase"],
            _ => ["intro", "general", "général"],
        };
        return hints.Length == 0 ? catalog.DefaultChannelId : hints.Select(hint => catalog.Channels
                .FirstOrDefault(channel => channel.Name.Contains(hint, StringComparison.OrdinalIgnoreCase))?.Id)
            .FirstOrDefault(id => id is not null);
    }

    private sealed class AgentScopedAudit(ICommonsAudit inner, string agentEntityId) : ICommonsAudit
    {
        public Task RecordAsync(string eventName, IReadOnlyDictionary<string, object?> safeMetadata,
            CancellationToken ct = default)
        {
            var metadata = new Dictionary<string, object?>(safeMetadata)
                { ["agent_entity_id"] = agentEntityId };
            return inner.RecordAsync(eventName, metadata, ct);
        }
    }
}
