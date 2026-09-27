using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SSO_Auth.Api.Net;

namespace Jellyfin.Plugin.SSO_Auth.Api.Avatar;

/// <summary>
/// Downloads bounded raster avatars with address checks on the actual connection, following
/// Flowfin's avatar security policy. Redirects and proxies cannot bypass the address checks.
/// </summary>
internal static class AvatarDownload
{
    internal const int MaximumBytes = 5 * 1024 * 1024;

    /// <summary>Loads a raster avatar from a data URL or a public HTTP endpoint.</summary>
    /// <param name="url">The avatar URL.</param>
    /// <returns>The image content and normalized media type.</returns>
    internal static async Task<(MemoryStream Stream, string ContentType)> LoadAsync(string url)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int comma = url.IndexOf(',', StringComparison.Ordinal);
            if (comma < 0 || comma > 64 || !url.Substring(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                || url.Length - comma - 1 > ((MaximumBytes + 2) / 3) * 4)
            {
                throw new InvalidDataException("Invalid or oversized inline avatar.");
            }

            var mediaType = url.Substring(5, comma - 12);
            if (!AvatarContentType.TryResolveExtension(mediaType, out _))
            {
                throw new InvalidDataException("Unsupported avatar type.");
            }

            var bytes = Convert.FromBase64String(url.Substring(comma + 1));
            if (bytes.Length > MaximumBytes)
            {
                throw new InvalidDataException("Oversized inline avatar.");
            }

            return (new MemoryStream(bytes), mediaType.ToLowerInvariant());
        }

        if (!AvatarUrlValidator.IsAllowedUrl(url, out var uri) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException("Avatar endpoint is not permitted.");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = async (context, cancellationToken) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
                foreach (var address in addresses)
                {
                    if (IpAddressClassifier.IsBlockedAddress(address))
                    {
                        continue;
                    }

                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (SocketException)
                    {
                        socket.Dispose();
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }

                throw new HttpRequestException("No permitted avatar address was reachable.");
            }
        };
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (!AvatarContentType.TryResolveExtension(contentType, out _) || response.Content.Headers.ContentLength > MaximumBytes)
        {
            throw new InvalidDataException("Unsupported or oversized avatar.");
        }

        using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        var result = new MemoryStream();
        try
        {
            var buffer = new byte[8192];
            int count;
            while ((count = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (result.Length + count > MaximumBytes)
                {
                    throw new InvalidDataException("Oversized avatar.");
                }

                result.Write(buffer, 0, count);
            }

            result.Position = 0;
            return (result, contentType);
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }
}
