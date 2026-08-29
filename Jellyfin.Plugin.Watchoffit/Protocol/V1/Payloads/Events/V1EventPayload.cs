using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Events;

/// <summary>
/// Discriminated union of every v1 event payload. Mirrors
/// <c>v1EventPayloadSchema</c> in
/// <c>packages/core/src/integrations/watchoffit-plugin-protocol/v1.ts</c>.
/// The <see cref="Kind"/> literal selects the concrete shape; callers use
/// a <c>switch</c> on <see cref="Kind"/> to narrow to the matching record.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(V1PlaybackStartEvent), "playback_start")]
[JsonDerivedType(typeof(V1PlaybackProgressEvent), "playback_progress")]
[JsonDerivedType(typeof(V1PlaybackStopEvent), "playback_stop")]
[JsonDerivedType(typeof(V1UserDataEvent), "user_data")]
[JsonDerivedType(typeof(V1HeartbeatEvent), "heartbeat")]
[JsonDerivedType(typeof(V1InventoryManifestEvent), "inventory_manifest")]
[JsonDerivedType(typeof(V1SyncCompletedEvent), "sync_completed")]
public abstract record V1EventPayload
{
    /// <summary>Discriminator literal. One of <c>playback_start</c>, <c>playback_progress</c>, <c>playback_stop</c>, <c>user_data</c>, <c>heartbeat</c>, <c>inventory_manifest</c>, <c>sync_completed</c>.</summary>
    [JsonPropertyName("kind")]
    public abstract string Kind { get; }
}

/// <summary>
/// Item-level identity fields shared by every event payload. The fields
/// are flat on the wire (no nested <c>identity</c> object) because the
/// TypeScript schema uses <c>v1IdentitySchema.extend({...}).strict()</c>.
/// </summary>
public abstract record V1ItemIdentityEvent : V1EventPayload
{
    /// <summary>Jellyfin item identifier as exposed by the plugin.</summary>
    [JsonPropertyName("jellyfinItemId")]
    public required string JellyfinItemId { get; init; }

    /// <summary>Watchoffit user identifier.</summary>
    [JsonPropertyName("watchoffitUserId")]
    public required string WatchoffitUserId { get; init; }

    /// <summary>Provider-neutral media kind.</summary>
    [JsonPropertyName("mediaKind")]
    public required V1MediaKind MediaKind { get; init; }

    /// <summary>
    /// Optional provider IDs (TMDB/IMDb/TVDB). The
    /// <c>WhenWritingNull</c> ignore matches the TypeScript schema's
    /// <c>optional</c> rather than <c>nullable</c>: events without a
    /// provider map omit the field entirely instead of emitting
    /// <c>"providerIds": null</c>.
    /// </summary>
    [JsonPropertyName("providerIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public V1ProviderIds? ProviderIds { get; init; }
}

/// <summary>Common playback block shared by the three playback events.</summary>
public abstract record V1PlaybackEvent : V1ItemIdentityEvent
{
    /// <summary>Session id from Jellyfin. Used to deduplicate parallel progress bursts.</summary>
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    /// <summary>Position in Jellyfin ticks (1 tick = 100ns). Watchoffit normalizes on read.</summary>
    [JsonPropertyName("positionTicks")]
    public required long PositionTicks { get; init; }

    /// <summary>Runtime in Jellyfin ticks. Required for progress; 0 is allowed for start.</summary>
    [JsonPropertyName("runtimeTicks")]
    public required long RuntimeTicks { get; init; }
}

/// <summary>Playback started. Emitted once per session per item.</summary>
public sealed record V1PlaybackStartEvent : V1PlaybackEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Kind => "playback_start";

    /// <summary>ISO 8601 timestamp Jellyfin recorded for the start.</summary>
    [JsonPropertyName("startedAt")]
    public required string StartedAt { get; init; }
}

/// <summary>Periodic progress sample. The plugin coalesces these to one event per ~10 s.</summary>
public sealed record V1PlaybackProgressEvent : V1PlaybackEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Kind => "playback_progress";

    /// <summary><c>true</c> when playback is paused (Jellyfin's "Paused" flag).</summary>
    [JsonPropertyName("isPaused")]
    public required bool IsPaused { get; init; }
}

