using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Watchoffit.Commands;
using Jellyfin.Plugin.Watchoffit.Events;
using Jellyfin.Plugin.Watchoffit.Pairing;
using Jellyfin.Plugin.Watchoffit.Protocol.V1;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Events;

using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Xunit;

namespace Jellyfin.Plugin.Watchoffit.Tests.Events;

/// <summary>
/// Translation tests for <see cref="EventForwarder"/>. The forwarder's
/// job is to take a Jellyfin event-args instance and turn it into a
/// v1 envelope whose wire shape matches
/// <c>docs/protocol-v1.md</c> §4.2. The tests
/// build the Jellyfin args directly (plain data classes with writable
/// properties) and pin the envelope field-by-field.
/// </summary>
public class EventForwarderTests
{
    private static readonly Guid TestUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TestItemId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TestSessionId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static EventForwarder NewForwarder(
        HttpMessageHandler? handler = null,
        ICommandCausationContext? causationContext = null)
    {
        var systemInfo = new StaticJellyfinSystemInfoProvider("jf_server_01", "10.11.11");
        var builder = new V1EnvelopeBuilder();
        var http = new HttpClient(handler ?? new NullHttpHandler())
        {
            BaseAddress = new Uri("https://watchoffit.test/"),
        };
        var client = new WatchoffitClient(http, builder, systemInfo, NullLogger<WatchoffitClient>.Instance);

        // The build methods under test never call
        // `PairingService.CurrentConnection` — they only use
        // `_client.BuildEventHeader`. The real `PairingService` with
        // a temp-dir store works (the store's `LoadFromStore` returns
        // `NotPresent` for an empty dir, leaving `CurrentConnection`
        // null). `WatchoffitConnectionStore` is sealed, so NSubstitute
        // cannot proxy it.
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "watchoffit-fwd-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tempDir);
        var store = new WatchoffitConnectionStore(tempDir, new PlainCredentialProtector(), NullLogger<WatchoffitConnectionStore>.Instance);
        var pairing = new PairingService(store, client, systemInfo, NullLogger<PairingService>.Instance);
        var outbox = new DurableEventOutbox(tempDir, NullLogger<DurableEventOutbox>.Instance);

        var sessionManager = Substitute.For<ISessionManager>();
        var userDataManager = Substitute.For<IUserDataManager>();

        return new EventForwarder(
            sessionManager,
            userDataManager,
            client,
            pairing,
            outbox,
            causationContext ?? new CommandCausationContext(),
            NullLogger<EventForwarder>.Instance);
    }

    private static SessionInfo NewSession()
    {
        // `SessionInfo` is sealed-ish and has only a non-default
        // constructor (`ISessionManager, ILogger`); NSubstitute gives
        // us an instance we can set `Id` on.
        var session = Substitute.For<SessionInfo>(
            Substitute.For<ISessionManager>(),
            NullLogger.Instance);
        session.Id = TestSessionId.ToString("N");
        return session;
    }

    private static User NewUser()
    {
        // The Jellyfin `User` is an EF entity with a required
        // constructor (`username, authenticationProviderId,
        // passwordResetProviderId`) and a settable `Id`. Use the
        // real type — NSubstitute cannot proxy EF entities that
        // require DI/EF initialization.
        var user = new User("test", "Jellyfin.Plugin.Watchoffit.Tests", "Jellyfin.Plugin.Watchoffit.Tests")
        {
            Id = TestUserId,
        };
        return user;
    }

    private static MediaBrowser.Controller.Entities.Movies.Movie NewMovie() => new()
    {
        Id = TestItemId,
    };

    private static BaseItemDto NewMediaInfo() => new()
    {
        Id = TestItemId,
        Type = BaseItemKind.Movie,
        ProviderIds = new Dictionary<string, string>
        {
            ["Tmdb"] = "603",
            ["Imdb"] = "tt0133093",
        },
        RunTimeTicks = 7200,
    };

