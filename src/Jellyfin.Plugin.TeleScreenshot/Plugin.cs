using System.Reflection;
using System.Runtime.Loader;
using Jellyfin.Plugin.TeleScreenshot.Configuration;
using Jellyfin.Plugin.TeleScreenshot.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.TeleScreenshot;

/// <summary>
/// Main plugin entry point. Registers the screenshot button UI transformation (via the
/// File Transformation plugin) and exposes the config page.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasPluginConfiguration, IHasWebPages
{
    private readonly ILogger<Plugin> _logger;

    /// <summary>
    /// Stable, never-changing plugin id. Matches <c>build.yaml</c>/<c>manifest.json</c>.
    /// </summary>
    public override Guid Id => Guid.Parse("13ec7a52-d9a7-46c3-be6c-c58149187a6c");

    public override string Name => "TeleScreenshot";

    /// <summary>
    /// Singleton accessor for the live plugin instance (used by the API controller to reach
    /// the current configuration without going through DI indirection).
    /// </summary>
    public static Plugin Instance { get; private set; } = null!;

    /// <summary>
    /// True when the File Transformation plugin was found and the index.html transformer was
    /// registered. Surfaced so the controller can report a helpful error if a screenshot is
    /// requested while UI injection is unavailable (the button itself wouldn't be visible then,
    /// but a direct API call could still arrive).
    /// </summary>
    public bool UiInjectionEnabled { get; private set; }

    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        IServiceProvider serviceProvider,
        ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _logger = logger;

        try
        {
            RegisterIndexTransformation();
            UiInjectionEnabled = true;
        }
        catch (Exception ex)
        {
            // The File Transformation plugin is an optional dependency. If it is missing the
            // config page and API still work, but the player button will not appear.
            _logger.LogWarning(ex,
                "Could not register the index.html transformation with the File Transformation " +
                "plugin. The screenshot button will not be injected into the player UI. " +
                "Install IAmParadox27/jellyfin-plugin-file-transformation and restart Jellyfin.");
            UiInjectionEnabled = false;
        }
    }

    /// <summary>
    /// Locate the File Transformation plugin (loaded in a separate AssemblyLoadContext) and
    /// call <c>PluginInterface.RegisterTransformation(JObject)</c> via reflection, registering
    /// <see cref="Transformers.IndexTransformer.Transform"/> as the index.html transformer.
    /// </summary>
    private void RegisterIndexTransformation()
    {
        const string fileTransformationAssemblyName = "Jellyfin.Plugin.FileTransformation";
        const string pluginInterfaceFullTypeName = "Jellyfin.Plugin.FileTransformation.PluginInterface";

        Assembly? ftAssembly = AssemblyLoadContext.All
            .SelectMany(static alc => alc.Assemblies)
            .FirstOrDefault(a =>
            {
                var an = a.GetName();
                return string.Equals(an.Name, fileTransformationAssemblyName, StringComparison.OrdinalIgnoreCase);
            });

        if (ftAssembly == null)
        {
            _logger.LogWarning(
                "File Transformation plugin assembly '{Name}' not found. UI injection disabled.",
                fileTransformationAssemblyName);
            return;
        }

        Type? pluginInterfaceType = ftAssembly.GetType(pluginInterfaceFullTypeName);
        MethodInfo? registerMethod = pluginInterfaceType?.GetMethod(
            "RegisterTransformation",
            BindingFlags.Public | BindingFlags.Static);

        if (pluginInterfaceType == null || registerMethod == null)
        {
            _logger.LogWarning(
                "File Transformation plugin found, but the expected PluginInterface.RegisterTransformation " +
                "entry point is missing (plugin version mismatch?). UI injection disabled.");
            return;
        }

        // Payload keys must match TransformationRegistrationPayload property names (case-insensitive).
        // Use empty TransformationEndpoint so the callback path is taken. Id is fixed so that
        // re-registration on plugin reload updates the same entry instead of stacking duplicates.
        var payload = new JObject
        {
            ["Id"] = TransformationIds.IndexHtml,
            ["FileNamePattern"] = "index.html",
            ["TransformationEndpoint"] = string.Empty,
            ["TransformationPipe"] = null,
            ["CallbackAssembly"] = typeof(Transformers.IndexTransformer).Assembly.FullName,
            ["CallbackClass"] = typeof(Transformers.IndexTransformer).FullName,
            ["CallbackMethod"] = nameof(Transformers.IndexTransformer.Transform),
        };

        registerMethod.Invoke(null, new object?[] { payload });
        _logger.LogInformation(
            "Registered index.html transformation with the File Transformation plugin. " +
            "The screenshot button will be injected into the web player UI.");
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        string prefix = GetType().Namespace!;
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{prefix}.Configuration.configPage.html"
            },
            new PluginPageInfo
            {
                Name = "telescreenshot.js",
                // FrontendModule prevents the page from being listed as a navigable config page;
                // this resource is served as a JS asset only.
                EmbeddedResourcePath = $"{prefix}.Web.telescreenshot.js"
            },
            new PluginPageInfo
            {
                Name = "telescreenshot.css",
                EmbeddedResourcePath = $"{prefix}.Web.telescreenshot.css"
            }
        };
    }
}

/// <summary>
/// Stable guids for transformations registered with the File Transformation plugin.
/// </summary>
internal static class TransformationIds
{
    /// <summary>Id of the registered index.html transformation (never changes across versions).</summary>
    public static readonly Guid IndexHtml = Guid.Parse("ceebc9e4-2200-4c6b-8102-a27e27db22ce");
}
