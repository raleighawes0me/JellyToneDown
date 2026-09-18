using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyToneDown.Configuration;
using Jellyfin.Plugin.JellyToneDown.Middleware;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyToneDown.Services;

/// <summary>
/// How the browser script is currently reaching the web client, if at all.
/// </summary>
public enum InjectionStatus
{
    /// <summary>The browser slider is switched off in the plugin configuration.</summary>
    Disabled = 0,

    /// <summary>The script tag has been written into index.html on disk.</summary>
    Injected = 1,

    /// <summary>index.html could not be found.</summary>
    WebClientNotFound = 2,

    /// <summary>index.html exists but is not writable by the Jellyfin process.</summary>
    NotWritable = 3,

    /// <summary>Something else went wrong; see the server log.</summary>
    Failed = 4,

    /// <summary>The script tag is being added to index.html as it is served.</summary>
    ServedInResponse = 5,

    /// <summary>
    /// Response rewriting is on, but nobody has loaded the web client since the server
    /// started, so it has had nothing to act on yet.
    /// </summary>
    ResponseNotSeenYet = 6
}

/// <summary>
/// Keeps the on-disk copy of index.html in the state the configuration asks for, and reports
/// how the browser script is reaching the web client.
/// </summary>
/// <remarks>
/// Since 1.1.0 the script normally goes in via <see cref="IndexHtmlInjectionMiddleware"/>,
/// which rewrites the response and never touches the file. This service then has only one job
/// in that mode: making sure no block written by an older version is left behind on disk.
/// Patching the file is still available as an explicit choice.
/// </remarks>
public sealed class ScriptInjectionService : IHostedService
{
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<ScriptInjectionService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScriptInjectionService"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public ScriptInjectionService(IApplicationPaths applicationPaths, ILogger<ScriptInjectionService> logger)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    /// <summary>
    /// Gets the result of the most recent attempt to patch index.html on disk.
    /// </summary>
    public static InjectionStatus LastStatus { get; private set; } = InjectionStatus.Disabled;

    /// <summary>
    /// Gets a human-readable note about the most recent attempt to patch index.html on disk.
    /// </summary>
    public static string LastMessage { get; private set; } = "Not run yet.";

    /// <summary>
    /// Describes how the browser script is reaching the web client right now, for the config
    /// page. In response mode this reflects what has actually happened to real page loads
    /// rather than merely what is configured.
    /// </summary>
    /// <returns>A status and a sentence explaining it.</returns>
    public static (InjectionStatus Status, string Message) Describe()
    {
        var config = Plugin.Config;

        if (!config.InjectClientScript)
        {
            return (
                InjectionStatus.Disabled,
                "The in-browser slider is switched off. Everyone still gets their level from "
                + "the server, in the web client as well as everywhere else.");
        }

        if (config.ScriptInjectionMethod == ScriptInjectionMethod.Disk)
        {
            return (LastStatus, LastMessage);
        }

        var count = IndexHtmlInjectionMiddleware.InjectedResponses;
        if (count == 0)
        {
            return (
                InjectionStatus.ResponseNotSeenYet,
                "Ready, but nobody has loaded the web client since the server started, so there "
                + "has been nothing to add the script to yet. Open the web client in another tab "
                + "and come back.");
        }

        return (
            InjectionStatus.ServedInResponse,
            "Added to "
            + count.ToString(CultureInfo.InvariantCulture)
            + " web client page load(s) since the server started. Nothing was written to the "
            + "web client directory, so a Jellyfin upgrade cannot undo it.");
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Apply();

        var plugin = Plugin.Instance;
        if (plugin is not null)
        {
            plugin.ConfigurationChanged += OnConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is not null)
        {
            plugin.ConfigurationChanged -= OnConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Brings index.html on disk into line with the configuration: patched when the on-disk
    /// method is selected, clean otherwise.
    /// </summary>
    public void Apply()
    {
        var config = Plugin.Config;
        var wantsDiskPatch = config.InjectClientScript
            && config.ScriptInjectionMethod == ScriptInjectionMethod.Disk;

        try
        {
            var indexPath = Path.Combine(_applicationPaths.WebPath, "index.html");

            if (!File.Exists(indexPath))
            {
                LastStatus = InjectionStatus.WebClientNotFound;
                LastMessage = "Could not find the web client's index.html at " + indexPath + ".";
                _logger.LogInformation("JellyToneDown: {Message}", LastMessage);
                return;
            }

            var original = File.ReadAllText(indexPath);
            var stripped = RemoveExistingBlock(original);
            string updated;

            if (wantsDiskPatch)
            {
                var closingBody = stripped.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (closingBody < 0)
                {
                    LastStatus = InjectionStatus.Failed;
                    LastMessage = "index.html has no </body> tag to insert the script before.";
                    _logger.LogWarning("JellyToneDown: {Message}", LastMessage);
                    return;
                }

                updated = stripped[..closingBody] + ClientScriptTag.Build() + stripped[closingBody..];
            }
            else
            {
                updated = stripped;
            }

            if (string.Equals(updated, original, StringComparison.Ordinal))
            {
                SetIdleStatus(wantsDiskPatch, alreadyInPlace: true);
                return;
            }

            File.WriteAllText(indexPath, updated);
            SetIdleStatus(wantsDiskPatch, alreadyInPlace: false);
            _logger.LogInformation("JellyToneDown: {Message}", LastMessage);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            if (!wantsDiskPatch)
            {
                // We were only tidying up after an older version. Being unable to write is
                // the normal case on a package install, and it does not matter: the middleware
                // recognises a block that is already in the page and will not add a second.
                LastStatus = InjectionStatus.Disabled;
                LastMessage = "index.html is not writable, which is fine - nothing needs to be "
                    + "written to it.";
                _logger.LogDebug(ex, "JellyToneDown: index.html is not writable during cleanup.");
                return;
            }

            LastStatus = InjectionStatus.NotWritable;
            LastMessage = "index.html is not writable by the Jellyfin process, so the on-disk "
                + "patch cannot be applied. Switch the method back to rewriting the response, "
                + "which needs no write access.";
            _logger.LogWarning(ex, "JellyToneDown: {Message}", LastMessage);
        }
        catch (Exception ex)
        {
            LastStatus = InjectionStatus.Failed;
            LastMessage = "Unexpected error while patching the web client: " + ex.Message;
            _logger.LogError(ex, "JellyToneDown: {Message}", LastMessage);
        }
    }

    private static string RemoveExistingBlock(string html)
    {
        // Removes any block this plugin has previously written, including ones from older
        // versions with a different script URL.
        return Regex.Replace(
            html,
            Regex.Escape(ClientScriptTag.StartMarker) + ".*?" + Regex.Escape(ClientScriptTag.EndMarker),
            string.Empty,
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5));
    }

    private static void SetIdleStatus(bool wantsDiskPatch, bool alreadyInPlace)
    {
        if (wantsDiskPatch)
        {
            LastStatus = InjectionStatus.Injected;
            LastMessage = alreadyInPlace
                ? "The script tag is already in index.html on disk."
                : "Added the script tag to index.html. Reload the web client once to pick it up.";
            return;
        }

        LastStatus = InjectionStatus.Disabled;
        LastMessage = alreadyInPlace
            ? "index.html on disk is unpatched, as intended."
            : "Removed an old script tag from index.html.";
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e)
    {
        Apply();
    }
}
