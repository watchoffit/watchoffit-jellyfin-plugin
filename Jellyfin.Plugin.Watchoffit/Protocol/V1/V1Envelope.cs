using System.Text.Json.Serialization;

using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Acks;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Commands;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Events;

namespace Jellyfin.Plugin.Watchoffit.Protocol.V1;

/// <summary>
/// Discriminated union of every v1 envelope. Mirrors
/// <c>v1EnvelopeSchema</c> in
/// <c>packages/core/src/integrations/watchoffit-plugin-protocol/v1.ts</c>.
/// The <see cref="Kind"/> literal selects the concrete shape; callers use
/// a <c>switch</c> on <see cref="Kind"/> to narrow to the matching record.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(V1CommandEnvelope), "command")]
[JsonDerivedType(typeof(V1EventEnvelope), "event")]
[JsonDerivedType(typeof(V1AckEnvelope), "ack")]
[JsonDerivedType(typeof(V1ErrorEnvelope), "error")]
public abstract record V1Envelope
{
    /// <summary>Discriminator literal. Mirrors the top-level <c>kind</c> on the wire.</summary>
    [JsonPropertyName("kind")]
    public abstract V1EnvelopeKind Kind { get; }

    /// <summary>Header block. The parser asserts <see cref="V1Header.Kind"/> equals <see cref="Kind"/>.</summary>
    [JsonPropertyName("header")]
    public required V1Header Header { get; init; }
}

/// <summary>Command envelope: request/response traffic. Carries a <see cref="V1CommandPayload"/>.</summary>
public sealed record V1CommandEnvelope : V1Envelope
{
    /// <inheritdoc />
    [JsonPropertyName("kind")]
    [JsonIgnore]
    public override V1EnvelopeKind Kind => V1EnvelopeKind.Command;

    /// <inheritdoc cref="V1CommandPayload"/>
    [JsonPropertyName("payload")]
    public required V1CommandPayload Payload { get; init; }
}

/// <summary>Event envelope: asynchronous notification. Carries a <see cref="V1EventPayload"/>.</summary>
public sealed record V1EventEnvelope : V1Envelope
{
    /// <inheritdoc />
    [JsonPropertyName("kind")]
    [JsonIgnore]
    public override V1EnvelopeKind Kind => V1EnvelopeKind.Event;

    /// <inheritdoc cref="V1EventPayload"/>
    [JsonPropertyName("payload")]
    public required V1EventPayload Payload { get; init; }
}

/// <summary>Ack envelope: success reply to a command. <c>header.correlationId</c> is required and must equal <c>payload.commandId</c>.</summary>
public sealed record V1AckEnvelope : V1Envelope
{
    /// <inheritdoc />
    [JsonPropertyName("kind")]
    [JsonIgnore]
    public override V1EnvelopeKind Kind => V1EnvelopeKind.Ack;

    /// <inheritdoc cref="V1AckPayload"/>
    [JsonPropertyName("payload")]
    [JsonConverter(typeof(V1AckPayloadJsonConverter))]
    public required V1AckPayload Payload { get; init; }
}

/// <summary>Error envelope: failure reply to a command. <c>header.correlationId</c> is required.</summary>
public sealed record V1ErrorEnvelope : V1Envelope
{
    /// <inheritdoc />
    [JsonPropertyName("kind")]
    [JsonIgnore]
    public override V1EnvelopeKind Kind => V1EnvelopeKind.Error;

    /// <inheritdoc cref="V1ErrorPayload"/>
    [JsonPropertyName("payload")]
    public required V1ErrorPayload Payload { get; init; }
}
