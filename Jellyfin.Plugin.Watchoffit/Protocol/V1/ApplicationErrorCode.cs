namespace Jellyfin.Plugin.Watchoffit.Protocol.V1;

/// <summary>
/// Recommended application-level error codes for the <c>error</c> envelope's
/// <c>payload.code</c> field. The protocol accepts any
/// <c>[A-Z0-9_]{1,64}</c> string on the wire; this enum is a strict subset
/// that both ends of the protocol SHOULD use so dashboards and dedup rules
/// stay in sync.
///
/// Parser and transport errors use <see cref="SafeErrorCode"/> and are
/// returned by <see cref="V1EnvelopeParser"/> only — they never appear in
/// the <c>error</c> envelope's payload.
/// </summary>
public enum ApplicationErrorCode
{
    /// <summary>No matching Jellyfin item was found for the given identity.</summary>
    ItemNotFound,

    /// <summary>The Jellyfin item is known but has no TMDB/IMDb/TVDB id that Watchoffit can resolve.</summary>
    ItemUnresolved,

    /// <summary>No Watchoffit ↔ Jellyfin user mapping exists for the supplied user id.</summary>
    UserNotMapped,

    /// <summary>The Jellyfin library is excluded by the operator's persistent rules.</summary>
    LibraryExcluded,

    /// <summary>The action was a no-op because the target state already matches the request.</summary>
    AlreadyApplied,

    /// <summary>The Watchoffit outbox is at capacity and the change could not be enqueued.</summary>
    OutboxFull,

    /// <summary>Jellyfin is throttling the plugin (HTTP 429 with a <c>Retry-After</c> header).</summary>
    RateLimitedByRemote,

    /// <summary>The one-time pairing code did not match the issued value.</summary>
    InvalidPairingCode,

    /// <summary>The pairing code's TTL elapsed before the plugin could redeem it.</summary>
    PairingCodeExpired,

    /// <summary>The pairing code was already consumed by a prior request.</summary>
    PairingCodeAlreadyUsed,

    /// <summary>The Jellyfin server version is below the minimum required by the protocol.</summary>
    JellyfinVersionUnsupported,

    /// <summary>The plugin's <c>pluginGuid</c> does not match the value Watchoffit expects.</summary>
    JellyfinPluginGuidMismatch,

    /// <summary>The credential was rejected as revoked or otherwise invalid.</summary>
    CredentialRevoked,

    /// <summary>The plugin could not persist the new credential to disk.</summary>
    CredentialWriteFailed,
}
