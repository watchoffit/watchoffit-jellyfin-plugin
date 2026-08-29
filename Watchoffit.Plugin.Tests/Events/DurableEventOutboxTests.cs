using System.Net;

using Jellyfin.Plugin.Watchoffit.Events;
using Jellyfin.Plugin.Watchoffit.Pairing;
using Jellyfin.Plugin.Watchoffit.Protocol.V1;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Acks;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Events;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Jellyfin.Plugin.Watchoffit.Tests.Events;

/// <summary>Durability and delivery tests for the plugin event outbox.</summary>
public sealed class DurableEventOutboxTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "watchoffit-outbox-" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    [Fact]
    public void Enqueue_PersistsFullEnvelopeAcrossReload()
    {
        var first = NewOutbox();
        var envelope = NewEnvelope(sequence: 4);

        Assert.Equal(EventOutboxEnqueueResult.Accepted, first.TryEnqueue(envelope));

        var afterRestart = NewOutbox();
        var recovered = Assert.IsType<DurableEventOutboxItem>(afterRestart.TryGetHead());
        Assert.Equal(envelope.Header.Id, recovered.Entry.Envelope.Header.Id);
        Assert.Equal(4, recovered.Entry.Envelope.Header.Sequence);
        Assert.IsType<V1PlaybackProgressEvent>(recovered.Entry.Envelope.Payload);
    }

    [Fact]
    public void Acknowledge_RemovesOnlyTheMatchingPersistedItem()
    {
        var outbox = NewOutbox();
        outbox.TryEnqueue(NewEnvelope(sequence: 1));
        var item = Assert.IsType<DurableEventOutboxItem>(outbox.TryGetHead());

        Assert.True(outbox.Acknowledge(item));

        Assert.Equal(0, outbox.QueueDepth);
        Assert.Null(outbox.TryGetHead());
    }

    [Fact]
    public async Task Worker_RejectsAckWithWrongCorrelationAndRetainsEntry()
    {
        var outbox = NewOutbox();
        var envelope = NewEnvelope(sequence: 1);
        outbox.TryEnqueue(envelope);
        var sender = new FakeSender(new EventOutboxDeliveryResult.AckReceived(NewAck("different-id")));
        var worker = NewWorker(outbox, sender);

        await worker.DeliverNextAsync(CancellationToken.None);

        Assert.Equal(1, outbox.QueueDepth);
        Assert.NotNull(outbox.TryGetHead()!.Entry.NextAttemptAt);
    }

    [Fact]
    public async Task Worker_RetainsRetryableFailureWithBackoff()
    {
        var now = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
        var outbox = NewOutbox();
        outbox.TryEnqueue(NewEnvelope(sequence: 1));
        var worker = NewWorker(
            outbox,
            new FakeSender(new EventOutboxDeliveryResult.RetryableFailure("timeout")),
            now);

        await worker.DeliverNextAsync(CancellationToken.None);

        var retained = Assert.IsType<DurableEventOutboxItem>(outbox.TryGetHead());
        Assert.Single(retained.Entry.FailureTimestamps);
        Assert.True(retained.Entry.NextAttemptAt > now);
        Assert.Equal(0, outbox.DeadLetterDepth);
    }

    [Fact]
    public void RecordRetry_MovesEventToDeadLetterAtRollingThreshold()
    {
        var outbox = NewOutbox();
        outbox.TryEnqueue(NewEnvelope(sequence: 1));
        var now = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

        EventOutboxRetryResult? last = null;
        for (var attempt = 0; attempt < DurableEventOutbox.MaximumFailuresPerHour; attempt++)
        {
            var item = Assert.IsType<DurableEventOutboxItem>(outbox.TryGetHead());
            last = outbox.RecordRetry(item, now, TimeSpan.FromSeconds(1), "timeout");
        }

        Assert.NotNull(last);
        Assert.True(last.DeadLettered);
        Assert.Equal(0, outbox.QueueDepth);
        Assert.Equal(1, outbox.DeadLetterDepth);
    }

    [Fact]
    public async Task Worker_DeliversStrictlyBySequence()
    {
        var outbox = NewOutbox();
        var later = NewEnvelope(sequence: 2);
        var earlier = NewEnvelope(sequence: 1);
        outbox.TryEnqueue(later);
        outbox.TryEnqueue(earlier);
        var sender = new FakeSender(
            new EventOutboxDeliveryResult.AckReceived(NewAck(earlier.Header.Id)),
            new EventOutboxDeliveryResult.AckReceived(NewAck(later.Header.Id)));
        var worker = NewWorker(outbox, sender);

        await worker.DeliverNextAsync(CancellationToken.None);
        await worker.DeliverNextAsync(CancellationToken.None);

        Assert.Equal([earlier.Header.Id, later.Header.Id], sender.SentIds);
        Assert.Equal(0, outbox.QueueDepth);
    }

    [Fact]
    public void Enqueue_RejectsNewEventsWhenBoundedQueueIsFull()
    {
        var outbox = NewOutbox(capacity: 1);

        Assert.Equal(EventOutboxEnqueueResult.Accepted, outbox.TryEnqueue(NewEnvelope(sequence: 1)));
        Assert.Equal(EventOutboxEnqueueResult.Full, outbox.TryEnqueue(NewEnvelope(sequence: 2)));
        Assert.Equal(1, outbox.QueueDepth);
        Assert.Equal(1, outbox.TryGetHead()!.Entry.Envelope.Header.Sequence);
    }

    [Fact]
    public async Task Worker_AcknowledgesRecoveredItemAfterRestart()
    {
        var original = NewOutbox();
        var envelope = NewEnvelope(sequence: 1);
        original.TryEnqueue(envelope);

        var reloaded = NewOutbox();
        var worker = NewWorker(
            reloaded,
            new FakeSender(new EventOutboxDeliveryResult.AckReceived(NewAck(envelope.Header.Id))));

        await worker.DeliverNextAsync(CancellationToken.None);

        Assert.Equal(0, reloaded.QueueDepth);
        Assert.Equal(0, NewOutbox().QueueDepth);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public async Task Sender_ClassifiesHttpFailures(HttpStatusCode statusCode, bool terminal)
    {
        var systemInfo = new StaticJellyfinSystemInfoProvider("jf-server", "10.11.11");
        var client = new WatchoffitClient(
            new HttpClient(new StatusCodeHandler(statusCode)),
            new V1EnvelopeBuilder(),
            systemInfo,
            NullLogger<WatchoffitClient>.Instance);
        var store = new WatchoffitConnectionStore(_dataPath, new PlainCredentialProtector(), NullLogger<WatchoffitConnectionStore>.Instance);
        store.Save(NewConnection());
        var pairing = new PairingService(store, client, systemInfo, NullLogger<PairingService>.Instance);
        pairing.LoadFromStore();
        var sender = new WatchoffitEventOutboxSender(client, pairing);

        var result = await sender.SendAsync(NewEnvelope(sequence: 1), CancellationToken.None);

        Assert.Equal(terminal, result is EventOutboxDeliveryResult.TerminalFailure);
        Assert.Equal(!terminal, result is EventOutboxDeliveryResult.RetryableFailure);
    }

    private DurableEventOutbox NewOutbox(int capacity = DurableEventOutbox.DefaultCapacity) =>
        new(_dataPath, NullLogger<DurableEventOutbox>.Instance, capacity);

    private static EventOutboxWorker NewWorker(
        DurableEventOutbox outbox,
        IEventOutboxSender sender,
        DateTimeOffset? now = null) =>
        new(
            outbox,
            sender,
            NullLogger<EventOutboxWorker>.Instance,
            () => now ?? DateTimeOffset.UtcNow,
            () => 0.5d);

    private static V1EventEnvelope NewEnvelope(long sequence) => new()
    {
        Header = new V1Header
        {
            Version = V1ProtocolConstants.ProtocolVersion,
            Kind = V1EnvelopeKind.Event,
            Id = $"evt-{sequence}-{Guid.NewGuid():N}",
            Sequence = sequence,
            Timestamp = "2026-08-27T12:00:00.000Z",
            ServerConnectionId = "scn-test",
        },
        Payload = new V1PlaybackProgressEvent
        {
            JellyfinItemId = "jellyfin-item",
            WatchoffitUserId = "jellyfin-user",
            MediaKind = V1MediaKind.Movie,
            SessionId = "session",
            PositionTicks = 1,
            RuntimeTicks = 10,
            IsPaused = false,
        },
    };

    private static V1AckEnvelope NewAck(string eventId) => new()
    {
        Header = new V1Header
        {
            Version = V1ProtocolConstants.ProtocolVersion,
            Kind = V1EnvelopeKind.Ack,
            Id = "ack-" + Guid.NewGuid().ToString("N"),
            Sequence = 1,
            Timestamp = "2026-08-27T12:00:00.000Z",
            ServerConnectionId = "scn-test",
            CorrelationId = eventId,
        },
        Payload = new V1BaseAck { CommandId = eventId },
    };

    private static WatchoffitConnection NewConnection() => new()
    {
        State = PairingState.Paired,
        BaseUrl = "https://watchoffit.test/",
        ServerConnectionId = "scn-test",
        WatchoffitServerName = "Watchoffit",
        JellyfinServerId = "jf-server",
        Credential = new WatchoffitCredential { Value = "credential" },
        CreatedAt = "2026-08-27T12:00:00.000Z",
    };

    private sealed class FakeSender : IEventOutboxSender
    {
        private readonly Queue<EventOutboxDeliveryResult> _responses;

        public FakeSender(params EventOutboxDeliveryResult[] responses)
        {
            _responses = new Queue<EventOutboxDeliveryResult>(responses);
        }

        public List<string> SentIds { get; } = [];

        public Task<EventOutboxDeliveryResult> SendAsync(V1EventEnvelope envelope, CancellationToken cancellationToken)
        {
            SentIds.Add(envelope.Header.Id);
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class StatusCodeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public StatusCodeHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_statusCode));
    }
}
