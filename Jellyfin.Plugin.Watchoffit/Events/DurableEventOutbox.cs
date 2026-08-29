using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Jellyfin.Plugin.Watchoffit.Protocol.V1;

using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Watchoffit.Events;

/// <summary>
/// Durable FIFO queue for outbound Jellyfin event envelopes.
/// </summary>
/// <remarks>
/// Every pending event is an individual, atomically-written JSON file under
/// the plugin data directory. Individual files make acknowledgement deletion
/// atomic, keep recovery simple, and ensure a process crash can never erase
/// an older queue entry while persisting a newer one.
/// </remarks>
public sealed class DurableEventOutbox
{
    /// <summary>Maximum number of pending events retained by a default installation.</summary>
    public const int DefaultCapacity = 1000;

    /// <summary>Number of retryable failures allowed inside the rolling failure window.</summary>
    public const int MaximumFailuresPerHour = 10;

    private const string PendingDirectoryName = "watchoffit-event-outbox";
    private const string DeadLetterDirectoryName = "dead-letter";
    private const string SequenceWatermarkFileName = "sequence-watermark.json";
    private const string TempFileSuffix = ".tmp";
    private const int EntryVersion = 1;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _pendingDirectory;
    private readonly string _deadLetterDirectory;
    private readonly int _capacity;
    private readonly ILogger _logger;
    private readonly object _ioLock = new();
    private readonly SemaphoreSlim _changed = new(0);

    /// <summary>
    /// Initializes a new instance of the <see cref="DurableEventOutbox"/> class.
    /// </summary>
    /// <param name="pluginDataPath">Jellyfin's per-plugin <c>DataFolderPath</c>.</param>
    /// <param name="logger">Logger used for durable queue diagnostics.</param>
    /// <param name="capacity">Maximum pending entry count. Intended to be overridden only by tests.</param>
    public DurableEventOutbox(string pluginDataPath, ILogger<DurableEventOutbox> logger, int capacity = DefaultCapacity)
    {
        if (string.IsNullOrWhiteSpace(pluginDataPath))
        {
            throw new ArgumentException("pluginDataPath must be a non-empty path", nameof(pluginDataPath));
        }

        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be positive");
        }

        _pendingDirectory = Path.Combine(pluginDataPath, PendingDirectoryName);
        _deadLetterDirectory = Path.Combine(_pendingDirectory, DeadLetterDirectoryName);
        _capacity = capacity;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Current pending queue depth, including entries restored after a restart.</summary>
    public int QueueDepth
    {
        get
        {
            lock (_ioLock)
            {
                return PendingPathsLocked().Count;
            }
        }
    }

    /// <summary>Current dead-letter depth.</summary>
    public int DeadLetterDepth
    {
        get
        {
            lock (_ioLock)
            {
                return Directory.Exists(_deadLetterDirectory)
                    ? Directory.GetFiles(_deadLetterDirectory, "*.json").Length
                    : 0;
            }
        }
    }

    /// <summary>Highest pending header sequence. Used for diagnostics and restart checks.</summary>
    internal long HighestPendingSequence
    {
        get
        {
            lock (_ioLock)
            {
                return ReadPendingItemsLocked().Select(static item => item.Entry.Envelope.Header.Sequence).DefaultIfEmpty(0).Max();
            }
        }
    }

    /// <summary>
    /// Restore the envelope builder's sequence from durable sender state before
    /// any new event envelope is created after a plugin restart.
    /// </summary>
    /// <param name="envelopeBuilder">Shared v1 header builder to advance.</param>
    internal void RestoreSequenceWatermark(V1EnvelopeBuilder envelopeBuilder)
    {
        ArgumentNullException.ThrowIfNull(envelopeBuilder);

        lock (_ioLock)
        {
            var persisted = ReadSequenceWatermarkLocked();
            // A crash between entry and watermark writes can leave a new
            // pending item beyond the saved watermark. Include it to repair
            // the state, while the persisted watermark protects acknowledged
            // entries that are no longer present in the queue.
            var pending = ReadPendingItemsLocked()
                .Select(static item => item.Entry.Envelope.Header.Sequence)
                .DefaultIfEmpty(0)
                .Max();
            var watermark = Math.Max(persisted, pending);
            if (watermark != persisted)
            {
                WriteSequenceWatermarkLocked(watermark);
            }

            envelopeBuilder.EnsureAtLeast(watermark);
        }
    }

