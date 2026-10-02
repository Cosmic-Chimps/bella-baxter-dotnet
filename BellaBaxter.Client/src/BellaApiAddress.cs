using System;
using System.Net;

// Backlog §2.31 / issue #710 — the ONE statement, on the CLIENT side, of "may this process send its
// credentials to this Bella address".
//
// WHAT TRAVELS OVER IT. `bella spiffe agent` posts its bootstrap token to it and receives the SVID
// private key from it; the .NET SPIFFE SDK trades a JWT-SVID for a `bax-` lease over it and a relying
// party fetches the trust bundle — its TRUST ANCHOR — from it. Over plain http off the machine, anyone
// on the path reads the token and the key, or swaps the trust bundle and has the relying party accept
// SVIDs they minted themselves.
//
// THE RULE. Absolute; `https` with a host; OR `http` to a LOOPBACK host — `localhost`, 127.0.0.0/8,
// ::1 (an IPv4-mapped ::ffff:127.x counts, being the same address) — judged on the PARSED host, never
// by string prefix: `http://localhost.evil.test`, `http://127.0.0.1.nip.io` and
// `http://localhost@evil.test` all name somebody else's machine and are refused. Anything else,
// including `*.localhost` (RFC 6761 reserves it, but not every resolver honours that), is refused.
//
// WHY LOOPBACK IS ALLOWED HERE, AND WHY THIS IS NOT THE API's `OidcIssuerAddress` RULE. Do not "unify"
// them. Spec 030 refused a loopback exception for the Kubernetes OIDC issuer, for reasons that do not
// transfer:
//   • That address is fetched by the SERVER. "Loopback" there is the API host itself, reachable by any
//     process on it, and the address is a trust anchor for EVERY workload in the environment — the last
//     place to add a convenience. The one flow that wanted it (the spec-028 kind lab) already serves
//     https://localhost:8443.
//   • This address is dialled by the CLIENT. "Loopback" is the caller's own machine: the developer's
//     laptop running the API under Aspire (http://localhost:5522), where the bytes never reach a
//     network another host can observe. Refusing it would break the dev plane and protect nothing.
// Same scheme question, different party dialling, different blast radius — hence two rules.
//
// NO ESCAPE HATCH, by decision (backlog §2.31). Every first-party plane reaches the API over https
// off-loopback: SaaS (https://api.bella-baxter.io), Dokploy (App__ApiBaseUrl = https://{Domains:Api}),
// the compose customer bundle (https://<IP>:443 behind the wizard-minted internal CA — its plain-http
// hops exist only on the compose network), and dev on loopback. A switch here would make the weaker rule
// reachable by setting one variable, which is how "temporary" plain http survives into production.
//
// ONE FILE, TWO ASSEMBLIES. Public in BellaBaxter.Client, which the CLI and BellaBaxter.Spiffe.Client
// both reference. BellaBaxter.Spiffe.AspNetCore targets net8.0/net9.0 and deliberately depends on
// nothing, so it compiles THIS file as a linked source with BELLA_API_ADDRESS_INTERNAL defined, which
// makes the copy internal: a consumer referencing both packages sees one public type, not two. Both
// projects live in apps/sdk/dotnet, the tree the public bella-baxter-dotnet repository mirrors, so the
// link resolves there too.

#if BELLA_API_ADDRESS_INTERNAL
namespace BellaBaxter.Spiffe.AspNetCore;
#else
namespace BellaBaxter.Client;
#endif

/// <summary>What <see cref="BellaApiAddress.Judge"/> decided about a Bella API base address.</summary>
#if BELLA_API_ADDRESS_INTERNAL
internal
#else
public
#endif
enum BellaApiAddressVerdict
{
    /// <summary><c>https</c> with a host, or <c>http</c> to a loopback host.</summary>
    Acceptable,

    /// <summary>Absent, blank, relative or unparseable.</summary>
    NotAbsolute,

