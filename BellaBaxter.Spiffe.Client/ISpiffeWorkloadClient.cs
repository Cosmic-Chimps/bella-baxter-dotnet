namespace BellaBaxter.Spiffe.Client;

/// <summary>
/// Retrieves a JWT-SVID from the local bella agent sidecar.
/// </summary>
public interface ISpiffeWorkloadClient
{
    /// <summary>
    /// Fetches a JWT-SVID for the given audience from the bella agent.
    /// </summary>
    /// <param name="audience">The audience claim the SVID should be issued for (default: "bella-api").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The raw JWT-SVID string.</returns>
    Task<string> FetchJwtSvidAsync(string audience = "bella-api", CancellationToken cancellationToken = default);
}
