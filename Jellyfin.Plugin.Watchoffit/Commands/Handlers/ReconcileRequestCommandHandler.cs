using System.Globalization;
using System.Text.Json;

using Jellyfin.Plugin.Watchoffit.Events;
using Jellyfin.Plugin.Watchoffit.Pairing;
using Jellyfin.Plugin.Watchoffit.Protocol.V1;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Acks;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Commands;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Events;

using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Watchoffit.Commands.Handlers;

/// <summary>
/// Handles Watchoffit's v1 <c>reconcile_request</c> command by re-emitting
/// Jellyfin's current user-data snapshot for the requested item and user.
/// </summary>
public sealed class ReconcileRequestCommandHandler : ICommandHandler
{
    private static readonly JsonSerializerOptions SerializerOptions = new();

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly WatchoffitClient _client;
    private readonly PairingService _pairing;
    private readonly DurableEventOutbox _outbox;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReconcileRequestCommandHandler"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin library lookup service.</param>
    /// <param name="userManager">Jellyfin user lookup service.</param>
    /// <param name="userDataManager">Jellyfin user-data read service.</param>
    /// <param name="client">Shared Watchoffit envelope builder.</param>
    /// <param name="pairing">Current pairing state and server connection id.</param>
    /// <param name="outbox">Durable event queue that will deliver the snapshot.</param>
    public ReconcileRequestCommandHandler(
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
    public string CommandKind => "reconcile_request";

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

        if (!TryParseGuid(payload.WatchoffitUserId, out var userId))
        {
            return Task.FromResult(V1CommandResult.NoopWithNote("invalid_user_id"));
        }

        if (!TryParseGuid(payload.JellyfinItemId, out var itemId))
        {
            return Task.FromResult(V1CommandResult.NoopWithNote("invalid_item_id"));
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return Task.FromResult(V1CommandResult.NoopWithNote("user_not_found"));
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return Task.FromResult(V1CommandResult.NoopWithNote("item_not_found"));
        }

        var userData = _userDataManager.GetUserData(user, item);
        var lastPlayedAt = userData?.LastPlayedDate is { } date
            ? FormatTimestamp(date.ToUniversalTime())
            : null;
        var payloadEvent = new V1UserDataEvent
        {
            JellyfinItemId = payload.JellyfinItemId,
            WatchoffitUserId = payload.WatchoffitUserId,
            MediaKind = payload.MediaKind,
            ProviderIds = payload.ProviderIds,
            Played = userData?.Played ?? false,
            PlayCount = Math.Max(0, userData?.PlayCount ?? 0),
            IsFavorite = userData?.IsFavorite ?? false,
            LastPlayedAt = lastPlayedAt,
        };
        var envelope = new V1EventEnvelope
        {
            Header = _client.BuildEventHeader(
                connection.ServerConnectionId,
                "evt_user_data",
                correlationId: command.CommandId),
            Payload = payloadEvent,
        };

        return Task.FromResult(_outbox.TryEnqueue(envelope) switch
        {
            EventOutboxEnqueueResult.Full => V1CommandResult.NoopWithNote("event_outbox_full"),
            EventOutboxEnqueueResult.AlreadyQueued => V1CommandResult.OkWithNote("reconcile_snapshot_already_queued"),
            _ => V1CommandResult.OkWithNote("reconcile_snapshot_queued"),
        });
    }

    private static V1ReconcileRequestCommand? DeserializePayload(V1LeasedCommand command)
    {
        try
        {
            return command.Payload.Deserialize<V1ReconcileRequestCommand>(SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryParseGuid(string value, out Guid guid)
    {
        return Guid.TryParseExact(value, "N", out guid)
            || Guid.TryParse(value, out guid);
    }

    private static string FormatTimestamp(DateTime timestamp)
    {
        var utc = timestamp.Kind == DateTimeKind.Utc
            ? timestamp
            : timestamp.ToUniversalTime();
        return utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }
}
