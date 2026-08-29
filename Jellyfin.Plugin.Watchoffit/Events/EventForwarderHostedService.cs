using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Watchoffit.Events;

/// <summary>
/// ASP.NET Core <see cref="IHostedService"/> that wires the
/// <see cref="EventForwarder"/> into Jellyfin's event bus on
/// application start and tears it down on application stop.
/// </summary>
/// <remarks>
/// The service holds no state of its own. The forwarder is a
/// singleton; <see cref="StartAsync"/> is what subscribes to the
/// session and user-data events, and <see cref="StopAsync"/> is
/// what unsubscribes. A failure in either step is logged but not
/// re-thrown — Jellyfin's own startup should not be blocked by
/// a forwarder wire-up error.
/// </remarks>
public sealed class EventForwarderHostedService : IHostedService
{
    private readonly EventForwarder _forwarder;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventForwarderHostedService"/> class.
    /// </summary>
    /// <param name="forwarder">Singleton event forwarder; resolved from the DI container.</param>
    public EventForwarderHostedService(EventForwarder forwarder)
    {
        _forwarder = forwarder ?? throw new ArgumentNullException(nameof(forwarder));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _forwarder.Attach();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _forwarder.Detach();
        return Task.CompletedTask;
    }
}
