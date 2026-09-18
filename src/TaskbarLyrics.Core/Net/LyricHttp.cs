using System.Net;
using System.Net.Http.Headers;

namespace TaskbarLyrics.Core.Net;

/// <summary>
/// Shared <see cref="HttpClient"/> for lyric providers.
/// <para>
/// A single long-lived client avoids socket exhaustion. The browser-like headers
/// are required: QQ Music's lyric endpoints reject requests without a matching
/// <c>Referer</c> and <c>User-Agent</c>.
/// </para>
/// </summary>
public static class LyricHttp
{
    private const string DesktopUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

    private static readonly Lazy<HttpClient> LazyClient = new(() =>
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(12),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(DesktopUserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
        client.DefaultRequestHeaders.ConnectionClose = false;

        return client;
    });

    public static HttpClient Client => LazyClient.Value;

    /// <summary>Perform a GET and return the raw body, or null when the call fails.</summary>
    public static async Task<string?> GetStringAsync(
        Uri url,
        string referer,
        CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri(referer);
            using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await ReadBoundedAsync(resp, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Perform a JSON POST and return the raw body, or null when the call fails.</summary>
    public static async Task<string?> PostJsonAsync(
        Uri url,
        string json,
        string referer,
        CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Referrer = new Uri(referer);
            req.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await ReadBoundedAsync(resp, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Upper bound on a lyric response body. Real payloads are tens of kilobytes;
    /// the cap stops a hostile or malfunctioning endpoint from streaming an
    /// unbounded body into memory.
    /// </summary>
    private const int MaxResponseBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Read a response body, refusing anything beyond
    /// <see cref="MaxResponseBytes"/>.
    /// </summary>
    private static async Task<string?> ReadBoundedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        // Reject early when the server declares an oversized body.
        if (resp.Content.Headers.ContentLength is > MaxResponseBytes) return null;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var buffer = new byte[81920];
        using var accumulated = new MemoryStream();

        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0) break;

            // Enforce the cap even when Content-Length was absent or lied.
            if (accumulated.Length + read > MaxResponseBytes) return null;

            accumulated.Write(buffer, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(accumulated.ToArray());
    }

    /// <summary>Decode base64 that may contain either UTF-8 text or raw bytes.</summary>
    public static string? TryDecodeBase64(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        try
        {
            var bytes = Convert.FromBase64String(base64.Trim());
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }
}
