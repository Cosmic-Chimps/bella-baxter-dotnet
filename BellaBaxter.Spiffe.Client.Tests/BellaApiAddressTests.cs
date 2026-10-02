using System.Text.RegularExpressions;
using BellaBaxter.Client;
using BellaBaxter.Spiffe.Client;
using Xunit;

namespace BellaBaxter.Spiffe.Client.Tests;

/// <summary>
/// Backlog §2.31 / issue #710 — the Bella base address is https, or plain http to THIS machine only.
/// </summary>
/// <remarks>
/// The loopback cases are judged on the PARSED host. Each "refused" case below is an address a string
/// prefix or substring check would have accepted, which is why they are here.
/// </remarks>
public class BellaApiAddressTests
{
    [Theory]
    [InlineData("https://api.bella-baxter.io")]
    [InlineData("https://192.168.1.20")]            // the compose bundle, by LAN IP, behind its internal CA
    [InlineData("HTTPS://API.EXAMPLE.TEST/")]
    [InlineData("http://localhost:5522")]            // the dev API under Aspire
    [InlineData("http://LOCALHOST:5522")]
    [InlineData("http://127.0.0.1:5522")]
    [InlineData("http://127.10.20.30")]              // all of 127.0.0.0/8 is loopback
    [InlineData("http://[::1]:5522")]
    [InlineData("http://[::ffff:127.0.0.1]:5522")]   // the same address, IPv4-mapped
    public void Https_and_loopback_http_are_accepted(string address)
    {
        Assert.Equal(BellaApiAddressVerdict.Acceptable, BellaApiAddress.Judge(address, out _));
        Assert.Null(BellaApiAddress.Problem(address, "BellaBaseUrl"));
    }

    [Theory]
    [InlineData("http://api.bella-baxter.io")]
    [InlineData("http://192.168.1.20")]              // a LAN address is somebody's network, not this machine
    [InlineData("http://10.0.0.5:5522")]
    [InlineData("http://host.docker.internal:5522")] // the kind lab's host bridge: off-machine from a pod
    [InlineData("http://localhost.evil.test")]       // prefix "localhost"
    [InlineData("http://127.0.0.1.nip.io")]          // prefix "127.0.0.1"
    [InlineData("http://localhost@evil.test")]       // "localhost" is the USERINFO; the host is evil.test
    [InlineData("http://api.localhost")]             // RFC 6761 is not honoured by every resolver
    [InlineData("http://0.0.0.0:5522")]              // "any address" is not loopback
    [InlineData("http://[::ffff:10.0.0.5]")]
    public void Plain_http_off_this_machine_is_refused(string address)
    {
        Assert.Equal(BellaApiAddressVerdict.PlainHttpOffLoopback, BellaApiAddress.Judge(address, out _));
        var problem = BellaApiAddress.Problem(address, "BellaBaseUrl");
        Assert.NotNull(problem);
        Assert.Contains("BellaBaseUrl must use https", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("api.bella-baxter.io")]
    [InlineData("/api")] // on Unix this parses as file:///api — refused either way, for a different reason
    public void Absent_or_relative_addresses_are_refused(string? address)
    {
        Assert.NotEqual(BellaApiAddressVerdict.Acceptable, BellaApiAddress.Judge(address, out _));
        Assert.NotNull(BellaApiAddress.Problem(address, "BellaBaseUrl"));
    }

    [Theory]
    [InlineData("ftp://api.bella-baxter.io")]
    [InlineData("file:///etc/passwd")]
    [InlineData("localhost:5522")]                   // parses with scheme "localhost"
    public void Other_schemes_are_refused(string address)
    {
        Assert.Equal(BellaApiAddressVerdict.UnsupportedScheme, BellaApiAddress.Judge(address, out _));
    }

    [Fact]
    public void Creating_a_client_refuses_plain_http_off_this_machine()
    {
        var ex = Assert.Throws<ArgumentException>(() => BellaSpiffeClientFactory.Create(
            new SpiffeClientOptions { BellaBaseUrl = "http://bella.internal.example", EnvironmentId = Guid.NewGuid() },
            new NeverCalledWorkloadClient()));

        Assert.Contains("BellaBaseUrl must use https", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://api.bella-baxter.io")]
    [InlineData("http://localhost:5522")]
    public void Creating_a_client_accepts_https_and_loopback(string address)
    {
        // Creation performs no network call, so this proves only that the rule admits the address.
        var client = BellaSpiffeClientFactory.Create(
            new SpiffeClientOptions { BellaBaseUrl = address, EnvironmentId = Guid.NewGuid() },
            new NeverCalledWorkloadClient());

        Assert.NotNull(client);
    }

    /// <summary>
    /// The loopback-aware rule has ONE author. A second copy — in the CLI, or in either SPIFFE package —
    /// is how the agent and the SDK come to disagree about whether an address may carry a credential.
    /// </summary>
    /// <remarks>
    /// Scans every C# file under the .NET SDK tree and, when this checkout has it, the CLI tree, for the
    /// loopback primitives the rule is built from. The allow-list is EMPTY: the author file is the only
    /// place they may appear. Paths are compared RELATIVE to each root, so a checkout under a directory
    /// named bin/obj cannot exclude everything and pass having read nothing (the spec-061 lesson).
    /// </remarks>
    [Fact]
    public void The_loopback_rule_has_one_author()
    {
        var sdkRoot = FindDirectoryUpwards(Path.Combine("apps", "sdk", "dotnet"))
            ?? FindDirectoryContaining("BellaBaxter.Spiffe.Client");
        Assert.NotNull(sdkRoot);

        var author = Path.Combine(sdkRoot!, "BellaBaxter.Client", "src", "BellaApiAddress.cs");
        Assert.True(File.Exists(author), author);

        var roots = new List<string> { sdkRoot! };
        if (FindDirectoryUpwards(Path.Combine("apps", "cli-dotnet")) is { } cliRoot)
            roots.Add(cliRoot);

        var primitive = new Regex(@"\bIsLoopback\b|IPAddress\.Loopback|IPv6Loopback|""localhost""",
            RegexOptions.CultureInvariant);

        var offenders = new List<string>();
        var read = 0;
        foreach (var root in roots)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Split('/').Any(segment => segment is "bin" or "obj" or "generated"))
                    continue;
                if (Path.GetFullPath(file) == Path.GetFullPath(author))
                    continue;
                if (relative.Contains(".Tests/", StringComparison.Ordinal))
                    continue; // tests NAME the cases; the rule is what must not be restated

                read++;
                var source = File.ReadAllText(file);
                if (primitive.IsMatch(source))
                    offenders.Add($"{Path.GetFileName(root)}/{relative}");
            }
        }

        Assert.True(read > 20, $"the scan read only {read} files — it is not looking where it should");
        Assert.True(offenders.Count == 0,
            "Loopback logic outside BellaApiAddress.cs — call BellaApiAddress.Judge/Problem/IsLoopbackHost "
            + "instead of restating the rule:\n  " + string.Join("\n  ", offenders));
    }

    private static string? FindDirectoryUpwards(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        return null;
    }

    // The public bella-baxter-dotnet repository IS apps/sdk/dotnet, so there the root is the directory
    // that contains the SPIFFE client project.
    private static string? FindDirectoryContaining(string child)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, child)))
            dir = dir.Parent;
        return dir?.FullName;
    }

    private sealed class NeverCalledWorkloadClient : ISpiffeWorkloadClient
    {
        public Task<string> FetchJwtSvidAsync(string audience = "bella-api", CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("creation must not fetch an SVID");
    }
}