    /// <summary>
    /// Persist a new event before any network operation is attempted.
    /// </summary>
    /// <param name="envelope">Fully constructed event envelope.</param>
    /// <returns>Whether the entry was accepted or rejected because the queue is full.</returns>
    internal EventOutboxEnqueueResult TryEnqueue(V1EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        lock (_ioLock)
        {
            ObserveSequenceLocked(envelope.Header.Sequence);
            var pendingPaths = PendingPathsLocked();
            if (pendingPaths.Count >= _capacity)
            {
                return EventOutboxEnqueueResult.Full;
            }

            var path = PendingPathFor(envelope);
            if (File.Exists(path))
            {
                // Re-enqueuing the same durable id is idempotent. Never overwrite
                // the original attempt metadata or move it behind newer events.
                return EventOutboxEnqueueResult.AlreadyQueued;
            }

            var entry = new DurableEventOutboxEntry
            {
                Version = EntryVersion,
                Envelope = envelope,
                FailureTimestamps = [],
                NextAttemptAt = null,
                LastFailure = null,
            };
            WriteAtomicallyLocked(path, entry);
        }

        SignalChanged();
        return EventOutboxEnqueueResult.Accepted;
    }

    /// <summary>Get the oldest pending entry by protocol header sequence.</summary>
    /// <returns>The next durable item, or <see langword="null"/> when the queue is empty.</returns>
    internal DurableEventOutboxItem? TryGetHead()
    {
        lock (_ioLock)
        {
            foreach (var item in ReadPendingItemsLocked())
            {
                if (File.Exists(DeadLetterPathFor(item.Path)))
                {
                    // A crash may occur after the durable dead-letter write and
                    // before deleting pending. Recovery must favour no resend.
                    TryDelete(item.Path);
                    continue;
                }

                return item;
            }

            return null;
        }
    }

    /// <summary>Delete a pending entry only after a validated matching acknowledgement.</summary>
    /// <param name="item">The pending item represented by the received acknowledgement.</param>
    /// <returns><see langword="true"/> when the matching pending entry was deleted.</returns>
    internal bool Acknowledge(DurableEventOutboxItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        lock (_ioLock)
        {
            if (!File.Exists(item.Path))
            {
                return false;
            }

            var current = TryReadEntryLocked(item.Path);
            if (current is null || !SameEnvelope(current.Envelope, item.Entry.Envelope))
            {
                return false;
            }

            File.Delete(item.Path);
        }

        SignalChanged();
        return true;
    }

    /// <summary>
    /// Persist retry metadata or move the event to dead-letter after the rolling
    /// ten-failure threshold.
    /// </summary>
    /// <param name="item">The pending item that failed delivery.</param>
    /// <param name="now">The time at which this retryable failure occurred.</param>
    /// <param name="delay">The calculated delay before the next delivery attempt.</param>
    /// <param name="reason">A credential-free failure description for diagnostics.</param>
    /// <returns>The durable retry update result.</returns>
    internal EventOutboxRetryResult RecordRetry(
        DurableEventOutboxItem item,
        DateTimeOffset now,
        TimeSpan delay,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        lock (_ioLock)
        {
            var current = TryReadEntryLocked(item.Path);
            if (current is null || !SameEnvelope(current.Envelope, item.Entry.Envelope))
            {
                return new EventOutboxRetryResult(false, 0);
            }

            var failures = current.FailureTimestamps
                .Where(timestamp => timestamp >= now - FailureWindow)
                .Append(now)
                .ToList();

            if (failures.Count >= MaximumFailuresPerHour)
            {
                MoveToDeadLetterLocked(
                    item.Path,
                    current with
                    {
                        FailureTimestamps = failures,
                        LastFailure = reason,
                        NextAttemptAt = null,
                    },
                    reason,
                    now);
                return new EventOutboxRetryResult(true, failures.Count);
            }

            WriteAtomicallyLocked(item.Path, current with
            {
                FailureTimestamps = failures,
                NextAttemptAt = now + delay,
                LastFailure = reason,
            });
            return new EventOutboxRetryResult(false, failures.Count);
        }
    }

