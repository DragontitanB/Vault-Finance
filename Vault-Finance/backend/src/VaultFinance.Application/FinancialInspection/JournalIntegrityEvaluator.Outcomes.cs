using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VaultFinance.Application.FinancialAccounts;

namespace VaultFinance.Application.FinancialInspection;

public sealed partial class JournalIntegrityEvaluator
{
    private sealed partial class Evaluation
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private static string? Name(int command) => command switch { 1 => "opening", 2 => "opening_correction", 3 => "income", 4 => "expense", 5 => "transfer", 6 => "reversal", _ => null };
        private static string? SuccessCode(int command) => command switch { 1 => "opening.initialized", 2 => "opening.corrected", 3 => "income.posted", 4 => "expense.posted", 5 => "transfer.posted", 6 => "reversal.posted", _ => null };
        private void OutcomesAndAudit()
        {
            foreach (var group in data.Outcomes.GroupBy(x => (x.UserId, x.CommandType, x.IdempotencyKey)).Where(x => x.Count() > 1)) Add("idempotency.duplicate_scope", outcome: group.First().Id);
            foreach (var outcome in data.Outcomes)
            {
                if (outcome.Id == Guid.Empty || Name(outcome.CommandType) is null || !Utc(outcome.RecordedAtUtc) ||
                    string.IsNullOrEmpty(outcome.IdempotencyKey) || outcome.IdempotencyKey.Length > 128 ||
                    outcome.IdempotencyKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) ||
                    outcome.PayloadFingerprint.Length != 64 || outcome.PayloadFingerprint.Any(c => c is not (>= '0' and <= '9') and not (>= 'A' and <= 'F')) || outcome.FingerprintVersion < 1)
                    Add("idempotency.metadata_invalid", outcome: outcome.Id);
                var audit = data.Audits.Where(x => x.OutcomeId == outcome.Id).ToArray();
                if (audit.Length != 1) Add("audit.membership_invalid", outcome: outcome.Id);
                else if (audit[0].Action != $"financial.{Name(outcome.CommandType)}.{(outcome.OutcomeKind == 1 ? "posted" : "rejected")}" ||
                    audit[0].RecordedAtUtc != outcome.RecordedAtUtc || !Utc(audit[0].RecordedAtUtc)) Add("audit.contract_invalid", outcome: outcome.Id);
                if (outcome.FingerprintVersion != 1) { Unsupported("idempotency.fingerprint_version_unsupported", outcome.Id); continue; }
                try
                {
                    using var document = JsonDocument.Parse(outcome.ReceiptJson);
                    var json = document.RootElement;
                    if (!Object(json) || !json.TryGetProperty("receiptVersion", out var version) || !version.TryGetInt32(out int receiptVersion))
                    { Add("idempotency.receipt_invalid", outcome: outcome.Id); continue; }
                    if (receiptVersion != 1) { Unsupported("idempotency.receipt_version_unsupported", outcome.Id); continue; }
                    if (outcome.OutcomeKind == 2)
                    {
                        if (outcome.BatchId is not null || !json.TryGetProperty("failure", out var failure) || failure.GetString() != outcome.ResultCode)
                            Add("idempotency.rejection_invalid", outcome: outcome.Id);
                        if (outcome.ResultCode == "idempotency.conflict" || SuccessCode(outcome.CommandType) == outcome.ResultCode)
                            Add("idempotency.rejection_invalid", outcome: outcome.Id);
                        else if (!KnownRejection(outcome.CommandType, outcome.ResultCode)) Unsupported("idempotency.result_unsupported", outcome.Id);
                        if (audit.Length == 1) CheckSummary(audit[0], outcome, null, null);
                        // The original rejected payload is deliberately not retained. Its hash cannot be recomputed honestly.
                    }
                    else if (outcome.OutcomeKind == 1 && outcome.BatchId is Guid batchId && batches.TryGetValue(batchId, out var batch))
                    {
                        if (outcome.CommandType != batch.CommandType || outcome.RecordedAtUtc != batch.RecordedAtUtc || outcome.ResultCode != SuccessCode(outcome.CommandType)) Add("idempotency.posted_invalid", outcome: outcome.Id);
                        var rows = data.Operations.Where(x => x.BatchId == batchId).ToArray();
                        var versions = data.Openings.Where(x => x.BatchId == batchId).ToArray();
                        string? canonical = CheckPostedReceipt(outcome, batch, rows, versions);
                        if (canonical is not null && Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(canonical))) != outcome.PayloadFingerprint)
                            Add("idempotency.fingerprint_mismatch", outcome: outcome.Id);
                        if (audit.Length == 1) CheckSummary(audit[0], outcome, rows.FirstOrDefault(), versions.FirstOrDefault());
                    }
                    else Add("idempotency.membership_invalid", outcome: outcome.Id);
                }
                catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
                { Add("idempotency.receipt_invalid", outcome: outcome.Id); }
            }
            foreach (var audit in data.Audits)
                if (audit.Id == Guid.Empty || !data.Outcomes.Any(x => x.Id == audit.OutcomeId)) Add("audit.orphan", outcome: audit.OutcomeId);
        }
        private void Unsupported(string code, Guid outcome) { incomplete = true; Add(code, outcome: outcome); }
        private static bool Object(JsonElement element) => element.ValueKind == JsonValueKind.Object && element.EnumerateObject().GroupBy(x => x.Name, StringComparer.Ordinal).All(x => x.Count() == 1);
        private static string Tail(string? reason) => reason is null ? "-1:" : reason.Length.ToString(CultureInfo.InvariantCulture) + ":" + reason;
        private static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

        private string? CheckPostedReceipt(JournalOutcome outcome, JournalBatch batch, JournalOperation[] rows, JournalOpening[] versions)
        {
            string? canonical = null;
            bool valid = false;
            if (outcome.CommandType is 1 or 2 && versions.Length == 1)
            {
                var v = versions[0];
                var opening = v.OpeningOperationId is Guid id ? operations.GetValueOrDefault(id) : null;
                decimal amount = opening?.Amount ?? 0m;
                if (outcome.CommandType == 1)
                {
                    valid = Receipt(outcome, new OpeningReceipt(1, v.AccountId, batch.Id, v.OpeningOperationId, 1, amount, v.Currency, batch.RecordedAtUtc, batch.EffectiveDate));
                    canonical = $"VF-OPEN\n1\n{v.AccountId:D}\n{Amount(amount)}\n{v.Currency}\n{Tail(v.Reason)}";
                }
                else
                {
                    var previous = data.Openings.FirstOrDefault(x => x.AccountId == v.AccountId && x.Currency == v.Currency && x.Revision == v.PreviousRevision);
                    if (previous is not null)
                    {
                        decimal oldAmount = previous.OpeningOperationId is Guid oldId ? operations.GetValueOrDefault(oldId)?.Amount ?? 0m : 0m;
                        var compensation = rows.FirstOrDefault(x => x.Kind == 5);
                        valid = Receipt(outcome, new OpeningCorrectionReceipt(1, v.AccountId, batch.Id, previous.Revision, v.Revision,
                            previous.OpeningOperationId, compensation?.Id, v.OpeningOperationId, oldAmount, amount, v.Currency, batch.RecordedAtUtc, batch.EffectiveDate));
                        canonical = $"VF-CORRECT-OPEN\n1\n{v.AccountId:D}\n{previous.Revision.ToString(CultureInfo.InvariantCulture)}\n{Amount(amount)}\n{v.Currency}\n{Tail(v.Reason)}";
                    }
                }
                if (opening is not null && opening.Reason != v.Reason) valid = false;
                if (rows.Any(x => x.Reason != v.Reason)) valid = false;
            }
            else if (rows.Length == 1)
            {
                var op = rows[0];
                if (outcome.CommandType is 3 or 4 && op.Amount is decimal amount)
                {
                    Guid account = (outcome.CommandType == 3 ? op.DestinationAccountId : op.SourceAccountId) ?? Guid.Empty;
                    valid = Receipt(outcome, new FinancialEntryReceipt(1, account, batch.Id, op.Id,
                        (VaultFinance.Domain.FinancialOperations.FinancialOperationKind)op.Kind, amount, op.Currency, batch.RecordedAtUtc, batch.EffectiveDate));
                    canonical = $"{(outcome.CommandType == 3 ? "VF-INCOME" : "VF-EXPENSE")}\n1\n{account:D}\n{Amount(amount)}\n{op.Currency}\n{Tail(op.Reason)}";
                }
                else if (outcome.CommandType == 5 && op.Amount is decimal transferred && op.SourceAccountId is Guid from && op.DestinationAccountId is Guid to)
                {
                    valid = Receipt(outcome, new FinancialTransferReceipt(1, from, to, batch.Id, op.Id,
                        (VaultFinance.Domain.FinancialOperations.FinancialOperationKind)op.Kind, transferred, op.Currency, batch.RecordedAtUtc, batch.EffectiveDate));
                    canonical = $"VF-TRANSFER\n1\n{from:D}\n{to:D}\n{Amount(transferred)}\n{op.Currency}\n{Tail(op.Reason)}";
                }
                else if (outcome.CommandType == 6 && op.OriginalOperationId is Guid originalId && operations.TryGetValue(originalId, out var original) && original.Amount is decimal reversed)
                {
                    Guid? debit = original.Kind is 2 or 4 ? original.DestinationAccountId : null;
                    Guid? credit = original.Kind is 3 or 4 ? original.SourceAccountId : null;
                    valid = Receipt(outcome, new FinancialReversalReceipt(1, originalId, (VaultFinance.Domain.FinancialOperations.FinancialOperationKind)original.Kind,
                        batch.Id, op.Id, VaultFinance.Domain.FinancialOperations.FinancialOperationKind.Reversal, reversed, original.Currency, debit, credit, batch.RecordedAtUtc, batch.EffectiveDate));
                    canonical = $"VF-REVERSE\n1\n{originalId:D}\n{Tail(op.Reason)}";
                }
            }
            if (!valid) Add("idempotency.receipt_invalid", outcome: outcome.Id);
            return canonical;
        }

        private static bool Receipt<T>(JournalOutcome outcome, T expected)
        {
            using var doc = JsonDocument.Parse(outcome.ReceiptJson);
            var json = doc.RootElement;
            // Require the known v1 fields but tolerate additional noncontract fields for compatible readers.
            var required = JsonSerializer.SerializeToElement(expected, JsonOptions);
            if (required.EnumerateObject().Any(x => !json.TryGetProperty(x.Name, out _))) return false;
            if (json.GetProperty("recordedAtUtc").GetDateTimeOffset().Offset != TimeSpan.Zero) return false;
            return EqualityComparer<T>.Default.Equals(expected, JsonSerializer.Deserialize<T>(outcome.ReceiptJson, JsonOptions));
        }
        private void CheckSummary(JournalAudit audit, JournalOutcome outcome, JournalOperation? op, JournalOpening? version)
        {
            try
            {
                using var doc = JsonDocument.Parse(audit.SummaryJson); var json = doc.RootElement;
                bool valid = Object(json) && json.TryGetProperty("batchId", out var batch) &&
                    (outcome.BatchId is Guid id ? batch.ValueKind == JsonValueKind.String && batch.GetGuid() == id : batch.ValueKind == JsonValueKind.Null);
                if (outcome.CommandType == 5)
                    valid &= GuidField("sourceAccountId", op?.SourceAccountId) && GuidField("destinationAccountId", op?.DestinationAccountId);
                else if (outcome.CommandType == 6) valid &= GuidField("originalOperationId", op?.OriginalOperationId);
                else
                {
                    valid &= GuidField("accountId", version?.AccountId ?? (op?.Kind == 3 ? op.SourceAccountId : op?.DestinationAccountId));
                    if (outcome.CommandType == 2) valid &= json.TryGetProperty("expectedRevision", out var revision) && revision.TryGetInt32(out int number) && number >= 1 && (version is null || number == version.PreviousRevision);
                }
                if (!valid) Add("audit.summary_invalid", outcome: outcome.Id);
                bool GuidField(string name, Guid? expected) => json.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String && field.TryGetGuid(out var actual) && actual != Guid.Empty && (!expected.HasValue || actual == expected.Value);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
            { Add("audit.summary_invalid", outcome: outcome.Id); }
        }
        private static bool KnownRejection(int command, string code) => code switch
        {
            "account.not_found" or "account.archived" or "account.currency_mismatch" => command is >= 1 and <= 5,
            "account.uninitialized" => command is >= 2 and <= 5,
            "opening.already_initialized" => command == 1,
            "opening.version_conflict" or "opening.unchanged" or "opening.revision_limit_exceeded" => command == 2,
            "funds.insufficient" => command is 2 or 4 or 5 or 6,
            "balance.limit_exceeded" => command is 2 or 3 or 5 or 6,
            "operation.not_found" or "operation.not_reversible" or "reversal.already_reversed" => command == 6,
            _ => false
        };
    }
}
