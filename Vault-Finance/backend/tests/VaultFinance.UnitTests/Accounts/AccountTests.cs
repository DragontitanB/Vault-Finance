using VaultFinance.Domain.Accounts;
using VaultFinance.Domain.Exceptions;

namespace VaultFinance.UnitTests.Accounts;

public sealed class AccountTests
{
    [Theory]
    [InlineData("dop", "DOP")]
    [InlineData(" usd ", "USD")]
    public void Constructor_CreatesActiveAccountWithNormalizedValues(string currency, string expected)
    {
        var id = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var account = new Account(id, userId, "  Emergency fund  ", currency);

        Assert.Equal(id, account.Id);
        Assert.Equal(userId, account.UserId);
        Assert.Equal("Emergency fund", account.Name);
        Assert.Equal(expected, account.Currency);
        Assert.Equal(AccountStatus.Active, account.Status);
    }

    [Fact]
    public void Constructor_RejectsEmptyIdentifier()
    {
        var exception = Assert.Throws<DomainException>(() =>
            new Account(Guid.Empty, Guid.NewGuid(), "Cash", "DOP"));

        Assert.Contains("Account identifier", exception.Message);
    }

    [Fact]
    public void Constructor_RejectsEmptyOwnerIdentifier()
    {
        var exception = Assert.Throws<DomainException>(() =>
            new Account(Guid.NewGuid(), Guid.Empty, "Cash", "DOP"));

        Assert.Contains("owner identifier", exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void Constructor_RejectsMissingName(string? name)
    {
        var exception = Assert.Throws<DomainException>(() =>
            new Account(Guid.NewGuid(), Guid.NewGuid(), name!, "DOP"));

        Assert.Contains("name is required", exception.Message);
    }

    [Fact]
    public void Constructor_RejectsNameBeyondMaximumLength()
    {
        var exception = Assert.Throws<DomainException>(() =>
            new Account(Guid.NewGuid(), Guid.NewGuid(), new string('A', 101), "DOP"));

        Assert.Contains("100", exception.Message);
    }

    [Fact]
    public void Constructor_AppliesNameLengthLimitAfterTrimming()
    {
        var name = new string('A', 100);

        var account = new Account(Guid.NewGuid(), Guid.NewGuid(), $"  {name}  ", "DOP");

        Assert.Equal(name, account.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("EUR")]
    [InlineData("US")]
    [InlineData("uſd")]
    public void Constructor_RejectsInvalidCurrency(string? currency)
    {
        Assert.Throws<DomainException>(() =>
            new Account(Guid.NewGuid(), Guid.NewGuid(), "Cash", currency!));
    }

    [Fact]
    public void Rename_ChangesNameWhilePreservingIdentityOwnerCurrencyAndStatus()
    {
        var account = CreateAccount();
        var id = account.Id;
        var userId = account.UserId;

        account.Rename("  Savings  ");

        Assert.Equal("Savings", account.Name);
        Assert.Equal(id, account.Id);
        Assert.Equal(userId, account.UserId);
        Assert.Equal("DOP", account.Currency);
        Assert.Equal(AccountStatus.Active, account.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void Rename_RejectsMissingNameWithoutChangingAccount(string? name)
    {
        var account = CreateAccount();

        Assert.Throws<DomainException>(() => account.Rename(name!));

        Assert.Equal("Cash", account.Name);
        Assert.Equal(AccountStatus.Active, account.Status);
    }

    [Fact]
    public void Rename_RejectsNameBeyondMaximumLengthWithoutChangingAccount()
    {
        var account = CreateAccount();

        Assert.Throws<DomainException>(() => account.Rename(new string('A', 101)));

        Assert.Equal("Cash", account.Name);
        Assert.Equal(AccountStatus.Active, account.Status);
    }

    [Fact]
    public void Rename_AcceptsMaximumLengthAfterTrimming()
    {
        var account = CreateAccount();
        var name = new string('A', 100);

        account.Rename($"  {name}  ");

        Assert.Equal(name, account.Name);
    }

    [Fact]
    public void Archive_PreservesIdentityOwnerNameAndCurrency()
    {
        var account = CreateAccount();
        var id = account.Id;
        var userId = account.UserId;

        account.Archive();

        Assert.Equal(AccountStatus.Archived, account.Status);
        Assert.Equal(id, account.Id);
        Assert.Equal(userId, account.UserId);
        Assert.Equal("Cash", account.Name);
        Assert.Equal("DOP", account.Currency);
    }

    [Fact]
    public void Archive_IsIdempotent()
    {
        var account = CreateAccount();
        account.Archive();

        account.Archive();

        Assert.Equal(AccountStatus.Archived, account.Status);
        Assert.Equal("Cash", account.Name);
    }

    [Theory]
    [InlineData("Savings")]
    [InlineData("Cash")]
    [InlineData("")]
    public void Rename_RejectsArchivedAccountAndPreservesItsState(string name)
    {
        var account = CreateAccount();
        account.Archive();

        var exception = Assert.Throws<DomainException>(() => account.Rename(name));

        Assert.Contains("archived account cannot be edited", exception.Message);
        Assert.Equal("Cash", account.Name);
        Assert.Equal(AccountStatus.Archived, account.Status);
    }

    private static Account CreateAccount() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Cash", "DOP");
}
