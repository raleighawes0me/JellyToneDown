namespace Jellyfin.Plugin.JellyToneDown.Services;

/// <summary>
/// The one definition of the script tag JellyToneDown adds to the web client.
/// </summary>
/// <remarks>
/// Both ways of adding it - rewriting the response and patching index.html on disk - build
/// the block from here, so they cannot drift apart, and each can recognise the other's work
/// by its markers and decline to add a second copy.
/// </remarks>
public static class ClientScriptTag
{
    /// <summary>
    /// The comment that opens the block.
    /// </summary>
    public const string StartMarker = "<!-- JellyToneDown:start -->";

    /// <summary>
    /// The comment that closes the block.
    /// </summary>
    public const string EndMarker = "<!-- JellyToneDown:end -->";

    /// <summary>
    /// Builds the block to insert immediately before the closing body tag.
    /// </summary>
    /// <returns>The markers with the script tag between them.</returns>
    /// <remarks>
    /// The source is relative on purpose. index.html is served at <c>/web/</c>, so <c>../</c>
    /// resolves to the server root whether or not a base URL is configured. The version is in
    /// the query string so upgrading the plugin busts the browser's cache of the script.
    /// </remarks>
    public static string Build()
    {
        var version = Plugin.Instance?.Version?.ToString() ?? "0";

        return StartMarker
            + "<script src=\"../JellyToneDown/ClientScript?v=" + version + "\" defer></script>"
            + EndMarker;
    }
}
