using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Watchoffit.Pairing;

/// <summary>
/// Outcome of <see cref="WatchoffitConnectionStore.TryLoad"/>.
/// </summary>
public abstract record ConnectionLoadResult
{
    /// <summary>Valid version-1 connection found and parsed.</summary>
    public sealed record Loaded(WatchoffitConnection Connection) : ConnectionLoadResult;

    /// <summary>No file exists; the plugin is in the <see cref="PairingState.None"/> state.</summary>
    public sealed record NotPresent : ConnectionLoadResult;

    /// <summary>The file exists but the JSON could not be parsed. The operator should delete it and re-pair.</summary>
    public sealed record Corrupt(string Reason) : ConnectionLoadResult;

    /// <summary>The file declares a <c>version</c> the plugin does not know. No migration path is available yet.</summary>
    public sealed record UnsupportedVersion(int Version) : ConnectionLoadResult;
}

/// <summary>
/// Persists <see cref="WatchoffitConnection"/> under the plugin's
/// <c>dataFolder</c> (see <c>meta.json</c> in the plugin scaffold and
/// <c>docs/pairing-design.md</c> §5.1).
/// </summary>
/// <remarks>
/// All writes go through a uniquely-named temp file plus
/// <see cref="File.Replace(string, string, string?)"/> so a crashed
/// plugin never leaves a half-written <c>connection.json</c> on disk.
/// The credential is the only field that requires
/// <see cref="ICredentialProtector"/> handling; the rest of the record
/// is plain JSON.
///
/// The store is the only owner of the on-disk schema. Tests target
/// this class directly to keep the schema in lockstep with the design
/// doc — see <c>Watchoffit.Plugin.Tests/Pairing/WatchoffitConnectionStoreTests.cs</c>.
/// </remarks>
public sealed class WatchoffitConnectionStore
{
    /// <summary>Current on-disk schema version. Matches <see cref="WatchoffitConnection.Version"/>.</summary>
    public const int CurrentVersion = 2;

