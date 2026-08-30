using System.Globalization;
using System.Net;

using Jellyfin.Plugin.Watchoffit.Protocol.V1;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Acks;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Commands;

using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Watchoffit.Pairing;

/// <summary>
/// Outcome of a pairing state transition. Mirrors the state machine in
/// <c>docs/pairing-design.md</c> §8 — the
/// service returns the new state and an optional
/// <see cref="ApplicationErrorCode"/> when the transition is refused.
/// </summary>
public sealed record PairingTransitionResult(
    PairingState NewState,
    string? ErrorCode,
    string? ErrorMessage,
    WatchoffitConnection? Connection)
{
    /// <summary>True when the transition reached <see cref="PairingState.Paired"/>.</summary>
    public bool IsPaired => NewState == PairingState.Paired;
}

/// <summary>
/// State machine for the v1 pairing flow. Mirrors
/// <c>docs/pairing-design.md</c> §8.
/// </summary>
/// <remarks>
/// The service is thread-safe: every public method takes
/// <see cref="_stateLock"/> before reading or writing the in-memory
/// snapshot. The in-memory snapshot is the source of truth; the
/// on-disk file is a persistence mirror and is rewritten on every
/// transition. The dashboard reads <see cref="CurrentState"/> and
/// <see cref="CurrentConnection"/> to render the UI.
/// </remarks>
public sealed class PairingService
{
    private readonly WatchoffitConnectionStore _store;
    private readonly WatchoffitClient _client;
    private readonly IJellyfinSystemInfoProvider _systemInfo;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    private PairingState _currentState = PairingState.None;
    private WatchoffitConnection? _currentConnection;

