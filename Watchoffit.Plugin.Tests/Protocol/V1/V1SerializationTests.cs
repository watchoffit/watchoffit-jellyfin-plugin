using System.Text.Json;

using Jellyfin.Plugin.Watchoffit.Protocol.V1;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Acks;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Commands;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Events;

using Xunit;

namespace Jellyfin.Plugin.Watchoffit.Tests.Protocol.V1;

/// <summary>
/// Serialization tests for the v1 wire contract. Optional fields must be
/// absent when their C# value is null, while nullable fields must retain an
/// explicit JSON null so Watchoffit can distinguish the two states.
/// </summary>
public class V1SerializationTests
{
    private const string FixtureDir = "fixtures/v1";

    private static readonly string[] BackfillMovieAndEpisodeLiterals = ["movie", "episode"];

    [Fact]
    public void Serialize_UserDataEvent_OmitsOptionalNullsAndKeepsExplicitNullableNull()
    {
        V1EventPayload payload = new V1UserDataEvent
        {
            JellyfinItemId = "jf-item-1",
            WatchoffitUserId = "user-1",
            MediaKind = V1MediaKind.Movie,
            Played = false,
            PlayCount = 0,
            IsFavorite = null,
            LastPlayedAt = null,
            ProviderIds = null,
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var root = document.RootElement;

        Assert.False(root.TryGetProperty("providerIds", out _));
        Assert.False(root.TryGetProperty("isFavorite", out _));
        Assert.True(root.TryGetProperty("lastPlayedAt", out var lastPlayedAt));
        Assert.Equal(JsonValueKind.Null, lastPlayedAt.ValueKind);
    }

    [Fact]
    public void Serialize_OptionalCommandFieldsAndProviderMembers_AreOmittedWhenNull()
    {
        V1CommandPayload payload = new V1MarkPlayedCommand
        {
            JellyfinItemId = "jf-item-1",
            WatchoffitUserId = "user-1",
            MediaKind = V1MediaKind.Movie,
            ProviderIds = new V1ProviderIds { Tmdb = "603" },
            WatchedAt = null,
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var root = document.RootElement;

        Assert.False(root.TryGetProperty("watchedAt", out _));
        Assert.True(root.TryGetProperty("providerIds", out var providerIds));
        Assert.Equal("603", providerIds.GetProperty("tmdb").GetString());
        Assert.False(providerIds.TryGetProperty("imdb", out _));
        Assert.False(providerIds.TryGetProperty("tvdb", out _));
    }

    [Fact]
    public void Serialize_UnsolicitedError_OmitsOptionalCommandId()
    {
        var payload = new V1ErrorPayload
        {
            Code = "INTERNAL_ERROR",
            Message = "plugin reconnecting",
            CommandId = null,
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));

        Assert.False(document.RootElement.TryGetProperty("commandId", out _));
    }

    [Fact]
    public void Serialize_BaseAck_OmitsOptionalNote()
    {
        V1AckPayload payload = new V1BaseAck
        {
            CommandId = "cmd-1",
            Status = V1AckStatus.Ok,
            Note = null,
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));

        Assert.False(document.RootElement.TryGetProperty("note", out _));
    }

    [Fact]
    public void Serialize_InventoryManifestEvent_UsesExpectedWireShape()
    {
        V1EventPayload payload = new V1InventoryManifestEvent
        {
            Provider = "jellyfin",
            Generation = 4,
            CapturedAt = "2026-08-28T10:00:00.000Z",
            ChunkIndex = 0,
            ChunkCount = 1,
            Server = new V1InventoryServer { RemoteServerId = "server-1", Name = "Family", Version = "10.11.11", PluginVersion = "1.0.0" },
            Users = [new V1InventoryUser { RemoteUserId = "user-1", Name = "Alex", IsAdministrator = true, IsDisabled = false }],
            Libraries = [new V1InventoryLibrary { RemoteLibraryId = "library-1", Name = "Movies", CollectionType = "movies" }],
            UserLibraries = [new V1InventoryUserLibrary { RemoteUserId = "user-1", RemoteLibraryId = "library-1" }],
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var root = document.RootElement;

        Assert.Equal("inventory_manifest", root.GetProperty("kind").GetString());
        Assert.Equal("jellyfin", root.GetProperty("provider").GetString());
        Assert.Equal("server-1", root.GetProperty("server").GetProperty("remoteServerId").GetString());
        Assert.Equal("library-1", root.GetProperty("userLibraries")[0].GetProperty("remoteLibraryId").GetString());
    }

    [Fact]
    public void Serialize_CanonicalFixtures_RemainJsonEquivalent()
    {
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, FixtureDir);

        foreach (var fixturePath in Directory.GetFiles(fixtureDirectory, "*.json"))
        {
            var fixtureJson = File.ReadAllText(fixturePath);
            var result = V1EnvelopeParser.Parse(fixtureJson);
            var ok = Assert.IsType<V1ParseResult.Ok>(result);

            using var expected = JsonDocument.Parse(fixtureJson);
            using var actual = JsonDocument.Parse(JsonSerializer.Serialize<V1Envelope>(ok.Envelope));
            Assert.True(
                JsonElement.DeepEquals(expected.RootElement, actual.RootElement),
                $"Serialized fixture differs: {Path.GetFileName(fixturePath)}\nExpected: {expected.RootElement}\nActual: {actual.RootElement}");
        }
    }

