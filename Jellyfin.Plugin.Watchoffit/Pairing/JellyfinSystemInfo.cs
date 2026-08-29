using MediaBrowser.Common;

namespace Jellyfin.Plugin.Watchoffit.Pairing;

/// <summary>
/// Snapshot of the local Jellyfin server identity the plugin needs to
/// put on the wire during pairing. Decoupled from the live
/// <c>IApplicationHost</c> so it can be stubbed in unit tests.
/// </summary>
/// <param name="JellyfinServerId">Jellyfin <c>System.Id</c> — stable across the lifetime of the server.</param>
/// <param name="JellyfinVersion">Jellyfin server version, dotted (<c>10.11.11</c>).</param>
public sealed record JellyfinSystemInfo(string JellyfinServerId, string JellyfinVersion);

/// <summary>
/// Source of the local Jellyfin server identity. The default
/// implementation reads from <c>IApplicationHost</c>; tests use the
/// in-memory variant.
/// </summary>
public interface IJellyfinSystemInfoProvider
{
    /// <summary>Returns the current local server identity. Must be safe to call from any thread.</summary>
    /// <returns>The Jellyfin <c>System.Id</c> + version.</returns>
    JellyfinSystemInfo GetCurrent();
}

/// <summary>
/// Production <see cref="IJellyfinSystemInfoProvider"/> backed by
/// Jellyfin's <c>IApplicationHost</c>. The values are cached on
/// construction because <c>System.Id</c> is stable for the server's
/// lifetime and reading it on every pairing flow would be wasteful.
/// </summary>
public sealed class LiveJellyfinSystemInfoProvider : IJellyfinSystemInfoProvider
{
    private readonly JellyfinSystemInfo _cached;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveJellyfinSystemInfoProvider"/> class.
    /// </summary>
    /// <param name="applicationHost">Jellyfin's <c>IApplicationHost</c>. Both <c>SystemId</c> and the server version are read once.</param>
    public LiveJellyfinSystemInfoProvider(IApplicationHost applicationHost)
    {
        ArgumentNullException.ThrowIfNull(applicationHost);
        _cached = new JellyfinSystemInfo(applicationHost.SystemId, applicationHost.ApplicationVersionString);
    }

    /// <inheritdoc />
    public JellyfinSystemInfo GetCurrent() => _cached;
}

/// <summary>In-memory <see cref="IJellyfinSystemInfoProvider"/> for tests.</summary>
public sealed class StaticJellyfinSystemInfoProvider : IJellyfinSystemInfoProvider
{
    private readonly JellyfinSystemInfo _info;

    /// <summary>Initializes a new instance with a fixed identity.</summary>
    /// <param name="serverId">Jellyfin <c>System.Id</c>.</param>
    /// <param name="version">Jellyfin server version.</param>
    public StaticJellyfinSystemInfoProvider(string serverId, string version)
    {
        _info = new JellyfinSystemInfo(serverId, version);
    }

    /// <inheritdoc />
    public JellyfinSystemInfo GetCurrent() => _info;
}
