using Jellyfin.Plugin.Watchoffit.Pairing;
using Jellyfin.Plugin.Watchoffit.Protocol.V1;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Watchoffit.Events;

/// <summary>
/// Sends a persisted event envelope using the currently paired Watchoffit connection.
/// </summary>
internal interface IEventOutboxSender
{
    /// <summary>
    /// Deliver a queued envelope without changing its durable state.
    /// </summary>
    /// <param name="envelope">The persisted envelope to send unchanged.</param>
    /// <param name="cancellationToken">Cancellation token for plugin shutdown.</param>
    /// <returns>The classified remote-delivery result.</returns>
    Task<EventOutboxDeliveryResult> SendAsync(V1EventEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>Classified result of one queued event delivery attempt.</summary>
internal abstract record EventOutboxDeliveryResult
{
    /// <summary>A remote acknowledgement was received.</summary>
    internal sealed record AckReceived(V1AckEnvelope Envelope) : EventOutboxDeliveryResult;

    /// <summary>A structured remote application error was received.</summary>
    internal sealed record ApplicationErrorReceived(V1ErrorEnvelope Envelope) : EventOutboxDeliveryResult;

    /// <summary>The request could be retried after a transport failure.</summary>
    internal sealed record RetryableFailure(string Reason) : EventOutboxDeliveryResult;

    /// <summary>No active pairing is available yet; retain the entry without consuming retries.</summary>
    internal sealed record NotConnected : EventOutboxDeliveryResult;

    /// <summary>The active pairing changed and the queued event cannot be validly delivered.</summary>
    internal sealed record TerminalFailure(string Reason) : EventOutboxDeliveryResult;
}

/// <summary>
/// Production sender that translates the pairing client result into outbox semantics.
/// </summary>
internal sealed class WatchoffitEventOutboxSender : IEventOutboxSender
{
    private readonly WatchoffitClient _client;
    private readonly PairingService _pairing;

    /// <summary>Initializes a new instance of the <see cref="WatchoffitEventOutboxSender"/> class.</summary>
    /// <param name="client">HTTP client for Watchoffit protocol calls.</param>
    /// <param name="pairing">Current paired connection state.</param>
    public WatchoffitEventOutboxSender(WatchoffitClient client, PairingService pairing)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _pairing = pairing ?? throw new ArgumentNullException(nameof(pairing));
    }

    /// <inheritdoc />
    public async Task<EventOutboxDeliveryResult> SendAsync(V1EventEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var connection = _pairing.CurrentConnection;
        if (connection is null)
        {
            return new EventOutboxDeliveryResult.NotConnected();
        }

        if (!string.Equals(connection.ServerConnectionId, envelope.Header.ServerConnectionId, StringComparison.Ordinal))
        {
            return new EventOutboxDeliveryResult.TerminalFailure("paired server connection changed");
        }

        var result = await _client.SendEventAsync(
                connection.BaseUrl,
                connection.ServerConnectionId,
                connection.Credential.Value,
                envelope,
                cancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            WatchoffitCallResult.Ack ack => new EventOutboxDeliveryResult.AckReceived(ack.Envelope),
            WatchoffitCallResult.ApplicationError error => new EventOutboxDeliveryResult.ApplicationErrorReceived(error.Envelope),
            WatchoffitCallResult.TransportFailure failure when IsTerminalHttpStatus(failure.StatusCode) =>
                new EventOutboxDeliveryResult.TerminalFailure($"HTTP {failure.StatusCode}"),
            WatchoffitCallResult.TransportFailure failure => new EventOutboxDeliveryResult.RetryableFailure(failure.Reason),
            _ => new EventOutboxDeliveryResult.RetryableFailure("unknown Watchoffit event result"),
        };
    }

    private static bool IsTerminalHttpStatus(int? statusCode) =>
        statusCode is >= 400 and < 500 and not 408 and not 429;
}

/// <summary>
/// Single-consumer background worker for the durable Jellyfin event outbox.
/// </summary>
/// <remarks>
/// The worker only ever looks at the lowest sequence entry, so retries block
/// later events and preserve wire ordering. An acknowledgement has to prove it
/// belongs to that entry before the corresponding file is deleted.
/// </remarks>
internal sealed class EventOutboxWorker : BackgroundService
{
    private static readonly TimeSpan DisconnectedPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(5);

    private readonly DurableEventOutbox _outbox;
    private readonly IEventOutboxSender _sender;
    private readonly ILogger _logger;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<double> _nextJitter;

    /// <summary>Initializes a new instance of the <see cref="EventOutboxWorker"/> class.</summary>
    /// <param name="outbox">Durable event store to consume.</param>
    /// <param name="sender">Authenticated Watchoffit transport.</param>
    /// <param name="logger">Queue diagnostics logger.</param>
    public EventOutboxWorker(
        DurableEventOutbox outbox,
        IEventOutboxSender sender,
        ILogger<EventOutboxWorker> logger)
        : this(outbox, sender, logger, () => DateTimeOffset.UtcNow, Random.Shared.NextDouble)
    {
    }

