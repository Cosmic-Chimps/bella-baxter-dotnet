using System.Security.Cryptography;
using System.Text;

namespace BellaBaxter.Client;

/// <summary>
/// Verifies the HMAC-SHA256 signature on incoming Bella Baxter webhook requests.
/// </summary>
public static class WebhookSignatureVerifier
{
    /// <summary>
    /// Verifies the <c>X-Bella-Signature</c> header on a received webhook request.
    /// </summary>
    /// <param name="secret">The webhook signing secret (whsec-xxx value).</param>
    /// <param name="signatureHeader">Value of the X-Bella-Signature header.</param>
    /// <param name="rawBody">The raw request body bytes.</param>
    /// <param name="toleranceSeconds">Maximum age of the timestamp in seconds. Default 300 (5 min).</param>
    /// <returns><c>true</c> if the signature is valid and within tolerance.</returns>
    public static bool Verify(string secret, string signatureHeader, byte[] rawBody,
        int toleranceSeconds = 300)
    {
        // Parse header: "t={unix},v1={hex}"
        long timestamp = 0;
        string? expectedSig = null;

        foreach (var part in signatureHeader.Split(','))
        {
            var idx = part.IndexOf('=');
            if (idx < 0) continue;
            var key = part[..idx].Trim();
            var value = part[(idx + 1)..].Trim();
            if (key == "t" && long.TryParse(value, out var t))
                timestamp = t;
            else if (key == "v1")
                expectedSig = value;
        }

        if (timestamp == 0 || expectedSig is null)
            return false;

        // Validate timestamp tolerance (replay-attack protection)
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(nowUnix - timestamp) > toleranceSeconds)
            return false;

        // Compute HMAC-SHA256: key=UTF8(secret), data=UTF8("{t}.{rawBody}")
        var signingInput = $"{timestamp}.{Encoding.UTF8.GetString(rawBody)}";
        var computedMAC = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(signingInput));
        var computedSig = Convert.ToHexString(computedMAC).ToLowerInvariant();

        // Timing-safe compare
        var expectedBytes = Encoding.ASCII.GetBytes(expectedSig);
        var computedBytes = Encoding.ASCII.GetBytes(computedSig);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, computedBytes);
    }
}
