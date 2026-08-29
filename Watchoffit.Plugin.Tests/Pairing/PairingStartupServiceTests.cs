using Jellyfin.Plugin.Watchoffit.Pairing;
using Jellyfin.Plugin.Watchoffit.Protocol.V1;
using Jellyfin.Plugin.Watchoffit.Events;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Events;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Jellyfin.Plugin.Watchoffit.Tests.Pairing;

/// <summary>Tests the startup boundary that restores a persisted pairing.</summary>
public sealed class PairingStartupServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "watchoffit-startup-" + Guid.NewGuid().ToString("N"));

    public PairingStartupServiceTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task StartAsync_LoadsPersistedPairedConnection()
    {
        var store = new WatchoffitConnectionStore(
            _tempDir,
            new PlainCredentialProtector(),
            NullLogger<WatchoffitConnectionStore>.Instance);
        store.Save(new WatchoffitConnection
        {
            State = PairingState.Paired,
            BaseUrl = "https://watchoffit.example.com",
            ServerConnectionId = "scn_01",
            Credential = new WatchoffitCredential { Scheme = "plain", Value = "credential" },
        });
        using var http = new HttpClient { BaseAddress = new Uri("https://watchoffit.example.com/") };
        var systemInfo = new StaticJellyfinSystemInfoProvider("jellyfin_01", "10.11.11");
        var builder = new V1EnvelopeBuilder();
        var pairing = new PairingService(
            store,
            new WatchoffitClient(http, builder, systemInfo, NullLogger<WatchoffitClient>.Instance),
            systemInfo,
            NullLogger<PairingService>.Instance);
        var outbox = new DurableEventOutbox(_tempDir, NullLogger<DurableEventOutbox>.Instance);
        var startup = new PairingStartupService(
            pairing,
            outbox,
            builder,
            NullLogger<PairingStartupService>.Instance);

        await startup.StartAsync(CancellationToken.None);

        Assert.Equal(PairingState.Paired, pairing.CurrentState);
        Assert.Equal("scn_01", pairing.CurrentConnection?.ServerConnectionId);
    }

    [Fact]
    public async Task StartAsync_RestoresEventSequenceWatermark()
    {
        var store = new WatchoffitConnectionStore(
            _tempDir,
            new PlainCredentialProtector(),
            NullLogger<WatchoffitConnectionStore>.Instance);
        using var http = new HttpClient { BaseAddress = new Uri("https://watchoffit.example.com/") };
        var systemInfo = new StaticJellyfinSystemInfoProvider("jellyfin_01", "10.11.11");
        var builder = new V1EnvelopeBuilder();
        var pairing = new PairingService(
            store,
            new WatchoffitClient(http, builder, systemInfo, NullLogger<WatchoffitClient>.Instance),
            systemInfo,
            NullLogger<PairingService>.Instance);
        var outbox = new DurableEventOutbox(_tempDir, NullLogger<DurableEventOutbox>.Instance);
        Assert.Equal(
            EventOutboxEnqueueResult.Accepted,
            outbox.TryEnqueue(NewQueuedEvent(sequence: 41)));

        var startup = new PairingStartupService(
            pairing,
            outbox,
            builder,
            NullLogger<PairingStartupService>.Instance);

        await startup.StartAsync(CancellationToken.None);

        Assert.Equal(41, builder.CurrentSequence);
        Assert.Equal(42, builder.NextSequence());
    }

    private static V1EventEnvelope NewQueuedEvent(long sequence) => new()
    {
        Header = new V1Header
        {
            Version = V1ProtocolConstants.ProtocolVersion,
            Kind = V1EnvelopeKind.Event,
            Id = "evt_startup_watermark",
            Sequence = sequence,
            Timestamp = "2026-08-28T10:00:00.000Z",
            ServerConnectionId = "scn_01",
        },
        Payload = new V1UserDataEvent
        {
            JellyfinItemId = "item_01",
            WatchoffitUserId = "user_01",
            MediaKind = V1MediaKind.Movie,
            Played = true,
            PlayCount = 1,
            IsFavorite = false,
        },
    };
}