/// <summary>Playback ended. Carries whether the item finished.</summary>
public sealed record V1PlaybackStopEvent : V1PlaybackEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Kind => "playback_stop";

    /// <summary><c>true</c> if Jellyfin reports the item as fully played.</summary>
    [JsonPropertyName("playedToCompletion")]
    public required bool PlayedToCompletion { get; init; }

    /// <summary>ISO 8601 timestamp Jellyfin recorded for the stop.</summary>
    [JsonPropertyName("stoppedAt")]
    public required string StoppedAt { get; init; }
}

/// <summary>
/// Mirror of Jellyfin's <c>UserDataSaved</c>. Sent when the user changes
/// the played/favorite state from the web UI rather than from a playback
/// event.
/// </summary>
public sealed record V1UserDataEvent : V1ItemIdentityEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Kind => "user_data";

    /// <summary>Played state after the change.</summary>
    [JsonPropertyName("played")]
    public required bool Played { get; init; }

    /// <summary>Play count after the change.</summary>
    [JsonPropertyName("playCount")]
    public required int PlayCount { get; init; }

    /// <summary>Optional favorite flag.</summary>
    [JsonPropertyName("isFavorite")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsFavorite { get; init; }

    /// <summary>
    /// ISO 8601 timestamp of the last play, or <c>null</c> if never.
    /// The TypeScript schema declares this as <c>nullable()</c>
    /// rather than <c>optional()</c>, so the wire form
    /// <c>"lastPlayedAt": null</c> is valid and round-trips on the
    /// Watchoffit side. We emit <c>null</c> explicitly when there is no
    /// last-played timestamp to keep the field shape stable across
    /// the v1 channel.
    /// </summary>
    [JsonPropertyName("lastPlayedAt")]
    public string? LastPlayedAt { get; init; }
}

/// <summary>
/// Periodic liveness signal from the plugin. Carries the current queue
/// depth and the highest seen monotonic <c>sequence</c> so Watchoffit can
/// detect gaps.
/// </summary>
public sealed record V1HeartbeatEvent : V1ItemIdentityEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Kind => "heartbeat";

    /// <summary>Outbound queue depth inside the plugin.</summary>
    [JsonPropertyName("queueDepth")]
    public required int QueueDepth { get; init; }

    /// <summary>Highest monotonic <c>sequence</c> the plugin has emitted so far.</summary>
    [JsonPropertyName("lastSequence")]
    public required long LastSequence { get; init; }

    /// <summary>Plugin version string (matches <c>meta.json</c>).</summary>
    [JsonPropertyName("pluginVersion")]
    public required string PluginVersion { get; init; }
}

/// <summary>Complete discovered Jellyfin server inventory.</summary>
public sealed record V1InventoryManifestEvent : V1EventPayload
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Kind => "inventory_manifest";

    /// <summary>Inventory provider literal.</summary>
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    /// <summary>Nonnegative inventory generation.</summary>
    [JsonPropertyName("generation")]
    public required long Generation { get; init; }

    /// <summary>ISO 8601 capture timestamp.</summary>
    [JsonPropertyName("capturedAt")]
    public required string CapturedAt { get; init; }

    /// <summary>Zero-based nonnegative chunk index.</summary>
    [JsonPropertyName("chunkIndex")]
    public required int ChunkIndex { get; init; }

    /// <summary>Nonnegative number of chunks.</summary>
    [JsonPropertyName("chunkCount")]
    public required int ChunkCount { get; init; }

    /// <summary>Discovered server details.</summary>
    [JsonPropertyName("server")]
    public required V1InventoryServer Server { get; init; }

    /// <summary>Discovered users.</summary>
    [JsonPropertyName("users")]
    public required IReadOnlyList<V1InventoryUser> Users { get; init; }

    /// <summary>Discovered libraries.</summary>
    [JsonPropertyName("libraries")]
    public required IReadOnlyList<V1InventoryLibrary> Libraries { get; init; }

    /// <summary>User-to-library access entries.</summary>
    [JsonPropertyName("userLibraries")]
    public required IReadOnlyList<V1InventoryUserLibrary> UserLibraries { get; init; }
}

/// <summary>Server details included in an inventory manifest.</summary>
public sealed record V1InventoryServer
{
    /// <summary>Remote server identifier.</summary>
    [JsonPropertyName("remoteServerId")]
    public required string RemoteServerId { get; init; }

    /// <summary>Server display name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Jellyfin version.</summary>
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    /// <summary>Watchoffit plugin version.</summary>
    [JsonPropertyName("pluginVersion")]
    public required string PluginVersion { get; init; }
}

