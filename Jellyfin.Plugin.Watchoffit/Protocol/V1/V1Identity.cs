using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Watchoffit.Protocol.V1;

/// <summary>
/// Common identity block for every item-level command and event. Mirrors
/// <c>v1IdentitySchema</c> in
/// <c>packages/core/src/integrations/watchoffit-plugin-protocol/v1.ts</c>.
/// </summary>
/// <remarks>
/// Pairing commands do NOT extend this record; they use
/// <see cref="V1JellyfinServerIdentity"/> instead, because pairing is bound
/// to a Jellyfin server identity rather than a media item.
/// </remarks>
public sealed record V1Identity
{
    /// <summary>
    /// Jellyfin item identifier as exposed by the plugin. Stable across the
    /// lifetime of the Jellyfin server's library.
    /// </summary>
    [JsonPropertyName("jellyfinItemId")]
    public required string JellyfinItemId { get; init; }

    /// <summary>
    /// Watchoffit user identifier. UUIDv7 in Watchoffit, GUID in Jellyfin. The
    /// plugin must pass the Watchoffit-side id through unchanged after user
    /// mapping has been performed.
    /// </summary>
    [JsonPropertyName("watchoffitUserId")]
    public required string WatchoffitUserId { get; init; }

    /// <summary>
    /// Provider-neutral media kind. Only <c>movie</c> and <c>episode</c> are in v1.
    /// </summary>
    [JsonPropertyName("mediaKind")]
    public required V1MediaKind MediaKind { get; init; }

    /// <summary>
    /// Optional provider IDs (TMDB/IMDb/TVDB). At least one SHOULD be
    /// present so Watchoffit can resolve the item without a Jellyfin round-trip.
    /// </summary>
    [JsonPropertyName("providerIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public V1ProviderIds? ProviderIds { get; init; }
}
