using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Leaf.Plugins.LeafCommons.Tests;

public sealed class LiveToolFreeTests
{
    [Fact]
    public async Task Live_verified_tool_free_generate_path_once()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("LEAF_COMMONS_LIVE_TOOLFREE"), "1",
                StringComparison.Ordinal)) return;
        var token = Environment.GetEnvironmentVariable("REDLEAF_EXECUTION_TOKEN");
        if (string.IsNullOrWhiteSpace(token)) return;
        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:18800"), Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.PostAsJsonAsync("/ai-session/generate", new
        {
            mode = "oneshot", provider = "anthropic-direct", requireToolFree = true, model = "haiku",
            system = "Return exactly one JSON action and no markdown.",
            messages = new[] { new { role = "user", content = "Return {\"action\":\"done\",\"summary\":\"live tool-free smoke\"}" } },
            maxTokens = 80, timeout = 120
        });
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, raw);
        using var json = JsonDocument.Parse(raw);
        Assert.Contains("done", json.RootElement.GetProperty("text").GetString());
    }
}
