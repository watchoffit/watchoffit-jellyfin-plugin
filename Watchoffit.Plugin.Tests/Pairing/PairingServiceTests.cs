using System.Net;
using System.Text;
using System.Text.Json;

using Jellyfin.Plugin.Watchoffit.Pairing;
using Jellyfin.Plugin.Watchoffit.Protocol.V1;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Jellyfin.Plugin.Watchoffit.Tests.Pairing;

/// <summary>
/// Tests for <see cref="PairingService"/>. Each test stands up an
/// in-memory <see cref="WatchoffitConnectionStore"/>, a
/// <see cref="FakeHttpMessageHandler"/>, and exercises the state
/// machine end-to-end without a network or a real Jellyfin host.
/// </summary>
public sealed class PairingServiceTests : IDisposable
{
    private readonly string _tempDir;

    public PairingServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "watchoffit-svc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
                // best-effort
            }
        }

        GC.SuppressFinalize(this);
    }

    private WatchoffitConnectionStore NewStore() => new(
        _tempDir,
        new PlainCredentialProtector(),
        NullLogger<WatchoffitConnectionStore>.Instance);

    private static (HttpClient client, FakeHttpMessageHandler handler) NewClient()
    {
        var handler = new FakeHttpMessageHandler();
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://watchoffit.test/"),
        };
        return (client, handler);
    }

    private PairingService NewService(out FakeHttpMessageHandler handler)
    {
        var (client, h) = NewClient();
        handler = h;
        var builder = new V1EnvelopeBuilder();
        var systemInfo = new StaticJellyfinSystemInfoProvider("jf_server_01", "10.11.11");
        var watchoffitClient = new WatchoffitClient(client, builder, systemInfo, NullLogger<WatchoffitClient>.Instance);
        return new PairingService(NewStore(), watchoffitClient, systemInfo, NullLogger<PairingService>.Instance);
    }

    [Fact]
    public void LoadFromStore_NoFile_StateStaysNone()
    {
        var service = NewService(out _);
        service.LoadFromStore();
        Assert.Equal(PairingState.None, service.CurrentState);
        Assert.Null(service.CurrentConnection);
    }

    [Fact]
    public void LoadFromStore_PresentPairedFile_LoadsState()
    {
        var store = NewStore();
        store.Save(new WatchoffitConnection
        {
            State = PairingState.Paired,
            BaseUrl = "https://watchoffit.example.com",
            ServerConnectionId = "scn_01",
            Credential = new WatchoffitCredential { Scheme = "plain", Value = "cred_x" },
        });

        var (client, _) = NewClient();
        var builder = new V1EnvelopeBuilder();
        var systemInfo = new StaticJellyfinSystemInfoProvider("jf_server_01", "10.11.11");
        var watchoffitClient = new WatchoffitClient(client, builder, systemInfo, NullLogger<WatchoffitClient>.Instance);
        var service = new PairingService(store, watchoffitClient, systemInfo, NullLogger<PairingService>.Instance);

        service.LoadFromStore();
        Assert.Equal(PairingState.Paired, service.CurrentState);
        Assert.NotNull(service.CurrentConnection);
        Assert.Equal("scn_01", service.CurrentConnection!.ServerConnectionId);
    }

    [Fact]
    public async Task ChallengeAsync_HappyPath_ReturnsAck()
    {
        var service = NewService(out var handler);
        handler.EnqueueAckForChallenge("scn_01", "Family", "AB12CD", "2030-01-01T00:00:00.000Z");

        var (ack, code, msg) = await service.ChallengeAsync("https://watchoffit.example.com", CancellationToken.None);

        Assert.Null(code);
        Assert.Null(msg);
        Assert.NotNull(ack);
        Assert.Equal("scn_01", ack!.ServerConnectionId);
        Assert.Equal("AB12CD", ack.PairingCode);
    }

    [Fact]
    public async Task ChallengeAsync_TransportFailure_ReturnsErrorCode()
    {
        var service = NewService(out var handler);
        handler.NextResponse = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        var (ack, code, msg) = await service.ChallengeAsync("https://watchoffit.example.com", CancellationToken.None);

        Assert.Null(ack);
        Assert.NotNull(code);
        Assert.NotNull(msg);
    }

    [Fact]
    public async Task RedeemAsync_HappyPath_TransitionsToPairedAndPersists()
    {
        var service = NewService(out var handler);
        handler.EnqueueAckForRedeem("scn_01", "Family Watchoffit", "cred_secret_value", "2026-08-27T10:02:01.000Z");

        var result = await service.RedeemAsync(
            "https://watchoffit.example.com",
            "scn_01",
            "AB12CD",
            CancellationToken.None);

        Assert.Equal(PairingState.Paired, result.NewState);
        Assert.NotNull(result.Connection);
        Assert.Equal("scn_01", result.Connection!.ServerConnectionId);
        Assert.Equal("cred_secret_value", result.Connection.Credential.Value);
        Assert.Equal(PairingState.Paired, service.CurrentState);
    }

    [Fact]
    public async Task DisconnectAsync_DropsLocalState_EvenWhenRemoteUnreachable()
    {
        var service = NewService(out var handler);
        handler.EnqueueAckForRedeem("scn_01", "Family Watchoffit", "cred_secret", "2026-08-27T10:02:01.000Z");
        await service.RedeemAsync("https://watchoffit.example.com", "scn_01", "AB12CD", CancellationToken.None);
        Assert.Equal(PairingState.Paired, service.CurrentState);

        // Now simulate the remote being unreachable on the revoke call.
        handler.NextResponse = new HttpResponseMessage(HttpStatusCode.GatewayTimeout);

        var result = await service.DisconnectAsync(CancellationToken.None);

        Assert.Equal(PairingState.None, result.NewState);
        Assert.Equal(PairingState.None, service.CurrentState);
        Assert.Null(service.CurrentConnection);
    }

    [Fact]
    public void LoadFromStore_ResetsToNone_OnCorruptFile()
    {
        File.WriteAllText(Path.Combine(_tempDir, "connection.json"), "{ not json");
        var service = NewService(out _);

        service.LoadFromStore();

        Assert.Equal(PairingState.None, service.CurrentState);
    }

    [Fact]
    public void MarkRevokedFromRemote_StaleServerConnectionId_LeavesCurrentPairingIntact()
    {
        var service = NewServiceWithPairedConnection("scn_current");

        service.MarkRevokedFromRemote("stale heartbeat HTTP 401", "scn_old");

        Assert.Equal(PairingState.Paired, service.CurrentState);
        Assert.Equal("scn_current", service.CurrentConnection?.ServerConnectionId);
    }

    [Fact]
    public void MarkRevokedFromRemote_MatchingServerConnectionId_DropsCurrentPairing()
    {
        var service = NewServiceWithPairedConnection("scn_current");

        service.MarkRevokedFromRemote("heartbeat HTTP 401", "scn_current");

        Assert.Equal(PairingState.None, service.CurrentState);
        Assert.Null(service.CurrentConnection);
    }

    private PairingService NewServiceWithPairedConnection(string serverConnectionId)
    {
        var store = NewStore();
        store.Save(new WatchoffitConnection
        {
            State = PairingState.Paired,
            BaseUrl = "https://watchoffit.example.com",
            ServerConnectionId = serverConnectionId,
            Credential = new WatchoffitCredential { Scheme = "plain", Value = "cred_current" },
        });

        var (client, _) = NewClient();
        var builder = new V1EnvelopeBuilder();
        var systemInfo = new StaticJellyfinSystemInfoProvider("jf_server_01", "10.11.11");
        var watchoffitClient = new WatchoffitClient(client, builder, systemInfo, NullLogger<WatchoffitClient>.Instance);
        var service = new PairingService(store, watchoffitClient, systemInfo, NullLogger<PairingService>.Instance);
        service.LoadFromStore();
        return service;
    }
}

