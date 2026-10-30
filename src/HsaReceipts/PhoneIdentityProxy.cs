using System.Diagnostics;
using System.Net;
using Yarp.ReverseProxy.Forwarder;

namespace HsaReceipts;

internal static class PhoneIdentityProxy
{
    private static readonly string[] PrivateCookieNames =
    [
        "__Host-HsaReceipts",
        "__Host-HsaReceipts-Antiforgery"
    ];

    public static bool IsAllowedPath(PathString path) =>
        path.StartsWithSegments("/realms/household")
        || path.StartsWithSegments("/resources");

    public static string? RemovePrivateCookies(string? cookieHeader)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return null;
        }

        var cookies = cookieHeader
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(cookie =>
            {
                var separator = cookie.IndexOf('=');
                var name = separator >= 0 ? cookie[..separator] : cookie;
                return !PrivateCookieNames.Contains(name, StringComparer.Ordinal);
            })
            .ToArray();

        return cookies.Length == 0 ? null : string.Join("; ", cookies);
    }

    public static HttpMessageInvoker CreateHttpClient() =>
        new(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            EnableMultipleHttp2Connections = true,
            ActivityHeadersPropagator =
                new ReverseProxyPropagator(DistributedContextPropagator.Current),
            ConnectTimeout = TimeSpan.FromSeconds(15)
        });

    public static void Map(
        WebApplication app,
        string destinationPrefix,
        Uri publicOrigin,
        HttpMessageInvoker httpClient)
    {
        var requestConfig = new ForwarderRequestConfig
        {
            ActivityTimeout = TimeSpan.FromSeconds(100)
        };
        var transformer = new IdentityTransformer(publicOrigin);

        app.Map("/realms/{**catchAll}", Forward);
        app.Map("/resources/{**catchAll}", Forward);
        app.Map("/admin/{**catchAll}", Block);
        return;

        static Task Block(HttpContext context)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        async Task Forward(HttpContext context, IHttpForwarder forwarder)
        {
            if (!IsAllowedPath(context.Request.Path))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var error = await forwarder.SendAsync(
                context,
                destinationPrefix,
                httpClient,
                requestConfig,
                transformer);
            if (error == ForwarderError.None || context.Response.HasStarted)
            {
                return;
            }

            var exception = context.GetForwarderErrorFeature()?.Exception;
            app.Logger.LogError(exception, "Phone identity forwarding failed with {Error}.", error);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
    }

    internal sealed class DevTunnelHeaderHandler(HttpMessageHandler innerHandler)
        : DelegatingHandler(innerHandler)
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation(
                "X-Tunnel-Skip-AntiPhishing-Page",
                "true");
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class IdentityTransformer(Uri publicOrigin) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(
            HttpContext httpContext,
            HttpRequestMessage proxyRequest,
            string destinationPrefix,
            CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(
                httpContext,
                proxyRequest,
                destinationPrefix,
                cancellationToken);

            proxyRequest.Headers.Host = null;
            proxyRequest.Headers.Remove("X-Forwarded-Host");
            proxyRequest.Headers.Remove("X-Forwarded-Proto");
            proxyRequest.Headers.TryAddWithoutValidation(
                "X-Forwarded-Host",
                publicOrigin.Authority);
            proxyRequest.Headers.TryAddWithoutValidation(
                "X-Forwarded-Proto",
                Uri.UriSchemeHttps);
            proxyRequest.Headers.Remove("Cookie");
            var cookies = RemovePrivateCookies(httpContext.Request.Headers.Cookie);
            if (cookies is not null)
            {
                proxyRequest.Headers.TryAddWithoutValidation("Cookie", cookies);
            }
        }
    }
}