    private const string ConnectionFileName = "connection.json";
    private const string TempFilePrefix = ".connection.json.";
    private const string TempFileSuffix = ".tmp";
    private const string BackupFileSuffix = ".bak";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _pluginDataPath;
    private readonly ICredentialProtector _credentialProtector;
    private readonly ILogger _logger;
    private readonly object _ioLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchoffitConnectionStore"/> class.
    /// </summary>
    /// <param name="pluginDataPath">
    /// Plugin data folder, normally <c>BasePlugin&lt;T&gt;.DataFolderPath</c>
    /// (e.g. <c>/config/plugins/Watchoffit/</c> in a standard container
    /// layout — matches the <c>dataFolder</c> field in <c>meta.json</c>).
    /// </param>
    /// <param name="credentialProtector">Credential at-rest protection. Use <see cref="CredentialProtectorFactory.CreateForCurrentPlatform"/> for the default per-platform pick.</param>
    /// <param name="logger">Plugin logger. The store logs load/save outcomes without ever touching the credential value.</param>
    public WatchoffitConnectionStore(string pluginDataPath, ICredentialProtector credentialProtector, ILogger logger)
    {
        _pluginDataPath = string.IsNullOrWhiteSpace(pluginDataPath)
            ? throw new ArgumentException("pluginDataPath must be a non-empty path", nameof(pluginDataPath))
            : pluginDataPath;
        _credentialProtector = credentialProtector ?? throw new ArgumentNullException(nameof(credentialProtector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Absolute path of the connection file under the plugin's data folder.</summary>
    public string ConnectionFilePath => Path.Combine(_pluginDataPath, ConnectionFileName);

    /// <summary>
    /// Read the on-disk <c>connection.json</c> if present.
    /// </summary>
    /// <returns>
    /// One of <see cref="ConnectionLoadResult.Loaded"/>,
    /// <see cref="ConnectionLoadResult.NotPresent"/>,
    /// <see cref="ConnectionLoadResult.Corrupt"/>, or
    /// <see cref="ConnectionLoadResult.UnsupportedVersion"/>. The caller
    /// maps the result to a <see cref="PairingState"/> transition; the
    /// store does not mutate state on its own.
    /// </returns>
    public ConnectionLoadResult TryLoad()
    {
        lock (_ioLock)
        {
            return TryLoadLocked();
        }
    }

    private ConnectionLoadResult TryLoadLocked()
    {
        if (!File.Exists(ConnectionFilePath))
        {
            return new ConnectionLoadResult.NotPresent();
        }

        string raw;
        try
        {
            using var reader = new StreamReader(
                new FileStream(ConnectionFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            raw = reader.ReadToEnd();
        }
        catch (FileNotFoundException)
        {
            // A concurrent Forget() removed the file between Exists and
            // open. Treat as NotPresent rather than Corrupt.
            return new ConnectionLoadResult.NotPresent();
        }
        catch (DirectoryNotFoundException)
        {
            return new ConnectionLoadResult.NotPresent();
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to read {Path}", ConnectionFilePath);
            return new ConnectionLoadResult.Corrupt($"read failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied reading {Path}", ConnectionFilePath);
            return new ConnectionLoadResult.Corrupt($"access denied: {ex.Message}");
        }

        // Per design §5.3: "WatchoffitConnectionStore reads version first.
        // Unknown versions are refused without partially loading
        // credentials." We do this via JsonDocument so a future-version
        // file that renamed or removed a field does not get reported as
        // Corrupt before we can see its version literal.
        int? declaredVersion = null;
        try
        {
            using var versionProbe = JsonDocument.Parse(raw);
            if (versionProbe.RootElement.ValueKind == JsonValueKind.Object
                && versionProbe.RootElement.TryGetProperty("version", out var versionEl)
                && versionEl.ValueKind == JsonValueKind.Number
                && versionEl.TryGetInt32(out var version))
            {
                declaredVersion = version;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Malformed JSON in {Path}", ConnectionFilePath);
            return new ConnectionLoadResult.Corrupt($"malformed JSON: {ex.Message}");
        }

        if (declaredVersion is { } v && v != CurrentVersion)
        {
            return new ConnectionLoadResult.UnsupportedVersion(v);
        }

        WatchoffitConnection? connection;
        try
        {
            connection = JsonSerializer.Deserialize<WatchoffitConnection>(raw, SerializerOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Malformed JSON in {Path}", ConnectionFilePath);
            return new ConnectionLoadResult.Corrupt($"malformed JSON: {ex.Message}");
        }

        if (connection is null)
        {
            return new ConnectionLoadResult.Corrupt("deserializer returned null");
        }

        if (connection.Version != CurrentVersion)
        {
            // The version field is required and we just re-read it from
            // the same document. If it differs from CurrentVersion, the
            // file was hand-edited between the probe and the parse, or
            // the deserializer stripped the literal. Either way refuse.
            return new ConnectionLoadResult.UnsupportedVersion(connection.Version);
        }

        // Unprotect in-memory; the on-disk value stays wrapped.
        var unprotected = connection with
        {
            Credential = connection.Credential with
            {
                Value = Unprotect(connection.Credential),
            },
        };

        return new ConnectionLoadResult.Loaded(unprotected);
    }

    /// <summary>
    /// Atomically write the connection to disk. The method writes to a
    /// uniquely-named temp file in the same directory, fsyncs, and only
    /// then renames over the existing file. A crash mid-write leaves
    /// the previous file untouched. Concurrent calls are serialized
    /// through an internal lock so two threads cannot race on the same
    /// temp file.
    /// </summary>
    /// <param name="connection">Connection to persist. The store wraps <see cref="WatchoffitConnection.Credential"/> through <see cref="ICredentialProtector"/> before writing.</param>
    public void Save(WatchoffitConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        lock (_ioLock)
        {
            SaveLocked(connection);
        }
    }

    private void SaveLocked(WatchoffitConnection connection)
    {
        Directory.CreateDirectory(_pluginDataPath);

        var protectedConnection = connection with
        {
            Credential = connection.Credential with
            {
                Value = _credentialProtector.Protect(connection.Credential.Value),
            },
            Version = CurrentVersion,
        };

        var json = JsonSerializer.Serialize(protectedConnection, SerializerOptions);

        // Per-thread unique temp path so concurrent saves (e.g. one from
        // the dashboard page handler and one from a reconcile tick) do
        // not race on the same temp file. The GUID makes the name
        // collision-free; the prefix keeps the file discoverable in an
        // operator's `ls` of the data folder.
        var tempPath = Path.Combine(
            _pluginDataPath,
            TempFilePrefix + Guid.NewGuid().ToString("N") + TempFileSuffix);

        // Create the temp file with restrictive permissions from the
        // start on Unix. Doing the chmod after a default-umask write
        // would leave a window in which another local user could read
        // the plaintext credential.
        FileStreamOptions streamOptions = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using (var stream = new FileStream(tempPath, streamOptions))
            {
                using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(ConnectionFilePath))
            {
                var backupPath = ConnectionFilePath + BackupFileSuffix;
                TryDelete(backupPath);
                File.Replace(tempPath, ConnectionFilePath, backupPath);
            }
            else
            {
                File.Move(tempPath, ConnectionFilePath);
            }
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Remove the connection file. Used by the disconnect path and by
    /// the revoke path to drop the local state. Idempotent — returns
    /// <c>true</c> if no file existed.
    /// </summary>
    /// <returns><c>true</c> if a file was removed; <c>false</c> if no file existed.</returns>
    public bool Forget()
    {
        lock (_ioLock)
        {
            if (!File.Exists(ConnectionFilePath))
            {
                return false;
            }

            File.Delete(ConnectionFilePath);
            return true;
        }
    }

    private string Unprotect(WatchoffitCredential credential)
    {
        if (credential.Scheme != _credentialProtector.Scheme)
        {
            throw new InvalidOperationException(
                $"Credential scheme '{credential.Scheme}' does not match active protector '{_credentialProtector.Scheme}'. " +
                "The connection.json was written by a different plugin build; pair again to migrate.");
        }

        return _credentialProtector.Unprotect(credential.Value);
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
            // Best-effort; the caller is about to throw the original failure.
        }
    }
}
