using System;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyToneDown.Services;

/// <summary>
/// Reads the bitrate of an item's audio track.
/// </summary>
/// <remarks>
/// Jellyfin already probed every library item during the scan, so the bitrate can be read
/// straight out of the media streams instead of spawning ffprobe on the request path.
/// </remarks>
public static class MediaBitrate
{
    /// <summary>
    /// Gets the bitrate of an item's first audio stream, in bits per second.
    /// </summary>
    /// <param name="mediaSourceManager">The media source manager.</param>
    /// <param name="itemId">The item.</param>
    /// <returns>The bitrate, or null if it is not known.</returns>
    public static int? GetSourceAudioBitrate(IMediaSourceManager mediaSourceManager, Guid itemId)
    {
        ArgumentNullException.ThrowIfNull(mediaSourceManager);

        try
        {
            foreach (var stream in mediaSourceManager.GetMediaStreams(itemId))
            {
                if (stream.Type == MediaStreamType.Audio && stream.BitRate is > 0)
                {
                    return stream.BitRate;
                }
            }
        }
        catch (Exception)
        {
            // An unscanned or unprobed item simply has no bitrate to report. The caller
            // falls back to a fixed quality setting.
        }

        return null;
    }
}
