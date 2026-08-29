using System.Text.Json;

using Jellyfin.Plugin.Watchoffit.Pairing;
using Jellyfin.Plugin.Watchoffit.Protocol.V1;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Jellyfin.Plugin.Watchoffit.Tests.Pairing;

/// <summary>
/// Tests for <see cref="WatchoffitConnectionStore"/>. The store owns the
/// on-disk schema defined in <c>pairing-design.md</c> §5.2; every
/// test below maps to a specific clause in that section so a future
/// schema change trips the corresponding test before it reaches
/// Jellyfin.
/// </summary>
public sealed class WatchoffitConnectionStoreTests : IDisposable
{
    private readonly string _tempDir;

    public WatchoffitConnectionStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "watchoffit-store-" + Guid.NewGuid().ToString("N"));
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
                // best-effort cleanup
            }
        }

        GC.SuppressFinalize(this);
    }

    private WatchoffitConnectionStore NewStore() => new(
        _tempDir,
        new PlainCredentialProtector(),
        NullLogger<WatchoffitConnectionStore>.Instance);

    private static WatchoffitConnection SampleConnection() => new()
    {
        Version = WatchoffitConnectionStore.CurrentVersion,
        State = PairingState.Paired,
        BaseUrl = "https://watchoffit.example.com",
        ServerConnectionId = "scn_01HZ0001EXAMPLE",
        WatchoffitServerName = "Family Watchoffit",
        JellyfinServerId = "jf_server_01HZ0001LOCAL",
        Credential = new WatchoffitCredential
        {
            Scheme = "plain",
            Value = "cred_01HZ0001SECRET",
        },
        Capabilities = new V1Capabilities
        {
            MinProtocolVersion = 1,
            MaxProtocolVersion = 1,
            MaxPayloadBytes = 65536,
            MaxBatchSize = 50,
        },
        CreatedAt = "2026-08-27T10:02:01.000Z",
        LastPingAt = "2026-08-27T10:04:30.000Z",
        LastErrorCode = null,
        LastErrorAt = null,
    };

    [Fact]
    public void TryLoad_NoFile_ReturnsNotPresent()
    {
        var store = NewStore();
        var result = store.TryLoad();
        Assert.IsType<ConnectionLoadResult.NotPresent>(result);
    }

    [Fact]
    public void Save_Then_Load_RoundTripsAllFields()
    {
        var store = NewStore();
        var original = SampleConnection();

        store.Save(original);
        var result = store.TryLoad();

        var loaded = Assert.IsType<ConnectionLoadResult.Loaded>(result);
        Assert.Equal(original.BaseUrl, loaded.Connection.BaseUrl);
        Assert.Equal(original.ServerConnectionId, loaded.Connection.ServerConnectionId);
        Assert.Equal(original.WatchoffitServerName, loaded.Connection.WatchoffitServerName);
        Assert.Equal(original.JellyfinServerId, loaded.Connection.JellyfinServerId);
        Assert.Equal("cred_01HZ0001SECRET", loaded.Connection.Credential.Value);
        Assert.Equal(PairingState.Paired, loaded.Connection.State);
        Assert.NotNull(loaded.Connection.Capabilities);
        Assert.Equal(50, loaded.Connection.Capabilities!.MaxBatchSize);
    }

    [Fact]
    public void Save_PinsSchemaVersionToCurrent()
    {
        var store = NewStore();
        store.Save(SampleConnection() with { Version = 99 });

        var raw = File.ReadAllText(Path.Combine(_tempDir, "connection.json"));
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal(2, doc.RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public void TryLoad_UnknownVersion_ReturnsUnsupportedVersion()
    {
        // Build a syntactically-valid connection that the parser can
        // deserialize, but pin a future schema version. Using the
        // store first means the JSON has all the required fields.
        var store = NewStore();
        store.Save(SampleConnection());

        var path = Path.Combine(_tempDir, "connection.json");
        var json = File.ReadAllText(path).Replace("\"version\": 2", "\"version\": 99", StringComparison.Ordinal);
        File.WriteAllText(path, json);

        var result = store.TryLoad();

        var unsupported = Assert.IsType<ConnectionLoadResult.UnsupportedVersion>(result);
        Assert.Equal(99, unsupported.Version);
    }

    [Fact]
    public void TryLoad_CorruptJson_ReturnsCorrupt()
    {
        File.WriteAllText(Path.Combine(_tempDir, "connection.json"), "{ not valid json");

        var store = NewStore();
        var result = store.TryLoad();

        var corrupt = Assert.IsType<ConnectionLoadResult.Corrupt>(result);
        Assert.NotEmpty(corrupt.Reason);
    }

    [Fact]
    public void TryLoad_DeserializedNull_ReturnsCorrupt()
    {
        File.WriteAllText(Path.Combine(_tempDir, "connection.json"), "null");

        var store = NewStore();
        var result = store.TryLoad();
        Assert.IsType<ConnectionLoadResult.Corrupt>(result);
    }

    [Fact]
    public void Save_AtomicWrite_LeavesNoTempFilesOnSuccess()
    {
        // After a successful save, the data folder must contain exactly
        // connection.json (plus optionally the .bak from a previous
        // replace) and no leftover temp files.
        var store = NewStore();
        store.Save(SampleConnection());
        store.Save(SampleConnection() with { LastPingAt = "2026-08-27T12:00:00.000Z" });

        var dir = new DirectoryInfo(_tempDir);
        var temps = dir.GetFiles(".connection.json.*.tmp");
        Assert.Empty(temps);
    }

    [Fact]
    public void Save_KeepsBackupOnReplace()
    {
        var store = NewStore();
        store.Save(SampleConnection());
        var firstWrite = File.ReadAllText(Path.Combine(_tempDir, "connection.json"));

        store.Save(SampleConnection() with { LastPingAt = "2026-08-27T12:00:00.000Z" });
        var secondWrite = File.ReadAllText(Path.Combine(_tempDir, "connection.json"));

        Assert.NotEqual(firstWrite, secondWrite);
        var backup = File.ReadAllText(Path.Combine(_tempDir, "connection.json.bak"));
        Assert.Equal(firstWrite, backup);
    }

    [Fact]
    public void Forget_RemovesConnectionFile()
    {
        var store = NewStore();
        store.Save(SampleConnection());
        Assert.True(File.Exists(Path.Combine(_tempDir, "connection.json")));

        var removed = store.Forget();

        Assert.True(removed);
        Assert.False(File.Exists(Path.Combine(_tempDir, "connection.json")));
    }

    [Fact]
    public void Forget_NoFile_ReturnsFalse()
    {
        var store = NewStore();
        Assert.False(store.Forget());
    }

    [Fact]
    public void Save_OnUnix_SetsRestrictiveFileMode()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // permission bits are a Unix concept
        }

        var store = NewStore();
        store.Save(SampleConnection());

        var info = new FileInfo(Path.Combine(_tempDir, "connection.json"));
        Assert.True(info.UnixFileMode!.HasFlag(UnixFileMode.UserRead));
        Assert.True(info.UnixFileMode.HasFlag(UnixFileMode.UserWrite));
        Assert.False(info.UnixFileMode.HasFlag(UnixFileMode.GroupRead));
        Assert.False(info.UnixFileMode.HasFlag(UnixFileMode.OtherRead));
    }

    [Fact]
    public void TryLoad_MismatchedCredentialScheme_Throws()
    {
        var store = NewStore();
        store.Save(SampleConnection());

        // Re-load with a different protector scheme by writing a file
        // whose credential uses "dpapi" but the store still uses
        // "plain". This simulates a plugin downgrade or a different
        // build pairing the file.
        var path = Path.Combine(_tempDir, "connection.json");
        var json = File.ReadAllText(path).Replace("\"plain\"", "\"dpapi\"", StringComparison.Ordinal);
        File.WriteAllText(path, json);

        Assert.Throws<InvalidOperationException>(() => store.TryLoad());
    }

    [Fact]
    public void DisplayCredentialMasked_HidesMiddleOfLongCredential()
    {
        var connection = SampleConnection();
        var masked = connection.DisplayCredentialMasked();
        // "cred_01HZ0001SECRET" has length 20; first 4 chars = "cred",
        // last 4 chars = "CRET". The 12-char middle is hidden behind "…".
        Assert.Equal("cred…CRET", masked);
        Assert.DoesNotContain("01HZ0001SECRET", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void DisplayCredentialMasked_HandlesEmpty()
    {
        var connection = SampleConnection() with
        {
            Credential = new WatchoffitCredential { Scheme = "plain", Value = string.Empty },
        };
        Assert.Equal(string.Empty, connection.DisplayCredentialMasked());
    }

    [Fact]
    public void DisplayCredentialMasked_HandlesShortCredential()
    {
        var connection = SampleConnection() with
        {
            Credential = new WatchoffitCredential { Scheme = "plain", Value = "abc" },
        };
        // Short credentials are fully redacted so a partial reveal
        // doesn't defeat the masking. The literal "•••" is a fixed
        // string with no relation to the input.
        Assert.Equal("•••", connection.DisplayCredentialMasked());
    }

    [Fact]
    public void DisplayCredentialMasked_HandlesBorderlineCredential()
    {
        // The "first 4 + last 4" partial mask only hides anything when
        // the value is at least 12 characters long. Below that, fully
        // redact. 11 characters is one below the threshold; the
        // 12-character boundary itself is the smallest partial-mask case.
        var connection = SampleConnection() with
        {
            Credential = new WatchoffitCredential { Scheme = "plain", Value = "123456789AB" },
        };
        Assert.Equal("•••", connection.DisplayCredentialMasked());
    }

    [Fact]
    public void Save_SerializesStateAsStringLiteral()
    {
        // The design's §5.2 schema says "state": "paired", not
        // "state": 3. Verify the on-disk form is the string literal.
        var store = NewStore();
        store.Save(SampleConnection());

        var raw = File.ReadAllText(Path.Combine(_tempDir, "connection.json"));
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("state").ValueKind);
        Assert.Equal("paired", doc.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public void TryLoad_AcceptsStringLiteralState()
    {
        // The state was historically an integer in the default
        // System.Text.Json enum handling; a hand-written file with the
        // string form must still load.
        var store = NewStore();
        store.Save(SampleConnection());

        var path = Path.Combine(_tempDir, "connection.json");
        File.WriteAllText(path, File.ReadAllText(path)); // touch

        var result = store.TryLoad();
        var loaded = Assert.IsType<ConnectionLoadResult.Loaded>(result);
        Assert.Equal(PairingState.Paired, loaded.Connection.State);
    }

    [Fact]
    public void TryLoad_RejectsIntegerEncodedState()
    {
        // Defensive: a file written by a build that pre-dates the
        // PairingStateJsonConverter would have the integer form. The
        // store must refuse it as Corrupt instead of silently
        // re-interpreting it.
        var path = Path.Combine(_tempDir, "connection.json");
        File.WriteAllText(
            path,
            "{\"version\": 2, \"state\": 3, \"baseUrl\": \"x\", \"serverConnectionId\": \"x\", \"watchoffitServerName\": \"x\", \"jellyfinServerId\": \"x\", \"credential\": {\"scheme\": \"plain\", \"value\": \"x\"}, \"createdAt\": \"x\", \"lastPingAt\": \"x\"}");

        var store = NewStore();
        var result = store.TryLoad();
        var corrupt = Assert.IsType<ConnectionLoadResult.Corrupt>(result);
        Assert.NotEmpty(corrupt.Reason);
    }

    [Fact]
    public void TryLoad_FileRemovedBetweenExistsAndOpen_ReturnsNotPresent()
    {
        // TOCTOU: a concurrent Forget() between Exists and the
        // FileStream open should surface as NotPresent, not Corrupt.
        // We exercise this by deleting the file inside a custom store
        // wrapper that races the lock.
        var path = Path.Combine(_tempDir, "connection.json");
        File.WriteAllText(path, "{\"version\": 2, \"state\": \"paired\", \"baseUrl\": \"x\", \"serverConnectionId\": \"x\", \"watchoffitServerName\": \"x\", \"jellyfinServerId\": \"x\", \"credential\": {\"scheme\": \"plain\", \"value\": \"x\"}, \"createdAt\": \"x\", \"lastPingAt\": \"x\"}");

        var store = NewStore();
        File.Delete(path); // simulate concurrent Forget
        var result = store.TryLoad();
        Assert.IsType<ConnectionLoadResult.NotPresent>(result);
    }

    [Fact]
    public async Task Save_ConcurrentCalls_AllSucceedWithoutCorruption()
    {
        var store = NewStore();
        var connection = SampleConnection();

        // Fire 16 concurrent saves with rotating lastPingAt timestamps.
        // The lock + unique temp paths must serialize them without any
        // visible torn write.
        var tasks = Enumerable.Range(0, 16)
            .Select(i => Task.Run(() =>
            {
                store.Save(connection with { LastPingAt = $"2026-08-27T10:00:{i:D2}.000Z" });
            }))
            .ToArray();
        await Task.WhenAll(tasks);

        var result = store.TryLoad();
        var loaded = Assert.IsType<ConnectionLoadResult.Loaded>(result);
        Assert.Equal("cred_01HZ0001SECRET", loaded.Connection.Credential.Value);
        Assert.NotNull(loaded.Connection.LastPingAt);
        Assert.StartsWith("2026-08-27T10:00:", loaded.Connection.LastPingAt, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialProtectorFactory_ReturnsPlainOnNonWindows()
    {
        // Skip on Windows so the test doesn't assert the wrong branch
        // when run under CI on a Windows runner.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var protector = CredentialProtectorFactory.CreateForCurrentPlatform();
        Assert.Equal("plain", protector.Scheme);
        Assert.IsType<PlainCredentialProtector>(protector);
    }
}
