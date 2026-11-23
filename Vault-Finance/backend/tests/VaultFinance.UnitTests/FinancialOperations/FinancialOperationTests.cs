using System.Reflection;
using VaultFinance.Domain.Accounts;
using VaultFinance.Domain.Exceptions;
using VaultFinance.Domain.FinancialOperations;
using VaultFinance.Domain.ValueObjects;

namespace VaultFinance.UnitTests.FinancialOperations;

public sealed class FinancialOperationTests
{
    private readonly Guid owner = Guid.NewGuid();
    private static DateTimeOffset Instant => new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(FinancialOperationKind.Opening)]
    [InlineData(FinancialOperationKind.Income)]
    [InlineData(FinancialOperationKind.Expense)]
    [InlineData(FinancialOperationKind.Transfer)]
    public void Factory_CreatesExpectedImmutableShape(FinancialOperationKind kind)
    {
        var source = NewAccount();
        var destination = NewAccount();
        var id = Guid.NewGuid();
        var amount = Amount();
        var posting = Posting();

        var operation = Create(kind, id, owner, source, destination, amount, posting, "  Test reason  ");

        Assert.Equal(id, operation.Id);
        Assert.Equal(owner, operation.UserId);
        Assert.Equal(kind, operation.Kind);
        Assert.Equal(FinancialOperationStatus.Confirmed, operation.Status);
        Assert.Same(posting, operation.Posting);
        Assert.Same(amount, operation.Amount);
        Assert.Equal("USD", operation.Currency);
        Assert.Equal("Test reason", operation.Reason);
        Assert.Null(operation.OriginalOperationId);
        Assert.Null(operation.OriginalOperationKind);
        Assert.Equal(kind is FinancialOperationKind.Expense or FinancialOperationKind.Transfer
            ? source.Id : (Guid?)null, operation.SourceAccountId);
        Assert.Equal(kind == FinancialOperationKind.Transfer ? destination.Id :
            kind == FinancialOperationKind.Expense ? null : source.Id, operation.DestinationAccountId);
    }

    [Theory]
    [InlineData(" dop ")]
    [InlineData("usd")]
    public void OrdinaryOperations_AcceptBothCurrenciesAndMaximum(string currency)
    {
        var account = NewAccount(currency);
        foreach (var kind in OrdinaryKinds)
        {
            var operation = Create(kind, Guid.NewGuid(), owner, account, NewAccount(currency),
                new FinancialAmount(new Money(FinancialAmount.MaximumValue, currency)), Posting(), "Opening");
            Assert.Equal(account.Currency, operation.Currency);
            Assert.Equal(FinancialAmount.MaximumValue, operation.Amount!.Value.Amount);
        }
    }

    [Theory]
    [InlineData(FinancialOperationKind.Opening)]
    [InlineData(FinancialOperationKind.Income)]
    [InlineData(FinancialOperationKind.Expense)]
    [InlineData(FinancialOperationKind.Transfer)]
    public void OrdinaryOperations_RejectInvalidIdentityOwnershipCurrencyAndState(FinancialOperationKind kind)
    {
        var account = NewAccount();
        var other = NewAccount();

        Assert.Throws<DomainException>(() => Create(kind, Guid.Empty, owner, account, other, Amount(), Posting()));
        Assert.Throws<DomainException>(() => Create(kind, Guid.NewGuid(), Guid.Empty, account, other, Amount(), Posting()));
        Assert.Throws<DomainException>(() => Create(kind, Guid.NewGuid(), Guid.NewGuid(), account, other, Amount(), Posting()));
        Assert.Throws<DomainException>(() => Create(kind, Guid.NewGuid(), owner, account, other, Amount("DOP"), Posting()));

        account.Archive();
        Assert.Throws<DomainException>(() => Create(kind, Guid.NewGuid(), owner, account, other, Amount(), Posting()));
    }

    [Fact]
    public void Transfer_ValidatesBothParticipantsAndDistinctIdentity()
    {
        var source = NewAccount();
        Assert.Throws<DomainException>(() => FinancialOperation.CreateTransfer(
            Guid.NewGuid(), owner, source, source, Amount(), Posting()));
        var sameIdentity = new Account(source.Id, owner, "Snapshot", "USD");
        Assert.Throws<DomainException>(() => FinancialOperation.CreateTransfer(
            Guid.NewGuid(), owner, source, sameIdentity, Amount(), Posting()));
        Assert.Throws<DomainException>(() => FinancialOperation.CreateTransfer(
            Guid.NewGuid(), owner, source, new Account(Guid.NewGuid(), Guid.NewGuid(), "Foreign", "USD"), Amount(), Posting()));
        Assert.Throws<DomainException>(() => FinancialOperation.CreateTransfer(
            Guid.NewGuid(), owner, source, NewAccount("DOP"), Amount(), Posting()));
        var archived = NewAccount();
        archived.Archive();
        Assert.Throws<DomainException>(() => FinancialOperation.CreateTransfer(
            Guid.NewGuid(), owner, source, archived, Amount(), Posting()));
    }

    [Theory]
    [InlineData(FinancialOperationKind.Opening)]
    [InlineData(FinancialOperationKind.Income)]
    [InlineData(FinancialOperationKind.Expense)]
    [InlineData(FinancialOperationKind.Transfer)]
    public void OrdinaryOperations_RejectMissingDependencies(FinancialOperationKind kind)
    {
        Assert.Throws<ArgumentNullException>(() =>
            Create(kind, Guid.NewGuid(), owner, null!, NewAccount(), Amount(), Posting()));
        Assert.Throws<ArgumentNullException>(() =>
            Create(kind, Guid.NewGuid(), owner, NewAccount(), NewAccount(), null!, Posting()));
        Assert.Throws<ArgumentNullException>(() =>
            Create(kind, Guid.NewGuid(), owner, NewAccount(), NewAccount(), Amount(), null!));
    }

    [Fact]
    public void Transfer_RejectsNullDestination()
    {
        Assert.Throws<ArgumentNullException>(() => FinancialOperation.CreateTransfer(
            Guid.NewGuid(), owner, NewAccount(), null!, Amount(), Posting()));
    }

    [Theory]
    [InlineData(FinancialOperationKind.Opening)]
    [InlineData(FinancialOperationKind.Income)]
    [InlineData(FinancialOperationKind.Expense)]
    [InlineData(FinancialOperationKind.Transfer)]
    public void LaterAccountChanges_DoNotMutateOperation(FinancialOperationKind kind)
    {
        var source = NewAccount();
        var destination = NewAccount();
        var original = Create(kind, Guid.NewGuid(), owner, source, destination, Amount(), Posting());

        source.Rename("Renamed");
        source.Archive();
        destination.Archive();

        Assert.Equal(kind, original.Kind);
        Assert.Equal(1.23m, original.Amount!.Value.Amount);
        Assert.Equal("USD", original.Currency);
        Assert.Equal(FinancialOperationStatus.Confirmed, original.Status);
    }

    [Theory]
    [InlineData(FinancialOperationKind.Opening)]
    [InlineData(FinancialOperationKind.Income)]
    [InlineData(FinancialOperationKind.Expense)]
    [InlineData(FinancialOperationKind.Transfer)]
    public void Reversal_ReferencesEveryOrdinaryKindWithoutCopyingMutableFinancialFields(FinancialOperationKind kind)
    {
        var source = NewAccount();
        var destination = NewAccount();
        var original = Create(kind, Guid.NewGuid(), owner, source, destination, Amount(), Posting());
        source.Archive();
        destination.Archive();
        var context = Posting(Instant.AddMinutes(1));

        var reversal = FinancialOperation.CreateReversal(Guid.NewGuid(), owner, original, context, "  Correction  ");

        Assert.Equal(FinancialOperationKind.Reversal, reversal.Kind);
        Assert.Equal(FinancialOperationStatus.Confirmed, reversal.Status);
        Assert.Equal(owner, reversal.UserId);
        Assert.Equal(original.Id, reversal.OriginalOperationId);
        Assert.Equal(kind, reversal.OriginalOperationKind);
        Assert.Equal(original.Currency, reversal.Currency);
        Assert.Same(context, reversal.Posting);
        Assert.Null(reversal.Amount);
        Assert.Null(reversal.SourceAccountId);
        Assert.Null(reversal.DestinationAccountId);
        Assert.Equal("Correction", reversal.Reason);
        Assert.Equal(kind, original.Kind);
        Assert.Equal(1.23m, original.Amount!.Value.Amount);
        Assert.Equal(FinancialOperationStatus.Confirmed, original.Status);
    }

    [Fact]
    public void Reversal_RejectsInvalidIdentityOwnerChronologyAndRecursiveReversal()
    {
        var original = Income();
        var later = Posting(Instant.AddDays(1));

        Assert.Throws<DomainException>(() => FinancialOperation.CreateReversal(Guid.Empty, owner, original, later, "Reason"));
        Assert.Throws<DomainException>(() => FinancialOperation.CreateReversal(Guid.NewGuid(), Guid.Empty, original, later, "Reason"));
        Assert.Throws<DomainException>(() => FinancialOperation.CreateReversal(Guid.NewGuid(), Guid.NewGuid(), original, later, "Reason"));
        Assert.Throws<DomainException>(() => FinancialOperation.CreateReversal(original.Id, owner, original, later, "Reason"));
        Assert.Throws<DomainException>(() => FinancialOperation.CreateReversal(Guid.NewGuid(), owner, original, original.Posting, "Reason"));
        Assert.Throws<DomainException>(() => FinancialOperation.CreateReversal(Guid.NewGuid(), owner, original, Posting(Instant.AddTicks(-1)), "Reason"));

        var reversal = FinancialOperation.CreateReversal(Guid.NewGuid(), owner, original, later, "Reason");
        Assert.Throws<DomainException>(() => FinancialOperation.CreateReversal(
            Guid.NewGuid(), owner, reversal, Posting(Instant.AddDays(2)), "Reason"));
    }

    [Fact]
    public void Reversal_RejectsNullDependencies()
    {
        Assert.Throws<ArgumentNullException>(() =>
            FinancialOperation.CreateReversal(Guid.NewGuid(), owner, null!, Posting(), "Reason"));
        Assert.Throws<ArgumentNullException>(() =>
            FinancialOperation.CreateReversal(Guid.NewGuid(), owner, Income(), null!, "Reason"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void OpeningAndReversal_RequireReason(string? reason)
    {
        Assert.Throws<DomainException>(() =>
            FinancialOperation.CreateOpening(Guid.NewGuid(), owner, NewAccount(), Amount(), Posting(), reason!));
        Assert.Throws<DomainException>(() =>
            FinancialOperation.CreateReversal(Guid.NewGuid(), owner, Income(), Posting(), reason!));
    }

    [Fact]
    public void Reasons_AreBoundedAndOptionalWhitespaceIsAbsent()
    {
        foreach (var kind in OrdinaryKinds)
        {
            Assert.Throws<DomainException>(() => Create(kind, Guid.NewGuid(), owner, NewAccount(), NewAccount(),
                Amount(), Posting(), new string('x', FinancialOperation.MaximumReasonLength + 1)));
            Assert.Equal(FinancialOperation.MaximumReasonLength, Create(kind, Guid.NewGuid(), owner,
                NewAccount(), NewAccount(), Amount(), Posting(),
                new string('x', FinancialOperation.MaximumReasonLength)).Reason!.Length);
        }
        Assert.Throws<DomainException>(() => FinancialOperation.CreateReversal(
            Guid.NewGuid(), owner, Income(), Posting(), new string('x', 201)));
        Assert.Null(FinancialOperation.CreateIncome(Guid.NewGuid(), owner, NewAccount(), Amount(), Posting(), "  ").Reason);
        Assert.Null(FinancialOperation.CreateExpense(Guid.NewGuid(), owner, NewAccount(), Amount(), Posting()).Reason);
        Assert.Null(FinancialOperation.CreateTransfer(Guid.NewGuid(), owner, NewAccount(), NewAccount(), Amount(), Posting()).Reason);
    }

    [Fact]
    public void LocalReversalDescriptors_DoNotClaimGlobalPostingUniqueness()
    {
        var original = Income();
        var first = FinancialOperation.CreateReversal(Guid.NewGuid(), owner, original, Posting(), "First");
        var second = FinancialOperation.CreateReversal(Guid.NewGuid(), owner, original, Posting(), "Second");

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.OriginalOperationId, second.OriginalOperationId);
        Assert.Equal(FinancialOperationKind.Income, original.Kind);
    }

    [Fact]
    public void PublicApi_HasNoMutableStateOrTransitionPaths()
    {
        Assert.True(typeof(FinancialOperation).IsSealed);
        Assert.Empty(typeof(FinancialOperation).GetConstructors());
        foreach (var type in new[] { typeof(FinancialOperation), typeof(FinancialAmount), typeof(FinancialPostingContext) })
        {
            Assert.All(type.GetProperties(), property => Assert.Null(property.SetMethod));
        }

        Assert.DoesNotContain(typeof(FinancialOperation).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => !method.IsSpecialName);
        Assert.Equal(new[] { FinancialOperationStatus.Confirmed }, Enum.GetValues<FinancialOperationStatus>());
    }

    private Account NewAccount(string currency = "USD") => new(Guid.NewGuid(), owner, "Synthetic", currency);
    private static FinancialAmount Amount(string currency = "USD") => new(new Money(1.23m, currency));
    private static FinancialPostingContext Posting(DateTimeOffset? instant = null) => new(Guid.NewGuid(), instant ?? Instant);
    private FinancialOperation Income() => FinancialOperation.CreateIncome(Guid.NewGuid(), owner, NewAccount(), Amount(), Posting());

    private static readonly FinancialOperationKind[] OrdinaryKinds =
    [
        FinancialOperationKind.Opening, FinancialOperationKind.Income,
        FinancialOperationKind.Expense, FinancialOperationKind.Transfer
    ];

    private static FinancialOperation Create(FinancialOperationKind kind, Guid id, Guid userId,
        Account account, Account destination, FinancialAmount amount, FinancialPostingContext posting, string? reason = "Test") =>
        kind switch
        {
            FinancialOperationKind.Opening => FinancialOperation.CreateOpening(id, userId, account, amount, posting, reason!),
            FinancialOperationKind.Income => FinancialOperation.CreateIncome(id, userId, account, amount, posting, reason),
            FinancialOperationKind.Expense => FinancialOperation.CreateExpense(id, userId, account, amount, posting, reason),
            FinancialOperationKind.Transfer => FinancialOperation.CreateTransfer(id, userId, account, destination, amount, posting, reason),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
}
