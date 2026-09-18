using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.JellyToneDown.Middleware;

/// <summary>
/// Inserts the plugin's middleware at the very front of Jellyfin's request pipeline.
/// </summary>
/// <remarks>
/// Jellyfin registers plugin services into the same service collection the web host is
/// built from, and ASP.NET Core resolves every registered <see cref="IStartupFilter"/> when
/// it builds the request pipeline. That is the supported way for a plugin to add middleware
/// without patching the server.
/// <para>
/// Running before authentication means <c>HttpContext.User</c> is not populated yet, which
/// is why the middleware resolves the caller through <c>IAuthorizationContext</c> instead.
/// </para>
/// </remarks>
public sealed class ThemeGainStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return app =>
        {
            // Both run outermost, which is what the index rewriter needs: stripping
            // Accept-Encoding on the way in only yields an uncompressed response if nothing
            // downstream has already committed to compressing it. The two match disjoint
            // paths, so their order relative to each other does not matter.
            app.UseMiddleware<IndexHtmlInjectionMiddleware>();
            app.UseMiddleware<ThemeGainMiddleware>();
            next(app);
        };
    }
}
