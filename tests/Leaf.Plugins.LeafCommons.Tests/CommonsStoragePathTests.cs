using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Leaf.Plugins.LeafCommons.Tests;

/// <summary>
/// Leaf Community's only own folder is the shareable-set store. It used to be built as
/// <c>%LOCALAPPDATA%\RedLeaf\PluginData\leaf-commons</c>, which is exactly what RedLeaf gives a plugin while its data is in
/// the default place, so nothing moves; with a relocated data location RedLeaf moves <c>PluginData</c> whole.
/// </summary>
public sealed class CommonsStoragePathTests : IDisposable
{
    private readonly string _dataRoot = Directory.CreateTempSubdirectory("commons-storage").FullName;

    private string DataDirectory => Path.Combine(_dataRoot, "PluginData", LeafCommonsPlugin.PluginId);

    public void Dispose()
    {
        try { Directory.Delete(_dataRoot, true); } catch (IOException) { }
    }

    private ServiceProvider Build(string dataDirectory)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var plugin = new LeafCommonsPlugin();
        plugin.ConfigureServices(services, new PluginContext
        {
            Manifest = plugin.Manifest,
            PluginDirectory = _dataRoot,
            DevMode = true,
            DataDirectory = dataDirectory,
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Every_shareable_store_resolves_under_the_data_directory()
    {
        using var provider = Build(DataDirectory);
        var stores = provider.GetRequiredService<AgentShareableStores>();

        var legacy = stores.For(CommonsStateStore.LegacyNovaAgentId).RootPath;
        var other = stores.For("0f0e0d0c-0b0a-0908-0706-050403020100").RootPath;

        Assert.Equal(Path.Combine(DataDirectory, "shareable"), legacy);
        Assert.StartsWith(Path.Combine(DataDirectory, "shareable-agents") + Path.DirectorySeparatorChar, other);
    }

    [Fact]
    public void A_data_location_that_moved_is_followed_not_the_default_folder()
    {
        var moved = Path.Combine(_dataRoot, "elsewhere", "PluginData", LeafCommonsPlugin.PluginId);
        using var provider = Build(moved);

        var path = provider.GetRequiredService<AgentShareableStores>().For("any-agent").RootPath;

        Assert.StartsWith(moved, path);
        Assert.DoesNotContain(Path.Combine("RedLeaf", "PluginData"), path);
    }

    [Fact]
    public void The_manifest_requires_a_kernel_that_has_the_data_directory()
        => Assert.Equal("^0.1.13", new LeafCommonsPlugin().Manifest.KernelApi);
}