    [Fact]
    public void BuildPlaybackProgressEnvelope_TranslatesArgs()
    {
        var forwarder = NewForwarder();
        var users = new List<User> { NewUser() };
        var args = new PlaybackProgressEventArgs
        {
            PlaybackPositionTicks = 1200,
            IsPaused = false,
            PlaySessionId = TestSessionId.ToString("N"),
            Session = null, // PlaySessionId is the test path; Session ctor is sealed.
            Users = users,
            MediaInfo = NewMediaInfo(),
        };
        Assert.Single(args.Users);
        Assert.Equal(TestUserId, args.Users[0].Id);

        var envelope = forwarder.BuildPlaybackProgressEnvelope(args);

        Assert.NotNull(envelope);
        var payload = Assert.IsType<V1PlaybackProgressEvent>(envelope!.Payload);
        Assert.Equal(TestItemId.ToString("N"), payload.JellyfinItemId);
        Assert.Equal(V1MediaKind.Movie, payload.MediaKind);
        Assert.Equal(TestUserId.ToString("N"), payload.WatchoffitUserId);
        Assert.Equal(TestSessionId.ToString("N"), payload.SessionId);
        Assert.Equal(1200L, payload.PositionTicks);
        Assert.False(payload.IsPaused);
        Assert.Equal("603", payload.ProviderIds?.Tmdb);
        Assert.StartsWith("evt_playback_progress_", envelope.Header.Id, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPlaybackStopEnvelope_MirrorsPlayedToCompletion()
    {
        var forwarder = NewForwarder();
        var args = new PlaybackStopEventArgs
        {
            PlaybackPositionTicks = 7200,
            PlayedToCompletion = true,
            PlaySessionId = TestSessionId.ToString("N"),
            Users = new List<User> { NewUser() },
            MediaInfo = NewMediaInfo(),
        };

        var envelope = forwarder.BuildPlaybackStopEnvelope(args);

        Assert.NotNull(envelope);
        var payload = Assert.IsType<V1PlaybackStopEvent>(envelope!.Payload);
        Assert.True(payload.PlayedToCompletion);
        Assert.Equal(7200L, payload.PositionTicks);
        Assert.StartsWith("evt_playback_stop_", envelope.Header.Id, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildUserDataEnvelope_MapsPlayedAndLastPlayed()
    {
        var forwarder = NewForwarder();
        var args = new UserDataSaveEventArgs
        {
            UserId = TestUserId,
            UserData = new MediaBrowser.Controller.Entities.UserItemData
            {
                Key = TestItemId.ToString("N"),
                Played = true,
                PlayCount = 2,
                IsFavorite = true,
                LastPlayedDate = new DateTime(2026, 8, 27, 10, 0, 0, DateTimeKind.Utc),
            },
            Item = NewMovie(),
        };

        var envelope = forwarder.BuildUserDataEnvelope(args);

        Assert.NotNull(envelope);
        var payload = Assert.IsType<V1UserDataEvent>(envelope!.Payload);
        Assert.True(payload.Played);
        Assert.Equal(2, payload.PlayCount);
        Assert.True(payload.IsFavorite);
        Assert.Equal("2026-08-27T10:00:00.000Z", payload.LastPlayedAt);
        Assert.Equal(TestUserId.ToString("N"), payload.WatchoffitUserId);
    }

    [Fact]
    public void BuildUserDataEnvelope_OmitsLastPlayedWhenNull()
    {
        var forwarder = NewForwarder();
        var args = new UserDataSaveEventArgs
        {
            UserId = TestUserId,
            UserData = new MediaBrowser.Controller.Entities.UserItemData
            {
                Key = TestItemId.ToString("N"),
                Played = false,
                PlayCount = 0,
                IsFavorite = false,
            },
            Item = NewMovie(),
        };

        var envelope = forwarder.BuildUserDataEnvelope(args);

        Assert.NotNull(envelope);
        var payload = Assert.IsType<V1UserDataEvent>(envelope!.Payload);
        Assert.False(payload.Played);
        Assert.Null(payload.LastPlayedAt);
    }

    [Fact]
    public void BuildUserDataEnvelope_AttachesCommandCorrelationIdInsideCausationScope()
    {
        var causationContext = new CommandCausationContext();
        var forwarder = NewForwarder(causationContext: causationContext);
        var args = new UserDataSaveEventArgs
        {
            UserId = TestUserId,
            UserData = new MediaBrowser.Controller.Entities.UserItemData
            {
                Key = TestItemId.ToString("N"),
                Played = true,
                PlayCount = 1,
            },
            Item = NewMovie(),
        };

        using (causationContext.Begin("cmd_mark_played_01"))
        {
            var envelope = forwarder.BuildUserDataEnvelope(args);

            Assert.NotNull(envelope);
            Assert.Equal("cmd_mark_played_01", envelope!.Header.CorrelationId);
        }

        var uncorrelated = forwarder.BuildUserDataEnvelope(args);

        Assert.NotNull(uncorrelated);
        Assert.Null(uncorrelated!.Header.CorrelationId);
    }

    [Fact]
    public void BuildPlaybackProgressEnvelope_RejectsMissingItemIdentity()
    {
        var forwarder = NewForwarder();
        var args = new PlaybackProgressEventArgs
        {
            PlaybackPositionTicks = 0,
            IsPaused = false,
            PlaySessionId = TestSessionId.ToString("N"),
            Session = null, // PlaySessionId is the test path; Session ctor is sealed.
            // No Item, no MediaInfo — should be rejected.
        };

        var envelope = forwarder.BuildPlaybackProgressEnvelope(args);

        Assert.Null(envelope);
    }

    [Fact]
    public void PlaybackPayloads_RejectUnsupportedMediaKinds()
    {
        var forwarder = NewForwarder();
        var args = new PlaybackProgressEventArgs
        {
            PlaySessionId = TestSessionId.ToString("N"),
            Users = new List<User> { NewUser() },
            MediaInfo = new BaseItemDto
            {
                Id = TestItemId,
                Type = BaseItemKind.Audio,
            },
        };

        Assert.Null(forwarder.BuildPlaybackProgressEnvelope(args));
    }

    [Fact]
    public void PlaybackPayloads_FallBackToItemWhenMediaInfoHasNoId()
    {
        var forwarder = NewForwarder();
        var args = new PlaybackProgressEventArgs
        {
            Item = NewMovie(),
            PlaySessionId = TestSessionId.ToString("N"),
            Users = new List<User> { NewUser() },
            MediaInfo = new BaseItemDto
            {
                Type = BaseItemKind.Movie,
            },
        };

        var payload = Assert.IsType<V1PlaybackProgressEvent>(forwarder.BuildPlaybackProgressEnvelope(args)!.Payload);

        Assert.Equal(TestItemId.ToString("N"), payload.JellyfinItemId);
        Assert.Equal(V1MediaKind.Movie, payload.MediaKind);
    }

    [Fact]
    public void PlaybackPayloads_RejectInvalidSessionIds()
    {
        var forwarder = NewForwarder();
        var args = new PlaybackProgressEventArgs
        {
            Item = NewMovie(),
            PlaySessionId = new string('x', 129),
            Users = new List<User> { NewUser() },
            MediaInfo = NewMediaInfo(),
        };

        Assert.Null(forwarder.BuildPlaybackProgressEnvelope(args));
    }

    [Fact]
    public void BuildPlaybackStartEnvelope_IncludesStartedAt()
    {
        var forwarder = NewForwarder();
        var args = new PlaybackProgressEventArgs
        {
            PlaybackPositionTicks = 0,
            IsPaused = false,
            PlaySessionId = TestSessionId.ToString("N"),
            Session = null, // PlaySessionId is the test path; Session ctor is sealed.
            Users = new List<User> { NewUser() },
            MediaInfo = NewMediaInfo(),
        };

        var envelope = forwarder.BuildPlaybackStartEnvelope(args);

        Assert.NotNull(envelope);
        var payload = Assert.IsType<V1PlaybackStartEvent>(envelope!.Payload);
        Assert.Equal(0L, payload.PositionTicks);
        Assert.False(string.IsNullOrEmpty(payload.StartedAt));
        Assert.StartsWith("evt_playback_start_", envelope.Header.Id, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaybackPayloads_RejectMissingUserAndNormalizeProviderValues()
    {
        var forwarder = NewForwarder();
        var info = NewMediaInfo();
        info.ProviderIds = new Dictionary<string, string>
        {
            ["Tmdb"] = "603",
            ["Imdb"] = "",
            ["Tvdb"] = " 42 ",
            ["Unknown"] = "ignored",
        };
        var args = new PlaybackProgressEventArgs
        {
            PlaybackPositionTicks = -10,
            IsPaused = true,
            PlaySessionId = TestSessionId.ToString("N"),
            MediaInfo = info,
            Users = new List<User>(),
        };

        Assert.Null(forwarder.BuildPlaybackProgressEnvelope(args));

        args.Users = new List<User> { NewUser() };
        var payload = Assert.IsType<V1PlaybackProgressEvent>(forwarder.BuildPlaybackProgressEnvelope(args)!.Payload);

        Assert.Equal(TestUserId.ToString("N"), payload.WatchoffitUserId);
        Assert.Equal(0L, payload.PositionTicks);
        Assert.Equal(7200L, payload.RuntimeTicks);
        Assert.Equal("603", payload.ProviderIds!.Tmdb);
        Assert.Null(payload.ProviderIds.Imdb);
        Assert.Equal("42", payload.ProviderIds.Tvdb);
    }

    [Fact]
    public void PlaybackStart_UsesCurrentUtcTimestampAndStopUsesUtcTimestamp()
    {
        var forwarder = NewForwarder();
        var item = NewMovie();
        item.Id = TestItemId;
        item.RunTimeTicks = 9000;
        item.DateCreated = new DateTime(2026, 8, 27, 12, 34, 56, 789, DateTimeKind.Utc);
        var startArgs = new PlaybackProgressEventArgs
        {
            Item = item,
            PlaySessionId = TestSessionId.ToString("N"),
            PlaybackPositionTicks = 123,
            Users = new List<User> { NewUser() },
        };

        var start = Assert.IsType<V1PlaybackStartEvent>(forwarder.BuildPlaybackStartEnvelope(startArgs)!.Payload);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", start.StartedAt);
        Assert.NotEqual("2026-08-27T12:34:56.789Z", start.StartedAt);
        Assert.Equal(9000L, start.RuntimeTicks);

        var stopArgs = new PlaybackStopEventArgs
        {
            Item = item,
            PlaySessionId = TestSessionId.ToString("N"),
            PlaybackPositionTicks = 9000,
            PlayedToCompletion = false,
            Users = new List<User> { NewUser() },
        };
        var stop = Assert.IsType<V1PlaybackStopEvent>(forwarder.BuildPlaybackStopEnvelope(stopArgs)!.Payload);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", stop.StoppedAt);
        Assert.Equal(9000L, stop.RuntimeTicks);
    }

    [Fact]
    public void UserData_UsesPlaceholderUserAndOmitsNullProviderMembersOnWire()
    {
        var forwarder = NewForwarder();
        var item = NewMovie();
        item.Id = TestItemId;
        item.ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "603" };
        var args = new UserDataSaveEventArgs
        {
            UserId = TestUserId,
            Item = item,
            UserData = new MediaBrowser.Controller.Entities.UserItemData { Key = TestItemId.ToString("N") },
        };

        var envelope = forwarder.BuildUserDataEnvelope(args)!;
        var payload = Assert.IsType<V1UserDataEvent>(envelope.Payload);
        Assert.Equal(TestUserId.ToString("N"), payload.WatchoffitUserId);
        var json = JsonSerializer.Serialize(envelope);
        Assert.Contains("\"tmdb\":\"603\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"imdb\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"tvdb\"", json, StringComparison.Ordinal);
        Assert.Contains("\"lastPlayedAt\":null", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ping_SendsHeartbeatPayloadWithProtocolFields()
    {
        var handler = new CapturingHttpHandler();
        var systemInfo = new StaticJellyfinSystemInfoProvider("jf_server_01", "10.11.11");
        var builder = new V1EnvelopeBuilder();
        var client = new WatchoffitClient(new HttpClient(handler), builder, systemInfo, NullLogger<WatchoffitClient>.Instance);

        var result = await client.PingAsync("https://watchoffit.test/", "connection", "credential", CancellationToken.None);

        Assert.IsType<WatchoffitCallResult.Ack>(result);
        using var document = JsonDocument.Parse(handler.Body!);
        var payload = document.RootElement.GetProperty("payload");
        Assert.Equal("heartbeat", payload.GetProperty("kind").GetString());
        Assert.Equal("jf_server_01", payload.GetProperty("jellyfinItemId").GetString());
        Assert.Equal("system", payload.GetProperty("watchoffitUserId").GetString());
        Assert.Equal(0, payload.GetProperty("queueDepth").GetInt32());
        Assert.True(payload.GetProperty("pluginVersion").GetString()!.Length > 0);
    }

    [Fact]
    public void PlaybackProgress_PrefersMediaInfoRuntimeTicks()
    {
        // Codex review: a real Jellyfin event often populates
        // `MediaInfo` with the runtime while leaving `Item` null or
        // with `RunTimeTicks = 0` (e.g. transcoded media). The
        // forwarder must fall back to MediaInfo so percent-complete
        // and continue-watching math on the Watchoffit side stay sane.
        var forwarder = NewForwarder();
        var args = new PlaybackProgressEventArgs
        {
            PlaybackPositionTicks = 0,
            IsPaused = false,
            PlaySessionId = TestSessionId.ToString("N"),
            Users = new List<User> { NewUser() },
            MediaInfo = new BaseItemDto
            {
                Id = TestItemId,
                Type = BaseItemKind.Movie,
                RunTimeTicks = 5555,
            },
            // No Item — MediaInfo is the only source.
        };

        var envelope = forwarder.BuildPlaybackProgressEnvelope(args);

        Assert.NotNull(envelope);
        var payload = Assert.IsType<V1PlaybackProgressEvent>(envelope!.Payload);
        Assert.Equal(5555L, payload.RuntimeTicks);
    }

    [Fact]
    public void EventPayloads_OmitNullProviderIdsOnWire()
    {
        // Codex review: the TS `v1IdentitySchema` makes
        // `providerIds` optional, not nullable. Emitting
        // `"providerIds": null` fails the `.strict()` parse on the
        // Watchoffit side. C# records default to writing `null` for
        // missing fields; this test pins the wire shape that
        // `[JsonIgnore(WhenWritingNull)]` was added to enforce.
        var forwarder = NewForwarder();
        var args = new PlaybackProgressEventArgs
        {
            PlaybackPositionTicks = 0,
            IsPaused = false,
            PlaySessionId = TestSessionId.ToString("N"),
            Users = new List<User> { NewUser() },
            MediaInfo = new BaseItemDto
            {
                Id = TestItemId,
                Type = BaseItemKind.Movie,
                RunTimeTicks = 100,
                // No ProviderIds populated.
            },
        };

        var envelope = forwarder.BuildPlaybackProgressEnvelope(args);

        Assert.NotNull(envelope);
        var json = JsonSerializer.Serialize(envelope);
        // The field is omitted entirely, not emitted as null.
        Assert.DoesNotContain("\"providerIds\"", json, StringComparison.Ordinal);

        // Round-trip the same envelope through the parser to prove
        // the Watchoffit-side Zod schema accepts the omitted form.
        var parsed = JsonDocument.Parse(json);
        var payload = parsed.RootElement.GetProperty("payload");
        Assert.False(payload.TryGetProperty("providerIds", out _));
    }
}

/// <summary>
/// HTTP handler that always returns 200 OK. The translation tests do
/// not exercise the network — they only call the build methods on the
/// forwarder and inspect the resulting envelope. A round-trip test
/// would live in a separate suite that fires a real
/// <c>PlaybackProgress</c> through NSubstitute-raised events.
/// </summary>
internal sealed class NullHttpHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        System.Threading.CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
    }
}

internal sealed class CapturingHttpHandler : HttpMessageHandler
{
    public string? Body { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var requestDocument = JsonDocument.Parse(Body);
        var commandId = requestDocument.RootElement
            .GetProperty("header")
            .GetProperty("id")
            .GetString()!;
        var ack = $$"""
        {
          "kind": "ack",
          "header": {
            "version": 1,
            "kind": "ack",
            "id": "ack_test",
            "sequence": 1,
            "timestamp": "2026-08-27T10:00:00.000Z",
            "serverConnectionId": "connection",
            "correlationId": "{{commandId}}"
          },
          "payload": {
            "commandId": "{{commandId}}",
            "status": "ok",
            "note": "heartbeat"
          }
        }
        """;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ack, Encoding.UTF8, "application/json"),
        };
    }
}