/// <summary>
/// Stub <see cref="HttpMessageHandler"/> that returns a single
/// pre-queued <see cref="HttpResponseMessage"/>. Tests queue the
/// success response they want; <see cref="NextResponse"/> is the
/// one-shot slot.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    public HttpResponseMessage? NextResponse { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (NextResponse is null)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("no queued response", Encoding.UTF8, "application/json"),
            });
        }

        var response = NextResponse;
        NextResponse = null;
        return Task.FromResult(response);
    }

    public void EnqueueAckForChallenge(string scn, string name, string code, string expiresAt)
    {
        var ackJson = $$"""
        {
          "kind": "ack",
          "header": {
            "version": 1,
            "kind": "ack",
            "id": "ack_challenge_01",
            "correlationId": "cmd_challenge_01",
            "sequence": 1,
            "timestamp": "2026-08-27T10:01:01.000Z",
            "serverConnectionId": "{{scn}}"
          },
          "payload": {
            "commandId": "cmd_challenge_01",
            "status": "ok",
            "serverConnectionId": "{{scn}}",
            "watchoffitServerName": "{{name}}",
            "pairingCode": "{{code}}",
            "expiresAt": "{{expiresAt}}"
          }
        }
        """;
        NextResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ackJson, Encoding.UTF8, "application/json"),
        };
    }

    public void EnqueueAckForRedeem(string scn, string name, string credential, string issuedAt)
    {
        var ackJson = $$"""
        {
          "kind": "ack",
          "header": {
            "version": 1,
            "kind": "ack",
            "id": "ack_redeem_01",
            "correlationId": "cmd_redeem_01",
            "sequence": 1,
            "timestamp": "{{issuedAt}}",
            "serverConnectionId": "{{scn}}"
          },
          "payload": {
            "commandId": "cmd_redeem_01",
            "status": "ok",
            "serverConnectionId": "{{scn}}",
            "watchoffitServerName": "{{name}}",
            "issuedAt": "{{issuedAt}}",
            "credential": "{{credential}}"
          }
        }
        """;
        NextResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ackJson, Encoding.UTF8, "application/json"),
        };
    }
}
