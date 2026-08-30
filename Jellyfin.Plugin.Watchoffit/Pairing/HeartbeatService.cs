using System.Net;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Watchoffit.Pairing;

/// <summary>
/// Periodically sends the v1 heartbeat event that keeps the Watchoffit
/// admin health view fresh.
/// </summary>
public sealed class HeartbeatService : BackgroundService
{
    private static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly WatchoffitClient _client;
    private readonly PairingService _pairing;
    private readonly ILogger _logger;
    private readonly TimeSpan _heartbeatInterval;

    /// <summary>
    /// Initializes a new instance of the <see cref="HeartbeatService"/> class.
    /// </summary>
    /// <param name="client">HTTP client used to post heartbeat envelopes to Watchoffit.</param>
    /// <param name="pairing">Source of the active pairing credential.</param>
    /// <param name="logger">Plugin diagnostics logger.</param>
    public HeartbeatService(
        WatchoffitClient client,
        PairingService pairing,
        ILogger<HeartbeatService> logger)
        : this(client, pairing, logger, DefaultHeartbeatInterval)
    {
    }

    /// <summary>
    /// Test-friendly constructor with an overridable interval.
    /// </summary>
    /// <param name="client">HTTP client used to post heartbeat envelopes to Watchoffit.</param>
    /// <param name="pairing">Source of the active pairing credential.</param>
    /// <param name="logger">Plugin diagnostics logger.</param>
    /// <param name="heartbeatInterval">Delay between heartbeat attempts.</param>
    internal HeartbeatService(
        WatchoffitClient client,
        PairingService pairing,
        ILogger<HeartbeatService> logger,
        TimeSpan heartbeatInterval)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(logger);

        if (heartbeatInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval), heartbeatInterval, "heartbeat interval must be positive");
        }

        _client = client;
        _pairing = pairing;
        _logger = logger;
        _heartbeatInterval = heartbeatInterval;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await HeartbeatOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Watchoffit heartbeat loop threw; will retry next tick");
            }

            try
            {
                await Task.Delay(_heartbeatInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Run one heartbeat tick against the current paired connection.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for plugin shutdown.</param>
    /// <returns>A task that completes when the heartbeat attempt is done.</returns>
    internal async Task HeartbeatOnceAsync(CancellationToken cancellationToken)
    {
        var connection = _pairing.CurrentConnection;
        if (connection is null || connection.State != PairingState.Paired)
        {
            return;
        }

        var result = await _client.PingAsync(
                connection.BaseUrl,
                connection.ServerConnectionId,
                connection.Credential.Value,
                cancellationToken)
            .ConfigureAwait(false);

        switch (result)
        {
            case WatchoffitCallResult.Ack:
                _pairing.MarkContactSucceeded(DateTimeOffset.UtcNow);
                _logger.LogDebug("Watchoffit heartbeat succeeded");
                break;

            case WatchoffitCallResult.ApplicationError applicationError:
                _logger.LogWarning(
                    "Watchoffit heartbeat refused with {Code}: {Message}",
                    applicationError.Envelope.Payload.Code,
                    applicationError.Envelope.Payload.Message);
                if (string.Equals(applicationError.Envelope.Payload.Code, "AUTH_REQUIRED", StringComparison.Ordinal))
                {
                    _pairing.MarkRevokedFromRemote(
                        applicationError.Envelope.Payload.Code,
                        connection.ServerConnectionId);
                }

                break;

            case WatchoffitCallResult.TransportFailure failure:
                if (failure.StatusCode is (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden)
                {
                    _pairing.MarkRevokedFromRemote(
                        $"heartbeat HTTP {failure.StatusCode}",
                        connection.ServerConnectionId);
                    return;
                }

                _logger.LogWarning(
                    "Watchoffit heartbeat failed (status {Status}): {Reason}",
                    failure.StatusCode,
                    failure.Reason);
                break;

            default:
                _logger.LogWarning(
                    "Watchoffit heartbeat returned an unknown result type {Type}; skipping this tick",
                    result.GetType().Name);
                break;
        }
    }
}
