namespace BellaBaxter.Client;

/// <summary>
/// A secrets response that should have been end-to-end encrypted to this client's key was not, or did not decrypt,
/// so it is refused rather than returned (#1050). <see cref="Code"/> says which.
/// </summary>
/// <remarks>
/// <para><b>(a) <see cref="DecryptionFailed"/>.</b> <see cref="E2EEncryptionHandler"/> and <see cref="ZkeDekHandler"/>
/// used to catch a failed decryption, write a line to stderr and return the response untouched — the caller then read
/// the still-encrypted envelope, or <c>bellabaxter:v1:</c> ciphertext, as if it were its secrets. Constitution
/// Principle I forbids a catch that swallows a verification failure and continues.</para>
/// <para><b>(b) <see cref="PlaintextResponse"/>.</b> Once the client has presented <c>X-E2E-Public-Key</c> on a read the
/// server encrypts (<see cref="ZkePresentedKey.RequiresEnvelope"/>), a plain answer is what a header-stripping
/// intermediary or a server regression would serve. Accepting it would hand the caller secrets it believes were
/// end-to-end encrypted. There is no fallback and no opt-out; a client that does not want to require the envelope must
/// not present the key. The rule and the codes are cross-SDK (apps/sdk/SDK_CONTRACT.md, "a presented key requires an
/// envelope"): every SDK raises its own <c>E2EEResponseError</c>/<c>E2EEResponseException</c> with the same codes.</para>
/// <para>The message names the request path and what failed, never key material, ciphertext or the body. The cause is
/// the <see cref="Exception.InnerException"/>.</para>
/// </remarks>
public sealed class E2EEResponseException : Exception
{
    /// <summary>The key was presented on an envelope-required read and the 2xx answer was not an envelope.</summary>
    public const string PlaintextResponse = "e2ee-plaintext-response";

    /// <summary>An envelope (or an at-rest value) was malformed, tampered with, or encrypted to another key.</summary>
    public const string DecryptionFailed = "e2ee-decryption-failed";

    /// <summary><see cref="PlaintextResponse"/> or <see cref="DecryptionFailed"/>; stable across SDKs.</summary>
    public string Code { get; }

    private E2EEResponseException(string code, string message, Exception? inner = null) : base(message, inner) =>
        Code = code;

    internal static E2EEResponseException Plaintext(HttpRequestMessage request) =>
        new(PlaintextResponse,
            $"E2EE response expected but plaintext received for {request.RequestUri?.AbsolutePath}; " +
            $"refusing it ({PlaintextResponse})");

    internal static E2EEResponseException Undecryptable(HttpRequestMessage request, string what, Exception? inner = null) =>
        new(DecryptionFailed,
            $"E2EE response could not be decrypted for {request.RequestUri?.AbsolutePath} ({what}); " +
            $"refusing it ({DecryptionFailed})", inner);
}
