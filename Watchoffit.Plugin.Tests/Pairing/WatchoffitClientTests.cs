using System.Net;
using System.Net.Http;
using System.Text;

using Jellyfin.Plugin.Watchoffit.Pairing;
using Jellyfin.Plugin.Watchoffit.Protocol.V1;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Jellyfin.Plugin.Watchoffit.Tests.Pairing;

/// <summary>
/// Tests for <see cref="WatchoffitClient"/>. The HTTP client is exercised
/// against a <see cref="SequencedHttpMessageHandler"/> that records
/// every request and queues a response per call. The tests pin the
/// envelope wire shape (header, body, auth header) on the way out and
/// the parsed result on the way back.
/// </summary>
public sealed class WatchoffitClientTests
{
    private static (HttpClient http, SequencedHttpMessageHandler handler) NewClient(string? baseAddress = null)
    {
        var handler = new SequencedHttpMessageHandler();
        var client = new HttpClient(handler);
        if (baseAddress is not null)
        {
            client.BaseAddress = new Uri(baseAddress);
        }

        return (client, handler);
    }

    private static WatchoffitClient NewServiceClient(HttpClient client)
    {
        var builder = new V1EnvelopeBuilder();
        var systemInfo = new StaticJellyfinSystemInfoProvider("jf_server_01", "10.11.11");
        return new WatchoffitClient(client, builder, systemInfo, NullLogger<WatchoffitClient>.Instance);
    }

    [Fact]
    public async Task ChallengeAsync_SendsServerIdentityAndParsesAck()
    {
        var (client, handler) = NewClient("https://watchoffit.test/");
        handler.Enqueue(OkResponse(ackEnvelopeJson(
            kind: "challenge",
            commandId: "cmd_x",
            serverConnectionId: "scn_01",
            watchoffitServerName: "Family",
            pairingCode: "AB12CD",
            expiresAt: "2030-01-01T00:00:00.000Z")));

        var result = await NewServiceClient(client).ChallengeAsync("https://watchoffit.test", CancellationToken.None);

        var ack = Assert.IsType<WatchoffitCallResult.Ack>(result);
        var challenge = Assert.IsType<Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Acks.V1ChallengeAck>(ack.Envelope.Payload);
        Assert.Equal("scn_01", challenge.ServerConnectionId);
        Assert.Equal("AB12CD", challenge.PairingCode);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/watchoffit-plugin/pairing/challenge", request.Path);
        Assert.NotNull(request.Body);
        Assert.Contains("\"jellyfinServerId\":\"jf_server_01\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"jellyfinVersion\":\"10.11.11\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"pluginGuid\":\"ed8e9c41-2e0f-5872-93f2-06feb1bc37d1\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"challenge_request\"", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedeemAsync_AttachesServerConnectionIdAndPairingCode()
    {
        var (client, handler) = NewClient("https://watchoffit.test/");
        handler.Enqueue(OkResponse(ackEnvelopeJson(
            kind: "redeem",
            commandId: "cmd_y",
            serverConnectionId: "scn_01",
            watchoffitServerName: "Family",
            credential: "cred_secret",
            issuedAt: "2026-08-27T10:02:01.000Z")));

        var result = await NewServiceClient(client).RedeemAsync(
            "https://watchoffit.test", "scn_01", "AB12CD", CancellationToken.None);

        var ack = Assert.IsType<WatchoffitCallResult.Ack>(result);
        var redeem = Assert.IsType<Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Acks.V1RedeemAck>(ack.Envelope.Payload);
        Assert.Equal("cred_secret", redeem.Credential);

        var request = Assert.Single(handler.Requests);
        Assert.Contains("\"serverConnectionId\":\"scn_01\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"pairingCode\":\"AB12CD\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"redeem_request\"", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevokeAsync_AttachesBearerCredential()
    {
        var (client, handler) = NewClient("https://watchoffit.test/");
        handler.Enqueue(OkResponse(ackEnvelopeJson(kind: "revoke", commandId: "cmd_z", serverConnectionId: "scn_01")));

        var result = await NewServiceClient(client).RevokeAsync(
            "https://watchoffit.test", "scn_01", "cred_x", CancellationToken.None);

        Assert.IsType<WatchoffitCallResult.Ack>(result);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("Bearer cred_x", request.Authorization);
    }

    [Fact]
    public async Task PingAsync_SendsEventEnvelope_WithoutCapabilities()
    {
        var (client, handler) = NewClient("https://watchoffit.test/");
        handler.Enqueue(OkResponse(genericAckJson(commandId: "evt_hb")));

        var result = await NewServiceClient(client).PingAsync(
            "https://watchoffit.test", "scn_01", "cred_x", CancellationToken.None);

        Assert.IsType<WatchoffitCallResult.Ack>(result);

        var request = Assert.Single(handler.Requests);
        Assert.Contains("\"kind\":\"event\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"heartbeat\"", request.Body, StringComparison.Ordinal);
        // Per the protocol: event envelopes do not carry capabilities.
        Assert.DoesNotContain("\"capabilities\"", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpFailure_NonOkStatus_ReturnsTransportFailure()
    {
        var (client, handler) = NewClient("https://watchoffit.test/");
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom", Encoding.UTF8, "application/json"),
        });

        var result = await NewServiceClient(client).ChallengeAsync("https://watchoffit.test", CancellationToken.None);

        var failure = Assert.IsType<WatchoffitCallResult.TransportFailure>(result);
        Assert.Equal(500, failure.StatusCode);
    }

    [Fact]
    public async Task HttpFailure_WithNonEnvelopeJson_ReturnsTransportFailure()
    {
        var (client, handler) = NewClient("https://watchoffit.test/");
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("{\"ok\":false}", Encoding.UTF8, "application/json"),
        });

        var result = await NewServiceClient(client).PingAsync(
            "https://watchoffit.test", "scn_01", "cred_x", CancellationToken.None);

        var failure = Assert.IsType<WatchoffitCallResult.TransportFailure>(result);
        Assert.Equal(502, failure.StatusCode);
        Assert.Equal("HTTP 502", failure.Reason);
    }

    [Fact]
    public async Task HttpFailure_WithErrorEnvelope_ReturnsApplicationError()
    {
        var (client, handler) = NewClient("https://watchoffit.test/");
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(errorEnvelopeJson("PAIRING_CODE_ALREADY_USED"), Encoding.UTF8, "application/json"),
        });

        var result = await NewServiceClient(client).RedeemAsync(
            "https://watchoffit.test",
            "scn_01",
            "AB12CD",
            CancellationToken.None);

        var error = Assert.IsType<WatchoffitCallResult.ApplicationError>(result);
        Assert.Equal("PAIRING_CODE_ALREADY_USED", error.Envelope.Payload.Code);
        Assert.Equal("pairing code already used", error.Envelope.Payload.Message);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("file:///tmp/watchoffit")]
    public async Task ChallengeAsync_InvalidBaseUrl_ReturnsTransportFailure(string baseUrl)
    {
        var (client, handler) = NewClient();

        var result = await NewServiceClient(client).ChallengeAsync(baseUrl, CancellationToken.None);

        var failure = Assert.IsType<WatchoffitCallResult.TransportFailure>(result);
        Assert.Equal("baseUrl must be an absolute HTTP(S) URL", failure.Reason);
        Assert.Empty(handler.Requests);
    }

