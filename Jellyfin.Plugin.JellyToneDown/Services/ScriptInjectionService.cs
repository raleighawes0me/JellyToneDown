using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyToneDown.Services;

/// <summary>
/// The outcome of the last attempt to patch the web client.
/// </summary>
public enum InjectionStatus
{
    /// <summary>Injection is switched off in the plugin configuration.</summary>
    Disabled = 0,

    /// <summary>The script tag is present in index.html.</summary>
    Injected = 1,

    /// <summary>index.html could not be found.</summary>
    WebClientNotFound = 2,

    /// <summary>index.html exists but is not writable by the Jellyfin process.</summary>
    NotWritable = 3,

    /// <summary>Something else went wrong; see the server log.</summary>
    Failed = 4
}

/// <summary>
/// Adds the plugin's browser script to the Jellyfin web client by patching index.html,
/// and takes it back out again when the feature is switched off.
/// </summary>
/// <remarks>
/// Jellyfin has no supported hook for adding a script to the web client, so like every
/// other plugin that needs one, this edits index.html in place. The edit is re-applied on
/// every server start because upgrading Jellyfin or the web client replaces that file.
/// <para>
/// On a package install the web client is usually owned by root while the server runs as
/// the jellyfin user, in which case the patch cannot be written. That is not fatal:
/// the plugin's server-side gain covers the web client too, and only the in-browser
/// slider is lost.
/// </para>
/// </remarks>
public sealed class ScriptInjectionService : IHostedService
{
    private const string MarkerStart = "<!-- JellyToneDown:start -->";
    private const string MarkerEnd = "<!-- JellyToneDown:end -->";

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
    /// Gets the result of the most recent patch attempt, for display on the config page.
    /// </summary>
    public static InjectionStatus LastStatus { get; private set; } = InjectionStatus.Disabled;

    /// <summary>
    /// Gets a human-readable note about the most recent patch attempt.
    /// </summary>
    public static string LastMessage { get; private set; } = "Not run yet.";

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
    /// Applies or removes the patch according to the current configuration.
    /// </summary>
    public void Apply()
    {
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

            var wanted = Plugin.Config.InjectClientScript;
            string updated;

            if (wanted)
            {
                var version = Plugin.Instance?.Version?.ToString() ?? "0";
                var block = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{MarkerStart}<script src=\"../JellyToneDown/ClientScript?v={version}\" defer></script>{MarkerEnd}");

                var closingBody = stripped.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (closingBody < 0)
                {
                    LastStatus = InjectionStatus.Failed;
                    LastMessage = "index.html has no </body> tag to insert the script before.";
                    _logger.LogWarning("JellyToneDown: {Message}", LastMessage);
                    return;
                }

                updated = stripped[..closingBody] + block + stripped[closingBody..];
            }
            else
            {
                updated = stripped;
            }

            if (string.Equals(updated, original, StringComparison.Ordinal))
            {
                LastStatus = wanted ? InjectionStatus.Injected : InjectionStatus.Disabled;
                LastMessage = wanted
                    ? "The script tag is already in place."
                    : "Script injection is switched off.";
                return;
            }

            File.WriteAllText(indexPath, updated);

            LastStatus = wanted ? InjectionStatus.Injected : InjectionStatus.Disabled;
            LastMessage = wanted
                ? "Added the script tag to index.html. Reload the web client once to pick it up."
                : "Removed the script tag from index.html.";

            _logger.LogInformation("JellyToneDown: {Message}", LastMessage);
        }
        catch (UnauthorizedAccessException ex)
        {
            LastStatus = InjectionStatus.NotWritable;
            LastMessage = "index.html is not writable by the Jellyfin process, so the in-browser "
                + "slider is unavailable. Server-side volume adjustment still works everywhere, "
                + "including in the web client.";
            _logger.LogWarning(ex, "JellyToneDown: {Message}", LastMessage);
        }
        catch (IOException ex)
        {
            LastStatus = InjectionStatus.NotWritable;
            LastMessage = "Could not write to index.html: " + ex.Message;
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
        // Remove any block this plugin previously wrote, including ones from older versions
        // with a different script URL.
        return Regex.Replace(
            html,
            Regex.Escape(MarkerStart) + ".*?" + Regex.Escape(MarkerEnd),
            string.Empty,
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5));
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e)
    {
        Apply();
    }
}
