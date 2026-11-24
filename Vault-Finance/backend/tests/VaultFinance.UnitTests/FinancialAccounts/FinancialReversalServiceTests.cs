using VaultFinance.Application.FinancialAccounts;

namespace VaultFinance.UnitTests.FinancialAccounts;

public sealed class FinancialReversalServiceTests
{
    [Theory]
    [InlineData("  Synthetic correction  ")]
    [InlineData("Synthetic \"correction\"\n\u03b1")]
    public async Task Reversal_PassesOnlyTheNormalizedIntentAndSeparateTrustedOwner(string reason)
    {
        var journal = new CapturingJournal();
        var owner = new FinancialOwner(Guid.NewGuid());
        var request = new FinancialReversalRequest(Guid.NewGuid(), reason);
        using var cancellation = new CancellationTokenSource();
        var result = await new FinancialAccountService(journal).RecordReversalAsync(owner, request, "Key_1", cancellation.Token);
        Assert.Same(journal.Result, result); Assert.Same(owner, journal.Owner);
        Assert.Equal(new(request.OriginalOperationId, reason.Trim(), "Key_1"), journal.Reversal);
        Assert.Equal(cancellation.Token, journal.Token); Assert.Equal(1, journal.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task Reversal_RequiresAnAuditableReason(string? reason) =>
        await Invalid(new(Guid.NewGuid(), reason));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad key")]
    [InlineData("key\0")]
    public async Task Reversal_RejectsInvalidKeys(string? key) =>
        await Invalid(new(Guid.NewGuid(), "Synthetic reason"), key!);

    [Fact]
    public async Task Reversal_RejectsEmptyOriginalAndExcessiveReasonOrKey()
    {
        await Invalid(new(Guid.Empty, "Synthetic reason"));
        await Invalid(new(Guid.NewGuid(), new string('r', 201)));
        await Invalid(new(Guid.NewGuid(), "Synthetic reason"), new string('k', 129));
    }

    [Fact]
    public async Task Reversal_RequiresExplicitDependenciesAndExposesNoClientControlledEffects()
    {
        var service = new FinancialAccountService(new CapturingJournal());
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.RecordReversalAsync(null!, new(Guid.NewGuid(), "Synthetic"), "key"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.RecordReversalAsync(new(Guid.NewGuid()), null!, "key"));
        Assert.Equal(new[] { "OriginalOperationId", "Reason" }, typeof(FinancialReversalRequest).GetProperties().Select(p => p.Name));
        Assert.Equal(new[] { "OriginalOperationId", "Reason", "IdempotencyKey" }, typeof(ValidatedFinancialReversal).GetProperties().Select(p => p.Name));
        Assert.DoesNotContain(typeof(FinancialReversalReceipt).GetProperties(), p => p.Name.Contains("Balance", StringComparison.Ordinal));
    }

    private static async Task Invalid(FinancialReversalRequest request, string key = "key")
    {
        var journal = new CapturingJournal();
        var result = await new FinancialAccountService(journal).RecordReversalAsync(new(Guid.NewGuid()), request, key);
        Assert.Equal(FinancialFailure.InvalidInput, result.Failure); Assert.NotEmpty(result.Errors!);
        Assert.Equal(0, journal.Calls);
    }

    private sealed class CapturingJournal : IFinancialJournal
    {
        public Task<OpeningCorrectionOutcome> CorrectOpeningAsync(FinancialOwner owner, ValidatedOpeningCorrection correction, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public FinancialOwner? Owner { get; private set; }
        public ValidatedFinancialReversal? Reversal { get; private set; }
        public CancellationToken Token { get; private set; }
        public int Calls { get; private set; }
        public FinancialReversalOutcome Result { get; } = new(Failure: FinancialFailure.AlreadyReversed);
        public Task<FinancialReversalOutcome> RecordReversalAsync(FinancialOwner owner, ValidatedFinancialReversal reversal, CancellationToken cancellationToken)
        {
            Owner = owner; Reversal = reversal; Token = cancellationToken; Calls++; return Task.FromResult(Result);
        }
        public Task<OpeningOutcome> InitializeOpeningAsync(FinancialOwner owner, ValidatedOpening opening, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BalanceOutcome> GetBalanceAsync(FinancialOwner owner, Guid accountId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FinancialEntryOutcome> RecordIncomeAsync(FinancialOwner owner, ValidatedFinancialEntry entry, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FinancialEntryOutcome> RecordExpenseAsync(FinancialOwner owner, ValidatedFinancialEntry entry, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FinancialTransferOutcome> RecordTransferAsync(FinancialOwner owner, ValidatedFinancialTransfer transfer, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
