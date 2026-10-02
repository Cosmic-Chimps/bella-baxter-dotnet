using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BellaBaxter.Spiffe.AspNetCore.Tests;

/// <summary>
/// Backlog §2.31 / issue #710 — the relying party fetches its TRUST ANCHOR from <c>BellaBaseUrl</c>, so
/// that address is https, or plain http to this machine only. The rule itself is tested once, beside
/// its author (<c>BellaApiAddressTests</c> in the client's tests); these prove this package applies it.
/// </summary>
public class TrustBundleAddressTests
{
    [Fact]
    public void Registering_with_plain_http_off_this_machine_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddBellaSpiffe(o =>
            {
                o.BellaBaseUrl = "http://bella.internal.example";
                o.EnvironmentId = Guid.NewGuid();
            }));

        Assert.Contains("BellaBaseUrl must use https", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://api.bella-baxter.io")]
    [InlineData("http://localhost:5522")]
    [InlineData("http://127.0.0.1:5522")]
    public void Registering_with_https_or_loopback_is_accepted(string address)
    {
        new ServiceCollection().AddBellaSpiffe(o =>
        {
            o.BellaBaseUrl = address;
            o.EnvironmentId = Guid.NewGuid();
        });
    }

    [Fact]
    public void Options_registered_WITHOUT_the_builder_are_still_refused()
    {
        // SpiffeOptions is public with init-only members, so a caller can register it directly and never
        // pass through SpiffeOptionsBuilder.Build. The cache is what fetches the bundle, so it re-checks.
        var options = new SpiffeOptions
        {
            BellaBaseUrl = "http://10.0.0.5:5522",
            EnvironmentId = Guid.NewGuid(),
        };

        Assert.Throws<InvalidOperationException>(() => new SpiffeTrustBundleCache(
            options,
            new ServiceCollection().AddHttpClient().BuildServiceProvider().GetRequiredService<IHttpClientFactory>(),
            NullLogger<SpiffeTrustBundleCache>.Instance));
    }
}
