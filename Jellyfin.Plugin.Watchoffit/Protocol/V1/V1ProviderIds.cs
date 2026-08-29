using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Watchoffit.Protocol.V1;

/// <summary>
/// Provider IDs known to Jellyfin. At least one of the IDs SHOULD be
/// present on the wire so Watchoffit can resolve the item without a Jellyfin
/// round-trip.
/// </summary>
/// <remarks>
/// Each member is <c>[JsonIgnore(WhenWritingNull)]</c> so the wire
/// shape matches the TypeScript schema's <c>v1ProviderIdsSchema</c>
/// (an object of optional strings, NOT an object of nullable
/// strings). Emitting <c>"tvdb": null</c> would fail the strict
/// <c>.strict()</c> parse on the Watchoffit side.
/// </remarks>
public sealed record V1ProviderIds
{
    /// <summary>TMDB id, if Jellyfin knows it.</summary>
    [JsonPropertyName("tmdb")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tmdb { get; init; }

    /// <summary>IMDb id, if Jellyfin knows it.</summary>
    [JsonPropertyName("imdb")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Imdb { get; init; }

    /// <summary>TVDB id, if Jellyfin knows it.</summary>
    [JsonPropertyName("tvdb")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tvdb { get; init; }
}
