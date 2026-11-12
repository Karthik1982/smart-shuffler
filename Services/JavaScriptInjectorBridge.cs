using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.SmartShufflePlaylist.Services;

public sealed class JavaScriptInjectorBridge : IHostedService
{
    private const string ScriptId = "7a28699c-e118-4666-84a0-e106de3866fa-native-progress";
    private const string ScriptName = "Smart Shuffle Playlist Progress";
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RetryWindow = TimeSpan.FromMinutes(2);

    private readonly ILogger<JavaScriptInjectorBridge> _logger;
    private CancellationTokenSource? _cts;

    public JavaScriptInjectorBridge(ILogger<JavaScriptInjectorBridge> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => RegisterWithRetryAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    private async Task RegisterWithRetryAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + RetryWindow;
        while (!cancellationToken.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            try
            {
                if (TryRegister())
                {
                    _logger.LogInformation("Registered native playlist progress script with JavaScript Injector.");
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "JavaScript Injector registration attempt failed.");
            }

            try
            {
                await Task.Delay(RetryInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static bool TryRegister()
    {
        var assembly = AssemblyLoadContext.All
            .SelectMany(context => context.Assemblies)
            .FirstOrDefault(a => a.FullName?.Contains("Jellyfin.Plugin.JavaScriptInjector", StringComparison.Ordinal) == true);

        if (assembly == null)
        {
            return false;
        }

        var interfaceType = assembly.GetType("Jellyfin.Plugin.JavaScriptInjector.PluginInterface");
        var registerMethod = interfaceType?.GetMethod(
            "RegisterScript",
            BindingFlags.Public | BindingFlags.Static);

        if (registerMethod == null)
        {
            return false;
        }

        var payload = new JObject
        {
            ["Id"] = ScriptId,
            ["Name"] = ScriptName,
            ["Script"] = LoadScript(),
            ["Src"] = string.Empty,
            ["Module"] = false,
            ["Enabled"] = true,
            ["RequiresAuthentication"] = true,
            ["PluginId"] = Plugin.Instance?.Id.ToString() ?? "7a28699c-e118-4666-84a0-e106de3866fa",
            ["PluginName"] = Plugin.Instance?.Name ?? "Smart Shuffle Playlist",
            ["PluginVersion"] = Plugin.Instance?.Version.ToString() ?? "1.0.0.0"
        };

        var result = registerMethod.Invoke(null, new object[] { payload });
        return result is true;
    }

    private static string LoadScript()
    {
        var assembly = typeof(Plugin).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .First(name => name.EndsWith("nativeProgress.js", StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
