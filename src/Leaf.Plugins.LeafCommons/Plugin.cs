using System.Reflection;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Leaf.Plugins.LeafCommons;

public sealed class LeafCommonsPlugin : ILeafPlugin
{
    public const string PluginId = "leaf-commons";
    public PluginManifest Manifest { get; } = LoadManifest();
    private static PluginManifest LoadManifest() { using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("plugin.json") ?? throw new InvalidOperationException("Embedded plugin.json missing"); using var reader = new StreamReader(stream); return PluginManifest.Parse(reader.ReadToEnd()); }

    public void ConfigureServices(IServiceCollection services, PluginContext ctx)
    {
        services.AddSingleton<SecretScanner>(); services.AddSingleton<OutboundFilter>(); services.AddSingleton<DigestFormatter>();
        services.AddSingleton<ShareableSetBuilder>();
        var pluginDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedLeaf", "PluginData", PluginId);
        services.AddSingleton(new AgentShareableStores(pluginDataRoot));
        services.AddSingleton(sp => new CommonsStateStore(sp.GetRequiredKeyedService<IEntityStore>(PluginId)));
        services.AddSingleton(sp => new AgentDirectory(sp.GetRequiredKeyedService<IEntityStore>(PluginId)));
        services.AddSingleton<ICommonsAudit>(sp => sp.GetRequiredService<CommonsStateStore>());
        services.AddSingleton<IToolFreeModelClient, ToolFreeModelClient>();
        services.AddHttpClient("commons-relay");
        services.AddSingleton<RelayClientFactory>();
        services.AddSingleton<IAvatarImageSource>(sp => new RedLeafAvatarImageSource(
            sp.GetRequiredKeyedService<IEntityStore>(PluginId),
            sp.GetRequiredService<IAssets>()));
        services.AddSingleton<AvatarImageProcessor>();
        services.AddSingleton(sp => new PublicAgentAvatarSync(
            sp.GetRequiredService<CommonsStateStore>(), sp.GetRequiredService<IAvatarImageSource>(),
            sp.GetRequiredService<AvatarImageProcessor>(), sp.GetRequiredService<RelayClientFactory>(),
            sp.GetRequiredKeyedService<IPluginEvents>(PluginId), sp.GetRequiredService<ICommonsAudit>()));
        services.AddSingleton<CommonsDigestSink>();
        services.AddSingleton<IDigestSink>(sp => sp.GetRequiredService<CommonsDigestSink>());
        services.AddSingleton<PublicAgentThreads>();
        services.AddSingleton(sp => new CommonsNovaContextExtension(
            sp.GetRequiredKeyedService<IPluginExtensions>(PluginId),
            sp.GetRequiredService<CommonsStateStore>()));
        services.AddSingleton<PublicAgentRuntime>();
        services.AddSingleton(sp => new PublicAgentCoordinator(
            sp.GetRequiredService<CommonsStateStore>(), sp.GetRequiredService<AgentDirectory>(),
            sp.GetRequiredService<RelayClientFactory>(), sp.GetRequiredService<PublicAgentRuntime>(),
            sp.GetRequiredKeyedService<IPluginEvents>(PluginId), sp.GetRequiredService<ICommonsAudit>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()));
    }
    public void MapEndpoints(RouteGroupBuilder group) => CommonsEndpoints.Map(group);
    public async Task OnStartupAsync(IPluginHost host, CancellationToken ct)
    {
        host.GetRequiredService<CommonsNovaContextExtension>().Start();
        var stopping = host.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        await host.GetRequiredService<PublicAgentAvatarSync>().StartAsync(stopping);
        await host.GetRequiredService<PublicAgentCoordinator>().StartAsync(stopping);
    }
}
