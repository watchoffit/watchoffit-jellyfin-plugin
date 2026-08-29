using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Jellyfin.Plugin.Watchoffit.Protocol.V1.JsonConverters;

namespace Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Acks;

/// <summary>Status codes for an <see cref="V1AckPayload"/>.</summary>
[JsonConverter(typeof(V1AckStatusJsonConverter))]
public enum V1AckStatus
{
    /// <summary>Successful execution. Side effects applied as requested.</summary>
    Ok,

    /// <summary>Request was a no-op because the target state already matches.</summary>
    Noop,
}

internal sealed class V1AckStatusJsonConverter : JsonConverter<V1AckStatus>
{
    public override V1AckStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("V1AckStatus must be a JSON string");
        }

        var value = reader.GetString();
        return value switch
        {
            "ok" => V1AckStatus.Ok,
            "noop" => V1AckStatus.Noop,
            _ => throw new JsonException($"Unknown V1AckStatus literal: {value ?? "<null>"}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, V1AckStatus value, JsonSerializerOptions options)
    {
        var literal = value switch
        {
            V1AckStatus.Ok => "ok",
            V1AckStatus.Noop => "noop",
            _ => throw new JsonException($"Unsupported V1AckStatus: {value}"),
        };
        writer.WriteStringValue(literal);
    }
}

/// <summary>
/// Discriminated union of every v1 ack payload. Mirrors
/// <c>v1AckPayloadSchema</c> in
/// <c>packages/core/src/integrations/watchoffit-plugin-protocol/v1.ts</c>.
/// </summary>
/// <remarks>
/// The wire union is order-sensitive: <c>rotate_credential</c> wins on
/// <c>newCredential</c>, <c>redeem_request</c> wins on <c>credential</c>,
/// <c>challenge_request</c> wins on <c>expiresAt</c> + <c>pairingCode</c>,
/// the generic shape is the fallback. The C# mirror needs a manual
/// converter because the union has no <c>kind</c> discriminator on the
/// wire (it is field-presence discriminated); see
/// <see cref="V1AckPayloadJsonConverter"/>.
/// </remarks>
[JsonConverter(typeof(V1AckPayloadJsonConverter))]
public abstract record V1AckPayload
{
    /// <summary>Id of the command being answered.</summary>
    [JsonPropertyName("commandId")]
    public required string CommandId { get; init; }

    /// <summary>Application status. Successful pair flows always use <see cref="V1AckStatus.Ok"/>.</summary>
    [JsonPropertyName("status")]
    public V1AckStatus Status { get; init; } = V1AckStatus.Ok;

    /// <summary>Optional human-readable hint for diagnostics. Never shown to end users.</summary>
    [JsonPropertyName("note")]
    public string? Note { get; init; }
}

/// <summary>Generic ack shape used by <c>mark_played</c>, <c>mark_unplayed</c>, <c>ping</c>, <c>reconcile_request</c>, and <c>revoke_request</c>.</summary>
public sealed record V1BaseAck : V1AckPayload;

/// <summary>Ack for the <c>rotate_credential</c> command. Carries the fresh credential.</summary>
public sealed record V1RotateCredentialAck : V1AckPayload
{
    /// <summary>New credential issued by Watchoffit. Status is always <see cref="V1AckStatus.Ok"/>.</summary>
    [JsonPropertyName("newCredential")]
    public required string NewCredential { get; init; }

    /// <summary>Server-side timestamp the rotation was applied.</summary>
    [JsonPropertyName("rotatedAt")]
    public required string RotatedAt { get; init; }
}

/// <summary>Ack for the <c>challenge_request</c> command. Carries the pairing code and expiry.</summary>
public sealed record V1ChallengeAck : V1AckPayload
{
    /// <summary>Freshly minted <c>serverConnectionId</c> assigned by Watchoffit.</summary>
    [JsonPropertyName("serverConnectionId")]
    public required string ServerConnectionId { get; init; }

    /// <summary>Watchoffit server's display name (shown in the Jellyfin dashboard).</summary>
    [JsonPropertyName("watchoffitServerName")]
    public required string WatchoffitServerName { get; init; }

    /// <summary>One-time pairing code the administrator pastes back into Watchoffit.</summary>
    [JsonPropertyName("pairingCode")]
    [JsonConverter(typeof(V1PairingCodeJsonConverter))]
    public required string PairingCode { get; init; }

    /// <summary>ISO 8601 timestamp the pairing code expires.</summary>
    [JsonPropertyName("expiresAt")]
    public required string ExpiresAt { get; init; }
}

/// <summary>Ack for the <c>redeem_request</c> command. Carries the issued credential.</summary>
public sealed record V1RedeemAck : V1AckPayload
{
    /// <summary>Server connection id the credential is bound to.</summary>
    [JsonPropertyName("serverConnectionId")]
    public required string ServerConnectionId { get; init; }

    /// <summary>Watchoffit server's display name.</summary>
    [JsonPropertyName("watchoffitServerName")]
    public required string WatchoffitServerName { get; init; }

    /// <summary>ISO 8601 timestamp the credential was issued.</summary>
    [JsonPropertyName("issuedAt")]
    public required string IssuedAt { get; init; }

    /// <summary>Opaque credential issued by Watchoffit. Not a password, not a user secret.</summary>
    [JsonPropertyName("credential")]
    public required string Credential { get; init; }
}

/// <summary>
/// Manual JSON converter for <see cref="V1AckPayload"/>. Mirrors the
/// TypeScript union: tries the most specific shape first, falls back to
/// the generic shape. The same precedence applies on write so the wire
/// format round-trips.
/// </summary>
public sealed class V1AckPayloadJsonConverter : JsonConverter<V1AckPayload>
{
    /// <inheritdoc />
    public override V1AckPayload? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("V1AckPayload must be a JSON object");
        }

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("commandId", out var commandId) || commandId.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("V1AckPayload must have a string 'commandId'");
        }

        var id = commandId.GetString()!;
        string? note = root.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        var status = ParseRequiredStatus(root);

        // 0) command_poll_ack: discriminated by the literal `kind: "command_poll_ack"`
        //    and requires a `commands` array. The other branches are field-presence
        //    discriminated, so this branch has to come first: a poll response that
        //    happens to carry no commands (empty queue) still needs to be matched
        //    here so the plugin can iterate the (empty) array. The `.strict()` rules
        //    on the other base branches mean a stray `kind` member would otherwise
        //    reject the envelope.
        if (root.TryGetProperty("kind", out var kindEl) && kindEl.ValueKind == JsonValueKind.String
            && string.Equals(kindEl.GetString(), "command_poll_ack", StringComparison.Ordinal))
        {
            if (status != V1AckStatus.Ok)
            {
                throw new JsonException("command_poll_ack must have status=\"ok\"");
            }

            if (!root.TryGetProperty("commands", out var commandsEl) || commandsEl.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("command_poll_ack must have a `commands` array");
            }

            var commands = new List<V1LeasedCommand>(commandsEl.GetArrayLength());
            foreach (var cmd in commandsEl.EnumerateArray())
            {
                if (cmd.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("commands[] entries must be JSON objects");
                }

                if (!cmd.TryGetProperty("commandId", out var cmdIdEl) || cmdIdEl.ValueKind != JsonValueKind.String)
                {
                    throw new JsonException("commands[].commandId must be a string");
                }

                if (!cmd.TryGetProperty("commandKind", out var cmdKindEl) || cmdKindEl.ValueKind != JsonValueKind.String)
                {
                    throw new JsonException("commands[].commandKind must be a string");
                }

                if (!cmd.TryGetProperty("payload", out var payloadEl))
                {
                    throw new JsonException("commands[].payload is required");
                }

                if (!cmd.TryGetProperty("leaseUntil", out var leaseEl) || leaseEl.ValueKind != JsonValueKind.Number)
                {
                    throw new JsonException("commands[].leaseUntil must be a positive integer (unix seconds)");
                }

                if (!cmd.TryGetProperty("attemptToken", out var attemptEl) || attemptEl.ValueKind != JsonValueKind.String)
                {
                    throw new JsonException("commands[].attemptToken must be a string");
                }

                // `JsonElement.Clone` decouples the entry from the parsed
                // document so the caller can read the payload after the
                // converter's `using JsonDocument` block goes out of scope.
                commands.Add(new V1LeasedCommand
                {
                    CommandId = cmdIdEl.GetString()!,
                    CommandKind = cmdKindEl.GetString()!,
                    Payload = payloadEl.Clone(),
                    LeaseUntil = leaseEl.GetInt64(),
                    AttemptToken = attemptEl.GetString()!,
                });
            }

            return new V1CommandPollAckPayload
            {
                CommandId = id,
                Status = status,
                Note = note,
                Commands = commands,
            };
        }

        // 1) rotate_credential ack: requires newCredential + rotatedAt.
        //    TS schema pins status to literal "ok" — a noop rotation would
        //    leave the plugin holding a credential Watchoffit believes is
        //    retired, so we reject anything other than ok.
        if (root.TryGetProperty("newCredential", out var newCred) && newCred.ValueKind == JsonValueKind.String
            && root.TryGetProperty("rotatedAt", out var rotatedAt) && rotatedAt.ValueKind == JsonValueKind.String)
        {
            if (status != V1AckStatus.Ok)
            {
                throw new JsonException("rotate_credential ack must have status=\"ok\"");
            }

            return new V1RotateCredentialAck
            {
                CommandId = id,
                Status = V1AckStatus.Ok,
                Note = note,
                NewCredential = newCred.GetString()!,
                RotatedAt = rotatedAt.GetString()!,
            };
        }

        // 2) redeem ack: requires credential + issuedAt + serverConnectionId + watchoffitServerName.
        //    TS schema pins status to literal "ok" — the credential only
        //    exists on success, so any other status is an envelope bug.
        if (root.TryGetProperty("credential", out var credential) && credential.ValueKind == JsonValueKind.String
            && root.TryGetProperty("issuedAt", out var issuedAt) && issuedAt.ValueKind == JsonValueKind.String
            && root.TryGetProperty("serverConnectionId", out var scn) && scn.ValueKind == JsonValueKind.String
            && root.TryGetProperty("watchoffitServerName", out var ssn) && ssn.ValueKind == JsonValueKind.String)
        {
            if (status != V1AckStatus.Ok)
            {
                throw new JsonException("redeem_request ack must have status=\"ok\"");
            }

            return new V1RedeemAck
            {
                CommandId = id,
                Status = V1AckStatus.Ok,
                Note = note,
                ServerConnectionId = scn.GetString()!,
                WatchoffitServerName = ssn.GetString()!,
                IssuedAt = issuedAt.GetString()!,
                Credential = credential.GetString()!,
            };
        }

        // 3) challenge ack: requires expiresAt + pairingCode + serverConnectionId + watchoffitServerName.
        //    TS schema pins status to literal "ok"; the pairingCode must
        //    match the wire regex (enforced here because the manual
        //    converter bypasses the V1PairingCodeJsonConverter on the
        //    challenge ack's pairingCode field).
        if (root.TryGetProperty("expiresAt", out var expiresAt) && expiresAt.ValueKind == JsonValueKind.String
            && root.TryGetProperty("pairingCode", out var pairingCode) && pairingCode.ValueKind == JsonValueKind.String
            && root.TryGetProperty("serverConnectionId", out var scn2) && scn2.ValueKind == JsonValueKind.String
            && root.TryGetProperty("watchoffitServerName", out var ssn2) && ssn2.ValueKind == JsonValueKind.String)
        {
            if (status != V1AckStatus.Ok)
            {
                throw new JsonException("challenge_request ack must have status=\"ok\"");
            }

            var code = pairingCode.GetString()!;
            if (!Regex.IsMatch(code, V1PairingCodeJsonConverter.Pattern))
            {
                throw new JsonException(
                    $"challenge_request ack pairingCode must match {V1PairingCodeJsonConverter.Pattern} (got {code.Length} chars)");
            }

            return new V1ChallengeAck
            {
                CommandId = id,
                Status = V1AckStatus.Ok,
                Note = note,
                ServerConnectionId = scn2.GetString()!,
                WatchoffitServerName = ssn2.GetString()!,
                PairingCode = code,
                ExpiresAt = expiresAt.GetString()!,
            };
        }

        // 4) Generic shape: commandId + status + optional note. Covers
        //    mark_played, mark_unplayed, ping, reconcile_request, revoke_request.
        return new V1BaseAck
        {
            CommandId = id,
            Status = status,
            Note = note,
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, V1AckPayload value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("commandId", value.CommandId);
        writer.WriteString("status", value.Status switch
        {
            V1AckStatus.Ok => "ok",
            V1AckStatus.Noop => "noop",
            _ => throw new JsonException($"Unsupported V1AckStatus: {value.Status}"),
        });
        if (value.Note is not null)
        {
            writer.WriteString("note", value.Note);
        }

        switch (value)
        {
            case V1RotateCredentialAck r:
                writer.WriteString("newCredential", r.NewCredential);
                writer.WriteString("rotatedAt", r.RotatedAt);
                break;
            case V1RedeemAck d:
                writer.WriteString("serverConnectionId", d.ServerConnectionId);
                writer.WriteString("watchoffitServerName", d.WatchoffitServerName);
                writer.WriteString("issuedAt", d.IssuedAt);
                writer.WriteString("credential", d.Credential);
                break;
            case V1ChallengeAck c:
                writer.WriteString("serverConnectionId", c.ServerConnectionId);
                writer.WriteString("watchoffitServerName", c.WatchoffitServerName);
                writer.WriteString("pairingCode", c.PairingCode);
                writer.WriteString("expiresAt", c.ExpiresAt);
                break;
            case V1CommandPollAckPayload poll:
                // The plugin currently only *receives* a command_poll_ack
                // (it is the response of POST /api/watchoffit-plugin/command/poll).
                // Write the wire shape so a future feature that round-trips
                // a synthetic poll ack in tests or local fixtures still
                // serializes the `commands` array byte-for-byte.
                writer.WriteString("kind", "command_poll_ack");
                writer.WritePropertyName("commands");
                writer.WriteStartArray();
                foreach (var command in poll.Commands)
                {
                    writer.WriteStartObject();
                    writer.WriteString("commandId", command.CommandId);
                    writer.WriteString("commandKind", command.CommandKind);
                    writer.WritePropertyName("payload");
                    // `JsonElement.WriteTo` replays the raw JSON into the
                    // current writer without re-encoding through a managed
                    // round-trip. This is what the server emits on the wire,
                    // so cloning a payload through `WriteTo` preserves the
                    // original byte shape.
                    command.Payload.WriteTo(writer);
                    writer.WriteNumber("leaseUntil", command.LeaseUntil);
                    writer.WriteString("attemptToken", command.AttemptToken);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                break;
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Parses the ack <c>status</c> field as required by the TS schema
    /// (the base ack shape is <c>commandId + status + optional note</c>).
    /// Returns <see cref="V1AckStatus.Ok"/> when the field is absent so
    /// the most common wire case stays compact; throws on an unknown
    /// literal so a typo from the server side becomes a hard failure.
    /// </summary>
    private static V1AckStatus ParseRequiredStatus(JsonElement root)
    {
        if (!root.TryGetProperty("status", out var status))
        {
            return V1AckStatus.Ok;
        }

        if (status.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("ack payload.status must be a string (\"ok\" or \"noop\")");
        }

        return status.GetString() switch
        {
            "ok" => V1AckStatus.Ok,
            "noop" => V1AckStatus.Noop,
            _ => throw new JsonException($"Unknown V1AckStatus literal: {status.GetString() ?? "<null>"}"),
        };
    }
}
