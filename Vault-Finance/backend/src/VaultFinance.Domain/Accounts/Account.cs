using VaultFinance.Domain.Exceptions;
using VaultFinance.Domain.ValueObjects;

namespace VaultFinance.Domain.Accounts;

public sealed class Account
{
    public const int MaximumNameLength = 100;

    public Guid Id { get; }
    public Guid UserId { get; }
    public string Name { get; private set; }
    public string Currency { get; }
    public AccountStatus Status { get; private set; }

    public Account(Guid id, Guid userId, string name, string currency)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException("Account identifier must not be empty.");
        }

        if (userId == Guid.Empty)
        {
            throw new DomainException("Account owner identifier must not be empty.");
        }

        Id = id;
        UserId = userId;
        Name = ValidateName(name);
        Currency = CurrencyCode.Normalize(currency);
        Status = AccountStatus.Active;
    }

    public void Rename(string name)
    {
        if (Status == AccountStatus.Archived)
        {
            throw new DomainException("An archived account cannot be edited.");
        }

        Name = ValidateName(name);
    }

    public void Archive() => Status = AccountStatus.Archived;

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("Account name is required.");
        }

        var trimmedName = name.Trim();

        if (trimmedName.Length > MaximumNameLength)
        {
            throw new DomainException($"Account name must not exceed {MaximumNameLength} characters.");
        }

        return trimmedName;
    }
}
