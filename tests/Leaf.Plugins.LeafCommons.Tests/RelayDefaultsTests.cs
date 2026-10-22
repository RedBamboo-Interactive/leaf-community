using Xunit;

namespace Leaf.Plugins.LeafCommons.Tests;

public sealed class RelayDefaultsTests
{
    private const string LabRelay =
        "https://nova-commons-relay-lab.becherel-laurent.workers.dev";

    [Fact]
    public async Task Fresh_install_uses_production_and_persists_it_as_an_empty_value()
    {
        var entities = new MemoryEntityStore();
        var store = new CommonsStateStore(entities);

        var fresh = await store.GetOwnerSettingsAsync();
        Assert.Equal(CommonsRelayDefaults.ProductionBaseUrl, fresh.RelayBaseUrl);
        Assert.False(CommonsRelayDefaults.IsCustom(fresh.RelayBaseUrl));

        await store.SaveOwnerSettingsAsync(fresh);
        var entity = await entities.GetBySlugAsync("leaf-commons-settings", "settings");
        Assert.Equal("", entity!.Data["relay_base_url"]!.GetValue<string>());
        Assert.Equal(CommonsRelayDefaults.ProductionBaseUrl,
            (await store.GetOwnerSettingsAsync()).RelayBaseUrl);
    }

    [Fact]
    public async Task A_non_empty_relay_override_is_preserved_and_used()
    {
        var entities = new MemoryEntityStore();
        var store = new CommonsStateStore(entities);

        await store.SaveOwnerSettingsAsync(new(null, LabRelay, "ExampleUser",
            [], [], [], "owner"));

        var saved = await store.GetOwnerSettingsAsync();
        Assert.Equal(LabRelay, saved.RelayBaseUrl);
        Assert.True(CommonsRelayDefaults.IsCustom(saved.RelayBaseUrl));
        var entity = await entities.GetBySlugAsync("leaf-commons-settings", "settings");
        Assert.Equal(LabRelay, entity!.Data["relay_base_url"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_config_values_resolve_to_the_single_production_default(string? value)
        => Assert.Equal(CommonsRelayDefaults.ProductionBaseUrl,
            CommonsRelayDefaults.EffectiveBaseUrl(value));
}
