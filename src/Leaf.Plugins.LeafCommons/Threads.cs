using System.Text.Json.Nodes;

namespace Leaf.Plugins.LeafCommons;

public sealed record PublicThreadItem(
    string ThreadRef,
    string ChannelName,
    string Title,
    DateTimeOffset LastActivityAt,
    string LastSnippet,
    bool Unread,
    string? Scenario,
    string? AgentId = null,
    string? AgentName = null);

public sealed record PublicThreadAuthor(
    string Kind,
    string Name,
    string? AvatarUrl = null);

public sealed record PublicThreadMessage(
    string Id,
    DateTimeOffset At,
    PublicThreadAuthor Author,
    string Text,
    string? Outcome = null,
    string? Reason = null);

public sealed record PublicThread(
    string ThreadRef,
    string Title,
    string ChannelName,
    IReadOnlyList<PublicThreadMessage> Messages,
    string? AgentId = null);

public sealed class PublicNovaThreads(RelayHttpClient relay, CommonsStateStore state)
{
    public async Task<IReadOnlyList<PublicThreadItem>> ListAsync(
        CommonsSettings settings, CancellationToken ct = default)
    {
        IReadOnlyList<RelayThreadSummary> remote;
        try { remote = await relay.GetThreadsAsync(ct); }
        catch (RelayFeatureUnavailableException) { remote = []; }

        var outcomes = Outcomes(await state.ListAuditsAsync(ct))
            .Where(item => item.AgentEntityId is null || string.Equals(item.AgentEntityId,
                settings.AgentEntityId, StringComparison.OrdinalIgnoreCase)).ToArray();
        var byThread = outcomes.GroupBy(item => item.ThreadRef, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.At).ToArray(), StringComparer.Ordinal);
        var items = remote.Select(item =>
        {
            byThread.TryGetValue(item.ThreadRef, out var local);
            var latest = local?.FirstOrDefault();
            return new PublicThreadItem(
                item.ThreadRef,
                item.ChannelName,
                string.IsNullOrWhiteSpace(item.Title) ? item.ThreadRef : item.Title,
                latest is not null && latest.At > item.LastActivityAt ? latest.At : item.LastActivityAt,
                item.LastSnippet,
                item.Unread,
                item.Scenario ?? latest?.Scenario, settings.AgentEntityId,
                AgentName(settings));
        }).ToList();

        var remoteRefs = remote.Select(item => item.ThreadRef).ToHashSet(StringComparer.Ordinal);
        foreach (var (threadRef, local) in byThread.Where(pair => !remoteRefs.Contains(pair.Key)))
        {
            var latest = local[0];
            items.Add(new(threadRef, "", threadRef, latest.At,
                OutcomeSnippet(latest.Outcome, latest.Reason), false, latest.Scenario,
                settings.AgentEntityId, AgentName(settings)));
        }
        return items.OrderByDescending(item => item.LastActivityAt).ToArray();
    }

    public async Task<PublicThread> GetAsync(
        string threadRef, CommonsSettings settings, CancellationToken ct = default)
    {
        IReadOnlyList<RelayThreadSummary> summaries;
        IReadOnlyList<RelayThreadMessage> remote;
        try { summaries = await relay.GetThreadsAsync(ct); }
        catch (RelayFeatureUnavailableException) { summaries = []; }
        try { remote = await relay.GetThreadMessagesAsync(threadRef, ct); }
        catch (RelayFeatureUnavailableException) { remote = []; }

        var audits = Outcomes(await state.ListAuditsAsync(ct))
            .Where(item => string.Equals(item.ThreadRef, threadRef, StringComparison.Ordinal))
            .Where(item => item.AgentEntityId is null || string.Equals(item.AgentEntityId,
                settings.AgentEntityId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var delivered = audits.Where(item => item.MessageId is not null)
            .GroupBy(item => item.MessageId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.At).First(), StringComparer.Ordinal);

        var messages = remote.Select(item =>
        {
            delivered.TryGetValue(item.Id, out var local);
            var self = item.Author.Kind == "envoy"
                && ((!string.IsNullOrWhiteSpace(settings.EnvoyId)
                        && string.Equals(item.Author.EnvoyId, settings.EnvoyId, StringComparison.Ordinal))
                    || string.Equals(item.Author.Name, settings.Persona, StringComparison.OrdinalIgnoreCase));
            var kind = item.Author.Kind == "human" ? "human" : self ? "self" : "envoy";
            return new PublicThreadMessage(item.Id, item.At,
                new(kind, item.Author.Name, item.Author.AvatarUrl), item.Text,
                local?.Outcome, local?.Reason);
        }).ToList();

        var deliveredIds = remote.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var outcome in audits.Where(item => item.MessageId is null || !deliveredIds.Contains(item.MessageId)))
        {
            messages.Add(new($"audit-{outcome.Id:N}", outcome.At,
                new("self", settings.Persona), "", outcome.Outcome, outcome.Reason));
        }

        foreach (var digest in await state.ListDigestsForThreadAsync(threadRef, ct,
            settings.AgentEntityId))
        {
            messages.Add(new($"digest-{digest.Id:N}", digest.CreatedAt,
                new("self", "Public Nova digest"), digest.Content));
        }

        var summary = summaries.FirstOrDefault(item => string.Equals(item.ThreadRef, threadRef, StringComparison.Ordinal));
        return new(threadRef,
            string.IsNullOrWhiteSpace(summary?.Title) ? threadRef : summary.Title,
            summary?.ChannelName ?? "",
            messages.OrderBy(item => item.At).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray(),
            settings.AgentEntityId);
    }

    private static IReadOnlyList<LocalOutcome> Outcomes(IReadOnlyList<StoredAudit> audits)
    {
        var items = new List<LocalOutcome>();
        foreach (var audit in audits)
        {
            var thread = String(audit.Metadata, "thread");
            if (string.IsNullOrWhiteSpace(thread)) continue;
            var scenario = String(audit.Metadata, "scenario");
            var agentEntityId = String(audit.Metadata, "agent_entity_id");
            switch (audit.Event)
            {
                case "envoy_blocked":
                    items.Add(new(audit.Id, thread, audit.CreatedAt, "blocked",
                        String(audit.Metadata, "reason") ?? "filter_blocked", null, scenario, agentEntityId));
                    break;
                case "envoy_failed":
                    items.Add(new(audit.Id, thread, audit.CreatedAt, "failed",
                        String(audit.Metadata, "reason") ?? "unknown_failure", null, scenario, agentEntityId));
                    break;
                case "envoy_done" when String(audit.Metadata, "reason") == "scenario_disabled":
                    items.Add(new(audit.Id, thread, audit.CreatedAt, "blocked",
                        "scenario_disabled", null, scenario, agentEntityId));
                    break;
                case "relay_post":
                    var accepted = Boolean(audit.Metadata, "accepted") == true;
                    var delivery = String(audit.Metadata, "delivery");
                    items.Add(new(audit.Id, thread, audit.CreatedAt,
                        accepted && delivery?.Contains("fallback", StringComparison.Ordinal) == true
                            ? "fallback"
                            : accepted ? "posted" : "failed",
                        accepted ? null : String(audit.Metadata, "code") ?? "relay_rejected",
                        String(audit.Metadata, "messageId"), scenario, agentEntityId));
                    break;
            }
        }
        return items;
    }

    private static string OutcomeSnippet(string outcome, string? reason)
        => string.IsNullOrWhiteSpace(reason) ? outcome : $"{outcome}: {reason}";

    private static string? String(JsonObject data, string key)
        => data[key] is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;

    private static bool? Boolean(JsonObject data, string key)
        => data[key] is JsonValue value && value.TryGetValue<bool>(out var result) ? result : null;

    private static string AgentName(CommonsSettings settings)
    {
        var separator = settings.Persona.IndexOf(" · ", StringComparison.Ordinal);
        return separator > 0 ? settings.Persona[..separator] : settings.Persona;
    }

    private sealed record LocalOutcome(
        Guid Id, string ThreadRef, DateTimeOffset At, string Outcome,
        string? Reason, string? MessageId, string? Scenario, string? AgentEntityId);
}

