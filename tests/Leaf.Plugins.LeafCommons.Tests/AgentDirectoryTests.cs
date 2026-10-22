using System.Text.Json.Nodes;
using Xunit;

namespace Leaf.Plugins.LeafCommons.Tests;

public sealed class AgentDirectoryTests
{
    private const string OwnerId = "28a07bf5-3a37-47d4-a9ab-bdc09221d547";

    [Fact]
    public async Task Lists_account_and_local_user_agents_but_not_plugin_system_agents()
    {
        var entities = new MemoryEntityStore();
        Seed(entities, "b863d153-f23d-431a-91c4-b5ffd8d0815e", "Leni", OwnerId);
        Seed(entities, "a7b059ac-f678-8b58-819b-1bcf3acb22db", "Nova", OwnerId);
        Seed(entities, "1a7bfe1d-fbc0-b950-aa93-50eb05408c67", "Spark", OwnerId);
        Seed(entities, "1147ae5e-9651-4fb1-80ce-e4f3a69c7f53", "Axl", "local-user");
        Seed(entities, "822baf37-e955-4e88-827e-91d437719ed6", "Muse", "local-user");
        Seed(entities, "a134c8bf-c7df-4c53-86ff-6afbb6cb34f7", "Wren", "local-user");
        Seed(entities, "e89c610d-0f2e-442d-b1f9-a83f7dc84bb2", "Gemma", "local-user");
        var copilot = entities.Seed(
            Guid.Parse("2c08d5c8-a206-4358-9f34-ec2a34d57582"), "agent",
            "The Ember Concord Copilot", "storyboard-2db07761979b44bbba934a9d1becf4f3",
            new JsonObject
            {
                ["owner_id"] = OwnerId,
                ["owner_plugin"] = "e765a440-43d7-0e5a-b3b1-d1088afad217",
                ["workspace"] = "da9e234a-1936-4aed-bc47-8c0dfde34cd3",
            }, "plugin:storyboard");

        var directory = new AgentDirectory(entities);
        var agents = await directory.ListAsync(OwnerId);

        Assert.Equal(
        [
            "Axl", "Gemma", "Leni", "Muse", "Nova", "Spark", "Wren",
        ], agents.Select(agent => agent.Name).ToArray());
        Assert.NotNull(await directory.GetAsync(
            "1147ae5e-9651-4fb1-80ce-e4f3a69c7f53", OwnerId));
        Assert.Null(await directory.GetAsync(copilot.Id.ToString(), OwnerId));
    }

    private static Leaf.Sdk.LeafEntity Seed(
        MemoryEntityStore entities, string id, string name, string createdBy)
        => entities.Seed(Guid.Parse(id), "agent", name,
            name.ToLowerInvariant().Replace(' ', '-'), [], createdBy);
}
