namespace BellaBaxter.Spiffe.Client;

// Spec 001 T044 (US6, FR-028) — deciding how a workload authenticates, without being told.
//
// THE PROMISE is that the same build of an application works in three places with no code change: in a
// pod beside a SPIFFE agent (attested identity, no stored secret), on a developer laptop (an API key
// in the environment), and in CI (likewise). The application asks for a client; this decides how.
//
// TWO RULES MAKE THAT SAFE RATHER THAN MERELY CONVENIENT:
//
//  1. THE AGENT WINS WHEN PRESENT. If an agent socket is there, the SPIFFE path is used even when an
//     API key also happens to be set. Preferring the key would mean a workload that was deliberately
//     given an attested identity silently falls back to a long-lived shared secret — the exact thing
//     this feature exists to remove — and nothing in any log would say so.
//
//  2. NEITHER AVAILABLE IS A REFUSAL, NOT A DEFAULT. No socket and no key means the caller gets an
//     exception naming both options. Returning an unauthenticated client would defer the failure to
//     the first API call, where it arrives as a 401 that looks like a credential problem rather than a
//     configuration one.
//
// It does NOT probe the socket by connecting. Detection answers "how should this process
// authenticate", which is a startup-configuration question; whether the agent is healthy right now is
// a different question with a different answer over time, and the token fetch reports it properly when
// it happens. Refusing to start because an agent was momentarily unready would make a pod's start order
// load-bearing.

/// <summary>How a workload will authenticate.</summary>
public enum SpiffeCredentialMode
{
    /// <summary>A SPIFFE agent socket is present; fetch a JWT-SVID and exchange it.</summary>
    WorkloadApi,

    /// <summary>No agent, but an API key is in the environment.</summary>
    ApiKey,

    /// <summary>Neither. The caller must be told rather than handed something that fails later.</summary>
    None,
}

/// <summary>What detection found, and where.</summary>
/// <param name="Mode">The credential mode to use.</param>
/// <param name="SocketPath">The agent socket, when one was found.</param>
/// <param name="SocketFromEnvironment">
/// True when the path came from <c>SPIFFE_ENDPOINT_SOCKET</c> rather than the default location. Worth
/// reporting: an operator who set that variable and still sees the API-key path has a typo.
/// </param>
public sealed record SpiffeCredentialDetection(
    SpiffeCredentialMode Mode,
    string? SocketPath = null,
    bool SocketFromEnvironment = false)
{
    /// <summary>An explanation for the caller, whichever way it went.</summary>
    public string Description => Mode switch
    {
        SpiffeCredentialMode.WorkloadApi =>
            $"Using SPIFFE workload identity via the agent at {SocketPath}"
            + (SocketFromEnvironment ? $" (from {SpiffeAgentDetection.EndpointSocketVariable})." : " (default path)."),
        SpiffeCredentialMode.ApiKey =>
            $"No SPIFFE agent socket found; using the API key from {SpiffeAgentDetection.ApiKeyVariable}.",
        _ => "No credentials available.",
    };
}

/// <summary>Detects how this process should authenticate to Bella.</summary>
public static class SpiffeAgentDetection
{
    /// <summary>The SPIFFE spec's own portability variable, and what standard clients read.</summary>
    public const string EndpointSocketVariable = "SPIFFE_ENDPOINT_SOCKET";

    /// <summary>Where an API key is read from when there is no agent.</summary>
    public const string ApiKeyVariable = "BELLA_API_KEY";

    /// <summary>Directory name used under the runtime root for the default socket path.</summary>
    public const string DefaultSocketDirectory = "bella-spiffe";

    /// <summary>Default socket file name.</summary>
    public const string DefaultSocketFile = "workload.sock";

    /// <summary>Decides how to authenticate.</summary>
    /// <param name="getEnvironmentVariable">Injected for testing; defaults to the real environment.</param>
    /// <param name="fileExists">Injected for testing; defaults to the real filesystem.</param>
    /// <param name="runtimeRoot">Injected for testing; defaults to XDG_RUNTIME_DIR else the home directory.</param>
    public static SpiffeCredentialDetection Detect(
        Func<string, string?>? getEnvironmentVariable = null,
        Func<string, bool>? fileExists = null,
        string? runtimeRoot = null)
    {
        var env = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var exists = fileExists ?? File.Exists;

        var configured = env(EndpointSocketVariable);
        var fromEnvironment = !string.IsNullOrWhiteSpace(configured);

        var path = fromEnvironment
            ? Normalize(configured!.Trim())
            : DefaultSocketPath(runtimeRoot ?? DefaultRuntimeRoot(env));

        if (exists(path))
        {
            return new SpiffeCredentialDetection(SpiffeCredentialMode.WorkloadApi, path, fromEnvironment);
        }

        // The agent takes precedence when present, so this is only reached with no socket — see rule 1
        // in the file header for why the ORDER matters rather than being a preference.
        return string.IsNullOrWhiteSpace(env(ApiKeyVariable))
            ? new SpiffeCredentialDetection(SpiffeCredentialMode.None, path, fromEnvironment)
            : new SpiffeCredentialDetection(SpiffeCredentialMode.ApiKey, path, fromEnvironment);
    }

    /// <summary>The message a caller sees when nothing is available.</summary>
    /// <remarks>
    /// Names BOTH options with the exact spellings, because the commonest cause is a variable set in
    /// the wrong place and the second commonest is not knowing the agent existed.
    /// </remarks>
    public static string NoCredentialsMessage(SpiffeCredentialDetection detection) =>
        "No way to authenticate to Bella was found. Either run the SPIFFE agent "
        + $"('bella spiffe agent') so a socket exists at {detection.SocketPath} — set "
        + $"{EndpointSocketVariable} to use a different path — or set {ApiKeyVariable} to a "
        + "bax- API key.";

    /// <summary>
    /// Strips a <c>unix:</c> prefix, which the SPIFFE spec allows in the endpoint variable.
    /// </summary>
    /// <remarks>
    /// Standard SPIFFE tooling accepts <c>unix:///path</c> as well as a bare path. Not handling it
    /// would make a correctly-configured pod fall through to the API-key branch, and the resulting 401
    /// would send the operator to check credentials rather than a URI scheme.
    /// </remarks>
    internal static string Normalize(string value)
    {
        const string scheme = "unix://";
        if (value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            var rest = value[scheme.Length..];
            // `unix:///run/x.sock` leaves a leading slash; `unix://run/x.sock` does not.
            return rest.StartsWith('/') ? rest : "/" + rest;
        }

        return value.StartsWith("unix:", StringComparison.OrdinalIgnoreCase)
            ? value["unix:".Length..]
            : value;
    }

    private static string DefaultRuntimeRoot(Func<string, string?> env)
    {
        var xdg = env("XDG_RUNTIME_DIR");
        return string.IsNullOrWhiteSpace(xdg)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : xdg;
    }

    private static string DefaultSocketPath(string runtimeRoot) =>
        Path.Combine(runtimeRoot, DefaultSocketDirectory, DefaultSocketFile);
}
