namespace Jellyfin.Plugin.Watchoffit.Commands;

/// <summary>
/// Tracks the command currently mutating Jellyfin state so events raised
/// by that mutation can be correlated back to the command on the wire.
/// </summary>
public interface ICommandCausationContext
{
    /// <summary>
    /// Gets the active v1 command id, or <c>null</c> when Jellyfin is
    /// raising an event from a real user action.
    /// </summary>
    string? CurrentCommandId { get; }

    /// <summary>
    /// Begin a scoped command-causation marker.
    /// </summary>
    /// <param name="commandId">The v1 command id being executed.</param>
    /// <returns>A disposable scope that restores the previous marker.</returns>
    IDisposable Begin(string commandId);
}

/// <summary>
/// Async-local implementation of <see cref="ICommandCausationContext"/>.
/// Jellyfin raises <c>UserDataSaved</c> on the same logical execution path
/// as <c>SaveUserData</c>, so an async-local marker lets the event
/// forwarder attach the originating command id without global mutable state.
/// </summary>
public sealed class CommandCausationContext : ICommandCausationContext
{
    private readonly AsyncLocal<string?> _currentCommandId = new();

    /// <inheritdoc />
    public string? CurrentCommandId => _currentCommandId.Value;

    /// <inheritdoc />
    public IDisposable Begin(string commandId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);

        var previous = _currentCommandId.Value;
        _currentCommandId.Value = commandId;
        return new Scope(this, previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly CommandCausationContext _owner;
        private readonly string? _previous;
        private bool _disposed;

        public Scope(CommandCausationContext owner, string? previous)
        {
            _owner = owner;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _owner._currentCommandId.Value = _previous;
            _disposed = true;
        }
    }
}
