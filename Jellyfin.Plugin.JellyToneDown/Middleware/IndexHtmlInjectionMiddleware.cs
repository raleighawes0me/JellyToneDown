using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyToneDown.Configuration;
using Jellyfin.Plugin.JellyToneDown.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyToneDown.Middleware;

/// <summary>
/// Adds the plugin's browser script to the web client by rewriting index.html as it is
/// served, rather than editing the file on disk.
/// </summary>
/// <remarks>
/// Jellyfin still has no supported hook for adding a script to the web client, so plugins
/// that need one have historically patched index.html in place. On a package install that
/// file is owned by root while the server runs as the jellyfin user, so the patch usually
/// fails - and where it succeeds, the next jellyfin-web upgrade replaces the file and undoes
/// it. Rewriting the response instead needs no write access and cannot be undone by an
/// upgrade.
/// <para>
/// The middleware is deliberately timid. It touches nothing but a 200 text/html response to
/// the web client's own index, it no-ops when the markers are already present, and on any
/// failure it serves the original bytes: losing the slider is a small thing, breaking the web
/// client is not.
/// </para>
/// </remarks>
public sealed class IndexHtmlInjectionMiddleware
{
    private static long _injected;
    private static int _loggedOnce;

    private readonly RequestDelegate _next;
    private readonly ILogger<IndexHtmlInjectionMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IndexHtmlInjectionMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="logger">The logger.</param>
    public IndexHtmlInjectionMiddleware(RequestDelegate next, ILogger<IndexHtmlInjectionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Gets the number of page loads the script tag has been added to since the server
    /// started. The config page reports this, because "it is enabled" and "it is actually
    /// reaching the browser" are different claims.
    /// </summary>
    public static long InjectedResponses => Interlocked.Read(ref _injected);

    /// <summary>
    /// Handles a request.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <returns>A task.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Cheapest test first: this runs on every single request the server handles, and all
        // but a handful of them are not the web client's index page.
        if (!IsWebClientIndex(context.Request.Path.Value)
            || !HttpMethods.IsGet(context.Request.Method))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var config = Plugin.Config;
        if (!config.InjectClientScript
            || config.ScriptInjectionMethod != ScriptInjectionMethod.Response)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Ask the static file handler for something we can actually rewrite: no compression,
        // because we would have to decompress it first, and no partial content, because a 206
        // would go out with a length that stops matching the moment the tag is added.
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("Range");
        context.Request.Headers.Remove("If-Range");

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch
        {
            // Nothing has reached the real response stream yet, so hand it back untouched and
            // let the host render its own error page. Flushing the buffer here would commit a
            // truncated body under a 200.
            context.Response.Body = originalBody;
            throw;
        }

        context.Response.Body = originalBody;
        buffer.Seek(0, SeekOrigin.Begin);

        var isHtml = context.Response.StatusCode == StatusCodes.Status200OK
            && (context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false);

        if (!isHtml)
        {
            // A 304, a redirect, an error page: pass it through byte for byte.
            await buffer.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        string html;
        using (var reader = new StreamReader(buffer, Encoding.UTF8, true, 1024, leaveOpen: true))
        {
            html = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        }

        var bytes = Encoding.UTF8.GetBytes(Insert(html));

        context.Response.ContentType = "text/html;charset=utf-8";
        context.Response.ContentLength = bytes.Length;

        // The body is no longer the file on disk, so the static handler's validators would be
        // wrong, and we do not serve ranges of a document we just rewrote.
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.Headers.Remove("Accept-Ranges");

        await originalBody.WriteAsync(bytes.AsMemory(), context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Matches the web app shell however it is asked for - bare <c>/web</c>, <c>/web/</c> from
    /// the SPA route, and an explicit <c>/web/index.html</c>. Matching on the tail rather than
    /// the whole path keeps this correct when Jellyfin is hosted under a base URL.
    /// </summary>
    private static bool IsWebClientIndex(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web", StringComparison.OrdinalIgnoreCase);
    }

    private string Insert(string html)
    {
        try
        {
            if (html.Contains(ClientScriptTag.StartMarker, StringComparison.OrdinalIgnoreCase))
            {
                // Already there - almost certainly written to index.html on disk by an older
                // version of this plugin, on an install where that worked. Adding a second
                // copy would load the script twice.
                return html;
            }

            var closingBody = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (closingBody < 0)
            {
                return html;
            }

            var result = html[..closingBody] + ClientScriptTag.Build() + html[closingBody..];

            Interlocked.Increment(ref _injected);

            if (Interlocked.Exchange(ref _loggedOnce, 1) == 0)
            {
                _logger.LogInformation(
                    "JellyToneDown: adding the browser script to index.html as it is served. "
                    + "Nothing is written to the web client directory.");
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "JellyToneDown: could not add the browser script to index.html; serving the page unchanged.");
            return html;
        }
    }
}