    [Fact]
    public void Serialize_BackfillRequestCommand_OmitsOptionalNullsAndMatchesWireShape()
    {
        // The per-library backfill command. Optional `libraryId` and
        // `mediaKinds` must be absent (not `null`) when the C# value is
        // null so the wire stays byte-for-byte compatible with the
        // TypeScript schema.
        V1CommandPayload payload = new V1BackfillRequestCommand
        {
            WatchoffitUserId = "user-1",
            LibraryId = "library-movies",
            MediaKinds = [V1MediaKind.Movie, V1MediaKind.Episode],
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var root = document.RootElement;

        Assert.Equal("backfill_request", root.GetProperty("kind").GetString());
        Assert.Equal("user-1", root.GetProperty("watchoffitUserId").GetString());
        Assert.Equal("library-movies", root.GetProperty("libraryId").GetString());
        var kinds = root.GetProperty("mediaKinds").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(BackfillMovieAndEpisodeLiterals, kinds);
    }

    [Fact]
    public void Serialize_BackfillRequestCommand_AllLibrariesSweep_OmitsLibraryIdAndMediaKinds()
    {
        V1CommandPayload payload = new V1BackfillRequestCommand
        {
            WatchoffitUserId = "user-1",
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var root = document.RootElement;

        Assert.Equal("backfill_request", root.GetProperty("kind").GetString());
        Assert.False(root.TryGetProperty("libraryId", out _));
        Assert.False(root.TryGetProperty("mediaKinds", out _));
    }

    [Fact]
    public void RoundTrip_BackfillRequestCommand_PreservesAllFields()
    {
        // The polymorphic `JsonDerivedType` attribute must dispatch the
        // payload back to `V1BackfillRequestCommand` on deserialize.
        V1CommandPayload payload = new V1BackfillRequestCommand
        {
            WatchoffitUserId = "user-1",
            LibraryId = "library-movies",
            MediaKinds = [V1MediaKind.Movie],
        };

        var json = JsonSerializer.Serialize<V1CommandPayload>(payload);
        var roundTripped = JsonSerializer.Deserialize<V1CommandPayload>(json);

        var backfill = Assert.IsType<V1BackfillRequestCommand>(roundTripped);
        Assert.Equal("user-1", backfill.WatchoffitUserId);
        Assert.Equal("library-movies", backfill.LibraryId);
        Assert.Single(backfill.MediaKinds!);
        Assert.Equal(V1MediaKind.Movie, backfill.MediaKinds![0]);
    }

    [Fact]
    public void Serialize_SyncCompletedEvent_MatchesWireShape()
    {
        V1EventPayload payload = new V1SyncCompletedEvent
        {
            WatchoffitUserId = "user-1",
            SyncKind = V1SyncKind.Backfill,
            LibraryId = "library-movies",
            Total = 1500,
            Processed = 1480,
            Failed = 20,
            CompletedAt = "2026-08-28T20:34:41.000Z",
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var root = document.RootElement;

        Assert.Equal("sync_completed", root.GetProperty("kind").GetString());
        Assert.Equal("user-1", root.GetProperty("watchoffitUserId").GetString());
        Assert.Equal("backfill", root.GetProperty("syncKind").GetString());
        Assert.Equal("library-movies", root.GetProperty("libraryId").GetString());
        Assert.Equal(1500, root.GetProperty("total").GetInt32());
        Assert.Equal(1480, root.GetProperty("processed").GetInt32());
        Assert.Equal(20, root.GetProperty("failed").GetInt32());
        Assert.Equal("2026-08-28T20:34:41.000Z", root.GetProperty("completedAt").GetString());
    }

    [Fact]
    public void Serialize_SyncCompletedEvent_AllLibrariesSweep_OmitsLibraryId()
    {
        V1EventPayload payload = new V1SyncCompletedEvent
        {
            WatchoffitUserId = "user-1",
            SyncKind = V1SyncKind.Reconcile,
            Total = 200,
            Processed = 200,
            Failed = 0,
            CompletedAt = "2026-08-28T20:34:41.000Z",
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var root = document.RootElement;

        Assert.Equal("reconcile", root.GetProperty("syncKind").GetString());
        Assert.False(root.TryGetProperty("libraryId", out _));
    }

    [Fact]
    public void RoundTrip_SyncCompletedEvent_PreservesAllFields()
    {
        V1EventPayload payload = new V1SyncCompletedEvent
        {
            WatchoffitUserId = "user-1",
            SyncKind = V1SyncKind.Backfill,
            LibraryId = "library-movies",
            Total = 1500,
            Processed = 1480,
            Failed = 20,
            CompletedAt = "2026-08-28T20:34:41.000Z",
        };

        var json = JsonSerializer.Serialize<V1EventPayload>(payload);
        var roundTripped = JsonSerializer.Deserialize<V1EventPayload>(json);

        var syncCompleted = Assert.IsType<V1SyncCompletedEvent>(roundTripped);
        Assert.Equal("user-1", syncCompleted.WatchoffitUserId);
        Assert.Equal(V1SyncKind.Backfill, syncCompleted.SyncKind);
        Assert.Equal("library-movies", syncCompleted.LibraryId);
        Assert.Equal(1500, syncCompleted.Total);
        Assert.Equal(1480, syncCompleted.Processed);
        Assert.Equal(20, syncCompleted.Failed);
    }

    [Fact]
    public void SyncKindJsonConverter_RejectsUnknownLiteral()
    {
        // Wire literal outside the protocol's enum must throw at parse
        // time, never silently coerce to a default.
        var ex = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<V1SyncKind>("\"unknown\""));
        Assert.Contains("Unknown V1SyncKind literal", ex.Message, StringComparison.Ordinal);
    }
}