    internal EventOutboxWorker(
        DurableEventOutbox outbox,
        IEventOutboxSender sender,
        ILogger<EventOutboxWorker> logger,
        Func<DateTimeOffset> utcNow,
        Func<double> nextJitter)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _nextJitter = nextJitter ?? throw new ArgumentNullException(nameof(nextJitter));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = await DeliverNextAsync(stoppingToken).ConfigureAwait(false);
            if (wait is null)
            {
                await _outbox.WaitForChangeAsync(stoppingToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Delay(wait.Value, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Process the queue head once. Exposed internally for focused durable-outbox tests.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for plugin shutdown.</param>
    /// <returns><see langword="null"/> when the queue is empty; otherwise the delay before the next pass.</returns>
    internal async Task<TimeSpan?> DeliverNextAsync(CancellationToken cancellationToken)
    {
        var item = _outbox.TryGetHead();
        if (item is null)
        {
            return null;
        }

        var now = _utcNow();
        if (item.Entry.NextAttemptAt is { } nextAttemptAt && nextAttemptAt > now)
        {
            return nextAttemptAt - now;
        }

        var result = await _sender.SendAsync(item.Entry.Envelope, cancellationToken).ConfigureAwait(false);
        switch (result)
        {
            case EventOutboxDeliveryResult.AckReceived ack when IsAcknowledgementFor(ack.Envelope, item.Entry.Envelope):
                if (!_outbox.Acknowledge(item))
                {
                    _logger.LogWarning(
                        "Watchoffit event {EnvelopeId} acknowledgement arrived after the queue entry changed",
                        item.Entry.Envelope.Header.Id);
                }

                return TimeSpan.Zero;

            case EventOutboxDeliveryResult.AckReceived:
                return Retry(item, now, "received acknowledgement for a different event");

            case EventOutboxDeliveryResult.ApplicationErrorReceived applicationError
                when IsResponseFor(applicationError.Envelope, item.Entry.Envelope)
                    && IsRetryableApplicationError(applicationError.Envelope.Payload.Code):
                return Retry(item, now, $"remote application error: {applicationError.Envelope.Payload.Code}");

            case EventOutboxDeliveryResult.ApplicationErrorReceived applicationError
                when IsResponseFor(applicationError.Envelope, item.Entry.Envelope):
                _outbox.MoveToDeadLetter(
                    item,
                    now,
                    $"terminal remote application error: {applicationError.Envelope.Payload.Code}");
                return TimeSpan.Zero;

            case EventOutboxDeliveryResult.ApplicationErrorReceived:
                return Retry(item, now, "received application error for a different event");

            case EventOutboxDeliveryResult.RetryableFailure failure:
                return Retry(item, now, failure.Reason);

            case EventOutboxDeliveryResult.TerminalFailure failure:
                _outbox.MoveToDeadLetter(item, now, failure.Reason);
                return TimeSpan.Zero;

            case EventOutboxDeliveryResult.NotConnected:
                return DisconnectedPollInterval;

            default:
                return Retry(item, now, "unrecognised event delivery result");
        }
    }

    private TimeSpan Retry(DurableEventOutboxItem item, DateTimeOffset now, string reason)
    {
        var delay = CalculateBackoff(item.Entry, now);
        var outcome = _outbox.RecordRetry(item, now, delay, reason);
        if (!outcome.DeadLettered)
        {
            _logger.LogWarning(
                "Watchoffit event {EnvelopeId} sequence {Sequence} delivery failed ({Reason}); retry {FailureCount} in {Delay}",
                item.Entry.Envelope.Header.Id,
                item.Entry.Envelope.Header.Sequence,
                reason,
                outcome.FailureCount,
                delay);
        }

        return outcome.DeadLettered ? TimeSpan.Zero : delay;
    }

    private TimeSpan CalculateBackoff(DurableEventOutboxEntry entry, DateTimeOffset now)
    {
        var failuresInWindow = entry.FailureTimestamps.Count(timestamp => timestamp >= now - TimeSpan.FromHours(1));
        var unjitteredSeconds = Math.Min(
            MaximumBackoff.TotalSeconds,
            Math.Pow(2, Math.Min(failuresInWindow, 16)));
        var jitter = 0.8d + (Math.Clamp(_nextJitter(), 0d, 1d) * 0.4d);
        return TimeSpan.FromMilliseconds(Math.Min(MaximumBackoff.TotalMilliseconds, unjitteredSeconds * 1000d * jitter));
    }

    private static bool IsAcknowledgementFor(V1AckEnvelope acknowledgement, V1EventEnvelope envelope) =>
        IsResponseFor(acknowledgement, envelope)
        && string.Equals(acknowledgement.Payload.CommandId, envelope.Header.Id, StringComparison.Ordinal);

    private static bool IsResponseFor(V1Envelope response, V1EventEnvelope envelope) =>
        string.Equals(response.Header.CorrelationId, envelope.Header.Id, StringComparison.Ordinal);

    private static bool IsRetryableApplicationError(string code) =>
        string.Equals(code, "OUTBOX_FULL", StringComparison.Ordinal)
        || string.Equals(code, "RATE_LIMITED_BY_REMOTE", StringComparison.Ordinal);
}
