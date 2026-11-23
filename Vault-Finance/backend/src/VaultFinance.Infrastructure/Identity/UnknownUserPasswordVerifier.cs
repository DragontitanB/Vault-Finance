using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace VaultFinance.Infrastructure.Identity;

// Uses Identity's own hasher to reduce the obvious timing difference for unknown/locked users.
internal sealed class UnknownUserPasswordVerifier
{
    private readonly PasswordHasher<ApplicationUser> _hasher;
    private readonly ApplicationUser _user = new();
    private readonly string _hash;

    public UnknownUserPasswordVerifier(IOptions<PasswordHasherOptions> options)
    {
        _hasher = new PasswordHasher<ApplicationUser>(options);
        _hash = _hasher.HashPassword(_user, Guid.NewGuid().ToString("N"));
    }

    public void Verify(string password) => _hasher.VerifyHashedPassword(_user, _hash, password);
}
