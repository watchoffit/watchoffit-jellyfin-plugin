using System.Net;
using System.Text;
using System.Text.Json;

using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Watchoffit.Commands.Handlers;
using Jellyfin.Plugin.Watchoffit.Events;
using Jellyfin.Plugin.Watchoffit.Pairing;
using Jellyfin.Plugin.Watchoffit.Protocol.V1;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Acks;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Events;

using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Xunit;

namespace Jellyfin.Plugin.Watchoffit.Tests.Commands;

/// <summary>
/// Tests for <see cref="ReconcileRequestCommandHandler"/>. The handler
/// turns a server-requested reconciliation into a durable
/// <c>user_data</c> snapshot sent through the normal event outbox.
/// </summary>
public sealed class ReconcileRequestCommandHandlerTests : IDisposable
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ItemId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly string _tempDir;

    public ReconcileRequestCommandHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "watchoffit-reconcile-" + Guid.NewGuid().ToString("N"));
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
    public void CommandKind_IsReconcileRequest()
    {
        var services = NewServices(NewUserData(played: true), paired: true);

        Assert.Equal("reconcile_request", services.Handler.CommandKind);
    }

    [Fact]
    public async Task HandleAsync_QueuesCurrentUserDataSnapshot()
    {
        var userData = NewUserData(played: true);
        userData.PlayCount = 4;
        userData.IsFavorite = true;
        userData.LastPlayedDate = new DateTime(2026, 8, 28, 10, 11, 12, DateTimeKind.Utc);
        var services = NewServices(userData, paired: true);

        var result = await services.Handler.HandleAsync(NewReconcileCommand(), CancellationToken.None);

        Assert.Equal("ok", result.Status);
        Assert.Equal("reconcile_snapshot_queued", result.Note);
        var queued = services.Outbox.TryGetHead();
        Assert.NotNull(queued);
        Assert.Equal("cmd_reconcile_01", queued.Entry.Envelope.Header.CorrelationId);
        Assert.Equal("scn_01", queued.Entry.Envelope.Header.ServerConnectionId);
        var payload = Assert.IsType<V1UserDataEvent>(queued.Entry.Envelope.Payload);
        Assert.Equal(ItemId.ToString("N"), payload.JellyfinItemId);
        Assert.Equal(UserId.ToString("N"), payload.WatchoffitUserId);
        Assert.Equal(V1MediaKind.Movie, payload.MediaKind);
        Assert.True(payload.Played);
        Assert.Equal(4, payload.PlayCount);
        Assert.True(payload.IsFavorite);
        Assert.Equal("2026-08-28T10:11:12.000Z", payload.LastPlayedAt);
    }

    [Fact]
    public async Task HandleAsync_MissingUserDataQueuesUnplayedSnapshot()
    {
        var services = NewServices(null, paired: true);

        var result = await services.Handler.HandleAsync(NewReconcileCommand(), CancellationToken.None);

        Assert.Equal("ok", result.Status);
        var queued = services.Outbox.TryGetHead();
        Assert.NotNull(queued);
        var payload = Assert.IsType<V1UserDataEvent>(queued.Entry.Envelope.Payload);
        Assert.False(payload.Played);
        Assert.Equal(0, payload.PlayCount);
        Assert.False(payload.IsFavorite);
        Assert.Null(payload.LastPlayedAt);
    }

    [Fact]
    public async Task HandleAsync_NotPairedReturnsNoop()
    {
        var services = NewServices(NewUserData(played: false), paired: false);

        var result = await services.Handler.HandleAsync(NewReconcileCommand(), CancellationToken.None);

        Assert.Equal("noop", result.Status);
        Assert.Equal("not_paired", result.Note);
        Assert.Null(services.Outbox.TryGetHead());
    }

    [Fact]
    public async Task HandleAsync_InvalidUserIdReturnsNoop()
    {
        var services = NewServices(NewUserData(played: false), paired: true);

        var result = await services.Handler.HandleAsync(
            NewLeasedCommand(
                """
                {
                  "kind": "reconcile_request",
                  "jellyfinItemId": "22222222222222222222222222222222",
                  "watchoffitUserId": "not-a-guid",
                  "mediaKind": "movie",
                  "reason": "manual"
                }
                """),
            CancellationToken.None);

        Assert.Equal("noop", result.Status);
        Assert.Equal("invalid_user_id", result.Note);
        Assert.Null(services.Outbox.TryGetHead());
    }

    [Fact]
    public async Task HandleAsync_OutboxFullReturnsNoop()
    {
        var services = NewServices(NewUserData(played: false), paired: true, outboxCapacity: 1);
        Assert.Equal(EventOutboxEnqueueResult.Accepted, services.Outbox.TryEnqueue(NewExistingEvent()));

        var result = await services.Handler.HandleAsync(NewReconcileCommand(), CancellationToken.None);

        Assert.Equal("noop", result.Status);
        Assert.Equal("event_outbox_full", result.Note);
    }

    [Fact]
    public async Task HandleAsync_CancellationHonored()
    {
        var services = NewServices(NewUserData(played: false), paired: true);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => services.Handler.HandleAsync(NewReconcileCommand(), cts.Token));
    }

    private HandlerServices NewServices(
        UserItemData? userData,
        bool paired,
        int outboxCapacity = DurableEventOutbox.DefaultCapacity)
    {
        var user = new User("test", "Jellyfin.Plugin.Watchoffit.Tests", "Jellyfin.Plugin.Watchoffit.Tests")
        {
            Id = UserId,
        };
        var item = new MediaBrowser.Controller.Entities.Movies.Movie
        {
            Id = ItemId,
        };

        var libraryManager = Substitute.For<ILibraryManager>();
        libraryManager.GetItemById(ItemId).Returns(item);

        var userManager = Substitute.For<IUserManager>();
        userManager.GetUserById(UserId).Returns(user);

        var userDataManager = Substitute.For<IUserDataManager>();
        userDataManager.GetUserData(user, item).Returns(userData);

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
        var handler = new ReconcileRequestCommandHandler(
            libraryManager,
            userManager,
            userDataManager,
            client,
            pairing,
            outbox);

        return new HandlerServices(handler, outbox);
    }

    private static UserItemData NewUserData(bool played) => new()
    {
        Key = ItemId.ToString("N"),
        Played = played,
        PlayCount = played ? 1 : 0,
        IsFavorite = false,
        LastPlayedDate = played ? new DateTime(2026, 8, 28, 10, 0, 0, DateTimeKind.Utc) : null,
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

    private static V1LeasedCommand NewReconcileCommand() => NewLeasedCommand(
        """
        {
          "kind": "reconcile_request",
          "jellyfinItemId": "22222222222222222222222222222222",
          "watchoffitUserId": "11111111111111111111111111111111",
          "mediaKind": "movie",
          "reason": "post_restart"
        }
        """);

    private static V1LeasedCommand NewLeasedCommand(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        return new V1LeasedCommand
        {
            CommandId = "cmd_reconcile_01",
            CommandKind = "reconcile_request",
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
            JellyfinItemId = ItemId.ToString("N"),
            WatchoffitUserId = UserId.ToString("N"),
            MediaKind = V1MediaKind.Movie,
            Played = false,
            PlayCount = 0,
            IsFavorite = false,
            LastPlayedAt = null,
        },
    };

    private sealed record HandlerServices(
        ReconcileRequestCommandHandler Handler,
        DurableEventOutbox Outbox);

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
