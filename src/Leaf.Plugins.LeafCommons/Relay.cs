using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.LeafCommons;

public sealed record OAuthExchange(
    string Credential, string Status, string PersonaName, string PublicName,
    string EnvoyId, string? AvatarObjectKey, string? AgentName = null);
public sealed record RelayProfile(string PublicName, string PersonaName, string? AgentName = null);
public sealed record RelayThreadSummary(
    string ThreadRef, string ChannelName, string Title, DateTimeOffset LastActivityAt,
    string LastSnippet, bool Unread, string? Scenario);
/// <summary>A channel the relay lets this Envoy write in.</summary>
public sealed record RelayChannel(string Id, string Name, string Kind);
public sealed record RelayChannels(IReadOnlyList<RelayChannel> Channels, string? DefaultChannelId);
/// <param name="IsYourOwner">The relay matched this human's Discord ID to the reading Envoy's
/// verified owner. Never inferred from a name.</param>
public sealed record RelayThreadAuthor(string Kind, string Name, string? AvatarUrl = null, string? EnvoyId = null,
    bool IsYourOwner = false);
public sealed record RelayThreadMessage(
    string Id, DateTimeOffset At, RelayThreadAuthor Author, string Text, string? Scenario = null,
    IReadOnlyList<RelayImageAttachment>? Images = null);

public sealed class RelayApiException(string reason, string message, int statusCode)
    : Exception(message)
{
    public string Reason { get; } = reason;
    public int StatusCode { get; } = statusCode;
}

public sealed class RelayFeatureUnavailableException(string feature)
    : Exception($"Relay feature '{feature}' is unavailable");

