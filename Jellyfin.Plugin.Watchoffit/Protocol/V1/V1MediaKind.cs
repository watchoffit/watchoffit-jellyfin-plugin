using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Watchoffit.Protocol.V1;

/// <summary>
/// Provider-neutral media kind. Only <c>movie</c> and <c>episode</c> are
/// defined in v1. The string conversion uses the wire literals directly so
/// serialized JSON stays byte-for-byte compatible with
/// <c>packages/core/src/integrations/watchoffit-plugin-protocol/v1.ts</c>.
/// </summary>
[JsonConverter(typeof(V1MediaKindJsonConverter))]
public enum V1MediaKind
{
    /// <summary>A standalone movie.</summary>
    Movie,

    /// <summary>A single episode of a TV show.</summary>
    Episode,
}

/// <summary>
/// JSON converter for <see cref="V1MediaKind"/>. Uses the wire literals
/// (<c>"movie"</c>, <c>"episode"</c>) directly so the round-trip matches
/// the TypeScript schema byte-for-byte.
/// </summary>
internal sealed class V1MediaKindJsonConverter : JsonConverter<V1MediaKind>
{
    /// <inheritdoc />
    public override V1MediaKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("V1MediaKind must be a JSON string");
        }

        var value = reader.GetString();
        return value switch
        {
            "movie" => V1MediaKind.Movie,
            "episode" => V1MediaKind.Episode,
            _ => throw new JsonException($"Unknown V1MediaKind literal: {value ?? "<null>"}"),
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, V1MediaKind value, JsonSerializerOptions options)
    {
        var literal = value switch
        {
            V1MediaKind.Movie => "movie",
            V1MediaKind.Episode => "episode",
            _ => throw new JsonException($"Unsupported V1MediaKind: {value}"),
        };
        writer.WriteStringValue(literal);
    }
}
