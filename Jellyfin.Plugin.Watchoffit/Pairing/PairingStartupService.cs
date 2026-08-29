using Jellyfin.Plugin.Watchoffit.Events;
using Jellyfin.Plugin.Watchoffit.Protocol.V1;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Watchoffit.Pairing;

/// <summary>
/// Restores the persisted pairing snapshot before plugin background workers
/// begin to send or accept synchronization work.
/// </summary>
/// <remarks>
/// Jellyfin creates the plugin instance before its dependency injection
/// container is ready, so rehydration cannot safely run in
/// <see cref="WatchoffitPlugin"/>. Hosted services are started in registration
/// order, which makes this the explicit startup barrier for the event worker
/// and event forwarder.
/// </remarks>
public sealed class PairingStartupService : IHostedService
{
    private readonly PairingService _pairing;
    private readonly DurableEventOutbox _outbox;
    private readonly V1EnvelopeBuilder _envelopeBuilder;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PairingStartupService"/> class.
    /// </summary>
    /// <param name="pairing">Pairing state machine backed by the durable connection store.</param>
    /// <param name="outbox">Durable outbound event queue with the persisted sequence watermark.</param>
    /// <param name="envelopeBuilder">Shared v1 envelope builder whose sequence counter is restored at startup.</param>
    /// <param name="logger">Plugin diagnostics logger.</param>
    public PairingStartupService(
        PairingService pairing,
        DurableEventOutbox outbox,
        V1EnvelopeBuilder envelopeBuilder,
        ILogger<PairingStartupService> logger)
    {
        _pairing = pairing ?? throw new ArgumentNullException(nameof(pairing));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _envelopeBuilder = envelopeBuilder ?? throw new ArgumentNullException(nameof(envelopeBuilder));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _pairing.LoadFromStore();
        _outbox.RestoreSequenceWatermark(_envelopeBuilder);
        _logger.LogInformation("Watchoffit pairing restored to state {State}", _pairing.CurrentState);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
