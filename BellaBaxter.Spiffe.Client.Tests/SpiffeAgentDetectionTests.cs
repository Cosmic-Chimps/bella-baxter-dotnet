using BellaBaxter.Spiffe.Client;
using Xunit;

namespace BellaBaxter.Spiffe.Client.Tests;

/// <summary>
/// Spec 001 T044 (US6, FR-028) — how a workload decides what credential to use.
/// </summary>
/// <remarks>
/// <para>The promise is that one build of an application works in a pod beside a SPIFFE agent, on a
/// developer laptop, and in CI, with no code change. The risk is that the convenience quietly picks the
/// weaker credential, so the two ordering rules below are the whole test.</para>
/// </remarks>
public class SpiffeAgentDetectionTests
{
    private const string Socket = "/run/bella-spiffe/workload.sock";

    private static Func<string, string?> Env(Dictionary<string, string?> values) =>
        key => values.TryGetValue(key, out var v) ? v : null;

    [Fact]
    public void An_agent_socket_is_used_when_present()
    {
        var result = SpiffeAgentDetection.Detect(
            Env(new() { [SpiffeAgentDetection.EndpointSocketVariable] = Socket }),
            fileExists: p => p == Socket);

        Assert.Equal(SpiffeCredentialMode.WorkloadApi, result.Mode);
        Assert.Equal(Socket, result.SocketPath);
        Assert.True(result.SocketFromEnvironment);
    }

    [Fact]
    public void The_AGENT_WINS_over_an_API_key_that_is_also_set()
    {
        // The rule that makes auto-detection safe rather than merely convenient. Preferring the key
        // would let a workload that was deliberately given an attested identity fall back silently to a
        // long-lived shared secret — the exact thing this whole feature exists to remove — and nothing
        // in any log would say it happened.
        var result = SpiffeAgentDetection.Detect(
            Env(new()
            {
                [SpiffeAgentDetection.EndpointSocketVariable] = Socket,
                [SpiffeAgentDetection.ApiKeyVariable] = "bax-key",
            }),
            fileExists: p => p == Socket);

        Assert.Equal(SpiffeCredentialMode.WorkloadApi, result.Mode);
    }

    [Fact]
    public void An_API_key_is_used_when_there_is_NO_agent()
    {
        var result = SpiffeAgentDetection.Detect(
            Env(new() { [SpiffeAgentDetection.ApiKeyVariable] = "bax-key" }),
            fileExists: _ => false,
            runtimeRoot: "/run/user/1000");

        Assert.Equal(SpiffeCredentialMode.ApiKey, result.Mode);
    }

