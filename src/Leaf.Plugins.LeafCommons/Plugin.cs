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
        services.AddSingleton<Func<(Uri BaseUri, string? Credential)>>(sp => () => { var settings = sp.GetRequiredService<CommonsStateStore>().GetSettingsAsync().GetAwaiter().GetResult(); return (new Uri(settings.RelayBaseUrl), settings.Credential); });
        services.AddHttpClient("commons-relay"); services.AddHttpClient("commons-digest");
        services.AddSingleton(sp => new RelayHttpClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("commons-relay"), sp.GetRequiredService<Func<(Uri BaseUri, string? Credential)>>()));
        services.AddSingleton<IEnvoyRelay>(sp => sp.GetRequiredService<RelayHttpClient>());
        services.AddSingleton(sp => new NovaDigestSink(sp.GetRequiredService<IHttpClientFactory>().CreateClient("commons-digest"), sp.GetRequiredKeyedService<IExecutionTokens>(PluginId), sp.GetRequiredService<CommonsStateStore>(), sp.GetRequiredService<DigestFormatter>(), sp.GetRequiredService<ICommonsAudit>()));
        services.AddSingleton<IDigestSink>(sp => sp.GetRequiredService<NovaDigestSink>());
        services.AddSingleton<CommonsRuntime>();
        services.AddSingleton(sp => new ConnectorCore((request, ct) => sp.GetRequiredService<CommonsRuntime>().HandleAsync(request, ct)));
        services.AddSingleton<RelayConnector>();
    }
    public void MapEndpoints(RouteGroupBuilder group) => CommonsEndpoints.Map(group);
    public Task OnStartupAsync(IPluginHost host, CancellationToken ct) => host.GetRequiredService<RelayConnector>().StartAsync(host.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
}
