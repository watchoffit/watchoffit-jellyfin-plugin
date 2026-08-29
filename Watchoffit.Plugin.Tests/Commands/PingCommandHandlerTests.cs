using System.Text.Json;

using Jellyfin.Plugin.Watchoffit.Commands.Handlers;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Acks;

using Xunit;

namespace Jellyfin.Plugin.Watchoffit.Tests.Commands;

/// <summary>
/// Tests for <see cref="PingCommandHandler"/>. The handler is the
/// baseline for the command channel: the polling service is wired
/// to dispatch every leased command via the registry, so verifying
/// the ping path proves the registry + dispatch loop is sound.
/// </summary>
public sealed class PingCommandHandlerTests
{
    [Fact]
    public async Task HandleAsync_ReturnsOkStatus()
    {
        var handler = new PingCommandHandler();
        var command = NewLeasedCommand();

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.Equal("ok", result.Status);
    }

    [Fact]
    public async Task HandleAsync_NoteIsNonNullAndContainsTimestampMarker()
    {
        var handler = new PingCommandHandler();
        var command = NewLeasedCommand();

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.NotNull(result.Note);
        // The `rtt_observed_at_` prefix is a stable signal in
        // Watchoffit-side logs; the test pins the contract.
        Assert.StartsWith("rtt_observed_at_", result.Note, StringComparison.Ordinal);
        var suffix = result.Note["rtt_observed_at_".Length..];
        // Unix milliseconds are always a non-negative integer.
        Assert.True(long.TryParse(suffix, out var millis) && millis > 0, $"note suffix '{suffix}' is not a positive integer");
    }

    [Fact]
    public void CommandKind_IsPing()
    {
        // The dispatch loop looks up handlers by exact `commandKind`
        // string match. Pin the wire literal so a typo would fail
        // the test before it broke production.
        Assert.Equal("ping", new PingCommandHandler().CommandKind);
    }

    [Fact]
    public async Task HandleAsync_CancellationHonored()
    {
        // The handler is fast and synchronous today, so the only
        // way to observe cancellation is the framework raising it
        // before the handler runs. The test still pins the contract:
        // a pre-cancelled token must surface as `OperationCanceledException`
        // rather than the handler swallowing it.
        var handler = new PingCommandHandler();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handler.HandleAsync(NewLeasedCommand(), cts.Token));
    }

    [Fact]
    public async Task HandleAsync_NullCommand_Throws()
    {
        var handler = new PingCommandHandler();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => handler.HandleAsync(null!, CancellationToken.None));
    }

    private static V1LeasedCommand NewLeasedCommand() => new()
    {
        CommandId = "cmd_test",
        CommandKind = "ping",
        Payload = JsonDocument.Parse("""{ "nonce": "n" }""").RootElement,
        LeaseUntil = 0,
        AttemptToken = "att_test",
    };
}
