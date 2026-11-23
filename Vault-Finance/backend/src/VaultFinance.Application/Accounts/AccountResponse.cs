using VaultFinance.Domain.Accounts;

namespace VaultFinance.Application.Accounts;

public sealed record AccountResponse(Guid Id, string Name, string Currency, string Status)
{
    public static AccountResponse From(Account account) =>
        new(account.Id, account.Name, account.Currency, account.Status.ToString());
}

public sealed record AccountPage(
    IReadOnlyList<AccountResponse> Items, int Page, int PageSize, int TotalCount);
