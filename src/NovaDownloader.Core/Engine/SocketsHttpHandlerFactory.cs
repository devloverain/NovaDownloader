using System.Net;

namespace NovaDownloader.Core.Engine;

public static class SocketsHttpHandlerFactory
{
    public static SocketsHttpHandler CreateHandler(int maxConnectionsPerServer = 64, TimeSpan? timeout = null, IWebProxy? proxy = null)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = maxConnectionsPerServer,
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = timeout ?? TimeSpan.FromSeconds(15),
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always
        };

        if (proxy != null)
        {
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }

        return handler;
    }

    public static HttpClient CreateClient(
        int maxConnectionsPerServer = 64,
        string? userAgent = null,
        TimeSpan? timeout = null,
        IWebProxy? proxy = null)
    {
        var handler = CreateHandler(maxConnectionsPerServer, timeout, proxy);
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(60)
        };

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        }

        return client;
    }

    public static IWebProxy? CreateProxy(bool enabled, string? address, string? username = null, string? password = null)
    {
        if (!enabled || string.IsNullOrWhiteSpace(address)) return null;
        try
        {
            var wp = new WebProxy(address.Trim());
            if (!string.IsNullOrWhiteSpace(username))
            {
                wp.Credentials = new NetworkCredential(username, password ?? string.Empty);
            }
            return wp;
        }
        catch
        {
            return null;
        }
    }
}
