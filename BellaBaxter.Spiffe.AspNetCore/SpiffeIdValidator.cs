using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Formats.Asn1;

namespace BellaBaxter.Spiffe.AspNetCore;

/// <summary>
/// Validates SPIFFE IDs and performs glob-pattern matching.
/// </summary>
public static class SpiffeIdValidator
{
    // spiffe://{tenant}/{project}/{env}/{workload}
    //
    // Anchored with \z, NOT $. In .NET, `$` also matches immediately BEFORE a final newline, so
    // `…/billing-service\n` would parse as a valid SPIFFE ID and be carried into the claims verbatim.
    // The SAN comes from a CA-issued certificate so nobody can currently choose it, but an identity
    // parser is the wrong place to leave a trailing-whitespace equivalence lying around.
    private static readonly Regex SpiffeUriPattern =
        new(@"^spiffe://(?<tenant>[^/]+)/(?<project>[^/]+)/(?<env>[^/]+)/(?<workload>[^/]+)\z",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses a SPIFFE URI into its component parts.
    /// Returns null if the URI is not a valid SPIFFE ID.
    /// </summary>
    public static SpiffeIdParts? Parse(string spiffeUri)
    {
        var m = SpiffeUriPattern.Match(spiffeUri);
        if (!m.Success) return null;
        return new SpiffeIdParts(
            FullUri: spiffeUri,
            Tenant:   m.Groups["tenant"].Value,
            Project:  m.Groups["project"].Value,
            Env:      m.Groups["env"].Value,
            Workload: m.Groups["workload"].Value);
    }

    /// <summary>
    /// Tests whether <paramref name="spiffeId"/> matches the glob <paramref name="pattern"/>.
    /// <list type="bullet">
    ///   <item><c>*</c> matches a single path segment (no slashes)</item>
    ///   <item><c>**</c> matches any number of path segments (including slashes)</item>
    /// </list>
    /// </summary>
    public static bool MatchesGlobPattern(string pattern, string spiffeId)
    {
        // Convert glob to regex: escape everything, then replace wildcards.
        // Handle ** before * to avoid double-substitution.
        const string doubleStarPlaceholder = "\x00DSTAR\x00";

        // Anchored with \z rather than $: .NET's `$` matches before a final newline too, so a pattern
        // naming one workload would also admit `…/billing-service\n`. On an ALLOW-LIST that is the
        // wrong direction to be lenient in, and it costs one character to close.
        var regexStr = "^" +
            Regex.Escape(pattern)
                 .Replace(@"\*\*", doubleStarPlaceholder)
                 .Replace(@"\*", "[^/]*")
                 .Replace(doubleStarPlaceholder, ".*")
            + @"\z";

        return Regex.IsMatch(spiffeId, regexStr,
            RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    /// <summary>
    /// Extracts all URI entries from the Subject Alternative Name (SAN) extension of a certificate.
    /// Parses the ASN.1 structure directly for reliable, cross-platform behaviour.
    /// </summary>
    public static IReadOnlyList<string> GetSanUris(X509Certificate2 cert)
    {
        const string sanOid = "2.5.29.17";
        var sanExt = cert.Extensions[sanOid];
        if (sanExt == null) return Array.Empty<string>();

        var uris = new List<string>();
        try
        {
            // SAN ::= SEQUENCE OF GeneralName
            // uniformResourceIdentifier [6] IMPLICIT IA5String
            var reader = new AsnReader(sanExt.RawData, AsnEncodingRules.DER);
            var seq = reader.ReadSequence();
            while (seq.HasData)
            {
                var tag = seq.PeekTag();
                if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 6)
                {
                    // Read the raw TLV and manually extract the string value.
                    var encoded = seq.ReadEncodedValue();
                    var span = encoded.Span;
                    // tag is 1 byte (0x86), then DER length, then content
                    int offset = 1; // skip tag byte
                    int contentLen = DecodeDerLength(span, ref offset);
                    uris.Add(Encoding.ASCII.GetString(span.Slice(offset, contentLen)));
                }
                else
                {
                    seq.ReadEncodedValue(); // skip
                }
            }
        }
        catch
        {
            // Ignore malformed SAN extensions.
        }
        return uris;
    }

    private static int DecodeDerLength(ReadOnlySpan<byte> data, ref int offset)
    {
        var first = data[offset++];
        if ((first & 0x80) == 0) return first; // short form
        int numBytes = first & 0x7F;
        int length = 0;
        for (int i = 0; i < numBytes; i++)
            length = (length << 8) | data[offset++];
        return length;
    }
}

/// <summary>Component parts of a parsed SPIFFE URI.</summary>
public sealed record SpiffeIdParts(
    string FullUri,
    string Tenant,
    string Project,
    string Env,
    string Workload);