    /// <summary>
    /// Initializes a new instance of the <see cref="PairingService"/> class.
    /// </summary>
    /// <param name="store">Connection store for the on-disk <c>connection.json</c>.</param>
    /// <param name="client">HTTP client for the Watchoffit plugin endpoints.</param>
    /// <param name="systemInfo">Source of the local Jellyfin server identity; used to populate the persisted <c>jellyfinServerId</c> field.</param>
    /// <param name="logger">Plugin logger. State transitions and round-trip outcomes are logged without ever logging credential values.</param>
    public PairingService(
        WatchoffitConnectionStore store,
        WatchoffitClient client,
        IJellyfinSystemInfoProvider systemInfo,
        ILogger<PairingService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _systemInfo = systemInfo ?? throw new ArgumentNullException(nameof(systemInfo));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Current state, thread-safe.</summary>
    public PairingState CurrentState
    {
        get
        {
            _stateLock.Wait();
            try
            {
                return _currentState;
            }
            finally
            {
                _stateLock.Release();
            }
        }
    }

    /// <summary>Current connection, thread-safe. Null in <see cref="PairingState.None"/>.</summary>
    public WatchoffitConnection? CurrentConnection
    {
        get
        {
            _stateLock.Wait();
            try
            {
                return _currentConnection;
            }
            finally
            {
                _stateLock.Release();
            }
        }
    }

    /// <summary>
    /// Load the on-disk connection on plugin startup. The runtime
    /// becomes whatever the file says (or <see cref="PairingState.None"/>
    /// if no file is present). Heartbeat is NOT started here — that
    /// belongs to the next commit's <c>PairingBackgroundService</c>.
    /// </summary>
    public void LoadFromStore()
    {
        var result = _store.TryLoad();
        _stateLock.Wait();
        try
        {
            switch (result)
            {
                case ConnectionLoadResult.Loaded loaded:
                    _currentConnection = loaded.Connection;
                    _currentState = loaded.Connection.State;
                    _logger.LogInformation(
                        "Loaded pairing state {State} for server {Server}",
                        _currentState,
                        _currentConnection.ServerConnectionId);
                    break;
                case ConnectionLoadResult.NotPresent:
                    _currentConnection = null;
                    _currentState = PairingState.None;
                    break;
                case ConnectionLoadResult.Corrupt corrupt:
                    _currentConnection = null;
                    _currentState = PairingState.None;
                    _logger.LogWarning("connection.json is corrupt: {Reason}", corrupt.Reason);
                    break;
                case ConnectionLoadResult.UnsupportedVersion unsupported:
                    _currentConnection = null;
                    _currentState = PairingState.None;
                    _logger.LogWarning("connection.json declares unsupported version {Version}", unsupported.Version);
                    break;
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// Step 1 of the pairing flow. Sends <c>challenge_request</c> and
    /// returns the ack payload so the UI can render the one-time code.
    /// Transitions the state to <see cref="PairingState.Challenge"/> on
    /// success, back to <see cref="PairingState.None"/> on failure.
    /// </summary>
    /// <param name="baseUrl">Watchoffit base URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tuple of (ack payload on success, error code on failure, error message on failure).</returns>
    public async Task<(V1ChallengeAck? Ack, string? ErrorCode, string? ErrorMessage)> ChallengeAsync(
        string baseUrl,
        CancellationToken cancellationToken)
    {
        var result = await _client.ChallengeAsync(baseUrl, cancellationToken).ConfigureAwait(false);
        return ClassifyChallenge(result);
    }

    /// <summary>
    /// Step 2 of the pairing flow. Redeems the user-entered code for a
    /// durable credential. On success, transitions to
    /// <see cref="PairingState.Paired"/> and persists the connection
    /// atomically.
    /// </summary>
    /// <param name="baseUrl">Watchoffit base URL.</param>
    /// <param name="serverConnectionId">Server connection id from the challenge ack.</param>
    /// <param name="pairingCode">6-16 char uppercase alphanumeric code shown in the Jellyfin dashboard.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Transition result with the new state and the persisted connection on success.</returns>
    public async Task<PairingTransitionResult> RedeemAsync(
        string baseUrl,
        string serverConnectionId,
        string pairingCode,
        CancellationToken cancellationToken)
    {
        var result = await _client.RedeemAsync(baseUrl, serverConnectionId, pairingCode, cancellationToken)
            .ConfigureAwait(false);
        if (result is not WatchoffitCallResult.Ack ack)
        {
            return MapTransportFailure(result, PairingState.None);
        }

        if (ack.Envelope.Payload is not V1RedeemAck redeem)
        {
            _logger.LogError("Redeem ack had unexpected payload kind {Kind}", ack.Envelope.Payload.GetType().Name);
            return new PairingTransitionResult(
                PairingState.None,
                ApplicationErrorCode.ItemNotFound.ToString(),
                "unexpected ack payload kind",
                null);
        }

        var info = _systemInfo.GetCurrent();
        var connection = new WatchoffitConnection
        {
            Version = WatchoffitConnectionStore.CurrentVersion,
            State = PairingState.Paired,
            BaseUrl = baseUrl,
            ServerConnectionId = redeem.ServerConnectionId,
            WatchoffitServerName = redeem.WatchoffitServerName,
            JellyfinServerId = info.JellyfinServerId,
            Credential = new WatchoffitCredential { Scheme = "plain", Value = redeem.Credential },
            Capabilities = V1EnvelopeBuilder.DefaultCapabilities,
            CreatedAt = redeem.IssuedAt,
            LastPingAt = string.Empty,
            LastErrorCode = null,
            LastErrorAt = null,
        };

        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _store.Save(connection);
            _currentConnection = connection;
            _currentState = PairingState.Paired;
        }
        finally
        {
            _stateLock.Release();
        }

        return new PairingTransitionResult(PairingState.Paired, null, null, connection);
    }

    /// <summary>
    /// Exchanges one opaque connection string for a durable paired connection.
    /// </summary>
    /// <param name="connectionString">Short-lived pairing bundle generated by Watchoffit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A paired connection or a safe validation/transport error.</returns>
    public Task<PairingTransitionResult> ConnectAsync(string connectionString, CancellationToken cancellationToken)
    {
        if (!WatchoffitConnectionString.TryParse(connectionString, out var parsed, out var error))
        {
            return Task.FromResult(new PairingTransitionResult(
                PairingState.None,
                nameof(SafeErrorCode.InvalidEnvelope),
                error,
                null));
        }

        return RedeemAsync(parsed!.BaseUrl, parsed.ServerConnectionId, parsed.PairingCode, cancellationToken);
    }

    /// <summary>
    /// Disconnect. Sends <c>revoke_request</c> to Watchoffit, then drops
    /// the local state. A Watchoffit failure does NOT keep the local
    /// credential — operator-initiated disconnect must succeed locally
    /// even when the remote is unreachable (matches design §7.1: "do
    /// not send more requests with that credential").
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Transition result. Always <see cref="PairingState.None"/> on return.</returns>
    public async Task<PairingTransitionResult> DisconnectAsync(CancellationToken cancellationToken)
    {
        WatchoffitConnection? snapshot;
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            snapshot = _currentConnection;
        }
        finally
        {
            _stateLock.Release();
        }

        if (snapshot is not null)
        {
            // Best-effort remote revoke. We log the failure but always
            // drop local state — see design §7.1.
            try
            {
                var result = await _client.RevokeAsync(
                    snapshot.BaseUrl,
                    snapshot.ServerConnectionId,
                    snapshot.Credential.Value,
                    cancellationToken).ConfigureAwait(false);
                if (result is WatchoffitCallResult.TransportFailure failure)
                {
                    _logger.LogWarning(
                        "Remote revoke failed (status {Status}); dropping local state anyway",
                        failure.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Remote revoke threw; dropping local state anyway");
            }
        }

        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _store.Forget();
            _currentConnection = null;
            _currentState = PairingState.None;
        }
        finally
        {
            _stateLock.Release();
        }

        return new PairingTransitionResult(PairingState.None, null, null, null);
    }

    /// <summary>
    /// Mark the local pairing as revoked because the remote refused
    /// the credential. Used by the <c>CommandPollingService</c> when
    /// Watchoffit returns 401/403 on the long-poll channel — the existing
    /// credential is dead on the Watchoffit side, so every other worker
    /// (event forwarder, outbox, inventory publisher) must stop
    /// trying to use it. The local snapshot is dropped, the on-disk
    /// <c>connection.json</c> is forgotten, and the state moves to
    /// <see cref="PairingState.None"/>. The operator has to repair
    /// the credential manually (or via a fresh pairing flow).
    /// </summary>
    /// <param name="reason">Short diagnostic surfaced to the plugin log; never includes the credential value.</param>
    /// <param name="expectedServerConnectionId">Server connection id from the request that was rejected.</param>
    public void MarkRevokedFromRemote(string reason, string expectedServerConnectionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        ArgumentException.ThrowIfNullOrEmpty(expectedServerConnectionId);

        _stateLock.Wait();
        try
        {
            if (_currentState == PairingState.None || _currentConnection is null)
            {
                // Already forgotten — the operator may have repaired
                // the state between the poll and the mark. Logging
                // the no-op is noisy and unhelpful.
                return;
            }

            if (!string.Equals(_currentConnection.ServerConnectionId, expectedServerConnectionId, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Watchoffit rejected stale pairing {RejectedServer}; current pairing is {CurrentServer}, so local state was left intact",
                    expectedServerConnectionId,
                    _currentConnection.ServerConnectionId);
                return;
            }

            _logger.LogError(
                "Watchoffit rejected the pairing credential ({Reason}); dropping local pairing state. The operator must repair or re-pair.",
                reason);

            _store.Forget();
            _currentConnection = null;
            _currentState = PairingState.None;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// Record the latest successful Watchoffit contact in the in-memory status
    /// snapshot. This intentionally does not persist every poll to disk.
    /// </summary>
    /// <param name="timestamp">UTC instant of the successful contact.</param>
    public void MarkContactSucceeded(DateTimeOffset timestamp)
    {
        _stateLock.Wait();
        try
        {
            if (_currentConnection is null)
            {
                return;
            }

            _currentConnection = _currentConnection with
            {
                LastPingAt = timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            };
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private (V1ChallengeAck? Ack, string? ErrorCode, string? ErrorMessage) ClassifyChallenge(WatchoffitCallResult result)
    {
        switch (result)
        {
            case WatchoffitCallResult.Ack ack when ack.Envelope.Payload is V1ChallengeAck challenge:
                return (challenge, null, null);
            case WatchoffitCallResult.Ack:
                return (null, nameof(SafeErrorCode.InvalidEnvelope), "unexpected ack payload kind");
            case WatchoffitCallResult.ApplicationError error:
                return (null, error.Envelope.Payload.Code, error.Envelope.Payload.Message);
            case WatchoffitCallResult.TransportFailure failure:
                return (null, MapTransportCode(failure.StatusCode), failure.Reason);
            default:
                return (null, SafeErrorCode.InternalError.ToString(), "unknown result");
        }
    }

    private static string MapTransportCode(int? statusCode) => statusCode switch
    {
        null => nameof(SafeErrorCode.InternalError),
        (int)HttpStatusCode.Unauthorized => nameof(SafeErrorCode.AuthRequired),
        (int)HttpStatusCode.TooManyRequests => nameof(SafeErrorCode.RateLimited),
        _ => nameof(SafeErrorCode.InternalError),
    };

    private PairingTransitionResult MapTransportFailure(WatchoffitCallResult result, PairingState fallbackState)
    {
        switch (result)
        {
            case WatchoffitCallResult.ApplicationError error:
                return new PairingTransitionResult(
                    fallbackState,
                    error.Envelope.Payload.Code,
                    error.Envelope.Payload.Message,
                    _currentConnection);
            case WatchoffitCallResult.TransportFailure failure:
                return new PairingTransitionResult(
                    fallbackState,
                    MapTransportCode(failure.StatusCode),
                    failure.Reason,
                    _currentConnection);
            case WatchoffitCallResult.Ack ack:
                return new PairingTransitionResult(
                    fallbackState,
                    nameof(SafeErrorCode.InvalidEnvelope),
                    "unexpected ack kind: " + ack.Envelope.Payload.GetType().Name,
                    _currentConnection);
            default:
                return new PairingTransitionResult(
                    fallbackState,
                    SafeErrorCode.InternalError.ToString(),
                    "unknown",
                    _currentConnection);
        }
    }
}
