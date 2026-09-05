namespace Qbitflow.Infrastructure.Security;

/// <summary>Encrypts/decrypts instance credentials before they touch the database.</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedText);
}