public sealed class RelayHttpClient(
    HttpClient http,
    Func<CancellationToken, Task<(Uri BaseUri, string? Credential)>> settings,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IEnvoyRelay, IAvatarUploader
{
    public Task<RelayProfile> ReadProfileAsync(CancellationToken ct = default)
        => PatchProfileAsync(null, null, ct);

    public async Task<Uri> BuildAuthorizeUriAsync(Uri callback,
        CancellationToken ct = default, string? agentEntityId = null, string? agentName = null)
    {
        var (baseUri, _) = await settings(ct).ConfigureAwait(false);
        if (!(callback.Scheme == "leaf" || (callback.Scheme == "http" && callback.IsLoopback))) throw new ArgumentException("OAuth callback must be leaf: or loopback HTTP");
        var query = "/v1/oauth/authorize?connectorRedirectUri=" + Uri.EscapeDataString(callback.ToString());
        if (!string.IsNullOrWhiteSpace(agentEntityId))
            query += "&agentEntityId=" + Uri.EscapeDataString(agentEntityId);
        if (!string.IsNullOrWhiteSpace(agentName))
            query += "&agentName=" + Uri.EscapeDataString(agentName);
        return new Uri(baseUri, query);
    }

    public async Task<OAuthExchange> ExchangeAsync(string exchangeCode, CancellationToken ct = default)
    {
        var (baseUri, _) = await settings(ct).ConfigureAwait(false);
        using var response = await http.PostAsJsonAsync(new Uri(baseUri, "/v1/oauth/exchange"), new { exchangeCode }, ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement; var envoy = root.GetProperty("envoy");
        var persona = envoy.GetProperty("personaName").GetString()!;
        var publicName = envoy.TryGetProperty("publicName", out var publicNameNode)
            ? publicNameNode.GetString()
            : CommonsSettings.PublicNameFromPersona(persona);
        return new(root.GetProperty("credential").GetString()!, envoy.GetProperty("status").GetString()!,
            persona, publicName ?? "", envoy.GetProperty("id").GetString()!,
            envoy.TryGetProperty("avatarObjectKey", out var avatar) ? avatar.GetString() : null,
            String(envoy, "agentName"));
    }

    public async Task<RelayProfile> UpdateProfileAsync(string publicName, CancellationToken ct = default)
    {
        var (baseUri, credential) = await settings(ct).ConfigureAwait(false);
        return await UpdateProfileAtAsync(baseUri, credential, publicName, ct);
    }

    public async Task<RelayProfile> UpdateProfileAtAsync(
        Uri baseUri, string? credential, string publicName, CancellationToken ct = default)
    {
        using var response = await SendAuthenticatedAsync(baseUri, credential, HttpMethod.Patch,
            "/v1/profile", JsonContent.Create(new { publicName }), ct);
        using var json = await ReadJsonAsync(response, ct);
        if (!response.IsSuccessStatusCode) throw RelayError(response, json.RootElement, "profile_update_failed");
        var envoy = json.RootElement.TryGetProperty("envoy", out var wrapped)
            ? wrapped
            : json.RootElement;
        var acceptedName = envoy.TryGetProperty("publicName", out var name)
            ? name.GetString()
            : publicName;
        var persona = envoy.TryGetProperty("personaName", out var personaName)
            ? personaName.GetString()
            : $"Public Agent · {acceptedName}";
        return new(acceptedName ?? publicName, persona ?? $"Public Agent · {publicName}");
    }

    public async Task<RelayProfile> UpdateAgentProfileAsync(
        string publicName, string agentName, CancellationToken ct = default)
        => await PatchProfileAsync(publicName, agentName, ct);

    public async Task<RelayProfile> PatchProfileAsync(
        string? publicName, string? agentName, CancellationToken ct = default)
    {
        var (baseUri, credential) = await settings(ct).ConfigureAwait(false);
        var body = new JsonObject();
        if (publicName is not null) body["publicName"] = publicName;
        if (agentName is not null) body["agentName"] = agentName;
        using var response = await SendAuthenticatedAsync(baseUri, credential, HttpMethod.Patch,
            "/v1/profile", JsonContent.Create(body), ct);
        using var json = await ReadJsonAsync(response, ct);
        if (!response.IsSuccessStatusCode) throw RelayError(response, json.RootElement, "profile_update_failed");
        var envoy = json.RootElement.TryGetProperty("envoy", out var wrapped) ? wrapped : json.RootElement;
        var acceptedName = String(envoy, "publicName") ?? publicName
            ?? throw new InvalidDataException("Relay profile response omitted publicName");
        var acceptedAgentName = String(envoy, "agentName") ?? agentName;
        var persona = String(envoy, "personaName") ?? $"{acceptedAgentName} · {acceptedName}";
        return new(acceptedName, persona, acceptedAgentName);
    }

    public async Task SuspendAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Patch, "/v1/profile",
            JsonContent.Create(new { suspended = true }), ct);
        using var json = await ReadJsonAsync(response, ct);
        if (!response.IsSuccessStatusCode)
            throw RelayError(response, json.RootElement, "envoy_suspend_failed");
    }

    /// <summary>
    /// Lifts the owner's own switch-off with exactly <c>{"suspended":false}</c>. Returns the Envoy's
    /// status afterwards (<c>active</c>, or <c>pending</c> for a legacy pending Envoy). A host,
    /// filter or membership suspension is refused with 403 <c>envoy_suspended</c>.
    /// </summary>
    public async Task<string?> ResumeAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Patch, "/v1/profile",
            JsonContent.Create(new { suspended = false }), ct);
        using var json = await ReadJsonAsync(response, ct);
        if (!response.IsSuccessStatusCode)
            throw RelayError(response, json.RootElement, "envoy_resume_failed");
        return EnvoyStatus(json.RootElement);
    }

    /// <summary>
    /// The Envoy's relay status from an empty profile patch, which changes nothing. A suspended
    /// Envoy is refused with 403 <c>envoy_suspended</c>.
    /// </summary>
    public async Task<string?> ReadStatusAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Patch, "/v1/profile",
            JsonContent.Create(new JsonObject()), ct);
        using var json = await ReadJsonAsync(response, ct);
        if (!response.IsSuccessStatusCode)
            throw RelayError(response, json.RootElement, "profile_read_failed");
        return EnvoyStatus(json.RootElement);
    }

    private static string? EnvoyStatus(JsonElement root)
        => String(root.TryGetProperty("envoy", out var envoy) && envoy.ValueKind == JsonValueKind.Object
            ? envoy : root, "status");

    public async Task<AvatarUploadResult> UploadAvatarAsync(byte[] bytes, string contentType, CancellationToken ct = default)
    {
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var response = await SendAsync(HttpMethod.Put, "/v1/envoy/avatar", content, ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!response.IsSuccessStatusCode)
        {
            var reason = json.RootElement.TryGetProperty("error", out var error)
                ? error.GetString() ?? "avatar_upload_failed"
                : "avatar_upload_failed";
            throw new AvatarSyncException(reason);
        }
        var root = json.RootElement;
        return new(
            root.GetProperty("avatarKey").GetString()!,
            root.GetProperty("avatarUrl").GetString()!,
            root.GetProperty("contentHash").GetString()!,
            root.GetProperty("changed").GetBoolean());
    }

    public async Task<IReadOnlyList<RelayMessage>> ReadHistoryAsync(string threadRef, CancellationToken ct = default)
    {
        var messages = await GetThreadMessagesAsync(threadRef, ct);
        // History is text: a message's images are named, never shown, so a text-only reader knows
        // they were there.
        return messages.Select(item => new RelayMessage(item.Author.Name,
            item.Images is { Count: > 0 } images
                ? string.Join("\n", new[] { item.Text }.Concat(images.Select(image => $"[image: {image.Filename}]"))
                    .Where(line => line.Length > 0))
                : item.Text,
            item.At)).ToArray();
    }

    public async Task<IReadOnlyList<RelayThreadSummary>> GetThreadsAsync(CancellationToken ct = default)
    {
        var result = new List<RelayThreadSummary>();
        string? cursor = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var path = "/v1/threads?limit=100" + (cursor is null
                ? ""
                : "&cursor=" + Uri.EscapeDataString(cursor));
            using var response = await SendAsync(HttpMethod.Get, path, null, ct);
            if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.NotImplemented)
                throw new RelayFeatureUnavailableException("threads");
            using var json = await ReadJsonAsync(response, ct);
            if (!response.IsSuccessStatusCode) throw RelayError(response, json.RootElement, "threads_failed");
            if (json.RootElement.TryGetProperty("items", out var items)
                && items.ValueKind == JsonValueKind.Array)
            {
                result.AddRange(items.EnumerateArray().Select(item => new RelayThreadSummary(
                    item.GetProperty("threadRef").GetString() ?? "",
                    String(item, "channelName") ?? String(item, "channel") ?? "",
                    String(item, "title") ?? "",
                    Time(item, "lastActivityAt"),
                    String(item, "lastSnippet") ?? "",
                    Boolean(item, "unread") ?? (Integer(item, "unreadPings") ?? 0) > 0,
                    String(item, "scenario"))).Where(item => item.ThreadRef.Length > 0));
            }
            cursor = String(json.RootElement, "nextCursor");
            if (result.Count > 10_000)
                throw new RelayApiException("threads_too_many", "The relay returned too many threads.", 502);
            if (!string.IsNullOrWhiteSpace(cursor) && !seenCursors.Add(cursor))
                throw new RelayApiException("threads_cursor_loop", "The relay repeated a thread cursor.", 502);
        } while (!string.IsNullOrWhiteSpace(cursor));
        return result;
    }

    public async Task<IReadOnlyList<RelayThreadMessage>> GetThreadMessagesAsync(
        string threadRef, CancellationToken ct = default)
    {
        // The relay pages oldest first and keeps at most 500 messages, so the newest are on the
        // last page: follow the cursor to the end.
        var list = new List<RelayThreadMessage>();
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var path = "/v1/threads/" + Uri.EscapeDataString(threadRef) + "/messages?limit=100"
                + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            using var response = await SendAsync(HttpMethod.Get, path, null, ct);
            if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.NotImplemented)
                throw new RelayFeatureUnavailableException("thread-messages");
            using var json = await ReadJsonAsync(response, ct);
            if (!response.IsSuccessStatusCode) throw RelayError(response, json.RootElement, "thread_messages_failed");
            if (!json.RootElement.TryGetProperty("messages", out var messages)
                || messages.ValueKind != JsonValueKind.Array) return list;
            AddThreadMessages(list, messages);
            cursor = json.RootElement.TryGetProperty("nextCursor", out var next)
                && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            if (string.IsNullOrEmpty(cursor)) break;
        }
        return list;
    }

    private static void AddThreadMessages(List<RelayThreadMessage> list, JsonElement messages)
    {
        foreach (var item in messages.EnumerateArray())
        {
            var actor = item.GetProperty("actor");
            var kind = String(actor, "kind") ?? "human";
            var name = String(actor, "personaName") ?? String(actor, "displayName") ?? kind;
            list.Add(new(
                String(item, "id") ?? Guid.NewGuid().ToString("N"),
                Time(item, "createdAt"),
                new(kind, name, String(actor, "avatarUrl"), String(actor, "envoyId"),
                    kind == "human" && Boolean(actor, "isYourOwner") == true),
                String(item, "content") ?? "",
                String(item, "scenario"),
                ConnectorCore.ParseImages(item).Images is { Count: > 0 } images ? images : null));
        }
    }

    public Task<RelayPostResult> PostAsync(string threadRef, string content, IReadOnlyList<string>? addresses = null, string? idempotencyKey = null, CancellationToken ct = default)
        => PostAsync(threadRef, content, [], addresses, idempotencyKey, ct);

    /// <summary>
    /// A reply that may carry images: multipart <c>payload_json</c> plus <c>files[n]</c>. Without
    /// images it is an ordinary JSON post.
    /// </summary>
    public Task<RelayPostResult> PostAsync(string threadRef, string content,
        IReadOnlyList<RelayOutboundImage> images, IReadOnlyList<string>? addresses = null,
        string? idempotencyKey = null, CancellationToken ct = default)
    {
        var body = PostBody(content, addresses, idempotencyKey);
        body["threadRef"] = threadRef;
        if (images.Count == 0) return PostBodyAsync(body, ct);
        body["images"] = new JsonArray(images.Select(image => (JsonNode)(image.Description is null
            ? new JsonObject() : new JsonObject { ["description"] = image.Description })).ToArray());
        return PostBodyAsync(body, ct, () =>
        {
            // Quoted names and a plain text payload part, as browsers send them: every multipart
            // parser (the Workers runtime included) reads payload_json as a string field.
            var form = new MultipartFormDataContent();
            var payload = new ByteArrayContent(Encoding.UTF8.GetBytes(body.ToJsonString()));
            payload.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"payload_json\"" };
            form.Add(payload);
            for (var index = 0; index < images.Count; index++)
            {
                var file = new ByteArrayContent(images[index].Bytes);
                file.Headers.ContentType = new MediaTypeHeaderValue(images[index].ContentType);
                file.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
                {
                    Name = $"\"files[{index}]\"",
                    FileName = $"\"image-{index + 1}\"",
                };
                form.Add(file);
            }
            return form;
        });
    }

    public Task<RelayPostResult> OpenAsync(string channelId, string? title, string content, IReadOnlyList<string>? addresses = null, string? idempotencyKey = null, CancellationToken ct = default)
    {
        var body = PostBody(content, addresses, idempotencyKey);
        body["channelId"] = channelId;
        if (!string.IsNullOrWhiteSpace(title)) body["title"] = title;
        return PostBodyAsync(body, ct);
    }

    private static JsonObject PostBody(string content, IReadOnlyList<string>? addresses, string? idempotencyKey) => new()
    {
        ["content"] = content,
        ["addresses"] = new JsonArray((addresses ?? []).Select(address => (JsonNode?)address).ToArray()),
        ["idempotencyKey"] = idempotencyKey ?? Guid.NewGuid().ToString("N"),
    };

    /// <summary>The channels this Envoy may post in, and the forum for questions to the community.</summary>
    public async Task<RelayChannels> GetChannelsAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/v1/channels", null, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.NotImplemented)
            throw new RelayFeatureUnavailableException("channels");
        using var json = await ReadJsonAsync(response, ct);
        if (!response.IsSuccessStatusCode) throw RelayError(response, json.RootElement, "channels_failed");
        var channels = json.RootElement.TryGetProperty("channels", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Select(item => new RelayChannel(String(item, "id") ?? "",
                String(item, "name") ?? "", String(item, "kind") ?? "text"))
                .Where(item => item.Id.Length > 0).ToArray()
            : [];
        return new(channels, String(json.RootElement, "defaultChannelId"));
    }

    private async Task<RelayPostResult> PostBodyAsync(JsonObject body, CancellationToken ct, Func<HttpContent>? content = null)
    {
        var waited = TimeSpan.Zero;
        for (var attempt = 0; ; attempt++)
        {
            using var response = await SendAsync(HttpMethod.Post, "/v1/posts", content?.Invoke() ?? JsonContent.Create(body), ct);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = json.RootElement; var decision = root.TryGetProperty("decision", out var d) ? d.GetString() : null;
            var result = Result(root, decision);
            if ((int)response.StatusCode == 409 && result.Code == "idempotency_in_progress" && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (1 << attempt)), ct);
                continue;
            }
            // The relay asks the host to install a webhook on a channel's first post and records
            // nothing for the idempotency key, so the same body is retried once the host has it.
            if ((int)response.StatusCode == 503 && result.Code == "webhook_pending"
                && WebhookPendingDelay(root) is var wait && waited + wait <= WebhookPendingBudget)
            {
                await (delay ?? Task.Delay)(wait, ct);
                waited += wait;
                continue;
            }
            return result;
        }
    }

    /// <summary>
    /// How long a post waits for the host to install a channel's webhook. The host checks every
    /// 2 s, but a loaded host can miss a few: Sibylle's first #bug-reports answer gave up after
    /// 12 s while Laurent's database was unreachable for a minute, one second before the webhook
    /// arrived (2026-10-31). Settled answers post in the background, so waiting costs nobody.
    /// </summary>
    internal static readonly TimeSpan WebhookPendingBudget = TimeSpan.FromSeconds(60);

    private static TimeSpan WebhookPendingDelay(JsonElement root)
        => TimeSpan.FromSeconds(root.TryGetProperty("retryAfterSeconds", out var retry)
            && retry.TryGetInt32(out var seconds) ? Math.Clamp(seconds, 1, 5) : 2);

    private static RelayPostResult Result(JsonElement root, string? decision) => new(decision == "posted",
        root.TryGetProperty("reason", out var reason) ? reason.GetString() : null,
        root.TryGetProperty("messageId", out var id) ? id.GetString() : null,
        root.TryGetProperty("threadId", out var thread) ? thread.GetString() : null);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        var (baseUri, credential) = await settings(ct).ConfigureAwait(false);
        return await SendAuthenticatedAsync(baseUri, credential, method, path, content, ct);
    }

    private async Task<HttpResponseMessage> SendAuthenticatedAsync(
        Uri baseUri, string? credential, HttpMethod method, string path, HttpContent? content,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(credential)) throw new InvalidOperationException("Relay credential is not configured");
        using var request = new HttpRequestMessage(method, new Uri(baseUri, path)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return await http.SendAsync(request, ct);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var raw = await response.Content.ReadAsStringAsync(ct);
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw); }
        catch (JsonException) { return JsonDocument.Parse("{}"); }
    }

    private static RelayApiException RelayError(
        HttpResponseMessage response, JsonElement root, string fallback)
    {
        var reason = String(root, "error") ?? fallback;
        var message = String(root, "message") ?? reason;
        return new(reason, message, (int)response.StatusCode);
    }

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? Boolean(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static int? Integer(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed) ? parsed : null;

    private static DateTimeOffset Time(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return DateTimeOffset.UnixEpoch;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var milliseconds))
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        return value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;
    }
}

