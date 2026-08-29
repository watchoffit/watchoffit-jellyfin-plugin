using System.Globalization;

using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Acks;

namespace Jellyfin.Plugin.Watchoffit.Commands.Handlers;

/// <summary>
/// Handles the v1 <c>ping</c> command. The handler acknowledges the
/// round-trip and records an RTT marker in the <c>note</c> field so
/// Watchoffit can compute RTT and detect clock drift.
/// </summary>
/// <remarks>
/// The current server build never enqueues a <c>ping</c> — the plugin
/// only ever sends pings itself, as the "I'm here, anything for me?"
/// probe on <c>POST /api/watchoffit-plugin/command/poll</c>. The handler
/// exists so the registry can dispatch a leased <c>ping</c> when the
/// server eventually queues one (e.g. to drive a connection-keepalive
/// from the Watchoffit side). Today the handler is only exercised by the
/// unit tests, which use it to prove the polling channel works
/// end-to-end.
/// </remarks>
public sealed class PingCommandHandler : ICommandHandler
{
    /// <inheritdoc />
    public string CommandKind => "ping";

    /// <inheritdoc />
    public Task<V1CommandResult> HandleAsync(
        V1LeasedCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The handler does no real work, but it must still honor a
        // pre-cancelled token so the polling service's shutdown
        // path can short-circuit a leased batch.
        cancellationToken.ThrowIfCancellationRequested();

        // The note is purely informational — Watchoffit does not act on it
        // today. The marker is stable per second so logs that compare
        // the note to a wall-clock time can eyeball clock drift at a
        // glance, and a `rtt_observed_at_` prefix makes the field
        // greppable in any Watchoffit-side debug build.
        var note = string.Format(
            CultureInfo.InvariantCulture,
            "rtt_observed_at_{0}",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        return Task.FromResult(V1CommandResult.OkWithNote(note));
    }
}