    /// <summary>Move a terminally failed entry to durable dead-letter storage.</summary>
    /// <param name="item">The pending item that reached a terminal failure.</param>
    /// <param name="now">The time at which the item was dead-lettered.</param>
    /// <param name="reason">A credential-free terminal failure description.</param>
    internal void MoveToDeadLetter(DurableEventOutboxItem item, DateTimeOffset now, string reason)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        lock (_ioLock)
        {
            var current = TryReadEntryLocked(item.Path);
            if (current is null || !SameEnvelope(current.Envelope, item.Entry.Envelope))
            {
                return;
            }

            MoveToDeadLetterLocked(item.Path, current with { LastFailure = reason, NextAttemptAt = null }, reason, now);
        }

        SignalChanged();
    }

    /// <summary>Wait until the queue changes, or cancellation is requested.</summary>
    /// <param name="cancellationToken">Cancellation token for plugin shutdown.</param>
    /// <returns>A task that completes when the queue has changed.</returns>
    internal Task WaitForChangeAsync(CancellationToken cancellationToken) => _changed.WaitAsync(cancellationToken);

    private void MoveToDeadLetterLocked(
        string pendingPath,
        DurableEventOutboxEntry entry,
        string reason,
        DateTimeOffset now)
    {
        var deadLetter = new DurableEventDeadLetter
        {
            Entry = entry,
            Reason = reason,
            DeadLetteredAt = now,
        };
        WriteAtomicallyLocked(DeadLetterPathFor(pendingPath), deadLetter);
        File.Delete(pendingPath);
        _logger.LogError(
            "Watchoffit event {EnvelopeId} sequence {Sequence} moved to dead-letter: {Reason}",
            entry.Envelope.Header.Id,
            entry.Envelope.Header.Sequence,
            reason);
    }

    private DurableEventOutboxItem[] ReadPendingItemsLocked()
    {
        var items = new List<DurableEventOutboxItem>();
        foreach (var path in PendingPathsLocked())
        {
            var entry = TryReadEntryLocked(path);
            if (entry is null)
            {
                MoveCorruptEntryToDeadLetterLocked(path);
                continue;
            }

            items.Add(new DurableEventOutboxItem(path, entry));
        }

        return items
            .OrderBy(static item => item.Entry.Envelope.Header.Sequence)
            .ThenBy(static item => item.Entry.Envelope.Header.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private List<string> PendingPathsLocked()
    {
        if (!Directory.Exists(_pendingDirectory))
        {
            return [];
        }

        // The pending directory holds both durable event entries
        // (one JSON per envelope) and the on-disk sequence
        // watermark. The watermark is bookkeeping, not a pending
        // payload, and must not be counted by `QueueDepth`,
        // scanned by `ReadPendingItemsLocked`, or moved to the
        // dead-letter directory when a future reader decides it
        // looks corrupt. Filter it out by file name.
        return Directory.GetFiles(_pendingDirectory, "*.json")
            .Where(path => !string.Equals(Path.GetFileName(path), SequenceWatermarkFileName, StringComparison.Ordinal))
            .Where(path => !string.Equals(Path.GetDirectoryName(path), _deadLetterDirectory, StringComparison.Ordinal))
            .ToList();
    }

    private DurableEventOutboxEntry? TryReadEntryLocked(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var entry = JsonSerializer.Deserialize<DurableEventOutboxEntry>(json, SerializerOptions);
            if (entry is null || entry.Version != EntryVersion || entry.Envelope is null)
            {
                _logger.LogError("Invalid Watchoffit outbox entry at {Path}", path);
                return null;
            }

            return entry;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read Watchoffit event outbox entry {Path}", path);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Malformed Watchoffit event outbox entry {Path}", path);
            return null;
        }
    }

    private void MoveCorruptEntryToDeadLetterLocked(string path)
    {
        Directory.CreateDirectory(_deadLetterDirectory);
        var target = Path.Combine(_deadLetterDirectory, $"corrupt-{Path.GetFileName(path)}");
        if (!File.Exists(target))
        {
            File.Move(path, target);
        }
        else
        {
            TryDelete(path);
        }

        _logger.LogError("Malformed Watchoffit event outbox entry moved to {Path}", target);
    }

    private void WriteAtomicallyLocked<T>(string targetPath, T value)
    {
        var directory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("outbox target has no parent directory");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}{TempFileSuffix}");
        var json = JsonSerializer.Serialize(value, SerializerOptions);
        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using (var stream = new FileStream(tempPath, options))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(targetPath))
            {
                File.Replace(tempPath, targetPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, targetPath);
            }
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private string PendingPathFor(V1EventEnvelope envelope)
    {
        var idHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Header.Id))).ToLowerInvariant();
        return Path.Combine(_pendingDirectory, $"{envelope.Header.Sequence:D20}-{idHash}.json");
    }

    private string DeadLetterPathFor(string pendingPath) =>
        Path.Combine(_deadLetterDirectory, Path.GetFileName(pendingPath));

    private string SequenceWatermarkPath => Path.Combine(_pendingDirectory, SequenceWatermarkFileName);

    private void ObserveSequenceLocked(long sequence)
    {
        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "event sequence cannot be negative");
        }

        if (sequence > ReadSequenceWatermarkLocked())
        {
            WriteSequenceWatermarkLocked(sequence);
        }
    }

    private long ReadSequenceWatermarkLocked()
    {
        if (!File.Exists(SequenceWatermarkPath))
        {
            return 0;
        }

        try
        {
            var state = JsonSerializer.Deserialize<DurableEventOutboxSequenceWatermark>(
                File.ReadAllText(SequenceWatermarkPath),
                SerializerOptions);
            if (state is not null && state.Version == EntryVersion && state.Sequence >= 0)
            {
                return state.Sequence;
            }

            _logger.LogError("Invalid Watchoffit event sequence watermark at {Path}", SequenceWatermarkPath);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read Watchoffit event sequence watermark {Path}", SequenceWatermarkPath);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Malformed Watchoffit event sequence watermark {Path}", SequenceWatermarkPath);
        }

        return 0;
    }

    private void WriteSequenceWatermarkLocked(long sequence)
    {
        WriteAtomicallyLocked(
            SequenceWatermarkPath,
            new DurableEventOutboxSequenceWatermark { Version = EntryVersion, Sequence = sequence });
    }

    private static bool SameEnvelope(V1EventEnvelope left, V1EventEnvelope right) =>
        left.Header.Id == right.Header.Id && left.Header.Sequence == right.Header.Sequence;

    private void SignalChanged()
    {
        try
        {
            _changed.Release();
        }
        catch (SemaphoreFullException)
        {
            // The semaphore only acts as a wake-up hint. A saturated signal
            // means a worker already has work to observe.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Preserve the original failure path. The next startup can recover
            // a leftover temp file without losing any durable queue entry.
        }
    }
}

