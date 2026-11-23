namespace VaultFinance.Application.Accounts;

public interface IAccountService
{
    Task<AccountOutcome> CreateAsync(Guid userId, CreateAccountRequest request, CancellationToken cancellationToken);
    Task<AccountListOutcome> ListAsync(Guid userId, AccountListQuery query, CancellationToken cancellationToken);
    Task<AccountOutcome> GetAsync(Guid userId, Guid accountId, CancellationToken cancellationToken);
    Task<AccountOutcome> RenameAsync(Guid userId, Guid accountId, RenameAccountRequest request, CancellationToken cancellationToken);
    Task<AccountOutcome> ArchiveAsync(Guid userId, Guid accountId, CancellationToken cancellationToken);
}
