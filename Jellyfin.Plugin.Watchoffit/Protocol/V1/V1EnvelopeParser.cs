using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Watchoffit.Protocol.V1;

/// <summary>
/// Discriminated result of <c>V1EnvelopeParser.Parse</c>. Either a
/// <see cref="V1ParseResult.Ok"/> with the validated envelope, or a
/// <see cref="V1ParseResult.Failure"/> with a stable error code.
/// </summary>
public abstract record V1ParseResult
{
    private V1ParseResult()
    {
    }

    /// <summary>Successful parse carrying the validated envelope.</summary>
    public sealed record Ok(V1Envelope Envelope) : V1ParseResult;

    /// <summary>Parse failure carrying a stable <see cref="SafeErrorCode"/> and a short diagnostic.</summary>
    public sealed record Failure(SafeErrorCode Code, string Message) : V1ParseResult;
}

/// <summary>
/// Non-throwing parser for the v1 wire format. Mirrors <c>parseV1Envelope</c>
/// in <c>packages/core/src/integrations/watchoffit-plugin-protocol/v1.ts</c>:
///   1. The root must be a JSON object.
///   2. <c>header.version</c> (when present) must equal
///      <see cref="V1ProtocolConstants.ProtocolVersion"/>; otherwise
///      <see cref="SafeErrorCode.ProtocolVersionUnsupported"/> is returned.
///   3. The discriminated-union parser runs. Any failure becomes
///      <see cref="SafeErrorCode.InvalidEnvelope"/> with the issue summary
///      in <c>Message</c>.
///   4. The per-payload size cap (<see cref="V1ProtocolConstants.MaxPayloadBytes"/>)
///      is enforced via a best-effort JSON re-serialize. Exceeding it
///      returns <see cref="SafeErrorCode.InvalidEnvelope"/>.
///   5. Cross-field invariants (<c>envelope.kind == header.kind</c>, ack
///      <c>header.correlationId == payload.commandId</c>) are enforced
///      here because System.Text.Json's discriminated-union deserializer
///      does not express them on the C# side.
/// </summary>
public static class V1EnvelopeParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        // Mirror the TS `.strict()` zod schemas: an envelope that carries
        // a JSON member not declared by the C# record must be rejected
        // (the TS side returns INVALID_ENVELOPE for the same input).
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// Parse the supplied JSON document into a validated v1 envelope.
    /// </summary>
    /// <param name="document">JSON document parsed from the wire.</param>
    /// <returns>Either <see cref="V1ParseResult.Ok"/> with a fully typed envelope, or <see cref="V1ParseResult.Failure"/> with a stable error code.</returns>
    public static V1ParseResult Parse(JsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return ParseElement(document.RootElement);
    }

    /// <summary>
    /// Parse a JSON string into a validated v1 envelope.
    /// </summary>
    /// <param name="json">Raw JSON text from the wire.</param>
    /// <returns>Either <see cref="V1ParseResult.Ok"/> or <see cref="V1ParseResult.Failure"/>.</returns>
    public static V1ParseResult Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return new V1ParseResult.Failure(SafeErrorCode.InvalidEnvelope, $"Malformed JSON: {ex.Message}");
        }

        using (document)
        {
            return ParseElement(document.RootElement);
        }
    }

    private static V1ParseResult ParseElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new V1ParseResult.Failure(SafeErrorCode.InvalidEnvelope, "Envelope root must be a JSON object");
        }

        // Step 1: protocol version check.
        if (root.TryGetProperty("header", out var headerEl) && headerEl.ValueKind == JsonValueKind.Object
            && headerEl.TryGetProperty("version", out var versionEl))
        {
            if (versionEl.ValueKind != JsonValueKind.Number || !versionEl.TryGetInt32(out var version)
                || version != V1ProtocolConstants.ProtocolVersion)
            {
                return new V1ParseResult.Failure(
                    SafeErrorCode.ProtocolVersionUnsupported,
                    $"Unsupported protocol version: {(versionEl.ValueKind == JsonValueKind.Number ? versionEl.GetRawText() : "<non-number>")}");
            }
        }

        // Step 2: full discriminated-union parse.
        V1Envelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<V1Envelope>(root.GetRawText(), SerializerOptions)
                ?? throw new JsonException("Deserializer returned null");
        }
        catch (JsonException ex)
        {
            return new V1ParseResult.Failure(
                SafeErrorCode.InvalidEnvelope,
                ex.Message.Length > 256 ? ex.Message[..256] : ex.Message);
        }

        // Step 3: payload size cap.
        var payloadBytes = EstimatePayloadBytes(envelope);
        if (payloadBytes > V1ProtocolConstants.MaxPayloadBytes)
        {
            return new V1ParseResult.Failure(
                SafeErrorCode.InvalidEnvelope,
                $"Payload exceeds V1_MAX_PAYLOAD_BYTES ({V1ProtocolConstants.MaxPayloadBytes}): {payloadBytes}");
        }

        // Step 4: envelope.kind == header.kind.
        if (envelope.Header.Kind != envelope.Kind)
        {
            return new V1ParseResult.Failure(
                SafeErrorCode.InvalidEnvelope,
                $"header.kind ({envelope.Header.Kind}) does not match envelope kind ({envelope.Kind})");
        }

        // Step 5: per-kind header/payload invariants.
        switch (envelope)
        {
            case V1CommandEnvelope:
                if (envelope.Header.Capabilities is null)
                {
                    return new V1ParseResult.Failure(
                        SafeErrorCode.InvalidEnvelope,
                        "command envelope must include header.capabilities");
                }

                break;

            case V1AckEnvelope ack:
                {
                    if (envelope.Header.CorrelationId is null)
                    {
                        return new V1ParseResult.Failure(
                            SafeErrorCode.InvalidEnvelope,
                            "ack envelope must include header.correlationId");
                    }

                    if (ack.Payload.CommandId != envelope.Header.CorrelationId)
                    {
                        return new V1ParseResult.Failure(
                            SafeErrorCode.InvalidEnvelope,
                            $"ack payload.commandId ({ack.Payload.CommandId}) does not match header.correlationId ({envelope.Header.CorrelationId})");
                    }

                    break;
                }

            case V1ErrorEnvelope err:
                {
                    if (envelope.Header.CorrelationId is null)
                    {
                        return new V1ParseResult.Failure(
                            SafeErrorCode.InvalidEnvelope,
                            "error envelope must include header.correlationId");
                    }

                    if (err.Payload.CommandId is not null && err.Payload.CommandId != envelope.Header.CorrelationId)
                    {
                        return new V1ParseResult.Failure(
                            SafeErrorCode.InvalidEnvelope,
                            $"error payload.commandId ({err.Payload.CommandId}) does not match header.correlationId ({envelope.Header.CorrelationId})");
                    }

                    break;
                }
        }

        return new V1ParseResult.Ok(envelope);
    }

    private static int EstimatePayloadBytes(V1Envelope envelope)
    {
        try
        {
            var json = envelope switch
            {
                V1CommandEnvelope c => JsonSerializer.Serialize(c.Payload, SerializerOptions),
                V1EventEnvelope e => JsonSerializer.Serialize(e.Payload, SerializerOptions),
                V1AckEnvelope a => JsonSerializer.Serialize(a.Payload, SerializerOptions),
                V1ErrorEnvelope er => JsonSerializer.Serialize(er.Payload, SerializerOptions),
                _ => "{}",
            };

            // The TS mirror uses `JSON.stringify(payload).length` on a JS
            // string, which counts UTF-16 code units. We approximate that
            // by using UTF-8 byte count: it is the closest byte-faithful
            // measurement available without a full JSON re-parse, and any
            // divergence only makes the C# side more conservative (refuse
            // a payload the TS side would have accepted by 1-3 bytes).
            return Encoding.UTF8.GetByteCount(json);
        }
        catch
        {
            return int.MaxValue;
        }
    }
}
