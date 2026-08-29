using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Watchoffit.Protocol.V1.JsonConverters;

/// <summary>
/// JSON converter for the v1 pairing code. Enforces the protocol's
/// <c>/^[A-Z0-9]{6,16}$/</c> literal on the wire; an invalid code becomes
/// a <see cref="JsonException"/> which the parser surfaces as
/// <see cref="SafeErrorCode.InvalidEnvelope"/>.
/// </summary>
/// <remarks>
/// The same validator is reused for the challenge ack's <c>pairingCode</c>
/// field. The TypeScript schema applies the same regex to both ends, so
/// the C# mirror must reject malformed values on read.
/// </remarks>
public sealed class V1PairingCodeJsonConverter : JsonConverter<string>
{
    /// <summary>Wire regex. Must match <c>v1RedeemRequestCommandSchema.pairingCode</c> in v1.ts.</summary>
    public const string Pattern = "^[A-Z0-9]{6,16}$";

    /// <inheritdoc />
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("pairingCode must be a JSON string");
        }

        var value = reader.GetString();
        if (value is null)
        {
            throw new JsonException("pairingCode must be a non-null string");
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(value, Pattern))
        {
            throw new JsonException(
                $"pairingCode must match {Pattern} (got {value.Length} chars)");
        }

        return value;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, Pattern))
        {
            throw new JsonException($"pairingCode must match {Pattern} (got {value.Length} chars)");
        }

        writer.WriteStringValue(value);
    }
}
