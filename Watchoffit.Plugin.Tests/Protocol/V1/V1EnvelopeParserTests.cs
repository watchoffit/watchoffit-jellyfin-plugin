using System.Text.Json;

using Jellyfin.Plugin.Watchoffit.Protocol.V1;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Acks;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Commands;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Events;

using Xunit;

namespace Jellyfin.Plugin.Watchoffit.Tests.Protocol.V1;

/// <summary>
/// Tests for <see cref="V1EnvelopeParser"/>. The canonical fixtures in
/// <c>Watchoffit.Plugin.Tests/fixtures/v1</c> are copies of the TypeScript
/// fixtures in <c>packages/core/test/fixtures/watchoffit-plugin-protocol/v1</c>
/// and MUST match byte-for-byte on the wire.
/// </summary>
public class V1EnvelopeParserTests
{
    private const string FixtureDir = "fixtures/v1";

    private static string[] ListFixtures()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, FixtureDir);
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.json").Select(path => Path.GetFileName(path) ?? throw new InvalidOperationException("Fixture path has no file name")).OrderBy(n => n, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
    }

    private static string ReadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, FixtureDir, name);
        return File.ReadAllText(path);
    }

    [Theory]
    [InlineData("ack-challenge-ok.json")]
    [InlineData("ack-ok.json")]
    [InlineData("ack-redeem-ok.json")]
    [InlineData("ack-revoke-ok.json")]
    [InlineData("command-challenge-request.json")]
    [InlineData("command-mark-played-episode.json")]
    [InlineData("command-mark-played-movie.json")]
    [InlineData("command-mark-unplayed.json")]
    [InlineData("command-ping.json")]
    [InlineData("command-reconcile-request.json")]
    [InlineData("command-redeem-request.json")]
    [InlineData("command-revoke-request.json")]
    [InlineData("error-item-not-found.json")]
    [InlineData("event-heartbeat.json")]
    [InlineData("event-playback-progress.json")]
    [InlineData("event-playback-start.json")]
    [InlineData("event-playback-stop.json")]
    [InlineData("event-user-data.json")]
    public void Parse_AcceptsCanonicalFixture(string fixture)
    {
        var json = ReadFixture(fixture);
        var result = V1EnvelopeParser.Parse(json);

        Assert.True(result is V1ParseResult.Ok, $"expected Ok, got {result}");
        var ok = (V1ParseResult.Ok)result;
        Assert.Equal(V1ProtocolConstants.ProtocolVersion, ok.Envelope.Header.Version);
        Assert.Equal(ok.Envelope.Kind, ok.Envelope.Header.Kind);
    }

    [Fact]
    public void Parse_ChallengeRequest_PayloadShapeIsCorrect()
    {
        var json = ReadFixture("command-challenge-request.json");
        var result = V1EnvelopeParser.Parse(json);

        var ok = Assert.IsType<V1ParseResult.Ok>(result);
        var cmd = Assert.IsType<V1CommandEnvelope>(ok.Envelope);
        var challenge = Assert.IsType<V1ChallengeRequestCommand>(cmd.Payload);
        Assert.Equal("jf_server_01HZ0001LOCAL", challenge.JellyfinServerId);
        Assert.Equal("10.11.11", challenge.JellyfinVersion);
        Assert.Equal("1.0.0.0", challenge.PluginVersion);
        Assert.Equal(Guid.Parse("ed8e9c41-2e0f-5872-93f2-06feb1bc37d1"), challenge.PluginGuid);
    }

    [Fact]
    public void Parse_RedeemRequest_PayloadShapeIsCorrect()
    {
        var json = ReadFixture("command-redeem-request.json");
        var result = V1EnvelopeParser.Parse(json);

        var ok = Assert.IsType<V1ParseResult.Ok>(result);
        var cmd = Assert.IsType<V1CommandEnvelope>(ok.Envelope);
        var redeem = Assert.IsType<V1RedeemRequestCommand>(cmd.Payload);
        Assert.Equal("AB12CD", redeem.PairingCode);
        Assert.Equal("jf_server_01HZ0001LOCAL", redeem.JellyfinServerId);
    }

    [Fact]
    public void Parse_RevokeRequest_PayloadShapeIsCorrect()
    {
        var json = ReadFixture("command-revoke-request.json");
        var result = V1EnvelopeParser.Parse(json);

        var ok = Assert.IsType<V1ParseResult.Ok>(result);
        var cmd = Assert.IsType<V1CommandEnvelope>(ok.Envelope);
        var revoke = Assert.IsType<V1RevokeRequestCommand>(cmd.Payload);
        Assert.Equal("jf_server_01HZ0001LOCAL", revoke.JellyfinServerId);
        Assert.Equal(Guid.Parse("ed8e9c41-2e0f-5872-93f2-06feb1bc37d1"), revoke.PluginGuid);
    }

    [Fact]
    public void Parse_ChallengeAck_PayloadShapeIsCorrect()
    {
        var json = ReadFixture("ack-challenge-ok.json");
        var result = V1EnvelopeParser.Parse(json);

        var ok = Assert.IsType<V1ParseResult.Ok>(result);
        var ack = Assert.IsType<V1AckEnvelope>(ok.Envelope);
        var challenge = Assert.IsType<V1ChallengeAck>(ack.Payload);
        Assert.Equal("scn_01HZ0001EXAMPLE", challenge.ServerConnectionId);
        Assert.Equal("Family Watchoffit", challenge.WatchoffitServerName);
        Assert.Equal("AB12CD", challenge.PairingCode);
        Assert.Equal("2026-08-27T10:11:00.000Z", challenge.ExpiresAt);
    }

    [Fact]
    public void Parse_RedeemAck_PayloadShapeIsCorrect()
    {
        var json = ReadFixture("ack-redeem-ok.json");
        var result = V1EnvelopeParser.Parse(json);

        var ok = Assert.IsType<V1ParseResult.Ok>(result);
        var ack = Assert.IsType<V1AckEnvelope>(ok.Envelope);
        var redeem = Assert.IsType<V1RedeemAck>(ack.Payload);
        Assert.Equal("cred_01HZ0001SECRET", redeem.Credential);
        Assert.Equal("scn_01HZ0001EXAMPLE", redeem.ServerConnectionId);
    }

    [Fact]
    public void Parse_RevokeAck_PayloadShapeIsCorrect()
    {
        var json = ReadFixture("ack-revoke-ok.json");
        var result = V1EnvelopeParser.Parse(json);

        var ok = Assert.IsType<V1ParseResult.Ok>(result);
        var ack = Assert.IsType<V1AckEnvelope>(ok.Envelope);
        var baseAck = Assert.IsType<V1BaseAck>(ack.Payload);
        Assert.Equal("credential revoked", baseAck.Note);
    }

    [Fact]
    public void Parse_HeartbeatEvent_PayloadShapeIsCorrect()
    {
        var json = ReadFixture("event-heartbeat.json");
        var result = V1EnvelopeParser.Parse(json);

        var ok = Assert.IsType<V1ParseResult.Ok>(result);
        var evt = Assert.IsType<V1EventEnvelope>(ok.Envelope);
        var heartbeat = Assert.IsType<V1HeartbeatEvent>(evt.Payload);
        Assert.Equal(0, heartbeat.QueueDepth);
        Assert.Equal(5, heartbeat.LastSequence);
        Assert.Equal("1.0.0", heartbeat.PluginVersion);
        Assert.Equal("jf-server-01HZ", heartbeat.JellyfinItemId);
        Assert.Equal(V1MediaKind.Movie, heartbeat.MediaKind);
    }

    [Fact]
    public void Parse_InventoryManifestEvent_PayloadShapeIsCorrect()
    {
        var json = """
        {
          "kind": "event",
          "header": { "version": 1, "kind": "event", "id": "evt_inventory_01", "sequence": 6, "timestamp": "2026-08-28T10:00:00.000Z", "serverConnectionId": "scn_01" },
          "payload": {
            "kind": "inventory_manifest", "provider": "jellyfin", "generation": 4, "capturedAt": "2026-08-28T10:00:00.000Z", "chunkIndex": 0, "chunkCount": 1,
            "server": { "remoteServerId": "server-1", "name": "Family", "version": "10.11.11", "pluginVersion": "1.0.0" },
            "users": [{ "remoteUserId": "user-1", "name": "Alex", "isAdministrator": true, "isDisabled": false }],
            "libraries": [{ "remoteLibraryId": "library-1", "name": "Movies", "collectionType": "movies" }],
            "userLibraries": [{ "remoteUserId": "user-1", "remoteLibraryId": "library-1" }]
          }
        }
        """;

        var result = V1EnvelopeParser.Parse(json);
        var ok = Assert.IsType<V1ParseResult.Ok>(result);
        var evt = Assert.IsType<V1EventEnvelope>(ok.Envelope);
        var manifest = Assert.IsType<V1InventoryManifestEvent>(evt.Payload);
        Assert.Equal("jellyfin", manifest.Provider);
        Assert.Equal(4, manifest.Generation);
        Assert.Equal("Alex", manifest.Users[0].Name);
        Assert.Equal("movies", manifest.Libraries[0].CollectionType);
    }

    [Fact]
    public void Parse_RotateCredentialAck_PayloadShapeIsCorrect()
    {
        var envelope = """
        {
          "kind": "ack",
          "header": {
            "version": 1,
            "kind": "ack",
            "id": "ack_rotate_01",
            "correlationId": "cmd_rotate_01",
            "sequence": 1,
            "timestamp": "2026-08-26T20:40:00.000Z",
            "serverConnectionId": "scn_01"
          },
          "payload": {
            "commandId": "cmd_rotate_01",
            "status": "ok",
            "newCredential": "cred_NEW",
            "rotatedAt": "2026-08-26T20:40:00.000Z"
          }
        }
        """;

        var result = V1EnvelopeParser.Parse(envelope);

        var ok = Assert.IsType<V1ParseResult.Ok>(result);
        var ack = Assert.IsType<V1AckEnvelope>(ok.Envelope);
        var rotate = Assert.IsType<V1RotateCredentialAck>(ack.Payload);
        Assert.Equal("cred_NEW", rotate.NewCredential);
        Assert.Equal("2026-08-26T20:40:00.000Z", rotate.RotatedAt);
    }

    [Fact]
    public void Parse_ErrorEnvelope_PayloadShapeIsCorrect()
    {
        var json = ReadFixture("error-item-not-found.json");
        var result = V1EnvelopeParser.Parse(json);

        var ok = Assert.IsType<V1ParseResult.Ok>(result);
        var err = Assert.IsType<V1ErrorEnvelope>(ok.Envelope);
        Assert.Equal("ITEM_NOT_FOUND", err.Payload.Code);
    }

    [Fact]
    public void Parse_RejectsUnsupportedProtocolVersion()
    {
        var envelope = """
        {
          "kind": "command",
          "header": {
            "version": 2,
            "kind": "command",
            "id": "cmd_01",
            "sequence": 1,
            "timestamp": "2026-08-26T20:34:41.000Z",
            "serverConnectionId": "scn_01",
            "capabilities": { "minProtocolVersion": 2, "maxProtocolVersion": 2, "maxPayloadBytes": 65536, "maxBatchSize": 50 }
          },
          "payload": { "kind": "ping", "jellyfinItemId": "x", "watchoffitUserId": "u", "mediaKind": "movie", "nonce": "n" }
        }
        """;

        var result = V1EnvelopeParser.Parse(envelope);

        var failure = Assert.IsType<V1ParseResult.Failure>(result);
        Assert.Equal(SafeErrorCode.ProtocolVersionUnsupported, failure.Code);
    }

    [Fact]
    public void Parse_RejectsUnknownCommandPayloadKind()
    {
        var envelope = """
        {
          "kind": "command",
          "header": {
            "version": 1,
            "kind": "command",
            "id": "cmd_01",
            "sequence": 1,
            "timestamp": "2026-08-26T20:34:41.000Z",
            "serverConnectionId": "scn_01",
            "capabilities": { "minProtocolVersion": 1, "maxProtocolVersion": 1, "maxPayloadBytes": 65536, "maxBatchSize": 50 }
          },
          "payload": { "kind": "delete_everything" }
        }
        """;

        var result = V1EnvelopeParser.Parse(envelope);

        var failure = Assert.IsType<V1ParseResult.Failure>(result);
        Assert.Equal(SafeErrorCode.InvalidEnvelope, failure.Code);
    }

    [Fact]
    public void Parse_RejectsRedeemRequestWithLowercaseCode()
    {
        var envelope = """
        {
          "kind": "command",
          "header": {
            "version": 1,
            "kind": "command",
            "id": "cmd_redeem_01",
            "sequence": 1,
            "timestamp": "2026-08-27T10:02:00.000Z",
            "serverConnectionId": "scn_01",
            "capabilities": { "minProtocolVersion": 1, "maxProtocolVersion": 1, "maxPayloadBytes": 65536, "maxBatchSize": 50 }
          },
          "payload": { "kind": "redeem_request", "pairingCode": "ab12cd", "jellyfinServerId": "jf_server_01" }
        }
        """;

        var result = V1EnvelopeParser.Parse(envelope);

        var failure = Assert.IsType<V1ParseResult.Failure>(result);
        Assert.Equal(SafeErrorCode.InvalidEnvelope, failure.Code);
    }

    [Fact]
    public void Parse_RejectsChallengeRequestWithNonUuidPluginGuid()
    {
        var envelope = """
        {
          "kind": "command",
          "header": {
            "version": 1,
            "kind": "command",
            "id": "cmd_challenge_01",
            "sequence": 1,
            "timestamp": "2026-08-27T10:01:00.000Z",
            "serverConnectionId": "pending",
            "capabilities": { "minProtocolVersion": 1, "maxProtocolVersion": 1, "maxPayloadBytes": 65536, "maxBatchSize": 50 }
          },
          "payload": {
            "kind": "challenge_request",
            "jellyfinServerId": "jf_server_01",
            "jellyfinVersion": "10.11.11",
            "pluginVersion": "1.0.0.0",
            "pluginGuid": "not-a-uuid"
          }
        }
        """;

        var result = V1EnvelopeParser.Parse(envelope);

        var failure = Assert.IsType<V1ParseResult.Failure>(result);
        Assert.Equal(SafeErrorCode.InvalidEnvelope, failure.Code);
    }

    [Fact]
    public void Parse_RejectsCommandWithoutCapabilities()
    {
        var envelope = """
        {
          "kind": "command",
          "header": {
            "version": 1,
            "kind": "command",
            "id": "cmd_01",
            "sequence": 1,
            "timestamp": "2026-08-26T20:34:41.000Z",
            "serverConnectionId": "scn_01"
          },
          "payload": { "kind": "ping", "jellyfinItemId": "x", "watchoffitUserId": "u", "mediaKind": "movie", "nonce": "n" }
        }
        """;

        var result = V1EnvelopeParser.Parse(envelope);

        var failure = Assert.IsType<V1ParseResult.Failure>(result);
        Assert.Equal(SafeErrorCode.InvalidEnvelope, failure.Code);
    }

    [Fact]
    public void Parse_RejectsAckWithoutCorrelationId()
    {
        var envelope = """
        {
          "kind": "ack",
          "header": {
            "version": 1,
            "kind": "ack",
            "id": "ack_01",
            "sequence": 1,
            "timestamp": "2026-08-26T20:34:42.000Z",
            "serverConnectionId": "scn_01"
          },
          "payload": { "commandId": "cmd_01", "status": "ok" }
        }
        """;

        var result = V1EnvelopeParser.Parse(envelope);

        var failure = Assert.IsType<V1ParseResult.Failure>(result);
        Assert.Equal(SafeErrorCode.InvalidEnvelope, failure.Code);
    }

    [Fact]
    public void Parse_RejectsAckWithMismatchedCorrelationIdAndCommandId()
    {
        var envelope = """
        {
          "kind": "ack",
          "header": {
            "version": 1,
            "kind": "ack",
            "id": "ack_01",
            "correlationId": "cmd_OTHER",
            "sequence": 1,
            "timestamp": "2026-08-26T20:34:42.000Z",
            "serverConnectionId": "scn_01"
          },
          "payload": { "commandId": "cmd_01", "status": "ok" }
        }
        """;

        var result = V1EnvelopeParser.Parse(envelope);

        var failure = Assert.IsType<V1ParseResult.Failure>(result);
        Assert.Equal(SafeErrorCode.InvalidEnvelope, failure.Code);
    }

    [Fact]
    public void Parse_RejectsEnvelopeKindHeaderKindMismatch()
    {
        var envelope = """
        {
          "kind": "command",
          "header": {
            "version": 1,
            "kind": "event",
            "id": "cmd_01",
            "sequence": 1,
            "timestamp": "2026-08-26T20:34:41.000Z",
            "serverConnectionId": "scn_01",
            "capabilities": { "minProtocolVersion": 1, "maxProtocolVersion": 1, "maxPayloadBytes": 65536, "maxBatchSize": 50 }
          },
          "payload": { "kind": "ping", "jellyfinItemId": "x", "watchoffitUserId": "u", "mediaKind": "movie", "nonce": "n" }
        }
        """;

        var result = V1EnvelopeParser.Parse(envelope);

        var failure = Assert.IsType<V1ParseResult.Failure>(result);
        Assert.Equal(SafeErrorCode.InvalidEnvelope, failure.Code);
    }

    [Fact]
    public void Parse_RejectsNonObjectRoot()
    {
        var result = V1EnvelopeParser.Parse("[1, 2, 3]");
        var failure = Assert.IsType<V1ParseResult.Failure>(result);
        Assert.Equal(SafeErrorCode.InvalidEnvelope, failure.Code);
    }

    [Fact]
    public void Parse_RejectsMalformedJson()
    {
        var result = V1EnvelopeParser.Parse("{ this is not json");
        var failure = Assert.IsType<V1ParseResult.Failure>(result);
        Assert.Equal(SafeErrorCode.InvalidEnvelope, failure.Code);
    }

    [Fact]
    public void Parse_AllFixturesAreExercised()
    {
        // Sentinel: if a fixture is added to the directory but the
        // [Theory] above is not updated, the test still passes because
        // xUnit enumerates [InlineData] statically. This test makes the
        // discovery explicit so a missing inline data points back to a
        // forgotten fixture copy.
        var fixtures = ListFixtures();
        Assert.Equal(18, fixtures.Length);
    }

    [Fact]
    public void Parse_RejectsRotateCredentialAckWithNoopStatus()
    {
        // The TS schema pins rotate_credential's status to literal "ok".
        // "noop" must not be accepted — see pairing-design.md §7 + the
        // strict-rotation invariant in v1.ts.
        var envelope = """
        {
          "kind": "ack",
          "header": {
            "version": 1,
            "kind": "ack",
            "id": "ack_rotate_01",
            "correlationId": "cmd_rotate_01",
            "sequence": 1,
            "timestamp": "2026-08-26T20:40:00.000Z",
            "serverConnectionId": "scn_01"
          },
          "payload": {
            "commandId": "cmd_rotate_01",
            "status": "noop",
            "newCredential": "cred_NEW",
            "rotatedAt": "2026-08-26T20:40:00.000Z"
          }
        }
        """;

        var result = V1EnvelopeParser.Parse(envelope);

        var failure = Assert.IsType<V1ParseResult.Failure>(result);
        Assert.Equal(SafeErrorCode.InvalidEnvelope, failure.Code);
    }

    [Fact]
    public void Parse_RejectsRedeemAckWithNoopStatus()
    {
        var envelope = """
        {
          "kind": "ack",
          "header": {
            "version": 1,
            "kind": "ack",
            "id": "ack_redeem_01",
            "correlationId": "cmd_redeem_01",
            "sequence": 1,
            "timestamp": "2026-08-27T10:02:01.000Z",
            "serverConnectionId": "scn_01"
          },
          "payload": {
            "commandId": "cmd_redeem_01",
            "status": "noop",
            "serverConnectionId": "scn_01",
            "watchoffitServerName": "Family Watchoffit",
            "issuedAt": "2026-08-27T10:02:01.000Z",
            "credential": "cred_01"
          }
        }
        """;

        var result = V1EnvelopeParser.Parse(envelope);

        var failure = Assert.IsType<V1ParseResult.Failure>(result);
        Assert.Equal(SafeErrorCode.InvalidEnvelope, failure.Code);
    }

    [Fact]
    public void Parse_RejectsChallengeAckWithLowercasePairingCode()
    {
        // The manual ack converter must enforce the wire regex because
        // it bypasses the V1PairingCodeJsonConverter attribute path.
        var envelope = """
        {
          "kind": "ack",
          "header": {
            "version": 1,
            "kind": "ack",
            "id": "ack_challenge_01",
            "correlationId": "cmd_challenge_01",
            "sequence": 1,
            "timestamp": "2026-08-27T10:01:01.000Z",
            "serverConnectionId": "scn_01"
          },
          "payload": {
            "commandId": "cmd_challenge_01",
            "status": "ok",
            "serverConnectionId": "scn_01",
            "watchoffitServerName": "Family Watchoffit",
            "pairingCode": "ab12cd",
            "expiresAt": "2026-08-27T10:11:00.000Z"
          }
        }
        """;

        var result = V1EnvelopeParser.Parse(envelope);

        var failure = Assert.IsType<V1ParseResult.Failure>(result);
        Assert.Equal(SafeErrorCode.InvalidEnvelope, failure.Code);
    }

    [Fact]
    public void Parse_RejectsUnknownEnvelopeField()
    {
        // UnmappedMemberHandling.Disallow mirrors TS .strict().
        var envelope = """
        {
          "kind": "command",
          "header": {
            "version": 1,
            "kind": "command",
            "id": "cmd_01",
            "sequence": 1,
            "timestamp": "2026-08-26T20:34:41.000Z",
            "serverConnectionId": "scn_01",
            "capabilities": { "minProtocolVersion": 1, "maxProtocolVersion": 1, "maxPayloadBytes": 65536, "maxBatchSize": 50 }
          },
          "payload": { "kind": "rotate_credential" },
          "secretInjection": "rm -rf /"
        }
        """;

        var result = V1EnvelopeParser.Parse(envelope);

        var failure = Assert.IsType<V1ParseResult.Failure>(result);
        Assert.Equal(SafeErrorCode.InvalidEnvelope, failure.Code);
    }

    [Fact]
    public void Parse_AcceptsAckWithoutExplicitStatus()
    {
        // The TS ack base shape allows status to be absent on the wire;
        // absent status is treated as "ok". This is the same leniency
        // the TS schema allows via z.string().optional() + a default
        // normalization in the union.
        var envelope = """
        {
          "kind": "ack",
          "header": {
            "version": 1,
            "kind": "ack",
            "id": "ack_01",
            "correlationId": "cmd_01",
            "sequence": 1,
            "timestamp": "2026-08-26T20:34:42.000Z",
            "serverConnectionId": "scn_01"
          },
          "payload": { "commandId": "cmd_01" }
        }
        """;

        var result = V1EnvelopeParser.Parse(envelope);

        var ok = Assert.IsType<V1ParseResult.Ok>(result);
        var ack = Assert.IsType<V1AckEnvelope>(ok.Envelope);
        var baseAck = Assert.IsType<V1BaseAck>(ack.Payload);
        Assert.Equal(V1AckStatus.Ok, baseAck.Status);
    }
}
