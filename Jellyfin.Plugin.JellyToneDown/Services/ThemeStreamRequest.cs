using System;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.JellyToneDown.Services;

/// <summary>
/// A parsed media-delivery request that JellyToneDown might care about.
/// </summary>
public sealed class ThemeStreamRequest
{
    private ThemeStreamRequest(Guid itemId, bool isVideo, string? requestedContainer)
    {
        ItemId = itemId;
        IsVideo = isVideo;
        RequestedContainer = requestedContainer;
    }

    /// <summary>
    /// Gets the item being requested.
    /// </summary>
    public Guid ItemId { get; }

    /// <summary>
    /// Gets a value indicating whether this came in on a video route.
    /// </summary>
    public bool IsVideo { get; }

    /// <summary>
    /// Gets the container the URL explicitly asked for ("mp3" from /stream.mp3), or null.
    /// </summary>
    public string? RequestedContainer { get; }

    /// <summary>
    /// Attempts to parse a request path into something we may want to intervene in.
    /// </summary>
    /// <remarks>
    /// The routes Jellyfin uses for direct delivery are /Audio/{id}/universal,
    /// /Audio/{id}/stream[.container] and /Videos/{id}/stream[.container]. HLS routes
    /// (main.m3u8 and the segment routes) are deliberately not matched: rewriting a
    /// segmented stream from middleware is not safe, and theme media is never delivered
    /// that way in practice.
    /// <para>
    /// Matching is done on the last three path segments so that a configured base URL
    /// (/jellyfin/Audio/...) is handled without any extra work.
    /// </para>
    /// </remarks>
    /// <param name="request">The incoming request.</param>
    /// <param name="parsed">The parsed result.</param>
    /// <returns>True if this is an audio or video delivery request.</returns>
    public static bool TryParse(HttpRequest request, out ThemeStreamRequest? parsed)
    {
        parsed = null;

        if (request is null || !request.Path.HasValue)
        {
            return false;
        }

        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            return false;
        }

        var segments = request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3)
        {
            return false;
        }

        var kind = segments[^3];
        var idSegment = segments[^2];
        var verb = segments[^1];

        bool isVideo;
        if (string.Equals(kind, "Audio", StringComparison.OrdinalIgnoreCase))
        {
            isVideo = false;
        }
        else if (string.Equals(kind, "Videos", StringComparison.OrdinalIgnoreCase))
        {
            isVideo = true;
        }
        else
        {
            return false;
        }

        if (!Guid.TryParse(idSegment, out var itemId) || itemId.Equals(Guid.Empty))
        {
            return false;
        }

        string? container = null;

        if (string.Equals(verb, "universal", StringComparison.OrdinalIgnoreCase))
        {
            if (isVideo)
            {
                return false;
            }
        }
        else if (string.Equals(verb, "stream", StringComparison.OrdinalIgnoreCase))
        {
            // No explicit container in the path.
        }
        else if (verb.StartsWith("stream.", StringComparison.OrdinalIgnoreCase))
        {
            container = verb[7..];
            if (container.Length == 0)
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        parsed = new ThemeStreamRequest(itemId, isVideo, container);
        return true;
    }

    /// <summary>
    /// Gets the containers this client said it can play, from the query string.
    /// </summary>
    /// <param name="request">The incoming request.</param>
    /// <returns>The container list, or an empty array if the client did not say.</returns>
    public string[] GetClientContainers(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (RequestedContainer is not null)
        {
            return [RequestedContainer];
        }

        // The universal endpoint takes a comma-separated list of containers the client supports.
        if (request.Query.TryGetValue("container", out var values))
        {
            var raw = values.ToString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
        }

        return [];
    }
}
