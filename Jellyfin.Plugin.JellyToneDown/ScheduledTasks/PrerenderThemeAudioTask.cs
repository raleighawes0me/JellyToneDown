using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyToneDown.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyToneDown.ScheduledTasks;

/// <summary>
/// Encodes every theme in the library ahead of time, at each volume level currently in use.
/// </summary>
/// <remarks>
/// Adjusted copies are produced on demand anyway, but the first play of a given theme then
/// waits a moment for ffmpeg. Running this once after changing the volume means every theme
/// starts instantly from then on. It is a manual task by default; nothing schedules it.
/// </remarks>
public class PrerenderThemeAudioTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly GainCacheService _cache;
    private readonly ILogger<PrerenderThemeAudioTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PrerenderThemeAudioTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="cache">The gain cache.</param>
    /// <param name="logger">The logger.</param>
    public PrerenderThemeAudioTask(
        ILibraryManager libraryManager,
        GainCacheService cache,
        ILogger<PrerenderThemeAudioTask> logger)
    {
        _libraryManager = libraryManager;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Pre-render theme audio";

    /// <inheritdoc />
    public string Key => "JellyToneDownPrerender";

    /// <inheritdoc />
    public string Description =>
        "Encodes every theme song and theme video at the volume levels currently configured, "
        + "so the first play of each one does not have to wait. Your theme files are not modified.";

    /// <inheritdoc />
    public string Category => "JellyToneDown";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        var config = plugin.Configuration;

        var extraTypes = new List<ExtraType>();
        if (config.ApplyToThemeSongs)
        {
            extraTypes.Add(ExtraType.ThemeSong);
        }

        if (config.ApplyToThemeVideos)
        {
            extraTypes.Add(ExtraType.ThemeVideo);
        }

        if (extraTypes.Count == 0)
        {
            progress.Report(100);
            return;
        }

        var levels = CollectLevels(config);
        if (levels.Count == 0)
        {
            _logger.LogInformation("JellyToneDown: every level is 100%, nothing to pre-render");
            progress.Report(100);
            return;
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            ExtraTypes = extraTypes.ToArray(),
            Recursive = true
        });

        _logger.LogInformation(
            "JellyToneDown: pre-rendering {Items} theme item(s) at {Levels} level(s)",
            items.Count,
            levels.Count);

        var totalUnits = Math.Max(1, items.Count * levels.Count);
        var done = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var isVideo = item.ExtraType == ExtraType.ThemeVideo;

            var sourceContainer = item.Container;
            if (string.IsNullOrWhiteSpace(sourceContainer))
            {
                sourceContainer = Path.GetExtension(item.Path)?.TrimStart('.');
            }

            var profile = ContainerProfile.Find(sourceContainer, isVideo);

            foreach (var percent in levels)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (profile is not null)
                {
                    var amplitude = VolumeResolver.ToAmplitude(percent, config.Curve);
                    try
                    {
                        await _cache
                            .GetOrCreateAsync(item, profile, amplitude, isVideo, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "JellyToneDown: could not pre-render {Item}", item.Name);
                    }
                }

                done++;
                progress.Report(done * 100d / totalUnits);
            }
        }

        var maxBytes = (long)Math.Max(config.MaxCacheSizeMb, 0) * 1024L * 1024L;
        if (maxBytes > 0)
        {
            _cache.Prune(maxBytes);
        }

        progress.Report(100);
    }

    private static List<int> CollectLevels(Configuration.PluginConfiguration config)
    {
        var levels = new HashSet<int>();

        var defaultPercent = Math.Clamp(config.DefaultVolumePercent, 0, 100);
        if (defaultPercent < 100)
        {
            levels.Add(defaultPercent);
        }

        if (config.AllowUserOverrides)
        {
            foreach (var entry in config.UserOverrides)
            {
                var percent = Math.Clamp(entry.VolumePercent, 0, 100);
                if (percent < 100)
                {
                    levels.Add(percent);
                }
            }
        }

        return [.. levels];
    }
}
