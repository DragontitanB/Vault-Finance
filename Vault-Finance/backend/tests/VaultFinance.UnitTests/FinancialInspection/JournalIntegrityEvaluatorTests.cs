using System.Text.Json;
using VaultFinance.Application.FinancialInspection;
using VaultFinance.Domain.FinancialOperations;

namespace VaultFinance.UnitTests.FinancialInspection;

public sealed class JournalIntegrityEvaluatorTests
{
    [Theory]
    [InlineData("uninitialized")]
    [InlineData("zero")]
    [InlineData("positive")]
    [InlineData("entries")]
    [InlineData("transfer-reversals")]
    [InlineData("corrections")]
    [InlineData("negative-prefix")]
    [InlineData("archived-cancellation")]
    public void ValidHistory_IsHealthyWithoutInventingInitializationOrCheckingComponentPrefixes(string scenario)
    {
        var f = new InspectionFixture();
        if (scenario != "uninitialized") f.Open(f.A, scenario == "zero" ? 0m : 100m);
        if (scenario == "entries") { f.Income(f.A, 20m, "Synthetic \u03b1\U0001F4B0"); f.Expense(f.A, 30m); }
        if (scenario == "transfer-reversals")
        { f.Open(f.B, 0m); f.Reverse(f.Income(f.A, 20m)); f.Reverse(f.Expense(f.A, 10m)); f.Reverse(f.Transfer(f.A, f.B, 30m)); }
        if (scenario == "corrections") { f.Correct(f.A, 80m); f.Correct(f.A, 0m); f.Correct(f.A, 20m); f.Correct(f.A, 50m); }
        if (scenario == "negative-prefix") { f.Expense(f.A, 80m); f.Correct(f.A, 80m); }
        if (scenario == "archived-cancellation") f.Correct(f.A, 0m);
        var snapshot = f.Snapshot();
        if (scenario == "archived-cancellation") snapshot = snapshot with { Accounts = snapshot.Accounts.Select(x => x with { Status = 2 }).ToArray() };
        var report = new JournalIntegrityEvaluator().Evaluate(snapshot);
        Assert.Equal(InspectionStatus.Healthy, report.Status); Assert.Empty(report.Findings);
        Assert.Equal(scenario != "uninitialized", report.Accounts.Single(x => x.AccountId == f.A).IsInitialized);
        Assert.Equal(scenario == "transfer-reversals", report.Accounts.Single(x => x.AccountId == f.B).IsInitialized);
    }

    [Theory]
    [InlineData(1, "account.not_found")]
    [InlineData(1, "opening.already_initialized")]
    [InlineData(2, "opening.unchanged")]
    [InlineData(2, "opening.version_conflict")]
    [InlineData(2, "funds.insufficient")]
    [InlineData(3, "account.uninitialized")]
    [InlineData(3, "balance.limit_exceeded")]
    [InlineData(4, "funds.insufficient")]
    [InlineData(5, "account.not_found")]
    [InlineData(5, "balance.limit_exceeded")]
    [InlineData(6, "operation.not_found")]
    [InlineData(6, "operation.not_reversible")]
    [InlineData(6, "reversal.already_reversed")]
    public void PersistedRejections_AreNotOrphanBatchesOrInvalidOptionalMovements(int command, string code)
    {
        var f = new InspectionFixture(); f.Open(f.A, 0m); f.Reject(command, code, Guid.NewGuid());
        var report = new JournalIntegrityEvaluator().Evaluate(f.Snapshot());
        Assert.Equal(InspectionStatus.Healthy, report.Status); Assert.Empty(report.Findings);
    }

