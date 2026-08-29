using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Watchoffit.Pairing;

/// <summary>
/// A short-lived, opaque pairing bundle copied from Watchoffit into the plugin.
/// </summary>
/// <remarks>
/// The format is <c>watchoffit-jellyfin:v1:&lt;base64url-json&gt;</c>. The JSON is
/// intentionally opaque to the user, but it is not a credential: it contains
/// only a one-time pairing code with a server-side expiry. Keeping the Watchoffit
/// URL and code together removes the error-prone multi-field setup flow.
/// </remarks>
public sealed record WatchoffitConnectionString(string BaseUrl, string ServerConnectionId, string PairingCode)
{
    private const string Prefix = "watchoffit-jellyfin:v1:";
    private static readonly Regex PairingCodePattern = new("^[A-Z0-9]{6,16}$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // The TypeScript protocol uses camelCase. Make encoding canonical and
        // accept it explicitly on read rather than relying on host defaults.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Encodes this bundle for display in Watchoffit's pairing UI.</summary>
    /// <returns>Versioned opaque connection string.</returns>
    public string Encode()
    {
        if (!TryNormalizeBaseUrl(BaseUrl, out var baseUrl))
        {
            throw new InvalidOperationException("Connection string contains an invalid Watchoffit URL.");
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new Payload(baseUrl, ServerConnectionId, PairingCode),
            SerializerOptions);
        return Prefix + Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Parses and validates a connection string without making a network request.</summary>
    /// <param name="value">Connection string supplied by the administrator.</param>
    /// <param name="connection">Parsed result when successful.</param>
    /// <param name="error">Safe error description when parsing fails.</param>
    /// <returns><see langword="true"/> if the string is valid.</returns>
    public static bool TryParse(string? value, out WatchoffitConnectionString? connection, out string? error)
    {
        connection = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            error = "invalid connection string format";
            return false;
        }

        var encoded = value[Prefix.Length..].Trim();
        if (encoded.Length == 0 || encoded.Length > 4096)
        {
            error = "invalid connection string payload";
            return false;
        }

        Payload? payload;
        try
        {
            var normalized = encoded.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + ((4 - (normalized.Length % 4)) % 4), '=');
            payload = JsonSerializer.Deserialize<Payload>(Convert.FromBase64String(normalized), SerializerOptions);
        }
        catch (FormatException)
        {
            error = "invalid connection string encoding";
            return false;
        }
        catch (JsonException)
        {
            error = "invalid connection string payload";
            return false;
        }

        if (payload is null
            || !TryNormalizeBaseUrl(payload.BaseUrl, out var baseUrl)
            || string.IsNullOrWhiteSpace(payload.ServerConnectionId)
            || payload.ServerConnectionId.Length > 128
            || !PairingCodePattern.IsMatch(payload.PairingCode ?? string.Empty))
        {
            error = "invalid connection string values";
            return false;
        }

        connection = new WatchoffitConnectionString(baseUrl, payload.ServerConnectionId, payload.PairingCode!);
        return true;
    }

    private static bool TryNormalizeBaseUrl(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/');
        return true;
    }

    private sealed record Payload(string BaseUrl, string ServerConnectionId, string PairingCode);
}
