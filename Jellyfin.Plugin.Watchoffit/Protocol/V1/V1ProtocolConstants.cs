namespace Jellyfin.Plugin.Watchoffit.Protocol.V1;

/// <summary>
/// Wire constants for the Watchoffit ↔ Jellyfin plugin protocol v1.
/// These values are part of the public protocol and must match
/// <c>packages/core/src/integrations/watchoffit-plugin-protocol/v1.ts</c> byte-for-byte.
/// </summary>
public static class V1ProtocolConstants
{
    /// <summary>
    /// Currently supported protocol version. Bumped on any breaking wire change.
    /// </summary>
    public const int ProtocolVersion = 1;

    /// <summary>
    /// Per-message cap. Larger payloads must be split (events) or refused (commands).
    /// </summary>
    public const int MaxPayloadBytes = 64 * 1024;

    /// <summary>
    /// Maximum number of events that may be batched in a single envelope.
    /// </summary>
    public const int MaxBatchSize = 50;
}
