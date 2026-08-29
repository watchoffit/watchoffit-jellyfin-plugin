using System.Globalization;
using System.Text.Json;

using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Watchoffit.Events;
using Jellyfin.Plugin.Watchoffit.Pairing;
using Jellyfin.Plugin.Watchoffit.Protocol.V1;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Acks;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Commands;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Events;

using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Watchoffit.Commands.Handlers;

/// <summary>
/// Handles Watchoffit's v1 <c>backfill_request</c> command by walking one
/// Jellyfin library (or every library the user can access when
/// <see cref="V1BackfillRequestCommand.LibraryId"/> is omitted) and
/// enqueueing a <c>user_data</c> event for every item the user has
/// played. A single <c>sync_completed</c> event closes the run.
///
/// This is the durable replacement for Watchoffit's legacy admin-key
/// <c>backfillLibraries</c> call. Per-item progress is signaled by the
/// <c>user_data</c> events themselves; per-library completion is
/// signaled by the <c>sync_completed</c> envelope. Resumability on
/// plugin crash is implicit: the command's lease is reaped by the
/// Watchoffit cron and re-leased on the next poll, at which point the
/// plugin walks the unfinished library from scratch — cheap relative
/// to the wire cost, and avoids any plugin-side state.
/// </summary>
public sealed class BackfillRequestCommandHandler : ICommandHandler
{
    private static readonly JsonSerializerOptions SerializerOptions = new();

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly WatchoffitClient _client;
    private readonly PairingService _pairing;
    private readonly DurableEventOutbox _outbox;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackfillRequestCommandHandler"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin library lookup service.</param>
    /// <param name="userManager">Jellyfin user lookup service.</param>
    /// <param name="userDataManager">Jellyfin user-data read service.</param>
    /// <param name="client">Shared Watchoffit envelope builder.</param>
    /// <param name="pairing">Current pairing state and server connection id.</param>
    /// <param name="outbox">Durable event queue that receives the per-item <c>user_data</c> events and the closing <c>sync_completed</c> event.</param>
    public BackfillRequestCommandHandler(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        WatchoffitClient client,
        PairingService pairing,
        DurableEventOutbox outbox)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
        _userDataManager = userDataManager ?? throw new ArgumentNullException(nameof(userDataManager));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _pairing = pairing ?? throw new ArgumentNullException(nameof(pairing));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
    }

    /// <inheritdoc />
    public string CommandKind => "backfill_request";

    /// <inheritdoc />
    public Task<V1CommandResult> HandleAsync(
        V1LeasedCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        var connection = _pairing.CurrentConnection;
        if (connection is null || connection.State != PairingState.Paired)
        {
            return Task.FromResult(V1CommandResult.NoopWithNote("not_paired"));
        }

        var payload = DeserializePayload(command);
        if (payload is null)
        {
            return Task.FromResult(V1CommandResult.NoopWithNote("invalid_payload"));
        }

        if (!Guid.TryParse(payload.WatchoffitUserId, out var userId))
        {
            return Task.FromResult(V1CommandResult.NoopWithNote("invalid_user_id"));
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return Task.FromResult(V1CommandResult.NoopWithNote("user_not_found"));
        }

        Guid? libraryGuid = null;
        if (!string.IsNullOrEmpty(payload.LibraryId))
        {
            if (!Guid.TryParse(payload.LibraryId, out var parsed))
            {
                return Task.FromResult(V1CommandResult.NoopWithNote("invalid_library_id"));
            }

            libraryGuid = parsed;
        }

        var includeKinds = (payload.MediaKinds is { Count: > 0 } ? payload.MediaKinds : null)
            ?? new[] { V1MediaKind.Movie, V1MediaKind.Episode };
        var jellyfinKinds = new List<BaseItemKind>();
        foreach (var includeKind in includeKinds)
        {
            if (!TryToBaseItemKind(includeKind, out var jellyfinKind))
            {
                return Task.FromResult(V1CommandResult.NoopWithNote("invalid_media_kind"));
            }

            jellyfinKinds.Add(jellyfinKind);
        }

        var total = 0;
        var processed = 0;
        var failed = 0;
        var hasOutboxFull = false;
        foreach (var item in EnumerateItems(user, libraryGuid, jellyfinKinds, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += 1;

            var outcome = TryEnqueueUserDataEvent(connection.ServerConnectionId, command.CommandId, user, item);
            switch (outcome)
            {
                case EnqueueOutcome.Enqueued:
                    processed += 1;
                    break;
                case EnqueueOutcome.RejectedByOutbox:
                    failed += 1;
                    hasOutboxFull = true;
                    break;
                case EnqueueOutcome.Skipped:
                    // The item is unplayed, not a Movie/Episode, or has
                    // no provider ids — none of which count as a
                    // failure for `sync_completed.failed`.
                    break;
            }
        }

        EnqueueSyncCompleted(
            connection.ServerConnectionId,
            command.CommandId,
            userId,
            V1SyncKind.Backfill,
            libraryGuid,
            total,
            processed,
            failed);

        var note = hasOutboxFull
            ? $"backfill: processed={processed} failed={failed} total={total} (event_outbox_full)"
            : $"backfill: processed={processed} failed={failed} total={total}";

        return Task.FromResult(
            failed == 0
                ? V1CommandResult.OkWithNote(note)
                : V1CommandResult.NoopWithNote(note));
    }

    private enum EnqueueOutcome
    {
        /// <summary>The durable outbox accepted the envelope.</summary>
        Enqueued,

        /// <summary>The outbox rejected the envelope because it is full.</summary>
        RejectedByOutbox,

        /// <summary>The item is not eligible (unplayed, wrong kind, etc.).</summary>
        Skipped,
    }

    private EnqueueOutcome TryEnqueueUserDataEvent(
        string serverConnectionId,
        string commandId,
        User user,
        BaseItem item)
    {
        var userData = _userDataManager.GetUserData(user, item);
        if (userData is null || !userData.Played)
        {
            // Match the legacy `backfillUserHistory` filter: only items
            // the user has actually played. Unplayed items carry no
            // signal Watchoffit would act on.
            return EnqueueOutcome.Skipped;
        }

        var mediaKind = MapBaseItemKind(item);
        if (mediaKind is null)
        {
            return EnqueueOutcome.Skipped;
        }

        var providerIds = BuildProviderIds(item);
        var lastPlayedAt = userData.LastPlayedDate is { } date
            ? FormatTimestamp(date.ToUniversalTime())
            : null;

        var payloadEvent = new V1UserDataEvent
        {
            JellyfinItemId = item.Id.ToString("N", CultureInfo.InvariantCulture),
            WatchoffitUserId = user.Id.ToString("N", CultureInfo.InvariantCulture),
            MediaKind = mediaKind.Value,
            ProviderIds = providerIds,
            Played = userData.Played,
            PlayCount = Math.Max(0, userData.PlayCount),
            IsFavorite = userData.IsFavorite,
            LastPlayedAt = lastPlayedAt,
        };

        var envelope = new V1EventEnvelope
        {
            Header = _client.BuildEventHeader(
                serverConnectionId,
                "evt_user_data",
                correlationId: commandId),
            Payload = payloadEvent,
        };

        return _outbox.TryEnqueue(envelope) == EventOutboxEnqueueResult.Accepted
            ? EnqueueOutcome.Enqueued
            : EnqueueOutcome.RejectedByOutbox;
    }

    private void EnqueueSyncCompleted(
        string serverConnectionId,
        string commandId,
        Guid userId,
        V1SyncKind syncKind,
        Guid? libraryId,
        int total,
        int processed,
        int failed)
    {
        var payloadEvent = new V1SyncCompletedEvent
        {
            WatchoffitUserId = userId.ToString("N", CultureInfo.InvariantCulture),
            SyncKind = syncKind,
            LibraryId = libraryId?.ToString("N", CultureInfo.InvariantCulture),
            Total = total,
            Processed = processed,
            Failed = failed,
            CompletedAt = FormatTimestamp(DateTime.UtcNow),
        };

        var envelope = new V1EventEnvelope
        {
            Header = _client.BuildEventHeader(
                serverConnectionId,
                "evt_sync_completed",
                correlationId: commandId),
            Payload = payloadEvent,
        };

        // The outbox is bounded. If the walk enqueues saturated it
        // earlier, this enqueue will also fail. We accept that
        // — Watchoffit will treat the missing sync_completed as a
        // 'plugin crashed mid-walk' signal and rely on the lease
        // reaping to re-issue the command on the next poll. The
        // `failed` count above already captures how many per-item
        // events were rejected by the outbox.
        _ = _outbox.TryEnqueue(envelope);
    }

    private IEnumerable<BaseItem> EnumerateItems(
        User user,
        Guid? libraryId,
        IReadOnlyList<BaseItemKind> jellyfinKinds,
        CancellationToken cancellationToken)
    {
        if (libraryId is { } singleLibrary)
        {
            foreach (var item in QueryItems(user, singleLibrary, jellyfinKinds, cancellationToken))
            {
                yield return item;
            }

            yield break;
        }

        // No libraryId → walk every library the user can access.
        // Jellyfin's collection-folders are themselves `BaseItem`s
        // of `BaseItemKind.CollectionFolder`. We list them once
        // (without recursion so we get the folder rows, not their
        // children), then recurse per-folder.
        var libraryQuery = new InternalItemsQuery
        {
            User = user,
            IncludeItemTypes = new[] { BaseItemKind.CollectionFolder },
            Recursive = false,
        };
        var libraries = _libraryManager.GetItemList(libraryQuery);
        foreach (var library in libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in QueryItems(user, library.Id, jellyfinKinds, cancellationToken))
            {
                yield return item;
            }
        }
    }

    private IEnumerable<BaseItem> QueryItems(
        User user,
        Guid parentId,
        IReadOnlyList<BaseItemKind> jellyfinKinds,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var query = new InternalItemsQuery
        {
            User = user,
            ParentId = parentId,
            IncludeItemTypes = jellyfinKinds.ToArray(),
            Recursive = true,
        };
        return _libraryManager.GetItemList(query);
    }

    private static V1BackfillRequestCommand? DeserializePayload(V1LeasedCommand command)
    {
        try
        {
            return command.Payload.Deserialize<V1BackfillRequestCommand>(SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryToBaseItemKind(V1MediaKind kind, out BaseItemKind baseItemKind)
    {
        switch (kind)
        {
            case V1MediaKind.Movie:
                baseItemKind = BaseItemKind.Movie;
                return true;
            case V1MediaKind.Episode:
                baseItemKind = BaseItemKind.Episode;
                return true;
            default:
                baseItemKind = default;
                return false;
        }
    }

    private static V1MediaKind? MapBaseItemKind(BaseItem item) => item switch
    {
        Episode => V1MediaKind.Episode,
        Movie => V1MediaKind.Movie,
        _ => null,
    };

    private static V1ProviderIds? BuildProviderIds(BaseItem item)
    {
        if (item.ProviderIds is null || item.ProviderIds.Count == 0)
        {
            return null;
        }

        return new V1ProviderIds
        {
            Tmdb = TryGet(item.ProviderIds, "Tmdb"),
            Imdb = TryGet(item.ProviderIds, "Imdb"),
            Tvdb = TryGet(item.ProviderIds, "Tvdb"),
        };
    }

    private static string? TryGet(Dictionary<string, string> providerIds, string key)
    {
        return providerIds.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;
    }

    private static string FormatTimestamp(DateTime timestamp)
    {
        var utc = timestamp.Kind == DateTimeKind.Utc
            ? timestamp
            : timestamp.ToUniversalTime();
        return utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }
}
