using System.Net;
using System.Text;
using System.Text.Json;

using Jellyfin.Plugin.Watchoffit.Commands;
using Jellyfin.Plugin.Watchoffit.Commands.Handlers;
using Jellyfin.Plugin.Watchoffit.Pairing;
using Jellyfin.Plugin.Watchoffit.Protocol.V1;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Jellyfin.Plugin.Watchoffit.Tests.Commands;

/// <summary>
/// Integration tests for <see cref="CommandPollingService"/>. The
/// service drives a real <see cref="WatchoffitClient"/> against a
/// <see cref="SequencedHttpMessageHandler"/> so the wire format is
/// pinned end-to-end; only the transport is faked.
/// </summary>
public sealed class CommandPollingServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SequencedHttpMessageHandler _handler;
    private readonly HttpClient _http;
    private readonly WatchoffitClient _watchoffitClient;
    private readonly WatchoffitConnectionStore _store;
    private readonly PairingService _pairing;
    private readonly CommandPollingService _service;

    public CommandPollingServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "watchoffit-poll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _handler = new SequencedHttpMessageHandler();
        _http = new HttpClient(_handler) { BaseAddress = new Uri("https://watchoffit.test/") };

        var builder = new V1EnvelopeBuilder();
        var systemInfo = new StaticJellyfinSystemInfoProvider("jf_server_01", "10.11.11");
        _watchoffitClient = new WatchoffitClient(_http, builder, systemInfo, NullLogger<WatchoffitClient>.Instance);

        _store = new WatchoffitConnectionStore(
            _tempDir,
            new PlainCredentialProtector(),
            NullLogger<WatchoffitConnectionStore>.Instance);

        _pairing = new PairingService(_store, _watchoffitClient, systemInfo, NullLogger<PairingService>.Instance);

        var handlers = new CommandHandlerRegistry(new ICommandHandler[] { new PingCommandHandler() });
        _service = new CommandPollingService(
            _watchoffitClient,
            _pairing,
            handlers,
            NullLogger<CommandPollingService>.Instance,
            pollInterval: TimeSpan.FromMilliseconds(50));
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

        // The `BackgroundService` registers an internal cancellation
        // source that needs to dispose when the test does. Stop
        // the service explicitly so the source is released and we
        // can silence the CA2213 warning on `_service`.
        _service.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _service.Dispose();
        _http.Dispose();
        _handler.Dispose();

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task PollOnceAsync_LeasedPingCommand_DispatchesAndAcks()
    {
        // Pairing must be in `Paired` state with a real credential
        // for the polling service to send a poll envelope.
        SeedPairedConnection();
        EnqueuePollResponseWithSinglePing();
        EnqueueGenericAck();

        await _service.PollOnceAsync(CancellationToken.None);

        // Two requests fired: one for /command/poll, one for
        // /command/ack. The order matters because the ack uses the
        // `commandId` and `attemptToken` from the poll response.
        Assert.Equal(2, _handler.Requests.Count);
        var poll = _handler.Requests[0];
        var ack = _handler.Requests[1];
        Assert.Equal("/api/watchoffit-plugin/command/poll", poll.Path);
        Assert.Equal("/api/watchoffit-plugin/command/ack", ack.Path);

        // The poll request carries a v1 ping command envelope.
        Assert.NotNull(poll.Body);
        Assert.Contains("\"kind\":\"command\"", poll.Body, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"ping\"", poll.Body, StringComparison.Ordinal);
        Assert.Contains("\"serverConnectionId\":\"scn_01\"", poll.Body, StringComparison.Ordinal);

        // The ack request carries the leased commandId + status
        // + the attempt-token echo in `header.id`.
        Assert.NotNull(ack.Body);
        Assert.Contains("\"commandId\":\"cmd_test_001\"", ack.Body, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"ok\"", ack.Body, StringComparison.Ordinal);
        Assert.Contains("\"correlationId\":\"cmd_test_001\"", ack.Body, StringComparison.Ordinal);
        // The attempt-token echo: `header.id` on the ack envelope
        // equals the `attemptToken` from the poll response.
        Assert.Contains("\"id\":\"att_test_001\"", ack.Body, StringComparison.Ordinal);

        // The pairing is still `Paired` — a successful poll does
        // not revoke the credential.
        Assert.Equal(PairingState.Paired, _pairing.CurrentState);
    }

    [Fact]
    public async Task PollOnceAsync_NotPaired_SkipsPoll()
    {
        // No seed → state stays `None`. The service must not even
        // open a connection.
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("unused", Encoding.UTF8, "application/json"),
        });

        await _service.PollOnceAsync(CancellationToken.None);

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task PollOnceAsync_EmptyCommands_DoesNotSendAck()
    {
        SeedPairedConnection();
        EnqueueEmptyPollResponse();

        await _service.PollOnceAsync(CancellationToken.None);

        // Only the poll request fires — an empty lease batch is
        // not acked, by design.
        Assert.Single(_handler.Requests);
        Assert.Equal("/api/watchoffit-plugin/command/poll", _handler.Requests[0].Path);
    }

    [Fact]
    public async Task PollOnceAsync_UnauthorizedResponse_MarksPairingRevoked()
    {
        SeedPairedConnection();
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("credential rejected", Encoding.UTF8, "application/json"),
        });

        await _service.PollOnceAsync(CancellationToken.None);

        // 401 on the poll channel → the credential is dead on
        // the Watchoffit side; every other worker must stop using it.
        Assert.Equal(PairingState.None, _pairing.CurrentState);
        Assert.Null(_pairing.CurrentConnection);

        // No ack was sent — there was no leased command to ack.
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task PollOnceAsync_ForbiddenResponse_MarksPairingRevoked()
    {
        SeedPairedConnection();
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("forbidden", Encoding.UTF8, "application/json"),
        });

        await _service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(PairingState.None, _pairing.CurrentState);
        Assert.Null(_pairing.CurrentConnection);
    }

    [Fact]
    public async Task PollOnceAsync_GenericServerError_LeavesPairingIntact()
    {
        SeedPairedConnection();
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom", Encoding.UTF8, "application/json"),
        });

        await _service.PollOnceAsync(CancellationToken.None);

        // A 5xx is unrecoverable for this tick but the credential
        // is not invalidated. The pairing stays intact so the
        // next tick can retry.
        Assert.Equal(PairingState.Paired, _pairing.CurrentState);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task PollOnceAsync_AckFailure_LeavesPairingIntact()
    {
        SeedPairedConnection();
        EnqueuePollResponseWithSinglePing();
        // The ack path returns 5xx — the lease stays unacked on
        // the server side, but the local pairing is still valid.
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("ack failed", Encoding.UTF8, "application/json"),
        });

        await _service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(PairingState.Paired, _pairing.CurrentState);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task PollOnceAsync_GenericAckPayload_LogsAndSkips()
    {
        // The server only emits `command_poll_ack` for this route
        // today, but a future regression that sends a generic
        // ack must surface as a hard warning, not a silent
        // mis-iteration. The polling service logs and returns
        // without acking.
        SeedPairedConnection();
        _handler.Enqueue(OkResponse(genericAckJson("poll_xyz")));

        await _service.PollOnceAsync(CancellationToken.None);

        Assert.Single(_handler.Requests);
        Assert.Equal(PairingState.Paired, _pairing.CurrentState);
    }

    [Fact]
    public void Constructor_RejectsZeroOrNegativeInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CommandPollingService(
                _watchoffitClient,
                _pairing,
                new CommandHandlerRegistry(Array.Empty<ICommandHandler>()),
                NullLogger<CommandPollingService>.Instance,
                pollInterval: TimeSpan.Zero));
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

    private void EnqueuePollResponseWithSinglePing()
    {
        _handler.Enqueue(OkResponse(commandPollAckJson(
            pollCommandId: "poll_xyz_001",
            serverConnectionId: "scn_01",
            commandId: "cmd_test_001",
            commandKind: "ping",
            attemptToken: "att_test_001",
            nonce: "nonce_test")));
    }

    private void EnqueueEmptyPollResponse()
    {
        _handler.Enqueue(OkResponse(commandPollAckJson(
            pollCommandId: "poll_xyz_002",
            serverConnectionId: "scn_01",
            commandId: null,
            commandKind: null,
            attemptToken: null,
            nonce: null)));
    }

    private void EnqueueGenericAck()
    {
        _handler.Enqueue(OkResponse(genericAckJson("cmd_test_001")));
    }

    private static HttpResponseMessage OkResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static string commandPollAckJson(
        string pollCommandId,
        string serverConnectionId,
        string? commandId,
        string? commandKind,
        string? attemptToken,
        string? nonce) => commandId is null
            ? $$"""
            {
              "kind": "ack",
              "header": {
                "version": 1, "kind": "ack", "id": "ack_{{pollCommandId}}",
                "correlationId": "{{pollCommandId}}", "sequence": 1,
                "timestamp": "2026-08-28T12:00:00.000Z", "serverConnectionId": "{{serverConnectionId}}"
              },
              "payload": {
                "commandId": "{{pollCommandId}}",
                "status": "ok",
                "kind": "command_poll_ack",
                "commands": []
              }
            }
            """
            : $$"""
            {
              "kind": "ack",
              "header": {
                "version": 1, "kind": "ack", "id": "ack_{{pollCommandId}}",
                "correlationId": "{{pollCommandId}}", "sequence": 1,
                "timestamp": "2026-08-28T12:00:00.000Z", "serverConnectionId": "{{serverConnectionId}}"
              },
              "payload": {
                "commandId": "{{pollCommandId}}",
                "status": "ok",
                "kind": "command_poll_ack",
                "commands": [
                  {
                    "commandId": "{{commandId}}",
                    "commandKind": "{{commandKind}}",
                    "payload": { "nonce": "{{nonce}}" },
                    "leaseUntil": 1735776000,
                    "attemptToken": "{{attemptToken}}"
                  }
                ]
              }
            }
            """;

    private static string genericAckJson(string commandId) => $$"""
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

/// <summary>
/// Test <see cref="HttpMessageHandler"/> that records every request
/// and returns a queued response in order. Mirrors the
/// <c>SequencedHttpMessageHandler</c> in
/// <c>WatchoffitClientTests.cs</c> but lives next to the polling tests
/// for locality.
/// </summary>
internal sealed class SequencedHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    private readonly List<RecordedRequest> _requests = new();

    public IReadOnlyList<RecordedRequest> Requests => _requests;

    public void Enqueue(HttpResponseMessage response) => _responses.Enqueue(response);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        _requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri?.AbsolutePath ?? string.Empty,
            body,
            request.Headers.Authorization?.ToString()));
        if (_responses.Count == 0)
        {
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("no queued response", Encoding.UTF8, "application/json"),
            };
        }

        return _responses.Dequeue();
    }
}

/// <summary>Captured outgoing request for assertions.</summary>
internal sealed record RecordedRequest(HttpMethod Method, string Path, string? Body, string? Authorization);
