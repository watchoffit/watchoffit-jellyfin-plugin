using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Acks;

/// <summary>
/// Ack payload returned by <c>POST /api/watchoffit-plugin/command/poll</c>.
/// Mirrors <c>v1CommandPollAckPayloadSchema</c> in
/// <c>packages/core/src/integrations/watchoffit-plugin-protocol/v1.ts</c>.
/// </summary>
/// <remarks>
/// Discriminated from the base ack via the literal <c>kind: "command_poll_ack"</c>.
/// Without the discriminator, the wire would just be an ack with an
/// extra <c>commands</c> field — self-describing the shape lets the C#
/// parser extract the leased commands cleanly even though the existing
/// v1 ack parser is field-presence discriminated rather than
/// <c>kind</c>-discriminated.
///
/// The <c>kind</c> field is intentionally NOT exposed as a typed
/// property on the record: the other <see cref="V1AckPayload"/>
/// derived records (<see cref="V1BaseAck"/>, <see cref="V1ChallengeAck"/>,
/// etc.) are field-presence discriminated by <see cref="V1AckPayloadJsonConverter"/>,
/// not by a typed <c>Kind</c> property. The
/// <c>command_poll_ack</c> branch is matched on the raw JSON
/// <c>kind</c> literal inside the converter; callers that need to
/// assert the discriminator at runtime should use a <c>is V1CommandPollAckPayload</c>
/// type check rather than reading a property.
/// </remarks>
public sealed record V1CommandPollAckPayload : V1AckPayload
{
    /// <summary>Commands leased to this poll. Empty when the long-poll window closed without traffic.</summary>
    [JsonPropertyName("commands")]
    public required IReadOnlyList<V1LeasedCommand> Commands { get; init; }
}
