namespace BellaBaxter.Spiffe.AspNetCore;

internal static class SpiffeConstants
{
    /// <summary>Named HttpClient used by <see cref="SpiffeTrustBundleCache"/> to fetch the trust bundle.</summary>
    internal const string HttpClientName = "BellaSpiffeTrustBundle";
}
