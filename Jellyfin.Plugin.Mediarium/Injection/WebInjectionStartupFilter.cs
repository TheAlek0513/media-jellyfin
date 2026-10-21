using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Mediarium.Injection;

/// <summary>
/// Adds the plugin's script to Jellyfin Web's index.html as it is sent, so no
/// file on disk changes and nothing is left behind when the plugin goes.
/// </summary>
public class WebInjectionStartupFilter : IStartupFilter
{
    private const string Marker = "MediariumRequests/client.js";

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(InjectAsync);
            next(app);
        };
    }

    private static bool IsIndex(PathString path)
    {
        var p = path.Value ?? string.Empty;
        return p.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task InjectAsync(HttpContext context, Func<Task> next)
    {
        if (!HttpMethods.IsGet(context.Request.Method)
            || !IsIndex(context.Request.Path)
            || Plugin.Instance?.Configuration.AddToJellyfinWeb == false)
        {
            await next().ConfigureAwait(false);
            return;
        }

        // Ask for the plain, full file so it can be changed.
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("If-None-Match");
        context.Request.Headers.Remove("If-Modified-Since");

        var original = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next().ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = original;
        }

        buffer.Position = 0;
        var isHtml = context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true;
        if (context.Response.StatusCode != StatusCodes.Status200OK || !isHtml)
        {
            await buffer.CopyToAsync(original).ConfigureAwait(false);
            return;
        }

        var html = Encoding.UTF8.GetString(buffer.ToArray());
        if (!html.Contains(Marker, StringComparison.Ordinal))
        {
            var version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0";
            var tag = "<script src=\"../" + Marker + "?v=" + version + "\" defer></script>";
            var at = html.LastIndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            html = at >= 0 ? html.Insert(at, tag) : html + tag;
        }

        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers.ETag = default;
        context.Response.Headers.LastModified = default;
        await original.WriteAsync(bytes).ConfigureAwait(false);
    }
}