    private static HttpResponseMessage OkResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static string ackEnvelopeJson(string kind, string commandId, string serverConnectionId, string? watchoffitServerName = null, string? pairingCode = null, string? expiresAt = null, string? credential = null, string? issuedAt = null) => kind switch
    {
        "challenge" => $$"""
        {
          "kind": "ack",
          "header": {
            "version": 1, "kind": "ack", "id": "ack_{{commandId}}",
            "correlationId": "{{commandId}}", "sequence": 1,
            "timestamp": "2026-08-27T10:01:01.000Z", "serverConnectionId": "{{serverConnectionId}}"
          },
          "payload": {
            "commandId": "{{commandId}}", "status": "ok",
            "serverConnectionId": "{{serverConnectionId}}",
            "watchoffitServerName": "{{watchoffitServerName}}",
            "pairingCode": "{{pairingCode}}",
            "expiresAt": "{{expiresAt}}"
          }
        }
        """,
        "redeem" => $$"""
        {
          "kind": "ack",
          "header": {
            "version": 1, "kind": "ack", "id": "ack_{{commandId}}",
            "correlationId": "{{commandId}}", "sequence": 1,
            "timestamp": "2026-08-27T10:02:01.000Z", "serverConnectionId": "{{serverConnectionId}}"
          },
          "payload": {
            "commandId": "{{commandId}}", "status": "ok",
            "serverConnectionId": "{{serverConnectionId}}",
            "watchoffitServerName": "{{watchoffitServerName}}",
            "issuedAt": "{{issuedAt}}",
            "credential": "{{credential}}"
          }
        }
        """,
        "revoke" => $$"""
        {
          "kind": "ack",
          "header": {
            "version": 1, "kind": "ack", "id": "ack_{{commandId}}",
            "correlationId": "{{commandId}}", "sequence": 1,
            "timestamp": "2026-08-27T10:04:01.000Z", "serverConnectionId": "{{serverConnectionId}}"
          },
          "payload": {
            "commandId": "{{commandId}}", "status": "ok",
            "note": "credential revoked"
          }
        }
        """,
        _ => throw new ArgumentException($"unknown kind: {kind}", nameof(kind)),
    };

    private static string genericAckJson(string commandId) => $$"""
    {
      "kind": "ack",
      "header": {
        "version": 1, "kind": "ack", "id": "ack_{{commandId}}",
        "correlationId": "{{commandId}}", "sequence": 1,
        "timestamp": "2026-08-27T10:04:30.000Z", "serverConnectionId": "scn_01"
      },
      "payload": { "commandId": "{{commandId}}", "status": "ok", "note": "heartbeat" }
    }
    """;

    private static string errorEnvelopeJson(string code) => $$"""
    {
      "kind": "error",
      "header": {
        "version": 1, "kind": "error", "id": "err_cmd_y",
        "correlationId": "cmd_y", "sequence": 1,
        "timestamp": "2026-08-27T10:02:01.000Z", "serverConnectionId": "scn_01"
      },
      "payload": {
        "commandId": "cmd_y",
        "code": "{{code}}",
        "message": "pairing code already used"
      }
    }
    """;
}

/// <summary>
/// Test <see cref="HttpMessageHandler"/> that records every request
/// and returns a queued response in order. The handler also exposes
/// the path each request hit so tests can assert on routing.
/// </summary>
internal sealed class SequencedHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    private readonly List<RecordedRequest> _requests = new();

    public IReadOnlyList<RecordedRequest> Requests => _requests;

    public void Enqueue(HttpResponseMessage response) => _responses.Enqueue(response);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        _requests.Add(new RecordedRequest(request.Method, request.RequestUri?.AbsolutePath ?? string.Empty, body, request.Headers.Authorization?.ToString()));

        if (_responses.Count == 0)
        {
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("no queued response", System.Text.Encoding.UTF8, "application/json"),
            };
        }

        return _responses.Dequeue();
    }
}

/// <summary>Captured outgoing request for assertions.</summary>
internal sealed record RecordedRequest(HttpMethod Method, string Path, string? Body, string? Authorization);