public sealed record RelayListeningEvent(
    string Id, string Type, string Channel, string Content, string? Sender, string? ThreadRef,
    string? MessageId = null, string? AuthorDisplayName = null,
    DateTimeOffset? Timestamp = null, string? Operation = null, string? SuspendedBy = null);

public sealed class ConnectorCore(
    Func<EnvoyRunRequest, CancellationToken, Task> handler,
    Func<RelayListeningEvent, CancellationToken, Task>? listeningHandler = null)
{
    private readonly HashSet<string> processed = new(StringComparer.Ordinal);

    public async Task<bool> ProcessAsync(JsonElement frame, Func<string, Task> ack, CancellationToken ct = default)
    {
        if (!frame.TryGetProperty("id", out var idNode) || idNode.ValueKind != JsonValueKind.String) return false;
        var id = idNode.GetString()!;
        if (!processed.Add(id)) { await ack(id); return false; }
        if (frame.TryGetProperty("expiresAt", out var expires) && expires.ValueKind == JsonValueKind.Number && expires.GetInt64() <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) { await ack(id); return false; }
        var type = frame.GetProperty("type").GetString();
        if (type is "ping" or "event" or "announcement" or "owner_notice")
        {
            var thread = frame.TryGetProperty("threadRef", out var threadNode)
                && threadNode.ValueKind == JsonValueKind.String
                ? threadNode.GetString() : null;
            var content = frame.TryGetProperty("content", out var contentNode) ? contentNode.GetString() ?? "" : frame.TryGetProperty("reason", out var reason) ? reason.GetString() ?? "owner notice" : "owner notice";
            var sender = frame.TryGetProperty("sender", out var senderNode) ? senderNode.GetRawText() : null;
            var replyToDisplayName = frame.TryGetProperty("sender", out var replySender)
                && replySender.ValueKind == JsonValueKind.Object
                && replySender.TryGetProperty("displayName", out var displayName)
                && displayName.ValueKind == JsonValueKind.String
                ? displayName.GetString() : null;
            var channel = frame.TryGetProperty("channel", out var channelNode)
                && channelNode.ValueKind == JsonValueKind.String
                ? channelNode.GetString() ?? "" : "";
            var messageId = frame.TryGetProperty("messageId", out var messageIdNode)
                && messageIdNode.ValueKind == JsonValueKind.String
                ? messageIdNode.GetString() : null;
            var authorDisplayName = frame.TryGetProperty("authorDisplayName", out var authorNode)
                && authorNode.ValueKind == JsonValueKind.String
                ? authorNode.GetString() : null;
            DateTimeOffset? timestamp = frame.TryGetProperty("timestamp", out var timestampNode)
                && timestampNode.ValueKind == JsonValueKind.Number
                && timestampNode.TryGetInt64(out var timestampMilliseconds)
                ? DateTimeOffset.FromUnixTimeMilliseconds(timestampMilliseconds) : null;
            var operation = frame.TryGetProperty("operation", out var operationNode)
                && operationNode.ValueKind == JsonValueKind.String
                ? operationNode.GetString() : null;
            var suspendedBy = type == "owner_notice"
                ? SuspensionSources.FromNotice(Text(frame, "suspendedBy"), Text(frame, "reason")) : null;
            var (images, omittedImages) = ParseImages(frame);
            var listeningOnly = type is "owner_notice" or "announcement" || (type == "event"
                && (string.IsNullOrWhiteSpace(thread)
                    || string.Equals(channel, "announcements", StringComparison.OrdinalIgnoreCase)));
            if (listeningOnly)
            {
                if (listeningHandler is not null)
                    await listeningHandler(new(id, type!, channel, content, sender, thread,
                        messageId, authorDisplayName, timestamp, operation, suspendedBy), ct);
            }
            else
                await handler(new(thread, type!, content, sender, id, Addressed: type == "ping",
                    Channel: channel, ReplyToDisplayName: replyToDisplayName,
                    Images: images, OmittedImages: omittedImages,
                    OpenInvitation: type == "event" && Text(frame, "invitation") == "open"), ct);
        }
        await ack(id);
        return true;
    }

    /// <summary>
    /// The relay's image descriptors, bounded to four. Only descriptors are accepted: a frame can
    /// never hand the connector an Asset reference, so every image an Agent sees was fetched and
    /// checked locally.
    /// </summary>
    internal static (IReadOnlyList<RelayImageAttachment> Images, int Omitted) ParseImages(JsonElement frame)
    {
        var omitted = frame.TryGetProperty("omittedAttachments", out var omittedNode)
            && omittedNode.ValueKind == JsonValueKind.Number && omittedNode.TryGetInt32(out var count)
            ? Math.Clamp(count, 0, 100) : 0;
        if (!frame.TryGetProperty("attachments", out var values)
            || values.ValueKind != JsonValueKind.Array) return ([], omitted);
        var result = new List<RelayImageAttachment>();
        foreach (var item in values.EnumerateArray())
        {
            var id = item.ValueKind == JsonValueKind.Object ? Text(item, "id") : null;
            var filename = id is null ? null : Text(item, "filename");
            var contentType = id is null ? null : Text(item, "contentType");
            var url = id is null ? null : Text(item, "url");
            var size = id is not null && item.TryGetProperty("size", out var sizeNode)
                && sizeNode.ValueKind == JsonValueKind.Number && sizeNode.TryGetInt64(out var parsedSize)
                ? parsedSize : -1;
            if (id is null || filename is null || contentType is null || url is null || size < 0
                || result.Count >= CommonsImages.MaxImages)
            {
                omitted++;
                continue;
            }
            result.Add(new(id, filename, contentType, size, url,
                Dimension(item, "width"), Dimension(item, "height"), Text(item, "description")));
        }
        return (result, omitted);
    }

    private static int? Dimension(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed) ? parsed : null;

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>The relay refused the WebSocket handshake with HTTP 401 or 403 before upgrading.</summary>
public sealed class RelayConnectRefusedException(int statusCode)
    : Exception($"The relay refused the connection with HTTP {statusCode}")
{
    public int StatusCode { get; } = statusCode;
}

/// <param name="onRefused">Decides what a refused handshake means (suspended, pending, revoked):
/// the delay before the next attempt, or null to stop the connector.</param>
public sealed class RelayConnector(Func<CancellationToken, Task<(Uri BaseUri, string? Credential)>> settings, ConnectorCore core,
    Func<string, CancellationToken, Task> setStatus, ILogger<RelayConnector> logger,
    Func<CancellationToken, Task>? onConnected = null,
    Func<int, CancellationToken, Task<TimeSpan?>>? onRefused = null) : IAsyncDisposable
{
    private static readonly AsyncLocal<bool> InsideLoop = new();
    private readonly SemaphoreSlim single = new(1, 1);
    private CancellationTokenSource? running;
    private Task? loop;

    /// <summary>Completes when the loop has stopped (cancelled, or a refusal ended it).</summary>
    public Task Completion => loop ?? Task.CompletedTask;

    public Task StartAsync(CancellationToken stopping)
    {
        if (running is not null) return Task.CompletedTask;
        running = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var token = running.Token;
        loop = Detached.Run(() => LoopAsync(token));
        return Task.CompletedTask;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        InsideLoop.Value = true;
        await single.WaitAsync(ct);
        try
        {
            var delay = TimeSpan.FromSeconds(1);
            // One log line per failure state, not per retry: the same refusal or error repeats
            // quietly until the connector connects or the failure changes.
            string? failure = null;
            while (!ct.IsCancellationRequested)
            {
                TimeSpan wait;
                try
                {
                    await ConnectOnceAsync(ct);
                    delay = TimeSpan.FromSeconds(1);
                    if (failure is not null) logger.LogInformation("Leaf Community connector recovered");
                    failure = null;
                    continue;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (RelayConnectRefusedException refused)
                {
                    var key = $"refused:{refused.StatusCode}";
                    if (failure != key)
                        logger.LogWarning("Leaf Community relay refused the connection (HTTP {Status})", refused.StatusCode);
                    failure = key;
                    var next = onRefused is null ? delay : await RefusedSafelyAsync(refused.StatusCode, ct);
                    if (next is null)
                    {
                        logger.LogWarning("Leaf Community connector stopped: the relay refuses this Agent");
                        break;
                    }
                    if (onRefused is null) await TrySetStatusAsync("reconnecting", ct);
                    wait = next.Value;
                    delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
                }
                catch (Exception error)
                {
                    var key = $"{error.GetType().FullName}:{error.Message}";
                    if (failure != key) logger.LogError(error, "Leaf Community connector iteration failed");
                    else logger.LogDebug("Leaf Community connector iteration failed again ({Type})", error.GetType().Name);
                    failure = key;
                    await TrySetStatusAsync("reconnecting", ct);
                    wait = delay;
                    delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
                }
                try { await Task.Delay(wait + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500)), ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }
        }
        finally { single.Release(); }
    }

    private async Task<TimeSpan?> RefusedSafelyAsync(int statusCode, CancellationToken ct)
    {
        try { return await onRefused!(statusCode, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogWarning("Leaf Community refusal handling failed ({Type}: {Message})", error.GetType().Name, error.Message);
            return TimeSpan.FromSeconds(30);
        }
    }

    /// <summary>A failed status save must never end the retry loop.</summary>
    private async Task TrySetStatusAsync(string status, CancellationToken ct)
    {
        try { await SetStatusAsync(status, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            logger.LogWarning("Leaf Community connector status '{Status}' could not be saved ({Type})", status, error.GetType().Name);
        }
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        var (baseUri, credential) = await settings(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(credential)) { await SetStatusAsync("disconnected", ct); await Task.Delay(TimeSpan.FromSeconds(5), ct); return; }
        var uri = new UriBuilder(baseUri) { Scheme = baseUri.Scheme == "https" ? "wss" : "ws", Path = "/v1/connect", Query = "" }.Uri;
        using var ws = new ClientWebSocket(); ws.Options.SetRequestHeader("Authorization", "Bearer " + credential);
        ws.Options.CollectHttpResponseDetails = true;
        try { await ws.ConnectAsync(uri, ct); }
        catch (WebSocketException) when (ws.HttpStatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            throw new RelayConnectRefusedException((int)ws.HttpStatusCode);
        }
        if (onConnected is not null) await onConnected(ct);
        await SetStatusAsync("active", ct);
        var buffer = new byte[64 * 1024];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var data = new MemoryStream(); WebSocketReceiveResult received;
            do { received = await ws.ReceiveAsync(buffer, ct); if (received.MessageType == WebSocketMessageType.Close) return; data.Write(buffer, 0, received.Count); } while (!received.EndOfMessage);
            using var json = JsonDocument.Parse(data.ToArray());
            if (json.RootElement.TryGetProperty("type", out var type) && type.GetString() == "hello") continue;
            await core.ProcessAsync(json.RootElement, id => SendAsync(ws, JsonSerializer.Serialize(new { type = "ack", id }), ct), ct);
        }
    }

    private static Task SendAsync(ClientWebSocket ws, string json, CancellationToken ct) => ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
    private async Task SetStatusAsync(string status, CancellationToken ct)
        => await setStatus(status, ct);

    /// <summary>
    /// Cancels the loop and waits for it to stop, unless called from inside the loop (a refusal
    /// handler stopping its own connector), where waiting would deadlock.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (running is not { } cts) return;
        running = null;
        await cts.CancelAsync();
        if (loop is { } task && !InsideLoop.Value)
            try { await task; } catch (OperationCanceledException) { }
        cts.Dispose();
    }
}
