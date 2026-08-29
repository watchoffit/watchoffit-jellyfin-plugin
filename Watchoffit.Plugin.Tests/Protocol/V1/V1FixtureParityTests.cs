using Xunit;

namespace Jellyfin.Plugin.Watchoffit.Tests.Protocol.V1;

/// <summary>
/// Cross-repo fixture parity test. The C# fixtures under
/// <c>fixtures/v1</c> are copies of the canonical TypeScript fixtures
/// under <c>packages/core/test/fixtures/watchoffit-plugin-protocol/v1</c>
/// and must stay byte-for-byte identical so a wire format change on the
/// TS side trips a C# test before it reaches a paired Jellyfin install.
/// </summary>
public class V1FixtureParityTests
{
    [Fact]
    public void CsharpFixtures_MatchTypeScriptFixtures_ByteForByte()
    {
        var csDir = Path.Combine(AppContext.BaseDirectory, "fixtures/v1");
        var tsDir = LocateTypeScriptFixtures();

        Assert.True(Directory.Exists(csDir), $"C# fixture dir missing: {csDir}");
        if (tsDir is null)
        {
            return;
        }

        var csFiles = Directory.GetFiles(csDir, "*.json")
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        var tsFiles = Directory.GetFiles(tsDir, "*.json")
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(tsFiles, csFiles);

        foreach (var name in csFiles)
        {
            var cs = File.ReadAllText(Path.Combine(csDir, name!));
            var ts = File.ReadAllText(Path.Combine(tsDir, name!));
            Assert.True(cs == ts, $"Fixture {name} differs between TS and C# copies");
        }
    }

    private static string? LocateTypeScriptFixtures()
    {
        var configured = Environment.GetEnvironmentVariable("WATCHOFFIT_CORE_FIXTURES_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var siblingCheckout = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../../watchoffit/packages/core/test/fixtures/watchoffit-plugin-protocol/v1"));

        return Directory.Exists(siblingCheckout) ? siblingCheckout : null;
    }
}
