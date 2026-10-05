using System;
using System.Net.Http;

namespace BellaBaxter.Client;

/// <summary>
/// Decides when a client presents its device key, and is the single author of that decision for both
/// <see cref="ZkeDekHandler"/> and <see cref="E2EEncryptionHandler"/>.
/// </summary>
/// <remarks>
/// <para><b>Issue #635 — presenting the key and decrypting the answer are two different questions, and
/// conflating them broke every read.</b> Both handlers used one `/secrets` path test for both. That is
/// right for decryption: only secrets responses come back encrypted. It is wrong for the header,
/// because the server's device gate governs more than `/secrets` — and in particular it governs
/// <c>GET /api/v1/tenants/me/zke</c>, the status call the CLI makes BEFORE a read. That call went out
/// with no key, the server correctly answered <c>presentedKeyRegistered: false</c>, and the CLI refused
/// itself before reaching any secret. A registered, active device could not read anything.</para>
///
/// <para><b>Why every API path rather than a list of the governed ones.</b> A list is a second copy of
/// the server's policy, maintained in a different repository and released on a different cadence; when
/// the server governs one more route, the client that needs updating is the one already in the field.
/// The server ignores the header where it does not apply, so presenting it everywhere costs a few
/// bytes and cannot grant anything — the gate compares it against what is REGISTERED, so a key the
/// caller does not hold is worthless. That asymmetry is what makes the broad rule safe: being wrong in
/// this direction wastes bandwidth, and being wrong in the other direction locks the customer out.</para>
///
/// <para><b>A public key is not a secret.</b> It rides a header on every secrets request already and is
/// readable on any machine holding the pair. Sending it on more requests to the same origin discloses
/// nothing new.</para>
/// </remarks>
internal static class ZkePresentedKey
{
    /// <summary>The request header carrying the caller's public key. One spelling, one place.</summary>
    internal const string HeaderName = "X-E2E-Public-Key";

    /// <summary>
    /// True when this request should carry the device key: any Bella API call. Scoped to
    /// <c>/api/</c> so a client pointed at an unrelated host by a redirect does not present it.
    /// </summary>
    internal static bool ShouldPresent(HttpRequestMessage request) =>
        request.RequestUri?.AbsolutePath.Contains("/api/", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// True when the RESPONSE may be an encrypted secrets payload worth decrypting. Deliberately still
    /// the narrow `/secrets` test — widening this would have the handlers trying to decrypt ordinary
    /// JSON, which is the opposite mistake and just as broken.
    /// </summary>
    internal static bool CarriesEncryptedPayload(HttpRequestMessage request) =>
        request.RequestUri?.AbsolutePath.Contains("/secrets", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// True when the server ALWAYS encrypts a 2xx answer to a presented key, so a plain one must be refused
    /// (#1050 b). The value-carrying reads of apps/sdk/SDK_CONTRACT.md, "Which Endpoints Support E2EE" — and
    /// deliberately not every <c>/secrets</c> path: <c>…/secrets/version</c>, <c>/manifest</c>, <c>/hash</c>,
    /// <c>/{key}/metadata</c>, <c>/{key}/versions</c> and every write answer in plain JSON even with the key, and
    /// refusing those would break them. The same matcher, segment for segment, is in every SDK.
    /// </summary>
    internal static bool RequiresEnvelope(HttpRequestMessage request)
    {
        if (request.Method != HttpMethod.Get) return false;
        var path = request.RequestUri?.AbsolutePath;
        if (path is null) return false;

        const string anchor = "/api/v1/projects/";
        var at = path.IndexOf(anchor, StringComparison.Ordinal);
        if (at < 0) return false;

        // [0] is the project; a trailing slash routes to the same endpoint, so it must not dodge the rule.
        var s = path[(at + anchor.Length)..].TrimEnd('/').Split('/');
        return s switch
        {
            [_, "secrets"] => true,                                                         // listGlobalSecrets
            [_, "environments", _, "secrets"] => true,                                      // getAllEnvironmentSecrets
            [_, "environments", _, "secrets", "export"] => true,                            // exportEnvironmentSecrets
            [_, "environments", _, "providers", _, "secrets"] => true,                      // listSecrets
            [_, "environments", _, "providers", _, "secrets", var key] => key != "hash",    // getSecret, exportSecrets
            [_, "environments", _, "providers", _, "secrets", _, "versions", var n] =>      // getSecretVersion
                n.Length > 0 && n.All(char.IsAsciiDigit),
            _ => false,
        };
    }
}
