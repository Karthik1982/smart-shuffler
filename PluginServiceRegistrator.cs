using System.Reflection;
using Jellyfin.Plugin.SmartShufflePlaylist.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.SmartShufflePlaylist;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<SmartShuffleService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<SmartShuffleService>());
        services.AddSingleton<IHostedService, JavaScriptInjectorBridge>();
        services.AddControllers().AddApplicationPart(Assembly.GetExecutingAssembly());
    }
}
