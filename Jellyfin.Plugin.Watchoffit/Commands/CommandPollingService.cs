using System.Net;

using Jellyfin.Plugin.Watchoffit.Pairing;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Acks;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Watchoffit.Commands;

/// <summary>
/// Long-polls Watchoffit for queued commands and acks the lease results.
/// The service mirrors the
/// <c>EventForwarderHostedService</c> lifecycle: it owns no state
/// itself, reads <see cref="PairingService.CurrentState"/> + <see cref="PairingService.CurrentConnection"/>
/// every tick, and gates network I/O on the local pairing being
/// <see cref="PairingState.Paired"/>.
/// </summary>
/// <remarks>
/// This is the ping-only baseline. The polling channel itself
/// (request/response shape, lease echo, ack flow) is fully wired;
/// the only registered <see cref="ICommandHandler"/> is
/// <see cref="Handlers.PingCommandHandler"/>, so a real server that
/// never enqueues a <c>ping</c> still completes the round-trip but
/// executes zero handler work. Future commits add
/// <c>mark_played</c> / <c>reconcile_request</c> / etc. handlers
/// without touching the polling loop itself.
/// </remarks>
public sealed class CommandPollingService : BackgroundService
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    private readonly WatchoffitClient _client;
    private readonly PairingService _pairing;
    private readonly ICommandHandlerRegistry _handlers;
    private readonly ILogger _logger;
    private readonly TimeSpan _pollInterval;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommandPollingService"/> class.
    /// </summary>
    /// <param name="client">HTTP client used to post poll + ack envelopes to Watchoffit.</param>
    /// <param name="pairing">Source of the active <see cref="PairingState"/> and credential.</param>
    /// <param name="handlers">Lookup of registered <see cref="ICommandHandler"/> instances.</param>
    /// <param name="logger">Plugin diagnostics logger. Failures and revoked-state transitions are logged at <c>Error</c>; the routine idle pass is at <c>Debug</c>.</param>
    public CommandPollingService(
        WatchoffitClient client,
        PairingService pairing,
        ICommandHandlerRegistry handlers,
        ILogger<CommandPollingService> logger)
        : this(client, pairing, handlers, logger, DefaultPollInterval)
    {
    }

    /// <summary>
    /// Test-friendly constructor. Same as the production constructor
    /// but with an overridable poll interval so unit tests can drive
    /// the loop deterministically.
    /// </summary>
    /// <param name="client">HTTP client used to post poll + ack envelopes to Watchoffit.</param>
    /// <param name="pairing">Source of the active <see cref="PairingState"/> and credential.</param>
    /// <param name="handlers">Lookup of registered <see cref="ICommandHandler"/> instances.</param>
    /// <param name="logger">Plugin diagnostics logger.</param>
    /// <param name="pollInterval">Sleep between poll ticks. Production uses 5 s; tests pass something sub-100 ms.</param>
    internal CommandPollingService(
        WatchoffitClient client,
        PairingService pairing,
        ICommandHandlerRegistry handlers,
        ILogger<CommandPollingService> logger,
        TimeSpan pollInterval)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(logger);

        if (pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), pollInterval, "poll interval must be positive");
        }

        _client = client;
        _pairing = pairing;
        _handlers = handlers;
        _logger = logger;
        _pollInterval = pollInterval;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Plugin shutdown — the loop exits without logging.
                throw;
            }
            catch (Exception ex)
            {
                // A bug inside the polling loop (handler dispatch,
                // envelope shape, etc.) must not crash the worker.
                // The next tick retries with a fresh lease state.
                _logger.LogError(ex, "Watchoffit command polling loop threw; will retry next tick");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Run exactly one poll tick: snapshot the pairing, post the
    /// poll envelope, dispatch + ack every leased command. Exposed
    /// internally so the unit tests can drive the loop without
    /// waiting for the real 5 s sleep.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for plugin shutdown.</param>
    /// <returns>A task that completes when the tick (including any per-command acks) is done.</returns>
    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var connection = _pairing.CurrentConnection;
        if (connection is null || connection.State != PairingState.Paired)
        {
            // No credential to send with — skip silently. The next
            // tick will see the state again; if the operator pairs
            // in the meantime, the worker picks up on the following
            // pass without a restart.
            return;
        }

        var result = await _client.PollCommandAsync(
                connection.BaseUrl,
                connection.ServerConnectionId,
                connection.Credential.Value,
                cancellationToken)
            .ConfigureAwait(false);

        switch (result)
        {
            case WatchoffitCallResult.Ack ack:
                _pairing.MarkContactSucceeded(DateTimeOffset.UtcNow);
                await HandlePollAckAsync(connection, ack.Envelope, cancellationToken).ConfigureAwait(false);
                break;

            case WatchoffitCallResult.ApplicationError applicationError:
                _logger.LogWarning(
                    "Watchoffit command poll refused with {Code}: {Message}",
                    applicationError.Envelope.Payload.Code,
                    applicationError.Envelope.Payload.Message);
                break;

            case WatchoffitCallResult.TransportFailure failure:
                HandleTransportFailure(failure);
                break;

            default:
                _logger.LogWarning(
                    "Watchoffit command poll returned an unknown result type {Type}; skipping this tick",
                    result.GetType().Name);
                break;
        }
    }

    private async Task HandlePollAckAsync(
        WatchoffitConnection connection,
        Protocol.V1.V1AckEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (envelope.Payload is not V1CommandPollAckPayload poll)
        {
            // The server only emits `command_poll_ack` for this
            // route today, but the wire format allows a generic
            // ack. A future server that switches back to the
            // generic shape would be silently lost here, so we
            // log a warning that the polling channel needs to
            // revisit. The brief explicitly requires we do NOT
            // fall through silently on unknown payload kinds, so
            // this is a hard surfacing, not a no-op.
            _logger.LogWarning(
                "Watchoffit command poll returned a non-command-poll ack payload ({Kind}); skipping",
                envelope.Payload.GetType().Name);
            return;
        }

        if (poll.Commands.Count == 0)
        {
            _logger.LogDebug("Watchoffit command poll returned no leased commands");
            return;
        }

        foreach (var leased in poll.Commands)
        {
            await DispatchAndAckAsync(connection, leased, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DispatchAndAckAsync(
        WatchoffitConnection connection,
        V1LeasedCommand leased,
        CancellationToken cancellationToken)
    {
        if (!_handlers.TryGet(leased.CommandKind, out var handler))
        {
            // The plugin does not have a handler for this kind yet
            // (e.g. `mark_played` lives in a later commit). The
            // server's lease-reap cron will reclaim the row, so
            // there is nothing to do here. We still ack with
            // `noop` so the server's dedup table has a consistent
            // entry and the next poll is not blocked on this id.
            _logger.LogWarning(
                "Watchoffit leased command {CommandId} (kind={CommandKind}) has no plugin handler; acking as noop",
                leased.CommandId,
                leased.CommandKind);

            await TryAckAsync(
                connection,
                leased,
                V1CommandResult.Noop(),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        V1CommandResult result;
        try
        {
            result = await handler.HandleAsync(leased, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A buggy handler MUST NOT block the rest of the poll
            // batch. We log and ack as `noop` so the server's
            // state machine can move on; the operator sees the
            // exception in the plugin log.
            _logger.LogError(
                ex,
                "Command handler {Kind} threw for leased command {CommandId}; acking as noop",
                leased.CommandKind,
                leased.CommandId);
            result = V1CommandResult.Noop();
        }

        await TryAckAsync(connection, leased, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task TryAckAsync(
        WatchoffitConnection connection,
        V1LeasedCommand leased,
        V1CommandResult result,
        CancellationToken cancellationToken)
    {
        var ackResult = await _client.AckCommandAsync(
                connection.BaseUrl,
                connection.ServerConnectionId,
                connection.Credential.Value,
                leased,
                result,
                cancellationToken)
            .ConfigureAwait(false);

        switch (ackResult)
        {
            case WatchoffitCallResult.Ack:
                _logger.LogDebug(
                    "Acked Watchoffit leased command {CommandId} as {Status}",
                    leased.CommandId,
                    result.Status);
                break;

            case WatchoffitCallResult.ApplicationError applicationError:
                _logger.LogWarning(
                    "Watchoffit refused ack for {CommandId} with {Code}: {Message}",
                    leased.CommandId,
                    applicationError.Envelope.Payload.Code,
                    applicationError.Envelope.Payload.Message);
                break;

            case WatchoffitCallResult.TransportFailure failure:
                // A failed ack is not fatal: the server's lease-reap
                // cron will reclaim the row once the lease expires.
                // We log at Warning so the operator can correlate
                // with a missing side effect.
                _logger.LogWarning(
                    "Ack for {CommandId} hit transport failure ({Reason}); the server will reclaim the lease",
                    leased.CommandId,
                    failure.Reason);
                break;

            default:
                _logger.LogWarning(
                    "Ack for {CommandId} returned an unknown result type {Type}",
                    leased.CommandId,
                    ackResult.GetType().Name);
                break;
        }
    }

    private void HandleTransportFailure(WatchoffitCallResult.TransportFailure failure)
    {
        if (failure.StatusCode is (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden)
        {
            // The credential was rejected by Watchoffit. Drop the local
            // pairing so every other worker (event outbox, event
            // forwarder, inventory publisher) stops trying to use
            // the dead credential. The operator has to repair via
            // the dashboard.
            _logger.LogError(
                "Watchoffit command poll returned HTTP {Status} ({Reason}); marking local pairing as revoked",
                failure.StatusCode,
                failure.Reason);
            _pairing.MarkRevokedFromRemote(
                $"command poll HTTP {failure.StatusCode}: {failure.Reason}");
            return;
        }

        // 4xx (not 401/403), 5xx, DNS, TLS, timeout — all classified
        // as `unrecoverable for this tick`. The 5 s loop sleep below
        // is the backoff (linear, no exponential — the brief is
        // explicit about keeping the behaviour simple).
        _logger.LogWarning(
            "Watchoffit command poll hit a transport failure (status {Status}, reason {Reason}); will retry next tick",
            failure.StatusCode,
            failure.Reason);
    }
}
