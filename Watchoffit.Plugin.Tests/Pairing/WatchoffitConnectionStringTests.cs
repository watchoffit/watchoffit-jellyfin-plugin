using Jellyfin.Plugin.Watchoffit.Pairing;

using Xunit;

namespace Jellyfin.Plugin.Watchoffit.Tests.Pairing;

/// <summary>Unit tests for the self-contained one-field pairing bundle.</summary>
public sealed class WatchoffitConnectionStringTests
{
    private const string CrossLanguageFixture =
        "watchoffit-jellyfin:v1:eyJiYXNlVXJsIjoiaHR0cHM6Ly93YXRjaG9mZml0LmV4YW1wbGUuY29tL2Jhc2UiLCJzZXJ2ZXJDb25uZWN0aW9uSWQiOiJzY25fMDFIWiIsInBhaXJpbmdDb2RlIjoiQUIxMkNEIn0";

    [Fact]
    public void Encode_ThenTryParse_RoundTripsNormalizedValues()
    {
        var encoded = new WatchoffitConnectionString(
            "https://watchoffit.example.com/base/",
            "scn_01HZ",
            "AB12CD").Encode();

        Assert.Equal(CrossLanguageFixture, encoded);

        var result = WatchoffitConnectionString.TryParse(encoded, out var parsed, out var error);

        Assert.True(result, error);
        Assert.Equal("https://watchoffit.example.com/base", parsed?.BaseUrl);
        Assert.Equal("scn_01HZ", parsed?.ServerConnectionId);
        Assert.Equal("AB12CD", parsed?.PairingCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://watchoffit.example.com")]
    [InlineData("watchoffit-jellyfin:v1:not-valid-base64!")]
    public void TryParse_RejectsInvalidFormat(string value)
    {
        Assert.False(WatchoffitConnectionString.TryParse(value, out _, out _));
    }

    [Fact]
    public void Encode_RejectsUrlWithCredentialsOrFragment()
    {
        var invalid = new WatchoffitConnectionString(
            "https://user:password@watchoffit.example.com/#secret",
            "scn_01HZ",
            "AB12CD");

        Assert.Throws<InvalidOperationException>(() => invalid.Encode());
    }
}
