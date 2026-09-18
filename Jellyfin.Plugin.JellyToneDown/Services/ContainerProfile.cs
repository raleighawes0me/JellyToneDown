using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyToneDown.Services;

/// <summary>
/// Describes a container JellyToneDown knows how to re-encode into, and how to do it.
/// </summary>
public sealed class ContainerProfile
{
    private static readonly Dictionary<string, ContainerProfile> _audio =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["mp3"] = new ContainerProfile("mp3", "mp3", "audio/mpeg", ["-c:a", "libmp3lame", "-q:a", "2"]),
            ["m4a"] = new ContainerProfile("m4a", "m4a", "audio/mp4", ["-c:a", "aac", "-b:a", "256k"]),
            ["m4b"] = new ContainerProfile("m4b", "m4b", "audio/mp4", ["-c:a", "aac", "-b:a", "256k"]),
            ["aac"] = new ContainerProfile("aac", "aac", "audio/aac", ["-c:a", "aac", "-b:a", "256k"]),
            ["ogg"] = new ContainerProfile("ogg", "ogg", "audio/ogg", ["-c:a", "libvorbis", "-q:a", "6"]),
            ["oga"] = new ContainerProfile("oga", "oga", "audio/ogg", ["-c:a", "libvorbis", "-q:a", "6"]),
            ["opus"] = new ContainerProfile("opus", "opus", "audio/ogg", ["-c:a", "libopus", "-b:a", "192k"]),
            ["flac"] = new ContainerProfile("flac", "flac", "audio/flac", ["-c:a", "flac"]),
            ["wav"] = new ContainerProfile("wav", "wav", "audio/wav", ["-c:a", "pcm_s16le"]),
        };

    private static readonly Dictionary<string, ContainerProfile> _video =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["mp4"] = new ContainerProfile("mp4", "mp4", "video/mp4", ["-c:v", "copy", "-c:a", "aac", "-b:a", "256k", "-movflags", "+faststart"]),
            ["m4v"] = new ContainerProfile("m4v", "m4v", "video/mp4", ["-c:v", "copy", "-c:a", "aac", "-b:a", "256k", "-movflags", "+faststart"]),
            ["mkv"] = new ContainerProfile("matroska", "mkv", "video/x-matroska", ["-c:v", "copy", "-c:a", "aac", "-b:a", "256k"]),
            ["webm"] = new ContainerProfile("webm", "webm", "video/webm", ["-c:v", "copy", "-c:a", "libopus", "-b:a", "192k"]),
        };

    private ContainerProfile(string ffmpegFormat, string extension, string mimeType, string[] encoderArgs)
    {
        FfmpegFormat = ffmpegFormat;
        Extension = extension;
        MimeType = mimeType;
        EncoderArgs = encoderArgs;
    }

    /// <summary>
    /// Gets the value for ffmpeg's -f flag.
    /// </summary>
    public string FfmpegFormat { get; }

    /// <summary>
    /// Gets the file extension used for the cached copy.
    /// </summary>
    public string Extension { get; }

    /// <summary>
    /// Gets the Content-Type to serve the cached copy with.
    /// </summary>
    public string MimeType { get; }

    /// <summary>
    /// Gets the codec arguments.
    /// </summary>
    public IReadOnlyList<string> EncoderArgs { get; }

    /// <summary>
    /// Looks up a container by name.
    /// </summary>
    /// <param name="container">The container name, for example "mp3".</param>
    /// <param name="isVideo">Whether to look in the video table.</param>
    /// <returns>The profile, or null if we cannot produce that container.</returns>
    public static ContainerProfile? Find(string? container, bool isVideo)
    {
        if (string.IsNullOrWhiteSpace(container))
        {
            return null;
        }

        var table = isVideo ? _video : _audio;
        return table.TryGetValue(container.Trim().TrimStart('.'), out var profile) ? profile : null;
    }

    /// <summary>
    /// Chooses the container to encode into.
    /// </summary>
    /// <remarks>
    /// Preference order is: the source's own container if the client can take it (so the
    /// common theme.mp3 case is a straight mp3 to mp3 re-encode), then the first container
    /// the client listed that we can actually produce. If neither works we return null and
    /// the caller hands the request back to Jellyfin untouched, so the theme still plays --
    /// just at its original volume.
    /// </remarks>
    /// <param name="sourceContainer">The container of the file on disk.</param>
    /// <param name="clientContainers">The containers the client said it supports.</param>
    /// <param name="isVideo">Whether this is a theme video.</param>
    /// <returns>The chosen profile, or null.</returns>
    public static ContainerProfile? Choose(string? sourceContainer, IReadOnlyList<string> clientContainers, bool isVideo)
    {
        ArgumentNullException.ThrowIfNull(clientContainers);

        var source = Find(sourceContainer, isVideo);

        if (clientContainers.Count == 0)
        {
            return source;
        }

        if (source is not null)
        {
            foreach (var candidate in clientContainers)
            {
                if (string.Equals(candidate.Trim().TrimStart('.'), source.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    return source;
                }
            }
        }

        foreach (var candidate in clientContainers)
        {
            var profile = Find(candidate, isVideo);
            if (profile is not null)
            {
                return profile;
            }
        }

        return null;
    }
}