public sealed class PublicAgentThreads(
    RelayClientFactory relays, CommonsStateStore state, AgentDirectory directory)
{
    public async Task<IReadOnlyList<PublicThreadItem>> ListAsync(
        string? agentEntityId = null, CancellationToken ct = default)
    {
        var configuration = await state.GetConfigurationAsync(ct);
        var liveNames = string.IsNullOrWhiteSpace(configuration.Owner.OwnerUserId)
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : (await directory.ListAsync(configuration.Owner.OwnerUserId, ct)).ToDictionary(
                item => item.AgentEntityId, item => item.Name, StringComparer.OrdinalIgnoreCase);
        var agents = configuration.Agents.Where(agent => agent.Enabled
            && (agentEntityId is null || string.Equals(agent.AgentEntityId, agentEntityId,
                StringComparison.OrdinalIgnoreCase))).ToArray();
        var result = new List<PublicThreadItem>();
        foreach (var agent in agents)
        {
            var service = new PublicNovaThreads(relays.For(agent.AgentEntityId), state);
            var items = await service.ListAsync(Legacy(configuration.Owner, agent), ct);
            result.AddRange(items.Select(item => item with
            {
                AgentName = liveNames.GetValueOrDefault(agent.AgentEntityId) ?? agent.AgentName,
            }));
        }
        return result.OrderByDescending(item => item.LastActivityAt).ToArray();
    }

    public async Task<PublicThread> GetAsync(string threadRef, CancellationToken ct = default)
    {
        var configuration = await state.GetConfigurationAsync(ct);
        var audits = await state.ListAuditsAsync(ct);
        var agentId = audits.Where(item => item.Metadata["thread"]?.GetValue<string>() == threadRef)
            .Select(item => item.Metadata["agent_entity_id"]?.GetValue<string>())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var candidates = configuration.Agents.Where(agent => agent.Enabled
            && (agentId is null || string.Equals(agent.AgentEntityId, agentId,
                StringComparison.OrdinalIgnoreCase))).ToArray();
        RelayApiException? lastError = null;
        foreach (var agent in candidates)
        {
            try
            {
                var service = new PublicNovaThreads(relays.For(agent.AgentEntityId), state);
                var thread = await service.GetAsync(threadRef, Legacy(configuration.Owner, agent), ct);
                if (thread.Messages.Count > 0 || candidates.Length == 1) return thread;
            }
            catch (RelayApiException error) { lastError = error; }
        }
        if (lastError is not null) throw lastError;
        return new(threadRef, threadRef, "", [], agentId);
    }

    private static CommonsSettings Legacy(CommonsOwnerSettings owner, PublicAgentSettings agent)
        => new(owner.EntityId, owner.RelayBaseUrl, agent.Credential, agent.ConnectionStatus,
            agent.DisplayPersona(owner.PublicName), agent.Avatar, agent.ScenarioToggles, agent.ShareableFolders,
            owner.PersonalWords, owner.Canaries, null, owner.LinkAllowlist,
            owner.OwnerUserId, agent.AgentEntityId, agent.AvatarSource, agent.AvatarContentHash,
            agent.AvatarSyncStatus, agent.AvatarSyncReason, agent.AvatarResolvedAssetId,
            agent.AvatarSyncedAt, owner.PublicName, agent.EnvoyId);
}
