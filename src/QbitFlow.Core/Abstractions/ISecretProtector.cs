namespace QbitFlow.Core.Abstractions;

/// <summary>Protects secrets at rest. Phase 1 ships a base64 ("none") implementation.</summary>
public interface ISecretProtector
{
    (byte[] Ciphertext, byte[] Nonce) Protect(string plaintext);
    string Unprotect(byte[] ciphertext, byte[] nonce);
}
