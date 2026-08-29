using System.Text.Json;
using System.Text.Json.Serialization;

using Jellyfin.Plugin.Watchoffit.Protocol.V1;

namespace Jellyfin.Plugin.Watchoffit.Pairing;

/// <summary>
/// JSON converter that emits and parses <see cref="PairingState"/> as a
/// lower-case wire literal (e.g. <c>"paired"</c>) instead of the default
/// System.Text.Json integer form. The on-disk schema in
/// <c>docs/pairing-design.md</c> §5.2 explicitly
/// requires the string form; using the default enum converter would
/// write <c>"state": 3</c> and break every operator inspecting the file.
/// </summary>
internal sealed class PairingStateJsonConverter : JsonConverter<PairingState>
{
    /// <inheritdoc />
    public override PairingState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("PairingState must be a JSON string");
        }

        return reader.GetString() switch
        {
            "none" => PairingState.None,
            "challenge" => PairingState.Challenge,
            "handshake" => PairingState.Handshake,
            "paired" => PairingState.Paired,
            "rotating" => PairingState.Rotating,
            "revoked" => PairingState.Revoked,
            _ => throw new JsonException(
                $"Unknown PairingState literal: {reader.GetString() ?? "<null>"}"),
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, PairingState value, JsonSerializerOptions options)
    {
        var literal = value switch
        {
            PairingState.None => "none",
            PairingState.Challenge => "challenge",
            PairingState.Handshake => "handshake",
            PairingState.Paired => "paired",
            PairingState.Rotating => "rotating",
            PairingState.Revoked => "revoked",
            _ => throw new JsonException($"Unsupported PairingState: {value}"),
        };
        writer.WriteStringValue(literal);
    }
}

/// <summary>
/// DTO for <c>connection.json</c>. Mirrors the on-disk schema in
/// <c>docs/pairing-design.md</c> §5.2.
/// </summary>
/// <remarks>
/// The credential is never exposed by this record's properties — the
/// store extracts it on load and hands it back via
/// <see cref="Credential"/>, but logging/render code MUST go through
/// <see cref="DisplayCredentialMasked"/> instead of the raw value. The
/// pairing-design doc explicitly forbids logging or rendering the
/// credential value (paired-page §2.3 and §9).
/// </remarks>
public sealed record WatchoffitConnection
{
    /// <summary>Current on-disk schema version. Bumped on any breaking change.</summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = 2;

    /// <summary>Pairing state mirror. The runtime is the source of truth; this field is for operators inspecting the file.</summary>
    [JsonPropertyName("state")]
    [JsonConverter(typeof(PairingStateJsonConverter))]
    public PairingState State { get; init; } = PairingState.None;

    /// <summary>Watchoffit base URL the plugin talks to (no trailing slash).</summary>
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Server connection id assigned by Watchoffit during <c>redeem</c>.</summary>
    [JsonPropertyName("serverConnectionId")]
    public string ServerConnectionId { get; init; } = string.Empty;

    /// <summary>Watchoffit server's display name (shown in the Jellyfin dashboard).</summary>
    [JsonPropertyName("watchoffitServerName")]
    public string WatchoffitServerName { get; init; } = string.Empty;

    /// <summary>Jellyfin server id (<c>System.Id</c>) the credential is bound to.</summary>
    [JsonPropertyName("jellyfinServerId")]
    public string JellyfinServerId { get; init; } = string.Empty;

    /// <summary>Stored credential. See <see cref="DisplayCredentialMasked"/> for safe rendering.</summary>
    [JsonPropertyName("credential")]
    public WatchoffitCredential Credential { get; init; } = new();

    /// <summary>Capabilities agreed during pairing. The plugin uses this to refuse traffic that exceeds the limits.</summary>
    [JsonPropertyName("capabilities")]
    public V1Capabilities? Capabilities { get; init; }

    /// <summary>ISO 8601 UTC timestamp the connection was first created.</summary>
    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; init; } = string.Empty;

    /// <summary>ISO 8601 UTC timestamp of the last successful <c>ping</c>. May be empty before the first heartbeat.</summary>
    [JsonPropertyName("lastPingAt")]
    public string LastPingAt { get; init; } = string.Empty;

    /// <summary>Last <c>ApplicationErrorCode</c> surfaced from a failed pairing/ping attempt. <c>null</c> when healthy.</summary>
    [JsonPropertyName("lastErrorCode")]
    public string? LastErrorCode { get; init; }

    /// <summary>ISO 8601 UTC timestamp of <see cref="LastErrorCode"/>. <c>null</c> when healthy.</summary>
    [JsonPropertyName("lastErrorAt")]
    public string? LastErrorAt { get; init; }

    /// <summary>
    /// Returns a credential value safe to render in the dashboard or log.
    /// Long credentials show the first 4 and last 4 characters around an
    /// ellipsis; the partial-mask format hides at least the middle
    /// characters. Anything shorter than 12 characters is fully redacted
    /// because the "first 4 + last 4" partial would otherwise reveal
    /// the whole value (a 12-char token would lose only 4 chars to the
    /// ellipsis; a 9-char token would lose only the middle one).
    /// Empty credentials return an empty string.
    /// </summary>
    /// <returns>Masked credential string suitable for UI/log rendering.</returns>
    public string DisplayCredentialMasked()
    {
        var v = Credential.Value;
        if (string.IsNullOrEmpty(v))
        {
            return string.Empty;
        }

        if (v.Length < 12)
        {
            return "•••";
        }

        return string.Concat(v.AsSpan(0, 4), "…", v.AsSpan(v.Length - 4, 4));
    }
}

/// <summary>
/// Stored credential. The <see cref="Scheme"/> field tells the store
/// which <see cref="ICredentialProtector"/> to use; the current
/// implementation only emits <c>plain</c> (see pairing-design §11.2).
/// </summary>
public sealed record WatchoffitCredential
{
    /// <summary>Protection scheme. Must be one of <c>plain</c> or <c>dpapi</c>.</summary>
    [JsonPropertyName("scheme")]
    public string Scheme { get; init; } = "plain";

    /// <summary>Protected credential value. The raw value must never be logged.</summary>
    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;
}
