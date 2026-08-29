using System.Text.Json;
using System.Text.Json.Serialization;

using Jellyfin.Plugin.Watchoffit.Protocol.V1.JsonConverters;

namespace Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Commands;

/// <summary>Reason codes accepted by <see cref="V1ReconcileRequestCommand"/>.</summary>
[JsonConverter(typeof(V1ReconcileReasonJsonConverter))]
public enum V1ReconcileReason
{
    /// <summary>No ack arrived within the expected window.</summary>
    MissedAck,

    /// <summary>Replayed after Watchoffit restarted.</summary>
    PostRestart,

    /// <summary>Operator-triggered reconcile.</summary>
    Manual,
}

internal sealed class V1ReconcileReasonJsonConverter : JsonConverter<V1ReconcileReason>
{
    public override V1ReconcileReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("V1ReconcileReason must be a JSON string");
        }

        var value = reader.GetString();
        return value switch
        {
            "missed_ack" => V1ReconcileReason.MissedAck,
            "post_restart" => V1ReconcileReason.PostRestart,
            "manual" => V1ReconcileReason.Manual,
            _ => throw new JsonException($"Unknown V1ReconcileReason literal: {value ?? "<null>"}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, V1ReconcileReason value, JsonSerializerOptions options)
    {
        var literal = value switch
        {
            V1ReconcileReason.MissedAck => "missed_ack",
            V1ReconcileReason.PostRestart => "post_restart",
            V1ReconcileReason.Manual => "manual",
            _ => throw new JsonException($"Unsupported V1ReconcileReason: {value}"),
        };
        writer.WriteStringValue(literal);
    }
}

/// <summary>
/// Discriminated union of every v1 command payload. Mirrors
/// <c>v1CommandPayloadSchema</c> in
/// <c>packages/core/src/integrations/watchoffit-plugin-protocol/v1.ts</c>.
/// The <see cref="Kind"/> literal selects the concrete shape; callers use
/// a <c>switch</c> on <see cref="Kind"/> to narrow to the matching record.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(V1MarkPlayedCommand), "mark_played")]
[JsonDerivedType(typeof(V1MarkUnplayedCommand), "mark_unplayed")]
[JsonDerivedType(typeof(V1PingCommand), "ping")]
[JsonDerivedType(typeof(V1ReconcileRequestCommand), "reconcile_request")]
[JsonDerivedType(typeof(V1BackfillRequestCommand), "backfill_request")]
[JsonDerivedType(typeof(V1RotateCredentialCommand), "rotate_credential")]
[JsonDerivedType(typeof(V1ChallengeRequestCommand), "challenge_request")]
[JsonDerivedType(typeof(V1RedeemRequestCommand), "redeem_request")]
[JsonDerivedType(typeof(V1RevokeRequestCommand), "revoke_request")]
public abstract record V1CommandPayload
{
    /// <summary>Discriminator literal. One of <c>mark_played</c>, <c>mark_unplayed</c>, <c>ping</c>, <c>reconcile_request</c>, <c>backfill_request</c>, <c>rotate_credential</c>, <c>challenge_request</c>, <c>redeem_request</c>, <c>revoke_request</c>.</summary>
    [JsonPropertyName("kind")]
    public abstract string Kind { get; }

    /// <summary>Marker for the pairing-flow commands (challenge/redeem/revoke).</summary>
    [JsonIgnore]
    public bool IsPairingCommand => Kind is "challenge_request" or "redeem_request" or "revoke_request";
}

/// <summary>
/// Item-level identity fields shared by <see cref="V1MarkPlayedCommand"/>,
/// <see cref="V1MarkUnplayedCommand"/>, <see cref="V1PingCommand"/>, and
/// <see cref="V1ReconcileRequestCommand"/>. The fields are flat on the
/// wire (no nested <c>identity</c> object) because the TypeScript schema
/// uses <c>v1IdentitySchema.extend({...}).strict()</c>.
/// </summary>
public abstract record V1ItemIdentityCommand : V1CommandPayload
{
    /// <summary>Jellyfin item identifier as exposed by the plugin.</summary>
    [JsonPropertyName("jellyfinItemId")]
    public required string JellyfinItemId { get; init; }

    /// <summary>Watchoffit user identifier. UUIDv7 in Watchoffit, GUID in Jellyfin.</summary>
    [JsonPropertyName("watchoffitUserId")]
    public required string WatchoffitUserId { get; init; }

    /// <summary>Provider-neutral media kind.</summary>
    [JsonPropertyName("mediaKind")]
    public required V1MediaKind MediaKind { get; init; }

    /// <summary>Optional provider IDs (TMDB/IMDb/TVDB).</summary>
    [JsonPropertyName("providerIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public V1ProviderIds? ProviderIds { get; init; }
}

/// <summary>
/// Server-level identity fields shared by <see cref="V1ChallengeRequestCommand"/>
/// and <see cref="V1RevokeRequestCommand"/>. Mirrors
/// <c>v1JellyfinServerIdentitySchema</c>.
/// </summary>
public abstract record V1ServerIdentityCommand : V1CommandPayload
{
    /// <summary>Jellyfin server id (<c>System.Id</c>).</summary>
    [JsonPropertyName("jellyfinServerId")]
    public required string JellyfinServerId { get; init; }

    /// <summary>Jellyfin server version, dotted (<c>10.11.11</c>).</summary>
    [JsonPropertyName("jellyfinVersion")]
    public required string JellyfinVersion { get; init; }

    /// <summary>Plugin version, dotted (<c>1.0.0.0</c>).</summary>
    [JsonPropertyName("pluginVersion")]
    public required string PluginVersion { get; init; }

    /// <summary>Plugin's static UUIDv5 id. Must match <c>WatchoffitPlugin.Id</c>.</summary>
    [JsonPropertyName("pluginGuid")]
    public required Guid PluginGuid { get; init; }
}

/// <summary>
/// Mark one item as played. Jellyfin mirrors Watchoffit's intent; the watch
/// history is owned by Watchoffit, the on-disk state by Jellyfin.
/// </summary>
public sealed record V1MarkPlayedCommand : V1ItemIdentityCommand
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Kind => "mark_played";

    /// <summary>Optional ISO timestamp; if omitted Jellyfin uses <c>Now</c>.</summary>
    [JsonPropertyName("watchedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WatchedAt { get; init; }
}

/// <summary>Inverse of <see cref="V1MarkPlayedCommand"/>.</summary>
public sealed record V1MarkUnplayedCommand : V1ItemIdentityCommand
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Kind => "mark_unplayed";
}

/// <summary>
/// Health probe. The plugin replies with an <c>ack</c> carrying the
/// server's current monotonic clock so Watchoffit can compute RTT and detect
/// clock drift.
/// </summary>
public sealed record V1PingCommand : V1ItemIdentityCommand
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Kind => "ping";

    /// <summary>Opaque nonce echoed back in the ack payload.</summary>
    [JsonPropertyName("nonce")]
    public required string Nonce { get; init; }
}

/// <summary>
/// Ask Jellyfin to re-emit a recent <c>user_data</c> snapshot for this
/// item. Used after a missed ack or after Watchoffit restarts to reconcile state.
/// </summary>
public sealed record V1ReconcileRequestCommand : V1ItemIdentityCommand
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Kind => "reconcile_request";

    /// <summary>Why the reconcile was requested.</summary>
    [JsonPropertyName("reason")]
    public required V1ReconcileReason Reason { get; init; }
}

/// <summary>
/// User-scoped identity block shared by <see cref="V1BackfillRequestCommand"/>
/// and the matching <c>sync_completed</c> event. Mirrors
/// <c>v1UserScopeSchema</c> in
/// <c>packages/core/src/integrations/watchoffit-plugin-protocol/v1.ts</c>.
/// </summary>
public abstract record V1UserScopeCommand : V1CommandPayload
{
    /// <summary>Jellyfin user GUID, not the Watchoffit uuid.</summary>
    [JsonPropertyName("watchoffitUserId")]
    public required string WatchoffitUserId { get; init; }
}

/// <summary>
/// Ask Jellyfin to walk one library (or every library the user can access
/// when <see cref="LibraryId"/> is omitted) and emit a <c>user_data</c>
/// event per item the user has played. This is the durable replacement
/// for Watchoffit's legacy admin-key <c>backfillLibraries</c> call: progress
/// is signaled by the <c>user_data</c> events themselves, the run is
/// closed by a single <c>sync_completed</c> event when the walk finishes.
/// </summary>
public sealed record V1BackfillRequestCommand : V1UserScopeCommand
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Kind => "backfill_request";

    /// <summary>
    /// Jellyfin library id. When omitted, the plugin walks every
    /// library the user can access.
    /// </summary>
    [JsonPropertyName("libraryId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LibraryId { get; init; }

    /// <summary>
    /// Restrict the walk to these media kinds. When omitted the
    /// plugin defaults to <c>["movie", "episode"]</c>.
    /// </summary>
    [JsonPropertyName("mediaKinds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<V1MediaKind>? MediaKinds { get; init; }
}

/// <summary>
/// Internal v1 command used to deliver a rotated credential. The new
/// credential is returned in the ack's <c>newCredential</c> field.
/// </summary>
public sealed record V1RotateCredentialCommand : V1CommandPayload
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Kind => "rotate_credential";
}

/// <summary>
/// Step 1 of the pairing flow. The plugin binds a Jellyfin server identity
/// to a Watchoffit-side <c>serverConnectionId</c> and receives a one-time
/// pairing code + expiry.
/// </summary>
public sealed record V1ChallengeRequestCommand : V1ServerIdentityCommand
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Kind => "challenge_request";
}

/// <summary>
/// Step 2 of the pairing flow. The plugin redeems a single-use pairing
/// code minted by Watchoffit and receives a long-lived opaque credential.
/// </summary>
public sealed record V1RedeemRequestCommand : V1CommandPayload
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Kind => "redeem_request";

    /// <summary>The one-time pairing code shown in the Jellyfin dashboard.</summary>
    [JsonPropertyName("pairingCode")]
    [JsonConverter(typeof(V1PairingCodeJsonConverter))]
    public required string PairingCode { get; init; }

    /// <summary>Jellyfin server id the credential is bound to.</summary>
    [JsonPropertyName("jellyfinServerId")]
    public required string JellyfinServerId { get; init; }
}

/// <summary>
/// Step 3 of the pairing flow. The plugin revokes its current credential
/// on Watchoffit and then drops the local <c>connection.json</c>.
/// </summary>
public sealed record V1RevokeRequestCommand : V1ServerIdentityCommand
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Kind => "revoke_request";
}
