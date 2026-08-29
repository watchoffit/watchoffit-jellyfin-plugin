using System.Globalization;
using System.Text.Json;

using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Watchoffit.Commands;
using Jellyfin.Plugin.Watchoffit.Pairing;
using Jellyfin.Plugin.Watchoffit.Protocol.V1;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Events;

using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;

using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Watchoffit.Events;

/// <summary>
/// Translates Jellyfin's session events into v1 event envelopes and
/// persists them to the durable v1 outbox. Mirrors the inbound side
/// of <c>processV1Event</c> in
/// <c>packages/core/src/integrations/watchoffit-plugin-protocol/event-processor.ts</c>;
/// the wire-format spec is <c>docs/protocol-v1.md</c> §4.2.
/// </summary>
/// <remarks>
/// Lifecycle: <see cref="Attach"/> subscribes to the Jellyfin event
/// bus and starts queueing; <see cref="Detach"/> unsubscribes. The
/// class is single-instance per plugin install; the DI container
/// registers it as a singleton.
///
/// The forwarder holds the per-install credential via the
/// <see cref="PairingService"/>'s current connection snapshot — no
/// credential material is cached locally. When the plugin is not
/// paired the forwarder does not enqueue events because they cannot be bound
/// to a valid server connection. Network delivery is owned exclusively by
/// <see cref="EventOutboxWorker"/>.
///
/// The event payload's <c>watchoffitUserId</c> is currently the Jellyfin
/// user's stable id, pending the Phase 6 Watchoffit-to-Jellyfin user map.
/// Events without a Jellyfin user are dropped: <c>"system"</c> is a
/// heartbeat-only identity and is not a valid item-event user.
/// </remarks>
public sealed class EventForwarder : IDisposable
{
    private readonly ISessionManager _sessionManager;
    private readonly IUserDataManager _userDataManager;
    private readonly WatchoffitClient _client;
    private readonly PairingService _pairing;
    private readonly DurableEventOutbox _outbox;
    private readonly ICommandCausationContext _causationContext;
    private readonly ILogger _logger;

