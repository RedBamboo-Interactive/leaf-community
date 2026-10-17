using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Leaf.Plugins.LeafCommons;

public sealed record OAuthExchange(string Credential, string Status, string PersonaName, string EnvoyId, string? AvatarObjectKey);

public sealed class RelayHttpClient(HttpClient http, Func<(Uri BaseUri, string? Credential)> settings) : IEnvoyRelay
{
    public Uri BuildAuthorizeUri(Uri callback, IEnumerable<string> scenarios)
    {
        var (baseUri, _) = settings();
        if (!(callback.Scheme == "leaf" || (callback.Scheme == "http" && callback.IsLoopback))) throw new ArgumentException("OAuth callback must be leaf: or loopback HTTP");
        return new Uri(baseUri, "/v1/oauth/authorize?connectorRedirectUri=" + Uri.EscapeDataString(callback.ToString()) + "&scenarios=" + Uri.EscapeDataString(string.Join(',', scenarios)));
    }

    public async Task<OAuthExchange> ExchangeAsync(string exchangeCode, CancellationToken ct = default)
    {
        var (baseUri, _) = settings();
        using var response = await http.PostAsJsonAsync(new Uri(baseUri, "/v1/oauth/exchange"), new { exchangeCode }, ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement; var envoy = root.GetProperty("envoy");
        return new(root.GetProperty("credential").GetString()!, envoy.GetProperty("status").GetString()!, envoy.GetProperty("personaName").GetString()!, envoy.GetProperty("id").GetString()!, envoy.TryGetProperty("avatarObjectKey", out var avatar) ? avatar.GetString() : null);
    }

    public async Task UpdateProfileAsync(string persona, string? avatarObjectKey, IReadOnlyDictionary<string, bool> scenarioToggles, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Patch, "/v1/profile", JsonContent.Create(new
        {
            personaName = persona,
            avatarObjectKey,
            scenarioToggles,
        }), ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Relay profile update failed with HTTP {(int)response.StatusCode}");
    }

    public async Task<IReadOnlyList<RelayMessage>> ReadHistoryAsync(string threadRef, CancellationToken ct = default)
    {
        var list = new List<RelayMessage>();
        var path = "/v1/threads/" + Uri.EscapeDataString(threadRef) + "/messages?limit=100";
        using var response = await SendAsync(HttpMethod.Get, path, null, ct); response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        foreach (var item in json.RootElement.GetProperty("messages").EnumerateArray())
        {
            var actor = item.GetProperty("actor");
            var author = actor.TryGetProperty("personaName", out var persona) ? persona.GetString() : actor.TryGetProperty("displayName", out var name) ? name.GetString() : actor.GetProperty("kind").GetString();
            var when = item.TryGetProperty("createdAt", out var created) ? DateTimeOffset.FromUnixTimeMilliseconds(created.GetInt64()) : (DateTimeOffset?)null;
            list.Add(new(author ?? "unknown", item.GetProperty("content").GetString() ?? "", when));
        }
        return list;
    }

    public Task<RelayPostResult> PostAsync(string threadRef, string content, IReadOnlyList<string>? addresses = null, string? idempotencyKey = null, CancellationToken ct = default)
    {
        var body = new { scenario = "consult_reply", threadRef, content, addresses = addresses ?? [], idempotencyKey = idempotencyKey ?? Guid.NewGuid().ToString("N") };
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
        var (baseUri, credential) = settings();
        if (string.IsNullOrWhiteSpace(credential)) throw new InvalidOperationException("Envoy credential is not configured");
        var request = new HttpRequestMessage(method, new Uri(baseUri, path)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return await http.SendAsync(request, ct);
    }
}

public sealed class ConnectorCore(Func<EnvoyRunRequest, CancellationToken, Task> handler)
{
    private readonly HashSet<string> processed = new(StringComparer.Ordinal);

    public async Task<bool> ProcessAsync(JsonElement frame, Func<string, Task> ack, CancellationToken ct = default)
    {
        if (!frame.TryGetProperty("id", out var idNode) || idNode.ValueKind != JsonValueKind.String) return false;
        var id = idNode.GetString()!;
        if (!processed.Add(id)) { await ack(id); return false; }
        if (frame.TryGetProperty("expiresAt", out var expires) && expires.ValueKind == JsonValueKind.Number && expires.GetInt64() <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) { await ack(id); return false; }
        var type = frame.GetProperty("type").GetString();
        if (type is "ping" or "event" or "owner_notice")
        {
            var thread = frame.TryGetProperty("threadRef", out var threadNode) && threadNode.ValueKind == JsonValueKind.String ? threadNode.GetString()! : "announcements";
            var content = frame.TryGetProperty("content", out var contentNode) ? contentNode.GetString() ?? "" : frame.TryGetProperty("reason", out var reason) ? reason.GetString() ?? "owner notice" : "owner notice";
            var kind = type == "ping" && frame.TryGetProperty("scenario", out var scenario) ? scenario.GetString() ?? "consult_reply" : type;
            var sender = frame.TryGetProperty("sender", out var senderNode) ? senderNode.GetRawText() : null;
            await handler(new(thread, kind, content, sender, id), ct);
        }
        await ack(id);
        return true;
    }
}

public sealed class RelayConnector(Func<(Uri BaseUri, string? Credential)> settings, ConnectorCore core, CommonsStateStore state) : IAsyncDisposable
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
                catch { await SetStatusAsync("reconnecting", ct); await Task.Delay(delay + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500)), ct); delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2)); }
            }
        }
        finally { single.Release(); }
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        var (baseUri, credential) = settings();
        if (string.IsNullOrWhiteSpace(credential)) { await SetStatusAsync("disconnected", ct); await Task.Delay(TimeSpan.FromSeconds(5), ct); return; }
        var uri = new UriBuilder(baseUri) { Scheme = baseUri.Scheme == "https" ? "wss" : "ws", Path = "/v1/connect", Query = "" }.Uri;
        using var ws = new ClientWebSocket(); ws.Options.SetRequestHeader("Authorization", "Bearer " + credential);
        await ws.ConnectAsync(uri, ct);
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
    {
        var current = await state.GetSettingsAsync(ct);
        if (status == "reconnecting" && current.ConnectionStatus == "pending approval") return;
        if (current.ConnectionStatus != status) await state.SaveSettingsAsync(current with { ConnectionStatus = status }, ct);
    }
    public async ValueTask DisposeAsync() { if (running is null) return; await running.CancelAsync(); running.Dispose(); running = null; single.Dispose(); }
}
