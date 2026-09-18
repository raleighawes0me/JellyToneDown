using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyToneDown.Configuration;
using Jellyfin.Plugin.JellyToneDown.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyToneDown.Middleware;

/// <summary>
/// Intercepts delivery of theme songs and theme videos and serves a gain-adjusted copy
/// in their place.
/// </summary>
/// <remarks>
/// This runs ahead of Jellyfin's own pipeline, which is what makes it work for clients
/// that are not the web client: Swiftfin, Findroid, the Android TV app, Kodi and Infuse all
/// fetch theme media over these same routes, so quieter audio arrives already quiet and no
/// client-side support is needed. Anything the plugin cannot confidently handle is passed
/// straight through to Jellyfin untouched.
/// </remarks>
public sealed class ThemeGainMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILibraryManager _libraryManager;
    private readonly IAuthorizationContext _authorizationContext;
    private readonly GainCacheService _cache;
    private readonly ILogger<ThemeGainMiddleware> _logger;

    private long _requestsSincePrune;

    /// <summary>
    /// Initializes a new instance of the <see cref="ThemeGainMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="authorizationContext">The authorization context.</param>
    /// <param name="cache">The gain cache.</param>
    /// <param name="logger">The logger.</param>
    public ThemeGainMiddleware(
        RequestDelegate next,
        ILibraryManager libraryManager,
        IAuthorizationContext authorizationContext,
        GainCacheService cache,
        ILogger<ThemeGainMiddleware> logger)
    {
        _next = next;
        _libraryManager = libraryManager;
        _authorizationContext = authorizationContext;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Handles a request.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <returns>A task.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string? adjustedPath = null;
        string? mimeType = null;

        try
        {
            var result = await TryResolveAdjustedFileAsync(context).ConfigureAwait(false);
            adjustedPath = result.Path;
            mimeType = result.MimeType;
        }
        catch (OperationCanceledException)
        {
            // Client went away mid-encode. Nothing to serve, nothing to log.
            return;
        }
        catch (Exception ex)
        {
            // Never let this plugin take down media delivery. Fall through to Jellyfin.
            _logger.LogError(ex, "JellyToneDown: error while handling a theme request, passing it through untouched");
        }

        if (adjustedPath is null || mimeType is null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        await ServeFileAsync(context, adjustedPath, mimeType).ConfigureAwait(false);
    }

    private async Task<(string? Path, string? MimeType)> TryResolveAdjustedFileAsync(HttpContext context)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return (null, null);
        }

        if (!ThemeStreamRequest.TryParse(context.Request, out var parsed) || parsed is null)
        {
            return (null, null);
        }

        var item = _libraryManager.GetItemById(parsed.ItemId);
        if (item is null || item.ExtraType is null)
        {
            return (null, null);
        }

        var config = plugin.Configuration;

        var isThemeSong = item.ExtraType == ExtraType.ThemeSong;
        var isThemeVideo = item.ExtraType == ExtraType.ThemeVideo;

        if (isThemeSong && !config.ApplyToThemeSongs)
        {
            return (null, null);
        }

        if (isThemeVideo && !config.ApplyToThemeVideos)
        {
            return (null, null);
        }

        if (!isThemeSong && !isThemeVideo)
        {
            return (null, null);
        }

        var auth = await _authorizationContext.GetAuthorizationInfo(context).ConfigureAwait(false);

        if (IsExcludedClient(config, auth.Client))
        {
            LogVerbose(config, "client {Client} is excluded", auth.Client);
            return (null, null);
        }

        // When the browser script is doing the scaling, the web client must get untouched
        // audio or the reduction would be applied twice.
        if (config.WebClientHandling == WebClientHandling.ClientScript && IsWebClient(auth.Client))
        {
            LogVerbose(config, "leaving the web client to its own script");
            return (null, null);
        }

        var percent = VolumeResolver.GetPercentForUser(config, auth.UserId);
        if (percent >= 100)
        {
            return (null, null);
        }

        var amplitude = VolumeResolver.ToAmplitude(percent, config.Curve);

        var sourceContainer = item.Container;
        if (string.IsNullOrWhiteSpace(sourceContainer))
        {
            sourceContainer = Path.GetExtension(item.Path)?.TrimStart('.');
        }

        var profile = ContainerProfile.Choose(sourceContainer, parsed.GetClientContainers(context.Request), parsed.IsVideo);
        if (profile is null)
        {
            LogVerbose(
                config,
                "no container in common for {Item} (source {Source}), passing through",
                item.Name,
                sourceContainer);
            return (null, null);
        }

        var path = await _cache
            .GetOrCreateAsync(item, profile, amplitude, parsed.IsVideo, context.RequestAborted)
            .ConfigureAwait(false);

        if (path is null)
        {
            return (null, null);
        }

        LogVerbose(
            config,
            "serving {Item} to {Client} at {Percent}% ({Container})",
            item.Name,
            auth.Client,
            percent,
            profile.Extension);

        SchedulePruneIfDue(config);

        return (path, profile.MimeType);
    }

    private static bool IsWebClient(string? client)
    {
        return client is not null
            && client.Contains("web", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExcludedClient(PluginConfiguration config, string? client)
    {
        if (string.IsNullOrEmpty(client))
        {
            return false;
        }

        foreach (var excluded in config.ExcludedClients)
        {
            if (!string.IsNullOrWhiteSpace(excluded)
                && client.Contains(excluded.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void LogVerbose(PluginConfiguration config, string message, params object?[] args)
    {
        if (config.VerboseLogging)
        {
            _logger.LogInformation("JellyToneDown: " + message, args);
        }
    }

    private void SchedulePruneIfDue(PluginConfiguration config)
    {
        // Cheap amortised housekeeping: check the cache size every 200 served requests
        // rather than on every one.
        if (Interlocked.Increment(ref _requestsSincePrune) % 200 != 0)
        {
            return;
        }

        var maxBytes = (long)Math.Max(config.MaxCacheSizeMb, 0) * 1024L * 1024L;
        if (maxBytes <= 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                _cache.Prune(maxBytes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "JellyToneDown: cache prune failed");
            }
        });
    }

    private static async Task ServeFileAsync(HttpContext context, string path, string mimeType)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var length = info.Length;
        var response = context.Response;

        response.Headers.AcceptRanges = "bytes";
        response.ContentType = mimeType;
        response.Headers.CacheControl = "no-cache";

        long start = 0;
        var end = length - 1;
        var isPartial = false;

        var rangeHeader = context.Request.Headers.Range.ToString();
        if (!string.IsNullOrEmpty(rangeHeader)
            && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            // Only the first range of a possibly multi-range request is honoured, which is
            // all any Jellyfin client actually sends.
            var spec = rangeHeader[6..].Split(',')[0].Trim();
            var dash = spec.IndexOf('-');

            if (dash < 0)
            {
                isPartial = false;
            }
            else
            {
                var fromText = spec[..dash];
                var toText = spec[(dash + 1)..];

                if (fromText.Length == 0)
                {
                    // Suffix form: bytes=-500 means the last 500 bytes.
                    if (long.TryParse(toText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var suffix) && suffix > 0)
                    {
                        start = Math.Max(0, length - suffix);
                        isPartial = true;
                    }
                }
                else if (long.TryParse(fromText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var from))
                {
                    start = from;
                    if (toText.Length > 0
                        && long.TryParse(toText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var to))
                    {
                        end = Math.Min(to, length - 1);
                    }

                    isPartial = true;
                }
            }
        }

        if (isPartial && (start >= length || start > end || start < 0))
        {
            response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            response.Headers.ContentRange = string.Create(CultureInfo.InvariantCulture, $"bytes */{length}");
            return;
        }

        var count = end - start + 1;

        if (isPartial)
        {
            response.StatusCode = StatusCodes.Status206PartialContent;
            response.Headers.ContentRange = string.Create(CultureInfo.InvariantCulture, $"bytes {start}-{end}/{length}");
        }
        else
        {
            response.StatusCode = StatusCodes.Status200OK;
        }

        response.ContentLength = count;

        if (HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }

        await response.SendFileAsync(path, start, count, context.RequestAborted).ConfigureAwait(false);
    }
}
