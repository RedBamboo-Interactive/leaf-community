using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Leaf.Plugins.LeafCommons.Tests;

/// <summary>The packaged skill, checked against the kernel's plugin-skill seed rules and the code.</summary>
public sealed class LeafCommunitySkillSeedTests
{
    private static string SkillDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LeafCommons.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "seeds", "skills", CommonsAgentSkills.SkillSlug);
    }

    private static string Markdown()
        => File.ReadAllText(Path.Combine(SkillDirectory(), "SKILL.md"), new UTF8Encoding(false, true));

    private static string Body() => Markdown().Replace("\r\n", "\n");

    [Fact]
    public void Seed_follows_the_kernels_plugin_skill_rules()
    {
        // RedLeaf PluginSkillSeedDefinition: frontmatter first, only name and description,
        // name equal to the directory, a lowercase slug, a description of at most 1024
        // characters and a non-empty body. Any other file would be attached as text.
        var directory = SkillDirectory();
        Assert.Equal(["SKILL.md"], Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path).Replace('\\', '/')).ToArray());
        var lines = Body().Split('\n');
        Assert.Equal("---", lines[0]);
        var closing = Array.IndexOf(lines, "---", 1);
        Assert.True(closing > 1);
        var frontmatter = lines[1..closing].ToDictionary(
            line => line[..line.IndexOf(':')].Trim(), line => line[(line.IndexOf(':') + 1)..].Trim());
        Assert.Equal(["name", "description"], frontmatter.Keys.ToArray());
        Assert.Equal(Path.GetFileName(directory), frontmatter["name"]);
        Assert.Equal(CommonsAgentSkills.SkillSlug, frontmatter["name"]);
        Assert.Matches("^[a-z0-9]+(?:-[a-z0-9]+)*$", frontmatter["name"]);
        Assert.InRange(frontmatter["description"].Length, 1, 1024);
        Assert.NotEmpty(string.Join('\n', lines.Skip(closing + 1)).Trim());
    }

    [Fact]
    public void Description_triggers_on_Leaf_Community_briefs_and_digests()
    {
        var description = Body().Split('\n')[2];
        Assert.StartsWith("description: ", description);
        foreach (var term in new[] { "Leaf Community", "Leaf Discord server", "brief", "digest", "public" })
            Assert.Contains(term, description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Skill_states_the_enforced_notices_limits_and_codes()
    {
        var skill = Body();
        // The fixed notices are quoted exactly as the code posts them.
        Assert.Contains(CommonsOutboundReview.WithheldNotice, skill);
        Assert.Contains(CommonsOutboundReview.UnavailableNotice, skill);
        Assert.Contains(PublicAgentConversationCoordinator.SentinelUnavailableNotice, skill);
        Assert.Contains(EnvoyRunner.CoverageFallback, skill);
        Assert.Contains(CommonsOutboundReview.UndeliveredNotice("CODE").Replace("(error code: code)", "(error code: CODE)"), skill);
        // Images.
        Assert.Equal(4, CommonsImages.MaxImages);
        Assert.Equal(5 * 1024 * 1024, CommonsImages.MaxImageBytes);
        Assert.Equal(4096, CommonsImages.MaxDimension);
        Assert.Equal(1024, UntrustedContentImage.MaxDescriptionLength);
        Assert.Contains("at most 4 images", skill);
        Assert.Contains("5 MiB", skill);
        Assert.Contains("4096 px", skill);
        Assert.Contains("1,024 characters", skill);
        Assert.Equal(4, PublicAgentConversationCoordinator.MaxOutboundReviewAttempts);
        Assert.Contains("up to four times in total", skill);
        // Briefs.
        Assert.Contains("POST http://127.0.0.1:18804/api/apps/leaf-commons/briefs", skill);
        foreach (var kind in new[] { "showcase", "consult", "open_question", "bug", "bug_report", "intro_draft" })
            Assert.Contains($"`{kind}`", skill);
        foreach (var code in new[] { "owner_agent_required", "agent_not_public", "invalid_brief",
                     "starts_disabled", "unknown_channel", "content-too-long", "sentinel_held", "sentinel_unavailable" })
            Assert.Contains($"`{code}`", skill);
        Assert.Contains("Authorization: Bearer $REDLEAF_EXECUTION_TOKEN", skill);
        foreach (var field in new[] { "channel", "ownerRequested" })
            Assert.Contains($"`{field}`", skill);
        Assert.Contains("Answers when spoken to", skill);
        Assert.Contains("Starts conversations on its own", skill);
    }

    [Fact]
    public void Skill_is_generic_for_any_Leaf_installation()
    {
        var skill = Body();
        foreach (var term in new[] { "Nova", "Laurent", "becherel", "workers.dev", "Alice" })
            Assert.DoesNotContain(term, skill, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"\b\d{17,20}\b", skill); // Discord snowflakes (guild, channel, user IDs)
        Assert.DoesNotMatch(@"\b[A-Za-z]:[\\/]", skill); // machine paths
        Assert.DoesNotMatch(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", skill);
    }
}

public sealed class AgentSkillAssignmentTests
{
    internal const string Owner = "11111111-1111-1111-1111-111111111111";
    private const string OtherSkill = "d9dc6cfa-06ea-4e12-896f-416e2fa2a3fe";
    private const string AgentA = "a1000000-0000-0000-0000-000000000001";

    internal sealed record World(MemoryEntityStore Entities, CommonsAgentSkills Skills, LeafEntity Skill, LeafEntity Plugin);

    internal static World Create(bool seedSkill = true)
    {
        var entities = new MemoryEntityStore();
        var plugin = entities.Seed(Guid.NewGuid(), "plugin", "Leaf Community", LeafCommonsPlugin.PluginId, []);
        entities.PluginEntityId = plugin.Id;
        var skill = seedSkill
            ? entities.Seed(Guid.NewGuid(), "agent-skill", "Leaf Community", CommonsAgentSkills.SkillSlug,
                new JsonObject { ["owner_plugin"] = plugin.Id.ToString() }, createdBy: "plugin:leaf-commons")
            : entities.Seed(Guid.NewGuid(), "agent-skill", "Other", "other", []);
        entities.Seed(Guid.Parse(AgentA), "agent", "Agent A", "agent-a",
            new JsonObject { ["owner_id"] = Owner, ["skills"] = new JsonArray(OtherSkill) });
        return new World(entities, new CommonsAgentSkills(entities), skill, plugin);
    }

    internal static async Task<string[]> SkillsOf(MemoryEntityStore entities, string agentId)
        => ((await entities.GetAsync(Guid.Parse(agentId)))!.Data["skills"] as JsonArray ?? [])
            .Select(n => n!.GetValue<string>()).ToArray();

    [Fact]
    public async Task Adding_puts_the_skill_on_once_and_keeps_other_skills_and_the_ownership_stamp()
    {
        var w = Create();
        Assert.Equal(SkillAssignmentStatus.Added, (await w.Skills.AddAsync(AgentA, Owner)).Status);
        Assert.Equal([OtherSkill, w.Skill.Id.ToString()], await SkillsOf(w.Entities, AgentA));

        Assert.Equal(SkillAssignmentStatus.AlreadyPresent, (await w.Skills.AddAsync(AgentA, Owner)).Status);
        Assert.Equal([OtherSkill, w.Skill.Id.ToString()], await SkillsOf(w.Entities, AgentA));
        var agent = (await w.Entities.GetAsync(Guid.Parse(AgentA)))!;
        Assert.Null(agent.Data["owner_plugin"]);
        Assert.Equal(Owner, agent.Data["owner_id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("upper")]
    [InlineData("leaf-community")]
    public async Task A_hand_assigned_skill_by_id_or_slug_counts_as_present(string? form)
    {
        var w = Create();
        var reference = form switch
        {
            null => w.Skill.Id.ToString(),
            "upper" => w.Skill.Id.ToString().ToUpperInvariant(),
            _ => form,
        };
        await w.Entities.PatchAsync(Guid.Parse(AgentA), new JsonObject { ["skills"] = new JsonArray(OtherSkill, reference) });
        Assert.Equal(SkillAssignmentStatus.AlreadyPresent, (await w.Skills.AddAsync(AgentA, Owner)).Status);
        Assert.Equal([OtherSkill, reference], await SkillsOf(w.Entities, AgentA));
    }

    [Fact]
    public async Task Removing_takes_away_every_reference_to_the_skill_and_nothing_else()
    {
        var w = Create();
        await w.Entities.PatchAsync(Guid.Parse(AgentA), new JsonObject
            { ["skills"] = new JsonArray(OtherSkill, CommonsAgentSkills.SkillSlug, w.Skill.Id.ToString()) });

        Assert.Equal(SkillAssignmentStatus.Removed, (await w.Skills.RemoveAsync(AgentA, Owner)).Status);
        Assert.Equal([OtherSkill], await SkillsOf(w.Entities, AgentA));
        Assert.Equal(SkillAssignmentStatus.NotPresent, (await w.Skills.RemoveAsync(AgentA, Owner)).Status);
        Assert.Equal([OtherSkill], await SkillsOf(w.Entities, AgentA));
    }

    [Fact]
    public async Task An_Agent_owned_by_another_plugin_is_reported_manual_required_not_written()
    {
        var w = Create();
        var foreign = w.Entities.Seed(Guid.NewGuid(), "agent", "Seeded", "seeded", new JsonObject
        {
            ["owner_plugin"] = Guid.NewGuid().ToString(), ["owner_id"] = Owner, ["skills"] = new JsonArray(OtherSkill),
        });
        var result = await w.Skills.AddAsync(foreign.Id.ToString(), Owner);
        Assert.Equal(SkillAssignmentStatus.ManualRequired, result.Status);
        Assert.Equal([OtherSkill], await SkillsOf(w.Entities, foreign.Id.ToString()));
    }

    [Fact]
    public async Task Another_users_confidential_Agent_a_missing_Agent_and_a_missing_skill_are_left_alone()
    {
        var w = Create();
        var hidden = w.Entities.Seed(Guid.NewGuid(), "agent", "Private", "private", new JsonObject
            { ["confidential"] = true, ["owner_id"] = "33333333-3333-3333-3333-333333333333" });
        Assert.Equal(SkillAssignmentStatus.AgentMissing, (await w.Skills.AddAsync(hidden.Id.ToString(), Owner)).Status);
        Assert.Null(hidden.Data["skills"]);
        Assert.Equal(SkillAssignmentStatus.AgentMissing, (await w.Skills.AddAsync(Guid.NewGuid().ToString(), Owner)).Status);

        var noSkill = Create(seedSkill: false);
        Assert.Equal(SkillAssignmentStatus.SkillMissing, (await noSkill.Skills.AddAsync(AgentA, Owner)).Status);
        Assert.Equal([OtherSkill], await SkillsOf(noSkill.Entities, AgentA));
    }

    [Fact]
    public async Task A_user_made_skill_named_leaf_community_is_not_mistaken_for_the_plugin_skill()
    {
        var entities = new MemoryEntityStore();
        entities.Seed(Guid.NewGuid(), "plugin", "Leaf Community", LeafCommonsPlugin.PluginId, []);
        entities.Seed(Guid.NewGuid(), "agent-skill", "Mine", CommonsAgentSkills.SkillSlug,
            new JsonObject { ["owner_plugin"] = Guid.NewGuid().ToString() });
        entities.Seed(Guid.Parse(AgentA), "agent", "Agent A", "agent-a", new JsonObject { ["owner_id"] = Owner });
        Assert.Equal(SkillAssignmentStatus.SkillMissing,
            (await new CommonsAgentSkills(entities).AddAsync(AgentA, Owner)).Status);
    }
}

/// <summary>Attach and detach on the real owner actions: settings, startup, Agent removal, briefs.</summary>
public sealed class AgentSkillLifecycleTests : IAsyncLifetime
{
    private const string Owner = AgentSkillAssignmentTests.Owner;
    private const string AgentA = "a1000000-0000-0000-0000-000000000001";
    private const string AgentB = "b2000000-0000-0000-0000-000000000002";
    private const string Foreign = "c3000000-0000-0000-0000-000000000003";
    private readonly string root = Path.Combine(Path.GetTempPath(), "leaf-commons-skill-" + Guid.NewGuid().ToString("N"));
    private AgentSkillAssignmentTests.World world = null!;
    private CommonsStateStore store = null!;
    private PublicAgentCoordinator coordinator = null!;
    private WebApplication? app;
    private HttpClient http = null!;
    private ClaimsPrincipal principal = new(new ClaimsIdentity());

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        world = AgentSkillAssignmentTests.Create();
        world.Entities.Seed(Guid.Parse(AgentB), "agent", "Agent B", "agent-b", new JsonObject { ["owner_id"] = Owner });
        world.Entities.Seed(Guid.Parse(Foreign), "agent", "Someone else's", "foreign",
            new JsonObject { ["owner_id"] = "44444444-4444-4444-4444-444444444444" });
        store = new CommonsStateStore(world.Entities);
        await store.SaveOwnerSettingsAsync(new(null, "https://relay.example", "Pseudonym", [], [], [], Owner, "Pseudonym"));
        var relays = new RelayClientFactory(new FixedHttpClientFactory(new MultiAgentRelayHandler("Pseudonym")), store);
        var runtime = new PublicAgentRuntime(new CountingModel("unused"), new AgentShareableStores(root), relays,
            new OutboundFilter(new SecretScanner()), store, new NullDigestSink(),
            outboundReview: TestSentinel.Passing(world.Entities));
        coordinator = new PublicAgentCoordinator(store, new AgentDirectory(world.Entities), relays, runtime,
            new NullPluginEvents(), store, LoggerFactory.Create(_ => { }), world.Skills);
        var images = new ThrowingAvatarImageSource("unused");

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IAuthContext>(new TestAuth(Owner));
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(new AgentDirectory(world.Entities));
        builder.Services.AddSingleton(relays);
        builder.Services.AddSingleton<IAvatarImageSource>(images);
        builder.Services.AddSingleton(new PublicAgentAvatarSync(store, images, new AvatarImageProcessor(), relays,
            new NullPluginEvents(), store));
        builder.Services.AddSingleton(coordinator);
        builder.Services.AddSingleton(runtime);
        builder.Services.AddSingleton<PublicAgentThreads>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<PublicAgentConversationCoordinator>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<ShareableSetBuilder>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<AgentShareableStores>(_ => throw new NotSupportedException());
        app = builder.Build();
        app.Use((context, next) => { context.User = principal; return next(context); });
        CommonsEndpoints.Map(app.MapGroup("/api/apps/leaf-commons"));
        await app.StartAsync();
        http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        http?.Dispose();
        if (app is not null) await app.DisposeAsync();
        await coordinator.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private static object Settings(params (string Id, bool Enabled)[] agents) => new
    {
        relayBaseUrl = "https://relay.example", publicName = "Pseudonym", relayConfirmedPublicName = "Pseudonym",
        personalWords = Array.Empty<string>(), canaries = Array.Empty<string>(), linkAllowlist = Array.Empty<string>(),
        agents = agents.Select(agent => new
        {
            agentId = agent.Id, enabled = agent.Enabled, avatarSource = AvatarSources.AgentAvatar,
            shareableFolders = Array.Empty<string>(),
        }).ToArray(),
    };

    private async Task<JsonObject> PutSettingsAsync(params (string Id, bool Enabled)[] agents)
    {
        using var response = await http.PutAsJsonAsync("/api/apps/leaf-commons/settings", Settings(agents));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private static (string, string)[] Assignments(JsonObject response)
        => (response["skillAssignment"] as JsonArray ?? [])
            .Select(item => (item!["agentId"]!.GetValue<string>(), item["status"]!.GetValue<string>())).ToArray();

    [Fact]
    public async Task Enabling_in_settings_adds_the_skill_and_disabling_removes_it()
    {
        var skill = world.Skill.Id.ToString();
        var enabled = await PutSettingsAsync((AgentA, true), (AgentB, false));
        Assert.Equal([(AgentA, SkillAssignmentStatus.Added)], Assignments(enabled));
        Assert.Equal(["d9dc6cfa-06ea-4e12-896f-416e2fa2a3fe", skill], await AgentSkillAssignmentTests.SkillsOf(world.Entities, AgentA));
        Assert.Empty(await AgentSkillAssignmentTests.SkillsOf(world.Entities, AgentB)); // never public: untouched

        var again = await PutSettingsAsync((AgentA, true), (AgentB, false));
        Assert.Equal([(AgentA, SkillAssignmentStatus.AlreadyPresent)], Assignments(again));

        var disabled = await PutSettingsAsync((AgentA, false), (AgentB, false));
        Assert.Equal([(AgentA, SkillAssignmentStatus.Removed)], Assignments(disabled));
        Assert.Equal(["d9dc6cfa-06ea-4e12-896f-416e2fa2a3fe"], await AgentSkillAssignmentTests.SkillsOf(world.Entities, AgentA));
        Assert.False((await store.GetPublicAgentAsync(AgentA))!.Enabled);

        var still = await PutSettingsAsync((AgentA, false), (AgentB, false));
        Assert.Null(still["skillAssignment"]); // already off: nothing to report, nothing written
    }

    [Fact]
    public async Task Settings_save_the_two_conversation_switches_and_keep_them_when_left_out()
    {
        await PutSettingsAsync((AgentA, true));
        var defaults = (await store.GetPublicAgentAsync(AgentA))!.Switches;
        Assert.Equal(new ConversationSwitches(AnswersWhenSpokenTo: true, StartsConversations: false), defaults);

        using var response = await http.PutAsJsonAsync("/api/apps/leaf-commons/settings", new
        {
            relayBaseUrl = "https://relay.example", publicName = "Pseudonym", relayConfirmedPublicName = "Pseudonym",
            personalWords = Array.Empty<string>(), canaries = Array.Empty<string>(), linkAllowlist = Array.Empty<string>(),
            agents = new[] { new { agentId = AgentA, enabled = true, avatarSource = AvatarSources.AgentAvatar,
                shareableFolders = Array.Empty<string>(), answersWhenSpokenTo = false, startsConversations = true } },
        });
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.True(response.IsSuccessStatusCode, body.ToJsonString());
        var agent = body["agents"]!.AsArray().Single(item => item!["agentId"]!.GetValue<string>() == AgentA)!;
        Assert.False(agent["answersWhenSpokenTo"]!.GetValue<bool>());
        Assert.True(agent["startsConversations"]!.GetValue<bool>());
        Assert.Null(agent["scenarioToggles"]);

        await PutSettingsAsync((AgentA, true));
        Assert.Equal(new ConversationSwitches(false, true), (await store.GetPublicAgentAsync(AgentA))!.Switches);
    }

    [Fact]
    public async Task Settings_refuse_an_Agent_the_owner_does_not_own_before_any_skill_write()
    {
        using var response = await http.PutAsJsonAsync("/api/apps/leaf-commons/settings", Settings((Foreign, true)));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("agent_not_owned", await response.Content.ReadAsStringAsync());
        Assert.Null((await world.Entities.GetAsync(Guid.Parse(Foreign)))!.Data["skills"]);
    }

    [Fact]
    public async Task Startup_gives_already_public_Agents_the_skill_and_only_them()
    {
        await store.SavePublicAgentAsync(new(null, AgentA, "Agent A", true, null, "disconnected", null,
            ConversationSwitches.Default, []));
        await store.SavePublicAgentAsync(new(null, AgentB, "Agent B", false, null, "suspended", null,
            ConversationSwitches.Default, []));
        await store.SavePublicAgentAsync(new(null, Foreign, "Someone else's", true, null, "disconnected", null,
            ConversationSwitches.Default, []));

        await coordinator.StartAsync(CancellationToken.None);
        await coordinator.StartAsync(CancellationToken.None); // a second start changes nothing

        Assert.Equal(["d9dc6cfa-06ea-4e12-896f-416e2fa2a3fe", world.Skill.Id.ToString()],
            await AgentSkillAssignmentTests.SkillsOf(world.Entities, AgentA));
        Assert.Empty(await AgentSkillAssignmentTests.SkillsOf(world.Entities, AgentB));
        Assert.Empty(await AgentSkillAssignmentTests.SkillsOf(world.Entities, Foreign));
    }

    [Fact]
    public async Task Removing_a_public_Agent_takes_the_skill_away()
    {
        await PutSettingsAsync((AgentA, true));
        Assert.Contains(world.Skill.Id.ToString(), await AgentSkillAssignmentTests.SkillsOf(world.Entities, AgentA));

        // The Agent stops being the owner's (as SyncAgentNameAsync sees on entity.updated).
        await world.Entities.PatchAsync(Guid.Parse(AgentA), new JsonObject
            { ["owner_id"] = "44444444-4444-4444-4444-444444444444" });
        await coordinator.SyncAgentNameAsync(AgentA);

        Assert.False((await store.GetPublicAgentAsync(AgentA))!.Enabled);
        Assert.Equal(["d9dc6cfa-06ea-4e12-896f-416e2fa2a3fe"], await AgentSkillAssignmentTests.SkillsOf(world.Entities, AgentA));

        // A deleted Agent has nothing left to change and does not fail.
        await world.Entities.DeleteAsync(Guid.Parse(AgentA));
        await coordinator.HandleAgentRemovedAsync(AgentA);
    }

    private static ClaimsPrincipal Execution(string agentId, string beneficiary, params string[] contexts)
    {
        var context = string.Join(",", contexts.Select(kind => $$"""{"kind":"{{kind}}","id":"{{kind}}-1"}"""));
        var identity = $$"""{"app":{"id":"agent-app"},"actor":{"kind":"agent","entityId":"{{agentId}}"},"beneficiary":{"kind":"user","id":"{{beneficiary}}"},"context":[{{context}}]}""";
        return new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sub", beneficiary), new Claim("token_use", "execution"),
            new Claim("execution_identity", identity)], "test"));
    }

    private async Task<(HttpStatusCode Status, JsonObject Body)> PostBriefAsync(ClaimsPrincipal caller, object brief)
    {
        principal = caller;
        using var response = await http.PostAsJsonAsync("/api/apps/leaf-commons/briefs", brief);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject());
    }

    [Fact]
    public async Task Brief_route_accepts_only_the_public_Agent_from_a_private_discussion()
    {
        await store.SavePublicAgentAsync(new(null, AgentA, "Agent A", true, "credential-a", "active", null,
            ConversationSwitches.Default, []));
        var brief = new { kind = "consult", title = "Help with a build", content = "A short question.", ownerRequested = true };

        foreach (var caller in new[]
        {
            Execution(AgentA, Owner, "discussion", "external-conversation"), // a public turn
            Execution(AgentA, Owner, "automation"),                         // no private discussion
            Execution(AgentA, "55555555-5555-5555-5555-555555555555", "discussion"), // not the owner
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Owner)], "test")), // owner session, no Agent
        })
        {
            var refused = await PostBriefAsync(caller, brief);
            Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
            Assert.Equal("owner_agent_required", refused.Body["error"]!.GetValue<string>());
        }

        var notPublic = await PostBriefAsync(Execution(AgentB, Owner, "discussion"), brief);
        Assert.Equal(HttpStatusCode.Forbidden, notPublic.Status);
        Assert.Equal("agent_not_public", notPublic.Body["error"]!.GetValue<string>());

        var posted = await PostBriefAsync(Execution(AgentA, Owner, "discussion"), brief);
        Assert.Equal(HttpStatusCode.OK, posted.Status);
        Assert.Equal("posted", posted.Body["decision"]!.GetValue<string>());
        Assert.Equal("message-1", posted.Body["messageId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Brief_validation_and_the_2000_character_filter_limit_are_reported_as_documented()
    {
        await store.SavePublicAgentAsync(new(null, AgentA, "Agent A", true, "credential-a", "active", null,
            ConversationSwitches.Default, []));
        var caller = Execution(AgentA, Owner, "discussion");

        var untitled = await PostBriefAsync(caller, new { kind = "bug", content = "Steps to reproduce." });
        Assert.Equal(HttpStatusCode.BadRequest, untitled.Status);
        Assert.Equal("invalid_brief", untitled.Body["error"]!.GetValue<string>());
        var unknown = await PostBriefAsync(caller, new { kind = "announcement", content = "Hello." });
        Assert.Equal("invalid_brief", unknown.Body["error"]!.GetValue<string>());
        var tooMany = await PostBriefAsync(caller, new { kind = "showcase", content = "Look.",
            addresses = Enumerable.Range(0, 11).Select(i => $"envoy-{i}").ToArray() });
        Assert.Equal("invalid_brief", tooMany.Body["error"]!.GetValue<string>());

        var atLimit = await PostBriefAsync(caller, new { kind = "showcase", content = new string('a', 2_000), ownerRequested = true });
        Assert.Equal("posted", atLimit.Body["decision"]!.GetValue<string>());
        var overLimit = await PostBriefAsync(caller, new { kind = "showcase", content = new string('a', 2_001), ownerRequested = true });
        Assert.Equal(HttpStatusCode.OK, overLimit.Status);
        Assert.Equal("blocked", overLimit.Body["decision"]!.GetValue<string>());
        Assert.Equal("content-too-long", overLimit.Body["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_brief_on_the_Agents_own_initiative_needs_the_starts_conversations_switch()
    {
        await store.SavePublicAgentAsync(new(null, AgentA, "Agent A", true, "credential-a", "active", null,
            new ConversationSwitches(StartsConversations: false), []));
        var caller = Execution(AgentA, Owner, "discussion");

        var own = await PostBriefAsync(caller, new { kind = "showcase", content = "Something I made." });
        Assert.Equal(HttpStatusCode.OK, own.Status);
        Assert.Equal("blocked", own.Body["decision"]!.GetValue<string>());
        Assert.Equal("starts_disabled", own.Body["reason"]!.GetValue<string>());

        var asked = await PostBriefAsync(caller, new { kind = "showcase", content = "Something I made.", ownerRequested = true });
        Assert.Equal("posted", asked.Body["decision"]!.GetValue<string>());

        await store.SavePublicAgentAsync((await store.GetPublicAgentAsync(AgentA))! with
            { Switches = new(StartsConversations: true) });
        var allowed = await PostBriefAsync(caller, new { kind = "showcase", content = "Another thing I made." });
        Assert.Equal("posted", allowed.Body["decision"]!.GetValue<string>());
    }
}