    [Theory]
    [InlineData("reported", "balance.mismatch")]
    [InlineData("effect", "effect.mismatch")]
    [InlineData("missing-effect", "effect.missing_or_duplicate")]
    [InlineData("duplicate-effect", "effect.missing_or_duplicate")]
    [InlineData("version-gap", "opening.chain_invalid")]
    [InlineData("fork", "opening.fork")]
    [InlineData("zero-reference", "opening.reference_invalid")]
    [InlineData("extra-component", "batch.composition_invalid")]
    [InlineData("duplicate-reversal", "reversal.duplicate")]
    [InlineData("wrong-reference", "reversal.reference_invalid")]
    [InlineData("copied-reversal-amount", "reversal.reference_invalid")]
    [InlineData("missing-audit", "audit.membership_invalid")]
    [InlineData("orphan-audit", "audit.orphan")]
    [InlineData("audit-time", "audit.contract_invalid")]
    [InlineData("audit-summary", "audit.summary_invalid")]
    [InlineData("duplicate-scope", "idempotency.duplicate_scope")]
    [InlineData("fingerprint", "idempotency.fingerprint_mismatch")]
    [InlineData("receipt", "idempotency.receipt_invalid")]
    [InlineData("batch-outcome", "batch.outcome_missing_or_duplicate")]
    [InlineData("reader-failure", "balance.reader_rejected")]
    public void InvalidPureSnapshots_ProduceStableFindingsWithoutTouchingSql(string corruption, string code)
    {
        var f = new InspectionFixture(); f.Open(f.A, 100m); f.Open(f.B, 0m);
        f.Reverse(f.Transfer(f.A, f.B, 10m)); f.Correct(f.A, 80m);
        var s = f.Snapshot(); var reversal = s.Operations.First(x => x.Kind == 5 && x.OriginalOperationKind == 4);
        s = corruption switch
        {
            "reported" => s with { ReportedBalances = s.ReportedBalances.Select(x => x with { Amount = (x.Amount ?? 0m) + 1m }).ToArray() },
            "effect" => s with { Effects = s.Effects.Select((x, i) => i == 0 ? x with { SignedAmount = x.SignedAmount + 1m } : x).ToArray() },
            "missing-effect" => s with { Effects = s.Effects.Skip(1).ToArray() },
            "duplicate-effect" => s with { Effects = s.Effects.Concat([s.Effects[0]]).ToArray() },
            "version-gap" => s with { Openings = s.Openings.Where(x => x.AccountId != f.A || x.Revision != 1).ToArray() },
            "fork" => s with { Openings = s.Openings.Concat([s.Openings.Last() with { Revision = 3 }]).ToArray() },
            "zero-reference" => s with { Openings = s.Openings.Select(x => x.AccountId == f.B ? x with { OpeningOperationKind = 1 } : x).ToArray() },
            "extra-component" => s with { Operations = s.Operations.Concat([s.Operations[0] with { Id = Guid.NewGuid() }]).ToArray() },
            "duplicate-reversal" => s with { Operations = s.Operations.Concat([reversal with { Id = Guid.NewGuid() }]).ToArray() },
            "wrong-reference" => s with { Operations = s.Operations.Select(x => x.Id == reversal.Id ? x with { OriginalOperationId = Guid.NewGuid() } : x).ToArray() },
            "copied-reversal-amount" => s with { Operations = s.Operations.Select(x => x.Id == reversal.Id ? x with { Amount = 10m } : x).ToArray() },
            "missing-audit" => s with { Audits = s.Audits.Skip(1).ToArray() },
            "orphan-audit" => s with { Audits = s.Audits.Concat([s.Audits[0] with { Id = Guid.NewGuid(), OutcomeId = Guid.NewGuid() }]).ToArray() },
            "audit-time" => s with { Audits = s.Audits.Select(x => x with { RecordedAtUtc = x.RecordedAtUtc.AddSeconds(1) }).ToArray() },
            "audit-summary" => s with { Audits = s.Audits.Select(x => x with { SummaryJson = "{}" }).ToArray() },
            "duplicate-scope" => s with { Outcomes = s.Outcomes.Concat([s.Outcomes[0] with { Id = Guid.NewGuid() }]).ToArray() },
            "fingerprint" => s with { Outcomes = s.Outcomes.Select(x => x with { PayloadFingerprint = new string('A', 64) }).ToArray() },
            "receipt" => s with { Outcomes = s.Outcomes.Select(x => x with { ReceiptJson = "{\"receiptVersion\":1}" }).ToArray() },
            "batch-outcome" => s with { Outcomes = s.Outcomes.Skip(1).ToArray() },
            "reader-failure" => s with { ReportedBalances = s.ReportedBalances.Select(x => x with { FailureCode = "journal.inconsistent", Amount = null }).ToArray() },
            _ => throw new InvalidOperationException()
        };
        var report = new JournalIntegrityEvaluator().Evaluate(s);
        Assert.Equal(InspectionStatus.Discrepancies, report.Status); Assert.Contains(report.Findings, x => x.Code == code);
        Assert.All(report.Findings, x => Assert.DoesNotContain("Synthetic", x.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void IncorrectTransferCompensation_IsDetectedSeparatelyFromTotalBalance()
    {
        var f = new InspectionFixture(); f.Open(f.A, 100m); f.Open(f.B, 0m); var reversal = f.Reverse(f.Transfer(f.A, f.B, 20m));
        var snapshot = f.Snapshot(); snapshot = snapshot with { Effects = snapshot.Effects.Select(x => x.OperationId == reversal && x.AccountId == f.A ? x with { SignedAmount = 19m } : x).ToArray() };
        var report = new JournalIntegrityEvaluator().Evaluate(snapshot);
        Assert.Contains(report.Findings, x => x.Code == "effect.mismatch"); Assert.Contains(report.Findings, x => x.Code == "transfer.conservation_invalid");
    }

    [Fact]
    public void NegativeFinalBalance_IsDetectedAtTheCompleteBatchBoundary()
    {
        var f = new InspectionFixture(); f.Open(f.A, 10m); f.Expense(f.A, 20m);
        Assert.Contains(new JournalIntegrityEvaluator().Evaluate(f.Snapshot()).Findings, x => x.Code == "balance.negative");
    }

    [Fact]
    public void ArbitraryPrecisionReconstruction_DetectsTotalsBeyondClrDecimalWithoutOverflow()
    {
        var f = new InspectionFixture(); f.Open(f.A, 0m); var s = f.Snapshot();
        s = s with { Operations = Enumerable.Range(0, 800).Select(_ => new JournalOperation(Guid.NewGuid(), f.Owner, s.Batches[0].Id,
            2, "USD", FinancialAmount.MaximumValue, null, f.A, null, null, null)).ToArray() };
        Assert.Contains(new JournalIntegrityEvaluator().Evaluate(s).Findings, x => x.Code == "balance.limit_exceeded");
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("fingerprint")]
    [InlineData("result")]
    public void UnknownContracts_AreIncompleteInsteadOfHealthyOrInventedCorruption(string unsupported)
    {
        var f = new InspectionFixture(); f.Open(f.A, 0m); if (unsupported == "result") f.Reject(2, "future.business_rejection");
        var snapshot = f.Snapshot();
        snapshot = snapshot with { Outcomes = snapshot.Outcomes.Select(x => unsupported == "receipt" ? x with { ReceiptJson = x.ReceiptJson.Replace("\"receiptVersion\":1", "\"receiptVersion\":2") }
            : unsupported == "fingerprint" ? x with { FingerprintVersion = 2 } : x).ToArray() };
        Assert.Equal(InspectionStatus.Incomplete, new JournalIntegrityEvaluator().Evaluate(snapshot).Status);
    }

    [Fact]
    public void HistoricalV1Receipts_AreComparedSemanticallyAndNeverAgainstCurrentBalanceOrRevision()
    {
        var f = new InspectionFixture(); f.Open(f.A, 100m); f.Correct(f.A, 80m); f.Correct(f.A, 60m);
        f.Outcomes[0] = f.Outcomes[0] with { ReceiptJson = f.Outcomes[0].ReceiptJson.Replace("}", ",\"futureNote\":\"compatible\"}") };
        Assert.Equal(InspectionStatus.Healthy, new JournalIntegrityEvaluator().Evaluate(f.Snapshot()).Status);
    }

    [Fact]
    public void ForeignRowsAndIncompleteCapture_CannotProduceHealthyOrLeakForeignIdentifiers()
    {
        var f = new InspectionFixture(); var s = f.Snapshot();
        Assert.Equal(InspectionStatus.Incomplete, new JournalIntegrityEvaluator().Evaluate(s with { IsComplete = false }).Status);
        var result = new JournalIntegrityEvaluator().Evaluate(s with { Accounts = [s.Accounts[0] with { UserId = Guid.NewGuid() }] });
        Assert.Equal(InspectionStatus.ExecutionFailed, result.Status); Assert.Empty(result.Findings); Assert.Empty(result.Accounts);
    }
}
