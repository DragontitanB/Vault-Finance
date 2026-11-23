using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using VaultFinance.Application.Accounts;
using VaultFinance.Domain.Accounts;

namespace VaultFinance.Infrastructure.Persistence;

internal sealed class EfAccountStore(VaultFinanceDbContext database) : IAccountStore
{
    private static readonly Expression<Func<Account, AccountResponse>> Projection = account =>
        new AccountResponse(account.Id, account.Name, account.Currency,
            account.Status == AccountStatus.Active ? nameof(AccountStatus.Active) : nameof(AccountStatus.Archived));

    public async Task AddAsync(Account account, CancellationToken cancellationToken)
    {
        database.Accounts.Add(account);
        await database.SaveChangesAsync(cancellationToken);
    }

    public Task<Account?> FindOwnedAsync(Guid userId, Guid accountId, CancellationToken cancellationToken) =>
        Owned(userId).AsNoTracking().SingleOrDefaultAsync(account => account.Id == accountId, cancellationToken);

    public Task<AccountResponse?> GetOwnedAsync(Guid userId, Guid accountId, CancellationToken cancellationToken) =>
        Owned(userId).AsNoTracking().Where(account => account.Id == accountId)
            .Select(Projection).SingleOrDefaultAsync(cancellationToken);

    public async Task<AccountPage> ListOwnedAsync(Guid userId, AccountListQuery query, CancellationToken cancellationToken)
    {
        var accounts = Owned(userId).AsNoTracking();
        if (query.Status is { } status) { accounts = accounts.Where(account => account.Status == status); }

        var total = await accounts.CountAsync(cancellationToken);
        var ordered = query.Sort == "name_desc"
            ? accounts.OrderByDescending(account => account.Name).ThenBy(account => account.Id)
            : accounts.OrderBy(account => account.Name).ThenBy(account => account.Id);
        var items = await ordered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(Projection).ToListAsync(cancellationToken);
        return new(items, query.Page, query.PageSize, total);
    }

    public async Task<bool> TryRenameOwnedAsync(Account account, CancellationToken cancellationToken) =>
        await Owned(account.UserId)
            .Where(current => current.Id == account.Id && current.Status == AccountStatus.Active)
            .ExecuteUpdateAsync(update => update.SetProperty(current => current.Name, account.Name), cancellationToken) == 1;

    public async Task<bool> ArchiveOwnedAsync(Account account, CancellationToken cancellationToken) =>
        await Owned(account.UserId).Where(current => current.Id == account.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(current => current.Status, AccountStatus.Archived), cancellationToken) == 1;

    private IQueryable<Account> Owned(Guid userId) =>
        database.Accounts.Where(account => account.UserId == userId);
}
