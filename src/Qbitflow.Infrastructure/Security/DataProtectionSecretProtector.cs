using Microsoft.AspNetCore.DataProtection;

namespace Qbitflow.Infrastructure.Security;

public class DataProtectionSecretProtector : ISecretProtector
{
    private const string Purpose = "Qbitflow.InstanceCredentials.v1";
    private readonly IDataProtector _protector;

    public DataProtectionSecretProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedText) => _protector.Unprotect(protectedText);
}
