namespace Jellyfin.Plugin.Watchoffit.Protocol.V1;

/// <summary>
/// Stable, machine-readable error codes returned by
/// <see cref="V1EnvelopeParser.Parse(System.Text.Json.JsonDocument)"/>.
/// Mirrors the <c>SafeErrorCode</c> literal set in
/// <c>packages/core/src/integrations/watchoffit-plugin-protocol/v1.ts</c>.
/// </summary>
/// <remarks>
/// Codes are SCREAMING_SNAKE so they round-trip through JSON without quoting
/// and so they show up clearly in log aggregation queries. Parser and
/// transport errors use this enum; the <c>error</c> envelope's
/// <c>payload.code</c> uses <see cref="ApplicationErrorCode"/> instead.
/// </remarks>
public enum SafeErrorCode
{
    /// <summary>
    /// The envelope header advertises a protocol version this side does not speak.
    /// </summary>
    ProtocolVersionUnsupported,

    /// <summary>
    /// The envelope failed JSON shape validation (missing field, wrong type, unknown literal).
    /// </summary>
    InvalidEnvelope,

    /// <summary>
    /// The remote side required credentials and either none were sent or the token was rejected.
    /// </summary>
    AuthRequired,

    /// <summary>
    /// The remote side is rate-limiting requests. The HTTP layer should honor <c>Retry-After</c>.
    /// </summary>
    RateLimited,

    /// <summary>
    /// An unexpected internal error prevented the parser from completing.
    /// </summary>
    InternalError,
}
