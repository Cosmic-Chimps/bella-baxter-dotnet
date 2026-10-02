namespace BellaBaxter.Client;

/// <summary>
/// A secrets response could not be decrypted, so it is refused rather than returned (#1050).
/// </summary>
/// <remarks>
/// <para>Before this, <see cref="E2EEncryptionHandler"/> and <see cref="ZkeDekHandler"/> caught a failed decryption,
/// wrote a line to stderr and returned the response untouched — the caller then read the still-encrypted envelope, or
/// <c>bellabaxter:v1:</c> ciphertext, as if it were its secrets. Constitution Principle I forbids a catch that swallows
/// a verification failure and continues.</para>
/// <para>The message names the request path and what failed, never key material or ciphertext. The cause is the
/// <see cref="Exception.InnerException"/>.</para>
/// </remarks>
public sealed class E2EDecryptionException(string message, Exception? inner = null) : Exception(message, inner)
{
    internal static E2EDecryptionException For(HttpRequestMessage request, string what, Exception? inner = null) =>
        new($"The secrets response for {request.RequestUri?.AbsolutePath} could not be decrypted ({what}); " +
            "refusing to return it.", inner);
}
