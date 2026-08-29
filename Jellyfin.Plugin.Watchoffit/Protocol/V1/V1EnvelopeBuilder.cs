using System.Globalization;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.Watchoffit.Protocol.V1;

/// <summary>
/// Builds outgoing v1 envelopes. Centralises the id/sequence/timestamp
/// rules so the HTTP client cannot accidentally emit a malformed
/// header (the parser will reject it on the way back, but emitting
/// clean envelopes is faster to debug than chasing a 4xx).
/// </summary>
/// <remarks>
/// The builder is intentionally stateful: it owns the per-connection
/// sequence counter so two threads in the same plugin cannot interleave
/// the same <c>sequence</c> on the wire. The counter can be advanced from
/// durable outbox state after a plugin restart before any new envelope is
/// built.
/// </remarks>
public sealed class V1EnvelopeBuilder
{
    private readonly Func<DateTimeOffset> _clock;
    private long _sequence;

    /// <summary>
    /// Initializes a new instance of the <see cref="V1EnvelopeBuilder"/> class.
    /// </summary>
    /// <param name="clock">UTC clock. Tests can inject a fixed clock; production uses <c>() => DateTimeOffset.UtcNow</c>.</param>
    public V1EnvelopeBuilder(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
        _sequence = 0;
    }

    /// <summary>The literal <c>pending</c> value used as <c>serverConnectionId</c> on pre-pairing endpoints.</summary>
    public const string PendingServerConnectionId = "pending";

    /// <summary>The plugin's static GUID. Matches <c>WatchoffitPlugin.Id</c> and the <c>meta.json</c> <c>guid</c> field.</summary>
    public static readonly Guid PluginGuid = Guid.Parse("ed8e9c41-2e0f-5872-93f2-06feb1bc37d1");

    /// <summary>The current plugin version, sourced from the built assembly metadata.</summary>
    public static readonly string PluginVersion =
        typeof(V1EnvelopeBuilder).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

    /// <summary>The capabilities the plugin advertises to Watchoffit during pairing.</summary>
    public static readonly V1Capabilities DefaultCapabilities = new()
    {
        MinProtocolVersion = V1ProtocolConstants.ProtocolVersion,
        MaxProtocolVersion = V1ProtocolConstants.ProtocolVersion,
        MaxPayloadBytes = V1ProtocolConstants.MaxPayloadBytes,
        MaxBatchSize = V1ProtocolConstants.MaxBatchSize,
    };

    /// <summary>Current sequence value (post-increment would happen on <see cref="NextSequence"/>). Visible for tests.</summary>
    public long CurrentSequence => Interlocked.Read(ref _sequence);

    /// <summary>Atomically increment and return the next sequence value.</summary>
    /// <returns>The new sequence value to put in the envelope header.</returns>
    public long NextSequence() => Interlocked.Increment(ref _sequence);

    /// <summary>
    /// Atomically advance the sequence counter when a durable sender state
    /// proves that lower sequence values have already been observed.
    /// </summary>
    /// <param name="sequence">The minimum counter value that must be retained.</param>
    public void EnsureAtLeast(long sequence)
    {
        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "sequence cannot be negative");
        }

        while (true)
        {
            var current = Interlocked.Read(ref _sequence);
            if (current >= sequence)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _sequence, sequence, current) == current)
            {
                return;
            }
        }
    }

    /// <summary>Generate a unique envelope id. The format mirrors what Watchoffit emits: <c>cmd_&lt;base64url&gt;</c>.</summary>
    /// <param name="kind">"cmd" for command envelopes, "evt" for event envelopes, "ack"/"err" for replies.</param>
    /// <returns>A new envelope id.</returns>
    public string NewId(string kind)
    {
        Span<byte> bytes = stackalloc byte[10];
        RandomNumberGenerator.Fill(bytes);
        var base64Url = Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        return $"{kind}_{base64Url[..12]}";
    }

    /// <summary>Format the current UTC time as the protocol's strict <c>.fffZ</c> literal.</summary>
    /// <returns>ISO 8601 timestamp with mandatory millisecond precision, e.g. <c>2026-08-27T10:00:00.000Z</c>.</returns>
    public string NowTimestamp()
    {
        var t = _clock();
        // The wire format mandates exactly 3 fractional digits. The
        // default "O" format yields 7; we have to truncate.
        return t.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }

    /// <summary>Build a v1 command header.</summary>
    /// <param name="serverConnectionId">Bound connection id, or <see cref="PendingServerConnectionId"/> for pre-pairing.</param>
    /// <param name="payloadKind">Wire literal of the payload, e.g. <c>"challenge_request"</c>.</param>
    /// <returns>A new header with id, sequence, timestamp, capabilities, and the literal <c>command</c> kind.</returns>
    public V1Header BuildCommandHeader(string serverConnectionId, string payloadKind)
    {
        return new V1Header
        {
            Version = V1ProtocolConstants.ProtocolVersion,
            Kind = V1EnvelopeKind.Command,
            Id = NewId($"cmd_{payloadKind}"),
            Sequence = NextSequence(),
            Timestamp = NowTimestamp(),
            ServerConnectionId = serverConnectionId,
            Capabilities = DefaultCapabilities,
        };
    }

    /// <summary>
    /// Build a v1 ack header. Used by the
    /// <c>CommandPollingService</c> when it acks a leased command —
    /// the protocol's attempt-token echo convention places the lease's
    /// <c>att_&lt;uuidv7&gt;</c> token in <c>header.id</c> so the
    /// server can verify the ack came from the poll that leased the
    /// row.
    /// </summary>
    /// <param name="serverConnectionId">Bound connection id. Must match the leased command's bound connection.</param>
    /// <param name="id">Per the attempt-token echo convention this is the lease's <c>attemptToken</c> string. The caller is responsible for passing the verbatim <c>att_</c>-prefixed token.</param>
    /// <param name="correlationId">The <c>commandId</c> being acknowledged. Required by the v1 ack invariant.</param>
    /// <returns>A new header with the literal <c>ack</c> kind, fresh sequence + timestamp, and the supplied id / correlationId.</returns>
    public V1Header BuildAckHeader(string serverConnectionId, string id, string correlationId)
    {
        if (string.IsNullOrEmpty(serverConnectionId))
        {
            throw new ArgumentException("serverConnectionId is required", nameof(serverConnectionId));
        }

        if (string.IsNullOrEmpty(id))
        {
            throw new ArgumentException("id is required", nameof(id));
        }

        if (string.IsNullOrEmpty(correlationId))
        {
            throw new ArgumentException("correlationId is required", nameof(correlationId));
        }

        return new V1Header
        {
            Version = V1ProtocolConstants.ProtocolVersion,
            Kind = V1EnvelopeKind.Ack,
            Id = id,
            Sequence = NextSequence(),
            Timestamp = NowTimestamp(),
            ServerConnectionId = serverConnectionId,
            // Per the v1 protocol, ack envelopes do not carry
            // capabilities. Leaving it null lets
            // `JsonIgnoreCondition.WhenWritingNull` drop the field on
            // the wire.
            Capabilities = null,
            CorrelationId = correlationId,
        };
    }
}
