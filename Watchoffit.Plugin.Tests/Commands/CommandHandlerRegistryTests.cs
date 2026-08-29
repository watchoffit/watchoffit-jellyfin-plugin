using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using Jellyfin.Plugin.Watchoffit.Commands;
using Jellyfin.Plugin.Watchoffit.Commands.Handlers;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Acks;

using Xunit;

namespace Jellyfin.Plugin.Watchoffit.Tests.Commands;

/// <summary>
/// Tests for <see cref="CommandHandlerRegistry"/>. The registry is the
/// single point of dispatch for the polling service; a misrouted
/// <c>commandKind</c> would silently drop leased commands, so the
/// tests pin the lookup contract.
/// </summary>
public sealed class CommandHandlerRegistryTests
{
    [Fact]
    public void TryGet_RegisteredKind_ReturnsHandler()
    {
        var ping = new PingCommandHandler();
        var registry = new CommandHandlerRegistry(new ICommandHandler[] { ping });

        var found = registry.TryGet("ping", out var handler);

        Assert.True(found);
        Assert.Same(ping, handler);
    }

    [Fact]
    public void TryGet_UnknownKind_ReturnsFalseAndNull()
    {
        var registry = new CommandHandlerRegistry(new ICommandHandler[] { new PingCommandHandler() });

        var found = registry.TryGet("mark_played", out var handler);

        Assert.False(found);
        Assert.Null(handler);
    }

    [Fact]
    public void TryGet_EmptyRegistry_AlwaysReturnsFalse()
    {
        var registry = new CommandHandlerRegistry(Array.Empty<ICommandHandler>());

        Assert.False(registry.TryGet("ping", out var handler));
        Assert.Null(handler);
    }

    [Fact]
    public void Constructor_MultipleKinds_DispatchesByExactMatch()
    {
        var ping = new PingCommandHandler();
        var stub = new StubCommandHandler("mark_played");
        var registry = new CommandHandlerRegistry(new ICommandHandler[] { ping, stub });

        Assert.Same(ping, registry.Lookup("ping"));
        Assert.Same(stub, registry.Lookup("mark_played"));
    }

    [Fact]
    public void Constructor_DuplicateKind_Throws()
    {
        var first = new PingCommandHandler();
        var second = new StubCommandHandler("ping");

        var ex = Assert.Throws<InvalidOperationException>(
            () => new CommandHandlerRegistry(new ICommandHandler[] { first, second }));

        Assert.Contains("ping", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryGet_NullCommandKind_Throws()
    {
        var registry = new CommandHandlerRegistry(Array.Empty<ICommandHandler>());

        Assert.Throws<ArgumentNullException>(() => registry.TryGet(null!, out _));
    }

    [Fact]
    public void Constructor_NullHandlerInCollection_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new CommandHandlerRegistry(new ICommandHandler?[] { null }!));
    }

    [Fact]
    public void Constructor_RejectsDuplicateKindAcrossRealAndStubHandlers()
    {
        // Future commit will add a `mark_played` handler. Until
        // then, a stub with the same `commandKind` is the canonical
        // way to test lookup isolation between kinds. The registry
        // should refuse the duplicate regardless of whether one of
        // them is the real handler or a test stub.
        var ping = new PingCommandHandler();
        var stub = new StubCommandHandler("ping");

        Assert.Throws<InvalidOperationException>(
            () => new CommandHandlerRegistry(new ICommandHandler[] { ping, stub }));
    }

    private sealed class StubCommandHandler : ICommandHandler
    {
        public StubCommandHandler(string commandKind)
        {
            CommandKind = commandKind;
        }

        public string CommandKind { get; }

        public Task<V1CommandResult> HandleAsync(V1LeasedCommand command, CancellationToken cancellationToken)
        {
            return Task.FromResult(V1CommandResult.Ok());
        }
    }
}

/// <summary>Convenience extension to assert non-null lookups in tests.</summary>
internal static class CommandHandlerRegistryTestExtensions
{
    public static ICommandHandler? Lookup(this ICommandHandlerRegistry registry, string commandKind)
    {
        return registry.TryGet(commandKind, out var handler) ? handler : null;
    }
}
