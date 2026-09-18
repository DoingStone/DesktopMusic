using System.Text;

namespace TaskbarLyrics.Core.Net;

/// <summary>
/// Builds request URIs from untrusted values.
/// <para>
/// Track titles and artist names come from whatever the player reports and end up
/// in query strings. Interpolating them directly means a title containing
/// <c>&amp;</c>, <c>#</c>, <c>?</c> or a percent sign silently changes the request
/// — a song called "Me &amp; You" would corrupt the parameter list, and a crafted
/// title could inject extra parameters. Every value is therefore percent-encoded
/// against a fixed base, and the finished URI is validated before use.
/// </para>
/// </summary>
public static class QueryUri
{
    /// <summary>
    /// Compose a URI from a base address and name/value pairs.
    /// Null or empty values are omitted rather than sent as blanks.
    /// </summary>
    /// <exception cref="ArgumentException">The base is not an absolute http(s) address.</exception>
    public static Uri Build(string baseUrl, params (string Name, string? Value)[] parameters)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new ArgumentException($"Base URL is not absolute: {baseUrl}", nameof(baseUrl));
        }

        if (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException($"Unsupported scheme: {baseUri.Scheme}", nameof(baseUrl));
        }

        var query = new StringBuilder();

        foreach (var (name, value) in parameters)
        {
            if (string.IsNullOrEmpty(value)) continue;

            if (query.Length > 0) query.Append('&');
            query.Append(Encode(name)).Append('=').Append(Encode(value));
        }

        // Preserve any query the base already carried.
        var separator = baseUri.Query.Length > 0 ? "&" : "?";

        var builder = new StringBuilder(baseUri.GetLeftPart(UriPartial.Path));
        if (baseUri.Query.Length > 0) builder.Append(baseUri.Query);
        if (query.Length > 0) builder.Append(separator).Append(query);

        var text = builder.ToString();

        if (!Uri.TryCreate(text, UriKind.Absolute, out var result))
        {
            throw new ArgumentException("Composed URI is not valid.", nameof(baseUrl));
        }

        return result;
    }

    /// <summary>
    /// Percent-encode one component. Uses <see cref="Uri.EscapeDataString"/> on a
    /// bounded input: very long titles are truncated first so a pathological value
    /// cannot produce an unbounded URL.
    /// </summary>
    public static string Encode(string value)
    {
        const int MaxComponentLength = 512;

        var trimmed = value.Length > MaxComponentLength
            ? value[..MaxComponentLength]
            : value;

        // A lone surrogate would throw; strip anything that is not a valid scalar.
        trimmed = RemoveInvalidSurrogates(trimmed);

        return Uri.EscapeDataString(trimmed);
    }

    private static string RemoveInvalidSurrogates(string value)
    {
        StringBuilder? sb = null;

        for (int i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    sb?.Append(c).Append(value[i + 1]);
                    i++;
                    continue;
                }

                sb ??= new StringBuilder(value[..i]);
                continue;
            }

            if (char.IsLowSurrogate(c))
            {
                sb ??= new StringBuilder(value[..i]);
                continue;
            }

            sb?.Append(c);
        }

        return sb?.ToString() ?? value;
    }
}