/// <summary>Persisted pending event and retry metadata.</summary>
public sealed record DurableEventOutboxEntry
{
    /// <summary>On-disk entry schema version.</summary>
    public int Version { get; init; }

    /// <summary>Full v1 event envelope that will be retried unchanged.</summary>
    public required V1EventEnvelope Envelope { get; init; }

    /// <summary>Retryable failure timestamps in the current rolling window.</summary>
    public IReadOnlyList<DateTimeOffset> FailureTimestamps { get; init; } = [];

    /// <summary>Earliest retry time after exponential backoff.</summary>
    public DateTimeOffset? NextAttemptAt { get; init; }

    /// <summary>Last failure reason for diagnostics; never contains credentials.</summary>
    public string? LastFailure { get; init; }
}

/// <summary>Durable record retained after a terminal or exhausted failure.</summary>
public sealed record DurableEventDeadLetter
{
    /// <summary>Pending entry at the point it stopped retrying.</summary>
    public required DurableEventOutboxEntry Entry { get; init; }

    /// <summary>Failure reason visible to the operator through plugin logs.</summary>
    public required string Reason { get; init; }

    /// <summary>UTC instant at which the entry entered dead-letter storage.</summary>
    public DateTimeOffset DeadLetteredAt { get; init; }
}

/// <summary>Persistent high-water mark for emitted v1 sequence values.</summary>
public sealed record DurableEventOutboxSequenceWatermark
{
    /// <summary>On-disk watermark schema version.</summary>
    public int Version { get; init; }

    /// <summary>Highest event sequence observed by the durable outbox.</summary>
    public long Sequence { get; init; }
}

/// <summary>Result of a durable enqueue attempt.</summary>
public enum EventOutboxEnqueueResult
{
    /// <summary>The event was written to the pending queue.</summary>
    Accepted,

    /// <summary>An event with the same id is already pending.</summary>
    AlreadyQueued,

    /// <summary>The bounded pending queue is full; no existing event was overwritten.</summary>
    Full,
}

/// <summary>Pending file and its deserialized contents.</summary>
internal sealed record DurableEventOutboxItem(string Path, DurableEventOutboxEntry Entry);

/// <summary>Outcome of updating retry metadata.</summary>
internal sealed record EventOutboxRetryResult(bool DeadLettered, int FailureCount);
