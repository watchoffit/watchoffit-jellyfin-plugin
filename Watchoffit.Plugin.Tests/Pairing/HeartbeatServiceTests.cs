using System.Net;
using System.Text;

using Jellyfin.Plugin.Watchoffit.Pairing;
using Jellyfin.Plugin.Watchoffit.Protocol.V1;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Jellyfin.Plugin.Watchoffit.Tests.Pairing;

/// <summary>Tests the periodic heartbeat worker that drives Watchoffit health status.</summary>
public sealed class HeartbeatServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SequencedHttpMessageHandler _handler;
    private readonly HttpClient _http;
    private readonly WatchoffitConnectionStore _store;
    private readonly PairingService _pairing;
    private readonly HeartbeatService _service;

    public HeartbeatServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "watchoffit-heartbeat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _handler = new SequencedHttpMessageHandler();
        _http = new HttpClient(_handler) { BaseAddress = new Uri("https://watchoffit.test/") };

        var builder = new V1EnvelopeBuilder();
        var systemInfo = new StaticJellyfinSystemInfoProvider("jf_server_01", "10.11.11");
        var client = new WatchoffitClient(_http, builder, systemInfo, NullLogger<WatchoffitClient>.Instance);

        _store = new WatchoffitConnectionStore(
            _tempDir,
            new PlainCredentialProtector(),
            NullLogger<WatchoffitConnectionStore>.Instance);
        _pairing = new PairingService(_store, client, systemInfo, NullLogger<PairingService>.Instance);
        _service = new HeartbeatService(
            client,
            _pairing,
            NullLogger<HeartbeatService>.Instance,
            TimeSpan.FromMilliseconds(50));
    }

    public void Dispose()
    {
        _service.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _service.Dispose();
        _http.Dispose();
        _handler.Dispose();

        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task HeartbeatOnceAsync_Paired_SendsPingAndMarksContact()
    {
        SeedPairedConnection();
        _handler.Enqueue(OkResponse(GenericAckJson("evt_hb")));

        await _service.HeartbeatOnceAsync(CancellationToken.None);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("/api/watchoffit-plugin/ping", request.Path);
        Assert.Equal("Bearer cred_test", request.Authorization);
        Assert.Contains("\"kind\":\"event\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"heartbeat\"", request.Body, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(_pairing.CurrentConnection?.LastPingAt));
    }

    [Fact]
    public async Task HeartbeatOnceAsync_NotPaired_SkipsPing()
    {
        _handler.Enqueue(OkResponse(GenericAckJson("evt_unused")));

        await _service.HeartbeatOnceAsync(CancellationToken.None);

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task HeartbeatOnceAsync_UnauthorizedResponse_MarksPairingRevoked()
    {
        SeedPairedConnection();
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("credential rejected", Encoding.UTF8, "application/json"),
        });

        await _service.HeartbeatOnceAsync(CancellationToken.None);

        Assert.Equal(PairingState.None, _pairing.CurrentState);
        Assert.Null(_pairing.CurrentConnection);
    }

    [Fact]
    public void Constructor_RejectsZeroOrNegativeInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HeartbeatService(
                new WatchoffitClient(
                    _http,
                    new V1EnvelopeBuilder(),
                    new StaticJellyfinSystemInfoProvider("jf_server_01", "10.11.11"),
                    NullLogger<WatchoffitClient>.Instance),
                _pairing,
                NullLogger<HeartbeatService>.Instance,
                TimeSpan.Zero));
    }

    private void SeedPairedConnection()
    {
        _store.Save(new WatchoffitConnection
        {
            State = PairingState.Paired,
            BaseUrl = "https://watchoffit.test",
            ServerConnectionId = "scn_01",
            WatchoffitServerName = "Watchoffit",
            JellyfinServerId = "jf_server_01",
            Credential = new WatchoffitCredential { Scheme = "plain", Value = "cred_test" },
        });
        _pairing.LoadFromStore();
    }

    private static HttpResponseMessage OkResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static string GenericAckJson(string commandId) => $$"""
    {
      "kind": "ack",
      "header": {
        "version": 1, "kind": "ack", "id": "ack_{{commandId}}",
        "correlationId": "{{commandId}}", "sequence": 1,
        "timestamp": "2026-08-28T12:00:00.000Z", "serverConnectionId": "scn_01"
      },
      "payload": { "commandId": "{{commandId}}", "status": "ok", "note": "ok" }
    }
    """;
}
