using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.JellyToneDown.Services;

/// <summary>
/// Describes a container JellyToneDown knows how to re-encode into, and how to do it.
/// </summary>
public sealed class ContainerProfile
{
    /// <summary>
    /// Never encode below this, however low the source claims to be. Guards against
    /// nonsense bitrate metadata.
    /// </summary>
    private const int MinBitrateKbps = 32;

    private static readonly string[] _noArgs = [];

    private static readonly Dictionary<string, ContainerProfile> _audio =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["mp3"] = new ContainerProfile("mp3", "mp3", "audio/mpeg", ["-c:a", "libmp3lame"], ["-q:a", "2"], 192),
            ["m4a"] = new ContainerProfile("m4a", "m4a", "audio/mp4", ["-c:a", "aac"], ["-b:a", "192k"], 192),
            ["m4b"] = new ContainerProfile("m4b", "m4b", "audio/mp4", ["-c:a", "aac"], ["-b:a", "192k"], 192),
            ["aac"] = new ContainerProfile("adts", "aac", "audio/aac", ["-c:a", "aac"], ["-b:a", "192k"], 192),
            ["ogg"] = new ContainerProfile("ogg", "ogg", "audio/ogg", ["-c:a", "libvorbis"], ["-q:a", "6"], 192),
            ["oga"] = new ContainerProfile("ogg", "oga", "audio/ogg", ["-c:a", "libvorbis"], ["-q:a", "6"], 192),
            ["opus"] = new ContainerProfile("opus", "opus", "audio/ogg", ["-c:a", "libopus"], ["-b:a", "128k"], 128),
            ["flac"] = new ContainerProfile("flac", "flac", "audio/flac", ["-c:a", "flac"], _noArgs, null),
            ["wav"] = new ContainerProfile("wav", "wav", "audio/wav", ["-c:a", "pcm_s16le"], _noArgs, null),
        };

    private static readonly Dictionary<string, ContainerProfile> _video =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["mp4"] = new ContainerProfile("mp4", "mp4", "video/mp4", ["-c:a", "aac"], ["-b:a", "192k"], 192, ["-c:v", "copy", "-movflags", "+faststart"]),
            ["m4v"] = new ContainerProfile("mp4", "m4v", "video/mp4", ["-c:a", "aac"], ["-b:a", "192k"], 192, ["-c:v", "copy", "-movflags", "+faststart"]),
            ["mkv"] = new ContainerProfile("matroska", "mkv", "video/x-matroska", ["-c:a", "aac"], ["-b:a", "192k"], 192, ["-c:v", "copy"]),
            ["webm"] = new ContainerProfile("webm", "webm", "video/webm", ["-c:a", "libopus"], ["-b:a", "128k"], 128, ["-c:v", "copy"]),
        };

    private readonly string[] _codecArgs;
    private readonly string[] _unknownBitrateArgs;
    private readonly int? _ceilingKbps;
    private readonly string[] _extraArgs;

    private ContainerProfile(
        string ffmpegFormat,
        string extension,
        string mimeType,
        string[] codecArgs,
        string[] unknownBitrateArgs,
        int? ceilingKbps,
        string[]? extraArgs = null)
    {
        FfmpegFormat = ffmpegFormat;
        Extension = extension;
        MimeType = mimeType;
        _codecArgs = codecArgs;
        _unknownBitrateArgs = unknownBitrateArgs;
        _ceilingKbps = ceilingKbps;
        _extraArgs = extraArgs ?? _noArgs;
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
    /// Builds the codec arguments for this container.
    /// </summary>
    /// <remarks>
    /// The output bitrate is the lower of the source's own bitrate and a per-codec
    /// ceiling, so a 128 kbps theme stays 128 kbps instead of being re-encoded larger
    /// than it started. Lossless containers ignore the bitrate entirely, and when the
    /// source bitrate is unknown we fall back to a fixed quality setting.
    /// </remarks>
    /// <param name="sourceBitrateBps">The source audio bitrate in bits per second, if known.</param>
    /// <returns>The ffmpeg arguments.</returns>
    public IEnumerable<string> BuildEncoderArgs(int? sourceBitrateBps)
    {
        foreach (var arg in _codecArgs)
        {
            yield return arg;
        }

        if (_ceilingKbps is not null)
        {
            if (TryPickBitrate(sourceBitrateBps, _ceilingKbps.Value, out var kbps))
            {
                yield return "-b:a";
                yield return string.Create(CultureInfo.InvariantCulture, $"{kbps}k");
            }
            else
            {
                foreach (var arg in _unknownBitrateArgs)
                {
                    yield return arg;
                }
            }
        }

        foreach (var arg in _extraArgs)
        {
            yield return arg;
        }
    }

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

    private static bool TryPickBitrate(int? sourceBitrateBps, int ceilingKbps, out int kbps)
    {
        kbps = 0;

        if (sourceBitrateBps is not > 0)
        {
            return false;
        }

        var sourceKbps = sourceBitrateBps.Value / 1000;
        if (sourceKbps <= 0)
        {
            return false;
        }

        kbps = Math.Clamp(sourceKbps, MinBitrateKbps, ceilingKbps);
        return true;
    }
}
