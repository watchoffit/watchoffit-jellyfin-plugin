using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.Watchoffit.Commands;

/// <summary>
/// Singleton registry of every <see cref="ICommandHandler"/> registered
/// in the DI container. Built once at construction by grouping the
/// injected handlers by <see cref="ICommandHandler.CommandKind"/>; the
/// map is immutable for the lifetime of the plugin.
/// </summary>
/// <remarks>
/// A duplicate registration is a programming error (two handlers
/// claiming the same <c>commandKind</c> would race for the same
/// commands). The constructor throws so the misconfiguration fails
/// loud at startup rather than silently picking one of the two handlers
/// at runtime.
/// </remarks>
public sealed class CommandHandlerRegistry : ICommandHandlerRegistry
{
    private readonly Dictionary<string, ICommandHandler> _byKind;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommandHandlerRegistry"/> class.
    /// </summary>
    /// <param name="handlers">Every <see cref="ICommandHandler"/> registered in the DI container.</param>
    public CommandHandlerRegistry(IEnumerable<ICommandHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        var map = new Dictionary<string, ICommandHandler>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (!map.TryAdd(handler.CommandKind, handler))
            {
                throw new InvalidOperationException(
                    $"Multiple ICommandHandler instances registered for commandKind '{handler.CommandKind}'");
            }
        }

        _byKind = map;
    }

    /// <inheritdoc />
    public bool TryGet(string commandKind, [NotNullWhen(true)] out ICommandHandler? handler)
    {
        ArgumentNullException.ThrowIfNull(commandKind);
        return _byKind.TryGetValue(commandKind, out handler);
    }
}
