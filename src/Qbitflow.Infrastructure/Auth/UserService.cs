using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Qbitflow.Core.Domain;
using Qbitflow.Infrastructure.Persistence;

namespace Qbitflow.Infrastructure.Auth;

public class UserService(AppDbContext db) : IUserService
{
    private const int MinPasswordLength = 8;
    private static readonly PasswordHasher<User> Hasher = new();

    public Task<bool> AnyUsersExistAsync(CancellationToken ct = default) =>
        db.Users.AsNoTracking().AnyAsync(ct);

    public async Task<User> CreateUserAsync(string username, string password, CancellationToken ct = default)
    {
        username = username.Trim();
        if (username.Length == 0)
        {
            throw new ArgumentException("Username is required.", nameof(username));
        }
        if (password.Length < MinPasswordLength)
        {
            throw new ArgumentException($"Password must be at least {MinPasswordLength} characters.", nameof(password));
        }
        if (await db.Users.AsNoTracking().AnyAsync(u => u.Username == username, ct))
        {
            throw new ArgumentException("That username is already taken.", nameof(username));
        }

        var user = new User
        {
            Username = username,
            PasswordHash = string.Empty,
            CreatedAt = DateTimeOffset.UtcNow
        };
        user.PasswordHash = Hasher.HashPassword(user, password);

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return user;
    }

    public async Task<User?> ValidateCredentialsAsync(string username, string password, CancellationToken ct = default)
    {
        username = username.Trim();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Username == username, ct);
        if (user is null)
        {
            return null;
        }

        var result = Hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
        {
            return null;
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = Hasher.HashPassword(user, password);
        }

        user.LastLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return user;
    }

    public async Task ChangePasswordAsync(int userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        if (newPassword.Length < MinPasswordLength)
        {
            throw new ArgumentException($"Password must be at least {MinPasswordLength} characters.", nameof(newPassword));
        }

        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        var result = Hasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword);
        if (result == PasswordVerificationResult.Failed)
        {
            throw new InvalidOperationException("Current password is incorrect.");
        }

        user.PasswordHash = Hasher.HashPassword(user, newPassword);
        await db.SaveChangesAsync(ct);
    }
}
