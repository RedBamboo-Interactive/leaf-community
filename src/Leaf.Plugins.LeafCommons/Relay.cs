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
public sealed record RelayThreadAuthor(string Kind, string Name, string? AvatarUrl = null, string? EnvoyId = null);
public sealed record RelayThreadMessage(
    string Id, DateTimeOffset At, RelayThreadAuthor Author, string Text, string? Scenario = null);

public sealed class RelayApiException(string reason, string message, int statusCode)
    : Exception(message)
{
    public string Reason { get; } = reason;
    public int StatusCode { get; } = statusCode;
}

public sealed class RelayFeatureUnavailableException(string feature)
    : Exception($"Relay feature '{feature}' is unavailable");

public sealed class RelayHttpClient(HttpClient http, Func<CancellationToken, Task<(Uri BaseUri, string? Credential)>> settings) : IEnvoyRelay, IAvatarUploader
{
    public Task<RelayProfile> ReadProfileAsync(CancellationToken ct = default)
        => PatchProfileAsync(null, null, null, ct);

    public async Task<Uri> BuildAuthorizeUriAsync(Uri callback, IEnumerable<string> scenarios,
        CancellationToken ct = default, string? agentEntityId = null, string? agentName = null)
    {
        var (baseUri, _) = await settings(ct).ConfigureAwait(false);
        if (!(callback.Scheme == "leaf" || (callback.Scheme == "http" && callback.IsLoopback))) throw new ArgumentException("OAuth callback must be leaf: or loopback HTTP");
        var query = "/v1/oauth/authorize?connectorRedirectUri=" + Uri.EscapeDataString(callback.ToString())
            + "&scenarios=" + Uri.EscapeDataString(string.Join(',', scenarios));
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

    public async Task<RelayProfile> UpdateProfileAsync(
        string publicName, IReadOnlyDictionary<string, bool> scenarioToggles,
        CancellationToken ct = default)
    {
        var (baseUri, credential) = await settings(ct).ConfigureAwait(false);
        return await UpdateProfileAtAsync(baseUri, credential, publicName, scenarioToggles, ct);
    }

    public async Task<RelayProfile> UpdateProfileAtAsync(
        Uri baseUri, string? credential, string publicName,
        IReadOnlyDictionary<string, bool> scenarioToggles, CancellationToken ct = default)
    {
        using var response = await SendAuthenticatedAsync(baseUri, credential, HttpMethod.Patch,
            "/v1/profile", JsonContent.Create(new { publicName, scenarioToggles }), ct);
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
        string publicName, string agentName, IReadOnlyDictionary<string, bool> scenarioToggles,
        CancellationToken ct = default)
        => await PatchProfileAsync(publicName, agentName, scenarioToggles, ct);

    public async Task<RelayProfile> PatchProfileAsync(
        string? publicName, string? agentName,
        IReadOnlyDictionary<string, bool>? scenarioToggles,
        CancellationToken ct = default)
    {
        var (baseUri, credential) = await settings(ct).ConfigureAwait(false);
        var body = new JsonObject();
        if (publicName is not null) body["publicName"] = publicName;
        if (agentName is not null) body["agentName"] = agentName;
        if (scenarioToggles is not null)
            body["scenarioToggles"] = JsonSerializer.SerializeToNode(scenarioToggles);
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
        return messages.Select(item => new RelayMessage(item.Author.Name, item.Text, item.At)).ToArray();
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
        var path = "/v1/threads/" + Uri.EscapeDataString(threadRef) + "/messages?limit=100";
        using var response = await SendAsync(HttpMethod.Get, path, null, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.NotImplemented)
            throw new RelayFeatureUnavailableException("thread-messages");
        using var json = await ReadJsonAsync(response, ct);
        if (!response.IsSuccessStatusCode) throw RelayError(response, json.RootElement, "thread_messages_failed");
        var list = new List<RelayThreadMessage>();
        if (!json.RootElement.TryGetProperty("messages", out var messages)
            || messages.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in messages.EnumerateArray())
        {
            var actor = item.GetProperty("actor");
            var kind = String(actor, "kind") ?? "human";
            var name = String(actor, "personaName") ?? String(actor, "displayName") ?? kind;
            list.Add(new(
                String(item, "id") ?? Guid.NewGuid().ToString("N"),
                Time(item, "createdAt"),
                new(kind, name, String(actor, "avatarUrl"), String(actor, "envoyId")),
                String(item, "content") ?? "",
                String(item, "scenario")));
        }
        return list;
    }

    public Task<RelayPostResult> PostAsync(string threadRef, string content, IReadOnlyList<string>? addresses = null, string? idempotencyKey = null, CancellationToken ct = default)
    {
        return ReplyAsync(threadRef, "consult_reply", content, addresses, idempotencyKey, ct);
    }

    public Task<RelayPostResult> ReplyAsync(string threadRef, string scenario, string content,
        IReadOnlyList<string>? addresses = null, string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        var body = new { scenario, threadRef, content, addresses = addresses ?? [], idempotencyKey = idempotencyKey ?? Guid.NewGuid().ToString("N") };
        return PostBodyAsync(body, ct);
    }

    public Task<RelayPostResult> OpenAsync(string scenario, string? threadName, string content, IReadOnlyList<string>? addresses = null, string? idempotencyKey = null, CancellationToken ct = default)
    {
        var body = new { scenario, threadName, content, addresses = addresses ?? [], idempotencyKey = idempotencyKey ?? Guid.NewGuid().ToString("N") };
        return PostBodyAsync(body, ct);
    }

    private async Task<RelayPostResult> PostBodyAsync<T>(T body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var response = await SendAsync(HttpMethod.Post, "/v1/posts", JsonContent.Create(body), ct);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = json.RootElement; var decision = root.TryGetProperty("decision", out var d) ? d.GetString() : null;
            var result = Result(root, decision);
            if ((int)response.StatusCode != 409 || result.Code != "idempotency_in_progress" || attempt >= 3) return result;
            await Task.Delay(TimeSpan.FromMilliseconds(100 * (1 << attempt)), ct);
        }
    }

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
    DateTimeOffset? Timestamp = null, string? Operation = null);

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
            var kind = type == "ping" && frame.TryGetProperty("scenario", out var scenario) ? scenario.GetString() ?? "consult_reply" : type;
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
            var attachments = ParseAttachments(frame);
            var listeningOnly = type is "owner_notice" or "announcement" || (type == "event"
                && (string.IsNullOrWhiteSpace(thread)
                    || string.Equals(channel, "announcements", StringComparison.OrdinalIgnoreCase)));
            if (listeningOnly)
            {
                if (listeningHandler is not null)
                    await listeningHandler(new(id, type!, channel, content, sender, thread,
                        messageId, authorDisplayName, timestamp, operation), ct);
            }
            else
                await handler(new(thread, kind, content, sender, id, Addressed: type == "ping",
                    Channel: channel, ReplyToDisplayName: replyToDisplayName,
                    Attachments: attachments), ct);
        }
        await ack(id);
        return true;
    }

    private static IReadOnlyList<ExternalConversationAttachment> ParseAttachments(JsonElement frame)
    {
        if (!frame.TryGetProperty("attachments", out var values)
            || values.ValueKind != JsonValueKind.Array) return [];
        var result = new List<ExternalConversationAttachment>();
        foreach (var item in values.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = Text(item, "id");
            var name = Text(item, "name");
            var mediaType = Text(item, "mediaType") ?? Text(item, "media_type");
            var sha256 = Text(item, "sha256");
            var scopeToken = Text(item, "scopeToken") ?? Text(item, "scope_token");
            var size = item.TryGetProperty("size", out var sizeNode)
                && sizeNode.TryGetInt64(out var parsedSize) ? parsedSize : -1;
            if (id is null || name is null || mediaType is null || sha256 is null
                || scopeToken is null || size < 0) continue;
            result.Add(new(id, name, mediaType, size, sha256, scopeToken));
        }
        return result;
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

public sealed class RelayConnector(Func<CancellationToken, Task<(Uri BaseUri, string? Credential)>> settings, ConnectorCore core,
    Func<string, CancellationToken, Task> setStatus, ILogger<RelayConnector> logger,
    Func<CancellationToken, Task>? onConnected = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim single = new(1, 1);
    private CancellationTokenSource? running;

    public Task StartAsync(CancellationToken stopping)
    {
        if (running is not null) return Task.CompletedTask;
        running = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _ = Task.Run(() => LoopAsync(running.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        await single.WaitAsync(ct);
        try
        {
            var delay = TimeSpan.FromSeconds(1);
            while (!ct.IsCancellationRequested)
            {
                try { await ConnectOnceAsync(ct); delay = TimeSpan.FromSeconds(1); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    logger.LogError(error, "Leaf Community connector iteration failed");
                    await SetStatusAsync("reconnecting", ct);
                    await Task.Delay(delay + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500)), ct);
                    delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
                }
            }
        }
        finally { single.Release(); }
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        var (baseUri, credential) = await settings(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(credential)) { await SetStatusAsync("disconnected", ct); await Task.Delay(TimeSpan.FromSeconds(5), ct); return; }
        var uri = new UriBuilder(baseUri) { Scheme = baseUri.Scheme == "https" ? "wss" : "ws", Path = "/v1/connect", Query = "" }.Uri;
        using var ws = new ClientWebSocket(); ws.Options.SetRequestHeader("Authorization", "Bearer " + credential);
        await ws.ConnectAsync(uri, ct);
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
    public async ValueTask DisposeAsync() { if (running is null) return; await running.CancelAsync(); running.Dispose(); running = null; single.Dispose(); }
}
