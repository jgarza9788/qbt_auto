using Qbitflow.Core.Domain;

namespace Qbitflow.Infrastructure.Auth;

public interface IUserService
{
    Task<bool> AnyUsersExistAsync(CancellationToken ct = default);

    /// <summary>Throws ArgumentException if username is taken or the password fails minimum strength rules.</summary>
    Task<User> CreateUserAsync(string username, string password, CancellationToken ct = default);

    /// <summary>Returns null on any failure (unknown username or wrong password) -- callers must not distinguish the two.</summary>
    Task<User?> ValidateCredentialsAsync(string username, string password, CancellationToken ct = default);

    /// <summary>Throws InvalidOperationException if currentPassword is wrong.</summary>
    Task ChangePasswordAsync(int userId, string currentPassword, string newPassword, CancellationToken ct = default);
}
