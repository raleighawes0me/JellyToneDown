using System;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyToneDown.Configuration;
using Jellyfin.Plugin.JellyToneDown.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyToneDown.Api;

/// <summary>
/// A user's theme volume.
/// </summary>
public class ThemeVolumeDto
{
    /// <summary>
    /// Gets or sets the level that applies to this user, 0-100.
    /// </summary>
    public int VolumePercent { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this level is the user's own rather than
    /// the server default.
    /// </summary>
    public bool IsUserOverride { get; set; }

    /// <summary>
    /// Gets or sets the server-wide default, 0-100.
    /// </summary>
    public int ServerDefaultPercent { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether users are allowed to set their own level.
    /// </summary>
    public bool UserOverridesAllowed { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the browser script should scale volume
    /// itself rather than relying on server-side gain.
    /// </summary>
    public bool ClientSideScaling { get; set; }

    /// <summary>
    /// Gets or sets the curve name, "Cubic" or "Linear", so the browser can match it.
    /// </summary>
    public string Curve { get; set; } = nameof(VolumeCurve.Cubic);
}

/// <summary>
/// Admin-facing status.
/// </summary>
public class ThemeVolumeStatusDto
{
    /// <summary>
    /// Gets or sets the number of cached files.
    /// </summary>
    public int CachedFiles { get; set; }

    /// <summary>
    /// Gets or sets the total cache size in megabytes.
    /// </summary>
    public double CacheSizeMb { get; set; }

    /// <summary>
    /// Gets or sets the cache directory.
    /// </summary>
    public string CachePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the web client injection status.
    /// </summary>
    public string InjectionStatus { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a human-readable note about the injection status.
    /// </summary>
    public string InjectionMessage { get; set; } = string.Empty;
}

/// <summary>
/// The value to set.
/// </summary>
public class SetThemeVolumeRequest
{
    /// <summary>
    /// Gets or sets the requested level, 0-100.
    /// </summary>
    public int VolumePercent { get; set; }
}

/// <summary>
/// JellyToneDown's API.
/// </summary>
[ApiController]
[Route("JellyToneDown")]
public class JellyToneDownController : ControllerBase
{
    private static readonly object _saveLock = new();

    private readonly IAuthorizationContext _authorizationContext;
    private readonly GainCacheService _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyToneDownController"/> class.
    /// </summary>
    /// <param name="authorizationContext">The authorization context.</param>
    /// <param name="cache">The gain cache.</param>
    public JellyToneDownController(IAuthorizationContext authorizationContext, GainCacheService cache)
    {
        _authorizationContext = authorizationContext;
        _cache = cache;
    }

    /// <summary>
    /// Serves the browser script that index.html loads.
    /// </summary>
    /// <returns>The script.</returns>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetClientScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = typeof(Plugin).Namespace + ".Web.jellytonedown.js";

        var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=3600";
        return File(stream, "application/javascript; charset=utf-8");
    }

    /// <summary>
    /// Gets the theme volume that applies to the calling user.
    /// </summary>
    /// <returns>The user's settings.</returns>
    [HttpGet("Volume")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<ThemeVolumeDto>> GetVolume()
    {
        var config = Plugin.Config;
        var userId = await GetUserIdAsync().ConfigureAwait(false);

        return BuildDto(config, userId);
    }

    /// <summary>
    /// Sets the calling user's own theme volume.
    /// </summary>
    /// <param name="request">The new level.</param>
    /// <returns>The user's settings after the change.</returns>
    [HttpPost("Volume")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ThemeVolumeDto>> SetVolume([FromBody] SetThemeVolumeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        if (!plugin.Configuration.AllowUserOverrides)
        {
            return Forbid();
        }

        var userId = await GetUserIdAsync().ConfigureAwait(false);
        if (userId.Equals(Guid.Empty))
        {
            return Forbid();
        }

        lock (_saveLock)
        {
            VolumeResolver.SetUserPercent(plugin.Configuration, userId, request.VolumePercent);
            plugin.SaveConfiguration();
        }

        return BuildDto(plugin.Configuration, userId);
    }

    /// <summary>
    /// Clears the calling user's own level so the server default applies again.
    /// </summary>
    /// <returns>The user's settings after the change.</returns>
    [HttpDelete("Volume")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<ThemeVolumeDto>> ResetVolume()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        var userId = await GetUserIdAsync().ConfigureAwait(false);
        if (userId.Equals(Guid.Empty))
        {
            return Forbid();
        }

        lock (_saveLock)
        {
            VolumeResolver.ClearUserPercent(plugin.Configuration, userId);
            plugin.SaveConfiguration();
        }

        return BuildDto(plugin.Configuration, userId);
    }

    /// <summary>
    /// Gets cache and injection status, for the admin config page.
    /// </summary>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ThemeVolumeStatusDto> GetStatus()
    {
        var (bytes, files) = _cache.GetCacheStats();

        return new ThemeVolumeStatusDto
        {
            CachedFiles = files,
            CacheSizeMb = Math.Round(bytes / 1024d / 1024d, 1),
            CachePath = _cache.CacheRoot,
            InjectionStatus = ScriptInjectionService.LastStatus.ToString(),
            InjectionMessage = ScriptInjectionService.LastMessage
        };
    }

    /// <summary>
    /// Deletes every cached gain-adjusted file. Originals are untouched.
    /// </summary>
    /// <returns>The number of files removed.</returns>
    [HttpPost("ClearCache")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<int> ClearCache()
    {
        return _cache.Clear();
    }

    private static ThemeVolumeDto BuildDto(PluginConfiguration config, Guid userId)
    {
        var effective = VolumeResolver.GetPercentForUser(config, userId);

        var isOverride = false;
        if (config.AllowUserOverrides && !userId.Equals(Guid.Empty))
        {
            foreach (var entry in config.UserOverrides)
            {
                if (Guid.TryParse(entry.UserId, out var parsed) && parsed.Equals(userId))
                {
                    isOverride = true;
                    break;
                }
            }
        }

        return new ThemeVolumeDto
        {
            VolumePercent = effective,
            IsUserOverride = isOverride,
            ServerDefaultPercent = Math.Clamp(config.DefaultVolumePercent, 0, 100),
            UserOverridesAllowed = config.AllowUserOverrides,
            ClientSideScaling = config.WebClientHandling == WebClientHandling.ClientScript,
            Curve = config.Curve.ToString()
        };
    }

    private async Task<Guid> GetUserIdAsync()
    {
        var auth = await _authorizationContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        return auth.UserId;
    }
}
