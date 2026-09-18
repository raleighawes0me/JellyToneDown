using System;
using Jellyfin.Plugin.JellyToneDown.Configuration;

namespace Jellyfin.Plugin.JellyToneDown.Services;

/// <summary>
/// Works out which theme volume applies to a given user, and converts it to an
/// amplitude multiplier for ffmpeg's volume filter.
/// </summary>
public static class VolumeResolver
{
    /// <summary>
    /// Gets the effective theme volume percentage for a user.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="userId">The user, or <see cref="Guid.Empty"/> if unknown.</param>
    /// <returns>A percentage between 0 and 100.</returns>
    public static int GetPercentForUser(PluginConfiguration config, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.AllowUserOverrides && !userId.Equals(Guid.Empty))
        {
            foreach (var entry in config.UserOverrides)
            {
                if (Guid.TryParse(entry.UserId, out var parsed) && parsed.Equals(userId))
                {
                    return Math.Clamp(entry.VolumePercent, 0, 100);
                }
            }
        }

        return Math.Clamp(config.DefaultVolumePercent, 0, 100);
    }

    /// <summary>
    /// Converts a percentage to an amplitude multiplier.
    /// </summary>
    /// <param name="percent">The percentage, 0-100.</param>
    /// <param name="curve">The curve to apply.</param>
    /// <returns>An amplitude multiplier between 0 and 1.</returns>
    public static double ToAmplitude(int percent, VolumeCurve curve)
    {
        var normalized = Math.Clamp(percent, 0, 100) / 100d;
        return curve == VolumeCurve.Cubic
            ? Math.Pow(normalized, 3)
            : normalized;
    }

    /// <summary>
    /// Sets a user's personal level, adding or replacing their entry.
    /// </summary>
    /// <param name="config">The plugin configuration to mutate.</param>
    /// <param name="userId">The user.</param>
    /// <param name="percent">The level, 0-100.</param>
    public static void SetUserPercent(PluginConfiguration config, Guid userId, int percent)
    {
        ArgumentNullException.ThrowIfNull(config);

        var clamped = Math.Clamp(percent, 0, 100);
        var id = userId.ToString("N", System.Globalization.CultureInfo.InvariantCulture);

        var existing = config.UserOverrides;
        for (var i = 0; i < existing.Length; i++)
        {
            if (Guid.TryParse(existing[i].UserId, out var parsed) && parsed.Equals(userId))
            {
                existing[i].VolumePercent = clamped;
                return;
            }
        }

        var updated = new UserVolumeOverride[existing.Length + 1];
        Array.Copy(existing, updated, existing.Length);
        updated[existing.Length] = new UserVolumeOverride { UserId = id, VolumePercent = clamped };
        config.UserOverrides = updated;
    }

    /// <summary>
    /// Removes a user's personal level so they fall back to the server default.
    /// </summary>
    /// <param name="config">The plugin configuration to mutate.</param>
    /// <param name="userId">The user.</param>
    public static void ClearUserPercent(PluginConfiguration config, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(config);

        var kept = new System.Collections.Generic.List<UserVolumeOverride>(config.UserOverrides.Length);
        foreach (var entry in config.UserOverrides)
        {
            if (Guid.TryParse(entry.UserId, out var parsed) && parsed.Equals(userId))
            {
                continue;
            }

            kept.Add(entry);
        }

        config.UserOverrides = kept.ToArray();
    }
}
