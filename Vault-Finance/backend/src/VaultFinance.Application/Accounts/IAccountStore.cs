using VaultFinance.Domain.Accounts;

namespace VaultFinance.Application.Accounts;

// Use-case-specific persistence port: ownership is mandatory on every existing-resource operation.
public interface IAccountStore
{
    Task AddAsync(Account account, CancellationToken cancellationToken);
    Task<Account?> FindOwnedAsync(Guid userId, Guid accountId, CancellationToken cancellationToken);
    Task<AccountResponse?> GetOwnedAsync(Guid userId, Guid accountId, CancellationToken cancellationToken);
    Task<AccountPage> ListOwnedAsync(Guid userId, AccountListQuery query, CancellationToken cancellationToken);
    Task<bool> TryRenameOwnedAsync(Account account, CancellationToken cancellationToken);
    Task<bool> ArchiveOwnedAsync(Account account, CancellationToken cancellationToken);
}
