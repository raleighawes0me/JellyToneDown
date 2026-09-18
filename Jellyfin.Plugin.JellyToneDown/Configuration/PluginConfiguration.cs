using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyToneDown.Configuration;

/// <summary>
/// How a percentage on the slider maps to an actual amplitude multiplier.
/// </summary>
public enum VolumeCurve
{
    /// <summary>
    /// Matches the curve the Jellyfin web client uses for its own volume slider
    /// (amplitude = percent ^ 3), so a given percentage sounds about the same here
    /// as it does on the normal player.
    /// </summary>
    Cubic = 0,

    /// <summary>
    /// Straight amplitude scaling (amplitude = percent). Much gentler at the top of the
    /// range; 50% here is considerably louder than 50% on the cubic curve.
    /// </summary>
    Linear = 1
}

/// <summary>
/// How the Jellyfin web client should have its theme volume reduced.
/// </summary>
public enum WebClientHandling
{
    /// <summary>
    /// Treat the web client like every other client and serve it gain-adjusted audio
    /// from the server. Robust, and identical in behaviour to phones and TVs.
    /// </summary>
    ServerGain = 0,

    /// <summary>
    /// Let the injected browser script scale the volume in the page instead, and serve
    /// the web client untouched audio. Lossless and instant, but depends on the web
    /// client's internals and needs script injection to be working.
    /// </summary>
    ClientScript = 1
}

/// <summary>
/// How the plugin's browser script gets into the Jellyfin web client.
/// </summary>
public enum ScriptInjectionMethod
{
    /// <summary>
    /// Add the script tag to index.html as the server sends it, from middleware. Needs no
    /// write access to the web client directory, and a jellyfin-web upgrade cannot undo it.
    /// </summary>
    Response = 0,

    /// <summary>
    /// Edit index.html on disk when the server starts. Only possible where the web client
    /// directory is writable by the Jellyfin process, which on a package install it usually
    /// is not, and has to be re-applied after every web client upgrade.
    /// </summary>
    Disk = 1
}

/// <summary>
/// A single user's personal theme volume.
/// </summary>
public class UserVolumeOverride
{
    /// <summary>
    /// Gets or sets the user's ID, as a string (the XML serializer handles strings more
    /// predictably than <see cref="Guid"/> across config upgrades).
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets this user's theme volume, 0-100.
    /// </summary>
    public int VolumePercent { get; set; } = 100;
}

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the server-wide default theme volume, 0-100. Applies to any user who
    /// has not set their own.
    /// </summary>
    public int DefaultVolumePercent { get; set; } = 40;

    /// <summary>
    /// Gets or sets a value indicating whether theme songs (theme.mp3 and friends) are adjusted.
    /// </summary>
    public bool ApplyToThemeSongs { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the audio track of theme videos is adjusted.
    /// </summary>
    public bool ApplyToThemeVideos { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether users may set their own level.
    /// </summary>
    public bool AllowUserOverrides { get; set; } = true;

    /// <summary>
    /// Gets or sets the percent-to-amplitude curve.
    /// </summary>
    public VolumeCurve Curve { get; set; } = VolumeCurve.Cubic;

    /// <summary>
    /// Gets or sets how the web client is handled.
    /// </summary>
    public WebClientHandling WebClientHandling { get; set; } = WebClientHandling.ServerGain;

    /// <summary>
    /// Gets or sets a value indicating whether the plugin should patch the web client's
    /// index.html to load its browser script. Needed for the in-app slider.
    /// </summary>
    public bool InjectClientScript { get; set; } = true;

    /// <summary>
    /// Gets or sets how that script gets in. Defaults to rewriting the response, which works
    /// regardless of file permissions; the on-disk patch is kept only for installs that would
    /// rather not have middleware touching index.html.
    /// </summary>
    public ScriptInjectionMethod ScriptInjectionMethod { get; set; } = ScriptInjectionMethod.Response;

    /// <summary>
    /// Gets or sets the per-user levels.
    /// </summary>
    public UserVolumeOverride[] UserOverrides { get; set; } = Array.Empty<UserVolumeOverride>();

    /// <summary>
    /// Gets or sets the cache ceiling in megabytes. Oldest entries are pruned past this.
    /// </summary>
    public int MaxCacheSizeMb { get; set; } = 1024;

    /// <summary>
    /// Gets or sets client names that should be left completely alone, as reported in the
    /// auth header (for example "Jellyfin Android"). Case-insensitive.
    /// </summary>
    public string[] ExcludedClients { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets a value indicating whether to log every intercepted theme request.
    /// Useful when working out why a particular client is not being adjusted.
    /// </summary>
    public bool VerboseLogging { get; set; }
}
