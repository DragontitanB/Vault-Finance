using VaultFinance.Domain.Accounts;
using VaultFinance.Domain.Exceptions;

namespace VaultFinance.Application.Accounts;

public sealed class AccountService(IAccountStore store) : IAccountService
{
    public async Task<AccountOutcome> CreateAsync(Guid userId, CreateAccountRequest request, CancellationToken cancellationToken)
    {
        Account account;
        try { account = new Account(Guid.NewGuid(), userId, request.Name!, request.Currency!); }
        catch (DomainException exception) { return Invalid("account", exception.Message); }

        await store.AddAsync(account, cancellationToken);
        return new(AccountResponse.From(account));
    }

    public async Task<AccountListOutcome> ListAsync(Guid userId, AccountListQuery query, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (query.Page is < 1 or > AccountListQuery.MaximumPage)
        {
            errors["page"] = [$"Page must be between 1 and {AccountListQuery.MaximumPage}."];
        }
        if (query.PageSize is < 1 or > AccountListQuery.MaximumPageSize)
        {
            errors["pageSize"] = [$"Page size must be between 1 and {AccountListQuery.MaximumPageSize}."];
        }
        if (query.Status is not null && !Enum.IsDefined(query.Status.Value))
        {
            errors["status"] = ["Status must be Active or Archived."];
        }
        if (query.Sort is not ("name" or "name_desc"))
        {
            errors["sort"] = ["Sort must be name or name_desc."];
        }

        return errors.Count != 0
            ? new(Errors: errors)
            : new(await store.ListOwnedAsync(userId, query, cancellationToken));
    }

    public async Task<AccountOutcome> GetAsync(Guid userId, Guid accountId, CancellationToken cancellationToken)
    {
        var account = await store.GetOwnedAsync(userId, accountId, cancellationToken);
        return account is null ? new(Failure: AccountFailure.NotFound) : new(account);
    }

    public async Task<AccountOutcome> RenameAsync(
        Guid userId, Guid accountId, RenameAccountRequest request, CancellationToken cancellationToken)
    {
        var account = await store.FindOwnedAsync(userId, accountId, cancellationToken);
        if (account is null) { return new(Failure: AccountFailure.NotFound); }
        if (account.Status == AccountStatus.Archived) { return new(Failure: AccountFailure.Archived); }

        try { account.Rename(request.Name!); }
        catch (DomainException exception) { return Invalid("name", exception.Message); }

        if (!await store.TryRenameOwnedAsync(account, cancellationToken))
        {
            // A concurrent archive must not be overwritten by a stale active snapshot.
            var current = await store.GetOwnedAsync(userId, accountId, cancellationToken);
            return new(Failure: current is null ? AccountFailure.NotFound : AccountFailure.Archived);
        }

        return await GetAsync(userId, accountId, cancellationToken);
    }

    public async Task<AccountOutcome> ArchiveAsync(Guid userId, Guid accountId, CancellationToken cancellationToken)
    {
        var account = await store.FindOwnedAsync(userId, accountId, cancellationToken);
        if (account is null) { return new(Failure: AccountFailure.NotFound); }

        account.Archive();
        if (!await store.ArchiveOwnedAsync(account, cancellationToken))
        {
            return new(Failure: AccountFailure.NotFound);
        }

        return await GetAsync(userId, accountId, cancellationToken);
    }

    private static AccountOutcome Invalid(string field, string message) =>
        new(Failure: AccountFailure.InvalidInput, Errors: new Dictionary<string, string[]> { [field] = [message] });
}
