using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyToneDown.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.JellyToneDown;

/// <summary>
/// JellyToneDown: adjusts the volume of theme music and theme videos only.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// The plugin's unique identifier, as a string.
    /// </summary>
    public const string PluginGuid = "618453c4-1b13-4fe4-881d-19536be75add";

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        AppPaths = applicationPaths;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "JellyToneDown";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginGuid);

    /// <inheritdoc />
    public override string Description =>
        "Turns down theme music and theme videos, and nothing else.";

    /// <summary>
    /// Gets the application paths. <see cref="BasePlugin{T}.ApplicationPaths"/> is protected,
    /// so this exposes it to the plugin's own services.
    /// </summary>
    public IApplicationPaths AppPaths { get; }

    /// <summary>
    /// Gets the current configuration, falling back to defaults if the plugin is not loaded.
    /// </summary>
    public static PluginConfiguration Config => Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = "JellyToneDown",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            }
        ];
    }
}