    private bool _attached;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventForwarder"/> class.
    /// </summary>
    /// <param name="sessionManager">Jellyfin session manager; source of the playback events.</param>
    /// <param name="userDataManager">Jellyfin user-data manager; source of the <c>UserDataSaved</c> events.</param>
    /// <param name="client">Envelope factory shared with the v1 client.</param>
    /// <param name="pairing">Pairing state; supplies the credential, baseUrl, and serverConnectionId.</param>
    /// <param name="outbox">Durable queue that receives fully built event envelopes.</param>
    /// <param name="causationContext">Command causation context used to tag command-triggered events.</param>
    /// <param name="logger">Plugin logger. Queue failures are logged without leaking credentials.</param>
    public EventForwarder(
        ISessionManager sessionManager,
        IUserDataManager userDataManager,
        WatchoffitClient client,
        PairingService pairing,
        DurableEventOutbox outbox,
        ICommandCausationContext causationContext,
        ILogger<EventForwarder> logger)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _userDataManager = userDataManager ?? throw new ArgumentNullException(nameof(userDataManager));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _pairing = pairing ?? throw new ArgumentNullException(nameof(pairing));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _causationContext = causationContext ?? throw new ArgumentNullException(nameof(causationContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Subscribe to Jellyfin's event bus. Idempotent.</summary>
    public void Attach()
    {
        if (_attached)
        {
            return;
        }

        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _userDataManager.UserDataSaved += OnUserDataSaved;
        _attached = true;
        _logger.LogInformation("EventForwarder attached to Jellyfin event bus");
    }

    /// <summary>Unsubscribe from Jellyfin's event bus. Idempotent.</summary>
    public void Detach()
    {
        if (!_attached)
        {
            return;
        }

        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        _attached = false;
        _logger.LogInformation("EventForwarder detached from Jellyfin event bus");
    }

    /// <inheritdoc />
    public void Dispose() => Detach();

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            Queue(BuildPlaybackStartEnvelope(e));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EventForwarder.OnPlaybackStart failed");
        }
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            Queue(BuildPlaybackProgressEnvelope(e));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EventForwarder.OnPlaybackProgress failed");
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            Queue(BuildPlaybackStopEnvelope(e));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EventForwarder.OnPlaybackStopped failed");
        }
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        try
        {
            Queue(BuildUserDataEnvelope(e));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EventForwarder.OnUserDataSaved failed");
        }
    }

    private void Queue(V1EventEnvelope? envelope)
    {
        if (envelope is null)
        {
            return;
        }

        if (string.Equals(
                envelope.Header.ServerConnectionId,
                V1EnvelopeBuilder.PendingServerConnectionId,
                StringComparison.Ordinal))
        {
            _logger.LogDebug("Watchoffit event {EnvelopeId} ignored because the plugin is not paired", envelope.Header.Id);
            return;
        }

        var result = _outbox.TryEnqueue(envelope);
        if (result == EventOutboxEnqueueResult.Full)
        {
            _logger.LogError(
                "Watchoffit event queue is full; event {EnvelopeId} sequence {Sequence} was not persisted",
                envelope.Header.Id,
                envelope.Header.Sequence);
        }
    }

    /// <summary>
    /// Build the v1 envelope for a <c>PlaybackStart</c> event. Visible
    /// to the test project so the translation can be unit-tested
    /// without exercising the full event-bus path.
    /// </summary>
    /// <param name="e">Jellyfin's playback-progress event args (used for both PlaybackStart and PlaybackProgress).</param>
    /// <returns>A populated event envelope, or <c>null</c> when the args lack a usable item identity.</returns>
    internal V1EventEnvelope? BuildPlaybackStartEnvelope(PlaybackProgressEventArgs e)
    {
        var (jellyfinItemId, mediaKind, providerIds) = ExtractIdentity(e.Item, e.MediaInfo);
        if (jellyfinItemId is null || mediaKind is null)
        {
            return null;
        }

        var sessionId = ExtractSessionId(e.Session?.Id ?? e.PlaySessionId);
        if (sessionId is null)
        {
            return null;
        }

        var watchoffitUserId = ExtractWatchoffitUserId(e.Users);
        if (watchoffitUserId is null)
        {
            return null;
        }

        var runtimeTicks = PlaybackRuntimeTicks(e.Item, e.MediaInfo);

        var header = _client.BuildEventHeader(
            serverConnectionId: CurrentServerConnectionId() ?? V1EnvelopeBuilder.PendingServerConnectionId,
            idKindPrefix: "evt_playback_start",
            correlationId: CurrentCausationId());

        var payload = new V1PlaybackStartEvent
        {
            JellyfinItemId = jellyfinItemId,
            WatchoffitUserId = watchoffitUserId,
            MediaKind = mediaKind.Value,
            ProviderIds = providerIds,
            SessionId = sessionId,
            PositionTicks = NonNegativeTicks(e.PlaybackPositionTicks),
            RuntimeTicks = runtimeTicks,
            StartedAt = FormatTimestamp(DateTime.UtcNow),
        };
        return new V1EventEnvelope { Header = header, Payload = payload };
    }

    /// <summary>
    /// Build the v1 envelope for a <c>PlaybackProgress</c> event. Visible
    /// to the test project.
    /// </summary>
    /// <param name="e">Jellyfin's playback-progress event args.</param>
    /// <returns>A populated event envelope, or <c>null</c> when the args lack a usable item identity.</returns>
    internal V1EventEnvelope? BuildPlaybackProgressEnvelope(PlaybackProgressEventArgs e)
    {
        var (jellyfinItemId, mediaKind, providerIds) = ExtractIdentity(e.Item, e.MediaInfo);
        if (jellyfinItemId is null || mediaKind is null)
        {
            return null;
        }

        var sessionId = ExtractSessionId(e.Session?.Id ?? e.PlaySessionId);
        if (sessionId is null)
        {
            return null;
        }

        var watchoffitUserId = ExtractWatchoffitUserId(e.Users);
        if (watchoffitUserId is null)
        {
            return null;
        }

        var runtimeTicks = PlaybackRuntimeTicks(e.Item, e.MediaInfo);

        var header = _client.BuildEventHeader(
            serverConnectionId: CurrentServerConnectionId() ?? V1EnvelopeBuilder.PendingServerConnectionId,
            idKindPrefix: "evt_playback_progress",
            correlationId: CurrentCausationId());

        var payload = new V1PlaybackProgressEvent
        {
            JellyfinItemId = jellyfinItemId,
            WatchoffitUserId = watchoffitUserId,
            MediaKind = mediaKind.Value,
            ProviderIds = providerIds,
            SessionId = sessionId,
            PositionTicks = NonNegativeTicks(e.PlaybackPositionTicks),
            RuntimeTicks = runtimeTicks,
            IsPaused = e.IsPaused,
        };
        return new V1EventEnvelope { Header = header, Payload = payload };
    }

    /// <summary>
    /// Build the v1 envelope for a <c>PlaybackStop</c> event. Visible
    /// to the test project.
    /// </summary>
    /// <param name="e">Jellyfin's playback-stop event args.</param>
    /// <returns>A populated event envelope, or <c>null</c> when the args lack a usable item identity.</returns>
    internal V1EventEnvelope? BuildPlaybackStopEnvelope(PlaybackStopEventArgs e)
    {
        var (jellyfinItemId, mediaKind, providerIds) = ExtractIdentity(e.Item, e.MediaInfo);
        if (jellyfinItemId is null || mediaKind is null)
        {
            return null;
        }

        var sessionId = ExtractSessionId(e.Session?.Id ?? e.PlaySessionId);
        if (sessionId is null)
        {
            return null;
        }

        var watchoffitUserId = ExtractWatchoffitUserId(e.Users);
        if (watchoffitUserId is null)
        {
            return null;
        }

        var runtimeTicks = PlaybackRuntimeTicks(e.Item, e.MediaInfo);

        var header = _client.BuildEventHeader(
            serverConnectionId: CurrentServerConnectionId() ?? V1EnvelopeBuilder.PendingServerConnectionId,
            idKindPrefix: "evt_playback_stop",
            correlationId: CurrentCausationId());

        var payload = new V1PlaybackStopEvent
        {
            JellyfinItemId = jellyfinItemId,
            WatchoffitUserId = watchoffitUserId,
            MediaKind = mediaKind.Value,
            ProviderIds = providerIds,
            SessionId = sessionId,
            PositionTicks = NonNegativeTicks(e.PlaybackPositionTicks),
            RuntimeTicks = runtimeTicks,
            PlayedToCompletion = e.PlayedToCompletion,
            StoppedAt = FormatTimestamp(DateTime.UtcNow),
        };
        return new V1EventEnvelope { Header = header, Payload = payload };
    }

    /// <summary>
    /// Build the v1 envelope for a <c>UserDataSaved</c> event. Visible
    /// to the test project.
    /// </summary>
    /// <param name="e">Jellyfin's user-data-save event args.</param>
    /// <returns>A populated event envelope, or <c>null</c> when the args lack a usable item identity.</returns>
    internal V1EventEnvelope? BuildUserDataEnvelope(UserDataSaveEventArgs e)
    {
        if (e.UserData is null || e.Item is null || e.UserId == Guid.Empty)
        {
            return null;
        }

        var (jellyfinItemId, mediaKind, providerIds) = ExtractIdentity(e.Item, null);
        if (jellyfinItemId is null || mediaKind is null)
        {
            return null;
        }

        // Phase 5 has no Jellyfin→Watchoffit map yet, so use the Jellyfin
        // user's stable id. Unlike playback events, UserDataSaved carries
        // this id directly rather than a list of participating users.
        var watchoffitUserId = e.UserId.ToString("N", CultureInfo.InvariantCulture);

        var lastPlayedAt = e.UserData.LastPlayedDate is { } d
            ? FormatTimestamp(d.ToUniversalTime())
            : null;

        var header = _client.BuildEventHeader(
            serverConnectionId: CurrentServerConnectionId() ?? V1EnvelopeBuilder.PendingServerConnectionId,
            idKindPrefix: "evt_user_data",
            correlationId: CurrentCausationId());

        var payload = new V1UserDataEvent
        {
            JellyfinItemId = jellyfinItemId,
            WatchoffitUserId = watchoffitUserId,
            MediaKind = mediaKind.Value,
            ProviderIds = providerIds,
            Played = e.UserData.Played,
            PlayCount = Math.Max(0, e.UserData.PlayCount),
            IsFavorite = e.UserData.IsFavorite,
            LastPlayedAt = lastPlayedAt,
        };
        return new V1EventEnvelope { Header = header, Payload = payload };
    }

    /// <summary>
    /// Pull the item identity off the Jellyfin args. The
    /// <c>Item</c> and <c>MediaInfo</c> are usually redundant;
    /// <c>MediaInfo</c> (a <see cref="BaseItemDto"/>) is preferred
    /// because it always carries the provider ids the plugin
    /// scraped from the metadata provider, whereas <see cref="BaseItem"/>
    /// may not.
    /// </summary>
    private static (string? JellyfinItemId, V1MediaKind? MediaKind, V1ProviderIds? ProviderIds) ExtractIdentity(
        BaseItem? item,
        BaseItemDto? mediaInfo)
    {
        if (mediaInfo is not null && mediaInfo.Id != Guid.Empty)
        {
            var mediaKind = MapBaseItemKindEnum(mediaInfo.Type);
            if (mediaKind is null)
            {
                return (null, null, null);
            }

            return (
                mediaInfo.Id.ToString("N", CultureInfo.InvariantCulture),
                mediaKind,
                BuildProviderIds(mediaInfo.ProviderIds)
                    ?? (item is not null ? BuildProviderIdsFromBaseItem(item) : null));
        }

        if (item is not null)
        {
            var mediaKind = MapBaseItemKind(item);
            if (item.Id == Guid.Empty || mediaKind is null)
            {
                return (null, null, null);
            }

            return (
                item.Id.ToString("N", CultureInfo.InvariantCulture),
                mediaKind,
                BuildProviderIdsFromBaseItem(item));
        }

        return (null, null, null);
    }

    private static V1MediaKind? MapBaseItemKindEnum(BaseItemKind kind) => kind switch
    {
        BaseItemKind.Episode => V1MediaKind.Episode,
        BaseItemKind.Movie => V1MediaKind.Movie,
        _ => null,
    };

    private static V1MediaKind? MapBaseItemKind(BaseItem item) => item switch
    {
        MediaBrowser.Controller.Entities.TV.Episode => V1MediaKind.Episode,
        MediaBrowser.Controller.Entities.Movies.Movie => V1MediaKind.Movie,
        _ => null,
    };

    private static V1ProviderIds? BuildProviderIds(Dictionary<string, string>? providerIds)
    {
        if (providerIds is null || providerIds.Count == 0)
        {
            return null;
        }

        return BuildProviderIds(
            TryGet(providerIds, "Tmdb"),
            TryGet(providerIds, "Imdb"),
            TryGet(providerIds, "Tvdb"));
    }

    private static V1ProviderIds? BuildProviderIdsFromBaseItem(BaseItem item)
    {
        // BaseItem exposes provider ids via the IItemProviderManager /
        // a separate metadata table; for v1 we surface only the keys
        // we can read off `item.ProviderIds` if it exists.
        if (item.ProviderIds is null || item.ProviderIds.Count == 0)
        {
            return null;
        }

        return BuildProviderIds(
            TryGet(item.ProviderIds, "Tmdb"),
            TryGet(item.ProviderIds, "Imdb"),
            TryGet(item.ProviderIds, "Tvdb"));
    }

    private static V1ProviderIds? BuildProviderIds(string? tmdb, string? imdb, string? tvdb)
    {
        // `providerIds` is optional, not nullable. Do not emit an empty
        // object when Jellyfin supplies only unsupported or blank keys.
        if (tmdb is null && imdb is null && tvdb is null)
        {
            return null;
        }

        return new V1ProviderIds
        {
            Tmdb = tmdb,
            Imdb = imdb,
            Tvdb = tvdb,
        };
    }

    private static string? TryGet(Dictionary<string, string> source, string key)
    {
        foreach (var (provider, value) in source)
        {
            if (string.Equals(provider, key, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string? ExtractWatchoffitUserId(List<User>? users)
    {
        if (users is null)
        {
            return null;
        }

        foreach (var user in users)
        {
            if (user.Id != Guid.Empty)
            {
                return user.Id.ToString("N", CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    private static string? ExtractSessionId(string? sessionId)
    {
        var normalized = sessionId?.Trim();
        return !string.IsNullOrEmpty(normalized) && normalized.Length <= 128 ? normalized : null;
    }

    private static long PlaybackRuntimeTicks(BaseItem? item, BaseItemDto? mediaInfo) =>
        NonNegativeTicks(mediaInfo?.RunTimeTicks ?? item?.RunTimeTicks);

    private static long NonNegativeTicks(long? ticks) => Math.Max(0, ticks ?? 0);

    private string? CurrentServerConnectionId() => _pairing.CurrentConnection?.ServerConnectionId;

    private string? CurrentCausationId() => _causationContext.CurrentCommandId;

    private static string FormatTimestamp(DateTime timestamp)
    {
        var utc = timestamp.Kind == DateTimeKind.Utc
            ? timestamp
            : timestamp.ToUniversalTime();
        return utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }
}
