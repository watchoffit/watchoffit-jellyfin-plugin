using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Watchoffit.Protocol.V1;

/// <summary>
/// Capability declaration sent once during pairing and re-asserted on every
/// <c>heartbeat</c> event. Allows the receiver to refuse messages that exceed
/// its limits.
/// </summary>
public sealed record V1Capabilities
{
    /// <summary>
    /// Lowest protocol version this side still understands.
    /// </summary>
    [JsonPropertyName("minProtocolVersion")]
    public required int MinProtocolVersion { get; init; }

    /// <summary>
    /// Highest protocol version this side speaks.
    /// </summary>
    [JsonPropertyName("maxProtocolVersion")]
    public required int MaxProtocolVersion { get; init; }

    /// <summary>
    /// Max bytes the sender will put into a single payload.
    /// </summary>
    [JsonPropertyName("maxPayloadBytes")]
    public required int MaxPayloadBytes { get; init; }

    /// <summary>
    /// Max events batched in one envelope (1 for command channels).
    /// </summary>
    [JsonPropertyName("maxBatchSize")]
    public required int MaxBatchSize { get; init; }
}
