using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Watchoffit.Protocol.V1;

/// <summary>
/// Header fields common to every envelope kind. Mirrors the union of
/// <c>v1CommandHeaderSchema</c>, <c>v1EventHeaderSchema</c>,
/// <c>v1AckHeaderSchema</c>, and <c>v1ErrorHeaderSchema</c> in
/// <c>packages/core/src/integrations/watchoffit-plugin-protocol/v1.ts</c>.
/// </summary>
/// <remarks>
/// The TypeScript schema has separate per-kind header schemas so the parser
/// can express <c>command.capabilities</c> as required and
/// <c>event.ack.error.capabilities</c> as optional. The C# mirror collapses
/// that to a single record with all fields nullable; the per-envelope
/// validation (capabilities-required, correlationId-required, etc.) lives
/// in <see cref="V1EnvelopeParser"/>.
/// </remarks>
public sealed record V1Header
{
    /// <summary>
    /// Protocol version this envelope was encoded with. Must equal
    /// <see cref="V1ProtocolConstants.ProtocolVersion"/>.
    /// </summary>
    [JsonPropertyName("version")]
    public required int Version { get; init; }

    /// <summary>
    /// Discriminator that mirrors the top-level envelope <c>kind</c>. The
    /// parser asserts the two match.
    /// </summary>
    [JsonPropertyName("kind")]
    public required V1EnvelopeKind Kind { get; init; }

    /// <summary>
    /// Unique per logical message; reused on retries for at-least-once dedup.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Monotonic per-sender counter. Detects lost messages when the
    /// receiver sees a gap larger than 1 between two consecutive envelopes.
    /// </summary>
    [JsonPropertyName("sequence")]
    public required long Sequence { get; init; }

    /// <summary>
    /// Time the envelope was assembled, ISO 8601 UTC with the
    /// <c>.fffZ</c> precision required by the protocol.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public required string Timestamp { get; init; }

    /// <summary>
    /// Server identifier the connection is bound to. Used to refuse
    /// envelopes from a previous pair after credential rotation. The literal
    /// <c>pending</c> is allowed only on unauthenticated pairing endpoints.
    /// </summary>
    [JsonPropertyName("serverConnectionId")]
    public required string ServerConnectionId { get; init; }

    /// <summary>
    /// Required on every command envelope. Optional on event/ack/error.
    /// </summary>
    [JsonPropertyName("capabilities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public V1Capabilities? Capabilities { get; init; }

    /// <summary>
    /// Required on every ack and error envelope. Optional on command and event.
    /// Carries the id of the envelope that triggered this response.
    /// </summary>
    [JsonPropertyName("correlationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrelationId { get; init; }
}