/// <summary>User details included in an inventory manifest.</summary>
public sealed record V1InventoryUser
{
    /// <summary>Remote user identifier.</summary>
    [JsonPropertyName("remoteUserId")]
    public required string RemoteUserId { get; init; }

    /// <summary>User display name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Whether the user is an administrator.</summary>
    [JsonPropertyName("isAdministrator")]
    public required bool IsAdministrator { get; init; }

    /// <summary>Whether the user is disabled.</summary>
    [JsonPropertyName("isDisabled")]
    public required bool IsDisabled { get; init; }
}

/// <summary>Library details included in an inventory manifest.</summary>
public sealed record V1InventoryLibrary
{
    /// <summary>Remote library identifier.</summary>
    [JsonPropertyName("remoteLibraryId")]
    public required string RemoteLibraryId { get; init; }

    /// <summary>Library display name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Jellyfin collection type.</summary>
    [JsonPropertyName("collectionType")]
    public required string CollectionType { get; init; }
}

/// <summary>User-to-library access entry.</summary>
public sealed record V1InventoryUserLibrary
{
    /// <summary>Remote user identifier.</summary>
    [JsonPropertyName("remoteUserId")]
    public required string RemoteUserId { get; init; }

    /// <summary>Remote library identifier.</summary>
    [JsonPropertyName("remoteLibraryId")]
    public required string RemoteLibraryId { get; init; }
}

/// <summary>
/// Lifecycle event emitted by the plugin when a <c>backfill_request</c>
/// or <c>reconcile_request</c> walk finishes. Watchoffit uses the totals
/// to close the matching <c>jellyfinSyncRuns</c> row.
/// </summary>
public sealed record V1SyncCompletedEvent : V1EventPayload
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Kind => "sync_completed";

    /// <summary>Jellyfin user GUID, not the Watchoffit uuid.</summary>
    [JsonPropertyName("watchoffitUserId")]
    public required string WatchoffitUserId { get; init; }

    /// <summary>Which sync this closes: backfill or reconcile.</summary>
    [JsonPropertyName("syncKind")]
    public required V1SyncKind SyncKind { get; init; }

    /// <summary>
    /// Jellyfin library id the plugin just finished. Present for
    /// per-library walks (the common path); omitted for the "all
    /// libraries" admin backfill.
    /// </summary>
    [JsonPropertyName("libraryId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LibraryId { get; init; }

    /// <summary>Total items the plugin discovered in the walk.</summary>
    [JsonPropertyName("total")]
    public required int Total { get; init; }

    /// <summary><c>user_data</c> events the plugin successfully enqueued.</summary>
    [JsonPropertyName("processed")]
    public required int Processed { get; init; }

    /// <summary>Items the plugin tried to enqueue but the durable outbox rejected.</summary>
    [JsonPropertyName("failed")]
    public required int Failed { get; init; }

    /// <summary>ISO 8601 UTC instant the walk finished on the plugin side.</summary>
    [JsonPropertyName("completedAt")]
    public required string CompletedAt { get; init; }
}

/// <summary>Which kind of sync the <see cref="V1SyncCompletedEvent"/> is closing.</summary>
[JsonConverter(typeof(V1SyncKindJsonConverter))]
public enum V1SyncKind
{
    /// <summary>Closes a <c>backfill_request</c> walk.</summary>
    Backfill,

    /// <summary>Closes a <c>reconcile_request</c> walk.</summary>
    Reconcile,
}

/// <summary>
/// JSON converter for <see cref="V1SyncKind"/>. Uses the wire literals
/// (<c>"backfill"</c>, <c>"reconcile"</c>) directly so the round-trip
/// matches the TypeScript schema byte-for-byte.
/// </summary>
internal sealed class V1SyncKindJsonConverter : JsonConverter<V1SyncKind>
{
    /// <inheritdoc />
    public override V1SyncKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("V1SyncKind must be a JSON string");
        }

        var value = reader.GetString();
        return value switch
        {
            "backfill" => V1SyncKind.Backfill,
            "reconcile" => V1SyncKind.Reconcile,
            _ => throw new JsonException($"Unknown V1SyncKind literal: {value ?? "<null>"}"),
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, V1SyncKind value, JsonSerializerOptions options)
    {
        var literal = value switch
        {
            V1SyncKind.Backfill => "backfill",
            V1SyncKind.Reconcile => "reconcile",
            _ => throw new JsonException($"Unsupported V1SyncKind: {value}"),
        };
        writer.WriteStringValue(literal);
    }
}