    [Fact]
    public void NEITHER_available_is_a_refusal_not_an_unauthenticated_client()
    {
        // Returning something unauthenticated would defer the failure to the first API call, where it
        // arrives as a 401 that reads like a credential problem rather than a configuration one.
        var result = SpiffeAgentDetection.Detect(
            Env([]), fileExists: _ => false, runtimeRoot: "/run/user/1000");

        Assert.Equal(SpiffeCredentialMode.None, result.Mode);

        // And the message names BOTH options with their exact spellings: the commonest cause is a
        // variable set in the wrong place, the second commonest is not knowing the agent exists.
        var message = SpiffeAgentDetection.NoCredentialsMessage(result);
        Assert.Contains("bella spiffe agent", message, StringComparison.Ordinal);
        Assert.Contains(SpiffeAgentDetection.ApiKeyVariable, message, StringComparison.Ordinal);
        Assert.Contains(SpiffeAgentDetection.EndpointSocketVariable, message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_EMPTY_api_key_variable_counts_as_absent()
    {
        // An exported-but-empty variable is common in shell scripts and CI. Treating it as present
        // would produce a client that signs every request with an empty secret and fails at the server.
        foreach (var value in new[] { "", "   " })
        {
            var result = SpiffeAgentDetection.Detect(
                Env(new() { [SpiffeAgentDetection.ApiKeyVariable] = value }),
                fileExists: _ => false,
                runtimeRoot: "/run/user/1000");

            Assert.Equal(SpiffeCredentialMode.None, result.Mode);
        }
    }

    [Fact]
    public void The_default_socket_path_is_used_when_the_variable_is_unset()
    {
        var expected = Path.Combine(
            "/run/user/1000",
            SpiffeAgentDetection.DefaultSocketDirectory,
            SpiffeAgentDetection.DefaultSocketFile);

        var result = SpiffeAgentDetection.Detect(
            Env([]), fileExists: p => p == expected, runtimeRoot: "/run/user/1000");

        Assert.Equal(SpiffeCredentialMode.WorkloadApi, result.Mode);
        Assert.Equal(expected, result.SocketPath);
        Assert.False(result.SocketFromEnvironment);
    }

    [Fact]
    public void XDG_RUNTIME_DIR_is_honoured_for_the_default_path()
    {
        var expected = Path.Combine(
            "/run/user/501",
            SpiffeAgentDetection.DefaultSocketDirectory,
            SpiffeAgentDetection.DefaultSocketFile);

        var result = SpiffeAgentDetection.Detect(
            Env(new() { ["XDG_RUNTIME_DIR"] = "/run/user/501" }),
            fileExists: p => p == expected);

        Assert.Equal(SpiffeCredentialMode.WorkloadApi, result.Mode);
    }

    [Theory]
    [InlineData("unix:///run/bella/w.sock", "/run/bella/w.sock")]
    [InlineData("unix://run/bella/w.sock", "/run/bella/w.sock")]
    [InlineData("unix:/run/bella/w.sock", "/run/bella/w.sock")]
    [InlineData("/run/bella/w.sock", "/run/bella/w.sock")]
    public void A_unix_SCHEME_in_the_endpoint_variable_is_accepted(string configured, string expected)
    {
        // Standard SPIFFE tooling accepts `unix:///path` as well as a bare path. Not handling it would
        // make a correctly-configured pod fall through to the API-key branch, and the resulting 401
        // would send the operator to check credentials rather than a URI scheme.
        var result = SpiffeAgentDetection.Detect(
            Env(new() { [SpiffeAgentDetection.EndpointSocketVariable] = configured }),
            fileExists: p => p == expected);

        Assert.Equal(SpiffeCredentialMode.WorkloadApi, result.Mode);
        Assert.Equal(expected, result.SocketPath);
    }

    [Fact]
    public void A_configured_socket_that_does_NOT_exist_reports_the_configured_path()
    {
        // The path is reported even when nothing is there, because "no agent found" without the path is
        // the least useful possible message: the agent is usually running somewhere else.
        var result = SpiffeAgentDetection.Detect(
            Env(new() { [SpiffeAgentDetection.EndpointSocketVariable] = "/run/typo/w.sock" }),
            fileExists: _ => false);

        Assert.Equal(SpiffeCredentialMode.None, result.Mode);
        Assert.Equal("/run/typo/w.sock", result.SocketPath);
        Assert.Contains("/run/typo/w.sock", SpiffeAgentDetection.NoCredentialsMessage(result),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_description_says_which_credential_was_chosen_and_why()
    {
        // Logged at Information by the factory: which credential a workload ended up using is exactly
        // the fact an incident timeline needs, and it is invisible from outside the process.
        var agent = SpiffeAgentDetection.Detect(
            Env(new() { [SpiffeAgentDetection.EndpointSocketVariable] = Socket }),
            fileExists: p => p == Socket);
        Assert.Contains(Socket, agent.Description, StringComparison.Ordinal);
        Assert.Contains(SpiffeAgentDetection.EndpointSocketVariable, agent.Description, StringComparison.Ordinal);

        var key = SpiffeAgentDetection.Detect(
            Env(new() { [SpiffeAgentDetection.ApiKeyVariable] = "bax-key" }),
            fileExists: _ => false, runtimeRoot: "/run/user/1000");
        Assert.Contains(SpiffeAgentDetection.ApiKeyVariable, key.Description, StringComparison.Ordinal);
    }
}
