using Jellyfin.Plugin.IssueReporter.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IssueReporter;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<StateStoreService>();
        serviceCollection.AddSingleton<PrivacyRedactorService>();
        serviceCollection.AddSingleton<ErrorFilterService>();
        serviceCollection.AddSingleton<PluginDiscoveryService>();
        serviceCollection.AddSingleton<GitHubAuthService>();
        serviceCollection.AddSingleton<GitHubApiClient>();
        serviceCollection.AddSingleton<DeduplicationService>();
        serviceCollection.AddSingleton<IssueTemplateEngine>();
        serviceCollection.AddSingleton<ResourceEfficientLogMonitor>();
        serviceCollection.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<ResourceEfficientLogMonitor>());
        serviceCollection.AddSingleton<IssueReporterCoordinator>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<IssueReporterCoordinator>());
    }
}
