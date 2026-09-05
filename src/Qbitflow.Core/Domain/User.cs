namespace Qbitflow.Core.Domain;

/// <summary>An application login. PasswordHash is a PBKDF2 hash produced by PasswordHasher&lt;User&gt;, never plaintext.</summary>
public class User
{
    public int Id { get; set; }
    public required string Username { get; set; }
    public required string PasswordHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
}
