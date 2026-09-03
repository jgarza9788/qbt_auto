using System.Text;
using QbitFlow.Core.Abstractions;

namespace QbitFlow.Infrastructure.Secrets;

/// <summary>
/// The <c>SECRETS_ENCRYPTION=none</c> protector: base64 with a random nonce, no encryption.
/// Documented trade-off — secrets are effectively plaintext at rest in the SQLite file.
/// Phase 5 adds a Data Protection-backed implementation.
/// </summary>
public sealed class Base64SecretProtector : ISecretProtector
{
    public (byte[] Ciphertext, byte[] Nonce) Protect(string plaintext)
    {
        var nonce = new byte[12];
        System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);
        return (Encoding.UTF8.GetBytes(plaintext), nonce);
    }

    public string Unprotect(byte[] ciphertext, byte[] nonce) => Encoding.UTF8.GetString(ciphertext);
}