    /// <summary>A scheme other than <c>https</c>/<c>http</c>.</summary>
    UnsupportedScheme,

    /// <summary>Plain <c>http</c> to a host that is not this machine.</summary>
    PlainHttpOffLoopback,

    /// <summary>An address that names no host.</summary>
    NoHost,
}

/// <summary>
/// The client-side rule for a Bella API base address: https, or plain http to loopback only. No opt-out.
/// </summary>
#if BELLA_API_ADDRESS_INTERNAL
internal
#else
public
#endif
static class BellaApiAddress
{
    /// <summary>Judges <paramref name="value"/> as given (no trimming).</summary>
    /// <param name="value">The candidate base address.</param>
    /// <param name="parsed">Set whenever the value parsed as an absolute URI, including when refused.</param>
    public static BellaApiAddressVerdict Judge(string? value, out Uri? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return BellaApiAddressVerdict.NotAbsolute;
        }

        parsed = uri;

        var https = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var http = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        if (!https && !http)
        {
            return BellaApiAddressVerdict.UnsupportedScheme;
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            return BellaApiAddressVerdict.NoHost;
        }

        if (https || IsLoopbackHost(uri))
        {
            return BellaApiAddressVerdict.Acceptable;
        }

        return BellaApiAddressVerdict.PlainHttpOffLoopback;
    }

    /// <summary>True when <paramref name="value"/> may carry credentials.</summary>
    public static bool IsAcceptable(string? value) => Judge(value, out _) == BellaApiAddressVerdict.Acceptable;

    /// <summary>
    /// Null when acceptable, otherwise an operator-facing sentence naming <paramref name="setting"/> —
    /// the words the operator typed the value under, so the message points at what to change.
    /// </summary>
    public static string? Problem(string? value, string setting)
    {
        return Judge(value, out var uri) switch
        {
            BellaApiAddressVerdict.Acceptable => null,
            BellaApiAddressVerdict.PlainHttpOffLoopback =>
                $"{setting} must use https (got '{uri}'). Plain http is accepted only for this machine "
                + "(localhost, 127.0.0.0/8, ::1): this connection carries credentials, private keys and "
                + "trust anchors, and anyone on the network path could read or replace them.",
            BellaApiAddressVerdict.UnsupportedScheme =>
                $"{setting} must be an https:// address (got scheme '{uri!.Scheme}').",
            BellaApiAddressVerdict.NoHost =>
                $"{setting} names no host (got '{value}').",
            _ =>
                $"{setting} must be an absolute https:// address, for example https://api.bella-baxter.io "
                + $"(got '{value}').",
        };
    }

    /// <summary>Throws <see cref="ArgumentException"/> with <see cref="Problem"/>'s sentence when refused.</summary>
    public static Uri RequireAcceptable(string? value, string setting)
    {
        if (Problem(value, setting) is { } problem)
        {
            throw new ArgumentException(problem, nameof(value));
        }

        Judge(value, out var uri);
        return uri!;
    }

    /// <summary>
    /// True when the PARSED host is this machine: the name <c>localhost</c>, an address in 127.0.0.0/8,
    /// or <c>::1</c> (IPv4-mapped forms are unwrapped first). Never a prefix or substring match.
    /// </summary>
    public static bool IsLoopbackHost(Uri uri)
    {
        switch (uri.HostNameType)
        {
            case UriHostNameType.Dns:
                return string.Equals(uri.IdnHost, "localhost", StringComparison.OrdinalIgnoreCase);

            case UriHostNameType.IPv4:
            case UriHostNameType.IPv6:
                // DnsSafeHost strips the IPv6 brackets (and any scope id) that Host keeps.
                if (!IPAddress.TryParse(uri.DnsSafeHost, out var address))
                {
                    return false;
                }

                if (address.IsIPv4MappedToIPv6)
                {
                    address = address.MapToIPv4();
                }

                return IPAddress.IsLoopback(address);

            default:
                return false;
        }
    }
}
