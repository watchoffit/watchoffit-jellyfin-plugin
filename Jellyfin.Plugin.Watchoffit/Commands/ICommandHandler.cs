using System.Diagnostics.CodeAnalysis;

using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Acks;

namespace Jellyfin.Plugin.Watchoffit.Commands;

/// <summary>
/// Result of a single leased command execution. Mirrors the v1 ack
/// payload's <c>status</c> + optional <c>note</c> fields so the
/// <c>CommandPollingService</c> can echo it back to Watchoffit on
/// <c>POST /api/watchoffit-plugin/command/ack</c>.
/// </summary>
/// <param name="Status">Wire literal: <c>"ok"</c> for successful execution, <c>"noop"</c> for a no-op (target state already matched).</param>
/// <param name="Note">Optional human-readable hint for diagnostics. Never shown to end users.</param>
public sealed record V1CommandResult(string Status, string? Note = null)
{
    /// <summary>Successful execution. Side effects applied as requested.</summary>
    public const string OkStatus = "ok";

    /// <summary>Request was a no-op because the target state already matches.</summary>
    public const string NoopStatus = "noop";

    /// <summary>Pre-built <c>ok</c> result with no note.</summary>
    /// <returns>A <see cref="V1CommandResult"/> with <c>status=ok</c>.</returns>
    public static V1CommandResult Ok() => new(OkStatus);

    /// <summary>Pre-built <c>ok</c> result carrying a note.</summary>
    /// <param name="note">Diagnostic note forwarded to Watchoffit.</param>
    /// <returns>A <see cref="V1CommandResult"/> with <c>status=ok</c> and the supplied note.</returns>
    public static V1CommandResult OkWithNote(string note) => new(OkStatus, note);

    /// <summary>Pre-built <c>noop</c> result with no note.</summary>
    /// <returns>A <see cref="V1CommandResult"/> with <c>status=noop</c>.</returns>
    public static V1CommandResult Noop() => new(NoopStatus);

    /// <summary>Pre-built <c>noop</c> result carrying a note.</summary>
    /// <param name="note">Diagnostic note forwarded to Watchoffit.</param>
    /// <returns>A <see cref="V1CommandResult"/> with <c>status=noop</c> and the supplied note.</returns>
    public static V1CommandResult NoopWithNote(string note) => new(NoopStatus, note);
}

/// <summary>
/// Handler for a single v1 command kind leased via
/// <c>POST /api/watchoffit-plugin/command/poll</c>. Implementations are
/// registered as <see cref="Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton"/>
/// <c>ICommandHandler</c> services; the
/// <see cref="ICommandHandlerRegistry"/> groups them by
/// <see cref="CommandKind"/>.
/// </summary>
public interface ICommandHandler
{
    /// <summary>
    /// Gets the v1 <c>commandKind</c> this handler serves. Must match
    /// <see cref="V1LeasedCommand.CommandKind"/> exactly. Values are
    /// defined by <c>v1CommandPayloadSchema</c> in the protocol
    /// (e.g. <c>mark_played</c>, <c>ping</c>, <c>reconcile_request</c>,
    /// <c>rotate_credential</c>).
    /// </summary>
    string CommandKind { get; }

    /// <summary>
    /// Execute the leased command. The <see cref="V1LeasedCommand.Payload"/>
    /// is the opaque server-controlled body — implementations are
    /// expected to deserialize it into the per-kind C# shape (for
    /// example via <c>JsonElement.Deserialize&lt;T&gt;</c>).
    /// </summary>
    /// <param name="command">Leased command pulled from a poll response.</param>
    /// <param name="cancellationToken">Cancellation token for plugin shutdown.</param>
    /// <returns>The result that will be sent back to Watchoffit on the ack.</returns>
    Task<V1CommandResult> HandleAsync(
        V1LeasedCommand command,
        CancellationToken cancellationToken);
}

/// <summary>
/// Lookup for the registered <see cref="ICommandHandler"/> set. The
/// <c>CommandPollingService</c> resolves the handler by
/// <see cref="V1LeasedCommand.CommandKind"/> for every leased entry.
/// </summary>
public interface ICommandHandlerRegistry
{
    /// <summary>
    /// Attempt to resolve the handler for <paramref name="commandKind"/>.
    /// </summary>
    /// <param name="commandKind">Wire literal from <see cref="V1LeasedCommand.CommandKind"/>.</param>
    /// <param name="handler">Resolved handler, or <c>null</c> when the kind is unhandled.</param>
    /// <returns><c>true</c> when a handler is registered for the kind, <c>false</c> otherwise.</returns>
    bool TryGet(string commandKind, [NotNullWhen(true)] out ICommandHandler? handler);
}
