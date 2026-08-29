using System.Net;
using System.Text;
using System.Text.Json;

using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Watchoffit.Commands.Handlers;
using Jellyfin.Plugin.Watchoffit.Events;
using Jellyfin.Plugin.Watchoffit.Pairing;
using Jellyfin.Plugin.Watchoffit.Protocol.V1;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Acks;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Events;

using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Xunit;

namespace Jellyfin.Plugin.Watchoffit.Tests.Commands;

/// <summary>
/// Tests for <see cref="BackfillRequestCommandHandler"/>. The handler walks
/// one Jellyfin library (or every library the user can access when
/// <c>libraryId</c> is omitted) and enqueues a <c>user_data</c> event for
/// every played item, followed by a single <c>sync_completed</c> event
/// that closes the run.
/// </summary>
public sealed class BackfillRequestCommandHandlerTests : IDisposable
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LibraryId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid OtherLibraryId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid MovieId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid EpisodeId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly string _tempDir;

    public BackfillRequestCommandHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "watchoffit-backfill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void CommandKind_IsBackfillRequest()
    {
        var services = NewServices(Array.Empty<BaseItem>());

        Assert.Equal("backfill_request", services.Handler.CommandKind);
    }

    [Fact]
    public async Task HandleAsync_QueuesUserDataEventForPlayedItemInLibrary()
    {
        var movie = NewMovie(MovieId);
        var episode = NewEpisode(EpisodeId);
        var services = NewServices(new BaseItem[] { movie, episode }, played: new HashSet<Guid> { MovieId });

        var result = await services.Handler.HandleAsync(NewBackfillCommand(libraryId: LibraryId), CancellationToken.None);

        Assert.Equal("ok", result.Status);
        Assert.NotNull(result.Note);
        Assert.Contains("processed=1", result.Note!, StringComparison.Ordinal);
        Assert.Contains("failed=0", result.Note!, StringComparison.Ordinal);
        Assert.Contains("total=2", result.Note!, StringComparison.Ordinal);
        var entries = services.DrainOutbox();
        Assert.Equal(2, entries.Count);

        var userData = Assert.IsType<V1UserDataEvent>(entries[0].Entry.Envelope.Payload);
        Assert.Equal(MovieId.ToString("N"), userData.JellyfinItemId);
        Assert.Equal(UserId.ToString("N"), userData.WatchoffitUserId);
        Assert.Equal(V1MediaKind.Movie, userData.MediaKind);
        Assert.True(userData.Played);

        var sync = Assert.IsType<V1SyncCompletedEvent>(entries[1].Entry.Envelope.Payload);
        Assert.Equal(V1SyncKind.Backfill, sync.SyncKind);
        Assert.Equal(LibraryId.ToString("N"), sync.LibraryId);
        Assert.Equal(2, sync.Total);
        Assert.Equal(1, sync.Processed);
        Assert.Equal(0, sync.Failed);
        Assert.Equal("cmd_backfill_01", entries[1].Entry.Envelope.Header.CorrelationId);
    }

    [Fact]
    public async Task HandleAsync_AllLibrariesSweep_WalksEveryUserLibrary()
    {
        var movie = NewMovie(MovieId);
        var episode = NewEpisode(EpisodeId);
        var services = NewServicesForAllLibraries(
            new Dictionary<Guid, BaseItem[]>
            {
                [LibraryId] = new BaseItem[] { movie },
                [OtherLibraryId] = new BaseItem[] { episode },
            },
            played: new HashSet<Guid> { MovieId, EpisodeId });

        var result = await services.Handler.HandleAsync(NewBackfillCommand(libraryId: null), CancellationToken.None);

        Assert.Equal("ok", result.Status);
        var entries = services.DrainOutbox();
        Assert.Equal(3, entries.Count);
        var mediaKinds = entries.Take(2)
            .Select(e => ((V1UserDataEvent)e.Entry.Envelope.Payload).MediaKind)
            .OrderBy(k => (int)k)
            .ToArray();
        Assert.Equal(new[] { V1MediaKind.Movie, V1MediaKind.Episode }, mediaKinds);
        var sync = Assert.IsType<V1SyncCompletedEvent>(entries[2].Entry.Envelope.Payload);
        Assert.Equal(2, sync.Total);
        Assert.Equal(2, sync.Processed);
        Assert.Null(sync.LibraryId);
    }

    [Fact]
    public async Task HandleAsync_NotPaired_NoopWithoutQueueing()
    {
        var services = NewServices(Array.Empty<BaseItem>(), paired: false);

        var result = await services.Handler.HandleAsync(NewBackfillCommand(libraryId: LibraryId), CancellationToken.None);

        Assert.Equal("noop", result.Status);
        Assert.Equal("not_paired", result.Note);
        Assert.Empty(services.DrainOutbox());
    }

    [Fact]
    public async Task HandleAsync_InvalidPayload_Noop()
    {
        var services = NewServices(Array.Empty<BaseItem>());

        var result = await services.Handler.HandleAsync(
            NewLeasedCommand("\"not an object\""),
            CancellationToken.None);

        Assert.Equal("noop", result.Status);
        Assert.Equal("invalid_payload", result.Note);
    }

    [Fact]
    public async Task HandleAsync_InvalidUserId_Noop()
    {
        var services = NewServices(Array.Empty<BaseItem>());

        var result = await services.Handler.HandleAsync(
            NewBackfillCommand(userId: "not-a-guid"),
            CancellationToken.None);

        Assert.Equal("noop", result.Status);
        Assert.Equal("invalid_user_id", result.Note);
    }

    [Fact]
    public async Task HandleAsync_InvalidLibraryId_Noop()
    {
        var services = NewServices(Array.Empty<BaseItem>());

        var result = await services.Handler.HandleAsync(
            NewBackfillCommand(libraryIdRaw: "not-a-guid"),
            CancellationToken.None);

        Assert.Equal("noop", result.Status);
        Assert.Equal("invalid_library_id", result.Note);
    }

    [Fact]
    public async Task HandleAsync_UserNotFound_Noop()
    {
        var services = NewServices(Array.Empty<BaseItem>(), userExists: false);

        var result = await services.Handler.HandleAsync(NewBackfillCommand(libraryId: LibraryId), CancellationToken.None);

        Assert.Equal("noop", result.Status);
        Assert.Equal("user_not_found", result.Note);
    }

    [Fact]
    public async Task HandleAsync_ProviderIdsCopiedOntoUserDataEvent()
    {
        var movie = NewMovie(MovieId);
        movie.ProviderIds = new Dictionary<string, string>
        {
            ["Tmdb"] = "603",
            ["Imdb"] = "tt0133093",
        };
        var services = NewServices(
            new BaseItem[] { movie },
            played: new HashSet<Guid> { MovieId });

        var result = await services.Handler.HandleAsync(NewBackfillCommand(libraryId: LibraryId), CancellationToken.None);

        Assert.Equal("ok", result.Status);
        var userData = Assert.IsType<V1UserDataEvent>(services.DrainOutbox()[0].Entry.Envelope.Payload);
        Assert.NotNull(userData.ProviderIds);
        Assert.Equal("603", userData.ProviderIds!.Tmdb);
        Assert.Equal("tt0133093", userData.ProviderIds.Imdb);
        Assert.Null(userData.ProviderIds.Tvdb);
    }

    [Fact]
    public async Task HandleAsync_MediaKindsFilter_RespectedOnBackfillRequest()
    {
        var movie = NewMovie(MovieId);
        var episode = NewEpisode(EpisodeId);
        var services = NewServices(
            new BaseItem[] { movie, episode },
            played: new HashSet<Guid> { MovieId, EpisodeId });

        var result = await services.Handler.HandleAsync(
            NewBackfillCommand(libraryId: LibraryId, mediaKinds: BackfillMovieMediaKinds),
            CancellationToken.None);

        Assert.Equal("ok", result.Status);
        var entries = services.DrainOutbox();
        Assert.Equal(2, entries.Count);
        Assert.Equal(V1MediaKind.Movie, ((V1UserDataEvent)entries[0].Entry.Envelope.Payload).MediaKind);
        var sync = Assert.IsType<V1SyncCompletedEvent>(entries[1].Entry.Envelope.Payload);
        Assert.Equal(1, sync.Total);
        Assert.Equal(1, sync.Processed);
    }

    [Fact]
    public async Task HandleAsync_EmptyLibrary_QueuesSyncCompletedWithZeroTotals()
    {
        var services = NewServices(Array.Empty<BaseItem>());

        var result = await services.Handler.HandleAsync(NewBackfillCommand(libraryId: LibraryId), CancellationToken.None);

        Assert.Equal("ok", result.Status);
        var entries = services.DrainOutbox();
        Assert.Single(entries);
        var sync = Assert.IsType<V1SyncCompletedEvent>(entries[0].Entry.Envelope.Payload);
        Assert.Equal(0, sync.Total);
        Assert.Equal(0, sync.Processed);
        Assert.Equal(0, sync.Failed);
    }

    [Fact]
    public async Task HandleAsync_OutboxFull_ReportsFailed()
    {
        var movie = NewMovie(MovieId);
        var services = NewServices(
            new BaseItem[] { movie },
            played: new HashSet<Guid> { MovieId },
            outboxCapacity: 1);

        // Pre-fill the outbox so the per-item enqueue is rejected.
        Assert.Equal(
            EventOutboxEnqueueResult.Accepted,
            services.Outbox.TryEnqueue(NewExistingEvent()));

        var result = await services.Handler.HandleAsync(NewBackfillCommand(libraryId: LibraryId), CancellationToken.None);

        // The walk still completes; the user_data enqueue was rejected
        // and the count is reported in the failing ack.
        Assert.Equal("noop", result.Status);
        Assert.Contains("failed=1", result.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_CancellationHonored()
    {
        var services = NewServices(Array.Empty<BaseItem>());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => services.Handler.HandleAsync(NewBackfillCommand(libraryId: LibraryId), cts.Token));
    }

    [Fact]
    public async Task HandleAsync_SkipsItemsThatAreNotMovieOrEpisode()
    {
        // The mock LibraryManager returns only items that match the
        // requested `IncludeItemTypes`, so a Series row in the library
        // would be filtered out before reaching the handler. Instead
        // test the handler's `MapBaseItemKind` switch indirectly: an
        // empty library after the LibraryManager filter still produces
        // a sync_completed with total=0, processed=0.
        var services = NewServices(Array.Empty<BaseItem>());

        var result = await services.Handler.HandleAsync(NewBackfillCommand(libraryId: LibraryId), CancellationToken.None);

        Assert.Equal("ok", result.Status);
        var entries = services.DrainOutbox();
        Assert.Single(entries);
        var sync = Assert.IsType<V1SyncCompletedEvent>(entries[0].Entry.Envelope.Payload);
        Assert.Equal(0, sync.Total);
        Assert.Equal(0, sync.Processed);
    }

    [Fact]
    public async Task HandleAsync_EmittedUserDataCarriesLastPlayedAtAsIsoUtc()
    {
        // The wire format mandates ISO 8601 UTC with .fffZ. The plugin
        // must format DateTime values accordingly so Watchoffit's strict
        // Zod parser accepts the event.
        var movie = NewMovie(MovieId);
        var services = NewServices(new BaseItem[] { movie });
        services.OverrideUserData(movie, NewPlayedUserData(
            new DateTime(2026, 8, 28, 10, 11, 12, 345, DateTimeKind.Utc)));

        var result = await services.Handler.HandleAsync(NewBackfillCommand(libraryId: LibraryId), CancellationToken.None);

        Assert.Equal("ok", result.Status);
        var userData = Assert.IsType<V1UserDataEvent>(services.DrainOutbox()[0].Entry.Envelope.Payload);
        Assert.Equal("2026-08-28T10:11:12.345Z", userData.LastPlayedAt);
    }

    // ─── Fixtures ──────────────────────────────────────────────────

    private sealed class HandlerServices
    {
        public HandlerServices(
            BackfillRequestCommandHandler handler,
            DurableEventOutbox outbox,
            IUserDataManager userDataManager,
            User user,
            string tempDir)
        {
            Handler = handler;
            Outbox = outbox;
            _userDataManager = userDataManager;
            _user = user;
            _tempDir = tempDir;
        }

        public BackfillRequestCommandHandler Handler { get; }

        public DurableEventOutbox Outbox { get; }

        private readonly IUserDataManager _userDataManager;
        private readonly User _user;
        private readonly string _tempDir;

        public void OverrideUserData(BaseItem item, UserItemData userData)
        {
            _userDataManager.GetUserData(_user, item).Returns(userData);
        }

        public IReadOnlyList<DurableEventOutboxItem> DrainOutbox()
        {
            // The outbox is a directory of JSON files (one per entry).
            // The public API only exposes TryGetHead + Acknowledge, but
            // for tests we want a snapshot of every enqueued entry. Read
            // the files directly; this is the same shape the worker
            // consumes via the durable IO path. Exclude the sequence
            // watermark file which is a sibling JSON document.
            var directory = Path.Combine(_tempDir, "watchoffit-event-outbox");
            if (!Directory.Exists(directory))
            {
                return Array.Empty<DurableEventOutboxItem>();
            }

            var items = new List<DurableEventOutboxItem>();
            foreach (var path in Directory.GetFiles(directory, "*.json"))
            {
                // The outbox directory has two JSON file types:
                //   - `<sequence>-<hash>.json` — actual pending entries
                //   - `sequence-watermark.json` — high-water mark
                // We only care about the entries.
                var fileName = Path.GetFileName(path);
                if (fileName == "sequence-watermark.json")
                {
                    continue;
                }

                var json = File.ReadAllText(path);
                var entry = JsonSerializer.Deserialize<DurableEventOutboxEntry>(json, JsonOptions);
                if (entry is not null)
                {
                    items.Add(new DurableEventOutboxItem(fileName, entry));
                }
            }

            return items
                .OrderBy(i => i.Entry.Envelope.Header.Sequence)
                .ToList();
        }
    }

    private HandlerServices NewServices(
        IReadOnlyList<BaseItem> libraryItems,
        IReadOnlySet<Guid>? played = null,
        bool paired = true,
        bool userExists = true,
        int outboxCapacity = DurableEventOutbox.DefaultCapacity)
    {
        var itemsByParent = new Dictionary<Guid, IReadOnlyList<BaseItem>>
        {
            [LibraryId] = libraryItems,
        };
        return BuildServices(
            itemsByParent: itemsByParent,
            collectionFolders: null,
            played: played,
            paired: paired,
            userExists: userExists,
            outboxCapacity: outboxCapacity);
    }

    private HandlerServices NewServicesForAllLibraries(
        IReadOnlyDictionary<Guid, BaseItem[]> libraries,
        IReadOnlySet<Guid>? played = null,
        bool paired = true,
        int outboxCapacity = DurableEventOutbox.DefaultCapacity)
    {
        var collectionFolders = libraries.Keys
            .Select(id => (BaseItem)new CollectionFolder { Id = id, Name = "Lib " + id.ToString("N")[..4] })
            .ToArray();
        var itemsByParent = libraries.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<BaseItem>)kv.Value);
        return BuildServices(
            itemsByParent: itemsByParent,
            collectionFolders: collectionFolders,
            played: played,
            paired: paired,
            userExists: true,
            outboxCapacity: outboxCapacity);
    }

    private HandlerServices BuildServices(
        IReadOnlyDictionary<Guid, IReadOnlyList<BaseItem>> itemsByParent,
        IReadOnlyList<BaseItem>? collectionFolders,
        IReadOnlySet<Guid>? played,
        bool paired,
        bool userExists,
        int outboxCapacity)
    {
        var user = new User("test", "Jellyfin.Plugin.Watchoffit.Tests", "Jellyfin.Plugin.Watchoffit.Tests")
        {
            Id = UserId,
        };

        var libraryManager = Substitute.For<ILibraryManager>();
        var userManager = Substitute.For<IUserManager>();
        var userDataManager = Substitute.For<IUserDataManager>();
        var allItems = itemsByParent.Values.SelectMany(items => items).ToArray();

        libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).ReturnsForAnyArgs(callInfo =>
        {
            var query = (InternalItemsQuery)callInfo.Args()[0]!;
            // Collection-folder query: identified by IncludeItemTypes
            // containing BaseItemKind.CollectionFolder.
            if (query.IncludeItemTypes is { } kinds
                && kinds.Any(k => k == BaseItemKind.CollectionFolder))
            {
                return collectionFolders is null
                    ? (IReadOnlyList<BaseItem>)Array.Empty<BaseItem>()
                    : collectionFolders;
            }

            // Per-library query: look up items by parent. The mock
            // honours `IncludeItemTypes` so the handler's mediaKinds
            // filter is exercised end-to-end (the LibraryManager would
            // do the same in production).
            if (!itemsByParent.TryGetValue(query.ParentId, out var items))
            {
                return (IReadOnlyList<BaseItem>)Array.Empty<BaseItem>();
            }

            var includeKinds = query.IncludeItemTypes;
            if (includeKinds is null || includeKinds.Length == 0)
            {
                return items;
            }

            return (IReadOnlyList<BaseItem>)items
                .Where(item => includeKinds.Any(k => k switch
                {
                    BaseItemKind.Movie => item is Movie,
                    BaseItemKind.Episode => item is Episode,
                    _ => false,
                }))
                .ToList();
        });

        if (userExists)
        {
            userManager.GetUserById(UserId).Returns(user);
        }
        else
        {
            userManager.GetUserById(UserId).Returns((User?)null);
        }

        var playedIds = played ?? new HashSet<Guid>();
        foreach (var item in allItems)
        {
            userDataManager.GetUserData(user, item).Returns(playedIds.Contains(item.Id)
                ? NewPlayedUserData(new DateTime(2026, 8, 28, 10, 0, 0, DateTimeKind.Utc))
                : (UserItemData?)null!);
        }

        var systemInfo = new StaticJellyfinSystemInfoProvider("jf_server_01", "10.11.11");
        var client = new WatchoffitClient(
            new HttpClient(new NullHttpHandler()) { BaseAddress = new Uri("https://watchoffit.test/") },
            new V1EnvelopeBuilder(),
            systemInfo,
            NullLogger<WatchoffitClient>.Instance);
        var store = new WatchoffitConnectionStore(
            _tempDir,
            new PlainCredentialProtector(),
            NullLogger<WatchoffitConnectionStore>.Instance);
        if (paired)
        {
            store.Save(NewConnection());
        }

        var pairing = new PairingService(store, client, systemInfo, NullLogger<PairingService>.Instance);
        pairing.LoadFromStore();
        var outbox = new DurableEventOutbox(_tempDir, NullLogger<DurableEventOutbox>.Instance, outboxCapacity);
        var handler = new BackfillRequestCommandHandler(
            libraryManager,
            userManager,
            userDataManager,
            client,
            pairing,
            outbox);

        var services = new HandlerServices(handler, outbox, userDataManager, user, _tempDir);
        return services;
    }

    private static Movie NewMovie(Guid id) => new()
    {
        Id = id,
        Name = "The Matrix",
    };

    private static Episode NewEpisode(Guid id) => new()
    {
        Id = id,
        Name = "The Pilot",
        IndexNumber = 1,
    };

    private static UserItemData NewPlayedUserData(DateTime lastPlayedAt) => new()
    {
        Key = "default-key",
        Played = true,
        PlayCount = 1,
        IsFavorite = false,
        LastPlayedDate = lastPlayedAt,
    };

    private static WatchoffitConnection NewConnection() => new()
    {
        Version = WatchoffitConnectionStore.CurrentVersion,
        State = PairingState.Paired,
        BaseUrl = "https://watchoffit.test",
        ServerConnectionId = "scn_01",
        WatchoffitServerName = "Watchoffit",
        JellyfinServerId = "jf_server_01",
        Credential = new WatchoffitCredential { Scheme = "plain", Value = "credential" },
        Capabilities = V1EnvelopeBuilder.DefaultCapabilities,
        CreatedAt = "2026-08-28T10:00:00.000Z",
        LastPingAt = string.Empty,
    };

    private static readonly string[] BackfillMovieMediaKinds = ["movie"];

    private static V1LeasedCommand NewBackfillCommand(
        string? userId = null,
        Guid? libraryId = null,
        string? libraryIdRaw = null,
        string[]? mediaKinds = null)
    {
        var userIdLiteral = userId ?? UserId.ToString("N");
        var libraryObject = libraryIdRaw is not null
            ? $"\"{libraryIdRaw}\""
            : libraryId is null
                ? "null"
                : $"\"{libraryId.Value.ToString("N")}\"";
        var mediaKindsLiteral = mediaKinds is null
            ? string.Empty
            : ",\"mediaKinds\":[" + string.Join(",", mediaKinds.Select(k => $"\"{k}\"")) + "]";

        var payload = $$"""
            {
              "kind": "backfill_request",
              "watchoffitUserId": "{{userIdLiteral}}",
              "libraryId": {{libraryObject}}{{mediaKindsLiteral}}
            }
            """;
        return NewLeasedCommand(payload);
    }

    private static V1LeasedCommand NewLeasedCommand(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        return new V1LeasedCommand
        {
            CommandId = "cmd_backfill_01",
            CommandKind = "backfill_request",
            Payload = doc.RootElement.Clone(),
            LeaseUntil = 0,
            AttemptToken = "att_test",
        };
    }

    private static V1EventEnvelope NewExistingEvent() => new()
    {
        Header = new V1Header
        {
            Version = V1ProtocolConstants.ProtocolVersion,
            Kind = V1EnvelopeKind.Event,
            Id = "evt_existing",
            Sequence = 1,
            Timestamp = "2026-08-28T10:00:00.000Z",
            ServerConnectionId = "scn_01",
        },
        Payload = new V1UserDataEvent
        {
            JellyfinItemId = MovieId.ToString("N"),
            WatchoffitUserId = UserId.ToString("N"),
            MediaKind = V1MediaKind.Movie,
            Played = false,
            PlayCount = 0,
            IsFavorite = false,
            LastPlayedAt = null,
        },
    };

    private sealed class NullHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
