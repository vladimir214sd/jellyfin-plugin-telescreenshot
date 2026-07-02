using Jellyfin.Plugin.TeleScreenshot.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.TeleScreenshot;

/// <summary>
/// Registers the plugin's services with Jellyfin's DI container.
/// Implements <c>IPluginServiceRegistrator</c> — the entry point Jellyfin calls during startup
/// to wire up plugin-scoped services before the plugin assembly's controllers are activated.
/// </summary>
/// <remarks>
/// On Jellyfin 10.11 the interface signature is
/// <c>RegisterServices(IServiceCollection, IServerApplicationHost)</c>. The host parameter is
/// unused here but must be present to satisfy the contract.
/// </remarks>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost serverApplicationHost)
    {
        // Named client for outbound Telegram calls. AddHttpClient returns an IHttpClientBuilder
        // and registers IHttpClientFactory into the container automatically.
        serviceCollection.AddHttpClient<TelegramClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-TeleScreenshot/1.0");
        });

        // TelegramClient is a singleton wrapper around the typed HttpClient; controllers resolve
        // it transiently but the underlying HttpMessageHandler pool is managed by the factory.
        serviceCollection.AddSingleton<TelegramService>();
    }
}
