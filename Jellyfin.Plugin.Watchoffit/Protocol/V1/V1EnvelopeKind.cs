using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Watchoffit.Protocol.V1;

/// <summary>
/// Top-level envelope kind. The wire uses the literals <c>command</c>,
/// <c>event</c>, <c>ack</c>, and <c>error</c>; the JSON converter maps
/// between them and the enum so the round-trip matches the TypeScript
/// schema byte-for-byte.
/// </summary>
[JsonConverter(typeof(V1EnvelopeKindJsonConverter))]
public enum V1EnvelopeKind
{
    /// <summary>Request/response traffic, typically Watchoffit → Jellyfin or Plugin → Watchoffit (pairing).</summary>
    Command,

    /// <summary>Asynchronous notification, typically Jellyfin → Watchoffit.</summary>
    Event,

    /// <summary>Success reply to a command. Required <c>correlationId</c>.</summary>
    Ack,

    /// <summary>Failure reply to a command. Required <c>correlationId</c>.</summary>
    Error,
}

/// <summary>
/// JSON converter for <see cref="V1EnvelopeKind"/>. The wire literals
/// (<c>"command"</c>, <c>"event"</c>, <c>"ack"</c>, <c>"error"</c>) are
/// stable across both ends of the protocol and must not change without
/// bumping <see cref="V1ProtocolConstants.ProtocolVersion"/>.
/// </summary>
internal sealed class V1EnvelopeKindJsonConverter : JsonConverter<V1EnvelopeKind>
{
    /// <inheritdoc />
    public override V1EnvelopeKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("V1EnvelopeKind must be a JSON string");
        }

        var value = reader.GetString();
        return value switch
        {
            "command" => V1EnvelopeKind.Command,
            "event" => V1EnvelopeKind.Event,
            "ack" => V1EnvelopeKind.Ack,
            "error" => V1EnvelopeKind.Error,
            _ => throw new JsonException($"Unknown V1EnvelopeKind literal: {value ?? "<null>"}"),
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, V1EnvelopeKind value, JsonSerializerOptions options)
    {
        var literal = value switch
        {
            V1EnvelopeKind.Command => "command",
            V1EnvelopeKind.Event => "event",
            V1EnvelopeKind.Ack => "ack",
            V1EnvelopeKind.Error => "error",
            _ => throw new JsonException($"Unsupported V1EnvelopeKind: {value}"),
        };
        writer.WriteStringValue(literal);
    }
}
