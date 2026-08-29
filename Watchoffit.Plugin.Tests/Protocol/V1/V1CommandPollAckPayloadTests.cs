using System.Text.Json;

using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Acks;

using Xunit;

namespace Jellyfin.Plugin.Watchoffit.Tests.Protocol.V1;

/// <summary>
/// Round-trip + parser tests for the v1 <c>command_poll_ack</c> ack
/// payload. The new branch in <see cref="V1AckPayloadJsonConverter"/>
/// has to (a) extract the leased <c>commands</c> array correctly,
/// (b) preserve the opaque per-command <c>payload</c> as a
/// <see cref="JsonElement"/>, and (c) not regress the generic ack
/// path that other routes still depend on.
/// </summary>
public sealed class V1CommandPollAckPayloadTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new();

    [Fact]
    public void Read_PopulatesCommandPollAckPayload_FromPollResponse()
    {
        const string Json = """
        {
          "commandId": "poll_01936dd0-0000-7000-8000-000000000001",
          "status": "ok",
          "note": null,
          "kind": "command_poll_ack",
          "commands": [
            {
              "commandId": "cmd_01936dd0-0000-7000-8000-000000000002",
              "commandKind": "ping",
              "payload": { "nonce": "nonce_abc" },
              "leaseUntil": 1735776000,
              "attemptToken": "att_01936dd0-0000-7000-8000-000000000003"
            }
          ]
        }
        """;

        var result = JsonSerializer.Deserialize<V1AckPayload>(Json, SerializerOptions);

        var poll = Assert.IsType<V1CommandPollAckPayload>(result);
        Assert.Equal("poll_01936dd0-0000-7000-8000-000000000001", poll.CommandId);
        Assert.Equal(V1AckStatus.Ok, poll.Status);
        Assert.Null(poll.Note);

        var command = Assert.Single(poll.Commands);
        Assert.Equal("cmd_01936dd0-0000-7000-8000-000000000002", command.CommandId);
        Assert.Equal("ping", command.CommandKind);
        Assert.Equal(1735776000L, command.LeaseUntil);
        Assert.Equal("att_01936dd0-0000-7000-8000-000000000003", command.AttemptToken);

        // The opaque payload survives the round-trip as a JsonElement
        // so the handler can deserialize it per its own schema.
        Assert.Equal(JsonValueKind.Object, command.Payload.ValueKind);
        Assert.Equal("nonce_abc", command.Payload.GetProperty("nonce").GetString());
    }

    [Fact]
    public void Read_EmptyCommandsArray_RoutesToCommandPollAckPayload_NotBaseAck()
    {
        const string Json = """
        {
          "commandId": "poll_01936dd0-0000-7000-8000-000000000004",
          "status": "ok",
          "kind": "command_poll_ack",
          "commands": []
        }
        """;

        var result = JsonSerializer.Deserialize<V1AckPayload>(Json, SerializerOptions);

        // The empty-queue case is a `command_poll_ack` shape, not a
        // generic ack. The plugin would mis-iterate it as a
        // `V1BaseAck` if the converter fell through to the generic
        // branch, losing the discriminator entirely.
        var poll = Assert.IsType<V1CommandPollAckPayload>(result);
        Assert.Empty(poll.Commands);
    }

    [Fact]
    public void Read_GenericAck_StillFallsThroughToBaseAck_RegressionGuard()
    {
        // No `kind` discriminator + no extra fields → base ack.
        // A future refactor must not let the `command_poll_ack`
        // branch steal this shape (the other base branches are
        // `.strict()`-like — they expect the absence of `commands`).
        const string Json = """
        {
          "commandId": "cmd_xyz",
          "status": "ok"
        }
        """;

        var result = JsonSerializer.Deserialize<V1AckPayload>(Json, SerializerOptions);

        var baseAck = Assert.IsType<V1BaseAck>(result);
        Assert.Equal("cmd_xyz", baseAck.CommandId);
        Assert.Equal(V1AckStatus.Ok, baseAck.Status);
        Assert.Null(baseAck.Note);
    }

    [Fact]
    public void Read_NoopCommandPollAck_RoutesToCommandPollAckPayload()
    {
        // The schema pins `status` to "ok" for the command_poll_ack
        // branch. A "noop" would be a server bug; we verify the
        // converter throws loudly rather than silently coercing.
        const string Json = """
        {
          "commandId": "poll_01936dd0-0000-7000-8000-000000000005",
          "status": "noop",
          "kind": "command_poll_ack",
          "commands": []
        }
        """;

        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<V1AckPayload>(Json, SerializerOptions));
        Assert.Contains("command_poll_ack", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_MissingCommands_Throws()
    {
        // A `command_poll_ack` without a `commands` array is a wire
        // bug. The converter throws so a regression in the server
        // surfaces immediately on the next plugin poll.
        const string Json = """
        {
          "commandId": "poll_01936dd0-0000-7000-8000-000000000006",
          "status": "ok",
          "kind": "command_poll_ack"
        }
        """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<V1AckPayload>(Json, SerializerOptions));
    }

    [Fact]
    public void Read_CommandPayload_JsonElementIsIndependentClone()
    {
        // The converter clones the per-command `payload` JsonElement
        // so the parent JsonDocument can dispose without invalidating
        // the property. This test pins that contract by reading the
        // element after the call returns.
        const string Json = """
        {
          "commandId": "poll_x",
          "status": "ok",
          "kind": "command_poll_ack",
          "commands": [
            {
              "commandId": "cmd_x",
              "commandKind": "ping",
              "payload": { "nested": { "value": 42 } },
              "leaseUntil": 1,
              "attemptToken": "att_x"
            }
          ]
        }
        """;

        var result = JsonSerializer.Deserialize<V1AckPayload>(Json, SerializerOptions);
        var poll = Assert.IsType<V1CommandPollAckPayload>(result);
        var command = Assert.Single(poll.Commands);

        // Two sequential reads return the same value — the element
        // was cloned, not aliased into a disposed document.
        Assert.Equal(42, command.Payload.GetProperty("nested").GetProperty("value").GetInt32());
        Assert.Equal(42, command.Payload.GetProperty("nested").GetProperty("value").GetInt32());
    }

    [Fact]
    public void Write_RoundTripsTheWireShape()
    {
        // The plugin only ever receives a `command_poll_ack`, but the
        // `Write` side of the converter is exercised in unit tests
        // and may be used by future fixtures. The wire shape must
        // round-trip through `Write` → `Read` byte-for-byte.
        var payload = new V1CommandPollAckPayload
        {
            CommandId = "poll_1",
            Status = V1AckStatus.Ok,
            Note = "synthetic fixture",
            Commands = new[]
            {
                new V1LeasedCommand
                {
                    CommandId = "cmd_1",
                    CommandKind = "ping",
                    Payload = JsonDocument.Parse("""{ "nonce": "n1" }""").RootElement,
                    LeaseUntil = 12345,
                    AttemptToken = "att_1",
                },
            },
        };

        var json = JsonSerializer.Serialize<V1AckPayload>(payload, SerializerOptions);
        var roundTrip = JsonSerializer.Deserialize<V1AckPayload>(json, SerializerOptions);

        var roundTripPoll = Assert.IsType<V1CommandPollAckPayload>(roundTrip);
        Assert.Equal("poll_1", roundTripPoll.CommandId);
        var command = Assert.Single(roundTripPoll.Commands);
        Assert.Equal("cmd_1", command.CommandId);
        Assert.Equal("ping", command.CommandKind);
        Assert.Equal(12345, command.LeaseUntil);
        Assert.Equal("att_1", command.AttemptToken);
        Assert.Equal("n1", command.Payload.GetProperty("nonce").GetString());

        // The discriminator survives the round-trip.
        using var document = JsonDocument.Parse(json);
        Assert.Equal("command_poll_ack", document.RootElement.GetProperty("kind").GetString());
    }
}
