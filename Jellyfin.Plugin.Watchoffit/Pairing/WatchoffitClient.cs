using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Jellyfin.Plugin.Watchoffit.Commands;
using Jellyfin.Plugin.Watchoffit.Protocol.V1;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Acks;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Commands;
using Jellyfin.Plugin.Watchoffit.Protocol.V1.Payloads.Events;

using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Watchoffit.Pairing;

/// <summary>
/// Result of a <see cref="WatchoffitClient"/> round-trip. Mirrors the
/// design's wire trace: either a parsed ack envelope, a parsed error
/// envelope, or a transport failure that the caller must surface as a
/// specific <c>ApplicationErrorCode</c>.
/// </summary>
/// <remarks>
/// The HTTP client never throws on transport failures — those become
/// <see cref="WatchoffitCallResult.TransportFailure"/>. The pairing service
/// classifies them into the right <c>ApplicationErrorCode</c> based
/// on the call site (e.g. <c>JELLYFIN_UNREACHABLE</c> for redeem,
/// <c>RATE_LIMITED_BY_REMOTE</c> for ping-with-429).
/// </remarks>
public abstract record WatchoffitCallResult
{
    /// <summary>Successful HTTP round-trip; the body parsed to an ack envelope.</summary>
    public sealed record Ack(V1AckEnvelope Envelope) : WatchoffitCallResult;

    /// <summary>HTTP round-trip returned a structured <c>error</c> envelope.</summary>
    public sealed record ApplicationError(V1ErrorEnvelope Envelope) : WatchoffitCallResult;

    /// <summary>HTTP round-trip failed at the transport layer (DNS, TCP, TLS, timeout, non-2xx without envelope body, etc.).</summary>
    public sealed record TransportFailure(string Reason, int? StatusCode) : WatchoffitCallResult;
}

/// <summary>
/// Typed HTTP client that builds v1 envelopes and calls every endpoint
/// in <c>docs/pairing-design.md</c> §3.
/// </summary>
/// <remarks>
/// All public methods accept a <see cref="CancellationToken"/>. The
/// class is thread-safe — the underlying <see cref="HttpClient"/> is
/// designed for concurrent use, and the envelope builder uses an
/// atomic sequence counter so two threads cannot interleave the same
/// <c>sequence</c> on the wire.
///
/// HTTP timeouts are owned by the injected <see cref="HttpClient"/>
/// (<c>Timeout</c>). The class does not set its own timeout; the
/// construction site (the plugin entry point) configures a 10-second
/// timeout per the design's §3.7 wire trace.
/// </remarks>
public sealed class WatchoffitClient
{
    private static readonly MediaTypeWithQualityHeaderValue JsonMediaType = new("application/json");

