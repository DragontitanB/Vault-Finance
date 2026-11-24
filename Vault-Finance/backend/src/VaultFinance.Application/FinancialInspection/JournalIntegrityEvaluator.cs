using System.Globalization;
using System.Numerics;
using VaultFinance.Domain.FinancialOperations;

namespace VaultFinance.Application.FinancialInspection;

/// <summary>Independent canonical-row evaluator. No EF, SQL view calculation or writer validation calls.</summary>
public sealed partial class JournalIntegrityEvaluator
{
    public FinancialInspectionReport Evaluate(JournalSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsComplete || snapshot.OwnerId == Guid.Empty || snapshot.InspectionId == Guid.Empty || !UtcCapture(snapshot.CapturedAtUtc))
            return FinancialInspectionService.Failed(InspectionStatus.Incomplete, snapshot.OwnerId, "inspection.capture_incomplete");
        if (snapshot.Accounts.Any(x => x.UserId != snapshot.OwnerId) || snapshot.Batches.Any(x => x.UserId != snapshot.OwnerId) ||
            snapshot.Operations.Any(x => x.UserId != snapshot.OwnerId) || snapshot.Openings.Any(x => x.UserId != snapshot.OwnerId) ||
            snapshot.Outcomes.Any(x => x.UserId != snapshot.OwnerId) || snapshot.Audits.Any(x => x.UserId != snapshot.OwnerId) || snapshot.Effects.Any(x => x.UserId != snapshot.OwnerId))
            return FinancialInspectionService.Failed(InspectionStatus.ExecutionFailed, snapshot.OwnerId, "inspection.scope_invalid");
        return new Evaluation(snapshot).Run();
    }

    private static bool UtcCapture(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;

    private sealed partial class Evaluation(JournalSnapshot data)
    {
        private static readonly BigInteger Maximum = Cents(FinancialAmount.MaximumValue);
        private readonly List<InspectionFinding> findings = [];
        private bool incomplete;
        private Dictionary<Guid, JournalAccount> accounts = null!;
        private Dictionary<Guid, JournalBatch> batches = null!;
        private Dictionary<Guid, JournalOperation> operations = null!;
        private Dictionary<Guid, List<ExpectedEffect>> effects = [];
        private sealed record ExpectedEffect(Guid OperationId, Guid AccountId, Guid BatchId, string Currency, BigInteger Amount);

        public FinancialInspectionReport Run()
        {
            accounts = Unique(data.Accounts, x => x.Id, "account.duplicate");
            batches = Unique(data.Batches, x => x.Id, "batch.duplicate");
            operations = Unique(data.Operations, x => x.Id, "operation.duplicate");
            Unique(data.Outcomes, x => x.Id, "idempotency.duplicate"); Unique(data.Audits, x => x.Id, "audit.duplicate");
            foreach (var sequence in data.Batches.GroupBy(x => x.Sequence))
                if (sequence.Key <= 0 || sequence.Count() != 1) Add("batch.sequence_invalid", batch: sequence.First().Id);
            foreach (var account in data.Accounts)
                if (account.Id == Guid.Empty || !Currency(account.Currency) || account.Status is not (1 or 2)) Add("account.invalid", account: account.Id);
            foreach (var batch in data.Batches)
            {
                if (batch.Id == Guid.Empty || batch.CommandType is < 1 or > 6) Add("batch.invalid", batch: batch.Id);
                if (!Utc(batch.RecordedAtUtc) || batch.AccountingTimeZone != FinancialPostingContext.AccountingTimeZoneId ||
                    batch.EffectiveDate != DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(batch.RecordedAtUtc,
                        TimeZoneInfo.FindSystemTimeZoneById(FinancialPostingContext.AccountingTimeZoneId)).DateTime)) Add("calendar.invalid", batch: batch.Id);
            }
            foreach (var operation in data.Operations.Where(x => x.Kind != 5)) Ordinary(operation);
            foreach (var operation in data.Operations.Where(x => x.Kind == 5)) Reversal(operation);
            foreach (var group in data.Operations.Where(x => x.OriginalOperationId.HasValue).GroupBy(x => x.OriginalOperationId))
                if (group.Count() != 1) Add("reversal.duplicate", operation: group.Key);
            OpeningsAndBatches();
            CompareEffectsAndBalances();
            OutcomesAndAudit();
            return new(incomplete ? InspectionStatus.Incomplete : findings.Count == 0 ? InspectionStatus.Healthy : InspectionStatus.Discrepancies,
                data.OwnerId, data.InspectionId, data.CapturedAtUtc, data.ConsistencyMode,
                accounts.Values.OrderBy(x => x.Id).Select(x => new InspectedAccount(x.Id, Initialized(x))).ToArray(),
                findings.Distinct().OrderBy(x => x.Code, StringComparer.Ordinal).ThenBy(x => x.AccountId).ThenBy(x => x.BatchId).ThenBy(x => x.OperationId).ThenBy(x => x.OutcomeId).ToArray(),
                incomplete ? "inspection.contract_unsupported_or_truncated" : null);
        }

        private Dictionary<Guid, T> Unique<T>(IReadOnlyList<T> rows, Func<T, Guid> key, string code)
        {
            var result = new Dictionary<Guid, T>();
            foreach (var row in rows) if (!result.TryAdd(key(row), row)) Add(code);
            return result;
        }
        private void Add(string code, Guid? account = null, Guid? batch = null, Guid? operation = null, Guid? outcome = null, int? revision = null)
        {
            if (findings.Count >= 1000) { incomplete = true; return; }
            findings.Add(new(code, account, batch, operation, outcome, revision));
        }
        private static bool Currency(string currency) => currency is "DOP" or "USD";
        private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
        private static bool Reason(string? value, bool required) => value is null ? !required : value.Length is > 0 and <= 200 && value.Trim() == value;
        private bool Initialized(JournalAccount account) => data.Openings.Any(x => x.AccountId == account.Id && x.Currency == account.Currency && x.Revision == 1);
        private static BigInteger Cents(decimal value)
        {
            var bits = decimal.GetBits(value);
            BigInteger coefficient = (uint)bits[0] + ((BigInteger)(uint)bits[1] << 32) + ((BigInteger)(uint)bits[2] << 64);
            int scale = (bits[3] >> 16) & 255;
            if (scale <= 2) coefficient *= BigInteger.Pow(10, 2 - scale);
            else
            {
                var divisor = BigInteger.Pow(10, scale - 2);
                if (coefficient % divisor != 0) throw new FormatException("Non-cent monetary input.");
                coefficient /= divisor;
            }
            return bits[3] < 0 ? -coefficient : coefficient;
        }
        private bool Magnitude(decimal? amount, out BigInteger value)
        {
            value = 0;
            if (amount is null) return false;
            try { value = Cents(amount.Value); return value > 0 && value <= Maximum; }
            catch (FormatException) { return false; }
        }

        private void Ordinary(JournalOperation operation)
        {
            bool shape = operation.Kind switch
            {
                1 or 2 => operation.SourceAccountId is null && operation.DestinationAccountId.HasValue,
                3 => operation.SourceAccountId.HasValue && operation.DestinationAccountId is null,
                4 => operation.SourceAccountId.HasValue && operation.DestinationAccountId.HasValue && operation.SourceAccountId != operation.DestinationAccountId,
                _ => false
            };
            if (!shape || operation.OriginalOperationId is not null || operation.OriginalOperationKind is not null ||
                operation.Id == Guid.Empty || !Currency(operation.Currency) || !Reason(operation.Reason, operation.Kind == 1) ||
                !Magnitude(operation.Amount, out var magnitude))
            { Add("operation.shape_invalid", operation: operation.Id, batch: operation.BatchId); return; }
            if (!batches.ContainsKey(operation.BatchId)) Add("operation.batch_missing", operation: operation.Id);
            var list = new List<ExpectedEffect>();
            // Explicit domain semantics; this does not call the view or reproduce its SQL UNION implementation.
            switch (operation.Kind)
            {
                case 1: case 2: Credit(operation.DestinationAccountId!.Value, magnitude); break;
                case 3: Credit(operation.SourceAccountId!.Value, -magnitude); break;
                case 4: Credit(operation.SourceAccountId!.Value, -magnitude); Credit(operation.DestinationAccountId!.Value, magnitude); break;
            }
            effects[operation.Id] = list;
            void Credit(Guid accountId, BigInteger amount)
            {
                if (!accounts.TryGetValue(accountId, out var account) || account.Currency != operation.Currency)
                    Add("operation.participant_invalid", account: accountId, operation: operation.Id);
                list.Add(new(operation.Id, accountId, operation.BatchId, operation.Currency, amount));
            }
        }

        private void Reversal(JournalOperation reversal)
        {
            if (reversal.Amount is not null || reversal.SourceAccountId is not null || reversal.DestinationAccountId is not null ||
                reversal.Id == Guid.Empty || !Reason(reversal.Reason, true) || reversal.OriginalOperationId is not Guid originalId ||
                !operations.TryGetValue(originalId, out var original) || original.Kind is < 1 or > 4 || original.Id == reversal.Id ||
                reversal.OriginalOperationKind != original.Kind || reversal.Currency != original.Currency || original.BatchId == reversal.BatchId ||
                !effects.TryGetValue(original.Id, out var originalEffects))
            { Add("reversal.reference_invalid", operation: reversal.Id, batch: reversal.BatchId); return; }
            if (!batches.TryGetValue(reversal.BatchId, out var batch) || !batches.TryGetValue(original.BatchId, out var originalBatch) ||
                batch.RecordedAtUtc < originalBatch.RecordedAtUtc || batch.Sequence <= originalBatch.Sequence)
                Add("reversal.causality_invalid", operation: reversal.Id, batch: reversal.BatchId);
            effects[reversal.Id] = originalEffects.Select(x => x with { OperationId = reversal.Id, BatchId = reversal.BatchId, Amount = -x.Amount }).ToList();
            if (original.Kind == 4 && effects[reversal.Id].Aggregate(BigInteger.Zero, (total, x) => total + x.Amount) != 0)
                Add("transfer.conservation_invalid", operation: reversal.Id);
        }

        private void CompareEffectsAndBalances()
        {
            var expected = effects.Values.SelectMany(x => x).ToArray();
            foreach (var effect in data.Effects)
            {
                bool matches = expected.Any(x => x.OperationId == effect.OperationId && x.AccountId == effect.AccountId && x.BatchId == effect.BatchId && x.Currency == effect.Currency);
                BigInteger amount;
                try { amount = Cents(effect.SignedAmount); } catch (FormatException) { Add("effect.mismatch", operation: effect.OperationId); continue; }
                var canonical = expected.Where(x => x.OperationId == effect.OperationId && x.AccountId == effect.AccountId).ToArray();
                if (!matches || canonical.Length != 1 || canonical[0].Amount != amount ||
                    !batches.TryGetValue(effect.BatchId, out var batch) || effect.BatchSequence != batch.Sequence ||
                    effect.RecordedAtUtc != batch.RecordedAtUtc || effect.EffectiveDate != batch.EffectiveDate ||
                    !operations.TryGetValue(effect.OperationId, out var operation) || effect.Kind != operation.Kind)
                    Add("effect.mismatch", account: effect.AccountId, operation: effect.OperationId);
            }
            foreach (var effect in expected)
                if (data.Effects.Count(x => x.OperationId == effect.OperationId && x.AccountId == effect.AccountId) != 1)
                    Add("effect.missing_or_duplicate", account: effect.AccountId, operation: effect.OperationId);
            foreach (var operation in data.Operations.Where(x => x.Kind == 4 || (x.Kind == 5 && x.OriginalOperationKind == 4)))
            {
                try
                {
                    if (data.Effects.Where(x => x.OperationId == operation.Id).Aggregate(BigInteger.Zero, (sum, x) => sum + Cents(x.SignedAmount)) != 0)
                        Add("transfer.conservation_invalid", operation: operation.Id);
                }
                catch (FormatException) { Add("transfer.conservation_invalid", operation: operation.Id); }
            }

            foreach (var account in accounts.Values)
            {
                bool initialized = Initialized(account);
                BigInteger total = 0;
                DateTimeOffset? lastTime = null; DateOnly? lastDate = null;
                var root = data.Openings.FirstOrDefault(x => x.AccountId == account.Id && x.Revision == 1);
                foreach (var batch in data.Batches.OrderBy(x => x.Sequence))
                {
                    var batchEffects = expected.Where(x => x.AccountId == account.Id && x.BatchId == batch.Id).ToArray();
                    bool openingBatch = data.Openings.Any(x => x.AccountId == account.Id && x.BatchId == batch.Id);
                    if (batchEffects.Length == 0 && !openingBatch) continue;
                    if (root is null || !batches.TryGetValue(root.BatchId, out var rootBatch) || batch.Sequence < rootBatch.Sequence)
                        Add("opening.initialization_missing", account: account.Id, batch: batch.Id);
                    if (lastTime > batch.RecordedAtUtc || lastDate > batch.EffectiveDate) Add("calendar.regression", account: account.Id, batch: batch.Id);
                    lastTime = batch.RecordedAtUtc; lastDate = batch.EffectiveDate;
                    // Apply the complete batch delta once. A correction's internal prefix is never a cutoff.
                    total += batchEffects.Aggregate(BigInteger.Zero, (sum, x) => sum + x.Amount);
                    if (total < 0) Add("balance.negative", account: account.Id, batch: batch.Id);
                    if (total > Maximum) Add("balance.limit_exceeded", account: account.Id, batch: batch.Id);
                }
                var reported = data.ReportedBalances.Where(x => x.AccountId == account.Id).ToArray();
                if (reported.Length != 1) { Add("balance.report_missing_or_duplicate", account: account.Id); continue; }
                var normal = reported[0];
                if (normal.FailureCode is not null) { Add("balance.reader_rejected", account: account.Id); continue; }
                try
                {
                    if (normal.Currency != account.Currency || normal.IsInitialized != initialized ||
                        (initialized ? normal.Amount is null || Cents(normal.Amount.Value) != total : normal.Amount is not null))
                        Add("balance.mismatch", account: account.Id);
                }
                catch (FormatException) { Add("balance.mismatch", account: account.Id); }
            }
            foreach (var reported in data.ReportedBalances.Where(x => !accounts.ContainsKey(x.AccountId))) Add("balance.report_unknown_account");
        }
    }
}
