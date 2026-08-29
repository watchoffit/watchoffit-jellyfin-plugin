using Jellyfin.Plugin.Watchoffit.Protocol.V1;

using Xunit;

namespace Jellyfin.Plugin.Watchoffit.Tests.Protocol.V1;

/// <summary>Tests for generated envelope metadata.</summary>
public sealed class V1EnvelopeBuilderTests
{
    /// <summary>Generated ids always have a fixed length and the URL-safe Base64 alphabet.</summary>
    [Fact]
    public void NewId_AlwaysUsesFixedLengthBase64UrlSuffix()
    {
        var builder = new V1EnvelopeBuilder();

        for (var i = 0; i < 10_000; i++)
        {
            var id = builder.NewId("evt");

            Assert.Equal(16, id.Length);
            Assert.StartsWith("evt_", id, StringComparison.Ordinal);
            Assert.DoesNotContain('+', id);
            Assert.DoesNotContain('/', id);
            Assert.DoesNotContain('=', id);
        }
    }
}
