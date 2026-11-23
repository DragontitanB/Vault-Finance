using System.Text.Json.Serialization;
using VaultFinance.Domain.Accounts;

namespace VaultFinance.Application.Accounts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CreateAccountRequest
{
    public string? Name { get; init; }
    public string? Currency { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RenameAccountRequest
{
    public string? Name { get; init; }
}

// An optional empty JSON body is allowed; owner/status/currency input is not.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ArchiveAccountRequest { }

public sealed record AccountListQuery(
    int Page = 1, int PageSize = 20, AccountStatus? Status = null, string Sort = "name")
{
    public const int MaximumPage = 10_000;
    public const int MaximumPageSize = 100;
}
