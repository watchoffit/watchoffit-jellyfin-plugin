using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Watchoffit.Protocol.V1;

/// <summary>
/// Server-level identity block for every pairing command. Mirrors
/// <c>v1JellyfinServerIdentitySchema</c> in
/// <c>packages/core/src/integrations/watchoffit-plugin-protocol/v1.ts</c>.
/// </summary>
/// <remarks>
/// Pairing is bound to a Jellyfin server identity, not a media item, so
/// pairing commands use this record in place of <see cref="V1Identity"/>.
/// The <see cref="PluginGuid"/> must match the plugin's static
/// <c>WatchoffitPlugin.Id</c> so Watchoffit can refuse impersonator traffic.
/// </remarks>
public sealed record V1JellyfinServerIdentity
{
    /// <summary>
    /// Jellyfin server id (<c>System.Id</c> in Jellyfin's <c>SystemInfo</c>).
    /// Stable across the lifetime of the Jellyfin server's library.
    /// </summary>
    [JsonPropertyName("jellyfinServerId")]
    public required string JellyfinServerId { get; init; }

    /// <summary>
    /// Jellyfin server version, dotted (<c>10.11.11</c>).
    /// </summary>
    [JsonPropertyName("jellyfinVersion")]
    public required string JellyfinVersion { get; init; }

    /// <summary>
    /// Plugin version, dotted (<c>1.0.0.0</c>).
    /// </summary>
    [JsonPropertyName("pluginVersion")]
    public required string PluginVersion { get; init; }

    /// <summary>
    /// Plugin's static UUIDv5 id. Must match <c>WatchoffitPlugin.Id</c>.
    /// </summary>
    [JsonPropertyName("pluginGuid")]
    public required Guid PluginGuid { get; init; }
}
