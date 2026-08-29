using Jellyfin.Plugin.Watchoffit.Pairing;
using Jellyfin.Plugin.Watchoffit.Protocol.V1;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Events;

using MediaBrowser.Controller.Library;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Watchoffit.Events;

/// <summary>
/// Periodically publishes the Jellyfin user and library inventory after pairing.
/// </summary>
/// <remarks>
/// The service only writes envelopes to <see cref="DurableEventOutbox"/>. The
/// existing worker owns delivery and retries, so inventory collection never
/// blocks Jellyfin startup or requires a second networking path.
/// </remarks>
public sealed class InventoryPublisher : BackgroundService
{
    private const int MaxEntriesPerChunk = 200;
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PublishInterval = TimeSpan.FromMinutes(15);

    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IJellyfinSystemInfoProvider _systemInfo;
    private readonly WatchoffitClient _client;
    private readonly PairingService _pairing;
    private readonly DurableEventOutbox _outbox;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="InventoryPublisher"/> class.</summary>
    /// <param name="userManager">Jellyfin user directory and public user DTO source.</param>
    /// <param name="libraryManager">Jellyfin library directory.</param>
    /// <param name="systemInfo">Stable local Jellyfin server identity.</param>
    /// <param name="client">Shared Watchoffit envelope builder.</param>
    /// <param name="pairing">Current pairing state and connection.</param>
    /// <param name="outbox">Durable event queue.</param>
    /// <param name="logger">Diagnostic logger that never receives credentials.</param>
    public InventoryPublisher(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IJellyfinSystemInfoProvider systemInfo,
        WatchoffitClient client,
        PairingService pairing,
        DurableEventOutbox outbox,
        ILogger<InventoryPublisher> logger)
    {
        _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _systemInfo = systemInfo ?? throw new ArgumentNullException(nameof(systemInfo));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _pairing = pairing ?? throw new ArgumentNullException(nameof(pairing));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                PublishCurrentInventory();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Watchoffit inventory collection failed");
            }

            await Task.Delay(PublishInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Build and queue one complete inventory generation when paired.</summary>
    internal void PublishCurrentInventory()
    {
        var connection = _pairing.CurrentConnection;
        if (connection is null || connection.State != PairingState.Paired)
        {
            return;
        }

        var users = _userManager.GetUsers()
            .Select(user => _userManager.GetUserDto(user, null))
            .ToArray();
        var inventoryUsers = users
            .Select(user => new V1InventoryUser
            {
                RemoteUserId = user.Id.ToString("D"),
                Name = user.Name,
                IsAdministrator = user.Policy.IsAdministrator,
                IsDisabled = user.Policy.IsDisabled,
            })
            .ToArray();
        var libraries = _libraryManager.GetVirtualFolders()
            .Select(folder => new V1InventoryLibrary
            {
                RemoteLibraryId = folder.ItemId.ToString(),
                Name = folder.Name,
                CollectionType = folder.CollectionType?.ToString() ?? string.Empty,
            })
            .ToArray();
        var libraryIds = libraries
            .Select(library => Guid.TryParse(library.RemoteLibraryId, out var id) ? id : Guid.Empty)
            .ToArray();
        var access = users
            .SelectMany(user => libraryIds
                .Where(libraryId => libraryId != Guid.Empty
                    && (user.Policy.EnableAllFolders || user.Policy.EnabledFolders.Contains(libraryId)))
                .Select(libraryId => new V1InventoryUserLibrary
                {
                    RemoteUserId = user.Id.ToString("D"),
                    RemoteLibraryId = libraryId.ToString("D"),
                }))
            .ToArray();

        var chunkCount = Math.Max(
            1,
            new[] { inventoryUsers.Length, libraries.Length, access.Length }
                .Select(length => (int)Math.Ceiling(length / (double)MaxEntriesPerChunk))
                .Max());
        var header = _client.BuildEventHeader(connection.ServerConnectionId, "evt_inventory");
        var system = _systemInfo.GetCurrent();
        if (_outbox.QueueDepth + chunkCount > DurableEventOutbox.DefaultCapacity)
        {
            _logger.LogWarning("Watchoffit inventory generation {Generation} deferred because the event queue lacks capacity", header.Sequence);
            return;
        }

        for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            var payload = new V1InventoryManifestEvent
            {
                Provider = "jellyfin",
                // The persisted header sequence is monotonic across restarts and
                // is therefore a safer generation than wall-clock time.
                Generation = header.Sequence,
                CapturedAt = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ChunkIndex = chunkIndex,
                ChunkCount = chunkCount,
                Server = new V1InventoryServer
                {
                    RemoteServerId = system.JellyfinServerId,
                    Name = system.JellyfinServerId,
                    Version = system.JellyfinVersion,
                    PluginVersion = V1EnvelopeBuilder.PluginVersion,
                },
                Users = inventoryUsers.Skip(chunkIndex * MaxEntriesPerChunk).Take(MaxEntriesPerChunk).ToArray(),
                Libraries = libraries.Skip(chunkIndex * MaxEntriesPerChunk).Take(MaxEntriesPerChunk).ToArray(),
                UserLibraries = access.Skip(chunkIndex * MaxEntriesPerChunk).Take(MaxEntriesPerChunk).ToArray(),
            };
            var envelope = new V1EventEnvelope
            {
                Header = chunkIndex == 0
                    ? header
                    : _client.BuildEventHeader(connection.ServerConnectionId, "evt_inventory"),
                Payload = payload,
            };
            var enqueue = _outbox.TryEnqueue(envelope);
            if (enqueue == EventOutboxEnqueueResult.Full)
            {
                _logger.LogError("Watchoffit inventory queue is full; generation {Generation} was not fully queued", header.Sequence);
                return;
            }
        }

        _logger.LogInformation(
            "Queued Watchoffit inventory generation {Generation}: {Users} users, {Libraries} libraries, {Chunks} chunks",
            header.Sequence,
            inventoryUsers.Length,
            libraries.Length,
            chunkCount);
    }
}
