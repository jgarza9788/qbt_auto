using System.Text;
using Microsoft.AspNetCore.DataProtection;
using QbitFlow.Core.Abstractions;

namespace QbitFlow.Infrastructure.Secrets;

/// <summary>
/// <c>SECRETS_ENCRYPTION=dpapi</c>: encrypts with ASP.NET Core Data Protection. The key ring is
/// persisted to <c>SECRETS_KEY_DIR</c> (default <c>./data/keys</c>) on the same volume as the DB.
/// </summary>
public sealed class DataProtectionSecretProtector : ISecretProtector
{
    private readonly IDataProtector _protector;

    public DataProtectionSecretProtector(string keyDir)
    {
        Directory.CreateDirectory(keyDir);
        var provider = DataProtectionProvider.Create(new DirectoryInfo(keyDir));
        _protector = provider.CreateProtector("qbit-flow.source-secrets.v1");
    }

    public (byte[] Ciphertext, byte[] Nonce) Protect(string plaintext)
    {
        var cipher = _protector.Protect(Encoding.UTF8.GetBytes(plaintext));
        return (cipher, []);   // DP embeds its own IV; Nonce is unused
    }

    public string Unprotect(byte[] ciphertext, byte[] nonce) =>
        Encoding.UTF8.GetString(_protector.Unprotect(ciphertext));
}

public static class SecretProtectorFactory
{
    public static ISecretProtector Create()
    {
        var mode = Environment.GetEnvironmentVariable("SECRETS_ENCRYPTION")?.Trim().ToLowerInvariant();
        if (mode == "dpapi")
        {
            var dir = Environment.GetEnvironmentVariable("SECRETS_KEY_DIR");
            if (string.IsNullOrWhiteSpace(dir))
                dir = Path.Combine(AppContext.BaseDirectory, "data", "keys");
            return new DataProtectionSecretProtector(dir);
        }
        return new Base64SecretProtector();
    }
}
