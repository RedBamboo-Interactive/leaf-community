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
        var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedLeaf", "PluginData", PluginId, "shareable");
        services.AddSingleton(new ShareableStore(dataRoot));
        services.AddSingleton(sp => new CommonsStateStore(sp.GetRequiredKeyedService<IEntityStore>(PluginId)));
        services.AddSingleton<ICommonsAudit>(sp => sp.GetRequiredService<CommonsStateStore>());
        services.AddSingleton<IToolFreeModelClient, ToolFreeModelClient>();
        // Async on purpose: plugin code can run on the kernel's WPF dispatcher, where blocking on
        // GetSettingsAsync deadlocks the UI thread (and with it the in-process restart).
        services.AddSingleton<Func<CancellationToken, Task<(Uri BaseUri, string? Credential)>>>(sp =>
        {
            var state = sp.GetRequiredService<CommonsStateStore>();
            return async ct =>
            {
                var current = await state.GetSettingsAsync(ct).ConfigureAwait(false);
                return (new Uri(current.RelayBaseUrl), current.Credential);
            };
        });
        services.AddHttpClient("commons-relay");
        services.AddSingleton(sp => new RelayHttpClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("commons-relay"), sp.GetRequiredService<Func<CancellationToken, Task<(Uri BaseUri, string? Credential)>>>()));
        services.AddSingleton<IEnvoyRelay>(sp => sp.GetRequiredService<RelayHttpClient>());
        services.AddSingleton<IAvatarUploader>(sp => sp.GetRequiredService<RelayHttpClient>());
        services.AddSingleton<IAvatarImageSource>(sp => new RedLeafAvatarImageSource(
            sp.GetRequiredKeyedService<IEntityStore>(PluginId),
            sp.GetRequiredService<IAssets>()));
        services.AddSingleton<AvatarImageProcessor>();
        services.AddSingleton(sp => new AvatarSyncService(
            sp.GetRequiredService<CommonsStateStore>(),
            sp.GetRequiredService<IAvatarImageSource>(),
            sp.GetRequiredService<AvatarImageProcessor>(),
            sp.GetRequiredService<IAvatarUploader>(),
            sp.GetRequiredKeyedService<IPluginEvents>(PluginId),
            sp.GetRequiredService<ICommonsAudit>()));
        services.AddSingleton<CommonsDigestSink>();
        services.AddSingleton<IDigestSink>(sp => sp.GetRequiredService<CommonsDigestSink>());
        services.AddSingleton<PublicNovaThreads>();
        services.AddSingleton(sp => new CommonsNovaContextExtension(
            sp.GetRequiredKeyedService<IPluginExtensions>(PluginId),
            sp.GetRequiredService<CommonsStateStore>()));
        services.AddSingleton<CommonsRuntime>();
        services.AddSingleton(sp => new ConnectorCore((request, ct) => sp.GetRequiredService<CommonsRuntime>().HandleAsync(request, ct)));
        services.AddSingleton<RelayConnector>();
    }
    public void MapEndpoints(RouteGroupBuilder group) => CommonsEndpoints.Map(group);
    public async Task OnStartupAsync(IPluginHost host, CancellationToken ct)
    {
        host.GetRequiredService<CommonsNovaContextExtension>().Start();
        var stopping = host.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        await host.GetRequiredService<AvatarSyncService>().StartAsync(stopping);
        await host.GetRequiredService<RelayConnector>().StartAsync(stopping);
    }
}