    private readonly HttpClient _http;
    private readonly V1EnvelopeBuilder _envelopeBuilder;
    private readonly IJellyfinSystemInfoProvider _systemInfo;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchoffitClient"/> class.
    /// </summary>
    /// <param name="http">Configured <see cref="HttpClient"/>. The caller owns <c>BaseAddress</c> and <c>Timeout</c>.</param>
    /// <param name="envelopeBuilder">Header builder. Shared across the plugin so sequence numbers are monotonic per server connection.</param>
    /// <param name="systemInfo">Source of the local Jellyfin server identity used on pairing requests.</param>
    /// <param name="logger">Plugin logger. The client logs request/response timing and the wire error code on failure; it never logs the credential value.</param>
    public WatchoffitClient(
        HttpClient http,
        V1EnvelopeBuilder envelopeBuilder,
        IJellyfinSystemInfoProvider systemInfo,
        ILogger<WatchoffitClient> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _envelopeBuilder = envelopeBuilder ?? throw new ArgumentNullException(nameof(envelopeBuilder));
        _systemInfo = systemInfo ?? throw new ArgumentNullException(nameof(systemInfo));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Step 1 of the pairing flow. Binds the local Jellyfin identity
    /// to a Watchoffit-side <c>serverConnectionId</c> and mints a one-time
    /// pairing code.
    /// </summary>
    /// <param name="baseUrl">Watchoffit base URL (no trailing slash).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ack envelope with the challenge payload, or a structured failure.</returns>
    public async Task<WatchoffitCallResult> ChallengeAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var info = _systemInfo.GetCurrent();
        var payload = new V1ChallengeRequestCommand
        {
            JellyfinServerId = info.JellyfinServerId,
            JellyfinVersion = info.JellyfinVersion,
            PluginVersion = V1EnvelopeBuilder.PluginVersion,
            PluginGuid = V1EnvelopeBuilder.PluginGuid,
        };

        var envelope = new V1CommandEnvelope
        {
            Header = _envelopeBuilder.BuildCommandHeader(V1EnvelopeBuilder.PendingServerConnectionId, "challenge_request"),
            Payload = payload,
        };

        return await SendCommandAsync(baseUrl, "api/watchoffit-plugin/pairing/challenge", envelope, credential: null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Step 2 of the pairing flow. Redeems the single-use pairing code
    /// for a long-lived opaque credential.
    /// </summary>
    /// <param name="baseUrl">Watchoffit base URL.</param>
    /// <param name="serverConnectionId">Server connection id returned by the challenge ack.</param>
    /// <param name="pairingCode">6-16 char uppercase alphanumeric code shown in the Jellyfin dashboard.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ack envelope with the redeem payload, or a structured failure.</returns>
    public async Task<WatchoffitCallResult> RedeemAsync(
        string baseUrl,
        string serverConnectionId,
        string pairingCode,
        CancellationToken cancellationToken)
    {
        var info = _systemInfo.GetCurrent();
        var payload = new V1RedeemRequestCommand
        {
            PairingCode = pairingCode,
            JellyfinServerId = info.JellyfinServerId,
        };

        var envelope = new V1CommandEnvelope
        {
            Header = _envelopeBuilder.BuildCommandHeader(serverConnectionId, "redeem_request"),
            Payload = payload,
        };

        return await SendCommandAsync(baseUrl, "api/watchoffit-plugin/pairing/redeem", envelope, credential: null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Step 3 of the pairing flow. Revokes the remote credential.
    /// </summary>
    /// <param name="baseUrl">Watchoffit base URL.</param>
    /// <param name="serverConnectionId">Bound server connection id.</param>
    /// <param name="credential">Current credential used to authenticate the revoke request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ack envelope with the generic <c>V1BaseAck</c> payload, or a structured failure.</returns>
    public async Task<WatchoffitCallResult> RevokeAsync(
        string baseUrl,
        string serverConnectionId,
        string credential,
        CancellationToken cancellationToken)
    {
        var info = _systemInfo.GetCurrent();
        var payload = new V1RevokeRequestCommand
        {
            JellyfinServerId = info.JellyfinServerId,
            JellyfinVersion = info.JellyfinVersion,
            PluginVersion = V1EnvelopeBuilder.PluginVersion,
            PluginGuid = V1EnvelopeBuilder.PluginGuid,
        };

        var envelope = new V1CommandEnvelope
        {
            Header = _envelopeBuilder.BuildCommandHeader(serverConnectionId, "revoke_request"),
            Payload = payload,
        };

        // The design's wire trace (§3.6) shows HTTP DELETE for revoke,
        // but `HttpClient.SendAsync` discards DELETE request bodies in
        // practice. The plugin sends POST to `/api/watchoffit-plugin/pairing/credential`
        // with the same body so the envelope is preserved on the wire.
        // Watchoffit's revoke handler accepts POST; the design will be
        // updated to match in a follow-up so TS and C# agree.
        return await SendCommandAsync(baseUrl, "api/watchoffit-plugin/pairing/credential", envelope, credential, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Periodic heartbeat. The plugin sends this every 30 s ± 20 %
    /// after a successful pair.
    /// </summary>
    /// <param name="baseUrl">Watchoffit base URL.</param>
    /// <param name="serverConnectionId">Bound server connection id.</param>
    /// <param name="credential">Current credential used to authenticate the ping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ack envelope with a generic payload, or a structured failure.</returns>
    public async Task<WatchoffitCallResult> PingAsync(
        string baseUrl,
        string serverConnectionId,
        string credential,
        CancellationToken cancellationToken)
    {
        // ping uses a heartbeat event per the design's §3.7 wire trace,
        // not the command shape. The envelope kind is "event".
        var info = _systemInfo.GetCurrent();
        var payload = new V1HeartbeatEvent
        {
            JellyfinItemId = info.JellyfinServerId,
            WatchoffitUserId = "system",
            MediaKind = V1MediaKind.Movie,
            QueueDepth = 0,
            LastSequence = _envelopeBuilder.CurrentSequence,
            PluginVersion = V1EnvelopeBuilder.PluginVersion,
        };

        var header = BuildEventHeader(serverConnectionId, "evt_heartbeat");

        var envelope = new V1EventEnvelope { Header = header, Payload = payload };
        return await SendEnvelopeAsync(baseUrl, "api/watchoffit-plugin/ping", envelope, credential, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Send a v1 event envelope. Used by the <c>EventForwarder</c> to
    /// push playback / user-data notifications to Watchoffit over the
    /// v1 channel. Authenticated with the per-install credential.
    /// </summary>
    /// <param name="baseUrl">Watchoffit base URL (no trailing slash).</param>
    /// <param name="serverConnectionId">Bound server connection id.</param>
    /// <param name="credential">Per-install credential.</param>
    /// <param name="envelope">Pre-built event envelope (header + payload).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ack envelope, or a structured failure.</returns>
    public Task<WatchoffitCallResult> SendEventAsync(
        string baseUrl,
        string serverConnectionId,
        string credential,
        V1EventEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return SendEnvelopeAsync(
            baseUrl,
            "api/watchoffit-plugin/event",
            envelope,
            credential,
            cancellationToken);
    }

    /// <summary>
    /// Long-poll Watchoffit for queued commands. Sends a v1 <c>ping</c>
    /// command envelope and returns the parsed response. The response
    /// is a v1 <c>ack</c> envelope whose payload is
    /// <see cref="V1CommandPollAckPayload"/>; the polling service
    /// narrows the typed <see cref="WatchoffitCallResult.Ack"/> to that
    /// shape before iterating leased commands.
    /// </summary>
    /// <param name="baseUrl">Watchoffit base URL (no trailing slash).</param>
    /// <param name="serverConnectionId">Bound server connection id.</param>
    /// <param name="credential">Per-install credential.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ack envelope carrying the leased commands (possibly empty), or a structured failure.</returns>
    public Task<WatchoffitCallResult> PollCommandAsync(
        string baseUrl,
        string serverConnectionId,
        string credential,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseUrl);
        ArgumentException.ThrowIfNullOrEmpty(serverConnectionId);
        ArgumentException.ThrowIfNullOrEmpty(credential);

        // The v1 ping command schema requires an identity block
        // (jellyfinItemId + watchoffitUserId + mediaKind) even though the
        // /command/poll route does not use the values — the v1
        // discriminated union pins identity on every item-level
        // command. We use the local Jellyfin server id as the item
        // placeholder (the same synthetic value the heartbeat event
        // uses) so the schema is satisfied without inventing a real
        // item. The nonce is fresh per call so Watchoffit can dedup
        // accidental replays.
        var info = _systemInfo.GetCurrent();
        var payload = new V1PingCommand
        {
            JellyfinItemId = info.JellyfinServerId,
            WatchoffitUserId = "system",
            MediaKind = V1MediaKind.Movie,
            Nonce = _envelopeBuilder.NewId("nonce"),
        };

        var envelope = new V1CommandEnvelope
        {
            Header = _envelopeBuilder.BuildCommandHeader(serverConnectionId, "ping"),
            Payload = payload,
        };

        return SendEnvelopeAsync(
            baseUrl,
            "api/watchoffit-plugin/command/poll",
            envelope,
            credential,
            cancellationToken);
    }

    /// <summary>
    /// Ack a leased command back to Watchoffit. The wire shape is a v1
    /// <c>ack</c> envelope with <c>payload.commandId</c> set to the
    /// leased command id and <c>header.id</c> set to the lease's
    /// <c>attemptToken</c> (per the v1 attempt-token echo convention;
    /// see <c>apps/server/src/routes/watchoffit-plugin-v1.ts</c> lines
    /// 890-913 for the server-side validation).
    /// </summary>
    /// <param name="baseUrl">Watchoffit base URL (no trailing slash).</param>
    /// <param name="serverConnectionId">Bound server connection id.</param>
    /// <param name="credential">Per-install credential.</param>
    /// <param name="leased">Leased command from the poll response.</param>
    /// <param name="result">Handler outcome to ack back to Watchoffit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ack envelope echoing the receipt, or a structured failure.</returns>
    public Task<WatchoffitCallResult> AckCommandAsync(
        string baseUrl,
        string serverConnectionId,
        string credential,
        V1LeasedCommand leased,
        V1CommandResult result,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseUrl);
        ArgumentException.ThrowIfNullOrEmpty(serverConnectionId);
        ArgumentException.ThrowIfNullOrEmpty(credential);
        ArgumentNullException.ThrowIfNull(leased);
        ArgumentNullException.ThrowIfNull(result);

        var envelope = new V1AckEnvelope
        {
            Header = _envelopeBuilder.BuildAckHeader(serverConnectionId, leased.AttemptToken, leased.CommandId),
            Payload = new V1BaseAck
            {
                CommandId = leased.CommandId,
                Status = ParseAckStatus(result.Status),
                Note = result.Note,
            },
        };

        return SendEnvelopeAsync(
            baseUrl,
            "api/watchoffit-plugin/command/ack",
            envelope,
            credential,
            cancellationToken);
    }

    private static V1AckStatus ParseAckStatus(string status)
    {
        if (string.Equals(status, V1CommandResult.OkStatus, StringComparison.Ordinal))
        {
            return V1AckStatus.Ok;
        }

        if (string.Equals(status, V1CommandResult.NoopStatus, StringComparison.Ordinal))
        {
            return V1AckStatus.Noop;
        }

        throw new ArgumentException(
            $"V1CommandResult.Status must be '{V1CommandResult.OkStatus}' or '{V1CommandResult.NoopStatus}' (got '{status}')",
            nameof(status));
    }

    /// <summary>
    /// Build a v1 event header with a fresh id, sequence, and timestamp.
    /// The envelope kind is fixed to <see cref="V1EnvelopeKind.Event"/>
    /// and capabilities are not included (events never carry them).
    /// </summary>
    /// <param name="serverConnectionId">Bound server connection id.</param>
    /// <param name="idKindPrefix">Prefix for the new id (e.g. <c>evt_playback_start</c>).</param>
    /// <param name="correlationId">Optional id of the command that caused this event.</param>
    /// <returns>A populated <see cref="V1Header"/>.</returns>
    public V1Header BuildEventHeader(
        string serverConnectionId,
        string idKindPrefix,
        string? correlationId = null)
    {
        return new V1Header
        {
            Version = V1ProtocolConstants.ProtocolVersion,
            Kind = V1EnvelopeKind.Event,
            Id = _envelopeBuilder.NewId(idKindPrefix),
            Sequence = _envelopeBuilder.NextSequence(),
            Timestamp = _envelopeBuilder.NowTimestamp(),
            ServerConnectionId = serverConnectionId,
            Capabilities = null,
            CorrelationId = correlationId,
        };
    }

    private async Task<WatchoffitCallResult> SendCommandAsync(
        string baseUrl,
        string path,
        V1CommandEnvelope envelope,
        string? credential,
        CancellationToken cancellationToken)
    {
        return await SendEnvelopeAsync(baseUrl, path, envelope, credential, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<WatchoffitCallResult> SendEnvelopeAsync(
        string baseUrl,
        string path,
        V1Envelope envelope,
        string? credential,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new WatchoffitCallResult.TransportFailure("baseUrl is empty", null);
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
            || !Uri.TryCreate(baseUri, path, out var requestUri))
        {
            return new WatchoffitCallResult.TransportFailure("baseUrl must be an absolute HTTP(S) URL", null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(JsonSerializer.Serialize(envelope, V1EnvelopeBuilderContext.Default.V1Envelope), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(JsonMediaType);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        if (!string.IsNullOrEmpty(credential))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WatchoffitCallResult.TransportFailure("timeout", null);
        }
        catch (HttpRequestException ex)
        {
            return new WatchoffitCallResult.TransportFailure(ex.Message, null);
        }

        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var parsed = V1EnvelopeParser.Parse(body);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Watchoffit call {Path} returned HTTP {Status}",
                    path,
                    (int)response.StatusCode);

                if (parsed is V1ParseResult.Ok { Envelope: V1ErrorEnvelope error })
                {
                    return new WatchoffitCallResult.ApplicationError(error);
                }

                return new WatchoffitCallResult.TransportFailure(
                    $"HTTP {(int)response.StatusCode}",
                    (int)response.StatusCode);
            }

            return parsed switch
            {
                V1ParseResult.Ok ok when ok.Envelope is V1AckEnvelope ack => new WatchoffitCallResult.Ack(ack),
                V1ParseResult.Ok ok when ok.Envelope is V1ErrorEnvelope err => new WatchoffitCallResult.ApplicationError(err),
                V1ParseResult.Ok ok => new WatchoffitCallResult.TransportFailure(
                    $"unexpected envelope kind: {ok.Envelope.Kind}",
                    (int)response.StatusCode),
                V1ParseResult.Failure failure => new WatchoffitCallResult.TransportFailure(
                    $"invalid envelope: {failure.Code} {failure.Message}",
                    (int)response.StatusCode),
                _ => new WatchoffitCallResult.TransportFailure("unknown parse result", (int)response.StatusCode),
            };
        }
        finally
        {
            response.Dispose();
        }
    }
}

/// <summary>
/// System.Text.Json source-generation context. Avoids reflection at
/// runtime and makes the wire serialization AOT-friendly.
/// </summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(V1Envelope))]
[System.Text.Json.Serialization.JsonSerializable(typeof(V1CommandEnvelope))]
[System.Text.Json.Serialization.JsonSerializable(typeof(V1EventEnvelope))]
[System.Text.Json.Serialization.JsonSerializable(typeof(V1HeartbeatEvent))]
internal sealed partial class V1EnvelopeBuilderContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
